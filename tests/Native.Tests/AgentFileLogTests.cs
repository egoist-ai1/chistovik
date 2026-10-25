using System.Text;
using Chistovik.Native.Ipc;
using Xunit;

namespace Chistovik.NativeTests;

/// <summary>Файловый журнал агента: предел размера, границы строк и UTF-8, потоки, недоступный каталог. Только временная папка.</summary>
public class AgentFileLogTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "chistovik-agentlog-" + Guid.NewGuid().ToString("N"));

    public AgentFileLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    string LogPath => Path.Combine(_dir, AgentFileLog.FileName);

    [Fact]
    public void Lines_before_attach_are_kept_and_flushed_in_order()
    {
        var log = new AgentFileLog();
        log.Write("first");
        log.Write("second");
        Assert.False(File.Exists(LogPath)); // до подключения каталога ничего не пишется

        Assert.True(log.Attach(_dir));
        log.Write("third");

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("first", lines[0]);
        Assert.EndsWith("second", lines[1]);
        Assert.EndsWith("third", lines[2]);
    }

    [Fact]
    public void Paths_and_sids_are_removed_from_lines()
    {
        var log = new AgentFileLog();
        Assert.True(log.Attach(_dir));

        log.Write(@"journal-open-failed:IOException: Нет доступа к C:\Users\Ivan\AppData\x.db и \\server\share\f для S-1-5-21-111-222-333-1001");

        var text = File.ReadAllText(LogPath);
        Assert.DoesNotContain("Ivan", text);
        Assert.DoesNotContain("server", text);
        Assert.DoesNotContain("S-1-5-21", text);
        Assert.Contains("journal-open-failed:IOException", text);
    }

    [Fact]
    public void Size_is_bounded_and_trimmed_on_a_line_boundary_without_breaking_utf8()
    {
        const int max = 4000;
        var log = new AgentFileLog(max);
        Assert.True(log.Attach(_dir));

        for (var i = 0; i < 400; i++) log.Write("запись номер " + i + " журнал агента ёжик");

        var info = new FileInfo(LogPath);
        Assert.True(info.Length <= max + 200, "размер " + info.Length);
        var strict = new UTF8Encoding(false, true); // бросит на разрезанном символе
        var lines = strict.GetString(File.ReadAllBytes(LogPath)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.Contains("запись номер", l)); // ни одной обрезанной строки
        Assert.EndsWith("399 журнал агента ёжик", lines[^1]);
    }

    [Fact]
    public void Concurrent_writers_lose_no_lines()
    {
        var log = new AgentFileLog();
        Assert.True(log.Attach(_dir));

        Parallel.For(0, 8, t =>
        {
            for (var i = 0; i < 100; i++) log.Write($"t{t}-{i}");
        });

        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(800, lines.Length);
        Assert.Equal(800, lines.Select(l => l[(l.IndexOf(' ', 11) + 1)..]).Distinct().Count());
    }

    [Fact]
    public void Missing_directory_is_not_created_and_nothing_throws()
    {
        var missing = Path.Combine(_dir, "no", "such");
        var log = new AgentFileLog();
        log.Write("before");

        Assert.False(log.Attach(missing));
        log.Write("after");

        Assert.False(Directory.Exists(missing));
        Assert.Null(log.FilePath);
    }

    [Fact]
    public void Path_that_is_a_file_or_locked_does_not_throw()
    {
        var asFile = Path.Combine(_dir, "plain");
        File.WriteAllText(asFile, "x");
        var log = new AgentFileLog();
        Assert.False(log.Attach(asFile));
        log.Write("ignored");

        // Файл журнала занят чужим эксклюзивным дескриптором: запись молча пропускается.
        var log2 = new AgentFileLog();
        Assert.True(log2.Attach(_dir));
        log2.Write("seed"); // файл создаётся первой записью
        using (new FileStream(LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            log2.Write("blocked");
        }
        log2.Write("after");
        Assert.Contains("after", File.ReadAllText(LogPath));
    }

    [Fact]
    public void Close_drops_pending_lines_and_disables_the_file()
    {
        var log = new AgentFileLog();
        log.Write("secret-untrusted");
        log.Close();

        Assert.False(log.Attach(_dir));
        log.Write("later");

        Assert.False(File.Exists(LogPath));
    }
}
