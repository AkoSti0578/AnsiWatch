using AnsiWatch.Config.Tabs;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Conversion;
using AnsiWatch.Core.Status;

namespace AnsiWatch.Config;

/// <summary>Hoofdvenster van AnsiWatch.Config.</summary>
internal sealed class MainForm : Form
{
    private readonly FoldersTab _folders = new();
    private readonly SettingsTab _settings = new();
    private readonly ServiceTab _service = new();
    private readonly TestTab _test;
    private readonly Label _footer = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 9, 3, 3) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private AnsiWatchOptions _loaded = new();
    private bool _dirty;

    public MainForm()
    {
        Text = "AnsiWatch – configuratie";
        Icon = Ui.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        Size = new Size(1150, 760);

        _test = new TestTab(CurrentConversionSettings);
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 4) };
        tabs.TabPages.AddRange(new TabPage[] { _folders, _settings, _service, _test });

        var save = Ui.Button("Opslaan", (_, _) => Save(), 110);
        var reload = Ui.Button("Herladen", (_, _) => { if (ConfirmDiscard()) { LoadConfig(); } }, 110);
        var footer = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_footer, 0, 0);
        footer.Controls.Add(reload, 1, 0);
        footer.Controls.Add(save, 2, 0);
        AcceptButton = null;

        Controls.Add(tabs);
        Controls.Add(footer);

        _folders.Changed += MarkDirty;
        _settings.Changed += MarkDirty;
        _timer.Tick += (_, _) => RefreshStatus();
        Load += (_, _) =>
        {
            LoadConfig();
            RefreshStatus();
            _timer.Start();
        };
        FormClosing += (_, e) => e.Cancel = !ConfirmDiscard();
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                Save();
                e.Handled = true;
            }
        };
    }

    private void LoadConfig()
    {
        try
        {
            _loaded = ConfigStore.Load(AppPaths.ConfigFile);
        }
        catch (ConfigException ex)
        {
            Ui.Error(this, ex.Message + "\n\nDe standaardinstellingen worden getoond. Bij opslaan wordt het bestand overschreven.");
            _loaded = new AnsiWatchOptions();
        }

        _folders.LoadFrom(_loaded);
        _settings.LoadFrom(_loaded);
        _dirty = false;
        UpdateFooter();
    }

    private AnsiWatchOptions? CollectOptions(out string? error)
    {
        var options = _loaded.Clone();
        _folders.SaveTo(options);
        return _settings.SaveTo(options, out error) ? options : null;
    }

    private ConversionSettings? CurrentConversionSettings()
    {
        var options = CollectOptions(out var error);
        if (options is null)
        {
            Ui.Error(this, error!);
            return null;
        }

        return options.ToConversionSettings();
    }

    private void Save()
    {
        var options = CollectOptions(out var error);
        if (options is null)
        {
            Ui.Error(this, error!);
            return;
        }

        var issues = OptionsValidator.Validate(options, checkFileSystem: true);
        var errors = issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            Ui.Error(this, "De configuratie is niet opgeslagen:\n\n• " + string.Join("\n• ", errors.Select(e => e.Message)));
            return;
        }

        var warnings = issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList();
        if (warnings.Count > 0 && MessageBox.Show(this,
                "Let op:\n\n• " + string.Join("\n• ", warnings.Select(w => w.Message)) + "\n\nToch opslaan?",
                "AnsiWatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            ConfigStore.Save(AppPaths.ConfigFile, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Error(this, $"Opslaan mislukt: {ex.Message}");
            return;
        }

        _loaded = options;
        _dirty = false;
        UpdateFooter("Opgeslagen. De service neemt de wijzigingen binnen enkele seconden over (geen herstart nodig).");
    }

    private void RefreshStatus()
    {
        var snapshot = ServiceStatusSnapshot.TryLoad();
        _service.RefreshStatus(snapshot);
        _folders.ShowServiceStatus(snapshot);
    }

    private void MarkDirty()
    {
        _dirty = true;
        UpdateFooter();
    }

    private void UpdateFooter(string? message = null)
    {
        _footer.Text = message ?? (_dirty ? "● Niet-opgeslagen wijzigingen" : $"Configuratie: {AppPaths.ConfigFile}");
        _footer.ForeColor = _dirty ? Color.DarkOrange : SystemColors.ControlText;
    }

    private bool ConfirmDiscard() =>
        !_dirty || MessageBox.Show(this, "Er zijn niet-opgeslagen wijzigingen. Deze negeren?", "AnsiWatch",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
        }

        base.Dispose(disposing);
    }
}
