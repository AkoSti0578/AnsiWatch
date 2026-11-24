using System.Text;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;
using AnsiWatch.Core.Text;

namespace AnsiWatch.Tests;

public class TranscoderTests
{
    static TranscoderTests() => CodePageSupport.EnsureRegistered();

    [Fact]
    public void DutchText_IsByteForByteEqualToEncodingGetBytes()
    {
        var output = AnsiTranscoder.TranscodeString(TestText.Dutch, 1252, UnmappableCharacterAction.Skip, out var report);
        Assert.Equal(Encoding.GetEncoding(1252).GetBytes(TestText.Dutch), output);
        Assert.False(report.HasUnmappable);
        Assert.Equal(TestText.Dutch.Length, report.CharCount);
    }

    [Theory]
    [InlineData("é", 0xE9)]
    [InlineData("ë", 0xEB)]
    [InlineData("ï", 0xEF)]
    [InlineData("ç", 0xE7)]
    [InlineData("€", 0x80)]
    public void SpecificCharacters_MapToExpected1252Bytes(string ch, int expected)
    {
        var output = AnsiTranscoder.TranscodeString(ch, 1252, UnmappableCharacterAction.Skip, out _);
        Assert.Equal(new[] { (byte)expected }, output);
    }

    [Fact]
    public void LineEndings_ArePreservedExactly()
    {
        const string text = "a\r\nb\nc\rd\r\n\r\n\n";
        var output = AnsiTranscoder.TranscodeString(text, 1252, UnmappableCharacterAction.Skip, out var report);
        Assert.Equal(Encoding.ASCII.GetBytes(text), output);
        Assert.Equal(6, report.LineCount);
    }

    [Fact]
    public void NoBomIsWritten()
    {
        var output = AnsiTranscoder.TranscodeString("x", 1252, UnmappableCharacterAction.Skip, out _);
        Assert.Equal(new byte[] { 0x78 }, output);
    }

    [Fact]
    public void Unmappable_IsReportedWithLineAndColumn()
    {
        const string text = "Regel één\r\nWał\nok 😀 klaar";
        using var input = new MemoryStream(Encoding.Unicode.GetBytes(text));
        var report = AnsiTranscoder.Analyze(input, 1252);

        Assert.Equal(2, report.Issues.Count);
        Assert.Equal(new UnmappableCharacter(2, 3, 'ł'), report.Issues[0]);
        Assert.Equal(new UnmappableCharacter(3, 4, 0x1F600), report.Issues[1]);
        Assert.Equal("😀", report.Issues[1].Character);
        Assert.Contains("regel 2, positie 3", report.Issues[0].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Replace_UsesQuestionMark_LikeEncodingWithReplacementFallback()
    {
        const string text = "Łódź 😀 €";
        var output = AnsiTranscoder.TranscodeString(text, 1252, UnmappableCharacterAction.Replace, out var report);
        var expected = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback).GetBytes(text);
        Assert.Equal(expected, output);
        Assert.Equal(3, report.Issues.Count); // Ł, ź en 😀; ó bestaat wel in 1252
        Assert.Equal(0x80, output[^1]);
    }

    [Fact]
    public void BestFit_MapsŁToL()
    {
        var output = AnsiTranscoder.TranscodeString("Łódź", 1252, UnmappableCharacterAction.BestFit, out var report);
        Assert.Equal(Encoding.GetEncoding(1252).GetBytes("Łódź"), output);
        Assert.Equal((byte)'L', output[0]);
        Assert.Equal(0xF3, output[1]); // ó bestaat wel in 1252
        Assert.True(report.HasUnmappable);
    }

    [Fact]
    public void Skip_WriteModeThrowsOnUnmappable_AsSafetyNet()
    {
        Assert.Throws<EncoderFallbackException>(() => AnsiTranscoder.TranscodeString("Ł", 1252, UnmappableCharacterAction.Skip, out _));
    }

    [Fact]
    public void InvalidUtf16_LoneSurrogate_Throws()
    {
        var bytes = new byte[] { 0x41, 0x00, 0x00, 0xD8, 0x42, 0x00 }; // A, lone high surrogate, B
        using var input = new MemoryStream(bytes);
        Assert.Throws<InvalidUtf16Exception>(() => AnsiTranscoder.Analyze(input, 1252));
    }

    [Fact]
    public void InvalidUtf16_OddLength_Throws()
    {
        using var input = new MemoryStream(new byte[] { 0x41, 0x00, 0x42 });
        Assert.Throws<InvalidUtf16Exception>(() => AnsiTranscoder.Analyze(input, 1252));
    }

    [Fact]
    public void SurrogatePairAcrossChunkBoundary_IsHandled()
    {
        // 32768 chars per blok: plaats een emoji precies op de blokgrens.
        var text = new string('a', 32767) + "😀" + new string('b', 10);
        using var input = new MemoryStream(Encoding.Unicode.GetBytes(text));
        var report = AnsiTranscoder.Analyze(input, 1252);
        Assert.Single(report.Issues);
        Assert.Equal(0x1F600, report.Issues[0].CodePoint);
        Assert.Equal(32768, report.Issues[0].Column);

        var output = AnsiTranscoder.TranscodeString(text, 1252, UnmappableCharacterAction.Replace, out _);
        Assert.Equal(Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback).GetBytes(text), output);
    }

    [Fact]
    public void IssueReporting_IsCapped()
    {
        var text = string.Concat(Enumerable.Repeat("Ł", 500));
        using var input = new MemoryStream(Encoding.Unicode.GetBytes(text));
        var report = AnsiTranscoder.Analyze(input, 1252, maxIssues: 10);
        Assert.Equal(10, report.Issues.Count);
        Assert.True(report.IssuesTruncated);
    }

    [Fact]
    public void RandomRepresentableText_RoundTripsByteForByte()
    {
        var enc = TestText.Ansi1252;
        // Alle tekens die in 1252 bestaan (inclusief control chars, behalve ongedefinieerde bytes).
        var alphabet = Enumerable.Range(0, 256)
            .Select(b => enc.GetString(new[] { (byte)b }))
            .Distinct()
            .ToArray();
        var rnd = new Random(1252);
        for (var round = 0; round < 25; round++)
        {
            var sb = new StringBuilder();
            var len = rnd.Next(0, 100_000);
            for (var i = 0; i < len; i++)
            {
                sb.Append(alphabet[rnd.Next(alphabet.Length)]);
            }

            var text = sb.ToString();
            var output = AnsiTranscoder.TranscodeString(text, 1252, UnmappableCharacterAction.Skip, out var report);
            Assert.Equal(enc.GetBytes(text), output);
            Assert.False(report.HasUnmappable);
        }
    }

    [Fact]
    public void HashOfDecodedOutput_EqualsSourceHash()
    {
        using var input = new MemoryStream(Encoding.Unicode.GetBytes(TestText.Dutch));
        using var output = new MemoryStream();
        var report = AnsiTranscoder.Transcode(input, output, 1252, UnmappableCharacterAction.Skip, computeHash: true);
        output.Position = 0;
        Assert.Equal(report.SourceTextHash, AnsiTranscoder.HashDecodedText(output, 1252));
    }

    [Theory]
    [InlineData(1250, "Łódź zażółć gęślą jaźń")]
    [InlineData(1251, "Привет, мир")]
    [InlineData(1253, "Καλημέρα")]
    public void OtherCodePages_AreConfigurable(int codePage, string text)
    {
        var output = AnsiTranscoder.TranscodeString(text, codePage, UnmappableCharacterAction.Skip, out var report);
        Assert.False(report.HasUnmappable);
        Assert.Equal(Encoding.GetEncoding(codePage).GetBytes(text), output);
    }
}
