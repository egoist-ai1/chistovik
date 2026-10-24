using Chistovik.Core.Policy;
using Xunit;

namespace Chistovik.CoreTests;

public class PathNormalizerTests
{
    [Theory]
    [InlineData(@"C:\Windows", @"C:\Windows")]
    [InlineData(@"c:\windows\", @"C:\windows")]
    [InlineData(@"C:/Windows/System32", @"C:\Windows\System32")]
    [InlineData(@"\\?\C:\Windows", @"C:\Windows")]
    [InlineData(@"\\.\C:\Windows", @"C:\Windows")]
    [InlineData(@"\??\C:\Windows", @"C:\Windows")]
    [InlineData(@"C:\\Windows\\\System32", @"C:\Windows\System32")]
    [InlineData(@"C:\Windows\.\System32", @"C:\Windows\System32")]
    [InlineData(@"C:\Users\..\Windows", @"C:\Windows")]
    [InlineData(@"C:\Windows. \System32 .", @"C:\Windows\System32")]
    [InlineData(@"C:\", @"C:\")]
    [InlineData(@"d:\Games\Foo Bar", @"D:\Games\Foo Bar")]
    public void Normalizes(string input, string expected)
    {
        Assert.True(PathNormalizer.TryNormalize(input, out var n, out var err), err);
        Assert.Equal(expected, n);
    }

    [Theory]
    [InlineData(@"C:\a", @"C:\a", true)]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\A\B", @"C:\a", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    [InlineData(@"C:\a", @"C:\a\b", false)]
    [InlineData(@"C:\a", @"C:\", true)]
    public void IsEqualOrUnder_respects_component_boundaries(string path, string root, bool expected)
        => Assert.Equal(expected, PathNormalizer.IsEqualOrUnder(path, root));

    [Fact]
    public void Components_of_root_is_empty()
        => Assert.Empty(PathNormalizer.Components(@"C:\"));
}
