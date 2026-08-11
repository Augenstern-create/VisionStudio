using Microsoft.Win32;
using System.Runtime.InteropServices;
using VisionStudio.Core;

namespace VisionStudio.App;

public static class WindowsFileAssociation
{
    public const string ProgrammaticId = "VisionStudio.Project";

    public static bool EnsureRegistered(string? executablePath = null)
    {
        if (!OperatingSystem.IsWindows()) return false;
        executablePath ??= Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) return false;

        try
        {
            using (var extension = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProjectService.ProjectExtension}"))
            {
                extension.SetValue("", ProgrammaticId);
                extension.SetValue("Content Type", "application/x-visionstudio-project");
            }

            using (var projectType = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgrammaticId}"))
            {
                projectType.SetValue("", "Vision Studio Project");
                projectType.SetValue("FriendlyTypeName", "Vision Studio Project");
            }

            using (var icon = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgrammaticId}\DefaultIcon"))
                icon.SetValue("", $"\"{executablePath}\",0");

            using (var command = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgrammaticId}\shell\open\command"))
                command.SetValue("", BuildOpenCommand(executablePath));

            var applicationName = Path.GetFileName(executablePath);
            using (var supported = Registry.CurrentUser.CreateSubKey($@"Software\Classes\Applications\{applicationName}\SupportedTypes"))
                supported.SetValue(ProjectService.ProjectExtension, "");

            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }

    public static string BuildOpenCommand(string executablePath) => $"\"{Path.GetFullPath(executablePath)}\" \"%1\"";

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
