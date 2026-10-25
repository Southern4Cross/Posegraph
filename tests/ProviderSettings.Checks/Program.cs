using System.Net;
using System.Security.Cryptography;
using System.Text;
using Posegraph.Backend;

var checks = new (string Name, Func<Task> Run)[]
{
    ("Normalizes provider roots, custom bases and completion URLs", NormalizeUrls),
    ("Rejects invalid or credential-bearing endpoints before sending", RejectEndpoints),
    ("Sends only the intended bearer key and accepts local providers without a key", Authentication),
    ("Filters, deduplicates and sorts provider model IDs", ModelIds),
    ("Rejects malformed and oversized catalog responses without echoing content", InvalidCatalogs),
    ("Redacts provider error bodies and connection exception details", SafeFailures),
    ("Propagates caller cancellation of an in-flight catalog request", Cancellation),
    ("Uses real Windows DPAPI for empty and random test-key roundtrips", ProtectedKeyRoundtrip),
};
foreach (var (name, run) in checks)
{
    await run().WaitAsync(TimeSpan.FromSeconds(10));
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"{checks.Length} provider settings checks passed (production sources linked; fake HTTP, real Windows DPAPI).");

static Task NormalizeUrls()
{
    foreach (var (input, expected) in new[]
    {
        ("https://provider.example", "https://provider.example/v1/models"),
        (" https://provider.example/v1/ ", "https://provider.example/v1/models"),
        ("http://127.0.0.1:1234/custom/v2", "http://127.0.0.1:1234/custom/v2/models"),
        ("https://provider.example/v1/chat/completions/", "https://provider.example/v1/models"),
        ("https://provider.example/custom/chat/completions", "https://provider.example/custom/models"),
        ("http://localhost:8080/v1/models", "http://localhost:8080/v1/models"),
    })
        Assert(ModelCatalogClient.ModelsUri(input).AbsoluteUri == expected, "Incorrect catalog URL");
    return Task.CompletedTask;
}

static async Task RejectEndpoints()
{
    using var handler = new FakeProvider((_, _) => Task.FromResult(Json("{\"data\":[]}")));
    using var client = new ModelCatalogClient(handler);
    foreach (var endpoint in new[]
    {
        "", "invalid", "file:///private", "ftp://provider.example",
        "https://user:password@provider.example/v1", "https://provider.example/v1?api-key=private",
        "https://provider.example/v1#private", "https://bad host.example/v1",
        "https://provider.example:999999/v1", "https://provider.example/v1/responses",
        "https://provider.example/v1/messages",
    })
    {
        var error = await Failure(() => client.FetchAsync(endpoint, ""));
        Assert(error is InvalidOperationException && !error.Message.Contains("private"), "Unsafe endpoint error");
    }
    Assert(handler.Calls == 0, "Invalid endpoint reached provider");
}

static async Task Authentication()
{
    var requests = new List<(HttpMethod Method, string Url, string? Auth, string Accept)>();
    using var handler = new FakeProvider((request, _) =>
    {
        requests.Add((request.Method, request.RequestUri!.AbsoluteUri,
            request.Headers.Authorization?.ToString(), request.Headers.Accept.ToString()));
        return Task.FromResult(Json("{\"data\":[]}"));
    });
    using var client = new ModelCatalogClient(handler);
    await client.FetchAsync("https://provider.example/v1/chat/completions", "  fake-test-key  ");
    await client.FetchAsync("http://localhost:1234/v1", "");
    Assert(requests[0] == (HttpMethod.Get, "https://provider.example/v1/models", "Bearer fake-test-key", "application/json"),
        "Catalog request lost its method, URL, auth or Accept header");
    Assert(requests[1].Auth is null, "Unauthenticated local service received a bearer key");
    var error = await Failure(() => client.FetchAsync("https://provider.example/v1", "fake\r\nheader"));
    Assert(error is InvalidOperationException && handler.Calls == 2, "Invalid key reached HTTP transport");
}

static async Task ModelIds()
{
    var response = """
        {"data":[{"id":" z-model "},{"id":"a-model"},{"id":"A-model"},{"id":"a-model"},
        {"id":""},{"id":"   "},{"id":"bad\nmodel"},{"id":17},{"other":"ignored"},null,"ignored"]}
        """;
    using var handler = new FakeProvider((_, _) => Task.FromResult(Json(response)));
    using var client = new ModelCatalogClient(handler);
    Assert((await client.FetchAsync("https://provider.example", "")).SequenceEqual(["A-model", "a-model", "z-model"]),
        "Model filtering, identity or stable sorting changed");
    using var emptyHandler = new FakeProvider((_, _) => Task.FromResult(Json("{\"data\":[]}")));
    using var emptyClient = new ModelCatalogClient(emptyHandler);
    Assert((await emptyClient.FetchAsync("https://provider.example", "")).Length == 0, "Empty catalogs should be valid");
}

static async Task InvalidCatalogs()
{
    foreach (var response in new[] { "not-json-private", "null", "[]", "{}", "{\"data\":{}}", "{\"data\":[{\"id\":12}]}" })
    {
        using var handler = new FakeProvider((_, _) => Task.FromResult(Json(response)));
        using var client = new ModelCatalogClient(handler);
        var error = await Failure(() => client.FetchAsync("https://provider.example", ""));
        Assert(error is InvalidOperationException && !error.Message.Contains(response), "Malformed catalog body leaked");
    }
    // No Content-Length: exercise the stream budget instead of just the header check.
    using var largeHandler = new FakeProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StreamContent(new StreamingBytes(new byte[2 * 1024 * 1024 + 1])),
    }));
    using var largeClient = new ModelCatalogClient(largeHandler);
    var tooLarge = await Failure(() => largeClient.FetchAsync("https://provider.example", ""));
    Assert(tooLarge is InvalidOperationException && tooLarge.Message.Contains("过大"), "Unbounded catalog stream accepted");
}

static async Task SafeFailures()
{
    const string secret = "fake-private-provider-value";
    foreach (var status in new[] { 302, 400, 401, 403, 404, 405, 429, 500 })
    {
        using var handler = new FakeProvider((_, _) => Task.FromResult(Json(secret, status)));
        using var client = new ModelCatalogClient(handler);
        var error = await Failure(() => client.FetchAsync("https://provider.example", secret));
        Assert(error is InvalidOperationException && !error.ToString().Contains(secret), "Provider error exposed sensitive content");
        Assert(handler.Calls == 1, "Provider error was retried unexpectedly");
    }
    foreach (var transportError in new Exception[] { new HttpRequestException(secret), new IOException(secret) })
    {
        using var handler = new FakeProvider((_, _) => Task.FromException<HttpResponseMessage>(transportError));
        using var client = new ModelCatalogClient(handler);
        var error = await Failure(() => client.FetchAsync("https://provider.example", secret));
        Assert(error is InvalidOperationException && !error.ToString().Contains(secret), "Transport error details leaked");
    }
}

static async Task Cancellation()
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var handler = new FakeProvider(async (_, token) =>
    {
        entered.SetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException("Unreachable");
    });
    using var client = new ModelCatalogClient(handler);
    using var cts = new CancellationTokenSource();
    var request = client.FetchAsync("https://provider.example", "", cts.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
    cts.Cancel();
    var error = await Failure(() => request);
    Assert(error is OperationCanceledException, "Caller cancellation was converted into a provider error");
}

static Task ProtectedKeyRoundtrip()
{
    Assert(OperatingSystem.IsWindows(), "DPAPI check requires Windows");
    Assert(ApiKeyProtection.Protect("") == "" && ApiKeyProtection.Unprotect("") == "", "Empty key changed");
    var key = "test-only-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    var protectedKey = ApiKeyProtection.Protect(key);
    Assert(protectedKey.Length > 0 && protectedKey != key, "DPAPI did not protect the test key");
    Assert(!Encoding.UTF8.GetString(Convert.FromBase64String(protectedKey)).Contains(key), "Ciphertext contained the plaintext test key");
    Assert(ApiKeyProtection.Unprotect(protectedKey) == key, "Windows account DPAPI roundtrip failed");
    return Task.CompletedTask;
}

static HttpResponseMessage Json(string content, int status = 200)
    => new((HttpStatusCode)status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

static async Task<Exception> Failure(Func<Task> action)
{
    try { await action(); }
    catch (Exception error) { return error; }
    throw new InvalidOperationException("Expected request to fail");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class FakeProvider(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        return respond(request, ct);
    }
}

sealed class StreamingBytes(byte[] bytes) : MemoryStream(bytes)
{
    public override bool CanSeek => false;
}
