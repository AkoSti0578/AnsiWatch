using AnsiWatch.Core.Text;

namespace AnsiWatch.Core.Configuration;

/// <summary>Ernst van een validatiemelding.</summary>
public enum ValidationSeverity
{
    /// <summary>Configuratie werkt, maar let op.</summary>
    Warning,
    /// <summary>Configuratie is ongeldig.</summary>
    Error,
}

/// <summary>Eén validatiemelding.</summary>
public sealed record ValidationIssue(ValidationSeverity Severity, string Message)
{
    /// <inheritdoc />
    public override string ToString() => $"{(Severity == ValidationSeverity.Error ? "FOUT" : "Waarschuwing")}: {Message}";
}

/// <summary>Valideert <see cref="AnsiWatchOptions"/>.</summary>
public static class OptionsValidator
{
    /// <summary>Toegestane logniveaus.</summary>
    public static IReadOnlyList<string> LogLevels { get; } = new[] { "Verbose", "Debug", "Information", "Warning", "Error", "Fatal" };

    /// <summary>
    /// Valideert de opties. Met <paramref name="checkFileSystem"/> wordt ook gekeken of paden bestaan.
    /// Onbereikbare paden zijn een waarschuwing (een share kan tijdelijk offline zijn), geen fout.
    /// </summary>
    public static IReadOnlyList<ValidationIssue> Validate(AnsiWatchOptions options, bool checkFileSystem = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var issues = new List<ValidationIssue>();
        void Error(string m) => issues.Add(new ValidationIssue(ValidationSeverity.Error, m));
        void Warn(string m) => issues.Add(new ValidationIssue(ValidationSeverity.Warning, m));

        if (!CodePageSupport.IsValidTargetCodePage(options.CodePage, out var cpError))
        {
            Error(cpError!);
        }

        if (!Enum.IsDefined(options.UnmappableCharacterAction))
        {
            Error($"Onbekende UnmappableCharacterAction '{options.UnmappableCharacterAction}'.");
        }

        if (options.RescanIntervalSeconds is < 5 or > 86400)
        {
            Error("RescanIntervalSeconds moet tussen 5 en 86400 liggen.");
        }

        if (options.BackupRetentionDays is < 0 or > 3650)
        {
            Error("BackupRetentionDays moet tussen 0 (nooit opruimen) en 3650 liggen.");
        }

        if (options.DebounceMilliseconds is < 0 or > 60000)
        {
            Error("DebounceMilliseconds moet tussen 0 en 60000 liggen.");
        }

        if (options.LockTimeoutSeconds is < 1 or > 3600)
        {
            Error("LockTimeoutSeconds moet tussen 1 en 3600 liggen.");
        }

        if (options.MaxParallelism is < 1 or > 64)
        {
            Error("MaxParallelism moet tussen 1 en 64 liggen.");
        }

        if (!LogLevels.Contains(options.LogLevel, StringComparer.OrdinalIgnoreCase))
        {
            Error($"LogLevel '{options.LogLevel}' is ongeldig. Kies uit: {string.Join(", ", LogLevels)}.");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var watch in options.WatchPaths)
        {
            if (string.IsNullOrWhiteSpace(watch.Path))
            {
                Error("Een bewaakt pad is leeg.");
                continue;
            }

            if (!IsFullyQualified(watch.Path))
            {
                Error($"'{watch.Path}' is geen volledig pad (gebruik bijv. D:\\Export of \\\\server\\share\\map).");
                continue;
            }

            if (!seen.Add(PathUtil.Normalize(watch.Path)))
            {
                Error($"'{watch.Path}' staat dubbel in de lijst.");
            }

            if (checkFileSystem && watch.Enabled && !Directory.Exists(watch.Path))
            {
                Warn($"'{watch.Path}' bestaat niet of is (nu) niet bereikbaar. De service blijft het periodiek opnieuw proberen.");
            }
        }

        if (!options.WatchPaths.Any(p => p.Enabled))
        {
            Warn("Er zijn geen actieve bewaakte paden.");
        }

        ValidateSideFolder(options, options.BackupFolder, "BackupFolder", issues);
        ValidateSideFolder(options, options.QuarantineFolder, "QuarantineFolder", issues);
        return issues;
    }

    /// <summary>True als er geen fouten (wel eventueel waarschuwingen) zijn.</summary>
    public static bool IsValid(IEnumerable<ValidationIssue> issues) => issues.All(i => i.Severity != ValidationSeverity.Error);

    private static void ValidateSideFolder(AnsiWatchOptions options, string? folder, string name, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        if (!IsFullyQualified(folder))
        {
            issues.Add(new ValidationIssue(ValidationSeverity.Error, $"{name} '{folder}' is geen volledig pad."));
            return;
        }

        foreach (var watch in options.WatchPaths.Where(w => !string.IsNullOrWhiteSpace(w.Path) && IsFullyQualified(w.Path)))
        {
            if (PathUtil.IsSameOrUnder(folder, watch.Path))
            {
                issues.Add(new ValidationIssue(ValidationSeverity.Warning,
                    $"{name} '{folder}' ligt binnen het bewaakte pad '{watch.Path}'. AnsiWatch negeert deze map automatisch, maar een aparte locatie is overzichtelijker."));
            }
        }
    }

    private static bool IsFullyQualified(string path)
    {
        try
        {
            return Path.IsPathFullyQualified(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
