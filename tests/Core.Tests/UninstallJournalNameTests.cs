using Chistovik.Core.Execution;
using Chistovik.Core.Journal;
using Xunit;

namespace Chistovik.CoreTests;

/// <summary>Имя программы для журнала: чистка символов, обрезка без разрыва суррогатной пары, запись и чтение JSON.</summary>
public class UninstallJournalNameTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("7-Zip 23.01", "7-Zip 23.01")]
    [InlineData("  Git\t", "Git")]
    [InlineData("Evil‮exe.txt", "Evilexe.txt")]
    public void Name_is_cleaned(string? input, string? expected)
        => Assert.Equal(expected, UninstallPlanner.JournalName(input));

    [Fact]
    public void Long_name_is_cut_on_a_code_point_boundary_and_survives_json()
    {
        // Эмодзи занимает позиции 119–120 (суррогатная пара): обрезка по 120 знакам не должна оставить половину.
        var name = new string('a', 119) + "\U0001F600" + "tail";
        var cut = UninstallPlanner.JournalName(name);

        Assert.NotNull(cut);
        Assert.True(cut!.Length <= 120);
        Assert.False(char.IsHighSurrogate(cut[^1]));

        var json = StateJson.UninstallTargetJson(new UninstallTarget("machine", "x64", "k", "exe", null, cut));
        Assert.Equal(cut, StateJson.ParseUninstallTarget(json).Name);
    }

    [Fact]
    public void Lone_surrogate_is_dropped()
        => Assert.Equal("ab", UninstallPlanner.JournalName("a\uD83Db"));
}
