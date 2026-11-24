using AnsiWatch.Core.Diagnostics;

namespace AnsiWatch.Service.Maintenance;

/// <summary>Ruimt back-ups op die ouder zijn dan de bewaartermijn.</summary>
public static class BackupRetention
{
    /// <summary>
    /// Verwijdert bestanden in <paramref name="backupFolder"/> waarvan de back-updatum (map yyyy-MM-dd of
    /// het tijdstip van aanmaken) ouder is dan <paramref name="retentionDays"/>; daarna lege mappen.
    /// </summary>
    /// <returns>Aantal verwijderde bestanden.</returns>
    public static int Cleanup(string backupFolder, int retentionDays, ILogger logger, DateTime? nowUtc = null)
    {
        if (retentionDays <= 0 || !Directory.Exists(backupFolder))
        {
            return 0;
        }

        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-retentionDays);
        var removed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(backupFolder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    // Back-ups krijgen de tijdstempel van het origineel; daarom telt de aanmaaktijd van de back-up zelf
                    // niet. We gebruiken de datummap (yyyy-MM-dd) en vallen terug op de laatste toegang/aanmaak.
                    var stamp = BackupDate(file) ?? File.GetLastAccessTimeUtc(file);
                    if (stamp < cutoff)
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(ex, "Back-up {File} kon niet verwijderd worden.", file);
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(backupFolder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        Directory.Delete(dir);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // in gebruik
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(AnsiWatchEventIds.BackupCleanupFailed, ex, "Opruimen van back-ups in {Folder} mislukt: {Error}", backupFolder, ex.Message);
            return removed;
        }

        if (removed > 0)
        {
            logger.LogInformation(AnsiWatchEventIds.BackupCleanup, "{Count} back-up(s) ouder dan {Days} dagen verwijderd uit {Folder}.", removed, retentionDays, backupFolder);
        }

        return removed;
    }

    private static DateTime? BackupDate(string file)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(file));
        return DateTime.TryParseExact(folder, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeLocal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date)
            ? date.AddDays(1) // een back-up van dag X is pas na X+retentie+1 verlopen
            : null;
    }
}
