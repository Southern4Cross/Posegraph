using System.Collections.Concurrent;
using Posegraph.Backend;

namespace Posegraph.Library;

public enum RecognitionState { Queued, Running, Done, Failed }

public sealed record RecognitionStatus(RecognitionState State, string? Error = null);

/// <summary>
/// Content-hash recognition cache and bounded batch queue. Opening a completed
/// library item restores its archive while other selected images keep running.
/// </summary>
public sealed class RecognitionService(ImageLibrary library, BackendClient client, Func<int>? parallelism = null) : IDisposable
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, RecognitionStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    // The library uses one temporary archive filename per hash. Serialize all
    // foreground/background access to the same content, including duplicate paths.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _contentGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly HashSet<Task> _opens = [];
    private Task _work = Task.CompletedTask;
    private bool _worker, _disposed;

    public int Pending => _queue.Count;
    public bool Busy { get { lock (_sync) return _worker; } }
    public Task Completion
    {
        get { lock (_sync) return Task.WhenAll(_opens.Append(_work)); }
    }

    public event Action<string, bool>? Completed;
    public RecognitionStatus? Status(string path) => _status.GetValueOrDefault(path);

    public Task<(SessionInfo Info, string Sha1)> OpenAsync(string path, bool force = false)
        => Track(() => OpenCoreAsync(path, force));

    private Task<T> Track<T>(Func<Task<T>> operation)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var task = operation();
            _opens.Add(task);
            _ = task.ContinueWith(t => { lock (_sync) _opens.Remove(t); },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }

    private async Task<(SessionInfo Info, string Sha1)> OpenCoreAsync(string path, bool force)
    {
        var bytes = await File.ReadAllBytesAsync(path, _cts.Token);
        var sha1 = library.RememberHash(path, bytes);
        var name = Path.GetFileNameWithoutExtension(path);
        var gate = _contentGates.GetOrAdd(sha1, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(_cts.Token);
        SessionInfo? info = null;
        try
        {
            if (!force && library.ReadCache(sha1) is { } archive)
            {
                try { info = await client.RestoreSessionAsync(archive, name); }
                catch (BackendException ex) when (ex.StatusCode == 400) { library.DeleteCache(sha1); }
            }
            if (info is null)
            {
                // Keep inference alive through disposal so its returned session
                // can be deleted; cancellation only skips work not yet started.
                info = await client.CreateSessionAsync(bytes, name);
                try { library.WriteCache(sha1, await client.SessionArchiveAsync(info.SessionId)); }
                catch { /* A failed cache write must not prevent foreground use. */ }
            }
            if (_cts.IsCancellationRequested)
            {
                await DeleteSessionAsync(info.SessionId);
                throw new OperationCanceledException(_cts.Token);
            }
            _status[path] = new RecognitionStatus(RecognitionState.Done);
            library.Save();
            return (info, sha1);
        }
        finally { gate.Release(); }
    }

    public Task<SessionInfo?> RestoreAsync(string sha1, string name)
        => Track(() => RestoreCoreAsync(sha1, name));

    private async Task<SessionInfo?> RestoreCoreAsync(string sha1, string name)
    {
        var gate = _contentGates.GetOrAdd(sha1, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(_cts.Token);
        try
        {
            if (library.ReadCache(sha1) is not { } archive) return null;
            var session = await client.RestoreSessionAsync(archive, name);
            if (_cts.IsCancellationRequested)
            {
                await DeleteSessionAsync(session.SessionId);
                throw new OperationCanceledException(_cts.Token);
            }
            return session;
        }
        finally { gate.Release(); }
    }

    public void Enqueue(IEnumerable<string> paths)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var path in paths)
            {
                if (_status.TryGetValue(path, out var state) && state.State is RecognitionState.Queued or RecognitionState.Running) continue;
                _status[path] = new RecognitionStatus(RecognitionState.Queued);
                _queue.Enqueue(path);
            }
            if (!_worker && !_queue.IsEmpty) StartWorker();
        }
    }

    private void StartWorker()
    {
        _worker = true;
        var limit = Math.Clamp(parallelism?.Invoke() ?? 1, 1, 8);
        _work = Task.Run(() => WorkAsync(limit));
    }

    public void CancelPending()
    {
        lock (_sync)
            while (_queue.TryDequeue(out var path)) _status.TryRemove(path, out _);
    }

    private async Task WorkAsync(int limit)
    {
        try
        {
            await client.ConfigureRecognitionAsync(limit);
            await Task.WhenAll(Enumerable.Range(0, limit).Select(_ => WorkSlotAsync()));
        }
        catch (Exception ex)
        {
            while (_queue.TryDequeue(out var path))
            {
                _status[path] = new RecognitionStatus(RecognitionState.Failed, ex.Message);
                Notify(path, false);
            }
        }
        finally
        {
            lock (_sync)
            {
                _worker = false;
                if (!_queue.IsEmpty && !_disposed) StartWorker();
            }
        }
    }

    private async Task WorkSlotAsync()
    {
        while (!_cts.IsCancellationRequested && _queue.TryDequeue(out var path))
        {
            if (!_status.ContainsKey(path)) continue;
            _status[path] = new RecognitionStatus(RecognitionState.Running);
            var ok = false;
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, _cts.Token);
                var sha1 = library.RememberHash(path, bytes);
                var gate = _contentGates.GetOrAdd(sha1, _ => new SemaphoreSlim(1));
                await gate.WaitAsync(_cts.Token);
                try
                {
                    if (!library.HasCache(sha1))
                    {
                        SessionInfo? session = null;
                        try
                        {
                            session = await client.CreateSessionAsync(bytes, Path.GetFileNameWithoutExtension(path));
                            library.WriteCache(sha1, await client.SessionArchiveAsync(session.SessionId));
                        }
                        finally
                        {
                            if (session is not null) await DeleteSessionAsync(session.SessionId);
                        }
                    }
                }
                finally { gate.Release(); }
                _status[path] = new RecognitionStatus(RecognitionState.Done);
                ok = true;
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                _status.TryRemove(path, out _);
                return;
            }
            catch (Exception ex) { _status[path] = new RecognitionStatus(RecognitionState.Failed, ex.Message); }
            library.Save();
            Notify(path, ok);
        }
    }

    private async Task DeleteSessionAsync(string sid)
    {
        try { await client.DeleteSessionAsync(sid); }
        catch { /* Restarted/stopped backends have already discarded the session. */ }
    }

    private void Notify(string path, bool ok)
    {
        // Dispose waits for a callback already being dispatched, and after it
        // returns no UI subscriber can be invoked by late inference completions.
        lock (_sync)
        {
            foreach (var callback in Completed?.GetInvocationList() ?? [])
            {
                if (_disposed) return;
                try { ((Action<string, bool>)callback)(path, ok); }
                catch { /* A UI subscriber cannot terminate other recognition jobs. */ }
            }
        }
    }

    public void Rename(string from, string to)
    {
        if (_status.TryRemove(from, out var state)) _status[to] = state;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _cts.Cancel();
            CancelPending();
            _ = Completion.ContinueWith(t => { _ = t.Exception; _cts.Dispose(); }, TaskScheduler.Default);
        }
    }
}
