using System.Collections.Concurrent;
using Dalamud.Game.ClientState.Objects.Types;

namespace Dalamud.Game.ClientState.Objects.Types
{
    public sealed record ObjectName(string TextValue);
    public interface IGameObject
    {
        int ObjectIndex { get; }
        string ObjectKind { get; }
        uint BaseId { get; }
        ObjectName Name { get; }
    }
    public interface ICharacter : IGameObject
    {
        byte[] Customize { get; }
    }
    public sealed class GameObject : ICharacter
    {
        public int ObjectIndex => 1;
        public string ObjectKind => "Player";
        public uint BaseId => 0;
        public ObjectName Name => new("Test actor");
        public byte[] Customize => [];
    }
}

namespace Dalamud.Plugin.Services
{
    public interface IFramework
    {
        Task<T> RunOnFrameworkThread<T>(Func<T> callback);
        Task DelayTicks(int ticks);
    }
    public interface IClientState
    {
        bool IsGPosing { get; }
    }
    public interface ITargetManager
    {
        IGameObject? GPoseTarget { get; }
    }

    public sealed class ClientState : IClientState
    {
        public bool IsGPosing => true;
    }
    public sealed class TargetManager : ITargetManager
    {
        public IGameObject? GPoseTarget { get; } = new GameObject();
    }

    /// <summary>Deterministic scheduler: callbacks and tick waits can be released separately.</summary>
    public sealed class Framework : IFramework
    {
        private sealed record Delay(int Ticks, TaskCompletionSource Completion);
        private readonly ConcurrentQueue<Delay> _delays = new();
        private readonly ConcurrentQueue<Action> _callbacks = new();
        private readonly SemaphoreSlim _delayAdded = new(0);
        public bool HoldCallbacks { get; set; }

        public Task<T> RunOnFrameworkThread<T>(Func<T> callback)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Invoke()
            {
                try { completion.SetResult(callback()); }
                catch (OperationCanceledException ex) { completion.SetCanceled(ex.CancellationToken); }
                catch (Exception ex) { completion.SetException(ex); }
            }
            if (HoldCallbacks) _callbacks.Enqueue(Invoke);
            else Invoke();
            return completion.Task;
        }

        public Task DelayTicks(int ticks)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _delays.Enqueue(new Delay(ticks, completion));
            _delayAdded.Release();
            return completion.Task;
        }

        public async Task ReleaseDelayAsync(int expectedTicks)
        {
            if (!await _delayAdded.WaitAsync(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException($"No {expectedTicks}-tick delay was scheduled");
            if (!_delays.TryDequeue(out var delay) || delay.Ticks != expectedTicks)
                throw new InvalidOperationException($"Expected a {expectedTicks}-tick delay");
            delay.Completion.SetResult();
        }

        public async Task WaitForDelayAsync()
        {
            if (!await _delayAdded.WaitAsync(TimeSpan.FromSeconds(5)))
                throw new InvalidOperationException("No delay was scheduled");
            _delayAdded.Release();
        }

        public void ReleaseCallback()
        {
            if (!_callbacks.TryDequeue(out var callback))
                throw new InvalidOperationException("No framework callback was scheduled");
            callback();
        }
    }
}

namespace Posegraph
{
    public static class Loc
    {
        public static string T(string text) => text;
    }
    public static class Races
    {
        public static string? FromModel(IGameObject _) => "c0101";
        public static string? FromCharacter(IGameObject _) => "c0101";
    }
    // These scheduler/apply checks do not read native game memory. The actual
    // ActorRigReader requires the live FFXIV skeleton; represent its unavailable
    // result here while linking the production snapshot DTOs.
    public static class ActorRigReader
    {
        public static Backend.ActorRig? Read(IGameObject actor, string pose) => null;
    }
    public sealed class BrioBridge
    {
        public int FreezeCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public int ResetCalls { get; private set; }
        public List<string> AppliedPoses { get; } = [];
        public IGameObject[] GetActors() => [new GameObject()];
        public string? GetPose(IGameObject _) => "{}";
        public bool Freeze(IGameObject _) { FreezeCalls++; return true; }
        public bool ApplyPose(IGameObject _, string pose)
        {
            ApplyCalls++;
            AppliedPoses.Add(pose);
            return true;
        }
        public bool ResetPoseKeepTransform(IGameObject _) { ResetCalls++; return true; }
    }
}
