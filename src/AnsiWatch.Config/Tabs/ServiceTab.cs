using System.Diagnostics;
using System.ServiceProcess;
using AnsiWatch.Config.Services;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;
using AnsiWatch.Core.Status;

namespace AnsiWatch.Config.Tabs;

/// <summary>Tabblad "Service": status, bediening, logs en live statistieken.</summary>
internal sealed class ServiceTab : TabPage
{
    private readonly Label _state = Ui.Label("-", bold: true);
    private readonly Label _account = Ui.Label("-");
    private readonly Label _startType = Ui.Label("-");
    private readonly Label _stats = Ui.Label("-");
    private readonly Label _heartbeat = Ui.Label("-");
    private readonly Button _start;
    private readonly Button _stop;
    private readonly Button _restart;
    private readonly ListView _recent = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true, ShowItemToolTips = true };

    public ServiceTab() : base("Service")
    {
        _start = Ui.Button("Start", async (_, _) => await RunAsync(ServiceManager.StartAsync, "starten").ConfigureAwait(true), 90);
        _stop = Ui.Button("Stop", async (_, _) => await RunAsync(ServiceManager.StopAsync, "stoppen").ConfigureAwait(true), 90);
        _restart = Ui.Button("Herstart", async (_, _) => await RunAsync(ServiceManager.RestartAsync, "herstarten").ConfigureAwait(true), 90);

        var grid = Ui.Grid(2);
        grid.Dock = DockStyle.Top;
        void Row(string label, Control control)
        {
            grid.Controls.Add(Ui.Label(label));
            grid.Controls.Add(control);
        }

        Row("Status:", _state);
        Row("Account:", _account);
        Row("Opstarttype:", _startType);
        Row("Statistieken:", _stats);
        Row("Laatste melding:", _heartbeat);
        Row(string.Empty, Ui.Row(_start, _stop, _restart));
        Row("Logging:", Ui.Row(
            Ui.Button("Log openen", (_, _) => OpenLog()),
            Ui.Button("Logmap openen", (_, _) => OpenFolder(AppPaths.LogDirectory)),
            Ui.Button("Logboeken (Event Viewer)", (_, _) => Shell("eventvwr.msc", "/c:Application")),
            Ui.Button("config.json openen", (_, _) => OpenFolder(AppPaths.DataDirectory))));

        _recent.Columns.Add("Tijd", 70);
        _recent.Columns.Add("Resultaat", 220);
        _recent.Columns.Add("Bestand", 380);
        _recent.Columns.Add("Grootte", 140);
        _recent.Columns.Add("Duur", 70);
        _recent.Columns.Add("Toelichting", 400);

        var recentBox = new GroupBox { Text = "Recente conversies en waarschuwingen", Dock = DockStyle.Fill, Padding = new Padding(8) };
        recentBox.Controls.Add(_recent);

        Controls.Add(recentBox);
        Controls.Add(grid);
    }

    /// <summary>Werkt de weergave bij (aangeroepen door een timer in het hoofdvenster).</summary>
    public void RefreshStatus(ServiceStatusSnapshot? snapshot)
    {
        var status = ServiceManager.GetStatus();
        _state.Text = ServiceManager.ToDutch(status);
        _state.ForeColor = status switch
        {
            ServiceControllerStatus.Running => Color.DarkGreen,
            null or ServiceControllerStatus.Stopped => Color.Firebrick,
            _ => Color.DarkOrange,
        };
        _account.Text = ServiceManager.GetAccount() ?? "-";
        _startType.Text = ServiceManager.GetStartType();
        _start.Enabled = status is ServiceControllerStatus.Stopped;
        _stop.Enabled = status is ServiceControllerStatus.Running;
        _restart.Enabled = status is ServiceControllerStatus.Running;

        if (snapshot is null || status != ServiceControllerStatus.Running)
        {
            _stats.Text = "-";
            _heartbeat.Text = snapshot is null ? "Geen status.json gevonden." : $"Service niet actief (laatste status {snapshot.UpdatedUtc.ToLocalTime():g}).";
            return;
        }

        _stats.Text = $"{snapshot.Converted} geconverteerd ({Ui.FormatBytes(snapshot.BytesProcessed)}) · {snapshot.Skipped} overgeslagen · " +
                      $"{snapshot.Warnings} waarschuwingen · {snapshot.Errors} fouten · wachtrij {snapshot.QueueLength} · sinds {snapshot.StartedUtc.ToLocalTime():g}";
        var age = DateTime.UtcNow - snapshot.UpdatedUtc;
        _heartbeat.Text = snapshot.ConfigError is not null
            ? $"⚠ Configuratiefout: {snapshot.ConfigError}"
            : age > TimeSpan.FromSeconds(30) ? $"⚠ Geen statusupdate sinds {age.TotalSeconds:F0} s" : $"Versie {snapshot.Version}, PID {snapshot.ProcessId}, bijgewerkt {snapshot.UpdatedUtc.ToLocalTime():T}";
        _heartbeat.ForeColor = snapshot.ConfigError is not null || age > TimeSpan.FromSeconds(30) ? Color.Firebrick : SystemColors.ControlText;

        _recent.BeginUpdate();
        _recent.Items.Clear();
        foreach (var r in snapshot.Recent)
        {
            var status2 = Enum.TryParse<ConversionStatus>(r.Status, out var s) ? s.ToDutch() : r.Status;
            var item = new ListViewItem(r.TimeUtc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture));
            item.SubItems.Add(status2);
            item.SubItems.Add(r.Path);
            item.SubItems.Add($"{Ui.FormatBytes(r.OriginalSize)} → {Ui.FormatBytes(r.NewSize)}");
            item.SubItems.Add($"{r.DurationMs} ms");
            item.SubItems.Add(r.Message ?? string.Empty);
            item.ToolTipText = r.Message ?? string.Empty;
            item.ForeColor = r.Status switch
            {
                nameof(ConversionStatus.Converted) => SystemColors.WindowText,
                nameof(ConversionStatus.Failed) => Color.Firebrick,
                _ => Color.DarkOrange,
            };
            _recent.Items.Add(item);
        }

        _recent.EndUpdate();
    }

    private async Task RunAsync(Func<Task> action, string verb)
    {
        UseWaitCursor = true;
        _start.Enabled = _stop.Enabled = _restart.Enabled = false;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception)
        {
            Ui.Error(this, $"Service {verb} mislukt: {ex.GetBaseException().Message}\n\nControleer het Event Log (bron AnsiWatch) en het logbestand.");
        }
        finally
        {
            UseWaitCursor = false;
            RefreshStatus(ServiceStatusSnapshot.TryLoad());
        }
    }

    private void OpenLog()
    {
        var latest = Directory.Exists(AppPaths.LogDirectory)
            ? new DirectoryInfo(AppPaths.LogDirectory).GetFiles("ansiwatch-*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
            : null;
        if (latest is null)
        {
            Ui.Info(this, $"Er is nog geen logbestand in {AppPaths.LogDirectory}.");
            return;
        }

        Shell(latest.FullName);
    }

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Shell("explorer.exe", $"\"{folder}\"");
    }

    private static void Shell(string file, string? args = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true, Arguments = args ?? string.Empty })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show(ex.Message, "AnsiWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
