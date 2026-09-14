using Amazon.CloudFront;
using Amazon.CloudFront.Model;
using Lz.Core.Config;
using Lz.Aws.Webapp;
// Amazon.CloudFront.Model also defines a TenantConfig — alias to ours.
using TenantConfig = Lz.Core.Config.TenantConfig;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Topologies;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Ops;

/// <summary>
/// Publishes a tenant's tenantconfig YAML to SSM Parameter Store
/// (<c>/{sk}/{tk}/{env}/tenantconfig</c>) out-of-band — i.e. without a full
/// <c>deploytenant</c>. It restarts nothing and checks nothing: a running service sees
/// the new value only if it reloads that parameter, which lz cannot observe.
///
/// This mirrors the SSM upload that <c>AwsServicesPostDeployAction</c> performs
/// during <c>deploytenant</c> on ecs-fargate-keycloak (same file, same placeholder
/// substitution), so the out-of-band path stays byte-identical to a full deploy. No
/// other topology's deploy writes the parameter, and <see cref="RefusalFor"/> refuses
/// there.
///
/// The <c>/config</c> CloudFront behavior is currently <c>CachingDisabled</c>, so
/// an invalidation is not required for clients to see new values on their next
/// <c>/config</c> fetch. The optional invalidation exists for the case where
/// <c>/config</c> is later given a cache TTL.
/// </summary>
public static class AwsTenantConfigPublisher
{
    /// <summary>
    /// Why <c>lz updateconfig</c> must not run for this system, or null when it may. Where the topology's deploy never
    /// writes <c>/{sk}/{tk}/{env}/tenantconfig</c> (<see cref="IAwsPlatformFactory.PublishesTenantConfigParameter"/>),
    /// no service reads it either — on ecs-fargate-cognito-dynamodb the tenant service's role may read only
    /// <c>/{sk}/{env}/*</c> — so the command wrote a parameter nothing would see, and reported success.
    /// </summary>
    public static string? RefusalFor(SystemConfig config, IAwsPlatformFactory platform)
    {
        if (platform.PublishesTenantConfigParameter) return null;

        var sk = config.SystemKey;
        var env = config.Environment;
        return $"{config.Topology} never publishes /{sk}/{{tenant}}/{env}/tenantconfig: no deploy on this topology writes that " +
               $"parameter and no service reads it (the tenant service's role may read only /{sk}/{env}/*), so updateconfig " +
               $"would write a value nothing sees. The service loads its runtime configuration from /{sk}/{env}/.";
    }

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
        Console.WriteLine($"  Wrote {paramName} ({yamlContent.Length} bytes). Nothing was restarted; " +
                          "a running service sees it only if it reloads that parameter.");
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
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        var creds = AwsCredentialsFactory.Resolve(profile);
        return creds != null
            ? new AmazonCloudFrontClient(creds, endpoint)
            : new AmazonCloudFrontClient(endpoint);
    }
}
