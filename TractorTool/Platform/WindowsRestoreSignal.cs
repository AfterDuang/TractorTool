using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TractorTool.Core.Storage;

namespace TractorTool.Platform;

/// <summary>Reopening the same profile restores a hidden window even if Explorer lost its tray icon.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsRestoreSignal : IDisposable
{
    private readonly EventWaitHandle _event;
    private RegisteredWaitHandle? _wait;
    public bool IsExisting { get; }
    public WindowsRestoreSignal(string profile)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Absolute(profile).ToUpperInvariant())));
        _event = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TractorTool-" + id, out var created);
        IsExisting = !created;
        if (IsExisting) _event.Set();
    }
    public void Listen(Action restore) => _wait = ThreadPool.RegisterWaitForSingleObject(_event, (_, _) => restore(), null, Timeout.Infinite, false);
    public void Dispose() { _wait?.Unregister(null); _event.Dispose(); }
}
