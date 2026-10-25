using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Posegraph;
using Posegraph.Backend;

var checks = new (string Name, Func<Task> Run)[]
{
    ("Clamps values below one and posts only recognition_parallelism", ClampLowerBound),
    ("Clamps values above eight", ClampUpperBound),
    ("Accepts matching backend confirmation for each concurrency 1 through 8", MatchingConfirmation),
    ("Rejects missing parallelism confirmation above one", MissingConfirmation),
    ("Rejects mismatched parallelism confirmation above one", MismatchedConfirmation),
    ("Rejects malformed parallelism confirmation above one", MalformedConfirmation),
    ("Accepts default one on older backend without confirmation", DefaultCompatible),
    ("Propagates non-success HTTP status and backend detail", BackendError),
    ("Sends current Accept-Language on every configuration request", LanguageHeader),
    ("Posts advice pose, anatomy rig, parameters and explicit reference opt-in", AdvicePayload),
    ("Advice defaults do not opt into sending a reference image", AdviceDefaults),
    ("Maps advice evidence, joint suggestions and limitations", AdviceResponse),
    ("Cancels an advice request already received by the backend", AdviceCancellation),
    ("Distinguishes an absent advice route from a missing reference session", AdviceErrorDistinction),
    ("Rejects a null advice result", AdviceEmptyResponse),
};

foreach (var (name, run) in checks)
{
    await run().WaitAsync(TimeSpan.FromSeconds(20));
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"{checks.Length} BackendClient checks passed (production source linked, real loopback HTTP).");

static string Confirmation(int count)
    => JsonSerializer.Serialize(new { model = new { config = new { recognitionParallelism = count } } });

static async Task<(Exception? Error, CapturedRequest Request)> Exchange(int parallelism, string response, int status = 200)
{
    using var backend = new LocalBackend(new MockResponse(status, response));
    using var client = new BackendClient(() => backend.Port);
    Exception? error = null;
    try { await client.ConfigureRecognitionAsync(parallelism).WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (Exception ex) { error = ex; }
    var request = (await backend.Requests)[0];
    Assert(request.Method == "POST" && request.Path == "/model/config", "Unexpected request method/path");
    Assert(request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true,
        "Configuration request was not JSON");
    Assert(request.Language == Loc.Lang, "Configuration request lost Accept-Language");
    return (error, request);
}

static void AssertPayload(CapturedRequest request, int expected)
{
    using var body = JsonDocument.Parse(request.Body);
    Assert(body.RootElement.GetProperty("recognition_parallelism").GetInt32() == expected,
        $"Expected serialized recognition_parallelism={expected}");
    Assert(body.RootElement.EnumerateObject().Count() == 1,
        "Recognition configuration unexpectedly overwrote other backend settings");
}

static void AssertUnsupported(Exception? error)
{
    Assert(error is InvalidOperationException && error.Message == "当前后端不支持并行识别，请更新并重启后端",
        $"Expected backend upgrade error, got {error?.ToString() ?? "success"}");
}

static async Task ClampLowerBound()
{
    var (error, request) = await Exchange(int.MinValue, Confirmation(1));
    Assert(error is null, "Clamped default was rejected");
    AssertPayload(request, 1);
}

static async Task ClampUpperBound()
{
    var (error, request) = await Exchange(int.MaxValue, Confirmation(8));
    Assert(error is null, "Clamped maximum was rejected");
    AssertPayload(request, 8);
}

static async Task MatchingConfirmation()
{
    for (var count = 1; count <= 8; count++)
    {
        var (error, request) = await Exchange(count, Confirmation(count));
        Assert(error is null, $"Matching confirmation for {count} was rejected: {error}");
        AssertPayload(request, count);
    }
}

static async Task MissingConfirmation()
{
    foreach (var response in new[] { "{}", "{\"model\":{}}", "{\"model\":{\"config\":{}}}" })
    {
        var (error, request) = await Exchange(3, response);
        AssertUnsupported(error);
        AssertPayload(request, 3);
    }
}

static async Task MismatchedConfirmation()
{
    var (error, _) = await Exchange(4, Confirmation(1));
    AssertUnsupported(error);
}

static async Task MalformedConfirmation()
{
    foreach (var value in new[] { "\"3\"", "null", "true", "3.5" })
    {
        var (error, _) = await Exchange(3, "{\"model\":{\"config\":{\"recognitionParallelism\":" + value + "}}}");
        AssertUnsupported(error);
    }
}

static async Task DefaultCompatible()
{
    var (error, request) = await Exchange(1, "{\"model\":{\"config\":{}}}");
    Assert(error is null, $"Default sequential mode rejected older backend: {error}");
    AssertPayload(request, 1);
}

static async Task BackendError()
{
    var (error, _) = await Exchange(4, "{\"detail\":\"invalid recognition_parallelism\"}", 422);
    Assert(error is InvalidOperationException && error.Message.Contains("422") &&
        error.Message.Contains("invalid recognition_parallelism"), "Backend status/detail were not preserved");
}

static async Task LanguageHeader()
{
    using var backend = new LocalBackend(new MockResponse(200, Confirmation(1)), new MockResponse(200, Confirmation(2)));
    using var client = new BackendClient(() => backend.Port);
    var original = Loc.Lang;
    try
    {
        Loc.Lang = "zh";
        await client.ConfigureRecognitionAsync(1);
        Loc.Lang = "en";
        await client.ConfigureRecognitionAsync(2);
        var requests = await backend.Requests;
        Assert(requests[0].Language == "zh" && requests[1].Language == "en",
            "Accept-Language did not follow the current UI language on the same client");
    }
    finally { Loc.Lang = original; }
}

static PoseAdviceRequest BasicAdviceRequest()
    => new("{\"Bones\":{}}", "c0101", new PoseChatProvider("https://model.example/v1", "test-key", "test-model"));

static string AdviceJson()
    => """
       {"summary":"The pose keeps the left hand near the head.","suggestions":[
         {"bone":"j_kubi","issue":"Neck tilt","evidence":"The head and neck tilt in the same direction.","recommendation":"Reduce neck tilt while keeping the hand placement."},
         {"bone":null,"issue":"Intent unclear","evidence":"No camera view was supplied.","recommendation":"Check the silhouette in the game view."}
       ],"limitations":["No rendered character screenshot was supplied."]}
       """;

static async Task<(PoseAdviceResponse? Result, Exception? Error, CapturedRequest Request)> ExchangeAdvice(
    PoseAdviceRequest payload, string response, int status = 200)
{
    using var backend = new LocalBackend(new MockResponse(status, response));
    using var client = new BackendClient(() => backend.Port);
    PoseAdviceResponse? result = null;
    Exception? error = null;
    try { result = await client.PoseAdviceAsync(payload, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)); }
    catch (Exception ex) { error = ex; }
    var request = (await backend.Requests)[0];
    Assert(request.Method == "POST" && request.Path == "/chat/advice", "Advice used an unexpected method/path");
    Assert(request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true,
        "Advice request was not JSON");
    Assert(request.Language == Loc.Lang, "Advice request lost Accept-Language");
    return (result, error, request);
}

static async Task AdvicePayload()
{
    const string pose = "{\"Bones\":{\"j_kao\":{\"Position\":\"0,1.7,0\",\"Rotation\":\"0,0,0,1\",\"Scale\":\"1,1,1\"}}}";
    const string actorPose = "{\"Bones\":{\"j_f_mab_l\":{\"Position\":\"0.02,1.72,0.1\"}}}";
    var parameters = new RetargetParams
    {
        Amplitude = new() { ["neck"] = 0.7f },
        Offsets = new() { ["j_kao"] = [0, 8, 0] },
        HandStrength = 0.8f,
        Ground = false,
    };
    var rig = new ActorRig([
        new ActorRigBone("j_kao", "j_kubi", [0, 0.1f, 0], [0, 0, 0, 1], [1, 1, 1]),
        new ActorRigBone("j_f_mab_l", "j_kao", [0.02f, 0.02f, 0.1f], [0, 0, 0, 1], [1, 1, 1]),
    ]);
    var warning = new PoseWarning("j_kubi", "warn", 41, 35, "颈部转动过大", "neck", 0.7f);
    var payload = new PoseAdviceRequest(pose, "c0201", BasicAdviceRequest().Provider, "保持手位，检查肩颈", parameters,
        [warning], "reference-42", true, rig, actorPose);
    var (_, error, request) = await ExchangeAdvice(payload, AdviceJson());
    Assert(error is null, $"Advice failed: {error}");
    using var body = JsonDocument.Parse(request.Body);
    var root = body.RootElement;
    var fields = root.EnumerateObject().Select(x => x.Name).Order().ToArray();
    Assert(fields.SequenceEqual(new[] { "actor_pose", "focus", "include_image", "params", "pose", "provider", "race", "rig", "session_id", "warnings" }),
        "Advice fields did not follow the backend contract (or included chat mutation fields)");
    Assert(root.GetProperty("pose").GetString() == pose && root.GetProperty("actor_pose").GetString() == actorPose,
        "Advice did not preserve separate preview and current actor pose snapshots");
    Assert(root.GetProperty("race").GetString() == "c0201" && root.GetProperty("focus").GetString() == payload.Focus,
        "Advice lost race or the user's focus");
    Assert(root.GetProperty("include_image").GetBoolean() && root.GetProperty("session_id").GetString() == "reference-42",
        "Advice lost explicit reference-image opt-in or its session");
    var provider = root.GetProperty("provider");
    Assert(provider.GetProperty("endpoint").GetString() == payload.Provider.Endpoint &&
        provider.GetProperty("api_key").GetString() == "test-key" && provider.GetProperty("model").GetString() == "test-model",
        "Advice did not send the configured compatible provider contract");
    var tuning = root.GetProperty("params");
    Assert(Math.Abs(tuning.GetProperty("amplitude").GetProperty("neck").GetSingle() - 0.7f) < 0.0001f &&
        tuning.GetProperty("offsets").GetProperty("j_kao")[1].GetInt32() == 8 && !tuning.GetProperty("ground").GetBoolean(),
        "Advice lost current image-tuning parameters");
    var sentWarning = root.GetProperty("warnings")[0];
    Assert(sentWarning.GetProperty("bone").GetString() == warning.Bone && sentWarning.GetProperty("message").GetString() == warning.Message &&
        sentWarning.GetProperty("value").GetSingle() == 41 && sentWarning.GetProperty("limit").GetSingle() == 35,
        "Advice lost the measured local-rule evidence");
    var bones = root.GetProperty("rig").GetProperty("bones");
    Assert(bones.GetArrayLength() == 2 && bones[1].GetProperty("name").GetString() == "j_f_mab_l" &&
        bones[1].GetProperty("parent").GetString() == "j_kao" && bones[1].GetProperty("reference_position").GetArrayLength() == 3 &&
        bones[1].GetProperty("reference_rotation")[3].GetInt32() == 1 && bones[1].GetProperty("reference_scale")[0].GetInt32() == 1,
        "Advice lost actual rig hierarchy/neutral transforms or used incorrect property names");
}

static async Task AdviceDefaults()
{
    var (_, error, request) = await ExchangeAdvice(BasicAdviceRequest(), AdviceJson());
    Assert(error is null, $"Default advice failed: {error}");
    using var body = JsonDocument.Parse(request.Body);
    var root = body.RootElement;
    Assert(!root.GetProperty("include_image").GetBoolean() && root.GetProperty("session_id").ValueKind == JsonValueKind.Null,
        "Default advice unexpectedly opted into reference-image sending");
    Assert(!root.TryGetProperty("warnings", out var warnings) ||
        (warnings.ValueKind == JsonValueKind.Array && warnings.GetArrayLength() == 0),
        "Default advice sent null warnings (backend requires omission or an empty array)");
    Assert(root.GetProperty("focus").GetString() == "" && root.GetProperty("params").ValueKind == JsonValueKind.Null &&
        root.GetProperty("rig").ValueKind == JsonValueKind.Null && root.GetProperty("actor_pose").ValueKind == JsonValueKind.Null,
        "Default advice invented focus, image parameters or actor metadata");
    Assert(!root.TryGetProperty("history", out _) && !root.TryGetProperty("ground", out _),
        "Read-only advice was serialized as a pose-edit request");
}

static async Task AdviceResponse()
{
    var (response, error, _) = await ExchangeAdvice(BasicAdviceRequest(), AdviceJson());
    Assert(error is null && response is not null, $"Advice response failed: {error}");
    Assert(response!.Summary == "The pose keeps the left hand near the head." && response.Suggestions.Length == 2,
        "Advice summary/suggestion list were not decoded");
    Assert(response.Suggestions[0] == new PoseAdviceSuggestion("j_kubi", "Neck tilt", "The head and neck tilt in the same direction.",
        "Reduce neck tilt while keeping the hand placement."), "Advice issue/evidence/recommendation were not decoded");
    Assert(response.Suggestions[1].Bone is null && response.Limitations.SequenceEqual(["No rendered character screenshot was supplied."]),
        "Non-joint advice or limitations were not decoded");
}

static async Task AdviceCancellation()
{
    int port;
    using (var probe = new TcpListener(IPAddress.Loopback, 0))
    {
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
    }
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    using var client = new BackendClient(() => port);
    using var cts = new CancellationTokenSource();
    var operation = client.PoseAdviceAsync(BasicAdviceRequest(), cts.Token);
    var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
    using (var reader = new StreamReader(context.Request.InputStream))
        await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5));
    Assert(context.Request.Url?.AbsolutePath == "/chat/advice", "Cancellation did not exercise an in-flight advice request");
    cts.Cancel();
    Exception? error = null;
    try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (Exception ex) { error = ex; }
    context.Response.Close();
    Assert(error is OperationCanceledException, $"Advice ignored in-flight cancellation: {error}");
}

static async Task AdviceErrorDistinction()
{
    var (_, routeError, _) = await ExchangeAdvice(BasicAdviceRequest(), "{\"detail\":\"Not Found\"}", 404);
    Assert(routeError is InvalidOperationException && routeError.Message == "当前后端未加载 AI 建议接口，请更新并重启后端",
        $"Missing advice route did not explain the backend upgrade: {routeError}");
    var (_, sessionError, _) = await ExchangeAdvice(BasicAdviceRequest() with { IncludeImage = true, SessionId = "expired" },
        "{\"detail\":\"reference session expired\"}", 404);
    Assert(sessionError is InvalidOperationException && sessionError.Message.Contains("404") &&
        sessionError.Message.Contains("reference session expired") && !sessionError.Message.Contains("未加载 AI 建议接口"),
        "A missing reference session was incorrectly presented as a missing advice route");
    var (_, providerError, _) = await ExchangeAdvice(BasicAdviceRequest(), "{\"detail\":\"model rate limited\"}", 502);
    Assert(providerError is InvalidOperationException && providerError.Message.Contains("502") && providerError.Message.Contains("model rate limited"),
        "Advice dropped the backend status or safe provider error");
}

static async Task AdviceEmptyResponse()
{
    var (response, error, _) = await ExchangeAdvice(BasicAdviceRequest(), "null");
    Assert(response is null && error is InvalidOperationException && error.Message.Contains("空响应"),
        "A null advice response was accepted as a usable result");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
