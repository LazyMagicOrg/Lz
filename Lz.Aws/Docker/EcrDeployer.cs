using System.Diagnostics;
using System.Runtime.InteropServices;
using Lz.Core.Config;
using Lz.Core.Docker;

namespace Lz.Aws.Docker;

/// <summary>
/// Builds Docker images and pushes them to AWS ECR.
/// Uses docker and aws CLI tools via process execution.
/// </summary>
public class EcrDeployer
{
    /// <summary>
    /// Build a Docker image and push it to an ECR repository.
    /// <paramref name="untaggedImageRetentionDays"/> (Hygiene opt-in): when set,
    /// an ECR lifecycle policy expiring UNTAGGED images older than that many
    /// days is ensured on the repository; null = no policy is written — the
    /// pre-existing baseline (every push of a reused tag orphans the prior
    /// digest, which then accrues storage forever).
    /// <paramref name="buildTagRetentionCount"/> (Hygiene opt-in): when set, an
    /// immutable <c>b-…</c> tag is pushed alongside <paramref name="tag"/> and the
    /// newest N such images are retained by a second lifecycle rule. This is what
    /// keeps a rollback target alive: with only a moving <c>:latest</c>, each push
    /// orphans its predecessor and the untagged rule deletes it, so after one
    /// quiet retention window the repository holds one image and nothing to roll
    /// back to.
    /// </summary>
    public async Task DeployAsync(
        string serviceName,
        ContainerDefinition container,
        string configDirectory,
        string ecrRepoName,
        string profile,
        string region,
        string tag,
        int? untaggedImageRetentionDays = null,
        int? buildTagRetentionCount = null)
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

        // 3b. Hygiene opt-in: cap image growth with a lifecycle policy. Both rules are
        // written together so the policy is always internally consistent — see
        // EnsureLifecyclePolicyAsync for why they are one decision and not two.
        if (untaggedImageRetentionDays is int || buildTagRetentionCount is int)
            await EnsureLifecyclePolicyAsync(
                profile, region, ecrRepoName, untaggedImageRetentionDays, buildTagRetentionCount);

        // 4. Sync local NuGet packages into build context (if configured)
        if (container.SyncPackages)
        {
            // Determine the project to restore from the BuildArgs or convention
            var projectRelPath = container.BuildArgs?.TryGetValue("ContainerName", out var cn) == true
                ? $"Containers/{cn}/{cn}.csproj"
                : $"Containers/{serviceName}/{serviceName}.csproj";

            await DockerPackageSyncer.SyncAsync(contextPath, projectRelPath);
        }

        // 5. Docker build
        Console.WriteLine($"Building Docker image '{serviceName}'...");
        await DockerBuildAsync(contextPath, dockerfilePath, localImage, container.BuildArgs, container.Platform);

        // 6. Tag and push
        Console.WriteLine($"Tagging image as {imageUri}...");
        await RunAsync("docker", $"tag {localImage} {imageUri}");

        Console.WriteLine($"Pushing to ECR...");
        await RunAsync("docker", $"push {imageUri}");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully pushed {serviceName} → {imageUri}");
        Console.ResetColor();

        // 6b. Hygiene opt-in: a second, IMMUTABLE tag on the same digest. `tag` above is
        // a moving pointer (`:latest` by convention), so the digest it named a moment ago
        // is now untagged and on the untagged-expiry clock. This tag is what keeps that
        // digest addressable as a rollback target, and it carries the only provenance the
        // image has. Pushing an already-pushed digest under a second tag uploads no
        // layers — it registers a manifest, so the cost is one API round trip.
        if (buildTagRetentionCount is int)
        {
            var buildTag = BuildTag(contextPath);
            var buildUri = $"{registryUri}/{ecrRepoName}:{buildTag}";
            Console.WriteLine($"Applying immutable build tag {buildTag}...");
            await RunAsync("docker", $"tag {localImage} {buildUri}");
            await RunAsync("docker", $"push {buildUri}");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  Rollback target: {buildUri}");
            Console.ResetColor();
        }

        // Prune dangling images and build cache to prevent VHDX bloat
        Console.WriteLine("Pruning unused Docker images and build cache...");
        await RunSilentAsync("docker", "image prune -a --force");
        await RunSilentAsync("docker", "builder prune --force --keep-storage 10GB");
        Console.WriteLine("  Prune complete.");
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
        string imageName, Dictionary<string, string>? buildArgs,
        string platform = "linux/amd64")
    {
        var buildArgStr = "";
        if (buildArgs != null)
        {
            foreach (var (key, value) in buildArgs)
                buildArgStr += $" --build-arg {key}={value}";
        }

        // Always build via buildx with attestations DISABLED and a single target
        // platform. BuildKit's default provenance/SBOM attestations turn the push
        // into an OCI image *index* carrying an `attestation-manifest`; AWS Lambda's
        // CreateFunction rejects that ("image manifest, config or layer media type
        // ... is not supported"), even though ECS/Fargate accepts it. A plain
        // `docker build` on modern Docker Desktop produces exactly that index, so we
        // use `buildx --provenance=false --sbom=false --platform <target>`, which
        // yields a single-platform image manifest that BOTH Lambda and Fargate accept.
        // (The same image serves both the lambda-* and ecs-* topologies.) The target
        // comes from ContainerDefinition.Platform (default linux/amd64; linux/arm64
        // pairs with LambdaOptions.Architecture=arm64 and cross-builds under QEMU
        // on an x86 host — slower, but the pushed manifest is what matters).
        Console.WriteLine($"  (buildx → {platform}, attestations off for Lambda compatibility)");
        await RunAsync("docker",
            $"buildx build --no-cache --provenance=false --sbom=false --platform {platform} " +
            $"-f \"{dockerfilePath}\"{buildArgStr} -t {imageName} --load \"{contextPath}\"");
    }

    /// <summary>The prefix every immutable build tag carries; also the lifecycle rule's filter.</summary>
    private const string BuildTagPrefix = "b-";

    /// <summary>
    /// The immutable per-push tag: <c>b-{yyyyMMdd-HHmmss}</c> in UTC, plus
    /// <c>-g{sha}</c> when <paramref name="contextPath"/> resolves to a git commit
    /// and <c>-dirty</c> when that tree has uncommitted changes.
    /// <para>
    /// The timestamp — not the sha — is what makes the tag unique: two pushes of
    /// the same commit are two different images (the Dockerfile uses floating base
    /// tags and copies the operator's package cache), so a sha-only tag would
    /// collide and silently re-point. The <c>-dirty</c> marker matters for the same
    /// reason a version tool emits one: a sha on a modified tree names a commit that
    /// does not describe the bytes, and an unqualified sha would assert provenance
    /// the image does not have.
    /// </para>
    /// </summary>
    private static string BuildTag(string contextPath)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var sha = TryGitDescribe(contextPath);
        return sha is null ? $"{BuildTagPrefix}{stamp}" : $"{BuildTagPrefix}{stamp}-{sha}";
    }

    /// <summary>
    /// <c>g{shortSha}</c> for the commit at <paramref name="contextPath"/>, suffixed
    /// <c>-dirty</c> when the tree has uncommitted changes; null when the path is not
    /// a git working tree or git is unavailable. Best-effort by design — provenance is
    /// a bonus here, and a build must never fail because git is missing.
    /// </summary>
    private static string? TryGitDescribe(string contextPath)
    {
        try
        {
            var sha = RunCaptureAsync("git", $"-C \"{contextPath}\" rev-parse --short=8 HEAD")
                .GetAwaiter().GetResult().Trim();

            // Guard the shape rather than trusting the exit code: a non-repo path can
            // still print a message, and an ECR tag admits only [a-zA-Z0-9._-].
            if (sha.Length is < 4 or > 40 || !sha.All(char.IsAsciiLetterOrDigit))
                return null;

            var status = RunCaptureAsync("git", $"-C \"{contextPath}\" status --porcelain")
                .GetAwaiter().GetResult();
            return string.IsNullOrWhiteSpace(status) ? $"g{sha}" : $"g{sha}-dirty";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Idempotently apply the repository's lifecycle policy. Both rules are written in
    /// one call because a lifecycle policy is replaced wholesale, never merged — writing
    /// one rule would silently drop the other.
    /// <list type="bullet">
    ///   <item>Priority 1 — expire UNTAGGED images older than
    ///     <paramref name="untaggedRetentionDays"/>.</item>
    ///   <item>Priority 2 — of the images tagged <c>b-*</c>, retain the newest
    ///     <paramref name="buildTagRetentionCount"/> and expire the rest.</item>
    /// </list>
    /// The two rules select disjoint sets, so their relative priority does not affect
    /// the outcome; AWS documents that "an image is expired or archived by exactly one
    /// or zero rules" and that only one rule may select untagged images, which this
    /// policy satisfies. Rule 2 is what bounds growth once every push carries a durable
    /// tag and nothing becomes untagged any more.
    /// </summary>
    private async Task EnsureLifecyclePolicyAsync(
        string profile, string region, string repoName,
        int? untaggedRetentionDays, int? buildTagRetentionCount)
    {
        var rules = new List<string>();
        var described = new List<string>();

        if (untaggedRetentionDays is int days)
        {
            rules.Add(
                "{" +
                    "\"rulePriority\":1," +
                    "\"description\":\"lz hygiene: expire untagged images\"," +
                    "\"selection\":{" +
                        "\"tagStatus\":\"untagged\"," +
                        "\"countType\":\"sinceImagePushed\"," +
                        "\"countUnit\":\"days\"," +
                        $"\"countNumber\":{days}" +
                    "}," +
                    "\"action\":{\"type\":\"expire\"}" +
                "}");
            described.Add($"untagged > {days}d expire");
        }

        if (buildTagRetentionCount is int keep)
        {
            rules.Add(
                "{" +
                    "\"rulePriority\":2," +
                    "\"description\":\"lz hygiene: retain the newest build-tagged images\"," +
                    "\"selection\":{" +
                        "\"tagStatus\":\"tagged\"," +
                        $"\"tagPrefixList\":[\"{BuildTagPrefix}\"]," +
                        "\"countType\":\"imageCountMoreThan\"," +
                        $"\"countNumber\":{keep}" +
                    "}," +
                    "\"action\":{\"type\":\"expire\"}" +
                "}");
            described.Add($"keep newest {keep} {BuildTagPrefix}* images");
        }

        var policy = "{\"rules\":[" + string.Join(",", rules) + "]}";

        // JSON as a CLI arg survives quoting differently per OS shell; write to a
        // temp file and pass file:// which is quoting-proof on both.
        var tmp = Path.Combine(Path.GetTempPath(), $"ecr-lifecycle-{repoName}.json");
        await File.WriteAllTextAsync(tmp, policy);
        try
        {
            Console.WriteLine($"  Ensuring ECR lifecycle policy ({string.Join("; ", described)})...");
            await RunAsync("aws",
                $"ecr put-lifecycle-policy --profile {profile} --region {region} " +
                $"--repository-name {repoName} --lifecycle-policy-text file://{tmp}");
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* best-effort temp cleanup */ }
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

    /// <summary>
    /// Resolve the image digest of a specific tag in an ECR repository (e.g.
    /// the digest that <c>:latest</c> currently points at). Returns null if the
    /// repo or tag doesn't exist. Used by <c>lz updatecontainer</c> to decide
    /// whether a running service is already on the newest image.
    /// </summary>
    public static async Task<string?> GetImageDigestAsync(
        string profile, string region, string repoName, string tag)
    {
        try
        {
            var output = await RunCaptureAsync("aws",
                $"ecr describe-images --profile {profile} --region {region} " +
                $"--repository-name {repoName} --image-ids imageTag={tag} " +
                $"--query \"imageDetails[0].imageDigest\" --output text");

            var digest = output.Trim();
            return string.IsNullOrWhiteSpace(digest) ||
                   digest.Equals("None", StringComparison.OrdinalIgnoreCase)
                ? null
                : digest;
        }
        catch
        {
            // Repository or tag not found, or CLI error.
            return null;
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
