using System.Text;
using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Aws.Shared;
using Lz.Aws.Webapp;

namespace Lz.Aws.Ecs;

/// <summary>
/// Outcome of updating a single CloudFront Function.
/// </summary>
public enum EdgeUpdateOutcome
{
    /// <summary>Code was published (or would be, in dry-run).</summary>
    Updated,
    /// <summary>Live code already matches the repo source — nothing to do.</summary>
    Skipped,
    /// <summary>No matching CloudFront Function found for the tenant.</summary>
    NotFound,
    /// <summary>An error occurred while updating this function.</summary>
    Failed,
}

/// <summary>Per-function result for reporting.</summary>
public record EdgeFunctionResult(
    string FunctionType,
    string? FunctionName,
    EdgeUpdateOutcome Outcome,
    string? Detail = null);

/// <summary>
/// Updates a tenant's CloudFront Functions in place, entirely via the AWS SDK
/// (no Pulumi). This mirrors <see cref="AwsParkManager"/>: the edge is changed
/// imperatively so Pulumi state stays clean and a later <c>deploytenant</c>
/// re-publishes the identical code (both read the same <c>CloudFront/*.js</c>
/// source), so there is no drift.
///
/// Why this exists: <c>deploytenant</c> picks up CloudFront-function edits, but
/// only by running a full Pulumi up that scales ECS services to 0 first
/// (see AwsEcsTenantServiceComponent.desiredCount) — a service-interruption
/// window. A CloudFront Function code change is natively in-place
/// (UpdateFunction → PublishFunction), so <c>lz updateedge</c> applies it with
/// zero downtime and no container restart.
///
/// The three functions and how their code is prepared — kept byte-identical to
/// AwsCloudFrontComponent so the published result matches a full deploy:
///   • CFViewerRequest.js  → viewer-request on the default behavior.
///       Minified + validated via CfFunctionCodePrep, with the same five
///       template substitutions (${RootDomainParameter}, ${LegacyDomainsJson},
///       ${KvsId}, ${ExploreBucketDomain}, ${ParkBucketDomain}).
///   • CFViewerResponse.js → viewer-response (optional). Minified + validated,
///       no substitutions.
///   • CFExploreRewrite.js → viewer-request on /explore/* (optional). Read raw
///       (File.ReadAllText) — matches the component, which does not minify it.
/// </summary>
public class AwsEdgeUpdater
{
    private readonly string _systemKey;
    private readonly string _profile;
    private readonly string _region;

    /// <summary>Logical function types this updater knows how to publish.</summary>
    public static readonly string[] FunctionTypes =
        { "viewer-request", "viewer-response", "explore-rewrite" };

    public AwsEdgeUpdater(string systemKey, string profile, string region)
    {
        _systemKey = systemKey;
        _profile = profile;
        _region = region;
    }

    private sealed record FnDef(string Type, string JsFileName, string NameSuffix, bool UsePrep);

    private static readonly FnDef[] _defs =
    {
        new("viewer-request",  "CFViewerRequest.js",  "-viewer-request",  true),
        new("viewer-response", "CFViewerResponse.js", "-viewer-response", true),
        new("explore-rewrite", "CFExploreRewrite.js", "-explore-rewrite", false),
    };

    /// <summary>
    /// Publish the tenant's CloudFront Functions from its repo source files.
    /// </summary>
    /// <param name="functionFilter">
    /// Optional single function type ("viewer-request" | "viewer-response" |
    /// "explore-rewrite"); null updates all present functions.
    /// </param>
    public async Task<List<EdgeFunctionResult>> UpdateAsync(
        string tenantKey,
        string tenantSuffix,
        string env,
        string domain,
        string configDirectory,
        List<string>? legacyDomains,
        string? functionFilter,
        bool dryRun,
        CancellationToken ct)
    {
        var prefix = $"{_systemKey}-{tenantKey}";
        var cfDir = Path.Combine(configDirectory, "CloudFront");
        var results = new List<EdgeFunctionResult>();

        var defs = _defs
            .Where(d => functionFilter == null
                || d.Type.Equals(functionFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (defs.Count == 0)
        {
            throw new ArgumentException(
                $"Unknown --function '{functionFilter}'. " +
                $"Valid values: {string.Join(", ", FunctionTypes)}.");
        }

        // Keep only function types whose source file is present in the repo.
        var present = defs
            .Where(d => File.Exists(Path.Combine(cfDir, d.JsFileName)))
            .ToList();

        foreach (var missing in defs.Except(present))
        {
            results.Add(new EdgeFunctionResult(missing.Type, null,
                EdgeUpdateOutcome.Skipped,
                $"no {missing.JsFileName} in {cfDir}"));
        }

        if (present.Count == 0)
            return results;

        using var cf = CreateCloudFrontClient();

        // Enumerate LIVE functions once (paginated) for name-prefix matching.
        // CloudFront Functions are Pulumi-auto-named ("{sk}-{tk}-viewer-request-<hex>"),
        // so we match on the logical-name prefix rather than an exact name.
        var liveFns = await ListAllFunctionsAsync(cf, ct);

        // Resolve viewer-request substitution params lazily — only if needed,
        // since they require extra API calls (distribution + KVS lookups).
        string? exploreBucketDomain = null;
        string? parkBucketDomain = null;
        string? kvsUuid = null;
        var needSubs = present.Any(d => d.Type == "viewer-request");
        if (needSubs)
        {
            (exploreBucketDomain, parkBucketDomain) = await ResolveBucketDomainsAsync(cf, domain, ct);
            kvsUuid = await ResolveKvsUuidAsync(cf, prefix, ct);
        }

        var legacyDomainsJson = legacyDomains is { Count: > 0 }
            ? "[" + string.Join(",", legacyDomains.Select(d => $"\"{d}\"")) + "]"
            : "[]";

        foreach (var d in present)
        {
            try
            {
                var jsPath = Path.Combine(cfDir, d.JsFileName);

                // Prepare code exactly as AwsCloudFrontComponent does.
                string code;
                if (d.Type == "viewer-request")
                {
                    code = CfFunctionCodePrep.PrepareAndValidate(
                        jsPath, d.JsFileName,
                        ("${RootDomainParameter}", domain),
                        ("${LegacyDomainsJson}", legacyDomainsJson),
                        ("${KvsId}", kvsUuid ?? ""),
                        ("${ExploreBucketDomain}", exploreBucketDomain ?? ""),
                        ("${ParkBucketDomain}", parkBucketDomain ?? ""));
                }
                else if (d.UsePrep)
                {
                    code = CfFunctionCodePrep.PrepareAndValidate(jsPath, d.JsFileName);
                }
                else
                {
                    // explore-rewrite: raw read, matching the component (no minify).
                    code = File.ReadAllText(jsPath);
                }

                var fnName = liveFns
                    .FirstOrDefault(f => f.Name.StartsWith(prefix + d.NameSuffix, StringComparison.Ordinal))
                    ?.Name;

                if (fnName == null)
                {
                    results.Add(new EdgeFunctionResult(d.Type, null,
                        EdgeUpdateOutcome.NotFound,
                        $"no '{prefix}{d.NameSuffix}*' function (has deploytenant run?)"));
                    continue;
                }

                // Skip if the LIVE code already matches what we'd publish.
                var liveCode = await GetLiveCodeAsync(cf, fnName, ct);
                if (string.Equals(liveCode, code, StringComparison.Ordinal))
                {
                    results.Add(new EdgeFunctionResult(d.Type, fnName,
                        EdgeUpdateOutcome.Skipped, "live code already current"));
                    continue;
                }

                if (dryRun)
                {
                    results.Add(new EdgeFunctionResult(d.Type, fnName,
                        EdgeUpdateOutcome.Updated, "would publish (dry-run)"));
                    continue;
                }

                await PublishFunctionAsync(cf, fnName, code, ct);
                results.Add(new EdgeFunctionResult(d.Type, fnName, EdgeUpdateOutcome.Updated));
            }
            catch (Exception ex)
            {
                results.Add(new EdgeFunctionResult(d.Type, null,
                    EdgeUpdateOutcome.Failed, ex.Message));
            }
        }

        return results;
    }

    // ---------------------------------------------------------------
    // CloudFront Function update sequence
    // ---------------------------------------------------------------

    /// <summary>
    /// Update the DEVELOPMENT stage with new code, then publish to LIVE.
    /// The existing FunctionConfig (Runtime, Comment, KeyValueStore association)
    /// is preserved — only the code changes.
    /// </summary>
    private static async Task PublishFunctionAsync(
        AmazonCloudFrontClient cf, string name, string code, CancellationToken ct)
    {
        var describe = await cf.DescribeFunctionAsync(
            new DescribeFunctionRequest { Name = name }, ct);

        var update = await cf.UpdateFunctionAsync(new UpdateFunctionRequest
        {
            Name = name,
            IfMatch = describe.ETag,
            FunctionConfig = describe.FunctionSummary.FunctionConfig,
            FunctionCode = new MemoryStream(Encoding.UTF8.GetBytes(code)),
        }, ct);

        await cf.PublishFunctionAsync(new PublishFunctionRequest
        {
            Name = name,
            IfMatch = update.ETag,
        }, ct);
    }

    private static async Task<string> GetLiveCodeAsync(
        AmazonCloudFrontClient cf, string name, CancellationToken ct)
    {
        var resp = await cf.GetFunctionAsync(new GetFunctionRequest
        {
            Name = name,
            Stage = FunctionStage.LIVE,
        }, ct);
        return Encoding.UTF8.GetString(resp.FunctionCode.ToArray());
    }

    private static async Task<List<FunctionSummary>> ListAllFunctionsAsync(
        AmazonCloudFrontClient cf, CancellationToken ct)
    {
        var all = new List<FunctionSummary>();
        string? marker = null;
        do
        {
            var resp = await cf.ListFunctionsAsync(new ListFunctionsRequest
            {
                Stage = FunctionStage.LIVE,
                Marker = marker,
            }, ct);
            if (resp.FunctionList?.Items != null)
                all.AddRange(resp.FunctionList.Items);
            marker = resp.FunctionList?.NextMarker;
        } while (!string.IsNullOrEmpty(marker));
        return all;
    }

    // ---------------------------------------------------------------
    // Substitution-parameter resolution (byte-exact to the Pulumi component)
    // ---------------------------------------------------------------

    /// <summary>
    /// Read the explore/park bucket regional domain names from the live
    /// distribution's origins (ids "s3-explore"/"s3-park"). Reading them off the
    /// distribution guarantees the substituted values match exactly what
    /// AwsCloudFrontComponent injected (BucketRegionalDomainName).
    /// </summary>
    private async Task<(string explore, string park)> ResolveBucketDomainsAsync(
        AmazonCloudFrontClient cf, string domain, CancellationToken ct)
    {
        var distId = await WebappDeployer.FindDistributionIdAsync(domain, _profile, _region);
        if (string.IsNullOrEmpty(distId))
            throw new InvalidOperationException(
                $"No CloudFront distribution found for '{domain}'. Has deploytenant run?");

        var cfg = await cf.GetDistributionConfigAsync(
            new GetDistributionConfigRequest { Id = distId }, ct);
        var origins = cfg.DistributionConfig.Origins.Items;

        var explore = origins.FirstOrDefault(o => o.Id == "s3-explore")?.DomainName;
        var park = origins.FirstOrDefault(o => o.Id == "s3-park")?.DomainName;
        if (explore == null || park == null)
            throw new InvalidOperationException(
                $"Distribution {distId} is missing s3-explore/s3-park origins.");

        return (explore, park);
    }

    /// <summary>
    /// Find the tenant's KeyValueStore UUID (cf.kvs() needs the UUID, not the
    /// full ARN). KVS name convention: "{sk}-{tk}-kvs".
    /// </summary>
    private async Task<string> ResolveKvsUuidAsync(
        AmazonCloudFrontClient cf, string prefix, CancellationToken ct)
    {
        var kvsName = $"{prefix}-kvs";
        var list = await cf.ListKeyValueStoresAsync(new ListKeyValueStoresRequest(), ct);
        var arn = list.KeyValueStoreList?.Items?
            .FirstOrDefault(k => k.Name == kvsName)?.ARN;
        if (string.IsNullOrEmpty(arn))
            throw new InvalidOperationException(
                $"CloudFront KeyValueStore '{kvsName}' not found. Has deploytenant run?");
        return arn.Contains('/') ? arn.Split('/').Last() : arn;
    }

    // ---------------------------------------------------------------
    // Client / credentials (mirrors AwsParkManager)
    // ---------------------------------------------------------------

    private AmazonCloudFrontClient CreateCloudFrontClient()
    {
        var creds = GetCredentials();
        var region = Amazon.RegionEndpoint.GetBySystemName(_region);
        return creds != null
            ? new AmazonCloudFrontClient(creds, region)
            : new AmazonCloudFrontClient(region);
    }

    private Amazon.Runtime.AWSCredentials? GetCredentials()
    {
        var chain = new CredentialProfileStoreChain();
        chain.TryGetAWSCredentials(_profile, out var credentials);
        return credentials;
    }
}
