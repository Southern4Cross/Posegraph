using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Posegraph.Backend;

namespace Posegraph.UI;

public sealed partial class MainWindow
{
    private bool DrawRecognitionConcurrencySettings()
    {
        var changed = false;
        var parallelism = Math.Clamp(_config.BatchRecognitionParallelism, 1, 8);
        ImGui.TextUnformatted(Loc.T("多图识别并行数"));
        ImGui.SetNextItemWidth(70 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##imageParallelism", parallelism.ToString()))
        {
            for (var count = 1; count <= 8; count++)
                if (ImGui.Selectable(count.ToString(), parallelism == count))
                {
                    parallelism = count;
                    _config.BatchRecognitionParallelism = count;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        ItemTip(Loc.T("默认 1，使用原串行识别；2～8 个任务共享一份模型权重，下批生效。"));

        var estimate = RecognitionResources.Estimate(_config.BackendDevice, _config.MhrOnCpu);
        var vram = estimate.VramMB / 1024f;
        var ram = estimate.RamMB / 1024f;
        var vramLow = estimate.VramMB > 0 && _probe.VramFreeMB is { } free && estimate.VramMB > free;
        var ramLow = _probe.RamTotalMB > 0 && estimate.RamMB > _probe.RamAvailMB;
        var color = vramLow || ramLow ? Yellow : Grey;
        var summary = Loc.F("单模型基础预算：显存约 {0:0.0} GB / 内存约 {1:0.0} GB", vram, ram);
        ImGui.SameLine();
        ImGui.BeginGroup();
        if (ImGui.CalcTextSize(summary).X <= ImGui.GetContentRegionAvail().X)
            ImGui.TextColored(color, summary);
        else
        {
            // Keep both estimates to the right of the combo in narrow windows.
            ImGui.TextColored(color, Loc.F("单模型显存基础约 {0:0.0} GB", vram));
            ImGui.TextColored(color, Loc.F("单模型内存基础约 {0:0.0} GB", ram));
        }
        ImGui.EndGroup();
        if (ImGui.IsItemHovered())
        {
            var tip = Loc.T("按一份模型估算基础预算；并行任务共享权重，每个请求仍需额外的激活、输入与中间结果内存。")
                + "\n" + Loc.T("不含冷加载、游戏、图片与会话缓存，以及 CUDA 上下文和分配器保留；不是实时占用或保证峰值。");
            if (estimate.Scenario == RecognitionResourceScenario.Auto)
                tip += "\n" + Loc.T("自动模式按 GPU 方案估算；显存不足时可能改用 CPU，实际占用会变化。");
            else if (estimate.Scenario == RecognitionResourceScenario.Cpu)
                tip += "\n" + Loc.T("CPU 模式不为识别分配显存；先前 CUDA 上下文可能保留额外显存，直到后端退出。");
            var availableVram = _probe.VramFreeMB is { } available ? Loc.F("{0:0.0} GB", available / 1024f) : Loc.T("未知");
            var availableRam = _probe.RamTotalMB > 0 ? Loc.F("{0:0.0} GB", _probe.RamAvailMB / 1024f) : Loc.T("未知");
            tip += "\n" + Loc.F("当前可用：显存 {0} / 内存 {1}", availableVram, availableRam);
            if (vramLow || ramLow)
                tip += "\n" + Loc.T("单模型基础预算超过当前可用量，建议切换设备或释放资源；降低并行数可减少请求的额外占用。");
            ImGui.SetTooltip(tip);
        }
        return changed;
    }
}
