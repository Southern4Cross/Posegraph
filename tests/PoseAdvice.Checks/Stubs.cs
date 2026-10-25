using System.Collections.Concurrent;
using System.Numerics;
using Posegraph.Backend;

namespace Dalamud.Bindings.ImGui
{
    // The production partial runs unchanged. Only its UI/network/game surfaces
    // are replaced; disabled scopes prevent fake presses as real ImGui does.
    public static class ImGui
    {
        public static int DisabledDepth;
        public static readonly HashSet<string> Presses = [];
        public static readonly Dictionary<string, bool> Buttons = [];
        public static readonly List<string> Text = [];
        public static void Reset() { DisabledDepth = 0; Presses.Clear(); Buttons.Clear(); Text.Clear(); }
        public static bool Button(string label)
        {
            Buttons[label] = DisabledDepth == 0;
            return DisabledDepth == 0 && Presses.Remove(label);
        }
        public static bool SmallButton(string label) => Button(label);
        public static void TextDisabled(string text) => Text.Add(text);
        public static void TextWrapped(string text) => Text.Add(text);
        public static void TextColored(Vector4 color, string text) => Text.Add(text);
        public static bool InputText(string label, ref string value, uint maximum) => false;
        public static bool Checkbox(string label, ref bool value) => false;
        public static void SameLine() { }
        public static void Separator() { }
        public static void Spacing() { }
    }
}

namespace Dalamud.Interface.Utility.Raii
{
    public static class ImRaii
    {
        private sealed class Scope(bool disabled) : IDisposable
        {
            public void Dispose() { if (disabled) Dalamud.Bindings.ImGui.ImGui.DisabledDepth--; }
        }
        public static IDisposable Disabled(bool disabled)
        {
            if (disabled) Dalamud.Bindings.ImGui.ImGui.DisabledDepth++;
            return new Scope(disabled);
        }
        public static IDisposable PushId(int id) => new Scope(false);
    }
}

namespace Posegraph
{
    public static class Loc
    {
        public static string Lang { get; set; } = "zh";
        public static string T(string value) => value;
        public static string F(string value, params object?[] args) => string.Format(value, args);
    }

    public sealed class PoseApplier
    {
        public sealed record ActorSnapshot(string Pose, ActorRig? Rig);
        public string? Pose;
        public ActorRig? Rig;
        public int Calls;
        public readonly ConcurrentQueue<Task<ActorSnapshot?>> Planned = new();
        public Task<ActorSnapshot?> GetSnapshotAsync(int? actor)
        {
            Interlocked.Increment(ref Calls);
            return Planned.TryDequeue(out var task) ? task
                : Task.FromResult(Pose is null ? null : new ActorSnapshot(Pose, Rig));
        }
    }
}

namespace Posegraph.UI
{
    internal sealed record CapturedAdvice(PoseAdviceRequest Request, CancellationToken Token,
        TaskCompletionSource<PoseAdviceResponse> Completion);

    internal sealed class FakeClient
    {
        public readonly ConcurrentQueue<CapturedAdvice> Calls = new();
        public Task<PoseAdviceResponse> PoseAdviceAsync(PoseAdviceRequest request, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<PoseAdviceResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            Calls.Enqueue(new CapturedAdvice(request, ct, completion));
            // Deliberately ignore cancellation. Production orchestration must
            // reject a provider response that arrives after cancel/switch.
            return completion.Task;
        }
    }

    internal sealed class FakeConfig
    {
        public string ChatApiEndpoint = "https://provider.example/v1";
        public string ChatModel = "configured-model";
        public bool ChatIncludeImage = true;
    }
}
