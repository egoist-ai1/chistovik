using System.Reflection;
using Chistovik.App.Models;
using Chistovik.App.Services;
using Chistovik.App.ViewModels;
using Chistovik.Core.Ports;
using Xunit;

namespace App.Tests;

/// <summary>Дефекты интерфейса из ручной проверки в Sandbox (25.10.2026): устаревший список, имена элементов, службы пользователя.</summary>
public class ListDefectsTests
{
    sealed class MutableStartup : IStartupSource
    {
        public StartupInfo[] Items { get; set; } = Array.Empty<StartupInfo>();
        public Task<IReadOnlyList<StartupInfo>> GetStartupAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<StartupInfo>>(Items);
    }

    static StartupInfo Entry(string name) => new("run:hku:" + name, name, name + ".exe", "Реестр", true, true, null);

    [Fact]
    public async Task Startup_list_is_reread_every_time_the_page_is_shown_again()
    {
        var src = new MutableStartup { Items = new[] { Entry("A") } };
        var vm = new StartupViewModel(src);
        await vm.Loaded;
        Assert.Single(vm.Items);

        // Запись удалили или откатили вне приложения; пользователь ушёл на другой раздел и вернулся.
        src.Items = new[] { Entry("A"), Entry("B") };
        vm.OnShown();

        for (var i = 0; i < 100 && vm.Items.Count != 2; i++) await Task.Delay(20);
        Assert.Equal(2, vm.Items.Count);
    }

    static ServiceInfo Svc(string name, string display) => new(name, display, StartMode.Manual, Changeability.Modifiable, "r", @"C:\x.exe");

    [Fact]
    public async Task Per_user_copies_collapse_to_one_marked_row()
    {
        var vm = new ServicesViewModel(new ServiceSrc(
            Svc("cbdhsvc", "Служба буфера обмена"),
            Svc("cbdhsvc_1a2b3", "Служба буфера обмена_1a2b3"),
            Svc("Spooler", "Диспетчер печати")), null, new FakeDialogs());
        await vm.Loaded;

        Assert.Equal(2, vm.Items.Count);
        var row = vm.Items.Single(x => x.Name == "cbdhsvc");
        Assert.Equal("Служба буфера обмена (для пользователя)", row.DisplayName);
        Assert.DoesNotContain("1a2b3", row.DisplayName);
        Assert.Equal("Диспетчер печати", vm.Items.Single(x => x.Name == "Spooler").DisplayName);
    }

    [Fact]
    public async Task Per_user_instance_without_template_is_cleaned_and_marked()
    {
        var vm = new ServicesViewModel(new ServiceSrc(Svc("CDPUserSvc_4b92f", "Платформа_4b92f")), null, new FakeDialogs());
        await vm.Loaded;

        Assert.Equal("Платформа (для пользователя)", vm.Items.Single().DisplayName);
    }

    /// <summary>Элементы списков не должны получать имя типа от ToString: оно уходит в имя элемента для программ чтения с экрана.</summary>
    [Fact]
    public void List_item_view_models_override_ToString()
    {
        var asm = typeof(StartupViewModel).Assembly;
        var items = asm.GetTypes().Where(t =>
                t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition && !t.IsNested
                && (t.Namespace == "Chistovik.App.ViewModels" || t.Name == "ConflictRowViewModel")
                && !typeof(PageViewModel).IsAssignableFrom(t)
                && t.Name is not ("NavItem" or "MainViewModel")
                && !(t.IsAbstract && t.IsSealed)
                && t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
            .ToList();
        Assert.NotEmpty(items);

        var missing = items
            .Where(t => t.GetMethod("ToString", Type.EmptyTypes)!.DeclaringType != t)
            .Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0, "Нет ToString: " + string.Join(", ", missing));
    }
}
