using System.Diagnostics;
using System.Numerics;
using System.Text;
using Dalamud.Interface.Textures.TextureWraps;
using Posegraph.Backend;

namespace Posegraph.UI;

// This partial supplies only the surrounding application and test hooks. Every
// import, polling, selection, cancellation and cleanup method is the production file.
public sealed partial class MainWindow
{
    private readonly FakeClient _client = new();
    private readonly FakeTextures _textures = new();
    private readonly FakeConfig _config = new();
    private readonly FakePlugin _pi = new();
    private readonly FakeDialogs _fileDialog = new();
    private SessionInfo? _session;
    private IDalamudTextureWrap? _texture;
    private object? _result;
    private string? _selected;
    private RetargetParams _params = new();
    private int _person;
    private int? _selectTab;
    private volatile bool _recognizing;
    private bool _rtDirty, _rtPush;
    private static readonly Vector4 Red = new(1, 0, 0, 1), Yellow = new(1, 1, 0, 1), Green = new(0, 1, 0, 1), Grey = new(.5f);
    private bool BackendOnline => true;
    private readonly List<(string SessionId, bool Push)> _retargets = [];
    private void RequestRetarget(bool push) => _retargets.Add((_session!.SessionId, push));
    private string _status = "";
    private void SetStatus(string message, Vector4? color = null) => _status = message;
    private void SetError(string message) => _status = message;
    private static bool IsImage(string path) => ImageFiles.IsImage(path);
    private static string DeviceLabel(string? device) => device ?? "unknown";
    private void OpenImageDialog() { }

    public static async Task RunChecks()
    {
        await DefaultSerial();
        await ParallelCapAndOutOfOrder();
        await ParallelismBounds();
        await ParallelismSnapshot();
        await DisposeMultipleRecognitions();
        await ConfigurationFailureAndStop();
        await DisposePendingConfiguration();
        await BatchAndSelection();
        await SingleAutoLoad();
        await RapidSwitch();
        await ReplaceBatch();
        await StopAndRetry();
        await RetryCannotOverrideExplicitSelection();
        await DisposePendingTexture();
        await DisposePendingRecognition();
        await DisposePendingSources();
        Console.WriteLine("Image orchestration checks passed (production MainWindow.Images.cs linked).");
    }

    private void Begin(params string[] names)
        => BeginImageBatch(names.Select(name => new ImportedImage(name + ".png", bytes: [1])).ToArray());

    private async Task Until(Func<bool> condition, string description)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            PollImages();
            if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new Exception("Timed out: " + description);
            await Task.Delay(1);
        }
    }

    private async Task<RecognitionRequest> Recognition()
    {
        await Until(() => !_client.Recognition.IsEmpty, "recognition request");
        _client.Recognition.TryDequeue(out var request);
        return request!;
    }
    private static SessionInfo Session(string name) => new("sid-" + name, name, [32, 32], 10, "cpu", []);
    private static void Complete(RecognitionRequest request) => request.Completion.SetResult(Session(request.Name));

    private async Task<TextureRequest> ReachTexture(string expectedSid)
    {
        await Until(() => !_client.Previews.IsEmpty, "selected image preview request");
        _client.Previews.TryDequeue(out var preview);
        Check(preview!.SessionId == expectedSid, "downloads only the explicitly selected session");
        preview.Completion.SetResult(Encoding.UTF8.GetBytes(expectedSid));
        await Until(() => !_textures.Requests.IsEmpty, "texture creation request");
        _textures.Requests.TryDequeue(out var texture);
        return texture!;
    }

    private async Task LoadSelected(string name)
    {
        SelectImage(_images.Single(i => i.Name == name + ".png"));
        var request = await ReachTexture("sid-" + name);
        request.Completion.SetResult(new FakeTexture(request.SessionId));
        await Until(() => _session?.SessionId == "sid-" + name, "selected session commit");
    }

    private async Task FinishBatch(params string[] names)
    {
        foreach (var name in names)
        {
            var request = await Recognition();
            Check(request.Name == name, "recognition remains sequential in import order");
            Complete(request);
        }
        await Until(() => !_recognizing, "batch completion");
    }

    private async Task<RecognitionRequest[]> DrainRequests(int count)
    {
        await Until(() => _client.Recognition.Count >= count, $"{count} recognition requests");
        var requests = new List<RecognitionRequest>();
        while (_client.Recognition.TryDequeue(out var request)) requests.Add(request);
        Check(requests.Count == count, "scheduler fills available slots without exceeding its limit");
        return requests.ToArray();
    }

    private async Task Close()
    {
        _texture?.Dispose(); // MainWindow.Dispose owns the active preview.
        DisposeImages();
        await _client.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task DefaultSerial()
    {
        var window = new MainWindow();
        window.Begin("serial-a", "serial-b", "serial-c");
        var first = await window.Recognition();
        for (var i = 0; i < 4; i++) { window.PollImages(); await Task.Delay(1); }
        Check(window._config.BatchRecognitionParallelism == 1 && window._client.Outstanding == 1 && window._client.Recognition.IsEmpty,
              "default multi-image recognition has exactly one outstanding request");
        Complete(first);
        await window.FinishBatch("serial-b", "serial-c");
        Check(window._client.MaximumOutstanding == 1 && window._client.ConfiguredParallelism.SequenceEqual([1]),
              "default stays serial and configures the backend once per batch");
        await window.Close();
    }

    private static async Task ParallelCapAndOutOfOrder()
    {
        var window = new MainWindow();
        window._config.BatchRecognitionParallelism = 3;
        window._client.ConfigurationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Begin("a", "b", "c", "d", "e", "f");
        await window.Until(() => !window._client.ConfiguredParallelism.IsEmpty, "backend parallelism configuration");
        Check(window._client.Recognition.IsEmpty, "recognition waits for backend concurrency configuration to finish");
        window._client.ConfigurationGate.SetResult();
        var first = (await window.DrainRequests(3)).ToDictionary(r => r.Name);
        Check(first.Keys.Order().SequenceEqual(new[] { "a", "b", "c" }), "parallel scheduling starts the first N selected images");
        Complete(first["b"]);
        var d = await window.Recognition();
        Check(d.Name == "d" && window._images[1].Session?.SessionId == "sid-b",
              "out-of-order completion is assigned to its original list item and frees one slot");
        Check(window._selectedImage is null && window._client.Previews.IsEmpty && window._retargets.Count == 0,
              "completed parallel items stay unloaded until the user explicitly chooses one");
        window.SelectImage(window._images[0]);
        Check(window._selectedImage is null && window._client.Previews.IsEmpty,
              "a still-recognizing item cannot be selected or loaded");
        await window.LoadSelected("b");
        var selectedTexture = window._texture;
        Check(window._recognizing && window.ImageImportBusy &&
              window._client.Outstanding == 3 && window._retargets.SequenceEqual([("sid-b", true)]),
              "a completed image loads and retargets while other parallel recognition jobs continue");
        first["a"].Completion.SetException(new IOException("parallel failure"));
        var e = await window.Recognition();
        Check(e.Name == "e", "one failed parallel request does not block the queue");
        Complete(e);
        var f = await window.Recognition();
        Complete(f);
        Complete(first["c"]);
        Complete(d);
        await window.Until(() => !window._recognizing, "parallel batch completion");
        Check(window._client.MaximumOutstanding == 3 && !window._client.StartedBeforeConfigurationCompleted &&
              window._images.Count(i => i.Session is not null) == 5 && window._images[0].Error == "parallel failure" &&
              window._images.Where(i => i.Session is not null).All(i => i.Session!.Name + ".png" == i.Name),
              "configured concurrency is capped and mixed completion order preserves per-image results");
        Check(window._client.ConfiguredParallelism.SequenceEqual([3]) && window._retargets.SequenceEqual([("sid-b", true)]) &&
              window._session?.SessionId == "sid-b" && ReferenceEquals(window._texture, selectedTexture) && window._client.Previews.IsEmpty,
              "later completions and failures preserve the chosen preview without loading or applying unselected results");
        await window.Close();
    }

    private static async Task ParallelismBounds()
    {
        foreach (var (configured, expected) in new[] { (-4, 1), (99, 8) })
        {
            var window = new MainWindow();
            window._config.BatchRecognitionParallelism = configured;
            window.Begin(Enumerable.Range(0, 10).Select(i => "bound-" + i).ToArray());
            var requests = await window.DrainRequests(expected);
            Check(window._client.ConfiguredParallelism.SequenceEqual([expected]) && window._client.Outstanding == expected,
                  $"stored parallelism {configured} is clamped to {expected}");
            window._stopImageBatch = true;
            foreach (var request in requests) Complete(request);
            await window.Until(() => !window._recognizing, "all in-flight requests finish after stop");
            Check(window._images.Count(i => i.Skipped) == 10 - expected && window._client.Recognition.IsEmpty,
                  "stop drains all in-flight requests and skips every queued request");
            await window.Close();
        }
    }

    private static async Task ParallelismSnapshot()
    {
        var window = new MainWindow();
        window._config.BatchRecognitionParallelism = 2;
        window.Begin("snap-a", "snap-b", "snap-c", "snap-d");
        var first = await window.DrainRequests(2);
        window._config.BatchRecognitionParallelism = 1;
        foreach (var request in first) Complete(request);
        var second = await window.DrainRequests(2);
        foreach (var request in second) Complete(request);
        await window.Until(() => !window._recognizing, "snapshot batch completion");
        Check(window._client.ConfiguredParallelism.SequenceEqual([2]) && window._client.MaximumOutstanding == 2,
              "changing settings during a batch keeps its original concurrency snapshot");
        window._client.MaximumOutstanding = 0;
        window.Begin("next-a", "next-b");
        await window.FinishBatch("next-a", "next-b");
        Check(window._client.ConfiguredParallelism.SequenceEqual([2, 1]) && window._client.MaximumOutstanding == 1,
              "the next batch picks up the changed parallelism setting");
        await window.Close();
    }

    private static async Task DisposeMultipleRecognitions()
    {
        var window = new MainWindow();
        window._config.BatchRecognitionParallelism = 3;
        window.Begin("dispose-a", "dispose-b", "dispose-c", "never-started");
        var requests = await window.DrainRequests(3);
        window.DisposeImages();
        Complete(requests[0]);
        requests[1].Completion.SetException(new IOException("shutdown inference failure"));
        Check(!window._client.Disposed.Task.IsCompleted, "disposal keeps the HTTP client alive until all parallel requests finish");
        Complete(requests[2]);
        await window._client.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(requests.Where((_, index) => index != 1).All(r => window._client.Deleted.Contains("sid-" + r.Name)) &&
              window._client.Recognition.IsEmpty && window._client.Outstanding == 0,
              "disposal drains every parallel request, deletes each successful session and starts no queued work");
    }

    private static async Task ConfigurationFailureAndStop()
    {
        var failed = new MainWindow();
        failed._client.ConfigurationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        failed.Begin("configure-a", "configure-b");
        await failed.Until(() => !failed._client.ConfiguredParallelism.IsEmpty, "configuration request before failure");
        failed._client.ConfigurationGate.SetException(new IOException("backend rejected configuration"));
        await failed.Until(() => !failed._recognizing, "failed configuration handling");
        Check(failed._images.All(i => i.Error?.Contains("backend rejected configuration") == true) &&
              failed._client.Recognition.IsEmpty && failed._client.Previews.IsEmpty,
              "failed concurrency configuration marks queued images failed and starts no recognition");
        await failed.Close();

        var stopped = new MainWindow();
        stopped._config.BatchRecognitionParallelism = 2;
        stopped._client.ConfigurationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        stopped.Begin("stop-before-a", "stop-before-b");
        await stopped.Until(() => !stopped._client.ConfiguredParallelism.IsEmpty, "configuration request before stop");
        stopped._stopImageBatch = true;
        stopped._client.ConfigurationGate.SetResult();
        await stopped.Until(() => !stopped._recognizing, "stop during configuration");
        Check(stopped._images.All(i => i.Skipped) && stopped._client.Recognition.IsEmpty,
              "stop during configuration skips the whole queue before any inference starts");
        await stopped.Close();
    }

    private static async Task DisposePendingConfiguration()
    {
        var window = new MainWindow();
        window._client.ConfigurationGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Begin("config-inflight", "config-queued");
        await window.Until(() => !window._client.ConfiguredParallelism.IsEmpty, "pending configuration before disposal");
        window.DisposeImages();
        Check(!window._client.Disposed.Task.IsCompleted, "disposal waits for an in-flight configuration request");
        window._client.ConfigurationGate.SetResult();
        await window._client.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(window._client.Recognition.IsEmpty, "late configuration completion cannot start work after disposal");
    }

    private static async Task BatchAndSelection()
    {
        var window = new MainWindow();
        window.Begin("a", "bad", "c");
        Complete(await window.Recognition());
        var bad = await window.Recognition();
        window.SelectImage(window._images[1]);
        Check(window._selectedImage is null && window._client.Previews.IsEmpty,
              "unrecognized items cannot be selected or loaded");
        Check(window._client.Previews.IsEmpty && window._textures.Requests.IsEmpty && window._retargets.Count == 0,
              "multi-image pre-recognition does not automatically download, allocate or apply a completed image");
        await window.LoadSelected("a");
        var activeTexture = window._texture;
        Check(window._recognizing && window._retargets.SequenceEqual([("sid-a", true)]),
              "a ready image can be loaded and applied during serial recognition of the remaining queue");
        bad.Completion.SetException(new IOException("broken image"));
        var last = await window.Recognition();
        window.SelectImage(window._images[1]);
        Check(window._session?.SessionId == "sid-a" && ReferenceEquals(window._texture, activeTexture),
              "selecting a failed item leaves the active ready preview intact");
        Complete(last);
        await window.Until(() => !window._recognizing, "failed image followed by completion");
        Check(window._images.Count(i => i.Session is not null) == 2 && window._images[1].Error == "broken image",
              "bad image is recorded and remaining images still finish");
        Check(window._client.Previews.IsEmpty && window._textures.Requests.IsEmpty && window._retargets.SequenceEqual([("sid-a", true)]) &&
              window._session?.SessionId == "sid-a" && ReferenceEquals(window._texture, activeTexture),
              "finishing a batch does not replace or reapply the active ready preview");
        await window.LoadSelected("c");
        Check(window._retargets.SequenceEqual([("sid-a", true), ("sid-c", true)]),
              "switching to another image retargets only that explicit selection");
        await window.Close();
    }

    private static async Task SingleAutoLoad()
    {
        var window = new MainWindow();
        window._config.BatchRecognitionParallelism = 8;
        window.Begin("single");
        Complete(await window.Recognition());
        var texture = await window.ReachTexture("sid-single");
        texture.Completion.SetResult(new FakeTexture("single"));
        await window.Until(() => window._session is not null, "single image auto load");
        Check(window._retargets.SequenceEqual([("sid-single", true)]) && window._client.ConfiguredParallelism.SequenceEqual([1]),
              "single-image import keeps automatic loading and configures only one inference slot");
        await window.Close();
    }

    private static async Task RapidSwitch()
    {
        var window = new MainWindow();
        window.Begin("old", "new", "still-recognizing");
        Complete(await window.Recognition());
        Complete(await window.Recognition());
        var recognition = await window.Recognition();
        window.SelectImage(window._images[0]);
        var staleRequest = await window.ReachTexture("sid-old");
        var oldPoseToken = window._imagePoseCts.Token;
        window.SelectImage(window._images[1]);
        Check(oldPoseToken.IsCancellationRequested, "switching images cancels the previous pose application generation");
        var currentRequest = await window.ReachTexture("sid-new");
        var currentTexture = new FakeTexture("current");
        currentRequest.Completion.SetResult(currentTexture);
        await window.Until(() => window._session?.SessionId == "sid-new", "newest selection commit");
        var staleTexture = new FakeTexture("stale");
        staleRequest.Completion.SetResult(staleTexture);
        await window.Until(() => staleTexture.Disposed, "stale texture disposal");
        Check(window._recognizing && ReferenceEquals(window._texture, currentTexture) && window._retargets.SequenceEqual([("sid-new", true)]),
              "while recognition continues, late old texture is disposed and cannot replace or retarget the current selection");
        Complete(recognition);
        await window.Until(() => !window._recognizing, "background batch completion after rapid preview switches");
        Check(ReferenceEquals(window._texture, currentTexture) && window._retargets.SequenceEqual([("sid-new", true)]),
              "background batch completion preserves the most recent explicit selection");
        await window.Close();
    }

    private static async Task ReplaceBatch()
    {
        var window = new MainWindow();
        window.Begin("a", "b");
        await window.FinishBatch("a", "b");
        await window.LoadSelected("a");
        var oldTexture = (FakeTexture)window._texture!;
        window._person = 3;
        window._selected = "old-joint";
        window._rtDirty = window._rtPush = true;
        window.Begin("replacement", "second");
        Check(window._session is null && window._result is null && oldTexture.Disposed &&
              window._selected is null && !window._rtDirty && !window._rtPush && window._selectTab == 0,
              "replacing a batch clears the loaded session and disposes its preview");
        Check(window._client.Deleted.Contains("sid-a") && window._client.Deleted.Contains("sid-b"),
              "replacing a batch deletes all previous recognized sessions");
        await window.FinishBatch("replacement", "second");
        await window.Close();
    }

    private static async Task StopAndRetry()
    {
        var window = new MainWindow();
        window.Begin("recognized", "skipped", "also-skipped");
        var inflight = await window.Recognition();
        window._stopImageBatch = true;
        Complete(inflight);
        await window.Until(() => !window._recognizing, "stopped batch completion");
        Check(window._images[0].Session is not null && window._images.Skip(1).All(i => i.Skipped) &&
              window._client.Recognition.IsEmpty,
              "stop preserves current recognition and skips queued images");
        window.RecognizeImageAgain(window._images[1]);
        Complete(await window.Recognition());
        var request = await window.ReachTexture("sid-skipped");
        request.Completion.SetResult(new FakeTexture("retry"));
        await window.Until(() => window._session?.SessionId == "sid-skipped", "retry preview commit");
        Check(!window._images[1].Skipped && window._person == 0 && window._retargets.SequenceEqual([("sid-skipped", true)]),
              "retry recognizes the requested item, resets person selection and loads its result");
        await window.Close();
    }

    private static async Task RetryCannotOverrideExplicitSelection()
    {
        foreach (var retrySucceeds in new[] { true, false })
        {
            var window = new MainWindow();
            window.Begin("ready", "retry");
            Complete(await window.Recognition());
            (await window.Recognition()).Completion.SetException(new IOException("first recognition failed"));
            await window.Until(() => !window._recognizing, "initial retry fixture completion");
            window.RecognizeImageAgain(window._images[1]);
            var retry = await window.Recognition();
            await window.LoadSelected("ready");
            var readyTexture = window._texture;
            window.SetStatus("Applied selected ready pose");
            window.PollImages();
            Check(window._recognizing && window._status == "Applied selected ready pose",
                  "background retry progress preserves feedback for the explicit ready selection");
            if (retrySucceeds) Complete(retry);
            else retry.Completion.SetException(new IOException("retry recognition failed"));
            await window.Until(() => !window._recognizing, "retry completion after ready-image selection");
            Check(window._session?.SessionId == "sid-ready" && ReferenceEquals(window._texture, readyTexture) &&
                  window._retargets.SequenceEqual([("sid-ready", true)]) && window._client.Previews.IsEmpty &&
                  window._status == "Applied selected ready pose",
                  $"retry {(retrySucceeds ? "success" : "failure")} cannot override or reapply the explicit ready selection");
            await window.Close();
        }
    }

    private static async Task DisposePendingTexture()
    {
        var window = new MainWindow();
        window.Begin("first", "second");
        await window.FinishBatch("first", "second");
        window.SelectImage(window._images[0]);
        var pending = await window.ReachTexture("sid-first");
        window.DisposeImages();
        var texture = new FakeTexture("pending");
        pending.Completion.SetResult(texture);
        await window._client.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(texture.Disposed && window._client.Deleted.Contains("sid-first") && window._client.Deleted.Contains("sid-second"),
              "disposal drains pending textures and deletes all sessions before closing the client");
    }

    private static async Task DisposePendingRecognition()
    {
        var window = new MainWindow();
        window.Begin("inflight", "queued");
        var pending = await window.Recognition();
        window.DisposeImages();
        Complete(pending);
        await window._client.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(window._client.Deleted.Contains("sid-inflight") && window._client.Recognition.IsEmpty,
              "disposal deletes an inference that finishes late and does not start queued images");
    }

    private static async Task DisposePendingSources()
    {
        var window = new MainWindow();
        var files = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        window._imageFilesTask = files.Task;
        window.DisposeImages();
        Check(!window._client.Disposed.Task.IsCompleted, "disposal waits for pending folder work");
        files.SetException(new IOException("enumeration failed after shutdown"));
        await window._client.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(window._client.Disposed.Task.IsCompletedSuccessfully, "disposal observes source failures and cancellation");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new Exception("FAILED: " + description);
        Console.WriteLine("PASS: " + description);
    }
}
