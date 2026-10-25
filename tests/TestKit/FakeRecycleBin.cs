using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>Пустышка T0 (расширяется в T3).</summary>
public sealed class FakeRecycleBin : IRecycleBin
{
    public string TokenSid { get; set; } = TestEnv.Sid;
    public long Bytes { get; set; }
    public int EmptyCalls { get; private set; }

    public long QueryBytes() => Bytes;

    public void Empty()
    {
        EmptyCalls++;
        Bytes = 0;
    }

    public string CurrentTokenSid() => TokenSid;
}
