using System.Globalization;

namespace Chistovik.Core.Policy;

/// <summary>
/// Единое чтение флагов записи Uninstall (NoRemove, SystemComponent) для агента и App.
/// Флаг поднят для числа (DWORD, QWORD) не равного нулю и для строки (REG_SZ), которая разбирается как целое не равное нулю.
/// Нет значения, нуль, мусорная строка и другие типы: флаг не поднят.
/// </summary>
public static class RegFlag
{
    public static bool IsSet(object? data) => data switch
    {
        int i => i != 0,
        uint u => u != 0,
        long l => l != 0,
        ulong ul => ul != 0,
        string s => long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n != 0,
        _ => false,
    };
}
