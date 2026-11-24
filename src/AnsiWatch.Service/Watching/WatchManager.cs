using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Diagnostics;
using AnsiWatch.Core.Status;

namespace AnsiWatch.Service.Watching;

/// <summary>
/// Beheert de verzameling <see cref="PathWatcher"/>s en past die live aan bij een configuratiewijziging
/// (toevoegen, verwijderen, submappen aan/uit) zonder herstart.
/// </summary>
public sealed class WatchManager : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WatchManager> _logger;
    private readonly Action<string> _onFileEvent;
    private readonly Action<PathWatcher> _onRescanRequested;
    private readonly object _gate = new();
    private Dictionary<string, PathWatcher> _watchers = new(StringComparer.Ordinal);
    private List<WatchPathOptions> _disabled = new();
    private volatile string[] _excludedFolders = Array.Empty<string>();

    /// <summary>Maakt de manager.</summary>
    public WatchManager(ILoggerFactory loggerFactory, Action<string> onFileEvent, Action<PathWatcher> onRescanRequested)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<WatchManager>();
        _onFileEvent = onFileEvent;
        _onRescanRequested = onRescanRequested;
    }

    /// <summary>Actieve watchers.</summary>
    public IReadOnlyList<PathWatcher> Watchers
    {
        get { lock (_gate) { return _watchers.Values.ToList(); } }
    }

    /// <summary>Past de set watchers aan. Retourneert de nieuw toegevoegde watchers.</summary>
    public IReadOnlyList<PathWatcher> Apply(AnsiWatchOptions options)
    {
        _excludedFolders = new[] { options.BackupFolder, options.QuarantineFolder }
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f!)
            .ToArray();

        var desired = options.WatchPaths
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.Path))
            .GroupBy(PathWatcher.MakeKey)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var added = new List<PathWatcher>();
        var removed = new List<PathWatcher>();
        lock (_gate)
        {
            var next = new Dictionary<string, PathWatcher>(StringComparer.Ordinal);
            foreach (var (key, pathOptions) in desired)
            {
                if (_watchers.TryGetValue(key, out var existing))
                {
                    next[key] = existing;
                }
                else
                {
                    var watcher = new PathWatcher(pathOptions, _loggerFactory.CreateLogger<PathWatcher>(), _onFileEvent, _onRescanRequested, IsExcluded);
                    next[key] = watcher;
                    added.Add(watcher);
                }
            }

            removed.AddRange(_watchers.Where(kv => !next.ContainsKey(kv.Key)).Select(kv => kv.Value));
            _watchers = next;
            _disabled = options.WatchPaths.Where(p => !p.Enabled).Select(p => p.Clone()).ToList();
        }

        foreach (var watcher in removed)
        {
            watcher.Dispose();
            _logger.LogInformation(AnsiWatchEventIds.WatchStopped, "Bewaking gestopt: {Path}.", watcher.Options.Path);
        }

        return added;
    }

    /// <summary>True als het bestand in de back-up- of quarantainemap ligt.</summary>
    public bool IsExcluded(string path)
    {
        foreach (var folder in _excludedFolders)
        {
            if (PathUtil.IsSameOrUnder(path, folder))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Status van alle paden (ook uitgeschakelde).</summary>
    public List<WatchPathStatus> GetStatus()
    {
        lock (_gate)
        {
            var list = _watchers.Values.Select(w => w.GetStatus()).ToList();
            list.AddRange(_disabled.Select(d => new WatchPathStatus { Path = d.Path, IncludeSubfolders = d.IncludeSubfolders, State = "Uitgeschakeld" }));
            return list;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }
}
