using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Aws.Config;    // config.Aws().PrivateNetwork (Phase 2 gate)
using Lz.Aws.Tailscale; // AwsTailscalePostDeployAction (Phase 2)
using Lz.Aws.Interfaces;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Topologies;

/// <summary>
/// Platform factory for ECSExpress topology.
/// ECS Fargate in public subnets (no NAT) + DynamoDB + Cognito + CloudFront KVS.
/// Shares the Cognito/DynamoDB/S3 capability components with the Lambda topology.
/// </summary>
public class AwsEcsFargateCognitoDynamodbPlatformFactory : IAwsPlatformFactory
{
    private readonly SystemConfig _config;

    public AwsEcsFargateCognitoDynamodbPlatformFactory(SystemConfig config)
    {
        _config = config;
    }

    // ECSExpress-specific components
    public virtual ISystemNetworkComponent CreateNetwork() => new AwsFargateNetworkComponent();
    public virtual IComputeEnvironmentComponent CreateComputeEnvironment() => new AwsFargateComputeComponent();
    public virtual ITenantServiceComponent CreateTenantService() => new AwsFargateTenantServiceComponent(_config);
    public virtual ITenantCdnComponent CreateTenantCdn() => new AwsCloudFrontKvsComponent();
    public virtual void DeployTenantDnsAndCert(TenantConfig tenantConfig, INetworkOutputs network, ICdnOutputs? cdn = null) { }

    /// <summary>
    /// Tailscale split DNS for the tenant's VPN-only names — the Fargate port
    /// of the Ecs topology's implementation, invoked by SystemDeployment after
    /// every tenant Pulumi up. Config-driven where Ecs hardcodes shop./auth.:
    /// each PrivateNetwork.SplitDnsHosts label h becomes an entry
    /// h.{RootDomain} → the VPC resolver, applied via the Tailscale API's PATCH
    /// (merge) semantics — only the named domains are touched, so tailnet DNS
    /// config owned by other systems/environments is never clobbered.
    /// Triple-gated no-op (empty list / no PrivateNetwork+Tailscale opt-in /
    /// no RootDomain) so the sibling systems stay byte-identical. Failures are
    /// warn-and-continue: a tailnet-side hiccup (or an API key without DNS
    /// scope) must not fail a tenant deploy — the printed fallback is the same
    /// one-line admin-console step the automation replaces.
    /// </summary>
    public virtual async Task UpdateTenantSplitDnsAsync(TenantConfig tenantConfig)
    {
        var pn = _config.Aws().PrivateNetwork;
        var hosts = pn is { Enabled: true, Tailscale: true } ? pn.SplitDnsHosts : null;
        if (hosts == null || hosts.Count == 0 || string.IsNullOrEmpty(tenantConfig.RootDomain))
            return;

        var resolver = Tailscale.AwsTailscalePostDeployAction.CalculateVpcDnsResolver(_config.VpcCidr);
        var entries = new Dictionary<string, string[]>();
        foreach (var h in hosts)
        {
            var label = h?.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(label)) continue;
            entries[$"{label}.{tenantConfig.RootDomain}"] = new[] { resolver };
        }
        if (entries.Count == 0) return;

        try
        {
            var apiKey = await new Tailscale.AwsTailscalePostDeployAction(_config).GetTailscaleApiKeyAsync();
            Console.WriteLine("Updating Tailscale split DNS for tenant VPN-only names...");
            foreach (var (domain, resolvers) in entries)
                Console.WriteLine($"  {domain} → {resolvers[0]}");

            using var client = new Lz.Core.Tailscale.TailscaleApiClient(apiKey);
            await client.SetSplitDnsAsync(entries);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  Tailscale split DNS updated.");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Warning: Tailscale split DNS update failed: {ex.Message}");
            Console.WriteLine("  (A 403 usually means the stored tailscale-api-key lacks DNS write scope.)");
            Console.WriteLine("  Manual fallback — Tailscale admin console → DNS → split DNS:");
            foreach (var (domain, resolvers) in entries)
                Console.WriteLine($"    {domain} → nameserver {resolvers[0]}");
            Console.ResetColor();
        }
    }

    // Shared capability components (DynamoDB, S3/Secrets, Cognito, stub FileStorage)
    public virtual IDatabaseComponent CreateDatabase() => new AwsDynamoDbComponent();
    public virtual IFileStorageComponent CreateFileStorage() => new AwsS3FileStorageComponent();
    public virtual IAuthServiceComponent CreateAuthService() => new AwsCognitoComponent();
    public virtual ITenantDataComponent CreateTenantData() => new AwsTenantDataComponent();
    public virtual IEmailComponent CreateEmail() => new AwsSesComponent();
    public virtual IServiceComponent CreateService() => new AwsFargateServiceComponent(_config);

    // Tailscale subnet router (Phase 2) — opt-in via PrivateNetwork.Tailscale.
    // Off (default / no PrivateNetwork block) => null, byte-identical to today.
    public virtual ITailscaleComponent? CreateTailscale()
        => _config.Aws().PrivateNetwork is { Enabled: true, Tailscale: true }
            ? new AwsTailscaleAsgComponent()
            : null;
    public virtual IPostDeployAction? GetFoundationPostDeployAction()
        => new AwsEcsFargateCognitoDynamodbFoundationPostDeployAction(_config);
    // deploysystem-phase hook: ensure the {SystemKey} system table (idempotent).
    public virtual IPostDeployAction? GetSystemPostDeployAction()
        => new AwsEcsFargateCognitoDynamodbFoundationPostDeployAction(_config);
    public virtual IPostDeployAction? GetTailscalePostDeployAction(SystemDefinition? system = null)
        => _config.Aws().PrivateNetwork is { Enabled: true, Tailscale: true }
            ? new AwsTailscalePostDeployAction(_config, system)
            : null;
    public virtual ITailscaleKeyManager? GetTailscaleKeyManager()
        => _config.Aws().PrivateNetwork is { Enabled: true, Tailscale: true }
            ? new AwsTailscalePostDeployAction(_config)
            : null;
    public virtual ITenantKeycloakSeeder? GetTenantKeycloakSeeder() => null;

    public virtual IPostDeployAction? GetFoundationServiceDeployAction(SystemDefinition system) => null;

    public virtual IPostDeployAction? GetServiceDeployAction(
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
        => new AwsEcsFargateCognitoDynamodbPostDeployAction(_config, services, tenantKey, tenantConfig);

    public virtual ITransitionChecker CreateTransitionChecker()
        => new AwsTransitionChecker(_config);

    // No Lambda gate-checker, no seed tasks, no config init
    public virtual IGateCheckerComponent? CreateGateChecker() => null;
    public virtual IConfigInitRunner? GetConfigInitRunner() => null;
    public virtual IPostSeedRunner? GetPostSeedRunner() => null;
    public virtual IAdminSetupRunner? GetAdminSetupRunner() => null;
    public virtual ISeedTaskComponent? CreateSeedTask() => null;
    public virtual string? CreateSeedBucket(SharedConfig sharedConfig, string systemKey) => null;

    public virtual (INetworkOutputs Network, IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database, IFileStorageOutputs FileStorage)
        LookupFoundation(SystemConfig config)
        => AwsEcsFargateCognitoDynamodbFoundationLookup.Lookup(config);
}
