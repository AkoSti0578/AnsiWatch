using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;
using AnsiWatch.Core.Status;
using AnsiWatch.Service;
using AnsiWatch.Service.Configuration;
using AnsiWatch.Service.Status;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnsiWatch.Tests;

/// <summary>End-to-end: echte FileSystemWatcher, wachtrij en converter, zonder Windows-service-host.</summary>
[Collection("Service")]
public sealed class ServiceIntegrationTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private readonly ConcurrentQueue<ConversionResult> _results = new();
    private ConfigMonitor? _config;
    private AnsiWatchWorker? _worker;

    private string Watch => Path.Combine(_dir.Path, "watch");

    private string ConfigPath => Path.Combine(_dir.Path, "data", "config.json");

    private string StatusPath => Path.Combine(_dir.Path, "data", "status.json");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Watch);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync(CancellationToken.None);
            _worker.Dispose();
        }

        _config?.Dispose();
        _dir.Dispose();
    }

    private async Task StartAsync(AnsiWatchOptions options)
    {
        ConfigStore.Save(ConfigPath, options);
        _config = new ConfigMonitor(NullLogger<ConfigMonitor>.Instance, ConfigPath);
        _worker = new AnsiWatchWorker(_config, NullLoggerFactory.Instance, new StatusTracker(), StatusPath);
        _worker.Processed += r => _results.Enqueue(r);
        await _worker.StartAsync(CancellationToken.None);
    }

    private static AnsiWatchOptions Options(params string[] paths) => new()
    {
        WatchPaths = paths.Select(p => new WatchPathOptions { Path = p }).ToList(),
        RescanIntervalSeconds = 5,
    };

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
        }

        return condition();
    }

    private static bool IsAnsi(string path, string text)
    {
        try
        {
            return File.ReadAllBytes(path).AsSpan().SequenceEqual(Encoding.GetEncoding(1252).GetBytes(text));
        }
        catch (IOException)
        {
            return false;
        }
    }

    [Fact]
    public async Task NewFile_IsConvertedWithinAboutOneSecond()
    {
        await StartAsync(Options(Watch));
        await Task.Delay(300); // watcher actief

        var path = Path.Combine(Watch, "nieuw.txt");
        var sw = Stopwatch.StartNew();
        TestText.WriteUtf16(path, TestText.Dutch);

        Assert.True(await WaitUntilAsync(() => IsAnsi(path, TestText.Dutch), TimeSpan.FromSeconds(5)), "Bestand is niet geconverteerd.");
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"Conversie duurde {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task ExistingFiles_AreConvertedByInitialScan_AnsiFilesUntouched()
    {
        var utf16 = TestText.WriteUtf16(Path.Combine(Watch, "bestond-al.txt"), "Één");
        var ansi = Path.Combine(Watch, "ansi.txt");
        File.WriteAllBytes(ansi, TestText.Ansi1252.GetBytes("Één"));
        var ansiStamp = File.GetLastWriteTimeUtc(ansi);

        await StartAsync(Options(Watch));

        Assert.True(await WaitUntilAsync(() => IsAnsi(utf16, "Één"), TimeSpan.FromSeconds(5)));
        Assert.True(IsAnsi(ansi, "Één"));
        Assert.Equal(ansiStamp, File.GetLastWriteTimeUtc(ansi));
    }

    [Fact]
    public async Task ManyFiles_AreAllConverted_EachExactlyOnce()
    {
        await StartAsync(Options(Watch));
        await Task.Delay(300);

        const int count = 200;
        for (var i = 0; i < count; i++)
        {
            TestText.WriteUtf16(Path.Combine(Watch, $"bulk{i:D3}.txt"), $"Regel {i}: café €\r\n");
        }

        Assert.True(await WaitUntilAsync(
            () => Enumerable.Range(0, count).All(i => IsAnsi(Path.Combine(Watch, $"bulk{i:D3}.txt"), $"Regel {i}: café €\r\n")),
            TimeSpan.FromSeconds(30)));

        await Task.Delay(1000);
        var converted = _results.Where(r => r.Status.IsConverted()).GroupBy(r => r.Path).ToList();
        Assert.Equal(count, converted.Count);
        Assert.All(converted, g => Assert.Single(g));
        Assert.DoesNotContain(_results, r => r.Status == ConversionStatus.Failed);
    }

    [Fact]
    public async Task TempFiles_AndOtherExtensions_AreIgnored()
    {
        await StartAsync(Options(Watch));
        await Task.Delay(300);

        var csv = Path.Combine(Watch, "data.csv");
        File.WriteAllBytes(csv, TestText.Utf16("a;b"));
        var trigger = TestText.WriteUtf16(Path.Combine(Watch, "trigger.txt"), "x");

        Assert.True(await WaitUntilAsync(() => IsAnsi(trigger, "x"), TimeSpan.FromSeconds(5)));
        Assert.Equal(TestText.Utf16("a;b"), File.ReadAllBytes(csv));
        Assert.DoesNotContain(_results, r => r.Path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConfigChange_AddsNewPath_WithoutRestart()
    {
        var second = Path.Combine(_dir.Path, "tweede");
        Directory.CreateDirectory(second);
        await StartAsync(Options(Watch));

        var early = TestText.WriteUtf16(Path.Combine(second, "vooraf.txt"), "vooraf");
        await Task.Delay(1500);
        Assert.False(IsAnsi(early, "vooraf"), "Pad is nog niet geconfigureerd, mag niet geconverteerd zijn.");

        ConfigStore.Save(ConfigPath, Options(Watch, second));

        Assert.True(await WaitUntilAsync(() => IsAnsi(early, "vooraf"), TimeSpan.FromSeconds(15)), "Nieuw pad niet opgepakt na configwijziging.");
        var live = TestText.WriteUtf16(Path.Combine(second, "live.txt"), "live");
        Assert.True(await WaitUntilAsync(() => IsAnsi(live, "live"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task InvalidConfig_KeepsPreviousConfiguration()
    {
        await StartAsync(Options(Watch));
        await File.WriteAllTextAsync(ConfigPath, "{ \"WatchPaths\": [ kapot ");
        Assert.True(await WaitUntilAsync(() => _config!.LastError is not null, TimeSpan.FromSeconds(15)));

        var path = TestText.WriteUtf16(Path.Combine(Watch, "nogsteeds.txt"), "werkt");
        Assert.True(await WaitUntilAsync(() => IsAnsi(path, "werkt"), TimeSpan.FromSeconds(5)), "Vorige configuratie had actief moeten blijven.");
    }

    [Fact]
    public async Task OfflinePath_DoesNotCrash_AndIsPickedUpWhenItAppears()
    {
        var later = Path.Combine(_dir.Path, "komt-later");
        await StartAsync(Options(Watch, later));
        await Task.Delay(500);

        var status = ServiceStatusSnapshot.TryLoad(StatusPath);
        Assert.True(await WaitUntilAsync(() => ServiceStatusSnapshot.TryLoad(StatusPath)?.Paths.Any(p => p.Path == later && p.State == "Offline") == true, TimeSpan.FromSeconds(5)));

        Directory.CreateDirectory(later);
        var path = TestText.WriteUtf16(Path.Combine(later, "eindelijk.txt"), "online");
        Assert.True(await WaitUntilAsync(() => IsAnsi(path, "online"), TimeSpan.FromSeconds(12)), "Pad dat later beschikbaar komt, is niet opgepakt.");
    }

    [Fact]
    public async Task Subfolders_OnlyWhenEnabled()
    {
        var sub = Path.Combine(Watch, "sub");
        Directory.CreateDirectory(sub);
        await StartAsync(new AnsiWatchOptions
        {
            WatchPaths = { new WatchPathOptions { Path = Watch, IncludeSubfolders = true } },
            RescanIntervalSeconds = 5,
        });
        await Task.Delay(300);

        var path = TestText.WriteUtf16(Path.Combine(sub, "diep.txt"), "diep");
        Assert.True(await WaitUntilAsync(() => IsAnsi(path, "diep"), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task QuarantineFolderInsideWatchPath_IsIgnored()
    {
        var quarantine = Path.Combine(Watch, "quarantaine");
        await StartAsync(new AnsiWatchOptions
        {
            WatchPaths = { new WatchPathOptions { Path = Watch, IncludeSubfolders = true } },
            QuarantineFolder = quarantine,
            RescanIntervalSeconds = 5,
        });
        await Task.Delay(300);

        var path = TestText.WriteUtf16(Path.Combine(Watch, "pools.txt"), "Łódź");
        Assert.True(await WaitUntilAsync(() => File.Exists(Path.Combine(quarantine, "pools.txt")), TimeSpan.FromSeconds(5)));
        await Task.Delay(1000);
        Assert.Single(_results, r => r.Status == ConversionStatus.Quarantined);
        Assert.DoesNotContain(_results, r => r.Path.StartsWith(quarantine, StringComparison.Ordinal));
        Assert.Equal(TestText.Utf16("Łódź"), File.ReadAllBytes(Path.Combine(quarantine, "pools.txt")));
    }
}
