using System.Text;
using AnsiWatch.Core.Detection;

namespace AnsiWatch.Tests;

public class EncodingDetectorTests
{
    private static DetectionResult Detect(byte[] bytes, bool noBom = false) => EncodingDetector.Detect(bytes, bytes.Length, noBom);

    [Fact]
    public void Utf16LeWithBom_IsDetected()
    {
        var r = Detect(TestText.Utf16("Hallo"));
        Assert.Equal(TextEncodingKind.Utf16LeBom, r.Kind);
        Assert.Equal(2, r.PayloadOffset);
        Assert.True(r.IsUtf16Le);
    }

    [Fact]
    public void BomOnly_IsUtf16Le() => Assert.Equal(TextEncodingKind.Utf16LeBom, Detect([0xFF, 0xFE]).Kind);

    [Fact]
    public void Empty_IsEmpty() => Assert.Equal(TextEncodingKind.Empty, Detect([]).Kind);

    [Fact]
    public void Utf32LeBom_IsNotMistakenForUtf16()
    {
        var r = Detect(new UTF32Encoding(false, true).GetPreamble().Concat(new UTF32Encoding(false, false).GetBytes("abc")).ToArray());
        Assert.Equal(TextEncodingKind.Utf32LeBom, r.Kind);
        Assert.False(r.IsUtf16Le);
    }

    [Theory]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }, TextEncodingKind.Utf16BeBom)]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x41 }, TextEncodingKind.Utf8Bom)]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF }, TextEncodingKind.Utf32BeBom)]
    public void OtherBoms_AreNotUtf16Le(byte[] bytes, TextEncodingKind expected)
    {
        var r = Detect(bytes, noBom: true);
        Assert.Equal(expected, r.Kind);
        Assert.False(r.IsUtf16Le);
    }

    [Fact]
    public void Ansi_IsOther_EvenWithHeuristic()
    {
        var ansi = TestText.Ansi1252.GetBytes(TestText.Dutch);
        Assert.Equal(TextEncodingKind.Other, Detect(ansi, noBom: true).Kind);
    }

    [Fact]
    public void Utf8WithoutBom_IsOther_EvenWithHeuristic()
    {
        var utf8 = Encoding.UTF8.GetBytes(TestText.Dutch);
        Assert.Equal(TextEncodingKind.Other, Detect(utf8, noBom: true).Kind);
    }

    [Fact]
    public void Utf16LeWithoutBom_OnlyWhenHeuristicEnabled()
    {
        var bytes = TestText.Utf16(TestText.Dutch, bom: false);
        Assert.Equal(TextEncodingKind.Other, Detect(bytes, noBom: false).Kind);
        Assert.Equal(TextEncodingKind.Utf16LeNoBom, Detect(bytes, noBom: true).Kind);
        Assert.Equal(0, Detect(bytes, noBom: true).PayloadOffset);
    }

    [Fact]
    public void Utf16WithoutBom_OddLength_IsRejected()
    {
        var bytes = TestText.Utf16("Hallo wereld", bom: false).Append((byte)0x41).ToArray();
        Assert.Equal(TextEncodingKind.Other, Detect(bytes, noBom: true).Kind);
    }

    [Fact]
    public void Utf32LeWithoutBom_IsRejectedByHeuristic()
    {
        var bytes = new UTF32Encoding(false, false).GetBytes("Hallo wereld, dit is UTF-32");
        Assert.Equal(TextEncodingKind.Other, Detect(bytes, noBom: true).Kind);
    }

    [Fact]
    public void BinaryNoise_IsOther()
    {
        var rnd = new Random(42);
        var bytes = new byte[4096];
        rnd.NextBytes(bytes);
        bytes[0] = 0x12;
        Assert.Equal(TextEncodingKind.Other, Detect(bytes, noBom: true).Kind);
    }
}
