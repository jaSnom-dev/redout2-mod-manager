using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
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
    Check(manager.GetRows().Single(row => row.FileName == "Off.pak").Status == "Disabled", "disabled import");
    Check(File.Exists(Path.Combine(paks, "Alpha.sig")), "signature import");
    var again = manager.Import([zip]);
    Check(again.Imported == 0 && again.Skipped == 3, "idempotent import");

    manager.Apply(new Dictionary<string, bool> { ["Alpha.pak"] = false, ["Off.pak"] = true });
    Check(File.Exists(Path.Combine(paks, "Alpha.pak.disabled")) &&
          File.Exists(Path.Combine(paks, "Alpha.sig.disabled")) &&
          File.Exists(Path.Combine(paks, "Off.pak")), "toggle PAK and signature");
    Check(manager.GetRows().Single(row => row.FileName == "Beta.pak").Conflicts == "None", "inactive conflict exclusion");

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
    reloaded.ApplyLoadOrder(["003_Alpha.pak", "001_Beta.pak", "002_Gamma.pak", "004_Unknown.pak"]);
    Check(File.Exists(Path.Combine(paks, "001_Alpha.pak")) && File.Exists(Path.Combine(paks, "002_Beta.pak")) &&
          File.Exists(Path.Combine(paks, "002_Beta.sig")) && File.Exists(Path.Combine(legacy, "003_Gamma.pak")),
        "repeated ordering replaces existing prefixes");
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
        }
        catch (Exception ex) { failure = ex; }
    });
    if (OperatingSystem.IsWindows()) thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new Exception("UI integration checks failed.", failure);
}
