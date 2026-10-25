using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Posegraph.Backend;

/// <summary>Lists a provider's models without requiring the local pose backend.</summary>
public sealed class ModelCatalogClient : IDisposable
{
    private const int MaxResponseBytes = 2 * 1024 * 1024;
    private const int MaxModels = 10000;
    private readonly HttpClient _http;

    public ModelCatalogClient() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }

    internal ModelCatalogClient(HttpMessageHandler handler)
        => _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

    internal static Uri ModelsUri(string endpoint)
    {
        var value = endpoint.Trim();
        if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.Host.Length == 0 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new InvalidOperationException(Loc.T("API 端点无效，请填写 API 基址或 /chat/completions 地址"));

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.EndsWith("/responses", StringComparison.Ordinal) || path.EndsWith("/messages", StringComparison.Ordinal))
            throw new InvalidOperationException(Loc.T("API 端点无效，请填写 API 基址或 /chat/completions 地址"));
        if (path.Length == 0) path = "/v1";
        else if (path.EndsWith("/chat/completions", StringComparison.Ordinal))
            path = path[..^"/chat/completions".Length];
        if (!path.EndsWith("/models", StringComparison.Ordinal)) path += "/models";
        return new UriBuilder(uri) { Path = path }.Uri;
    }

    public async Task<string[]> FetchAsync(string endpoint, string apiKey, CancellationToken ct = default)
    {
        var uri = ModelsUri(endpoint);
        var key = apiKey.Trim();
        if (key.Any(c => c < 32 || c > 126))
            throw new InvalidOperationException(Loc.T("API 密钥格式无效"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException(Loc.T("模型列表认证失败，请检查 API 密钥"));
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                throw new InvalidOperationException(Loc.F("服务不支持拉取模型（HTTP {0}）", (int)response.StatusCode));
            if (!response.IsSuccessStatusCode)
                // Provider error bodies may echo credentials. Keep them out of the UI.
                throw new InvalidOperationException(Loc.F("拉取模型失败：HTTP {0}", (int)response.StatusCode));
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InvalidOperationException(Loc.T("模型列表过大，请检查 API 端点"));

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, token);
                if (count == 0) break;
                if (data.Length + count > MaxResponseBytes)
                    throw new InvalidOperationException(Loc.T("模型列表过大，请检查 API 端点"));
                data.Write(buffer, 0, count);
            }
            using var json = JsonDocument.Parse(data.GetBuffer().AsMemory(0, (int)data.Length));
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("data", out var models) || models.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException(Loc.T("模型服务返回了无效的模型列表"));
            if (models.GetArrayLength() > MaxModels)
                throw new InvalidOperationException(Loc.T("模型列表过大，请检查 API 端点"));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in models.EnumerateArray())
            {
                if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String) continue;
                var name = id.GetString()!.Trim();
                if (name.Length is > 0 and <= 256 && !name.Any(char.IsControl)) ids.Add(name);
            }
            if (models.GetArrayLength() > 0 && ids.Count == 0)
                throw new InvalidOperationException(Loc.T("模型服务返回了无效的模型列表"));
            return ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ThenBy(id => id, StringComparer.Ordinal).ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(Loc.T("拉取模型超时，请重试"));
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(Loc.T("模型服务返回了无效的模型列表"));
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(Loc.T("无法连接模型服务，请检查 API 端点和网络"));
        }
        catch (IOException)
        {
            throw new InvalidOperationException(Loc.T("无法连接模型服务，请检查 API 端点和网络"));
        }
    }

    public void Dispose() => _http.Dispose();
}
