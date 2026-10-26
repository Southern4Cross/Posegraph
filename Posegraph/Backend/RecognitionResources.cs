namespace Posegraph.Backend;

internal enum RecognitionResourceScenario
{
    Cpu,
    Gpu,
    Auto,
}

/// <summary>Single-model base allowances in MiB; request working memory is additional.</summary>
internal readonly record struct RecognitionResourceEstimate(
    int VramMB, int RamMB, RecognitionResourceScenario Scenario);

/// <summary>
/// Uses the existing probe's one-model baseline without loading hardware/models.
/// Shared requests reuse weights but add activations and intermediate results;
/// cold loading, allocator/context memory, the game and caches are additional.
/// These base allowances are neither live measurements nor guaranteed peaks.
/// </summary>
internal static class RecognitionResources
{
    // docs/benchmark.md: moving the shared MHR back to the GPU adds about
    // 0.75 GB in the bf16 measurements; the settings UI uses the same allowance.
    private const int MhrOnGpuMB = 750;

    internal static RecognitionResourceEstimate Estimate(string device, bool mhrOnCpu)
    {
        var scenario = device switch
        {
            "cpu" => RecognitionResourceScenario.Cpu,
            "cuda" => RecognitionResourceScenario.Gpu,
            _ => RecognitionResourceScenario.Auto,
        };
        // Auto is a GPU planning scenario, not a claim about the selected device.
        var vram = scenario == RecognitionResourceScenario.Cpu ? 0
            : SystemProbe.RequiredVramMB + (mhrOnCpu ? 0 : MhrOnGpuMB);
        return new(vram, SystemProbe.RequiredRamMB, scenario);
    }
}
