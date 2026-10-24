using Chistovik.App.Models;

namespace Chistovik.App.Services;

public interface IProgramsSource { Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(); }
public interface IStartupSource { Task<IReadOnlyList<StartupInfo>> GetStartupAsync(); }
public interface IJournalSource { Task<IReadOnlyList<JournalOperation>> GetOperationsAsync(); }

/// <summary>Диалоги. Сейчас заглушка, позже — настоящие подтверждения.</summary>
public interface IDialogService { void ShowStub(string title, string message); }
