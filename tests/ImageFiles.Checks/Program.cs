using Posegraph.UI;

var fixture = Path.Combine(Path.GetTempPath(), $"Posegraph-ImageFiles-{Guid.NewGuid():N}");
Directory.CreateDirectory(fixture);
try
{
    var nested = Path.Combine(fixture, "nested");
    Directory.CreateDirectory(nested);
    var alpha = Create("a.PNG");
    var zeta = Create("Zebra.jpeg");
    var nestedImage = Create("nested/b.avif");
    Create("nested/readme.txt");
    Create("false.jp");
    Create("double.png.exe");
    var expected = new[] { alpha, nestedImage, zeta }.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

    Check(ImageFiles.Collect([zeta, fixture, alpha, nestedImage]).SequenceEqual(expected),
          "mixed files/folders expand recursively, deduplicate and sort deterministically");
    Check(ImageFiles.Collect([alpha, alpha.ToUpperInvariant()]).Length == 1,
          "Windows path case variants are deduplicated");
    Check(ImageFiles.Collect([Path.Combine(fixture, "missing.png"), Path.Combine(fixture, "missing-folder"),
                              Path.Combine(fixture, "nested/readme.txt"), "", "::invalid::*"] ).Length == 0,
          "unsupported and nonexistent inputs are ignored");
    Check(ImageFiles.Collect([Path.Combine(nested, "..", "a.PNG")]).SequenceEqual([alpha]),
          "file paths are normalized to full paths");
    foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif", ".tif", ".tiff", ".avif" })
        Check(ImageFiles.IsImage("photo" + extension.ToUpperInvariant()), $"accepts {extension}, case insensitive");
    foreach (var name in new[] { "photo.jp", "photo.p", "photo.iff", "photo.png.exe", "photo", "photo." })
        Check(!ImageFiles.IsImage(name), $"rejects false extension match {name}");

    // A junction back to an ancestor must not cause recursion or duplicate its files.
    var link = Path.Combine(nested, "loop");
    try
    {
        Directory.CreateSymbolicLink(link, fixture);
        Check(ImageFiles.Collect([fixture]).SequenceEqual(expected), "does not follow directory symlink loops");
    }
    catch (UnauthorizedAccessException)
    {
        Console.WriteLine("SKIP: directory symlink check requires Windows developer mode or elevation.");
    }
    finally
    {
        if (Directory.Exists(link)) Directory.Delete(link);
    }
    Console.WriteLine("Image import checks passed.");

    string Create(string relative)
    {
        var path = Path.Combine(fixture, relative.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllText(path, "image content is intentionally not decoded by collection");
        return path;
    }
}
finally
{
    var absolute = Path.GetFullPath(fixture);
    var prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(absolute).StartsWith("Posegraph-ImageFiles-", StringComparison.Ordinal))
        throw new InvalidOperationException("Refusing to remove unexpected fixture path.");
    Directory.Delete(absolute, true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAILED: " + message);
    Console.WriteLine("PASS: " + message);
}
