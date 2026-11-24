using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Diagnostics;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Sinks.EventLog;

namespace AnsiWatch.Service.Logging;

/// <summary>
/// Serilog: rolling logbestand (dagelijks, 30 dagen) in %ProgramData%\AnsiWatch\logs en het
/// Windows Event Log (bron "AnsiWatch") voor start/stop, waarschuwingen en fouten.
/// </summary>
public static class LoggingSetup
{
    private const string FileTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] ({EvtId}) {Message:lj}{NewLine}{Exception}";

    /// <summary>Runtime-aanpasbaar logniveau (volgt LogLevel uit config.json).</summary>
    public static LoggingLevelSwitch LevelSwitch { get; } = new(LogEventLevel.Information);

    /// <summary>Of het Event Log beschikbaar is.</summary>
    public static bool EventLogEnabled { get; private set; }

    /// <summary>Bouwt de Serilog-logger.</summary>
    public static Logger CreateLogger(bool console)
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);

        var config = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
            .Enrich.With<EventIdEnricher>()
            .WriteTo.File(
                Path.Combine(AppPaths.LogDirectory, "ansiwatch-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                shared: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                outputTemplate: FileTemplate,
                formatProvider: CultureInfo.InvariantCulture);

        if (console)
        {
            config.WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] ({EvtId}) {Message:lj}{NewLine}{Exception}", formatProvider: CultureInfo.InvariantCulture);
        }

        if (OperatingSystem.IsWindows() && EnsureEventSource())
        {
            EventLogEnabled = true;
            AddEventLogSink(config);
        }

        return config.CreateLogger();
    }

    /// <summary>Zet het logniveau op basis van de configuratiewaarde.</summary>
    public static void ApplyLevel(string? level)
    {
        LevelSwitch.MinimumLevel = level?.Trim().ToUpperInvariant() switch
        {
            "VERBOSE" or "TRACE" => LogEventLevel.Verbose,
            "DEBUG" => LogEventLevel.Debug,
            "WARNING" or "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "FATAL" or "CRITICAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information,
        };
    }

    /// <summary>Waarschuwingen en fouten, plus start/stop en enkele andere belangrijke informatieve events.</summary>
    internal static bool IsEventLogWorthy(LogEvent e) =>
        e.Level >= LogEventLevel.Warning || (GetEventId(e) is { } id && AnsiWatchEventIds.AlwaysToEventLog.Contains(id));

    internal static int? GetEventId(LogEvent e) =>
        e.Properties.TryGetValue("EventId", out var value) && value is StructureValue sv
            ? sv.Properties.FirstOrDefault(p => p.Name == "Id")?.Value is ScalarValue { Value: int id } ? id : null
            : null;

    [SupportedOSPlatform("windows")]
    private static void AddEventLogSink(LoggerConfiguration config) =>
        config.WriteTo.Logger(lc => lc
            .Filter.ByIncludingOnly(IsEventLogWorthy)
            .WriteTo.EventLog(
                source: AppPaths.EventSourceName,
                logName: "Application",
                manageEventSource: false,
                outputTemplate: "{Message:lj}{NewLine}{Exception}",
                formatProvider: CultureInfo.InvariantCulture,
                eventIdProvider: new AnsiWatchEventIdProvider()));

    [SupportedOSPlatform("windows")]
    private static bool EnsureEventSource()
    {
        try
        {
            if (EventLog.SourceExists(AppPaths.EventSourceName))
            {
                return true;
            }

            // Normaal maakt de MSI de bron aan; als fallback (admin-account) doen we het zelf.
            EventLog.CreateEventSource(new EventSourceCreationData(AppPaths.EventSourceName, "Application"));
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Voegt de scalar EvtId toe voor het logbestand.</summary>
    private sealed class EventIdEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory) =>
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("EvtId", GetEventId(logEvent)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "----"));
    }

    /// <summary>Gebruikt de Microsoft.Extensions.Logging EventId als Event Log event-ID.</summary>
    private sealed class AnsiWatchEventIdProvider : IEventIdProvider
    {
        public ushort ComputeEventId(LogEvent logEvent) => GetEventId(logEvent) is { } id and > 0 and <= ushort.MaxValue
            ? (ushort)id
            : logEvent.Level switch
            {
                LogEventLevel.Fatal or LogEventLevel.Error => (ushort)AnsiWatchEventIds.ServiceError.Id,
                _ => (ushort)9999,
            };
    }
}
