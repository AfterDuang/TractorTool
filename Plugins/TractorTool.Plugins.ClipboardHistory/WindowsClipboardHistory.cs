using Windows.ApplicationModel.DataTransfer;

namespace TractorTool.Plugins.ClipboardHistory;

public enum HistoryStatus { Success, Disabled, Denied, Failed }
public sealed record HistoryEntry(string Timestamp, string Text, string Detail);
public sealed record HistoryResult(HistoryStatus Status, IReadOnlyList<HistoryEntry> Items);

public interface IClipboardHistoryReader
{
    Task<HistoryResult> ReadAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsClipboardHistory : IClipboardHistoryReader
{
    // Called on the focused UI thread; never log, save, or listen for clipboard contents.
    public async Task<HistoryResult> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await Clipboard.GetHistoryItemsAsync().AsTask(cancellationToken);
        if (result.Status != ClipboardHistoryItemsResultStatus.Success)
            return new(result.Status == ClipboardHistoryItemsResultStatus.ClipboardHistoryDisabled
                ? HistoryStatus.Disabled : result.Status == ClipboardHistoryItemsResultStatus.AccessDenied
                    ? HistoryStatus.Denied : HistoryStatus.Failed, []);
        var entries = new List<HistoryEntry>();
        foreach (var item in result.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string timestamp = "", text = "", detail = "";
            try
            {
                timestamp = item.Timestamp.ToLocalTime().ToString("g");
                if (item.Content.Contains(StandardDataFormats.Text))
                {
                    text = await item.Content.GetTextAsync().AsTask(cancellationToken);
                    // Bound rendering costs; full contents remain owned by Windows.
                    if (text.Length > 20000) { text = text[..20000]; detail = "内容较长，仅展示前 20,000 个字符。"; }
                    else if (text.Length == 0) detail = "此项目的文本为空。";
                }
                else detail = "此项目不是文本，当前版本暂不展示此类型。";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { detail = "此项目已失效或暂时无法读取，请刷新后再试。"; }
            entries.Add(new(timestamp, text, detail));
        }
        return new(HistoryStatus.Success, entries);
    }
}
