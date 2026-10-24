namespace Chistovik.Core.Policy;

/// <summary>
/// Класс объекта для политик задач и автозагрузки (по аналогии с <see cref="ServiceClass"/>).
/// Forbidden и ReadOnly не меняются никогда; RevertOnly меняется только откатом собственного шага журнала;
/// Modifiable можно менять напрямую (с записью в журнал). Всё неопознанное попадает в ReadOnly или Forbidden.
/// </summary>
public enum ChangeClass { Forbidden, ReadOnly, RevertOnly, Modifiable }
