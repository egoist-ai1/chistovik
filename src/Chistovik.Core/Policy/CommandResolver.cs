namespace Chistovik.Core.Policy;

/// <summary>
/// Безопасный разбор командной строки (запись автозагрузки, действие задачи) до пути исполняемого файла.
/// Раскрывает только переменные, известные из <see cref="PolicyContext"/>; любая другая переменная окружения,
/// относительный путь, сетевой путь, незакрытая кавычка или управляющие символы дают отказ (вызывающий трактует его как ReadOnly).
/// </summary>
internal sealed class CommandResolver
{
    static readonly string[] Extensions =
        ["exe", "com", "bat", "cmd", "scr", "lnk", "msc", "cpl", "vbs", "vbe", "js", "jse", "wsf", "ps1", "msi", "dll"];

    readonly (string Name, string Value)[] _vars;

    public CommandResolver(PolicyContext ctx)
    {
        var win = ctx.Norm(ctx.WindowsDir);
        _vars =
        [
            ("SystemRoot", win), ("windir", win),
            ("ProgramFiles", ctx.Norm(ctx.ProgramFiles)), ("ProgramW6432", ctx.Norm(ctx.ProgramFiles)),
            ("ProgramFiles(x86)", ctx.Norm(ctx.ProgramFilesX86)),
            ("ProgramData", ctx.Norm(ctx.ProgramData)),
        ];
    }

    /// <summary>
    /// Выделяет путь исполняемого файла из <paramref name="command"/> и приводит его к нормальному виду.
    /// <paramref name="exe"/> непуст только при true. Аргументы не возвращаются: они не влияют на решение.
    /// </summary>
    public bool TryResolve(string? command, out string exe, out string error)
        => TryResolve(command, out exe, out _, out error);

    /// <summary>То же, что <see cref="TryResolve(string?, out string, out string)"/>, но возвращает и аргументы (остаток строки после файла, без раскрытия переменных).</summary>
    public bool TryResolve(string? command, out string exe, out string args, out string error)
    {
        exe = string.Empty;
        args = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(command)) { error = "Пустая команда"; return false; }
        if (command.Length > 32767) { error = "Слишком длинная команда"; return false; }
        foreach (var ch in command)
            if (ch < 0x20) { error = "Управляющие символы в команде"; return false; }

        var s = command.Trim();
        string raw;
        string rest;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            if (end < 2) { error = "Незакрытая кавычка или пустой путь"; return false; }
            raw = s[1..end];
            rest = s[(end + 1)..];
        }
        else
        {
            raw = FirstTokenWithKnownExtension(s) ?? (s.Contains(' ') ? string.Empty : s);
            if (raw.Length == 0) { error = "Не удалось выделить файл из команды"; return false; }
            rest = s[raw.Length..];
        }

        raw = Expand(raw);
        if (!PathNormalizer.TryNormalize(raw, out var n, out var err)) { error = err; return false; }
        exe = n;
        args = rest.Trim();
        return true;
    }

    static string? FirstTokenWithKnownExtension(string s)
    {
        // Первое вхождение «.ext», за которым пробел или конец строки: «C:\Program Files\A\a.exe /x».
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '.') continue;
            foreach (var ext in Extensions)
            {
                var e = i + 1 + ext.Length;
                if (e > s.Length || !string.Equals(s.Substring(i + 1, ext.Length), ext, StringComparison.OrdinalIgnoreCase)) continue;
                if (e == s.Length || s[e] == ' ') return s[..e];
            }
        }
        return null;
    }

    string Expand(string raw)
    {
        if (!raw.Contains('%')) return raw;
        foreach (var (name, value) in _vars)
            raw = raw.Replace("%" + name + "%", value, StringComparison.OrdinalIgnoreCase);
        return raw;   // остаток «%» отклонит PathNormalizer («переменные окружения должны быть раскрыты»)
    }
}
