using System.Net.Http.Json;
using System.Text.Json;

namespace Posegraph.Backend;

/// <summary>后端批量并行识别的实际情况（/health 的 model.parallel）。Effective 为 null 表示共享模型没在运行。</summary>
public sealed record ParallelState(int Requested, int? Effective, string? Note, string? Disabled);

/// <summary>批量识别并行（实验）用到的接口，与 Library/RecognitionService.Parallel.cs 一起，可以整块删掉。</summary>
public sealed partial class BackendClient
{
    /// <summary>批量识别开始前同步并行数，设备和释放策略不变。后端不认识这个设置（旧版本）时抛异常，调用方改走串行。</summary>
    public async Task ConfigureRecognitionAsync(int parallelism)
    {
        var limit = Math.Clamp(parallelism, 1, 8);
        using var resp = await _http.PostAsJsonAsync(Url("/model/config"), new { recognition_parallelism = limit }, Json);
        var body = await ReadAsync<JsonElement>(resp);
        var confirmed = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("model", out var model) &&
            model.ValueKind == JsonValueKind.Object && model.TryGetProperty("config", out var config) &&
            config.ValueKind == JsonValueKind.Object && config.TryGetProperty("recognitionParallelism", out var value) &&
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var actual) && actual == limit;
        // 旧后端会忽略不认识的字段、照样串行：只有 1 可以接受，大于 1 不能假装在并行
        if (limit > 1 && !confirmed)
            throw new InvalidOperationException(Loc.T("当前后端不支持并行识别，请更新并重启后端"));
    }

    /// <summary>后端并行识别的实际路数、被削减或停用的原因；旧后端或连不上时返回 null。</summary>
    public async Task<ParallelState?> ParallelStateAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var health = await _http.GetFromJsonAsync<JsonElement>(Url("/health"), Json, cts.Token);
            return health.TryGetProperty("model", out var model) && model.TryGetProperty("parallel", out var parallel)
                ? parallel.Deserialize<ParallelState>(Json)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
