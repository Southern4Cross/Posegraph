using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Posegraph.Library;
using Posegraph.UI;
using SkiaSharp;

namespace UiPreview;

/// <summary>
/// 图库压力测试：几千张图的文件夹里连续滚动，统计缩略图加载的资源占用。
/// 环境变量 STRESS_COUNT（图片张数，默认 3000）、STRESS_FRAMES（滚动帧数，默认 900）、STRESS_WHEEL（每帧滚轮格数，默认 3）。
/// 图片是硬链接（几张源图反复链接，不占磁盘）：普通照片 + 6000×4000 的「手机原图」。
/// </summary>
public static class Stress
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, nint security);

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) ? v : fallback;

    public static void Library(App app, string samples)
    {
        var count = Env("STRESS_COUNT", 3000);
        var frames = Env("STRESS_FRAMES", 900);
        var wheel = Env("STRESS_WHEEL", 3);
        var root = Path.Combine(Path.GetTempPath(), "PosegraphStress");
        var folder = MakeFolder(root, samples, count);

        var field = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(ImageLibrary).GetField("<Root>k__BackingField", field)!.SetValue(app.Main.Library, root);
        typeof(ImageLibrary).GetField("<CacheDir>k__BackingField", field)!.SetValue(app.Main.Library, Path.Combine(root, "_cache"));
        typeof(LibraryWindow).GetMethod("Navigate", field)!.Invoke(app.Library, [folder]);

        TextureStats.Async = true;
        app.Library.IsOpen = true;
        app.Library.Position = new Vector2(20, 20);
        app.Library.Size = new Vector2(1180, 820) / app.Host.Scale;
        void Draw() => Host.DrawWindow(app.Library);
        var clock = Stopwatch.StartNew();
        var released = 0;
        int Frame()
        {
            var data = app.Host.Frame(Draw);
            var missing = app.Host.Renderer.CountReleasedTextures(data);
            released += missing;
            Thread.Sleep(16);   // 约 60 帧/秒，后台解码按真实时间推进
            return missing;
        }

        // 等后台列完目录
        for (var i = 0; i < 300 && Entries(app).Length < count; i++) Frame();
        Console.WriteLine($"图片 {Entries(app).Length} 张，目录 {folder}");
        Report("开始", clock, app);
        app.Host.MousePos = new Vector2(20 + 600, 20 + 420);   // 网格中间
        for (var f = 1; f <= frames; f++)
        {
            app.Host.MouseWheel = -wheel;
            Frame();
            if (f % 120 == 0) Report($"滚动 {f} 帧", clock, app);
        }
        Report("停止滚动", clock, app);
        var before = Keys(app);
        for (var i = 0; i < 60; i++) Frame();
        var after = Keys(app);
        var added = after.Except(before).ToList();
        Console.WriteLine($"停止后 1 秒：缓存里新增 {added.Count} 个条目，例如 {string.Join(", ", added.Take(6).Select(Path.GetFileName))}；" +
                          $"网格滚动位置 {GridScroll(app)}");
        var stopped = TextureStats.Decodes;
        for (var s = 1; s <= 30; s++)
        {
            for (var i = 0; i < 60; i++) Frame();
            if (s % 5 == 0) Report($"停止后 {s} 秒（之后又解码 {TextureStats.Decodes - stopped} 张）", clock, app);
            if (TextureStats.Decoding == 0 && s >= 5 && Loading(app) == 0) break;
        }
        Console.WriteLine($"引用已释放纹理的绘制命令：{released} 次");
        var stuck = new List<string>();
        foreach (System.Collections.DictionaryEntry kv in App.Get<System.Collections.IDictionary>(Thumbs(app), "_entries"))
        {
            var t = kv.Value!.GetType();
            if (t.GetProperty("Wrap")!.GetValue(kv.Value) is null && !(bool)t.GetField("Failed")!.GetValue(kv.Value)!)
                stuck.Add($"{Path.GetFileName((string)kv.Key)}(Idle={t.GetField("Idle")!.GetValue(kv.Value)})");
        }
        Console.WriteLine($"仍在加载：{stuck.Count} 个，例如 {string.Join(", ", stuck.Take(8))}");
        if (stuck.Count > 0)
        {
            var wic = typeof(ThumbnailCache).Assembly.GetType("Posegraph.Library.WicThumbnail")!.GetMethod("Decode")!;
            var file = Path.Combine(Path.GetDirectoryName(Keys(app).First())!, stuck[0].Split('(')[0]);
            try
            {
                var r = wic.Invoke(null, [file, 256]);
                Console.WriteLine($"直接 WIC 解码 {Path.GetFileName(file)}：{r}");
            }
            catch (Exception ex) { Console.WriteLine($"直接 WIC 解码 {Path.GetFileName(file)} 失败：{ex.InnerException ?? ex}"); }
        }
        app.Library.Dispose();
        Report("关闭图库后", clock, app);
    }

    /// <summary>WIC 缩略图：常见格式、缩放尺寸、坏文件报错、解码后文件句柄已释放（能立即删除）。</summary>
    public static void Wic()
    {
        var decode = typeof(ThumbnailCache).Assembly.GetType("Posegraph.Library.WicThumbnail")!.GetMethod("Decode")!;
        var dir = Path.Combine(Path.GetTempPath(), "PosegraphWic");
        Directory.CreateDirectory(dir);
        using var surface = SKSurface.Create(new SKImageInfo(1200, 800));
        surface.Canvas.Clear(new SKColor(200, 30, 40));   // 红：检查 R/B 没有颠倒
        using var image = surface.Snapshot();
        var files = new Dictionary<string, byte[]>
        {
            ["a.jpg"] = image.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray(),
            ["a.png"] = image.Encode(SKEncodedImageFormat.Png, 100).ToArray(),
            ["a.webp"] = image.Encode(SKEncodedImageFormat.Webp, 90).ToArray(),
            ["fake.jpg"] = "<!DOCTYPE html><html></html>"u8.ToArray(),
        };
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            string result;
            try
            {
                var (rgba, w, h) = ((byte[], int, int))decode.Invoke(null, [path, 256])!;
                result = $"{w}×{h}，第一个像素 RGBA {rgba[0]},{rgba[1]},{rgba[2]},{rgba[3]}";
            }
            catch (System.Reflection.TargetInvocationException ex) { result = $"失败：{ex.InnerException!.GetType().Name} {ex.InnerException.Message}"; }
            File.Delete(path);   // 句柄没释放的话这里会抛异常
            Console.WriteLine($"{name,-9} {result}；解码后可立即删除");
        }
    }

    private static string[] Entries(App app)
        => (string[])typeof(LibraryWindow).GetMethod("Entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(app.Library, null)!;

    private static ThumbnailCache Thumbs(App app) => App.Get<ThumbnailCache>(app.Library, "_thumbs");

    private static HashSet<string> Keys(App app)
        => App.Get<System.Collections.IDictionary>(Thumbs(app), "_entries").Keys.Cast<string>().ToHashSet();

    /// <summary>图库网格子窗口的滚动位置 / 最大值。</summary>
    private static string GridScroll(App app)
    {
        foreach (var w in new[] { app.Library.WindowName })
        {
            var window = Dalamud.Bindings.ImGui.ImGuiP.FindWindowByName(w);
            if (window.IsNull) continue;
            for (var i = 0; i < window.DC.ChildWindows.Size; i++)
            {
                var body = window.DC.ChildWindows[i];
                for (var j = 0; j < body.DC.ChildWindows.Size; j++)
                {
                    var c = body.DC.ChildWindows[j];
                    if (c.ScrollMax.Y > 0) return $"{c.Scroll.Y:0}/{c.ScrollMax.Y:0}";
                }
            }
        }
        return "?";
    }

    /// <summary>缓存里还没加载好（也没失败）的条目数：可见的格子还在显示「加载中」。</summary>
    private static int Loading(App app)
    {
        var entries = App.Get<System.Collections.IDictionary>(Thumbs(app), "_entries");
        var n = 0;
        foreach (var e in entries.Values)
        {
            var t = e!.GetType();
            var wrap = t.GetProperty("Wrap")!.GetValue(e);
            var failed = (bool)t.GetField("Failed")!.GetValue(e)!;
            var idle = t.GetField("Idle")?.GetValue(e) is true;   // 滚出视野没解码的，不算
            if (wrap is null && !failed && !idle) n++;
        }
        return n;
    }

    private static void Report(string label, Stopwatch clock, App app)
    {
        using var p = Process.GetCurrentProcess();
        var entries = App.Get<System.Collections.IDictionary>(Thumbs(app), "_entries").Count;
        Console.WriteLine(
            $"{clock.Elapsed.TotalSeconds,6:0.0}s {label,-28} 缓存条目 {entries,4}（加载中 {Loading(app),4}）  " +
            $"存活纹理 {TextureStats.Live,4} 张 {TextureStats.LiveBytes / 1048576.0,7:0} MB（峰值 {TextureStats.PeakBytes / 1048576.0,6:0} MB）  " +
            $"同时解码 {TextureStats.Decoding}（峰值 {TextureStats.PeakDecoding}）  累计解码 {TextureStats.Decodes,5} 张 {TextureStats.DecodedBytes / 1073741824.0,6:0.0} GB  " +
            $"进程内存 {p.PrivateMemorySize64 / 1048576.0,6:0} MB");
        TextureStats.ResetPeaks();
    }

    private static string MakeFolder(string root, string samples, int count)
    {
        var folder = Path.Combine(root, $"图片{count}");
        if (Directory.Exists(folder) && Directory.GetFiles(folder).Length == count) return folder;
        if (Directory.Exists(root)) Directory.Delete(root, true);
        var sources = Path.Combine(root, "_sources");
        Directory.CreateDirectory(sources);
        Directory.CreateDirectory(folder);
        var list = Directory.GetFiles(samples).Where(ImageLibrary.IsImage).Take(12)
            .Select((path, i) => { var dest = Path.Combine(sources, $"s{i}{Path.GetExtension(path)}"); File.Copy(path, dest); return dest; })
            .ToList();
        // 手机原图大小（6000×4000），每 5 张里有一张
        for (var i = 0; i < 3; i++)
        {
            var big = Path.Combine(sources, $"big{i}.jpg");
            using var surface = SKSurface.Create(new SKImageInfo(6000, 4000));
            using var paint = new SKPaint { Shader = SKShader.CreatePerlinNoiseFractalNoise(0.002f + i * 0.001f, 0.004f, 4, i) };
            surface.Canvas.DrawRect(0, 0, 6000, 4000, paint);
            using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Jpeg, 90);
            File.WriteAllBytes(big, data.ToArray());
            list.Add(big);
        }
        var normal = list.Where(p => !Path.GetFileName(p).StartsWith("big")).ToList();
        var bigs = list.Where(p => Path.GetFileName(p).StartsWith("big")).ToList();
        for (var i = 0; i < count; i++)
        {
            var src = i % 5 == 4 ? bigs[i / 5 % bigs.Count] : normal[i % normal.Count];
            var link = Path.Combine(folder, $"{i:D5}{Path.GetExtension(src)}");
            if (!CreateHardLink(link, src, 0)) File.Copy(src, link);
        }
        return folder;
    }
}
