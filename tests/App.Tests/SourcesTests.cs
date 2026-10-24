using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Services.Changes;
using Chistovik.App.Services.Windows;
using Chistovik.Core.Catalog;
using Chistovik.Core.Journal;
using Chistovik.Core.Ports;
using Xunit;

namespace App.Tests;

public class SourcesTests
{
    const string TaskXml = """
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Principals><Principal id="A"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings><Enabled>false</Enabled></Settings>
          <Actions><Exec><Command>C:\tools\upd.exe</Command><Arguments>/silent</Arguments></Exec></Actions>
        </Task>
        """;

    [Fact]
    public void Startup_approved_first_odd_byte_means_disabled()
    {
        var approved = new Dictionary<string, object?>
        {
            ["Off"] = new byte[] { 3, 0, 0, 0 },
            ["On"] = new byte[] { 2, 0, 0, 0 },
        };

        Assert.False(RegistryStartupSource.IsEnabled(approved, "Off"));
        Assert.True(RegistryStartupSource.IsEnabled(approved, "On"));
        Assert.True(RegistryStartupSource.IsEnabled(approved, "Missing"));
        Assert.True(RegistryStartupSource.IsEnabled(null, "Any"));
    }

    [Fact]
    public void Tweak_state_missing_value_is_not_reported_as_on()
    {
        var tweak = TweakCatalog.Shared.Tweaks.First();

        var state = RegistryTweakStateSource.Evaluate(tweak, new Dictionary<string, object?>());

        Assert.NotEqual(TweakState.On, state);
    }

    [Fact]
    public void Tweak_state_dword_matches_on_value()
    {
        var tweak = TweakCatalog.Shared.Tweaks.FirstOrDefault(t => t.ValueType == "DWord" && t.OnValue.TryGetUInt32(out _));
        if (tweak is null) return;
        tweak.OnValue.TryGetUInt32(out var on);

        var state = RegistryTweakStateSource.Evaluate(tweak, new Dictionary<string, object?> { [tweak.Value] = unchecked((int)on) });

        Assert.Equal(TweakState.On, state);
    }

    [Fact]
    public void Task_xml_is_parsed_into_task_info()
    {
        var info = FolderTaskSource.Parse(@"\Vendor\Updater", TaskXml, new ConservativeTaskClassifier());

        Assert.NotNull(info);
        Assert.Equal("Updater", info!.Name);
        Assert.False(info.Enabled);
        Assert.Contains("upd.exe", info.Summary);
        Assert.Contains("/silent", info.Summary);
    }

    [Fact]
    public void Broken_task_xml_is_skipped()
    {
        Assert.Null(FolderTaskSource.Parse(@"\X", "<не xml", new ConservativeTaskClassifier()));
    }

    [Fact]
    public void Microsoft_tasks_are_never_modifiable()
    {
        var info = FolderTaskSource.Parse(@"\Microsoft\Windows\Defrag\ScheduledDefrag", TaskXml, new ConservativeTaskClassifier());

        Assert.NotNull(info);
        Assert.Equal(Changeability.Forbidden, info!.Class);
    }

    [Theory]
    [InlineData(OperationState.Done, true)]
    [InlineData(OperationState.Partial, true)]
    [InlineData(OperationState.Undone, false)]
    public void Journal_operation_reversibility_follows_state(OperationState state, bool reversible)
    {
        var s = new OperationSummary(Guid.NewGuid(), OperationKind.Tweaks, "x", state, null, DateTimeOffset.UtcNow, null);

        Assert.Equal(reversible, JournalSource.ToOperation(s).Reversible);
    }

    [Fact]
    public void Undo_operation_is_not_reversible()
    {
        var s = new OperationSummary(Guid.NewGuid(), OperationKind.Undo, "x", OperationState.Done, null, DateTimeOffset.UtcNow, null);

        Assert.False(JournalSource.ToOperation(s).Reversible);
    }

    [Fact]
    public async Task Missing_journal_file_gives_empty_list_not_error()
    {
        var source = new JournalSource(() => throw new JournalException(JournalException.NotFound, "нет базы"));

        var ops = await source.GetOperationsAsync();
        var steps = await source.GetStepsAsync(Guid.NewGuid());

        Assert.Empty(ops);
        Assert.Empty(steps);
    }

    [Fact]
    public async Task Null_reader_gives_empty_journal()
    {
        var source = new JournalSource(() => new NullJournalReader());

        Assert.Empty(await source.GetOperationsAsync());
    }
}
