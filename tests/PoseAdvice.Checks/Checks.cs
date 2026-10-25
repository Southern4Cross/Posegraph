using System.Numerics;
using System.Text.Json;
using Dalamud.Bindings.ImGui;
using Posegraph.Backend;

namespace Posegraph.UI;

public sealed partial class MainWindow
{
    private static readonly Vector4 Yellow = new(1, 1, 0, 1);
    private readonly FakeConfig _config = new();
    private readonly FakeClient _client = new();
    private readonly PoseApplier _applier = new();
    private string _chatApiKey = "test-key";
    private bool ImageWorkBusy, _rtBusy, _rtDirty, _chatImageDetached, _chatPoseActive, _chatFreshStart;
    private Task? _chatTask;
    private int? _actor = 7;
    private string? _actorKey = "actor-7", _currentSignature = "actor-7:c0101", _raceOverride, _detectedRace = "c0101";
    private SessionInfo? _session;
    private int _person, _selectTab;
    private RetargetResult? _result;
    private RetargetParams _params = new();
    private string _chatPrompt = "";
    private string? _selected;
    private bool BackendOnline = true;
    private static void ItemTip(string text) { }
    private static string JointLabel(string name) => name switch
    {
        "j_kao" => "头部",
        "j_f_mab_l" => "左眼睑",
        _ => name,
    };

    private const string PoseA = "{\"Description\":\"first\",\"Bones\":{\"j_kao\":{\"Position\":\"0,1,0\",\"Rotation\":\"0,0,0,1\",\"Scale\":\"1,1,1\"}}}";
    private const string PoseB = "{\"Bones\":{\"j_kao\":{\"Position\":\"0,1.2,0\",\"Rotation\":\"0,0,0,1\",\"Scale\":\"1,1,1\"}}}";
    private static readonly PoseAdviceSuggestion Suggestion = new("j_kao", "头部倾斜", "头相对胸部侧倾", "保持挥手，仅稍微收敛头部侧倾");
    private static readonly PoseAdviceResponse Response = new("挥手姿势", [Suggestion], ["未提供游戏截图"]);

    private MainWindow(bool preview = true)
    {
        ImGui.Reset();
        Loc.Lang = "zh";
        _applier.Pose = PoseA;
        _applier.Rig = new ActorRig([new ActorRigBone("j_kao", null, [0, 1, 0], [0, 0, 0, 1], [1, 1, 1])]);
        if (preview)
        {
            _session = new SessionInfo("image-a", "a.png", [512, 512], 1, "cpu", []);
            _result = Preview(PoseA);
        }
    }

    private static RetargetResult Preview(string pose) => new(pose, [], [],
        JsonSerializer.SerializeToElement(new { }), null,
        new Stick([new StickJoint("j_kao", [0, 1, 0], null)], []), 1, null);

    private static string ActorWithFace(string head = "0,1,0", string lid = "0,1.6,0", string description = "actor")
        => JsonSerializer.Serialize(new
        {
            Description = description,
            Bones = new Dictionary<string, object>
            {
                ["j_kao"] = new { Position = head, Rotation = "0,0,0,1", Scale = "1,1,1" },
                ["j_f_mab_l"] = new { Position = lid, Rotation = "0,0,0,1", Scale = "1,1,1" },
            },
        });

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (timeout.IsCancellationRequested) throw new TimeoutException(message);
            await Task.Delay(5);
        }
    }

    private async Task<CapturedAdvice> ClickStart()
    {
        ImGui.Presses.Add("AI 分析当前姿势");
        DrawAdvice();
        await Until(() => !_client.Calls.IsEmpty, "Explicit click did not send advice request");
        Assert(_client.Calls.TryPeek(out var call), "Missing captured advice call");
        return call!;
    }

    private async Task Complete(CapturedAdvice call)
    {
        call.Completion.SetResult(Response);
        await Until(() => _adviceTask is { IsCompleted: true }, "Advice task did not finish");
        // Suppress unrelated periodic live checks in individual test cases.
        _adviceLastPoseCheck = DateTime.UtcNow;
        PollAdvice();
    }

    public static async Task RunChecks()
    {
        var checks = new (string Name, Func<Task> Run)[]
        {
            ("Opening and polling hints never requests a model", NoAutomaticRequest),
            ("Recognition, retarget and chat activity block advice requests", BusyGuards),
            ("Missing pose sources and offline backends disable explicit requests", SourceGuards),
            ("Explicit zero-warning preview analysis captures pose, rig and copied inputs", ExplicitPreview),
            ("Reference sharing requires the advice-specific opt-in", ReferenceOptIn),
            ("Identical preview refresh preserves advice and transfers only a draft", IdenticalPreviewAndDraft),
            ("Offsets and ordinary tuning parameters invalidate captured advice", ParameterChanges),
            ("Actor, appearance, race, image, person and language switches reject old suggestions", ContextChanges),
            ("Cancel ignores a late provider reply and preserves preview", CancelLateReply),
            ("Pose changes during a provider request reject the response", PoseChangesInFlight),
            ("Live actor analysis works without a local preview", LiveActor),
            ("Live actor changes during analysis invalidate the reply", LiveChangedBeforeCompletion),
            ("Live transfer rechecks actual actor pose and never calls the model", LiveChangedBeforeTransfer),
            ("Export metadata changes do not invalidate the captured live pose", LiveMetadataIgnored),
            ("Periodic checks detect external live actor changes", LivePeriodicCheck),
            ("Missing face bones changing during model analysis invalidate preview advice", PreviewFaceChangesBeforeCompletion),
            ("Periodic checks detect external face changes merged into a preview", PreviewFacePeriodicCheck),
            ("Preview advice transfer rechecks face bones supplied by the actor", PreviewFaceChangesBeforeTransfer),
            ("Actor body overridden by preview and export metadata preserve advice", PreviewBodyOverridePreserved),
            ("Loaded neutral rig changes invalidate preview advice", PreviewRigChanges),
            ("Provider failure preserves preview and supplies an error", ProviderFailure),
            ("Disposal cancels a pending request and rejects its late success", DisposePending),
        };
        foreach (var (name, run) in checks)
        {
            await run().WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine($"PASS {name}");
        }
        Console.WriteLine($"{checks.Length} pose advice UI checks passed (production MainWindow.Advice.cs linked; controlled game/provider surfaces).");
    }

    private static Task NoAutomaticRequest()
    {
        var window = new MainWindow();
        for (var i = 0; i < 5; i++) { window.DrawAdvice(); window.PollAdvice(); }
        Assert(window._client.Calls.IsEmpty && window._applier.Calls == 0, "Viewing hints accessed model/game pose without a click");
        Assert(ImGui.Buttons["AI 分析当前姿势"], "No-warning preview analysis button was disabled");
        return Task.CompletedTask;
    }

    private static Task BusyGuards()
    {
        foreach (var busy in new Action<MainWindow>[]
        {
            w => w.ImageWorkBusy = true,
            w => w._rtBusy = true,
            w => w._rtDirty = true,
            w => w._chatTask = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task,
        })
        {
            var window = new MainWindow();
            busy(window);
            ImGui.Presses.Add("AI 分析当前姿势");
            window.DrawAdvice();
            Assert(!ImGui.Buttons["AI 分析当前姿势"], "Advice button remained enabled during pose work");
            window.StartAdvice(); // Invocation guard must also hold independently of rendering.
            Assert(window._client.Calls.IsEmpty && window._adviceTask is null, "Busy state allowed provider work");
        }
        var chatPreview = new MainWindow { _chatPoseActive = true };
        Assert(chatPreview.CurrentAdviceKey().Parameters is null, "Image tuning parameters leaked into a chat pose analysis");
        return Task.CompletedTask;
    }

    private static Task SourceGuards()
    {
        foreach (var unavailable in new Action<MainWindow>[]
        {
            w => w._actorKey = null,
            w => w._chatFreshStart = true,
            w => w.BackendOnline = false,
        })
        {
            var window = new MainWindow(preview: false);
            unavailable(window);
            ImGui.Presses.Add("AI 分析当前姿势");
            window.DrawAdvice();
            Assert(!ImGui.Buttons["AI 分析当前姿势"], "Unavailable pose/backend still enabled analysis");
            window.StartAdvice();
            Assert(window._client.Calls.IsEmpty && window._adviceTask is null, "Unavailable source started model work");
        }
        return Task.CompletedTask;
    }

    private static async Task ExplicitPreview()
    {
        var window = new MainWindow();
        window._adviceFocus = "  保持挥手，检查左肩  ";
        window._params.Offsets["j_kao"] = [1, 2, 3];
        var before = window._result;
        var call = await window.ClickStart();
        Assert(call.Request.Pose == PoseA && call.Request.ActorPose == PoseA, "Wrong preview/actor snapshot");
        Assert(call.Request.Rig == window._applier.Rig, "Actual loaded rig was not included");
        Assert(call.Request.Race == "c0101" && call.Request.SessionId == "image-a", "Race/image context was lost");
        Assert(call.Request.Focus == "保持挥手，检查左肩", "Focus was not captured/trimmed");
        Assert(call.Request.Params is not null && call.Request.Params != window._params, "Parameters were not copied");
        Assert(!ReferenceEquals(call.Request.Params!.Offsets["j_kao"], window._params.Offsets["j_kao"]), "Offsets array was shared with live UI");
        Assert(call.Request.Warnings is { Length: 0 }, "No-warning preview should still be analyzed");
        Assert(!call.Request.IncludeImage && window._config.ChatIncludeImage, "Chat image preference implicitly shared reference");
        await window.Complete(call);
        Assert(window._advice == Response && window.AdviceCurrent, "Current response was not displayed");
        Assert(ReferenceEquals(window._result, before) && window._chatPrompt == "", "Read-only advice changed preview/chat");
        window.DisposeAdvice();
    }

    private static async Task ReferenceOptIn()
    {
        foreach (var detached in new[] { false, true })
        {
            var window = new MainWindow { _adviceIncludeImage = true, _chatImageDetached = detached };
            var call = await window.ClickStart();
            Assert(call.Request.IncludeImage == !detached, "Reference opt-in ignored detached image state");
            if (detached) Assert(call.Request.SessionId is null, "Detached image session was included");
            await window.Complete(call);
            window.DisposeAdvice();
        }
    }

    private static async Task IdenticalPreviewAndDraft()
    {
        var window = new MainWindow();
        var call = await window.ClickStart();
        await window.Complete(call);
        window._result = Preview(PoseA); // A fresh RetargetResult with identical semantic inputs.
        window.PollAdvice();
        Assert(window.AdviceCurrent && window._advice == Response, "Harmless retarget discarded advice");
        window._chatPrompt = "已有草稿";
        ImGui.Presses.Add("转入对话调整");
        window.DrawAdvice();
        window.PollAdvice();
        Assert(window._chatPrompt.StartsWith("已有草稿\n\n") && window._chatPrompt.Contains(Suggestion.Recommendation), "Transfer lost draft/recommendation");
        Assert(window._chatPrompt.Contains(Suggestion.Bone!) && window._chatPrompt.Contains("头部") &&
            window._chatPrompt.Contains(Suggestion.Evidence), "Transfer discarded bone ID, body label or supporting evidence");
        Assert(window._selectTab == 5 && window._client.Calls.Count == 1 && window._chatTask is null, "Transfer requested generation instead of drafting");
        Assert(window._result!.Pose == PoseA, "Draft transfer changed pose");
        window.DisposeAdvice();
    }

    private static async Task ParameterChanges()
    {
        foreach (var change in new Action<RetargetParams>[]
        {
            p => p.Offsets["j_kao"] = [0, 5, 0],
            p => p.Amplitude["neck"] = 0.7f,
            p => p.HandStrength = 0.4f,
        })
        {
            var window = new MainWindow();
            var call = await window.ClickStart();
            await window.Complete(call);
            change(window._params);
            window.PollAdvice();
            Assert(!window.AdviceCurrent && window._adviceStale, "Changed tuning did not invalidate advice");
            window.TransferAdvice(Suggestion);
            Assert(window._chatPrompt == "" && window._client.Calls.Count == 1, "Stale tuning suggestion was transferred");
            window.DisposeAdvice();
        }
    }

    private static async Task ContextChanges()
    {
        foreach (var (label, change) in new (string, Action<MainWindow>)[]
        {
            ("actor selection", w => w._actor = 8),
            ("actor identity", w => w._actorKey = "actor-8"),
            ("appearance", w => w._currentSignature = "actor-7:c0201"),
            ("race", w => w._raceOverride = "c0901"),
            ("image", w => w._session = w._session! with { SessionId = "image-b" }),
            ("person", w => w._person = 1),
            ("language", _ => Loc.Lang = "en"),
            ("preview", w => w._result = Preview(PoseB)),
        })
        {
            var window = new MainWindow();
            var call = await window.ClickStart();
            await window.Complete(call);
            change(window);
            // Exercise click-time validation without a PollAdvice frame first.
            window.TransferAdvice(Suggestion);
            Assert(!window.AdviceCurrent && window._chatPrompt == "", $"{label}: stale advice reached chat");
            Assert(window._client.Calls.Count == 1, $"{label}: implicit new model request");
            window.DisposeAdvice();
        }
    }

    private static async Task CancelLateReply()
    {
        var window = new MainWindow();
        var before = window._result;
        var call = await window.ClickStart();
        ImGui.Presses.Add("取消分析");
        window.DrawAdvice();
        Assert(call.Token.IsCancellationRequested, "Cancel did not cancel provider token");
        await window.Complete(call);
        Assert(window._advice is null && window._adviceError == "已取消分析", "Late reply replaced cancellation state");
        Assert(ReferenceEquals(window._result, before), "Cancel changed preview");
        window.DisposeAdvice();
    }

    private static async Task PoseChangesInFlight()
    {
        var window = new MainWindow();
        var call = await window.ClickStart();
        window._result = Preview(PoseB);
        window.PollAdvice();
        Assert(call.Token.IsCancellationRequested, "Changed preview did not cancel pending analysis");
        await window.Complete(call);
        Assert(window._advice is null && window._result.Pose == PoseB, "Outdated reply replaced current state");
        window.DisposeAdvice();
    }

    private static async Task LiveActor()
    {
        var window = new MainWindow(preview: false);
        var call = await window.ClickStart();
        Assert(call.Request.Pose == PoseA && call.Request.Params is null && call.Request.Warnings is { Length: 0 }, "Live source used invented preview/parameters");
        await window.Complete(call);
        Assert(window.AdviceCurrent && window._result is null, "Live analysis required or created preview");
        window.TransferAdvice(Suggestion);
        Assert(window._chatPrompt == "", "Live transfer skipped fresh actor verification");
        window.PollAdvice();
        Assert(window._chatPrompt.Contains(Suggestion.Recommendation) && window._client.Calls.Count == 1, "Verified live transfer did not only draft");
        window.DisposeAdvice();
    }

    private static async Task LiveChangedBeforeCompletion()
    {
        var window = new MainWindow(preview: false);
        var call = await window.ClickStart();
        window._applier.Pose = PoseB;
        await window.Complete(call);
        Assert(!window.AdviceCurrent && window._adviceStale, "External Brio change during model request was missed");
        window.TransferAdvice(Suggestion);
        Assert(window._chatPrompt == "", "Outdated live actor suggestion transferred");
        window.DisposeAdvice();
    }

    private static async Task LiveChangedBeforeTransfer()
    {
        var window = new MainWindow(preview: false);
        var call = await window.ClickStart();
        await window.Complete(call);
        window._applier.Pose = PoseB;
        window.TransferAdvice(Suggestion);
        window.PollAdvice();
        Assert(window._chatPrompt == "" && !window.AdviceCurrent, "Live actor changed since analysis but transfer was accepted");
        Assert(window._client.Calls.Count == 1 && window._result is null, "Live verification called model or changed preview");
        window.DisposeAdvice();
    }

    private static async Task LiveMetadataIgnored()
    {
        var window = new MainWindow(preview: false);
        var call = await window.ClickStart();
        window._applier.Pose = PoseA.Replace("first", "new export timestamp");
        await window.Complete(call);
        Assert(window.AdviceCurrent, "Export metadata-only change invalidated identical actor bones");
        window.TransferAdvice(Suggestion);
        window.PollAdvice();
        Assert(window._chatPrompt.Contains(Suggestion.Recommendation), "Metadata-only export prevented valid transfer");
        window.DisposeAdvice();
    }

    private static async Task LivePeriodicCheck()
    {
        var window = new MainWindow(preview: false);
        var call = await window.ClickStart();
        await window.Complete(call);
        window._applier.Pose = PoseB;
        window._adviceLastPoseCheck = DateTime.MinValue;
        window.PollAdvice();
        window.PollAdvice();
        Assert(!window.AdviceCurrent && window._adviceStale, "Periodic actor check missed external pose change");
        Assert(window._client.Calls.Count == 1, "Periodic pose check triggered model request");
        window.DisposeAdvice();
    }

    private static async Task PreviewFaceChangesBeforeCompletion()
    {
        var window = new MainWindow();
        window._applier.Rig = null;
        window._applier.Pose = ActorWithFace();
        var call = await window.ClickStart();
        Assert(call.Request.ActorPose!.Contains("j_f_mab_l") && !call.Request.Pose.Contains("j_f_mab_l"),
            "Test did not capture actor-dependent face bone missing from preview");
        window._applier.Pose = ActorWithFace(lid: "0,1.61,0");
        await window.Complete(call);
        Assert(!window.AdviceCurrent && window._adviceStale, "Face changed during analysis but preview advice stayed current");
        Assert(window._result!.Pose == PoseA, "Invalidating actor-dependent face advice changed preview body");
        window.DisposeAdvice();
    }

    private static async Task PreviewFacePeriodicCheck()
    {
        var window = new MainWindow();
        window._applier.Rig = null;
        window._applier.Pose = ActorWithFace();
        var call = await window.ClickStart();
        await window.Complete(call);
        Assert(window.AdviceCurrent, "Initial merged face snapshot was not current");
        window._applier.Pose = ActorWithFace(lid: "0,1.61,0");
        window._adviceLastPoseCheck = DateTime.MinValue;
        window.PollAdvice();
        window.PollAdvice();
        Assert(!window.AdviceCurrent && window._adviceStale, "Periodic preview check ignored merged face change");
        Assert(window._client.Calls.Count == 1, "Dependency validation called the model");
        window.DisposeAdvice();
    }

    private static async Task PreviewFaceChangesBeforeTransfer()
    {
        var window = new MainWindow();
        window._applier.Rig = null;
        window._applier.Pose = ActorWithFace();
        var call = await window.ClickStart();
        await window.Complete(call);
        window._applier.Pose = ActorWithFace(lid: "0,1.61,0");
        window.TransferAdvice(Suggestion);
        Assert(window._chatPrompt == "", "Preview transfer skipped dependent actor verification");
        window.PollAdvice();
        Assert(!window.AdviceCurrent && window._chatPrompt == "", "Stale actor face suggestion was transferred through preview path");
        Assert(window._client.Calls.Count == 1, "Preview transfer automatically requested a model");
        window.DisposeAdvice();
    }

    private static async Task PreviewBodyOverridePreserved()
    {
        var window = new MainWindow();
        window._applier.Rig = null;
        window._applier.Pose = ActorWithFace();
        var call = await window.ClickStart();
        // j_kao is authoritative in PoseA; only actor-only bones supply data.
        window._applier.Pose = ActorWithFace(head: "0,2,0", description: "second export");
        await window.Complete(call);
        Assert(window.AdviceCurrent, "Actor transform already overridden by preview incorrectly invalidated response");
        window._applier.Pose = ActorWithFace(head: "0,3,0", description: "third export");
        window._adviceLastPoseCheck = DateTime.MinValue;
        window.PollAdvice();
        window.PollAdvice();
        Assert(window.AdviceCurrent, "Periodic dependency check compared overridden body or export metadata");
        window._applier.Pose = ActorWithFace(head: "0,4,0", description: "fourth export");
        window.TransferAdvice(Suggestion);
        window.PollAdvice();
        Assert(window.AdviceCurrent && window._chatPrompt.Contains(Suggestion.Evidence), "Valid preview transfer rejected unrelated actor body/metadata change");
        Assert(window._client.Calls.Count == 1 && window._result!.Pose == PoseA, "Dependency verification changed preview or sent new model call");
        window.DisposeAdvice();
    }

    private static async Task PreviewRigChanges()
    {
        var window = new MainWindow();
        var call = await window.ClickStart();
        window._applier.Rig = new ActorRig([new ActorRigBone("j_kao", null, [0, 1.1f, 0], [0, 0, 0, 1], [1, 1, 1])]);
        await window.Complete(call);
        Assert(!window.AdviceCurrent && window._adviceStale, "Loaded rig changed during analysis but advice kept old body correspondence");
        window.DisposeAdvice();
    }

    private static async Task ProviderFailure()
    {
        var window = new MainWindow();
        var before = window._result;
        var call = await window.ClickStart();
        call.Completion.SetException(new InvalidOperationException("provider unavailable"));
        await Until(() => window._adviceTask is { IsCompleted: true }, "Failed advice task did not finish");
        window.PollAdvice();
        Assert(window._adviceError == "provider unavailable" && window._advice is null, "Provider failure was hidden or produced advice");
        Assert(ReferenceEquals(window._result, before) && window._chatPrompt == "", "Provider failure changed preview/chat");
        window.DisposeAdvice();
    }

    private static async Task DisposePending()
    {
        var window = new MainWindow();
        var call = await window.ClickStart();
        window.DisposeAdvice();
        Assert(call.Token.IsCancellationRequested, "Disposal did not cancel pending provider call");
        call.Completion.SetResult(Response);
        await Until(() => window._adviceTask is { IsCompleted: true }, "Disposed advice task did not settle");
        Assert(window._adviceTask!.IsCanceled && window._advice is null, "Late success survived disposal cancellation");
    }
}
