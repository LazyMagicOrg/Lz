using System.Diagnostics;
using System.Runtime.Versioning;
using Lz.Aws.Pipeline;

namespace Lz.Aws.Deployer;

/// <summary>
/// Lays out and runs the Notation CLI inside the Lambda filesystem.
///
/// <para>COPIED TO /tmp, NOT RUN IN PLACE, for two reasons that are both about the filesystem rather
/// than about Notation: the package directory is read-only, and a zip built on Windows carries no
/// Unix mode bits, so the binaries arrive without execute permission. The gate-checker Lambda in this
/// repository ships its psql binaries the same way and handles them the same way — copy, then chmod.
/// COPIED, NOT SYMLINKED: notation-go's trust store rejects symlinks outright.</para>
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class NotationCli : INotation
{
    private static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(60);

    private readonly string _packageRoot;

    public NotationCli(string packageRoot) => _packageRoot = packageRoot;

    public Task<string?> InstallAsync(NotationLayout layout, string trustPolicyJson)
    {
        var missing = NotationLayout.PackagedFiles
            .Where(f => !File.Exists(Path.Combine(_packageRoot, f)))
            .ToList();

        if (missing.Count > 0)
            return Task.FromResult<string?>(
                $"this package was built without the verifier (missing {string.Join(", ", missing)}), so no " +
                "image can be checked.");

        foreach (var dir in new[]
                 {
                     layout.Home, layout.CacheHome, layout.PluginDirectory, layout.TrustStoreDirectory,
                     Path.GetDirectoryName(layout.NotationPath)!,
                 })
            Directory.CreateDirectory(dir);

        // Rewritten on EVERY invocation, not only on a cold start: /tmp survives between invocations of
        // a warm environment, and a trust policy left over from a previous configuration must not be
        // what the next deploy is checked against.
        Executable(Path.Combine(_packageRoot, NotationLayout.PackageFolder, NotationLayout.NotationFile), layout.NotationPath);
        Executable(Path.Combine(_packageRoot, NotationLayout.PackageFolder, NotationLayout.PluginFile), layout.PluginPath);
        File.Copy(
            Path.Combine(_packageRoot, NotationLayout.PackageFolder, NotationLayout.RootCertificateFile),
            $"{layout.TrustStoreDirectory}/{NotationLayout.RootCertificateFile}",
            overwrite: true);
        File.WriteAllText(layout.TrustPolicyPath, trustPolicyJson);

        return Task.FromResult<string?>(null);
    }

    public async Task<(int ExitCode, string Stdout, string Stderr)> VerifyAsync(
        NotationLayout layout, string reference, IReadOnlyDictionary<string, string> environment)
    {
        var psi = new ProcessStartInfo(layout.NotationPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("verify");
        psi.ArgumentList.Add(reference);

        // The inherited environment stays — the Signer plugin needs the function's AWS credentials to
        // check revocation — and the layout's variables are laid over it. The password travels in the
        // environment, never in the argument list, and is never logged.
        foreach (var (name, value) in environment)
            psi.Environment[name] = value;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("notation could not be started.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(VerifyTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, await stdout, $"notation did not finish within {VerifyTimeout.TotalSeconds:0} seconds. {await stderr}");
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    private static void Executable(string source, string destination)
    {
        File.Copy(source, destination, overwrite: true);
        File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
