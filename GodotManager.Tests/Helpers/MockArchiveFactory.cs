using System;
using System.IO;
using System.IO.Compression;

namespace GodotManager.Tests.Helpers;

/// <summary>
/// Creates mock Godot archive files for testing.
/// </summary>
public static class MockArchiveFactory
{
    /// <summary>
    /// A flat archive: the Godot binary plus README.txt, no subdirectories.
    /// </summary>
    public static string CreateMockGodotArchive() => CreateArchive(includeNestedEntry: false);

    /// <summary>
    /// As <see cref="CreateMockGodotArchive"/>, plus an entry nested two levels deep.
    /// Mirrors the GodotSharp/ tree that Godot's .NET builds ship, so tests can prove
    /// extraction and the --force merge actually recurse into subdirectories.
    /// </summary>
    public static string CreateMockGodotArchiveWithNestedEntry() => CreateArchive(includeNestedEntry: true);

    private static string CreateArchive(bool includeNestedEntry)
    {
        var tempFile = Path.GetTempFileName();
        var zipPath = Path.ChangeExtension(tempFile, ".zip");
        File.Delete(tempFile);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var exeName = OperatingSystem.IsWindows() ? "Godot.exe" : "godot";
            WriteEntry(archive, exeName, "Mock Godot binary");
            WriteEntry(archive, "README.txt", "Mock Godot Engine");

            if (includeNestedEntry)
            {
                WriteEntry(archive, "GodotSharp/Api/GodotSharp.dll", "Mock GodotSharp assembly");
            }
        }

        return zipPath;
    }

    private static void WriteEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.WriteLine(content);
    }
}
