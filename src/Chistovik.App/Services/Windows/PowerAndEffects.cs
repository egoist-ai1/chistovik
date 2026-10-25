using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Chistovik.App.Models;
using Chistovik.Core.Catalog;
using Microsoft.Win32;

namespace Chistovik.App.Services.Windows;

/// <summary>Чтение питания без прав через powrprof: активная схема и значения параметров из каталога. Ничего не пишет.</summary>
public sealed class PowrProfPowerSource(PowerCatalog catalog) : IPowerSource
{
    public Task<PowerSnapshot> ReadAsync() => Task.Run(() =>
    {
        try
        {
            Guid? active = null;
            if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) == 0 && ptr != IntPtr.Zero)
            {
                try { active = Marshal.PtrToStructure<Guid>(ptr); }
                finally { LocalFree(ptr); }
            }

            var values = new Dictionary<string, (uint, uint)>(StringComparer.Ordinal);
            if (active is { } scheme)
            {
                foreach (var s in catalog.Settings)
                {
                    var sub = s.Subgroup;
                    var set = s.Setting;
                    var sch = scheme;
                    if (PowerReadACValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, out var ac) == 0
                        && PowerReadDCValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, out var dc) == 0)
                        values[s.Id] = (ac, dc);
                }
            }
            return new PowerSnapshot(active, values, active is null ? "Не удалось прочитать активную схему питания" : null,
                EnumerateSchemes(), IsModernStandby());
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or Win32Exception)
        {
            return new PowerSnapshot(null, new Dictionary<string, (uint, uint)>(), "Чтение питания недоступно: " + e.Message);
        }
    });

    /// <summary>Схемы, существующие в системе (PowerEnumerate, ACCESS_SCHEME). null: перечислить не удалось.</summary>
    static IReadOnlyCollection<Guid>? EnumerateSchemes()
    {
        const uint accessScheme = 16, errorNoMoreItems = 259;
        var result = new List<Guid>();
        var mem = Marshal.AllocHGlobal(16);
        try
        {
            for (uint i = 0; i < 1000; i++)
            {
                uint size = 16;
                var rc = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, accessScheme, i, mem, ref size);
                if (rc == errorNoMoreItems) return result;
                if (rc != 0) return null;
                result.Add(Marshal.PtrToStructure<Guid>(mem));
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    /// <summary>Modern Standby: поле AoAc (смещение 20) в SYSTEM_POWER_CAPABILITIES (76 байт). Не прочитано: false.</summary>
    static bool IsModernStandby()
    {
        const int capsSize = 76, aoAcOffset = 20, systemPowerCapabilities = 4;
        var mem = Marshal.AllocHGlobal(capsSize);
        try
        {
            for (var i = 0; i < capsSize; i++) Marshal.WriteByte(mem, i, 0);
            return CallNtPowerInformation(systemPowerCapabilities, IntPtr.Zero, 0, mem, capsSize) == 0 && Marshal.ReadByte(mem, aoAcOffset) != 0;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    [DllImport("powrprof.dll")]
    static extern uint PowerEnumerate(IntPtr root, IntPtr scheme, IntPtr subGroup, uint flags, uint index, IntPtr buffer, ref uint size);

    [DllImport("powrprof.dll")]
    static extern int CallNtPowerInformation(int level, IntPtr input, uint inputLength, IntPtr output, uint outputLength);

    [DllImport("powrprof.dll")]
    static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadACValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    static extern uint PowerReadDCValueIndex(IntPtr rootKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);
}

/// <summary>
/// Эффекты после ответа агента (DESIGN-0.1 §5): SystemParametersInfo с SPIF_SENDCHANGE без SPIF_UPDATEINIFILE
/// (реестр уже записан агентом), WM_SETTINGCHANGE, перезапуск Проводника по кнопке.
/// </summary>
public sealed class WindowsShellEffects : IShellEffects
{
    const uint SpiSetMouse = 0x0004;
    const uint SpiSetDragFullWindows = 0x0025;
    const uint SpiSetAnimation = 0x0049;
    const uint SpifSendChange = 0x0002;
    const uint WmSettingChange = 0x001A;
    static readonly IntPtr HwndBroadcast = new(0xFFFF);

    public void Apply(string effect)
    {
        switch (effect)
        {
            case "none": return;
            case "spi-dragfull":
                SystemParametersInfo(SpiSetDragFullWindows, ReadDesktopFlag(@"Control Panel\Desktop", "DragFullWindows") ? 1u : 0u, IntPtr.Zero, SpifSendChange);
                return;
            case "spi-animation":
                SetAnimation(ReadDesktopFlag(@"Control Panel\Desktop\WindowMetrics", "MinAnimate"));
                return;
            case "spi-mouse":
                SetMouse();
                return;
            case "broadcast":
                SendMessageTimeout(HwndBroadcast, WmSettingChange, UIntPtr.Zero, "ImmersiveColorSet", 0x2, 2000, out _);
                return;
            default:
                throw new InvalidOperationException("Неизвестный эффект: " + effect);
        }
    }

    public void RestartExplorer()
    {
        // Оболочку перезапускает сама Windows; если не стартовала за 3 секунды, запускаем explorer.exe сами.
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { if (p.SessionId == Process.GetCurrentProcess().SessionId) p.Kill(); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        Thread.Sleep(3000);
        if (Process.GetProcessesByName("explorer").Length == 0)
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true })?.Dispose();
    }

    static bool ReadDesktopFlag(string path, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(path);
        return k?.GetValue(name) is string s && s.Trim() == "1";
    }

    static void SetAnimation(bool on)
    {
        var info = new AnimationInfo { CbSize = (uint)Marshal.SizeOf<AnimationInfo>(), MinAnimate = on ? 1 : 0 };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<AnimationInfo>());
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SystemParametersInfo(SpiSetAnimation, info.CbSize, ptr, SpifSendChange);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    static void SetMouse()
    {
        using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse");
        int Num(string n) => int.TryParse(k?.GetValue(n) as string, out var v) ? v : 0;
        var arr = new[] { Num("MouseThreshold1"), Num("MouseThreshold2"), Num("MouseSpeed") };
        var ptr = Marshal.AllocHGlobal(sizeof(int) * 3);
        try
        {
            Marshal.Copy(arr, 0, ptr, 3);
            SystemParametersInfo(SpiSetMouse, 0, ptr, SpifSendChange);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AnimationInfo
    {
        public uint CbSize;
        public int MinAnimate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SystemParametersInfo(uint action, uint param, IntPtr pvParam, uint winIni);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
}
