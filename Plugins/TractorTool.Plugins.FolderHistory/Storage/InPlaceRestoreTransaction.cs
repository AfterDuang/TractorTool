using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using TractorTool.Plugins.FolderHistory.Models;

namespace TractorTool.Plugins.FolderHistory.Storage;

public sealed record InPlaceRestorePlan(
    [property: JsonRequired] string Root,
    [property: JsonRequired] Guid RepositoryId,
    [property: JsonRequired] string TargetVersionId,
    [property: JsonRequired] string? Selection,
    [property: JsonRequired] IReadOnlyList<string> Exclusions,
    [property: JsonRequired] IReadOnlyList<HistoryEntry> Baseline,
    [property: JsonRequired] IReadOnlyList<HistoryEntry> Target)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Selections { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> SelectedPaths => Selections ?? (Selection is null ? [HistorySelection.Root] : [Selection]);

    [JsonIgnore]
    public IReadOnlyList<HistoryChange> Changes => HistoryComparison.Compare(Baseline, Target);
}

public sealed class InPlaceRestoreState
{
    internal int SchemaVersion { get; set; } = 3;
    public Guid Id { get; set; } = Guid.NewGuid();
    public InPlaceRestorePlan Plan { get; set; } = null!;
    public string ProtectionVersionId { get; set; } = "";
    public IReadOnlyList<string> RetainedOriginalPaths { get; set; } = [];
    internal HashSet<string> RetainedOriginalSet { get; set; } = new(StringComparer.Ordinal);
    public string Status { get; set; } = "Preparing";
    internal List<InPlaceRestoreStep> Steps { get; set; } = [];
    internal int AppliedCount { get; set; }
    internal bool StepStarted { get; set; }
    internal int RollbackNext { get; set; } = -1;
    internal bool RollbackStarted { get; set; }
}

public sealed record InPlaceRestoreStep(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] HistoryEntry Entry,
    [property: JsonRequired] string Status);

public static class InPlaceRestoreTransaction
{
    private const string ActiveName = "restore-pending";
    private const string PlanName = "plan.json";
    private const string RecordName = "operation.json";
    private const int StateLimit = 128 * 1024 * 1024;
    private const int ProgressLimit = 4096;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static Task<InPlaceRestorePlan> PreviewAsync(string root, HistoryVersion version, string? selection,
        IReadOnlyList<string> exclusions, CancellationToken token) =>
        PreviewSelectionAsync(root, version, [selection ?? HistorySelection.Root], exclusions, token);

    public static async Task<InPlaceRestorePlan> PreviewSelectionAsync(string root, HistoryVersion version, IReadOnlyList<string> selections,
        IReadOnlyList<string> exclusions, CancellationToken token)
    {
        root = HistoryPaths.Absolute(root);
        HistoryPaths.RequireDirectory(root);
        if (ReadPending(root) is not null) throw new InvalidOperationException("请先处理未完成的恢复。");
        exclusions = HistoryExclusions.Normalize(exclusions);
        if (selections.Any(selection => selection != HistorySelection.Root && HistoryExclusions.IsExcluded(HistoryPaths.NormalizeRelative(selection), exclusions)))
            throw new InvalidDataException("所选路径已被排除，不会修改该路径。");
        selections = HistorySelection.Resolve(version, selections);
        var baseline = await ReadSelectedWorkAsync(root, selections, exclusions, token).ConfigureAwait(false);
        var target = HistorySelection.Scope(version.Entries.Where(item => !HistoryExclusions.IsExcluded(item.Path, exclusions)).ToArray(), selections)
            .ToDictionary(item => item.Path, HistoryPaths.Comparer);
        var selected = selections.ToHashSet(HistoryPaths.Comparer);
        foreach (var directory in baseline.Where(item => item.IsDirectory))
        {
            var outsideSelection = !HistorySelection.Contains(selected, directory.Path);
            var containsExcluded = exclusions.Any(rule => HistoryPaths.IsSameOrDescendant(directory.Path, rule));
            if (!outsideSelection && !containsExcluded) continue;
            if (target.TryGetValue(directory.Path, out var replacement) && replacement != directory)
                throw new IOException("恢复会改动所选范围外或排除项的上级目录，请调整恢复范围：" + directory.Path);
            target[directory.Path] = directory;
        }
        var plan = new InPlaceRestorePlan(root, HistoryStore.Read(root).Id, version.Id,
            selections.Count == 1 && selections[0] != HistorySelection.Root ? selections[0] : null, exclusions,
            baseline, target.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray()) { Selections = selections };
        ValidatePlan(root, plan);
        return plan;
    }

    public static async Task EnsurePreviewCurrentAsync(string root, InPlaceRestorePlan plan, CancellationToken token)
    {
        ValidatePlan(root, plan);
        var current = await ReadSelectedWorkAsync(root, plan.SelectedPaths, plan.Exclusions, token).ConfigureAwait(false);
        if (!HistoryComparison.Equal(plan.Baseline, current))
            throw new IOException("预览后工作文件发生变化，请重新预览；尚未修改工作文件。");
    }

    private static async Task<IReadOnlyList<HistoryEntry>> ReadSelectedWorkAsync(string root, IReadOnlyList<string> paths,
        IReadOnlyList<string> exclusions, CancellationToken token)
    {
        var current = await WorkingTree.ScanAsync(root, exclusions, token).ConfigureAwait(false);
        var ancestors = paths.SelectMany(HistorySelection.Ancestors).ToHashSet(HistoryPaths.Comparer);
        if (current.Any(entry => !entry.IsDirectory && ancestors.Contains(entry.Path)))
            throw new IOException("所选项目的上级目前是文件，请选择该上级预览类型替换。");
        return HistorySelection.Scope(current, paths);
    }

    public static async Task<(string? Warning, string? OriginalsDirectory)> ApplyAsync(string root, InPlaceRestorePlan plan, string staging,
        IReadOnlyList<string> retainedOriginalPaths, CancellationToken token)
    {
        ValidatePlan(root, plan);
        if (ReadPending(root) is not null) throw new InvalidOperationException("请先处理未完成的恢复。");
        ArchiveTerminal(root);
        await EnsurePreviewCurrentAsync(root, plan, token).ConfigureAwait(false);
        var extracted = (await WorkingTree.ScanAsync(staging, excludeHistory: false, token).ConfigureAwait(false))
            .ToDictionary(item => item.Path, StringComparer.Ordinal);
        var steps = BuildSteps(plan);
        foreach (var step in steps.Where(step => step.Kind == "CreateFile"))
            if (!extracted.TryGetValue(step.Entry.Path, out var item) || item != step.Entry)
                throw new InvalidDataException("恢复暂存区缺少已校验的目标文件：" + step.Entry.Path);
        var state = new InPlaceRestoreState { Plan = plan, RetainedOriginalPaths = retainedOriginalPaths.ToArray(), Steps = steps };
        ValidateRetainedOriginals(state);
        var directory = Active(root);
        HistoryPaths.CheckPath(directory);
        if (HistoryPaths.Attributes(directory) is not null) throw new IOException("恢复事务目录已被占用。");
        var initial = Path.Combine(HistoryPaths.HistoryDirectory(root), "restore-init-" + state.Id.ToString("N"));
        HistoryPaths.CheckPath(initial);
        if (HistoryPaths.Attributes(initial) is not null) throw new IOException("恢复初始化目录已被占用。");
        Directory.CreateDirectory(initial);
        foreach (var area in new[] { "ready", "undo", "discard", "rollback" }) Directory.CreateDirectory(Path.Combine(initial, area));
        SaveFrozen(initial, state);
        SaveProgress(initial, state);
        Directory.Move(initial, directory);
        for (var index = 0; index < state.Steps.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var step = state.Steps[index];
            if (step.Kind == "CreateFile")
            {
                var source = HistoryPaths.ResolveWorkPath(staging, step.Entry.Path);
                await WorkingTree.CopyFileAsync(source, Material(root, "ready", index), () => { }, token).ConfigureAwait(false);
                await RequireMaterialAsync(root, "ready", index, step.Entry, token).ConfigureAwait(false);
            }
            else if (step.Kind == "CreateDirectory") Directory.CreateDirectory(Material(root, "ready", index));
        }
        await EnsurePreviewCurrentAsync(root, plan, token).ConfigureAwait(false);
        state.Status = "Applying";
        Save(root, state);
        var warning = await ContinueAsync(root, state, token).ConfigureAwait(false);
        return (warning, OriginalsDirectory(root, state));
    }

    public static InPlaceRestoreState? ReadPending(string root)
    {
        var state = Read(root);
        return state is null || state.Status is "Completed" or "Reverted" ? null : state;
    }

    public static void RebindPending(string root)
    {
        root = HistoryPaths.Absolute(root);
        var state = Read(root, allowRelocation: true);
        if (state is null) return;
        if (state.Plan.Root != root)
        {
            state.Plan = state.Plan with { Root = root };
            SaveFrozen(Active(root), state);
        }
        if (state.Status is "Completed" or "Reverted") ArchiveTerminal(root);
    }

    public static async Task<(string? Warning, string? OriginalsDirectory)> ResumeAsync(string root, CancellationToken token)
    {
        var state = ReadPending(root) ?? throw new InvalidOperationException("没有未完成的恢复。");
        if (state.Status != "Applying")
            throw new InvalidOperationException(state.Status == "Preparing" ? "恢复材料尚未准备完整，请先撤销本次恢复。" : "本次恢复已开始撤销，请继续撤销。");
        var warning = await ContinueAsync(root, state, token).ConfigureAwait(false);
        return (warning, OriginalsDirectory(root, state));
    }

    public static async Task<string?> RollbackAsync(string root, CancellationToken token)
    {
        var state = ReadPending(root) ?? throw new InvalidOperationException("没有未完成的恢复。");
        if (state.Status == "Preparing")
        {
            state.Status = "Reverted";
            Save(root, state);
            return await FinishAsync(root, state).ConfigureAwait(false);
        }
        if (state.Status != "Reverting")
        {
            state.RollbackNext = state.AppliedCount + (state.StepStarted ? 1 : 0) - 1;
            state.Status = "Reverting";
            Save(root, state);
        }
        while (state.RollbackNext >= 0)
        {
            token.ThrowIfCancellationRequested();
            var index = state.RollbackNext;
            var step = state.Steps[index];
            var previousStatus = state.RollbackStarted ? "Reverting" : index < state.AppliedCount ? "Applied" : "Started";
            state.RollbackStarted = true;
            Save(root, state);
            await UndoStepAsync(root, state, step, index, previousStatus, token).ConfigureAwait(false);
            state.RollbackNext--;
            state.RollbackStarted = false;
            Save(root, state);
        }
        var current = HistorySelection.Scope(await WorkingTree.ScanAsync(root, state.Plan.Exclusions, token).ConfigureAwait(false), state.Plan.SelectedPaths);
        if (!HistoryComparison.Equal(current, state.Plan.Baseline))
            throw new IOException("撤销后仍存在预览之外的更改，已保留当前内容和恢复材料；请处理冲突后继续检查。");
        state.Status = "Reverted";
        Save(root, state);
        return await FinishAsync(root, state).ConfigureAwait(false);
    }

    private static async Task<string?> ContinueAsync(string root, InPlaceRestoreState state, CancellationToken token)
    {
        while (state.AppliedCount < state.Steps.Count)
        {
            token.ThrowIfCancellationRequested();
            var index = state.AppliedCount;
            var step = state.Steps[index] with { Status = state.StepStarted ? "Started" : "Pending" };
            state.StepStarted = true;
            Save(root, state);
            await ApplyStepAsync(root, state, step, index, token).ConfigureAwait(false);
            state.AppliedCount++;
            state.StepStarted = false;
            Save(root, state);
        }
        var actual = HistorySelection.Scope(await WorkingTree.ScanAsync(root, state.Plan.Exclusions, token).ConfigureAwait(false), state.Plan.SelectedPaths);
        if (!HistoryComparison.Equal(actual, state.Plan.Target))
            throw new IOException("恢复后的文件与冻结计划不符，可能有新写入；已保留恢复原件和操作记录。");
        state.Status = "Completed";
        Save(root, state);
        return await FinishAsync(root, state).ConfigureAwait(false);
    }

    private static async Task ApplyStepAsync(string root, InPlaceRestoreState state, InPlaceRestoreStep step, int index, CancellationToken token)
    {
        var entry = step.Entry;
        var target = HistoryPaths.ResolveWorkPath(root, entry.Path);
        var ready = Material(root, "ready", index);
        var undo = UndoMaterial(root, state, index);
        if (step.Kind == "RemoveFile")
        {
            if (HistoryPaths.Attributes(undo) is null)
            {
                await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(undo)!);
                await WorkingTree.MoveExpectedFileAsync(target, undo, entry, token).ConfigureAwait(false);
            }
            // Validate after moving: a write between the preview/hash and rename is retained, never overwritten.
            await RequireMaterialAsync(undo, entry, token).ConfigureAwait(false);
            if (HistoryPaths.Attributes(target) is not null) throw Conflict(entry.Path);
        }
        else if (step.Kind == "RemoveDirectory")
        {
            if (HistoryPaths.Attributes(target) is null && step.Status == "Started") return;
            await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Delete(target, recursive: false);
        }
        else if (step.Kind == "CreateFile")
        {
            if (HistoryPaths.Attributes(ready) is null && step.Status == "Started")
            {
                await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
                return;
            }
            await RequireMaterialAsync(root, "ready", index, entry, token).ConfigureAwait(false);
            if (HistoryPaths.Attributes(target) is not null) throw Conflict(entry.Path);
            token.ThrowIfCancellationRequested();
            await WorkingTree.MoveExpectedFileAsync(ready, target, entry, token).ConfigureAwait(false);
            await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
        }
        else
        {
            if (HistoryPaths.Attributes(ready) is null && step.Status == "Started")
            {
                await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
                return;
            }
            HistoryPaths.RequireDirectory(ready);
            if (Directory.EnumerateFileSystemEntries(ready).Any()) throw new InvalidDataException("恢复目录占位区出现未知内容。");
            if (HistoryPaths.Attributes(target) is not null) throw Conflict(entry.Path);
            token.ThrowIfCancellationRequested();
            Directory.Move(ready, target);
        }
    }

    private static async Task UndoStepAsync(string root, InPlaceRestoreState state, InPlaceRestoreStep step, int index, string previousStatus, CancellationToken token)
    {
        var entry = step.Entry;
        var target = HistoryPaths.ResolveWorkPath(root, entry.Path);
        var undo = UndoMaterial(root, state, index);
        var ready = Material(root, "ready", index);
        var discard = Material(root, "discard", index);
        if (step.Kind == "CreateFile")
        {
            if (HistoryPaths.Attributes(ready) is not null) return;
            if (HistoryPaths.Attributes(discard) is null)
            {
                if (HistoryPaths.Attributes(target) is null) return;
                await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await WorkingTree.MoveExpectedFileAsync(target, discard, entry, token).ConfigureAwait(false);
            }
            await RequireMaterialAsync(root, "discard", index, entry, token).ConfigureAwait(false);
            if (HistoryPaths.Attributes(target) is not null) throw Conflict(entry.Path);
        }
        else if (step.Kind == "CreateDirectory")
        {
            if (HistoryPaths.Attributes(ready) is not null || HistoryPaths.Attributes(target) is null) return;
            await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Delete(target, recursive: false);
        }
        else if (step.Kind == "RemoveFile")
        {
            if (HistoryPaths.Attributes(undo) is null)
            {
                if (previousStatus is not ("Started" or "Reverting")) throw new InvalidDataException("恢复原件缺失，已停止撤销。");
                await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
                return;
            }
            await RequireMaterialAsync(undo, entry, token).ConfigureAwait(false);
            if (HistoryPaths.Attributes(target) is not null) throw Conflict(entry.Path);
            token.ThrowIfCancellationRequested();
            await WorkingTree.MoveExpectedFileAsync(undo, target, entry, token).ConfigureAwait(false);
            await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
        }
        else
        {
            var rollback = Material(root, "rollback", index);
            if (HistoryPaths.Attributes(target) is not null)
            {
                if (previousStatus != "Started" && HistoryPaths.Attributes(rollback) is not null) throw Conflict(entry.Path);
                await RequireCurrentAsync(root, entry, token).ConfigureAwait(false);
                return;
            }
            if (HistoryPaths.Attributes(rollback) is null) Directory.CreateDirectory(rollback);
            HistoryPaths.RequireDirectory(rollback);
            if (Directory.EnumerateFileSystemEntries(rollback).Any()) throw new InvalidDataException("撤销目录占位区出现未知内容。");
            token.ThrowIfCancellationRequested();
            Directory.Move(rollback, target);
        }
    }

    private static async Task RequireCurrentAsync(string root, HistoryEntry expected, CancellationToken token)
    {
        if (await WorkingTree.ReadEntryAsync(root, expected.Path, token).ConfigureAwait(false) != expected)
            throw Conflict(expected.Path);
    }

    private static Task RequireMaterialAsync(string root, string area, int index, HistoryEntry expected, CancellationToken token) =>
        RequireMaterialAsync(Material(root, area, index), expected, token);

    private static async Task RequireMaterialAsync(string file, HistoryEntry expected, CancellationToken token)
    {
        var actual = await WorkingTree.ReadFileAsync(file, expected.Path, token).ConfigureAwait(false);
        if (actual != expected) throw new IOException("恢复材料发生变化，原件和新内容均已保留，请检查：" + file);
    }

    private static IOException Conflict(string path) => new("恢复遇到预览后的更改或未知占位，不会覆盖；材料已保留：" + path);

    private static List<InPlaceRestoreStep> BuildSteps(InPlaceRestorePlan plan)
    {
        var before = plan.Baseline.ToHashSet();
        var after = plan.Target.ToHashSet();
        return plan.Baseline.Where(item => !after.Contains(item)).OrderByDescending(item => item.Path.Count(c => c == '/'))
            .ThenByDescending(item => item.Path, StringComparer.Ordinal)
            .Select(item => new InPlaceRestoreStep(item.IsDirectory ? "RemoveDirectory" : "RemoveFile", item, "Pending"))
            .Concat(plan.Target.Where(item => !before.Contains(item)).OrderBy(item => item.Path.Count(c => c == '/'))
                .ThenBy(item => item.Path, StringComparer.Ordinal)
                .Select(item => new InPlaceRestoreStep(item.IsDirectory ? "CreateDirectory" : "CreateFile", item, "Pending")))
            .ToList();
    }

    private static void ValidatePlan(string root, InPlaceRestorePlan plan)
    {
        root = HistoryPaths.Absolute(root);
        if (plan is null || plan.Root != root || plan.RepositoryId == Guid.Empty || HistoryStore.Read(root).Id != plan.RepositoryId ||
            string.IsNullOrWhiteSpace(plan.TargetVersionId) || Path.GetFileName(plan.TargetVersionId) != plan.TargetVersionId ||
            plan.Baseline is null || plan.Target is null || plan.Exclusions is null ||
            !plan.Exclusions.SequenceEqual(HistoryExclusions.Normalize(plan.Exclusions)) ||
            plan.Selection is not null && (plan.Selection != HistoryPaths.NormalizeRelative(plan.Selection) ||
                HistoryExclusions.IsExcluded(plan.Selection, plan.Exclusions)))
            throw new InvalidDataException("恢复计划的目录身份、路径或排除规则无效。");
        if (plan.Selections is { } selections)
        {
            var targetPaths = plan.Target.Select(entry => entry.Path).ToHashSet(StringComparer.Ordinal);
            if (!selections.SequenceEqual(HistorySelection.Normalize(selections)) ||
                plan.Selection != (selections.Count == 1 && selections[0] != HistorySelection.Root ? selections[0] : null) ||
                selections.Any(selection => selection != HistorySelection.Root &&
                    (HistoryExclusions.IsExcluded(selection, plan.Exclusions) || !targetPaths.Contains(selection))))
                throw new InvalidDataException("恢复计划的联合选择范围无效。");
        }
        foreach (var entries in new[] { plan.Baseline, plan.Target })
        {
            var paths = new Dictionary<string, HistoryEntry>(HistoryPaths.Comparer);
            foreach (var entry in entries)
            {
                if (entry is null || entry.Path != HistoryPaths.NormalizeRelative(entry.Path) ||
                    HistoryExclusions.IsExcluded(entry.Path, plan.Exclusions) || entry.Length < 0 ||
                    (entry.IsDirectory ? entry.Length != 0 || entry.Hash != "" : entry.Hash is null || entry.Hash.Length != 64 ||
                        entry.Hash.Any(c => !char.IsAsciiHexDigit(c))) || !paths.TryAdd(entry.Path, entry))
                    throw new InvalidDataException("恢复计划包含无效或冲突的工作路径。");
            }
            if (!entries.SequenceEqual(HistorySelection.Scope(entries, plan.SelectedPaths))) throw new InvalidDataException("恢复计划超出选择范围。");
            foreach (var entry in entries)
            {
                var separator = entry.Path.LastIndexOf('/');
                if (separator >= 0 && (!paths.TryGetValue(entry.Path[..separator], out var parent) || !parent.IsDirectory ||
                    !entry.Path.StartsWith(parent.Path + "/", StringComparison.Ordinal)))
                    throw new InvalidDataException("恢复计划缺少正确大小写的父目录。");
            }
        }
    }

    private static InPlaceRestoreState? Read(string root, bool allowRelocation = false)
    {
        var directory = Active(root);
        HistoryPaths.CheckPath(directory);
        if (HistoryPaths.Attributes(directory) is null) return null;
        HistoryPaths.RequireDirectory(directory);
        var frozen = ReadRecord<FrozenState>(Path.Combine(directory, PlanName), StateLimit);
        var progress = ReadRecord<ProgressState>(Path.Combine(directory, RecordName), ProgressLimit);
        if (frozen.SchemaVersion is not (1 or 2 or 3) || progress.SchemaVersion != 1 || frozen.Id == Guid.Empty || progress.Id != frozen.Id ||
            frozen.Plan is null || (frozen.SchemaVersion == 1) != (frozen.Plan.Selections is null) ||
            (frozen.SchemaVersion == 3) != (frozen.RetainedOriginalPaths is not null))
            throw new InvalidDataException("恢复计划与进度标识不匹配或格式不兼容。");
        var state = new InPlaceRestoreState
        {
            SchemaVersion = frozen.SchemaVersion, Id = frozen.Id, Plan = frozen.Plan, ProtectionVersionId = frozen.ProtectionVersionId,
            RetainedOriginalPaths = frozen.RetainedOriginalPaths ?? [], Status = progress.Status,
            AppliedCount = progress.AppliedCount, StepStarted = progress.StepStarted,
            RollbackNext = progress.RollbackNext, RollbackStarted = progress.RollbackStarted
        };
        if (allowRelocation)
        {
            if (state.Plan is null || state.Plan.Root != HistoryPaths.Absolute(state.Plan.Root))
                throw new InvalidDataException("恢复记录的原根路径无效。");
            ValidatePlan(root, state.Plan with { Root = HistoryPaths.Absolute(root) });
        }
        else ValidatePlan(root, state.Plan);
        state.Steps = BuildSteps(state.Plan);
        ValidateRetainedOriginals(state);
        var attempted = state.AppliedCount + (state.StepStarted ? 1 : 0);
        if ((state.SchemaVersion == 3 ? state.ProtectionVersionId != "" : string.IsNullOrWhiteSpace(state.ProtectionVersionId) ||
            Path.GetFileName(state.ProtectionVersionId) != state.ProtectionVersionId) ||
            state.Status is not ("Preparing" or "Applying" or "Reverting" or "Completed" or "Reverted") ||
            state.AppliedCount < 0 || state.AppliedCount > state.Steps.Count || attempted > state.Steps.Count ||
            state.RollbackNext < -1 || state.RollbackNext >= attempted || state.RollbackStarted && state.RollbackNext < 0 ||
            state.Status == "Preparing" && (attempted != 0 || state.RollbackNext != -1 || state.RollbackStarted) ||
            state.Status == "Completed" && (state.AppliedCount != state.Steps.Count || state.StepStarted) ||
            state.Status is "Applying" or "Completed" && (state.RollbackNext != -1 || state.RollbackStarted) ||
            state.Status == "Reverted" && (state.RollbackNext != -1 || state.RollbackStarted))
            throw new InvalidDataException("恢复记录状态无效，已保留材料并停止操作。");
        return state;
    }

    private sealed record FrozenState(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Guid Id,
        [property: JsonRequired] InPlaceRestorePlan Plan,
        [property: JsonRequired] string ProtectionVersionId,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? RetainedOriginalPaths = null);

    private static void ValidateRetainedOriginals(InPlaceRestoreState state)
    {
        var removed = state.Steps.Where(step => step.Kind == "RemoveFile").Select(step => step.Entry.Path).ToHashSet(StringComparer.Ordinal);
        if (state.RetainedOriginalPaths is null || state.RetainedOriginalPaths.Distinct(HistoryPaths.Comparer).Count() != state.RetainedOriginalPaths.Count ||
            state.RetainedOriginalPaths.Any(path => !removed.Contains(path)))
            throw new InvalidDataException("恢复原件保留范围与计划不符。");
        state.RetainedOriginalSet = state.RetainedOriginalPaths.ToHashSet(StringComparer.Ordinal);
    }

    private sealed record ProgressState(
        [property: JsonRequired] int SchemaVersion,
        [property: JsonRequired] Guid Id,
        [property: JsonRequired] string Status,
        [property: JsonRequired] int AppliedCount,
        [property: JsonRequired] bool StepStarted,
        [property: JsonRequired] int RollbackNext,
        [property: JsonRequired] bool RollbackStarted);

    private static T ReadRecord<T>(string file, int limit)
    {
        HistoryPaths.CheckPath(file);
        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > limit) throw new InvalidDataException("恢复记录超过读取限制。");
        return JsonSerializer.Deserialize<T>(input, Json) ?? throw new InvalidDataException("恢复记录为空。");
    }

    private static void Save(string root, InPlaceRestoreState state)
        => SaveProgress(Active(root), state);

    private static void SaveFrozen(string directory, InPlaceRestoreState state) => WriteRecord(directory, PlanName,
        new FrozenState(state.SchemaVersion, state.Id, state.Plan, state.ProtectionVersionId,
            state.SchemaVersion == 3 ? state.RetainedOriginalPaths : null), StateLimit);

    private static void SaveProgress(string directory, InPlaceRestoreState state) => WriteRecord(directory, RecordName,
        new ProgressState(1, state.Id, state.Status, state.AppliedCount, state.StepStarted, state.RollbackNext, state.RollbackStarted), ProgressLimit);

    private static void WriteRecord<T>(string directory, string name, T value, int limit)
    {
        HistoryPaths.RequireDirectory(directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > limit) throw new InvalidDataException("恢复记录超过容量限制，未修改下一项工作文件。");
        var temporary = Path.Combine(directory, "state-" + Guid.NewGuid().ToString("N") + ".tmp");
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        var destination = Path.Combine(directory, name);
        HistoryPaths.CheckPath(destination);
        File.Move(temporary, destination, overwrite: true);
    }

    private static async Task<string?> FinishAsync(string root, InPlaceRestoreState state)
    {
        var warnings = new List<string>();
        for (var index = 0; index < state.Steps.Count; index++)
        foreach (var area in new[] { "ready", "undo", "discard", "rollback" })
        {
            try
            {
                var material = area == "undo" ? UndoMaterial(root, state, index) : Material(root, area, index);
                var attributes = HistoryPaths.Attributes(material);
                if (attributes is null) continue;
                if (area == "undo" && state.Status == "Completed" && state.RetainedOriginalSet.Contains(state.Steps[index].Entry.Path))
                    continue;
                if (attributes.Value.HasFlag(FileAttributes.Directory)) Directory.Delete(material, recursive: false);
                else
                {
                    var expected = state.Steps[index].Entry;
                    using var input = new FileStream(material, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    var hash = await SHA256.HashDataAsync(input, CancellationToken.None).ConfigureAwait(false);
                    if (input.Length != expected.Length || Convert.ToHexString(hash) != expected.Hash)
                        throw new IOException("恢复材料已变化，未清理：" + material);
                    File.Delete(material);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { warnings.Add(error.Message); }
        }
        foreach (var area in new[] { "ready", "undo", "discard", "rollback" })
        {
            try
            {
                var path = Path.Combine(Active(root), area);
                HistoryPaths.RequireDirectory(path);
                if (Directory.EnumerateFileSystemEntries(path).Any()) warnings.Add("未清理变化或未知材料：" + path);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { warnings.Add(error.Message); }
        }
        try { ArchiveTerminal(root); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { warnings.Add(error.Message); }
        return warnings.Count == 0 ? null : "恢复已处理；部分临时材料仍保留：" + string.Join("；", warnings.Distinct().Take(3));
    }

    private static void ArchiveTerminal(string root)
    {
        var state = Read(root);
        if (state is null) return;
        if (state.Status is not ("Completed" or "Reverted")) throw new InvalidOperationException("恢复仍未完成。");
        var archive = Path.Combine(HistoryPaths.HistoryDirectory(root), "restore-records");
        HistoryPaths.CheckPath(archive);
        Directory.CreateDirectory(archive);
        var destination = Path.Combine(archive, state.Id.ToString("N"));
        HistoryPaths.CheckPath(destination);
        Directory.Move(Active(root), destination);
    }

    private static string Active(string root) => Path.Combine(HistoryPaths.HistoryDirectory(HistoryPaths.Absolute(root)), ActiveName);

    private static string? OriginalsDirectory(string root, InPlaceRestoreState state) => state.RetainedOriginalSet.Count == 0 ? null :
        HistoryPaths.Attributes(Active(root)) is not null ? Path.Combine(Active(root), "original") :
            Path.Combine(HistoryPaths.HistoryDirectory(root), "restore-records", state.Id.ToString("N"), "original");

    private static string UndoMaterial(string root, InPlaceRestoreState state, int index) =>
        state.Steps[index].Kind == "RemoveFile" && state.RetainedOriginalSet.Contains(state.Steps[index].Entry.Path)
            ? HistoryPaths.ResolveWorkPath(Path.Combine(Active(root), "original"), state.Steps[index].Entry.Path)
            : Material(root, "undo", index);

    private static string Material(string root, string area, int index)
    {
        var path = Path.Combine(Active(root), area, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        HistoryPaths.CheckPath(path);
        return path;
    }
}
