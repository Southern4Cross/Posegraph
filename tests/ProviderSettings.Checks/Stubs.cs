using System.Globalization;

namespace Posegraph;

internal static class Loc
{
    public static string T(string text) => text;
    public static string F(string text, params object?[] args)
        => string.Format(CultureInfo.InvariantCulture, text, args);
}
