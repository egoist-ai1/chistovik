using System.Windows.Input;
using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Services.Scanners;
using Chistovik.App.ViewModels;
using Chistovik.App.Views;
using Chistovik.Core.Findings;
using Chistovik.Core.Ipc;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;
using Xunit;

namespace App.Tests;

/// <summary>Пробел и Enter на строке списка переключают отметку (Чистка, Программы), фильтры «Чистки» работают и через выпадающие списки.</summary>
public class KeyboardToggleTests
{
    sealed class Fixed(params Finding[] findings) : IFindingScanner
    {
        public FindingArea Area => FindingArea.Disk;
        public Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Finding>>(findings);
    }

    static Finding F(string id, FindingArea area, string title, VerdictKind kind, ChangeClass cls = ChangeClass.Modifiable)
        => FindingFactory.Make(id, area, title, "Около 50 МБ, кэш.", kind, [ReasonCodes.Make(ReasonCodes.SizeNeedsAgent)], Risk.Low,
            Reversibility.Irreversible, Confidence.L1, new Explanation("a", "b", "c", "d"), cls);

    [Fact]
    public async Task Space_and_Enter_toggle_a_cleanup_row_that_can_be_selected()
    {
        var vm = new CleanupViewModel(new CleanupScanner([new Fixed(
            F("cleanup:temp-user", FindingArea.Disk, "Временные файлы", VerdictKind.Optional),
            F("cleanup:minidump", FindingArea.Disk, "Мини-дампы", VerdictKind.Protected, ChangeClass.ReadOnly))]));
        await vm.Loaded;
        var row = vm.Items.Single(r => r.Finding.Id == "cleanup:temp-user");
        var locked = vm.Items.Single(r => r.Finding.Id == "cleanup:minidump");

        Assert.True(RowKeyToggle.TryToggle(Key.Space, row));
        Assert.True(row.IsChecked);
        Assert.True(RowKeyToggle.TryToggle(Key.Enter, row));
        Assert.False(row.IsChecked);

        RowKeyToggle.TryToggle(Key.Space, locked);
        Assert.False(locked.IsChecked);
    }

    [Fact]
    public void Space_toggles_a_program_row_and_other_keys_are_ignored()
    {
        var count = 0;
        var row = new ProgramRowViewModel(new ProgramInfo("Prog", "Pub", "1.0", 1024, null, "Реестр"), () => count++);

        Assert.True(RowKeyToggle.TryToggle(Key.Space, row));
        Assert.True(row.IsChecked);
        Assert.Equal(1, count);
        Assert.False(RowKeyToggle.TryToggle(Key.A, row));
        Assert.True(row.IsChecked);
        Assert.False(RowKeyToggle.TryToggle(Key.Space, new object()));
    }

    [Fact]
    public async Task Combo_box_selection_filters_the_same_way_as_the_chip_commands()
    {
        var vm = new CleanupViewModel(new CleanupScanner([new Fixed(
            F("cleanup:temp-user", FindingArea.Disk, "Временные файлы", VerdictKind.Optional),
            F("regkey:hklm:-:x64:a", FindingArea.Registry, "Запись А", VerdictKind.Remove))]));
        await vm.Loaded;
        Assert.Equal("all", vm.SelectedArea!.Key);

        vm.SelectedArea = vm.AreaChips.Single(c => c.Key == "Registry");
        Assert.All(vm.Items, r => Assert.Equal("Реестр", r.AreaText));
        Assert.True(vm.AreaChips.Single(c => c.Key == "Registry").IsActive);

        vm.VerdictCommand.Execute("Optional");
        Assert.Equal("Optional", vm.SelectedVerdict!.Key);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Conflict_cause_does_not_show_the_operation_id()
    {
        var id = Guid.NewGuid();
        var row = new ConflictRowViewModel(new ConflictInfo(1, StepKind.RegistryValue, "x", id));
        Assert.DoesNotContain(id.ToString("D")[..8], row.Cause);
        Assert.Contains(id.ToString("D"), row.CauseDetail);
    }
}
