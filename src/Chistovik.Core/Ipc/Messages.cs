using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chistovik.Core.Ipc;

/// <summary>
/// Закрытое множество запросов к агенту: тип определяется атрибутами JsonDerivedType, всё остальное не десериализуется.
/// Операции с правами добавляются сюда по одной, каждая вместе с обработчиком, проверкой политики и тестами.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PingRequest), "Ping")]
public abstract record AgentRequest
{
    public required Guid Id { get; init; }
}

public sealed record PingRequest : AgentRequest;

public sealed record AgentResponse(Guid Id, bool Ok, string Code, string? Message = null);

public static class AgentJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
    };

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

/// <summary>Белый список операций агента. Сейчас единственная операция — Ping: привилегированных действий в агенте нет.</summary>
public sealed class RequestDispatcher
{
    public Task<AgentResponse> HandleAsync(ReadOnlyMemory<byte> frame)
    {
        AgentRequest request;
        try { request = AgentJson.DeserializeRequest(frame.Span); }
        catch (JsonException) { return Task.FromResult(new AgentResponse(Guid.Empty, false, "bad-request", "Запрос не распознан")); }

        return Task.FromResult(request switch
        {
            PingRequest p => new AgentResponse(p.Id, true, "pong"),
            _ => new AgentResponse(request.Id, false, "unknown-operation", "Операция не разрешена"),
        });
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
