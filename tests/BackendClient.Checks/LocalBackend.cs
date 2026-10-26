internal sealed record CapturedRequest(string Method, string Path, string Body, string? Language, string? ContentType, string? Query = null);

/// <summary>Real loopback HTTP, so production serialization, handlers and error parsing are exercised.</summary>
internal sealed class LocalBackend : IDisposable
{
    private readonly LoopbackServer _server;
    private readonly TaskCompletionSource<CapturedRequest[]> _requests = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Port => _server.Port;
    public Task<CapturedRequest[]> Requests => _requests.Task;

    public LocalBackend(params MockResponse[] responses)
    {
        var pending = new Queue<MockResponse>(responses);
        var captured = new List<CapturedRequest>();
        _server = new LoopbackServer(async request =>
        {
            using var reader = new StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();
            lock (pending)
            {
                captured.Add(new(request.HttpMethod, request.Url!.AbsolutePath, body,
                    request.Headers["Accept-Language"], request.ContentType, request.Url.Query));
                var response = pending.Dequeue();
                if (pending.Count == 0) _requests.TrySetResult(captured.ToArray());
                return response;
            }
        });
    }

    public void Dispose() => _server.Dispose();
}
