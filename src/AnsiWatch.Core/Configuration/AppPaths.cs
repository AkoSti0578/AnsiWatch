namespace AnsiWatch.Core.Configuration;

/// <summary>Vaste locaties van AnsiWatch-bestanden.</summary>
public static class AppPaths
{
    /// <summary>Omgevingsvariabele om de datamap te overschrijven (tests, ontwikkeling).</summary>
    public const string DataDirectoryOverrideVariable = "ANSIWATCH_DATA_DIR";

    /// <summary>Naam van de Windows-service.</summary>
    public const string ServiceName = "AnsiWatch";

    /// <summary>Naam van de Event Log-bron.</summary>
    public const string EventSourceName = "AnsiWatch";

    /// <summary>Extensie van tijdelijke bestanden; de watcher negeert deze.</summary>
    public const string TempFileExtension = ".ansiwatch.tmp";

    /// <summary>%ProgramData%\AnsiWatch (of de override).</summary>
    public static string DataDirectory
    {
        get
        {
            var overridePath = Environment.GetEnvironmentVariable(DataDirectoryOverrideVariable);
            return !string.IsNullOrWhiteSpace(overridePath)
                ? overridePath
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AnsiWatch");
        }
    }

    /// <summary>%ProgramData%\AnsiWatch\config.json.</summary>
    public static string ConfigFile => Path.Combine(DataDirectory, "config.json");

    /// <summary>%ProgramData%\AnsiWatch\logs.</summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>%ProgramData%\AnsiWatch\status.json (live status, geschreven door de service).</summary>
    public static string StatusFile => Path.Combine(DataDirectory, "status.json");
}
