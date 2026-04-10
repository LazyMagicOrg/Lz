using System.CommandLine;
using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Orchestration;
using Lz.Core.Plugin;
using Lz.Core.Validation;
using Lz.Aws;
using Lz.Aws.Docker;
using Lz.Aws.Ecs;
using Lz.Aws.Webapp;

namespace Lz.Cli;

class Program
{
    /// <summary>
    /// Global cancellation token source — wired to Console.CancelKeyPress (Ctrl+C).
    /// Passed through to all Pulumi operations so they abort gracefully.
    /// </summary>
    internal static readonly CancellationTokenSource Cts = new();

    static async Task<int> Main(string[] args)
    {
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // Prevent immediate process termination
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Cancellation requested — waiting for current operation to finish...");
            Console.WriteLine("Press Ctrl+C again to force-kill.");
            Console.ResetColor();
            if (Cts.IsCancellationRequested)
            {
                // Second Ctrl+C — force exit
                Environment.Exit(1);
            }
            Cts.Cancel();
        };

        var rootCommand = new RootCommand("Lz infrastructure deployment tool");

        // Load plugin (optional — core commands work without one)
        ILzPlugin? plugin = null;
        try
        {
            plugin = PluginLoader.LoadPlugin();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: Plugin load failed: {ex.Message}");
            Console.ResetColor();
        }

        // Shared options used across multiple commands
        var systemKeyOption = new Option<string?>("--systemkey",
            "System key (auto-detected if only one systemconfig exists for the env)");
        var envOption = new Option<string?>("--env",
            "Environment (auto-detected from folder hierarchy: _Dev → dev, _Test → test, _Prod → prod)");

        RegisterDeploySharedCommand(rootCommand);
        RegisterDeployFoundationCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDeployContainerCommand(rootCommand, systemKeyOption, envOption);
        RegisterDeployWebappCommand(rootCommand, systemKeyOption, envOption);
        RegisterDeployTenantCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDestroySharedCommand(rootCommand);
        RegisterDestroyFoundationCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDestroyTenantCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterStatusCommand(rootCommand, plugin, systemKeyOption, envOption);

        // Plugin-specific commands (e.g., seed)
        plugin?.RegisterCommands(rootCommand);

        return await rootCommand.InvokeAsync(args);
    }

    // ---------------------------------------------------------------
    // deployshared
    // ---------------------------------------------------------------

    private static void RegisterDeploySharedCommand(RootCommand root)
    {
        var cmd = new Command("deployshared",
            "Deploy shared-services infrastructure (Keycloak + Tailscale)");

        var themeOption = new Option<string?>("--theme",
            "Deploy only a Keycloak theme to EFS (skips full shared deploy)");
        cmd.AddOption(themeOption);

        cmd.SetHandler(async (themeName) =>
        {
            var sharedConfig = ConfigLoader.DiscoverAndLoadSharedConfig();

            // --theme: ad-hoc theme deploy only (no Pulumi, no full shared deploy)
            if (!string.IsNullOrEmpty(themeName))
            {
                var themeSourcePath = Path.Combine("keycloakthemes", themeName);
                if (!Path.IsPathRooted(themeSourcePath))
                    themeSourcePath = Path.GetFullPath(themeSourcePath);

                if (!Directory.Exists(themeSourcePath))
                {
                    Console.Error.WriteLine($"Theme directory not found: {themeSourcePath}");
                    Environment.ExitCode = 1;
                    return;
                }

                // Build a minimal SystemConfig with shared-account credentials
                var config = new SystemConfig
                {
                    SystemKey = "shared",
                    Environment = "shared",
                    Profile = sharedConfig.Profile,
                    Region = sharedConfig.Region,
                    SharedProfile = sharedConfig.Profile,
                    SharedRegion = sharedConfig.Region,
                };

                var themesBucket = $"keycloak-themes-{sharedConfig.SharedSuffix}";
                Console.WriteLine($"Deploying theme '{themeName}' from {themeSourcePath}...");
                var runner = new Lz.Aws.Lambda.AwsLambdaThemeDeployRunner(config, themesBucket);
                var success = await runner.DeployThemeAsync(themeName, themeSourcePath);

                if (!success)
                {
                    Console.Error.WriteLine("Theme deployment failed.");
                    Environment.ExitCode = 1;
                }
                return;
            }

            Console.WriteLine("Shared-services deployment");
            Console.WriteLine($"  Domain: {sharedConfig.Domain}");
            Console.WriteLine();

            // Ensure Pulumi state backend (S3 bucket + KMS key) exists
            if (sharedConfig.State != null)
                await AwsStateBootstrapper.BootstrapAsync(
                    sharedConfig.Profile, sharedConfig.Region, sharedConfig.State);

            var factory = CreateFactory(new SystemConfig
            {
                SystemKey = "shared",
                Environment = "shared",
                Platform = "aws",
                Topology = "ecs",
                Profile = sharedConfig.Profile,
                Region = sharedConfig.Region,
                SystemDomain = sharedConfig.Domain,
                VpcCidr = sharedConfig.VpcCidr,
                AdminAuth = "adminsauth",
                TrustedAccountIds = sharedConfig.TrustedAccountIds,
            });
            var deployment = new SharedDeployment(factory, sharedConfig, Cts.Token);
            await deployment.RunAsync();
        }, themeOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // deployfoundation
    // ---------------------------------------------------------------

    private static void RegisterDeployFoundationCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("deployfoundation",
            "Deploy foundation infrastructure (VPC, ECS, RDS, EFS)");

        var platformOption = new Option<string?>("--platform", "Override platform from config");
        var topologyOption = new Option<string?>("--topology", "Override topology from config");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(platformOption);
        cmd.AddOption(topologyOption);

        cmd.SetHandler(async (systemKey, env, platform, topology) =>
        {
            RequirePlugin(plugin, "deployfoundation");

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                if (platform != null) config.Platform = platform;
                if (topology != null) config.Topology = topology;

                // Resolve cross-account shared services references
                SharedConfig? sharedConfig = null;
                if (!string.IsNullOrEmpty(config.SharedProfile))
                {
                    // Use the shared account's region from sharedconfig.yaml, not the system's region
                    sharedConfig = ConfigLoader.DiscoverAndLoadSharedConfig();
                    var sharedRegion = sharedConfig.Region;

                    var sharedAccountId = await AwsAccountResolver.ResolveAccountIdAsync(
                        config.SharedProfile, sharedRegion);
                    config.SharedSecretArn =
                        $"arn:aws:secretsmanager:{sharedRegion}:{sharedAccountId}:secret:shared/system";
                    config.SharedRegion = sharedRegion;

                    // Resolve actual KMS key ARN — alias ARNs can't be used in IAM policy resources
                    config.SharedKmsKeyArn = await AwsAccountResolver.ResolveKmsKeyArnAsync(
                        config.SharedProfile, sharedRegion, "alias/shared-secrets-key");

                    Console.WriteLine($"  Shared account: {sharedAccountId}");
                    Console.WriteLine($"  Shared secret:  {config.SharedSecretArn}");
                    if (config.SharedKmsKeyArn != null)
                        Console.WriteLine($"  Shared KMS key: {config.SharedKmsKeyArn}");
                }

                // Propagate seed bucket config from shared account (only if shared config exists)
                if (sharedConfig == null)
                {
                    try { sharedConfig = ConfigLoader.DiscoverAndLoadSharedConfig(); }
                    catch { /* No sharedconfig.yaml — OK for topologies that don't use shared services */ }
                }
                if (sharedConfig != null)
                    ConfigLoader.PropagateSharedSeedData(config, sharedConfig);

                // Ensure Pulumi state backend (S3 bucket + KMS key) exists
                if (config.State != null)
                    await AwsStateBootstrapper.BootstrapAsync(
                        config.Profile, config.Region, config.State);

                var (system, factory) = PrepareSystem(plugin!, config);

                Console.WriteLine($"System: {config.SystemKey}, Environment: {config.Environment}");
                Console.WriteLine($"Platform: {config.Platform}, Topology: {config.Topology}");
                Console.WriteLine($"Domain: {config.SystemDomain}");
                Console.WriteLine();

                var deployment = new SystemDeployment(factory, system, config, Cts.Token);
                await deployment.DeployFoundationAsync();
            }
        }, systemKeyOption, envOption, platformOption, topologyOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // deploycontainer
    // ---------------------------------------------------------------

    private static void RegisterDeployContainerCommand(
        RootCommand root,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("deploycontainer",
            "Build and push container images to ECR");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (auto-detected if only one tenant)");
        var containerOption = new Option<string?>("--container",
            "Container name to build (builds all if not specified)");
        var tagOption = new Option<string>("--tag",
            () => "latest", "Docker image tag");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(containerOption);
        cmd.AddOption(tagOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, container, tag) =>
        {
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var deployer = new EcrDeployer();

                // Load both foundation and tenant container configs
                var foundationConfig = ConfigLoader
                    .DiscoverAndLoadFoundationContainerConfig(config.SystemKey, config.Environment);
                var tenantServiceConfig = ConfigLoader
                    .DiscoverAndLoadContainerServiceConfig(config.SystemKey, config.Environment);

                // Check if the requested container is a foundation container
                bool isFoundation = container != null
                    && foundationConfig.Containers.ContainsKey(container);

                // --- Foundation containers (system-scoped ECR, no tenant key) ---
                if (isFoundation || (container == null && foundationConfig.Containers.Count > 0))
                {
                    var foundationContainers = container != null
                        ? foundationConfig.Containers
                            .Where(c => c.Key.Equals(container, StringComparison.OrdinalIgnoreCase))
                            .ToDictionary(c => c.Key, c => c.Value)
                        : foundationConfig.Containers;

                    foreach (var (svcName, def) in foundationContainers)
                    {
                        var ecrName = $"{config.SystemKey}-{config.SystemSuffix}-{config.Environment}-{svcName}";

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"=== {svcName} (foundation) ===");
                        Console.ResetColor();

                        await deployer.DeployAsync(
                            svcName, def,
                            foundationConfig.ConfigDirectory,
                            ecrName,
                            config.Profile,
                            config.Region,
                            tag);
                    }

                    // If a specific foundation container was requested, we're done
                    if (isFoundation)
                        continue;
                }

                // --- Tenant containers (tenant-scoped ECR) ---
                // Skip if the requested container was already handled as foundation
                if (isFoundation)
                    continue;

                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, tenantConfig) in tenants)
                {
                    var containersToProcess = container != null
                        ? tenantServiceConfig.Containers
                            .Where(c => c.Key.Equals(container, StringComparison.OrdinalIgnoreCase))
                            .ToDictionary(c => c.Key, c => c.Value)
                        : tenantServiceConfig.Containers;

                    if (container != null && containersToProcess.Count == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine($"Warning: Container '{container}' not found in servicesconfig.");
                        Console.ResetColor();
                        continue;
                    }

                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region;

                    foreach (var (svcName, def) in containersToProcess)
                    {
                        // ECR repo is tenant-scoped: {sk}-{suffix}-{env}-{tk}-{container}
                        // Must match AwsEcsTenantServiceComponent.cs line 73
                        var ecrName = $"{config.SystemKey}-{tenantConfig.TenantSuffix}-{config.Environment}-{tk}-{svcName}";

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"=== {svcName} for tenant {tk} ===");
                        Console.ResetColor();

                        await deployer.DeployAsync(
                            svcName, def,
                            tenantServiceConfig.ConfigDirectory,
                            ecrName,
                            profile,
                            region,
                            tag);
                    }
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption, containerOption, tagOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // deploywebapp
    // ---------------------------------------------------------------

    private static void RegisterDeployWebappCommand(
        RootCommand root,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("deploywebapp",
            "Build and deploy a Blazor WASM web application to S3 + CloudFront");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (auto-detected if only one tenant)");
        var webappOption = new Option<string?>("--webapp",
            "Webapp solution folder name (e.g., 'StoreApp'). Not needed if running from inside the webapp folder.");
        var projectOption = new Option<string>("--project",
            () => "WASMApp", "Project subfolder and name within the webapp solution");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(webappOption);
        cmd.AddOption(projectOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, webapp, project) =>
        {
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                // Determine webapp folder path
                var cwd = Directory.GetCurrentDirectory();
                string webappFolder;

                if (!string.IsNullOrEmpty(webapp))
                {
                    // --webapp specified: resolve relative to cwd
                    webappFolder = Path.GetFullPath(Path.Combine(cwd, webapp));
                }
                else if (Directory.Exists(Path.Combine(cwd, project)))
                {
                    // No --webapp: assume cwd IS the webapp solution folder
                    webappFolder = cwd;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(
                        $"Cannot find project folder '{project}/' in current directory.\n" +
                        $"  Either run from the webapp solution folder, or use --webapp <folder>.");
                    Console.ResetColor();
                    return;
                }

                if (!Directory.Exists(webappFolder))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"Webapp folder not found: {webappFolder}");
                    Console.ResetColor();
                    return;
                }

                // Detect static site: folder has index.html but no {project}/{project}.csproj
                var isStaticSite = File.Exists(Path.Combine(webappFolder, "index.html"))
                    && !File.Exists(Path.Combine(webappFolder, project, $"{project}.csproj"));

                foreach (var (tk, tenantConfig) in tenants)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"=== deploywebapp: {project} for tenant {tk} ({(isStaticSite ? "static" : "blazor")}) ===");
                    Console.ResetColor();

                    // Derive S3 bucket name based on topology
                    var suffix = tenantConfig.TenantSuffix;
                    if (string.IsNullOrEmpty(suffix))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine("TenantSuffix not set in tenant config.");
                        Console.ResetColor();
                        return;
                    }

                    string bucketName;
                    if (config.Topology is "ecsexpress" or "apprunner")
                    {
                        // Webapp bucket: {sk}---webapp-{appName}-{ss}
                        // appName derived from webapp folder name, lowercased
                        var webappName = Path.GetFileName(webappFolder).ToLowerInvariant();
                        bucketName = $"{config.SystemKey}---webapp-{webappName}-{config.SystemSuffix}";
                    }
                    else
                    {
                        // ECS (Monro) convention: {sk}-{tk}-{suffix}-{env}-assets
                        bucketName = $"{config.SystemKey}-{tk}-{suffix}-{config.Environment}-assets";
                    }
                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region ?? "us-west-2";

                    // Look up CloudFront distribution ID from AWS (for cache invalidation)
                    var distributionId = "";
                    if (!config.Environment.Equals("dev", StringComparison.OrdinalIgnoreCase))
                    {
                        distributionId = await WebappDeployer.FindDistributionIdAsync(
                            tenantConfig.RootDomain, profile, region);
                    }

                    var deployer = new WebappDeployer();
                    if (isStaticSite)
                    {
                        await deployer.DeployStaticAsync(
                            webappFolder,
                            bucketName, distributionId,
                            profile, region, config.Environment);
                    }
                    else
                    {
                        await deployer.DeployAsync(
                            webappFolder, project, project,
                            bucketName, distributionId,
                            profile, region, config.Environment);
                    }
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption, webappOption, projectOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // deploytenant
    // ---------------------------------------------------------------

    private static void RegisterDeployTenantCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("deploytenant", "Deploy tenant infrastructure");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (deploys all tenants if not specified)");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);

        cmd.SetHandler(async (systemKey, env, tenantKey) =>
        {
            RequirePlugin(plugin, "deploytenant");

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                // Load container service config to check ECR images
                var containerServiceConfig = ConfigLoader
                    .DiscoverAndLoadContainerServiceConfig(config.SystemKey, config.Environment);

                // Resolve cross-account shared services references
                SharedConfig? sharedConfigTenant = null;
                if (!string.IsNullOrEmpty(config.SharedProfile))
                {
                    sharedConfigTenant = ConfigLoader.DiscoverAndLoadSharedConfig();
                    var sharedRegion = sharedConfigTenant.Region;

                    var sharedAccountId = await AwsAccountResolver.ResolveAccountIdAsync(
                        config.SharedProfile, sharedRegion);
                    config.SharedSecretArn =
                        $"arn:aws:secretsmanager:{sharedRegion}:{sharedAccountId}:secret:shared/system";
                    config.SharedKmsKeyArn = await AwsAccountResolver.ResolveKmsKeyArnAsync(
                        config.SharedProfile, sharedRegion, "alias/shared-secrets-key");
                    config.SharedRegion = sharedRegion;
                }

                // Propagate seed bucket config from shared account (only if shared config exists)
                if (sharedConfigTenant == null)
                {
                    try { sharedConfigTenant = ConfigLoader.DiscoverAndLoadSharedConfig(); }
                    catch { /* No sharedconfig.yaml — OK for topologies that don't use shared services */ }
                }
                if (sharedConfigTenant != null)
                    ConfigLoader.PropagateSharedSeedData(config, sharedConfigTenant);

                // Read SES SMTP secrets from shared/system for keycloak template replacements
                Dictionary<string, string?> smtpSecrets = new();
                if (!string.IsNullOrEmpty(config.SharedProfile))
                {
                    smtpSecrets = await AwsAccountResolver.ReadSecretEntriesAsync(
                        config.SharedProfile, config.SharedRegion!, "shared/system",
                        "SES_SMTP_USER", "SES_SMTP_PASSWORD", "SES_SMTP_DOMAIN", "SES_SMTP_URL");
                }

                var (system, factory) = PrepareSystem(plugin!, config);
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);

                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, tenantConfig) in tenants)
                {
                    // Pre-flight: verify ECR images exist for all containers
                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region;
                    var missingImages = new List<string>();

                    Console.WriteLine("Checking ECR images...");
                    foreach (var (svcName, _) in containerServiceConfig.Containers)
                    {
                        // ECR repo is system-scoped (created by deployfoundation), not per-tenant
                        var ecrName = $"{config.SystemKey}-{config.SystemSuffix}-{config.Environment}-{svcName}";
                        var exists = await EcrDeployer.CheckEcrImageExistsAsync(
                            profile, region, ecrName);
                        if (!exists)
                            missingImages.Add(svcName);
                    }

                    if (missingImages.Count == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("  \u2713 ecr-images");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine();
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("  GATE: ecr-images");
                        Console.WriteLine();
                        Console.WriteLine(
                            $"  ECR images missing for tenant '{tk}': {string.Join(", ", missingImages)}");
                        Console.WriteLine(
                            $"  Run 'lz deploycontainer --tenantkey {tk}' first to build and push container images.");
                        Console.WriteLine();
                        Console.WriteLine("  Re-run the same deploy command after completing this step.");
                        Console.ResetColor();
                        Console.WriteLine();
                        continue;
                    }

                    // Propagate shared-services context to tenant config
                    tenantConfig.SharedSecretArn = config.SharedSecretArn;
                    tenantConfig.SharedKmsKeyArn = config.SharedKmsKeyArn;
                    tenantConfig.CentralAuthDomain = config.CentralAuthDomain;

                    Console.WriteLine(
                        $"Deploying tenant '{tk}' for system " +
                        $"'{config.SystemKey}' ({config.Environment})");
                    await deployment.DeployTenantAsync(tk, tenantConfig, smtpSecrets);
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // destroyshared
    // ---------------------------------------------------------------

    private static void RegisterDestroySharedCommand(RootCommand root)
    {
        var cmd = new Command("destroyshared",
            "Destroy shared-services infrastructure");

        cmd.SetHandler(async () =>
        {
            var sharedConfig = ConfigLoader.DiscoverAndLoadSharedConfig();

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("WARNING: This will destroy the shared-services stack.");
            Console.ResetColor();
            Console.Write("Type 'yes' to confirm: ");
            var confirmation = Console.ReadLine();
            if (confirmation?.Trim().ToLowerInvariant() != "yes")
            {
                Console.WriteLine("Aborted.");
                return;
            }

            // Ensure Pulumi state backend exists (needed to find the stack)
            if (sharedConfig.State != null)
                await AwsStateBootstrapper.BootstrapAsync(
                    sharedConfig.Profile, sharedConfig.Region, sharedConfig.State);

            var factory = CreateFactory(new SystemConfig
            {
                SystemKey = "shared",
                Environment = "shared",
                Platform = "aws", Topology = "ecs",
                Profile = sharedConfig.Profile, Region = sharedConfig.Region,
                SystemDomain = sharedConfig.Domain,
                VpcCidr = sharedConfig.VpcCidr,
                AdminAuth = "adminsauth",
                TrustedAccountIds = sharedConfig.TrustedAccountIds,
            });
            var deployment = new SharedDeployment(factory, sharedConfig, Cts.Token);
            await deployment.DestroyAsync();
        });

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // destroyfoundation
    // ---------------------------------------------------------------

    private static void RegisterDestroyFoundationCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("destroyfoundation",
            "Destroy foundation infrastructure (VPC, ECS, RDS, EFS)");

        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);

        cmd.SetHandler(async (systemKey, env) =>
        {
            RequirePlugin(plugin, "destroyfoundation");

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"WARNING: This will destroy foundation for system " +
                    $"'{config.SystemKey}' ({config.Environment}).");
                Console.ResetColor();
                Console.Write("Type 'yes' to confirm: ");
                var confirmation = Console.ReadLine();
                if (confirmation?.Trim().ToLowerInvariant() != "yes")
                {
                    Console.WriteLine("Aborted.");
                    continue;
                }

                // Ensure Pulumi state backend exists (needed to find the stack)
                if (config.State != null)
                    await AwsStateBootstrapper.BootstrapAsync(
                        config.Profile, config.Region, config.State);

                var (system, factory) = PrepareSystem(plugin!, config);
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);
                await deployment.DestroyFoundationAsync();
            }
        }, systemKeyOption, envOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // destroytenant
    // ---------------------------------------------------------------

    private static void RegisterDestroyTenantCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("destroytenant",
            "Destroy tenant infrastructure");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (destroys all tenants if not specified)");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);

        cmd.SetHandler(async (systemKey, env, tenantKey) =>
        {
            RequirePlugin(plugin, "destroytenant");

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                // Ensure Pulumi state backend exists (needed to find the stack)
                if (config.State != null)
                    await AwsStateBootstrapper.BootstrapAsync(
                        config.Profile, config.Region, config.State);

                var (system, factory) = PrepareSystem(plugin!, config);
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);

                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, _) in tenants)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(
                        $"WARNING: This will destroy tenant '{tk}' for system " +
                        $"'{config.SystemKey}' ({config.Environment}).");
                    Console.ResetColor();
                    Console.Write("Type 'yes' to confirm: ");
                    var confirmation = Console.ReadLine();
                    if (confirmation?.Trim().ToLowerInvariant() != "yes")
                    {
                        Console.WriteLine("Aborted.");
                        continue;
                    }

                    await deployment.DestroyTenantAsync(tk);
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // status
    // ---------------------------------------------------------------

    private static void RegisterStatusCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("status", "Show deployment status");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);

        cmd.SetHandler(async (systemKey, env) =>
        {
            RequirePlugin(plugin, "status");

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var (system, factory) = PrepareSystem(plugin!, config);
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);

                await deployment.StatusFoundationAsync();

                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment);
                foreach (var (tk, _) in tenants)
                    await deployment.StatusTenantAsync(tk);
            }
        }, systemKeyOption, envOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static (SystemDefinition System, IPlatformFactory Factory)
        PrepareSystem(ILzPlugin plugin, SystemConfig config)
    {
        var system = plugin.CreateSystemDefinition();
        system.Define(config);

        var validation = TopologyValidator.Validate(system, config.Topology);
        if (!validation.IsValid)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            foreach (var error in validation.Errors)
                Console.Error.WriteLine($"  ERROR: {error}");
            Console.ResetColor();
            throw new InvalidOperationException("Topology validation failed.");
        }

        var factory = CreateFactory(config);
        return (system, factory);
    }

    private static IPlatformFactory CreateFactory(SystemConfig config)
    {
        return (config.Platform, config.Topology) switch
        {
            ("aws", "ecs") => new AwsEcsPlatformFactory(config),
            ("aws", "apprunner") => new Lz.Aws.AppRunner.AwsAppRunnerPlatformFactory(config),
            ("aws", "ecsexpress") => new Lz.Aws.EcsExpress.AwsEcsExpressPlatformFactory(config),
            _ => throw new ArgumentException(
                $"Unsupported platform/topology: {config.Platform}/{config.Topology}")
        };
    }

    private static void RequirePlugin(ILzPlugin? plugin, string commandName)
    {
        if (plugin == null)
            throw new InvalidOperationException(
                $"The '{commandName}' command requires a system plugin. " +
                "Build the Deploy/ project or create an lz.json file pointing to the plugin DLL.");
    }
}
