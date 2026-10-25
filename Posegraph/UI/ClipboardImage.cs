using Dalamud.Plugin.Services;

namespace Posegraph.UI;

/// <summary>Reads clipboard images through Dalamud and encodes them for the recognition backend.</summary>
internal static class ClipboardImage
{
    public static async Task<byte[]?> ReadPngAsync(
        ITextureProvider textures,
        ITextureReadbackProvider readback,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!textures.HasClipboardImage()) return null;

        var encoder = readback.GetSupportedImageEncoderInfos()
            .FirstOrDefault(codec => codec.MimeTypes.Contains("image/png", StringComparer.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(Loc.T("未找到 PNG 图像编码器"));

        using var texture = await textures.CreateFromClipboardAsync("Posegraph clipboard", cancellationToken);
        using var stream = new MemoryStream();
        await readback.SaveToStreamAsync(texture, encoder.ContainerGuid, stream,
            props: null, leaveWrapOpen: true, leaveStreamOpen: true, cancellationToken: cancellationToken);
        return stream.ToArray();
    }
}
