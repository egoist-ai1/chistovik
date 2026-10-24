using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;

namespace Chistovik.Native.Ipc;

/// <summary>Какой образ мы ожидаем на сервере канала: установленный Chistovik.Agent.exe рядом с приложением.</summary>
public sealed record ExpectedServer(string ImagePath, string ImageSha256);

/// <summary>Кто на самом деле держит серверный конец канала (собирает порт <see cref="IPipeServerProbe"/>).</summary>
public sealed record ActualServer(int Pid, string? ImagePath, string? ImageSha256);

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
            return new ActualServer(-1, null, null);

        var image = ClientProbe.ImagePathOf(pid);
        string? hash = null;
        if (image is not null)
        {
            try { hash = ClientVerifier.Sha256OfFile(image); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new ActualServer((int)pid, image, hash);
    }
}
