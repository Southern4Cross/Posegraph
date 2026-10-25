using System.Text.Json;
using System.Text.Json.Nodes;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;
using Posegraph.Backend;

namespace Posegraph.UI;

public sealed partial class MainWindow
{
    // Advice owns no preview or chat history. Its only mutation is an explicit
    // transfer into the chat draft, after validating the captured pose again.
    private sealed record AdviceKey(int? Actor, string? ActorKey, string? Signature, string? Session,
        int Person, string? Race, string? Preview, string? Parameters, bool FreshStart, string Language);
    private sealed record AdviceRun(PoseAdviceResponse Response, string? ActorDependency, bool ActorUnchanged);

    private string _adviceFocus = "";
    private bool _adviceIncludeImage;
    private string? _adviceError, _adviceActorDependency, _adviceTransferPrompt;
    private PoseAdviceResponse? _advice;
    private AdviceKey? _adviceKey;
    private bool _adviceStale;
    private int _adviceRevision, _adviceRequestRevision, _adviceCheckRevision;
    private Task<AdviceRun>? _adviceTask;
    private Task<PoseApplier.ActorSnapshot?>? _advicePoseCheck;
    private CancellationTokenSource? _adviceCts;
    private DateTime _adviceLastPoseCheck = DateTime.MinValue;

    private AdviceKey CurrentAdviceKey() => new(_actor, _actorKey, _currentSignature,
        _chatImageDetached ? null : _session?.SessionId, _person,
        _raceOverride ?? _detectedRace ?? _result?.Race?.Code, _result?.Pose,
        _session is not null && !_chatPoseActive ? JsonSerializer.Serialize(_params) : null,
        _chatFreshStart, Loc.Lang);

    private bool AdviceBusy => ImageWorkBusy || _rtBusy || _rtDirty || _chatTask is not null;
    private bool AdviceCurrent => !_adviceStale && _adviceKey is not null && _adviceKey == CurrentAdviceKey();

    private void InvalidateAdvice()
    {
        _adviceStale = true;
        _adviceRevision++;
        _adviceCts?.Cancel();
        _adviceTransferPrompt = null;
    }

    private void StartAdvice()
    {
        if (_adviceTask is not null || AdviceBusy) return;
        if (!BackendOnline)
        {
            _adviceError = Loc.T("后端离线：先点右上角电源按钮启动");
            return;
        }
        if (string.IsNullOrWhiteSpace(_config.ChatApiEndpoint) || string.IsNullOrWhiteSpace(_config.ChatModel))
        {
            _adviceError = Loc.T("请先在设置中配置 API 并选择模型");
            _selectTab = 4;
            return;
        }
        var key = CurrentAdviceKey();
        if (key.Preview is null && (key.ActorKey is null || key.FreshStart))
        {
            _adviceError = Loc.T("请先生成姿势预览，或在 GPose 中选择角色");
            return;
        }
        _adviceKey = key;
        _adviceError = null;
        _advice = null;
        _adviceActorDependency = null;
        _adviceTransferPrompt = null;
        _adviceStale = false;
        _adviceRequestRevision = ++_adviceRevision;
        var focus = _adviceFocus.Trim();
        var parameters = key.Parameters is null ? null : _params.Clone();
        var warnings = _result?.Warnings.ToArray() ?? [];
        var includeImage = _adviceIncludeImage && key.Session is not null;
        var provider = new PoseChatProvider(_config.ChatApiEndpoint.Trim(), _chatApiKey, _config.ChatModel.Trim());
        _adviceCts = new CancellationTokenSource();
        var ct = _adviceCts.Token;
        _adviceTask = Task.Run(async () =>
        {
            var actor = await _applier.GetSnapshotAsync(key.Actor);
            ct.ThrowIfCancellationRequested();
            var pose = key.Preview ?? actor?.Pose
                ?? throw new InvalidOperationException(Loc.T("无法读取角色姿势，请重新选择角色"));
            var dependency = BuildAdviceActorDependency(key.Preview, actor);
            var response = await _client.PoseAdviceAsync(new PoseAdviceRequest(pose, key.Race, provider, focus,
                parameters, warnings, key.Session, includeImage, actor?.Rig, actor?.Pose), ct);
            ct.ThrowIfCancellationRequested();
            // Brio can be edited outside Posegraph while the provider is busy.
            var unchanged = dependency is null || SameAdviceDependency(dependency,
                BuildAdviceActorDependency(key.Preview, await _applier.GetSnapshotAsync(key.Actor)));
            return new AdviceRun(response, dependency, unchanged);
        }, ct);
    }

    private static string? BuildAdviceActorDependency(string? preview, PoseApplier.ActorSnapshot? actor)
    {
        if (actor is null) return null;
        var actorBones = JsonNode.Parse(actor.Pose)?["Bones"] as JsonObject;
        var previewBones = preview is null ? null : JsonNode.Parse(preview)?["Bones"] as JsonObject;
        var inherited = new JsonObject();
        if (actorBones is not null)
            foreach (var bone in actorBones)
                if (previewBones?.ContainsKey(bone.Key) != true)
                    inherited[bone.Key] = bone.Value?.DeepClone();
        // Body transforms supplied by the preview override the live actor.
        // Missing face/accessory transforms and the actual rig still come from
        // the actor, so changes to either make this analysis obsolete.
        if (inherited.Count == 0 && actor.Rig is null) return null;
        return new JsonObject { ["Bones"] = inherited, ["Rig"] = JsonSerializer.SerializeToNode(actor.Rig) }.ToJsonString();
    }

    private static bool SameAdviceDependency(string expected, string? actual)
    {
        if (actual is null) return false;
        if (expected == actual) return true;
        try
        {
            return JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual));
        }
        catch (JsonException) { return false; }
    }

    private void PollAdvice()
    {
        if (!_adviceStale && _adviceKey is not null && _adviceKey != CurrentAdviceKey()) InvalidateAdvice();
        if (_adviceTask is { IsCompleted: true } task)
        {
            try
            {
                var run = task.GetAwaiter().GetResult();
                if (_adviceRequestRevision == _adviceRevision && AdviceCurrent)
                {
                    _advice = run.Response;
                    _adviceActorDependency = run.ActorDependency;
                    if (!run.ActorUnchanged) InvalidateAdvice();
                }
            }
            catch (OperationCanceledException)
            {
                if (_adviceRequestRevision == _adviceRevision) _adviceError = Loc.T("AI 分析超时，请重试或检查模型服务");
            }
            catch (Exception ex)
            {
                if (_adviceRequestRevision == _adviceRevision) _adviceError = ex.Message;
            }
            finally
            {
                _adviceTask = null;
                _adviceCts?.Dispose();
                _adviceCts = null;
            }
        }
        if (_advicePoseCheck is { IsCompleted: true } check)
        {
            try
            {
                var actor = check.GetAwaiter().GetResult();
                if (_adviceCheckRevision == _adviceRevision && AdviceCurrent)
                {
                    if (_adviceActorDependency is null || !SameAdviceDependency(_adviceActorDependency,
                        BuildAdviceActorDependency(_adviceKey?.Preview, actor))) InvalidateAdvice();
                    else if (_adviceTransferPrompt is { } prompt && !AdviceBusy) SetAdviceChatDraft(prompt);
                }
            }
            catch (Exception)
            {
                if (_adviceCheckRevision == _adviceRevision) InvalidateAdvice();
            }
            finally
            {
                _advicePoseCheck = null;
                _adviceTransferPrompt = null;
            }
        }
        if (_advice is not null && AdviceCurrent && _adviceActorDependency is not null &&
            _advicePoseCheck is null && DateTime.UtcNow - _adviceLastPoseCheck > TimeSpan.FromSeconds(1))
            CheckAdviceActorPose();
    }

    private void CheckAdviceActorPose()
    {
        _adviceLastPoseCheck = DateTime.UtcNow;
        _adviceCheckRevision = _adviceRevision;
        _advicePoseCheck = _applier.GetSnapshotAsync(_actor);
    }

    private void TransferAdvice(PoseAdviceSuggestion suggestion)
    {
        if (!AdviceCurrent || AdviceBusy) { InvalidateAdvice(); return; }
        var target = suggestion.Bone is { } bone ? $"{JointLabel(bone)} ({bone})" : Loc.T("整体姿势");
        var prompt = Loc.F("请基于当前姿势，仅针对以下问题生成调整预览，保留其他动作：\n部位：{0}\n问题：{1}\n依据：{2}\n建议：{3}",
            target, suggestion.Issue, suggestion.Evidence, suggestion.Recommendation);
        if (_adviceActorDependency is null) SetAdviceChatDraft(prompt);
        else
        {
            // An explicit transfer always checks the live actor again, even if
            // the periodic check just finished. It never calls the model.
            if (_advicePoseCheck is { } old)
                _ = old.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            _adviceTransferPrompt = prompt;
            CheckAdviceActorPose();
        }
    }

    private void SetAdviceChatDraft(string prompt)
    {
        var draft = string.IsNullOrWhiteSpace(_chatPrompt) ? prompt : _chatPrompt.TrimEnd() + "\n\n" + prompt;
        if (draft.Length > 4000)
        {
            _adviceError = Loc.T("对话输入已满，请先缩短草稿后再转入建议");
            return;
        }
        _chatPrompt = draft;
        _selectTab = 5;
    }

    private void DrawAdvice()
    {
        ImGui.TextDisabled(Loc.T("AI 姿势建议"));
        ImGui.TextWrapped(Loc.T("点击分析才会向已配置的模型发送当前姿势；建议不会修改预览或角色。"));
        ImGui.TextDisabled(Loc.F("模型：{0}", string.IsNullOrWhiteSpace(_config.ChatModel) ? Loc.T("尚未配置") : _config.ChatModel));
        if (ImGui.SmallButton(Loc.T("配置模型") + "##advice")) _selectTab = 4;
        using (ImRaii.Disabled(_adviceTask is not null))
        {
            ImGui.InputText(Loc.T("关注点（可选）##adviceFocus"), ref _adviceFocus, 1000);
            ItemTip(Loc.T("例如：保持挥手动作，重点检查左肩、手腕和重心。"));
            using (ImRaii.Disabled(_chatImageDetached || _session is null || _chatFreshStart))
                ImGui.Checkbox(Loc.T("发送参考图##adviceImage"), ref _adviceIncludeImage);
            ItemTip(Loc.T("仅本次分析使用此开关；发送的是导入参考图，需要视觉模型。"));
        }
        if (_adviceTask is not null)
        {
            ImGui.TextDisabled(Loc.T("正在分析当前姿势…"));
            if (ImGui.SmallButton(Loc.T("取消分析")))
            {
                InvalidateAdvice();
                _adviceError = Loc.T("已取消分析");
            }
        }
        else
        {
            using (ImRaii.Disabled(!BackendOnline || AdviceBusy ||
                (_result is null && (_actorKey is null || _chatFreshStart))))
                if (ImGui.Button(Loc.T("AI 分析当前姿势"))) StartAdvice();
        }
        if (_result is null)
            ImGui.TextWrapped(Loc.T(_actorKey is not null && !_chatFreshStart
                ? "分析来源：当前游戏角色姿势" : "请先生成姿势预览，或在 GPose 中选择角色"));
        else ImGui.TextDisabled(Loc.T("分析来源：当前预览姿势"));
        if (_adviceError is { } error) ImGui.TextWrapped(error);
        if (_adviceStale && _adviceKey is not null)
            ImGui.TextColored(Yellow, Loc.T("姿势已改变或分析已取消，请重新分析。"));
        if (_advice is not { } advice) return;
        ImGui.Spacing();
        ImGui.TextWrapped(advice.Summary);
        for (var i = 0; i < advice.Suggestions.Length; i++)
        {
            var suggestion = advice.Suggestions[i];
            using var id = ImRaii.PushId(i);
            ImGui.Separator();
            ImGui.TextWrapped(suggestion.Bone is { } bone
                ? Loc.F("{0}：{1}", JointLabel(bone), suggestion.Issue) : suggestion.Issue);
            ImGui.TextWrapped(Loc.F("依据：{0}", suggestion.Evidence));
            ImGui.TextWrapped(Loc.F("建议：{0}", suggestion.Recommendation));
            using (ImRaii.Disabled(!AdviceCurrent || AdviceBusy || _adviceTransferPrompt is not null))
            {
                if (suggestion.Bone is { } joint && _result?.Stick?.Joints.Any(j => j.Name == joint) == true)
                {
                    if (ImGui.SmallButton(Loc.T("定位关节")) && AdviceCurrent)
                    {
                        _selected = joint;
                        _selectTab = 1;
                    }
                    ImGui.SameLine();
                }
                if (ImGui.SmallButton(Loc.T("转入对话调整"))) TransferAdvice(suggestion);
            }
            ItemTip(Loc.T("仅填入对话草稿；仍需点击“生成预览”，然后手动应用。"));
        }
        foreach (var limitation in advice.Limitations)
            ImGui.TextWrapped(Loc.F("分析范围：{0}", limitation));
    }

    private void DisposeAdvice()
    {
        InvalidateAdvice();
        _adviceCts?.Dispose();
        if (_adviceTask is { } task)
            _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        if (_advicePoseCheck is { } check)
            _ = check.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }
}
