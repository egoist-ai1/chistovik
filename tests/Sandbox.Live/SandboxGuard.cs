namespace Sandbox.Live;

/// <summary>
/// Первая проверка стенда. Живой ПК владельца трогать нельзя, поэтому работаем только если одновременно верны признаки:
/// учётная запись WDAGUtilityAccount (так называется пользователь Windows Sandbox), служба CExecSvc (есть только в Sandbox,
/// независимый признак), маркер-файл, который создаёт guest-run.ps1 внутри Sandbox, и совпадение его содержимого с токеном
/// запуска из командной строки (токен создаёт Run-Sandbox.ps1 на хосте).
/// Любое несовпадение: сообщение «Только в Windows Sandbox» и код выхода 3, ничего не читается и не пишется.
/// </summary>
static class SandboxGuard
{
    public const int ExitNotSandbox = 3;
    public const string MarkerPath = @"C:\ProgramData\ChistovikSandbox.marker";

    public static (bool Ok, string Reason) Check(string? token)
    {
        if (!string.Equals(Environment.UserName, "WDAGUtilityAccount", StringComparison.OrdinalIgnoreCase))
            return (false, "учётная запись не WDAGUtilityAccount");
        if (!HasService("CExecSvc"))
            return (false, "нет службы CExecSvc");
        if (string.IsNullOrWhiteSpace(token) || token.Length < 16)
            return (false, "нет токена запуска Sandbox");
        string text;
        try { text = File.ReadAllText(MarkerPath).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (false, "нет маркера Sandbox");
        }
        return string.Equals(text, token, StringComparison.Ordinal) ? (true, "ok") : (false, "маркер не совпал с токеном");
    }

    static bool HasService(string name)
    {
        // Только чтение: открываем диспетчер служб и службу на SERVICE_QUERY_STATUS. Любая ошибка = службы нет (закрытый исход).
        var scm = OpenSCManager(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero) return false;
        try
        {
            var svc = OpenService(scm, name, ServiceQueryStatus);
            if (svc == IntPtr.Zero) return false;
            CloseServiceHandle(svc);
            return true;
        }
        finally { CloseServiceHandle(scm); }
    }

    const uint ScManagerConnect = 0x0001;
    const uint ServiceQueryStatus = 0x0004;

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    static extern bool CloseServiceHandle(IntPtr handle);
}
