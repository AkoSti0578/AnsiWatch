namespace AnsiWatch.Core.Conversion;

/// <summary>Uitkomst van een bestandsconversie.</summary>
public enum ConversionStatus
{
    /// <summary>Geconverteerd, tekst exact behouden.</summary>
    Converted,
    /// <summary>Geconverteerd; niet-representeerbare tekens zijn '?' geworden.</summary>
    ConvertedWithReplacements,
    /// <summary>Geconverteerd met best-fit mapping.</summary>
    ConvertedWithBestFit,
    /// <summary>Geen UTF-16LE: ongemoeid gelaten.</summary>
    SkippedNotUtf16,
    /// <summary>Leeg bestand (0 bytes): niets te doen.</summary>
    SkippedEmpty,
    /// <summary>Niet geconverteerd wegens niet-representeerbare tekens (Skip).</summary>
    SkippedUnmappable,
    /// <summary>Niet geconverteerd wegens niet-representeerbare tekens en naar quarantaine verplaatst.</summary>
    Quarantined,
    /// <summary>Bestand bleef gelockt of groeide nog na de time-out.</summary>
    LockTimeout,
    /// <summary>Bestand bestaat (niet meer).</summary>
    NotFound,
    /// <summary>Bestand is tijdens de conversie gewijzigd; opnieuw proberen.</summary>
    ChangedDuringConversion,
    /// <summary>BOM zegt UTF-16LE, maar de inhoud is ongeldig.</summary>
    InvalidUtf16,
    /// <summary>Onverwachte fout; origineel is intact.</summary>
    Failed,
}

/// <summary>Hulpfuncties voor <see cref="ConversionStatus"/>.</summary>
public static class ConversionStatusExtensions
{
    /// <summary>True als het bestand daadwerkelijk herschreven is.</summary>
    public static bool IsConverted(this ConversionStatus status) =>
        status is ConversionStatus.Converted or ConversionStatus.ConvertedWithReplacements or ConversionStatus.ConvertedWithBestFit;

    /// <summary>True als het een (stille) overslag is zonder probleem.</summary>
    public static bool IsBenignSkip(this ConversionStatus status) =>
        status is ConversionStatus.SkippedNotUtf16 or ConversionStatus.SkippedEmpty or ConversionStatus.NotFound;

    /// <summary>True als het later opnieuw geprobeerd moet worden.</summary>
    public static bool ShouldRetry(this ConversionStatus status) =>
        status is ConversionStatus.ChangedDuringConversion or ConversionStatus.LockTimeout;

    /// <summary>Nederlandse omschrijving.</summary>
    public static string ToDutch(this ConversionStatus status) => status switch
    {
        ConversionStatus.Converted => "geconverteerd",
        ConversionStatus.ConvertedWithReplacements => "geconverteerd met vervangingen ('?')",
        ConversionStatus.ConvertedWithBestFit => "geconverteerd met best-fit mapping",
        ConversionStatus.SkippedNotUtf16 => "overgeslagen (geen UTF-16LE)",
        ConversionStatus.SkippedEmpty => "overgeslagen (leeg bestand)",
        ConversionStatus.SkippedUnmappable => "overgeslagen (niet-representeerbare tekens)",
        ConversionStatus.Quarantined => "naar quarantaine verplaatst (niet-representeerbare tekens)",
        ConversionStatus.LockTimeout => "time-out: bestand bleef in gebruik",
        ConversionStatus.NotFound => "bestand niet gevonden",
        ConversionStatus.ChangedDuringConversion => "bestand gewijzigd tijdens conversie",
        ConversionStatus.InvalidUtf16 => "ongeldige UTF-16LE-inhoud",
        ConversionStatus.Failed => "mislukt",
        _ => status.ToString(),
    };
}
