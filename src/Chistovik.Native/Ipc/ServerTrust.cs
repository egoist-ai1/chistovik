using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;

namespace Chistovik.Native.Ipc;

/// <summary>Какой образ мы ожидаем на сервере канала: установленный Chistovik.Agent.exe рядом с приложением.</summary>
public sealed record ExpectedServer(string ImagePath, string ImageSha256);

/// <summary>Кто на самом деле держит серверный конец канала (собирает порт <see cref="IPipeServerProbe"/>).</summary>
/// <param name="IsElevated">Токен процесса-сервера повышен (TokenElevation). Не удалось прочитать токен: false (отказ).</param>
public sealed record ActualServer(int Pid, string? ImagePath, string? ImageSha256, bool IsElevated);

/// <summary>
/// Проверка сервера канала со стороны приложения (зеркало <see cref="ClientVerifier"/>): путь образа процесса и SHA-256 образа
/// должны совпасть с ожидаемыми. Любое пустое значение это отказ. Решает только по переданным фактам, ничего не читает.
/// </summary>
public static class ServerVerifier
{
    public static (bool Ok, string Reason) Verify(ExpectedServer expected, ActualServer actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        if (actual.Pid <= 0) return (false, "pid");

        if (string.IsNullOrEmpty(actual.ImagePath)
            || !PathNormalizer.TryNormalize(actual.ImagePath, out var a, out _)
            || !PathNormalizer.TryNormalize(expected.ImagePath, out var e, out _)
            || !string.Equals(a, e, StringComparison.OrdinalIgnoreCase))
            return (false, "image-path");

        if (!TryHex(actual.ImageSha256, out var ah) || !TryHex(expected.ImageSha256, out var eh)
            || !CryptographicOperations.FixedTimeEquals(ah, eh))
            return (false, "image-hash");

        // Настоящий агент запускается по UAC с повышенным токеном; процесс без повышения, занявший канал, агентом не считается.
        if (!actual.IsElevated) return (false, "not-elevated");

        return (true, "ok");
    }

    static bool TryHex(string? s, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
        try { bytes = Convert.FromHexString(s); return true; }
        catch (FormatException) { return false; }
    }
}

/// <summary>Порт: что известно о процессе-сервере подключённого канала. Тесты подставляют готовые факты.</summary>
public interface IPipeServerProbe
{
    ActualServer Probe(NamedPipeClientStream pipe);
}

/// <summary>Реальный порт: GetNamedPipeServerProcessId, путь образа, SHA-256. При любой ошибке поля остаются пустыми (проверка их отвергнет).</summary>
public sealed class WindowsPipeServerProbe : IPipeServerProbe
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    public ActualServer Probe(NamedPipeClientStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid == 0)
            return new ActualServer(-1, null, null, false);

        var image = ClientProbe.ImagePathOf(pid);
        string? hash = null;
        if (image is not null)
        {
            try { hash = ClientVerifier.Sha256OfFile(image); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new ActualServer((int)pid, image, hash, IsElevated(pid));
    }

    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint TokenQuery = 0x0008;
    const int TokenElevationClass = 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, out uint info, uint length, out uint returned);

    /// <summary>Повышен ли токен процесса. Любая ошибка открытия или чтения токена даёт false (закрытый исход: сервер отвергается).</summary>
    static bool IsElevated(uint pid)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token)) return false;
            try
            {
                return GetTokenInformation(token, TokenElevationClass, out var elevated, sizeof(uint), out _) && elevated != 0;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
}
