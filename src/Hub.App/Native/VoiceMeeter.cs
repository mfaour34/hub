using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Hub.App.Native;

/// <summary>
/// VoiceMeeter Remote API (VoicemeeterRemote64.dll, shipped with VoiceMeeter). Potato layout:
/// strips 0–4 hardware, 5 = "Voicemeeter Input" (VAIO), 6 = "Voicemeeter AUX Input", 7 = "Voicemeeter VAIO3 Input";
/// buses A1–A5 = Bus[0..4], B1–B3 = Bus[5..7].
/// </summary>
public sealed class VoiceMeeter : IDisposable
{
    public const int PotatoType = 3;
    const string UninstallKey = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}";

    delegate int LoginFn();
    delegate int RunFn(int type);
    delegate int GetTypeFn(out int type);
    delegate int DirtyFn();
    delegate int SetFloatFn(string name, float value);
    delegate int GetFloatFn(string name, out float value);
    delegate int SetScriptFn(string script);
    delegate int DeviceCountFn();
    delegate int DeviceDescFn(int index, out int type, StringBuilder name, StringBuilder hwid);

    readonly IntPtr _lib;
    readonly SetFloatFn _setFloat;
    readonly GetFloatFn _getFloat;
    readonly SetScriptFn _setScript;
    readonly DirtyFn _dirty;
    readonly LoginFn _logout;
    bool _loggedIn;

    public static string? DllPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
        var uninstall = key?.GetValue("UninstallString") as string;
        var dir = uninstall is null ? null : Path.GetDirectoryName(uninstall.Trim('"'));
        foreach (var candidate in new[] { dir, @"C:\Program Files (x86)\VB\Voicemeeter" })
            if (candidate is not null && File.Exists(Path.Combine(candidate, "VoicemeeterRemote64.dll")))
                return Path.Combine(candidate, "VoicemeeterRemote64.dll");
        return null;
    }

    public static bool IsPotatoInstalled() =>
        DllPath() is { } dll && File.Exists(Path.Combine(Path.GetDirectoryName(dll)!, "voicemeeter8x64.exe"));

    public VoiceMeeter()
    {
        var path = DllPath() ?? throw new InvalidOperationException("VoiceMeeter is not installed");
        _lib = NativeLibrary.Load(path);
        T Fn<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_lib, name));
        _setFloat = Fn<SetFloatFn>("VBVMR_SetParameterFloat");
        _getFloat = Fn<GetFloatFn>("VBVMR_GetParameterFloat");
        _setScript = Fn<SetScriptFn>("VBVMR_SetParameters");
        _dirty = Fn<DirtyFn>("VBVMR_IsParametersDirty");
        _logout = Fn<LoginFn>("VBVMR_Logout");

        _count = Fn<DeviceCountFn>("VBVMR_Output_GetDeviceNumber");
        _desc = Fn<DeviceDescFn>("VBVMR_Output_GetDeviceDescA");
        var getType = Fn<GetTypeFn>("VBVMR_GetVoicemeeterType");

        int login = Fn<LoginFn>("VBVMR_Login")();
        if (login < 0) throw new InvalidOperationException($"VoiceMeeter login failed ({login})");
        _loggedIn = true;
        if (login == 1) Fn<RunFn>("VBVMR_RunVoicemeeter")(6); // installed but not running: start Potato x64

        // A freshly started VoiceMeeter needs a few seconds before it answers and lists its devices.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        int type = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (getType(out type) == 0 && _dirty() >= 0 && _count() > 0) break;
            Thread.Sleep(250);
        }
        if (type == 0) throw new InvalidOperationException("VoiceMeeter did not start within 15 seconds");
        Type = type;
    }

    readonly DeviceCountFn _count;
    readonly DeviceDescFn _desc;

    public int Type { get; }

    /// <summary>Output devices VoiceMeeter can use for its A buses (read fresh each time).</summary>
    public IReadOnlyList<(int DriverType, string Name)> OutputDevices => ListOutputs(_count, _desc);

    static List<(int, string)> ListOutputs(DeviceCountFn count, DeviceDescFn desc)
    {
        var list = new List<(int, string)>();
        for (int i = 0, n = count(); i < n; i++)
        {
            var name = new StringBuilder(512);
            var hw = new StringBuilder(512);
            if (desc(i, out int type, name, hw) == 0) list.Add((type, name.ToString()));
        }
        return list;
    }

    public void Set(string parameter, float value)
    {
        _dirty();
        int r = _setFloat(parameter, value);
        if (r != 0) throw new InvalidOperationException($"VoiceMeeter rejected {parameter} ({r})");
    }

    public float Get(string parameter)
    {
        _dirty();
        return _getFloat(parameter, out var v) == 0 ? v : float.NaN;
    }

    public void Script(string script)
    {
        int r = _setScript(script);
        if (r != 0) throw new InvalidOperationException($"VoiceMeeter script error ({r})");
    }

    public void Dispose()
    {
        if (_loggedIn) _logout();
        _loggedIn = false;
        NativeLibrary.Free(_lib);
    }
}
