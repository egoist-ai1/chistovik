namespace Sandbox.Live;

/// <summary>
/// Первая проверка стенда. Живой ПК владельца трогать нельзя, поэтому работаем только если одновременно верны три признака:
/// учётная запись WDAGUtilityAccount (так называется пользователь Windows Sandbox), маркер-файл, который создаёт guest-run.ps1
/// внутри Sandbox, и совпадение его содержимого с токеном запуска из командной строки (токен создаёт Run-Sandbox.ps1 на хосте).
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
}
