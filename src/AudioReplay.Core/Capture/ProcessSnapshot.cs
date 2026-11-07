using System.Runtime.InteropServices;

namespace AudioReplay.Core.Capture;

internal sealed record ProcInfo(uint Pid, uint ParentPid, string ExeName);

/// <summary>
/// Lists processes through the Toolhelp snapshot API. Unlike <c>Process.GetProcessById</c>/<c>OpenProcess</c>
/// this does NOT open a handle to any process, so protected (anti-cheat guarded) games are never touched.
/// </summary>
internal static class ProcessSnapshot
{
    private const uint Th32CsSnapProcess = 0x2;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static Dictionary<uint, ProcInfo> Take()
    {
        var result = new Dictionary<uint, ProcInfo>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandle) return result;

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return result;

            do
            {
                result[entry.ProcessId] = new ProcInfo(entry.ProcessId, entry.ParentProcessId, entry.ExeFile);
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
