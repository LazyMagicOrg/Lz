using System.Runtime.InteropServices;

namespace Lz.Core.Orchestration;

/// <summary>
/// Ensures the Pulumi CLI is discoverable on PATH.
/// Shared by SharedDeployment and SystemDeployment.
/// </summary>
internal static class PulumiPathResolver
{
    private static bool _resolved;

    public static void EnsurePulumiOnPath()
    {
        if (_resolved) return;
        _resolved = true;

        var exeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pulumi.exe" : "pulumi";
        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in pathDirs)
        {
            if (File.Exists(Path.Combine(dir, exeName)))
                return;
        }

        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pulumi", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Pulumi"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pulumi", "bin"),
            }
            : new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pulumi", "bin"),
                "/usr/local/bin",
            };

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, exeName)))
            {
                var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                Environment.SetEnvironmentVariable("PATH", $"{candidate}{Path.PathSeparator}{currentPath}");
                Console.WriteLine($"Added Pulumi to PATH: {candidate}");
                return;
            }
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("WARNING: Pulumi CLI not found. Ensure 'pulumi' is installed and on your PATH.");
        Console.ResetColor();
    }
}
