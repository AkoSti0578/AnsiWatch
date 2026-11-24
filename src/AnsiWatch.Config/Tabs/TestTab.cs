using AnsiWatch.Core.Conversion;

namespace AnsiWatch.Config.Tabs;

/// <summary>
/// Tabblad "Testconversie": kies een bestand en zie wat AnsiWatch ermee zou doen, met de
/// instellingen zoals ze nu in het venster staan. Er wordt niets opgeslagen of gewijzigd.
/// </summary>
internal sealed class TestTab : TabPage
{
    private readonly Func<ConversionSettings?> _settings;
    private readonly Label _file = Ui.Label("Nog geen bestand gekozen.");
    private readonly Label _summary = Ui.Label(string.Empty);
    private readonly ListView _issues = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
    private readonly TextBox _preview = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = Ui.MonoFont,
        BackColor = SystemColors.Window,
    };
    private readonly Label _hex = Ui.Label(string.Empty);

    public TestTab(Func<ConversionSettings?> settings) : base("Testconversie")
    {
        _settings = settings;
        _summary.MaximumSize = new Size(900, 0);
        _hex.Font = Ui.MonoFont;

        _issues.Columns.Add("Regel", 70);
        _issues.Columns.Add("Positie", 70);
        _issues.Columns.Add("Teken", 60);
        _issues.Columns.Add("Unicode", 90);

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8) };
        top.Controls.Add(Ui.Row(Ui.Button("Bestand kiezen…", (_, _) => Choose()), Ui.Button("Opnieuw", (_, _) => Run()), _file));
        top.Controls.Add(Ui.Hint("Er wordt niets opgeslagen: het bestand wordt alleen gelezen. De instellingen uit het tabblad 'Instellingen' worden gebruikt, ook als ze nog niet opgeslagen zijn."));
        top.Controls.Add(_summary);
        top.Controls.Add(_hex);

        var issuesBox = new GroupBox { Text = "Niet-representeerbare tekens", Dock = DockStyle.Fill };
        issuesBox.Controls.Add(_issues);
        var previewBox = new GroupBox { Text = "Voorbeeld van het resultaat (ANSI, teruggelezen)", Dock = DockStyle.Fill };
        previewBox.Controls.Add(_preview);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 300, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(issuesBox);
        split.Panel2.Controls.Add(previewBox);

        Controls.Add(split);
        Controls.Add(top);
    }

    private string? _path;

    private void Choose()
    {
        using var dialog = new OpenFileDialog { Filter = "Tekstbestanden (*.txt)|*.txt|Alle bestanden (*.*)|*.*", Title = "Kies een bestand voor de testconversie" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _path = dialog.FileName;
            Run();
        }
    }

    private void Run()
    {
        if (_path is null)
        {
            return;
        }

        var settings = _settings();
        if (settings is null)
        {
            return;
        }

        UseWaitCursor = true;
        try
        {
            var result = FilePreview.Create(_path, settings);
            _file.Text = _path;
            _issues.BeginUpdate();
            _issues.Items.Clear();
            foreach (var issue in result.Issues)
            {
                var item = new ListViewItem(issue.Line.ToString(System.Globalization.CultureInfo.CurrentCulture));
                item.SubItems.Add(issue.Column.ToString(System.Globalization.CultureInfo.CurrentCulture));
                item.SubItems.Add(issue.Character);
                item.SubItems.Add(issue.CodePointText);
                _issues.Items.Add(item);
            }

            _issues.EndUpdate();

            var lines = new List<string>
            {
                $"Codering: {result.Detection.Description}   ·   Verwachte uitkomst: {result.ExpectedStatus.ToDutch()} (codepage {settings.CodePage}, {settings.UnmappableAction})",
            };
            if (result.Error is not null)
            {
                lines.Add($"Fout: {result.Error}");
            }
            else if (result.Detection.IsUtf16Le)
            {
                lines.Add($"Grootte: {Ui.FormatBytes(result.FileSize)} → {(result.ConvertedSize is { } s ? Ui.FormatBytes(s) : "n.v.t.")}   ·   " +
                          $"{result.CharCount:N0} tekens, {result.LineCount:N0} regels, regeleinden: {result.LineEndings.Crlf:N0}× CRLF, {result.LineEndings.Lf:N0}× LF (blijven ongewijzigd)");
                lines.Add(result.Issues.Count == 0
                    ? "✔ Alle tekens bestaan in de doel-codepage: de tekst blijft exact gelijk."
                    : $"⚠ {result.Issues.Count}{(result.IssuesTruncated ? "+" : string.Empty)} teken(s) bestaan niet in codepage {settings.CodePage}.");
            }

            _summary.Text = string.Join(Environment.NewLine, lines);
            _summary.ForeColor = result.Error is not null ? Color.Firebrick : result.Issues.Count > 0 ? Color.DarkOrange : SystemColors.ControlText;
            _hex.Text = string.IsNullOrEmpty(result.PreviewHex) ? string.Empty : $"Eerste bytes: {result.PreviewHex}";
            _preview.Text = result.PreviewText.ReplaceLineEndings("\r\n") + (result.PreviewTruncated ? "\r\n\r\n[… voorbeeld ingekort …]" : string.Empty);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }
}
