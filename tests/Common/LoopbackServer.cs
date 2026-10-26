using System.Net;
using System.Net.Sockets;
using System.Text;

internal sealed record MockResponse(int StatusCode, string Body);

/// <summary>Real concurrent loopback HTTP for the client and recognition-service checks.</summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly HttpListener _listener = new();
    public int Port { get; }

    public LoopbackServer(Func<HttpListenerRequest, Task<MockResponse>> respond)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = ListenAsync(respond);
    }

    private async Task ListenAsync(Func<HttpListenerRequest, Task<MockResponse>> respond)
    {
        try { while (_listener.IsListening) _ = ReplyAsync(await _listener.GetContextAsync(), respond); }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task ReplyAsync(HttpListenerContext context, Func<HttpListenerRequest, Task<MockResponse>> respond)
    {
        try
        {
            var response = await respond(context.Request);
            var bytes = Encoding.UTF8.GetBytes(response.Body);
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
        }
        finally { context.Response.Close(); }
    }

    public void Dispose() => _listener.Close();
}
