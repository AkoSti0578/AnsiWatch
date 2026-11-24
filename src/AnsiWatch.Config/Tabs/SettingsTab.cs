using System.Globalization;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Text;

namespace AnsiWatch.Config.Tabs;

/// <summary>Tabblad "Instellingen".</summary>
internal sealed class SettingsTab : TabPage
{
    private readonly ComboBox _codePage = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly ComboBox _action = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _actionHint = Ui.Hint(string.Empty);
    private readonly CheckBox _detectWithoutBom = new() { Text = "Ook UTF-16LE zonder BOM herkennen (heuristiek)", AutoSize = true };
    private readonly CheckBox _preserveTimestamps = new() { Text = "Originele aanmaak- en wijzigingsdatum behouden", AutoSize = true };
    private readonly CheckBox _verify = new() { Text = "Na schrijven verifiëren (terugdecoderen en vergelijken)", AutoSize = true };
    private readonly TextBox _backupFolder = new() { Width = 340 };
    private readonly NumericUpDown _retention = new() { Minimum = 0, Maximum = 3650, Width = 80 };
    private readonly TextBox _quarantineFolder = new() { Width = 340 };
    private readonly NumericUpDown _rescan = new() { Minimum = 5, Maximum = 86400, Width = 80 };
    private readonly ComboBox _logLevel = new() { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _debounce = new() { Minimum = 0, Maximum = 60000, Increment = 50, Width = 80 };
    private readonly NumericUpDown _lockTimeout = new() { Minimum = 1, Maximum = 3600, Width = 80 };
    private readonly NumericUpDown _parallelism = new() { Minimum = 1, Maximum = 64, Width = 80 };
    private bool _loading;

    public SettingsTab() : base("Instellingen")
    {
        foreach (var (cp, name) in CodePageSupport.CommonAnsiCodePages)
        {
            _codePage.Items.Add(new CodePageItem(cp, name));
        }

        _action.Items.AddRange(new object[]
        {
            new ActionItem(UnmappableCharacterAction.Skip, "Skip – niet converteren (standaard)",
                "Het bestand blijft ongewijzigd; er wordt een waarschuwing gelogd met regel en positie. Met een quarantainemap wordt het bestand daarheen verplaatst."),
            new ActionItem(UnmappableCharacterAction.Replace, "Replace – vervangen door '?'",
                "Het bestand wordt geconverteerd; tekens die niet bestaan worden '?'. De waarschuwing vermeldt regel en positie."),
            new ActionItem(UnmappableCharacterAction.BestFit, "BestFit – Windows best-fit mapping",
                "Het bestand wordt geconverteerd met de best-fit tabel van Windows (bijv. Ł → L, ő → o). Wat geen best-fit heeft, wordt '?'."),
        });
        _action.SelectedIndexChanged += (_, _) => _actionHint.Text = (_action.SelectedItem as ActionItem)?.Hint ?? string.Empty;
        _logLevel.Items.AddRange(OptionsValidator.LogLevels.Cast<object>().ToArray());

        var grid = Ui.Grid(2);
        void Row(string label, Control control)
        {
            grid.Controls.Add(Ui.Label(label));
            grid.Controls.Add(control);
        }

        void Wide(Control control)
        {
            grid.Controls.Add(control);
            grid.SetColumnSpan(control, 2);
        }

        Wide(Ui.Label("Conversie", bold: true));
        Row("Doel-codepage (ANSI):", _codePage);
        Row(string.Empty, Ui.Hint("Kies uit de lijst of typ een codepagenummer. 1252 = West-Europees (Nederlands, Engels, …)."));
        Row("Niet-representeerbare tekens:", _action);
        Row(string.Empty, _actionHint);
        Row(string.Empty, _detectWithoutBom);
        Row(string.Empty, _preserveTimestamps);
        Row(string.Empty, _verify);

        Wide(Ui.Label("Back-up en quarantaine", bold: true));
        Row("Back-upmap (optioneel):", Ui.Row(_backupFolder, Ui.Button("…", (_, _) => Browse(_backupFolder), 30)));
        Row("Back-ups bewaren (dagen):", Ui.Row(_retention, Ui.Hint("0 = nooit opruimen")));
        Row("Quarantainemap (optioneel):", Ui.Row(_quarantineFolder, Ui.Button("…", (_, _) => Browse(_quarantineFolder), 30)));
        Row(string.Empty, Ui.Hint("Alleen bij 'Skip': bestanden met niet-representeerbare tekens worden hierheen verplaatst, met een rapport ernaast."));

        Wide(Ui.Label("Bewaking en logging", bold: true));
        Row("Vangnet-scan elke (seconden):", Ui.Row(_rescan, Ui.Hint("nodig voor UNC/SMB-shares, waar meldingen kunnen ontbreken")));
        Row("Logniveau:", _logLevel);

        Wide(Ui.Label("Geavanceerd", bold: true));
        Row("Debounce per bestand (ms):", _debounce);
        Row("Max. wachten op gelockt bestand (s):", _lockTimeout);
        Row("Gelijktijdige conversies:", Ui.Row(_parallelism, Ui.Hint("wijziging actief na herstart service")));

        foreach (var control in new Control[] { _codePage, _action, _detectWithoutBom, _preserveTimestamps, _verify, _backupFolder, _retention, _quarantineFolder, _rescan, _logLevel, _debounce, _lockTimeout, _parallelism })
        {
            switch (control)
            {
                case ComboBox c:
                    c.SelectedIndexChanged += (_, _) => RaiseChanged();
                    c.TextChanged += (_, _) => RaiseChanged();
                    break;
                case CheckBox c:
                    c.CheckedChanged += (_, _) => RaiseChanged();
                    break;
                case NumericUpDown n:
                    n.ValueChanged += (_, _) => RaiseChanged();
                    break;
                default:
                    control.TextChanged += (_, _) => RaiseChanged();
                    break;
            }
        }

        Controls.Add(grid);
    }

    public event Action? Changed;

    public void LoadFrom(AnsiWatchOptions o)
    {
        _loading = true;
        var cpItem = _codePage.Items.Cast<CodePageItem>().FirstOrDefault(i => i.CodePage == o.CodePage);
        if (cpItem is not null)
        {
            _codePage.SelectedItem = cpItem;
        }
        else
        {
            _codePage.Text = o.CodePage.ToString(CultureInfo.InvariantCulture);
        }

        _action.SelectedItem = _action.Items.Cast<ActionItem>().First(i => i.Action == o.UnmappableCharacterAction);
        _detectWithoutBom.Checked = o.DetectWithoutBom;
        _preserveTimestamps.Checked = o.PreserveTimestamps;
        _verify.Checked = o.VerifyAfterWrite;
        _backupFolder.Text = o.BackupFolder ?? string.Empty;
        _retention.Value = Math.Clamp(o.BackupRetentionDays, 0, 3650);
        _quarantineFolder.Text = o.QuarantineFolder ?? string.Empty;
        _rescan.Value = Math.Clamp(o.RescanIntervalSeconds, 5, 86400);
        _logLevel.SelectedItem = OptionsValidator.LogLevels.FirstOrDefault(l => l.Equals(o.LogLevel, StringComparison.OrdinalIgnoreCase)) ?? "Information";
        _debounce.Value = Math.Clamp(o.DebounceMilliseconds, 0, 60000);
        _lockTimeout.Value = Math.Clamp(o.LockTimeoutSeconds, 1, 3600);
        _parallelism.Value = Math.Clamp(o.MaxParallelism, 1, 64);
        _loading = false;
    }

    /// <summary>Schrijft de velden naar de opties. False (met melding) bij een ongeldige codepage.</summary>
    public bool SaveTo(AnsiWatchOptions o, out string? error)
    {
        error = null;
        if (_codePage.SelectedItem is CodePageItem item && _codePage.Text == item.ToString())
        {
            o.CodePage = item.CodePage;
        }
        else if (int.TryParse(_codePage.Text.Split(' ')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cp))
        {
            o.CodePage = cp;
        }
        else
        {
            error = $"'{_codePage.Text}' is geen geldig codepagenummer.";
            return false;
        }

        o.UnmappableCharacterAction = ((ActionItem)_action.SelectedItem!).Action;
        o.DetectWithoutBom = _detectWithoutBom.Checked;
        o.PreserveTimestamps = _preserveTimestamps.Checked;
        o.VerifyAfterWrite = _verify.Checked;
        o.BackupFolder = string.IsNullOrWhiteSpace(_backupFolder.Text) ? null : _backupFolder.Text.Trim();
        o.BackupRetentionDays = (int)_retention.Value;
        o.QuarantineFolder = string.IsNullOrWhiteSpace(_quarantineFolder.Text) ? null : _quarantineFolder.Text.Trim();
        o.RescanIntervalSeconds = (int)_rescan.Value;
        o.LogLevel = _logLevel.SelectedItem as string ?? "Information";
        o.DebounceMilliseconds = (int)_debounce.Value;
        o.LockTimeoutSeconds = (int)_lockTimeout.Value;
        o.MaxParallelism = (int)_parallelism.Value;
        return true;
    }

    private void RaiseChanged()
    {
        if (!_loading)
        {
            Changed?.Invoke();
        }
    }

    private void Browse(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "Kies een map", ShowNewFolderButton = true };
        if (Directory.Exists(target.Text))
        {
            dialog.InitialDirectory = target.Text;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            target.Text = dialog.SelectedPath;
        }
    }

    private sealed record CodePageItem(int CodePage, string Name)
    {
        public override string ToString() => $"{CodePage} – {Name}";
    }

    private sealed record ActionItem(UnmappableCharacterAction Action, string Text, string Hint)
    {
        public override string ToString() => Text;
    }
}
