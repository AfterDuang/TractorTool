using System.Security.Cryptography;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Storage;

var fixture = Directory.CreateTempSubdirectory("TractorTool.FolderHistoryChecks-").FullName;
try
{
    var work = NewDirectory("work");
    var app = NewDirectory("app");
    var data = NewDirectory("data");
    var history = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(work)).FullName;
    var folder = Directory.CreateDirectory(Path.Combine(work, "设计图")).FullName;
    var empty = Path.Combine(folder, "空目录");
    Directory.CreateDirectory(empty);
    var file = Path.Combine(folder, "图.dat");
    byte[] bytes = [0, 0xff, 0x0a, 0x0d, 0];
    await File.WriteAllBytesAsync(file, bytes);
    await File.WriteAllTextAsync(Path.Combine(history, "index"), "not part of the work tree");
    await File.WriteAllTextAsync(Path.Combine(work, ".user-settings"), "keep ordinary dot files");

    Assert(HistoryPaths.NormalizeRoot(work, app, data) == Path.GetFullPath(work), "ordinary root rejected");
    Throws<InvalidDataException>(() => HistoryPaths.NormalizeRoot(fixture, app, data));
    Throws<InvalidDataException>(() => HistoryPaths.NormalizeRoot(history, app, data));
    foreach (var path in new[] { "../elsewhere", "folder/../elsewhere", "a//b", "x:stream", ".tractortool-history/index" })
        Throws<InvalidDataException>(() => HistoryPaths.ResolveWorkPath(work, path));

    var contents = await WorkingTree.ScanAsync(work, CancellationToken.None);
    Assert(contents.Select(item => item.Path).SequenceEqual(new[] { ".user-settings", "设计图", "设计图/图.dat", "设计图/空目录" }
        .OrderBy(path => path, StringComparer.Ordinal)), "scanner omitted work or captured its history");
    var capturedFile = contents.Single(item => item.Path == "设计图/图.dat");
    Assert(capturedFile.Length == bytes.Length && capturedFile.Hash == Convert.ToHexString(SHA256.HashData(bytes)), "unexpected file digest");
    Assert(contents.Single(item => item.Path == "设计图/空目录").IsDirectory, "empty directory lost");

    if (OperatingSystem.IsWindows())
    {
        Throws<InvalidDataException>(() => HistoryPaths.ResolveWorkPath(work, ".TRACTORTOOL-HISTORY/index"));
        using var writer = File.Open(file, FileMode.Open, FileAccess.Write, FileShare.Read);
        await ThrowsAsync<IOException>(() => WorkingTree.ScanAsync(work, CancellationToken.None));
    }

    var staging = NewDirectory("staging");
    var stageFolder = Directory.CreateDirectory(Path.Combine(staging, "设计图")).FullName;
    Directory.CreateDirectory(Path.Combine(stageFolder, "空目录"));
    var stageFile = Path.Combine(stageFolder, "图.dat");
    await File.WriteAllBytesAsync(stageFile, bytes);
    var expected = await WorkingTree.ScanAsync(staging, excludeHistory: false, CancellationToken.None);
    var output = Path.Combine(fixture, "export");
    await RestoreTransaction.SaveAsAsync(staging, output, "设计图/图.dat", expected, CancellationToken.None);
    Assert((await File.ReadAllBytesAsync(Path.Combine(output, "设计图", "图.dat"))).SequenceEqual(bytes), "selected file restored with different bytes or path");
    Assert(!Directory.Exists(Path.Combine(output, "设计图", "空目录")), "selecting a file restored an unselected sibling");
    await ThrowsAsync<IOException>(() => RestoreTransaction.SaveAsAsync(staging, output, null, expected, CancellationToken.None));
    Assert((await File.ReadAllBytesAsync(Path.Combine(output, "设计图", "图.dat"))).SequenceEqual(bytes), "existing output changed");

    var fullOutput = Path.Combine(fixture, "full-export");
    await RestoreTransaction.SaveAsAsync(staging, fullOutput, null, expected, CancellationToken.None);
    Assert(Directory.Exists(Path.Combine(fullOutput, "设计图", "空目录")), "full restore lost an empty directory");
    await ThrowsAsync<InvalidDataException>(() => RestoreTransaction.SaveAsAsync(staging, Path.Combine(history, "reserved"), null, expected, CancellationToken.None));
    var cancelledOutput = Path.Combine(fixture, "cancelled");
    await ThrowsAsync<OperationCanceledException>(() => RestoreTransaction.SaveAsAsync(staging, cancelledOutput, null, expected, new(true)));
    Assert(!Directory.Exists(cancelledOutput), "cancelled output was published");

    var changedOutput = Path.Combine(fixture, "changed-export");
    await File.WriteAllBytesAsync(stageFile, new byte[] { 0, 0xff, 0x0a, 0x0d, 1 });
    await ThrowsAsync<IOException>(() => RestoreTransaction.SaveAsAsync(staging, changedOutput, null, expected, CancellationToken.None));
    Assert(!Directory.Exists(changedOutput), "changed staging was accepted as the selected version");
    await File.WriteAllBytesAsync(stageFile, bytes);

    var unsafeOutput = Path.Combine(fixture, "unsafe-export");
    Directory.CreateDirectory(HistoryPaths.HistoryDirectory(staging));
    await ThrowsAsync<InvalidDataException>(() => RestoreTransaction.SaveAsAsync(staging, unsafeOutput, null, expected, CancellationToken.None));
    Assert(!Directory.Exists(unsafeOutput), "reserved staging subtree was published");

    var sameLengthChange = capturedFile with { Hash = new string('0', 64) };
    Assert(HistoryComparison.Compare([capturedFile], [sameLengthChange]).Single().Kind == "修改", "same-length content change lost");
    var originalName = capturedFile with { Path = "case.txt" };
    var newName = originalName with { Path = "CASE.txt" };
    var renamed = HistoryComparison.Compare([originalName], [newName]);
    Assert(renamed.Any(item => item.Path == originalName.Path && item.Kind == "删除") &&
        renamed.Any(item => item.Path == newName.Path && item.Kind == "新增"), "case-only rename did not expose both paths");

    await ServiceChecks.RunAsync(fixture);
    await EngineChecks.RunAsync(new DirectoryInfo(fixture));
    Console.WriteLine("Folder history file checks passed.");
}
finally
{
    Directory.Delete(fixture, recursive: true);
}

string NewDirectory(string name) => Directory.CreateDirectory(Path.Combine(fixture, name)).FullName;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}

static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
