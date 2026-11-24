using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace AnsiWatch.Service.Configuration;

/// <summary>
/// Bewaakt config.json en levert gevalideerde opties. Gebruikt de standaard
/// <c>AddJsonFile(..., reloadOnChange: true)</c>-pijplijn als wijzigingssignaal. Een ongeldige of
/// half-geschreven config wordt NIET toegepast: de laatst geldige configuratie blijft actief.
/// </summary>
public sealed class ConfigMonitor : IDisposable
{
    private readonly ILogger<ConfigMonitor> _logger;
    private readonly string _configPath;
    private readonly IConfigurationRoot _configuration;
    private readonly IDisposable _changeRegistration;
    private readonly PhysicalFileProvider _fileProvider;
    private readonly object _gate = new();
    private Timer? _debounce;
    private (long Length, DateTime LastWrite) _loadedStamp;

    /// <summary>Maakt de monitor en laadt de configuratie direct.</summary>
    public ConfigMonitor(ILogger<ConfigMonitor> logger, string? configPath = null)
    {
        _logger = logger;
        _configPath = Path.GetFullPath(configPath ?? AppPaths.ConfigFile);
        var directory = Path.GetDirectoryName(_configPath)!;
        Directory.CreateDirectory(directory);

        _fileProvider = new PhysicalFileProvider(directory) { UsePollingFileWatcher = false };
        _configuration = new ConfigurationBuilder()
            .AddJsonFile(source =>
            {
                source.FileProvider = _fileProvider;
                source.Path = Path.GetFileName(_configPath);
                source.Optional = true;
                source.ReloadOnChange = true;
                source.ReloadDelay = 300;
                // Ongeldige JSON mag de service nooit laten crashen; wij valideren zelf in Reload().
                source.OnLoadException = ctx => ctx.Ignore = true;
            })
            .Build();

        Current = LoadInitial();
        _changeRegistration = ChangeToken.OnChange(_configuration.GetReloadToken, ScheduleReload);
    }

    /// <summary>De actieve (laatst geldige) configuratie.</summary>
    public AnsiWatchOptions Current { get; private set; }

    /// <summary>Laatste configuratiefout, of null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Pad van config.json.</summary>
    public string ConfigPath => _configPath;

    /// <summary>Wordt aangeroepen met de nieuwe opties na een geldige wijziging.</summary>
    public event Action<AnsiWatchOptions>? Changed;

    /// <summary>
    /// Vangnet voor netwerk-/cloudlocaties waar change-notificaties ontbreken: vergelijkt grootte en
    /// wijzigingstijd van config.json en herlaadt als die afwijken.
    /// </summary>
    public void PollForChanges()
    {
        if (Stamp() != _loadedStamp)
        {
            ScheduleReload();
        }
    }

    /// <summary>Herlaadt direct (synchroon). Retourneert true als er nieuwe opties zijn toegepast.</summary>
    public bool Reload()
    {
        lock (_gate)
        {
            var stamp = Stamp();
            AnsiWatchOptions options;
            try
            {
                options = ConfigStore.Load(_configPath);
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
            {
                LastError = ex.Message;
                _loadedStamp = stamp;
                _logger.LogError(AnsiWatchEventIds.ConfigInvalid, "Configuratie kon niet gelezen worden; de vorige configuratie blijft actief. {Error}", ex.Message);
                return false;
            }

            var issues = OptionsValidator.Validate(options, checkFileSystem: false);
            if (!OptionsValidator.IsValid(issues))
            {
                LastError = string.Join(" ", issues.Where(i => i.Severity == ValidationSeverity.Error).Select(i => i.Message));
                _loadedStamp = stamp;
                _logger.LogError(AnsiWatchEventIds.ConfigInvalid, "Configuratie is ongeldig; de vorige configuratie blijft actief. {Errors}", LastError);
                return false;
            }

            foreach (var warning in issues.Where(i => i.Severity == ValidationSeverity.Warning))
            {
                _logger.LogWarning(AnsiWatchEventIds.ConfigWarning, "Configuratie: {Warning}", warning.Message);
            }

            LastError = null;
            _loadedStamp = stamp;
            Current = options;
            _logger.LogInformation(AnsiWatchEventIds.ConfigLoaded,
                "Configuratie geladen uit {ConfigPath}: {PathCount} pad(en), codepage {CodePage}, actie {Action}, rescan {Rescan}s.",
                _configPath, options.WatchPaths.Count(p => p.Enabled), options.CodePage, options.UnmappableCharacterAction, options.RescanIntervalSeconds);
        }

        Changed?.Invoke(Current);
        return true;
    }

    private AnsiWatchOptions LoadInitial()
    {
        Current = new AnsiWatchOptions();
        Reload();
        return Current;
    }

    private void ScheduleReload()
    {
        // Eén save in een editor geeft vaak meerdere events; even wachten en dan één keer herladen.
        lock (_gate)
        {
            _debounce ??= new Timer(_ => SafeReload(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(500, Timeout.Infinite);
        }
    }

    private void SafeReload()
    {
        try
        {
            Reload();
        }
        catch (Exception ex)
        {
            _logger.LogError(AnsiWatchEventIds.ServiceError, ex, "Onverwachte fout bij het herladen van de configuratie.");
        }
    }

    private (long, DateTime) Stamp()
    {
        try
        {
            var info = new FileInfo(_configPath);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : (-1, DateTime.MinValue);
        }
        catch (IOException)
        {
            return (-1, DateTime.MinValue);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _changeRegistration.Dispose();
        _debounce?.Dispose();
        (_configuration as IDisposable)?.Dispose();
        _fileProvider.Dispose();
    }
}
