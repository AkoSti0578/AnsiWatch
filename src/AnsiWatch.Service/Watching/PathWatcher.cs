using System.IO.Enumeration;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Diagnostics;
using AnsiWatch.Core.Status;

namespace AnsiWatch.Service.Watching;

/// <summary>Een bij de scan gevonden kandidaat.</summary>
public readonly record struct ScannedFile(string Path, long Length, DateTime LastWriteUtc);

/// <summary>
/// Bewaakt één map: FileSystemWatcher (Created/Changed/Renamed, filter *.txt, 64 KB buffer) plus
/// een volledige scan als vangnet. Een onbereikbaar pad (offline share) wordt gelogd en periodiek
/// opnieuw geprobeerd; de service crasht nooit door een pad.
/// </summary>
public sealed class PathWatcher : IDisposable
{
    private const int BufferSize = 64 * 1024;

    private readonly ILogger _logger;
    private readonly Action<string> _onFileEvent;
    private readonly Action<PathWatcher> _onRescanRequested;
    private readonly Func<string, bool> _isExcluded;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private bool _disposed;
    private bool _wasOffline;
    private int _scanning;

    /// <summary>Maakt een watcher (start nog niet).</summary>
    public PathWatcher(
        WatchPathOptions options,
        ILogger logger,
        Action<string> onFileEvent,
        Action<PathWatcher> onRescanRequested,
        Func<string, bool> isExcluded)
    {
        Options = options.Clone();
        _logger = logger;
        _onFileEvent = onFileEvent;
        _onRescanRequested = onRescanRequested;
        _isExcluded = isExcluded;
    }

    /// <summary>Configuratie van dit pad.</summary>
    public WatchPathOptions Options { get; }

    /// <summary>Unieke sleutel (pad + submappen).</summary>
    public string Key => MakeKey(Options);

    /// <summary>True als de FileSystemWatcher actief is.</summary>
    public bool IsOnline
    {
        get { lock (_gate) { return _watcher is not null; } }
    }

    /// <summary>Laatste foutmelding.</summary>
    public string? LastError { get; private set; }

    /// <summary>Laatste geslaagde scan.</summary>
    public DateTime? LastScanUtc { get; private set; }

    /// <summary>Aantal kandidaten bij de laatste scan.</summary>
    public int FilesSeen { get; private set; }

    /// <summary>Sleutel voor een padconfiguratie.</summary>
    public static string MakeKey(WatchPathOptions options) =>
        $"{SafeNormalize(options.Path).ToUpperInvariant()}|{options.IncludeSubfolders}";

    /// <summary>
    /// Start de watcher als die nog niet draait. Retourneert true als hij (opnieuw) online kwam,
    /// zodat de aanroeper een volledige scan kan doen.
    /// </summary>
    public bool EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null)
            {
                return false;
            }
        }

        // Directory.Exists kan op een offline share lang blokkeren: buiten de lock.
        bool exists;
        try
        {
            exists = Directory.Exists(Options.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            exists = false;
            LastError = ex.Message;
        }

        if (!exists)
        {
            MarkOffline(LastError ?? "map bestaat niet of is niet bereikbaar");
            return false;
        }

        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(Options.Path, "*.txt")
            {
                IncludeSubdirectories = Options.IncludeSubfolders,
                InternalBufferSize = BufferSize,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            watcher.Created += OnChanged;
            watcher.Changed += OnChanged;
            watcher.Renamed += OnRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            MarkOffline(ex.Message);
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                watcher.Dispose();
                return false;
            }

            _watcher = watcher;
        }

        LastError = null;
        if (_wasOffline)
        {
            _wasOffline = false;
            _logger.LogInformation(AnsiWatchEventIds.WatchPathRecovered, "Pad {Path} is weer bereikbaar; bewaking hervat.", Options.Path);
        }
        else
        {
            _logger.LogInformation(AnsiWatchEventIds.WatchStarted, "Bewaking gestart: {Path} (submappen: {Subfolders}).", Options.Path, Options.IncludeSubfolders);
        }

        return true;
    }

    /// <summary>
    /// Volledige scan van de map. Levert alle kandidaat-.txt-bestanden; ook verweesde tijdelijke bestanden
    /// ouder dan <paramref name="tempCleanupAge"/> worden opgeruimd (indien opgegeven).
    /// </summary>
    public List<ScannedFile>? Scan(CancellationToken cancellationToken, TimeSpan? tempCleanupAge = null)
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1)
        {
            return null; // vorige scan loopt nog (bijv. trage share)
        }

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = Options.IncludeSubfolders,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.Directory,
                ReturnSpecialDirectories = false,
            };

            var files = new List<ScannedFile>();
            var enumerable = new FileSystemEnumerable<ScannedFile>(
                Options.Path,
                (ref FileSystemEntry e) => new ScannedFile(e.ToFullPath(), e.Length, e.LastWriteTimeUtc.UtcDateTime),
                options)
            {
                ShouldIncludePredicate = (ref FileSystemEntry e) =>
                    !e.IsDirectory && FileSystemName.MatchesSimpleExpression("*.txt", e.FileName, ignoreCase: true),
            };

            foreach (var file in enumerable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (PathUtil.IsCandidateFile(file.Path) && !_isExcluded(file.Path))
                {
                    files.Add(file);
                }
            }

            if (tempCleanupAge is { } age)
            {
                CleanupTempFiles(age, options);
            }

            LastScanUtc = DateTime.UtcNow;
            FilesSeen = files.Count;
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Share weggevallen tijdens de scan: watcher stoppen; de periodieke lus probeert het opnieuw.
            StopWatcher();
            MarkOffline(ex.Message);
            return null;
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    /// <summary>Statusregel voor status.json.</summary>
    public WatchPathStatus GetStatus() => new()
    {
        Path = Options.Path,
        IncludeSubfolders = Options.IncludeSubfolders,
        State = IsOnline ? "Actief" : "Offline",
        Message = LastError,
        LastScanUtc = LastScanUtc,
        FilesSeen = FilesSeen,
    };

    private void CleanupTempFiles(TimeSpan age, EnumerationOptions options)
    {
        var removed = 0;
        foreach (var temp in Directory.EnumerateFiles(Options.Path, "*" + AppPaths.TempFileExtension, options))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(temp) > age)
                {
                    File.Delete(temp);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // in gebruik: volgende keer
            }
        }

        if (removed > 0)
        {
            _logger.LogInformation(AnsiWatchEventIds.TempCleanup, "{Count} verweesd(e) tijdelijk(e) bestand(en) opgeruimd in {Path}.", removed, Options.Path);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e) => Forward(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e) => Forward(e.FullPath);

    private void Forward(string path)
    {
        if (PathUtil.IsCandidateFile(path) && !_isExcluded(path))
        {
            _onFileEvent(path);
        }
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        var ex = e.GetException();
        if (ex is InternalBufferOverflowException)
        {
            // Te veel events tegelijk: we weten niet welke we gemist hebben → volledige rescan.
            _logger.LogWarning(AnsiWatchEventIds.WatcherOverflow, "Buffer-overflow van de watcher op {Path}; volledige rescan wordt uitgevoerd.", Options.Path);
            _onRescanRequested(this);
            return;
        }

        // Meestal: netwerkpad weggevallen. Watcher opnieuw opbouwen zodra het pad weer bereikbaar is.
        _logger.LogWarning(AnsiWatchEventIds.WatcherError, ex, "Watcher-fout op {Path}: {Error}. Bewaking wordt opnieuw opgebouwd.", Options.Path, ex.Message);
        StopWatcher();
        LastError = ex.Message;
        _wasOffline = true;
        _onRescanRequested(this);
    }

    private void MarkOffline(string reason)
    {
        LastError = reason;
        if (!_wasOffline)
        {
            _wasOffline = true;
            _logger.LogWarning(AnsiWatchEventIds.WatchPathUnavailable,
                "Pad {Path} is niet bereikbaar ({Reason}). AnsiWatch blijft het periodiek opnieuw proberen.", Options.Path, reason);
        }
    }

    private void StopWatcher()
    {
        FileSystemWatcher? watcher;
        lock (_gate)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is not null)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // negeren
            }
        }
    }

    private static string SafeNormalize(string path)
    {
        try
        {
            return PathUtil.Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        StopWatcher();
    }
}
