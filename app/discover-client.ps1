[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)

# WeGame 启动的客户端可能不向 WMI 返回参数；使用 Windows 的有限查询权限读取。
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class TimoClientProcess {
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder path, ref int length);
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr handle, int info, IntPtr buffer, int size, out int length);

    [StructLayout(LayoutKind.Sequential)]
    struct UnicodeString {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    public static string CommandLine(int pid) {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        IntPtr buffer = IntPtr.Zero;
        try {
            int length;
            NtQueryInformationProcess(handle, 60, IntPtr.Zero, 0, out length);
            if (length < 1 || length > 1024 * 1024) return null;
            buffer = Marshal.AllocHGlobal(length);
            if (NtQueryInformationProcess(handle, 60, buffer, length, out length) != 0) return null;
            var value = (UnicodeString)Marshal.PtrToStructure(buffer, typeof(UnicodeString));
            return Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
        } finally {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseHandle(handle);
        }
    }

    public static string ImagePath(int pid) {
        var handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) return null;
        try {
            var path = new StringBuilder(32768);
            int length = path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref length) ? path.ToString() : null;
        } finally {
            CloseHandle(handle);
        }
    }
}
'@

Get-CimInstance Win32_Process -Filter "Name = 'LeagueClientUx.exe' OR Name = 'LeagueClient.exe'" |
    ForEach-Object {
        [PSCustomObject]@{
            Name = $_.Name
            ProcessId = $_.ProcessId
            CommandLine = if ($_.CommandLine) { $_.CommandLine } else { [TimoClientProcess]::CommandLine($_.ProcessId) }
            ExecutablePath = if ($_.ExecutablePath) { $_.ExecutablePath } else { [TimoClientProcess]::ImagePath($_.ProcessId) }
        }
    } | ConvertTo-Json -Compress
