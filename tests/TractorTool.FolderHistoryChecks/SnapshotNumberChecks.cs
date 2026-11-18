using System.Text.Json;
using System.Text.Json.Nodes;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Services;
using TractorTool.Plugins.FolderHistory.Storage;
using TractorTool.Plugins.FolderHistory.ViewModels;

internal static class SnapshotNumberChecks
{
    public static void Run(string fixture)
    {
        CheckCapacity(fixture);
        foreach (var schema in new[] { 1, 2 })
        {
            var root = Directory.CreateDirectory(Path.Combine(fixture, $"numbers-v{schema}")).FullName;
            var library = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(root)).FullName;
            var file = Path.Combine(library, "history.json");
            var legacy = JsonSerializer.SerializeToNode(new
            {
                schemaVersion = schema, id = Guid.NewGuid(), engine = "Duplicati",
                versions = new[] { "a", "b", "c" }.Select(id => new { id, sourceRoot = root, note = id, pinned = id == "a" })
            })!.AsObject();
            if (schema == 2)
            {
                legacy["protectionEnabled"] = false;
                legacy["settings"] = JsonSerializer.SerializeToNode(new { stableSeconds = 5, minimumIntervalSeconds = 60,
                    keepLatestVersions = 0, keepDays = 0, excludedPaths = Array.Empty<string>() });
                legacy["pendingDeleteIds"] = new JsonArray();
                legacy["pendingDeleteAll"] = false;
                foreach (var version in legacy["versions"]!.AsArray()) version!["restoreProtection"] = false;
            }
            var source = JsonSerializer.SerializeToUtf8Bytes(legacy);
            File.WriteAllBytes(file, source);

            var state = HistoryStore.Read(root);
            Require(state.Versions.Select(v => v.Number).SequenceEqual([1L, 2, 3]) && File.ReadAllBytes(file).SequenceEqual(source),
                "browsing must number legacy receipts in insertion order without changing the library");
            state.Versions.RemoveAt(1);
            HistoryStore.Save(root, state);
            state = HistoryStore.Read(root);
            Require(state.SchemaVersion == 3 && state.Versions.Select(v => v.Number).SequenceEqual([1L, 3]) &&
                state.Versions[0] is { Id: "a", Pinned: true, Note: "a" }, "cleanup renumbered a surviving snapshot or lost its note");

            var duplicated = JsonNode.Parse(File.ReadAllBytes(file))!;
            duplicated["versions"]![1]!["number"] = 1;
            File.WriteAllText(file, duplicated.ToJsonString());
            try { HistoryStore.Read(root); throw new InvalidOperationException("duplicate snapshot numbers were accepted"); }
            catch (InvalidDataException) { }
            state.Versions.Clear();
            HistoryStore.Save(root, state);
            state = HistoryStore.Read(root);
            Require(state.AddVersion("d", root, "", false).Number == 4, "clearing history reused a snapshot number");
            HistoryStore.Save(root, state);
            Require(HistoryStore.Read(root).Versions.Single().Number == 4, "new snapshot number did not survive reopening");
        }

        var time = DateTimeOffset.UnixEpoch;
        SavedVersion[] saved = [new(new("first", time, fixture, []), "", false, 1),
            new(new("second", time.AddHours(-1), fixture, []), "", false, 2)];
        Require(new HistoryWorkspace("clock", saved).Versions[0].Id == "second" &&
            HistoryRetention.Candidates(saved, new() { KeepLatestVersions = 1 }, time).Single().Id == "first",
            "a clock change reordered history or scheduled the newest snapshot for cleanup");
    }

    private static void CheckCapacity(string fixture)
    {
        const int oldLimit = 8 * 1024 * 1024;
        const int newLimit = 2 * oldLimit;
        var root = Directory.CreateDirectory(Path.Combine(fixture, "numbers-capacity")).FullName;
        var library = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(root)).FullName;
        var file = Path.Combine(library, "history.json");
        var id = Guid.NewGuid();
        var note = new string('x', 500);
        string Volume(int index) => $"duplicati-{DateTime.UnixEpoch.AddSeconds(index):yyyyMMddTHHmmssZ}.dlist.zip";
        byte[] Legacy(int count) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, id, engine = "Duplicati",
            versions = Enumerable.Range(1, count).Select(i => new { id = Volume(i), sourceRoot = root, note, pinned = false })
        });
        var firstSize = Legacy(1).Length;
        var receiptSize = Legacy(2).Length - firstSize;
        var oldCount = 1 + (oldLimit - firstSize) / receiptSize;
        File.WriteAllBytes(file, Legacy(oldCount));

        var state = HistoryStore.Read(root);
        HistoryStore.Save(root, state);
        var migratedSize = new FileInfo(file).Length;
        Require(migratedSize > oldLimit && HistoryStore.Read(root).LastSnapshotNumber == oldCount,
            "a valid near-limit compact legacy file could not migrate with its new fields");

        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var rowSize = JsonSerializer.SerializeToUtf8Bytes(state.Versions[^1], web).Length;
        // Allow the comma and additional counter digits when estimating how many rows fit.
        var extra = (newLimit - migratedSize - 4) / (rowSize + 4);
        for (var i = 1; i <= extra; i++) state.AddVersion(Volume(oldCount + i), root, note, false);
        HistoryStore.Save(root, state);
        state.PendingDeleteIds = state.Versions.Select(v => v.Id).ToList();
        state.PendingDeleteAll = true;
        HistoryStore.Save(root, state);
        Require(new FileInfo(file).Length > newLimit && HistoryStore.Read(root).PendingDeleteIds.Count == state.Versions.Count,
            "a full repository could not persist the IDs needed to start and resume cleanup");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
