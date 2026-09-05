using Lz.Aws.Interfaces.Outputs;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Core.Orchestration;
using Pulumi;
using Pulumi.Automation;
using Pulumi.Automation.Events;
using Lz.Aws.Compute;
using Lz.Aws.Docker;

namespace Lz.Aws.Orchestration;

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

    /// <summary>
    /// AWS-extended factory view for the capabilities not on the core
    /// interface (Tailscale, Keycloak seeder, S3 seed bucket, etc.). Null
    /// if the active factory isn't an AWS factory — all call sites guard
    /// with null-conditional access.
    /// </summary>
    private IAwsPlatformFactory? AwsFactory => _factory as IAwsPlatformFactory;

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
    public async Task DeployFoundationAsync(string? tailscaleApiKey = null)
    {
        var stackName = $"{_config.SystemKey}-{_config.Environment}";
        var checker = _factory.CreateTransitionChecker();

        Console.WriteLine($"=== System Phase ===");
        Console.WriteLine($"Stack: {stackName}");
        Console.WriteLine($"Central auth: {_config.CentralAuthDomain}");
        Console.WriteLine();

        // --- Pre-flight: Check all gates before any Pulumi up ---

        if (_system.FoundationInfraGates.Count > 0)
        {
            Console.WriteLine("Checking system infrastructure gates...");
            var infraGatePassed = await TransitionGate.CheckAndReportAsync(
                checker, _system.FoundationInfraGates, _config.SystemKey);

            if (!infraGatePassed)
                return;
        }

        if (_system.FoundationGates.Count > 0)
        {
            Console.WriteLine("Checking system transition gates...");
            var gatePassed = await TransitionGate.CheckAndReportAsync(
                checker, _system.FoundationGates, _config.SystemKey);

            if (!gatePassed)
                return;
        }

        // Acquire the Tailscale key manager once. Non-null on any system whose
        // factory wires Tailscale — Monro (ecs-fargate-keycloak, always) or Scutara
        // (Fargate when PrivateNetwork.Tailscale). Null on the ~10 plain siblings.
        var keyManager = AwsFactory?.GetTailscaleKeyManager();

        // --- Tailscale gates + auto-seed (only for systems that use Tailscale) ---
        // Gate on (UsesVpn || keyManager != null): UsesVpn=true captures Monro (whose
        // topology declares it); keyManager!=null captures the Fargate private
        // Tailscale opt-in whose topology leaves UsesVpn=false. The ~10 plain siblings
        // (keyManager==null, UsesVpn==false) skip the whole block — byte-identical.
        if (_system.UsesVpn || keyManager != null)
        {
            // Auto-seed the API key BEFORE the gate, so a first-time single-account
            // deploy (Scutara) is one command: value from --tailscale-key or an
            // interactive masked prompt, written to the resolved system secret.
            // No-op when the key is already stored (e.g. Monro's shared/system).
            if (keyManager != null)
                await keyManager.EnsureApiKeySeededAsync(tailscaleApiKey);

            var tailscaleGates = new List<TransitionRequirement>
            {
                new TransitionRequirement
                {
                    Name = "tailscale-api-key",
                    CheckType = TransitionCheckType.SecretEntry,
                    SecretName = Lz.Aws.Tailscale.AwsTailscalePostDeployAction.ResolveSystemSecretId(_config),
                    Profile = _config.Aws().SharedProfile ?? _config.Profile,
                    Region = !string.IsNullOrEmpty(_config.Aws().SharedRegion)
                        ? _config.Aws().SharedRegion : _config.Region,
                    CheckTarget = "tailscale-api-key",
                    Description =
                        "Tailscale API key is required for managing subnet routers.\n" +
                        "  1. Create an API key at https://login.tailscale.com/admin/settings/keys\n" +
                        "  2. Pass it with:  lz deploysystem --tailscale-key <key>\n" +
                        "     (or add 'tailscale-api-key' to the system secret in Secrets Manager)\n" +
                        "  3. Re-run: lz deploysystem",
                },
            };

            Console.WriteLine("Checking Tailscale gates...");
            var tailscaleGatePassed = await TransitionGate.CheckAndReportAsync(
                checker, tailscaleGates, _config.SystemKey);

            if (!tailscaleGatePassed)
                return;
        }

        // --- Auto-ensure: create/refresh auth key + SSH key if missing or expired ---
        if (keyManager != null)
        {
            await keyManager.EnsureAuthKeyAsync();
            await keyManager.EnsureSshKeyAsync();
        }

        // --- Pre-deploy cleanup (e.g., clear stale private zone records) ---
        await _factory.CleanupBeforeFoundationAsync();

        // --- Pulumi up: deploy everything including Tailscale in one pass ---
        Console.WriteLine();
        Console.WriteLine("Deploying system infrastructure...");
        var result = await PulumiUpAsync(stackName, includeTailscale: true);

        // --- Post-deploy: Configure Tailscale devices ---
        var tailscalePostDeploy = AwsFactory?.GetTailscalePostDeployAction(_system);
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
            Console.WriteLine("Building and deploying system services...");
            var foundationOutputs = result.Outputs.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Value);
            await foundationServiceDeploy.ExecuteAsync(foundationOutputs);
        }

        // --- Post-deploy: imperative system-scope ensures (e.g. the {SystemKey}
        // system DynamoDB table on the Cognito topologies). Deliberately a
        // SEPARATE hook from GetFoundationPostDeployAction, which deployshared
        // runs in the shared-services account (Keycloak init) — before this hook
        // existed, systems that never run deployshared (lambda/ecsexpress/
        // apprunner) ended up with NO system table at all. Idempotent.
        var systemPostDeploy = _factory.GetSystemPostDeployAction();
        if (systemPostDeploy != null)
        {
            Console.WriteLine();
            Console.WriteLine("Running system post-deploy actions...");
            var systemOutputs = result.Outputs.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Value);
            await systemPostDeploy.ExecuteAsync(systemOutputs);
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("System deployment complete.");
        Console.ResetColor();
    }

    /// <summary>
    /// Run Pulumi up for the foundation stack.
    /// </summary>
    private async Task<UpResult> PulumiUpAsync(string stackName, bool includeTailscale)
    {
        var stack = await CreateOrSelectStack(stackName, BuildFoundationProgram(includeTailscale));

        // Cognito custom domains are internally CloudFront-backed: after a
        // destroy, the domain name stays "taken" until AWS releases the
        // internal distribution (~15 min, not queryable via any API). A rapid
        // destroy→redeploy therefore fails CreateUserPoolDomain with a 400
        // until the window clears — found by the teardown-redeploy drill,
        // 2026-07-12. `pulumi up` is resumable (partial state + the refresh
        // below re-sync each attempt), so retry on exactly that error.
        const int maxAttempts = 15;
        var retryDelay = TimeSpan.FromMinutes(2);

        for (var attempt = 1; ; attempt++)
        {
            // Always refresh before up — catch state drift from cross-stack
            // operations, manual changes, or prior failed deploys (including
            // our own previous attempt).
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

            try
            {
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
            catch (Pulumi.Automation.Commands.Exceptions.CommandException ex) when (
                attempt < maxAttempts
                && ex.Message.Contains("CreateUserPoolDomain", StringComparison.Ordinal))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine(
                    $"Cognito custom domain not yet released by AWS after the previous " +
                    $"destroy (attempt {attempt}/{maxAttempts}). Waiting " +
                    $"{retryDelay.TotalMinutes:0} min and retrying the up...");
                Console.ResetColor();
                await Task.Delay(retryDelay, _ct);
            }
        }
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
        var splitDnsTask = AwsFactory?.UpdateTenantSplitDnsAsync(tenantConfig);
        if (splitDnsTask != null) await splitDnsTask;

        // --- Init config: create tenant DB + write Settings.txt/usersettings.json ---
        var configInit = _factory.GetConfigInitRunner();
        if (configInit != null)
        {
            var ecs = tenantConfig.Aws().ECS ?? new EcsConfig();
            var dbName = ecs.DatabaseName
                ?? $"{_config.SystemKey}_{tenantKey}_{_config.Environment}_smartstore";
            var appUser = $"{_config.SystemKey}_{tenantKey}_app";
            var platformDbName = ecs.PlatformDatabaseName; // null if not configured — Lambda skips platform DB creation

            // S3-native media seed: when the tenant opts into "s3" storage, the
            // Lambda seeds media straight into the bucket and activates the provider.
            var mediaStorage = tenantConfig.MediaStorage ?? "filesystem";
            var mediaBucket = tenantConfig.MediaBucket
                ?? $"{_config.SystemKey}-{tenantKey}--media--{tenantConfig.TenantSuffix}";

            Console.WriteLine();
            Console.WriteLine("Initializing tenant config (database + EFS config files)...");
            var initOk = await configInit.RunInitConfigAsync(tenantKey, dbName, appUser,
                userSettings: tenantConfig.Smartstore,
                platformDatabaseName: platformDbName,
                mediaBucket: mediaBucket,
                mediaStorage: mediaStorage);
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
            var ecs2 = tenantConfig.Aws().ECS ?? new EcsConfig();
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
            var ecsAdmin = tenantConfig.Aws().ECS ?? new EcsConfig();
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
        var keycloakSeeder = (_factory as IAwsPlatformFactory)?.GetTenantKeycloakSeeder();
        if (keycloakSeeder != null)
        {
            var seedConfig = AwsKeycloakConfigLoader.DiscoverTenantKeycloakSeedConfig(
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
        // Before the program closure is built — see PreviewTenantAsync, which must do the
        // same thing so plan and apply agree.
        await ResolveImageDigestsAsync(tenantKey, tenantConfig);

        var stack = await CreateOrSelectStack(stackName, BuildTenantProgram(tenantKey, tenantConfig));

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
        Console.WriteLine($"Destroying system stack '{stackName}'...");
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
        // Select-only: destroying a never-deployed stack must not CREATE an
        // empty stack in the backend as a side effect.
        WorkspaceStack stack;
        try
        {
            stack = await SelectStackReadOnly(stackName);
        }
        catch (Pulumi.Automation.Commands.Exceptions.StackNotFoundException)
        {
            Console.WriteLine($"Stack '{stackName}': not deployed — nothing to destroy.");
            return;
        }

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
    /// Show deployment status for the foundation stack, including the DEPLOYED
    /// topology vs the CONFIGURED one (drift-flagged). Deployed comes from the
    /// stack's `topology` output (recorded by deploysystem); older stacks that
    /// predate the output fall back to inference from the foundation's network
    /// component type.
    /// </summary>
    public async Task StatusFoundationAsync()
    {
        var stackName = $"{_config.SystemKey}-{_config.Environment}";
        try
        {
            // Select-only: status is read-only and must not create an empty
            // stack when probing a never-deployed system.
            var stack = await SelectStackReadOnly(stackName);
            await PrintStackInfoAsync(stackName, stack);

            string? deployed = null;
            var outputs = await stack.GetOutputsAsync();
            if (outputs.TryGetValue("topology", out var t) && t.Value is string s && !string.IsNullOrEmpty(s))
                deployed = s;
            deployed ??= await InferTopologyFromStateAsync(stack, foundation: true);
            PrintTopologyLine(deployed, source: outputs.ContainsKey("topology") ? "stack output" : "inferred");
        }
        catch (Pulumi.Automation.Commands.Exceptions.StackNotFoundException)
        {
            Console.WriteLine($"Stack '{stackName}': not deployed (no stack in the backend).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Stack '{stackName}': {ex.Message}");
        }
    }

    /// <summary>
    /// Show deployment status for a single tenant stack, including the tenant's
    /// DEPLOYED compute topology inferred from its resource types (the tenant
    /// service component type is a definitive per-topology discriminator).
    /// </summary>
    public async Task StatusTenantAsync(string tenantKey)
    {
        var tenantStackName = $"{_config.SystemKey}-{tenantKey}-{_config.Environment}";
        try
        {
            // Select-only — see StatusFoundationAsync.
            var stack = await SelectStackReadOnly(tenantStackName);
            await PrintStackInfoAsync(tenantStackName, stack);
            PrintTopologyLine(await InferTopologyFromStateAsync(stack, foundation: false), source: "inferred from tenant compute");
        }
        catch (Pulumi.Automation.Commands.Exceptions.StackNotFoundException)
        {
            Console.WriteLine($"Stack '{tenantStackName}': not deployed (no stack in the backend).");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Stack '{tenantStackName}': {ex.Message}");
        }
    }

    private void PrintTopologyLine(string? deployed, string source)
    {
        var configured = _config.Topology;
        if (deployed == null)
        {
            Console.WriteLine($"  Topology: {configured} (configured); deployed: unknown — run deploysystem/deploytenant to record it");
            return;
        }
        // Containment (not equality) so the foundation's ambiguous no-VPC inference
        // ("apprunner or lambda-…") counts as a match for either configured value.
        if (deployed.Contains(configured, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  Topology: {deployed} (deployed = configured; {source})");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Topology: {deployed} (deployed, {source}) != {configured} (configured) -- DRIFT: config changed since the last deploy");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Infer the deployed topology from the Pulumi state's component-resource types.
    /// Tenant stacks are definitive (each topology has a distinct tenant-service
    /// type). Foundation stacks are definitive except that apprunner and
    /// lambda-cognito-dynamodb share the same no-VPC network — for that
    /// pair the tenant compute is the tiebreaker, so the foundation reports the
    /// ambiguity honestly.
    /// </summary>
    private static async Task<string?> InferTopologyFromStateAsync(WorkspaceStack stack, bool foundation)
    {
        try
        {
            var export = await stack.ExportStackAsync();
            if (!export.Json.TryGetProperty("deployment", out var dep)
                || !dep.TryGetProperty("resources", out var resources)) return null;

            var types = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in resources.EnumerateArray())
                if (r.TryGetProperty("type", out var ty) && ty.GetString() is { } tyStr)
                    types.Add(tyStr);

            if (!foundation)
            {
                if (types.Contains("lz:aws:LambdaTenantService")) return "lambda-cognito-dynamodb";
                if (types.Contains("lz:aws:EcsExpressTenantService")) return "ecs-fargate-cognito-dynamodb";
                if (types.Contains("lz:aws:EcsTenantService")) return "ecs-fargate-keycloak";
                if (types.Contains("lz:aws:AppRunnerTenantService")) return "apprunner";
                return null;
            }

            if (types.Contains("lz:aws:EcsExpressNetwork")) return "ecs-fargate-cognito-dynamodb";
            if (types.Contains("lz:aws:EcsNetwork")) return "ecs-fargate-keycloak";
            if (types.Contains("lz:aws:AppRunnerNetwork")) return "apprunner or lambda-cognito-dynamodb (shared no-VPC network — see the tenant compute line)";
            return null;
        }
        catch
        {
            return null; // status must never fail over inference
        }
    }

    /// <summary>
    /// Print the last-deploy summary (end time, result, resource-change counts)
    /// for a stack. Shared by the system, tenant, and shared-services status.
    /// </summary>
    internal static async Task PrintStackInfoAsync(string stackName, WorkspaceStack stack)
    {
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

        // Vector store: OpenSearch Serverless (aoss) collection for semantic
        // matching — opt-in via systemconfig VectorStore (absent = nothing
        // provisioned; see VectorStoreConfig). Foundation-owned so it exists in
        // every account the system deploys to — the original out-of-band
        // collection was silently left behind by an account migration, which
        // deploy ownership makes impossible to repeat.
        Lz.Aws.VectorStore.AwsVectorStoreComponent? vectorStore = null;
        if (_config.VectorStore != null)
            vectorStore = new Lz.Aws.VectorStore.AwsVectorStoreComponent(_config);

        // Gate Checker: Lambda for verifying EFS/DB data at gate-check time
        IGateCheckerOutputs? gateCheckerOutputs = null;
        var gateCheckerComponent = AwsFactory?.CreateGateChecker();
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
            // Record which topology this deploy actually provisioned, so `lz status`
            // can report the DEPLOYED topology (and flag drift against the config)
            // without inferring it from resource types.
            ["topology"] = _config.Topology,
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
                    if (pool.Authority != null)
                        exports[$"auth_{poolName}_authority"] = pool.Authority;
                    // BFF confidential-client outputs — only present when the
                    // pool set ProvisionBffClient. Null otherwise, so unconfigured
                    // pools export exactly the same keys as before.
                    if (pool.BffClientId != null)
                        exports[$"auth_{poolName}_bffClientId"] = pool.BffClientId;
                    if (pool.BffClientSecret != null)
                        exports[$"auth_{poolName}_bffClientSecret"] = pool.BffClientSecret;
                    // Smartstore confidential-client outputs — only present when the
                    // pool set ProvisionSmartstoreClient. Null otherwise, so
                    // unconfigured pools export exactly the same keys as before.
                    if (pool.SmartstoreClientId != null)
                        exports[$"auth_{poolName}_smartstoreClientId"] = pool.SmartstoreClientId;
                    if (pool.SmartstoreClientSecret != null)
                        exports[$"auth_{poolName}_smartstoreClientSecret"] = pool.SmartstoreClientSecret;
                }

                // Combined poolName -> userPoolId map (JSON) so a tenant stack can
                // emit the LZ_AUTH_{POOL}_USERPOOLID env vars the AppHost's
                // DiscoverAuthenticators REQUIRES, without enumerating dynamic
                // per-pool stack-output keys. (The Fargate topology previously
                // never injected these, so its AppHost container crash-looped with
                // "No authenticators configured.")
                var poolIdEntries = poolOutputs.Pools
                    .Select(kv => kv.Value.UserPoolId.Apply(id =>
                        new System.Collections.Generic.KeyValuePair<string, string>(kv.Key, id)))
                    .ToArray();
                exports["auth_userPoolIdsJson"] = Output.All(poolIdEntries)
                    .Apply(entries => System.Text.Json.JsonSerializer.Serialize(
                        entries.ToDictionary(e => e.Key, e => e.Value)));
            }
        }

        if (gateCheckerOutputs != null)
            exports["gateCheckerFunctionName"] = gateCheckerOutputs.FunctionName;

        if (seedTaskOutputs != null)
        {
            exports["seederTaskFamily"] = seedTaskOutputs.TaskFamily;
            exports["seederImageRepoUrl"] = seedTaskOutputs.ContainerImageRepositoryUrl;
        }

        // Tenant stacks consume these via StackReference: the endpoint becomes
        // the service's OpenSearch__Endpoint env var; the ARN scopes its
        // aoss:APIAccessAll IAM statement. Absent when not opted in.
        if (vectorStore != null)
        {
            exports["vectorStoreEndpoint"] = vectorStore.CollectionEndpoint;
            exports["vectorStoreCollectionArn"] = vectorStore.CollectionArn;
            exports["vectorStoreCollectionName"] = vectorStore.CollectionName;
        }

        return new FoundationOutputs(networkOutputs, computeOutputs, databaseOutputs, fileStorageOutputs, gateCheckerOutputs, seedTaskOutputs, exports);
    }

    // ---------------------------------------------------------------
    // Pulumi programs (shared by up and preview so a preview faithfully
    // predicts what an up would do)
    // ---------------------------------------------------------------

    /// <summary>The foundation resource graph (VPC/compute/data/auth + optional Tailscale).</summary>
    private Func<IDictionary<string, object?>> BuildFoundationProgram(bool includeTailscale)
        => () =>
        {
            var foundation = DeployFoundation();
            var exports = new Dictionary<string, object?>(foundation.Exports);

            if (includeTailscale)
            {
                var tailscale = AwsFactory?.CreateTailscale();
                if (tailscale != null)
                {
                    var tailscaleOutputs = tailscale.Deploy(_config, foundation.Network, foundation.FileStorage);
                    exports["tailscaleAsgId"] = tailscaleOutputs.AutoScalingGroupId;
                }
            }

            return exports;
        };

    /// <summary>The tenant resource graph (tenant data + services + CDN + DNS).</summary>
    private Func<IDictionary<string, object?>> BuildTenantProgram(string tenantKey, TenantConfig tenantConfig)
        => () =>
        {
            // Look up existing foundation resources (created by deploysystem).
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
        };

    // ---------------------------------------------------------------
    // Preview (read-only dry run) — no post-deploy actions ever run
    // ---------------------------------------------------------------

    /// <summary>
    /// Preview the foundation stack. Returns true if the plan is destructive
    /// (contains any replace/delete). No changes are applied.
    /// </summary>
    public Task<bool> PreviewFoundationAsync(bool refresh = false)
    {
        var stackName = $"{_config.SystemKey}-{_config.Environment}";
        Console.WriteLine($"=== Foundation preview: {stackName} (no changes will be applied) ===");
        Console.WriteLine();
        return PulumiPreviewAsync(stackName, BuildFoundationProgram(includeTailscale: true), refresh);
    }

    /// <summary>
    /// Preview a tenant stack. Returns true if the plan is destructive
    /// (contains any replace/delete). No changes are applied.
    /// </summary>
    public async Task<bool> PreviewTenantAsync(string tenantKey, TenantConfig tenantConfig, bool refresh = false)
    {
        var stackName = $"{_config.SystemKey}-{tenantKey}-{_config.Environment}";
        Console.WriteLine($"=== Tenant preview: {tenantKey} ({stackName}) (no changes will be applied) ===");
        Console.WriteLine();
        // MUST run here too, not only on the deploy path. The preview and the deploy build
        // the SAME program closure, so resolving on one side only would have the preview
        // plan a different image than the deploy applies — a preview that lies in exactly
        // the direction that matters.
        await ResolveImageDigestsAsync(tenantKey, tenantConfig);
        return await PulumiPreviewAsync(stackName, BuildTenantProgram(tenantKey, tenantConfig), refresh);
    }

    /// <summary>
    /// Resolve each tenant service's container-image digest from ECR into
    /// <see cref="TenantConfig.ResolvedImageDigests"/>, before the Pulumi program is built.
    ///
    /// <para><b>Imperative on purpose.</b> The obvious alternative — a plan-time
    /// <c>aws.ecr.getImage</c> invoke — is disqualified by the bootstrap case:
    /// <c>lz previewtenant</c> is documented to work before <c>deploycontainer</c> ("the
    /// image need not exist yet"), and that data source raises a provider ERROR when the
    /// repository or tag is absent, which would turn the first preview on every new system
    /// into a failure.</para>
    ///
    /// <para>A null digest is NOT an error: it falls back to the tag, which is the same
    /// branch a non-opted-in system takes. Reachable on the deploy path too, not just on
    /// preview — the deploytenant pre-flight accepts a repository holding ONLY untagged or
    /// <c>b-</c>-tagged images, which passes the gate while yielding no <c>:latest</c>
    /// digest.</para>
    /// </summary>
    private async Task ResolveImageDigestsAsync(string tenantKey, TenantConfig tenantConfig)
    {
        // Not opted in => zero AWS calls, nothing populated, byte-identical behaviour.
        if (!ImagePinPolicy.ForTenantService(_config.Rollback).PinDigest) return;

        var profile = tenantConfig.Profile ?? _config.Profile;
        var region = tenantConfig.Region ?? _config.Region;

        foreach (var svc in _system.ServiceLayerServices.Concat(_system.HostLayerServices))
        {
            if (svc.Docker == null) continue;

            // Same formula as the component (AwsFargateTenantServiceComponent) and the
            // deploytenant pre-flight, so all three agree on which repository is meant.
            var ecrName = $"{_config.SystemKey}-{tenantConfig.TenantSuffix}-{_config.Environment}-{tenantKey}-{svc.Name}";
            var digest = await EcrDeployer.GetImageDigestAsync(profile, region, ecrName, "latest");

            if (digest is null)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"  No :latest digest for {ecrName} — image reference falls back to the tag " +
                    $"(this is normal before the first deploycontainer).");
                Console.ResetColor();
                continue;
            }

            tenantConfig.ResolvedImageDigests[svc.Name] = digest;
            Console.WriteLine($"  {svc.Name}: pinning image digest {digest}");
        }
    }

    /// <summary>
    /// Run Pulumi preview for a stack. Preview never mutates the world; if
    /// <paramref name="refresh"/> is set it first runs a refresh (which writes the
    /// refreshed state) so the diff reflects live AWS state. Returns true if the
    /// plan contains any replace/delete operation — used by <c>--fail-on-replace</c>.
    /// </summary>
    private async Task<bool> PulumiPreviewAsync(
        string stackName, Func<IDictionary<string, object?>> program, bool refresh)
    {
        var stack = await CreateOrSelectStack(stackName, program);

        if (refresh)
        {
            Console.WriteLine("Running Pulumi refresh (syncing state with AWS — writes refreshed state)...");
            Console.WriteLine();
            await stack.RefreshAsync(new RefreshOptions
            {
                OnEvent = HandleEngineEvent,
                OnStandardError = PrintStdErr,
            }, _ct);
            Console.WriteLine();
        }

        Console.WriteLine("Running Pulumi preview...");
        Console.WriteLine();

        var result = await stack.PreviewAsync(new PreviewOptions
        {
            OnEvent = HandleEngineEvent,
            OnStandardError = PrintStdErr,
        }, _ct);

        Console.WriteLine();
        return PrintPreviewSummary(result.ChangeSummary);
    }

    /// <summary>
    /// Print the preview change summary and return whether it is destructive
    /// (contains any replace or delete operation).
    /// </summary>
    internal static bool PrintPreviewSummary(IReadOnlyDictionary<OperationType, int> changeSummary)
    {
        var meaningful = changeSummary?.Where(kv => kv.Key != OperationType.Same).ToList()
                         ?? new List<KeyValuePair<OperationType, int>>();
        if (meaningful.Count == 0)
        {
            Console.WriteLine("Preview: no changes.");
            return false;
        }

        Console.WriteLine("Preview — planned changes:");
        foreach (var kv in meaningful)
            Console.WriteLine($"  {kv.Key.ToString().ToLowerInvariant()}: {kv.Value}");

        changeSummary!.TryGetValue(OperationType.Replace, out var replaces);
        changeSummary.TryGetValue(OperationType.Delete, out var deletes);
        var destructive = replaces + deletes > 0;
        if (destructive)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠ destructive: {replaces} replace, {deletes} delete — stateful resources may be recreated.");
            Console.ResetColor();
        }
        return destructive;
    }

    internal static void PrintStdErr(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(msg);
        Console.ResetColor();
    }

    private async Task<WorkspaceStack> CreateOrSelectStack(
        string stackName, Func<IDictionary<string, object?>> program)
    {
        var stackArgs = BuildStackArgs(stackName, program);
        var stack = await LocalWorkspace.CreateOrSelectStackAsync(stackArgs);

        if (!string.IsNullOrEmpty(_config.Region))
            await stack.SetConfigAsync("aws:region", new ConfigValue(_config.Region));

        Console.WriteLine($"Stack '{stackName}' ready (project: lz-{_config.SystemKey})");

        return stack;
    }

    /// <summary>
    /// Select an EXISTING stack without creating one. Used by the read-only
    /// paths (status) and by destroy — probing a never-deployed system must
    /// not leave an empty stack behind in the backend. Throws
    /// <see cref="Pulumi.Automation.Commands.Exceptions.StackNotFoundException"/>
    /// when the stack does not exist.
    /// </summary>
    private async Task<WorkspaceStack> SelectStackReadOnly(string stackName)
    {
        var stackArgs = BuildStackArgs(
            stackName, () => new Dictionary<string, object?>());
        return await LocalWorkspace.SelectStackAsync(stackArgs);
    }

    private InlineProgramArgs BuildStackArgs(
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
        return stackArgs;
    }
}
