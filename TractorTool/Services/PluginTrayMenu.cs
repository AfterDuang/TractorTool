using Avalonia.Controls;
using Avalonia.Threading;
using TractorTool.Contracts;
using TractorTool.Core.Storage;
using TractorTool.UI;

namespace TractorTool.Services;

internal sealed class PluginTrayMenu(string pluginId, string name, IAppLog log) : IPluginTrayMenu, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Item> _items = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private bool _committed;
    private TaskCompletionSource? _shutdown;
    private Action? _remove;
    public NativeMenuItem Group { get; } = new(name) { Menu = new NativeMenu() };

    public IPluginTrayItem Add(string id, PluginTrayItemState state, Func<CancellationToken, Task> execute)
    {
        Dispatcher.UIThread.VerifyAccess();
        AppPaths.ValidateId(id);
        Validate(state);
        ArgumentNullException.ThrowIfNull(execute);
        lock (_gate)
        {
            if (_committed || _shutdown is not null) throw new InvalidOperationException("只能在插件菜单注册期间添加项目。");
            if (_items.ContainsKey(id)) throw new InvalidDataException("插件菜单 ID 重复。");
            var item = new Item(this, state, execute);
            _items.Add(id, item);
            Group.Menu!.Items.Add(item.Native);
            Apply(item);
            return item;
        }
    }

    public void Commit(Action remove)
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (_gate) { _committed = true; _remove = remove; }
    }

    private static void Validate(PluginTrayItemState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrWhiteSpace(state.Text)) throw new ArgumentException("菜单文字不能为空。", nameof(state));
    }

    private void Update(Item item, PluginTrayItemState state)
    {
        lock (_gate)
        {
            if (_shutdown is not null) return;
            Validate(state);
            item.State = state;
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply(item);
        else Dispatcher.UIThread.Post(() => Apply(item));
    }

    private void Apply(Item item)
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (_gate)
        {
            if (_shutdown is not null) return;
            item.Native.Header = item.State.Text;
            item.Native.ToggleType = item.State.IsChecked.HasValue ? MenuItemToggleType.CheckBox : MenuItemToggleType.None;
            item.Native.IsChecked = item.State.IsChecked ?? false;
            item.Native.IsEnabled = item.State.IsEnabled && item.Running is null;
        }
    }

    private async Task ExecuteAsync(Item item)
    {
        Dispatcher.UIThread.VerifyAccess();
        Func<CancellationToken, Task> execute;
        TaskCompletionSource completion;
        string text;
        lock (_gate)
        {
            if (!_committed || _shutdown is not null || !item.State.IsEnabled || item.Running is not null) return;
            execute = item.Execute!;
            text = item.State.Text;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            item.Running = completion.Task;
        }
        try
        {
            Apply(item);
            // Start on the UI thread so short window actions work; plugins must await expensive work.
            await execute(_stopping.Token);
            log.Write(pluginId, $"托盘操作完成：{text}");
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { log.Write(pluginId, $"托盘操作已取消：{text}"); }
        catch (Exception ex) { log.Write(pluginId, $"托盘操作失败：{text}", ex); }
        finally
        {
            lock (_gate) item.Running = null;
            try { Apply(item); }
            finally { completion.TrySetResult(); }
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] running;
        lock (_gate)
        {
            if (_shutdown is not null) return new(_shutdown.Task);
            completion = _shutdown = new(TaskCreationOptions.RunContinuationsAsynchronously);
            running = _items.Values.Select(i => i.Running).OfType<Task>().ToArray();
            foreach (var item in _items.Values) item.Execute = null;
        }
        _ = StopAsync(running, completion);
        return new(completion.Task);
    }

    private async Task StopAsync(Task[] running, TaskCompletionSource completion)
    {
        try
        {
            var cancellation = _stopping.CancelAsync();
            try { await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Group.IsEnabled = false;
                foreach (var item in _items.Values) { item.Native.IsEnabled = false; item.Native.Click -= item.OnClick; }
                _remove?.Invoke();
                _remove = null;
                Group.Menu!.Items.Clear();
            }); }
            catch (Exception ex) { log.Write(pluginId, "移除插件托盘菜单失败", ex); }
            try { await cancellation; }
            catch (Exception ex) { log.Write(pluginId, "托盘操作取消回调失败", ex); }
            await Task.WhenAll(running);
            _items.Clear();
            _stopping.Dispose();
        }
        catch (Exception ex) { log.Write(pluginId, "释放插件托盘菜单失败", ex); }
        finally { completion.TrySetResult(); }
    }

    private sealed class Item : IPluginTrayItem
    {
        private readonly PluginTrayMenu _owner;
        public PluginTrayItemState State;
        public Func<CancellationToken, Task>? Execute;
        public Task? Running;
        public NativeMenuItem Native { get; } = new();
        public EventHandler OnClick { get; }

        public Item(PluginTrayMenu owner, PluginTrayItemState state, Func<CancellationToken, Task> execute)
        {
            _owner = owner; State = state; Execute = execute;
            OnClick = (_, _) => _ = owner.ExecuteAsync(this);
            Native.Click += OnClick;
        }
        public void Update(PluginTrayItemState state) => _owner.Update(this, state);
    }
}
