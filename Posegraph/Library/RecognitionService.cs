using System.Collections.Concurrent;
using Posegraph.Backend;

namespace Posegraph.Library;

public enum RecognitionState { Queued, Running, Done, Failed }

public sealed record RecognitionStatus(RecognitionState State, string? Error = null);

/// <summary>
/// 识别与识别缓存：同一张图（按内容）识别过就从缓存恢复会话，不再跑模型；批量识别按队列逐张进行（后端一次只跑一个模型）。
/// releaseImmediately：模型释放策略是「识别后立即释放」（批量结束时由这里卸载，见 Batch）。
/// </summary>
public sealed class RecognitionService(ImageLibrary library, BackendClient client, Func<bool> releaseImmediately) : IDisposable
{
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly ConcurrentDictionary<string, RecognitionStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _cts = new();
    private int _worker;
    private int _batches;   // 进行中的批次数：大于 0 时识别请求带 hold，模型不在每张之后卸载
    private int _held;      // 本轮批次里真的跑过模型（结束时才需要卸载）

    /// <summary>
    /// 批量识别范围（主图 + 辅助视角、拆分模式全部应用、批量队列）：范围内模型只加载一次。
    /// 释放策略为「立即释放」时，所有批次都结束后卸载一次；其他策略照常按空闲时间释放。可以嵌套、并行。
    /// </summary>
    public IAsyncDisposable Batch()
    {
        Interlocked.Increment(ref _batches);
        return new BatchScope(this);
    }

    private sealed class BatchScope(RecognitionService owner) : IAsyncDisposable
    {
        private int _done;

        public ValueTask DisposeAsync() => Interlocked.Exchange(ref _done, 1) == 1 ? ValueTask.CompletedTask : owner.EndBatchAsync();
    }

    private async ValueTask EndBatchAsync()
    {
        if (Interlocked.Decrement(ref _batches) > 0 || Interlocked.Exchange(ref _held, 0) == 0 || !releaseImmediately()) return;
        try
        {
            await client.UnloadModelAsync();
        }
        catch
        {
            // 后端没响应时它自己会在空闲一分钟后释放
        }
    }

    private Task<SessionInfo> CreateAsync(byte[] bytes, string name)
    {
        var hold = Volatile.Read(ref _batches) > 0;
        if (hold) Interlocked.Exchange(ref _held, 1);
        return client.CreateSessionAsync(bytes, name, hold);
    }

    public int Pending => _queue.Count;
    public bool Busy => Volatile.Read(ref _worker) != 0;

    /// <summary>识别完成一张（批量队列里的）：path、是否成功。</summary>
    public event Action<string, bool>? Completed;

    public RecognitionStatus? Status(string path) => _status.GetValueOrDefault(path);

    /// <summary>打开一张图：有缓存就恢复，没有（或 force）就识别并写入缓存。返回会话与内容哈希。</summary>
    public async Task<(SessionInfo Info, string Sha1)> OpenAsync(string path, bool force = false)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        var sha1 = library.RememberHash(path, bytes);
        var name = Path.GetFileNameWithoutExtension(path);
        if (!force && library.ReadCache(sha1) is { } archive)
        {
            try
            {
                return (await client.RestoreSessionAsync(archive, name), sha1);
            }
            catch (BackendException ex) when (ex.StatusCode == 400)
            {
                library.DeleteCache(sha1);   // 缓存坏了或格式过时：重新识别
            }
        }
        var info = await CreateAsync(bytes, name);
        try
        {
            library.WriteCache(sha1, await client.SessionArchiveAsync(info.SessionId));
        }
        catch
        {
            // 缓存写不进去不影响这次使用
        }
        _status[path] = new RecognitionStatus(RecognitionState.Done);
        library.Save();
        return (info, sha1);
    }

    private readonly ConcurrentDictionary<string, AuxView> _views = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>辅助视角图上次融合时识别出的视角（菜单里显示）；还没融合过返回 null。</summary>
    public AuxView? ViewOf(string auxPath) => _views.GetValueOrDefault(auxPath);

    /// <summary>
    /// 把主图挂着的辅助视角图（没识别过的先识别，结果同样进识别缓存）融合进主图的会话，之后的重定向默认用融合结果。
    /// 没有辅助图时清掉会话里的融合结果。辅助图的会话用完就删。返回 (辅助图路径, 视角) 列表。
    /// </summary>
    public async Task<(string Path, AuxView View)[]> AttachAuxAsync(string sid, string sha1)
    {
        await using var batch = Batch();
        var aux = library.AuxImages(sha1);
        var sessions = new List<string>();
        try
        {
            foreach (var path in aux)
                sessions.Add((await OpenAsync(path)).Info.SessionId);
            var views = await client.SetViewsAsync(sid, sessions);
            var result = aux.Zip(views).ToArray();
            foreach (var (path, view) in result) _views[path] = view;
            return result;
        }
        finally
        {
            foreach (var s in sessions)
                _ = client.DeleteSessionAsync(s).ContinueWith(_ => { });
        }
    }

    /// <summary>会话丢了（后端重启、被挤出）时用缓存恢复；没有缓存返回 null。</summary>
    public async Task<SessionInfo?> RestoreAsync(string sha1, string name)
        => library.ReadCache(sha1) is { } archive ? await client.RestoreSessionAsync(archive, name) : null;

    /// <summary>加入批量识别队列（已在队列或已有缓存的跳过）。</summary>
    public void Enqueue(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (_status.TryGetValue(path, out var s) && s.State is RecognitionState.Queued or RecognitionState.Running) continue;
            _status[path] = new RecognitionStatus(RecognitionState.Queued);
            _queue.Enqueue(path);
        }
        if (Interlocked.CompareExchange(ref _worker, 1, 0) == 0)
            _ = Task.Run(WorkAsync);
    }

    /// <summary>清空还没开始的；正在识别的那张会做完。</summary>
    public void CancelPending()
    {
        while (_queue.TryDequeue(out var path))
            _status.TryRemove(path, out _);
    }

    private async Task WorkAsync()
    {
        try
        {
            await using var batch = Batch();   // 整个队列只加载一次模型
            while (!_cts.IsCancellationRequested && _queue.TryDequeue(out var path))
            {
                if (!_status.ContainsKey(path)) continue;   // 已取消
                _status[path] = new RecognitionStatus(RecognitionState.Running);
                var ok = false;
                try
                {
                    var bytes = await File.ReadAllBytesAsync(path, _cts.Token);
                    var sha1 = library.RememberHash(path, bytes);
                    // 挂着的辅助视角图一起识别，打开时不用再等
                    foreach (var file in library.AuxImages(sha1).Prepend(path))
                    {
                        var data = file == path ? bytes : await File.ReadAllBytesAsync(file, _cts.Token);
                        var hash = file == path ? sha1 : library.RememberHash(file, data);
                        if (library.HasCache(hash)) continue;
                        var info = await CreateAsync(data, Path.GetFileNameWithoutExtension(file));
                        library.WriteCache(hash, await client.SessionArchiveAsync(info.SessionId));
                        await client.DeleteSessionAsync(info.SessionId);   // 批量识别的会话不留在后端，免得挤掉当前图
                    }
                    _status[path] = new RecognitionStatus(RecognitionState.Done);
                    ok = true;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _status[path] = new RecognitionStatus(RecognitionState.Failed, ex.Message);
                }
                library.Save();
                Completed?.Invoke(path, ok);
            }
        }
        finally
        {
            Volatile.Write(ref _worker, 0);
            if (!_queue.IsEmpty && !_cts.IsCancellationRequested && Interlocked.CompareExchange(ref _worker, 1, 0) == 0)
                _ = Task.Run(WorkAsync);
        }
    }

    /// <summary>文件改名 / 移动后，状态跟着走。</summary>
    public void Rename(string from, string to)
    {
        if (_status.TryRemove(from, out var s)) _status[to] = s;
    }

    public void Dispose()
    {
        _cts.Cancel();
        CancelPending();
    }
}
