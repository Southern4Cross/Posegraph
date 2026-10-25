namespace Posegraph.Backend;

internal enum RecognitionResourceScenario
{
    Cpu,
    Gpu,
    Auto,
}

/// <summary>Approximate planning allowances for the selected number of model workers, in MiB.</summary>
internal readonly record struct RecognitionResourceEstimate(
    int Workers, int VramMB, int RamMB, RecognitionResourceScenario Scenario);

/// <summary>
/// Estimates model-worker budgets without probing or loading any hardware/model.
/// These are planning allowances, not measured usage or guaranteed peak limits.
/// RAM includes the existing cold-load allowance; game memory, input images and
/// session caches, and CUDA remnants from other/previous processes are additional.
/// </summary>
internal static class RecognitionResources
{
    // docs/benchmark.md: moving the shared MHR back to the GPU adds about
    // 0.75 GB in the bf16 measurements; the settings UI uses the same allowance.
    private const int MhrOnGpuMB = 750;

    internal static RecognitionResourceEstimate Estimate(string device, int parallelism, bool mhrOnCpu)
    {
        var workers = Math.Clamp(parallelism, 1, 8);
        var scenario = device switch
        {
            "cpu" => RecognitionResourceScenario.Cpu,
            "cuda" => RecognitionResourceScenario.Gpu,
            _ => RecognitionResourceScenario.Auto,
        };
        // Automatic mode may distribute workers between CPU and GPU according to
        // availability at load time. Present a GPU planning scenario with an
        // explicit CPU-fallback note, never claim it is the selected live device.
        var vramPerWorker = scenario == RecognitionResourceScenario.Cpu ? 0
            : SystemProbe.RequiredVramMB + (mhrOnCpu ? 0 : MhrOnGpuMB);
        return new(workers, vramPerWorker * workers, SystemProbe.RequiredRamMB * workers, scenario);
    }
}
