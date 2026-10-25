using System.IO;
using Chistovik.App.Services;
using Xunit;

namespace App.Tests;

/// <summary>Журнал необработанных ошибок: дописывание, предел размера, отсутствие исключений. Только временная папка.</summary>
public class CrashLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chistovik-crashlog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Write_creates_directory_and_appends_entries()
    {
        var log = new CrashLog(Path.Combine(_dir, "sub", "crash.log"));
        Assert.True(log.Write("Dispatcher", new InvalidOperationException("первая")));
        Assert.True(log.Write("AppDomain", new InvalidOperationException("вторая")));
        var text = File.ReadAllText(log.FilePath);
        Assert.Contains("[Dispatcher]", text);
        Assert.Contains("[AppDomain]", text);
        Assert.Contains("первая", text);
        Assert.Contains("вторая", text);
    }

    [Fact]
    public void Write_trims_oversized_file_and_keeps_newest_entry()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "crash.log");
        File.WriteAllBytes(path, new byte[CrashLog.MaxBytes + 1000]);
        var log = new CrashLog(path);
        Assert.True(log.Write("Dispatcher", new Exception("свежая ошибка")));
        Assert.True(new FileInfo(path).Length < CrashLog.MaxBytes);
        Assert.Contains("свежая ошибка", File.ReadAllText(path));
    }

    [Fact]
    public void Write_returns_false_instead_of_throwing_when_path_is_unusable()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "file");
        File.WriteAllText(blocker, "x");
        // Каталог «file» это файл: создать под ним подкаталог нельзя.
        var log = new CrashLog(Path.Combine(blocker, "crash.log"));
        Assert.False(log.Write("Dispatcher", new Exception("e")));
    }

    [Fact]
    public void Write_accepts_null_exception_and_user_message_names_the_file()
    {
        var log = new CrashLog(Path.Combine(_dir, "crash.log"));
        Assert.True(log.Write("AppDomain", null));
        Assert.Contains(log.FilePath, log.UserMessage());
        Assert.StartsWith("Чистовик столкнулся с ошибкой.", log.UserMessage());
    }
}
