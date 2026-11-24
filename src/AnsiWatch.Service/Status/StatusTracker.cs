using System.Reflection;
using System.Text.Json;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;
using AnsiWatch.Core.Status;

namespace AnsiWatch.Service.Status;

/// <summary>Houdt tellers en recente conversies bij en schrijft status.json.</summary>
public sealed class StatusTracker
{
    private const int MaxRecent = 50;
    private readonly object _gate = new();
    private readonly LinkedList<RecentConversion> _recent = new();
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private long _converted;
    private long _skipped;
    private long _warnings;
    private long _errors;
    private long _bytes;
    private int _dirty = 1;

    /// <summary>Versie (informatief) van de service.</summary>
    public static string Version { get; } =
        typeof(StatusTracker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(StatusTracker).Assembly.GetName().Version?.ToString() ?? "?";

    /// <summary>Aantal geconverteerde bestanden.</summary>
    public long Converted => Interlocked.Read(ref _converted);

    /// <summary>Registreert een resultaat.</summary>
    public void Record(ConversionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Status)
        {
            case var s when s.IsConverted():
                Interlocked.Increment(ref _converted);
                Interlocked.Add(ref _bytes, result.OriginalSize);
                if (s != ConversionStatus.Converted)
                {
                    Interlocked.Increment(ref _warnings);
                }

                break;
            case var s when s.IsBenignSkip():
                Interlocked.Increment(ref _skipped);
                return; // overgeslagen bestanden niet in "recent" (zou die lijst laten vollopen)
            case ConversionStatus.Failed:
                Interlocked.Increment(ref _errors);
                break;
            case ConversionStatus.ChangedDuringConversion:
                return;
            default:
                Interlocked.Increment(ref _warnings);
                break;
        }

        lock (_gate)
        {
            _recent.AddFirst(new RecentConversion
            {
                TimeUtc = DateTime.UtcNow,
                Path = result.Path,
                Status = result.Status.ToString(),
                OriginalSize = result.OriginalSize,
                NewSize = result.NewSize,
                DurationMs = (long)result.Duration.TotalMilliseconds,
                Message = result.Error ?? (result.Issues.Count > 0 ? result.DescribeIssues(3) : null),
            });
            while (_recent.Count > MaxRecent)
            {
                _recent.RemoveLast();
            }
        }

        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Markeert de status als gewijzigd (bijv. padstatus).</summary>
    public void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>Maakt een momentopname.</summary>
    public ServiceStatusSnapshot Snapshot(IReadOnlyList<WatchPathStatus> paths, int queueLength, string? configError)
    {
        lock (_gate)
        {
            return new ServiceStatusSnapshot
            {
                Version = Version,
                ProcessId = Environment.ProcessId,
                Account = $"{Environment.UserDomainName}\\{Environment.UserName}",
                StartedUtc = _startedUtc,
                UpdatedUtc = DateTime.UtcNow,
                Converted = Interlocked.Read(ref _converted),
                Skipped = Interlocked.Read(ref _skipped),
                Warnings = Interlocked.Read(ref _warnings),
                Errors = Interlocked.Read(ref _errors),
                BytesProcessed = Interlocked.Read(ref _bytes),
                QueueLength = queueLength,
                ConfigError = configError,
                Paths = paths.ToList(),
                Recent = _recent.ToList(),
            };
        }
    }

    /// <summary>Schrijft status.json atomair.</summary>
    public static void Write(ServiceStatusSnapshot snapshot, string? path = null)
    {
        path ??= AppPaths.StatusFile;
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, ConfigStore.JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>True (en reset) als er iets gewijzigd is sinds de vorige aanroep.</summary>
    public bool ConsumeDirty() => Interlocked.Exchange(ref _dirty, 0) == 1;
}
