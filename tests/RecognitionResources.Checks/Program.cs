using Posegraph.Backend;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAILED: " + description);
    checks++;
    Console.WriteLine("PASS: " + description);
}

var gpu = RecognitionResources.Estimate("cuda", 1, true);
Check(gpu.Workers == 1 && gpu.VramMB == SystemProbe.RequiredVramMB &&
      gpu.RamMB == SystemProbe.RequiredRamMB && gpu.Scenario == RecognitionResourceScenario.Gpu,
      "GPU baseline uses existing one-model planning allowances");

for (var workers = 1; workers <= 8; workers++)
{
    var estimate = RecognitionResources.Estimate("cuda", workers, true);
    Check(estimate.Workers == workers && estimate.VramMB == workers * SystemProbe.RequiredVramMB &&
          estimate.RamMB == workers * SystemProbe.RequiredRamMB,
          $"{workers} worker allowance scales with independent full-model instances");
    var cpu = RecognitionResources.Estimate("cpu", workers, false);
    Check(cpu.Scenario == RecognitionResourceScenario.Cpu && cpu.VramMB == 0 && cpu.RamMB == estimate.RamMB,
          $"{workers} CPU workers have no inference VRAM allowance and retain cold-load RAM allowance");
}

var gpuMhr = RecognitionResources.Estimate("cuda", 3, false);
var cpuMhr = RecognitionResources.Estimate("cuda", 3, true);
Check(gpuMhr.VramMB - cpuMhr.VramMB == 3 * 750 && gpuMhr.RamMB == cpuMhr.RamMB,
      "MHR-on-GPU adds the documented per-worker VRAM allowance without discounting cold-load RAM");

var automatic = RecognitionResources.Estimate("auto", 4, true);
var forcedGpu = RecognitionResources.Estimate("cuda", 4, true);
Check(automatic.Scenario == RecognitionResourceScenario.Auto && automatic.VramMB == forcedGpu.VramMB &&
      automatic.RamMB == forcedGpu.RamMB,
      "Auto retains a GPU planning scenario and an explicit fallback state, independent of live free memory");

foreach (var (requested, expected) in new[] { (int.MinValue, 1), (-2, 1), (0, 1), (9, 8), (int.MaxValue, 8) })
{
    var estimate = RecognitionResources.Estimate("cuda", requested, true);
    Check(estimate.Workers == expected && estimate.VramMB == expected * SystemProbe.RequiredVramMB &&
          estimate.RamMB == expected * SystemProbe.RequiredRamMB,
          $"invalid persisted concurrency {requested} is clamped to {expected} without overflow");
}

var cpuOn = RecognitionResources.Estimate("cpu", 2, true);
var cpuOff = RecognitionResources.Estimate("cpu", 2, false);
Check(cpuOn == cpuOff, "MHR GPU-placement option does not affect CPU-mode estimate");
Check(RecognitionResources.Estimate("unknown", 2, true).Scenario == RecognitionResourceScenario.Auto,
      "unknown device retains explicit automatic-scenario qualification");
Console.WriteLine($"{checks} recognition resource estimate checks passed (production source linked; no hardware probe).");

namespace Posegraph
{
    // Only the existing probe's error translation is stubbed. The real
    // SystemProbe constants are linked; no probe instance is created or ticked.
    internal static class Loc
    {
        public static string T(string text) => text;
        public static string F(string text, params object?[] args) => string.Format(text, args);
    }
}
