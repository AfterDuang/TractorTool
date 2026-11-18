using System.Collections.Concurrent;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using TractorTool.Plugins.FolderHistory.Storage;

namespace TractorTool.Plugins.FolderHistory.ViewModels;

internal sealed partial class FolderHistoryViewModel
{
    private WorkingDirectoryListing? _directoryListing;
    private FolderRegistration? _watchedFolder;
    private CancellationTokenSource? _directoryTracking;
    private Channel<bool>? _directoryUpdates;
    private string[] _treeExclusions = [];
    private bool _watchFailed;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLiveTreeWarning))] private string _liveTreeWarning = "";
    public bool HasLiveTreeWarning => LiveTreeWarning.Length != 0;

    private void StopDirectoryTracking()
    {
        _directoryTracking?.Cancel();
        _watchedFolder = null;
        _directoryTracking = null;
        _directoryUpdates = null;
        LiveTreeWarning = "";
    }

    private void TrackDirectory(FolderItem folder, string[] exclusions)
    {
        if (_directoryUpdates is not null && !_watchFailed && _watchedFolder is { } watched && watched.Id == folder.Info.Id &&
            HistoryPaths.Comparer.Equals(watched.Root, folder.Root) &&
            exclusions.SequenceEqual(_treeExclusions, HistoryPaths.Comparer))
        {
            _directoryUpdates.Writer.TryWrite(true);
            return;
        }
        StopDirectoryTracking();
        _treeExclusions = exclusions;
        _watchFailed = false;
        var request = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        var updates = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
        _directoryTracking = request;
        _watchedFolder = new(folder.Root, folder.Info.Id);
        _directoryUpdates = updates;
        _ = ObserveDirectoryAsync(folder.Root, folder.Info.Id, exclusions, updates, request);
    }

    private async Task ObserveDirectoryAsync(string root, Guid repositoryId, string[] exclusions, Channel<bool> updates, CancellationTokenSource request)
    {
        using var watcher = new FileSystemWatcher();
        var token = request.Token;
        using var parentWatcher = new FileSystemWatcher();
        using var cancelWatchers = token.Register(() => { watcher.Dispose(); parentWatcher.Dispose(); });
        var rootName = Path.GetFileName(root);
        var generation = 0;
        var boundGeneration = -1;
        var visibleParents = new ConcurrentDictionary<string, byte>(HistoryPaths.Comparer);
        Exception? parentWatchError = null;
        Exception? watchError = null;
        void Rebind()
        {
            Interlocked.Increment(ref generation);
            updates.Writer.TryWrite(true);
        }
        void SignalRoot(string? name)
        {
            if (HistoryPaths.Comparer.Equals(name, rootName)) Rebind();
        }
        void Signal(string? name)
        {
            if (name is null) return;
            var relative = name.Replace(Path.DirectorySeparatorChar, '/');
            var split = relative.LastIndexOf('/');
            var parent = split < 0 ? "" : relative[..split];
            // Atomic replacement of the identity file also recovers a directory restored in stages.
            if (HistoryPaths.Comparer.Equals(relative, HistoryPaths.DirectoryName) ||
                HistoryPaths.Comparer.Equals(relative, HistoryPaths.DirectoryName + "/" + HistoryStore.StateName) ||
                !HistoryPaths.IsHistoryPath(relative) && Volatile.Read(ref visibleParents).ContainsKey(parent))
                updates.Writer.TryWrite(true);
        }

        try
        {
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes;
            watcher.Created += (_, e) => Signal(e.Name);
            watcher.Deleted += (_, e) => Signal(e.Name);
            watcher.Changed += (_, e) => Signal(e.Name);
            watcher.Renamed += (_, e) => { Signal(e.OldName); Signal(e.Name); };
            watcher.Error += (_, e) =>
            {
                if (e.GetException() is not InternalBufferOverflowException)
                    Interlocked.Exchange(ref watchError, e.GetException());
                updates.Writer.TryWrite(true);
            };
            try
            {
                // A watcher opened on the directory follows its handle when the directory itself moves.
                parentWatcher.Path = Path.GetDirectoryName(root)!;
                parentWatcher.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName;
                parentWatcher.Created += (_, e) => SignalRoot(e.Name);
                parentWatcher.Deleted += (_, e) => SignalRoot(e.Name);
                parentWatcher.Renamed += (_, e) => { SignalRoot(e.OldName); SignalRoot(e.Name); };
                parentWatcher.Error += (_, e) =>
                {
                    if (e.GetException() is not InternalBufferOverflowException)
                        Interlocked.Exchange(ref parentWatchError, e.GetException());
                    Rebind();
                };
                parentWatcher.EnableRaisingEvents = true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
            { parentWatchError = error; }

            updates.Writer.TryWrite(true);
            while (await updates.Reader.WaitToReadAsync(token))
            {
                // Coalesce a burst of names, but still publish progress during continuous changes.
                await Task.Delay(100, token);
                updates.Reader.TryRead(out _);
                var epoch = Volatile.Read(ref generation);
                if (boundGeneration != epoch)
                {
                    boundGeneration = epoch;
                    try
                    {
                        watcher.EnableRaisingEvents = false;
                        Interlocked.Exchange(ref watchError, null);
                        watcher.Path = root;
                        watcher.EnableRaisingEvents = true;
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
                    { Interlocked.Exchange(ref watchError, error); }
                }
                var expanded = _expanded.ToHashSet(HistoryPaths.Comparer);
                var workspace = _workspace;
                var reading = new ConcurrentDictionary<string, byte>(HistoryPaths.Comparer);
                Volatile.Write(ref visibleParents, reading);
                var (listing, visible) = await Task.Run(() =>
                {
                    var disk = WorkingDirectoryListing.Read(root, repositoryId, expanded, exclusions, token,
                        directory => reading.TryAdd(directory, 0));
                    return (disk, workspace?.VisibleTree(expanded, disk, token));
                }, token);
                token.ThrowIfCancellationRequested();
                if (epoch != Volatile.Read(ref generation) || !ReferenceEquals(workspace, _workspace) || !_expanded.SetEquals(expanded)) continue;
                _directoryListing = listing;
                _watchFailed = Volatile.Read(ref watchError) is not null || Volatile.Read(ref parentWatchError) is not null;
                LiveTreeWarning = listing.Warning.Length != 0 ? listing.Warning : _watchFailed ? "实时监听不可用，请使用“更多 → 刷新”。" : "";
                if (visible is null || VisibleTreeItems.SequenceEqual(visible)) continue;
                var previous = _scope;
                _settingSelection = true;
                try { RebuildTree(visible); }
                finally { _settingSelection = false; }
                if (!previous.SetEquals(_scope)) UpdateTimeline(SelectedVersion?.Id, null, null);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!token.IsCancellationRequested)
            {
                _watchFailed = true;
                LiveTreeWarning = "无法更新文件树：" + error.GetBaseException().Message;
            }
        }
        finally
        {
            updates.Writer.TryComplete();
            if (ReferenceEquals(_directoryTracking, request)) _directoryTracking = null;
            request.Dispose();
        }
    }
}
