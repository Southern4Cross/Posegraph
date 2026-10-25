using System.IO.Compression;
using System.Text.Json;

namespace Posegraph.Backend;

/// <summary>
/// 一键包用户的后端更新。插件与后端同版本号、从同一个 git 标签发布：插件经插件库自动更新后，
/// 这里从 GitHub 下载同一标签的源码压缩包（https://github.com/&lt;repo&gt;/archive/refs/tags/vX.Y.Z.zip），
/// 只替换一键包里的 backend\src（Python、依赖、模型都不动）。
/// 新源码要求的运行环境版本（version.json 的 runtime）高于一键包（根目录 runtime.json）时不替换，提示重新下载完整一键包。
/// 替换前停后端、旧版备份到 src.bak；新版启动失败自动回滚。
/// </summary>
public sealed class BackendUpdater : IDisposable
{
    public const string Repository = "extrant/Posegraph";
    private const long MaxArchiveBytes = 64L * 1024 * 1024;

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };

    public sealed record VersionInfo(string Version, int Runtime);

    public volatile bool Busy;
    public string? Status { get; private set; }

    public static Version PluginVersion => typeof(BackendUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>与插件同版本的后端标签，如 v0.3.1。</summary>
    public static string PluginTag => $"v{PluginVersion.Major}.{PluginVersion.Minor}.{PluginVersion.Build}";

    /// <summary>已安装后端的版本（backend\src\posegraph\version.json）；读不到为 null（旧版本或路径不对）。</summary>
    public static VersionInfo? Installed(string backendDir)
        => ReadVersion(Path.Combine(backendDir, "src", "posegraph", "version.json"));

    /// <summary>一键包的运行环境版本（一键包根目录 runtime.json）；null 表示不是一键包（源码安装请用 git 更新）。</summary>
    public static int? PackageRuntime(string backendDir)
    {
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backendDir.TrimEnd('\\', '/'))) ?? "", "runtime.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("runtime").GetInt32();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>已安装的后端是否比插件旧（需要更新）。版本读不到也算旧。</summary>
    public static bool NeedsUpdate(string backendDir)
        => Installed(backendDir) is not { } v || !System.Version.TryParse(v.Version, out var installed) || installed < Trim(PluginVersion);

    private static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    private static VersionInfo? ReadVersion(string path)
    {
        try { return Parse(File.ReadAllText(path)); }
        catch { return null; }
    }

    private static VersionInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new VersionInfo(doc.RootElement.GetProperty("version").GetString() ?? "", doc.RootElement.GetProperty("runtime").GetInt32());
    }

    /// <summary>
    /// 下载并安装与插件同版本的后端源码。stopBackend：停掉正在运行的后端并等它退出；
    /// startAndCheck：启动后端并确认新版本能正常响应。返回错误信息，成功为 null。
    /// </summary>
    public async Task<string?> UpdateAsync(string backendDir, string proxyPrefix, Func<Task> stopBackend, Func<string, Task<bool>> startAndCheck)
    {
        if (Busy) return Loc.T("正在更新中");
        Busy = true;
        try
        {
            var runtime = PackageRuntime(backendDir);
            if (runtime is null) return Loc.T("不是一键包安装（缺少 runtime.json），源码安装请用 git 更新");

            var tag = PluginTag;
            var url = $"{proxyPrefix.Trim()}https://github.com/{Repository}/archive/refs/tags/{tag}.zip";
            Status = Loc.F("下载 {0}…", tag);
            byte[] data;
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                if (!resp.IsSuccessStatusCode) return Loc.F("下载失败：HTTP {0}（{1}）", (int)resp.StatusCode, url);
                if (resp.Content.Headers.ContentLength > MaxArchiveBytes) return Loc.T("下载的文件过大，已取消");
                data = await resp.Content.ReadAsByteArrayAsync();
            }

            Status = Loc.T("校验…");
            using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var root = zip.Entries.FirstOrDefault()?.FullName.Split('/')[0] + "/";
            var srcPrefix = root + "backend/src/";
            var versionEntry = zip.GetEntry(srcPrefix + "posegraph/version.json");
            if (versionEntry is null) return Loc.T("压缩包里没有后端源码（backend/src/posegraph/version.json）");
            VersionInfo incoming;
            using (var reader = new StreamReader(versionEntry.Open()))
                incoming = Parse(await reader.ReadToEndAsync());
            if ("v" + incoming.Version != tag) return Loc.F("版本不符：压缩包是 {0}，需要 {1}", incoming.Version, tag);
            if (incoming.Runtime > runtime)
                return Loc.F("新版后端需要运行环境 {0}，一键包是 {1}：请重新下载完整一键包", incoming.Runtime, runtime);

            var src = Path.Combine(backendDir, "src");
            var staged = src + ".new";
            var backup = src + ".bak";
            if (Directory.Exists(staged)) Directory.Delete(staged, true);
            var stagedFull = Path.GetFullPath(staged) + Path.DirectorySeparatorChar;
            foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(srcPrefix) && e.FullName.Length > srcPrefix.Length))
            {
                var target = Path.GetFullPath(Path.Combine(staged, entry.FullName[srcPrefix.Length..]));
                if (!target.StartsWith(stagedFull, StringComparison.OrdinalIgnoreCase)) return Loc.T("压缩包里有不安全的路径，已取消");
                if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, true);
            }

            Status = Loc.T("停止后端…");
            await stopBackend();
            if (Directory.Exists(backup)) Directory.Delete(backup, true);
            Directory.Move(src, backup);
            Directory.Move(staged, src);

            Status = Loc.T("启动新版后端…");
            if (await startAndCheck(incoming.Version)) return null;

            // 回滚：新版起不来就换回旧版
            Status = Loc.T("新版启动失败，回滚…");
            await stopBackend();
            var failed = src + ".failed";
            if (Directory.Exists(failed)) Directory.Delete(failed, true);
            Directory.Move(src, failed);
            Directory.Move(backup, src);
            await startAndCheck("");
            return Loc.F("新版后端启动失败，已回滚到旧版（新版保留在 {0}）", failed);
        }
        catch (Exception ex)
        {
            return Loc.F("更新失败：{0}", ex.Message);
        }
        finally
        {
            Busy = false;
            Status = null;
        }
    }

    public void Dispose() => _http.Dispose();
}
