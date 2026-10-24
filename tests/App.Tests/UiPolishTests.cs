using System.IO;
using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.Snapshots;
using Chistovik.App.ViewModels;
using Chistovik.App.Views;
using Chistovik.Core.Ipc;
using Chistovik.Core.Ports;
using Xunit;

namespace App.Tests;

/// <summary>Мелкие части интерфейса без окон: настройки окна, разбор текста диалога, сводка результата, тексты причин.</summary>
public class UiPolishTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chistovik-ui-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // ----- ui.json -----

    [Fact]
    public void Missing_file_gives_defaults_with_welcome_not_seen()
    {
        var s = new FileUiSettingsStore(Path.Combine(_dir, "ui.json")).Load();
        Assert.False(s.WelcomeSeen);
        Assert.Null(s.Width);
    }

    [Fact]
    public void Settings_round_trip_and_survive_garbage()
    {
        var path = Path.Combine(_dir, "sub", "ui.json");
        var store = new FileUiSettingsStore(path);
        store.Save(new UiSettings { Left = 10, Top = 20, Width = 1000, Height = 700, LastSection = "Службы", WelcomeSeen = true });
        var back = store.Load();
        Assert.Equal(1000, back.Width);
        Assert.Equal("Службы", back.LastSection);
        Assert.True(back.WelcomeSeen);

        File.WriteAllText(path, "{ это не json");
        Assert.Null(store.Load().Width);
    }

    [Fact]
    public void Sanitize_drops_absurd_sizes()
    {
        var s = FileUiSettingsStore.Sanitize(new UiSettings { Width = 5, Height = 5, Left = double.NaN, Top = 1 });
        Assert.Null(s.Width);
        Assert.Null(s.Left);
        Assert.Null(s.Top);
    }

    [Fact]
    public void Unwritable_path_does_not_throw()
    {
        var blocker = Path.Combine(_dir, "file");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocker, "x");
        new FileUiSettingsStore(Path.Combine(blocker, "ui.json")).Save(new UiSettings());
    }

    // ----- диалог подтверждения -----

    [Fact]
    public void Confirm_text_splits_into_change_and_rollback()
    {
        var (what, rollback) = ConfirmDialog.Split("Что изменится: служба X перестанет запускаться.\nОткуда вернуть: журнал, кнопка «Откатить».");
        Assert.Equal("служба X перестанет запускаться.", what);
        Assert.Equal("журнал, кнопка «Откатить».", rollback);
    }

    [Fact]
    public void Confirm_text_without_markers_is_kept_whole()
    {
        var (what, rollback) = ConfirmDialog.Split("Просто текст.");
        Assert.Equal("Просто текст.", what);
        Assert.Equal("", rollback);
    }

    // ----- сводка результата -----

    [Fact]
    public void Summary_counts_applied_skipped_and_failed_with_reasons()
    {
        var steps = new[]
        {
            Step(StepState.Done, null),
            Step(StepState.Skipped, "no-change"),
            Step(StepState.Skipped, "external-change"),
            Step(StepState.Failed, "access-denied"),
        };
        var (counts, problems) = ResultSummary.Build(steps, applied: true);
        Assert.Equal("Применено: 2 · Пропущено: 1 · Не получилось: 1", counts);
        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, p => p.Contains("права администратора"));
    }

    [Fact]
    public void Undo_summary_says_returned()
    {
        var (counts, problems) = ResultSummary.Build(new[] { Step(StepState.Undone, null) }, applied: false);
        Assert.Equal("Возвращено: 1", counts);
        Assert.Empty(problems);
    }

    private static StepResult Step(StepState state, string? code)
        => new(1, 0, StepKind.RegistryValue, state, code);

    // ----- тексты причин -----

    private static readonly string[] AgentCodes =
    {
        "bad-name", "bad-path", "bad-request", "bad-state", "changed-after-preflight",
        "journal-acl-bad-ace", "journal-acl-broad-write", "journal-acl-foreign-write", "journal-acl-no-aces", "journal-acl-no-facts",
        "journal-acl-trusted", "journal-bad-payload", "journal-bad-state", "journal-bad-value-type", "journal-corrupt", "journal-dir-error",
        "journal-dir-missing", "journal-dir-owner", "journal-dir-reparse", "journal-newer-schema", "journal-no-state", "journal-not-found",
        "journal-target-key-mismatch", "journal-unavailable", "journal-unknown-kind", "journal-untrusted",
        "power-active", "power-bad-guid", "power-bad-name", "power-bad-template", "power-catalog-scheme", "power-catalog-setting",
        "power-create-own", "power-delete-active", "power-delete-builtin", "power-delete-foreign", "power-delete-own", "power-foreign-scheme",
        "power-out-of-range", "power-own-scheme", "power-scheme-mismatch", "power-scheme-name-mismatch", "power-scheme-not-active",
        "power-scheme-not-in-catalog", "power-scheme-not-installed", "power-setting-not-in-catalog", "power-unknown-setting",
        "reg-agent-no-hkcu", "reg-assoc", "reg-bad-client-sid", "reg-bad-key", "reg-bad-location", "reg-bad-value", "reg-bad-value-type",
        "reg-classes-code", "reg-classes-default-deny", "reg-classes-entry", "reg-classes-local-settings", "reg-classes-shell",
        "reg-classes-system", "reg-classes-write", "reg-client-hive", "reg-defender", "reg-environment", "reg-foreign-sid", "reg-foreign-user",
        "reg-hive-root", "reg-hkcc", "reg-hkcr", "reg-logon-script", "reg-microsoft", "reg-nonsoftware", "reg-open-failed", "reg-persistence",
        "reg-policies", "reg-policies-microsoft", "reg-policies-microsoft-exception", "reg-program-entry", "reg-protected-node",
        "reg-resolved-mismatch", "reg-run-keys", "reg-smartscreen", "reg-structure", "reg-symlink", "reg-system-allowlist", "reg-system-branch",
        "reg-system-default-deny", "reg-system-hive", "reg-system-user", "reg-target-not-built-by-planner", "reg-uac",
        "service-bad-name", "service-bad-start", "service-boot-start", "service-driver", "service-edge", "service-elevate-outside-program-files",
        "service-hardlist", "service-has-dependents", "service-in-windows", "service-microsoft-unowned", "service-modifiable",
        "service-name-mismatch", "service-no-image", "service-not-found", "service-ppl", "service-security-product", "service-shared",
        "service-system-zone", "service-unknown", "service-unknown-start", "service-unknown-type",
        "startup-bad-id", "startup-bad-signature-facts", "startup-bad-target", "startup-command-changed", "startup-entry-missing",
        "startup-in-windows", "startup-microsoft-in-windows", "startup-modifiable", "startup-security-component", "startup-system-zone",
        "startup-unparsed",
        "task-action-not-exec", "task-action-unresolved", "task-bad-path", "task-disable-or-revert", "task-elevated", "task-exec-in-windows",
        "task-exec-outside-roots", "task-exec-system-zone", "task-modifiable", "task-no-actions", "task-not-found", "task-path-mismatch",
        "task-privileged-principal", "task-protected-tree", "task-unknown-principal", "task-unknown-runlevel",
        "tweak-bad-hive", "tweak-not-in-catalog",
    };

    [Fact]
    public void Every_known_agent_code_has_a_plain_russian_explanation()
    {
        foreach (var code in AgentCodes)
        {
            Assert.True(ReasonTexts.IsKnown(code), "нет объяснения для " + code);
            var text = ReasonTexts.Explain(code);
            Assert.NotEqual(ReasonTexts.Unsure, text);
            Assert.Matches("[А-Яа-я]", text);
        }
    }

    [Fact]
    public void Unknown_code_falls_back_to_unsure()
    {
        Assert.Equal(ReasonTexts.Unsure, ReasonTexts.Explain("totally-new-code"));
    }

    // ----- план снимков -----

    [Fact]
    public void Snapshot_plan_covers_minimum_window_and_welcome()
    {
        var plan = SnapshotPlan.Build();
        foreach (var screen in SnapshotPlan.Screens)
            Assert.Contains(plan, s => s.Screen == screen.Id && s.State == SnapshotState.Ready && s.Width == 900 && s.Height == 600);
        Assert.Contains(plan, s => s.Screen == "overview" && s.State == SnapshotState.Welcome);
        Assert.Equal("overview-welcome_1280x800_96.png",
            new SnapshotSpec("overview", SnapshotState.Welcome, 1280, 800, 96).FileName);
    }
}
