using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Chistovik.Core.Catalog;
using Chistovik.Core.Ports;

namespace Chistovik.Core.Ipc;

/// <summary>
/// Закрытое множество запросов к агенту: тип определяется атрибутами JsonDerivedType, всё остальное не десериализуется.
/// В запросах только идентификаторы из каталога и инвентаря агента: ключей, путей, GUID, команд, XML и SID нет (DESIGN-0.1 §3).
/// Каждый запрос сам проверяет формат полей в <see cref="Validate"/>; обработчик вызывается только после успешной проверки.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PingRequest), "Ping")]
[JsonDerivedType(typeof(ApplyTweaksRequest), "ApplyTweaks")]
[JsonDerivedType(typeof(ApplyModeRequest), "ApplyMode")]
[JsonDerivedType(typeof(SetStartupEntryRequest), "SetStartupEntry")]
[JsonDerivedType(typeof(SetServiceStartRequest), "SetServiceStart")]
[JsonDerivedType(typeof(SetTaskEnabledRequest), "SetTaskEnabled")]
[JsonDerivedType(typeof(SetPowerSchemeRequest), "SetPowerScheme")]
[JsonDerivedType(typeof(SetPowerSettingRequest), "SetPowerSetting")]
[JsonDerivedType(typeof(UndoRequest), "Undo")]
public abstract record AgentRequest
{
    public required Guid Id { get; init; }

    /// <summary>null, если поля допустимы; иначе причина отказа (для кода bad-request).</summary>
    public abstract string? Validate();
}

public sealed record PingRequest : AgentRequest
{
    public override string? Validate() => null;
}

public sealed record TweakChange
{
    public required string TweakId { get; init; }
    public required bool Enabled { get; init; }
}

public sealed record ApplyTweaksRequest : AgentRequest
{
    public const int MaxChanges = 32;

    public required IReadOnlyList<TweakChange> Changes { get; init; }

    public override string? Validate()
    {
        if (Changes is null || Changes.Count is < 1 or > MaxChanges) return "Changes: нужно от 1 до 32 элементов";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Changes)
        {
            if (c is null || c.TweakId is null) return "Changes: пустой элемент";
            if (!Rules.CatalogId.IsMatch(c.TweakId)) return "TweakId: недопустимый формат";
            if (!seen.Add(c.TweakId)) return "Changes: повтор TweakId";
            if (TweakCatalog.Shared.FindTweak(c.TweakId) is null) return "TweakId: нет в каталоге";
        }
        return null;
    }
}

public sealed record ApplyModeRequest : AgentRequest
{
    public required string ModeId { get; init; }

    public override string? Validate()
    {
        if (ModeId is null || !Rules.CatalogId.IsMatch(ModeId)) return "ModeId: недопустимый формат";
        return TweakCatalog.Shared.FindMode(ModeId) is null ? "ModeId: нет в каталоге" : null;
    }
}

public sealed record SetStartupEntryRequest : AgentRequest
{
    public const int MaxEntryIdLength = 300;

    public required string EntryId { get; init; }
    public required bool Enabled { get; init; }

    public override string? Validate()
    {
        if (EntryId is null || EntryId.Length is < 1 or > MaxEntryIdLength) return "EntryId: длина от 1 до 300";
        foreach (var ch in EntryId)
            if (ch < ' ' || ch == '\u007f') return "EntryId: управляющие символы недопустимы";
        return Rules.EntryId.IsMatch(EntryId) ? null : "EntryId: недопустимый формат";
    }
}

public sealed record SetServiceStartRequest : AgentRequest
{
    public required string ServiceName { get; init; }
    public required StartMode Start { get; init; }

    public override string? Validate()
    {
        if (ServiceName is null || !Rules.ServiceName.IsMatch(ServiceName)) return "ServiceName: недопустимое имя";
        if (ServiceName.EndsWith(' ') || ServiceName.StartsWith(' ')) return "ServiceName: пробел в начале или конце";
        return Enum.IsDefined(Start) ? null : "Start: недопустимое значение";
    }
}

public sealed record SetTaskEnabledRequest : AgentRequest
{
    public const int MaxTaskPathLength = 1000;

    public required string TaskPath { get; init; }
    public required bool Enabled { get; init; }

    public override string? Validate()
    {
        if (TaskPath is null || TaskPath.Length > MaxTaskPathLength || !Rules.TaskPath.IsMatch(TaskPath))
            return "TaskPath: недопустимый путь";
        foreach (var seg in TaskPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (seg is "." or ".." || seg.EndsWith(' ') || seg.StartsWith(' ') || seg.EndsWith('.'))
                return "TaskPath: недопустимый сегмент";
        return null;
    }
}

/// <summary>Выбор схемы питания. В JSON: saver, bal, high, ult.</summary>
public enum PowerPlan
{
    [JsonStringEnumMemberName("saver")] Saver,
    [JsonStringEnumMemberName("bal")] Bal,
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("ult")] Ult,
}

public sealed record SetPowerSchemeRequest : AgentRequest
{
    public required PowerPlan Plan { get; init; }

    public override string? Validate() => Enum.IsDefined(Plan) ? null : "Plan: недопустимое значение";
}

/// <summary>Идентификаторы параметров питания. Совпадают с power.json (проверяется тестом).</summary>
public static class PowerSettingIds
{
    public static readonly IReadOnlyList<string> All = ["min", "max", "boost", "display", "sleep", "aspm", "usb"];
}

public sealed record SetPowerSettingRequest : AgentRequest
{
    public required string SettingId { get; init; }
    public uint? Ac { get; init; }
    public uint? Dc { get; init; }

    public override string? Validate()
    {
        if (SettingId is null || !PowerSettingIds.All.Contains(SettingId)) return "SettingId: нет в каталоге";
        var entry = PowerCatalog.Shared.FindSetting(SettingId);
        if (entry is null) return "SettingId: нет в каталоге";
        if (Ac is null && Dc is null) return "Ac или Dc: нужно хотя бы одно значение";
        foreach (var v in new[] { Ac, Dc })
            if (v is { } x && (x < entry.Min || x > entry.Max)) return $"Значение вне диапазона {entry.Min}–{entry.Max}";
        return null;
    }
}

public enum ConflictAction { Overwrite, Skip }

public sealed record UndoResolution
{
    public required long StepId { get; init; }
    public required ConflictAction Action { get; init; }
}

public sealed record UndoRequest : AgentRequest
{
    public const int MaxResolutions = 256;

    public required Guid OperationId { get; init; }
    public IReadOnlyList<UndoResolution>? Resolutions { get; init; }

    public override string? Validate()
    {
        if (OperationId == Guid.Empty) return "OperationId: пустой";
        if (Resolutions is null) return null;
        if (Resolutions.Count > MaxResolutions) return "Resolutions: слишком много";
        var seen = new HashSet<long>();
        foreach (var r in Resolutions)
        {
            if (r is null) return "Resolutions: пустой элемент";
            if (r.StepId <= 0) return "Resolutions: StepId должен быть положительным";
            if (!seen.Add(r.StepId)) return "Resolutions: повтор StepId";
            if (!Enum.IsDefined(r.Action)) return "Resolutions: недопустимое действие";
        }
        return null;
    }
}

/// <summary>Правила форматов полей запросов (DESIGN-0.1 §3 и §7).</summary>
static partial class Rules
{
    /// <summary>Идентификаторы каталога (твики, режимы): латиница, цифры, «_» и «-».</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,64}\z", RegexOptions.CultureInvariant)]
    public static partial Regex CatalogIdRegex();
    public static Regex CatalogId { get; } = CatalogIdRegex();

    [GeneratedRegex(@"^(run:(hklm64|hklm32|hku):[^\r\n]+|folder:(user|common):[^\r\n]+)\z", RegexOptions.CultureInvariant)]
    public static partial Regex EntryIdRegex();
    public static Regex EntryId { get; } = EntryIdRegex();

    /// <summary>Имя службы: тот же алфавит и длина 1–256, что в ServicePolicy (ограничение ≤257 из спецификации выполняется).</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_.\-$@ ]{1,256}\z", RegexOptions.CultureInvariant)]
    public static partial Regex ServiceNameRegex();
    public static Regex ServiceName { get; } = ServiceNameRegex();

    /// <summary>Путь задачи: ведущий «\», сегменты 1–255 символов без \ / : * ? " &lt; &gt; | и управляющих.</summary>
    [GeneratedRegex("^(\\\\[^\\\\/:*?\"<>|\\u0000-\\u001f]{1,255})+\\z", RegexOptions.CultureInvariant)]
    public static partial Regex TaskPathRegex();
    public static Regex TaskPath { get; } = TaskPathRegex();
}

/// <summary>Шаг в ответе агента.</summary>
public sealed record StepResult(long StepId, int Ord, StepKind Kind, StepState State, string? Code);

/// <summary>Конфликт отката: текущее состояние цели не совпадает ни с before, ни с after.</summary>
public sealed record ConflictInfo(long StepId, StepKind Kind, string CurrentDisplay, Guid? ChangedByOperationId);

public sealed record AgentResponse(
    Guid Id,
    bool Ok,
    string Code,
    string? Message = null,
    Guid? OperationId = null,
    IReadOnlyList<StepResult>? Steps = null,
    IReadOnlyList<ConflictInfo>? Conflicts = null);

/// <summary>Коды ответа агента (DESIGN-0.1 §3). Pong — ответ на Ping.</summary>
public static class ResponseCodes
{
    public const string Pong = "pong";
    public const string Ok = "ok";
    public const string NoChange = "no-change";
    public const string Partial = "partial";
    public const string PolicyDenied = "policy-denied";
    public const string NotFound = "not-found";
    public const string Conflict = "conflict";
    public const string Busy = "busy";
    public const string JournalUnavailable = "journal-unavailable";
    public const string JournalUntrusted = "journal-untrusted";
    public const string JournalNewerSchema = "journal-newer-schema";
    public const string BadRequest = "bad-request";
    public const string UnknownOperation = "unknown-operation";
    public const string Internal = "internal";
}

/// <summary>Проверенный клиент канала (заполняет Native после ClientVerifier).</summary>
public sealed record ClientContext(string UserSid, int Pid, int SessionId);

/// <summary>Обработчик операций с правами. Получает только запрос, прошедший Validate; повтор того же Id возвращает сохранённый результат.</summary>
public interface IOperationHandler
{
    Task<AgentResponse> HandleAsync(AgentRequest request, ClientContext client, CancellationToken ct);
}

/// <summary>
/// Строгие перечисления: только строка, совпадающая с именем члена (или с JsonStringEnumMemberName) с учётом регистра.
/// Числа, числа в строках, «A,B» (флаги) и неизвестные имена отвергаются. Действует вместо JsonStringEnumConverter,
/// который принимает перечисление через запятую как побитовое ИЛИ.
/// </summary>
public sealed class StrictEnumConverter : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(typeof(Impl<>).MakeGenericType(typeToConvert))!;

    sealed class Impl<T> : JsonConverter<T> where T : struct, Enum
    {
        readonly Dictionary<string, T> _byName = new(StringComparer.Ordinal);
        readonly Dictionary<T, string> _byValue = [];

        public Impl()
        {
            foreach (var f in typeof(T).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            {
                var name = f.GetCustomAttributes(typeof(JsonStringEnumMemberNameAttribute), false)
                    .Cast<JsonStringEnumMemberNameAttribute>().FirstOrDefault()?.Name ?? f.Name;
                var value = (T)f.GetValue(null)!;
                _byName[name] = value;
                _byValue.TryAdd(value, name);
            }
        }

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Ожидалась строка-имя");
            var s = reader.GetString();
            if (s is null || !_byName.TryGetValue(s, out var v)) throw new JsonException("Неизвестное значение перечисления");
            return v;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        {
            if (!_byValue.TryGetValue(value, out var name)) throw new JsonException("Значение перечисления вне набора");
            writer.WriteStringValue(name);
        }
    }
}

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.Strict,
        };
        // Перечисления только строками-именами, без чисел (Start=0, Start=99) и без «A,B»; см. StrictEnumConverter.
        o.Converters.Add(new StrictEnumConverter());
        return o;
    }

    public static byte[] Serialize(AgentRequest request) => JsonSerializer.SerializeToUtf8Bytes(request, Options);

    public static byte[] Serialize(AgentResponse response) => JsonSerializer.SerializeToUtf8Bytes(response, Options);

    /// <exception cref="JsonException">Неизвестный тип, лишние поля, неверный формат.</exception>
    public static AgentRequest DeserializeRequest(ReadOnlySpan<byte> utf8)
    {
        try { return JsonSerializer.Deserialize<AgentRequest>(utf8, Options) ?? throw new JsonException("Пустой запрос"); }
        catch (NotSupportedException e) { throw new JsonException("Нет типа запроса", e); } // нет дискриминатора
    }

    public static AgentResponse DeserializeResponse(ReadOnlySpan<byte> utf8)
        => JsonSerializer.Deserialize<AgentResponse>(utf8, Options) ?? throw new JsonException("Пустой ответ");
}

/// <summary>Кадр: 4 байта длины (little-endian) и JSON. Пустые и слишком большие кадры отвергаются.</summary>
public static class FrameCodec
{
    public const int MaxFrameBytes = 1 << 20;

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length == 0 || payload.Length > MaxFrameBytes)
            throw new InvalidDataException("Недопустимый размер кадра");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>null — собеседник закрыл поток между кадрами. Обрыв внутри кадра это ошибка.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken ct = default, int maxBytes = MaxFrameBytes)
    {
        var header = new byte[4];
        var first = await ReadFullyAsync(stream, header, ct).ConfigureAwait(false);
        if (first == 0) return null;
        if (first != 4) throw new InvalidDataException("Обрыв внутри заголовка кадра");

        var len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len <= 0 || len > Math.Min(maxBytes, MaxFrameBytes)) throw new InvalidDataException("Недопустимый размер кадра");

        var buf = new byte[len];
        var got = await ReadFullyAsync(stream, buf, ct).ConfigureAwait(false);
        if (got != len) throw new InvalidDataException("Обрыв внутри кадра");
        return buf;
    }

    static async Task<int> ReadFullyAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }
}

/// <summary>
/// Белый список операций агента. Ping отвечает сам; остальные типы после Validate передаются в <see cref="IOperationHandler"/>.
/// Без обработчика все операции с правами получают unknown-operation.
/// </summary>
public sealed class RequestDispatcher(IOperationHandler? handler = null)
{
    public async Task<AgentResponse> HandleAsync(ReadOnlyMemory<byte> frame, ClientContext? client = null, CancellationToken ct = default)
    {
        AgentRequest request;
        try { request = AgentJson.DeserializeRequest(frame.Span); }
        catch (JsonException) { return new AgentResponse(Guid.Empty, false, ResponseCodes.BadRequest, "Запрос не распознан"); }

        if (request.Id == Guid.Empty)
            return new AgentResponse(Guid.Empty, false, ResponseCodes.BadRequest, "Пустой Id запроса");

        var problem = request.Validate();
        if (problem is not null)
            return new AgentResponse(request.Id, false, ResponseCodes.BadRequest, problem);

        if (request is PingRequest) return new AgentResponse(request.Id, true, ResponseCodes.Pong);

        if (handler is null)
            return new AgentResponse(request.Id, false, ResponseCodes.UnknownOperation, "Операция не разрешена");
        if (client is null)
            return new AgentResponse(request.Id, false, ResponseCodes.Internal, "Нет данных о клиенте");

        try { return await handler.HandleAsync(request, client, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            // Подробности исключения клиенту не отдаём.
            return new AgentResponse(request.Id, false, ResponseCodes.Internal, "Внутренняя ошибка агента");
        }
    }
}

/// <summary>Кто мы ожидаем на другом конце канала.</summary>
public sealed record ExpectedClient(int Pid, string ImagePath, string ImageSha256, int SessionId, string UserSid);

/// <summary>Кто на самом деле подключился (собирает слой Native).</summary>
public sealed record ActualClient(int Pid, string? ImagePath, string? ImageSha256, int SessionId, string? UserSid);

/// <summary>Проверка клиента: PID, путь образа, SHA-256 образа, сеанс и SID пользователя должны совпасть. Любое пустое значение это отказ.</summary>
public static class ClientVerifier
{
    public static (bool Ok, string Reason) Verify(ExpectedClient expected, ActualClient actual)
    {
        if (actual.Pid != expected.Pid) return (false, "pid");
        if (actual.SessionId != expected.SessionId) return (false, "session");

        if (string.IsNullOrEmpty(actual.UserSid)
            || !string.Equals(actual.UserSid, expected.UserSid, StringComparison.OrdinalIgnoreCase))
            return (false, "user");

        if (string.IsNullOrEmpty(actual.ImagePath)
            || !Policy.PathNormalizer.TryNormalize(actual.ImagePath, out var a, out _)
            || !Policy.PathNormalizer.TryNormalize(expected.ImagePath, out var e, out _)
            || !string.Equals(a, e, StringComparison.OrdinalIgnoreCase))
            return (false, "image-path");

        if (!TryHex(actual.ImageSha256, out var ah) || !TryHex(expected.ImageSha256, out var eh)
            || !CryptographicOperations.FixedTimeEquals(ah, eh))
            return (false, "image-hash");

        return (true, "ok");
    }

    public static string Sha256OfFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    static bool TryHex(string? s, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(s) || s.Length != 64) return false;
        try { bytes = Convert.FromHexString(s); return true; }
        catch (FormatException) { return false; }
    }
}
