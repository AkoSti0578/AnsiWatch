using System.Collections.Concurrent;
using System.Threading.Channels;
using AnsiWatch.Core.Configuration;

namespace AnsiWatch.Service.Processing;

/// <summary>
/// Wachtrij met debounce en ontdubbeling per pad. Elk event voor een pad schuift het moment van
/// verwerken op (debounce); zolang een pad wacht of in de rij staat, wordt het niet nog eens toegevoegd.
/// De verwerking gebeurt door consumers die via <see cref="ReadAllAsync"/> lezen.
/// </summary>
public sealed class ConversionQueue
{
    private readonly Channel<string> _ready = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentDictionary<string, long> _due = new(PathUtil.Comparer);
    private readonly ConcurrentDictionary<string, byte> _inProgress = new(PathUtil.Comparer);
    private readonly TimeProvider _time;

    /// <summary>Maakt een wachtrij.</summary>
    public ConversionQueue(TimeProvider? timeProvider = null) => _time = timeProvider ?? TimeProvider.System;

    /// <summary>Aantal paden dat wacht of klaarstaat.</summary>
    public int Count => _due.Count;

    /// <summary>Aantal paden dat nu verwerkt wordt.</summary>
    public int InProgressCount => _inProgress.Count;

    /// <summary>Totaal aantal geaccepteerde (niet-dubbele) aanmeldingen.</summary>
    public long TotalEnqueued => Interlocked.Read(ref _totalEnqueued);

    private long _totalEnqueued;

    /// <summary>Meldt een pad aan. Retourneert false als het al wachtte (dan is alleen de debounce verlengd).</summary>
    public bool Enqueue(string path, TimeSpan debounce)
    {
        var dueTicks = _time.GetTimestamp() + (long)(debounce.TotalSeconds * _time.TimestampFrequency);
        var added = false;
        _due.AddOrUpdate(path, _ => { added = true; return dueTicks; }, (_, existing) => Math.Max(existing, dueTicks));
        if (!added)
        {
            return false;
        }

        Interlocked.Increment(ref _totalEnqueued);
        if (debounce <= TimeSpan.Zero)
        {
            _ready.Writer.TryWrite(path);
        }
        else
        {
            _ = ReleaseWhenDueAsync(path);
        }

        return true;
    }

    /// <summary>Levert paden waarvan de debounce verstreken is.</summary>
    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) => _ready.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Markeert het pad als in behandeling. False als een andere consumer het al verwerkt;
    /// de aanroeper moet het dan opnieuw aanmelden.
    /// </summary>
    public bool TryBeginProcessing(string path)
    {
        if (!_inProgress.TryAdd(path, 0))
        {
            return false;
        }

        // Vanaf nu mag een nieuw event voor dit pad weer een nieuwe verwerking aanmelden.
        _due.TryRemove(path, out _);
        return true;
    }

    /// <summary>Markeert het pad als klaar.</summary>
    public void EndProcessing(string path) => _inProgress.TryRemove(path, out _);

    /// <summary>Sluit de wachtrij af.</summary>
    public void Complete() => _ready.Writer.TryComplete();

    private async Task ReleaseWhenDueAsync(string path)
    {
        try
        {
            while (_due.TryGetValue(path, out var due))
            {
                var remaining = _time.GetElapsedTime(_time.GetTimestamp(), due);
                if (due <= _time.GetTimestamp())
                {
                    break;
                }

                await Task.Delay(remaining, _time).ConfigureAwait(false);
            }

            if (_due.ContainsKey(path))
            {
                _ready.Writer.TryWrite(path);
            }
        }
        catch (Exception)
        {
            // Een timer kan alleen falen bij afsluiten; het pad wordt bij de volgende scan opnieuw gevonden.
            _due.TryRemove(path, out _);
        }
    }
}
