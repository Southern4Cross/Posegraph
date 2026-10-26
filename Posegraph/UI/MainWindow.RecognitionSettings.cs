using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Posegraph.UI;

/// <summary>设置页的「多图识别并行数」（实验，配合 Library/RecognitionService.Parallel.cs）。</summary>
public sealed partial class MainWindow
{
    private bool DrawRecognitionConcurrencySettings()
    {
        var changed = false;
        var parallelism = Math.Clamp(_config.BatchRecognitionParallelism, 1, 8);
        ImGui.SetNextItemWidth(70 * ImGuiHelpers.GlobalScale);
        if (ImGui.BeginCombo("##imageParallelism", parallelism.ToString()))
        {
            for (var count = 1; count <= 8; count++)
                if (ImGui.Selectable(count.ToString(), parallelism == count))
                {
                    _config.BatchRecognitionParallelism = count;
                    changed = true;
                }
            ImGui.EndCombo();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted(Loc.T("多图识别并行数（实验）"));
        ItemTip(Loc.T("只影响图库的批量识别，单张识别始终逐张进行。默认 1（逐张）。\n2～8 时几张图共享一份模型同时识别：GPU 上整批约省 25%~35% 的时间，每多一路约多占 200MB 显存（CPU 模式约 350MB 内存）。\n后端按剩余显存 / 内存自动减少路数；出错时自动改回逐张识别，并提示一次。\nCPU 模式提速有限，还会和游戏抢 CPU，不建议开"));
        if (parallelism > 1 && _config.BackendDevice == "cpu")
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, Loc.T("CPU 模式提速有限"));
        }
        return changed;
    }
}
