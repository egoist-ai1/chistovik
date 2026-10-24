using System.Text;
using Chistovik.Core.Ipc;
using Xunit;

namespace Chistovik.CoreTests;

public class FrameCodecTests
{
    [Fact]
    public async Task Roundtrip()
    {
        var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, "hello"u8.ToArray());
        await FrameCodec.WriteAsync(ms, "world"u8.ToArray());
        ms.Position = 0;
        Assert.Equal("hello", Encoding.UTF8.GetString((await FrameCodec.ReadAsync(ms))!));
        Assert.Equal("world", Encoding.UTF8.GetString((await FrameCodec.ReadAsync(ms))!));
        Assert.Null(await FrameCodec.ReadAsync(ms));
    }

    [Fact]
    public async Task Oversized_and_empty_frames_are_rejected()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.WriteAsync(new MemoryStream(), ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<InvalidDataException>(
            () => FrameCodec.WriteAsync(new MemoryStream(), new byte[FrameCodec.MaxFrameBytes + 1]));

        // Заголовок заявляет 2 ГиБ: читаем только 4 байта и отказываем, память не выделяется.
        var hostile = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]);
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(hostile));

        var negative = new MemoryStream([0xFF, 0xFF, 0xFF, 0xFF]);
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(negative));

        var zero = new MemoryStream([0, 0, 0, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(zero));
    }

    [Fact]
    public async Task Truncated_frame_is_an_error()
    {
        var ms = new MemoryStream();
        await FrameCodec.WriteAsync(ms, "abcdef"u8.ToArray());
        var cut = ms.ToArray()[..^2];
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(new MemoryStream(cut)));
        await Assert.ThrowsAsync<InvalidDataException>(() => FrameCodec.ReadAsync(new MemoryStream([1, 0])));
    }
}

public class DispatcherTests
{
    static readonly RequestDispatcher D = new();

    [Fact]
    public async Task Ping_is_answered()
    {
        var id = Guid.NewGuid();
        var r = await D.HandleAsync(AgentJson.Serialize(new PingRequest { Id = id }));
        Assert.True(r.Ok);
        Assert.Equal("pong", r.Code);
        Assert.Equal(id, r.Id);
    }

    [Theory]
    [InlineData("""{"type":"DeleteFile","id":"00000000-0000-0000-0000-000000000001","path":"C:\\Windows"}""")]
    [InlineData("""{"type":"MoveToQuarantine","id":"00000000-0000-0000-0000-000000000001","path":"C:\\x"}""")]
    [InlineData("""{"id":"00000000-0000-0000-0000-000000000001"}""")]
    [InlineData("""{"type":"Ping","id":"00000000-0000-0000-0000-000000000001","extra":1}""")]
    [InlineData("""{"type":"Ping"}""")]
    [InlineData("""{"type":"Ping","id":"not-a-guid"}""")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("не json")]
    public async Task Unknown_or_malformed_requests_are_rejected(string json)
    {
        var r = await D.HandleAsync(Encoding.UTF8.GetBytes(json));
        Assert.False(r.Ok);
        Assert.Equal("bad-request", r.Code);
    }

    [Fact]
    public void Request_type_set_is_closed()
    {
        // Любой новый тип запроса должен быть добавлен осознанно вместе с этим тестом, обработчиком и проверкой политики.
        var types = typeof(AgentRequest).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(AgentRequest))).Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(["PingRequest"], types);
    }
}

public class ClientVerifierTests
{
    static readonly string Hash = new('A', 64);
    static readonly ExpectedClient Expected = new(1234, @"C:\Program Files\Chistovik\Chistovik.App.exe", Hash, 1, "S-1-5-21-1-2-3-1001");

    static ActualClient Good() => new(1234, @"C:\Program Files\Chistovik\Chistovik.App.exe", Hash, 1, "S-1-5-21-1-2-3-1001");

    [Fact]
    public void Matching_client_is_accepted()
        => Assert.True(ClientVerifier.Verify(Expected, Good()).Ok);

    [Fact]
    public void Path_comparison_ignores_case_and_prefix()
    {
        Assert.True(ClientVerifier.Verify(Expected, Good() with { ImagePath = @"\\?\c:\program files\chistovik\CHISTOVIK.APP.EXE" }).Ok);
        Assert.True(ClientVerifier.Verify(Expected, Good() with { ImageSha256 = Hash.ToLowerInvariant() }).Ok);
    }

    [Theory]
    [InlineData("pid")]
    [InlineData("session")]
    [InlineData("user")]
    [InlineData("image-path")]
    [InlineData("image-hash")]
    public void Any_mismatch_is_rejected(string what)
    {
        var a = what switch
        {
            "pid" => Good() with { Pid = 999 },
            "session" => Good() with { SessionId = 2 },
            "user" => Good() with { UserSid = "S-1-5-21-1-2-3-1002" },
            "image-path" => Good() with { ImagePath = @"C:\Users\Egoist\AppData\Local\Temp\evil.exe" },
            _ => Good() with { ImageSha256 = new string('B', 64) },
        };
        var (ok, reason) = ClientVerifier.Verify(Expected, a);
        Assert.False(ok);
        Assert.Equal(what, reason);
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("", "x")]
    [InlineData("relative.exe", "x")]
    [InlineData(@"C:\Program Files\Chistovik\Chistovik.App.exe", null)]
    [InlineData(@"C:\Program Files\Chistovik\Chistovik.App.exe", "")]
    [InlineData(@"C:\Program Files\Chistovik\Chistovik.App.exe", "ZZ")]
    [InlineData(@"C:\Program Files\Chistovik\Chistovik.App.exe", "AA")]
    public void Missing_or_malformed_values_are_rejected(string? path, string? hash)
    {
        var a = Good() with { ImagePath = path, ImageSha256 = hash ?? (path is null ? Hash : null) };
        if (hash == "x") a = a with { ImageSha256 = Hash };
        Assert.False(ClientVerifier.Verify(Expected, a).Ok);
    }

    [Fact]
    public void Missing_user_sid_is_rejected()
        => Assert.False(ClientVerifier.Verify(Expected, Good() with { UserSid = null }).Ok);
}
