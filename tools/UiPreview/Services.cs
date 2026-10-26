using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using SkiaSharp;

namespace UiPreview;

/// <summary>Skia 图片当作 Dalamud 纹理（Handle 是渲染器里的编号）。</summary>
public sealed class SkTexture : IDalamudTextureWrap
{
    private readonly SkiaRenderer _renderer;
    private readonly ulong _id;
    private readonly long _bytes;
    private int _disposed;

    public SkTexture(SkiaRenderer renderer, SKImage image)
    {
        _renderer = renderer;
        _id = renderer.Register(image);
        Width = image.Width;
        Height = image.Height;
        _bytes = (long)Width * Height * 4;
        TextureStats.Created(_bytes);
    }

    public ImTextureID Handle => new(_id);
    public int Width { get; }
    public int Height { get; }
    public Vector2 Size => new(Width, Height);
    public IDalamudTextureWrap CreateWrapSharingLowLevelResource() => this;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _renderer.Unregister(_id);
        TextureStats.Released(_bytes);
    }
}

/// <summary>纹理统计（压力测试用）：存活纹理数与像素字节（真机上是显存）、同时解码数，及各自峰值。</summary>
public static class TextureStats
{
    /// <summary>为 true 时像 Dalamud 一样在线程池里异步解码；默认同步（截图场景要确定性）。</summary>
    public static bool Async { get; set; }

    private static long _live, _liveBytes, _peakBytes, _decoding, _peakDecoding, _decodes, _decodedPixels;
    public static long Live => Interlocked.Read(ref _live);
    public static long LiveBytes => Interlocked.Read(ref _liveBytes);
    public static long PeakBytes => Interlocked.Read(ref _peakBytes);
    public static long Decoding => Interlocked.Read(ref _decoding);
    public static long PeakDecoding => Interlocked.Read(ref _peakDecoding);
    public static long Decodes => Interlocked.Read(ref _decodes);
    /// <summary>全尺寸解码过的像素字节累计（真机上每张都要整张传上显存再缩小）。</summary>
    public static long DecodedBytes => Interlocked.Read(ref _decodedPixels);

    public static void Created(long bytes)
    {
        Interlocked.Increment(ref _live);
        var now = Interlocked.Add(ref _liveBytes, bytes);
        for (long peak; now > (peak = PeakBytes);)
            if (Interlocked.CompareExchange(ref _peakBytes, now, peak) == peak) break;
    }

    public static void Released(long bytes)
    {
        Interlocked.Decrement(ref _live);
        Interlocked.Add(ref _liveBytes, -bytes);
    }

    public static IDisposable Decode()
    {
        Interlocked.Increment(ref _decodes);
        var now = Interlocked.Increment(ref _decoding);
        for (long peak; now > (peak = PeakDecoding);)
            if (Interlocked.CompareExchange(ref _peakDecoding, now, peak) == peak) break;
        return new Done();
    }

    public static void Decoded(long bytes) => Interlocked.Add(ref _decodedPixels, bytes);

    public static void ResetPeaks()
    {
        Interlocked.Exchange(ref _peakBytes, LiveBytes);
        Interlocked.Exchange(ref _peakDecoding, Decoding);
    }

    private sealed class Done : IDisposable
    {
        public void Dispose() => Interlocked.Decrement(ref _decoding);
    }
}

/// <summary>插件用到的 Dalamud 服务的离线替身。</summary>
public static class Services
{
    public static string ConfigDir { get; } = Path.Combine(Path.GetTempPath(), "PosegraphUiPreview");

    public static IDalamudPluginInterface PluginInterface(Func<IEnumerable<IExposedPlugin>> plugins)
        => Stub.Create<IDalamudPluginInterface>(s => s
            .On("GetPluginConfigDirectory", _ => Directory.CreateDirectory(ConfigDir).FullName)
            .On("get_InstalledPlugins", _ => plugins().ToList())
            .Returns("get_UiLanguage", "zh"));

    public static IExposedPlugin Plugin(string name, string version, bool loaded = true)
        => Stub.Create<IExposedPlugin>(s => s
            .Returns("get_InternalName", name)
            .Returns("get_Name", name)
            .Returns("get_IsLoaded", loaded)
            .Returns("get_Version", Version.Parse(version)));

    public static ITextureProvider Textures(SkiaRenderer renderer)
        => Stub.Create<ITextureProvider>(s => s
            .On("CreateFromImageAsync", a =>
            {
                var bytes = a[0] is ReadOnlyMemory<byte> m ? m.ToArray() : ReadAll((Stream)a[0]!);
                IDalamudTextureWrap Decode()
                {
                    using var _ = TextureStats.Decode();
                    // 立即整张解码（FromEncodedData 是懒解码，测不出真实开销）
                    using var encoded = SKImage.FromEncodedData(bytes) ?? throw new InvalidDataException("无法解码图片");
                    var image = encoded.ToRasterImage(true);
                    TextureStats.Decoded((long)image.Width * image.Height * 4);
                    return new SkTexture(renderer, image);
                }
                return TextureStats.Async ? Task.Run(Decode) : Task.FromResult(Decode());
            })
            .On("CreateFromRawAsync", a =>
            {
                using var _ = TextureStats.Decode();   // 插件已用 WIC 解码缩好，这里只算一次「解码」
                var spec = (RawImageSpecification)a[0]!;
                var pixels = a[1] is ReadOnlyMemory<byte> m ? m.ToArray() : ReadAll((Stream)a[1]!);
                TextureStats.Decoded(pixels.Length);
                var info = new SKImageInfo(spec.Width, spec.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
                var image = SKImage.FromPixelCopy(info, pixels, spec.Pitch) ?? throw new InvalidDataException("像素数据不对");
                return Task.FromResult<IDalamudTextureWrap>(new SkTexture(renderer, image));
            })
            .On("CreateFromExistingTextureAsync", a =>
            {
                var src = (IDalamudTextureWrap)a[0]!;
                var args = (TextureModificationArgs)a[1]!;
                var image = renderer.Get(src.Handle.Handle)!;
                var w = args.NewWidth > 0 ? args.NewWidth : image.Width;
                var h = args.NewHeight > 0 ? args.NewHeight : image.Height;
                using var surface = SKSurface.Create(new SKImageInfo(w, h));
                surface.Canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
                return Task.FromResult<IDalamudTextureWrap>(new SkTexture(renderer, surface.Snapshot()));
            })
            .Returns("HasClipboardImage", false));

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>RunOnFrameworkThread 直接在当前线程执行；DelayTicks 立即完成。</summary>
    public static IFramework Framework()
        => Stub.Create<IFramework>(s => s
            .On("RunOnFrameworkThread", (m, a) =>
            {
                var result = ((Delegate)a[0]!).DynamicInvoke();
                if (result is Task) return result;
                var type = m.ReturnType.IsGenericType ? m.ReturnType.GetGenericArguments()[0] : null;
                return type is null ? Task.CompletedTask
                    : typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type).Invoke(null, [result]);
            })
            .On("DelayTicks", _ => Task.CompletedTask));
}
