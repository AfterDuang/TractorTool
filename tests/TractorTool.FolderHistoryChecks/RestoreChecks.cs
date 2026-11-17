using System.Text.Json;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Storage;

internal static class RestoreChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static async Task RunAsync(string fixture)
    {
        var root = CreateRoot(fixture, "restore-normal");
        Write(root, "replace.txt", "current");
        Write(root, "new.txt", "remove this new file");
        Write(root, "folder-to-file/child.txt", "current child");
        Write(root, "file-to-folder", "current file");
        Write(root, "CaseDir/Case.TXT", "case sensitive bytes");
        Write(root, "keep/excluded.bin", "never change or delete this");
        var stage = Directory.CreateDirectory(Path.Combine(HistoryPaths.HistoryDirectory(root), "temp", "target")).FullName;
        Write(stage, "replace.txt", "old");
        Write(stage, "folder-to-file", "old file");
        Write(stage, "file-to-folder/child.txt", "old child");
        Write(stage, "casedir/case.txt", "case sensitive bytes");
        Directory.CreateDirectory(Path.Combine(stage, "empty"));
        var target = await VersionAsync(root, stage, "duplicati-20261007T120000Z.dlist.zip");
        var protection = await VersionAsync(root, root, "duplicati-20261007T130000Z.dlist.zip");
        var rules = HistoryExclusions.Normalize(["keep/excluded.bin", "keep/excluded.bin/child"]);
        Check(rules.SequenceEqual(new[] { "keep/excluded.bin" }), "排除规则未合并子树");
        Check(!HistoryExclusions.IsExcluded("keep/excluded.bin2", rules), "排除规则错误匹配同前缀兄弟");
        var plan = await InPlaceRestoreTransaction.PreviewAsync(root, target, null, rules, CancellationToken.None);
        Check(plan.Changes.Any(item => item.Path == "new.txt" && item.Kind == "删除"), "整目录预览缺少新文件删除");
        await InPlaceRestoreTransaction.ApplyAsync(root, plan, stage, protection.Id, CancellationToken.None);
        Check(HistoryComparison.Equal(plan.Target, await WorkingTree.ScanAsync(root, rules, CancellationToken.None)), "原位恢复的字节、目录、类型或大小写与预览不符");
        Check(File.ReadAllText(Path.Combine(root, "keep", "excluded.bin")) == "never change or delete this", "恢复修改了排除项");
        Check(InPlaceRestoreTransaction.ReadPending(root) is null, "成功恢复仍有未完成标记");
        Check(!Directory.EnumerateFiles(Path.Combine(HistoryPaths.HistoryDirectory(root), "restore-records"), "*", SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(path) is not ("operation.json" or "plan.json")), "成功恢复留下了原件或目标内容副本");

        Write(root, "delete-only.txt", "new protected bytes");
        protection = await VersionAsync(root, root, "duplicati-20261007T140000Z.dlist.zip");
        var deletion = await InPlaceRestoreTransaction.PreviewAsync(root, target, "delete-only.txt", rules, CancellationToken.None);
        Check(deletion.Target.Count == 0 && deletion.Changes.Single().Kind == "删除", "单项恢复未支持目标版本不存在的当前文件");
        await InPlaceRestoreTransaction.ApplyAsync(root, deletion, stage, protection.Id, CancellationToken.None);
        Check(!File.Exists(Path.Combine(root, "delete-only.txt")), "单项删除没有生效");

        var conflict = await InPlaceRestoreTransaction.PreviewAsync(root, protection, "replace.txt", rules, CancellationToken.None);
        Write(root, "replace.txt", "written after preview");
        await ExpectAsync<IOException>(() => InPlaceRestoreTransaction.ApplyAsync(root, conflict, stage, protection.Id, CancellationToken.None));
        Check(File.ReadAllText(Path.Combine(root, "replace.txt")) == "written after preview" && InPlaceRestoreTransaction.ReadPending(root) is null,
            "预览后更改被覆盖或错误进入事务");

        foreach (var action in new[] { "resume-moved-original", "resume-installed", "rollback", "preserve-new-write",
                     "moved-resume", "moved-rollback", "moved-completed" })
            await CheckInterruptedAsync(fixture, action);
        await CheckNestedHistoryAsync(fixture);
        await CheckProgressSizeAsync(fixture);
    }

    private static async Task CheckInterruptedAsync(string fixture, string action)
    {
        var root = CreateRoot(fixture, action);
        Write(root, "file.txt", "before restore");
        var history = HistoryPaths.HistoryDirectory(root);
        var stage = Directory.CreateDirectory(Path.Combine(history, "temp", "stage")).FullName;
        Write(stage, "file.txt", "target bytes");
        var version = await VersionAsync(root, stage, "duplicati-20261007T120000Z.dlist.zip");
        var protection = await VersionAsync(root, root, "duplicati-20261007T130000Z.dlist.zip");
        var plan = await InPlaceRestoreTransaction.PreviewAsync(root, version, "file.txt", [], CancellationToken.None);
        var active = Directory.CreateDirectory(Path.Combine(history, "restore-pending")).FullName;
        foreach (var area in new[] { "ready", "undo", "discard", "rollback" }) Directory.CreateDirectory(Path.Combine(active, area));
        File.Move(Path.Combine(root, "file.txt"), Path.Combine(active, "undo", "0"));
        var state = new InPlaceRestoreState
        {
            Plan = plan, ProtectionVersionId = protection.Id, Status = action == "moved-completed" ? "Completed" : "Applying"
        };
        if (action == "resume-moved-original")
        {
            File.Copy(Path.Combine(stage, "file.txt"), Path.Combine(active, "ready", "1"));
        }
        else File.Copy(Path.Combine(stage, "file.txt"), Path.Combine(root, "file.txt"));
        WriteTransactionFixture(active, state, action == "resume-moved-original" ? 0 : action == "moved-completed" ? 2 : 1,
            action != "moved-completed");
        string? previousRoot = null;
        if (action.StartsWith("moved-", StringComparison.Ordinal))
        {
            previousRoot = root;
            root += "-new-location";
            Directory.Move(previousRoot, root);
            Write(previousRoot, "file.txt", "unrelated old location");
            history = HistoryPaths.HistoryDirectory(root);
            active = Path.Combine(history, "restore-pending");
            Expect<InvalidDataException>(() => InPlaceRestoreTransaction.ReadPending(root));
            var identityPath = Path.Combine(history, "history.json");
            var identity = File.ReadAllText(identityPath);
            File.WriteAllText(identityPath, JsonSerializer.Serialize(new
                { schemaVersion = 1, id = Guid.NewGuid(), engine = "Duplicati", versions = Array.Empty<object>() }));
            Expect<InvalidDataException>(() => InPlaceRestoreTransaction.RebindPending(root));
            File.WriteAllText(identityPath, identity);
            InPlaceRestoreTransaction.RebindPending(root);
            await ExpectAsync<InvalidDataException>(() => InPlaceRestoreTransaction.EnsurePreviewCurrentAsync(root, plan, CancellationToken.None));
            if (action == "moved-completed")
            {
                Check(InPlaceRestoreTransaction.ReadPending(root) is null && !Directory.Exists(active), "移动后的已完成记录未能归档");
                Check(File.ReadAllText(Path.Combine(previousRoot, "file.txt")) == "unrelated old location", "归档写入了旧根");
                return;
            }
        }
        Check(InPlaceRestoreTransaction.ReadPending(root)?.ProtectionVersionId == protection.Id, "重启没有找到恢复前版本引用");
        if (action == "preserve-new-write")
        {
            Write(root, "file.txt", "user wrote after interruption");
            await ExpectAsync<IOException>(() => InPlaceRestoreTransaction.RollbackAsync(root, CancellationToken.None));
            Check(File.ReadAllText(Path.Combine(root, "file.txt")) == "user wrote after interruption" &&
                File.ReadAllText(Path.Combine(active, "undo", "0")) == "before restore" && InPlaceRestoreTransaction.ReadPending(root) is not null,
                "撤销覆盖了后写内容或清除了原件");
            var userCopy = Path.Combine(fixture, "user-copy-after-interruption.txt");
            File.Move(Path.Combine(root, "file.txt"), userCopy);
            await InPlaceRestoreTransaction.RollbackAsync(root, CancellationToken.None);
            Check(File.ReadAllText(Path.Combine(root, "file.txt")) == "before restore" &&
                File.ReadAllText(userCopy) == "user wrote after interruption", "移出后写冲突内容后应保留两份字节并可撤销");
        }
        else if (action is "rollback" or "moved-rollback")
        {
            await InPlaceRestoreTransaction.RollbackAsync(root, CancellationToken.None);
            Check(File.ReadAllText(Path.Combine(root, "file.txt")) == "before restore", "中断撤销未恢复原字节");
        }
        else
        {
            await InPlaceRestoreTransaction.ResumeAsync(root, CancellationToken.None);
            Check(File.ReadAllText(Path.Combine(root, "file.txt")) == "target bytes", "跨move/journal中断后未按事实继续");
        }
        Check(InPlaceRestoreTransaction.ReadPending(root) is null, "完成中断处理后仍有pending");
        if (previousRoot is not null)
            Check(File.ReadAllText(Path.Combine(previousRoot, "file.txt")) == "unrelated old location", "移动后的继续或撤销写入了旧根");
    }

    private static async Task CheckNestedHistoryAsync(string fixture)
    {
        var root = CreateRoot(fixture, "nested-history");
        Write(root, "parent.txt", "parent bytes");
        var parent = await VersionAsync(root, root, "duplicati-20261007T120000Z.dlist.zip");
        var plan = await InPlaceRestoreTransaction.PreviewAsync(root, parent, null, [], CancellationToken.None);
        var stage = Directory.CreateDirectory(Path.Combine(HistoryPaths.HistoryDirectory(root), "temp", "parent-stage")).FullName;
        Write(stage, "parent.txt", "parent bytes");
        var child = CreateRoot(root, "child");
        Write(child, "child.txt", "child bytes");
        var parentState = File.ReadAllBytes(Path.Combine(HistoryPaths.HistoryDirectory(root), "history.json"));
        var childState = File.ReadAllBytes(Path.Combine(HistoryPaths.HistoryDirectory(child), "history.json"));
        await ExpectAsync<InvalidDataException>(() => WorkingTree.ScanAsync(root, CancellationToken.None));
        await ExpectAsync<InvalidDataException>(() => InPlaceRestoreTransaction.PreviewAsync(root, parent, null, [], CancellationToken.None));
        await ExpectAsync<InvalidDataException>(() => InPlaceRestoreTransaction.ApplyAsync(root, plan, stage, parent.Id, CancellationToken.None));
        Check(parentState.SequenceEqual(File.ReadAllBytes(Path.Combine(HistoryPaths.HistoryDirectory(root), "history.json"))) &&
            childState.SequenceEqual(File.ReadAllBytes(Path.Combine(HistoryPaths.HistoryDirectory(child), "history.json"))) &&
            File.ReadAllText(Path.Combine(child, "child.txt")) == "child bytes", "父目录回退触及了移入的子库或工作内容");
        Check((await WorkingTree.ScanAsync(root, new[] { "child" }, CancellationToken.None)).All(item => !item.Path.StartsWith("child", StringComparison.Ordinal)),
            "明确排除整个子树不应遍历或修改该子树");
        Expect<InvalidDataException>(() => HistoryPaths.ResolveWorkPath(root, "child/.tractortool-history/repository/file"));
    }

    private static async Task CheckProgressSizeAsync(string fixture)
    {
        var root = CreateRoot(fixture, "restore-progress-size");
        for (var index = 0; index < 250; index++) Write(root, $"file-{index:D4}.txt", "original bytes");
        var history = HistoryPaths.HistoryDirectory(root);
        var empty = Directory.CreateDirectory(Path.Combine(history, "temp", "empty")).FullName;
        var target = await VersionAsync(root, empty, "duplicati-20261007T120000Z.dlist.zip");
        var protection = await VersionAsync(root, root, "duplicati-20261007T130000Z.dlist.zip");
        var plan = await InPlaceRestoreTransaction.PreviewAsync(root, target, null, [], CancellationToken.None);
        var active = Directory.CreateDirectory(Path.Combine(history, "restore-pending")).FullName;
        foreach (var area in new[] { "ready", "undo", "discard", "rollback" }) Directory.CreateDirectory(Path.Combine(active, area));
        WriteTransactionFixture(active, new() { Plan = plan, ProtectionVersionId = protection.Id, Status = "Applying" }, 0, true);
        var frozen = File.ReadAllBytes(Path.Combine(active, "plan.json"));
        Write(root, "file-0249.txt", "a later edit blocks the first removal");
        await ExpectAsync<IOException>(() => InPlaceRestoreTransaction.ResumeAsync(root, CancellationToken.None));
        var progressLength = new FileInfo(Path.Combine(active, "operation.json")).Length;
        Check(progressLength < 1024 && frozen.Length > progressLength * 20 &&
            frozen.SequenceEqual(File.ReadAllBytes(Path.Combine(active, "plan.json"))),
            "逐项进度必须为常数大小，不能每步重新写完整计划");
    }

    private static void WriteTransactionFixture(string active, InPlaceRestoreState state, int appliedCount, bool stepStarted)
    {
        File.WriteAllText(Path.Combine(active, "plan.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, state.Id, state.Plan, state.ProtectionVersionId }, Json));
        File.WriteAllText(Path.Combine(active, "operation.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, state.Id, state.Status, appliedCount, stepStarted, rollbackNext = -1, rollbackStarted = false }, Json));
    }

    private static string CreateRoot(string fixture, string name)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture, name)).FullName;
        var history = Directory.CreateDirectory(HistoryPaths.HistoryDirectory(root)).FullName;
        Directory.CreateDirectory(Path.Combine(history, "repository"));
        File.WriteAllText(Path.Combine(history, "history.json"), JsonSerializer.Serialize(new
        { schemaVersion = 1, id = Guid.NewGuid(), engine = "Duplicati", versions = Array.Empty<object>() }));
        return root;
    }

    private static void Write(string root, string relative, string text)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static async Task<HistoryVersion> VersionAsync(string root, string source, string id)
    {
        var entries = await WorkingTree.ScanAsync(source, CancellationToken.None);
        var native = new List<object> { new { type = "Folder", path = root + Path.DirectorySeparatorChar } };
        foreach (var entry in entries)
        {
            var path = Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            native.Add(entry.IsDirectory ? new { type = "Folder", path = path + Path.DirectorySeparatorChar } :
                (object)new { type = "File", path, size = entry.Length, hash = Convert.ToBase64String(Convert.FromHexString(entry.Hash)) });
        }
        var history = HistoryPaths.HistoryDirectory(root);
        EngineChecks.WriteVolume(Path.Combine(history, "repository", id), true, "SHA256", native.ToArray());
        return DuplicatiEngine.ReadVersion(history, id)!;
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void Expect<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
}
