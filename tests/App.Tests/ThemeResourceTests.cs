using System.IO;
using System.Text.RegularExpressions;
using Chistovik.App.ViewModels;
using Xunit;

namespace App.Tests;

/// <summary>
/// Ресурсы оформления без окон: у каждого раздела навигации есть векторная иконка и все три кисти его оттенка.
/// Читаем XAML как текст, поэтому проверка не требует WPF-потока.
/// </summary>
public class ThemeResourceTests
{
    private static string Themes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Chistovik.App", "Themes");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Не найдена папка Themes приложения.");
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(Themes(), file));

    [Fact]
    public void Every_nav_section_has_an_icon_geometry()
    {
        var icons = Read("Icons.xaml");
        foreach (var (title, look) in MainViewModel.Looks)
            Assert.True(icons.Contains($"x:Key=\"Ico.{look.Icon}\""), $"нет иконки Ico.{look.Icon} для раздела «{title}»");
    }

    [Fact]
    public void Every_nav_section_has_its_hue_brushes()
    {
        var tokens = Read("Tokens.xaml");
        foreach (var (title, look) in MainViewModel.Looks)
            foreach (var suffix in new[] { "Brush", "TintBrush", "LineBrush" })
                Assert.True(tokens.Contains($"x:Key=\"Hue{look.Hue}{suffix}\""), $"нет кисти Hue{look.Hue}{suffix} для раздела «{title}»");
    }

    [Fact]
    public void Icon_geometries_are_nonempty_paths()
    {
        var icons = Read("Icons.xaml");
        var matches = Regex.Matches(icons, "<Geometry x:Key=\"Ico\\.([a-z0-9-]+)\">([^<]*)</Geometry>");
        Assert.True(matches.Count >= 30, "иконок слишком мало: " + matches.Count);
        foreach (Match m in matches)
            Assert.Matches("^\\s*[Mm]", m.Groups[2].Value);
    }

    [Fact]
    public void Icon_keys_are_unique()
    {
        var keys = Regex.Matches(Read("Icons.xaml"), "x:Key=\"(Ico\\.[a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Display_text_formatting_is_not_used_for_text()
    {
        // «Display» даёт пикселизацию; во всех окнах и диалогах должен стоять Ideal.
        var root = Path.GetFullPath(Path.Combine(Themes(), ".."));
        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
            Assert.DoesNotContain("TextFormattingMode=\"Display\"", File.ReadAllText(file));
    }
}
