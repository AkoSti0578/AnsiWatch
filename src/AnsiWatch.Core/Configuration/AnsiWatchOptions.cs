using AnsiWatch.Core.Conversion;

namespace AnsiWatch.Core.Configuration;

/// <summary>
/// Volledig configuratiemodel. Wordt opgeslagen als %ProgramData%\AnsiWatch\config.json.
/// </summary>
public sealed class AnsiWatchOptions
{
    /// <summary>Te bewaken mappen.</summary>
    public List<WatchPathOptions> WatchPaths { get; set; } = new();

    /// <summary>Doel-codepage ("ANSI"). Standaard 1252 (West-Europees).</summary>
    public int CodePage { get; set; } = 1252;

    /// <summary>Gedrag bij tekens die niet in de codepage passen.</summary>
    public UnmappableCharacterAction UnmappableCharacterAction { get; set; } = UnmappableCharacterAction.Skip;

    /// <summary>Ook UTF-16LE zonder BOM herkennen (heuristiek).</summary>
    public bool DetectWithoutBom { get; set; }

    /// <summary>Originele CreationTime en LastWriteTime behouden.</summary>
    public bool PreserveTimestamps { get; set; } = true;

    /// <summary>Optionele back-upmap; leeg = geen back-up.</summary>
    public string? BackupFolder { get; set; }

    /// <summary>Aantal dagen dat back-ups bewaard blijven (0 = nooit opruimen).</summary>
    public int BackupRetentionDays { get; set; } = 30;

    /// <summary>Optionele quarantainemap voor bestanden die niet geconverteerd konden worden.</summary>
    public string? QuarantineFolder { get; set; }

    /// <summary>Interval van de vangnet-scan in seconden.</summary>
    public int RescanIntervalSeconds { get; set; } = 30;

    /// <summary>Verbose, Debug, Information, Warning, Error of Fatal.</summary>
    public string LogLevel { get; set; } = "Information";

    // ---- Geavanceerd (optioneel in config.json) ----

    /// <summary>Debounce per bestand in milliseconden.</summary>
    public int DebounceMilliseconds { get; set; } = 400;

    /// <summary>Maximale wachttijd op een gelockt bestand in seconden.</summary>
    public int LockTimeoutSeconds { get; set; } = 30;

    /// <summary>Maximaal aantal gelijktijdige conversies.</summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>Na het schrijven terugdecoderen en vergelijken met de bron (SHA-256).</summary>
    public bool VerifyAfterWrite { get; set; } = true;

    /// <summary>Diepe kopie.</summary>
    public AnsiWatchOptions Clone()
    {
        var clone = (AnsiWatchOptions)MemberwiseClone();
        clone.WatchPaths = WatchPaths.Select(p => p.Clone()).ToList();
        return clone;
    }

    /// <summary>Vertaalt naar de instellingen voor de bestandsconverter.</summary>
    public ConversionSettings ToConversionSettings() => new()
    {
        CodePage = CodePage,
        UnmappableAction = UnmappableCharacterAction,
        DetectWithoutBom = DetectWithoutBom,
        PreserveTimestamps = PreserveTimestamps,
        BackupFolder = string.IsNullOrWhiteSpace(BackupFolder) ? null : BackupFolder,
        QuarantineFolder = string.IsNullOrWhiteSpace(QuarantineFolder) ? null : QuarantineFolder,
        LockTimeout = TimeSpan.FromSeconds(Math.Max(1, LockTimeoutSeconds)),
        VerifyAfterWrite = VerifyAfterWrite,
    };
}
