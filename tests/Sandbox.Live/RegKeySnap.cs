using Microsoft.Win32;

namespace Sandbox.Live;

/// <summary>
/// Снимок ключа реестра вместе с подключами и значениями (в памяти). Нужен, чтобы на время сценария «ссылка HKU -> HKLM»
/// убрать существующий ключ пользователя и после сценария вернуть его как был.
/// </summary>
sealed class RegKeySnap
{
    readonly List<(string Name, object Value, RegistryValueKind Kind)> _values = [];
    readonly List<(string Name, RegKeySnap Snap)> _subs = [];

    public int Nodes { get; private set; } = 1;
    public int ValueCount => _values.Count + _subs.Sum(s => s.Snap.ValueCount);

    /// <summary>null, если ключа нет, значение не прочиталось или узлов больше предела (такой куст не трогаем).</summary>
    public static RegKeySnap? Capture(string sid, string key, int maxNodes)
    {
        using var b = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var k = b.OpenSubKey(Reg.Full(true, sid, key));
        return k is null ? null : CaptureKey(k, maxNodes);
    }

    static RegKeySnap? CaptureKey(RegistryKey k, int maxNodes)
    {
        var s = new RegKeySnap();
        foreach (var n in k.GetValueNames())
        {
            var kind = k.GetValueKind(n);
            var v = k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (v is null) return null;
            s._values.Add((n, v, kind));
        }
        foreach (var sub in k.GetSubKeyNames())
        {
            using var sk = k.OpenSubKey(sub);
            if (sk is null) return null;
            var child = CaptureKey(sk, maxNodes);
            if (child is null) return null;
            s.Nodes += child.Nodes;
            if (s.Nodes > maxNodes) return null;
            s._subs.Add((sub, child));
        }
        return s;
    }

    public void Restore(string sid, string key)
    {
        using var b = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var k = b.CreateSubKey(Reg.Full(true, sid, key), true);
        RestoreInto(k);
    }

    void RestoreInto(RegistryKey k)
    {
        foreach (var (n, v, kind) in _values) k.SetValue(n, v, kind);
        foreach (var (n, s) in _subs)
        {
            using var sk = k.CreateSubKey(n, true);
            s.RestoreInto(sk);
        }
    }
}
