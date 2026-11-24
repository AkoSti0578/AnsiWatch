using System.ServiceProcess;
using AnsiWatch.Core.Configuration;
using Microsoft.Win32;

namespace AnsiWatch.Config.Services;

/// <summary>Bediening en informatie van de Windows-service "AnsiWatch".</summary>
internal static class ServiceManager
{
    private const string RegistryPath = @"SYSTEM\CurrentControlSet\Services\" + AppPaths.ServiceName;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>Huidige status, of null als de service niet geïnstalleerd is.</summary>
    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var sc = new ServiceController(AppPaths.ServiceName);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Account waaronder de service draait (ObjectName), bijv. "DOMEIN\svc_ansiwatch" of "LocalSystem".</summary>
    public static string? GetAccount()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        return key?.GetValue("ObjectName") as string;
    }

    /// <summary>Opstarttype als tekst.</summary>
    public static string GetStartType()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
        if (key is null)
        {
            return "-";
        }

        var start = key.GetValue("Start") as int?;
        var delayed = key.GetValue("DelayedAutostart") as int? == 1;
        return start switch
        {
            2 when delayed => "Automatisch (vertraagd starten)",
            2 => "Automatisch",
            3 => "Handmatig",
            4 => "Uitgeschakeld",
            _ => "Onbekend",
        };
    }

    public static Task StartAsync() => Task.Run(() =>
    {
        using var sc = new ServiceController(AppPaths.ServiceName);
        if (sc.Status != ServiceControllerStatus.Running)
        {
            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, Timeout);
        }
    });

    public static Task StopAsync() => Task.Run(() =>
    {
        using var sc = new ServiceController(AppPaths.ServiceName);
        if (sc.Status != ServiceControllerStatus.Stopped)
        {
            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
        }
    });

    public static async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    public static string ToDutch(ServiceControllerStatus? status) => status switch
    {
        null => "Niet geïnstalleerd",
        ServiceControllerStatus.Running => "Actief",
        ServiceControllerStatus.Stopped => "Gestopt",
        ServiceControllerStatus.StartPending => "Wordt gestart…",
        ServiceControllerStatus.StopPending => "Wordt gestopt…",
        ServiceControllerStatus.Paused => "Gepauzeerd",
        _ => status.ToString()!,
    };
}
