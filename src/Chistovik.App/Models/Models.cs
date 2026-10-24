namespace Chistovik.App.Models;

public sealed record ProgramInfo(
    string Name, string Publisher, string Version, long SizeBytes, DateTime InstalledOn, string Source);

public enum RiskLevel { Known, Review, Suspicious }

public sealed record StartupInfo(
    string Name, string Command, string Publisher, bool Enabled, RiskLevel Risk, int ImpactMs);

public sealed record JournalStep(string Title, string Detail, bool Done);

public sealed record JournalOperation(
    string Id, string Title, DateTime When, string Status, bool Reversible, IReadOnlyList<JournalStep> Steps);
