using System.Text.Json;
using AnsiWatch.Core.Configuration;

namespace AnsiWatch.Core.Status;

/// <summary>
/// Live status van de service, periodiek geschreven naar %ProgramData%\AnsiWatch\status.json.
/// De configuratietool toont deze informatie (per pad online/offline, tellers, recente conversies).
/// </summary>
public sealed class ServiceStatusSnapshot
{
    /// <summary>Versie van de service.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Proces-ID.</summary>
    public int ProcessId { get; set; }

    /// <summary>Account waaronder de service draait.</summary>
    public string Account { get; set; } = string.Empty;

    /// <summary>Starttijd (UTC).</summary>
    public DateTime StartedUtc { get; set; }

    /// <summary>Tijdstip van deze momentopname (UTC).</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Aantal geconverteerde bestanden sinds de start.</summary>
    public long Converted { get; set; }

    /// <summary>Aantal overgeslagen bestanden (geen UTF-16LE, leeg).</summary>
    public long Skipped { get; set; }

    /// <summary>Aantal waarschuwingen (niet-representeerbaar, lock-time-out, ...).</summary>
    public long Warnings { get; set; }

    /// <summary>Aantal fouten.</summary>
    public long Errors { get; set; }

    /// <summary>Totaal aantal geconverteerde bytes (origineel).</summary>
    public long BytesProcessed { get; set; }

    /// <summary>Bestanden in de wachtrij.</summary>
    public int QueueLength { get; set; }

    /// <summary>Melding als config.json ongeldig is.</summary>
    public string? ConfigError { get; set; }

    /// <summary>Status per bewaakt pad.</summary>
    public List<WatchPathStatus> Paths { get; set; } = new();

    /// <summary>De meest recente conversies (nieuwste eerst).</summary>
    public List<RecentConversion> Recent { get; set; } = new();

    /// <summary>Leest status.json; null als het bestand ontbreekt of onleesbaar is.</summary>
    public static ServiceStatusSnapshot? TryLoad(string? path = null)
    {
        try
        {
            path ??= AppPaths.StatusFile;
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<ServiceStatusSnapshot>(stream, ConfigStore.JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>Status van één bewaakt pad.</summary>
public sealed class WatchPathStatus
{
    /// <summary>Het pad.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Submappen meegenomen.</summary>
    public bool IncludeSubfolders { get; set; }

    /// <summary>Actief, Offline of Uitgeschakeld.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Toelichting (bijv. de foutmelding bij Offline).</summary>
    public string? Message { get; set; }

    /// <summary>Laatste vangnet-scan (UTC).</summary>
    public DateTime? LastScanUtc { get; set; }

    /// <summary>Aantal .txt-bestanden bij de laatste scan.</summary>
    public int FilesSeen { get; set; }
}

/// <summary>Eén recente conversie.</summary>
public sealed class RecentConversion
{
    /// <summary>Tijdstip (UTC).</summary>
    public DateTime TimeUtc { get; set; }

    /// <summary>Bestand.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Resultaat (ConversionStatus als tekst).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Grootte origineel.</summary>
    public long OriginalSize { get; set; }

    /// <summary>Grootte na conversie.</summary>
    public long NewSize { get; set; }

    /// <summary>Duur in ms.</summary>
    public long DurationMs { get; set; }

    /// <summary>Toelichting.</summary>
    public string? Message { get; set; }
}
