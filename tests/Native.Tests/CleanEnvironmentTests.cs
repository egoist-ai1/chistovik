using System.Runtime.InteropServices;
using Chistovik.Native.Setup;
using Xunit;

namespace Chistovik.Native.Tests;

/// <summary>Окружение деинсталлятора: только системные переменные, TEMP и TMP заменены (ревью C1, п. 1).</summary>
public class CleanEnvironmentTests
{
    static Dictionary<string, string> Read(IntPtr block)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var offset = 0; ; )
        {
            var entry = Marshal.PtrToStringUni(IntPtr.Add(block, offset)) ?? string.Empty;
            if (entry.Length == 0) break;
            offset += (entry.Length + 1) * 2;
            var eq = entry.IndexOf('=', 1);
            if (eq > 0) map[entry[..eq]] = entry[(eq + 1)..];
        }
        return map;
    }

    [Fact]
    public void Block_has_replaced_temp_and_no_variables_of_the_agent_process()
    {
        const string probe = "CHISTOVIK_TEST_PROBE";
        const string temp = @"C:\ProgramData\Chistovik\tmp\test";
        Environment.SetEnvironmentVariable(probe, "1");
        Environment.SetEnvironmentVariable("COR_ENABLE_PROFILING", "1");
        var block = CleanEnvironment.Build(temp);
        try
        {
            var vars = Read(block);
            Assert.Equal(temp, vars["TEMP"]);
            Assert.Equal(temp, vars["TMP"]);
            Assert.DoesNotContain(probe, vars.Keys);
            Assert.DoesNotContain("COR_ENABLE_PROFILING", vars.Keys);
            Assert.Contains("SystemRoot", vars.Keys);
        }
        finally
        {
            CleanEnvironment.Free(block);
            Environment.SetEnvironmentVariable(probe, null);
            Environment.SetEnvironmentVariable("COR_ENABLE_PROFILING", null);
        }
    }
}
