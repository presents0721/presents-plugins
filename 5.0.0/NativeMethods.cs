using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace presents.OpsSuite;

/// <summary>
/// 纯 Win32 采集：宿主引用集里没有 System.Management，所以不用 WMI。
/// 这里提供三样东西：进程父链、进程命令行、TCP 监听端口归属。
/// 所有调用都做了失败降级，绝不因为权限不足把插件弄崩。
/// </summary>
internal static class NativeMethods
{
    const int TH32CS_SNAPPROCESS = 0x00000002;
    const int PROCESS_QUERY_INFORMATION = 0x0400;
    const int PROCESS_VM_READ = 0x0010;
    const int STATUS_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2A;
        public IntPtr Reserved2B;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(int access, bool inherit, int processId);

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr handle, int infoClass,
        ref PROCESS_BASIC_INFORMATION info, int infoLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr handle, IntPtr address, byte[] buffer,
        int size, out int bytesRead);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    static extern int GetExtendedTcpTable(IntPtr table, ref int size, bool ordered,
        int af, int tableClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    /// <summary>pid → (父 pid, 进程名)。失败时返回空表。</summary>
    public static Dictionary<int, (int ParentPid, string Name)> SnapshotProcessTree()
    {
        Dictionary<int, (int, string)> result = new();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;

        try
        {
            PROCESSENTRY32W entry = new() { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return result;

            do
            {
                int pid = (int)entry.th32ProcessID;
                if (pid >= 0 && !result.ContainsKey(pid))
                    result[pid] = ((int)entry.th32ParentProcessID, entry.szExeFile ?? "");
            }
            while (Process32NextW(snap, ref entry));
        }
        finally { CloseHandle(snap); }

        return result;
    }

    /// <summary>读取进程命令行；跨架构 / 权限不足时返回空串。</summary>
    public static string GetCommandLine(int pid)
    {
        if (pid <= 4) return "";
        IntPtr handle = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, pid);
        if (handle == IntPtr.Zero) return "";

        try
        {
            PROCESS_BASIC_INFORMATION pbi = new();
            int size = Marshal.SizeOf<PROCESS_BASIC_INFORMATION>();
            if (NtQueryInformationProcess(handle, 0, ref pbi, size, out _) != STATUS_SUCCESS)
                return "";

            // x64: PEB.ProcessParameters @ 0x20，RTL_USER_PROCESS_PARAMETERS.CommandLine @ 0x70
            int ptrSize = IntPtr.Size;
            byte[] ppBuffer = new byte[ptrSize];
            if (!Read(handle, pbi.PebBaseAddress + 0x20, ppBuffer)) return "";
            IntPtr parameters = new(BitConverter.ToInt64(ppBuffer, 0));
            if (parameters == IntPtr.Zero) return "";

            byte[] unicode = new byte[16];
            if (!Read(handle, parameters + 0x70, unicode)) return "";

            ushort length = BitConverter.ToUInt16(unicode, 0);
            long bufferAddress = BitConverter.ToInt64(unicode, 8);
            if (length == 0 || length > 65534 || bufferAddress == 0) return "";

            byte[] chars = new byte[length];
            if (!Read(handle, new IntPtr(bufferAddress), chars)) return "";
            return Encoding.Unicode.GetString(chars).TrimEnd('\0');
        }
        catch { return ""; }
        finally { CloseHandle(handle); }
    }

    static bool Read(IntPtr handle, IntPtr address, byte[] buffer)
    {
        if (address == IntPtr.Zero) return false;
        return ReadProcessMemory(handle, address, buffer, buffer.Length, out int read) && read == buffer.Length;
    }

    /// <summary>pid → 正在监听的 TCP 端口列表（IPv4）。失败返回空表。</summary>
    public static Dictionary<int, List<int>> GetListeningPorts()
    {
        Dictionary<int, List<int>> result = new();
        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;

        try
        {
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
            if (size <= 0) return result;

            IntPtr table = Marshal.AllocHGlobal(size);
            try
            {
                int rc = GetExtendedTcpTable(table, ref size, true, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
                if (rc != 0) return result;

                int count = Marshal.ReadInt32(table);
                int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr rowPtr = table + 4 + i * rowSize;
                    MIB_TCPROW_OWNER_PID row = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(rowPtr);
                    int port = (int)(((row.dwLocalPort & 0xFF) << 8) | ((row.dwLocalPort >> 8) & 0xFF));
                    int pid = (int)row.dwOwningPid;

                    if (!result.TryGetValue(pid, out List<int>? ports))
                        result[pid] = ports = new List<int>();
                    if (!ports.Contains(port)) ports.Add(port);
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        catch { /* 权限 / 平台差异都降级为空 */ }

        return result;
    }
}
