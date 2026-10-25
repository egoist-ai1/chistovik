using System.Runtime.CompilerServices;
using Chistovik.App.Controls;
using Xunit;

namespace App.Tests;

/// <summary>Контрол значка без окон: статический инициализатор не падает (иначе упадёт каждый экран с XAML).</summary>
public class IconControlTests
{
    [Fact]
    public void Icon_type_initializer_runs()
    {
        try
        {
            RuntimeHelpers.RunClassConstructor(typeof(Icon).TypeHandle);
        }
        catch (TypeInitializationException e)
        {
            Assert.Fail("Icon: " + e.InnerException);
        }
    }

    [Fact]
    public void Icon_defaults_are_sane()
    {
        Assert.Equal("", Icon.KindProperty.DefaultMetadata.DefaultValue);
        Assert.Equal(20.0, Icon.SizeProperty.DefaultMetadata.DefaultValue);
        Assert.Equal(1.75, Icon.StrokeWidthProperty.DefaultMetadata.DefaultValue);
    }
}
