namespace Posegraph.Library;

/// <summary>
/// 批量识别并行（实验）：设置里并行数大于 1 时，批量队列开几路同时请求后端（请求标 parallel，后端共享一份模型）。
/// 单张识别、辅助视角融合都不经过这里。出问题时把设置改回 1；要彻底去掉，删掉本文件、BackendClient.Parallel.cs
/// 和 WorkAsync 里调用 DrainParallelAsync 的一行，就是原来的串行。
/// 后端按显存 / 内存余量削减路数，共享模型不可用时自动改回串行（backend parallel_batch.py），这里只把原因提示一次。
/// </summary>
public sealed partial class RecognitionService
{
    private readonly HashSet<string> _noticed = [];
    private string? _notice;

    /// <summary>批量识别并行数（设置里的「多图识别并行数」）；1 为串行。</summary>
    public Func<int> Parallelism { get; init; } = () => 1;

    /// <summary>取走待显示的提示（后端不支持、并行被停用、路数被削减）；同一原因只提示一次。</summary>
    public string? TakeNotice() => Interlocked.Exchange(ref _notice, null);

    private void Notice(string text)
    {
        lock (_noticed)
            if (_noticed.Add(text)) _notice = text;
    }

    /// <summary>并行识别队列；没开并行、队列不到两张或后端不支持时直接返回，交给串行。</summary>
    private async Task DrainParallelAsync()
    {
        var requested = Math.Clamp(Parallelism(), 1, 8);
        if (requested <= 1 || _queue.Count < 2) return;
        try
        {
            await client.ConfigureRecognitionAsync(requested);
        }
        catch (Exception ex)
        {
            Notice(ex.Message);
            return;
        }
        await Task.WhenAll(Enumerable.Range(0, requested).Select(_ => Task.Run(() => DrainAsync(parallel: true))));
        // 批次还没结束（模型还在），后端的实际路数和停用原因都还在
        if (await client.ParallelStateAsync() is not { } state) return;
        if (state.Disabled is { Length: > 0 } reason)
            Notice(Loc.F("并行识别已停用，改为逐张识别：{0}", reason));
        else if (state.Effective is { } effective && effective < state.Requested)
            Notice(Loc.F("并行识别按余量只开了 {0} 路：{1}", effective, state.Note ?? ""));
    }
}
