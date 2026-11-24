using System.Reflection;

namespace AnsiWatch.Config;

/// <summary>Kleine hulpfuncties om de UI in code op te bouwen.</summary>
internal static class Ui
{
    public static Icon? AppIcon { get; } = LoadIcon();

    public static Font MonoFont { get; } = new("Consolas", 9.75f);

    public static Label Label(string text, bool bold = false)
    {
        var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 3, 3) };
        if (bold)
        {
            label.Font = new Font(Control.DefaultFont, FontStyle.Bold);
        }

        return label;
    }

    public static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(560, 0),
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(3, 0, 3, 8),
    };

    public static Button Button(string text, EventHandler onClick, int width = 0)
    {
        var button = new Button { Text = text, AutoSize = width == 0, Width = width == 0 ? 75 : width, Margin = new Padding(3), AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 2, 6, 2) };
        button.Click += onClick;
        return button;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
        panel.Controls.AddRange(controls);
        return panel;
    }

    public static TableLayoutPanel Grid(int columns)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = columns, AutoSize = true, AutoScroll = true, Padding = new Padding(10) };
        for (var i = 0; i < columns; i++)
        {
            grid.ColumnStyles.Add(i == columns - 1 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        }

        return grid;
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} bytes",
    };

    public static void Error(IWin32Window owner, string message) =>
        MessageBox.Show(owner, message, "AnsiWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);

    public static void Info(IWin32Window owner, string message) =>
        MessageBox.Show(owner, message, "AnsiWatch", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static Icon? LoadIcon()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AnsiWatch.ico");
        return stream is null ? null : new Icon(stream);
    }
}

/// <summary>Eenvoudige invoerdialoog (bijv. voor een UNC-pad).</summary>
internal sealed class InputDialog : Form
{
    private readonly TextBox _text = new() { Width = 460 };

    public InputDialog(string title, string prompt, string initial = "")
    {
        Text = title;
        Icon = Ui.AppIcon;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(10);
        _text.Text = initial;

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90 };
        var cancel = new Button { Text = "Annuleren", DialogResult = DialogResult.Cancel, Width = 90 };
        AcceptButton = ok;
        CancelButton = cancel;

        var layout = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, Dock = DockStyle.Fill };
        layout.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(460, 0) });
        layout.Controls.Add(_text);
        var buttons = Ui.Row(ok, cancel);
        buttons.Anchor = AnchorStyles.Right;
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }

    public string Value => _text.Text.Trim();
}
