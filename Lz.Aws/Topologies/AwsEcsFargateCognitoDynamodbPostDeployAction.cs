using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Aws;
using Lz.Aws.DynamoDB;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;
using Lz.Aws.Compute;

namespace Lz.Aws.Topologies;

/// <summary>
/// Tenant post-deploy action for ECSExpress:
/// 1. Creates tenant + subtenant DynamoDB tables (idempotent)
/// 2. Triggers ECS service force-new-deployment — skipped outright when the task definition
///    is digest-pinned (the image is moved by <c>lz updatecontainer</c> and nothing else),
///    otherwise conditional on the running tasks differing from ECR <c>:latest</c>
/// </summary>
public class AwsEcsFargateCognitoDynamodbPostDeployAction : IPostDeployAction
{
    private readonly SystemConfig _config;
    private readonly IReadOnlyList<ServiceDefinition> _services;
    private readonly string? _tenantKey;
    private readonly TenantConfig? _tenantConfig;

    public AwsEcsFargateCognitoDynamodbPostDeployAction(
        SystemConfig config,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
    {
        _config = config;
        _services = services;
        _tenantKey = tenantKey;
        _tenantConfig = tenantConfig;
    }

    public async Task ExecuteAsync(IDictionary<string, object> outputs)
    {
        // --- Step 1: Ensure DynamoDB tables for tenant + subtenants ---
        if (_tenantKey != null)
        {
            await EnsureTenantTablesAsync();
        }

        // --- Step 2: Trigger ECS force-new-deployment for each service ---
        //
        // Two tiers since 2026-09-05. This loop used to force unconditionally, which meant a
        // TENANT CONFIG deploy could ship an image nobody asked for: `lz previewtenant`
        // correctly reports "no changes" (this runs AFTER Pulumi, so no plan can show it) and
        // `lz deploytenant` rolled the service anyway — observed live on 2026-09-05, harmless
        // only because :latest happened to resolve to the digest already running.
        //
        //   Digest-pinned (Rollback opt-in, and a digest was actually resolved): SKIP
        //   OUTRIGHT. A forced deployment re-pulls the same immutable digest, so it can only
        //   ever be a pointless roll; Pulumi already re-pointed the service at its
        //   (running-digest) revision during the up.
        //
        //   Not pinned (every other workspace, byte-identical opt-out): CONDITIONAL, fail
        //   open. Skip when the registry and the running tasks already agree, force as
        //   before when they differ or when that cannot be established.
        //
        // The conditional tier is NOT gated on the Rollback opt-in: it removes an unrequested
        // action, so every workspace should have it.
        foreach (var svc in _services)
        {
            if (svc.Docker == null) continue;

            var serviceName = svc.Name;
            var sk = _config.SystemKey;
            var env = _config.Environment;
            var prefix = _tenantKey != null ? $"{sk}-{_tenantKey}-{serviceName}" : $"{sk}-{env}-{serviceName}";
            var clusterName = $"{sk}-{env}-cluster";
            var ecrName = _tenantKey != null && _tenantConfig != null
                ? $"{sk}-{_tenantConfig.TenantSuffix}-{env}-{_tenantKey}-{serviceName}"
                : null;

            var profile = _tenantConfig?.Profile ?? _config.Profile;
            var region = _tenantConfig?.Region ?? _config.Region;

            // Gate on the digest having actually been RESOLVED, not on config intent alone:
            // when no service and no :latest existed, ResolveImageDigestsAsync fell back to the
            // tag, the definition is not pinned, and the message below would be a lie. That
            // case takes the conditional tier like an un-opted-in system.
            var pinned = ImagePinPolicy.ForTenantService(_config.Rollback).PinDigest
                         && _tenantConfig?.ResolvedImageDigests.ContainsKey(serviceName) == true;
            if (pinned)
            {
                Console.WriteLine(
                    $"  {prefix}: task definition is digest-pinned — a tenant deploy never changes the " +
                    $"image; use 'lz updatecontainer' to ship one.");
                continue;
            }

            // Fail OPEN, deliberately: if we cannot establish that they agree — no ECR
            // access, an unresolvable tag, a service we cannot describe — force as before.
            // The old behaviour is the safe default here; a skipped deploy that was needed
            // is worse than a redundant one.
            var alreadyCurrent = ecrName != null &&
                await AwsContainerUpdater.RunningMatchesRegistryAsync(
                    profile, region, clusterName, prefix, ecrName, "latest");

            if (alreadyCurrent)
            {
                Console.WriteLine($"  {prefix} already runs the current :latest — skipping force-new-deployment.");
                continue;
            }

            Console.WriteLine($"  Triggering ECS force-new-deployment for {prefix}...");
            await ForceNewDeploymentAsync(clusterName, prefix);
        }

        // --- Step 3: Verify apex DNS resolved to the CloudFront alias ---
        // AwsCognitoComponent creates a placeholder `A 127.0.0.1`
        // at the apex during deploysystem (Cognito's CreateUserPoolDomain
        // requires a resolvable apex A). AwsCloudFrontKvsComponent
        // is supposed to overwrite it with an A-alias to the tenant
        // CloudFront distribution (AllowOverwrite=true). If that overwrite
        // didn't happen, the placeholder is still in place and any flow
        // that depends on the apex (the apex OAuth callback in particular)
        // will silently break — Pulumi reports success and there's no
        // visible signal until a real login attempt fails.
        if (_tenantKey != null && _tenantConfig != null)
        {
            await VerifyApexAliasAsync();
        }
    }

    private async Task EnsureTenantTablesAsync()
    {
        var sk = _config.SystemKey;
        var tk = _tenantKey!;
        var profile = _config.Profile;
        var region = _config.Region;

        Console.WriteLine($"  Ensuring DynamoDB tables for tenant '{tk}'...");

        var baseTags = new Dictionary<string, string>
        {
            { "System", sk },
            { "Tenant", tk },
        };

        // Tenant table: {SystemKey}_{TenantKey} (PK/SK envelope schema)
        var tenantTable = $"{sk}_{tk}";
        var created = await DynamoDbTableCreator.EnsureTableAsync(
            profile, region, tenantTable,
            new Dictionary<string, string>(baseTags) { { "Level", "tenant" } },
            TableDurabilityPolicy.ForTenantTable(_config.Durability));
        Console.WriteLine(created
            ? $"    {tenantTable} — created"
            : $"    {tenantTable} — exists");

        // Dedicated BFF session table: {SystemKey}_{TenantKey}_bff (id/sk schema).
        // Separate from the app data table so the BFF session store (id/sk point-ops)
        // never collides with the app repo (PK/SK envelope). See BffWiring.sessionTable.
        var bffSessionTable = $"{sk}_{tk}_bff";
        var bffCreated = await DynamoDbTableCreator.EnsureSessionTableAsync(
            profile, region, bffSessionTable,
            new Dictionary<string, string>(baseTags) { { "Level", "tenant" }, { "Purpose", "bff-sessions" } },
            TableDurabilityPolicy.ForBffSessionTable(_config.Durability));
        Console.WriteLine(bffCreated
            ? $"    {bffSessionTable} — created"
            : $"    {bffSessionTable} — exists");

        // SECOND BFF pool (consumerauth) session table: {SystemKey}_{TenantKey}_cbff.
        // Only when the tenant wires the consumerauth /cbff instance. (IAM is already covered by
        // the {sk}_{tk}_* tenant-service role policy.)
        if (_tenantConfig?.BffConsumerAuthEnabled == true)
        {
            var cbffSessionTable = $"{sk}_{tk}_cbff";
            var cbffCreated = await DynamoDbTableCreator.EnsureSessionTableAsync(
                profile, region, cbffSessionTable,
                new Dictionary<string, string>(baseTags) { { "Level", "tenant" }, { "Purpose", "bff-sessions-consumerauth" } },
                TableDurabilityPolicy.ForBffSessionTable(_config.Durability));
            Console.WriteLine(cbffCreated
                ? $"    {cbffSessionTable} — created"
                : $"    {cbffSessionTable} — exists");
        }

        // THIRD BFF pool (systemauth) session table: {SystemKey}_{TenantKey}_abff.
        // Only when the tenant wires the systemauth /abff instance. (IAM is already covered by
        // the {sk}_{tk}_* tenant-service role policy.)
        if (_tenantConfig?.BffSystemAuthEnabled == true)
        {
            var abffSessionTable = $"{sk}_{tk}_abff";
            var abffCreated = await DynamoDbTableCreator.EnsureSessionTableAsync(
                profile, region, abffSessionTable,
                new Dictionary<string, string>(baseTags) { { "Level", "tenant" }, { "Purpose", "bff-sessions-systemauth" } },
                TableDurabilityPolicy.ForBffSessionTable(_config.Durability));
            Console.WriteLine(abffCreated
                ? $"    {abffSessionTable} — created"
                : $"    {abffSessionTable} — exists");
        }

        // Per-subtenant infrastructure (S3 bucket + DynamoDB table) is
        // handled by the shared provisioner so the same logic runs here and
        // from `lz deploysubtenants`.
        if (_tenantConfig != null)
        {
            var accountId = await AwsAccountResolver.ResolveAccountIdAsync(profile, region);
            await Lz.Aws.Shared.SubtenantProvisioner.EnsureAllAsync(
                _config, _tenantConfig, profile, region, accountId);
        }
    }

    /// <summary>
    /// Asserts the tenant root domain's apex A record is the CloudFront
    /// alias the tenant stack should have created. Warns loudly if the
    /// placeholder (or any non-CloudFront record) is still present after
    /// a tenant deploy. Intentionally non-fatal — Pulumi already reported
    /// success, so erroring out here would be confusing; the warning
    /// surfaces the issue without unwinding everything else.
    /// </summary>
    private async Task VerifyApexAliasAsync()
    {
        var rootDomain = _tenantConfig!.RootDomain;
        if (string.IsNullOrEmpty(rootDomain))
        {
            Console.WriteLine("  Skipping apex DNS check — no RootDomain in tenant config.");
            return;
        }

        Console.WriteLine($"  Verifying apex DNS for {rootDomain}...");

        try
        {
            var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(_config.Profile, out var credentials))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Apex DNS check skipped — could not resolve credentials for profile '{_config.Profile}'.");
                Console.ResetColor();
                return;
            }

            // Route 53 is a global service routed through us-east-1. Pin
            // explicitly so the check works on machines whose AWS profile
            // doesn't set a DefaultRegion.
            using var r53 = new Amazon.Route53.AmazonRoute53Client(
                credentials, Amazon.RegionEndpoint.USEast1);

            // Find the hosted zone. ListHostedZonesByName matches as a prefix
            // search; verify the returned zone's name matches our root.
            var zoneName = rootDomain.EndsWith('.') ? rootDomain : rootDomain + ".";
            var zonesResp = await r53.ListHostedZonesByNameAsync(
                new Amazon.Route53.Model.ListHostedZonesByNameRequest { DNSName = zoneName });
            var zone = zonesResp.HostedZones?.FirstOrDefault(z => z.Name == zoneName);
            if (zone == null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Apex DNS check skipped — no Route 53 hosted zone for {rootDomain}.");
                Console.ResetColor();
                return;
            }

            var rrsResp = await r53.ListResourceRecordSetsAsync(
                new Amazon.Route53.Model.ListResourceRecordSetsRequest
                {
                    HostedZoneId = zone.Id,
                    StartRecordName = zoneName,
                    StartRecordType = Amazon.Route53.RRType.A,
                    MaxItems = "10",
                });
            var apexA = rrsResp.ResourceRecordSets?
                .FirstOrDefault(r => r.Name == zoneName && r.Type == Amazon.Route53.RRType.A);

            if (apexA == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  WARNING: No apex A record found for {rootDomain}.");
                Console.Error.WriteLine($"  Expected: A-alias to the tenant CloudFront distribution.");
                Console.ResetColor();
                return;
            }

            // The healthy state is an Alias record whose target ends with
            // .cloudfront.net (the tenant distribution). Anything else —
            // a literal IP (the placeholder) or a non-CloudFront alias —
            // means the tenant stack didn't reconcile the placeholder.
            var aliasTarget = apexA.AliasTarget?.DNSName;
            if (!string.IsNullOrEmpty(aliasTarget) &&
                aliasTarget.Contains(".cloudfront.net", StringComparison.OrdinalIgnoreCase))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  Apex DNS OK: {rootDomain} → {aliasTarget.TrimEnd('.')}");
                Console.ResetColor();
                return;
            }

            string actual;
            if (!string.IsNullOrEmpty(aliasTarget))
                actual = $"alias to {aliasTarget.TrimEnd('.')}";
            else
            {
                var literals = apexA.ResourceRecords != null
                    ? string.Join(", ", apexA.ResourceRecords.Select(r => r.Value))
                    : "(empty)";
                actual = $"literal record [{literals}]";
            }

            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"  WARNING: Apex DNS for {rootDomain} is {actual}.");
            Console.Error.WriteLine($"  Expected: A-alias to a *.cloudfront.net distribution.");
            Console.Error.WriteLine($"  Cause: AwsCognitoComponent creates a placeholder A 127.0.0.1 at");
            Console.Error.WriteLine($"  the apex during `lz deploysystem` so Cognito's CreateUserPoolDomain has a");
            Console.Error.WriteLine($"  resolvable parent. AwsCloudFrontKvsComponent is supposed to overwrite");
            Console.Error.WriteLine($"  it during `lz deploytenant` (AllowOverwrite=true on the alias resource).");
            Console.Error.WriteLine($"  If this warning persists after a successful deploytenant, the alias-creation");
            Console.Error.WriteLine($"  step didn't run — check the Pulumi tenant-stack output for `cf-alias` errors");
            Console.Error.WriteLine($"  and re-run. Login flows via the apex callback (https://{rootDomain.TrimEnd('.')}/oauth2/callback)");
            Console.Error.WriteLine($"  will fail until this is resolved.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Apex DNS check failed (non-fatal): {ex.Message}");
            Console.ResetColor();
        }
    }

    private async Task ForceNewDeploymentAsync(string clusterName, string serviceName)
    {
        try
        {
            var chain = new Amazon.Runtime.CredentialManagement.CredentialProfileStoreChain();
            if (!chain.TryGetAWSCredentials(_config.Profile, out var credentials))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"  Failed to resolve credentials for profile '{_config.Profile}'");
                Console.ResetColor();
                return;
            }

            using var client = new Amazon.ECS.AmazonECSClient(
                credentials,
                Amazon.RegionEndpoint.GetBySystemName(_config.Region));

            var describeResponse = await client.DescribeServicesAsync(
                new Amazon.ECS.Model.DescribeServicesRequest
                {
                    Cluster = clusterName,
                    Services = new List<string> { serviceName },
                });

            var service = describeResponse.Services.FirstOrDefault();
            if (service == null || service.Status != "ACTIVE")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  ECS service '{serviceName}' not found or not active.");
                Console.ResetColor();
                return;
            }

            await client.UpdateServiceAsync(
                new Amazon.ECS.Model.UpdateServiceRequest
                {
                    Cluster = clusterName,
                    Service = serviceName,
                    ForceNewDeployment = true,
                });

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ECS deployment triggered for {serviceName}.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: Could not trigger ECS deployment for {serviceName}: {ex.Message}");
            Console.ResetColor();
        }
    }
}
