using System.Runtime.InteropServices;
using System.Security.Principal;
using Chistovik.Core.Ports;
using Chistovik.Native.Registry;
using Xunit;

namespace Chistovik.Native.Tests;

/// <summary>
/// Запись через WindowsRegistryTree проверяется только в HKCU текущего пользователя (HKU\&lt;sid&gt;\Software), в ключах с уникальным именем
/// «ChistovikTreeTest-guid»; всё созданное убирается в finally. HKLM здесь не трогается.
/// </summary>
public sealed class RegistryTreeWriteTests
{
    static readonly string Sid = WindowsIdentity.GetCurrent().User!.Value;
    static readonly RegLocation SoftwareLoc = new(RegRoot.User, Sid, "Software", string.Empty);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegCreateKeyExW")]
    static extern int RegCreateKeyEx(IntPtr hKey, string subKey, int reserved, string? cls, int options, int sam, IntPtr security, out IntPtr result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    static extern int RegOpenKeyEx(IntPtr hKey, string subKey, int options, int sam, out IntPtr result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegSetValueExW")]
    static extern int RegSetValueEx(IntPtr hKey, string name, int reserved, int type, byte[] data, int size);

    [DllImport("advapi32.dll")]
    static extern int RegCloseKey(IntPtr hKey);

    [DllImport("ntdll.dll")]
    static extern int NtDeleteKey(IntPtr hKey);

    static IntPtr Hkcu => Microsoft.Win32.Registry.CurrentUser.Handle.DangerousGetHandle();

    [Fact]
    public void RawValues_with_a_limit_refuses_an_oversized_value_by_size_before_reading_it()
    {
        var tree = new WindowsRegistryTree();
        var name = "ChistovikTreeTest-" + Guid.NewGuid().ToString("N");
        using var parent = tree.OpenTree(SoftwareLoc, true)!;
        using var sub = parent.CreateSub(name, out var created);
        try
        {
            Assert.True(created);
            sub.SetRaw("big", RegType.Binary, new byte[3 << 20]);
            sub.SetRaw("small", RegType.Binary, [1, 2, 3]);

            Assert.Throws<RegValueTooLargeException>(() => sub.RawValues(1 << 20));
            Assert.Throws<RegValueTooLargeException>(() => sub.RawValues(2)); // «small» один не умещается в 2 байта
            Assert.Equal(2, sub.RawValues(4 << 20).Count);
            Assert.Equal(2, sub.RawValues().Count);

            // Повторное создание того же имени это существующий ключ.
            using var again = parent.CreateSub(name, out var createdAgain);
            Assert.False(createdAgain);
            Assert.False(again.IsSymbolicLink);
        }
        finally
        {
            try { sub.DeleteSelf(); }
            catch (Exception) { /* уборка */ }
        }
    }

    [Fact]
    public void Symlink_handle_refuses_write_create_and_open_for_delete_and_nothing_appears_in_the_target()
    {
        var tree = new WindowsRegistryTree();
        var linkName = "ChistovikTreeTest-link-" + Guid.NewGuid().ToString("N");
        var targetName = "ChistovikTreeTest-target-" + Guid.NewGuid().ToString("N");
        const int regOptionCreateLink = 2, regOptionOpenLink = 8, keyCreateLink = 0x20, keySetValue = 0x2, delete = 0x10000, regLink = 6;

        var rc = RegCreateKeyEx(Hkcu, @"Software\" + linkName, 0, null, regOptionCreateLink, keyCreateLink | keySetValue, IntPtr.Zero, out var h, out _);
        Assert.Equal(0, rc);
        try
        {
            var target = @"\Registry\User\" + Sid + @"\Software\" + targetName;
            var bytes = System.Text.Encoding.Unicode.GetBytes(target);
            Assert.Equal(0, RegSetValueEx(h, "SymbolicLinkValue", 0, regLink, bytes, bytes.Length));
        }
        finally
        {
            RegCloseKey(h);
        }

        try
        {
            using var parent = tree.OpenTree(SoftwareLoc, true)!;
            using var link = parent.CreateSub(linkName, out var created);
            Assert.False(created); // ссылка не разыменована: открыт существующий ключ-ссылка
            Assert.True(link.IsSymbolicLink);

            Assert.Throws<InvalidOperationException>(() => link.SetRaw("x", RegType.Binary, [1]));
            Assert.Throws<InvalidOperationException>(() => link.CreateSub("child", out _));
            Assert.Throws<InvalidOperationException>(() => link.OpenSub("child", true));
            Assert.Throws<InvalidOperationException>(() => link.DeleteSelf());

            using var probe = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\" + targetName);
            Assert.Null(probe); // цель ссылки не создана
        }
        finally
        {
            // Саму ссылку удаляем открытием с OPEN_LINK.
            if (RegOpenKeyEx(Hkcu, @"Software\" + linkName, regOptionOpenLink, delete, out var del) == 0)
            {
                NtDeleteKey(del);
                RegCloseKey(del);
            }
        }
    }
}
