using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Redout2ModManager.Unix;

internal sealed class MainWindow : Window
{
    private readonly ModManager _manager;
    private readonly TextBox _gamePath = new() { IsReadOnly = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _rows = new();
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Grid _content = new();
    private readonly List<string> _order = [];
    private IReadOnlyDictionary<string, ModRow> _displayedRows = new Dictionary<string, ModRow>();
    private readonly Button _moveUp;
    private readonly Button _moveDown;
    private readonly Button _applyOrder;
    private readonly Button _details;
    private readonly Button _uninstall;
    private string? _selectedMod;
    private bool _loadOrderPending;

    public MainWindow() : this(new ModManager()) { }

    internal MainWindow(ModManager manager)
    {
        _manager = manager;
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Title = $"Redout 2 Mod Manager v{version}";
        Width = 960;
        Height = 680;
        MinWidth = 820;
        MinHeight = 560;
        Background = Brush.Parse("#080A11");
        Foreground = Brush.Parse("#F4F6FF");

        _content.RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto");
        _content.Margin = new Thickness(24, 16, 24, 12);

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,150") };
        var heading = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto"),
            Margin = new Thickness(0, 0, 16, 0) };
        heading.Children.Add(new TextBlock { Text = $"REDOUT 2 MOD MANAGER v{version}", FontSize = 24, FontWeight = FontWeight.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis });
        var subtitle = new TextBlock { Text = "Install and toggle managed PAK mods. Game files are left untouched.",
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 4, 0, 0) };
        Grid.SetRow(subtitle, 1);
        heading.Children.Add(subtitle);
        var scanActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
        scanActions.Children.Add(MakeButton("Scan PAKs", async () => await Scan()));
        scanActions.Children.Add(MakeButton("Conflict List", async () => await ShowConflictList()));
        _details = MakeButton("Details", async () => await ShowSelectedDetails());
        scanActions.Children.Add(_details);
        foreach (var button in scanActions.Children.OfType<Button>()) button.Width = 150;
        Grid.SetRow(scanActions, 2);
        heading.Children.Add(scanActions);
        var pathLabel = new TextBlock { Text = "GAME FOLDER", Margin = new Thickness(3, 0, 0, 4) };
        Grid.SetRow(pathLabel, 3);
        heading.Children.Add(pathLabel);
        Grid.SetRow(_gamePath, 4);
        heading.Children.Add(_gamePath);
        header.Children.Add(heading);

        var links = new StackPanel { Spacing = 4 };
        links.Children.Add(MakeLinkButton("Nexus Mods", "nexus-mods.png", () => Open("https://www.nexusmods.com/redout2")));
        links.Children.Add(MakeLinkButton("GameBanana", "gamebanana.png", () => Open("https://gamebanana.com/games/24088")));
        var start = MakeLinkButton("Start Game", "redout2-steam.jpg", () => Open("steam://rungameid/1799930"));
        start.Background = Brush.Parse("#4356AB");
        links.Children.Add(start);
        links.Children.Add(MakeLinkButton("Discord", "discord-icon.png", () => Open("https://discord.com/invite/redout")));
        links.Children.Add(MakeButton("Browse...", async () => await Browse()));
        foreach (var button in links.Children.OfType<Button>()) button.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(links, 1);
        header.Children.Add(links);
        Add(header, 0);
        _status.Margin = new Thickness(3, 8, 3, 8);
        Add(_status, 1);

        var listPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var columnHeaders = new Grid { ColumnDefinitions = new ColumnDefinitions("260,150,*"),
            Background = Brush.Parse("#1A2238"), MinHeight = 28 };
        foreach (var (label, column) in new[] { ("Mod List", 0), ("Status", 1), ("Potential Conflicts", 2) })
        {
            var text = new TextBlock { Text = label, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, column);
            columnHeaders.Children.Add(text);
        }
        listPanel.Children.Add(columnHeaders);
        var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        listPanel.Children.Add(scroll);
        Add(listPanel, 2);

        var actions = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*"), Margin = new Thickness(0, 12, 0, 8) };
        var actionButtons = new[]
        {
            MakeButton("Import Mods...", async () => await Import()),
            MakeButton("Refresh Mod List", () => Run(() => { _manager.Refresh(); RefreshRows(); SetStatus("Mod list refreshed."); })),
            MakeButton("Toggle All", () =>
        {
            var enable = _checks.Values.Any(check => check.IsChecked != true);
            foreach (var check in _checks.Values) check.IsChecked = enable;
        }),
            MakeButton("Apply Changes", () => Run(() =>
        {
            _manager.Apply(_checks.ToDictionary(pair => pair.Key, pair => pair.Value.IsChecked == true, StringComparer.OrdinalIgnoreCase));
            RefreshRows();
            SetStatus("Mod changes applied.");
        })),
            _uninstall = MakeButton("Uninstall", async () => await UninstallSelected()),
            MakeButton("Mod Folder", () => Open(_manager.PaksDirectory))
        };
        for (var i = 0; i < actionButtons.Length; i++)
        {
            actionButtons[i].HorizontalAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(actionButtons[i], i);
            actions.Children.Add(actionButtons[i]);
        }
        Add(actions, 3);

        var orderActions = new StackPanel { Orientation = Orientation.Horizontal };
        _moveUp = MakeButton("Move Up", () => MoveSelected(-1));
        _moveDown = MakeButton("Move Down", () => MoveSelected(1));
        _applyOrder = MakeButton("Apply Load Order", async () => await ApplyLoadOrder());
        orderActions.Children.Add(_moveUp);
        orderActions.Children.Add(_moveDown);
        orderActions.Children.Add(_applyOrder);
        foreach (var button in orderActions.Children.OfType<Button>()) button.Width = 150;
        Add(orderActions, 4);
        UpdateOrderButtons();
        Content = _content;
        DragDrop.SetAllowDrop(_content, true);
        _content.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
        _content.AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            e.Handled = true;
            var files = e.Data.GetFiles();
            if (files is not null)
                await ImportFiles(files);
        });

        Opened += (_, _) => Run(() =>
        {
            var game = _manager.FindGame();
            if (game is null)
            {
                _gamePath.Text = "Redout 2 was not found. Use Browse... to select the game folder.";
                SetStatus("No game folder selected.");
            }
            else
            {
                _manager.SelectGame(game);
                RefreshRows();
                SetStatus("Game folder detected.");
            }
        });
    }

    private void Add(Control control, int row)
    {
        Grid.SetRow(control, row);
        _content.Children.Add(control);
    }

    private static Button MakeButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Margin = new Thickness(3),
            Padding = new Thickness(10, 6),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Background = Brush.Parse("#1A2238"),
            Foreground = Brush.Parse("#F4F6FF"),
            BorderBrush = Brush.Parse("#344E8A"),
            BorderThickness = new Thickness(1)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button MakeLinkButton(string text, string icon, Action action)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream($"Redout2ModManager.Assets.{icon}")
            ?? throw new InvalidOperationException($"Missing embedded icon: {icon}");
        var button = MakeButton(text, action);
        button.HorizontalContentAlignment = HorizontalAlignment.Left;
        button.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new Image { Source = new Bitmap(stream), Width = 20, Height = 20 },
                new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center }
            }
        };
        return button;
    }

    private async Task Browse()
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the Redout 2 installation folder",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            var path = folders[0].TryGetLocalPath();
            if (path is null) { await ShowMessage("The selected folder is not a local filesystem path."); return; }
            Run(() =>
            {
                _manager.SelectGame(path);
                _order.Clear();
                _checks.Clear();
                _selectedMod = null;
                _loadOrderPending = false;
                RefreshRows();
                SetStatus("Game folder selected.");
            });
        }
        catch (Exception ex) { SetStatus(ex.Message); await ShowMessage(ex.Message); }
    }

    private async Task Import()
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import Redout 2 mods",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("PAK and ZIP files") { Patterns = ["*.pak", "*.pak.disabled", "*.zip"] }]
            });
            if (files.Count > 0) await ImportFiles(files);
        }
        catch (Exception ex) { SetStatus(ex.Message); await ShowMessage(ex.Message); }
    }

    private async Task ImportFiles(IEnumerable<IStorageItem> files)
    {
        try
        {
            var paths = files.Select(file => file.TryGetLocalPath()).ToList();
            if (paths.Any(path => path is null))
            {
                await ShowMessage("Only local filesystem files can be imported.");
                return;
            }
            var result = _manager.Import(paths.Select(path => path!));
            RefreshRows();
            SetStatus($"Imported {result.Imported}; already managed: {result.Skipped}; failed: {result.Errors.Count}.");
            if (result.Errors.Count > 0) await ShowMessage(string.Join(Environment.NewLine, result.Errors));
        }
        catch (Exception ex) { SetStatus(ex.Message); await ShowMessage(ex.Message); }
    }

    private async Task Scan()
    {
        _content.IsEnabled = false;
        SetStatus("Scanning managed PAK indexes...");
        try
        {
            var errors = await Task.Run(_manager.Scan);
            RefreshRows();
            SetStatus(errors.Count == 0 ? "PAK scan completed." : $"PAK scan completed with {errors.Count} error(s).");
            if (errors.Count > 0) await ShowMessage(string.Join(Environment.NewLine, errors));
        }
        catch (Exception ex) { SetStatus(ex.Message); await ShowMessage(ex.Message); }
        finally { _content.IsEnabled = true; }
    }

    private void RefreshRows(IReadOnlyDictionary<string, bool>? stateOverrides = null)
    {
        RebuildRows(stateOverrides, _manager.GetRows());
    }

    private void RebuildRows(IReadOnlyDictionary<string, bool>? stateOverrides, IReadOnlyList<ModRow> rows)
    {
        var previousChecks = _checks.ToDictionary(pair => pair.Key, pair => pair.Value.IsChecked == true,
            StringComparer.OrdinalIgnoreCase);
        if (stateOverrides is not null)
            foreach (var (name, enabled) in stateOverrides) previousChecks[name] = enabled;
        var byName = rows.ToDictionary(row => row.FileName, StringComparer.OrdinalIgnoreCase);
        _displayedRows = byName;
        if (_loadOrderPending)
        {
            _order.RemoveAll(name => !byName.ContainsKey(name));
            foreach (var row in rows)
                if (!_order.Contains(row.FileName, StringComparer.OrdinalIgnoreCase)) _order.Add(row.FileName);
            _loadOrderPending = !_order.SequenceEqual(rows.Select(row => row.FileName), StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            _order.Clear();
            _order.AddRange(rows.Select(row => row.FileName));
        }
        if (_selectedMod is not null && !byName.ContainsKey(_selectedMod)) _selectedMod = null;
        _gamePath.Text = _manager.GameDirectory;
        _rows.Children.Clear();
        _checks.Clear();
        foreach (var name in _order)
        {
            var row = byName[name];
            var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("260,150,*"), MinHeight = 32 };
            var modCell = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var select = new RadioButton
            {
                GroupName = "LoadOrderSelection",
                IsChecked = string.Equals(_selectedMod, row.FileName, StringComparison.OrdinalIgnoreCase),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(select, $"Select {row.DisplayName} for load order");
            select.Click += (_, _) => { _selectedMod = row.FileName; UpdateOrderButtons(); };
            modCell.Children.Add(select);
            var check = new CheckBox
            {
                Content = new TextBlock { Text = row.DisplayName, TextTrimming = TextTrimming.CharacterEllipsis },
                IsChecked = previousChecks.TryGetValue(row.FileName, out var enabled) ? enabled : row.Enabled,
                VerticalAlignment = VerticalAlignment.Center
            };
            _checks.Add(row.FileName, check);
            Grid.SetColumn(check, 1);
            modCell.Children.Add(check);
            panel.Children.Add(modCell);
            var status = new TextBlock { Text = row.Status, Margin = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(status, 1);
            panel.Children.Add(status);
            var conflicts = new TextBlock
            {
                Text = row.Conflicts,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = row.HasConflicts ? Brushes.Orange : Foreground
            };
            ToolTip.SetTip(conflicts, row.HasConflicts ? new LazyText(() => row.Details) : row.Details);
            Grid.SetColumn(conflicts, 2);
            panel.Children.Add(conflicts);
            _rows.Children.Add(panel);
        }
        UpdateOrderButtons();
    }

    private void UpdateOrderButtons()
    {
        var index = _selectedMod is null ? -1 : _order.FindIndex(name =>
            name.Equals(_selectedMod, StringComparison.OrdinalIgnoreCase));
        _moveUp.IsEnabled = index > 0;
        _moveDown.IsEnabled = index >= 0 && index < _order.Count - 1;
        _applyOrder.IsEnabled = _loadOrderPending;
        _details.IsEnabled = index >= 0;
        _uninstall.IsEnabled = index >= 0;
    }

    private async Task ShowSelectedDetails()
    {
        if (_selectedMod is not null && _displayedRows.TryGetValue(_selectedMod, out var row))
            await ShowMessage(row.Details);
    }

    private async Task UninstallSelected()
    {
        if (_selectedMod is null || !_displayedRows.TryGetValue(_selectedMod, out var row))
            return;
        if (!await Confirm($"Uninstall {row.DisplayName}? This deletes its managed PAK and signature.", "Uninstall"))
            return;
        Run(() => { _manager.Uninstall(row.FileName); RefreshRows(); SetStatus($"Uninstalled {row.DisplayName}."); });
    }

    private void MoveSelected(int direction)
    {
        var index = _selectedMod is null ? -1 : _order.FindIndex(name =>
            name.Equals(_selectedMod, StringComparison.OrdinalIgnoreCase));
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _order.Count) return;
        (_order[index], _order[target]) = (_order[target], _order[index]);
        var rows = _manager.GetRows();
        _loadOrderPending = !_order.SequenceEqual(rows.Select(row => row.FileName), StringComparer.OrdinalIgnoreCase);
        RebuildRows(null, rows);
        SetStatus(_loadOrderPending ? "Order staged; use Apply Load Order to rename the files." : "Mod order unchanged.");
    }

    private async Task ApplyLoadOrder()
    {
        if (!_loadOrderPending) return;
        if (!await Confirm("Applying this order renames all managed PAKs and their matching signatures with numbered prefixes. " +
            "Existing numbered prefixes will be replaced. Game load precedence has not been verified, " +
            "so the numbering may not control which mod wins. Continue?", "Apply Load Order"))
            return;
        try
        {
            var desired = _checks.ToDictionary(pair => pair.Key, pair => pair.Value.IsChecked == true,
                StringComparer.OrdinalIgnoreCase);
            var renamed = _manager.ApplyLoadOrder(_order);
            var states = desired.ToDictionary(pair => renamed[pair.Key], pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (_selectedMod is not null) _selectedMod = renamed[_selectedMod];
            _loadOrderPending = false;
            RefreshRows(states);
            SetStatus("Renamed managed mods in the selected order. Game load precedence is unverified.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            await ShowMessage(ex.Message);
        }
    }

    private async Task ShowConflictList()
    {
        try
        {
            var report = new TextBox
            {
                Text = _manager.BuildConflictList(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap
            };
            var dialog = new Window
            {
                Title = "Potential archive conflicts",
                Width = 850,
                Height = 600,
                MinWidth = 500,
                MinHeight = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var layout = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(20) };
            layout.Children.Add(report);
            var close = MakeButton("Close", () => dialog.Close());
            Grid.SetRow(close, 1);
            layout.Children.Add(close);
            dialog.Content = layout;
            await dialog.ShowDialog(this);
        }
        catch (Exception ex) { SetStatus(ex.Message); await ShowMessage(ex.Message); }
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            _ = ShowMessage(ex.Message);
        }
    }

    private void Open(string target) => Run(() =>
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("Select the game folder first.");
        _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })
            ?? throw new InvalidOperationException("The desktop could not open the requested item.");
    });

    private void SetStatus(string text) => _status.Text = text;

    private async Task ShowMessage(string text)
    {
        var dialog = CreateDialog(text);
        var ok = MakeButton("OK", () => dialog.Close());
        ((StackPanel)dialog.Content!).Children.Add(ok);
        await dialog.ShowDialog(this);
    }

    private async Task<bool> Confirm(string text, string confirmLabel)
    {
        var dialog = CreateDialog(text);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(MakeButton("Cancel", () => dialog.Close(false)));
        buttons.Children.Add(MakeButton(confirmLabel, () => dialog.Close(true)));
        ((StackPanel)dialog.Content!).Children.Add(buttons);
        return await dialog.ShowDialog<bool>(this);
    }

    private static Window CreateDialog(string text) => new()
    {
        Title = "Redout 2 Mod Manager",
        Width = 650,
        Height = 360,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new ScrollViewer
                {
                    MaxHeight = 240,
                    Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }
                }
            }
        }
    };

    private sealed class LazyText(Func<string> text)
    {
        public override string ToString() => text();
    }
}
