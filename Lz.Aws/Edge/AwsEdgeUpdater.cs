using System.Text;
using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Aws.Shared;
using Lz.Aws.Webapp;
using Lz.Core.Config;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Ops;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Edge;

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
/// only by running a full Pulumi up that scales ECS services to 0 first — a
/// service-interruption window. A CloudFront Function code change is natively
/// in-place (UpdateFunction → PublishFunction), so <c>lz updateedge</c> applies
/// it with zero downtime and no container restart.
///
/// The three functions and how their code is prepared — byte-identical to
/// AwsCloudFrontKvsComponent so the published result matches a full deploy:
///   • CFRequest.js  → viewer-request on the default behavior.
///       Minified + validated via CfFunctionCodePrep. Substitution: ${KvsArn}.
///       CloudFront function name prefix: {sk}-{tk}-request-fn.
///   • CFResponse.js → viewer-response (CORS). Minified + validated.
///       Substitutions: __ALLOW_LOCALHOST_DEV__ / __ALLOWED_ORIGINS_JSON__
///       from the tenant's CDN.Cors config. Name prefix: {sk}-{tk}-response-fn.
///   • CFExplore.js  → viewer-request on /explore* ordered behavior (optional).
///       Minified + validated; no template substitutions (uses cf.kvs() directly).
///       Name prefix: {sk}-{tk}-explore-fn.
/// </summary>
public class AwsEdgeUpdater
{
    private readonly string _systemKey;
    private readonly string _profile;
    private readonly string _region;

    /// <summary>Logical function types this updater knows how to publish.</summary>
    public static readonly string[] FunctionTypes =
        { "viewer-request", "viewer-response", "explore-rewrite", "auth", "authconfig", "auth-callback" };

    public AwsEdgeUpdater(string systemKey, string profile, string region)
    {
        _systemKey = systemKey;
        _profile = profile;
        _region = region;
    }

    private sealed record FnDef(string Type, string JsFileName, string NameSuffix);

    private static readonly FnDef[] _defs =
    {
        new("viewer-request",  "CFRequest.js",  "-request-fn"),
        new("viewer-response", "CFResponse.js", "-response-fn"),
        new("explore-rewrite", "CFExplore.js",  "-explore-fn"),
        // OIDC-facade functions (attached to /auth/* and /authentication/*).
        // CFAuth + CFAuthConfig take the ${KvsArn} substitution like CFRequest;
        // CFAuthCallback is a simple function with no substitution.
        new("auth",            "CFAuth.js",         "-auth-fn"),
        new("authconfig",      "CFAuthConfig.js",   "-authconfig-fn"),
        new("auth-callback",   "CFAuthCallback.js", "-auth-callback-fn"),
    };

    /// <summary>
    /// Publish the tenant's CloudFront Functions from its repo source files.
    /// </summary>
    /// <param name="functionFilter">
    /// Optional single function type ("viewer-request" | "viewer-response" |
    /// "explore-rewrite"); null updates all present functions.
    /// </param>
    /// <param name="corsConfig">
    /// CORS settings from the tenant config — used to reproduce the
    /// CFResponse.js substitutions exactly as the Pulumi component does.
    /// If null, viewer-response is prepared with AllowLocalhostDev=false
    /// and no AllowedOrigins (production-safe defaults).
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
        CancellationToken ct,
        CorsConfig? corsConfig = null)
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
        // CloudFront Functions are Pulumi-auto-named ("{sk}-{tk}-request-fn-<hex>"),
        // so we match on the logical-name prefix rather than an exact name.
        var liveFns = await ListAllFunctionsAsync(cf, ct);

        // Resolve KVS ARN lazily — needed for CFRequest/CFExplore/CFAuth/CFAuthConfig.
        var needKvsArn = present.Any(d => d.Type is "viewer-request" or "explore-rewrite" or "auth" or "authconfig");
        string? kvsArn = null;
        if (needKvsArn)
            kvsArn = await ResolveKvsArnAsync(cf, prefix, ct);

        // CORS substitution values for CFResponse.js.
        var allowLocalhostJs = (corsConfig?.AllowLocalhostDev ?? false) ? "true" : "false";
        var allowedOriginsJson = System.Text.Json.JsonSerializer.Serialize(
            corsConfig?.AllowedOrigins ?? new List<string>());

        foreach (var d in present)
        {
            try
            {
                var jsPath = Path.Combine(cfDir, d.JsFileName);

                // Prepare code exactly as AwsCloudFrontKvsComponent does.
                string code;
                if (d.Type is "viewer-request" or "explore-rewrite" or "auth" or "authconfig")
                {
                    // CFRequest/CFAuth/CFAuthConfig substitute ${KvsArn}; CFExplore
                    // has no ${KvsArn} placeholder so the substitution is a no-op.
                    // All are created via CreateFunctionFromFile (minified), so
                    // PrepareAndValidate matches the component byte-for-byte.
                    code = CfFunctionCodePrep.PrepareAndValidate(
                        jsPath, d.JsFileName,
                        ("${KvsArn}", kvsArn ?? ""));
                }
                else if (d.Type == "viewer-response")
                {
                    code = CfFunctionCodePrep.PrepareAndValidate(
                        jsPath, d.JsFileName,
                        ("__ALLOW_LOCALHOST_DEV__", allowLocalhostJs),
                        ("__ALLOWED_ORIGINS_JSON__", allowedOriginsJson));
                }
                else
                {
                    // auth-callback: simple function, minified, no substitutions
                    // (matches CreateSimpleFunctionFromFile in the component).
                    code = CfFunctionCodePrep.PrepareAndValidate(jsPath, d.JsFileName);
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
    /// Find the tenant's KeyValueStore ARN. KVS name convention: "{sk}-{tk}-kvs".
    /// </summary>
    private async Task<string> ResolveKvsArnAsync(
        AmazonCloudFrontClient cf, string prefix, CancellationToken ct)
    {
        var kvsName = $"{prefix}-kvs";
        var list = await cf.ListKeyValueStoresAsync(new ListKeyValueStoresRequest(), ct);
        var arn = list.KeyValueStoreList?.Items?
            .FirstOrDefault(k => k.Name == kvsName)?.ARN;
        if (string.IsNullOrEmpty(arn))
            throw new InvalidOperationException(
                $"CloudFront KeyValueStore '{kvsName}' not found. Has deploytenant run?");
        return arn;
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
