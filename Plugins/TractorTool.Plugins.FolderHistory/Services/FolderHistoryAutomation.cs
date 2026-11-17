using TractorTool.Contracts;
using TractorTool.Plugins.FolderHistory.Models;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.Services;

// One due time per folder; events coalesce without periodic directory scans.
public sealed class CaptureSchedule
{
    public bool Pending { get; private set; }
    public long Generation { get; private set; }
    public DateTimeOffset LastChange { get; private set; }
    public DateTimeOffset? LastCompleted { get; private set; }

    public void Signal(DateTimeOffset now) { Pending = true; LastChange = now; Generation++; }
    public DateTimeOffset DueAt(FolderSettings settings)
    {
        var stable = LastChange.AddSeconds(settings.StableSeconds);
        var interval = LastCompleted?.AddSeconds(settings.MinimumIntervalSeconds) ?? DateTimeOffset.MinValue;
        return stable > interval ? stable : interval;
    }
    public void Complete(long capturedGeneration, DateTimeOffset now)
    {
        LastCompleted = now;
        if (capturedGeneration == Generation) Pending = false;
    }
    public void Defer(DateTimeOffset now) { LastCompleted = now; Signal(now); }
}

public sealed partial class FolderHistoryService
{
    private readonly object _observers = new();
    private readonly Dictionary<string, Observation> _observations = new(HistoryPaths.Comparer);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _automationTask;

    public Task RunAsync(CancellationToken stoppingToken)
    {
        lock (_lifetime)
        {
            if (_stopped) return Task.CompletedTask;
            return _automationTask ??= RunAutomationAsync(stoppingToken);
        }
    }

    private async Task RunAutomationAsync(CancellationToken stoppingToken)
    {
        using var stop = stoppingToken.Register(() => _stopping.Cancel());
        try
        {
            await ExclusiveAsync(token =>
            {
                foreach (var folder in _registry.Folders)
                {
                    try
                    {
                        _ = Open(folder.Root);
                        using var ownership = HistoryStore.Lock(folder.Root);
                        ConfigureObservation(folder.Root, Open(folder.Root), recheck: true);
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { SetRuntimeStatus(folder.Root, "需要处理：" + error.GetBaseException().Message, faulted: true); }
                }
                NotifyChanged();
                return Task.FromResult(true);
            }).ConfigureAwait(false);
            while (!_stopping.IsCancellationRequested)
            {
                string? nextRoot = null;
                var nextDue = DateTimeOffset.MaxValue;
                lock (_observers)
                    foreach (var pair in _observations)
                        if (pair.Value.Enabled && !pair.Value.Faulted && pair.Value.Schedule.Pending &&
                            pair.Value.Schedule.DueAt(pair.Value.Settings) < nextDue)
                        { nextRoot = pair.Key; nextDue = pair.Value.Schedule.DueAt(pair.Value.Settings); }
                if (nextRoot is null) { await _wake.WaitAsync(_stopping.Token).ConfigureAwait(false); continue; }
                var delay = nextDue - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero) { await _wake.WaitAsync(delay, _stopping.Token).ConfigureAwait(false); continue; }
                await CaptureAutomaticallyAsync(nextRoot).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        finally { StopObservations(); }
    }

    private Task CaptureAutomaticallyAsync(string root) => ExclusiveAsync(async token =>
    {
        long generation;
        lock (_observers)
        {
            if (!_observations.TryGetValue(root, out var observation) || !observation.Enabled || observation.Faulted ||
                !observation.Schedule.Pending || observation.Schedule.DueAt(observation.Settings) > DateTimeOffset.UtcNow) return false;
            generation = observation.Schedule.Generation;
        }
        try
        {
            var state = Open(root);
            if (!state.ProtectionEnabled) { ConfigureObservation(root, state, recheck: false); return false; }
            RequireNoPendingOperation(root, state);
            using var lease = await RequestEngineAsync(token, automatic: true).ConfigureAwait(false);
            using var ownership = HistoryStore.Lock(root);
            state = Open(root);
            RequireNoPendingOperation(root, state);
            if (!state.ProtectionEnabled) { ConfigureObservation(root, state, recheck: false); return false; }
            var engine = Engine(lease);
            // Startup and overflow use the same full scan as a change event.
            await CaptureAsync(root, state, engine, "自动保存", false, token, automatic: true).ConfigureAwait(false);
            lock (_observers)
                if (_observations.TryGetValue(root, out var observation))
                {
                    observation.Schedule.Complete(generation, DateTimeOffset.UtcNow);
                    if (!observation.Faulted) observation.Status = observation.Schedule.Pending ? "有更改待保存" : "已保存";
                }
            await ApplyRetentionAsync(root, state, engine, token).ConfigureAwait(false);
            NotifyChanged();
            return true;
        }
        catch (WorkingTreeChangedException) { return false; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            SetRuntimeStatus(root, "需要处理：" + error.GetBaseException().Message, faulted: true);
            _context.Log.Write(error is OperationCanceledException ? AppLogLevel.Info : AppLogLevel.Warning, _context.PluginId,
                "文件夹自动保护已暂停；请处理后重新开启：" + root, error is OperationCanceledException ? null : error);
            return false;
        }
    });

    private void DeferChangedCapture(string root)
    {
        lock (_observers)
            if (_observations.TryGetValue(root, out var observation))
            {
                observation.Schedule.Defer(DateTimeOffset.UtcNow);
                if (!observation.Faulted) observation.Status = observation.Enabled ? "有更改待保存，等待文件稳定" : "文件仍在变化，尚未保存完整版本";
            }
        WakeAutomation();
        NotifyChanged();
    }

    private void ConfigureObservation(string root, RepositoryState state, bool recheck)
    {
        lock (_observers)
        {
            if (_observations.Remove(root, out var old)) old.Watcher?.Dispose();
            var pending = InPlaceRestoreTransaction.ReadPending(root) is not null || state.PendingDeleteIds.Count > 0;
            var observation = new Observation
            {
                Enabled = state.ProtectionEnabled,
                Settings = state.Settings,
                Status = pending ? "需要处理未完成操作" : state.ProtectionEnabled ? "有更改待保存" : state.Versions.Count == 0 ? "尚无完整版本" : "已暂停",
                Faulted = pending
            };
            if (state.ProtectionEnabled && !pending && ReadCommitted(root, state).FirstOrDefault() is { } last)
                observation.Schedule.Complete(observation.Schedule.Generation, last.Version.CreatedAt);
            _observations[root] = observation;
            if (!state.ProtectionEnabled || pending || _stopping.IsCancellationRequested) return;
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 32 * 1024
            };
            watcher.Changed += (_, e) => ObserveChange(root, e.FullPath);
            watcher.Created += (_, e) => ObserveChange(root, e.FullPath);
            watcher.Deleted += (_, e) => ObserveChange(root, e.FullPath);
            watcher.Renamed += (_, e) => { ObserveChange(root, e.OldFullPath); ObserveChange(root, e.FullPath); };
            watcher.Error += (_, e) =>
            {
                if (e.GetException() is InternalBufferOverflowException) ObserveChange(root, null);
                else SetRuntimeStatus(root, "需要处理：目录监听不可用，请重新开启保护。", faulted: true);
            };
            observation.Watcher = watcher;
            watcher.EnableRaisingEvents = true;
            if (recheck) observation.Schedule.Signal(DateTimeOffset.UtcNow);
        }
        WakeAutomation();
    }

    private void ObserveChange(string root, string? fullPath)
    {
        var changed = false;
        lock (_observers)
        {
            if (_stopping.IsCancellationRequested || !_observations.TryGetValue(root, out var observation) ||
                !observation.Enabled) return;
            if (fullPath is not null)
            {
                var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
                if (HistoryPaths.IsHistoryPath(relative) || HistoryExclusions.IsExcluded(relative, observation.Settings.ExcludedPaths)) return;
            }
            changed = !observation.Schedule.Pending;
            observation.Schedule.Signal(DateTimeOffset.UtcNow);
            if (!observation.Faulted && observation.Status != "正在保存") observation.Status = "有更改待保存";
        }
        WakeAutomation();
        if (changed) NotifyChanged();
    }

    private void SetRuntimeStatus(string root, string status, bool? faulted = null)
    {
        lock (_observers)
        {
            if (!_observations.TryGetValue(root, out var observation)) _observations[root] = observation = new();
            if (faulted is null && observation.Faulted) return;
            observation.Status = status;
            if (faulted.HasValue) observation.Faulted = faulted.Value;
        }
        NotifyChanged();
    }

    private string? RuntimeStatus(string root)
    {
        lock (_observers) return _observations.TryGetValue(root, out var observation) ? observation.Status : null;
    }

    private bool RuntimeFaulted(string root)
    {
        lock (_observers) return _observations.TryGetValue(root, out var observation) && observation.Faulted;
    }

    private void RestoreObservationAfterMaintenance(string root)
    {
        lock (_observers)
            if (_observations.TryGetValue(root, out var observation) && !observation.Faulted)
            {
                observation.Status = !observation.Enabled ? "已暂停" : observation.Schedule.Pending ? "有更改待保存" : "已保存";
            }
        WakeAutomation();
        NotifyChanged();
    }

    private void WakeAutomation()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private void RemoveObservation(string root)
    {
        lock (_observers) if (_observations.Remove(root, out var observation)) observation.Watcher?.Dispose();
        WakeAutomation();
    }

    private void StopObservations()
    {
        lock (_observers)
        {
            foreach (var observation in _observations.Values) observation.Watcher?.Dispose();
            _observations.Clear();
        }
    }

    private sealed class Observation
    {
        public FileSystemWatcher? Watcher { get; set; }
        public CaptureSchedule Schedule { get; } = new();
        public FolderSettings Settings { get; init; } = new();
        public bool Enabled { get; init; }
        public bool Faulted { get; set; }
        public string Status { get; set; } = "已暂停";
    }
}
