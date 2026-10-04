using System.ComponentModel;
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
    private readonly Action? _pollingWarning;
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public Task<int> ExitCode => _exit.Task;

    private WindowsProcessSession(SafeJobHandle job, SafeWaitHandle process,
        AnonymousPipeServerStream output, AnonymousPipeServerStream error, Action? pollingWarning)
    {
        _job = job;
        _pollingWarning = pollingWarning;
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

    public static IProcessSession Start(ProcessRequest request) => Start(request, null);

    internal static IProcessSession Start(ProcessRequest request, Action? pollingWarning)
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
            Check(CreateProcessW(request.ExecutablePath, command, IntPtr.Zero, IntPtr.Zero, true,
                0x08080000, IntPtr.Zero, request.WorkingDirectory, ref startup, out var info)); // NO_WINDOW | EXTENDED_STARTUPINFO
            process = new(info.Process, ownsHandle: true);
            using var thread = new SafeWaitHandle(info.Thread, ownsHandle: true);
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            input.DisposeLocalCopyOfClientHandle();
            // Disposing the remaining input writer at return delivers EOF, never an interactive console.
            var session = new WindowsProcessSession(job, process, output, error, pollingWarning);
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

    public async Task StopTreeAsync(CancellationToken cancellationToken)
    {
        Check(QueryInformationJobObject(_job, 1, out var before, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero));
        if (before.ActiveProcesses == 0) return;
        Check(TerminateJobObject(_job, 0xC000013A));
        // Bounded cleanup only; normal process completion uses the OS wait above, with no idle polling.
        var warned = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Check(QueryInformationJobObject(_job, 1, out var accounting, Marshal.SizeOf<JobAccounting>(), IntPtr.Zero));
            if (accounting.ActiveProcesses == 0) return;
            if (!warned) { _pollingWarning?.Invoke(); warned = true; }
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
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
