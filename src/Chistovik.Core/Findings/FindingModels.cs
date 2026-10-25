using Chistovik.Core.Policy;

namespace Chistovik.Core.Findings;

/// <summary>
/// Область находки. Disk (C2): категория очистки диска, необратима. Registry (C3): запись реестра без файлов, удаляется с копией
/// и откатывается из журнала.
/// </summary>
public enum FindingArea { Programs, Startup, Services, Tasks, Disk, Registry }

/// <summary>Совет по объекту. Remove и Optional это совет, а не действие: в C0 ничего не меняется.</summary>
public enum VerdictKind { Remove, Optional, Keep, Protected, Unknown }

public enum Risk { Low, Medium, High }

/// <summary>Можно ли вернуть объект после изменения. NotAvailable: изменение не предлагается.</summary>
public enum Reversibility { Undoable, ReinstallOnly, Irreversible, NotAvailable }

/// <summary>
/// Насколько мы уверены. L0 подтверждено системой, L1 запись каталога, L2 прямое свидетельство (например, файла нет),
/// L3 эвристика по признакам, L4 данных нет.
/// </summary>
public enum Confidence { L0, L1, L2, L3, L4 }

/// <summary>Одна причина вердикта: код из закрытого списка <see cref="ReasonCodes"/>, понятный текст и необязательное свидетельство.</summary>
public sealed record Reason(string Code, string Text, string? Evidence = null);

/// <summary>Четыре обязательные фразы: что это, зачем нужно, что будет без него, как вернуть.</summary>
public sealed record Explanation(string WhatIsIt, string WhyNeeded, string IfRemoved, string HowToUndo);

public sealed record Verdict(
    VerdictKind Kind,
    string Headline,
    IReadOnlyList<Reason> Reasons,
    Risk Risk,
    Reversibility Reversibility,
    Confidence Confidence);

/// <summary>
/// Находка: объект системы и совет по нему. <see cref="Class"/> это класс по политике (что разрешено менять), а не совет:
/// объект с советом «убрать» и классом Forbidden остаётся только для просмотра. <see cref="OpenIn"/> это заголовок раздела,
/// где объект можно менять.
/// </summary>
public sealed record Finding(
    string Id,
    FindingArea Area,
    string Title,
    string Detail,
    Verdict Verdict,
    Explanation Explanation,
    ChangeClass Class,
    string OpenIn,
    string? IconSource = null);
