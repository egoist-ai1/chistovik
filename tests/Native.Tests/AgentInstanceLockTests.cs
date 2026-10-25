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

        // Владелец мьютекса зависит от токена раннера, поэтому «доверенность» владельца задаётся явно.
        var second = AgentInstanceLock.TryAcquire(name, AllowCurrentUser(), _ => true, out var o2, out _);

        Assert.Null(second);
        Assert.Equal(InstanceLockOutcome.Busy, o2);
    }

    [Fact]
    public void Existing_mutex_created_by_a_non_admin_owner_is_a_foreign_name_not_busy()
    {
        var name = UniqueName();
        using var first = AgentInstanceLock.TryAcquire(name, AllowCurrentUser(), out _);
        Assert.NotNull(first);
        SecurityIdentifier? seen = null;

        var second = AgentInstanceLock.TryAcquire(name, AllowCurrentUser(), owner => { seen = owner; return false; }, out var outcome, out _);

        Assert.Null(second);
        Assert.NotNull(seen); // владелец действительно прочитан из дескриптора
        Assert.Equal(InstanceLockOutcome.NameTaken, outcome);
    }

    [Fact]
    public void Only_administrators_and_system_are_trusted_owners()
    {
        Assert.True(AgentInstanceLock.IsTrustedOwner(new SecurityIdentifier("S-1-5-32-544")));
        Assert.True(AgentInstanceLock.IsTrustedOwner(new SecurityIdentifier("S-1-5-18")));
        Assert.False(AgentInstanceLock.IsTrustedOwner(new SecurityIdentifier("S-1-1-0")));           // Everyone
        Assert.False(AgentInstanceLock.IsTrustedOwner(new SecurityIdentifier("S-1-5-21-1-2-3-1001"))); // обычный пользователь
        Assert.False(AgentInstanceLock.IsTrustedOwner(new SecurityIdentifier("S-1-5-32-545")));        // Users
    }

    [Fact]
    public void Our_own_repeated_acquire_with_default_check_is_busy_only_when_the_owner_is_trusted()
    {
        var name = UniqueName();
        using var first = AgentInstanceLock.TryAcquire(name, AllowCurrentUser(), out _);
        Assert.NotNull(first);
        var owner = (SecurityIdentifier?)null;
        AgentInstanceLock.TryAcquire(name, AllowCurrentUser(), o => { owner = o; return true; }, out _, out _);

        // Полный путь без шва: исход определяется тем же правилом, что и IsTrustedOwner.
        var real = AgentInstanceLock.TryAcquire(name, AllowCurrentUser(), null, out var outcome, out _);

        Assert.Null(real);
        Assert.Equal(AgentInstanceLock.IsTrustedOwner(owner!) ? InstanceLockOutcome.Busy : InstanceLockOutcome.NameTaken, outcome);
    }

    [Fact]
    public void Lock_error_detail_is_only_the_exception_type_name()
    {
        var ours = AgentInstanceLock.TryAcquire(@"Local\no\such\namespace", out var outcome, out var detail);

        Assert.Null(ours);
        Assert.Equal(InstanceLockOutcome.Error, outcome);
        Assert.False(string.IsNullOrEmpty(detail));
        Assert.DoesNotContain('\\', detail!);
        Assert.DoesNotContain(' ', detail!);
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
