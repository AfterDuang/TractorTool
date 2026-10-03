namespace TractorTool.Core.Storage;

/// <summary>The fixed bootstrap location, not DataRoot, identifies the running user profile.</summary>
public sealed class InstanceLease : IDisposable
{
    private readonly FileStream _file;
    public InstanceLease(string bootstrapRoot)
    {
        Directory.CreateDirectory(bootstrapRoot);
        try { _file = new FileStream(Path.Combine(bootstrapRoot, "host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new IOException("该配置已有运行实例，或启动目录被占用。请从系统托盘恢复窗口，或检查目录权限。", ex); }
    }
    public void Dispose() => _file.Dispose();
}
