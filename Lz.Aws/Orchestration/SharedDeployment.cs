using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Lz.Core.Orchestration;
using Pulumi.Automation;
using Pulumi.Automation.Events;

namespace Lz.Aws.Orchestration;

/// <summary>
/// Deployment orchestrator for the shared-services account.
/// Deploys centralized Keycloak + Tailscale subnet routers.
/// This must be deployed before any per-environment stacks.
///
/// Step 1: Pulumi up — VPC, ECS cluster, RDS, EFS, Keycloak (+ Tailscale if api-key exists)
///   GATE: keycloakconfig
/// Step 2: Post-deploy — DB init, scale Keycloak, seed realms, store Tailscale OIDC secret
///   GATE: tailscale-api-key
///   Auto: ensure tailscale-auth-key (create via API if missing/expired)
/// Step 3: Pulumi up — additive: Tailscale subnet routers (no-op if included in Step 1)
/// Step 4: Post-deploy — approve routes, disable key expiry, configure split DNS
/// </summary>
public class SharedDeployment
{
    private readonly IPlatformFactory _factory;
    private readonly SharedConfig _config;
    private readonly CancellationToken _ct;
    private bool _adminBlockingEnabled;

    public SharedDeployment(
        IPlatformFactory factory, SharedConfig config,
        CancellationToken cancellationToken = default)
    {
        _factory = factory;
        _config = config;
        _ct = cancellationToken;
    }

    /// <summary>
    /// AWS-extended factory view for capabilities not on the core interface.
    /// Null when the active factory isn't an AWS factory.
    /// </summary>
    private IAwsPlatformFactory? AwsFactory => _factory as IAwsPlatformFactory;

    /// <summary>
    /// Deploy the shared-services account infrastructure.
    /// </summary>
    public async Task RunAsync()
    {
        var stackName = "shared";
        var checker = _factory.CreateTransitionChecker();

        Console.WriteLine($"=== Shared-Services Deployment ===");
        Console.WriteLine($"Stack: {stackName}");
        Console.WriteLine($"Domain: {_config.Domain}");
        Console.WriteLine();

        // Check if Tailscale is configured — determines whether to block
        // public Keycloak admin access (auth.{domain}/admin/ via VPN only).
        // Uses tailscale-api-key as the signal: if the API key exists, Tailscale is
        // configured and auth keys will be auto-created before ASG deployment.
        _adminBlockingEnabled = await ResolveAdminBlockingAsync();

        // If Tailscale is configured, ensure a valid auth key exists before ASG deployment.
        // This creates an auth key via the API if missing or expired.
        if (_adminBlockingEnabled)
        {
            var keyManager = AwsFactory?.GetTailscaleKeyManager();
            if (keyManager != null)
            {
                await keyManager.EnsureAuthKeyAsync();
                await keyManager.EnsureSshKeyAsync();
            }
        }

        Console.WriteLine();

        // --- GATE: keycloakconfig must exist before deploying ---
        var keycloakConfigFile = $"keycloakconfig.shared.shared.yaml";
        var keycloakGates = new List<TransitionRequirement>
        {
            new TransitionRequirement
            {
                Name = "keycloakconfig",
                CheckType = TransitionCheckType.Custom,
                CustomCheck = () => Task.FromResult(
                    ConfigLoader.DiscoverConfigFile(Directory.GetCurrentDirectory(), keycloakConfigFile) != null),
                Description =
                    $"Keycloak seed configuration is required before deploying shared-services.\n" +
                    $"  1. Create '{keycloakConfigFile}' in the monorepo root.\n" +
                    $"     This file defines the adminsauth realm, OIDC clients, roles, and groups.\n" +
                    $"  2. Re-run: lz deployshared",
            },
        };

        Console.WriteLine("Checking pre-deploy gates...");
        var keycloakGatePassed = await TransitionGate.CheckAndReportAsync(
            checker, keycloakGates, "shared");

        if (!keycloakGatePassed)
            return;

        // --- Step 1: Pulumi up — core infra + Keycloak ---
        // Include Tailscale if already configured (keys exist) to avoid
        // destroying and recreating the ASG on every subsequent deploy.
        Console.WriteLine(_adminBlockingEnabled
            ? "Step 1: Deploying shared-services infrastructure + Keycloak + Tailscale..."
            : "Step 1: Deploying shared-services infrastructure + Keycloak...");
        var result = await PulumiUpAsync(stackName, includeTailscale: _adminBlockingEnabled);

        // --- Step 2: Post-deploy — DB init, scale Keycloak, seed realms ---
        var postDeploy = _factory.GetFoundationPostDeployAction();
        if (postDeploy != null)
        {
            Console.WriteLine();
            Console.WriteLine("Step 2: Running post-deploy actions (Keycloak init + seed)...");
            var outputs = result.Outputs.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Value);
            await postDeploy.ExecuteAsync(outputs);
        }

        // --- GATE: tailscale-api-key must exist before deploying Tailscale ---
        var tailscaleGates = new List<TransitionRequirement>
        {
            new TransitionRequirement
            {
                Name = "tailscale-api-key",
                CheckType = TransitionCheckType.SecretEntry,
                SecretName = "shared/system",
                CheckTarget = "tailscale-api-key",
                Description =
                    "Tailscale API key is required for managing subnet routers.\n" +
                    "  1. Create an API key at https://login.tailscale.com/admin/settings/keys\n" +
                    "  2. Add 'tailscale-api-key' to the 'shared/system' secret in Secrets Manager.\n" +
                    "  3. Re-run: lz deployshared",
            },
        };

        Console.WriteLine();
        Console.WriteLine("Checking Tailscale gate...");
        var tailscaleGatePassed = await TransitionGate.CheckAndReportAsync(
            checker, tailscaleGates, "shared");

        if (!tailscaleGatePassed)
            return; // Stop — user must add Tailscale API key and re-run

        // --- Step 3: Pulumi up — additive: Tailscale ---
        Console.WriteLine();
        Console.WriteLine("Step 3: Deploying Tailscale subnet routers...");
        result = await PulumiUpAsync(stackName, includeTailscale: true);

        // --- Step 4: Configure Tailscale devices ---
        var tailscalePostDeploy = AwsFactory?.GetTailscalePostDeployAction();
        if (tailscalePostDeploy != null)
        {
            Console.WriteLine();
            Console.WriteLine("Step 4: Configuring Tailscale subnet routers...");
            var tailscaleOutputs = result.Outputs.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Value);
            await tailscalePostDeploy.ExecuteAsync(tailscaleOutputs);
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Shared-services deployment complete.");
        Console.ResetColor();
    }

    /// <summary>
    /// Destroy the shared-services stack.
    /// </summary>
    public async Task DestroyAsync()
    {
        var stackName = "shared";
        Console.WriteLine($"Destroying shared-services stack '{stackName}'...");

        var stack = await CreateOrSelectStack(stackName, () => new Dictionary<string, object?>());

        var result = await stack.DestroyAsync(new DestroyOptions
        {
            OnEvent = SystemDeployment.HandleEngineEvent,
            OnStandardError = msg =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(msg);
                Console.ResetColor();
            },
        }, _ct);

        Console.WriteLine();
        if (result.Summary.ResourceChanges != null)
        {
            Console.WriteLine("Destroy complete. Resource changes:");
            foreach (var kv in result.Summary.ResourceChanges)
                Console.WriteLine($"  {kv.Key}: {kv.Value}");
        }
    }

    private async Task<UpResult> PulumiUpAsync(string stackName, bool includeTailscale)
    {
        var stack = await CreateOrSelectStack(stackName, BuildSharedProgram(includeTailscale));

        Console.WriteLine("Running Pulumi up...");
        Console.WriteLine();

        var result = await stack.UpAsync(new UpOptions
        {
            OnEvent = SystemDeployment.HandleEngineEvent,
            OnStandardError = msg =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(msg);
                Console.ResetColor();
            },
        }, _ct);

        Console.WriteLine();
        if (result.Summary.ResourceChanges != null)
        {
            Console.WriteLine("Update complete. Resource changes:");
            foreach (var kv in result.Summary.ResourceChanges)
                Console.WriteLine($"  {kv.Key}: {kv.Value}");
        }

        return result;
    }

    /// <summary>
    /// Build an AwsSystemConfig from AwsSharedConfig so we can reuse existing
    /// AWS components (network, compute, database, auth) which expect SystemConfig.
    /// </summary>
    private AwsSystemConfig ToSystemConfig()
    {
        var aws = _config.Aws();
        return new AwsSystemConfig
        {
            SystemKey = "shared",
            Environment = "shared",
            Profile = _config.Profile,
            Region = _config.Region,
            CentralAuthDomain = _config.Domain,
            VpcCidr = _config.VpcCidr,
            State = _config.State,
            AdminAuth = "adminsauth",
            TrustedAccountIds = aws.TrustedAccountIds,
            SystemSuffix = _config.SharedSuffix,
            ECS = new EcsConfig
            {
                KeycloakImageTag = aws.Keycloak.ImageTag,
                KeycloakCpu = aws.Keycloak.Cpu,
                KeycloakMemory = aws.Keycloak.Memory,
                KeycloakThemePath = aws.Keycloak.ThemePath ?? new EcsConfig().KeycloakThemePath,
                DbInstanceClass = _config.DbInstanceClass,
                DbAllocatedStorage = _config.DbAllocatedStorage,
                TailscaleInstanceType = aws.TailscaleInstanceType,
                TailscaleDesiredCapacity = aws.TailscaleDesiredCapacity,
                LogRetentionDays = _config.LogRetentionDays,
            },
        };
    }

    private record SharedInfraResult(
        INetworkOutputs Network,
        IFileStorageOutputs FileStorage,
        Dictionary<string, object?> Exports);

    private SharedInfraResult DeploySharedInfra()
    {
        var systemConfig = ToSystemConfig();

        // Network: VPC, subnets, ALBs, security groups, ACM cert, Route 53
        var network = _factory.CreateNetwork();
        var networkOutputs = network.Deploy(systemConfig);

        // Compute: ECS cluster + Cloud Map namespace
        var compute = _factory.CreateComputeEnvironment();
        var computeOutputs = compute.Deploy(systemConfig, networkOutputs);

        // Database: RDS PostgreSQL for Keycloak
        var database = _factory.CreateDatabase();
        var databaseOutputs = database.Deploy(systemConfig, networkOutputs);

        // File Storage: EFS for Keycloak themes
        var fileStorage = _factory.CreateFileStorage();
        var fileStorageOutputs = fileStorage.Deploy(systemConfig, networkOutputs);

        // Gate Checker: Lambda for EFS writes (theme deploy, future shared-level operations)
        var gateCheckerComponent = AwsFactory?.CreateGateChecker();
        if (gateCheckerComponent != null)
        {
            gateCheckerComponent.Deploy(systemConfig, networkOutputs, databaseOutputs, fileStorageOutputs);
        }

        // Seed Data: S3 bucket for cross-account data transfer
        var seedBucketName = AwsFactory?.CreateSeedBucket(_config, systemConfig.SystemKey);

        // Auth: Keycloak ECS task + service + listener rules
        var auth = _factory.CreateAuthService();
        var authOutputs = auth.Deploy(systemConfig, networkOutputs, computeOutputs, databaseOutputs, fileStorageOutputs, _adminBlockingEnabled);

        var exports = new Dictionary<string, object?>
        {
            ["vpcId"] = networkOutputs.NetworkId,
            ["publicSubnetIds"] = networkOutputs.PublicSubnetIds,
            ["privateSubnetIds"] = networkOutputs.PrivateSubnetIds,
            ["clusterId"] = computeOutputs.ClusterId,
            ["dbEndpoint"] = databaseOutputs.Endpoint,
            ["efsId"] = fileStorageOutputs.FileSystemId,
            ["keycloakEndpoint"] = authOutputs.Endpoint,
            ["seedBucket"] = seedBucketName,
        };

        return new SharedInfraResult(networkOutputs, fileStorageOutputs, exports);
    }

    /// <summary>The shared-services resource graph (shared by up and preview).</summary>
    private Func<IDictionary<string, object?>> BuildSharedProgram(bool includeTailscale)
        => () =>
        {
            var result = DeploySharedInfra();

            if (includeTailscale)
            {
                var tailscale = AwsFactory?.CreateTailscale();
                if (tailscale != null)
                {
                    var systemConfig = ToSystemConfig();
                    var tailscaleOutputs = tailscale.Deploy(systemConfig, result.Network, result.FileStorage);
                    result.Exports["tailscaleAsgId"] = tailscaleOutputs.AutoScalingGroupId;
                }
            }

            return result.Exports;
        };

    /// <summary>
    /// Resolve whether Keycloak admin blocking is enabled: tailscale-api-key
    /// present in the shared/system secret. Read-only. Shared by RunAsync and
    /// PreviewAsync so a preview models the SAME program a deploy would apply —
    /// both the admin-block listener rules and Tailscale inclusion key off this
    /// flag, and a preview that skips the check plans phantom deletes of the
    /// kc-admin rules while pairing (blocking OFF, tailscale ON), a state
    /// RunAsync can never produce.
    /// </summary>
    private async Task<bool> ResolveAdminBlockingAsync()
    {
        var checker = _factory.CreateTransitionChecker();
        var enabled = await checker.CheckAsync(new TransitionRequirement
        {
            Name = "tailscale-admin-check",
            CheckType = TransitionCheckType.SecretEntry,
            SecretName = "shared/system",
            CheckTarget = "tailscale-api-key",
        }, "shared");

        Console.WriteLine(enabled
            ? "  Admin blocking: ON (tailscale-api-key found — public admin access blocked)"
            : "  Admin blocking: OFF (tailscale-api-key not found — public admin access open)");

        return enabled;
    }

    /// <summary>
    /// Preview (read-only dry run) the shared-services stack. Returns true if the
    /// plan is destructive (any replace/delete). No changes are applied; no
    /// post-deploy actions run.
    /// </summary>
    public async Task<bool> PreviewAsync(bool refresh = false)
    {
        var stackName = "shared";
        Console.WriteLine($"=== Shared-services preview: {stackName} (no changes will be applied) ===");
        Console.WriteLine();

        // Mirror RunAsync's admin-blocking resolution so the previewed program
        // matches what a deploy would apply. Read-only — unlike RunAsync, no
        // Tailscale auth/SSH keys are created here.
        _adminBlockingEnabled = await ResolveAdminBlockingAsync();
        Console.WriteLine();

        var stack = await CreateOrSelectStack(stackName, BuildSharedProgram(includeTailscale: _adminBlockingEnabled));

        if (refresh)
        {
            Console.WriteLine("Running Pulumi refresh (syncing state with AWS — writes refreshed state)...");
            Console.WriteLine();
            await stack.RefreshAsync(new RefreshOptions
            {
                OnEvent = SystemDeployment.HandleEngineEvent,
                OnStandardError = SystemDeployment.PrintStdErr,
            }, _ct);
            Console.WriteLine();
        }

        Console.WriteLine("Running Pulumi preview...");
        Console.WriteLine();

        var result = await stack.PreviewAsync(new PreviewOptions
        {
            OnEvent = SystemDeployment.HandleEngineEvent,
            OnStandardError = SystemDeployment.PrintStdErr,
        }, _ct);

        Console.WriteLine();
        return SystemDeployment.PrintPreviewSummary(result.ChangeSummary);
    }

    /// <summary>
    /// Show the shared-services stack's last-deploy status (read-only).
    /// </summary>
    public async Task StatusAsync()
    {
        var stackName = "shared";
        try
        {
            var stack = await CreateOrSelectStack(stackName, () => new Dictionary<string, object?>());
            await SystemDeployment.PrintStackInfoAsync(stackName, stack);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Stack '{stackName}': {ex.Message}");
        }
    }

    private async Task<WorkspaceStack> CreateOrSelectStack(
        string stackName, Func<IDictionary<string, object?>> program)
    {
        PulumiPathResolver.EnsurePulumiOnPath();

        var projectName = "lz-shared";
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
