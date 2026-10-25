using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Execution;

/// <summary>Итог обхода дерева. Sha256 пуст, если обход остановлен (ссылка или превышение пределов).</summary>
internal sealed record TreeStats(string Sha256, int Subkeys, int Values, long Bytes, bool HasLink, bool TooLarge, bool BadName = false, bool BadType = false)
{
    public bool Usable => !HasLink && !TooLarge && !BadName && !BadType;
}

/// <summary>
/// Канонический хэш поддерева реестра: имена подключей и значений в нижнем регистре и по порядку ordinal, тип, длина, байты.
/// Обход идёт по хэндлам с REG_OPTION_OPEN_LINK; ссылка в дереве останавливает обход. Подключи считаются как все потомки.
/// </summary>
internal static class RegistryTreeHasher
{
    public const int MaxSubkeys = RegistryPolicy.MaxOrphanSubkeysExpert;
    public const long MaxBytes = 1L << 20;
    public const int MaxDepth = 64;

    static readonly byte[] TagValue = [(byte)'V'];
    static readonly byte[] TagKey = [(byte)'K'];
    static readonly byte[] TagEnd = [(byte)'E'];

    sealed class Counters
    {
        public int Subkeys;
        public int Values;
        public long Bytes;
        public bool HasLink;
        public bool TooLarge;
        public bool BadName;
        public bool BadType;
    }

    public static TreeStats Compute(IRegTreeHandle root)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var c = new Counters();
        var ok = Walk(root, sha, c, 0);
        var hash = ok ? Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant() : string.Empty;
        return new TreeStats(hash, c.Subkeys, c.Values, c.Bytes, c.HasLink, c.TooLarge, c.BadName, c.BadType);
    }

    /// <summary>
    /// Имя безопасно для .reg: ни одного символа &lt; 0x20, ни 0x7F, ни «]» (он закрывает заголовок ключа, экранирования в .reg для ключей нет).
    /// Имена значений кавычатся и экранируются отдельно, но перевод строки или NUL в них всё равно ломает построчный формат.
    /// </summary>
    public static bool IsSafeName(string name)
    {
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch < ' ' || ch == '\u007f' || ch == ']') return false;
            // Непарный суррогат: в UTF-16 .reg и в хэше он превратился бы в U+FFFD, и копия не совпала бы с оригиналом.
            if (char.IsHighSurrogate(ch))
            {
                if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1])) return false;
                i++;
            }
            else if (char.IsLowSurrogate(ch)) return false;
        }
        return true;
    }

    static void PutInt(IncrementalHash h, int v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(b, v);
        h.AppendData(b);
    }

    static void PutName(IncrementalHash h, string s)
    {
        var bytes = Encoding.Unicode.GetBytes(s.ToLowerInvariant());
        PutInt(h, bytes.Length);
        h.AppendData(bytes);
    }

    static bool Walk(IRegTreeHandle h, IncrementalHash sha, Counters c, int depth)
    {
        if (depth > MaxDepth)
        {
            c.TooLarge = true;
            return false;
        }
        // Предел передаётся порту: значение больше остатка бюджета отвергается по размеру, до чтения его данных.
        IReadOnlyList<(string Name, RegType Type, byte[] Raw)> values;
        try
        {
            values = h.RawValues(MaxBytes - c.Bytes);
        }
        catch (RegValueTooLargeException)
        {
            c.TooLarge = true;
            return false;
        }
        foreach (var (name, type, raw) in values.OrderBy(v => v.Name.ToLowerInvariant(), StringComparer.Ordinal))
        {
            if (!IsSafeName(name))
            {
                c.BadName = true;
                return false;
            }
            if (type == RegType.Other)
            {
                c.BadType = true;
                return false;
            }
            c.Values++;
            c.Bytes += raw.Length;
            if (c.Bytes > MaxBytes)
            {
                c.TooLarge = true;
                return false;
            }
            sha.AppendData(TagValue);
            PutName(sha, name);
            PutInt(sha, (int)type);
            PutInt(sha, raw.Length);
            sha.AppendData(raw);
        }
        foreach (var name in h.SubKeyNames().OrderBy(n => n.ToLowerInvariant(), StringComparer.Ordinal))
        {
            if (!IsSafeName(name))
            {
                c.BadName = true;
                return false;
            }
            c.Subkeys++;
            if (c.Subkeys > MaxSubkeys)
            {
                c.TooLarge = true;
                return false;
            }
            using var child = h.OpenSub(name, false);
            if (child is null) continue;
            if (child.IsSymbolicLink)
            {
                c.HasLink = true;
                return false;
            }
            sha.AppendData(TagKey);
            PutName(sha, name);
            if (!Walk(child, sha, c, depth + 1)) return false;
            sha.AppendData(TagEnd);
        }
        return true;
    }
}

/// <summary>Копирование и рекурсивное удаление поддеревьев по хэндлам (без RegCopyTree и RegDeleteTree, которые следуют по ссылкам).</summary>
internal static class RegTreeOps
{
    /// <summary>
    /// Копирует значения и подключи src в dst. Приёмник может быть доступен клиенту без прав (HKU клиента, свежий ключ в Uninstall),
    /// поэтому каждый хэндл приёмника проверяется: ссылка на любом уровне это отказ (policy-denied, reg-symlink), иначе агент от имени SYSTEM
    /// записал бы по ссылке в чужой ключ.
    /// requireCreated=true (карантин, откат): каждый подключ приёмника обязан быть создан именно этим вызовом (disposition = новый ключ),
    /// уже существующий значит, что его подложили после создания родителя: external-change, в него не пишем.
    /// requireCreated=false (Heal: ключ уже частично удалён, часть подключей на месте): существующий подключ допустим, если он не ссылка;
    /// чужое содержимое всё равно не пройдёт итоговую сверку хэша.
    /// </summary>
    public static void Copy(IRegTreeHandle src, IRegTreeHandle dst, bool requireCreated = true)
    {
        if (dst.IsSymbolicLink) throw new StepFailureException(StepCodes.PolicyDenied);
        foreach (var (name, type, raw) in src.RawValues()) dst.SetRaw(name, type, raw);
        foreach (var name in src.SubKeyNames())
        {
            using var s = src.OpenSub(name, false);
            if (s is null) continue;
            if (s.IsSymbolicLink) throw new StepFailureException(StepCodes.RegTreeHasLink);
            using var d = dst.CreateSub(name, out var created);
            if (d.IsSymbolicLink) throw new StepFailureException(StepCodes.PolicyDenied);
            if (requireCreated && !created) throw new StepFailureException(StepCodes.ExternalChange);
            Copy(s, d, requireCreated);
        }
    }

    /// <summary>
    /// Предпроверка удаления: каждый подключ открывается с правом DELETE (OpenSub forWrite) и закрывается. Недоступный подключ даёт false
    /// до копии, а не посреди DeleteTree, когда ключ уже частично выпотрошен.
    /// Native открывает подключ на «запись» с маской READ|DELETE (не READ|WRITE|DELETE): удалению нужны перечисление детей и DELETE, а
    /// отсутствие KEY_WRITE у подключа не должно давать ложный отказ. Различие масок видно только на настоящем реестре с ACL, фейк его не
    /// моделирует (проверяется песочницей).
    /// </summary>
    public static bool CanDelete(IRegTreeHandle h, int depth = 0)
    {
        if (depth > RegistryTreeHasher.MaxDepth) return false;
        foreach (var name in h.SubKeyNames())
        {
            IRegTreeHandle? child;
            try { child = h.OpenSub(name, true); }
            catch (UnauthorizedAccessException) { return false; }
            if (child is null) continue;
            using (child)
            {
                if (child.IsSymbolicLink || !CanDelete(child, depth + 1)) return false;
            }
        }
        return true;
    }

    /// <summary>Дети удаляются в глубину и закрываются до родителя, корень последним (NtDeleteKey вступает в силу при закрытии хэндлов).</summary>
    public static void DeleteTree(IRegTreeHandle h)
    {
        foreach (var name in h.SubKeyNames().ToList())
        {
            using var child = h.OpenSub(name, true);
            if (child is null) continue;
            if (child.IsSymbolicLink) throw new StepFailureException(StepCodes.RegTreeHasLink);
            DeleteTree(child);
        }
        h.DeleteSelf();
    }

    public static bool Matches(TreeStats s, string expectedSha) => s.Usable && string.Equals(s.Sha256, expectedSha, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Экспорт поддерева в формат .reg (Windows Registry Editor Version 5.00, UTF-16LE с BOM, CRLF).</summary>
internal static class RegExport
{
    public static byte[] ToRegFile(string fullKeyPath, IRegTreeHandle root)
    {
        var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
        Export(sb, fullKeyPath, root, 0);
        var body = Encoding.Unicode.GetBytes(sb.ToString());
        var result = new byte[body.Length + 2];
        result[0] = 0xFF;
        result[1] = 0xFE;
        Buffer.BlockCopy(body, 0, result, 2, body.Length);
        return result;
    }

    static void Export(StringBuilder sb, string path, IRegTreeHandle h, int depth)
    {
        if (depth > RegistryTreeHasher.MaxDepth) return;
        // Защита второго уровня: Walk уже отвергает такие имена до копии, но сам экспорт не должен молча записать инъекцию в .reg.
        if (!RegistryTreeHasher.IsSafeName(path)) throw new StepFailureException(StepCodes.RegTreeBadName);
        sb.Append('[').Append(path).Append("]\r\n");
        foreach (var (name, type, raw) in h.RawValues().OrderBy(v => v.Name.Length == 0 ? 0 : 1).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            AppendValue(sb, name, type, raw);
        sb.Append("\r\n");
        foreach (var name in h.SubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            using var child = h.OpenSub(name, false);
            if (child is null || child.IsSymbolicLink) continue;
            Export(sb, path + "\\" + name, child, depth + 1);
        }
    }

    static string Escape(string s) => s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>REG_SZ записывается строкой, только если это каноническая строка: один хвостовой NUL и ни одного управляющего символа.</summary>
    static bool TryCanonicalString(byte[] raw, out string text)
    {
        text = string.Empty;
        if (raw.Length < 2 || raw.Length % 2 != 0) return false;
        var s = Encoding.Unicode.GetString(raw);
        if (s[^1] != '\0') return false;
        s = s[..^1];
        foreach (var ch in s)
            if (ch < ' ' || ch == '\u007f') return false;
        text = s;
        return true;
    }

    static void AppendValue(StringBuilder sb, string name, RegType type, byte[] raw)
    {
        if (!RegistryTreeHasher.IsSafeName(name)) throw new StepFailureException(StepCodes.RegTreeBadName);
        if (type == RegType.Other) throw new StepFailureException(StepCodes.RegTreeBadType);
        var label = name.Length == 0 ? "@" : "\"" + Escape(name) + "\"";
        sb.Append(label).Append('=');
        if (type == RegType.String && TryCanonicalString(raw, out var text))
        {
            sb.Append('"').Append(Escape(text)).Append("\"\r\n");
            return;
        }
        if (type == RegType.DWord && raw.Length == 4)
        {
            sb.Append("dword:").Append(BinaryPrimitives.ReadUInt32LittleEndian(raw).ToString("x8", System.Globalization.CultureInfo.InvariantCulture)).Append("\r\n");
            return;
        }
        var prefix = type switch
        {
            RegType.String => "hex(1):",
            RegType.ExpandString => "hex(2):",
            RegType.Binary => "hex:",
            RegType.DWord => "hex(4):",
            RegType.MultiString => "hex(7):",
            RegType.QWord => "hex(b):",
            _ => "hex(0):",
        };
        sb.Append(prefix);
        var col = label.Length + 1 + prefix.Length;
        for (var i = 0; i < raw.Length; i++)
        {
            sb.Append(raw[i].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            col += 2;
            if (i == raw.Length - 1) continue;
            sb.Append(',');
            col++;
            if (col >= 76)
            {
                sb.Append("\\\r\n  ");
                col = 2;
            }
        }
        sb.Append("\r\n");
    }
}

/// <summary>Шаг находки реестра, который умеет показать, что произошло бы (DryRun), не выполняя записи.</summary>
internal interface IFindingStep
{
    FindingPreview Preview(ExecEnv env);
}

internal sealed record FindingPreview(string? Display, int Subkeys, string? Rule);

/// <summary>
/// Удаление ключа-сироты (Uninstall\x, App Paths\x) с копией в HKLM\SOFTWARE\Chistovik\Quarantine\&lt;opId&gt;\&lt;n&gt;.
/// Порядок открытия: OpenTree, ссылка и ResolvedPath, перепроверка доказательства (OrphanProbe), обход дерева и хэш, шлюз.
/// Запись: хэш источника не изменился, копия, хэш копии равен ожидаемому (иначе копия удаляется, backup-mismatch), .reg, удаление в глубину.
/// </summary>
internal sealed class RegistryKeyDeleteStep : PlannedStep, IFindingStep
{
    readonly OrphanId? _id;
    readonly RegLocation _loc;
    readonly Guid _opId;
    readonly int _n;
    readonly string _sid;
    readonly bool _expert;
    string? _display;
    TreeStats? _stats;

    RegistryKeyDeleteStep(OrphanId? id, RegLocation loc, Guid opId, int n, string sid, bool expert, string targetJson, string? display)
        : base(StepKind.RegistryKeyDelete, RegTreeJson.KeyTargetKey(loc), targetJson, RegTreeJson.KeyAbsentJson)
    {
        _id = id;
        _loc = loc;
        _opId = opId;
        _n = n;
        _sid = sid;
        _expert = expert;
        _display = display;
    }

    /// <summary>Шаг только читает состояние (восстановление после обрыва): доказательство и политика не проверяются, записи нет.</summary>
    public bool RecoveryOnly { get; init; }

    /// <summary>Шаг для восстановления по записи журнала (StepFactory.Rebuild): доказательства не перечитываются.</summary>
    public static RegistryKeyDeleteStep ForRecovery(RegKeyDeleteTarget t, string clientSid)
    {
        var backupOk = RegTreeJson.TryParseBackupKey(t.Backup.Key, out var opId, out var n);
        return new RegistryKeyDeleteStep(null, RegTreeJson.LocationOf(t), backupOk ? opId : Guid.Empty, backupOk ? n : 0, clientSid, t.Expert,
            RegTreeJson.DeleteTargetJson(t), t.Display) { RecoveryOnly = true };
    }

    static OrphanEvidence Probe(ExecEnv env, OrphanId id, IRegTreeHandle h)
        => id.Class == OrphanClass.Arp ? OrphanProbe.Uninstall(env, h, id) : OrphanProbe.AppPath(env, h);

    static string? Trim(string? s) => s is { Length: > 120 } ? s[..120] : s;

    /// <summary>Планирование: агент сам читает ключ и строит target_json (доказательство, копия, имя для людей). Ключа нет: шаг пропускается.</summary>
    public static RegistryKeyDeleteStep Plan(ExecEnv env, OrphanId id, Guid opId, int n, string sid, bool expert)
    {
        var loc = id.Location(sid);
        OrphanEvidence ev;
        var gone = false;
        using (var h = env.RegistryTree.OpenTree(loc, false))
        {
            if (h is null)
            {
                gone = true;
                ev = OrphanEvidence.Failed("finding-gone");
            }
            else if (!env.Gate.CheckTreeHandle(loc, h).IsAllowed) ev = OrphanEvidence.Failed("reg-resolved-mismatch");
            else ev = Probe(env, id, h);
        }
        var display = Trim(ev.Display ?? id.Name);
        var backup = new RegKeyBackup(RegTreeJson.QuarantineKeyPath(opId, n), RegTreeJson.BackupFile(opId, n));
        var target = RegTreeJson.DeleteTarget(loc, ev.ToJson(), backup, expert, display);
        return new RegistryKeyDeleteStep(id, loc, opId, n, sid, expert, RegTreeJson.DeleteTargetJson(target), display)
        {
            PresetRefusal = gone ? new Refusal(StepCodes.NotFound, "finding-gone", true) : null,
        };
    }

    sealed record Eval(Refusal? Refusal, ApprovedRegistryTreeDelete? Approved, TreeStats? Stats);

    Eval Evaluate(ExecEnv env, IRegTreeHandle h)
    {
        var id = _id!;
        var th = env.Gate.CheckTreeHandle(_loc, h);
        if (!th.IsAllowed) return new Eval(Refusal.From(th), null, null);
        var ev = Probe(env, id, h);
        _display = Trim(ev.Display ?? _display);

        var g0 = env.Gate.CheckOrphanKeyDelete(_loc, h, ev, 0, _expert, _opId, _n, string.Empty, _sid);
        if (!g0.IsAllowed) return new Eval(Refusal.From(g0), null, null);

        if (!RegistryTreeHasher.IsSafeName(_loc.KeyPath))
            return new Eval(new Refusal(StepCodes.RegTreeBadName, "reg-tree-bad-name"), null, null);

        var stats = RegistryTreeHasher.Compute(h);
        _stats = stats;
        if (stats.HasLink) return new Eval(new Refusal(StepCodes.RegTreeHasLink, "reg-tree-has-link"), null, stats);
        if (stats.BadName) return new Eval(new Refusal(StepCodes.RegTreeBadName, "reg-tree-bad-name"), null, stats);
        if (stats.BadType) return new Eval(new Refusal(StepCodes.RegTreeBadType, "reg-tree-bad-type"), null, stats);
        if (stats.TooLarge && stats.Subkeys <= RegistryTreeHasher.MaxSubkeys)
            return new Eval(new Refusal(StepCodes.RegTreeTooLarge, "reg-tree-too-large"), null, stats);

        var g = env.Gate.CheckOrphanKeyDelete(_loc, h, ev, stats.Subkeys, _expert, _opId, _n, stats.Sha256, _sid);
        if (!g.IsAllowed) return new Eval(Refusal.From(g), null, stats);
        return new Eval(null, g.Approved, stats);
    }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        if (RecoveryOnly) return OpenOutcome.Ok(new KeySession(env, _loc, null, null, null));

        var h = env.RegistryTree.OpenTree(_loc, forWrite);
        if (h is null) return OpenOutcome.Refuse(new Refusal(StepCodes.NotFound, "finding-gone", true));
        var keep = false;
        try
        {
            var e = Evaluate(env, h);
            if (e.Refusal is { } r) return OpenOutcome.Refuse(r);
            // Право DELETE на каждый подключ проверяется до копии: иначе отказ обнаружился бы посреди DeleteTree.
            // В предпроверке (forWrite=false) для этого открывается отдельный хэндл с правом записи; он ничего не пишет и сразу закрывается.
            if (!Deletable(env, h, forWrite)) return OpenOutcome.Refuse(StepCodes.AccessDenied, "reg-tree-not-deletable");
            keep = forWrite;
            return OpenOutcome.Ok(new KeySession(env, _loc, forWrite ? h : null, e.Approved, e.Stats));
        }
        finally
        {
            if (!keep) h.Dispose();
        }
    }

    bool Deletable(ExecEnv env, IRegTreeHandle h, bool forWrite)
    {
        if (forWrite) return RegTreeOps.CanDelete(h);
        try
        {
            using var w = env.RegistryTree.OpenTree(_loc, true);
            return w is null || RegTreeOps.CanDelete(w);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public FindingPreview Preview(ExecEnv env)
    {
        var o = Open(env, null, false);
        if (o.Refusal is { } r) return new FindingPreview(_display, _stats?.Subkeys ?? 0, r.Rule ?? r.Code);
        o.Session!.Dispose();
        return new FindingPreview(_display, _stats?.Subkeys ?? 0, null);
    }

    /// <summary>
    /// Сессия удаления. До записи состояние берётся из обхода при открытии (хэш проверяется заново в Write), после записи читается свежим
    /// хэндлом. handle = null: только чтение.
    /// </summary>
    sealed class KeySession(ExecEnv env, RegLocation loc, IRegTreeHandle? handle, ApprovedRegistryTreeDelete? approved, TreeStats? stats) : IStepSession
    {
        IRegTreeHandle? _handle = handle;
        bool _written;

        public string ReadStateJson()
        {
            if (!_written && stats is { Usable: true }) return RegTreeJson.KeyStateJson(true, stats.Subkeys, stats.Values, stats.Sha256);
            using var h = env.RegistryTree.OpenTree(loc, false);
            if (h is null) return RegTreeJson.KeyAbsentJson;
            var s = RegistryTreeHasher.Compute(h);
            return RegTreeJson.KeyStateJson(true, s.Subkeys, s.Values, s.Usable ? s.Sha256 : null);
        }

        public void Write()
        {
            if (_handle is null || approved is null) throw new InvalidOperationException("Сессия открыта без права записи");
            _written = true;
            var now = RegistryTreeHasher.Compute(_handle);
            if (!RegTreeOps.Matches(now, approved.ExpectedSha256)) throw new StepFailureException(StepCodes.ExternalChange);

            Quarantine(_handle);
            try
            {
                RegTreeOps.DeleteTree(_handle);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Сбой посреди удаления оставляет ключ выпотрошенным, а шаг Failed не откатывается (Undo берёт только Done).
                // Простой вариант: недостающее копируется обратно из проверенной копии до исходного хэша.
                //  - восстановили: ключ цел, шаг Failed с кодом исходной ошибки (журнал и состояние согласованы, повтор возможен);
                //  - не удалось: шаг Failed с кодом partial-delete, копия в реестре и .reg остаётся для ручного возврата.
                // Done с partial-delete не выбран намеренно: откат отказывается писать поверх существующего ключа, то есть Undo
                // всё равно не смог бы вернуть недостающее, а журнал соврал бы, что ключ удалён.
                if (Heal()) throw;
                throw new StepFailureException(StepCodes.PartialDelete);
            }
            _handle.Dispose();
            _handle = null;
        }

        bool Heal()
        {
            try
            {
                using var copy = env.RegistryTree.OpenTree(approved!.Destination, false);
                if (copy is null || !RegTreeOps.Matches(RegistryTreeHasher.Compute(copy), approved.ExpectedSha256)) return false;
                // Приёмник это оригинал в HKU клиента: подключ-ссылка или чужой не-ссылочный ключ не принимается (см. Copy), heal тогда не удался.
                RegTreeOps.Copy(copy, _handle!, requireCreated: false);
                return RegTreeOps.Matches(RegistryTreeHasher.Compute(_handle!), approved.ExpectedSha256);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return false;
            }
        }

        void Quarantine(IRegTreeHandle source)
        {
            var chain = new List<IRegTreeHandle>();
            try
            {
                var cur = env.RegistryTree.OpenTree(new RegLocation(RegRoot.Machine, null, "SOFTWARE", string.Empty), true)
                          ?? throw new StepFailureException(StepCodes.AccessDenied);
                chain.Add(cur);
                foreach (var seg in new[] { "Chistovik", "Quarantine", approved!.OpId.ToString("N") })
                {
                    cur = cur.CreateSub(seg, out _);
                    chain.Add(cur);
                }
                var leaf = approved.N.ToString(System.Globalization.CultureInfo.InvariantCulture);

                // Устаревшая копия того же opId и n (повтор после обрыва) удаляется целиком перед записью.
                using (var stale = cur.OpenSub(leaf, true))
                {
                    if (stale is not null) RegTreeOps.DeleteTree(stale);
                }

                var dest = cur.CreateSub(leaf, out var created);
                chain.Add(dest);
                // Слот копии занят кем-то, кто появился после удаления устаревшей копии: чужой ключ не трогаем и не перезаписываем.
                if (!created) throw new StepFailureException(StepCodes.ExternalChange);
                if (dest.IsSymbolicLink || !StepGate.SameKey(dest.ResolvedPath, StepGate.ExpectedPath(approved.Destination)))
                {
                    TryDelete(dest);
                    throw new StepFailureException(StepCodes.PolicyDenied);
                }

                try
                {
                    RegTreeOps.Copy(source, dest);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    TryDelete(dest);
                    throw;
                }
                var copied = RegistryTreeHasher.Compute(dest);
                if (!RegTreeOps.Matches(copied, approved.ExpectedSha256))
                {
                    TryDelete(dest);
                    throw new StepFailureException(StepCodes.BackupMismatch);
                }

                if (env.Quarantine is { } store) store.WriteFile(approved.BackupFile, RegExport.ToRegFile(approved.FullKeyPath, dest));
            }
            finally
            {
                for (var i = chain.Count - 1; i >= 0; i--) chain[i].Dispose();
            }
        }

        static void TryDelete(IRegTreeHandle h)
        {
            try { RegTreeOps.DeleteTree(h); }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }

        public void Dispose()
        {
            _handle?.Dispose();
            _handle = null;
        }
    }
}

/// <summary>
/// Откат удаления ключа: копия из Quarantine обратно. Оригинал уже есть: записи нет (компаратор «ключ есть» даёт already-reverted).
/// Копии нет: backup-missing; хэш копии не равен before.treeSha256: backup-mismatch. Копия остаётся до очистки журнала.
/// </summary>
internal sealed class RegistryKeyRestoreStep : PlannedStep
{
    readonly RegKeyDeleteTarget _target;
    readonly RegLocation _loc;
    readonly string _sha;
    readonly string _sid;

    public RegistryKeyRestoreStep(RegKeyDeleteTarget target, string beforeJson, string clientSid)
        : base(StepKind.RegistryKeyDelete, RegTreeJson.KeyTargetKey(RegTreeJson.LocationOf(target)), RegTreeJson.DeleteTargetJson(target), beforeJson)
    {
        _target = target;
        _loc = RegTreeJson.LocationOf(target);
        _sid = clientSid;
        try { _sha = RegTreeJson.ParseKeyState(beforeJson).TreeSha256 ?? string.Empty; }
        catch (InvalidDataException) { _sha = string.Empty; }
    }

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        var g = env.Gate.CheckOrphanKeyRestore(_loc, _target.Backup.Key, _sha, _sid);
        if (!g.IsAllowed) return OpenOutcome.Refuse(g);
        var approved = g.Approved!;

        if (!forWrite)
        {
            var existing = env.RegistryTree.OpenTree(_loc, false);
            if (existing is not null)
            {
                using (existing)
                {
                    var th = env.Gate.CheckTreeHandle(_loc, existing);
                    if (!th.IsAllowed) return OpenOutcome.Refuse(th);
                }
            }
            return OpenOutcome.Ok(new RestoreSession(env, _loc, approved, null));
        }

        var path = _loc.KeyPath;
        var cut = path.LastIndexOf('\\');
        if (cut <= 0) return OpenOutcome.Refuse(StepCodes.PolicyDenied, "reg-bad-key");
        var parentLoc = new RegLocation(_loc.Root, _loc.UserSid, path[..cut], string.Empty);
        var parent = env.RegistryTree.OpenTree(parentLoc, true);
        if (parent is null) return OpenOutcome.Refuse(StepCodes.NotFound, "restore-parent-missing");
        var pt = env.Gate.CheckTreeHandle(parentLoc, parent);
        if (!pt.IsAllowed)
        {
            parent.Dispose();
            return OpenOutcome.Refuse(pt);
        }
        return OpenOutcome.Ok(new RestoreSession(env, _loc, approved, parent));
    }

    sealed class RestoreSession(ExecEnv env, RegLocation loc, ApprovedRegistryTreeRestore approved, IRegTreeHandle? parent) : IStepSession
    {
        public string ReadStateJson()
        {
            using var h = env.RegistryTree.OpenTree(loc, false);
            if (h is null) return RegTreeJson.KeyAbsentJson;
            var s = RegistryTreeHasher.Compute(h);
            return RegTreeJson.KeyStateJson(true, s.Subkeys, s.Values, s.Usable ? s.Sha256 : null);
        }

        public void Write()
        {
            if (parent is null) throw new InvalidOperationException("Сессия открыта без права записи");
            var leaf = loc.KeyPath[(loc.KeyPath.LastIndexOf('\\') + 1)..];
            using (var existing = parent.OpenSub(leaf, false))
            {
                if (existing is not null) throw new StepFailureException(StepCodes.ExternalChange);
            }

            using var backup = env.RegistryTree.OpenTree(approved.Backup, false) ?? throw new StepFailureException(StepCodes.BackupMissing);
            if (!env.Gate.CheckTreeHandle(approved.Backup, backup).IsAllowed) throw new StepFailureException(StepCodes.PolicyDenied);
            if (!RegTreeOps.Matches(RegistryTreeHasher.Compute(backup), approved.ExpectedSha256)) throw new StepFailureException(StepCodes.BackupMismatch);

            // Проверка «оригинала нет» и создание не атомарны: REG_OPENED_EXISTING_KEY значит, что ключ появился в этом окне (или это ссылка),
            // чужой ключ не трогаем и не удаляем.
            using var dest = parent.CreateSub(leaf, out var created);
            if (!created) throw new StepFailureException(StepCodes.ExternalChange);
            try
            {
                if (!env.Gate.CheckTreeHandle(loc, dest).IsAllowed) throw new StepFailureException(StepCodes.PolicyDenied);
                RegTreeOps.Copy(backup, dest);
                if (!RegTreeOps.Matches(RegistryTreeHasher.Compute(dest), approved.ExpectedSha256)) throw new StepFailureException(StepCodes.BackupMismatch);
            }
            catch
            {
                try { RegTreeOps.DeleteTree(dest); }
                catch (Exception e) when (e is not OperationCanceledException) { }
                throw;
            }
        }

        public void Dispose() => parent?.Dispose();
    }
}

/// <summary>
/// Общая часть удаления значения-сироты (Run/RunOnce, SharedDLLs) и возврата прежнего значения при откате.
/// Значение пишется через IRegistry (ApprovedRegistryWrite), потому что у IRegTreeHandle нет удаления значений.
/// </summary>
internal abstract class ValueCleanupStep : PlannedStep, IFindingStep
{
    protected readonly OrphanId? Id;
    protected readonly string Sid;
    protected readonly RegValueState? RestoreTo;
    protected RegLocation Loc;
    string? _display;

    protected ValueCleanupStep(StepKind kind, OrphanId? id, RegLocation loc, string sid, string targetJson, string desiredJson,
        RegValueState? restoreTo, string? display)
        : base(kind, StateJson.RegistryKey(loc), targetJson, desiredJson)
    {
        Id = id;
        Loc = loc;
        Sid = sid;
        RestoreTo = restoreTo;
        _display = display;
    }

    /// <summary>Шаг только читает состояние (восстановление после обрыва).</summary>
    public bool RecoveryOnly { get; init; }

    protected static string Absent => StateJson.RegistryJson(new RegValueState(false, RegType.None, null));

    /// <summary>Расположение значения и доказательство по свежему состоянию хэндла. null: значения больше нет.</summary>
    protected abstract (RegLocation Loc, OrphanEvidence Evidence)? Collect(ExecEnv env, IRegKeyHandle h);

    protected abstract GateResult<ApprovedRegistryWrite> GateRemove(ExecEnv env, RegLocation loc, IRegKeyHandle h, OrphanEvidence e);

    protected abstract GateResult<ApprovedRegistryWrite> GateRestore(ExecEnv env, IRegKeyHandle h, RegValueState desired);

    protected override OpenOutcome OpenCore(ExecEnv env, SimState? sim, bool forWrite)
    {
        if (RecoveryOnly)
        {
            var rh = env.Registry.Open(Loc, false, false);
            return OpenOutcome.Ok(new RegistrySession(rh, null, Loc.ValueName, StateJson.RegistryJson));
        }

        var h = env.Registry.Open(Loc, forWrite, false);
        if (h is null)
        {
            if (RestoreTo is not null) return OpenOutcome.Refuse(StepCodes.NotFound, "restore-key-missing");
            return OpenOutcome.Refuse(new Refusal(StepCodes.NotFound, "finding-gone", true));
        }
        try
        {
            if (RestoreTo is { } desired)
            {
                var gr = GateRestore(env, h, desired);
                if (!gr.IsAllowed)
                {
                    h.Dispose();
                    return OpenOutcome.Refuse(gr);
                }
                return OpenOutcome.Ok(new RegistrySession(h, gr.Approved, Loc.ValueName, StateJson.RegistryJson));
            }

            var c = Collect(env, h);
            if (c is null)
            {
                h.Dispose();
                return OpenOutcome.Refuse(new Refusal(StepCodes.NotFound, "finding-gone", true));
            }
            var (loc, ev) = c.Value;
            _display = ev.Display ?? _display;
            var g = GateRemove(env, loc, h, ev);
            if (!g.IsAllowed)
            {
                h.Dispose();
                return OpenOutcome.Refuse(g);
            }
            return OpenOutcome.Ok(new RegistrySession(h, g.Approved, loc.ValueName, StateJson.RegistryJson));
        }
        catch
        {
            h.Dispose();
            throw;
        }
    }

    public FindingPreview Preview(ExecEnv env)
    {
        var o = Open(env, null, false);
        if (o.Refusal is { } r) return new FindingPreview(_display, 0, r.Rule ?? r.Code);
        o.Session!.Dispose();
        return new FindingPreview(_display, 0, null);
    }
}

/// <summary>Удаление записи Run/RunOnce с подтверждённым доказательством «команда не находит файл»; при откате запись возвращается из журнала.</summary>
internal sealed class StartupRemoveStep : ValueCleanupStep
{
    StartupRemoveStep(OrphanId? id, RegLocation loc, string sid, string targetJson, string desiredJson, RegValueState? restoreTo, string? display)
        : base(StepKind.StartupRemove, id, loc, sid, targetJson, desiredJson, restoreTo, display)
    {
    }

    public static StartupRemoveStep Plan(ExecEnv env, OrphanId id, string sid)
    {
        var loc = id.Location(sid);
        OrphanEvidence ev;
        var gone = false;
        using (var h = env.Registry.Open(loc, false, false))
        {
            var v = h is null ? new RegValueState(false, RegType.None, null) : h.Read(loc.ValueName);
            if (!v.Exists)
            {
                gone = true;
                ev = OrphanEvidence.Failed("finding-gone");
            }
            else ev = OrphanProbe.RunValue(env, id, v);
        }
        return new StartupRemoveStep(id, loc, sid, RegTreeJson.ValueTargetJson(loc, ev.ToJson()), Absent, null, loc.ValueName)
        {
            PresetRefusal = gone ? new Refusal(StepCodes.NotFound, "finding-gone", true) : null,
        };
    }

    /// <summary>Откат или восстановление: desired = before из журнала (строка Run/RunOnce). Гейт CheckStartupRestore.</summary>
    public static StartupRemoveStep ForRestore(ValueRemoveTarget t, string beforeJson, string sid)
    {
        var loc = RegTreeJson.LocationOf(t);
        return new StartupRemoveStep(null, loc, sid, ValueJson(t), beforeJson, StateJson.ParseRegistry(beforeJson), t.Name);
    }

    public static StartupRemoveStep ForRecovery(ValueRemoveTarget t, string sid)
        => new(null, RegTreeJson.LocationOf(t), sid, ValueJson(t), Absent, null, t.Name) { RecoveryOnly = true };

    static string ValueJson(ValueRemoveTarget t) => RegTreeJson.ValueTargetJson(RegTreeJson.LocationOf(t), t.Evidence);

    protected override (RegLocation Loc, OrphanEvidence Evidence)? Collect(ExecEnv env, IRegKeyHandle h)
    {
        var v = h.Read(Loc.ValueName);
        if (!v.Exists) return null;
        return (Loc, OrphanProbe.RunValue(env, Id!, v));
    }

    protected override GateResult<ApprovedRegistryWrite> GateRemove(ExecEnv env, RegLocation loc, IRegKeyHandle h, OrphanEvidence e)
        => env.Gate.CheckStartupRemove(loc, h, e, Sid);

    protected override GateResult<ApprovedRegistryWrite> GateRestore(ExecEnv env, IRegKeyHandle h, RegValueState desired)
        => env.Gate.CheckStartupRestore(Loc, h, desired, Sid);
}

/// <summary>Удаление значения SharedDLLs, чей файл исчез; при откате счётчик возвращается стандартной проверкой записи.</summary>
internal sealed class SharedDllRemoveStep : ValueCleanupStep
{
    SharedDllRemoveStep(OrphanId? id, RegLocation loc, string sid, string targetJson, string desiredJson, RegValueState? restoreTo, string? display)
        : base(StepKind.SharedDllRemove, id, loc, sid, targetJson, desiredJson, restoreTo, display)
    {
    }

    public static SharedDllRemoveStep Plan(ExecEnv env, OrphanId id, string sid)
    {
        var parent = id.Location(sid);
        OrphanEvidence ev;
        var gone = false;
        var loc = parent;
        using (var h = env.Registry.Open(parent, false, false))
        {
            if (h is null)
            {
                gone = true;
                ev = OrphanEvidence.Failed("finding-gone");
            }
            else
            {
                var (name, evidence) = OrphanProbe.SharedDll(env, id, h);
                ev = evidence;
                if (name is null) gone = true;
                else loc = parent with { ValueName = name };
            }
        }
        // Имени значения нет: журналу нужен непустой Name, берём хэш из идентификатора.
        if (gone) loc = parent with { ValueName = id.Name };
        return new SharedDllRemoveStep(id, loc, sid, RegTreeJson.ValueTargetJson(loc, ev.ToJson()), Absent, null, loc.ValueName)
        {
            PresetRefusal = gone ? new Refusal(StepCodes.NotFound, "finding-gone", true) : null,
        };
    }

    public static SharedDllRemoveStep ForRestore(ValueRemoveTarget t, string beforeJson, string sid)
        => new(null, RegTreeJson.LocationOf(t), sid, RegTreeJson.ValueTargetJson(RegTreeJson.LocationOf(t), t.Evidence), beforeJson,
            StateJson.ParseRegistry(beforeJson), t.Name);

    public static SharedDllRemoveStep ForRecovery(ValueRemoveTarget t, string sid)
        => new(null, RegTreeJson.LocationOf(t), sid, RegTreeJson.ValueTargetJson(RegTreeJson.LocationOf(t), t.Evidence), Absent, null, t.Name)
        {
            RecoveryOnly = true,
        };

    protected override (RegLocation Loc, OrphanEvidence Evidence)? Collect(ExecEnv env, IRegKeyHandle h)
    {
        var (name, ev) = OrphanProbe.SharedDll(env, Id!, h);
        return name is null ? null : (Loc with { ValueName = name }, ev);
    }

    protected override GateResult<ApprovedRegistryWrite> GateRemove(ExecEnv env, RegLocation loc, IRegKeyHandle h, OrphanEvidence e)
        => env.Gate.CheckSharedDllRemove(loc, h, e);

    protected override GateResult<ApprovedRegistryWrite> GateRestore(ExecEnv env, IRegKeyHandle h, RegValueState desired)
        => env.Gate.CheckRegistryWrite(Loc, desired, h, Sid);
}

/// <summary>План операции «чистка реестра» по списку находок. Агент сам строит расположение из идентификатора и SID клиента.</summary>
internal static class FindingsPlanner
{
    public static OperationPlan Plan(ExecEnv env, ApplyFindingsRequest request, string clientSid, Guid opId)
    {
        var expert = new HashSet<string>(request.ExpertConfirmations ?? [], StringComparer.Ordinal);
        var steps = new List<PlannedStep>();
        for (var i = 0; i < request.Items.Count; i++)
        {
            var item = request.Items[i];
            if (item.Action != FindingActionKind.Remove) throw new PlanException(ResponseCodes.BadRequest, "Action: допустимо только Remove");
            if (!OrphanId.TryParse(item.FindingId, out var id, out var error))
                throw error == OrphanIdError.Unknown
                    ? new PlanException(StepCodes.PolicyDenied, "FindingId: неизвестный класс находки")
                    : new PlanException(ResponseCodes.BadRequest, "FindingId: недопустимый формат");
            try
            {
                steps.Add(id.Class switch
                {
                    OrphanClass.Arp or OrphanClass.AppPath => RegistryKeyDeleteStep.Plan(env, id, opId, i + 1, clientSid, expert.Contains(item.FindingId)),
                    OrphanClass.Run or OrphanClass.RunOnce => StartupRemoveStep.Plan(env, id, clientSid),
                    _ => SharedDllRemoveStep.Plan(env, id, clientSid),
                });
            }
            catch (Exception e) when (e is not OperationCanceledException and not PlanException)
            {
                throw new PlanException(ErrorCodes.From(e), ErrorCodes.Text(e));
            }
        }
        var first = request.Items[0].FindingId;
        var subject = request.Items.Count == 1 ? first : first + " +" + (request.Items.Count - 1);
        return new OperationPlan(OperationKind.Registry, subject.Length > 300 ? subject[..300] : subject, steps);
    }

    /// <summary>Что произойдёт при применении: по факту на находку (ключ — target_key шага). Ничего не пишется ни в реестр, ни в журнал.</summary>
    public static IReadOnlyList<FactEntry> DryRun(ExecEnv env, ApplyFindingsRequest request, string clientSid)
    {
        var plan = Plan(env, request, clientSid, request.Id);
        var facts = new List<FactEntry>();
        foreach (var step in plan.Steps)
        {
            var p = ((IFindingStep)step).Preview(env);
            facts.Add(new FactEntry(step.TargetKey, FactJson(p)));
        }
        return facts;
    }

    static string FactJson(FindingPreview p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            if (p.Display is null) w.WriteNull("display");
            else w.WriteString("display", p.Display);
            w.WriteNumber("subkeys", p.Subkeys);
            w.WriteBoolean("revertible", true);
            if (p.Rule is null) w.WriteNull("rule");
            else w.WriteString("rule", p.Rule);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
