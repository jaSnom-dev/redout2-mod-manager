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
    private readonly TextBlock _gamePath = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _rows = new() { Spacing = 5 };
    private readonly Dictionary<string, CheckBox> _checks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Grid _content = new();
    private readonly List<string> _order = [];
    private readonly Button _moveUp;
    private readonly Button _moveDown;
    private readonly Button _applyOrder;
    private string? _selectedMod;
    private bool _loadOrderPending;

    public MainWindow() : this(new ModManager()) { }

    internal MainWindow(ModManager manager)
    {
        _manager = manager;
        var version = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
            .InformationalVersion.Split('+')[0];
        Title = $"Redout 2 Mod Manager v{version}";
        Width = 1000;
        Height = 700;
        MinWidth = 720;
        MinHeight = 500;
        Background = Brush.Parse("#080A11");
        Foreground = Brush.Parse("#F4F6FF");

        _content.RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto,Auto");
        _content.Margin = new Thickness(20);

        var heading = new StackPanel { Spacing = 6 };
        heading.Children.Add(new TextBlock { Text = Title, FontSize = 24, FontWeight = FontWeight.Bold });
        heading.Children.Add(new TextBlock { Text = "Manage PAK mods without changing unregistered game files." });
        Add(heading, 0);

        var gamePanel = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        gamePanel.Children.Add(_gamePath);
        var browse = MakeButton("Browse...", async () => await Browse());
        Grid.SetColumn(browse, 1);
        gamePanel.Children.Add(browse);
        Add(gamePanel, 1);

        var listPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        listPanel.Children.Add(new TextBlock { Text = "Select a mod to change its order. Checkboxes control enabled state.", FontWeight = FontWeight.Bold });
        var scroll = new ScrollViewer { Content = _rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1);
        listPanel.Children.Add(scroll);
        Add(listPanel, 2);

        var actions = new WrapPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(MakeButton("Import Mods...", async () => await Import()));
        actions.Children.Add(MakeButton("Refresh Mod List", () => Run(() => { _manager.Refresh(); RefreshRows(); SetStatus("Mod list refreshed."); })));
        actions.Children.Add(MakeButton("Toggle All", () =>
        {
            var enable = _checks.Values.Any(check => check.IsChecked != true);
            foreach (var check in _checks.Values) check.IsChecked = enable;
        }));
        actions.Children.Add(MakeButton("Apply Changes", () => Run(() =>
        {
            _manager.Apply(_checks.ToDictionary(pair => pair.Key, pair => pair.Value.IsChecked == true, StringComparer.OrdinalIgnoreCase));
            RefreshRows();
            SetStatus("Mod changes applied.");
        })));
        actions.Children.Add(MakeButton("Scan PAKs", async () => await Scan()));
        actions.Children.Add(MakeButton("Conflict List", async () => await ShowConflictList()));
        _moveUp = MakeButton("Move Up", () => MoveSelected(-1));
        _moveDown = MakeButton("Move Down", () => MoveSelected(1));
        _applyOrder = MakeButton("Apply Load Order", async () => await ApplyLoadOrder());
        actions.Children.Add(_moveUp);
        actions.Children.Add(_moveDown);
        actions.Children.Add(_applyOrder);
        actions.Children.Add(MakeButton("Mod Folder", () => Open(_manager.PaksDirectory)));
        actions.Children.Add(MakeLinkButton("Start Game", "redout2-steam.jpg", () => Open("steam://rungameid/1799930")));
        actions.Children.Add(MakeLinkButton("Nexus Mods", "nexus-mods.png", () => Open("https://www.nexusmods.com/redout2")));
        actions.Children.Add(MakeLinkButton("GameBanana", "gamebanana.png", () => Open("https://gamebanana.com/games/24088")));
        actions.Children.Add(MakeLinkButton("Discord", "discord-icon.png", () => Open("https://discord.com/invite/redout")));
        Add(actions, 3);
        Add(_status, 4);
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
        var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 6) };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button MakeLinkButton(string text, string icon, Action action)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream($"Redout2ModManager.Assets.{icon}")
            ?? throw new InvalidOperationException($"Missing embedded icon: {icon}");
        var button = MakeButton(text, action);
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
        var previousChecks = _checks.ToDictionary(pair => pair.Key, pair => pair.Value.IsChecked == true,
            StringComparer.OrdinalIgnoreCase);
        if (stateOverrides is not null)
            foreach (var (name, enabled) in stateOverrides) previousChecks[name] = enabled;
        var rows = _manager.GetRows();
        var byName = rows.ToDictionary(row => row.FileName, StringComparer.OrdinalIgnoreCase);
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
            var panel = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,220,130,320,Auto,Auto") };
            var select = new RadioButton
            {
                GroupName = "LoadOrderSelection",
                IsChecked = string.Equals(_selectedMod, row.FileName, StringComparison.OrdinalIgnoreCase),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(select, $"Select {row.DisplayName} for load order");
            select.Click += (_, _) => { _selectedMod = row.FileName; UpdateOrderButtons(); };
            panel.Children.Add(select);
            var check = new CheckBox
            {
                Content = row.DisplayName,
                IsChecked = previousChecks.TryGetValue(row.FileName, out var enabled) ? enabled : row.Enabled,
                VerticalAlignment = VerticalAlignment.Center
            };
            _checks.Add(row.FileName, check);
            Grid.SetColumn(check, 1);
            panel.Children.Add(check);
            var status = new TextBlock { Text = row.Status, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(status, 2);
            panel.Children.Add(status);
            var conflicts = new TextBlock
            {
                Text = row.Conflicts,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = row.HasConflicts ? Brushes.Orange : Foreground
            };
            ToolTip.SetTip(conflicts, row.Details);
            Grid.SetColumn(conflicts, 3);
            panel.Children.Add(conflicts);
            var details = MakeButton("Details", async () => await ShowMessage(row.Details));
            Grid.SetColumn(details, 4);
            panel.Children.Add(details);
            var uninstall = MakeButton("Uninstall", async () =>
            {
                if (!await Confirm($"Uninstall {row.DisplayName}? This deletes its managed PAK and signature.", "Uninstall"))
                    return;
                Run(() => { _manager.Uninstall(row.FileName); RefreshRows(); SetStatus($"Uninstalled {row.DisplayName}."); });
            });
            Grid.SetColumn(uninstall, 5);
            panel.Children.Add(uninstall);
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
    }

    private void MoveSelected(int direction)
    {
        var index = _selectedMod is null ? -1 : _order.FindIndex(name =>
            name.Equals(_selectedMod, StringComparison.OrdinalIgnoreCase));
        var target = index + direction;
        if (index < 0 || target < 0 || target >= _order.Count) return;
        (_order[index], _order[target]) = (_order[target], _order[index]);
        _loadOrderPending = !_order.SequenceEqual(_manager.GetRows().Select(row => row.FileName), StringComparer.OrdinalIgnoreCase);
        RefreshRows();
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
}
