using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Aws.Docker;
using Lz.Aws.Ecs;
using Task = System.Threading.Tasks.Task;

namespace Lz.Aws.Lambda;

/// <summary>
/// The <c>lz updatecontainer</c> implementation for the lambda-* topologies. Lambda
/// resolves a container image's DIGEST at <c>UpdateFunctionCode</c> time — pushing a
/// new <c>:latest</c> to ECR changes NOTHING for the function, and a tenant Pulumi
/// re-deploy sees the same <c>ImageUri</c> string and no-ops. So the ONLY way to
/// roll a new build onto the per-tenant function is an explicit
/// <c>UpdateFunctionCode</c> (before this class existed, that was a manual AWS CLI
/// step — found live 2026-07-30 while shipping a fix).
///
/// Semantics mirror <see cref="AwsContainerUpdater"/> (the ECS path) and reuse its
/// result types: compare the latest ECR digest for the tag against the function's
/// currently-resolved image digest; roll only when they differ (or --force); --wait
/// polls <c>LastUpdateStatus</c> to Successful/Failed.
/// </summary>
public class AwsLambdaContainerUpdater
{
    private readonly string _profile;
    private readonly string _region;
    private readonly AmazonLambdaClient _lambda;

    private static readonly TimeSpan WaitTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public AwsLambdaContainerUpdater(string profile, string region)
    {
        _profile = profile;
        _region = region;
        _lambda = CreateClient(region, profile);
    }

    /// <summary>Does <paramref name="topology"/> route container updates to Lambda functions?</summary>
    public static bool IsLambdaTopology(string? topology)
        => topology?.StartsWith("lambda-", StringComparison.Ordinal) == true;

    /// <summary>
    /// Digest ("sha256:…") from a Lambda <c>Code.ResolvedImageUri</c>
    /// (<c>{repo-uri}@sha256:…</c>); null when absent/unparsable.
    /// </summary>
    public static string? ExtractDigest(string? resolvedImageUri)
    {
        if (string.IsNullOrEmpty(resolvedImageUri)) return null;
        var at = resolvedImageUri.IndexOf('@');
        return at > 0 && at < resolvedImageUri.Length - 1 ? resolvedImageUri[(at + 1)..] : null;
    }

    /// <summary>
    /// The configured image URI re-pointed at <paramref name="tag"/>
    /// (<c>{repo-uri}:{oldtag}</c> → <c>{repo-uri}:{tag}</c>). Preserves the
    /// account/region/repo exactly as deployed — never re-derives them.
    /// </summary>
    public static string? RetagImageUri(string? imageUri, string tag)
    {
        if (string.IsNullOrEmpty(imageUri)) return null;
        // The tag separator is the last ':' AFTER the last '/' (registry hosts carry no port here,
        // but guard anyway: a ':' before the final '/' is not a tag separator).
        var lastSlash = imageUri.LastIndexOf('/');
        var lastColon = imageUri.LastIndexOf(':');
        var baseUri = lastColon > lastSlash ? imageUri[..lastColon] : imageUri;
        return $"{baseUri}:{tag}";
    }

    /// <summary>Compare-and-(maybe)-roll a single tenant function.</summary>
    public async Task<ContainerUpdateResult> UpdateIfNewerAsync(
        string functionName, string ecrRepo, string tag,
        bool force, bool wait, bool dryRun, CancellationToken ct)
    {
        // 1. Latest digest available in ECR for the tag (same source as the ECS path).
        var ecrDigest = await EcrDeployer.GetImageDigestAsync(_profile, _region, ecrRepo, tag);
        if (string.IsNullOrEmpty(ecrDigest))
            return new(functionName, UpdateOutcome.NoEcrImage,
                $"no '{tag}' image in ECR repo {ecrRepo} — run 'lz deploycontainer' first");

        // 2. The digest the function is CURRENTLY running (resolved at its last code update).
        GetFunctionResponse fn;
        try
        {
            fn = await _lambda.GetFunctionAsync(new GetFunctionRequest { FunctionName = functionName }, ct);
        }
        catch (ResourceNotFoundException)
        {
            return new(functionName, UpdateOutcome.NoRunningTasks,
                "function not found — run 'lz deploytenant' to bring it up first");
        }
        var runningDigest = ExtractDigest(fn.Code?.ResolvedImageUri);

        // 3. Decide.
        if (!force && runningDigest != null
            && string.Equals(runningDigest, ecrDigest, StringComparison.Ordinal))
            return new(functionName, UpdateOutcome.UpToDate,
                $"function already runs {Short(ecrDigest)}");

        if (dryRun)
            return new(functionName, UpdateOutcome.WouldDeploy,
                $"would roll {Short(runningDigest)} → {Short(ecrDigest)}");

        // 4. Roll: UpdateFunctionCode with the deployed image URI re-pointed at the tag —
        //    Lambda re-resolves the digest even when the URI string is unchanged.
        var imageUri = RetagImageUri(fn.Code?.ImageUri, tag);
        if (string.IsNullOrEmpty(imageUri))
            return new(functionName, UpdateOutcome.Failed,
                "function has no ImageUri (not a container-image function?)");

        await _lambda.UpdateFunctionCodeAsync(new UpdateFunctionCodeRequest
        {
            FunctionName = functionName,
            ImageUri = imageUri,
        }, ct);

        if (!wait)
            return new(functionName, UpdateOutcome.Deployed,
                $"code update requested ({Short(runningDigest)} → {Short(ecrDigest)}); not waiting");

        // 5. --wait: poll LastUpdateStatus until Successful/Failed.
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var cfg = await _lambda.GetFunctionConfigurationAsync(
                new GetFunctionConfigurationRequest { FunctionName = functionName }, ct);
            var status = cfg.LastUpdateStatus;
            if (status == LastUpdateStatus.Successful)
                return new(functionName, UpdateOutcome.Verified,
                    $"rolled to {Short(ecrDigest)} (LastUpdateStatus=Successful)");
            if (status == LastUpdateStatus.Failed)
                return new(functionName, UpdateOutcome.Failed,
                    $"update failed: {cfg.LastUpdateStatusReason}");
            await Task.Delay(PollInterval, ct);
        }
        return new(functionName, UpdateOutcome.Failed,
            $"update did not reach Successful within {WaitTimeout.TotalMinutes:0} minutes");
    }

    private static string Short(string? digest)
        => string.IsNullOrEmpty(digest) ? "<none>"
           : digest.Length > 19 ? digest[..19] + "…" : digest;

    private static AmazonLambdaClient CreateClient(string region, string profile)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        if (!string.IsNullOrEmpty(profile))
        {
            var chain = new CredentialProfileStoreChain();
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                return new AmazonLambdaClient(credentials, regionEndpoint);
        }
        return new AmazonLambdaClient(regionEndpoint);
    }
}
