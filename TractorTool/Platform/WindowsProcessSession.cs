using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using TractorTool.Contracts.Processes;
using TractorTool.Core.Processes;

namespace TractorTool.Platform;

/// <summary>Windows 10+ atomic job assignment. The process cannot spawn outside our job before attachment.</summary>
internal sealed partial class WindowsProcessSession : IProcessSession
{
    private readonly SafeJobHandle _job;
    private readonly SafeWaitHandle _process;
    private readonly ProcessWaitHandle _signal;
    private readonly RegisteredWaitHandle _wait;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public Task<int> ExitCode => _exit.Task;

    private WindowsProcessSession(SafeJobHandle job, SafeWaitHandle process,
        AnonymousPipeServerStream output, AnonymousPipeServerStream error)
    {
        _job = job;
        _process = process;
        StandardOutput = new(output, Encoding.UTF8);
        StandardError = new(error, Encoding.UTF8);
        _signal = new(process);
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) =>
        {
            try
            {
                Check(GetExitCodeProcess(_process, out var code));
                _exit.TrySetResult(unchecked((int)code));
            }
            catch (Exception ex) { _exit.TrySetException(ex); }
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public static IProcessSession Start(ProcessRequest request)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            throw new PlatformNotSupportedException("受管理的工具进程当前需要 Windows 10 或更新版本。");
        var job = CreateJobObjectW(IntPtr.Zero, null);
        AnonymousPipeServerStream? output = null, error = null;
        SafeWaitHandle? process = null;
        var transferred = false;
        try
        {
            if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new JobExtendedLimits { Basic = new() { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            Check(SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<JobExtendedLimits>()));
            output = new(PipeDirection.In, HandleInheritability.Inheritable);
            error = new(PipeDirection.In, HandleInheritability.Inheritable);
            using var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
            using var attributes = new ProcessAttributes();
            attributes.Add(0x20002, [input.ClientSafePipeHandle.DangerousGetHandle(),
                output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle()]);
            attributes.Add(0x2000D, [job.DangerousGetHandle()]); // PROC_THREAD_ATTRIBUTE_JOB_LIST
            var startup = new StartupInfoEx
            {
                Startup = new()
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Flags = 0x100, // STARTF_USESTDHANDLES
                    Input = input.ClientSafePipeHandle.DangerousGetHandle(),
                    Output = output.ClientSafePipeHandle.DangerousGetHandle(),
                    Error = error.ClientSafePipeHandle.DangerousGetHandle()
                },
                Attributes = attributes.Pointer
            };
            // No shell. CreateProcessW requires a command line; quote the argument vector using Windows CRT rules.
            var command = BuildCommandLine(request.ExecutablePath, request.Arguments);
            var environment = request.EnvironmentVariables is { Count: > 0 } changes
                ? Marshal.StringToHGlobalUni(BuildEnvironmentBlock(changes, ReadInheritedEnvironment())) : IntPtr.Zero;
            ProcessInformation info;
            try
            {
                Check(CreateProcessW(request.ExecutablePath, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x08080400, environment, request.WorkingDirectory, ref startup, out info)); // NO_WINDOW | EXTENDED_STARTUPINFO | UNICODE_ENVIRONMENT
            }
            finally { if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment); }
            process = new(info.Process, ownsHandle: true);
            using var thread = new SafeWaitHandle(info.Thread, ownsHandle: true);
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            input.DisposeLocalCopyOfClientHandle();
            // Disposing the remaining input writer at return delivers EOF, never an interactive console.
            var session = new WindowsProcessSession(job, process, output, error);
            transferred = true;
            return session;
        }
        finally
        {
            if (!transferred)
            {
                job.Dispose(); // Includes failed setup after creation: owned children cannot survive the job handle.
                process?.Dispose();
                output?.Dispose();
                error?.Dispose();
            }
        }
    }

    public Task StopTreeAsync(CancellationToken cancellationToken)
    {
        if (IsTreeEmpty()) return Task.CompletedTask;
        // Only teardown needs a blocking native wait; no worker or notification queue during normal execution.
        return Task.Run(() => StopTree(cancellationToken));
    }

    private void StopTree(CancellationToken cancellationToken)
    {
        using var port = CreateIoCompletionPort(new IntPtr(-1), IntPtr.Zero, 0, 1);
        if (port.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var association = new JobCompletionPort { Port = port.DangerousGetHandle() };
        Check(SetInformationJobObject(_job, 7, ref association, Marshal.SizeOf<JobCompletionPort>()));
        try
        {
            Check(TerminateJobObject(_job, 0xC000013A));
            if (IsTreeEmpty()) return;
            // Posting wakes cancellation promptly. The native timeout also bounds a failed wakeup.
            using var cancel = cancellationToken.Register(() => PostQueuedCompletionStatus(port, 0, 1, IntPtr.Zero));
            var clock = Stopwatch.StartNew();
            while (!cancellationToken.IsCancellationRequested && clock.ElapsedMilliseconds < 3000)
            {
                var remaining = (uint)Math.Max(1, 3000 - clock.ElapsedMilliseconds);
                if (!GetQueuedCompletionStatus(port, out var message, out var key, out _, remaining))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error != 258) throw new Win32Exception(error); // WAIT_TIMEOUT
                    break;
                }
                if (key == 1) break;
                if (message == 4 && IsTreeEmpty()) return; // JOB_OBJECT_MSG_ACTIVE_PROCESS_ZERO
            }
            // Job notifications are not guaranteed: confirm state once at the deadline before reporting failure.
            if (IsTreeEmpty()) return;
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("工具进程树未在 3 秒内退出。");
        }
        finally
        {
            association.Port = IntPtr.Zero;
            Check(SetInformationJobObject(_job, 7, ref association, Marshal.SizeOf<JobCompletionPort>()));
        }
    }

    private bool IsTreeEmpty()
    {
        Check(QueryInformationJobObject(_job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero));
        return accounting.ActiveProcesses == 0;
    }

    public void Dispose()
    {
        _job.Dispose();
        _wait.Unregister(null);
        _signal.Dispose();
        _process.Dispose();
        _exit.TrySetCanceled();
        StandardOutput.Dispose();
        StandardError.Dispose();
    }

    internal static StringBuilder BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var command = new StringBuilder();
        foreach (var value in new[] { executable }.Concat(arguments))
        {
            if (command.Length != 0) command.Append(' ');
            command.Append('"');
            var slashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { slashes++; continue; }
                command.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                command.Append(character);
                slashes = 0;
            }
            command.Append('\\', slashes * 2).Append('"');
        }
        if (command.Length >= 32767) throw new ArgumentException("Windows 工具参数超过命令行长度限制。");
        return command;
    }

    internal static string BuildEnvironmentBlock(IReadOnlyDictionary<string, string?> changes, IDictionary inherited)
    {
        // Never set Environment variables on the host: concurrent invocations must remain independent.
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in inherited) variables.Add((string)entry.Key, (string)entry.Value!);
        foreach (var (name, value) in changes)
        {
            if (value is null) variables.Remove(name);
            else variables[name] = value;
        }
        var block = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            // A resource bound, not the smaller ANSI CreateProcess limit. Unicode blocks may exceed 32 KiB.
            if ((long)block.Length + name.Length + value.Length + 3 > 1024 * 1024)
                throw new ArgumentException("子进程环境超过 1 Mi 字符。", nameof(changes));
            block.Append(name).Append('=').Append(value).Append('\0');
        }
        if (block.Length == 0) block.Append('\0');
        return block.Append('\0').ToString();
    }

    private static IDictionary ReadInheritedEnvironment()
    {
        // Environment.GetEnvironmentVariables omits the Windows =C: / =D: drive-directory entries.
        // An explicit child block must preserve them as well as ordinary inherited variables.
        var pointer = GetEnvironmentStringsW();
        if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var offset = 0;
            while (Marshal.ReadInt16(pointer, offset * sizeof(char)) != 0)
            {
                var start = offset;
                while (Marshal.ReadInt16(pointer, offset * sizeof(char)) != 0)
                    if (++offset >= 1024 * 1024) throw new InvalidOperationException("继承的进程环境超过 1 Mi 字符。");
                var entry = Marshal.PtrToStringUni(IntPtr.Add(pointer, start * sizeof(char)), offset - start)!;
                var separator = entry.IndexOf('=', entry[0] == '=' ? 1 : 0);
                if (separator > 0) variables.TryAdd(entry[..separator], entry[(separator + 1)..]);
                offset++;
            }
            return variables;
        }
        finally { _ = FreeEnvironmentStringsW(pointer); }
    }

    private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        // Share the SafeHandle itself: the registered wait must retain the same reference count as its owner.
        public ProcessWaitHandle(SafeWaitHandle process) => SafeWaitHandle = process;
    }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private sealed class ProcessAttributes : IDisposable
    {
        private readonly List<IntPtr> _values = [];
        public IntPtr Pointer { get; }
        public ProcessAttributes()
        {
            nuint size = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref size);
            if (size == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Pointer = Marshal.AllocHGlobal(checked((nint)size));
            if (!InitializeProcThreadAttributeList(Pointer, 2, 0, ref size))
            {
                var error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(Pointer);
                throw new Win32Exception(error);
            }
        }
        public void Add(nuint attribute, IntPtr[] handles)
        {
            var bytes = checked(handles.Length * IntPtr.Size);
            var value = Marshal.AllocHGlobal(bytes);
            _values.Add(value);
            Marshal.Copy(handles, 0, value, handles.Length);
            Check(UpdateProcThreadAttribute(Pointer, 0, attribute, value, (nuint)bytes, IntPtr.Zero, IntPtr.Zero));
        }
        public void Dispose()
        {
            DeleteProcThreadAttributeList(Pointer);
            foreach (var value in _values) Marshal.FreeHGlobal(value);
            Marshal.FreeHGlobal(Pointer);
        }
    }
}
