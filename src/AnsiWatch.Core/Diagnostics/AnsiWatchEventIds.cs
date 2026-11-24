using Microsoft.Extensions.Logging;

namespace AnsiWatch.Core.Diagnostics;

/// <summary>
/// Vaste event-ID's. Deze nummers verschijnen in het Windows Event Log (bron "AnsiWatch")
/// en zijn gedocumenteerd in README.md. Wijzig bestaande nummers nooit: monitoring (RMM) kan erop filteren.
/// </summary>
public static class AnsiWatchEventIds
{
#pragma warning disable CA1707 // Underscores: leesbaar in documentatie.
    // 1xxx – service
    /// <summary>Service gestart.</summary>
    public static readonly EventId ServiceStarted = new(1000, nameof(ServiceStarted));
    /// <summary>Service gestopt.</summary>
    public static readonly EventId ServiceStopped = new(1001, nameof(ServiceStopped));
    /// <summary>Configuratie (opnieuw) geladen.</summary>
    public static readonly EventId ConfigLoaded = new(1002, nameof(ConfigLoaded));
    /// <summary>Configuratie ongeldig; vorige configuratie blijft actief.</summary>
    public static readonly EventId ConfigInvalid = new(1003, nameof(ConfigInvalid));
    /// <summary>Configuratiewaarschuwing.</summary>
    public static readonly EventId ConfigWarning = new(1004, nameof(ConfigWarning));
    /// <summary>Onverwachte fout in de service.</summary>
    public static readonly EventId ServiceError = new(1900, nameof(ServiceError));

    // 2xxx – conversies
    /// <summary>Bestand geconverteerd.</summary>
    public static readonly EventId FileConverted = new(2000, nameof(FileConverted));
    /// <summary>Geconverteerd met '?'-vervangingen.</summary>
    public static readonly EventId FileConvertedWithReplacements = new(2001, nameof(FileConvertedWithReplacements));
    /// <summary>Geconverteerd met best-fit mapping.</summary>
    public static readonly EventId FileConvertedWithBestFit = new(2002, nameof(FileConvertedWithBestFit));
    /// <summary>Overgeslagen wegens niet-representeerbare tekens.</summary>
    public static readonly EventId FileSkippedUnmappable = new(2003, nameof(FileSkippedUnmappable));
    /// <summary>Naar quarantaine verplaatst.</summary>
    public static readonly EventId FileQuarantined = new(2004, nameof(FileQuarantined));
    /// <summary>Bestand bleef gelockt.</summary>
    public static readonly EventId FileLockTimeout = new(2005, nameof(FileLockTimeout));
    /// <summary>Ongeldige UTF-16LE.</summary>
    public static readonly EventId FileInvalidUtf16 = new(2006, nameof(FileInvalidUtf16));
    /// <summary>Conversie mislukt.</summary>
    public static readonly EventId FileConversionFailed = new(2007, nameof(FileConversionFailed));
    /// <summary>Bestand overgeslagen (geen UTF-16LE); alleen in het logbestand op Debug-niveau.</summary>
    public static readonly EventId FileSkipped = new(2010, nameof(FileSkipped));

    // 3xxx – bewaking
    /// <summary>Pad wordt bewaakt.</summary>
    public static readonly EventId WatchStarted = new(3000, nameof(WatchStarted));
    /// <summary>Pad niet bereikbaar.</summary>
    public static readonly EventId WatchPathUnavailable = new(3001, nameof(WatchPathUnavailable));
    /// <summary>Pad weer bereikbaar.</summary>
    public static readonly EventId WatchPathRecovered = new(3002, nameof(WatchPathRecovered));
    /// <summary>Buffer-overflow van de FileSystemWatcher; volledige rescan.</summary>
    public static readonly EventId WatcherOverflow = new(3003, nameof(WatcherOverflow));
    /// <summary>Fout in de FileSystemWatcher.</summary>
    public static readonly EventId WatcherError = new(3004, nameof(WatcherError));
    /// <summary>Pad niet meer bewaakt.</summary>
    public static readonly EventId WatchStopped = new(3005, nameof(WatchStopped));
    /// <summary>Vangnet-scan vond bestanden die de watcher gemist had.</summary>
    public static readonly EventId RescanFoundMissed = new(3006, nameof(RescanFoundMissed));

    // 4xxx – onderhoud
    /// <summary>Back-ups opgeruimd.</summary>
    public static readonly EventId BackupCleanup = new(4000, nameof(BackupCleanup));
    /// <summary>Opruimen back-ups mislukt.</summary>
    public static readonly EventId BackupCleanupFailed = new(4001, nameof(BackupCleanupFailed));
    /// <summary>Verweesde tijdelijke bestanden opgeruimd.</summary>
    public static readonly EventId TempCleanup = new(4002, nameof(TempCleanup));
#pragma warning restore CA1707

    /// <summary>Event-ID's die ook op Information-niveau naar het Event Log gaan.</summary>
    public static IReadOnlySet<int> AlwaysToEventLog { get; } = new HashSet<int>
    {
        ServiceStarted.Id, ServiceStopped.Id, ConfigLoaded.Id, WatchPathRecovered.Id,
    };
}
