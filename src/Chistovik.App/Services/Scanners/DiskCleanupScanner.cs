using System.Security.Principal;
using Chistovik.App.ViewModels;
using Chistovik.Core.Catalog;
using Chistovik.Core.Findings;
using Chistovik.Core.Policy;
using Chistovik.Core.Ports;

namespace Chistovik.App.Services.Scanners;

/// <summary>
/// Очистка диска (C2): одна находка на категорию встроенного каталога. Категории пользователя App измеряет сам, без прав;
/// категории компьютера и Корзину меряет агент («размер уточнит агент»); категории с обработчиком Windows это только карточка
/// без размера. Рекомендованные каталогом категории (<c>DefaultOn</c>) получают совет «убрать», остальные «на ваше усмотрение».
/// Удаление необратимо, поэтому каждая находка так и помечена. Ничего не удаляет.
/// </summary>
public sealed class DiskCleanupScanner(CleanupCatalog catalog, IDiskMeasure measure, IProcessList processes, string? userSid = null)
    : IFindingScanner
{
    public const string IdPrefix = "cleanup:";

    public FindingArea Area => FindingArea.Disk;

    public Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        var sid = userSid ?? CurrentSid();
        var list = new List<Finding>();
        foreach (var c in catalog.Categories)
        {
            ct.ThrowIfCancellationRequested();
            list.Add(Make(c, sid));
        }
        return (IReadOnlyList<Finding>)list;
    }, ct);

    Finding Make(CleanupCategory c, string sid)
    {
        var handler = c.Mechanism == "handler";
        var bin = c.Mechanism == "recyclebin";
        var reasons = new List<Reason>();
        string detail;
        var confidence = Confidence.L1;

        if (c.Mechanism == "files" && c.Risk == "low") reasons.Add(ReasonCodes.Make(ReasonCodes.CacheRegenerable));

        if (handler)
        {
            detail = "Размер неизвестен: очищается штатной очисткой диска Windows";
            reasons.Add(ReasonCodes.Make(ReasonCodes.SizeNeedsAgent));
        }
        else if (bin || c.Scope == "machine")
        {
            detail = "Размер уточнит агент";
            reasons.Add(ReasonCodes.Make(ReasonCodes.SizeNeedsAgent));
        }
        else
        {
            var m = measure.Measure(c);
            if (m is null)
            {
                detail = "Размер уточнит агент";
                reasons.Add(ReasonCodes.Make(ReasonCodes.SizeNeedsAgent));
            }
            else
            {
                confidence = Confidence.L2;
                detail = m.Bytes > 0
                    ? $"Около {Fmt.Size(m.Bytes)}, файлов: {Fmt.Number((int)Math.Min(m.Files, int.MaxValue))}"
                    : m.Exists ? "Сейчас нечего очищать" : "Папки нет, очищать нечего";
                if (c.MinAgeHours > 0) detail += $" (считаются файлы старше {AgeText(c.MinAgeHours)})";
            }
        }

        if (!string.IsNullOrEmpty(c.SkipIfRunning) && processes.IsRunning(c.SkipIfRunning, sid))
        {
            reasons.Add(ReasonCodes.Make(ReasonCodes.BrowserRunning, c.SkipIfRunning));
            detail += detail.Length > 0 ? ". Браузер запущен, категория будет пропущена" : "Браузер запущен, категория будет пропущена";
        }
        if (reasons.Count == 0) reasons.Add(ReasonCodes.Make(ReasonCodes.SizeNeedsAgent));

        return FindingFactory.Make(
            IdPrefix + c.Id, FindingArea.Disk, c.Title, detail,
            c.DefaultOn ? VerdictKind.Remove : VerdictKind.Optional, reasons,
            c.Risk switch { "high" => Risk.High, "medium" => Risk.Medium, _ => Risk.Low },
            Reversibility.Irreversible, confidence, Explain(c, bin, handler),
            handler ? ChangeClass.ReadOnly : ChangeClass.Modifiable);
    }

    static Explanation Explain(CleanupCategory c, bool bin, bool handler)
    {
        if (bin)
            return new Explanation(c.Description,
                "Нужна, пока вы можете захотеть вернуть удалённый файл.",
                "Все файлы из Корзины исчезнут навсегда.",
                "Вернуть нельзя: после очистки файлы из Корзины восстановить не получится.");
        if (handler)
            return new Explanation(c.Description,
                "Служебные файлы Windows: старые обновления, драйверы, части доставки.",
                "Освободится место. Откат установленных обновлений или драйверов после этого может стать невозможным.",
                "Чистовик это не выполняет: карточка показывает, что очистку нужно запускать штатными средствами Windows.");
        var restart = c.Restart == "explorer" ? " Проводник перезапустится, значки на мгновение моргнут." : "";
        return new Explanation(c.Description,
            "Нужны только на время работы: программы и Windows создают их заново.",
            "Файлы пропадут. Программы пересоздадут кэш при необходимости, первый запуск может быть чуть медленнее." + restart,
            "Вернуть нельзя: файлы удаляются без копии. Это безопасно, потому что это кэш или временные данные. "
            + $"Файлы моложе {AgeText(Math.Max(c.MinAgeHours, 1))}, занятые и ссылки остаются.");
    }

    static string AgeText(int hours) => hours % 24 == 0 ? $"{hours / 24} сут." : $"{hours} ч";

    static string CurrentSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value ?? ""; }
        catch (Exception e) when (e is System.Security.SecurityException or PlatformNotSupportedException) { return ""; }
    }
}
