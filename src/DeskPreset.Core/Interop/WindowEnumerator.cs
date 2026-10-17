using System.Runtime.InteropServices;
using System.Text;
using static DeskPreset.Core.Interop.Native;

namespace DeskPreset.Core.Interop;

public enum WindowShowState { Normal, Minimized, Maximized }

public sealed record WindowBounds(int Left, int Top, int Width, int Height);

public sealed record WindowInfo(
    IntPtr Handle,
    string Title,
    string ClassName,
    uint ProcessId,
    string ExePath,
    string? AppUserModelId,
    Guid? DesktopId,
    WindowBounds Bounds,
    WindowShowState State)
{
    public string ExeName => Path.GetFileName(ExePath);
}

/// <summary>Lists the user-facing top-level windows across all virtual desktops.</summary>
public static unsafe class WindowEnumerator
{
    static readonly HashSet<string> IgnoredProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "TextInputHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe",
        "SearchApp.exe", "LockApp.exe", "ShellHost.exe", "SystemSettingsBroker.exe", "Widgets.exe",
    };

    static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
    };

    static readonly PROPERTYKEY PKEY_AppUserModel_ID = new() { fmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
    static readonly Guid IID_IPropertyStore = new("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");

    public static List<WindowInfo> GetWindows(VirtualDesktopService? desktops)
    {
        var handles = new List<IntPtr>();
        EnumWindows((h, _) => { handles.Add(h); return true; }, IntPtr.Zero);

        var self = (uint)Environment.ProcessId;
        var exeCache = new Dictionary<uint, string>();
        var result = new List<WindowInfo>();
        foreach (var h in handles)
        {
            if (!IsCandidate(h)) continue;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == self) continue;
            if (!exeCache.TryGetValue(pid, out var exe)) exeCache[pid] = exe = GetProcessPath(pid);
            if (IgnoredProcesses.Contains(Path.GetFileName(exe))) continue;

            var desktopId = desktops?.GetWindowDesktopId(h);
            if (desktops != null && desktopId == null) continue;

            var (bounds, state) = GetPlacement(h);
            result.Add(new WindowInfo(h, GetTitle(h), GetClass(h), pid, exe, GetAppUserModelId(h), desktopId, bounds, state));
        }
        return result;
    }

    static bool IsCandidate(IntPtr h)
    {
        if (!IsWindowVisible(h)) return false;
        if (GetWindow(h, GW_OWNER) != IntPtr.Zero) return false;
        var ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
        if ((ex & WS_EX_TOOLWINDOW) != 0) return false;
        if ((ex & WS_EX_NOACTIVATE) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        if (GetWindowTextLength(h) == 0) return false;
        if (IgnoredClasses.Contains(GetClass(h))) return false;
        // Windows on other desktops are cloaked by the shell; anything else cloaked is hidden for real.
        if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out var cloaked, sizeof(int)) >= 0
            && (cloaked & (DWM_CLOAKED_APP | DWM_CLOAKED_INHERITED)) != 0) return false;
        return true;
    }

    public static string GetTitle(IntPtr h)
    {
        var len = GetWindowTextLength(h);
        var sb = new StringBuilder(len + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static string GetClass(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string GetProcessPath(uint pid)
    {
        var hp = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hp == IntPtr.Zero) return string.Empty;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(hp, 0, sb, ref size) ? sb.ToString() : string.Empty;
        }
        finally { CloseHandle(hp); }
    }

    static string? GetAppUserModelId(IntPtr h)
    {
        if (SHGetPropertyStoreForWindow(h, IID_IPropertyStore, out var store) < 0 || store == IntPtr.Zero) return null;
        try
        {
            var key = PKEY_AppUserModel_ID;
            var pv = new PROPVARIANT();
            // IPropertyStore::GetValue is slot 5
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, PROPERTYKEY*, PROPVARIANT*, int>)Vt.Slot(store, 5))(store, &key, &pv);
            if (hr < 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ ? Marshal.PtrToStringUni(pv.ptr) : null; }
            finally { PropVariantClear(ref pv); }
        }
        finally { Vt.Release(store); }
    }

    public static (WindowBounds, WindowShowState) GetPlacement(IntPtr h)
    {
        var wp = new WINDOWPLACEMENT { length = sizeof(WINDOWPLACEMENT) };
        GetWindowPlacement(h, ref wp);
        var r = wp.rcNormalPosition;
        var state = wp.showCmd switch
        {
            2 => WindowShowState.Minimized,
            3 => WindowShowState.Maximized,
            _ => WindowShowState.Normal,
        };
        return (new WindowBounds(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), state);
    }

    public static void SetPlacement(IntPtr h, WindowBounds b, WindowShowState state)
    {
        var wp = new WINDOWPLACEMENT { length = sizeof(WINDOWPLACEMENT) };
        GetWindowPlacement(h, ref wp);
        wp.rcNormalPosition = new RECT { Left = b.Left, Top = b.Top, Right = b.Left + b.Width, Bottom = b.Top + b.Height };
        wp.showCmd = state switch
        {
            WindowShowState.Minimized => 7, // SW_SHOWMINNOACTIVE
            WindowShowState.Maximized => 3,
            _ => 4, // SW_SHOWNOACTIVATE
        };
        wp.flags = 0;
        SetWindowPlacement(h, wp);
    }

    public static void EnablePerMonitorDpi() => SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
}
