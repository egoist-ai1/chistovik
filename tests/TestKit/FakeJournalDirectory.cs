using Chistovik.Core.Ports;

namespace Chistovik.TestKit;

/// <summary>
/// Подставной каталог журнала: Inspect отдаёт заданные факты, CreateProtected создаёт настоящий (временный) каталог и
/// переключает факты на «доверенный» или «недоверенный» (CreatedIsTrusted). ACL не трогает.
/// </summary>
public sealed class FakeJournalDirectory : IJournalDirectory
{
    public const string Administrators = "S-1-5-32-544";
    public const string System = "S-1-5-18";
    public const string Users = "S-1-5-32-545";

    public static DirFacts Trusted { get; } = new(true, false, Administrators,
    [
        new DirAce(System, true, true),
        new DirAce(Administrators, true, true),
        new DirAce(Users, true, false),
    ]);

    /// <summary>Группе Users разрешена запись: JournalAclPolicy обязан отказать.</summary>
    public static DirFacts UsersCanWrite { get; } = new(true, false, Administrators,
    [
        new DirAce(System, true, true),
        new DirAce(Administrators, true, true),
        new DirAce(Users, true, true),
    ]);

    public static DirFacts Missing { get; } = new(false, false, null, []);

    public DirFacts Facts { get; set; } = Trusted;

    /// <summary>Какими станут факты после CreateProtected.</summary>
    public bool CreatedIsTrusted { get; set; } = true;

    public int InspectCount { get; private set; }

    public int CreateCount { get; private set; }

    public DirFacts Inspect(string path)
    {
        InspectCount++;
        return Facts;
    }

    public void CreateProtected(string path)
    {
        CreateCount++;
        Directory.CreateDirectory(path);
        Facts = CreatedIsTrusted ? Trusted : UsersCanWrite;
    }
}
