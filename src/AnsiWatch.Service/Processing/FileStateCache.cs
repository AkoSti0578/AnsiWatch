using System.Collections.Concurrent;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;

namespace AnsiWatch.Service.Processing;

/// <summary>
/// Onthoudt per bestand de laatst geziene (grootte, wijzigingstijd) en uitkomst, zodat de vangnet-scan
/// ongewijzigde bestanden niet steeds opnieuw opent. Gelockte bestanden krijgen een oplopende back-off.
/// </summary>
public sealed class FileStateCache
{
    private readonly ConcurrentDictionary<string, FileState> _states = new(PathUtil.Comparer);
    private readonly TimeProvider _time;

    /// <summary>Maakt een cache.</summary>
    public FileStateCache(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>Aantal bekende bestanden.</summary>
    public int Count => _states.Count;

    /// <summary>Bepaalt of een bij de scan gevonden bestand (opnieuw) verwerkt moet worden.</summary>
    public bool ShouldProcess(string path, long length, DateTime lastWriteUtc)
    {
        if (!_states.TryGetValue(path, out var state))
        {
            return true;
        }

        if (state.Length != length || state.LastWriteUtc != lastWriteUtc)
        {
            return true;
        }

        return state.RetryAfterUtc is { } retry && _time.GetUtcNow().UtcDateTime >= retry;
    }

    /// <summary>Legt de uitkomst van een verwerking vast.</summary>
    /// <returns>Het aantal opeenvolgende pogingen met dezelfde (herhaalbare) uitkomst.</returns>
    public int Record(string path, ConversionStatus status)
    {
        if (status == ConversionStatus.NotFound || status == ConversionStatus.Quarantined)
        {
            _states.TryRemove(path, out _);
            return 1;
        }

        long length;
        DateTime lastWrite;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                _states.TryRemove(path, out _);
                return 1;
            }

            length = info.Length;
            lastWrite = info.LastWriteTimeUtc;
        }
        catch (IOException)
        {
            return 1;
        }

        var attempts = 1;
        DateTime? retryAfter = null;
        if (status.ShouldRetry() || status == ConversionStatus.Failed)
        {
            attempts = _states.TryGetValue(path, out var previous) && previous.Status == status ? previous.Attempts + 1 : 1;
            // 1, 2, 4, 8 ... minuten, maximaal 30.
            var minutes = Math.Min(30, 1 << Math.Min(attempts - 1, 5));
            retryAfter = _time.GetUtcNow().UtcDateTime.AddMinutes(minutes);
        }

        _states[path] = new FileState(length, lastWrite, status, retryAfter, attempts);
        return attempts;
    }

    /// <summary>Verwijdert bekende bestanden onder <paramref name="root"/> die niet gezien zijn en niet meer bestaan.</summary>
    public void Prune(string root, IReadOnlySet<string> seen)
    {
        foreach (var key in _states.Keys)
        {
            if (!seen.Contains(key) && PathUtil.IsSameOrUnder(key, root) && !File.Exists(key))
            {
                _states.TryRemove(key, out _);
            }
        }
    }

    /// <summary>Wist alles (bijv. na een wijziging van codepage of detectie-instellingen).</summary>
    public void Clear() => _states.Clear();

    private sealed record FileState(long Length, DateTime LastWriteUtc, ConversionStatus Status, DateTime? RetryAfterUtc, int Attempts);
}
