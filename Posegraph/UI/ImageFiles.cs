namespace Posegraph.UI;

/// <summary>Resolves image imports without loading image contents into memory.</summary>
internal static class ImageFiles
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".avif",
    };

    internal static bool IsImage(string path) => Extensions.Contains(Path.GetExtension(path));

    /// <summary>Expands selected folders, ignoring inaccessible directories and directory links.</summary>
    internal static string[] Collect(IEnumerable<string> paths)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                    Add(file);
            }
            else Add(path);
        }
        return files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

        void Add(string path)
        {
            if (File.Exists(path) && IsImage(path))
                files.Add(Path.GetFullPath(path));
        }
    }
}
