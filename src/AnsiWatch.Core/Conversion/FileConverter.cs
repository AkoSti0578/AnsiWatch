using System.Diagnostics;
using System.Globalization;
using System.Text;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Detection;
using AnsiWatch.Core.Text;

namespace AnsiWatch.Core.Conversion;

/// <summary>
/// Converteert één bestand in place van UTF-16LE naar ANSI. Garanties:
/// <list type="bullet">
/// <item>Alleen echte UTF-16LE wordt aangeraakt; al het andere blijft byte-voor-byte gelijk.</item>
/// <item>Er wordt pas geconverteerd als het bestand exclusief (FileShare.None) te openen is én de grootte stabiel is.</item>
/// <item>Schrijven gebeurt naar een tijdelijk bestand in dezelfde map, daarna <see cref="File.Replace(string,string,string?,bool)"/>.
/// Gaat er iets mis, dan blijft het origineel intact en wordt het tijdelijke bestand opgeruimd.</item>
/// </list>
/// </summary>
public sealed class FileConverter
{
    private const int Win32SharingViolation = unchecked((int)0x80070020);
    private const int Win32LockViolation = unchecked((int)0x80070021);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(2);

    private readonly ConversionSettings _settings;

    /// <summary>Maakt een converter met de gegeven instellingen.</summary>
    public FileConverter(ConversionSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        CodePageSupport.EnsureRegistered();
    }

    /// <summary>De gebruikte instellingen.</summary>
    public ConversionSettings Settings => _settings;

    /// <summary>Testhaak: wordt aangeroepen vlak vóór het vervangen van het origineel.</summary>
    internal Action<string, string>? BeforeReplaceHook { get; set; }

    /// <summary>Converteert het bestand. Gooit alleen bij annulering; alle andere fouten staan in het resultaat.</summary>
    public async Task<ConversionResult> ConvertAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var stopwatch = Stopwatch.StartNew();
        var result = new ConversionResult { Path = path };
        string? tempPath = null;
        FileStream? source = null;

        try
        {
            // 1. Snelle voorcontrole zonder lock: wat zeker geen UTF-16LE is, hoeven we niet te locken.
            var quick = QuickDetect(path);
            if (quick is { } q)
            {
                result.Detection = q.Detection;
                result.OriginalSize = q.Length;
                if (q.Detection.Kind != TextEncodingKind.Empty && !q.Detection.IsUtf16Le)
                {
                    result.Status = ConversionStatus.SkippedNotUtf16;
                    result.NewSize = q.Length;
                    return result;
                }
            }
            else if (!File.Exists(path))
            {
                result.Status = ConversionStatus.NotFound;
                return result;
            }

            // 2. Wachten tot het schrijvende proces klaar is: exclusief openen + stabiele grootte.
            var waitWatch = Stopwatch.StartNew();
            var acquired = await AcquireExclusiveAsync(path, cancellationToken).ConfigureAwait(false);
            result.WaitDuration = waitWatch.Elapsed;
            if (acquired.Status is { } failure)
            {
                result.Status = failure;
                result.Error = acquired.Message;
                return result;
            }

            source = acquired.Stream!;
            var originalLength = source.Length;
            result.OriginalSize = originalLength;

            // 3. Definitieve detectie op de gelockte handle.
            var detection = DetectFromStream(source, _settings.DetectWithoutBom);
            result.Detection = detection;
            if (detection.Kind == TextEncodingKind.Empty)
            {
                result.Status = ConversionStatus.SkippedEmpty;
                return result;
            }

            if (!detection.IsUtf16Le)
            {
                result.Status = ConversionStatus.SkippedNotUtf16;
                result.NewSize = originalLength;
                return result;
            }

            var creationUtc = File.GetCreationTimeUtc(path);
            var lastWriteUtc = File.GetLastWriteTimeUtc(path);
            var attributes = File.GetAttributes(path);

            // 4. Bij Skip: eerst volledig controleren (ExceptionFallback) vóórdat er iets geschreven wordt.
            if (_settings.UnmappableAction == UnmappableCharacterAction.Skip)
            {
                source.Position = detection.PayloadOffset;
                TranscodeReport analysis;
                try
                {
                    analysis = AnsiTranscoder.Analyze(source, _settings.CodePage, _settings.MaxReportedIssues, cancellationToken);
                }
                catch (InvalidUtf16Exception ex)
                {
                    return InvalidUtf16(result, detection, ex);
                }

                if (analysis.HasUnmappable)
                {
                    result.Issues = analysis.Issues;
                    result.IssuesTruncated = analysis.IssuesTruncated;
                    result.Status = ConversionStatus.SkippedUnmappable;
                    await source.DisposeAsync().ConfigureAwait(false);
                    source = null;
                    TryQuarantine(path, result);
                    return result;
                }
            }

            // 5. Optionele back-up van het origineel (vanuit de gelockte handle).
            if (_settings.BackupFolder is not null)
            {
                result.BackupPath = Backup(source, path, _settings.BackupFolder, creationUtc, lastWriteUtc);
            }

            // 6. Transcoderen naar een tijdelijk bestand in dezelfde map.
            tempPath = CreateTempPath(path);
            TranscodeReport report;
            source.Position = detection.PayloadOffset;
            try
            {
                using var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
                report = AnsiTranscoder.Transcode(source, temp, _settings.CodePage, _settings.UnmappableAction,
                    _settings.MaxReportedIssues, computeHash: _settings.VerifyAfterWrite, cancellationToken);
                temp.Flush(flushToDisk: true);
            }
            catch (InvalidUtf16Exception ex)
            {
                return InvalidUtf16(result, detection, ex);
            }

            result.Issues = report.Issues;
            result.IssuesTruncated = report.IssuesTruncated;
            result.NewSize = report.BytesWritten;

            // 7. Verificatie: ANSI terugdecoderen moet exact de brontekst opleveren.
            if (_settings.VerifyAfterWrite && !report.HasUnmappable && report.SourceTextHash is { } sourceHash)
            {
                using var check = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                var roundTrip = AnsiTranscoder.HashDecodedText(check, _settings.CodePage, cancellationToken);
                if (!roundTrip.AsSpan().SequenceEqual(sourceHash))
                {
                    result.Status = ConversionStatus.Failed;
                    result.Error = "Verificatie mislukt: de ANSI-output decodeert niet exact terug naar de brontekst. Origineel ongewijzigd.";
                    return result;
                }

                result.Verified = true;
            }

            // 8. Lock loslaten en controleren dat niemand het bestand intussen gewijzigd heeft.
            await source.DisposeAsync().ConfigureAwait(false);
            source = null;
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                result.Status = ConversionStatus.NotFound;
                return result;
            }

            if (info.Length != originalLength || info.LastWriteTimeUtc != lastWriteUtc)
            {
                result.Status = ConversionStatus.ChangedDuringConversion;
                result.Error = "Het bestand is tijdens de conversie gewijzigd; het wordt opnieuw aangeboden.";
                return result;
            }

            BeforeReplaceHook?.Invoke(tempPath, path);

            // 9. Atomair vervangen (behoudt ACL's en attributen van het origineel).
            await ReplaceAsync(tempPath, path, attributes, result, cancellationToken).ConfigureAwait(false);
            tempPath = null;

            if (_settings.PreserveTimestamps)
            {
                TrySetTimestamps(path, creationUtc, lastWriteUtc, result);
            }

            result.Status = report.HasUnmappable
                ? (_settings.UnmappableAction == UnmappableCharacterAction.BestFit ? ConversionStatus.ConvertedWithBestFit : ConversionStatus.ConvertedWithReplacements)
                : ConversionStatus.Converted;
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            result.Status = ConversionStatus.NotFound;
            return result;
        }
        catch (DirectoryNotFoundException)
        {
            result.Status = ConversionStatus.NotFound;
            return result;
        }
        catch (Exception ex)
        {
            result.Status = ConversionStatus.Failed;
            result.Error = ex.Message;
            result.Exception = ex;
            return result;
        }
        finally
        {
            if (source is not null)
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }

            if (tempPath is not null)
            {
                TryDelete(tempPath);
            }

            result.Duration = stopwatch.Elapsed;
        }
    }

    /// <summary>Detecteert de codering van een (geopende) stream en zet de positie terug op 0.</summary>
    public static DetectionResult DetectFromStream(Stream stream, bool detectWithoutBom)
    {
        ArgumentNullException.ThrowIfNull(stream);
        stream.Position = 0;
        Span<byte> buffer = stackalloc byte[EncodingDetector.SampleSize];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer[total..])) > 0)
        {
            total += read;
        }

        stream.Position = 0;
        return EncodingDetector.Detect(buffer[..total], stream.Length, detectWithoutBom);
    }

    /// <summary>Detecteert de codering van een bestand zonder het te locken.</summary>
    public static DetectionResult DetectFile(string path, bool detectWithoutBom)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return DetectFromStream(stream, detectWithoutBom);
    }

    private (DetectionResult Detection, long Length)? QuickDetect(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
            return (DetectFromStream(stream, _settings.DetectWithoutBom), stream.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gelockt door de schrijver of (nog) niet leesbaar: de definitieve controle volgt na het wachten.
            return null;
        }
    }

    private async Task<(FileStream? Stream, ConversionStatus? Status, string? Message)> AcquireExclusiveAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        long? previousLength = null;
        DateTime previousWrite = default;
        var backoff = TimeSpan.FromMilliseconds(100);
        string lastProblem = "bestand in gebruik door een ander proces";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan delay;
            try
            {
                var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.SequentialScan);
                var length = stream.Length;
                var lastWrite = File.GetLastWriteTimeUtc(path);
                if (previousLength == length && previousWrite == lastWrite)
                {
                    return (stream, null, null);
                }

                // Eerste meting, of het bestand groeit nog: lock loslaten en na het stabiliteitsinterval opnieuw meten.
                lastProblem = previousLength is null ? lastProblem : "bestand groeit nog";
                previousLength = length;
                previousWrite = lastWrite;
                await stream.DisposeAsync().ConfigureAwait(false);
                delay = _settings.StabilityInterval;
            }
            catch (FileNotFoundException)
            {
                return (null, ConversionStatus.NotFound, null);
            }
            catch (DirectoryNotFoundException)
            {
                return (null, ConversionStatus.NotFound, null);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Read-only bestand of geen schrijfrechten voor het serviceaccount.
                return (null, ConversionStatus.Failed, $"Geen toegang (schrijfrechten serviceaccount of read-only bestand): {ex.Message}");
            }
            catch (IOException ex)
            {
                lastProblem = ex.HResult is Win32SharingViolation or Win32LockViolation
                    ? "bestand in gebruik door een ander proces"
                    : ex.Message;
                previousLength = null;
                delay = backoff;
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
            }

            if (deadline.Elapsed + delay > _settings.LockTimeout)
            {
                return (null, ConversionStatus.LockTimeout,
                    string.Create(CultureInfo.InvariantCulture, $"Na {_settings.LockTimeout.TotalSeconds:F0} s nog niet klaar: {lastProblem}."));
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceAsync(string tempPath, string path, FileAttributes attributes, ConversionResult result, CancellationToken cancellationToken)
    {
        var readOnly = attributes.HasFlag(FileAttributes.ReadOnly);
        if (readOnly)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        try
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
                    return;
                }
                catch (IOException ex) when (attempt < 5 && ex.HResult is Win32SharingViolation or Win32LockViolation)
                {
                    await Task.Delay(100 * attempt, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is PlatformNotSupportedException || (ex is IOException io && IsReplaceUnsupported(io)))
                {
                    // Sommige NAS/SMB-implementaties ondersteunen ReplaceFile niet: val terug op Move met overschrijven.
                    File.Move(tempPath, path, overwrite: true);
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                    result.Notes.Add($"File.Replace niet ondersteund op dit volume ({ex.Message}); teruggevallen op Move. ACL's zijn overgeërfd van de map.");
                    return;
                }
            }
        }
        finally
        {
            if (readOnly && File.Exists(path))
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            }
        }
    }

    private static bool IsReplaceUnsupported(IOException ex) =>
        (ex.HResult & 0xFFFF) is 50 /* ERROR_NOT_SUPPORTED */ or 1 /* ERROR_INVALID_FUNCTION */ or 1176 /* UNABLE_TO_MOVE_REPLACEMENT */ or 1177 /* UNABLE_TO_MOVE_REPLACEMENT_2 */;

    private static void TrySetTimestamps(string path, DateTime creationUtc, DateTime lastWriteUtc, ConversionResult result)
    {
        try
        {
            var readOnly = File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);
            if (readOnly)
            {
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            }

            File.SetCreationTimeUtc(path, creationUtc);
            File.SetLastWriteTimeUtc(path, lastWriteUtc);

            if (readOnly)
            {
                File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Notes.Add($"Tijdstempels konden niet hersteld worden: {ex.Message}");
        }
    }

    private static string Backup(FileStream source, string path, string backupRoot, DateTime creationUtc, DateTime lastWriteUtc)
    {
        var folder = Path.Combine(backupRoot, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var target = UniquePath(folder, Path.GetFileName(path));
        source.Position = 0;
        using (var backup = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(backup);
        }

        source.Position = 0;
        File.SetCreationTimeUtc(target, creationUtc);
        File.SetLastWriteTimeUtc(target, lastWriteUtc);
        return target;
    }

    private void TryQuarantine(string path, ConversionResult result)
    {
        if (_settings.QuarantineFolder is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_settings.QuarantineFolder);
            var target = UniquePath(_settings.QuarantineFolder, Path.GetFileName(path));
            File.Move(path, target);
            result.QuarantinePath = target;
            result.Status = ConversionStatus.Quarantined;

            var report = new StringBuilder()
                .AppendLine(CultureInfo.InvariantCulture, $"AnsiWatch quarantainerapport – {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine(CultureInfo.InvariantCulture, $"Origineel pad : {path}")
                .AppendLine(CultureInfo.InvariantCulture, $"Codepage      : {_settings.CodePage}")
                .AppendLine("Reden         : tekens die niet in de doel-codepage bestaan")
                .AppendLine();
            foreach (var issue in result.Issues)
            {
                report.AppendLine(issue.ToString());
            }

            if (result.IssuesTruncated)
            {
                report.AppendLine("… en meer.");
            }

            File.WriteAllText(target + ".ansiwatch-rapport.log", report.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Notes.Add($"Verplaatsen naar quarantaine mislukt: {ex.Message}");
        }
    }

    private static ConversionResult InvalidUtf16(ConversionResult result, DetectionResult detection, InvalidUtf16Exception ex)
    {
        // Zonder BOM was het slechts een vermoeden: dan is het gewoon "geen UTF-16LE".
        result.Status = detection.Kind == TextEncodingKind.Utf16LeNoBom ? ConversionStatus.SkippedNotUtf16 : ConversionStatus.InvalidUtf16;
        result.Error = ex.Message;
        return result;
    }

    internal static string CreateTempPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? ".";
        return Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid().ToString("N")[..8]}{AppPaths.TempFileExtension}");
    }

    private static string UniquePath(string folder, string fileName)
    {
        var candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        var name = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var stamp = DateTime.Now.ToString("HHmmss-fff", CultureInfo.InvariantCulture);
        for (var i = 1; ; i++)
        {
            candidate = Path.Combine(folder, i == 1 ? $"{name}_{stamp}{extension}" : $"{name}_{stamp}_{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Opruimen is best effort; de service ruimt verweesde .ansiwatch.tmp-bestanden ook op bij een scan.
        }
    }
}
