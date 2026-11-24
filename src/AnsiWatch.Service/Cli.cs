using System.Globalization;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;
using AnsiWatch.Core.Status;
using AnsiWatch.Service.Status;

namespace AnsiWatch.Service;

/// <summary>
/// Commandoregel-hulpmiddelen naast de service:
/// <code>
/// AnsiWatch.Service.exe init-config --watch-path "D:\Export;\\srv\share" [--include-subfolders] [--force]
/// AnsiWatch.Service.exe convert &lt;bestand&gt; [--dry-run] [--codepage 1252] [--action Skip|Replace|BestFit]
/// AnsiWatch.Service.exe status
/// AnsiWatch.Service.exe version
/// </code>
/// </summary>
internal static class Cli
{
    private static readonly string[] Commands = ["init-config", "convert", "status", "version", "help", "--help", "-h", "/?"];

    public static bool IsCommand(string arg) => Commands.Contains(arg, StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "init-config" => InitConfig(args),
                "convert" => await ConvertAsync(args).ConfigureAwait(false),
                "status" => ShowStatus(),
                "version" => Print($"AnsiWatch {StatusTracker.Version}"),
                _ => Help(),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or ConfigException or FormatException)
        {
            await Console.Error.WriteLineAsync($"Fout: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
    }

    private static int InitConfig(string[] args)
    {
        var configPath = Option(args, "--config") ?? AppPaths.ConfigFile;
        var watchPath = Option(args, "--watch-path");
        var created = ConfigInitializer.CreateInitialConfig(configPath, watchPath, BoolOption(args, "--include-subfolders"), Flag(args, "--force"));
        Console.WriteLine(created
            ? $"Initiële configuratie aangemaakt: {configPath}"
            : $"{configPath} bestaat al en is ongewijzigd gelaten (gebruik --force om te overschrijven).");
        return 0; // ook "bestond al" is een succes: een upgrade mag nooit falen op een bestaande config
    }

    private static async Task<int> ConvertAsync(string[] args)
    {
        var file = args.Skip(1).FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
            ?? throw new ArgumentException("Geef een bestand op: convert <bestand> [--dry-run]");
        var options = ConfigStore.Load(Option(args, "--config") ?? AppPaths.ConfigFile);
        if (Option(args, "--codepage") is { } cp)
        {
            options.CodePage = int.Parse(cp, CultureInfo.InvariantCulture);
        }

        if (Option(args, "--action") is { } action)
        {
            options.UnmappableCharacterAction = Enum.Parse<UnmappableCharacterAction>(action, ignoreCase: true);
        }

        var settings = options.ToConversionSettings();
        if (Flag(args, "--dry-run"))
        {
            var preview = FilePreview.Create(Path.GetFullPath(file), settings);
            Console.WriteLine($"Bestand     : {preview.Path}");
            Console.WriteLine($"Codering    : {preview.Detection.Description}");
            Console.WriteLine($"Verwachting : {preview.ExpectedStatus.ToDutch()}");
            if (preview.Error is not null)
            {
                Console.WriteLine($"Fout        : {preview.Error}");
            }

            foreach (var issue in preview.Issues.Take(50))
            {
                Console.WriteLine($"  - {issue}");
            }

            return preview.ExpectedStatus is ConversionStatus.Failed or ConversionStatus.InvalidUtf16 ? 1 : 0;
        }

        var result = await new FileConverter(settings).ConvertAsync(Path.GetFullPath(file)).ConfigureAwait(false);
        Console.WriteLine(result);
        if (result.Issues.Count > 0)
        {
            Console.WriteLine($"Niet-representeerbaar: {result.DescribeIssues(50)}");
        }

        if (result.Error is not null)
        {
            Console.WriteLine($"Fout: {result.Error}");
        }

        return result.Status is ConversionStatus.Failed or ConversionStatus.InvalidUtf16 or ConversionStatus.LockTimeout ? 1 : 0;
    }

    private static int ShowStatus()
    {
        var status = ServiceStatusSnapshot.TryLoad();
        if (status is null)
        {
            Console.WriteLine($"Geen status gevonden ({AppPaths.StatusFile}). Draait de service?");
            return 1;
        }

        Console.WriteLine($"AnsiWatch {status.Version}, PID {status.ProcessId}, account {status.Account}");
        Console.WriteLine($"Gestart {status.StartedUtc.ToLocalTime():g}, bijgewerkt {status.UpdatedUtc.ToLocalTime():T}");
        Console.WriteLine($"Geconverteerd {status.Converted}, overgeslagen {status.Skipped}, waarschuwingen {status.Warnings}, fouten {status.Errors}, wachtrij {status.QueueLength}");
        foreach (var p in status.Paths)
        {
            Console.WriteLine($"  [{p.State}] {p.Path}{(p.Message is null ? string.Empty : " – " + p.Message)}");
        }

        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
            AnsiWatch.Service.exe                       Start als service (of in console voor diagnose)
            AnsiWatch.Service.exe init-config --watch-path "D:\Export;\\srv\share" [--include-subfolders] [--force]
            AnsiWatch.Service.exe convert <bestand> [--dry-run] [--codepage 1252] [--action Skip|Replace|BestFit]
            AnsiWatch.Service.exe status
            AnsiWatch.Service.exe version
            """);
        return 0;
    }

    private static int Print(string text)
    {
        Console.WriteLine(text);
        return 0;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>
    /// "--naam" (true), "--naam 1|true|yes|ja" (true) of "--naam 0|false|no|nee|''" (false).
    /// De MSI geeft altijd een waarde mee: --include-subfolders "[WATCHSUBFOLDERS]".
    /// </summary>
    internal static bool BoolOption(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return true;
        }

        return args[index + 1].Trim().ToLowerInvariant() is "1" or "true" or "yes" or "ja" or "y" or "j";
    }

    private static bool Flag(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
