using System;
using System.IO;
using System.IO.Compression;

namespace GodotManager.Tests.Helpers;

/// <summary>
/// Creates mock Godot archive files for testing.
/// </summary>
public static class MockArchiveFactory
{
    public static string CreateMockGodotArchive()
    {
        var tempFile = Path.GetTempFileName();
        var zipPath = Path.ChangeExtension(tempFile, ".zip");
        File.Delete(tempFile);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var exeName = OperatingSystem.IsWindows() ? "Godot.exe" : "godot";
            var entry = archive.CreateEntry(exeName);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine("Mock Godot binary");
            }

            var readmeEntry = archive.CreateEntry("README.txt");
            using (var stream = readmeEntry.Open())
            using (var writer = new StreamWriter(stream))
            {
                writer.WriteLine("Mock Godot Engine");
            }
        }

        return zipPath;
    }
}
