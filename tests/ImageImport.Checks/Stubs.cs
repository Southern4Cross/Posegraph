using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using Posegraph.Backend;

namespace Dalamud.Bindings.ImGui
{
    public enum ImGuiHoveredFlags { AllowWhenDisabled }
    public static class ImGui
    {
        public static bool BeginPopup(string id) => false;
        public static bool MenuItem(string label) => false;
        public static void EndPopup() { }
        public static void TextUnformatted(string text) { }
        public static void ProgressBar(float progress, Vector2 size, string text) { }
        public static bool SmallButton(string label) => false;
        public static void TextWrapped(string text) { }
        public static void Separator() { }
        public static bool Selectable(string label, bool selected) => false;
        public static bool IsItemHovered(ImGuiHoveredFlags flags = default) => false;
        public static void SetTooltip(string text) { }
        public static void TextColored(Vector4 color, string text) { }
    }
}

namespace Dalamud.Interface.Textures.TextureWraps
{
    public interface IDalamudTextureWrap : IDisposable { }
}

namespace Dalamud.Interface.Utility.Raii
{
    public static class ImRaii
    {
        private sealed class Scope : IDisposable { public void Dispose() { } }
        public static IDisposable Disabled(bool disabled) => new Scope();
        public static IDisposable PushId(int id) => new Scope();
    }
}

namespace Posegraph.UI
{
    internal sealed record RecognitionRequest(string Name, TaskCompletionSource<SessionInfo> Completion);
    internal sealed record PreviewRequest(string SessionId, TaskCompletionSource<byte[]> Completion);
    internal sealed record TextureRequest(string SessionId, TaskCompletionSource<Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap> Completion);

    internal sealed class FakeClient : IDisposable
    {
        public readonly ConcurrentQueue<RecognitionRequest> Recognition = new();
        public readonly ConcurrentQueue<PreviewRequest> Previews = new();
        public readonly ConcurrentQueue<string> Deleted = new();
        public readonly ConcurrentQueue<int> ConfiguredParallelism = new();
        public TaskCompletionSource? ConfigurationGate;
        public int Outstanding;
        public int MaximumOutstanding;
        public bool StartedBeforeConfigurationCompleted;
        private volatile bool _configurationCompleted;
        public readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ConfigureRecognitionAsync(int parallelism, CancellationToken ct = default)
        {
            ConfiguredParallelism.Enqueue(parallelism);
            if (ConfigurationGate is { } gate) await gate.Task.WaitAsync(ct);
            _configurationCompleted = true;
        }
        public async Task<SessionInfo> CreateSessionAsync(byte[] image, string name)
        {
            if (!_configurationCompleted) StartedBeforeConfigurationCompleted = true;
            var outstanding = Interlocked.Increment(ref Outstanding);
            int previous;
            do { previous = MaximumOutstanding; }
            while (previous < outstanding && Interlocked.CompareExchange(ref MaximumOutstanding, outstanding, previous) != previous);
            var completion = new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            Recognition.Enqueue(new RecognitionRequest(name, completion));
            try { return await completion.Task; }
            finally { Interlocked.Decrement(ref Outstanding); }
        }
        // Intentionally do not honor cancellation here: the real orchestrator must
        // still reject results when a request completes after the user switches.
        public Task<byte[]> SessionImageAsync(string sid, CancellationToken ct)
        {
            var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            Previews.Enqueue(new PreviewRequest(sid, completion));
            return completion.Task;
        }
        public Task DeleteSessionAsync(string sid) { Deleted.Enqueue(sid); return Task.CompletedTask; }
        public void Dispose() => Disposed.TrySetResult();
    }

    internal sealed class FakeTexture(string id) : Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap
    {
        public string Id { get; } = id;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }

    internal sealed class FakeTextures
    {
        public readonly ConcurrentQueue<TextureRequest> Requests = new();
        public Task<Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap> CreateFromImageAsync(byte[] png)
        {
            var completion = new TaskCompletionSource<Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Enqueue(new TextureRequest(Encoding.UTF8.GetString(png), completion));
            return completion.Task;
        }
    }

    internal sealed class FakeConfig
    {
        public string LastImageDir { get; set; } = "";
        public bool PushAfterRecognize = true;
        public int BatchRecognitionParallelism = 1;
    }
    internal sealed class FakePlugin { public void SavePluginConfig(FakeConfig config) { } }
    internal sealed class FakeDialogs
    {
        public void OpenFolderDialog(string title, Action<bool, string> callback, string? directory, bool flag) { }
    }
    internal static class Loc
    {
        public static string T(string text) => text;
        public static string F(string text, params object?[] args) => string.Format(text, args);
    }
}
