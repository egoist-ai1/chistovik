using System.Security.AccessControl;
using System.Security.Principal;
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

    // Токен раннера не важен: DACL задаётся явно, поэтому причина отказа определяется однозначно.
    static MutexSecurity AllowCurrentUser()
    {
        var s = new MutexSecurity();
        s.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User!, MutexRights.FullControl, AccessControlType.Allow));
        return s;
    }

    static MutexSecurity AllowSystemOnly()
    {
        var s = new MutexSecurity();
        s.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            MutexRights.FullControl, AccessControlType.Allow));
        return s;
    }

    [Fact]
    public void Existing_mutex_we_can_open_is_reported_as_busy_not_as_a_foreign_name()
    {
        var name = UniqueName();
        using var first = AgentInstanceLock.TryAcquire(name, AllowCurrentUser(), out var o1);
        Assert.NotNull(first);
        Assert.Equal(InstanceLockOutcome.Acquired, o1);

        var second = AgentInstanceLock.TryAcquire(name, AllowCurrentUser(), out var o2);

        Assert.Null(second);
        Assert.Equal(InstanceLockOutcome.Busy, o2);
    }

    [Fact]
    public void Existing_mutex_with_a_foreign_dacl_is_reported_as_name_taken()
    {
        var name = UniqueName();
        using var foreign = AgentInstanceLock.TryAcquire(name, AllowSystemOnly(), out _); // создатель держит дескриптор, но открыть имя заново нельзя
        Assert.NotNull(foreign);

        var ours = AgentInstanceLock.TryAcquire(name, AllowCurrentUser(), out var outcome);

        Assert.Null(ours);
        Assert.Equal(InstanceLockOutcome.NameTaken, outcome);
    }

    [Fact]
    public void Unusable_name_is_an_error_not_a_crash()
    {
        var ours = AgentInstanceLock.TryAcquire(@"Local\no\such\namespace", out var outcome);

        Assert.Null(ours);
        Assert.Equal(InstanceLockOutcome.Error, outcome);
    }

    [Fact]
    public void Exit_codes_for_name_taken_and_lock_error_are_distinct_from_the_others()
    {
        var codes = new[] { AgentExitCodes.Ok, AgentExitCodes.BadArgs, AgentExitCodes.AlreadyRunning, AgentExitCodes.Idle,
            AgentExitCodes.BadEnvironment, AgentExitCodes.InstanceNameTaken, AgentExitCodes.InstanceLockError };
        Assert.Equal(codes.Length, codes.Distinct().Count());
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
