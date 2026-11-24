namespace AnsiWatch.Core.Configuration;

/// <summary>Hulpfuncties voor paden (hoofdletterongevoelig, zoals Windows).</summary>
public static class PathUtil
{
    /// <summary>Vergelijker voor paden.</summary>
    public static StringComparer Comparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>Volledig pad zonder afsluitende scheidingstekens.</summary>
    public static string Normalize(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = Path.GetFullPath(path.Trim());
        var root = Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>True als <paramref name="path"/> gelijk is aan of onder <paramref name="folder"/> ligt.</summary>
    public static bool IsSameOrUnder(string path, string folder)
    {
        try
        {
            var p = Normalize(path);
            var f = Normalize(folder);
            if (Comparer.Equals(p, f))
            {
                return true;
            }

            var prefix = f.EndsWith(Path.DirectorySeparatorChar) ? f : f + Path.DirectorySeparatorChar;
            return p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>True voor .txt-bestanden die geen tijdelijk AnsiWatch-bestand zijn.</summary>
    public static bool IsCandidateFile(string path)
    {
        if (string.IsNullOrEmpty(path) || path.EndsWith(AppPaths.TempFileExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }
}
