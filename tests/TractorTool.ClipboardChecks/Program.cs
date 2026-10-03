using TractorTool.Plugins.ClipboardHistory;

// Controlled service fixtures only. Never read/write the real Windows clipboard in this program.
var failed = 0;
var passed = 0;
async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool condition) { if (!condition) throw new Exception("Clipboard state assertion failed"); }
HistoryEntry Entry(string id, HistoryKind kind = HistoryKind.Text) => new(id, "fixture time", kind, "Synthetic summary " + id);
HistoryResult Snapshot(params HistoryEntry[] entries) => new(HistoryStatus.Success, entries);
ClipboardHistoryViewModel Model(Service service, CancellationToken token = default) => new(service, token, action => action());

foreach (var status in new[] { HistoryStatus.Disabled, HistoryStatus.Denied, HistoryStatus.Success, HistoryStatus.Failed })
    await Check("Clipboard explicit state " + status, async () =>
    {
        var vm = Model(new(_ => Task.FromResult(new HistoryResult(status, []))));
        await vm.InitializeAsync();
        var expected = status switch { HistoryStatus.Disabled => "尚未开启", HistoryStatus.Denied => "无法访问", HistoryStatus.Success => "为空", _ => "无法读取" };
        Assert(vm.Status.Contains(expected) && !vm.IsLoading && vm.Items.Count == 0);
        vm.OnNavigatedFrom();
    });

await Check("Initialization subscribes once; manual refresh retries the current preview", async () =>
{
    var service = new Service(_ => Task.FromResult(Snapshot(Entry("one"))));
    var vm = Model(service);
    await vm.InitializeAsync(); await vm.InitializeAsync();
    Assert(service.Reads == 1 && service.Watches == 1 && service.Previews == 1);
    await vm.RefreshCommand.ExecuteAsync(null);
    Assert(service.Reads == 2 && service.Previews == 2);
    vm.OnNavigatedFrom(); Assert(service.Disposed && service.Subscribers == 0 && !vm.CanRefresh && !vm.CanCopy);
});
await Check("Events coalesce into one follow-up snapshot and preserve selection", async () =>
{
    var delayed = new TaskCompletionSource<HistoryResult>();
    var service = new Service(_ => Task.FromResult(Snapshot(Entry("one"), Entry("two"))));
    var vm = Model(service); await vm.InitializeAsync();
    vm.SelectedEntry = vm.Items[1]; var selected = vm.SelectedEntry; var preview = vm.Preview;
    service.Read = _ => service.Reads == 2 ? delayed.Task : Task.FromResult(Snapshot(Entry("new"), Entry("one"), Entry("two")));
    var pending = vm.RequestRefreshAsync(); service.Raise(); service.Raise(); service.Raise();
    Assert(vm.Items.Count == 2 && ReferenceEquals(vm.SelectedEntry, selected));
    delayed.SetResult(Snapshot(Entry("one"), Entry("two"))); await pending;
    Assert(service.Reads == 3 && vm.Items.Count == 3 && ReferenceEquals(vm.SelectedEntry, selected) && ReferenceEquals(vm.Preview, preview));
    vm.OnNavigatedFrom();
});
await Check("A failed read still consumes an event received during that read", async () =>
{
    var delayed = new TaskCompletionSource<HistoryResult>();
    var service = new Service(_ => delayed.Task);
    var vm = Model(service); var initial = vm.InitializeAsync(); service.Raise();
    service.Read = _ => Task.FromResult(Snapshot(Entry("recovered")));
    delayed.SetException(new IOException("fixture")); await initial;
    Assert(service.Reads == 2 && vm.Items.Single().Id == "recovered"); vm.OnNavigatedFrom();
});
await Check("Copy uses original id even for types without a preview", async () =>
{
    var service = new Service(_ => Task.FromResult(Snapshot(Entry("opaque-original", HistoryKind.Other))));
    var vm = Model(service); await vm.InitializeAsync(); vm.CopyCommand.Execute(null);
    Assert(service.CopiedId == "opaque-original" && vm.CopyStatus.Contains("已复制")); vm.OnNavigatedFrom();
});
foreach (var status in new[] { HistoryCopyStatus.Denied, HistoryCopyStatus.Deleted, HistoryCopyStatus.Failed })
    await Check("Copy reports system outcome " + status, async () =>
    {
        var service = new Service(_ => Task.FromResult(Snapshot(Entry("one")))) { CopyResult = status };
        var vm = Model(service); await vm.InitializeAsync(); vm.CopyCommand.Execute(null);
        var expected = status switch { HistoryCopyStatus.Denied => "拒绝", HistoryCopyStatus.Deleted => "移除", _ => "失败" };
        Assert(vm.CopyStatus.Contains(expected)); vm.OnNavigatedFrom();
    });
await Check("A slow old preview cannot replace the current selected item", async () =>
{
    var late = new TaskCompletionSource<HistoryPreview>();
    var current = new TaskCompletionSource<HistoryPreview>();
    var service = new Service(_ => Task.FromResult(Snapshot(Entry("one"), Entry("two"))))
    { Preview = (id, _) => id == "one" ? late.Task : current.Task };
    var vm = Model(service); await vm.InitializeAsync(); var oldPreview = vm.PreviewCompletion; vm.SelectedEntry = vm.Items[1];
    var currentPreview = vm.PreviewCompletion;
    late.SetResult(new("stale", ""));
    await oldPreview;
    Assert(vm.SelectedEntry?.Id == "two" && vm.Preview is null && vm.IsPreviewLoading);
    current.SetResult(new("second", "")); await currentPreview;
    Assert(vm.Preview?.Text == "second" && !vm.IsPreviewLoading); vm.OnNavigatedFrom();
});
await Check("Manual refresh recovers a transient preview failure with identical metadata", async () =>
{
    var service = new Service(_ => Task.FromResult(Snapshot(Entry("image", HistoryKind.Image))))
    { Preview = (_, _) => throw new IOException("SENSITIVE_FIXTURE") };
    var vm = Model(service); await vm.InitializeAsync(); Assert(vm.Preview?.Detail.Contains("无法预览") == true);
    service.Preview = (_, _) => Task.FromResult(new HistoryPreview("recovered", ""));
    await vm.RefreshCommand.ExecuteAsync(null);
    Assert(service.Previews == 2 && vm.Preview?.Text == "recovered"); vm.OnNavigatedFrom();
});
await Check("Departed page rejects late history, late preview and queued events", async () =>
{
    var queued = new Queue<Action>();
    var delayed = new TaskCompletionSource<HistoryResult>();
    CancellationToken received = default;
    var service = new Service(token => { received = token; return delayed.Task; });
    var vm = new ClipboardHistoryViewModel(service, CancellationToken.None, queued.Enqueue);
    var pending = vm.InitializeAsync(); service.Raise(); vm.OnNavigatedFrom();
    Assert(received.IsCancellationRequested && service.Subscribers == 0);
    delayed.SetResult(Snapshot(Entry("late"))); await pending;
    while (queued.TryDequeue(out var action)) action();
    Assert(vm.Items.Count == 0 && vm.Preview is null && !vm.IsLoading && service.Reads == 1);
    var preview = new TaskCompletionSource<HistoryPreview>();
    service = new(_ => Task.FromResult(Snapshot(Entry("one")))) { Preview = (_, _) => preview.Task };
    vm = Model(service); await vm.InitializeAsync(); var oldPreview = vm.PreviewCompletion; vm.OnNavigatedFrom();
    preview.SetResult(new("late image substitute", "")); await oldPreview; Assert(vm.Preview is null && !vm.CanCopy);
});
await Check("Temporary access denial retains selection; disabled history clears it", async () =>
{
    var service = new Service(_ => Task.FromResult(Snapshot(Entry("one"))));
    var vm = Model(service); await vm.InitializeAsync(); var selected = vm.SelectedEntry;
    service.Read = _ => Task.FromResult(new HistoryResult(HistoryStatus.Denied, []));
    await vm.RequestRefreshAsync(); Assert(ReferenceEquals(vm.SelectedEntry, selected));
    service.Read = _ => Task.FromResult(new HistoryResult(HistoryStatus.Disabled, []));
    await vm.RequestRefreshAsync(); Assert(vm.SelectedEntry is null && vm.Preview is null && vm.Items.Count == 0);
    vm.OnNavigatedFrom();
});
await Check("Removing a selected system item selects a remaining entry", async () =>
{
    var service = new Service(_ => Task.FromResult(Snapshot(Entry("one"), Entry("two"))));
    var vm = Model(service); await vm.InitializeAsync();
    service.Read = _ => Task.FromResult(Snapshot(Entry("two"))); await vm.RequestRefreshAsync();
    Assert(vm.SelectedEntry?.Id == "two"); vm.OnNavigatedFrom();
});
await Check("Watcher failure preserves manual refresh and excludes exception contents", async () =>
{
    var service = new Service(_ => Task.FromResult(Snapshot(Entry("one")))) { FailWatch = true };
    var vm = Model(service); await vm.InitializeAsync(); await vm.RefreshCommand.ExecuteAsync(null);
    Assert(vm.HasWatchError && !vm.WatchError.Contains("SENSITIVE_FIXTURE") && service.Reads == 2); vm.OnNavigatedFrom();
});
await Check("Access exceptions exclude clipboard details", async () =>
{
    var vm = Model(new(_ => throw new UnauthorizedAccessException("SENSITIVE_FIXTURE")));
    await vm.InitializeAsync(); Assert(vm.Status.Contains("拒绝") && !vm.Status.Contains("SENSITIVE_FIXTURE")); vm.OnNavigatedFrom();
});
await Check("Application cancellation blocks late results and new actions", async () =>
{
    using var stopping = new CancellationTokenSource();
    var response = new TaskCompletionSource<HistoryResult>();
    var vm = Model(new(_ => response.Task), stopping.Token);
    var loading = vm.InitializeAsync(); stopping.Cancel(); response.SetResult(Snapshot(Entry("late")));
    await loading; Assert(vm.Items.Count == 0 && !vm.CanRefresh && !vm.CanCopy); vm.OnNavigatedFrom();
});
Console.WriteLine($"RESULT clipboard-model passed={passed} failed={failed}; Windows API was not called.");
return failed == 0 ? 0 : 1;

sealed class Service(Func<CancellationToken, Task<HistoryResult>> read) : IClipboardHistoryService
{
    private EventHandler? _changed;
    public event EventHandler? HistoryChanged { add { _changed += value; Subscribers++; } remove { _changed -= value; Subscribers--; } }
    public int Subscribers { get; private set; }
    public int Reads { get; private set; }
    public int Previews { get; private set; }
    public int Watches { get; private set; }
    public bool Disposed { get; private set; }
    public bool FailWatch { get; init; }
    public string? CopiedId { get; private set; }
    public HistoryCopyStatus CopyResult { get; init; } = HistoryCopyStatus.Success;
    public Func<CancellationToken, Task<HistoryResult>> Read { get; set; } = read;
    public Func<string, CancellationToken, Task<HistoryPreview>> Preview { get; set; } = (id, _) => Task.FromResult(new HistoryPreview("Synthetic preview " + id, ""));
    public void StartWatching() { Watches++; if (FailWatch) throw new IOException("SENSITIVE_FIXTURE"); }
    public void Raise() => _changed?.Invoke(this, EventArgs.Empty);
    public Task<HistoryResult> ReadAsync(CancellationToken token) { Reads++; return Read(token); }
    public Task<HistoryPreview> PreviewAsync(string id, CancellationToken token) { Previews++; return Preview(id, token); }
    public HistoryCopyStatus Copy(string id) { CopiedId = id; return CopyResult; }
    public void Dispose() => Disposed = true;
}
