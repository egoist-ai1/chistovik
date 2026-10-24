using System.Runtime.InteropServices;
using Chistovik.Core.Execution;
using Chistovik.Core.Ports;
using Chistovik.Native.Interop;

namespace Chistovik.Native.Services;

/// <summary>
/// Диспетчер служб (SCM). Перечисляются службы Win32 (SERVICE_WIN32); драйверы не показываются.
/// Open: нет службы, а при чтении ещё и отказ в доступе, дают null; отказ в доступе при write=true — исключение Win32Exception.
/// </summary>
public sealed class WindowsServiceControl : IServiceControl
{
    public IReadOnlyList<string> ListNames()
    {
        var scm = Win32.OpenSCManager(null, null, Win32.ScManagerConnect | Win32.ScManagerEnumerateService);
        if (scm == IntPtr.Zero) throw Win32.Fail(Marshal.GetLastWin32Error(), "OpenSCManager");
        try
        {
            var names = new List<string>();
            uint resume = 0;
            uint capacity = 1 << 16;
            var mem = Marshal.AllocHGlobal((int)capacity);
            try
            {
                while (true)
                {
                    var ok = Win32.EnumServicesStatusEx(scm, Win32.ScEnumProcessInfo, Win32.ServiceWin32, Win32.ServiceStateAll,
                        mem, capacity, out var needed, out var returned, ref resume, null);
                    var err = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && err != Win32.ErrorMoreData) throw Win32.Fail(err, "EnumServicesStatusEx");

                    var size = Marshal.SizeOf<Win32.EnumServiceStatusProcess>();
                    for (var i = 0; i < returned; i++)
                    {
                        var item = Marshal.PtrToStructure<Win32.EnumServiceStatusProcess>(mem + i * size);
                        var name = Marshal.PtrToStringUni(item.ServiceName);
                        if (!string.IsNullOrEmpty(name)) names.Add(name);
                    }
                    if (ok) break;
                    if (returned == 0 && needed > capacity)
                    {
                        // Буфер не вместил ни одной записи: увеличиваем и повторяем с тем же resume.
                        Marshal.FreeHGlobal(mem);
                        mem = IntPtr.Zero;
                        capacity = needed;
                        mem = Marshal.AllocHGlobal((int)capacity);
                    }
                }
            }
            finally { if (mem != IntPtr.Zero) Marshal.FreeHGlobal(mem); }
            return names;
        }
        finally { Win32.CloseServiceHandle(scm); }
    }

    public IServiceHandle? Open(string name, bool write)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var scm = Win32.OpenSCManager(null, null, Win32.ScManagerConnect);
        if (scm == IntPtr.Zero) throw Win32.Fail(Marshal.GetLastWin32Error(), "OpenSCManager");
        try
        {
            var access = Win32.ServiceQueryConfig | Win32.ServiceQueryStatus | Win32.ServiceEnumerateDependents;
            if (write) access |= Win32.ServiceChangeConfig;
            var svc = Win32.OpenService(scm, name, access);
            if (svc == IntPtr.Zero)
            {
                var err = Marshal.GetLastWin32Error();
                if (err == Win32.ErrorServiceDoesNotExist) return null;
                // Часть служб (например RpcSs) закрывает чтение конфигурации для обычного пользователя: для чтения это «недоступна».
                if (err == Win32.ErrorAccessDenied && !write) return null;
                throw Win32.Fail(err, $"OpenService '{name}'");
            }
            return new WindowsServiceHandle(svc, name, write);
        }
        finally { Win32.CloseServiceHandle(scm); }
    }
}

public sealed class WindowsServiceHandle : IServiceHandle
{
    private IntPtr _svc;
    private readonly string _name;
    private readonly bool _writable;

    internal WindowsServiceHandle(IntPtr svc, string name, bool writable)
    {
        _svc = svc;
        _name = name;
        _writable = writable;
    }

    public ServiceRaw Query()
    {
        ThrowIfDisposed();
        var cfg = QueryConfig();
        var delayed = ReadDword(Win32.ServiceConfigDelayedAutoStartInfo) is { } d && d != 0;
        // Для служб без защиты уровень может отвечать ошибкой «неверный уровень»: это «не защищена».
        var launchProtected = ReadDword(Win32.ServiceConfigLaunchProtected) is { } lp && lp != 0;
        return new ServiceRaw(_name, cfg.Type, cfg.Start, delayed, cfg.Image, launchProtected,
            CountDependents(), CountTriggers(), IsRunning());
    }

    public void SetStart(ApprovedServiceChange c)
    {
        ArgumentNullException.ThrowIfNull(c);
        ThrowIfDisposed();
        if (!_writable) throw new InvalidOperationException($"Служба '{_name}' открыта без права изменения.");
        if (!string.Equals(c.Name, _name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Разрешение выдано для службы '{c.Name}', а хэндл открыт для '{_name}'.");

        uint start = c.Start switch
        {
            StartMode.Automatic or StartMode.AutomaticDelayed => 2,
            StartMode.Manual => 3,
            StartMode.Disabled => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(c), "Неизвестный режим запуска."),
        };
        if (!Win32.ChangeServiceConfig(_svc, Win32.ServiceNoChange, start, Win32.ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
            throw Win32.Fail(Marshal.GetLastWin32Error(), $"ChangeServiceConfig '{_name}'");

        // Флаг отложенного запуска задаём всегда, чтобы не оставлять устаревшее значение при смене режима.
        var delayed = c.Start == StartMode.AutomaticDelayed ? 1 : 0;
        if (!Win32.ChangeServiceConfig2(_svc, Win32.ServiceConfigDelayedAutoStartInfo, ref delayed))
        {
            var err = Marshal.GetLastWin32Error();
            // Для неавтоматического запуска часть версий Windows отвечает «неверный параметр»: флаг не нужен.
            if (!(delayed == 0 && err == Win32.ErrorInvalidParameter))
                throw Win32.Fail(err, $"ChangeServiceConfig2(DelayedAutoStart) '{_name}'");
        }
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _svc, IntPtr.Zero);
        if (h != IntPtr.Zero) Win32.CloseServiceHandle(h);
    }

    private void ThrowIfDisposed()
    {
        if (_svc == IntPtr.Zero) throw new ObjectDisposedException(nameof(WindowsServiceHandle));
    }

    private (uint Type, uint Start, string? Image) QueryConfig()
    {
        Win32.QueryServiceConfig(_svc, IntPtr.Zero, 0, out var needed);
        var err = Marshal.GetLastWin32Error();
        if (err != Win32.ErrorInsufficientBuffer || needed == 0) throw Win32.Fail(err, $"QueryServiceConfig '{_name}'");
        var mem = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Win32.QueryServiceConfig(_svc, mem, needed, out _))
                throw Win32.Fail(Marshal.GetLastWin32Error(), $"QueryServiceConfig '{_name}'");
            var cfg = Marshal.PtrToStructure<Win32.QueryServiceConfigW>(mem);
            return (cfg.ServiceType, cfg.StartType, Marshal.PtrToStringUni(cfg.BinaryPathName));
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    /// <summary>Читает уровень QueryServiceConfig2, у которого результат — один DWORD первым полем. null, если уровень не поддержан.</summary>
    private uint? ReadDword(uint level)
    {
        var mem = Marshal.AllocHGlobal(16);
        try
        {
            if (Win32.QueryServiceConfig2(_svc, level, mem, 16, out _)) return (uint)Marshal.ReadInt32(mem);
            var err = Marshal.GetLastWin32Error();
            if (err == Win32.ErrorInvalidLevel || err == Win32.ErrorInvalidParameter) return null;
            throw Win32.Fail(err, $"QueryServiceConfig2({level}) '{_name}'");
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    private int CountTriggers()
    {
        // SERVICE_TRIGGER_INFO: первое поле cTriggers; дальше указатели внутри того же буфера.
        if (Win32.QueryServiceConfig2(_svc, Win32.ServiceConfigTriggerInfo, IntPtr.Zero, 0, out var needed)) return 0;
        var err = Marshal.GetLastWin32Error();
        if (err == Win32.ErrorInvalidLevel || err == Win32.ErrorInvalidParameter) return 0;
        if (err != Win32.ErrorInsufficientBuffer) throw Win32.Fail(err, $"QueryServiceConfig2(Trigger) '{_name}'");
        if (needed == 0) return 0;
        var mem = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Win32.QueryServiceConfig2(_svc, Win32.ServiceConfigTriggerInfo, mem, needed, out _))
                throw Win32.Fail(Marshal.GetLastWin32Error(), $"QueryServiceConfig2(Trigger) '{_name}'");
            return Marshal.ReadInt32(mem);
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    /// <summary>Число зависимых служб (EnumDependentServices возвращает и косвенно зависимых).</summary>
    private int CountDependents()
    {
        if (Win32.EnumDependentServices(_svc, Win32.ServiceStateAll, IntPtr.Zero, 0, out var needed, out var count))
            return (int)count; // зависимых нет
        var err = Marshal.GetLastWin32Error();
        if (err != Win32.ErrorMoreData) throw Win32.Fail(err, $"EnumDependentServices '{_name}'");
        var mem = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!Win32.EnumDependentServices(_svc, Win32.ServiceStateAll, mem, needed, out _, out count))
                throw Win32.Fail(Marshal.GetLastWin32Error(), $"EnumDependentServices '{_name}'");
            return (int)count;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }

    private bool IsRunning()
    {
        var size = (uint)Marshal.SizeOf<Win32.ServiceStatusProcess>();
        var mem = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!Win32.QueryServiceStatusEx(_svc, Win32.ScStatusProcessInfo, mem, size, out _))
                throw Win32.Fail(Marshal.GetLastWin32Error(), $"QueryServiceStatusEx '{_name}'");
            return Marshal.PtrToStructure<Win32.ServiceStatusProcess>(mem).CurrentState == Win32.ServiceRunning;
        }
        finally { Marshal.FreeHGlobal(mem); }
    }
}
