namespace AnsiWatch.Core.Configuration;

/// <summary>Maakt een initiële config.json aan (gebruikt door de MSI via de property WATCHPATH).</summary>
public static class ConfigInitializer
{
    /// <summary>
    /// Maakt config.json aan met de opgegeven paden, tenzij het bestand al bestaat en <paramref name="force"/> false is.
    /// Meerdere paden mogen met ';' gescheiden worden.
    /// </summary>
    /// <returns>True als er een bestand geschreven is.</returns>
    public static bool CreateInitialConfig(string configPath, string? watchPaths, bool includeSubfolders, bool force)
    {
        if (File.Exists(configPath) && !force)
        {
            return false;
        }

        var options = new AnsiWatchOptions();
        foreach (var path in SplitPaths(watchPaths))
        {
            options.WatchPaths.Add(new WatchPathOptions { Path = path, IncludeSubfolders = includeSubfolders, Enabled = true });
        }

        ConfigStore.Save(configPath, options);
        return true;
    }

    /// <summary>Splitst een ';'-gescheiden lijst, verwijdert quotes en dubbele/afsluitende backslashes.</summary>
    public static IEnumerable<string> SplitPaths(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        var seen = new HashSet<string>(PathUtil.Comparer);
        foreach (var raw in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = raw.Trim('"', ' ');
            if (path.Length > 3)
            {
                path = path.TrimEnd('\\', '/');
            }

            if (path.Length > 0 && seen.Add(path))
            {
                yield return path;
            }
        }
    }
}
