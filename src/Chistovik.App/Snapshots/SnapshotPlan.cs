using System.Globalization;

namespace Chistovik.App.Snapshots;

/// <summary>Состояние экрана на снимке. Ready — обычный вид с демонстрационными данными.</summary>
public enum SnapshotState { Ready, Empty, Loading, Error, ViewOnly, Partial, Welcome }

/// <summary>
/// Один снимок: экран, состояние, размер окна в независимых единицах и DPI (96 = 100 %, 192 = 200 %).
/// Для диалогов размер задаёт сам диалог: Width и Height равны 0, в имени файла стоит «auto».
/// </summary>
public sealed record SnapshotSpec(string Screen, SnapshotState State, int Width, int Height, int Dpi)
{
    public bool IsDialog => Screen.StartsWith("dialog-", StringComparison.Ordinal);

    public string StateSuffix => State switch
    {
        SnapshotState.Empty => "-empty",
        SnapshotState.Loading => "-loading",
        SnapshotState.Error => "-error",
        SnapshotState.ViewOnly => "-viewonly",
        SnapshotState.Partial => "-partial",
        SnapshotState.Welcome => "-welcome",
        _ => "",
    };

    /// <summary>Имя файла: экран[-состояние]_ширинаxвысота_dpi.png. Тема одна (Signal, тёмная), суффикса темы нет.</summary>
    public string FileName
    {
        get
        {
            var size = IsDialog ? "auto" : string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}");
            return string.Create(CultureInfo.InvariantCulture, $"{Screen}{StateSuffix}_{size}_{Dpi}.png");
        }
    }
}

/// <summary>Экран главного окна: короткое имя для файла, заголовок пункта навигации и состояния, которые у него бывают.</summary>
public sealed record SnapshotScreen(string Id, string NavTitle, SnapshotState[] ExtraStates);

/// <summary>Перечень снимков. Чистая логика без WPF: проверяется юнит-тестом, окна не создаёт.</summary>
public static class SnapshotPlan
{
    public static readonly (int Width, int Height)[] Sizes = { (1280, 800), (1920, 1080) };
    public static readonly int[] Dpis = { 96, 192 };

    /// <summary>Самое маленькое окно, которое разрешает программа: проверка, что ничего не обрезается.</summary>
    public static readonly (int Width, int Height) MinimumSize = (900, 600);

    private static readonly SnapshotState[] ListStates = { SnapshotState.Empty, SnapshotState.Loading, SnapshotState.Error };
    private static readonly SnapshotState[] LoadStates = { SnapshotState.Loading, SnapshotState.Error };
    private static readonly SnapshotState[] OverviewStates = { SnapshotState.Loading, SnapshotState.Error, SnapshotState.Welcome };
    private static readonly SnapshotState[] ChangeStates = { SnapshotState.ViewOnly, SnapshotState.Partial };
    private static readonly SnapshotState[] ListAndChange =
        { SnapshotState.Empty, SnapshotState.Loading, SnapshotState.Error, SnapshotState.ViewOnly, SnapshotState.Partial };
    private static readonly SnapshotState[] LoadAndChange =
        { SnapshotState.Loading, SnapshotState.Error, SnapshotState.ViewOnly, SnapshotState.Partial };

    public static readonly IReadOnlyList<SnapshotScreen> Screens = new SnapshotScreen[]
    {
        new("overview", "Обзор", OverviewStates),
        new("programs", "Инвентарь", ListStates),
        new("startup", "Автозагрузка", ListAndChange),
        new("services", "Службы", ListAndChange),
        new("tasks", "Задачи", ListAndChange),
        new("tweaks", "Твики", LoadAndChange),
        new("modes", "Режимы", ChangeStates),
        new("power", "Питание", LoadAndChange),
        new("journal", "Журнал и откат", ListAndChange),
    };

    public static readonly string[] Dialogs = { "dialog-confirm", "dialog-confirm-danger", "dialog-conflicts" };

    /// <summary>
    /// Обычный вид — во всех размерах и масштабах (1280×800 и 1920×1080, 100 % и 200 %). Остальные состояния —
    /// при 1280×800 и 100 %: их проверяют на содержание, а не на масштаб. Диалоги — при 100 % и 200 %.
    /// </summary>
    public static IReadOnlyList<SnapshotSpec> Build()
    {
        var list = new List<SnapshotSpec>();
        foreach (var screen in Screens)
        {
            foreach (var (w, h) in Sizes)
                foreach (var dpi in Dpis)
                    list.Add(new SnapshotSpec(screen.Id, SnapshotState.Ready, w, h, dpi));
            list.Add(new SnapshotSpec(screen.Id, SnapshotState.Ready, MinimumSize.Width, MinimumSize.Height, 96));
            foreach (var state in screen.ExtraStates)
                list.Add(new SnapshotSpec(screen.Id, state, Sizes[0].Width, Sizes[0].Height, 96));
        }
        foreach (var dialog in Dialogs)
            foreach (var dpi in Dpis)
                list.Add(new SnapshotSpec(dialog, SnapshotState.Ready, 0, 0, dpi));
        return list;
    }

    /// <summary>Разбор командной строки: --snapshots папка. requested = false — режим не просили.</summary>
    public static bool TryParseArgs(IReadOnlyList<string> args, out bool requested, out string? directory, out string? error)
    {
        requested = false;
        directory = null;
        error = null;
        var i = -1;
        for (var k = 0; k < args.Count; k++)
            if (string.Equals(args[k], "--snapshots", StringComparison.OrdinalIgnoreCase)) { i = k; break; }
        if (i < 0) return true;
        requested = true;
        if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = "После --snapshots нужна папка для PNG.";
            return false;
        }
        directory = args[i + 1];
        return true;
    }
}
