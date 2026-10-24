namespace Chistovik.Core.Policy;

/// <summary>Системные каталоги вне Windows, которыми владеет сама система (Defender, Security, Store); их файлы политики не меняют.</summary>
internal static class SystemRoots
{
    public static string[] Build(string programFiles, string programFilesX86, string programData)
        =>
        [
            programFiles + @"\Windows Defender", programFiles + @"\Windows Defender Advanced Threat Protection",
            programFiles + @"\Windows Security", programFiles + @"\WindowsApps",
            programFiles + @"\Common Files\Microsoft Shared", programFilesX86 + @"\Common Files\Microsoft Shared",
            programFilesX86 + @"\Windows Defender", programFilesX86 + @"\Windows Security",
            programData + @"\Microsoft\Windows Defender", programData + @"\Microsoft\Windows Defender Advanced Threat Protection",
            programData + @"\Microsoft\Windows Security Health",
        ];
}
