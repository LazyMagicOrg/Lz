using System.Diagnostics;
using System.Runtime.InteropServices;
using Lz.Core.Config;

namespace Lz.Aws.Docker;

/// <summary>
/// Builds Docker images and pushes them to AWS ECR.
/// Uses docker and aws CLI tools via process execution.
/// </summary>
public class EcrDeployer
{
    /// <summary>
    /// Build a Docker image and push it to an ECR repository.
    /// </summary>
    public async Task DeployAsync(
        string serviceName,
        ContainerDefinition container,
        string configDirectory,
        string ecrRepoName,
        string profile,
        string region,
        string tag)
    {
        // Resolve paths
        var contextPath = Path.GetFullPath(Path.Combine(configDirectory, container.Context));
        var dockerfilePath = Path.Combine(contextPath, container.Dockerfile);

        if (!Directory.Exists(contextPath))
            throw new DirectoryNotFoundException($"Docker build context not found: {contextPath}");
        if (!File.Exists(dockerfilePath))
            throw new FileNotFoundException($"Dockerfile not found: {dockerfilePath}");

        var localImage = $"{serviceName}:latest";

        Console.WriteLine($"  ECR repo:    {ecrRepoName}");
        Console.WriteLine($"  Context:     {contextPath}");
        Console.WriteLine($"  Dockerfile:  {dockerfilePath}");
        Console.WriteLine();

        // 1. Get AWS account ID
        var accountId = await GetAccountIdAsync(profile, region);
        var registryUri = $"{accountId}.dkr.ecr.{region}.amazonaws.com";
        var imageUri = $"{registryUri}/{ecrRepoName}:{tag}";

        Console.WriteLine($"  Image URI:   {imageUri}");
        Console.WriteLine();

        // 2. ECR login
        Console.WriteLine("Authenticating with ECR...");
        await EcrLoginAsync(profile, region, registryUri);

        // 3. Ensure ECR repo exists
        await EnsureEcrRepoAsync(profile, region, ecrRepoName);

        // 4. Docker build
        Console.WriteLine($"Building Docker image '{serviceName}'...");
        await DockerBuildAsync(contextPath, dockerfilePath, localImage, container.BuildArgs);

        // 5. Tag and push
        Console.WriteLine($"Tagging image as {imageUri}...");
        await RunAsync("docker", $"tag {localImage} {imageUri}");

        Console.WriteLine($"Pushing to ECR...");
        await RunAsync("docker", $"push {imageUri}");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully pushed {serviceName} → {imageUri}");
        Console.ResetColor();
        Console.WriteLine();
    }

    private async Task<string> GetAccountIdAsync(string profile, string region)
    {
        var result = await RunCaptureAsync("aws",
            $"sts get-caller-identity --profile {profile} --region {region} --query Account --output text");
        return result.Trim();
    }

    private async Task EcrLoginAsync(string profile, string region, string registryUri)
    {
        // Get ECR login password and pipe to docker login
        var password = await RunCaptureAsync("aws",
            $"ecr get-login-password --profile {profile} --region {region}");

        await RunWithStdinAsync("docker",
            $"login --username AWS --password-stdin {registryUri}",
            password.Trim());
    }

    private async Task EnsureEcrRepoAsync(string profile, string region, string repoName)
    {
        // Check if repo exists
        var exitCode = await RunSilentAsync("aws",
            $"ecr describe-repositories --profile {profile} --region {region} --repository-names {repoName}");

        if (exitCode != 0)
        {
            Console.WriteLine($"  Creating ECR repository '{repoName}'...");
            await RunAsync("aws",
                $"ecr create-repository --profile {profile} --region {region} " +
                $"--repository-name {repoName} " +
                $"--image-scanning-configuration scanOnPush=true");
        }
    }

    private async Task DockerBuildAsync(
        string contextPath, string dockerfilePath,
        string imageName, Dictionary<string, string>? buildArgs)
    {
        var buildArgStr = "";
        if (buildArgs != null)
        {
            foreach (var (key, value) in buildArgs)
                buildArgStr += $" --build-arg {key}={value}";
        }

        // Use buildx on non-x86_64 platforms (e.g., macOS ARM)
        var needsBuildx = !RuntimeInformation.OSArchitecture.ToString()
            .Contains("X64", StringComparison.OrdinalIgnoreCase);

        if (needsBuildx)
        {
            Console.WriteLine("  (Cross-platform build via buildx → linux/amd64)");
            await RunAsync("docker",
                $"buildx build --platform linux/amd64 -f \"{dockerfilePath}\"{buildArgStr} -t {imageName} --load \"{contextPath}\"");
        }
        else
        {
            await RunAsync("docker",
                $"build -f \"{dockerfilePath}\"{buildArgStr} -t {imageName} \"{contextPath}\"");
        }
    }

    /// <summary>
    /// Check whether an ECR repository exists and has at least one image.
    /// Returns true if the repo exists and contains images, false otherwise.
    /// </summary>
    public static async Task<bool> CheckEcrImageExistsAsync(
        string profile, string region, string repoName)
    {
        try
        {
            // list-images returns imageIds array; empty if no images pushed yet
            var output = await RunCaptureAsync("aws",
                $"ecr list-images --profile {profile} --region {region} " +
                $"--repository-name {repoName} --max-items 1 --query \"imageIds[0]\" --output text");

            // If there are images, output will be something like "sha256:abc123\tlatest"
            // If empty, output will be "None"
            return !string.IsNullOrWhiteSpace(output) &&
                   !output.Trim().Equals("None", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Repository doesn't exist or CLI error
            return false;
        }
    }

    // ---------------------------------------------------------------
    // Process helpers
    // ---------------------------------------------------------------

    /// <summary>Run a command, streaming stdout/stderr to the console. Throws on non-zero exit.</summary>
    private static async Task RunAsync(string command, string args)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {command} {args}");

        // Stream output in parallel
        var stdoutTask = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                Console.WriteLine(line);
        });
        var stderrTask = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Error.WriteLine(line);
                Console.ResetColor();
            }
        });

        await process.WaitForExitAsync();
        await Task.WhenAll(stdoutTask, stderrTask);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed (exit {process.ExitCode}): {command} {args}");
    }

    /// <summary>Run a command, capture stdout. Throws on non-zero exit.</summary>
    private static async Task<string> RunCaptureAsync(string command, string args)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {command} {args}");

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed (exit {process.ExitCode}): {command} {args}\n{stderr}");

        return stdout;
    }

    /// <summary>Run a command with stdin input. Throws on non-zero exit.</summary>
    private static async Task RunWithStdinAsync(string command, string args, string stdin)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {command} {args}");

        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Command failed (exit {process.ExitCode}): {command} {args}\n{stderr}");

        // Print docker login success message if present
        if (!string.IsNullOrWhiteSpace(stdout))
            Console.WriteLine($"  {stdout.Trim()}");
    }

    /// <summary>Run a command silently, return exit code (no throw).</summary>
    private static async Task<int> RunSilentAsync(string command, string args)
    {
        var psi = new ProcessStartInfo(command, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start: {command} {args}");

        await process.StandardOutput.ReadToEndAsync();
        await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return process.ExitCode;
    }
}
