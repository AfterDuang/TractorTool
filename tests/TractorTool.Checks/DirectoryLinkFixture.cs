using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Use a real junction on Windows: unlike symbolic links, this needs no developer mode or elevation.
internal static class DirectoryLinkFixture
{
    // Unix cannot retain the directory attribute on a dangling symbolic link.
    internal static IEnumerable<bool> DanglingCases => OperatingSystem.IsWindows() ? [false, true] : [false];

    internal static void Create(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }

        var destination = Path.GetFullPath(target);
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + destination);
        var display = Encoding.Unicode.GetBytes(destination);
        var buffer = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003); // IO_REPARSE_TAG_MOUNT_POINT
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)display.Length));
        substitute.CopyTo(buffer, 16);
        display.CopyTo(buffer, 16 + substitute.Length + 2);

        Directory.CreateDirectory(link);
        using var handle = CreateFile(link, 0x40000000, 0, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, 0, 0, out _, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security,
        uint creation, uint flags, nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
        nint output, int outputSize, out uint returned, nint overlapped);
}
