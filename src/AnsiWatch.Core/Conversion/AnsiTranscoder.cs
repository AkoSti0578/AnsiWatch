using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Text;

namespace AnsiWatch.Core.Conversion;

/// <summary>
/// Streaming UTF-16LE → ANSI-transcoder. Werkt in blokken, zodat ook bestanden van honderden MB
/// met een constant, klein geheugengebruik geconverteerd worden. De tekst wordt niet aangeraakt:
/// geen trimming, geen normalisatie van regeleinden, geen BOM in de output.
/// </summary>
public static class AnsiTranscoder
{
    private const int ByteBufferSize = 64 * 1024;
    private const int CharBufferSize = ByteBufferSize / 2;

    /// <summary>Standaard maximum aantal niet-representeerbare tekens dat met positie gerapporteerd wordt.</summary>
    public const int DefaultMaxIssues = 100;

    /// <summary>
    /// Analyseert de UTF-16LE-payload zonder iets te schrijven: telt tekens/regels en zoekt met
    /// <see cref="EncoderFallback.ExceptionFallback"/> alle tekens die niet in de codepage passen.
    /// </summary>
    /// <param name="utf16Payload">Stream gepositioneerd direct ná een eventuele BOM.</param>
    public static TranscodeReport Analyze(Stream utf16Payload, int codePage, int maxIssues = DefaultMaxIssues, CancellationToken cancellationToken = default)
        => Run(utf16Payload, output: null, codePage, UnmappableCharacterAction.Skip, maxIssues, computeHash: false, cancellationToken);

    /// <summary>
    /// Transcodeert de UTF-16LE-payload naar de doel-codepage.
    /// </summary>
    /// <param name="utf16Payload">Stream gepositioneerd direct ná een eventuele BOM.</param>
    /// <param name="output">Doelstream (de ANSI-bytes, zonder BOM).</param>
    /// <param name="codePage">Doel-codepage, bijvoorbeeld 1252.</param>
    /// <param name="action">
    /// Bij <see cref="UnmappableCharacterAction.Skip"/> wordt met <see cref="EncoderFallback.ExceptionFallback"/>
    /// geschreven: een niet-representeerbaar teken leidt dan tot een <see cref="EncoderFallbackException"/>.
    /// Roep in die modus eerst <see cref="Analyze"/> aan.
    /// </param>
    /// <param name="maxIssues">Maximaal aantal te rapporteren posities.</param>
    /// <param name="computeHash">SHA-256 over de brontekst berekenen (voor verificatie na het schrijven).</param>
    /// <param name="cancellationToken">Annulering.</param>
    /// <exception cref="InvalidUtf16Exception">De payload is geen geldige UTF-16LE.</exception>
    public static TranscodeReport Transcode(
        Stream utf16Payload,
        Stream output,
        int codePage,
        UnmappableCharacterAction action,
        int maxIssues = DefaultMaxIssues,
        bool computeHash = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        return Run(utf16Payload, output, codePage, action, maxIssues, computeHash, cancellationToken);
    }

    /// <summary>Converteert een string in het geheugen (handig voor tests en previews).</summary>
    public static byte[] TranscodeString(string text, int codePage, UnmappableCharacterAction action, out TranscodeReport report)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var input = new MemoryStream(Encoding.Unicode.GetBytes(text));
        using var output = new MemoryStream();
        report = Transcode(input, output, codePage, action);
        return output.ToArray();
    }

    /// <summary>
    /// Berekent SHA-256 over de tekst die ontstaat door <paramref name="ansi"/> terug te decoderen met de codepage.
    /// Gelijk aan <see cref="TranscodeReport.SourceTextHash"/> betekent: de conversie is verliesvrij.
    /// </summary>
    public static byte[] HashDecodedText(Stream ansi, int codePage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ansi);
        var decoder = CodePageSupport.GetDefaultEncoding(codePage).GetDecoder();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = ArrayPool<byte>.Shared.Rent(ByteBufferSize);
        var chars = ArrayPool<char>.Shared.Rent(ByteBufferSize * 2);
        try
        {
            int read;
            while ((read = ansi.Read(bytes, 0, ByteBufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                hash.AppendData(MemoryMarshal.AsBytes(chars.AsSpan(0, n)));
            }

            var last = decoder.GetChars(Array.Empty<byte>(), 0, 0, chars, 0, flush: true);
            hash.AppendData(MemoryMarshal.AsBytes(chars.AsSpan(0, last)));
            return hash.GetHashAndReset();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
            ArrayPool<char>.Shared.Return(chars);
        }
    }

    private static TranscodeReport Run(
        Stream input,
        Stream? output,
        int codePage,
        UnmappableCharacterAction action,
        int maxIssues,
        bool computeHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var report = new TranscodeReport();

        // Strikte UTF-16LE-decoder: ongeldige surrogaten of een oneven aantal bytes geven een exceptie.
        var decoder = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true).GetDecoder();

        // Scanner: stateless encoding met ExceptionFallback om niet-representeerbare tekens te vinden.
        var scanEncoding = CodePageSupport.GetEncoding(codePage, EncoderFallback.ExceptionFallback);

        Encoder? encoder = output is null ? null : CodePageSupport.GetEncoding(codePage, action switch
        {
            UnmappableCharacterAction.Replace => new EncoderReplacementFallback("?"),
            UnmappableCharacterAction.BestFit => CodePageSupport.GetDefaultEncoding(codePage).EncoderFallback,
            _ => EncoderFallback.ExceptionFallback,
        }).GetEncoder();

        using var hash = computeHash ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;

        var byteBuffer = ArrayPool<byte>.Shared.Rent(ByteBufferSize);
        // +1 voor een eventueel doorgeschoven high surrogate uit het vorige blok.
        var charBuffer = ArrayPool<char>.Shared.Rent(CharBufferSize + 2);
        var outBuffer = ArrayPool<byte>.Shared.Rent(Math.Max(ByteBufferSize, scanEncoding.GetMaxByteCount(CharBufferSize + 2)));

        var position = new TextPosition();
        var scanning = true;
        var carry = '\0';
        var hasCarry = false;

        try
        {
            var completed = false;
            while (!completed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(byteBuffer, 0, ByteBufferSize);
                var flush = read == 0;

                var offset = 0;
                if (hasCarry)
                {
                    charBuffer[0] = carry;
                    offset = 1;
                    hasCarry = false;
                }

                int charCount;
                try
                {
                    charCount = offset + decoder.GetChars(byteBuffer, 0, read, charBuffer, offset, flush);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new InvalidUtf16Exception(
                        $"Ongeldige UTF-16LE-data rond teken {report.CharCount + offset}: {ex.Message}", ex);
                }

                // Een high surrogate aan het eind van een blok bewaren we voor het volgende blok,
                // zodat de scanner een surrogaatpaar (bijv. een emoji) nooit doormidden ziet.
                if (!flush && charCount > 0 && char.IsHighSurrogate(charBuffer[charCount - 1]))
                {
                    carry = charBuffer[charCount - 1];
                    hasCarry = true;
                    charCount--;
                }

                var chars = new ReadOnlySpan<char>(charBuffer, 0, charCount);

                if (scanning && chars.Length > 0)
                {
                    scanning = Scan(chars, scanEncoding, position, report, maxIssues);
                }

                hash?.AppendData(MemoryMarshal.AsBytes(chars));
                position.Advance(chars);
                report.CharCount += chars.Length;

                if (encoder is not null && output is not null)
                {
                    EncodeChunk(encoder, chars, flush, outBuffer, output, report);
                }

                completed = flush;
            }

            report.LineCount = report.CharCount == 0 ? 0 : position.Line;
            report.SourceTextHash = hash?.GetHashAndReset();
            return report;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(byteBuffer);
            ArrayPool<char>.Shared.Return(charBuffer);
            ArrayPool<byte>.Shared.Return(outBuffer);
        }
    }

    private static void EncodeChunk(Encoder encoder, ReadOnlySpan<char> chars, bool flush, byte[] outBuffer, Stream output, TranscodeReport report)
    {
        var completed = false;
        while (!completed)
        {
            encoder.Convert(chars, outBuffer, flush, out var charsUsed, out var bytesUsed, out completed);
            if (bytesUsed > 0)
            {
                output.Write(outBuffer, 0, bytesUsed);
                report.BytesWritten += bytesUsed;
            }

            chars = chars[charsUsed..];
            if (chars.IsEmpty && !flush)
            {
                break;
            }
        }
    }

    /// <summary>Zoekt niet-representeerbare tekens. Geeft false terug zodra het maximum bereikt is.</summary>
    private static bool Scan(ReadOnlySpan<char> chars, Encoding scanEncoding, TextPosition position, TranscodeReport report, int maxIssues)
    {
        var start = 0;
        while (start < chars.Length)
        {
            try
            {
                scanEncoding.GetByteCount(chars[start..]);
                return true;
            }
            catch (EncoderFallbackException ex)
            {
                var index = start + ex.Index;
                if (report.Issues.Count >= maxIssues)
                {
                    report.IssuesTruncated = true;
                    return false;
                }

                int codePoint;
                int width;
                if (ex.IsUnknownSurrogate())
                {
                    codePoint = char.ConvertToUtf32(ex.CharUnknownHigh, ex.CharUnknownLow);
                    width = 2;
                }
                else
                {
                    codePoint = ex.CharUnknown;
                    width = 1;
                }

                var (line, column) = position.Locate(chars, index);
                report.Issues.Add(new UnmappableCharacter(line, column, codePoint));
                start = index + width;
            }
        }

        return true;
    }

    /// <summary>Houdt regel/kolom bij over blokgrenzen heen. Regels worden geteld op LF (dus CRLF en LF).</summary>
    private sealed class TextPosition
    {
        /// <summary>Regelnummer (1-based) aan het begin van het huidige blok.</summary>
        public long Line { get; private set; } = 1;

        /// <summary>Aantal tekens sinds de laatste LF, aan het begin van het huidige blok.</summary>
        private long _columnOffset;

        public (long Line, long Column) Locate(ReadOnlySpan<char> chunk, int index)
        {
            var before = chunk[..index];
            var line = Line + before.Count('\n');
            var lastNewLine = before.LastIndexOf('\n');
            var column = lastNewLine >= 0 ? index - lastNewLine : _columnOffset + index + 1;
            return (line, column);
        }

        public void Advance(ReadOnlySpan<char> chunk)
        {
            Line += chunk.Count('\n');
            var lastNewLine = chunk.LastIndexOf('\n');
            _columnOffset = lastNewLine >= 0 ? chunk.Length - lastNewLine - 1 : _columnOffset + chunk.Length;
        }
    }
}
