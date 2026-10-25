using Chistovik.Core.Catalog;
using Chistovik.Core.Findings;
using Chistovik.Core.Inventory;
using Chistovik.Core.Policy;
using Verdict = Chistovik.Core.Findings.Verdict;

namespace Chistovik.App.Services.Scanners;

/// <summary>Источник находок одной области для экрана «Чистка». Только чтение: ничего не меняет и прав не требует.</summary>
public interface IFindingScanner
{
    FindingArea Area { get; }

    Task<IReadOnlyList<Finding>> ScanAsync(CancellationToken ct = default);
}

/// <summary>Проверка существования файлов и дисков. За интерфейсом, чтобы тесты не касались настоящей файловой системы.</summary>
public interface IFileExists
{
    bool FileExists(string path);

    bool DirectoryExists(string path);

    /// <summary>true, если том (корень пути, например «D:\») доступен.</summary>
    bool DriveExists(string path);
}

/// <summary>Состояние продукта Windows Installer (MsiQueryProductState): 5 установлен, 2 отсутствует, -1 неизвестен.</summary>
public interface IMsiState
{
    int QueryProductState(string productCode);
}

/// <summary>Значение «по умолчанию» ключа (пустое имя). IRegistryReader его не отдаёт, а App Paths хранит путь именно там.</summary>
public interface IRegistryDefaultValue
{
    string? ReadDefault(RegistryHiveKind hive, RegistryViewKind view, string path);
}

/// <summary>Сколько занимает категория очистки: байты и число файлов, подпадающих под правила (возраст, шаблоны, исключения).</summary>
public sealed record DiskMeasurement(long Bytes, long Files, bool Exists);

/// <summary>Измерение категорий пользователя без прав. Категории компьютера измеряет агент.</summary>
public interface IDiskMeasure
{
    /// <summary>null, если категорию без прав измерить нельзя (корни недоступны или не определены).</summary>
    DiskMeasurement? Measure(CleanupCategory category);
}

/// <summary>Сборка находки из готовых частей: заголовок совета берётся у классификатора, чтобы тексты не расходились.</summary>
static class FindingFactory
{
    public static Finding Make(string id, FindingArea area, string title, string detail, VerdictKind kind,
        IReadOnlyList<Reason> reasons, Risk risk, Reversibility reversibility, Confidence confidence,
        Explanation explanation, ChangeClass cls, string openIn = "")
    {
        if (reasons.Count == 0) throw new InvalidOperationException("Вердикт без причин: " + id);
        return new Finding(id, area, title, detail,
            new Verdict(kind, FindingClassifier.Headline(kind, area), reasons, risk, reversibility, confidence),
            explanation, cls, openIn);
    }
}
