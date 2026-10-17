using System.Text.Json;

namespace DeskPreset.Core.Interop;

/// <summary>Layout of the undocumented shell interfaces for one range of Windows builds.</summary>
public sealed record ShellProfile(string Name, Guid ManagerInternal, Guid VirtualDesktop, bool HasSwitchAndMoveForeground);

public sealed record DesktopInfo(Guid Id, int Index, string Name)
{
    public string DisplayName => string.IsNullOrEmpty(Name) ? $"デスクトップ {Index + 1}" : Name;
}

/// <summary>
/// Wraps the Windows 11 virtual desktop shell interfaces. The internal ones are undocumented and change
/// between builds, so they are called through raw vtable slots with IIDs chosen from <see cref="Profiles"/>.
/// All calls must stay on the thread that created the instance (COM apartment).
/// </summary>
public sealed unsafe class VirtualDesktopService : IDisposable
{
    static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
    static readonly Guid IID_IServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    static readonly Guid IID_IApplicationViewCollection = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
    static readonly Guid IID_IVirtualDesktopManager = new("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");

    /// <summary>Known layouts, newest first. Extra ones can be supplied via interop.json next to presets.json.</summary>
    public static List<ShellProfile> Profiles { get; } =
    [
        new("Windows 11 24H2+", new("53F5CA0B-158F-4124-900C-057158060B27"), new("3F07F4BE-B107-441A-AF0F-39D82529072C"), true),
        new("Windows 11 22H2/23H2", new("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E"), new("3F07F4BE-B107-441A-AF0F-39D82529072C"), false),
    ];

    IntPtr _shell, _internal, _viewCollection, _publicManager;
    int _shift;

    public ShellProfile Profile { get; private set; } = null!;

    public VirtualDesktopService()
    {
        LoadExtraProfiles();
        Connect();
    }

    static void LoadExtraProfiles()
    {
        var path = Path.Combine(PresetRepository.DefaultDirectory, "interop.json");
        if (!File.Exists(path)) return;
        var extra = JsonSerializer.Deserialize<List<ShellProfile>>(File.ReadAllText(path));
        if (extra != null) Profiles.InsertRange(0, extra.Where(e => !Profiles.Contains(e)));
    }

    void Connect()
    {
        ReleaseAll();
        Vt.Check(Native.CoCreateInstance(CLSID_ImmersiveShell, IntPtr.Zero, Native.CLSCTX_LOCAL_SERVER, IID_IServiceProvider, out _shell));
        Vt.Check(Native.CoCreateInstance(CLSID_VirtualDesktopManager, IntPtr.Zero, Native.CLSCTX_ALL, IID_IVirtualDesktopManager, out _publicManager));
        _viewCollection = QueryService(IID_IApplicationViewCollection, IID_IApplicationViewCollection)
            ?? throw new NotSupportedException("IApplicationViewCollection を取得できませんでした。");

        foreach (var profile in Profiles)
        {
            var mgr = QueryService(CLSID_VirtualDesktopManagerInternal, profile.ManagerInternal);
            if (mgr == null) continue;
            _internal = mgr.Value;
            Profile = profile;
            _shift = profile.HasSwitchAndMoveForeground ? 1 : 0;
            try
            {
                // Validate the IVirtualDesktop IID as well before committing to this profile.
                var cur = GetCurrentDesktopPtr();
                Vt.Release(cur);
                return;
            }
            catch
            {
                Vt.Release(_internal);
                _internal = IntPtr.Zero;
            }
        }
        throw new NotSupportedException(
            $"この Windows ビルド ({Environment.OSVersion.Version}) の仮想デスクトップ API に対応していません。");
    }

    IntPtr? QueryService(Guid service, Guid iid)
    {
        IntPtr result;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Vt.Slot(_shell, 3);
        int hr = fn(_shell, &service, &iid, &result);
        return hr >= 0 && result != IntPtr.Zero ? result : null;
    }

    /// <summary>Runs a shell call, reconnecting once if explorer.exe was restarted.</summary>
    T Call<T>(Func<T> action)
    {
        try { return action(); }
        catch (System.Runtime.InteropServices.COMException ex) when (IsDisconnected(ex.HResult))
        {
            Connect();
            return action();
        }
    }

    static bool IsDisconnected(int hr) =>
        hr is unchecked((int)0x80010108) or unchecked((int)0x800706BA) or unchecked((int)0x800706BF);

    // ---------------- internal manager slots ----------------

    IntPtr GetCurrentDesktopPtr()
    {
        IntPtr d;
        Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vt.Slot(_internal, 6))(_internal, &d));
        // Ensure it really implements our IVirtualDesktop IID.
        return QueryDesktop(d);
    }

    IntPtr QueryDesktop(IntPtr unknown)
    {
        try
        {
            var iid = Profile.VirtualDesktop;
            Vt.Check(System.Runtime.InteropServices.Marshal.QueryInterface(unknown, in iid, out var desktop));
            return desktop;
        }
        finally { Vt.Release(unknown); }
    }

    IntPtr FindDesktopPtr(Guid id)
    {
        IntPtr d;
        Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Vt.Slot(_internal, 13 + _shift))(_internal, &id, &d));
        return d;
    }

    static Guid GetDesktopId(IntPtr desktop)
    {
        Guid id;
        Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)Vt.Slot(desktop, 4))(desktop, &id));
        return id;
    }

    static string GetDesktopName(IntPtr desktop)
    {
        IntPtr h;
        Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vt.Slot(desktop, 5))(desktop, &h));
        try { return Native.HStringToString(h); }
        finally { Native.WindowsDeleteString(h); }
    }

    // ---------------- public API ----------------

    public IReadOnlyList<DesktopInfo> GetDesktops() => Call(() =>
    {
        IntPtr array;
        Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vt.Slot(_internal, 7))(_internal, &array));
        try
        {
            uint count;
            Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Vt.Slot(array, 3))(array, &count));
            var list = new List<DesktopInfo>((int)count);
            var iid = Profile.VirtualDesktop;
            for (uint i = 0; i < count; i++)
            {
                IntPtr d;
                Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, IntPtr*, int>)Vt.Slot(array, 4))(array, i, &iid, &d));
                try { list.Add(new DesktopInfo(GetDesktopId(d), (int)i, GetDesktopName(d))); }
                finally { Vt.Release(d); }
            }
            return (IReadOnlyList<DesktopInfo>)list;
        }
        finally { Vt.Release(array); }
    });

    public Guid GetCurrentDesktopId() => Call(() =>
    {
        var d = GetCurrentDesktopPtr();
        try { return GetDesktopId(d); }
        finally { Vt.Release(d); }
    });

    public Guid CreateDesktop() => Call(() =>
    {
        IntPtr d;
        Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Vt.Slot(_internal, 10 + _shift))(_internal, &d));
        try { return GetDesktopId(d); }
        finally { Vt.Release(d); }
    });

    public void SwitchTo(Guid id) => Call(() =>
    {
        var d = FindDesktopPtr(id);
        try { Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vt.Slot(_internal, 9))(_internal, d)); }
        finally { Vt.Release(d); }
        return 0;
    });

    public void Rename(Guid id, string name) => Call(() =>
    {
        var d = FindDesktopPtr(id);
        Vt.Check(Native.WindowsCreateString(name, (uint)name.Length, out var h));
        try { Vt.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Vt.Slot(_internal, 15 + _shift))(_internal, d, h)); }
        finally
        {
            Native.WindowsDeleteString(h);
            Vt.Release(d);
        }
        return 0;
    });

    /// <summary>Desktop the window lives on, or null for windows not tracked by the shell (or pinned to all).</summary>
    public Guid? GetWindowDesktopId(IntPtr hwnd)
    {
        Guid id;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int>)Vt.Slot(_publicManager, 4))(_publicManager, hwnd, &id);
        return hr >= 0 && id != Guid.Empty ? id : null;
    }

    /// <summary>Moves any process's top-level window to the given desktop.</summary>
    public bool MoveWindow(IntPtr hwnd, Guid desktopId) => Call(() =>
    {
        IntPtr view;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Vt.Slot(_viewCollection, 6))(_viewCollection, hwnd, &view);
        if (hr < 0 || view == IntPtr.Zero)
        {
            // Fallback: documented API, works only for windows owned by this process.
            var id = desktopId;
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, int>)Vt.Slot(_publicManager, 5))(_publicManager, hwnd, &id) >= 0;
        }
        var d = FindDesktopPtr(desktopId);
        try
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)Vt.Slot(_internal, 4))(_internal, view, d) >= 0;
        }
        finally
        {
            Vt.Release(d);
            Vt.Release(view);
        }
    });

    void ReleaseAll()
    {
        Vt.Release(_internal);
        Vt.Release(_viewCollection);
        Vt.Release(_publicManager);
        Vt.Release(_shell);
        _internal = _viewCollection = _publicManager = _shell = IntPtr.Zero;
    }

    public void Dispose() => ReleaseAll();
}
