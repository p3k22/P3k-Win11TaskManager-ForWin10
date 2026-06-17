using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Win11TaskMan.Interop;

/// <summary>
/// Per-process detail queries for the Details tab: owning user, architecture,
/// full image path and priority control. All best-effort — protected/system
/// processes return blanks when we lack rights (run elevated for more).
/// </summary>
internal static partial class NativeMethods
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_SET_INFORMATION = 0x0200;
    private const uint TOKEN_QUERY = 0x0008;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int len, out int retLen);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupAccountSidW(
        string? system, IntPtr sid, StringBuilder name, ref int cchName,
        StringBuilder domain, ref int cchDomain, out int use);

    /// <summary>DOMAIN\user that owns the process, or "" if not queryable.</summary>
    public static string GetProcessUserName(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(h, TOKEN_QUERY, out token)) return "";
            GetTokenInformation(token, 1 /*TokenUser*/, IntPtr.Zero, 0, out int len);
            if (len <= 0) return "";
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetTokenInformation(token, 1, buf, len, out _)) return "";
                IntPtr sid = Marshal.ReadIntPtr(buf); // TOKEN_USER.User.Sid
                var name = new StringBuilder(256);
                var domain = new StringBuilder(256);
                int cn = 256, cd = 256;
                if (!LookupAccountSidW(null, sid, name, ref cn, domain, ref cd, out _)) return "";
                return name.ToString();
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return ""; }
        finally { if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);

    /// <summary>"x64" / "x86" / "ARM64" / "" for the process.</summary>
    public static string GetProcessArchitecture(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            if (!IsWow64Process2(h, out ushort pm, out ushort nm)) return "";
            ushort machine = pm == 0 ? nm : pm; // 0 = not WOW64 -> native machine
            return machine switch
            {
                0x8664 => "x64",
                0x014c => "x86",
                0xAA64 => "ARM64",
                0x01c4 => "ARM",
                _ => ""
            };
        }
        catch { return ""; }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder buf, ref uint size);

    /// <summary>Full path to the process image, or "".</summary>
    public static string GetProcessImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024);
            uint n = 1024;
            return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : "";
        }
        catch { return ""; }
        finally { CloseHandle(h); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetPriorityClass(IntPtr h, uint priorityClass);

    public static bool SetProcessPriority(int pid, uint priorityClass)
    {
        IntPtr h = OpenProcess(PROCESS_SET_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try { return SetPriorityClass(h, priorityClass); }
        finally { CloseHandle(h); }
    }

    // ===================== Terminal-services sessions (Users tab) =====================
    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr server, int reserved, int version, out IntPtr sessionInfo, out int count);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(
        IntPtr server, uint sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    public readonly record struct SessionInfo(uint SessionId, string User, string Status);

    public static List<SessionInfo> GetSessions()
    {
        var list = new List<SessionInfo>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out IntPtr buf, out int count)) return list;
        try
        {
            int stride = IntPtr.Size == 8 ? 24 : 12; // WTS_SESSION_INFOW: id(4)+pad+ptr(8)+state(4)+pad
            for (int i = 0; i < count; i++)
            {
                IntPtr entry = buf + i * stride;
                uint id = (uint)Marshal.ReadInt32(entry, 0);
                int state = Marshal.ReadInt32(entry, IntPtr.Size == 8 ? 16 : 8);
                string user = QuerySessionString(id, 5 /*WTSUserName*/);
                if (user.Length == 0) continue; // skip listener/services sessions
                list.Add(new SessionInfo(id, user, ConnectState(state)));
            }
        }
        finally { WTSFreeMemory(buf); }
        return list;
    }

    private static string QuerySessionString(uint id, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, id, infoClass, out IntPtr p, out _)) return "";
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { WTSFreeMemory(p); }
    }

    private static string ConnectState(int s) => s switch
    {
        0 => "Active",
        1 => "Connected",
        2 => "Connect query",
        3 => "Shadow",
        4 => "Disconnected",
        5 => "Idle",
        6 => "Listen",
        7 => "Reset",
        8 => "Down",
        9 => "Init",
        _ => ""
    };
}
