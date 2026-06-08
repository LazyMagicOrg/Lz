using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Amazon.Runtime.CredentialManagement;
using Lz.Core.Config;
using Lz.Aws.Webapp;
// Amazon.CloudFront.Model also defines a TenantConfig — alias to ours.
using TenantConfig = Lz.Core.Config.TenantConfig;

namespace Lz.Aws.Ecs;

/// <summary>
/// Publishes a tenant's runtime configuration to SSM Parameter Store
/// (<c>/{sk}/{tk}/{env}/tenantconfig</c>) out-of-band — i.e. without a full
/// <c>deploytenant</c>. The AppHost's SSM-backed refreshing configuration
/// provider picks up the change within its poll interval (~60s), so config
/// edits propagate with no container restart. See Service/Docs/DynamicConfig.md.
///
/// This mirrors the SSM upload that <c>AwsServicesPostDeployAction</c> performs
/// during <c>deploytenant</c> (same file, same placeholder substitution), so the
/// out-of-band path stays byte-identical to a full deploy.
///
/// The <c>/config</c> CloudFront behavior is currently <c>CachingDisabled</c>, so
/// an invalidation is not required for clients to see new values on their next
/// <c>/config</c> fetch. The optional invalidation exists for the case where
/// <c>/config</c> is later given a cache TTL.
/// </summary>
public static class AwsTenantConfigPublisher
{
    public static async Task<bool> PublishAsync(
        string monorepoRoot,
        SystemConfig config,
        string tenantKey,
        TenantConfig tenantConfig,
        bool invalidate,
        bool dryRun)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var profile = tenantConfig.Profile ?? config.Profile;
        var region = tenantConfig.Region ?? config.Region;

        var configFilename = $"tenantconfig.{sk}.{tenantKey}.{env}.yaml";
        var configSource = Path.Combine(monorepoRoot, configFilename);

        if (!File.Exists(configSource))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  No {configFilename} found at {monorepoRoot} — skipping.");
            Console.ResetColor();
            return false;
        }

        var yamlContent = await File.ReadAllTextAsync(configSource);

        // Replace self-referencing placeholders before upload — identical to
        // AwsServicesPostDeployAction.UploadTenantConfigToSsmAsync so the
        // out-of-band write matches what deploytenant would publish.
        yamlContent = yamlContent.Replace("<<rootdomain>>", tenantConfig.RootDomain);
        yamlContent = yamlContent.Replace("<<centralauthdomain>>", tenantConfig.CentralAuthDomain ?? "");

        var legacyDomain = tenantConfig.LegacyDomains?.FirstOrDefault();
        if (!string.IsNullOrEmpty(legacyDomain))
            yamlContent = yamlContent.Replace("<<legacydomain>>", legacyDomain);
        else
            yamlContent = string.Join("\n",
                yamlContent.Split('\n').Where(line => !line.Contains("<<legacydomain>>")));

        var paramName = $"/{sk}/{tenantKey}/{env}/tenantconfig";

        if (dryRun)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  [dry-run] would write {paramName} ({yamlContent.Length} bytes)" +
                              (invalidate ? " and invalidate /config" : ""));
            Console.ResetColor();
            return true;
        }

        await AwsAccountResolver.WriteSsmParameterAsync(
            profile, region, paramName, yamlContent,
            description: $"Tenant config for {sk}/{tenantKey}/{env}");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Wrote {paramName} ({yamlContent.Length} bytes). " +
                          "AppHost will pick it up within its poll interval (~60s).");
        Console.ResetColor();

        if (invalidate)
            await InvalidateConfigAsync(tenantConfig.RootDomain, profile, region);

        return true;
    }

    private static async Task InvalidateConfigAsync(string domain, string profile, string region)
    {
        var distributionId = await WebappDeployer.FindDistributionIdAsync(domain, profile, region);
        if (string.IsNullOrEmpty(distributionId))
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  No CloudFront distribution found for '{domain}' — skipping invalidation.");
            Console.ResetColor();
            return;
        }

        using var cf = CreateCloudFrontClient(profile, region);
        await cf.CreateInvalidationAsync(new CreateInvalidationRequest
        {
            DistributionId = distributionId,
            InvalidationBatch = new InvalidationBatch
            {
                // Stable caller reference derived from the path set — the runtime
                // forbids Date.Now-style nondeterminism elsewhere, but here a
                // simple unique-enough token is fine; CloudFront only requires it
                // be unique per concurrent batch.
                CallerReference = $"updateconfig-{domain}-{Guid.NewGuid():N}",
                Paths = new Paths { Quantity = 1, Items = new List<string> { "/config" } },
            },
        });

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Invalidated /config on {distributionId}.");
        Console.ResetColor();
    }

    private static AmazonCloudFrontClient CreateCloudFrontClient(string profile, string region)
    {
        var chain = new CredentialProfileStoreChain();
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        return chain.TryGetAWSCredentials(profile, out var creds)
            ? new AmazonCloudFrontClient(creds, endpoint)
            : new AmazonCloudFrontClient(endpoint);
    }
}
