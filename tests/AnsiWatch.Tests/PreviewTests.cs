using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;

namespace AnsiWatch.Tests;

public sealed class PreviewTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Preview_DoesNotModifyFile_AndShowsIssues()
    {
        var path = TestText.WriteUtf16(_dir.File("preview.txt"), "Café\r\nWał 😀\n");
        var before = File.ReadAllBytes(path);
        var stamp = File.GetLastWriteTimeUtc(path);

        var preview = FilePreview.Create(path, new ConversionSettings());

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.Equal(ConversionStatus.SkippedUnmappable, preview.ExpectedStatus);
        Assert.Equal(2, preview.Issues.Count);
        Assert.Equal("Café\r\nWa? ??\n", preview.PreviewText);
        Assert.Equal((1, 1), preview.LineEndings);
    }

    [Fact]
    public void Preview_Clean_ShowsConvertedSize()
    {
        var path = TestText.WriteUtf16(_dir.File("clean.txt"), TestText.Dutch);
        var preview = FilePreview.Create(path, new ConversionSettings());
        Assert.Equal(ConversionStatus.Converted, preview.ExpectedStatus);
        Assert.Equal(TestText.Dutch.Length, preview.ConvertedSize);
        Assert.Equal(TestText.Dutch, preview.PreviewText);
        Assert.StartsWith("C9 E9 6E", preview.PreviewHex, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_BestFit_ExpectsBestFit()
    {
        var path = TestText.WriteUtf16(_dir.File("bf.txt"), "Łukasz");
        var preview = FilePreview.Create(path, new ConversionSettings { UnmappableAction = UnmappableCharacterAction.BestFit });
        Assert.Equal(ConversionStatus.ConvertedWithBestFit, preview.ExpectedStatus);
        Assert.Equal("Lukasz", preview.PreviewText);
    }

    [Fact]
    public void Preview_AnsiFile_IsNotUtf16()
    {
        var path = _dir.File("ansi.txt");
        File.WriteAllBytes(path, TestText.Ansi1252.GetBytes("café"));
        Assert.Equal(ConversionStatus.SkippedNotUtf16, FilePreview.Create(path, new ConversionSettings()).ExpectedStatus);
    }
}
