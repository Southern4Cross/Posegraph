using Dalamud.Plugin.Services;
using Posegraph;

var checks = new (string Name, Func<Task> Run)[]
{
    ("Pre-canceled apply never freezes or applies", PreCanceledApply),
    ("Pre-canceled reset never resets", PreCanceledReset),
    ("Cancellation after freeze prevents first apply", CancelAfterFreeze),
    ("Cancellation after first apply prevents delayed second apply", CancelAfterFirstApply),
    ("Ordinary apply still performs both passes", OrdinaryApply),
    ("Cancellation before queued reset callback prevents reset", CancelQueuedReset),
};

foreach (var (name, run) in checks)
{
    await run().WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"{checks.Length} PoseApplier checks passed (production source linked).");

static (PoseApplier Applier, Framework Framework, BrioBridge Brio) Setup()
{
    var framework = new Framework();
    var brio = new BrioBridge();
    return (new PoseApplier(framework, new ClientState(), new TargetManager(), brio), framework, brio);
}

static async Task PreCanceledApply()
{
    var (applier, _, brio) = Setup();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await ExpectCanceled(applier.ApplyAsync(null, "pose-a", cancellation.Token));
    Assert(brio.FreezeCalls == 0 && brio.ApplyCalls == 0 && brio.ResetCalls == 0,
        "A pre-canceled apply touched the actor");
}

static async Task PreCanceledReset()
{
    var (applier, _, brio) = Setup();
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await ExpectCanceled(applier.ResetPoseAsync(null, cancellation.Token));
    Assert(brio.FreezeCalls == 0 && brio.ApplyCalls == 0 && brio.ResetCalls == 0,
        "A pre-canceled reset touched the actor");
}

static async Task CancelAfterFreeze()
{
    var (applier, framework, brio) = Setup();
    using var cancellation = new CancellationTokenSource();
    var task = applier.ApplyAsync(null, "old-selection", cancellation.Token);
    await framework.WaitForDelayAsync();
    Assert(brio.FreezeCalls == 1 && brio.ApplyCalls == 0, "Expected only the animation freeze");
    cancellation.Cancel();
    await framework.ReleaseDelayAsync(5);
    await ExpectCanceled(task);
    Assert(brio.ApplyCalls == 0, "Canceled selection applied after freeze settled");
}

static async Task CancelAfterFirstApply()
{
    var (applier, framework, brio) = Setup();
    using var cancellation = new CancellationTokenSource();
    var task = applier.ApplyAsync(null, "old-selection", cancellation.Token);
    await framework.ReleaseDelayAsync(5);
    await framework.WaitForDelayAsync();
    Assert(brio.ApplyCalls == 1, "Expected the first apply before the second-pass wait");
    cancellation.Cancel();
    await framework.ReleaseDelayAsync(30);
    await ExpectCanceled(task);
    Assert(brio.ApplyCalls == 1, "Canceled selection applied a delayed second time");
}

static async Task OrdinaryApply()
{
    var (applier, framework, brio) = Setup();
    var task = applier.ApplyAsync(null, "current-selection");
    await framework.ReleaseDelayAsync(5);
    await framework.ReleaseDelayAsync(30);
    var error = await task;
    Assert(error is null, "Ordinary apply returned an error");
    Assert(brio.FreezeCalls == 1 && brio.ApplyCalls == 2 && brio.ResetCalls == 0,
        "Ordinary apply did not freeze once and apply twice");
    Assert(brio.AppliedPoses.SequenceEqual(new[] { "current-selection", "current-selection" }),
        "The two passes did not receive the requested pose");
}

static async Task CancelQueuedReset()
{
    var (applier, framework, brio) = Setup();
    framework.HoldCallbacks = true;
    using var cancellation = new CancellationTokenSource();
    var task = applier.ResetPoseAsync(null, cancellation.Token);
    Assert(brio.ResetCalls == 0 && !task.IsCompleted, "Reset was not deferred to the framework callback");
    cancellation.Cancel();
    framework.ReleaseCallback();
    await ExpectCanceled(task);
    Assert(brio.ResetCalls == 0, "Canceled queued callback reset the actor");
}

static async Task ExpectCanceled(Task task)
{
    try { await task; }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected cancellation");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
