using System.Diagnostics;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;
using AnsiWatch.Core.Diagnostics;
using AnsiWatch.Service.Configuration;
using AnsiWatch.Service.Logging;
using AnsiWatch.Service.Maintenance;
using AnsiWatch.Service.Processing;
using AnsiWatch.Service.Status;
using AnsiWatch.Service.Watching;

namespace AnsiWatch.Service;

/// <summary>
/// De kern van de service: koppelt watchers en vangnet-scans aan de wachtrij en verwerkt die met
/// beperkte parallelliteit. Configuratiewijzigingen worden live toegepast.
/// </summary>
public sealed class AnsiWatchWorker : BackgroundService
{
    private static readonly TimeSpan TempCleanupAge = TimeSpan.FromMinutes(15);

    private readonly ConfigMonitor _config;
    private readonly ILogger<AnsiWatchWorker> _logger;
    private readonly StatusTracker _status;
    private readonly ConversionQueue _queue = new();
    private readonly FileStateCache _cache = new();
    private readonly WatchManager _watchManager;
    private readonly string? _statusFile;
    private readonly object _applyGate = new();
    private volatile AnsiWatchOptions _options = new();
    private volatile ConversionSettings _settings = new();
    private CancellationToken _stopping;
    private DateTime _lastTempCleanup = DateTime.MinValue;

    /// <summary>Maakt de worker.</summary>
    public AnsiWatchWorker(ConfigMonitor config, ILoggerFactory loggerFactory, StatusTracker status, string? statusFile = null)
    {
        _config = config;
        _status = status;
        _statusFile = statusFile;
        _logger = loggerFactory.CreateLogger<AnsiWatchWorker>();
        _watchManager = new WatchManager(loggerFactory, OnFileEvent, w => _ = ScanAsync(w, ScanReason.Recovery));
    }

    /// <summary>De wachtrij (voor tests en status).</summary>
    internal ConversionQueue Queue => _queue;

    /// <summary>Wordt aangeroepen na elke verwerking (voor tests).</summary>
    internal event Action<ConversionResult>? Processed;

    private enum ScanReason
    {
        Initial,
        Periodic,
        Recovery,
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        _logger.LogInformation(AnsiWatchEventIds.ServiceStarted,
            "AnsiWatch {Version} gestart als {Account} (PID {Pid}). Configuratie: {ConfigPath}. Event Log: {EventLog}.",
            StatusTracker.Version, $"{Environment.UserDomainName}\\{Environment.UserName}", Environment.ProcessId, _config.ConfigPath,
            LoggingSetup.EventLogEnabled ? "actief" : "niet beschikbaar");

        try
        {
            var initial = _config.Current;
            ApplyOptions(initial);
            _config.Changed += ApplyOptions;

            var parallelism = Math.Clamp(initial.MaxParallelism, 1, 64);
            var consumers = Enumerable.Range(0, parallelism).Select(_ => Task.Run(() => ConsumeAsync(stoppingToken), stoppingToken)).ToList();
            var maintenance = Task.Run(() => MaintenanceLoopAsync(stoppingToken), stoppingToken);

            await Task.WhenAll(consumers.Append(maintenance)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // normaal stoppen
        }
        catch (Exception ex)
        {
            _logger.LogCritical(AnsiWatchEventIds.ServiceError, ex, "Fatale fout in AnsiWatch: {Error}. De service stopt; Windows herstart hem volgens de herstelinstellingen.", ex.Message);
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            _config.Changed -= ApplyOptions;
        }
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _queue.Complete();
        TryWriteStatus(force: true);
        _watchManager.Dispose();
        _logger.LogInformation(AnsiWatchEventIds.ServiceStopped, "AnsiWatch gestopt. {Converted} bestand(en) geconverteerd in deze sessie.", _status.Converted);
    }

    /// <summary>Past (nieuwe) configuratie toe zonder herstart.</summary>
    internal void ApplyOptions(AnsiWatchOptions options)
    {
        IReadOnlyList<PathWatcher> added;
        lock (_applyGate)
        {
            var previous = _options;
            LoggingSetup.ApplyLevel(options.LogLevel);

            // Andere conversieregels: eerder overgeslagen bestanden opnieuw beoordelen.
            if (previous.CodePage != options.CodePage
                || previous.DetectWithoutBom != options.DetectWithoutBom
                || previous.UnmappableCharacterAction != options.UnmappableCharacterAction)
            {
                _cache.Clear();
            }

            if (previous.MaxParallelism != options.MaxParallelism && _stopping.CanBeCanceled)
            {
                _logger.LogWarning(AnsiWatchEventIds.ConfigWarning, "MaxParallelism is gewijzigd naar {Value}; dit wordt actief na een herstart van de service.", options.MaxParallelism);
            }

            _options = options;
            _settings = options.ToConversionSettings();
            added = _watchManager.Apply(options);
        }

        foreach (var watcher in added)
        {
            _ = ScanAsync(watcher, ScanReason.Initial);
        }

        _status.MarkDirty();
    }

    private void OnFileEvent(string path) =>
        _queue.Enqueue(path, TimeSpan.FromMilliseconds(_options.DebounceMilliseconds));

    private async Task ScanAsync(PathWatcher watcher, ScanReason reason)
    {
        try
        {
            await Task.Yield();
            var cameOnline = watcher.EnsureStarted();
            if (!watcher.IsOnline)
            {
                _status.MarkDirty();
                return;
            }

            var cleanupTemps = DateTime.UtcNow - _lastTempCleanup > TimeSpan.FromHours(1);
            var files = watcher.Scan(_stopping, cleanupTemps ? TempCleanupAge : null);
            if (files is null)
            {
                return;
            }

            if (cleanupTemps)
            {
                _lastTempCleanup = DateTime.UtcNow;
            }

            var debounce = TimeSpan.FromMilliseconds(_options.DebounceMilliseconds);
            var seen = new HashSet<string>(PathUtil.Comparer);
            var enqueued = 0;
            var recent = DateTime.UtcNow - TimeSpan.FromSeconds(2);
            foreach (var file in files)
            {
                seen.Add(file.Path);
                if (_cache.ShouldProcess(file.Path, file.Length, file.LastWriteUtc)
                    && _queue.Enqueue(file.Path, file.LastWriteUtc > recent ? debounce : TimeSpan.Zero))
                {
                    enqueued++;
                }
            }

            _cache.Prune(watcher.Options.Path, seen);
            _status.MarkDirty();

            var effectiveReason = cameOnline && reason == ScanReason.Periodic ? ScanReason.Recovery : reason;
            if (enqueued > 0)
            {
                if (effectiveReason == ScanReason.Periodic)
                {
                    _logger.LogInformation(AnsiWatchEventIds.RescanFoundMissed,
                        "Vangnet-scan van {Path}: {Count} nieuw(e) of gewijzigd(e) bestand(en) aangeboden.", watcher.Options.Path, enqueued);
                }
                else
                {
                    _logger.LogInformation("Scan ({Reason}) van {Path}: {Count} van {Total} bestand(en) aangeboden ter controle.",
                        effectiveReason == ScanReason.Initial ? "initieel" : "herstel", watcher.Options.Path, enqueued, files.Count);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(AnsiWatchEventIds.ServiceError, ex, "Scan van {Path} mislukt: {Error}", watcher.Options.Path, ex.Message);
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        await foreach (var path in _queue.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            if (!_queue.TryBeginProcessing(path))
            {
                // Wordt al door een andere consumer verwerkt: later opnieuw.
                _queue.Enqueue(path, TimeSpan.FromMilliseconds(Math.Max(250, _options.DebounceMilliseconds)));
                continue;
            }

            try
            {
                var converter = new FileConverter(_settings);
                var result = await converter.ConvertAsync(path, stoppingToken).ConfigureAwait(false);
                Handle(result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Mag nooit voorkomen (FileConverter vangt alles), maar een consumer mag nooit sterven.
                _logger.LogError(AnsiWatchEventIds.FileConversionFailed, ex, "Onverwachte fout bij verwerken van {Path}: {Error}", path, ex.Message);
            }
            finally
            {
                _queue.EndProcessing(path);
            }
        }
    }

    private void Handle(ConversionResult r)
    {
        var attempts = _cache.Record(r.Path, r.Status);
        _status.Record(r);
        var ms = (long)r.Duration.TotalMilliseconds;
        var waitMs = (long)r.WaitDuration.TotalMilliseconds;

        switch (r.Status)
        {
            case ConversionStatus.Converted:
                _logger.LogInformation(AnsiWatchEventIds.FileConverted,
                    "Geconverteerd: {Path} | {OriginalSize} → {NewSize} bytes | {DurationMs} ms (waarvan {WaitMs} ms wachten) | resultaat: {Result}{Verified}",
                    r.Path, r.OriginalSize, r.NewSize, ms, waitMs, r.Status, r.Verified ? " | geverifieerd" : string.Empty);
                break;
            case ConversionStatus.ConvertedWithReplacements:
                _logger.LogWarning(AnsiWatchEventIds.FileConvertedWithReplacements,
                    "Geconverteerd met vervangingen ('?'): {Path} | {OriginalSize} → {NewSize} bytes | {DurationMs} ms | niet-representeerbaar in codepage {CodePage}: {Issues}",
                    r.Path, r.OriginalSize, r.NewSize, ms, _settings.CodePage, r.DescribeIssues());
                break;
            case ConversionStatus.ConvertedWithBestFit:
                _logger.LogWarning(AnsiWatchEventIds.FileConvertedWithBestFit,
                    "Geconverteerd met best-fit mapping: {Path} | {OriginalSize} → {NewSize} bytes | {DurationMs} ms | benaderde tekens: {Issues}",
                    r.Path, r.OriginalSize, r.NewSize, ms, r.DescribeIssues());
                break;
            case ConversionStatus.SkippedUnmappable:
                _logger.LogWarning(AnsiWatchEventIds.FileSkippedUnmappable,
                    "NIET geconverteerd: {Path} bevat tekens die niet in codepage {CodePage} bestaan: {Issues}. Het bestand is ongewijzigd (UnmappableCharacterAction=Skip).",
                    r.Path, _settings.CodePage, r.DescribeIssues());
                break;
            case ConversionStatus.Quarantined:
                _logger.LogWarning(AnsiWatchEventIds.FileQuarantined,
                    "NIET geconverteerd en naar quarantaine verplaatst: {Path} → {QuarantinePath}. Niet-representeerbaar: {Issues}",
                    r.Path, r.QuarantinePath, r.DescribeIssues());
                break;
            case ConversionStatus.LockTimeout:
                if (attempts == 1)
                {
                    _logger.LogWarning(AnsiWatchEventIds.FileLockTimeout, "Time-out: {Path} bleef in gebruik ({Error}). Wordt later opnieuw geprobeerd.", r.Path, r.Error);
                }
                else
                {
                    _logger.LogDebug(AnsiWatchEventIds.FileLockTimeout, "Nog steeds in gebruik (poging {Attempt}): {Path}", attempts, r.Path);
                }

                break;
            case ConversionStatus.ChangedDuringConversion:
                _logger.LogDebug(AnsiWatchEventIds.FileSkipped, "{Path} gewijzigd tijdens conversie; opnieuw aangeboden.", r.Path);
                _queue.Enqueue(r.Path, TimeSpan.FromMilliseconds(Math.Max(250, _options.DebounceMilliseconds)));
                break;
            case ConversionStatus.InvalidUtf16:
                _logger.LogWarning(AnsiWatchEventIds.FileInvalidUtf16, "Ongeldige UTF-16LE in {Path}: {Error}. Bestand ongewijzigd.", r.Path, r.Error);
                break;
            case ConversionStatus.Failed:
                _logger.Log(attempts == 1 ? LogLevel.Error : LogLevel.Debug, AnsiWatchEventIds.FileConversionFailed, r.Exception,
                    "Conversie mislukt: {Path} | {DurationMs} ms | {Error}. Het origineel is intact.", r.Path, ms, r.Error);
                break;
            default:
                _logger.LogDebug(AnsiWatchEventIds.FileSkipped, "Overgeslagen: {Path} | {Size} bytes | {Result} ({Detection})",
                    r.Path, r.OriginalSize, r.Status.ToDutch(), r.Detection?.Description ?? "-");
                break;
        }

        foreach (var note in r.Notes)
        {
            _logger.LogWarning(AnsiWatchEventIds.FileConverted, "{Path}: {Note}", r.Path, note);
        }

        Processed?.Invoke(r);
    }

    private async Task MaintenanceLoopAsync(CancellationToken stoppingToken)
    {
        var lastRescan = Stopwatch.StartNew();
        var lastConfigPoll = Stopwatch.StartNew();
        var lastStatus = Stopwatch.StartNew();
        var lastRetention = Stopwatch.StartNew();
        var retentionDone = false;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var options = _options;
                if (lastRescan.Elapsed >= TimeSpan.FromSeconds(Math.Max(5, options.RescanIntervalSeconds)))
                {
                    lastRescan.Restart();
                    foreach (var watcher in _watchManager.Watchers)
                    {
                        _ = ScanAsync(watcher, ScanReason.Periodic);
                    }
                }

                if (lastConfigPoll.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    lastConfigPoll.Restart();
                    _config.PollForChanges();
                }

                if (lastStatus.Elapsed >= TimeSpan.FromSeconds(2))
                {
                    var force = lastStatus.Elapsed >= TimeSpan.FromSeconds(10);
                    if (TryWriteStatus(force))
                    {
                        lastStatus.Restart();
                    }
                }

                if ((!retentionDone && lastRetention.Elapsed >= TimeSpan.FromMinutes(1)) || lastRetention.Elapsed >= TimeSpan.FromHours(1))
                {
                    retentionDone = true;
                    lastRetention.Restart();
                    if (!string.IsNullOrWhiteSpace(options.BackupFolder))
                    {
                        var folder = options.BackupFolder;
                        var days = options.BackupRetentionDays;
                        _ = Task.Run(() => BackupRetention.Cleanup(folder, days, _logger), stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(AnsiWatchEventIds.ServiceError, ex, "Fout in onderhoudslus: {Error}", ex.Message);
            }
        }
    }

    private bool TryWriteStatus(bool force)
    {
        if (!_status.ConsumeDirty() && !force)
        {
            return false;
        }

        try
        {
            var snapshot = _status.Snapshot(_watchManager.GetStatus(), _queue.Count, _config.LastError);
            StatusTracker.Write(snapshot, _statusFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "status.json kon niet geschreven worden.");
            return false;
        }
    }
}
