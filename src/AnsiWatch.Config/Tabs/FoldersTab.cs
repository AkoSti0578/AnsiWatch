using AnsiWatch.Config.Services;
using AnsiWatch.Core.Configuration;
using AnsiWatch.Core.Status;

namespace AnsiWatch.Config.Tabs;

/// <summary>Tabblad "Mappen": bewaakte paden beheren en controleren.</summary>
internal sealed class FoldersTab : TabPage
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        GridLines = true,
        HideSelection = false,
        ShowItemToolTips = true,
    };

    private bool _loading;

    public FoldersTab() : base("Mappen")
    {
        Padding = new Padding(8);
        _list.Columns.Add("Actief / pad", 380);
        _list.Columns.Add("Submappen", 80);
        _list.Columns.Add("Servicestatus", 110);
        _list.Columns.Add("Controle", 420);
        _list.ItemChecked += (_, _) => { if (!_loading) { Changed?.Invoke(); } };
        _list.DoubleClick += (_, _) => ToggleSubfolders();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
        buttons.Controls.AddRange(new Control[]
        {
            Ui.Button("Map toevoegen…", (_, _) => AddFolder()),
            Ui.Button("UNC-pad toevoegen…", (_, _) => AddUnc()),
            Ui.Button("Verwijderen", (_, _) => RemoveSelected()),
            Ui.Button("Submappen aan/uit", (_, _) => ToggleSubfolders()),
            Ui.Button("Paden en rechten controleren", (_, _) => _ = ValidateAsync()),
        });

        var hint = Ui.Hint("Vink een pad uit om het tijdelijk niet te bewaken. Dubbelklik om submappen aan of uit te zetten. " +
                           "De controle bepaalt de effectieve NTFS-rechten van het serviceaccount (niet die van u). " +
                           "Wijzigingen worden na 'Opslaan' direct door de service overgenomen, zonder herstart.");
        hint.Dock = DockStyle.Bottom;

        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(hint);
    }

    public event Action? Changed;

    public void LoadFrom(AnsiWatchOptions options)
    {
        _loading = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var path in options.WatchPaths)
        {
            AddItem(path.Clone());
        }

        _list.EndUpdate();
        _loading = false;
    }

    public void SaveTo(AnsiWatchOptions options)
    {
        options.WatchPaths = _list.Items.Cast<ListViewItem>().Select(item =>
        {
            var path = ((WatchPathOptions)item.Tag!).Clone();
            path.Enabled = item.Checked;
            return path;
        }).ToList();
    }

    public void ShowServiceStatus(ServiceStatusSnapshot? status)
    {
        foreach (ListViewItem item in _list.Items)
        {
            var path = (WatchPathOptions)item.Tag!;
            var match = status?.Paths.FirstOrDefault(p => string.Equals(p.Path, path.Path, StringComparison.OrdinalIgnoreCase));
            item.SubItems[2].Text = match?.State ?? (status is null ? "-" : "nog niet geladen");
            item.SubItems[2].ForeColor = match?.State == "Offline" ? Color.Firebrick : SystemColors.WindowText;
            item.ToolTipText = match?.Message ?? string.Empty;
        }
    }

    private void AddItem(WatchPathOptions path)
    {
        var item = new ListViewItem(path.Path) { Checked = path.Enabled, Tag = path, UseItemStyleForSubItems = false };
        item.SubItems.Add(path.IncludeSubfolders ? "ja" : "nee");
        item.SubItems.Add("-");
        item.SubItems.Add(string.Empty);
        _list.Items.Add(item);
    }

    private void AddFolder()
    {
        using var dialog = new FolderBrowserDialog { Description = "Kies een map die AnsiWatch moet bewaken", UseDescriptionForTitle = true, ShowNewFolderButton = true };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            AddPath(dialog.SelectedPath);
        }
    }

    private void AddUnc()
    {
        using var dialog = new InputDialog("UNC-pad toevoegen",
            "Voer een UNC-pad in, bijvoorbeeld \\\\fileserver\\export\\out. Het serviceaccount (niet u) moet lees- en schrijfrechten hebben op share én map.",
            @"\\");
        if (dialog.ShowDialog(this) == DialogResult.OK && dialog.Value.Length > 2)
        {
            AddPath(dialog.Value);
        }
    }

    private void AddPath(string path)
    {
        path = path.Trim().Trim('"');
        if (path.Length > 3)
        {
            path = path.TrimEnd('\\');
        }

        if (!Path.IsPathFullyQualified(path))
        {
            Ui.Error(this, $"'{path}' is geen volledig pad.");
            return;
        }

        if (_list.Items.Cast<ListViewItem>().Any(i => string.Equals(((WatchPathOptions)i.Tag!).Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            Ui.Info(this, "Dit pad staat al in de lijst.");
            return;
        }

        if (!path.StartsWith(@"\\", StringComparison.Ordinal) && char.IsLetter(path[0]) && IsMappedDrive(path))
        {
            Ui.Info(this, "Let op: dit lijkt een netwerkschijf (driveletter). Een service ziet de driveletters van uw sessie niet. Gebruik het UNC-pad (\\\\server\\share\\map).");
        }

        AddItem(new WatchPathOptions { Path = path, Enabled = true });
        Changed?.Invoke();
    }

    private static bool IsMappedDrive(string path)
    {
        try
        {
            return new DriveInfo(path[..1]).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem item in _list.SelectedItems)
        {
            _list.Items.Remove(item);
        }

        Changed?.Invoke();
    }

    private void ToggleSubfolders()
    {
        foreach (ListViewItem item in _list.SelectedItems)
        {
            var path = (WatchPathOptions)item.Tag!;
            path.IncludeSubfolders = !path.IncludeSubfolders;
            item.SubItems[1].Text = path.IncludeSubfolders ? "ja" : "nee";
        }

        Changed?.Invoke();
    }

    private async Task ValidateAsync()
    {
        var account = ServiceManager.GetAccount();
        foreach (ListViewItem item in _list.Items)
        {
            item.SubItems[3].Text = "bezig…";
            item.SubItems[3].ForeColor = SystemColors.GrayText;
        }

        foreach (ListViewItem item in _list.Items)
        {
            var path = ((WatchPathOptions)item.Tag!).Path;
            // Op de achtergrond: een offline share kan lang blokkeren.
            var result = await Task.Run(() => AccessChecker.Check(path, account)).ConfigureAwait(true);
            item.SubItems[3].Text = (result.CanWrite switch { true => "✔ ", false => "✖ ", null => "? " }) + result.Message;
            item.SubItems[3].ForeColor = result.CanWrite switch { true => Color.DarkGreen, false => Color.Firebrick, null => Color.DarkOrange };
        }
    }
}
