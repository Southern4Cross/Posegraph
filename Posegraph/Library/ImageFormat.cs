using System.Text;

namespace Posegraph.Library;

/// <summary>按文件头判断图片的真实格式（排查「扩展名是 .jpg、实际是别的格式」用，只写进日志）。</summary>
internal static class ImageFormat
{
    /// <summary>如「HEIC（ftyp heic）」「HTML 网页」「未知」，后面附前 16 字节的十六进制。</summary>
    public static string Describe(byte[] data)
    {
        if (data.Length == 0) return "空文件（0 字节）";
        return $"{Guess(data)}，{data.Length} 字节，文件头 {Convert.ToHexString(data, 0, Math.Min(16, data.Length))}";
    }

    private static string Guess(byte[] d)
    {
        bool Starts(params byte[] magic) => d.Length >= magic.Length && d.AsSpan(0, magic.Length).SequenceEqual(magic);
        string Ascii(int start, int length) => d.Length >= start + length ? Encoding.ASCII.GetString(d, start, length) : "";

        if (Starts(0xFF, 0xD8, 0xFF)) return "JPEG";
        if (Starts(0x89, 0x50, 0x4E, 0x47)) return "PNG";
        if (Ascii(0, 4) == "RIFF" && Ascii(8, 4) == "WEBP") return "WebP";
        if (Ascii(0, 4) is "GIF8") return "GIF";
        if (Starts(0x42, 0x4D)) return "BMP";
        if (Starts(0x49, 0x49, 0x2A, 0x00) || Starts(0x4D, 0x4D, 0x00, 0x2A)) return "TIFF";
        if (Starts(0x49, 0x49, 0xBC)) return "JPEG XR";
        if (Starts(0xFF, 0x0A) || Ascii(4, 4) == "JXL ") return "JPEG XL";
        if (Ascii(4, 4) == "ftyp")
        {
            var brand = Ascii(8, 4);
            var kind = brand switch
            {
                "avif" or "avis" => "AVIF",
                "heic" or "heix" or "heim" or "heis" or "hevc" or "hevx" => "HEIC",
                "mif1" or "msf1" => "HEIF",
                _ => "ISO 媒体（视频？）",
            };
            return $"{kind}（ftyp {brand}）";
        }
        if (Ascii(0, 4) == "8BPS") return "PSD";
        if (Ascii(0, 4) == "wxgf") return "微信 wxgf（需在微信里另存为图片）";
        var head = Encoding.UTF8.GetString(d, 0, Math.Min(256, d.Length)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (head.StartsWith('<'))
            return head.Contains("<svg", StringComparison.OrdinalIgnoreCase) ? "SVG 矢量图" : "HTML / XML 网页（多半是防盗链页面另存成了图片）";
        if (head.StartsWith('{')) return "JSON 文本";
        return "未知格式";
    }
}
