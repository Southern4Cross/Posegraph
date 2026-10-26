using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace Posegraph.Library;

/// <summary>
/// 图库缩略图：后台用 WIC 边解码边缩到长边 side 像素再上传（WicThumbnail），原图不进显存。
/// 同时最多解码 2 张；排到时已经滚出视野（超过 StaleAfter 没画过）的不解码，下次画到时重新排队，
/// 快速翻几千张图时不会在背后把滚过的图全部解码一遍。单张超过 LoadTimeout 算失败，但仍占着名额直到真正做完，
/// 后台解码不会越堆越多。超过 MaxEntries 张时释放最久没画过的。Get / Forget / Clear 在绘制线程调用。
/// </summary>
public sealed class ThumbnailCache(ITextureProvider textures, IPluginLog log, int side) : IDisposable
{
    private const int MaxEntries = 300;
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(15);
    private const long StaleAfterMs = 1000;

    private sealed class Entry
    {
        private IDalamudTextureWrap? _wrap;
        private bool _dropped;
        public volatile bool Failed;
        public volatile bool Idle;     // 排到时已不在视野里，没有解码；再画到时重新排队
        public long LastUsed;
        public long LastSeen = Environment.TickCount64;
        public DateTime Stamp;
        public DateTime Checked;   // 上次检查文件修改时间（每 2 秒查一次，不每帧读磁盘）

        public IDalamudTextureWrap? Wrap
        {
            get { lock (this) return _wrap; }
        }

        public bool Dropped
        {
            get { lock (this) return _dropped; }
        }

        /// <summary>后台解码完成；如果已经被移出缓存就直接释放。</summary>
        public void Set(IDalamudTextureWrap wrap)
        {
            lock (this)
            {
                if (_dropped) wrap.Dispose();
                else _wrap = wrap;
            }
        }

        public void Drop()
        {
            lock (this)
            {
                _dropped = true;
                _wrap?.Dispose();
                _wrap = null;
            }
        }
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _slots = new(2);
    private readonly CancellationTokenSource _cts = new();
    private long _frame;

    /// <summary>返回缩略图；还没解码好返回 null（已排队），解码失败 failed = true。</summary>
    public IDalamudTextureWrap? Get(string path, out bool failed)
    {
        var now = DateTime.UtcNow;
        _entries.TryGetValue(path, out var e);
        if (e is null || now - e.Checked > TimeSpan.FromSeconds(2))
        {
            DateTime stamp;
            try { stamp = File.GetLastWriteTimeUtc(path); }
            catch { stamp = default; }
            if (e is null || e.Stamp != stamp)
            {
                e?.Drop();
                // 先记上「刚用过」再淘汰：否则新条目 LastUsed 为 0，缓存满（看过 300 张）后每张新图都会被立刻淘汰、
                // 下一帧又重建重新解码，无限循环（旧版快速翻大文件夹时显存被冲爆的原因）
                e = _entries[path] = new Entry { Stamp = stamp, LastUsed = ++_frame };
                Evict();
                _ = LoadAsync(path, e);
            }
            e.Checked = now;
        }
        e.LastSeen = Environment.TickCount64;
        if (e.Idle && !e.Failed && e.Wrap is null)
        {
            e.Idle = false;
            _ = LoadAsync(path, e);
        }
        e.LastUsed = ++_frame;
        failed = e.Failed;
        return e.Wrap;
    }

    /// <summary>文件改名 / 移动 / 删除后丢掉旧缓存。</summary>
    public void Forget(string path)
    {
        if (_entries.Remove(path, out var e)) e.Drop();
    }

    /// <summary>丢掉解码失败的条目（刷新时重试）。</summary>
    public void RetryFailed()
    {
        foreach (var (key, e) in _entries.Where(kv => kv.Value.Failed).ToList())
        {
            e.Drop();
            _entries.Remove(key);
        }
    }

    private async Task LoadAsync(string path, Entry e)
    {
        try
        {
            await _slots.WaitAsync(_cts.Token);
            try
            {
                if (e.Dropped || Environment.TickCount64 - Interlocked.Read(ref e.LastSeen) > StaleAfterMs)
                {
                    e.Idle = true;
                    return;
                }
                var work = CreateAsync(path);
                var finished = await Task.WhenAny(work, Task.Delay(LoadTimeout, _cts.Token)) == work;
                if (!finished)
                {
                    e.Failed = true;
                    log.Warning($"[Posegraph] 缩略图加载超时（{LoadTimeout.TotalSeconds} 秒）：{path}");
                }
                var wrap = await work;   // 超时也等它做完再让出名额
                if (finished) e.Set(wrap);
                else wrap.Dispose();
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            e.Failed = true;
            log.Warning(ex, $"[Posegraph] 缩略图加载失败：{path}");
        }
    }

    private async Task<IDalamudTextureWrap> CreateAsync(string path)
    {
        (byte[] Rgba, int Width, int Height) image;
        try
        {
            image = await Task.Run(() => WicThumbnail.Decode(path, side), _cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // WIC 直接解不了（少见格式、解码器缺失）：退回 Dalamud 的整张解码再缩小，结果和以前一样
            log.Debug($"[Posegraph] WIC 缩略图解码失败，改用 Dalamud 解码：{path}（{ex.Message}）");
            var bytes = await File.ReadAllBytesAsync(path, _cts.Token);
            return await TextureImages.ThumbnailAsync(textures, bytes, side, _cts.Token);
        }
        _cts.Token.ThrowIfCancellationRequested();
        return await textures.CreateFromRawAsync(RawImageSpecification.Rgba32(image.Width, image.Height), image.Rgba,
                                                 "Posegraph thumbnail", _cts.Token);
    }

    private void Evict()
    {
        if (_entries.Count <= MaxEntries) return;
        foreach (var (key, e) in _entries.OrderBy(kv => kv.Value.LastUsed).Take(_entries.Count - MaxEntries).ToList())
        {            e.Drop();
            _entries.Remove(key);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var e in _entries.Values) e.Drop();
        _entries.Clear();
    }
}
