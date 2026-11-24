using AnsiWatch.Core.Conversion;
using AnsiWatch.Service.Maintenance;
using AnsiWatch.Service.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnsiWatch.Tests;

public sealed class QueueAndCacheTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Queue_DeduplicatesAndDebounces()
    {
        var queue = new ConversionQueue();
        Assert.True(queue.Enqueue("/x/a.txt", TimeSpan.FromMilliseconds(200)));
        Assert.False(queue.Enqueue("/x/a.txt", TimeSpan.FromMilliseconds(200)));
        Assert.False(queue.Enqueue("/X/A.TXT", TimeSpan.FromMilliseconds(200))); // Windows: hoofdletterongevoelig
        Assert.True(queue.Enqueue("/x/b.txt", TimeSpan.Zero));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = new List<(string Path, DateTime At)>();
        var start = DateTime.UtcNow;
        await foreach (var path in queue.ReadAllAsync(cts.Token))
        {
            received.Add((path, DateTime.UtcNow));
            Assert.True(queue.TryBeginProcessing(path));
            queue.EndProcessing(path);
            if (received.Count == 2)
            {
                break;
            }
        }

        Assert.Equal("/x/b.txt", received[0].Path);
        Assert.Equal("/x/a.txt", received[1].Path);
        Assert.True(received[1].At - start >= TimeSpan.FromMilliseconds(180));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task Queue_EventsDuringDebounce_ExtendTheWait()
    {
        var queue = new ConversionQueue();
        var start = DateTime.UtcNow;
        queue.Enqueue("/x/a.txt", TimeSpan.FromMilliseconds(200));
        await Task.Delay(150);
        queue.Enqueue("/x/a.txt", TimeSpan.FromMilliseconds(200));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var _ in queue.ReadAllAsync(cts.Token))
        {
            break;
        }

        Assert.True(DateTime.UtcNow - start >= TimeSpan.FromMilliseconds(330));
    }

    [Fact]
    public void Queue_InProgress_PreventsParallelProcessingOfSamePath()
    {
        var queue = new ConversionQueue();
        queue.Enqueue("/x/a.txt", TimeSpan.Zero);
        Assert.True(queue.TryBeginProcessing("/x/a.txt"));
        Assert.True(queue.Enqueue("/x/a.txt", TimeSpan.Zero)); // nieuw event tijdens verwerken wordt geaccepteerd
        Assert.False(queue.TryBeginProcessing("/x/a.txt"));
        queue.EndProcessing("/x/a.txt");
        Assert.True(queue.TryBeginProcessing("/x/a.txt"));
    }

    [Fact]
    public void Cache_SkipsUnchanged_RetriesChanged()
    {
        var cache = new FileStateCache();
        var path = TestText.WriteUtf16(_dir.File("c.txt"), "abc");
        var info = new FileInfo(path);
        Assert.True(cache.ShouldProcess(path, info.Length, info.LastWriteTimeUtc));

        cache.Record(path, ConversionStatus.SkippedNotUtf16);
        info.Refresh();
        Assert.False(cache.ShouldProcess(path, info.Length, info.LastWriteTimeUtc));
        Assert.True(cache.ShouldProcess(path, info.Length + 1, info.LastWriteTimeUtc));
    }

    [Fact]
    public void Cache_LockTimeout_UsesBackoff()
    {
        var cache = new FileStateCache();
        var path = TestText.WriteUtf16(_dir.File("lock.txt"), "abc");
        Assert.Equal(1, cache.Record(path, ConversionStatus.LockTimeout));
        Assert.Equal(2, cache.Record(path, ConversionStatus.LockTimeout));
        var info = new FileInfo(path);
        Assert.False(cache.ShouldProcess(path, info.Length, info.LastWriteTimeUtc)); // nog in back-off
    }

    [Fact]
    public void BackupRetention_RemovesOldDateFolders()
    {
        var backup = _dir.File("backup");
        var old = Path.Combine(backup, "2020-01-01");
        var recent = Path.Combine(backup, DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(recent);
        File.WriteAllText(Path.Combine(old, "oud.txt"), "x");
        File.WriteAllText(Path.Combine(recent, "nieuw.txt"), "x");

        var removed = BackupRetention.Cleanup(backup, 30, NullLogger.Instance);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(old));
        Assert.True(File.Exists(Path.Combine(recent, "nieuw.txt")));
    }
}
