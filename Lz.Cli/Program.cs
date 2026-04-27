using System.CommandLine;
using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Orchestration;
using Lz.Aws.Orchestration;
using Lz.Core.Plugin;
using Lz.Core.Validation;
using Lz.Aws;
using Lz.Aws.Config;
using Lz.Aws.Docker;
using Lz.Aws.Ecs;
using Lz.Aws.Webapp;
using LzGen = Lz.Gen;

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

        // Register platform-specific config extensions before any config load.
        // Each platform library contributes YAML type mappings for its derived
        // config types (see Lz.Core.Config.IConfigExtensions). Adding a new
        // platform (Azure, GCP, ...) means registering its extensions here.
        ConfigLoader.RegisterExtensions(new AwsConfigExtensions());

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

        // Let the plugin contribute or override platform topology descriptors
        // (new topologies, derived variants, plugin-specific factory wiring).
        // Must run before any CreateFactory call.
        try
        {
            plugin?.RegisterTopologies();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Plugin topology registration failed: {ex.Message}");
            Console.ResetColor();
            return 1;
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
        RegisterDeployStaticSiteCommand(rootCommand, systemKeyOption, envOption);
        RegisterDeployTenantCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDeploySubtenantsCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDestroySubtenantCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDestroySharedCommand(rootCommand);
        RegisterDestroyFoundationCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDestroyTenantCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterStatusCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterParkCommand(rootCommand, systemKeyOption, envOption);
        RegisterUnparkCommand(rootCommand, systemKeyOption, envOption);
        RegisterGetEnvCommand(rootCommand);
        RegisterUtilCommand(rootCommand);
        RegisterGenCommand(rootCommand, plugin);

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

                // Build a minimal AwsSystemConfig with shared-account credentials
                var config = new AwsSystemConfig
                {
                    SystemKey = "shared",
                    Environment = "shared",
                    Topology = Lz.Aws.Topologies.AwsTopologies.EcsFargateKeycloak.Name,
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

            var factory = CreateFactory(new AwsSystemConfig
            {
                SystemKey = "shared",
                Environment = "shared",
                Platform = "aws",
                Topology = Lz.Aws.Topologies.AwsTopologies.EcsFargateKeycloak.Name,
                Profile = sharedConfig.Profile,
                Region = sharedConfig.Region,
                CentralAuthDomain = sharedConfig.Domain,
                VpcCidr = sharedConfig.VpcCidr,
                AdminAuth = "adminsauth",
                TrustedAccountIds = sharedConfig.Aws().TrustedAccountIds,
            });
            var deployment = new SharedDeployment(factory, sharedConfig, Cts.Token);
            await deployment.RunAsync();
        }, themeOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // deploysystem
    // ---------------------------------------------------------------

    private static void RegisterDeployFoundationCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("deploysystem",
            "Deploy system-level infrastructure (VPC, ECS, RDS, EFS, etc.)");

        var platformOption = new Option<string?>("--platform", "Override platform from config");
        var topologyOption = new Option<string?>("--topology", "Override topology from config");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(platformOption);
        cmd.AddOption(topologyOption);

        cmd.SetHandler(async (systemKey, env, platform, topology) =>
        {
            RequirePlugin(plugin, "deploysystem");

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                if (platform != null) config.Platform = platform;
                if (topology != null) config.Topology = topology;

                // Resolve cross-account shared services references
                SharedConfig? sharedConfig = null;
                if (!string.IsNullOrEmpty(config.Aws().SharedProfile))
                {
                    // Use the shared account's region from sharedconfig.yaml, not the system's region
                    sharedConfig = ConfigLoader.DiscoverAndLoadSharedConfig();
                    var sharedRegion = sharedConfig.Region;

                    var sharedAccountId = await AwsAccountResolver.ResolveAccountIdAsync(
                        config.Aws().SharedProfile, sharedRegion);
                    config.Aws().SharedSecretArn =
                        $"arn:aws:secretsmanager:{sharedRegion}:{sharedAccountId}:secret:shared/system";
                    config.Aws().SharedRegion = sharedRegion;

                    // Resolve actual KMS key ARN — alias ARNs can't be used in IAM policy resources
                    config.Aws().SharedKmsKeyArn = await AwsAccountResolver.ResolveKmsKeyArnAsync(
                        config.Aws().SharedProfile, sharedRegion, "alias/shared-secrets-key");

                    Console.WriteLine($"  Shared account: {sharedAccountId}");
                    Console.WriteLine($"  Shared secret:  {config.Aws().SharedSecretArn}");
                    if (config.Aws().SharedKmsKeyArn != null)
                        Console.WriteLine($"  Shared KMS key: {config.Aws().SharedKmsKeyArn}");
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
                Console.WriteLine($"Central auth: {config.CentralAuthDomain}");
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
                        Console.WriteLine($"=== {svcName} (system) ===");
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

                // --- Tenant containers ---
                // Skip if the requested container was already handled as foundation
                if (isFoundation)
                    continue;

                var containersToProcess = container != null
                    ? tenantServiceConfig.Containers
                        .Where(c => c.Key.Equals(container, StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(c => c.Key, c => c.Value)
                    : tenantServiceConfig.Containers;

                if (container != null && containersToProcess.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: Container '{container}' not found in containersbuild.");
                    Console.ResetColor();
                    continue;
                }

                // Per-tenant ECR (uniform across all topologies). The repo is
                // created on first push by EcrDeployer; Pulumi never owns it.
                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, tenantConfig) in tenants)
                {
                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region;

                    foreach (var (svcName, def) in containersToProcess)
                    {
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
        var targetPrefixOption = new Option<string?>("--target-prefix",
            "Optional S3 key prefix under /wwwroot/ for static-site deploys (e.g. 'explore' → s3://bucket/wwwroot/explore/). " +
            "Ignored for Blazor WASM deploys.");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(webappOption);
        cmd.AddOption(projectOption);
        cmd.AddOption(targetPrefixOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, webapp, project, targetPrefix) =>
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
                    // Topologies without centralized Keycloak use a system-wide webapp
                    // bucket ({sk}---webapp-{appName}-{ss}). The Keycloak topology uses
                    // per-tenant webapp buckets ({sk}-{tk}-...-webapp-{name}-{suffix}).
                    var topology = Lz.Aws.Topologies.AwsTopologies.Get(config.Topology);
                    if (!topology.UsesCentralAuth)
                    {
                        var webappName = Path.GetFileName(webappFolder).ToLowerInvariant();
                        bucketName = $"{config.SystemKey}---webapp-{webappName}-{config.SystemSuffix}";
                    }
                    else
                    {
                        bucketName = $"{config.SystemKey}-{tk}--webapp-storeapp-{suffix}";
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
                            profile, region, config.Environment,
                            targetPrefix);
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
        }, systemKeyOption, envOption, tenantKeyOption, webappOption, projectOption, targetPrefixOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // deploystaticsite
    // ---------------------------------------------------------------

    private static void RegisterDeployStaticSiteCommand(
        RootCommand root,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("deploystaticsite",
            "Deploy a per-subtenant static site (e.g. Hugo output) to its subtenant S3 bucket. " +
            "Source is the folder to sync (already built — this command does not run Hugo).");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key. Auto-detected if only one tenant matches the env.");
        var subtenantKeyOption = new Option<string?>("--subtenantkey",
            "Subtenant key. If omitted, deploys to every subtenant of the matched tenant(s).");
        var webappOption = new Option<string?>("--webapp",
            "Source folder relative to cwd (e.g. 'StaticSite', 'StaticSite/public'). " +
            "Not needed if cwd contains index.html at the top level.");
        var appNameOption = new Option<string>("--appname",
            () => "explore",
            "AppName segment used to form the bucket name and to match the YAML StaticSite behaviour. " +
            "Default 'explore' produces {sk}-{tk}-{stk}-webapp-explore-{sts}.");
        var prefixOption = new Option<string>("--prefix",
            () => "explore",
            "S3 key prefix under /wwwroot/ (i.e. the CloudFront behaviour path without slashes). " +
            "Default 'explore' → s3://{bucket}/wwwroot/explore/.");

        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(subtenantKeyOption);
        cmd.AddOption(webappOption);
        cmd.AddOption(appNameOption);
        cmd.AddOption(prefixOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, subtenantKey, webapp, appName, prefix) =>
        {
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                // Resolve source folder (same logic as deploywebapp)
                var cwd = Directory.GetCurrentDirectory();
                string webappFolder;
                if (!string.IsNullOrEmpty(webapp))
                    webappFolder = Path.GetFullPath(Path.Combine(cwd, webapp));
                else if (File.Exists(Path.Combine(cwd, "index.html")))
                    webappFolder = cwd;
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine(
                        "Cannot locate static-site source. Pass --webapp <folder> or run from a folder containing index.html.");
                    Console.ResetColor();
                    return;
                }

                if (!Directory.Exists(webappFolder))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"Source folder not found: {webappFolder}");
                    Console.ResetColor();
                    return;
                }

                foreach (var (tk, tenantConfig) in tenants)
                {
                    // Resolve subtenants to deploy to.
                    var subtenantKeys = !string.IsNullOrEmpty(subtenantKey)
                        ? new[] { subtenantKey }
                        : (tenantConfig.Subtenants?.Keys.ToArray() ?? Array.Empty<string>());

                    if (subtenantKeys.Length == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine(
                            $"  No subtenants found for tenant '{tk}'. Static-site deploy requires a subtenant (bucket naming is subtenant-scoped).");
                        Console.ResetColor();
                        continue;
                    }

                    // Subtenant bucket suffix cascades from TenantSuffix (matches BCPlugin).
                    var suffix = tenantConfig.TenantSuffix;
                    if (string.IsNullOrEmpty(suffix))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine("TenantSuffix not set in tenant config.");
                        Console.ResetColor();
                        return;
                    }
                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region ?? "us-west-2";

                    foreach (var stk in subtenantKeys)
                    {
                        var bucketName = $"{config.SystemKey}-{tk}-{stk}-webapp-{appName}-{suffix}";

                        // Look up CloudFront distribution ID for non-dev (cache invalidation)
                        var distributionId = "";
                        if (!config.Environment.Equals("dev", StringComparison.OrdinalIgnoreCase))
                        {
                            distributionId = await WebappDeployer.FindDistributionIdAsync(
                                tenantConfig.RootDomain, profile, region);
                        }

                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine(
                            $"=== deploystaticsite: {tk}/{stk} → {bucketName} (prefix: {prefix}) ===");
                        Console.ResetColor();

                        var deployer = new WebappDeployer();
                        await deployer.DeployStaticAsync(
                            webappFolder,
                            bucketName, distributionId,
                            profile, region, config.Environment,
                            prefix);
                    }
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption, subtenantKeyOption, webappOption, appNameOption, prefixOption);

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
        var refreshOption = new Option<bool>("--refresh",
            "Run Pulumi refresh before up to detect external state drift (e.g., after lz park)");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(refreshOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, refresh) =>
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
                if (!string.IsNullOrEmpty(config.Aws().SharedProfile))
                {
                    sharedConfigTenant = ConfigLoader.DiscoverAndLoadSharedConfig();
                    var sharedRegion = sharedConfigTenant.Region;

                    var sharedAccountId = await AwsAccountResolver.ResolveAccountIdAsync(
                        config.Aws().SharedProfile, sharedRegion);
                    config.Aws().SharedSecretArn =
                        $"arn:aws:secretsmanager:{sharedRegion}:{sharedAccountId}:secret:shared/system";
                    config.Aws().SharedKmsKeyArn = await AwsAccountResolver.ResolveKmsKeyArnAsync(
                        config.Aws().SharedProfile, sharedRegion, "alias/shared-secrets-key");
                    config.Aws().SharedRegion = sharedRegion;
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
                if (!string.IsNullOrEmpty(config.Aws().SharedProfile))
                {
                    smtpSecrets = await AwsAccountResolver.ReadSecretEntriesAsync(
                        config.Aws().SharedProfile, config.Aws().SharedRegion!, "shared/system",
                        "SES_SMTP_USER", "SES_SMTP_PASSWORD", "SES_SMTP_DOMAIN", "SES_SMTP_URL");
                }

                var (system, factory) = PrepareSystem(plugin!, config);
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);

                await Lz.Core.Orchestration.StackOutputReader.EnsureFoundationDeployedAsync(config);

                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, tenantConfig) in tenants)
                {
                    ValidateTenantConfig(config, tk, tenantConfig);

                    // Pre-flight: verify ECR images exist for all containers.
                    // Per-tenant ECR naming is uniform across topologies.
                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region;
                    var missingImages = new List<string>();

                    Console.WriteLine("Checking ECR images...");
                    foreach (var (svcName, _) in containerServiceConfig.Containers)
                    {
                        var ecrName = $"{config.SystemKey}-{tenantConfig.TenantSuffix}-{config.Environment}-{tk}-{svcName}";
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
                    tenantConfig.Aws().SharedSecretArn = config.Aws().SharedSecretArn;
                    tenantConfig.Aws().SharedKmsKeyArn = config.Aws().SharedKmsKeyArn;
                    tenantConfig.CentralAuthDomain = config.CentralAuthDomain;

                    Console.WriteLine(
                        $"Deploying tenant '{tk}' for system " +
                        $"'{config.SystemKey}' ({config.Environment})");
                    await deployment.DeployTenantAsync(tk, tenantConfig, smtpSecrets, refresh);

                    // Per-subtenant infrastructure (S3 buckets + DynamoDB tables)
                    // is managed imperatively, outside Pulumi. This runs after
                    // the tenant Pulumi up so first-time deploy is a single command.
                    var subProfile = tenantConfig.Profile ?? config.Profile;
                    var subRegion = tenantConfig.Region ?? config.Region;
                    var subAccountId = await AwsAccountResolver.ResolveAccountIdAsync(subProfile, subRegion);
                    await Lz.Aws.Shared.SubtenantProvisioner.EnsureAllAsync(
                        config, tenantConfig, subProfile, subRegion, subAccountId);

                    // Plugin-owned runtime refresh (typically CloudFront KVS).
                    await plugin!.RefreshTenantRuntimeAsync(config, tenantConfig);
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption, refreshOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // deploysubtenants / destroysubtenant — fast path for adding and
    // removing subtenants without re-running Pulumi on the tenant stack.
    // Works because the tenant CloudFront distribution carries a wildcard
    // TLS cert + wildcard alias, and the tenant's Route 53 zone has a
    // wildcard A-alias record pointing at the distribution — all subtenant
    // first-level subdomains route automatically. Per-subtenant S3 buckets
    // and DynamoDB tables are managed imperatively via SubtenantProvisioner.
    // ---------------------------------------------------------------

    private static void RegisterDeploySubtenantsCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("deploysubtenants",
            "Create missing subtenant S3 buckets + DynamoDB tables and refresh " +
            "runtime state (KVS). Fast path for adding subtenants without a " +
            "full `lz deploytenant` Pulumi run. Distribution-level resources " +
            "(CloudFront, Route 53) are covered by tenant-level wildcards.");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (applies to all tenants if not specified)");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);

        cmd.SetHandler(async (systemKey, env, tenantKey) =>
        {
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, tenantConfig) in tenants)
                {
                    ValidateTenantConfig(config, tk, tenantConfig);

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"=== deploysubtenants: {config.SystemKey}/{tk} ({config.Environment}) ===");
                    Console.ResetColor();

                    if (tenantConfig.Subtenants == null || tenantConfig.Subtenants.Count == 0)
                    {
                        Console.WriteLine("  No subtenants declared. Nothing to do.");
                        continue;
                    }

                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region;
                    var accountId = await AwsAccountResolver.ResolveAccountIdAsync(profile, region);

                    // Provision S3 buckets + DynamoDB tables
                    await Lz.Aws.Shared.SubtenantProvisioner.EnsureAllAsync(
                        config, tenantConfig, profile, region, accountId);

                    // Plugin refresh — typically writes CloudFront KVS entries
                    if (plugin != null)
                        await plugin.RefreshTenantRuntimeAsync(config, tenantConfig);
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption);

        root.AddCommand(cmd);
    }

    private static void RegisterDestroySubtenantCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("destroysubtenant",
            "Destroy one subtenant's S3 bucket and DynamoDB table. After " +
            "running, remove the subtenant from subtenantconfig and run " +
            "`lz deploysubtenants` to refresh KVS. The CloudFront KVS entry " +
            "for the removed subtenant is NOT cleared — clean it up via the " +
            "AWS console or CLI if its presence would be misleading.");

        var tenantKeyOption = new Option<string>("--tenantkey", "Tenant key") { IsRequired = true };
        var subtenantKeyOption = new Option<string>("--subtenantkey", "Subtenant key to destroy") { IsRequired = true };
        var forceOption = new Option<bool>("--force",
            "Empty the S3 bucket before deleting (data loss). Without --force, " +
            "deletion fails if the bucket is non-empty.");
        var yesOption = new Option<bool>("--yes", "Skip the confirmation prompt.");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(subtenantKeyOption);
        cmd.AddOption(forceOption);
        cmd.AddOption(yesOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, subtenantKey, force, yes) =>
        {
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var tenantConfig = ConfigLoader.DiscoverAndLoadTenantConfig(
                    config.SystemKey, tenantKey, config.Environment);

                if (!yes)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(
                        $"About to destroy subtenant '{subtenantKey}' under " +
                        $"{config.SystemKey}/{tenantKey} ({config.Environment}).");
                    Console.WriteLine(
                        "  - S3 bucket " +
                        $"{Lz.Aws.Shared.SubtenantBucketManager.BucketName(config.SystemKey, tenantKey, subtenantKey, config.SystemSuffix)} " +
                        $"will be deleted{(force ? " (emptied first — DATA LOSS)" : "")}.");
                    Console.WriteLine($"  - DynamoDB table {config.SystemKey}_{tenantKey}_{subtenantKey} will be deleted (DATA LOSS).");
                    Console.Write("Type 'yes' to confirm: ");
                    Console.ResetColor();
                    var response = Console.ReadLine();
                    if (!string.Equals(response, "yes", StringComparison.Ordinal))
                    {
                        Console.WriteLine("Aborted.");
                        return;
                    }
                }

                var profile = tenantConfig.Profile ?? config.Profile;
                var region = tenantConfig.Region ?? config.Region;

                await Lz.Aws.Shared.SubtenantProvisioner.DeleteOneAsync(
                    config, tenantConfig, subtenantKey, profile, region, force);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Subtenant '{subtenantKey}' destroyed.");
                Console.ResetColor();
                Console.WriteLine(
                    "Next: remove the subtenant from subtenantconfig.yaml and " +
                    "run `lz deploysubtenants` to refresh KVS. The KVS entry " +
                    "for the destroyed subtenant's domain will need manual cleanup.");
            }
        }, systemKeyOption, envOption, tenantKeyOption, subtenantKeyOption, forceOption, yesOption);

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

            var factory = CreateFactory(new AwsSystemConfig
            {
                SystemKey = "shared",
                Environment = "shared",
                Platform = "aws", Topology = Lz.Aws.Topologies.AwsTopologies.EcsFargateKeycloak.Name,
                Profile = sharedConfig.Profile, Region = sharedConfig.Region,
                CentralAuthDomain = sharedConfig.Domain,
                VpcCidr = sharedConfig.VpcCidr,
                AdminAuth = "adminsauth",
                TrustedAccountIds = sharedConfig.Aws().TrustedAccountIds,
            });
            var deployment = new SharedDeployment(factory, sharedConfig, Cts.Token);
            await deployment.DestroyAsync();
        });

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // destroysystem
    // ---------------------------------------------------------------

    private static void RegisterDestroyFoundationCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("destroysystem",
            "Destroy system-level infrastructure (VPC, ECS, RDS, EFS, etc.)");

        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);

        cmd.SetHandler(async (systemKey, env) =>
        {
            RequirePlugin(plugin, "destroysystem");

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
    // park
    // ---------------------------------------------------------------

    private static void RegisterParkCommand(
        RootCommand root,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("park",
            "Park tenant site(s) — show maintenance page via CloudFront");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (parks all tenants if not specified)");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);

        cmd.SetHandler(async (systemKey, env, tenantKey) =>
        {
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                // Discover the Park/ folder by searching upward from cwd
                var monorepoRoot = ConfigLoader.DiscoverMonorepoRoot(
                    config.SystemKey, config.Environment);
                if (monorepoRoot == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine("Cannot find monorepo root (no systemconfig found).");
                    Console.ResetColor();
                    return;
                }

                foreach (var (tk, tenantConfig) in tenants)
                {
                    var parkFolder = Path.Combine(monorepoRoot, "Park", tk);
                    var parkPage = Path.Combine(parkFolder, "index.html");

                    if (!File.Exists(parkPage))
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine(
                            $"Park page not found: {parkPage}\n" +
                            $"  Create Park/{tk}/index.html with your maintenance page HTML.");
                        Console.ResetColor();
                        continue;
                    }

                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region;

                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(
                        $"WARNING: This will park tenant '{tk}' ({tenantConfig.RootDomain}).");
                    Console.ResetColor();
                    Console.Write("Type 'yes' to confirm: ");
                    var confirmation = Console.ReadLine();
                    if (confirmation?.Trim().ToLowerInvariant() != "yes")
                    {
                        Console.WriteLine("Skipped.");
                        continue;
                    }

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"=== Parking tenant '{tk}' ===");
                    Console.ResetColor();

                    var manager = new Lz.Aws.Ecs.AwsParkManager(
                        config.SystemKey, profile, region);
                    await manager.ParkAsync(
                        tk,
                        tenantConfig.TenantSuffix,
                        config.Environment,
                        tenantConfig.RootDomain,
                        parkFolder,
                        tenantConfig.LegacyDomains);
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // unpark
    // ---------------------------------------------------------------

    private static void RegisterUnparkCommand(
        RootCommand root,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("unpark",
            "Unpark tenant site(s) — restore normal CloudFront operation");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (unparks all tenants if not specified)");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);

        cmd.SetHandler(async (systemKey, env, tenantKey) =>
        {
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, tenantConfig) in tenants)
                {
                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region;

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"=== Unparking tenant '{tk}' ===");
                    Console.ResetColor();

                    var manager = new Lz.Aws.Ecs.AwsParkManager(
                        config.SystemKey, profile, region);
                    await manager.UnparkAsync(tk, tenantConfig.RootDomain, tenantConfig.LegacyDomains);
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption);

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

        ValidateTopologyConfig(config);
        var factory = CreateFactory(config);
        return (system, factory);
    }

    private static IPlatformFactory CreateFactory(SystemConfig config)
    {
        return config.Platform switch
        {
            "aws" => Lz.Aws.Topologies.AwsTopologies.Get(config.Topology).CreateFactory(config),
            _ => throw new ArgumentException(
                $"Unsupported platform: '{config.Platform}'. Known: aws.")
        };
    }

    /// <summary>
    /// Run topology-specific config validation and abort with a combined error
    /// message if any checks fail. Factory construction happens afterwards.
    /// </summary>
    private static void ValidateTopologyConfig(SystemConfig config)
    {
        if (config.Platform != "aws") return;

        // Platform-mismatch: if Platform is aws but the loaded config isn't the
        // AWS-derived type, the AWS YamlDotNet extensions weren't active at load
        // time. Catch this clearly rather than letting downstream code NRE.
        if (config is not Lz.Aws.Config.AwsSystemConfig)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(
                $"Platform is 'aws' but SystemConfig did not resolve to AwsSystemConfig " +
                $"(got '{config.GetType().Name}'). AWS config extensions were not registered " +
                "at YAML load time — this is a tool packaging bug.");
            Console.ResetColor();
            throw new InvalidOperationException("Platform extensions not loaded.");
        }

        Lz.Aws.Topologies.AwsTopology topology;
        try
        {
            topology = Lz.Aws.Topologies.AwsTopologies.Get(config.Topology);
        }
        catch (ArgumentException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(ex.Message);
            Console.ResetColor();
            throw new InvalidOperationException("Unknown topology.");
        }

        var errors = new List<string>();
        topology.ValidateConfig?.Invoke(config, errors);
        if (errors.Count == 0) return;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(
            $"Topology '{topology.Name}' validation failed for system '{config.SystemKey}' ({config.Environment}):");
        foreach (var err in errors)
            Console.Error.WriteLine($"  - {err}");
        Console.ResetColor();
        throw new InvalidOperationException(
            $"Topology '{topology.Name}' config validation failed.");
    }

    /// <summary>
    /// Tenant-scoped preflight: validate sizings and other per-tenant settings
    /// that depend on both system and tenant config. Called from tenant-level
    /// commands (deploytenant, deploysubtenants) before any Pulumi work.
    /// </summary>
    private static void ValidateTenantConfig(SystemConfig system, string tenantKey, TenantConfig tenant)
    {
        if (system.Platform != "aws") return;

        var errors = new List<string>();
        var topology = Lz.Aws.Topologies.AwsTopologies.Get(system.Topology);

        // Tenant-key charset + subtenant-key + combined S3 bucket length.
        Lz.Aws.Config.AwsNamingValidator.ValidateTenantKeys(system, tenantKey, tenant, errors);

        // Fargate sizing — only for topologies that run Fargate tasks.
        if (topology.Compute is Lz.Aws.Topologies.AwsComputeKind.FargatePrivate
                             or Lz.Aws.Topologies.AwsComputeKind.FargatePublic)
        {
            var fargate = Lz.Aws.Config.AwsConfigMerger.GetEffectiveFargateConfig(system, tenant);
            Lz.Aws.Config.FargateValidator.Validate(fargate, errors, $"tenant '{tenantKey}'");
        }

        if (errors.Count == 0) return;

        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine(
            $"Tenant config validation failed for '{tenantKey}' ({system.SystemKey}/{system.Environment}):");
        foreach (var err in errors)
            Console.Error.WriteLine($"  - {err}");
        Console.ResetColor();
        throw new InvalidOperationException("Tenant config validation failed.");
    }

    // ---------------------------------------------------------------
    // getenv — print the discovered environment to stdout.
    // Intended for consumption by build systems (e.g., MSBuild Exec) that
    // want to pick up the folder-based env without reimplementing the rule.
    // Prints bare value to stdout: "dev" | "test" | "prod".
    // ---------------------------------------------------------------

    private static void RegisterGetEnvCommand(RootCommand root)
    {
        var cmd = new Command("getenv",
            "Print the environment (dev|test|prod) discovered from the current folder hierarchy.");

        cmd.SetHandler(() =>
        {
            try
            {
                var env = ConfigResolver.ResolveEnvironment();
                Console.Out.WriteLine(env);
                Environment.ExitCode = 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        });

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // util — grouping for general-purpose build helpers.
    // ---------------------------------------------------------------

    private static void RegisterUtilCommand(RootCommand root)
    {
        var util = new Command("util", "General-purpose build helpers.");
        RegisterUtilMergeJsCommand(util);
        root.AddCommand(util);
    }

    // ---------------------------------------------------------------
    // util merge js <output> <input1> [<input2> ...]
    //
    // Merges N ES-module files of the exact form
    //   export const <name> = { ... };
    // into a single file with the same export name. Later inputs override
    // earlier inputs on key collisions (shallow merge).
    //
    // Contract:
    //   - Each input must have a SINGLE `export const <name> = {...};` statement.
    //   - All inputs must use the same export name.
    //   - Object bodies must be strict JSON — QUOTED keys, no trailing commas.
    //     (Rationale: keeps the parser trivial and produces unambiguous output.)
    //   - No nested merge: top-level keys only. If a key's value is an object,
    //     the later input's object REPLACES the earlier one in full.
    // ---------------------------------------------------------------

    private static void RegisterUtilMergeJsCommand(Command parent)
    {
        var outputArg = new Argument<string>("output", "Path to write the merged file.");
        var inputsArg = new Argument<string[]>("inputs", "Two or more input files to merge, in override order (rightmost wins).")
        {
            Arity = ArgumentArity.OneOrMore
        };

        var cmd = new Command("merge", "Merge JS config files.");
        var js = new Command("js",
            "Merge ES-module files of the form `export const X = {...};` — rightmost input wins on key collision.");
        js.AddArgument(outputArg);
        js.AddArgument(inputsArg);

        js.SetHandler((string output, string[] inputs) =>
        {
            try
            {
                MergeJs(output, inputs);
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"lz util merge js: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, outputArg, inputsArg);

        cmd.AddCommand(js);
        parent.AddCommand(cmd);
    }

    // Core merge implementation — public-ish for potential future use.
    private static void MergeJs(string output, string[] inputs)
    {
        if (inputs == null || inputs.Length == 0)
            throw new ArgumentException("At least one input file is required.");

        // Locate `export const <name> = ` via regex; then find the matching
        // closing brace by counting depth. The regex alone can't bound the
        // body reliably when files contain leading comments or strings with
        // { } characters.
        var headerPattern = new System.Text.RegularExpressions.Regex(
            @"export\s+const\s+(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*\{");

        string? exportName = null;
        var merged = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);

        foreach (var path in inputs)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Input not found: {path}");

            var text = File.ReadAllText(path);
            var match = headerPattern.Match(text);
            if (!match.Success)
                throw new InvalidDataException(
                    $"{path}: no `export const X = {{` found (single top-level export required).");

            var secondMatch = headerPattern.Match(text, match.Index + match.Length);
            if (secondMatch.Success)
                throw new InvalidDataException(
                    $"{path}: multiple `export const` statements found — only one is allowed.");

            var name = match.Groups["name"].Value;
            if (exportName == null) exportName = name;
            else if (name != exportName)
                throw new InvalidDataException(
                    $"{path}: export name '{name}' does not match '{exportName}' from earlier input(s).");

            // Find the matching `}` by counting braces while tracking string
            // literals (both " and ') so braces inside strings don't confuse.
            var openIdx = match.Index + match.Length - 1; // position of the `{`
            var closeIdx = FindMatchingBrace(text, openIdx, path);
            var body = text.Substring(openIdx, closeIdx - openIdx + 1);

            System.Text.Json.JsonDocument doc;
            try
            {
                doc = System.Text.Json.JsonDocument.Parse(body,
                    new System.Text.Json.JsonDocumentOptions
                    {
                        CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidDataException(
                    $"{path}: object body is not valid JSON (QUOTED keys required, double-quoted strings only). {ex.Message}");
            }

            using (doc)
            {
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    throw new InvalidDataException($"{path}: export must be an object literal.");

                foreach (var prop in doc.RootElement.EnumerateObject())
                    merged[prop.Name] = prop.Value.Clone();
            }
        }

        // Serialize the merged object with stable indentation.
        var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
        var bodyJson = System.Text.Json.JsonSerializer.Serialize(merged, opts);

        var header =
            $"// Generated by `lz util merge js` at {DateTime.UtcNow:O}\n" +
            $"// Inputs: {string.Join(", ", inputs.Select(Path.GetFileName))}\n" +
            $"// DO NOT EDIT — regenerate via the build system.\n";
        var content = header + $"export const {exportName} = {bodyJson};\n";
        File.WriteAllText(output, content);

        Console.Out.WriteLine($"Wrote {output} (export {exportName}, {merged.Count} keys)");
    }

    // Given a text with `{` at openIdx, return the index of the matching
    // `}`. Counts brace depth while skipping over string literals so braces
    // inside strings don't affect the count.
    private static int FindMatchingBrace(string text, int openIdx, string path)
    {
        if (text[openIdx] != '{')
            throw new InvalidOperationException($"Internal: expected '{{' at index {openIdx}.");

        int depth = 0;
        for (int i = openIdx; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"' || c == '\'')
            {
                // Skip to matching quote, honouring backslash escapes.
                char quote = c;
                i++;
                while (i < text.Length && text[i] != quote)
                {
                    if (text[i] == '\\') i++; // skip next char
                    i++;
                }
                continue;
            }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        throw new InvalidDataException($"{path}: unterminated object literal (no matching closing brace).");
    }

    private static void RequirePlugin(ILzPlugin? plugin, string commandName)
    {
        if (plugin == null)
            throw new InvalidOperationException(
                $"The '{commandName}' command requires a system plugin. " +
                "Build the Deploy/ project or create an lz.json file pointing to the plugin DLL.");
    }

    // ---------------------------------------------------------------
    // gen — model-driven code generation (ported from LazyMagicMDD)
    // ---------------------------------------------------------------

    private static void RegisterGenCommand(RootCommand root, ILzPlugin? plugin)
    {
        var pathArg = new Argument<string?>("path", () => null,
            "Solution directory containing LazyMagic.yaml (defaults to the current directory).");
        var templatesOpt = new Option<string?>("--templates",
            "Override the bundled template directory. If omitted, lz looks for "
            + "ProjectTemplates/AWSTemplates alongside LazyMagic.yaml first, then "
            + "falls back to templates shipped inside the Lz.Gen assembly.");

        var cmd = new Command("gen",
            "Generate code and AWS templates from LazyMagic.yaml + OpenAPI specs.");
        cmd.AddArgument(pathArg);
        cmd.AddOption(templatesOpt);

        cmd.SetHandler(async (string? path, string? templatesOverride) =>
        {
            var solutionDir = Path.GetFullPath(path ?? Directory.GetCurrentDirectory());
            if (!File.Exists(Path.Combine(solutionDir, "LazyMagic.yaml")))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine(
                    $"No LazyMagic.yaml found in '{solutionDir}'. " +
                    "Run 'lz gen' from a folder containing LazyMagic.yaml, or pass the path as an argument.");
                Console.ResetColor();
                Environment.ExitCode = 1;
                return;
            }

            // Register custom directive/artifact types before parsing the YAML.
            // Two independent plugin paths, either/both can be present:
            //   1. Generate/bin/.../Generate.dll  — dedicated gen plugin (preferred)
            //   2. Deploy/bin/.../Deploy.dll      — deploy plugin that also implements ILzGenPlugin
            // The Deploy path is kept for backward compatibility; Generate is the new home
            // for system-specific directive and artifact types.
            LzGen.ILzGenPlugin? dedicatedGenPlugin = null;
            try
            {
                dedicatedGenPlugin = GenPluginLoader.LoadGenPlugin(solutionDir);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine($"Warning: Generate plugin load failed: {ex.Message}");
                Console.ResetColor();
            }

            void SafeRegister(LzGen.ILzGenPlugin? p, string source)
            {
                if (p == null) return;
                try { p.RegisterGenExtensions(); }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"Warning: {source}.RegisterGenExtensions threw: {ex.Message}");
                    Console.ResetColor();
                }
            }

            SafeRegister(dedicatedGenPlugin, "Generate plugin");
            if (plugin is LzGen.ILzGenPlugin genFromDeploy)
                SafeRegister(genFromDeploy, "Deploy plugin");

            var logger = new ConsoleGenLogger();
            var bundled = templatesOverride is not null
                ? Path.GetFullPath(templatesOverride)
                : null; // null → LzGenSolution derives from Assembly.Location
            try
            {
                var solution = new LzGen.LzGenSolution(logger, solutionDir, bundled);
                await solution.ProcessAsync();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine($"lz gen failed: {ex.Message}");
                Console.ResetColor();
                Environment.ExitCode = 1;
            }
        }, pathArg, templatesOpt);

        root.AddCommand(cmd);
    }

    /// <summary>
    /// Bridges Lz.Gen.ILogger to the same Console/color pattern used elsewhere
    /// in Lz.Cli. Kept internal so only Lz.Cli owns the user-facing output style.
    /// </summary>
    private sealed class ConsoleGenLogger : LzGen.ILogger
    {
        public void Info(string message) => Console.WriteLine(message);

        public Task InfoAsync(string message)
        {
            Console.WriteLine(message);
            return Task.CompletedTask;
        }

        public void Error(Exception ex, string message)
        {
            var prev = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(message);
            if (ex != null) Console.Error.WriteLine(ex);
            Console.ForegroundColor = prev;
        }

        public Task ErrorAsync(Exception ex, string message)
        {
            Error(ex, message);
            return Task.CompletedTask;
        }
    }
}
