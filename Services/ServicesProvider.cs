using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Win11TaskMan.Services;

/// <summary>
/// Service enumeration + basic control via the Service Control Manager
/// (advapi32). One EnumServicesStatusEx call returns name, display name,
/// state and PID for every Win32 service. Start/stop need elevation.
/// </summary>
public static class ServicesProvider
{
    public sealed record ServiceInfo(string Name, string DisplayName, int Pid, string Status);

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const uint SERVICE_WIN32 = 0x00000030;
    private const uint SERVICE_STATE_ALL = 0x00000003;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const int STRIDE = 56; // sizeof(ENUM_SERVICE_STATUS_PROCESSW) on x64

    // service access rights
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_CONTROL_STOP = 0x0001;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr h);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusExW(
        IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr services, uint bufSize, out uint bytesNeeded,
        out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, uint numArgs, IntPtr args);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, IntPtr status);

    public static List<ServiceInfo> Enumerate()
    {
        var list = new List<ServiceInfo>();
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return list;
        try
        {
            uint resume = 0;
            EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
            if (needed == 0) return list;

            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                resume = 0;
                if (!EnumServicesStatusExW(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                        buf, needed, out _, out uint count, ref resume, null))
                    return list;

                for (int i = 0; i < count; i++)
                {
                    IntPtr e = buf + i * STRIDE;
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e, 0)) ?? "";
                    string display = Marshal.PtrToStringUni(Marshal.ReadIntPtr(e, 8)) ?? "";
                    int state = Marshal.ReadInt32(e, 20);
                    int pid = Marshal.ReadInt32(e, 44);
                    list.Add(new ServiceInfo(name, display, pid, StateName(state)));
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseServiceHandle(scm); }
        return list;
    }

    private static string StateName(int s) => s switch
    {
        1 => "Stopped",
        2 => "Starting",
        3 => "Stopping",
        4 => "Running",
        5 => "Continuing",
        6 => "Pausing",
        7 => "Paused",
        _ => "Unknown"
    };

    /// <summary>Start or stop a service. Throws on failure (e.g. access denied).</summary>
    public static void Control(string name, bool start)
    {
        IntPtr scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new InvalidOperationException("Can't open the Service Control Manager.");
        try
        {
            IntPtr svc = OpenServiceW(scm, name, SERVICE_START | SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            IntPtr status = start ? IntPtr.Zero : Marshal.AllocHGlobal(36); // SERVICE_STATUS out buffer
            try
            {
                bool ok = start
                    ? StartServiceW(svc, 0, IntPtr.Zero)
                    : ControlService(svc, SERVICE_CONTROL_STOP, status);
                if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { if (status != IntPtr.Zero) Marshal.FreeHGlobal(status); CloseServiceHandle(svc); }
        }
        finally { CloseServiceHandle(scm); }
    }
}
