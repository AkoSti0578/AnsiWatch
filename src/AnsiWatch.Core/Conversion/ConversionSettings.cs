using AnsiWatch.Core.Configuration;

namespace AnsiWatch.Core.Conversion;

/// <summary>Instellingen voor <see cref="FileConverter"/>.</summary>
public sealed record ConversionSettings
{
    /// <summary>Doel-codepage.</summary>
    public int CodePage { get; init; } = 1252;

    /// <summary>Gedrag bij niet-representeerbare tekens.</summary>
    public UnmappableCharacterAction UnmappableAction { get; init; } = UnmappableCharacterAction.Skip;

    /// <summary>UTF-16LE zonder BOM heuristisch herkennen.</summary>
    public bool DetectWithoutBom { get; init; }

    /// <summary>CreationTime en LastWriteTime van het origineel herstellen.</summary>
    public bool PreserveTimestamps { get; init; } = true;

    /// <summary>Back-upmap of null.</summary>
    public string? BackupFolder { get; init; }

    /// <summary>Quarantainemap of null.</summary>
    public string? QuarantineFolder { get; init; }

    /// <summary>Maximale wachttijd tot het bestand exclusief geopend kan worden en stabiel is.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tijd tussen twee groottecontroles; de grootte moet tussen twee checks gelijk blijven.</summary>
    public TimeSpan StabilityInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Na schrijven terugdecoderen en hashen ter verificatie.</summary>
    public bool VerifyAfterWrite { get; init; } = true;

    /// <summary>Maximaal aantal te rapporteren niet-representeerbare tekens.</summary>
    public int MaxReportedIssues { get; init; } = AnsiTranscoder.DefaultMaxIssues;
}
