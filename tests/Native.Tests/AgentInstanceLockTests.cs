using Chistovik.Native.Ipc;
using Xunit;

namespace Chistovik.NativeTests;

/// <summary>Один агент на компьютер: второй захват того же имени не удаётся, после освобождения снова возможен.</summary>
public class AgentInstanceLockTests
{
    static string UniqueName() => @"Local\Chistovik.Agent.Test." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Second_acquire_of_the_same_name_is_refused_until_the_first_is_released()
    {
        var name = UniqueName();

        using (var first = AgentInstanceLock.TryAcquire(name))
        {
            Assert.NotNull(first);
            Assert.Null(AgentInstanceLock.TryAcquire(name));
        }

        using var again = AgentInstanceLock.TryAcquire(name);
        Assert.NotNull(again);
    }

    [Fact]
    public void Different_names_do_not_conflict()
    {
        using var a = AgentInstanceLock.TryAcquire(UniqueName());
        using var b = AgentInstanceLock.TryAcquire(UniqueName());

        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void Machine_wide_name_is_global_and_not_per_user()
    {
        Assert.Equal(@"Global\Chistovik.Agent", AgentInstanceLock.MachineName);
    }

    [Fact]
    public void Already_running_exit_code_is_three()
    {
        Assert.Equal(3, AgentExitCodes.AlreadyRunning);
    }
}
