using System.Text.RegularExpressions;
using Chistovik.App.Snapshots;
using Xunit;

namespace App.Tests;

/// <summary>Перечень снимков экранов и имена файлов. Окна не создаются и не запускаются.</summary>
public class SnapshotPlanTests
{
    static readonly string[] AllScreens =
        { "overview", "programs", "startup", "services", "tasks", "tweaks", "modes", "power", "journal" };

    [Fact]
    public void Every_screen_has_ready_snapshot_in_all_sizes_and_scales()
    {
        var plan = SnapshotPlan.Build();
        foreach (var screen in AllScreens)
            foreach (var (w, h) in new[] { (1280, 800), (1920, 1080) })
                foreach (var dpi in new[] { 96, 192 })
                    Assert.Contains(plan, s => s.Screen == screen && s.State == SnapshotState.Ready && s.Width == w && s.Height == h && s.Dpi == dpi);
    }

    [Fact]
    public void File_names_are_unique_png_without_spaces_or_cyrillic()
    {
        var names = SnapshotPlan.Build().Select(s => s.FileName).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.Matches(new Regex("^[a-z]+(-[a-z]+)*_(\\d+x\\d+|auto)_(96|120|168|192)\\.png$"), n));
    }

    [Fact]
    public void File_name_format_matches_screen_state_size_dpi()
    {
        Assert.Equal("startup_1280x800_96.png", new SnapshotSpec("startup", SnapshotState.Ready, 1280, 800, 96).FileName);
        Assert.Equal("services-empty_1920x1080_192.png", new SnapshotSpec("services", SnapshotState.Empty, 1920, 1080, 192).FileName);
        Assert.Equal("dialog-conflicts_auto_192.png", new SnapshotSpec("dialog-conflicts", SnapshotState.Ready, 0, 0, 192).FileName);
    }

    [Fact]
    public void List_screens_have_empty_loading_and_error_examples()
    {
        var plan = SnapshotPlan.Build();
        foreach (var screen in new[] { "programs", "startup", "services", "tasks", "journal" })
            foreach (var state in new[] { SnapshotState.Empty, SnapshotState.Loading, SnapshotState.Error })
                Assert.Contains(plan, s => s.Screen == screen && s.State == state);
    }

    [Fact]
    public void Screens_that_change_the_system_have_view_only_and_partial_examples()
    {
        var plan = SnapshotPlan.Build();
        foreach (var screen in new[] { "startup", "services", "tasks", "tweaks", "modes", "power", "journal" })
        {
            Assert.Contains(plan, s => s.Screen == screen && s.State == SnapshotState.ViewOnly);
            Assert.Contains(plan, s => s.Screen == screen && s.State == SnapshotState.Partial);
        }
    }

    [Fact]
    public void Dialogs_are_rendered_at_both_scales()
    {
        var plan = SnapshotPlan.Build();
        foreach (var dialog in new[] { "dialog-confirm", "dialog-confirm-danger", "dialog-conflicts" })
            foreach (var dpi in new[] { 96, 192 })
                Assert.Contains(plan, s => s.Screen == dialog && s.IsDialog && s.Dpi == dpi);
    }

    [Fact]
    public void Screen_nav_titles_exist_in_main_navigation()
    {
        var titles = new[] { "Обзор", "Чистка", "Программы", "Автозагрузка", "Службы", "Задачи", "Твики", "Режимы", "Питание", "Журнал и откат" };
        Assert.Equal(titles, SnapshotPlan.Screens.Select(s => s.NavTitle).ToArray());
    }

    [Fact]
    public void Args_are_parsed()
    {
        Assert.True(SnapshotPlan.TryParseArgs(new[] { "--demo" }, out var requested, out _, out _));
        Assert.False(requested);

        Assert.True(SnapshotPlan.TryParseArgs(new[] { "--snapshots", @"C:\tmp\shots" }, out requested, out var dir, out _));
        Assert.True(requested);
        Assert.Equal(@"C:\tmp\shots", dir);

        Assert.False(SnapshotPlan.TryParseArgs(new[] { "--snapshots" }, out requested, out _, out var error));
        Assert.True(requested);
        Assert.NotNull(error);
        Assert.False(SnapshotPlan.TryParseArgs(new[] { "--snapshots", "--demo" }, out _, out _, out _));
    }
}
