using Chistovik.Core.Catalog;
using Chistovik.Core.Ipc;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Chistovik.TestKit;
using Xunit;

namespace Chistovik.Core.Tests.Execution;

/// <summary>Параметра питания нет в схеме (ERROR_FILE_NOT_FOUND в Native): ответ not-found, а не внутренняя ошибка.</summary>
public class PowerMissingSettingTests
{
    [Fact]
    public void Missing_power_setting_gives_not_found_and_changes_nothing()
    {
        var t = new TestEnv();
        var entry = PowerCatalog.Shared.FindSetting("min")!;
        t.Power.MissingSettings.Add((entry.Subgroup, entry.Setting));

        var r = t.PowerSetting("min", 50, null);

        Assert.False(r.Ok);
        Assert.Equal(ResponseCodes.NotFound, r.Code);
        Assert.Equal(0, t.Power.WriteCount);
        var op = t.Journal.Get(r.OperationId!.Value)!;
        Assert.Equal(OperationState.Denied, op.Summary.State);
    }
}
