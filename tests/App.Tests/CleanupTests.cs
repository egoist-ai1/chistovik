using Chistovik.App.Mocks;
using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;
using Chistovik.Core.Findings;
using Chistovik.Core.Ports;
using Xunit;
using ChangeClass = Chistovik.Core.Policy.ChangeClass;

namespace App.Tests;

sealed class ProgramsSrc(params ProgramInfo[] items) : IProgramsSource
{
    public Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync() => Task.FromResult<IReadOnlyList<ProgramInfo>>(items);
}

sealed class NavRecorder : INavigator
{
    public List<string> Opened { get; } = new();
    public void Open(string title) => Opened.Add(title);
}

public class CleanupScannerTests
{
    static CleanupScanner Demo() => new(new MockProgramsSource(), new MockStartupSource(), new MockServiceSource(), new MockTaskSource());

    [Fact]
    public async Task Every_finding_has_four_phrases_and_a_reason()
    {
        var findings = await Demo().ScanAsync();

        Assert.NotEmpty(findings);
        foreach (var f in findings)
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Explanation.WhatIsIt), f.Title);
            Assert.False(string.IsNullOrWhiteSpace(f.Explanation.WhyNeeded), f.Title);
            Assert.False(string.IsNullOrWhiteSpace(f.Explanation.IfRemoved), f.Title);
            Assert.False(string.IsNullOrWhiteSpace(f.Explanation.HowToUndo), f.Title);
            Assert.NotEmpty(f.Verdict.Reasons);
        }
    }

    [Fact]
    public async Task Demo_data_shows_every_kind_of_advice()
    {
        var kinds = (await Demo().ScanAsync()).Select(f => f.Verdict.Kind).ToHashSet();

        foreach (var kind in Enum.GetValues<VerdictKind>())
            Assert.Contains(kind, kinds);
    }

    [Fact]
    public async Task Blocked_objects_are_protected_and_keep_their_class()
    {
        var findings = await Demo().ScanAsync();

        var blocked = findings.Where(f => f.Area == FindingArea.Services && f.Class == ChangeClass.Forbidden).ToList();
        Assert.NotEmpty(blocked);
        Assert.All(blocked, f => Assert.Equal(VerdictKind.Protected, f.Verdict.Kind));
    }

    [Fact]
    public async Task Service_outside_the_catalog_is_never_advised_for_removal()
    {
        var scanner = new CleanupScanner(new ProgramsSrc(), new StartupSrc(),
            new ServiceSrc(new ServiceInfo("SomeVendorSvc", "Служба неизвестного поставщика", StartMode.Automatic, Changeability.Modifiable, "",
                @"C:\Program Files\Vendor\svc.exe")),
            new TaskSrc());

        var finding = Assert.Single(await scanner.ScanAsync());

        Assert.NotEqual(VerdictKind.Remove, finding.Verdict.Kind);
    }

    [Fact]
    public void Changeability_maps_one_to_one_without_widening()
    {
        Assert.Equal(ChangeClass.Modifiable, CleanupScanner.Map(Changeability.Modifiable));
        Assert.Equal(ChangeClass.ReadOnly, CleanupScanner.Map(Changeability.ReadOnly));
        Assert.Equal(ChangeClass.Forbidden, CleanupScanner.Map(Changeability.Forbidden));
    }
}

public class CleanupViewModelTests
{
    static CleanupViewModel Demo(INavigator? nav = null)
        => new(new CleanupScanner(new MockProgramsSource(), new MockStartupSource(), new MockServiceSource(), new MockTaskSource()), nav);

    [Fact]
    public async Task Loads_and_lists_removable_items_first_with_nothing_chosen_in_advance()
    {
        var vm = Demo();
        await vm.Loaded;

        Assert.Equal(PageState.Ready, vm.State);
        Assert.NotEmpty(vm.Items);
        Assert.Equal(VerdictKind.Remove, vm.Items[0].Kind);
        Assert.True(vm.AreaChips[0].IsActive);
        Assert.True(vm.VerdictChips[0].IsActive);
    }

    [Fact]
    public async Task Area_and_advice_filters_narrow_the_list_and_mark_the_active_chip()
    {
        var vm = Demo();
        await vm.Loaded;

        vm.AreaCommand.Execute("Services");
        Assert.All(vm.Items, i => Assert.Equal(FindingArea.Services, i.Finding.Area));
        Assert.True(vm.AreaChips.Single(c => c.Key == "Services").IsActive);
        Assert.False(vm.AreaChips[0].IsActive);
        Assert.Contains("выбрано", vm.AreaChips.Single(c => c.Key == "Services").AutomationName);

        vm.VerdictCommand.Execute("Protected");
        Assert.All(vm.Items, i => Assert.Equal(VerdictKind.Protected, i.Kind));
        Assert.NotEmpty(vm.Items);
    }

    [Fact]
    public async Task Search_matches_title_and_reports_when_nothing_fits()
    {
        var vm = Demo();
        await vm.Loaded;

        vm.Search = "DiagTrack";
        Assert.NotEmpty(vm.Items);
        Assert.False(vm.NothingMatches);

        vm.Search = "такого-названия-нет";
        Assert.Empty(vm.Items);
        Assert.True(vm.NothingMatches);
        Assert.Null(vm.Selected);
        Assert.True(vm.NoSelection);
    }

    [Fact]
    public async Task First_row_is_selected_only_to_show_details()
    {
        var vm = Demo();
        await vm.Loaded;

        Assert.Same(vm.Items[0], vm.Selected);
        Assert.True(vm.HasSelection);
        Assert.False(string.IsNullOrWhiteSpace(vm.Selected!.WhatIsIt));
        Assert.False(string.IsNullOrWhiteSpace(vm.Selected.HowToUndo));
        Assert.NotEmpty(vm.Selected.Reasons);
    }

    [Fact]
    public async Task Open_button_goes_to_the_section_that_owns_the_object()
    {
        var nav = new NavRecorder();
        var vm = Demo(nav);
        await vm.Loaded;
        vm.AreaCommand.Execute("Startup");
        var row = vm.Items.First(i => i.CanOpen);

        row.OpenCommand.Execute(null);

        Assert.Equal(new[] { "Автозагрузка" }, nav.Opened);
    }

    [Fact]
    public async Task Empty_sources_give_the_empty_state()
    {
        var vm = new CleanupViewModel(new CleanupScanner(new ProgramsSrc(), new StartupSrc(), new ServiceSrc(), new TaskSrc()));
        await vm.Loaded;

        Assert.Equal(PageState.Empty, vm.State);
        Assert.Empty(vm.Items);
    }
}
