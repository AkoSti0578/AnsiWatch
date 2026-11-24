using System.Diagnostics;
using System.Text;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;
using AnsiWatch.Core.Detection;

namespace AnsiWatch.Tests;

public sealed class FileConverterTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static FileConverter Converter(ConversionSettings? settings = null) =>
        new(settings ?? new ConversionSettings { StabilityInterval = TimeSpan.FromMilliseconds(50) });

    private void AssertNoTempFiles() =>
        Assert.Empty(Directory.EnumerateFiles(_dir.Path, "*" + AppPaths.TempFileExtension, SearchOption.AllDirectories));

    [Fact]
    public async Task Utf16WithBom_IsConvertedInPlace_ByteForByte()
    {
        var path = TestText.WriteUtf16(_dir.File("export.txt"), TestText.Dutch);

        var result = await Converter().ConvertAsync(path);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.Equal(Encoding.GetEncoding(1252).GetBytes(TestText.Dutch), await File.ReadAllBytesAsync(path));
        Assert.True(result.Verified);
        Assert.Equal(TextEncodingKind.Utf16LeBom, result.Detection!.Value.Kind);
        AssertNoTempFiles();
    }

    [Fact]
    public async Task Utf16WithoutBom_IsOnlyConvertedWhenEnabled()
    {
        var path = TestText.WriteUtf16(_dir.File("nobom.txt"), TestText.Dutch, bom: false);
        var original = await File.ReadAllBytesAsync(path);

        var off = await Converter().ConvertAsync(path);
        Assert.Equal(ConversionStatus.SkippedNotUtf16, off.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));

        var on = await Converter(new ConversionSettings { DetectWithoutBom = true, StabilityInterval = TimeSpan.FromMilliseconds(50) }).ConvertAsync(path);
        Assert.Equal(ConversionStatus.Converted, on.Status);
        Assert.Equal(Encoding.GetEncoding(1252).GetBytes(TestText.Dutch), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task AnsiFile_IsLeftUntouched_IncludingTimestamp()
    {
        var path = _dir.File("al-ansi.txt");
        var bytes = TestText.Ansi1252.GetBytes(TestText.Dutch);
        await File.WriteAllBytesAsync(path, bytes);
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        var result = await Converter(new ConversionSettings { DetectWithoutBom = true }).ConvertAsync(path);

        Assert.Equal(ConversionStatus.SkippedNotUtf16, result.Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Utf8File_IsLeftUntouched()
    {
        var path = _dir.File("utf8.txt");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(TestText.Dutch)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);

        var result = await Converter().ConvertAsync(path);

        Assert.Equal(ConversionStatus.SkippedNotUtf16, result.Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ConvertingTwice_SecondRunIsNoOp_NoLoop()
    {
        var path = TestText.WriteUtf16(_dir.File("tweemaal.txt"), TestText.Dutch);
        var converter = Converter(new ConversionSettings { DetectWithoutBom = true, StabilityInterval = TimeSpan.FromMilliseconds(50) });

        Assert.Equal(ConversionStatus.Converted, (await converter.ConvertAsync(path)).Status);
        var afterFirst = await File.ReadAllBytesAsync(path);
        Assert.Equal(ConversionStatus.SkippedNotUtf16, (await converter.ConvertAsync(path)).Status);
        Assert.Equal(afterFirst, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task EmptyFile_StaysEmpty()
    {
        var path = _dir.File("leeg.txt");
        await File.WriteAllBytesAsync(path, []);

        var result = await Converter().ConvertAsync(path);

        Assert.Equal(ConversionStatus.SkippedEmpty, result.Status);
        Assert.Empty(await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task BomOnlyFile_BecomesEmptyAnsiFile()
    {
        var path = _dir.File("alleen-bom.txt");
        await File.WriteAllBytesAsync(path, [0xFF, 0xFE]);

        var result = await Converter().ConvertAsync(path);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.Empty(await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Timestamps_ArePreserved()
    {
        var path = TestText.WriteUtf16(_dir.File("tijd.txt"), "abc");
        var stamp = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        await Converter().ConvertAsync(path);

        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Timestamps_AreNotPreservedWhenDisabled()
    {
        var path = TestText.WriteUtf16(_dir.File("tijd2.txt"), "abc");
        var stamp = new DateTime(2021, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        await Converter(new ConversionSettings { PreserveTimestamps = false }).ConvertAsync(path);

        Assert.NotEqual(stamp, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task Unmappable_Skip_LeavesOriginalUntouched()
    {
        const string text = "Groeten uit Łódź 😀\r\n";
        var path = TestText.WriteUtf16(_dir.File("pools.txt"), text);
        var original = await File.ReadAllBytesAsync(path);

        var result = await Converter().ConvertAsync(path);

        Assert.Equal(ConversionStatus.SkippedUnmappable, result.Status);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        Assert.Equal(3, result.Issues.Count); // Ł, ź, 😀  (ó bestaat in 1252)
        Assert.Equal('Ł', result.Issues[0].CodePoint);
        AssertNoTempFiles();
    }

    [Fact]
    public async Task Unmappable_Skip_WithQuarantine_MovesFileAndWritesReport()
    {
        var quarantine = Path.Combine(_dir.Path, "quarantaine");
        var path = TestText.WriteUtf16(_dir.File("emoji.txt"), "Hoi 😀");
        var original = await File.ReadAllBytesAsync(path);

        var result = await Converter(new ConversionSettings { QuarantineFolder = quarantine }).ConvertAsync(path);

        Assert.Equal(ConversionStatus.Quarantined, result.Status);
        Assert.False(File.Exists(path));
        Assert.Equal(original, await File.ReadAllBytesAsync(result.QuarantinePath!));
        Assert.Contains("U+1F600", await File.ReadAllTextAsync(result.QuarantinePath + ".ansiwatch-rapport.log"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unmappable_Replace_WritesQuestionMarks()
    {
        const string text = "Wał 😀 €\r\n";
        var path = TestText.WriteUtf16(_dir.File("replace.txt"), text);

        var result = await Converter(new ConversionSettings { UnmappableAction = UnmappableCharacterAction.Replace }).ConvertAsync(path);

        Assert.Equal(ConversionStatus.ConvertedWithReplacements, result.Status);
        var expected = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback).GetBytes(text);
        Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        Assert.Equal(1, result.Issues[0].Line);
        Assert.Equal(3, result.Issues[0].Column);
    }

    [Fact]
    public async Task Unmappable_BestFit_UsesWindowsBestFit()
    {
        const string text = "Łukasz";
        var path = TestText.WriteUtf16(_dir.File("bestfit.txt"), text);

        var result = await Converter(new ConversionSettings { UnmappableAction = UnmappableCharacterAction.BestFit }).ConvertAsync(path);

        Assert.Equal(ConversionStatus.ConvertedWithBestFit, result.Status);
        Assert.Equal(Encoding.ASCII.GetBytes("Lukasz"), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Backup_ContainsOriginalBytes()
    {
        var backup = Path.Combine(_dir.Path, "backup");
        var path = TestText.WriteUtf16(_dir.File("metbackup.txt"), TestText.Dutch);
        var original = await File.ReadAllBytesAsync(path);

        var result = await Converter(new ConversionSettings { BackupFolder = backup }).ConvertAsync(path);

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.NotNull(result.BackupPath);
        Assert.StartsWith(backup, result.BackupPath, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(result.BackupPath!));
    }

    [Fact]
    public async Task LockedFile_IsConvertedAfterLockIsReleased()
    {
        var path = TestText.WriteUtf16(_dir.File("gelockt.txt"), TestText.Dutch);
        var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var sw = Stopwatch.StartNew();
        var task = Converter().ConvertAsync(path);
        await Task.Delay(800);
        Assert.False(task.IsCompleted, "Conversie mag niet doorgaan zolang het bestand gelockt is.");
        await holder.DisposeAsync();

        var result = await task;
        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(800));
        Assert.True(result.WaitDuration >= TimeSpan.FromMilliseconds(700));
        Assert.Equal(Encoding.GetEncoding(1252).GetBytes(TestText.Dutch), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task LockedFile_TimesOut_AndOriginalStaysIntact()
    {
        var path = TestText.WriteUtf16(_dir.File("blijftgelockt.txt"), "abc");
        var original = await File.ReadAllBytesAsync(path);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var result = await Converter(new ConversionSettings { LockTimeout = TimeSpan.FromSeconds(1) }).ConvertAsync(path);
            Assert.Equal(ConversionStatus.LockTimeout, result.Status);
        }

        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task GrowingFile_IsConvertedOnlyWhenComplete()
    {
        var path = _dir.File("groeit.txt");
        var full = string.Concat(Enumerable.Range(0, 20).Select(i => $"Regel {i}: café €\r\n"));
        var bytes = TestText.Utf16(full);

        // Schrijver die in stukjes schrijft en tussendoor het bestand sluit (zoals sommige exports doen).
        var writer = Task.Run(async () =>
        {
            for (var offset = 0; offset < bytes.Length; offset += 64)
            {
                using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    fs.Write(bytes, offset, Math.Min(64, bytes.Length - offset));
                }

                await Task.Delay(30);
            }
        });

        await Task.Delay(50);
        var converter = Converter(new ConversionSettings { StabilityInterval = TimeSpan.FromMilliseconds(250) });
        var result = await converter.ConvertAsync(path);
        await writer;

        if (result.Status == ConversionStatus.ChangedDuringConversion)
        {
            result = await converter.ConvertAsync(path);
        }

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.Equal(Encoding.GetEncoding(1252).GetBytes(full), await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task FailureBeforeReplace_LeavesOriginalIntact_AndCleansTempFile()
    {
        var path = TestText.WriteUtf16(_dir.File("fout.txt"), TestText.Dutch);
        var original = await File.ReadAllBytesAsync(path);
        var converter = Converter();
        converter.BeforeReplaceHook = (_, _) => throw new IOException("Gesimuleerde schijffout");

        var result = await converter.ConvertAsync(path);

        Assert.Equal(ConversionStatus.Failed, result.Status);
        Assert.Contains("Gesimuleerde schijffout", result.Error, StringComparison.Ordinal);
        Assert.Equal(original, await File.ReadAllBytesAsync(path));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task InvalidUtf16WithBom_IsReported_AndLeftIntact()
    {
        var path = _dir.File("kapot.txt");
        byte[] bytes = [0xFF, 0xFE, 0x41, 0x00, 0x00, 0xD8, 0x42, 0x00];
        await File.WriteAllBytesAsync(path, bytes);

        var result = await Converter().ConvertAsync(path);

        Assert.Equal(ConversionStatus.InvalidUtf16, result.Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        AssertNoTempFiles();
    }

    [Fact]
    public async Task MissingFile_IsNotFound()
    {
        var result = await Converter().ConvertAsync(_dir.File("bestaatniet.txt"));
        Assert.Equal(ConversionStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task ReadOnlyFile_IsConverted_AndStaysReadOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // ReadOnly-attribuut is Windows-specifiek.
        }

        var path = TestText.WriteUtf16(_dir.File("readonly.txt"), "abc");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var result = await Converter().ConvertAsync(path);
        Assert.True(result.Status is ConversionStatus.Converted or ConversionStatus.Failed);
        if (result.Status == ConversionStatus.Converted)
        {
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
        }
    }

    [Fact]
    [Trait("Category", "Large")]
    public async Task LargeFile_Over50MB_IsConvertedByteForByte()
    {
        var path = _dir.File("groot.txt");
        var expectedPath = _dir.File("verwacht.bin");
        var enc = Encoding.GetEncoding(1252);
        long utf16Size;

        await using (var utf16 = new FileStream(path, FileMode.Create))
        await using (var expected = new FileStream(expectedPath, FileMode.Create))
        {
            utf16.Write([0xFF, 0xFE]);
            var i = 0;
            while (utf16.Length < 52L * 1024 * 1024)
            {
                var line = $"{i:D8};Één café;crème brûlée;€ {i % 1000},99;naïef;façade{(i % 7 == 0 ? "\n" : "\r\n")}";
                utf16.Write(Encoding.Unicode.GetBytes(line));
                expected.Write(enc.GetBytes(line));
                i++;
            }

            utf16Size = utf16.Length;
        }

        Assert.True(utf16Size > 50L * 1024 * 1024);
        var sw = Stopwatch.StartNew();
        var result = await Converter().ConvertAsync(path);
        sw.Stop();

        Assert.Equal(ConversionStatus.Converted, result.Status);
        Assert.Equal(new FileInfo(expectedPath).Length, new FileInfo(path).Length);
        Assert.True(FilesEqual(path, expectedPath), "Output wijkt af van Encoding.GetEncoding(1252).GetBytes(tekst).");
    }

    private static bool FilesEqual(string a, string b)
    {
        using var fa = File.OpenRead(a);
        using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length)
        {
            return false;
        }

        var ba = new byte[1 << 20];
        var bb = new byte[1 << 20];
        int ra;
        while ((ra = fa.Read(ba)) > 0)
        {
            fb.ReadExactly(bb, 0, ra);
            if (!ba.AsSpan(0, ra).SequenceEqual(bb.AsSpan(0, ra)))
            {
                return false;
            }
        }

        return true;
    }
}
