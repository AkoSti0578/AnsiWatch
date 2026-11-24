using System.Text;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Detection;
using AnsiWatch.Core.Text;

namespace AnsiWatch.Core.Conversion;

/// <summary>Resultaat van een testconversie (er wordt niets opgeslagen).</summary>
public sealed class PreviewResult
{
    /// <summary>Pad van het bestand.</summary>
    public required string Path { get; init; }

    /// <summary>Bestandsgrootte in bytes.</summary>
    public long FileSize { get; init; }

    /// <summary>Gedetecteerde codering.</summary>
    public DetectionResult Detection { get; init; }

    /// <summary>Wat de service met dit bestand zou doen.</summary>
    public ConversionStatus ExpectedStatus { get; init; }

    /// <summary>Niet-representeerbare tekens.</summary>
    public IReadOnlyList<UnmappableCharacter> Issues { get; init; } = Array.Empty<UnmappableCharacter>();

    /// <summary>Er waren meer problemen dan gerapporteerd.</summary>
    public bool IssuesTruncated { get; init; }

    /// <summary>Verwachte grootte na conversie.</summary>
    public long? ConvertedSize { get; init; }

    /// <summary>Aantal tekens en regels.</summary>
    public long CharCount { get; init; }

    /// <summary>Aantal regels.</summary>
    public long LineCount { get; init; }

    /// <summary>Aantal CRLF- en losse LF-regeleinden in de bron.</summary>
    public (long Crlf, long Lf) LineEndings { get; init; }

    /// <summary>De ANSI-output teruggedecodeerd, zodat de gebruiker ziet wat er in het bestand komt te staan.</summary>
    public string PreviewText { get; init; } = string.Empty;

    /// <summary>De preview is ingekort.</summary>
    public bool PreviewTruncated { get; init; }

    /// <summary>Eerste bytes van de output in hex.</summary>
    public string PreviewHex { get; init; } = string.Empty;

    /// <summary>Foutmelding, indien de preview niet gelukt is.</summary>
    public string? Error { get; init; }
}

/// <summary>Testconversie voor de configuratietool: leest het bestand zonder het te wijzigen of te locken.</summary>
public static class FilePreview
{
    /// <summary>Maakt een preview. Het bestand wordt alleen gelezen (FileShare.ReadWrite).</summary>
    public static PreviewResult Create(string path, ConversionSettings settings, int maxPreviewChars = 100_000, int maxIssues = 1000)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CodePageSupport.EnsureRegistered();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var detection = FileConverter.DetectFromStream(stream, settings.DetectWithoutBom);
            if (detection.Kind == TextEncodingKind.Empty)
            {
                return new PreviewResult { Path = path, Detection = detection, ExpectedStatus = ConversionStatus.SkippedEmpty };
            }

            if (!detection.IsUtf16Le)
            {
                return new PreviewResult
                {
                    Path = path, FileSize = stream.Length, Detection = detection, ExpectedStatus = ConversionStatus.SkippedNotUtf16,
                    PreviewText = "(Dit bestand is geen UTF-16LE en wordt door AnsiWatch niet aangeraakt.)",
                };
            }

            stream.Position = detection.PayloadOffset;
            var analysis = AnsiTranscoder.Analyze(stream, settings.CodePage, maxIssues);

            // Output in het geheugen opbouwen. Bij Skip tonen we ter illustratie de output met '?'.
            var action = settings.UnmappableAction == UnmappableCharacterAction.Skip ? UnmappableCharacterAction.Replace : settings.UnmappableAction;
            stream.Position = detection.PayloadOffset;
            using var counting = new PreviewSink(maxPreviewChars * 2L);
            AnsiTranscoder.Transcode(stream, counting, settings.CodePage, action, maxIssues: 0);

            var ansi = counting.Captured;
            var text = CodePageSupport.GetDefaultEncoding(settings.CodePage).GetString(ansi);
            var truncated = counting.TotalBytes > ansi.Length || text.Length > maxPreviewChars;
            if (text.Length > maxPreviewChars)
            {
                text = text[..maxPreviewChars];
            }

            var expected = !analysis.HasUnmappable
                ? ConversionStatus.Converted
                : settings.UnmappableAction switch
                {
                    UnmappableCharacterAction.Replace => ConversionStatus.ConvertedWithReplacements,
                    UnmappableCharacterAction.BestFit => ConversionStatus.ConvertedWithBestFit,
                    _ => settings.QuarantineFolder is null ? ConversionStatus.SkippedUnmappable : ConversionStatus.Quarantined,
                };

            return new PreviewResult
            {
                Path = path,
                FileSize = stream.Length,
                Detection = detection,
                ExpectedStatus = expected,
                Issues = analysis.Issues,
                IssuesTruncated = analysis.IssuesTruncated,
                ConvertedSize = expected.IsConverted() ? counting.TotalBytes : null,
                CharCount = analysis.CharCount,
                LineCount = analysis.LineCount,
                LineEndings = CountLineEndings(text),
                PreviewText = text,
                PreviewTruncated = truncated,
                PreviewHex = ToHex(ansi.AsSpan(0, Math.Min(64, ansi.Length))),
            };
        }
        catch (InvalidUtf16Exception ex)
        {
            return new PreviewResult { Path = path, ExpectedStatus = ConversionStatus.InvalidUtf16, Error = ex.Message };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PreviewResult { Path = path, ExpectedStatus = ConversionStatus.Failed, Error = ex.Message };
        }
    }

    private static (long Crlf, long Lf) CountLineEndings(string text)
    {
        long crlf = 0, lf = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (i > 0 && text[i - 1] == '\r')
                {
                    crlf++;
                }
                else
                {
                    lf++;
                }
            }
        }

        return (crlf, lf);
    }

    private static string ToHex(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Stream die maximaal N bytes bewaart en de rest alleen telt.</summary>
    private sealed class PreviewSink : Stream
    {
        private readonly MemoryStream _buffer = new();
        private readonly long _limit;

        public PreviewSink(long limit) => _limit = limit;

        public long TotalBytes { get; private set; }

        public byte[] Captured => _buffer.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => TotalBytes;
        public override long Position { get => TotalBytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            var room = (int)Math.Max(0, Math.Min(count, _limit - _buffer.Length));
            if (room > 0)
            {
                _buffer.Write(buffer, offset, room);
            }

            TotalBytes += count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
