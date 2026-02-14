using System.Runtime.InteropServices;
using System.Security.Principal;

namespace GodotManager.Services;

internal static class WindowsElevationHelper
{
    public static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Removes the "Mark of the Web" Zone.Identifier alternate data stream from a file.
    /// Downloaded files are tagged by Windows, which can cause SmartScreen to silently
    /// block programmatic elevation via runas (producing error 1223 without showing UAC).
    /// </summary>
    public static void TryRemoveZoneIdentifier(string filePath)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(filePath))
        {
            return;
        }

        try
        {
            // The Zone.Identifier is stored as an NTFS Alternate Data Stream.
            // Deleting it is equivalent to right-click → Properties → Unblock.
            DeleteFile(filePath + ":Zone.Identifier");
        }
        catch
        {
            // Best effort — ignore failures (e.g. non-NTFS filesystem)
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool DeleteFile(string lpFileName);
}
