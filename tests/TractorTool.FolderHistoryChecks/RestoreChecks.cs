using System.Text.Json;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Processing;
using TractorTool.Plugins.FolderHistory.Services;
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
        await InPlaceRestoreTransaction.ApplyAsync(root, plan, stage, [], CancellationToken.None);
        Check(HistoryComparison.Equal(plan.Target, await WorkingTree.ScanAsync(root, rules, CancellationToken.None)), "原位恢复的字节、目录、类型或大小写与预览不符");
        Check(File.ReadAllText(Path.Combine(root, "keep", "excluded.bin")) == "never change or delete this", "恢复修改了排除项");
        Check(InPlaceRestoreTransaction.ReadPending(root) is null, "成功恢复仍有未完成标记");
        Check(!Directory.EnumerateFiles(Path.Combine(HistoryPaths.HistoryDirectory(root), "restore-records"), "*", SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(path) is not ("operation.json" or "plan.json")), "成功恢复留下了原件或目标内容副本");

        Write(root, "delete-only.txt", "new protected bytes");
        protection = await VersionAsync(root, root, "duplicati-20261007T140000Z.dlist.zip");
        await ExpectAsync<InvalidDataException>(() => InPlaceRestoreTransaction.PreviewSelectionAsync(root, target, ["delete-only.txt"], rules, CancellationToken.None));
        Check(File.Exists(Path.Combine(root, "delete-only.txt")), "所选路径在目标版本缺失时，不能删除当前工作文件");

        var conflict = await InPlaceRestoreTransaction.PreviewAsync(root, protection, "replace.txt", rules, CancellationToken.None);
        Write(root, "replace.txt", "written after preview");
        await ExpectAsync<IOException>(() => InPlaceRestoreTransaction.ApplyAsync(root, conflict, stage, [], CancellationToken.None));
        Check(File.ReadAllText(Path.Combine(root, "replace.txt")) == "written after preview" && InPlaceRestoreTransaction.ReadPending(root) is null,
            "预览后更改被覆盖或错误进入事务");

        foreach (var action in new[] { "resume-moved-original", "resume-installed", "rollback", "preserve-new-write",
                     "moved-resume", "moved-rollback", "moved-completed" })
        {
            await CheckInterruptedAsync(fixture, action);
            await CheckInterruptedAsync(fixture, action, localUndo: true);
        }
        await CheckSelectionAsync(fixture);
        await CheckNestedHistoryAsync(fixture);
        await CheckProgressSizeAsync(fixture);
        await CheckCleanupConfirmationsAsync(fixture);
    }

    private static async Task CheckCleanupConfirmationsAsync(string fixture)
    {
        foreach (var deleteAll in new[] { false, true })
        {
            var area = Directory.CreateDirectory(Path.Combine(fixture, "cleanup-confirmation-" + deleteAll)).FullName;
            var host = new ServiceChecks.TestHost(area);
            using var service = new FolderHistoryService(new("checks", host, host, host, CancellationToken.None)
                { Packages = host, Processes = host, Tasks = host });
            var root = CreateRoot(area, "work");
            const string first = "duplicati-20261007T120000Z.dlist.zip";
            const string last = "duplicati-20261007T130000Z.dlist.zip";
            await PopulateAsync("original library");
            await service.AddFolderAsync(root);
            try
            {
                var superseded = await service.GetCleanupPreviewAsync(root, deleteAll);
                var preview = await service.GetCleanupPreviewAsync(root + Path.DirectorySeparatorChar, deleteAll);
                await ExpectAsync<InvalidOperationException>(() => service.DeleteVersionsAsync(superseded));
                await ExpectAsync<InvalidOperationException>(() => service.DeleteVersionsAsync(preview with { }));
                await service.RemoveFolderAsync(root + Path.DirectorySeparatorChar);
                await service.AddFolderAsync(root);
                await ExpectAsync<InvalidOperationException>(() => service.DeleteVersionsAsync(preview));

                preview = await service.GetCleanupPreviewAsync(root, deleteAll);
                await service.RemoveFolderAsync(root);
                var original = root + "-original";
                Directory.Move(root, original);
                CreateRoot(area, "work");
                await PopulateAsync("replacement library with the same volume names");
                await service.AddFolderAsync(root);
                await ExpectAsync<InvalidOperationException>(() => service.DeleteVersionsAsync(preview));
                var current = await service.GetCleanupPreviewAsync(root, deleteAll);
                await ExpectAsync<InvalidOperationException>(() => service.DeleteVersionsAsync(preview));
                Check(host.PackageRequests == 0 && host.Queued == 0 && host.Processes == 0 &&
                    HistoryStore.Read(root).PendingDeleteIds.Count == 0 && HistoryStore.Read(root).Versions.Count == 2 &&
                    File.ReadAllText(Path.Combine(original, "file.txt")) == "original library" &&
                    File.ReadAllText(Path.Combine(root, "file.txt")) == "replacement library with the same volume names",
                    "旧清理确认触及了重新关联的历史库或进入了引擎准备");
                await ExpectAsync<OperationCanceledException>(() => service.DeleteVersionsAsync(current));
                Check(host.PackageRequests == 1 && host.Queued == 0 && host.Processes == 0,
                    "当前有效的清理确认没有走正常的依赖准备和用户拒绝路径");
            }
            finally { await service.StopAsync(); }

            async Task PopulateAsync(string text)
            {
                Write(root, "file.txt", text);
                await VersionAsync(root, root, first);
                await VersionAsync(root, root, last);
                var state = HistoryStore.Read(root);
                state.AddVersion(first, root, "first", false);
                state.AddVersion(last, root, "last", false);
                state.Settings = new() { KeepLatestVersions = 1 };
                HistoryStore.Save(root, state);
            }
        }
    }

    private static async Task CheckSelectionAsync(string fixture)
    {
        var root = CreateRoot(fixture, "restore-selection");
        var stage = Directory.CreateDirectory(Path.Combine(HistoryPaths.HistoryDirectory(root), "temp", "selected")).FullName;
        Write(root, "folder/keep.txt", "new");
        Write(root, "folder/remove.txt", "remove within selected folder");
        Write(root, "same-prefix.txt", "unchanged");
        Write(root, "sibling.txt", "new sibling");
        Write(root, "other.txt", "leave current bytes");
        Write(stage, "folder/keep.txt", "old");
        Write(stage, "sibling.txt", "old sibling");
        Write(stage, "other.txt", "old unrelated version");
        Write(stage, "new-parent/child.txt", "child");
        var target = await VersionAsync(root, stage, "duplicati-20261007T120000Z.dlist.zip");
        var previous = await VersionAsync(root, root, "duplicati-20261007T130000Z.dlist.zip");
        var blocked = await InPlaceRestoreTransaction.PreviewSelectionAsync(root, target,
            ["sibling.txt", "new-parent/child.txt"], [], CancellationToken.None);
        Write(root, "new-parent", "created after preview");
        await ExpectAsync<IOException>(() => InPlaceRestoreTransaction.ApplyAsync(root, blocked, stage, [], CancellationToken.None));
        Check(File.ReadAllText(Path.Combine(root, "sibling.txt")) == "new sibling" &&
            InPlaceRestoreTransaction.ReadPending(root) is null, "预览后新增的上级占位文件必须在所有工作文件写入前拒绝");
        foreach (string[] invalid in new string[][] { [], ["folder", "folder/remove.txt"], ["sibling.txt", "unknown"], ["", "unknown"] })
            await ExpectAsync<InvalidDataException>(() => InPlaceRestoreTransaction.PreviewSelectionAsync(root, target, invalid, [], CancellationToken.None));
        var plan = await InPlaceRestoreTransaction.PreviewSelectionAsync(root, target,
            ["sibling.txt", "folder/keep.txt", "folder", "sibling.txt"], [], CancellationToken.None);
        Check(plan.SelectedPaths.SequenceEqual(new[] { "folder", "sibling.txt" }) &&
            plan.Changes.Select(item => item.Path).SequenceEqual(new[] { "folder/keep.txt", "folder/remove.txt", "sibling.txt" }),
            "多项预览应合并父子和重复选择，并限于所选范围");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await ExpectAsync<OperationCanceledException>(() => InPlaceRestoreTransaction.ApplyAsync(root, plan, stage, [], cancelled.Token));
            Check(InPlaceRestoreTransaction.ReadPending(root) is null && File.ReadAllText(Path.Combine(root, "sibling.txt")) == "new sibling",
                "取消的恢复不能进入事务或改动工作文件");
        }
        await InPlaceRestoreTransaction.ApplyAsync(root, plan, stage, [], CancellationToken.None);
        Check(File.ReadAllText(Path.Combine(root, "sibling.txt")) == "old sibling" &&
            !File.Exists(Path.Combine(root, "folder", "remove.txt")) && File.ReadAllText(Path.Combine(root, "other.txt")) == "leave current bytes",
            "多选未按同一计划处理全部目标或触及范围外文件");
        var archived = Directory.EnumerateFiles(Path.Combine(HistoryPaths.HistoryDirectory(root), "restore-records"), "plan.json", SearchOption.AllDirectories).Single();
        using var record = JsonDocument.Parse(File.ReadAllText(archived));
        Check(record.RootElement.GetProperty("schemaVersion").GetInt32() == 3 &&
            record.RootElement.GetProperty("protectionVersionId").GetString() == "" &&
            record.RootElement.GetProperty("plan").GetProperty("selections").GetArrayLength() == 2, "新记录应冻结所有范围并使旧版拒读");
    }

    private static async Task CheckInterruptedAsync(string fixture, string action, bool localUndo = false)
    {
        var root = CreateRoot(fixture, (localUndo ? "local-undo-" : "") + action);
        Write(root, "file.txt", "before restore");
        var history = HistoryPaths.HistoryDirectory(root);
        var stage = Directory.CreateDirectory(Path.Combine(history, "temp", "stage")).FullName;
        Write(stage, "file.txt", "target bytes");
        var version = await VersionAsync(root, stage, "duplicati-20261007T120000Z.dlist.zip");
        var protection = localUndo ? null : await VersionAsync(root, root, "duplicati-20261007T130000Z.dlist.zip");
        var plan = await InPlaceRestoreTransaction.PreviewAsync(root, version, "file.txt", [], CancellationToken.None);
        // Exercise records written by 0.2.0 alongside the new frozen selection format.
        if (!localUndo && action is ("resume-moved-original" or "moved-rollback")) plan = plan with { Selections = null };
        var active = Directory.CreateDirectory(Path.Combine(history, "restore-pending")).FullName;
        foreach (var area in new[] { "ready", "undo", "discard", "rollback", "original" }) Directory.CreateDirectory(Path.Combine(active, area));
        File.Move(Path.Combine(root, "file.txt"), Path.Combine(active, localUndo ? "original" : "undo", localUndo ? "file.txt" : "0"));
        var state = new InPlaceRestoreState
        {
            Plan = plan, ProtectionVersionId = protection?.Id ?? "", RetainedOriginalPaths = localUndo ? ["file.txt"] : [],
            Status = action == "moved-completed" ? "Completed" : "Applying"
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
                if (localUndo) Check(File.ReadAllText(Path.Combine(history, "restore-records", state.Id.ToString("N"), "original", "file.txt")) == "before restore",
                    "完成记录归档前中断后，未保存原件必须仍按原路径可找到");
                return;
            }
        }
        Check(InPlaceRestoreTransaction.ReadPending(root)?.ProtectionVersionId == (protection?.Id ?? ""), "重启没有找到恢复记录");
        if (action == "preserve-new-write")
        {
            Write(root, "file.txt", "user wrote after interruption");
            await ExpectAsync<IOException>(() => InPlaceRestoreTransaction.RollbackAsync(root, CancellationToken.None));
            Check(File.ReadAllText(Path.Combine(root, "file.txt")) == "user wrote after interruption" &&
                File.ReadAllText(Path.Combine(active, localUndo ? "original" : "undo", localUndo ? "file.txt" : "0")) == "before restore" && InPlaceRestoreTransaction.ReadPending(root) is not null,
                "撤销覆盖了后写内容或清除了原件");
            var userCopy = root + "-user-copy-after-interruption.txt";
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
            var resumed = await InPlaceRestoreTransaction.ResumeAsync(root, CancellationToken.None);
            Check(File.ReadAllText(Path.Combine(root, "file.txt")) == "target bytes", "跨move/journal中断后未按事实继续");
            Check(resumed.OriginalsDirectory == (localUndo ? Path.Combine(history, "restore-records", state.Id.ToString("N"), "original") : null),
                "继续恢复必须直接公开保留原件目录，旧快照保护事务不伪造原件目录");
        }
        Check(InPlaceRestoreTransaction.ReadPending(root) is null, "完成中断处理后仍有pending");
        if (localUndo && action is not ("rollback" or "moved-rollback" or "preserve-new-write"))
            Check(File.ReadAllText(Path.Combine(history, "restore-records", state.Id.ToString("N"), "original", "file.txt")) == "before restore",
                "没有历史快照的原件必须按原路径保留，不能在成功恢复后删除");
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
        await ExpectAsync<InvalidDataException>(() => InPlaceRestoreTransaction.ApplyAsync(root, plan, stage, [], CancellationToken.None));
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
        File.WriteAllText(Path.Combine(active, "plan.json"), state.ProtectionVersionId.Length == 0
            ? JsonSerializer.Serialize(new { schemaVersion = 3, state.Id, state.Plan, state.ProtectionVersionId, state.RetainedOriginalPaths }, Json)
            : JsonSerializer.Serialize(new { schemaVersion = state.Plan.Selections is null ? 1 : 2, state.Id, state.Plan, state.ProtectionVersionId }, Json));
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
