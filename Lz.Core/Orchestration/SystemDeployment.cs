using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Automation;
using Pulumi.Automation.Events;

namespace Lz.Core.Orchestration;

/// <summary>
/// Deployment orchestrator for a single system (systemconfig).
/// Provides DeployFoundationAsync, DeployTenantAsync, DestroyFoundationAsync,
/// DestroyTenantAsync, and status methods. The CLI is responsible for
/// iterating over multiple systems and tenants — this class handles one at a time.
/// Each phase uses pre-flight gate checks followed by Pulumi up with post-deploy actions.
/// </summary>
public class SystemDeployment
{
    private readonly IPlatformFactory _factory;
    private readonly SystemDefinition _system;
    private readonly SystemConfig _config;
    private readonly CancellationToken _ct;

    public SystemDeployment(
        IPlatformFactory factory, SystemDefinition system, SystemConfig config,
        CancellationToken cancellationToken = default)
    {
        _factory = factory;
        _system = system;
        _config = config;
        _ct = cancellationToken;
    }

    // ---------------------------------------------------------------
    // Foundation phase
    // ---------------------------------------------------------------

    /// <summary>
    /// Foundation deployment: gates first, then a single Pulumi up, then post-deploy.
    /// Keycloak is NOT deployed here — it lives in the shared-services account.
    ///   Pre-flight: check all gates + auto-ensure tailscale-auth-key
    ///   Pulumi up: all infrastructure including Tailscale ASG
    ///   Post-deploy: configure Tailscale — approve routes, disable key expiry, split DNS
    /// </summary>
    public async Task DeployFoundationAsync()
    {
        var stackName = $"{_config.SystemKey}-{_config.Environment}";
        var checker = _factory.CreateTransitionChecker();

        Console.WriteLine($"=== Foundation Phase ===");
        Console.WriteLine($"Stack: {stackName}");
        Console.WriteLine($"Central auth: {_config.CentralAuthDomain}");
        Console.WriteLine();

        // --- Pre-flight: Check all gates before any Pulumi up ---

        if (_system.FoundationInfraGates.Count > 0)
        {
            Console.WriteLine("Checking foundation infrastructure gates...");
            var infraGatePassed = await TransitionGate.CheckAndReportAsync(
                checker, _system.FoundationInfraGates, _config.SystemKey);

            if (!infraGatePassed)
                return;
        }

        if (_system.FoundationGates.Count > 0)
        {
            Console.WriteLine("Checking foundation transition gates...");
            var gatePassed = await TransitionGate.CheckAndReportAsync(
                checker, _system.FoundationGates, _config.SystemKey);

            if (!gatePassed)
                return;
        }

        // --- Tailscale gates (only when VPN is enabled) ---
        if (_system.UsesVpn)
        {
            var tailscaleGates = new List<TransitionRequirement>
            {
                new TransitionRequirement
                {
                    Name = "tailscale-api-key",
                    CheckType = TransitionCheckType.SecretEntry,
                    SecretName = "shared/system",
                    Profile = _config.SharedProfile,
                    Region = _config.SharedRegion,
                    CheckTarget = "tailscale-api-key",
                    Description =
                        "Tailscale API key is required for managing subnet routers.\n" +
                        "  1. Create an API key at https://login.tailscale.com/admin/settings/keys\n" +
                        "  2. Add 'tailscale-api-key' to the 'shared/system' secret in Secrets Manager.\n" +
                        "  3. Re-run: lz deployfoundation",
                },
            };

            Console.WriteLine("Checking Tailscale gates...");
            var tailscaleGatePassed = await TransitionGate.CheckAndReportAsync(
                checker, tailscaleGates, _config.SystemKey);

            if (!tailscaleGatePassed)
                return;
        }

        // --- Auto-ensure: create/refresh auth key + SSH key if missing or expired ---
        var keyManager = _factory.GetTailscaleKeyManager();
        if (keyManager != null)
        {
            await keyManager.EnsureAuthKeyAsync();
            await keyManager.EnsureSshKeyAsync();
        }

        // --- Pre-deploy cleanup (e.g., clear stale private zone records) ---
        await _factory.CleanupBeforeFoundationAsync();

        // --- Pulumi up: deploy everything including Tailscale in one pass ---
        Console.WriteLine();
        Console.WriteLine("Deploying foundation infrastructure...");
        var result = await PulumiUpAsync(stackName, includeTailscale: true);

        // --- Post-deploy: Configure Tailscale devices ---
        var tailscalePostDeploy = _factory.GetTailscalePostDeployAction(_system);
        if (tailscalePostDeploy != null)
        {
            Console.WriteLine();
            Console.WriteLine("Configuring Tailscale subnet routers...");
            var tailscaleOutputs = result.Outputs.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Value);
            await tailscalePostDeploy.ExecuteAsync(tailscaleOutputs);
        }

        // --- Post-deploy: Build/push/scale foundation-level services (e.g., LiveKit) ---
        var foundationServiceDeploy = _factory.GetFoundationServiceDeployAction(_system);
        if (foundationServiceDeploy != null)
        {
            Console.WriteLine();
            Console.WriteLine("Building and deploying foundation services...");
            var foundationOutputs = result.Outputs.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Value);
            await foundationServiceDeploy.ExecuteAsync(foundationOutputs);
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Foundation deployment complete.");
        Console.ResetColor();
    }

    /// <summary>
    /// Run Pulumi up for the foundation stack.
    /// </summary>
    private async Task<UpResult> PulumiUpAsync(string stackName, bool includeTailscale)
    {
        var stack = await CreateOrSelectStack(stackName, () =>
        {
            var foundation = DeployFoundation();
            var exports = new Dictionary<string, object?>(foundation.Exports);

            if (includeTailscale)
            {
                var tailscale = _factory.CreateTailscale();
                if (tailscale != null)
                {
                    var tailscaleOutputs = tailscale.Deploy(_config, foundation.Network, foundation.FileStorage);
                    exports["tailscaleAsgId"] = tailscaleOutputs.AutoScalingGroupId;
                }
            }

            return exports;
        });

        // Always refresh before up — catch state drift from cross-stack
        // operations, manual changes, or prior failed deploys.
        Console.WriteLine("Running Pulumi refresh...");
        Console.WriteLine();

        await stack.RefreshAsync(new RefreshOptions
        {
            OnEvent = HandleEngineEvent,
            OnStandardError = msg =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(msg);
                Console.ResetColor();
            },
        }, _ct);

        Console.WriteLine();
        Console.WriteLine("Running Pulumi up...");
        Console.WriteLine();

        var result = await stack.UpAsync(new UpOptions
        {
            OnEvent = HandleEngineEvent,
            OnStandardError = msg =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(msg);
                Console.ResetColor();
            },
        }, _ct);

        Console.WriteLine();
        PrintResourceChanges("Update", result.Summary);

        return result;
    }

    // ---------------------------------------------------------------
    // Tenant phase
    // ---------------------------------------------------------------

    /// <summary>
    /// Tenant deployment: gates first, single Pulumi up, then post-deploy with gates.
    ///   Pre-flight: tenantconfig gate
    ///   Pulumi up: data + service-layer + host-layer + CDN (all at once, desiredCount=0)
    ///   GATE: EFS seeded, DB seeded (blocks until user seeds data)
    ///   Post-deploy: build/push/scale service-layer (SmartStore) — after seed confirmed
    ///   Post-deploy: build/push/scale host-layer (AppHost)
    ///   Post-deploy: seed per-tenant Keycloak realms (if keycloakconfig found)
    /// </summary>
    public async Task DeployTenantAsync(
        string tenantKey, TenantConfig tenantConfig,
        Dictionary<string, string?>? smtpSecrets = null,
        bool refresh = false)
    {
        var stackName = $"{_config.SystemKey}-{tenantKey}-{_config.Environment}";
        var checker = _factory.CreateTransitionChecker();

        Console.WriteLine($"=== Tenant Phase: {tenantKey} ===");
        Console.WriteLine($"Stack: {stackName}");
        Console.WriteLine();

        // --- Pre-flight: Check tenantconfig exists ---
        var sk = _config.SystemKey;
        var env = _config.Environment;
        var tenantConfigFilename = $"tenantconfig.{sk}.{tenantKey}.{env}.yaml";
        var tenantPreFlightGates = new List<TransitionRequirement>
        {
            new TransitionRequirement
            {
                Name = "tenantconfig",
                Description = $"Tenant configuration file is required.\n"
                    + $"  Create {tenantConfigFilename} in the monorepo root.",
                CheckType = TransitionCheckType.Custom,
                IsOneTime = true,
                CustomCheck = () => Task.FromResult(
                    ConfigLoader.DiscoverConfigFile(
                        Directory.GetCurrentDirectory(), tenantConfigFilename) != null),
            }
        };

        Console.WriteLine("Checking tenant pre-flight gates...");
        var preFlightPassed = await TransitionGate.CheckAndReportAsync(
            checker, tenantPreFlightGates, _config.SystemKey, tenantKey);

        if (!preFlightPassed)
            return;

        // --- Single Pulumi up: data + service-layer + host-layer + CDN ---
        // Always refresh before up. Multi-stack systems with cross-stack DNS records
        // and imperative operations (lz park) make state drift inevitable. The ~30s
        // refresh cost is negligible vs the risk of deploying against stale state.
        Console.WriteLine();
        Console.WriteLine("Deploying tenant infrastructure...");
        var result = await TenantPulumiUpAsync(stackName, tenantKey, tenantConfig, refresh: true);

        // --- Update Tailscale split DNS for tenant domains ---
        // Adds tenant RootDomain (and LegacyDomains) so VPN users can resolve
        // shop.{RootDomain} → internal ALB via the per-tenant private zone.
        await _factory.UpdateTenantSplitDnsAsync(tenantConfig);

        // --- Init config: create tenant DB + write Settings.txt/usersettings.json ---
        var configInit = _factory.GetConfigInitRunner();
        if (configInit != null)
        {
            var ecs = tenantConfig.ECS ?? new Config.EcsConfig();
            var dbName = ecs.DatabaseName
                ?? $"{_config.SystemKey}_{tenantKey}_{_config.Environment}_smartstore";
            var appUser = $"{_config.SystemKey}_{tenantKey}_app";
            var platformDbName = ecs.PlatformDatabaseName; // null if not configured — Lambda skips platform DB creation

            Console.WriteLine();
            Console.WriteLine("Initializing tenant config (database + EFS config files)...");
            var initOk = await configInit.RunInitConfigAsync(tenantKey, dbName, appUser,
                userSettings: tenantConfig.Smartstore,
                platformDatabaseName: platformDbName);
            if (!initOk)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Config init failed. Check CloudWatch logs for the gate-checker Lambda.");
                Console.ResetColor();
                return;
            }
        }

        // --- GATE: Check for data-seeding prerequisites ---
        // This must happen BEFORE scaling SmartStore, otherwise SmartStore
        // boots on an empty DB, runs EF migrations, and creates tables that
        // prevent the seed restore (which expects an empty database).
        var seedGates = _system.ServiceLayerServices
            .SelectMany(s => s.TransitionRequirements)
            .Concat(_system.HostLayerServices
                .SelectMany(s => s.TransitionRequirements))
            .ToList();

        if (seedGates.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Checking tenant transition gates...");
            var gatePassed = await TransitionGate.CheckAndReportAsync(
                checker, seedGates, _config.SystemKey, tenantKey);

            if (!gatePassed)
                return; // Stop — user must seed data and re-run
        }

        // --- Post-deploy: build/push/scale service-layer ---
        var serviceLayerServices = _system.ServiceLayerServices;
        if (serviceLayerServices.Count > 0)
        {
            var serviceAction = _factory.GetServiceDeployAction(_system, serviceLayerServices, tenantKey, tenantConfig);
            if (serviceAction != null)
            {
                Console.WriteLine();
                Console.WriteLine("Building and deploying service-layer (SmartStore)...");
                var outputs = result.Outputs.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Value);
                await serviceAction.ExecuteAsync(outputs);
            }
        }

        // --- Post-seed config: re-write Settings.txt with correct credentials ---
        // The seed process may overwrite Settings.txt with source-environment values.
        var postSeed = _factory.GetPostSeedRunner();
        if (postSeed != null)
        {
            var ecs2 = tenantConfig.ECS ?? new Config.EcsConfig();
            var dbName2 = ecs2.DatabaseName
                ?? $"{_config.SystemKey}_{tenantKey}_{_config.Environment}_smartstore";
            var appUser2 = $"{_config.SystemKey}_{tenantKey}_app";

            Console.WriteLine();
            Console.WriteLine("Running post-seed config (updating EFS config files)...");
            var postSeedOk = await postSeed.RunPostSeedAsync(tenantKey, dbName2, appUser2,
                userSettings: tenantConfig.Smartstore);
            if (!postSeedOk)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Post-seed config failed. Check CloudWatch logs for the gate-checker Lambda.");
                Console.ResetColor();
                return;
            }
        }

        // --- Setup SmartStore admin: create InternalAdmin + API credentials ---
        var adminSetup = _factory.GetAdminSetupRunner();
        if (adminSetup != null)
        {
            var ecsAdmin = tenantConfig.ECS ?? new Config.EcsConfig();
            var adminDbName = ecsAdmin.DatabaseName
                ?? $"{_config.SystemKey}_{tenantKey}_{_config.Environment}_smartstore";

            Console.WriteLine();
            Console.WriteLine("Setting up SmartStore admin and API credentials...");
            var adminOk = await adminSetup.RunSetupAdminAsync(tenantKey, adminDbName);
            if (!adminOk)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Admin setup failed. Check CloudWatch logs for the gate-checker Lambda.");
                Console.ResetColor();
                return;
            }
        }

        // --- Post-deploy: build/push/scale host-layer ---
        var hostLayerServices = _system.HostLayerServices;
        if (hostLayerServices.Count > 0)
        {
            var hostAction = _factory.GetServiceDeployAction(_system, hostLayerServices, tenantKey, tenantConfig);
            if (hostAction != null)
            {
                Console.WriteLine();
                Console.WriteLine("Building and deploying host-layer (AppHost)...");
                var outputs = result.Outputs.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Value);
                await hostAction.ExecuteAsync(outputs);
            }
        }

        // --- Post-deploy: seed per-tenant Keycloak realms ---
        var keycloakSeeder = _factory.GetTenantKeycloakSeeder();
        if (keycloakSeeder != null)
        {
            var seedConfig = ConfigLoader.DiscoverTenantKeycloakSeedConfig(
                _config.SystemKey, tenantKey, _config.Environment,
                tenantConfig, smtpSecrets ?? new Dictionary<string, string?>());

            if (seedConfig != null)
            {
                Console.WriteLine();
                await keycloakSeeder.SeedAsync(seedConfig, tenantKey);
            }
            else
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("No keycloakconfig found — skipping tenant Keycloak seeding.");
                Console.ResetColor();
            }
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Tenant '{tenantKey}' deployment complete.");
        Console.ResetColor();
    }

    /// <summary>
    /// Run Pulumi up for a tenant stack.
    /// </summary>
    private async Task<UpResult> TenantPulumiUpAsync(
        string stackName,
        string tenantKey,
        TenantConfig tenantConfig,
        bool refresh = false)
    {
        var stack = await CreateOrSelectStack(stackName, () =>
        {
            // Look up existing foundation resources (created by deployfoundation).
            // This uses data-source queries — no resources are created here.
            var (network, compute, database, fileStorage) = _factory.LookupFoundation(_config);
            var foundation = new FoundationOutputs(network, compute, database, fileStorage, null, null, new());
            var exports = new Dictionary<string, object?>();

            // Tenant data: EFS access points, tenant secret
            var tenantDataComponent = _factory.CreateTenantData();
            var tenantDataOutputs = tenantDataComponent.Deploy(
                tenantConfig, foundation.FileStorage, foundation.Database);
            exports[$"{tenantKey}_tenantSecretId"] = tenantDataOutputs.TenantSecretId;

            // Service-layer services (e.g., SmartStore at desiredCount:0)
            var tenantServiceComponent = _factory.CreateTenantService();
            foreach (var svc in _system.ServiceLayerServices)
            {
                var svcOutputs = tenantServiceComponent.Deploy(
                    svc.Name, svc, tenantConfig,
                    foundation.Network, foundation.Compute,
                    foundation.Database, tenantDataOutputs);
                exports[$"{tenantKey}_{svc.Name}_serviceId"] = svcOutputs.ServiceId;
                exports[$"{tenantKey}_{svc.Name}_endpoint"] = svcOutputs.Endpoint;
            }

            // Host-layer services (e.g., AppHost at desiredCount:0)
            foreach (var svc in _system.HostLayerServices)
            {
                var svcOutputs = tenantServiceComponent.Deploy(
                    svc.Name, svc, tenantConfig,
                    foundation.Network, foundation.Compute,
                    foundation.Database, tenantDataOutputs);
                exports[$"{tenantKey}_{svc.Name}_serviceId"] = svcOutputs.ServiceId;
                exports[$"{tenantKey}_{svc.Name}_endpoint"] = svcOutputs.Endpoint;
            }

            // CDN: CloudFront + S3 (creates ACM cert in us-east-1 automatically)
            var cdn = _factory.CreateTenantCdn();
            var cdnOutputs = cdn.Deploy(tenantConfig, foundation.Compute);
            exports[$"{tenantKey}_distributionId"] = cdnOutputs.DistributionId;
            exports[$"{tenantKey}_webappBucketId"] = cdnOutputs.WebappBucketId;
            exports[$"{tenantKey}_exploreBucketId"] = cdnOutputs.ExploreBucketId;

            // Tenant DNS + ALB certificates (SNI) + all public DNS records.
            // Runs AFTER CDN so it can create CloudFront alias records.
            // All DNS for all domains (root + legacy) managed here with stable
            // resource names keyed by domain — no identity conflicts on transitions.
            _factory.DeployTenantDnsAndCert(tenantConfig, foundation.Network, cdnOutputs);

            return exports;
        });

        // Refresh: sync Pulumi state with actual AWS resource state.
        // Used after `lz park` or manual AWS changes to detect drift.
        if (refresh)
        {
            Console.WriteLine("Running Pulumi refresh (detecting drift)...");
            Console.WriteLine();

            await stack.RefreshAsync(new RefreshOptions
            {
                OnEvent = HandleEngineEvent,
                OnStandardError = msg =>
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(msg);
                    Console.ResetColor();
                },
            }, _ct);

            Console.WriteLine();
        }

        Console.WriteLine("Running Pulumi up...");
        Console.WriteLine();

        var result = await stack.UpAsync(new UpOptions
        {
            OnEvent = HandleEngineEvent,
            OnStandardError = msg =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(msg);
                Console.ResetColor();
            },
        }, _ct);

        Console.WriteLine();
        PrintResourceChanges("Update", result.Summary);

        return result;
    }

    // ---------------------------------------------------------------
    // Destroy
    // ---------------------------------------------------------------

    /// <summary>
    /// Destroy the foundation stack for this system.
    /// </summary>
    public async Task DestroyFoundationAsync()
    {
        var stackName = $"{_config.SystemKey}-{_config.Environment}";
        Console.WriteLine($"Destroying foundation stack '{stackName}'...");
        await DestroyStackAsync(stackName);
    }

    /// <summary>
    /// Destroy a single tenant stack.
    /// </summary>
    public async Task DestroyTenantAsync(string tenantKey)
    {
        var tenantStackName = $"{_config.SystemKey}-{tenantKey}-{_config.Environment}";
        Console.WriteLine($"Destroying tenant stack '{tenantStackName}'...");
        await DestroyStackAsync(tenantStackName);
    }

    private async Task DestroyStackAsync(string stackName)
    {
        var stack = await CreateOrSelectStack(stackName, () => new Dictionary<string, object?>());

        var result = await stack.DestroyAsync(new DestroyOptions
        {
            OnEvent = HandleEngineEvent,
            OnStandardError = msg =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(msg);
                Console.ResetColor();
            },
        }, _ct);

        Console.WriteLine();
        PrintResourceChanges("Destroy", result.Summary);
    }

    // ---------------------------------------------------------------
    // Status
    // ---------------------------------------------------------------

    /// <summary>
    /// Show deployment status for the foundation stack.
    /// </summary>
    public async Task StatusFoundationAsync()
    {
        var stackName = $"{_config.SystemKey}-{_config.Environment}";
        await PrintStackStatusAsync(stackName);
    }

    /// <summary>
    /// Show deployment status for a single tenant stack.
    /// </summary>
    public async Task StatusTenantAsync(string tenantKey)
    {
        var tenantStackName = $"{_config.SystemKey}-{tenantKey}-{_config.Environment}";
        await PrintStackStatusAsync(tenantStackName);
    }

    private async Task PrintStackStatusAsync(string stackName)
    {
        try
        {
            var stack = await CreateOrSelectStack(stackName, () => new Dictionary<string, object?>());
            var info = await stack.GetInfoAsync();

            if (info == null)
            {
                Console.WriteLine($"Stack '{stackName}': No deployments yet.");
                return;
            }

            Console.WriteLine($"Stack: {stackName}");
            Console.WriteLine($"  Last updated: {info.EndTime}");
            Console.WriteLine($"  Result: {info.Result}");
            if (info.ResourceChanges != null)
            {
                foreach (var kv in info.ResourceChanges)
                    Console.WriteLine($"  {kv.Key}: {kv.Value}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Stack '{stackName}': {ex.Message}");
        }
    }

    // ---------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Handle Pulumi engine events to provide real-time resource operation logging.
    /// Shows resource operations as they start, not just when they complete.
    /// </summary>
    internal static void HandleEngineEvent(EngineEvent e)
    {
        if (e.ResourcePreEvent is { } pre)
        {
            var meta = pre.Metadata;
            var op = meta.Op;
            if (op == OperationType.Same) return; // Skip unchanged resources

            var type = meta.Type; // e.g., "aws:ec2/vpc:Vpc"
            var name = meta.Urn.Split("::").LastOrDefault() ?? meta.Urn;
            var opName = op.ToString().ToLowerInvariant();

            // Use color to distinguish operations
            Console.ForegroundColor = op switch
            {
                OperationType.Delete => ConsoleColor.Red,
                OperationType.Create => ConsoleColor.Cyan,
                OperationType.Update or OperationType.Replace => ConsoleColor.Yellow,
                _ => ConsoleColor.Gray,
            };

            // Extract short type name (e.g., "aws:ec2/vpc:Vpc" → "Vpc")
            var shortType = type.Contains(':') ? type.Split(':').Last() : type;
            Console.WriteLine($"  {opName,-8} {shortType}: {name}");
            Console.ResetColor();
        }
        else if (e.DiagnosticEvent is { } diag && diag.Severity == "error")
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"  ERROR: {diag.Message}");
            Console.ResetColor();
        }
    }

    private static void PrintResourceChanges(string action, UpdateSummary summary)
    {
        if (summary.ResourceChanges == null || summary.ResourceChanges.Count == 0)
        {
            Console.WriteLine($"{action} complete. No resource changes.");
            return;
        }

        Console.WriteLine($"{action} complete. Resource changes:");
        foreach (var kv in summary.ResourceChanges)
        {
            Console.WriteLine($"  {kv.Key}: {kv.Value}");
        }
    }

    /// <summary>
    /// Typed outputs from the foundation phase.
    /// </summary>
    private record FoundationOutputs(
        INetworkOutputs Network,
        IComputeEnvironmentOutputs Compute,
        IDatabaseOutputs Database,
        IFileStorageOutputs FileStorage,
        IGateCheckerOutputs? GateChecker,
        ISeedTaskOutputs? SeedTask,
        Dictionary<string, object?> Exports);

    private FoundationOutputs DeployFoundation()
    {
        // Network: VPC, subnets, IGW, NAT, security groups, ALBs, ACM cert, Route 53, VPC Flow Logs
        var network = _factory.CreateNetwork();
        var networkOutputs = network.Deploy(_config);

        // Compute: ECS cluster + Cloud Map namespace
        var compute = _factory.CreateComputeEnvironment();
        var computeOutputs = compute.Deploy(_config, networkOutputs);

        // Database: RDS PostgreSQL + system secret
        var database = _factory.CreateDatabase();
        var databaseOutputs = database.Deploy(_config, networkOutputs);

        // File Storage: EFS + mount targets + access points
        var fileStorage = _factory.CreateFileStorage();
        var fileStorageOutputs = fileStorage.Deploy(_config, networkOutputs);

        // Gate Checker: Lambda for verifying EFS/DB data at gate-check time
        IGateCheckerOutputs? gateCheckerOutputs = null;
        var gateCheckerComponent = _factory.CreateGateChecker();
        if (gateCheckerComponent != null)
        {
            gateCheckerOutputs = gateCheckerComponent.Deploy(_config, networkOutputs, databaseOutputs, fileStorageOutputs);
        }

        // Seed Task: ECS task definition + ECR for seeder (when SeedData configured)
        ISeedTaskOutputs? seedTaskOutputs = null;
        var seedTaskComponent = _factory.CreateSeedTask();
        if (seedTaskComponent != null)
        {
            seedTaskOutputs = seedTaskComponent.Deploy(_config, networkOutputs, databaseOutputs, fileStorageOutputs);
        }

        // Auth service — deploy in foundation when no shared account
        // (ECS/Keycloak is deployed via SharedDeployment; Cognito is per-environment)
        IServiceOutputs? authOutputs = null;
        if (_system.Auth != null && string.IsNullOrEmpty(_config.CentralAuthDomain))
        {
            var authService = _factory.CreateAuthService();
            authOutputs = authService.Deploy(_config, networkOutputs, computeOutputs, databaseOutputs, fileStorageOutputs, false);
        }

        // Foundation-level services (e.g., LiveKit SFU) — shared across tenants
        var foundationServices = _system.FoundationLayerServices;
        foreach (var svcDef in foundationServices)
        {
            var serviceComponent = _factory.CreateService();
            var svcOutputs = serviceComponent.Deploy(
                svcDef.Name, svcDef, networkOutputs, computeOutputs, databaseOutputs, fileStorageOutputs);
        }

        var exports = new Dictionary<string, object?>
        {
            ["vpcId"] = networkOutputs.NetworkId,
            ["publicSubnetIds"] = networkOutputs.PublicSubnetIds,
            ["privateSubnetIds"] = networkOutputs.PrivateSubnetIds,
            ["clusterId"] = computeOutputs.ClusterId,
            ["dbEndpoint"] = databaseOutputs.Endpoint,
            ["efsId"] = fileStorageOutputs.FileSystemId,
        };

        // Export auth outputs for downstream tenant deployment
        if (authOutputs != null)
        {
            exports["authServiceId"] = authOutputs.ServiceId;
            exports["authEndpoint"] = authOutputs.Endpoint;

            // If the auth component provides per-pool details, export them
            if (authOutputs is IAuthPoolOutputs poolOutputs)
            {
                foreach (var (poolName, pool) in poolOutputs.Pools)
                {
                    exports[$"auth_{poolName}_userPoolId"] = pool.UserPoolId;
                    exports[$"auth_{poolName}_clientId"] = pool.ClientId;
                    exports[$"auth_{poolName}_metadataUrl"] = pool.MetadataUrl;
                    if (pool.HostedUIDomain != null)
                        exports[$"auth_{poolName}_hostedUIDomain"] = pool.HostedUIDomain;
                }
            }
        }

        if (gateCheckerOutputs != null)
            exports["gateCheckerFunctionName"] = gateCheckerOutputs.FunctionName;

        if (seedTaskOutputs != null)
        {
            exports["seederTaskFamily"] = seedTaskOutputs.TaskFamily;
            exports["seederEcrUrl"] = seedTaskOutputs.EcrRepositoryUrl;
        }

        return new FoundationOutputs(networkOutputs, computeOutputs, databaseOutputs, fileStorageOutputs, gateCheckerOutputs, seedTaskOutputs, exports);
    }

    private async Task<WorkspaceStack> CreateOrSelectStack(
        string stackName, Func<IDictionary<string, object?>> program)
    {
        PulumiPathResolver.EnsurePulumiOnPath();

        var projectName = $"lz-{_config.SystemKey}";
        var stackArgs = new InlineProgramArgs(projectName, stackName, PulumiFn.Create(program));

        var envVars = new Dictionary<string, string?>();

        if (_config.State != null && !string.IsNullOrEmpty(_config.State.Backend))
            envVars["PULUMI_BACKEND_URL"] = _config.State.Backend;

        if (_config.State != null && !string.IsNullOrEmpty(_config.State.SecretsProvider))
        {
            stackArgs.SecretsProvider = _config.State.SecretsProvider;
            envVars["PULUMI_CONFIG_PASSPHRASE"] = "";
        }

        if (!string.IsNullOrEmpty(_config.Region))
            envVars["AWS_REGION"] = _config.Region;
        if (!string.IsNullOrEmpty(_config.Profile))
            envVars["AWS_PROFILE"] = _config.Profile;

        stackArgs.EnvironmentVariables = envVars;

        var stack = await LocalWorkspace.CreateOrSelectStackAsync(stackArgs);

        if (!string.IsNullOrEmpty(_config.Region))
            await stack.SetConfigAsync("aws:region", new ConfigValue(_config.Region));

        Console.WriteLine($"Stack '{stackName}' ready (project: {projectName})");

        return stack;
    }
}
