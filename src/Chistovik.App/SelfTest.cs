using System.Windows;
using System.Windows.Controls;
using Chistovik.App.ViewModels;

namespace Chistovik.App;

/// <summary>
/// Самопроверка без показа окна: переключает все разделы и разворачивает каждое представление,
/// чтобы ошибки ресурсов и привязок проявились сразу. Код 0 — всё в порядке.
/// </summary>
internal static class SelfTest
{
    public static int Run(MainWindow window, MainViewModel vm)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var failures = 0;
        // Окна диалогов создаются, но не показываются: проверяем только ресурсы и разметку.
        try { _ = new Views.StubDialog("t", "m"); }
        catch (Exception ex) { failures++; Console.WriteLine($"FAIL StubDialog: {ex.Message}"); }
        try { _ = new Views.ConfirmDialog(new Services.ConfirmContent("t", "m", "Да", Danger: true)); }
        catch (Exception ex) { failures++; Console.WriteLine($"FAIL ConfirmDialog: {ex.Message}"); }
        try
        {
            _ = new Views.ConflictsDialog(new[]
            {
                new Chistovik.Core.Ipc.ConflictInfo(1, Chistovik.Core.Ports.StepKind.RegistryValue, "значение 1", null),
            });
        }
        catch (Exception ex) { failures++; Console.WriteLine($"FAIL ConflictsDialog: {ex.Message}"); }
        foreach (var nav in vm.AllNavItems)
        {
            try
            {
                vm.SelectedNav = nav;
                var page = vm.Current ?? throw new InvalidOperationException("Current = null");
                var template = (DataTemplate)window.FindResource(new DataTemplateKey(page.GetType()));
                var view = (FrameworkElement)template.LoadContent();
                view.DataContext = page;
                view.Measure(new Size(1000, 700));
                view.Arrange(new Rect(0, 0, 1000, 700));
                view.UpdateLayout();

                if (page is StatefulPageViewModel st && (st.IsError || st.IsLoading))
                    throw new InvalidOperationException($"состояние раздела: {st.State}, {st.StateMessage}");

                var rows = view is DependencyObject d ? CountListItems(d) : 0;
                Console.WriteLine($"OK   {nav.Title} -> {page.GetType().Name}, строк в списках: {rows}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL {nav.Title}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(failures == 0 ? "SELFTEST OK" : $"SELFTEST FAILED: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static int CountListItems(DependencyObject root)
    {
        var total = 0;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ListBox lb) total += lb.ItemContainerGenerator.Items.Count;
            total += CountListItems(child);
        }
        return total;
    }
}
