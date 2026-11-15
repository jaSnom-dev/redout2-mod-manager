using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Redout2ModManager;
using Redout2ModManager.Unix;

var root = Path.Combine(Path.GetTempPath(), "RedoutUnixTests-" + Guid.NewGuid().ToString("N"));
var paks = Path.Combine(root, "game", "Redout2", "Content", "Paks");
var settings = Path.Combine(root, "settings");
Directory.CreateDirectory(paks);
try
{
    var manager = new ModManager(settings);
    manager.SelectGame(Path.Combine(root, "game"));
    var zip = Path.Combine(root, "mods.zip");
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
    {
        Add(archive, "first/Alpha.pak", "alpha");
        Add(archive, "first/Alpha.sig", "signature");
        Add(archive, "first/Alpha.pak.list", "Content/Car.uasset\n");
        Add(archive, "second/Beta.pak", "beta");
        Add(archive, "second/Beta.pak.list", "content\\car.uasset\n");
        Add(archive, "third/Off.pak.disabled", "off");
    }

    var imported = manager.Import([zip]);
    Check(imported.Imported == 3 && imported.Errors.Count == 0, "nested ZIP import");
    Check(manager.GetRows().Single(row => row.FileName == "Alpha.pak").Conflicts == "1 file(s) with Beta.pak", "asset-list conflict names archive");
    var report = manager.BuildConflictList();
    Check(report.Contains("Archive: Alpha.pak") && report.Contains("Conflicts with: Beta.pak") &&
          report.Contains("Archive: Beta.pak") && report.Contains("Conflicts with: Alpha.pak") &&
          !report.Contains("Archive: Off.pak"), "complete conflict report excludes disabled mods");
    var alphaRow = manager.GetRows().Single(row => row.FileName == "Alpha.pak");
    Check(alphaRow.HasConflicts && alphaRow.Details == "Content/Car.uasset (also in Beta.pak)",
        "on-demand conflict details keep full overlap text");
    var pendingAlphaRow = manager.GetRows().Single(row => row.FileName == "Alpha.pak");
    Check(manager.GetRows().Single(row => row.FileName == "Off.pak").Status == "Disabled", "disabled import");
    Check(File.Exists(Path.Combine(paks, "Alpha.sig")), "signature import");
    var again = manager.Import([zip]);
    Check(again.Imported == 0 && again.Skipped == 3, "idempotent import");

    manager.Apply(new Dictionary<string, bool> { ["Alpha.pak"] = false, ["Off.pak"] = true });
    Check(File.Exists(Path.Combine(paks, "Alpha.pak.disabled")) &&
          File.Exists(Path.Combine(paks, "Alpha.sig.disabled")) &&
          File.Exists(Path.Combine(paks, "Off.pak")), "toggle PAK and signature");
    Check(manager.GetRows().Single(row => row.FileName == "Beta.pak").Conflicts == "None", "inactive conflict exclusion");
    Check(pendingAlphaRow.Details == "Content/Car.uasset (also in Beta.pak)" &&
          manager.GetRows().Single(row => row.FileName == "Alpha.pak").Details == "This mod is disabled and excluded from conflict detection.",
        "unevaluated row details retain their original snapshot");

    var restored = new ModManager(settings);
    Check(restored.FindGame() == Path.Combine(root, "game"), "saved game selection");
    restored.SelectGame(Path.Combine(root, "game"));
    Check(restored.GetRows().Count == 3, "manifest persistence");
    var scanErrors = restored.Scan();
    Check(scanErrors.Count == 3, "invalid PAK scan errors surfaced");
    Check(restored.GetRows().Single(row => row.FileName == "Off.pak").Conflicts == "Not scanned", "failed scan remains unscanned");

    var duplicate = Path.Combine(root, "duplicate.zip");
    using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
    {
        Add(archive, "one/New.pak", "first");
        Add(archive, "two/new.pak", "second");
    }
    var duplicateResult = restored.Import([duplicate]);
    Check(duplicateResult.Errors.Count == 1 && !File.Exists(Path.Combine(paks, "New.pak")), "duplicate archive rollback");
    restored.Uninstall("Alpha.pak");
    Check(!File.Exists(Path.Combine(paks, "Alpha.pak.disabled")) &&
          !File.Exists(Path.Combine(paks, "Alpha.sig.disabled")), "uninstall");
    TestConflictEdges(root);
    TestLoadOrder(root);
    if (args.Contains("--ui")) TestUi(root);
    var pakPath = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
    if (pakPath is not null)
        Check(PakArchiveScanner.Scan(pakPath).Count > 0, "real PAK index scan");
    Console.WriteLine("All Unix mod manager integration checks passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

static void Add(ZipArchive archive, string name, string content)
{
    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
    writer.Write(content);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception("Failed: " + description);
    Console.WriteLine("PASS " + description);
}

static void TestConflictEdges(string root)
{
    var game = Path.Combine(root, "edge-game");
    var paks = Path.Combine(game, "Content", "Paks");
    var source = Path.Combine(root, "edge-source");
    Directory.CreateDirectory(paks);
    Directory.CreateDirectory(source);
    var lists = new (string Name, string? Paths)[]
    {
        ("A", "Content/Zed.uasset\nContent/Shared.uasset\nContent/OnlyA.uasset\n"),
        ("B", "content/shared.UASSET\nContent/Zed.uasset\n"),
        ("C", "CONTENT/SHARED.uasset\n"),
        ("D", null),
        ("E", null)
    };
    foreach (var (name, paths) in lists)
    {
        File.WriteAllText(Path.Combine(source, name + ".pak"), name);
        if (paths is not null) File.WriteAllText(Path.Combine(source, name + ".pak.list"), paths);
    }
    var manager = new ModManager(Path.Combine(root, "edge-settings"));
    manager.SelectGame(game);
    var imported = manager.Import(lists.Select(item => Path.Combine(source, item.Name + ".pak")));
    Check(imported.Imported == 5 && imported.Errors.Count == 0, "edge conflict import");
    var modsField = typeof(ModManager).GetField("_mods", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var mods = (System.Collections.IList)modsField.GetValue(manager)!;
    var entry = mods.Cast<object>().Single(mod => (string)mod.GetType().GetProperty("FileName")!.GetValue(mod)! == "E.pak");
    var entryType = entry.GetType();
    var info = new FileInfo(Path.Combine(paks, "E.pak"));
    entryType.GetProperty("HasPakScan")!.SetValue(entry, true);
    entryType.GetProperty("ScannedAssetPaths")!.SetValue(entry, new List<string> { "Content/Shared.uasset" });
    entryType.GetProperty("ScannedPakLength")!.SetValue(entry, info.Length);
    entryType.GetProperty("ScannedPakLastWriteTimeUtcTicks")!.SetValue(entry, info.LastWriteTimeUtc.Ticks);

    var nl = Environment.NewLine;
    ModRow Row(string name) => manager.GetRows().Single(row => row.FileName == name);
    var a = Row("A.pak");
    var b = Row("B.pak");
    Check(a.Conflicts == "2 file(s) with B.pak; 1 file(s) with C.pak; 1 file(s) with E.pak" &&
          b.Conflicts == "2 file(s) with A.pak; 1 file(s) with C.pak; 1 file(s) with E.pak" &&
          Row("C.pak").Conflicts == "1 file(s) with A.pak; 1 file(s) with B.pak; 1 file(s) with E.pak" &&
          Row("D.pak").Conflicts == "Not scanned",
        "case-insensitive overlap counts across list and scan sources");
    Check(a.Details == "Content/Shared.uasset (also in B.pak, C.pak, E.pak)" + nl + "Content/Zed.uasset (also in B.pak)" &&
          b.Details == "content/shared.UASSET (also in A.pak, C.pak, E.pak)" + nl + "Content/Zed.uasset (also in A.pak)",
        "row details follow each mod's own path order and spelling");
    var report = manager.BuildConflictList();
    Check(report.Contains("Archive: A.pak" + nl + "  Content/Shared.uasset" + nl + "    Conflicts with: B.pak, C.pak, E.pak" + nl +
              "  Content/Zed.uasset" + nl + "    Conflicts with: B.pak" + nl + nl + "Archive: B.pak" + nl) &&
          report.Contains("Archive: E.pak" + nl + "  Content/Shared.uasset" + nl + "    Conflicts with: A.pak, B.pak, C.pak" + nl) &&
          report.EndsWith("Not scanned (conflicts are unknown):" + nl + "  D.pak" + nl),
        "complete report ordering and unscanned section");

    File.Delete(Path.Combine(paks, "B.pak"));
    File.WriteAllText(Path.Combine(paks, "E.pak"), "changed outside the manager");
    a = Row("A.pak");
    report = manager.BuildConflictList();
    Check(a.Conflicts == "1 file(s) with C.pak" && Row("E.pak").Conflicts == "Not scanned" &&
          a.Details == "Content/Shared.uasset (also in C.pak)" &&
          !report.Contains("Archive: B.pak") && !report.Contains("Archive: E.pak") &&
          report.EndsWith("Not scanned (conflicts are unknown):" + nl + "  D.pak" + nl + "  E.pak" + nl),
        "external file changes and stale scans are reflected immediately");

    var canonical = new List<string> { "A/y", "b/x", "c/d" };
    Check(ReferenceEquals(PakAssetListReader.NormalizeSortedPaths(canonical), canonical),
        "canonical manifest paths are reused");
    Check(PakAssetListReader.NormalizeSortedPaths(["c/d", "a\\Y", "./b//x/", "A/y", "b/x"])
              .SequenceEqual(["a/Y", "b/x", "c/d"]) &&
          PakAssetListReader.NormalizeSortedPaths(["a", "A"]).SequenceEqual(["a"]) &&
          PakAssetListReader.NormalizePath("a/./b") == "a/b",
        "non-canonical manifest paths are still normalized, de-duplicated and sorted");
    foreach (var unsafePath in new[] { "../x", "a/../b", "/abs", "a:b", "a\0b", "." })
    {
        try { PakAssetListReader.NormalizePath(unsafePath); Check(false, "unsafe path rejected: " + unsafePath); }
        catch (InvalidDataException) { }
    }
    Check(true, "unsafe asset paths are rejected");
}

static void TestLoadOrder(string root)
{
    var game = Path.Combine(root, "order-game");
    var paks = Path.Combine(game, "Content", "Paks");
    var settings = Path.Combine(root, "order-settings");
    var source = Path.Combine(root, "order-sources");
    Directory.CreateDirectory(paks);
    Directory.CreateDirectory(source);
    foreach (var name in new[] { "Alpha.pak", "Alpha.sig", "Beta.pak", "Beta.sig", "Gamma.pak", "Unknown.pak" })
        File.WriteAllText(Path.Combine(source, name), name);
    File.WriteAllText(Path.Combine(source, "Alpha.pak.list"), "Content/Car.uasset\n");
    File.WriteAllText(Path.Combine(source, "Beta.pak.list"), "content/car.uasset\n");
    var manager = new ModManager(settings);
    manager.SelectGame(game);
    var imported = manager.Import([Path.Combine(source, "Alpha.pak"), Path.Combine(source, "Beta.pak"),
        Path.Combine(source, "Gamma.pak"), Path.Combine(source, "Unknown.pak")]);
    Check(imported.Imported == 4 && imported.Errors.Count == 0, "load-order fixtures imported");
    Check(manager.BuildConflictList().Contains("Not scanned (conflicts are unknown):") &&
          manager.BuildConflictList().Contains("Unknown.pak"), "conflict report lists unscanned archives");
    manager.Apply(new Dictionary<string, bool> { ["Beta.pak"] = false, ["Gamma.pak"] = false });
    var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.GetFullPath(paks).TrimEnd(Path.DirectorySeparatorChar))))[..16];
    var legacy = Path.Combine(settings, "DisabledMods", key);
    Directory.CreateDirectory(legacy);
    File.Move(Path.Combine(paks, "Gamma.pak.disabled"), Path.Combine(legacy, "Gamma.pak"));
    var order = new[] { "Beta.pak", "Gamma.pak", "Alpha.pak", "Unknown.pak" };
    var manifest = Directory.GetFiles(settings, "*-mods.json").Single();
    var before = File.ReadAllText(manifest);

    File.WriteAllText(Path.Combine(paks, "001_Beta.sig"), "unmanaged");
    ExpectFailure(() => manager.ApplyLoadOrder(order), "signature collision is rejected");
    Check(File.ReadAllText(manifest) == before && File.Exists(Path.Combine(paks, "Beta.pak.disabled")) &&
          File.ReadAllText(Path.Combine(paks, "001_Beta.sig")) == "unmanaged", "collision leaves files and manifest unchanged");
    File.Delete(Path.Combine(paks, "001_Beta.sig"));

    File.WriteAllText(Path.Combine(paks, "Unknown.sig"), "unregistered");
    ExpectFailure(() => manager.ApplyLoadOrder(order), "unregistered matching signature is rejected");
    Check(File.Exists(Path.Combine(paks, "Unknown.pak")) && File.Exists(Path.Combine(paks, "Unknown.sig")),
        "unregistered signature is not orphaned");
    File.Delete(Path.Combine(paks, "Unknown.sig"));

    Directory.CreateDirectory(manifest + ".tmp");
    ExpectFailure(() => manager.ApplyLoadOrder(order), "manifest write failure is surfaced");
    Check(File.ReadAllText(manifest) == before && File.Exists(Path.Combine(paks, "Alpha.pak")) &&
          File.Exists(Path.Combine(paks, "Alpha.sig")) && File.Exists(Path.Combine(paks, "Beta.pak.disabled")) &&
          File.Exists(Path.Combine(paks, "Beta.sig.disabled")) && File.Exists(Path.Combine(legacy, "Gamma.pak")) &&
          !Directory.EnumerateFiles(paks, ".redout2-order-*.tmp").Any(), "failed rename rolls back files and manifest");
    Directory.Delete(manifest + ".tmp");

    var renamed = manager.ApplyLoadOrder(order);
    Check(renamed["Beta.pak"] == "001_Beta.pak" && renamed["Gamma.pak"] == "002_Gamma.pak" &&
          renamed["Alpha.pak"] == "003_Alpha.pak" && renamed["Unknown.pak"] == "004_Unknown.pak",
        "numbered load order mapping");
    Check(File.Exists(Path.Combine(paks, "001_Beta.pak.disabled")) &&
          File.Exists(Path.Combine(paks, "001_Beta.sig.disabled")) &&
          File.Exists(Path.Combine(legacy, "002_Gamma.pak")) &&
          File.Exists(Path.Combine(paks, "003_Alpha.pak")) && File.Exists(Path.Combine(paks, "003_Alpha.sig")) &&
          File.Exists(Path.Combine(source, "Alpha.pak.list")), "renames preserve file states, signatures, and sources");
    var reloaded = new ModManager(settings);
    reloaded.SelectGame(game);
    Check(reloaded.GetRows().Select(row => row.FileName).SequenceEqual(
        new[] { "001_Beta.pak", "002_Gamma.pak", "003_Alpha.pak", "004_Unknown.pak" }),
        "numbered order persists in manifest");
    reloaded.Apply(new Dictionary<string, bool> { ["001_Beta.pak"] = true });
    Check(reloaded.GetRows().Single(row => row.FileName == "003_Alpha.pak").Conflicts ==
        "1 file(s) with 001_Beta.pak", "conflict metadata follows renamed archives");
    var beforeReorder = reloaded.GetRows().Single(row => row.FileName == "001_Beta.pak");
    reloaded.ApplyLoadOrder(["003_Alpha.pak", "001_Beta.pak", "002_Gamma.pak", "004_Unknown.pak"]);
    Check(File.Exists(Path.Combine(paks, "001_Alpha.pak")) && File.Exists(Path.Combine(paks, "002_Beta.pak")) &&
          File.Exists(Path.Combine(paks, "002_Beta.sig")) && File.Exists(Path.Combine(legacy, "003_Gamma.pak")),
        "repeated ordering replaces existing prefixes");
    Check(beforeReorder.Details == "content/car.uasset (also in 003_Alpha.pak)",
        "lazy details retain original archive names after reorder");
    Check(reloaded.GetRows().Single(row => row.FileName == "002_Beta.pak").Details ==
        "content/car.uasset (also in 001_Alpha.pak)", "fresh details use renamed archive names");
}

static void ExpectFailure(Action action, string description)
{
    try { action(); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.WriteLine("PASS " + description);
        return;
    }
    throw new Exception("Failed: " + description);
}

static void TestUi(string root)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().SetupWithoutStarting();
            var game = Path.Combine(root, "ui-game");
            var paks = Path.Combine(game, "Content", "Paks");
            var source = Path.Combine(root, "ui-source");
            Directory.CreateDirectory(paks);
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Alpha.pak"), "alpha");
            File.WriteAllText(Path.Combine(source, "Beta.pak"), "beta");
            var manager = new ModManager(Path.Combine(root, "ui-settings"));
            manager.SelectGame(game);
            Check(manager.Import([Path.Combine(source, "Alpha.pak"), Path.Combine(source, "Beta.pak")]).Imported == 2,
                "UI fixtures imported");
            var window = new MainWindow(manager);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = typeof(MainWindow);
            var rows = (StackPanel)type.GetField("_rows", flags)!.GetValue(window)!;
            var checks = (Dictionary<string, CheckBox>)type.GetField("_checks", flags)!.GetValue(window)!;
            var apply = (Button)type.GetField("_applyOrder", flags)!.GetValue(window)!;
            var up = (Button)type.GetField("_moveUp", flags)!.GetValue(window)!;
            var down = (Button)type.GetField("_moveDown", flags)!.GetValue(window)!;
            checks["Beta.pak"].IsChecked = false;
            var select = (RadioButton)((Grid)rows.Children[1]).Children[0];
            select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            checks = (Dictionary<string, CheckBox>)type.GetField("_checks", flags)!.GetValue(window)!;
            var order = (List<string>)type.GetField("_order", flags)!.GetValue(window)!;
            Check(order.SequenceEqual(new[] { "Beta.pak", "Alpha.pak" }) && apply.IsEnabled &&
                  checks["Beta.pak"].IsChecked == false && File.Exists(Path.Combine(paks, "Beta.pak")) &&
                  !File.Exists(Path.Combine(paks, "001_Beta.pak")), "UI move stages order without renaming and preserves checkboxes");
            type.GetMethod("RefreshRows", flags)!.Invoke(window, [null]);
            checks = (Dictionary<string, CheckBox>)type.GetField("_checks", flags)!.GetValue(window)!;
            Check(order.SequenceEqual(new[] { "Beta.pak", "Alpha.pak" }) && checks["Beta.pak"].IsChecked == false,
                "UI refresh preserves staged order and checkbox state");
            down.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(!apply.IsEnabled && order.SequenceEqual(new[] { "Alpha.pak", "Beta.pak" }),
                "UI reversing a move clears pending order");
            up.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            var dialog = window.OwnedWindows.Single();
            var confirmButtons = (StackPanel)((StackPanel)dialog.Content!).Children[1];
            ((Button)confirmButtons.Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(apply.IsEnabled && File.Exists(Path.Combine(paks, "Beta.pak")) &&
                  !File.Exists(Path.Combine(paks, "001_Beta.pak")), "cancelling confirmation leaves order staged");
            apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            dialog = window.OwnedWindows.Single();
            confirmButtons = (StackPanel)((StackPanel)dialog.Content!).Children[1];
            ((Button)confirmButtons.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            checks = (Dictionary<string, CheckBox>)type.GetField("_checks", flags)!.GetValue(window)!;
            Check(!apply.IsEnabled && File.Exists(Path.Combine(paks, "001_Beta.pak")) &&
                  File.Exists(Path.Combine(paks, "002_Alpha.pak")) && checks["001_Beta.pak"].IsChecked == false,
                "confirming applies order and preserves pending checkbox state");
            var reportTask = (Task)type.GetMethod("ShowConflictList", flags)!.Invoke(window, [])!;
            Dispatcher.UIThread.RunJobs();
            dialog = window.OwnedWindows.Single();
            var reportLayout = (Grid)dialog.Content!;
            var report = (TextBox)reportLayout.Children[0];
            Check(report.IsReadOnly && report.Text!.Contains("Not scanned (conflicts are unknown):") &&
                  report.Text.Contains("001_Beta.pak"), "UI conflict report is copyable and lists archives");
            ((Button)reportLayout.Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Check(reportTask.IsCompletedSuccessfully, "UI conflict report closes");
            window.Close();

            var tipGame = Path.Combine(root, "ui-tip-game");
            var tipSource = Path.Combine(root, "ui-tip-source");
            Directory.CreateDirectory(Path.Combine(tipGame, "Content", "Paks"));
            Directory.CreateDirectory(tipSource);
            foreach (var name in new[] { "Alpha", "Beta" })
            {
                File.WriteAllText(Path.Combine(tipSource, name + ".pak"), name);
                File.WriteAllText(Path.Combine(tipSource, name + ".pak.list"), "Content/Car.uasset\n");
            }
            var tipManager = new ModManager(Path.Combine(root, "ui-tip-settings"));
            tipManager.SelectGame(tipGame);
            tipManager.Import([Path.Combine(tipSource, "Alpha.pak"), Path.Combine(tipSource, "Beta.pak")]);
            var tipWindow = new MainWindow(tipManager);
            tipWindow.Show();
            Dispatcher.UIThread.RunJobs();
            var tipRows = (StackPanel)type.GetField("_rows", flags)!.GetValue(tipWindow)!;
            var tipSelect = (RadioButton)((Grid)tipRows.Children[0]).Children[0];
            tipSelect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ((Button)type.GetField("_moveDown", flags)!.GetValue(tipWindow)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var tipConflicts = (TextBlock)((Grid)tipRows.Children[1]).Children[3];
            Check(tipConflicts.Text == "1 file(s) with Beta.pak", "UI move rebuild keeps conflict text");
            ToolTip.SetIsOpen(tipConflicts, true);
            Dispatcher.UIThread.RunJobs();
            var tooltip = tipConflicts.GetValue(ToolTipDiagnostics.ToolTipProperty);
            Check(tooltip is Visual tooltipVisual && tooltipVisual.GetVisualDescendants().OfType<TextBlock>()
                    .Any(text => text.Text == "Content/Car.uasset (also in Beta.pak)"),
                "conflict tooltip renders full lazy details");
            ToolTip.SetIsOpen(tipConflicts, false);
            tipWindow.Close();
        }
        catch (Exception ex) { failure = ex; }
    });
    if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new Exception("UI integration checks failed.", failure);
}
