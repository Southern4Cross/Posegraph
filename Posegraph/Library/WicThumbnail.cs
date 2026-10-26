using System.Runtime.InteropServices;

namespace Posegraph.Library;

/// <summary>
/// 用 Windows 图像组件（WIC，Dalamud 解码图片用的也是它）边解码边缩小，只有缩略图大小的像素进内存。
/// ITextureProvider.CreateFromImageAsync 会先把原图整张做成显存纹理再缩（6000×4000 的手机原图约 96MB），
/// 图库里快速翻几千张图时显存会被冲爆。
/// </summary>
internal static class WicThumbnail
{
    private const uint GenericRead = 0x80000000;
    private const int MetadataCacheOnDemand = 0;
    private const int InterpolationFant = 3;   // 缩小质量好，所有 Windows 版本都有
    private static Guid _rgba32 = new("f5c7ad2d-6a8d-43dd-a7a8-a29935261ae9");   // GUID_WICPixelFormat32bppRGBA（直通 alpha）

    /// <summary>解码第一帧，等比缩到长边不超过 maxSide（小图不放大），返回 RGBA 像素。WIC 解不了时抛异常。</summary>
    public static (byte[] Rgba, int Width, int Height) Decode(string path, int maxSide)
    {
        IWICImagingFactory? factory = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapSource? frame = null;
        IWICBitmapScaler? scaler = null;
        IWICFormatConverter? converter = null;
        try
        {
            factory = (IWICImagingFactory)new WicImagingFactory();
            factory.CreateDecoderFromFilename(path, IntPtr.Zero, GenericRead, MetadataCacheOnDemand, out decoder);
            decoder.GetFrame(0, out frame);
            frame.GetSize(out var width, out var height);
            var scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
            var w = Math.Max(1, (int)Math.Round(width * scale));
            var h = Math.Max(1, (int)Math.Round(height * scale));
            factory.CreateBitmapScaler(out scaler);
            scaler.Initialize(frame, (uint)w, (uint)h, InterpolationFant);
            factory.CreateFormatConverter(out converter);
            converter.Initialize((IWICBitmapSource)scaler, ref _rgba32, 0, IntPtr.Zero, 0, 0);
            var pixels = new byte[w * h * 4];
            converter.CopyPixels(IntPtr.Zero, (uint)(w * 4), (uint)pixels.Length, pixels);
            return (pixels, w, h);
        }
        finally
        {
            // 立即释放：解码器开着文件句柄，不放掉的话改名 / 删除会失败
            foreach (var com in new object?[] { converter, scaler, frame, decoder, factory })
                if (com is not null) Marshal.FinalReleaseComObject(com);
        }
    }

    // ---- WIC 的 COM 接口：只声明用到的方法；没用到的方法也要按虚表顺序占位，签名无所谓

    [ComImport, Guid("cacaf262-9370-4615-a13b-9f5539da4c0a")]
    private class WicImagingFactory { }

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string filename, IntPtr vendor, uint desiredAccess,
                                       int metadataOptions, out IWICBitmapDecoder decoder);
        void CreateDecoderFromStream();
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();
        void CreateFormatConverter(out IWICFormatConverter converter);
        void CreateBitmapScaler(out IWICBitmapScaler scaler);
    }

    [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        void QueryCapability();
        void Initialize();
        void GetContainerFormat();
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        void GetPreview();
        void GetColorContexts();
        void GetThumbnail();
        void GetFrameCount(out uint count);
        void GetFrame(uint index, out IWICBitmapSource frame);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapSource
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double dpiX, out double dpiY);
        void CopyPalette(IntPtr palette);
        void CopyPixels(IntPtr rect, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] buffer);
    }

    // COM 互操作不支持接口继承：派生接口要把 IWICBitmapSource 的方法按顺序重新写一遍
    [ComImport, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapScaler
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double dpiX, out double dpiY);
        void CopyPalette(IntPtr palette);
        void CopyPixels(IntPtr rect, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] buffer);
        void Initialize(IWICBitmapSource source, uint width, uint height, int mode);
    }

    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter
    {
        void GetSize(out uint width, out uint height);
        void GetPixelFormat(out Guid format);
        void GetResolution(out double dpiX, out double dpiY);
        void CopyPalette(IntPtr palette);
        void CopyPixels(IntPtr rect, uint stride, uint bufferSize, [Out, MarshalAs(UnmanagedType.LPArray)] byte[] buffer);
        void Initialize(IWICBitmapSource source, ref Guid format, int dither, IntPtr palette, double alphaThreshold, int paletteType);
    }
}
