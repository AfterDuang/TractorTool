using TractorTool.Plugins.ClipboardHistory;

// Deterministic view-model checks with explicit fake results; never call the real Windows clipboard.
var failed = 0;
var passed = 0;
async Task Check(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool condition) { if (!condition) throw new Exception("Clipboard state assertion failed"); }
foreach (var status in new[] { HistoryStatus.Disabled, HistoryStatus.Denied, HistoryStatus.Success, HistoryStatus.Failed })
{
    await Check("Clipboard explicit state " + status, async () =>
    {
        var vm = new ClipboardHistoryViewModel(new Reader(_ => Task.FromResult(new HistoryResult(status, []))), CancellationToken.None);
        await vm.InitializeAsync();
        var expected = status switch { HistoryStatus.Disabled => "尚未开启", HistoryStatus.Denied => "拒绝", HistoryStatus.Success => "为空", _ => "无法读取" };
        Assert(vm.Status.Contains(expected) && !vm.IsLoading && vm.Items.Count == 0);
        vm.OnNavigatedFrom();
    });
}
await Check("Clipboard initialization once and manual refresh", async () =>
{
    int calls = 0;
    var vm = new ClipboardHistoryViewModel(new Reader(_ => Task.FromResult(new HistoryResult(HistoryStatus.Success,
        [new("fixture time", "Synthetic text " + ++calls, "")]))), CancellationToken.None);
    await vm.InitializeAsync(); await vm.InitializeAsync();
    Assert(calls == 1 && vm.Items.Single().Text.EndsWith('1'));
    await vm.RefreshCommand.ExecuteAsync(null); Assert(calls == 2 && vm.Items.Single().Text.EndsWith('2'));
    vm.OnNavigatedFrom(); Assert(vm.Items.Count == 0 && !vm.RefreshCommand.CanExecute(null));
});
await Check("Clipboard late completion cannot repopulate departed page", async () =>
{
    var response = new TaskCompletionSource<HistoryResult>();
    CancellationToken received = default;
    var vm = new ClipboardHistoryViewModel(new Reader(token => { received = token; return response.Task; }), CancellationToken.None);
    var loading = vm.InitializeAsync(); Assert(vm.IsLoading);
    vm.OnNavigatedFrom(); Assert(received.IsCancellationRequested);
    response.SetResult(new(HistoryStatus.Success, [new("fixture", "Synthetic late text", "")]));
    await loading; Assert(vm.Items.Count == 0 && !vm.IsLoading && !vm.CanRefresh);
});
await Check("Clipboard access error excludes exception contents", async () =>
{
    var vm = new ClipboardHistoryViewModel(new Reader(_ => throw new UnauthorizedAccessException("SENSITIVE_FIXTURE")), CancellationToken.None);
    await vm.InitializeAsync(); Assert(vm.Status.Contains("拒绝") && !vm.Status.Contains("SENSITIVE_FIXTURE"));
    vm.OnNavigatedFrom();
});
await Check("Clipboard application cancellation blocks late results", async () =>
{
    using var stopping = new CancellationTokenSource();
    var response = new TaskCompletionSource<HistoryResult>();
    var vm = new ClipboardHistoryViewModel(new Reader(_ => response.Task), stopping.Token);
    var loading = vm.InitializeAsync(); stopping.Cancel();
    response.SetResult(new(HistoryStatus.Success, [new("fixture", "Synthetic text", "")]));
    await loading; Assert(vm.Items.Count == 0 && !vm.CanRefresh); vm.OnNavigatedFrom();
});
Console.WriteLine($"RESULT clipboard-model passed={passed} failed={failed}; Windows API was not called.");
return failed == 0 ? 0 : 1;

sealed class Reader(Func<CancellationToken, Task<HistoryResult>> read) : IClipboardHistoryReader
{
    public Task<HistoryResult> ReadAsync(CancellationToken token) => read(token);
}
