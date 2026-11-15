using System.IO.Compression;
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
    Check(manager.GetRows().Single(row => row.FileName == "Alpha.pak").Conflicts == "1 asset(s)", "asset-list conflict");
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
    if (args.Length > 0)
        Check(PakArchiveScanner.Scan(args[0]).Count > 0, "real PAK index scan");
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
