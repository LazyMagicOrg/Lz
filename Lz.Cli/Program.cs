using System.CommandLine;
using System.Reflection;
using Lz.Core.Config;
using Lz.Core.Definitions;
using Lz.Core.Interfaces;
using Lz.Core.Orchestration;
using Lz.Aws.Orchestration;
using Lz.Core.Plugin;
using Lz.Core.Repos;
using Lz.Core.Validation;
using Lz.Aws;
using Lz.Aws.Config;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Docker;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
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

        // Intercept --version BEFORE System.CommandLine sees it. The default
        // handler only prints the running assembly's InformationalVersion,
        // which would conflate the three distinct version axes (runner +
        // cli + plugin) into one number. The custom output below names each
        // axis explicitly with provenance — see Platform/LzRunnerSplit.md
        // in the Monro repo for design rationale.
        if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v"))
        {
            PrintVersionInfo(plugin);
            return 0;
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
        RegisterUpdateContainerCommand(rootCommand, systemKeyOption, envOption);
        RegisterUpdateEdgeCommand(rootCommand, systemKeyOption, envOption);
        RegisterUpdateConfigCommand(rootCommand, systemKeyOption, envOption);
        RegisterDeploySubtenantsCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterPreviewCommands(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDestroySubtenantCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDestroySharedCommand(rootCommand);
        RegisterDestroyFoundationCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterDestroyTenantCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterUnlockCommand(rootCommand, systemKeyOption, envOption);
        RegisterStatusCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterVerifyCommand(rootCommand, plugin, systemKeyOption, envOption);
        RegisterParkCommand(rootCommand, systemKeyOption, envOption);
        RegisterUnparkCommand(rootCommand, systemKeyOption, envOption);
        RegisterGetEnvCommand(rootCommand);
        RegisterGetTenantsCommand(rootCommand);
        RegisterReposCommand(rootCommand);
        RegisterUtilCommand(rootCommand);
        RegisterGenCommand(rootCommand, plugin);

        // Plugin-specific commands (e.g., seed)
        plugin?.RegisterCommands(rootCommand);

        // Top-level help: render the command list grouped into sections for
        // readability (Shared / System / Tenant / Subtenant / Misc). Subcommand
        // help (e.g. `lz deploytenant -h`) is unaffected. Placed after plugin
        // command registration so plugin-contributed commands are included.
        if (args.Length == 0 ||
            (args.Length == 1 && (args[0] == "--help" || args[0] == "-h" || args[0] == "-?")))
        {
            PrintGroupedHelp(rootCommand);
            return 0;
        }

        var rc = await rootCommand.InvokeAsync(args);
        // Handlers signal failure via Environment.ExitCode (the established
        // pattern throughout this file), but a Main that RETURNS an int makes
        // the runtime ignore Environment.ExitCode — so honor it explicitly.
        // InvokeAsync's own non-zero results (parse errors, thrown exceptions)
        // still win.
        return rc != 0 ? rc : Environment.ExitCode;
    }

    // ---------------------------------------------------------------
    // --version handler
    // ---------------------------------------------------------------

    /// <summary>
    /// Renders the 3-line <c>lz --version</c> output: dispatcher (lz.runner),
    /// infrastructure logic (lz.cli), and tenant plugin. Each line includes
    /// provenance so the user can tell at a glance WHERE each piece was
    /// resolved from.
    ///
    /// Reads runner info from env vars set by Lz.Runner before it spawned us
    /// (see LzRunner/Lz.Runner/Program.cs). If those env vars are absent the
    /// process wasn't launched via the runner; falls back to "(unknown)".
    /// </summary>
    /// <summary>
    /// Render <c>lz --help</c> with the top-level commands grouped into sections
    /// for readability. Command descriptions are read from the registered
    /// commands (the source of truth, including plugin-contributed ones); any
    /// command not assigned to a section below still appears under "Other".
    /// </summary>
    private static void PrintGroupedHelp(RootCommand root)
    {
        var sections = new[]
        {
            ("Shared",    "shared-services account (Keycloak + Tailscale)",
                new[] { "previewshared", "deployshared", "destroyshared" }),
            ("System",    "foundation stack: VPC, Cognito, DynamoDB, ECS cluster",
                new[] { "previewsystem", "deploysystem", "destroysystem" }),
            ("Tenant",    "per-tenant stack + per-tenant operations",
                new[] { "previewtenant", "deploytenant", "deploycontainer", "updatecontainer",
                        "updateconfig", "updateedge", "updatekvs", "deploywebapp", "deployassets",
                        "park", "unpark", "destroytenant" }),
            ("Subtenant", "per-subtenant resources",
                new[] { "deploysubtenants", "deploystaticsite", "destroysubtenant" }),
            ("Misc",      "discovery, codegen, utilities",
                new[] { "status", "getenv", "gettenants", "gen", "util", "deletetestusers", "unlock" }),
        };

        var byName = root.Subcommands.ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);
        var categorized = new HashSet<string>(sections.SelectMany(s => s.Item3), StringComparer.Ordinal);

        var pad = byName.Keys.Select(n => n.Length).DefaultIfEmpty(12).Max() + 2;
        var descWidth = 72;
        try { if (!Console.IsOutputRedirected) descWidth = Math.Max(36, Console.WindowWidth - pad - 6); }
        catch { /* no console (output redirected) — keep the default width */ }

        Console.WriteLine("Lz infrastructure deployment tool");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  lz [command] [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --version       Show version information");
        Console.WriteLine("  -?, -h, --help  Show help and usage information");
        Console.WriteLine();
        Console.WriteLine("Commands:");

        void Row(string name)
        {
            if (!byName.TryGetValue(name, out var c)) return;
            Console.WriteLine($"    {name.PadRight(pad)}{Summarize(c.Description, descWidth)}");
        }

        void Header(string title, string blurb)
        {
            Console.WriteLine();
            var color = !Console.IsOutputRedirected;
            if (color) Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  {title}");
            if (color) Console.ResetColor();
            Console.WriteLine($" — {blurb}");
        }

        foreach (var (title, blurb, cmds) in sections)
        {
            Header(title, blurb);
            foreach (var name in cmds) Row(name);
        }

        // Safety net: any registered command not assigned above still shows up,
        // so newly-added (or plugin) commands are never silently dropped.
        var leftovers = byName.Keys.Where(n => !categorized.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        if (leftovers.Count > 0)
        {
            Header("Other", "uncategorized — assign a section in PrintGroupedHelp");
            foreach (var name in leftovers) Row(name);
        }

        Console.WriteLine();
        Console.WriteLine("Run 'lz <command> -h' for command-specific help.");
    }

    /// <summary>Collapse whitespace and truncate a command description to one line.</summary>
    private static string Summarize(string? description, int max)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var text = System.Text.RegularExpressions.Regex.Replace(description.Trim(), @"\s+", " ");
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', Math.Min(max - 1, text.Length - 1));
        if (cut < max / 2) cut = max - 1;
        return text.Substring(0, cut).TrimEnd() + "…";
    }

    private static void PrintVersionInfo(ILzPlugin? plugin)
    {
        // -- Line 1: the dispatcher
        var runnerVersion = Environment.GetEnvironmentVariable("LZ_RUNNER_VERSION") ?? "(unknown — not launched via lz.runner)";
        var toolStoreHint = ResolveToolStorePath(runnerVersion);
        Console.WriteLine($"lz.runner:     {runnerVersion}");
        if (!string.IsNullOrEmpty(toolStoreHint))
            Console.WriteLine($"               installed: {toolStoreHint}");

        Console.WriteLine();

        // -- Line 2: the infrastructure logic (this assembly)
        var cliAsm = typeof(Program).Assembly;
        var cliVer = cliAsm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? cliAsm.GetName().Version?.ToString()
                     ?? "(unknown)";
        var resolvedNupkg = Environment.GetEnvironmentVariable("LZ_RUNNER_NUPKG_PATH");
        var resolvedFeed  = Environment.GetEnvironmentVariable("LZ_RUNNER_FEED");
        Console.WriteLine($"lz.cli:        {cliVer}");
        if (!string.IsNullOrEmpty(resolvedNupkg))
            Console.WriteLine($"               resolved:  {resolvedNupkg}");
        if (!string.IsNullOrEmpty(resolvedFeed))
            Console.WriteLine($"               feed:      {resolvedFeed}");

        Console.WriteLine();

        // -- Line 3: the tenant plugin (if any)
        if (plugin != null)
        {
            var pluginAsm = plugin.GetType().Assembly;
            var pluginName = pluginAsm.GetName().Name ?? "(unnamed)";
            var pluginVer  = pluginAsm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                             ?? pluginAsm.GetName().Version?.ToString()
                             ?? "(unknown)";
            Console.WriteLine($"deploy plugin: {pluginName} {pluginVer}");
            if (!string.IsNullOrEmpty(pluginAsm.Location))
                Console.WriteLine($"               loaded:    {pluginAsm.Location}");
        }
        else
        {
            Console.WriteLine("deploy plugin: (none — no lz.json marker or Deploy/bin/.../Deploy.dll in scope)");
        }
    }

    /// <summary>
    /// Best-effort guess at the <c>dotnet tool</c> store path for the running
    /// Lz.Runner version. Pure cosmetics for the --version output; if we can't
    /// find it, return empty and let the caller skip the line.
    /// </summary>
    private static string ResolveToolStorePath(string runnerVersion)
    {
        // Strip any +commit-hash suffix that InformationalVersion carries.
        var clean = runnerVersion.Split('+')[0];
        if (string.IsNullOrEmpty(clean) || clean.Contains(' ')) return string.Empty;

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(userProfile)) return string.Empty;

        var candidate = Path.Combine(userProfile, ".dotnet", "tools", ".store", "lz.runner", clean);
        return Directory.Exists(candidate) ? candidate : string.Empty;
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
                var runner = new AwsLambdaThemeDeployRunner(config, themesBucket);
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
    // preview (read-only dry run of a Pulumi stack — no changes applied,
    // no post-deploy actions; --fail-on-replace gates topology switches)
    // ---------------------------------------------------------------

    private static void RegisterPreviewCommands(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var platformOption = new Option<string?>("--platform", "Override platform from config");
        var topologyOption = new Option<string?>("--topology",
            "Override topology from config — e.g. preview a switch to lambda-cognito-dynamodb");
        var refreshOption = new Option<bool>("--refresh",
            "Refresh state from AWS before previewing for an accurate diff (writes the refreshed state)");
        var failOnReplaceOption = new Option<bool>("--fail-on-replace",
            "Exit non-zero (2) if the plan contains any replace/delete — the topology-switch guardrail");
        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (previews all tenants if not specified)");

        // ---- previewsystem ----
        var systemCmd = new Command("previewsystem",
            "Preview (dry-run) the system/foundation stack. No changes are applied.");
        systemCmd.AddOption(systemKeyOption);
        systemCmd.AddOption(envOption);
        systemCmd.AddOption(platformOption);
        systemCmd.AddOption(topologyOption);
        systemCmd.AddOption(refreshOption);
        systemCmd.AddOption(failOnReplaceOption);
        systemCmd.SetHandler(async (systemKey, env, platform, topology, refresh, failOnReplace) =>
        {
            RequirePlugin(plugin, "previewsystem");
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);
            var anyDestructive = false;
            foreach (var config in configs)
            {
                if (platform != null) config.Platform = platform;
                if (topology != null) config.Topology = topology;
                TryResolveSharedContext(config);
                var (system, factory) = PrepareSystem(plugin!, config);
                Console.WriteLine($"System: {config.SystemKey}  Env: {config.Environment}  Topology: {config.Topology}");
                Console.WriteLine();
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);
                anyDestructive |= await deployment.PreviewFoundationAsync(refresh);
            }
            if (failOnReplace && anyDestructive)
            {
                Console.Error.WriteLine("previewsystem: replace/delete operations detected and --fail-on-replace was set.");
                Environment.ExitCode = 2;
            }
        }, systemKeyOption, envOption, platformOption, topologyOption, refreshOption, failOnReplaceOption);
        root.AddCommand(systemCmd);

        // ---- previewtenant ----
        var tenantCmd = new Command("previewtenant",
            "Preview (dry-run) a tenant stack — including the CloudFront edge. No changes are applied. " +
            "Works before 'lz deploycontainer' (the image need not exist yet).");
        tenantCmd.AddOption(systemKeyOption);
        tenantCmd.AddOption(envOption);
        tenantCmd.AddOption(tenantKeyOption);
        tenantCmd.AddOption(topologyOption);
        tenantCmd.AddOption(refreshOption);
        tenantCmd.AddOption(failOnReplaceOption);
        tenantCmd.SetHandler(async (systemKey, env, tenantKey, topology, refresh, failOnReplace) =>
        {
            RequirePlugin(plugin, "previewtenant");
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);
            var anyDestructive = false;
            foreach (var config in configs)
            {
                if (topology != null) config.Topology = topology;
                TryResolveSharedContext(config);
                var (system, factory) = PrepareSystem(plugin!, config);
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);
                var tenants = ConfigResolver.ResolveTenantConfigs(config.SystemKey, config.Environment, tenantKey);
                foreach (var (tk, tenantConfig) in tenants)
                {
                    ValidateTenantConfig(config, tk, tenantConfig);
                    tenantConfig.CentralAuthDomain = config.CentralAuthDomain;
                    anyDestructive |= await deployment.PreviewTenantAsync(tk, tenantConfig, refresh);
                }
            }
            if (failOnReplace && anyDestructive)
            {
                Console.Error.WriteLine("previewtenant: replace/delete operations detected and --fail-on-replace was set.");
                Environment.ExitCode = 2;
            }
        }, systemKeyOption, envOption, tenantKeyOption, topologyOption, refreshOption, failOnReplaceOption);
        root.AddCommand(tenantCmd);

        // ---- previewshared ----
        var sharedCmd = new Command("previewshared",
            "Preview (dry-run) the shared-services (Keycloak + Tailscale) stack. No changes are applied.");
        sharedCmd.AddOption(refreshOption);
        sharedCmd.AddOption(failOnReplaceOption);
        sharedCmd.SetHandler(async (refresh, failOnReplace) =>
        {
            var sharedConfig = ConfigLoader.DiscoverAndLoadSharedConfig();
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
            var destructive = await deployment.PreviewAsync(refresh);
            if (failOnReplace && destructive)
            {
                Console.Error.WriteLine("previewshared: replace/delete operations detected and --fail-on-replace was set.");
                Environment.ExitCode = 2;
            }
        }, refreshOption, failOnReplaceOption);
        root.AddCommand(sharedCmd);
    }

    /// <summary>
    /// Best-effort shared-services seed-data propagation for preview. Preview is
    /// read-only: it does NOT bootstrap state or resolve cross-account secret ARNs
    /// (so a keycloak-topology preview that depends on those may differ slightly
    /// from a real deploy). Topologies without shared services no-op here.
    /// </summary>
    private static void TryResolveSharedContext(SystemConfig config)
    {
        try
        {
            var sharedConfig = ConfigLoader.DiscoverAndLoadSharedConfig();
            ConfigLoader.PropagateSharedSeedData(config, sharedConfig);
        }
        catch { /* no sharedconfig.yaml — fine for cognito/dynamodb/lambda topologies */ }
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
        var secretOption = new Option<string[]>("--secret",
            "Supply a required-secret value non-interactively (repeatable): " +
            "--secret \"<name>:<key>=<value>\", e.g. --secret \"scu/icecat:ApiToken=abc123\". " +
            "Used to satisfy systemconfig RequiredSecrets from scripts; when omitted and a " +
            "console is attached, missing values are prompted for (input hidden). NOTE: " +
            "command-line values can land in shell history — prefer the prompt when running by hand.")
        {
            Arity = ArgumentArity.ZeroOrMore,
        };
        var tailscaleKeyOption = new Option<string?>("--tailscale-key",
            "Tailscale API access key to seed into the system secret ({systemkey}/system) before " +
            "the subnet router deploys. If omitted and the key is not already stored, you are " +
            "prompted (input hidden) when a console is attached; otherwise the deploy fails asking " +
            "for --tailscale-key. Create one at https://login.tailscale.com/admin/settings/keys. " +
            "NOTE: a value on the command line can land in shell history — prefer the prompt by hand.");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(platformOption);
        cmd.AddOption(topologyOption);
        cmd.AddOption(secretOption);
        cmd.AddOption(tailscaleKeyOption);

        cmd.SetHandler(async (systemKey, env, platform, topology, secretArgs, tailscaleKey) =>
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
                        config.Profile, config.Region, config.State,
                        config.Hygiene?.S3NoncurrentVersionExpirationDays);

                // Required secrets (systemconfig RequiredSecrets, absent = skip):
                // verify each exists with all keys BEFORE any deploy step; fill
                // missing values from --secret args or the hidden interactive
                // prompt; fail fast with instructions when neither is available.
                if (config.RequiredSecrets is { Count: > 0 })
                    await Lz.Aws.Secrets.AwsSecretsEnsurer.EnsureAsync(
                        config,
                        Lz.Aws.Secrets.SecretsPlanner.ParseSecretArgs(secretArgs),
                        PromptSecretValue);

                var (system, factory) = PrepareSystem(plugin!, config);

                Console.WriteLine($"System: {config.SystemKey}, Environment: {config.Environment}");
                Console.WriteLine($"Platform: {config.Platform}, Topology: {config.Topology}");
                Console.WriteLine($"Central auth: {config.CentralAuthDomain}");
                Console.WriteLine();

                var deployment = new SystemDeployment(factory, system, config, Cts.Token);
                await deployment.DeployFoundationAsync(tailscaleKey);
            }
        }, systemKeyOption, envOption, platformOption, topologyOption, secretOption, tailscaleKeyOption);

        root.AddCommand(cmd);
    }

    /// <summary>
    /// Hidden-input console prompt for a required-secret value. Returns null when
    /// no console is attached (stdin redirected — scripted/CI contexts must use
    /// --secret instead) or when the user enters nothing.
    /// </summary>
    private static string? PromptSecretValue(string secretName, string key)
    {
        if (Console.IsInputRedirected)
            return null;
        Console.Write($"  Enter value for secret '{secretName}' key '{key}' (input hidden): ");
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        return sb.Length == 0 ? null : sb.ToString();
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
                            tag,
                            config.Hygiene?.EcrUntaggedImageRetentionDays);
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
                            tag,
                            config.Hygiene?.EcrUntaggedImageRetentionDays);
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
    // updatecontainer
    //
    // Zero-downtime alternative to deploytenant for the common case of "just
    // ship the new container image". deploytenant scales the ECS service to 0
    // during the Pulumi 'up' (AwsFargateAlbTenantServiceComponent starts at
    // DesiredCount=0) and the post-deploy action scales it back — that gap is
    // the outage. updatecontainer instead issues a rolling UpdateService
    // (ForceNewDeployment=true) with DesiredCount untouched, so ECS replaces
    // the task with no downtime. It only forces a deploy when the running
    // image digest differs from the latest in ECR (unless --force), and by
    // default it waits until the new task is healthy (or reports a rollback);
    // pass --no-wait for fire-and-forget.
    //
    // Intended flow:  lz deploycontainer  →  lz updatecontainer
    // ---------------------------------------------------------------

    private static void RegisterUpdateContainerCommand(
        RootCommand root,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("updatecontainer",
            "Zero-downtime rolling redeploy of tenant container(s) to pick up the latest ECR image. " +
            "Run after 'lz deploycontainer'. Skips services already on the latest image unless --force. " +
            "Waits for the rollout to complete by default; pass --no-wait for fire-and-forget.");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (updates all tenants if not specified — matches deploytenant)");
        var containerOption = new Option<string?>("--container",
            "Container name to update (updates all if not specified)");
        var forceOption = new Option<bool>("--force",
            "Force a redeploy even if the running image already matches the latest in ECR");
        var noWaitOption = new Option<bool>("--no-wait",
            "Fire-and-forget: return as soon as the rolling deploy is requested, " +
            "instead of waiting for the new task to become healthy (the default)");
        var dryRunOption = new Option<bool>("--dry-run",
            "Report what would be redeployed without making any changes");
        var tagOption = new Option<string>("--tag",
            () => "latest", "ECR image tag to compare and deploy");

        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(containerOption);
        cmd.AddOption(forceOption);
        cmd.AddOption(noWaitOption);
        cmd.AddOption(dryRunOption);
        cmd.AddOption(tagOption);

        // Use the InvocationContext handler (8 options exceeds the typed
        // SetHandler overloads cleanly) and pull each value from the parse result.
        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            var systemKey = ctx.ParseResult.GetValueForOption(systemKeyOption);
            var env = ctx.ParseResult.GetValueForOption(envOption);
            var tenantKey = ctx.ParseResult.GetValueForOption(tenantKeyOption);
            var container = ctx.ParseResult.GetValueForOption(containerOption);
            var force = ctx.ParseResult.GetValueForOption(forceOption);
            var wait = !ctx.ParseResult.GetValueForOption(noWaitOption); // wait by default
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOption);
            var tag = ctx.ParseResult.GetValueForOption(tagOption) ?? "latest";

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            var anyFailure = false;

            foreach (var config in configs)
            {
                var containerServiceConfig = ConfigLoader
                    .DiscoverAndLoadContainerServiceConfig(config.SystemKey, config.Environment);

                var containersToProcess = container != null
                    ? containerServiceConfig.Containers
                        .Where(c => c.Key.Equals(container, StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(c => c.Key, c => c.Value)
                    : containerServiceConfig.Containers;

                if (container != null && containersToProcess.Count == 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Warning: Container '{container}' not found in servicesconfig.");
                    Console.ResetColor();
                    continue;
                }

                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, tenantConfig) in tenants)
                {
                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region;

                    // Lambda topologies have no ECS service to roll — the per-tenant
                    // FUNCTION must be rolled with UpdateFunctionCode (Lambda resolves
                    // the image digest at update time, so a pushed :latest is invisible
                    // until then; a tenant Pulumi re-deploy no-ops too since the
                    // ImageUri string never changes). Same compare/force/wait/dry-run
                    // semantics as the ECS path.
                    if (AwsLambdaContainerUpdater.IsLambdaTopology(config.Topology))
                    {
                        var lambdaUpdater = new AwsLambdaContainerUpdater(profile, region);
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine(
                            $"=== updatecontainer: tenant {tk} ({config.Environment}, lambda){(dryRun ? " [dry-run]" : "")} ===");
                        Console.ResetColor();

                        foreach (var (svcName, _) in containersToProcess)
                        {
                            // Must match AwsLambdaTenantServiceComponent (function name) and
                            // deploycontainer/deploytenant (ECR repo) naming.
                            var functionName = $"{config.SystemKey}-{tk}-{svcName}";
                            var lambdaEcrRepo =
                                $"{config.SystemKey}-{tenantConfig.TenantSuffix}-{config.Environment}-{tk}-{svcName}";
                            try
                            {
                                var result = await lambdaUpdater.UpdateIfNewerAsync(
                                    functionName, lambdaEcrRepo, tag, force, wait, dryRun, Cts.Token);
                                PrintUpdateResult(result);
                                if (result.Outcome == UpdateOutcome.Failed)
                                    anyFailure = true;
                            }
                            catch (Exception ex)
                            {
                                anyFailure = true;
                                Console.ForegroundColor = ConsoleColor.Red;
                                Console.WriteLine($"  [error] {functionName}: {ex.Message}");
                                Console.ResetColor();
                            }
                        }
                        continue;
                    }

                    var updater = new AwsContainerUpdater(profile, region);

                    // Cluster naming differs by topology: the EcsExpress family (the current
                    // ecs-fargate-* / lambda-* topologies) names it {sk}-{env}-cluster; the
                    // legacy Ecs platform uses {sk}-cluster. Resolve whichever actually exists
                    // instead of hardcoding one convention — the hardcoded {sk}-cluster
                    // predated the EcsExpress topology and caused "Cluster not found".
                    var cluster = await updater.ResolveClusterAsync(
                        new[] { $"{config.SystemKey}-{config.Environment}-cluster", $"{config.SystemKey}-cluster" },
                        Cts.Token);
                    if (cluster == null)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine(
                            $"  [error] no ECS cluster found for system '{config.SystemKey}' (tried " +
                            $"{config.SystemKey}-{config.Environment}-cluster and {config.SystemKey}-cluster). " +
                            "Run 'lz deploysystem' first.");
                        Console.ResetColor();
                        continue;
                    }

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(
                        $"=== updatecontainer: tenant {tk} ({config.Environment}){(dryRun ? " [dry-run]" : "")} ===");
                    Console.ResetColor();

                    foreach (var (svcName, _) in containersToProcess)
                    {
                        // Must match AwsFargateAlbTenantServiceComponent (service) and
                        // deploycontainer/deploytenant (ECR repo) naming.
                        var ecsService = $"{config.SystemKey}-{tk}-{svcName}";
                        var ecrRepo =
                            $"{config.SystemKey}-{tenantConfig.TenantSuffix}-{config.Environment}-{tk}-{svcName}";

                        try
                        {
                            var result = await updater.UpdateIfNewerAsync(
                                cluster, ecsService, ecrRepo, tag, force, wait, dryRun, Cts.Token);
                            PrintUpdateResult(result);
                            if (result.Outcome == UpdateOutcome.Failed)
                                anyFailure = true;
                        }
                        catch (Exception ex)
                        {
                            anyFailure = true;
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine($"  [error] {ecsService}: {ex.Message}");
                            Console.ResetColor();
                        }
                    }
                }
            }

            if (anyFailure)
                Environment.ExitCode = 1;
        });

        root.AddCommand(cmd);
    }

    private static void PrintUpdateResult(ContainerUpdateResult r)
    {
        var (color, label) = r.Outcome switch
        {
            UpdateOutcome.UpToDate       => (ConsoleColor.DarkGray, "up-to-date"),
            UpdateOutcome.Deployed       => (ConsoleColor.Green,    "deploying "),
            UpdateOutcome.Verified       => (ConsoleColor.Green,    "verified  "),
            UpdateOutcome.WouldDeploy    => (ConsoleColor.Yellow,   "would-depl"),
            UpdateOutcome.NoEcrImage     => (ConsoleColor.Yellow,   "no-image  "),
            UpdateOutcome.NoRunningTasks => (ConsoleColor.Yellow,   "not-running"),
            UpdateOutcome.Failed         => (ConsoleColor.Red,      "FAILED    "),
            _                                       => (ConsoleColor.Gray,     "?         "),
        };
        Console.ForegroundColor = color;
        Console.WriteLine($"  [{label}] {r.Service}: {r.Detail}");
        Console.ResetColor();
    }

    // ---------------------------------------------------------------
    // updateedge — in-place CloudFront Function update (zero downtime)
    // ---------------------------------------------------------------

    private static void RegisterUpdateEdgeCommand(
        RootCommand root,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("updateedge",
            "In-place update of a tenant's CloudFront Functions (viewer-request, " +
            "viewer-response, explore-rewrite) from the repo's CloudFront/*.js files. " +
            "Zero downtime — no Pulumi, no container restart. Run after editing a " +
            "CFViewerRequest.js etc. Skips functions whose live code already matches.");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (updates all tenants if not specified — matches deploytenant)");
        var functionOption = new Option<string?>("--function",
            "Which function to update: viewer-request | viewer-response | explore-rewrite " +
            "(all present functions if not specified)");
        var dryRunOption = new Option<bool>("--dry-run",
            "Report what would change without publishing any function");

        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(functionOption);
        cmd.AddOption(dryRunOption);

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            var systemKey = ctx.ParseResult.GetValueForOption(systemKeyOption);
            var env = ctx.ParseResult.GetValueForOption(envOption);
            var tenantKey = ctx.ParseResult.GetValueForOption(tenantKeyOption);
            var function = ctx.ParseResult.GetValueForOption(functionOption);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOption);

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            var anyFailure = false;

            foreach (var config in configs)
            {
                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, tenantConfig) in tenants)
                {
                    var profile = tenantConfig.Profile ?? config.Profile;
                    var region = tenantConfig.Region ?? config.Region;

                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(
                        $"=== updateedge: tenant {tk} ({config.Environment}){(dryRun ? " [dry-run]" : "")} ===");
                    Console.ResetColor();

                    try
                    {
                        var updater = new AwsEdgeUpdater(
                            config.SystemKey, profile, region);
                        var results = await updater.UpdateAsync(
                            tk,
                            tenantConfig.TenantSuffix,
                            config.Environment,
                            tenantConfig.RootDomain,
                            tenantConfig.ConfigDirectory,
                            tenantConfig.LegacyDomains,
                            function,
                            dryRun,
                            Cts.Token,
                            corsConfig: tenantConfig.CDN?.Cors);

                        foreach (var r in results)
                            PrintEdgeResult(r);

                        if (results.Any(r => r.Outcome == EdgeUpdateOutcome.Failed))
                            anyFailure = true;
                    }
                    catch (Exception ex)
                    {
                        anyFailure = true;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  [error] {tk}: {ex.Message}");
                        Console.ResetColor();
                    }
                }
            }

            if (anyFailure)
                Environment.ExitCode = 1;
        });

        root.AddCommand(cmd);
    }

    private static void PrintEdgeResult(EdgeFunctionResult r)
    {
        var (color, label) = r.Outcome switch
        {
            EdgeUpdateOutcome.Updated  => (ConsoleColor.Green,    "published "),
            EdgeUpdateOutcome.Skipped  => (ConsoleColor.DarkGray, "skipped   "),
            EdgeUpdateOutcome.NotFound => (ConsoleColor.Yellow,   "not-found "),
            EdgeUpdateOutcome.Failed   => (ConsoleColor.Red,      "FAILED    "),
            _                                      => (ConsoleColor.Gray,     "?         "),
        };
        Console.ForegroundColor = color;
        var detail = r.Detail != null ? $": {r.Detail}" : "";
        Console.WriteLine($"  [{label}] {r.FunctionType}{detail}");
        Console.ResetColor();
    }

    // ---------------------------------------------------------------
    // updateconfig — publish tenant config to SSM out-of-band (no restart)
    // ---------------------------------------------------------------

    private static void RegisterUpdateConfigCommand(
        RootCommand root,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("updateconfig",
            "Publish a tenant's runtime config (tenantconfig.*.yaml) to SSM Parameter Store " +
            "without a full deploytenant. The AppHost's refreshing config provider picks it up " +
            "within its poll interval (~60s) — no container restart. Pass --invalidate to also " +
            "invalidate the /config CloudFront path (only needed if /config is cached).");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Tenant key (updates all tenants if not specified — matches deploytenant)");
        var invalidateOption = new Option<bool>("--invalidate",
            "Also invalidate the /config CloudFront path (no-op while /config is CachingDisabled)");
        var dryRunOption = new Option<bool>("--dry-run",
            "Report what would be written without making any changes");

        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(invalidateOption);
        cmd.AddOption(dryRunOption);

        cmd.SetHandler(async (System.CommandLine.Invocation.InvocationContext ctx) =>
        {
            var systemKey = ctx.ParseResult.GetValueForOption(systemKeyOption);
            var env = ctx.ParseResult.GetValueForOption(envOption);
            var tenantKey = ctx.ParseResult.GetValueForOption(tenantKeyOption);
            var invalidate = ctx.ParseResult.GetValueForOption(invalidateOption);
            var dryRun = ctx.ParseResult.GetValueForOption(dryRunOption);

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            var anyFailure = false;

            foreach (var config in configs)
            {
                var monorepoRoot = ConfigLoader.DiscoverMonorepoRoot(
                    config.SystemKey, config.Environment);
                if (monorepoRoot == null)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine("Cannot find monorepo root (no systemconfig found).");
                    Console.ResetColor();
                    anyFailure = true;
                    continue;
                }

                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                foreach (var (tk, tenantConfig) in tenants)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(
                        $"=== updateconfig: tenant {tk} ({config.Environment}){(dryRun ? " [dry-run]" : "")} ===");
                    Console.ResetColor();

                    try
                    {
                        await AwsTenantConfigPublisher.PublishAsync(
                            monorepoRoot, config, tk, tenantConfig, invalidate, dryRun);
                    }
                    catch (Exception ex)
                    {
                        anyFailure = true;
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"  [error] {tk}: {ex.Message}");
                        Console.ResetColor();
                    }
                }
            }

            if (anyFailure)
                Environment.ExitCode = 1;
        });

        root.AddCommand(cmd);
    }

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
        var forceDeleteProtectedOption = new Option<bool>("--force-delete-protected",
            "Delete the subtenant DynamoDB table even if it has deletion " +
            "protection enabled (disables protection first, then deletes — DATA " +
            "LOSS). Without this flag, a protected table is left intact and the " +
            "destroy fails. Separate from --force (which only empties the S3 bucket).");
        var yesOption = new Option<bool>("--yes", "Skip the confirmation prompt.");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(subtenantKeyOption);
        cmd.AddOption(forceOption);
        cmd.AddOption(forceDeleteProtectedOption);
        cmd.AddOption(yesOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, subtenantKey, force, forceDeleteProtected, yes) =>
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
                    Console.WriteLine($"  - DynamoDB table {config.SystemKey}_{tenantKey}_{subtenantKey} will be deleted (DATA LOSS)" +
                        $"{(forceDeleteProtected ? "; deletion protection, if enabled, will be DISABLED first (--force-delete-protected)" : " — if it has deletion protection enabled, the destroy will FAIL unless you also pass --force-delete-protected")}.");
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
                    config, tenantConfig, subtenantKey, profile, region, force, forceDeleteProtected);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"Subtenant '{subtenantKey}' destroyed.");
                Console.ResetColor();
                Console.WriteLine(
                    "Next: remove the subtenant from subtenantconfig.yaml and " +
                    "run `lz deploysubtenants` to refresh KVS. The KVS entry " +
                    "for the destroyed subtenant's domain will need manual cleanup.");
            }
        }, systemKeyOption, envOption, tenantKeyOption, subtenantKeyOption, forceOption, forceDeleteProtectedOption, yesOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // unlock
    // ---------------------------------------------------------------

    private static void RegisterUnlockCommand(
        RootCommand root,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("unlock",
            "Release a stale Pulumi state lock on a stack (like `pulumi cancel`). A lock " +
            "left behind by a hard-killed deploy/destroy (Ctrl+C, crash) blocks further " +
            "operations with \"the stack is currently locked\". Defaults to the SYSTEM " +
            "(foundation) stack; pass --tenantkey to unlock a tenant stack. Use ONLY when " +
            "no deploy/destroy is actually running.");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Unlock the tenant stack {systemkey}-{tenantkey}-{env} instead of the foundation stack.");
        var yesOption = new Option<bool>("--yes", "Skip the confirmation prompt.");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(yesOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, yes) =>
        {
            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var stackName = string.IsNullOrEmpty(tenantKey)
                    ? $"{config.SystemKey}-{config.Environment}"
                    : $"{config.SystemKey}-{tenantKey}-{config.Environment}";

                IReadOnlyList<Lz.Aws.Orchestration.PulumiStateLock.LockRecord> locks;
                try
                {
                    locks = await Lz.Aws.Orchestration.PulumiStateLock.ListAsync(config, stackName);
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"Could not read locks for stack '{stackName}': {ex.Message}");
                    Console.ResetColor();
                    Environment.ExitCode = 1;
                    continue;
                }

                if (locks.Count == 0)
                {
                    Console.WriteLine($"Stack '{stackName}': no lock present — nothing to release.");
                    continue;
                }

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Stack '{stackName}' is locked by {locks.Count} lock(s):");
                foreach (var l in locks)
                    Console.WriteLine(
                        $"  - pid {(l.Pid?.ToString() ?? "?")} on host {l.Hostname ?? "?"} " +
                        $"({l.Username ?? "?"}) since {l.Timestamp ?? "?"}");
                Console.WriteLine(
                    "Release the lock ONLY if you are certain no deploy/destroy is still running " +
                    "(verify the host/PID above). Force-unlocking a live update can corrupt the stack state.");
                Console.ResetColor();

                if (!yes)
                {
                    Console.Write("Type 'yes' to release the lock: ");
                    if (!string.Equals(Console.ReadLine(), "yes", StringComparison.Ordinal))
                    {
                        Console.WriteLine("Aborted.");
                        continue;
                    }
                }

                try
                {
                    var removed = await Lz.Aws.Orchestration.PulumiStateLock.ReleaseAsync(config, stackName);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"Released {removed} lock(s) on stack '{stackName}'. Re-run your deploy now.");
                    Console.ResetColor();
                }
                catch (Exception ex)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Error.WriteLine($"Failed to release lock on '{stackName}': {ex.Message}");
                    Console.ResetColor();
                    Environment.ExitCode = 1;
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption, yesOption);

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

        var yesOption = new Option<bool>("--yes",
            "Skip the interactive confirmation prompt (for scripted/test runs)");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(yesOption);

        cmd.SetHandler(async (systemKey, env, yes) =>
        {
            RequirePlugin(plugin, "destroysystem");

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            // --yes must confirm a target the operator actually named; refusing
            // to blanket-confirm a runtime-discovered multi-system set.
            if (yes && systemKey == null && configs.Count > 1)
            {
                Console.Error.WriteLine(
                    $"--yes with {configs.Count} systems resolved and no --systemkey: " +
                    "refusing to blanket-confirm. Pass --systemkey (or drop --yes).");
                Environment.ExitCode = 1;
                return;
            }

            foreach (var config in configs)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(
                    $"WARNING: This will destroy foundation for system " +
                    $"'{config.SystemKey}' ({config.Environment}).");
                Console.ResetColor();
                if (yes)
                {
                    Console.WriteLine("Confirmed via --yes.");
                }
                else
                {
                    Console.Write("Type 'yes' to confirm: ");
                    var confirmation = Console.ReadLine();
                    if (confirmation?.Trim().ToLowerInvariant() != "yes")
                    {
                        Console.WriteLine("Aborted.");
                        continue;
                    }
                }

                // Ensure Pulumi state backend exists (needed to find the stack)
                if (config.State != null)
                    await AwsStateBootstrapper.BootstrapAsync(
                        config.Profile, config.Region, config.State,
                        config.Hygiene?.S3NoncurrentVersionExpirationDays);

                var (system, factory) = PrepareSystem(plugin!, config);
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);
                await deployment.DestroyFoundationAsync();
            }
        }, systemKeyOption, envOption, yesOption);

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
        var yesOption = new Option<bool>("--yes",
            "Skip the interactive confirmation prompt (for scripted/test runs)");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(yesOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, yes) =>
        {
            RequirePlugin(plugin, "destroytenant");

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                // Ensure Pulumi state backend exists (needed to find the stack)
                if (config.State != null)
                    await AwsStateBootstrapper.BootstrapAsync(
                        config.Profile, config.Region, config.State,
                        config.Hygiene?.S3NoncurrentVersionExpirationDays);

                var (system, factory) = PrepareSystem(plugin!, config);
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);

                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                // Same rule as destroysystem: --yes never blanket-confirms a
                // runtime-discovered multi-tenant set.
                if (yes && tenantKey == null && tenants.Count > 1)
                {
                    Console.Error.WriteLine(
                        $"--yes with {tenants.Count} tenants resolved and no --tenantkey: " +
                        "refusing to blanket-confirm. Pass --tenantkey (or drop --yes).");
                    Environment.ExitCode = 1;
                    return;
                }

                foreach (var (tk, _) in tenants)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine(
                        $"WARNING: This will destroy tenant '{tk}' for system " +
                        $"'{config.SystemKey}' ({config.Environment}).");
                    Console.ResetColor();
                    if (yes)
                    {
                        Console.WriteLine("Confirmed via --yes.");
                    }
                    else
                    {
                        Console.Write("Type 'yes' to confirm: ");
                        var confirmation = Console.ReadLine();
                        if (confirmation?.Trim().ToLowerInvariant() != "yes")
                        {
                            Console.WriteLine("Aborted.");
                            continue;
                        }
                    }

                    await deployment.DestroyTenantAsync(tk);
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption, yesOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // status
    // ---------------------------------------------------------------

    private static void RegisterStatusCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("status",
            "Show deployment status across all layers (shared + system + tenants). " +
            "Scope with --layer or --tenantkey.");
        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Show only this tenant (implies --layer tenant)");
        tenantKeyOption.AddAlias("--tenant");
        var layerOption = new Option<string?>("--layer",
            "Scope to one layer: shared | system | tenant (default: all)");
        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(layerOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, layer) =>
        {
            RequirePlugin(plugin, "status");

            layer = layer?.ToLowerInvariant();
            if (layer != null && layer != "shared" && layer != "system" && layer != "tenant" && layer != "all")
            {
                Console.Error.WriteLine($"--layer must be one of: shared, system, tenant. Got '{layer}'.");
                Environment.ExitCode = 1;
                return;
            }
            // --tenantkey implies the tenant layer.
            if (tenantKey != null && layer == null) layer = "tenant";
            var all = layer == null || layer == "all";
            var doShared = all || layer == "shared";
            var doSystem = all || layer == "system";
            var doTenants = all || layer == "tenant";

            // Shared services are account-wide (not per-system) — report once.
            if (doShared)
            {
                try
                {
                    var sharedConfig = ConfigLoader.DiscoverAndLoadSharedConfig();
                    var sharedFactory = CreateFactory(new AwsSystemConfig
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
                    await new SharedDeployment(sharedFactory, sharedConfig, Cts.Token).StatusAsync();
                }
                catch (Exception ex)
                {
                    // No sharedconfig.yaml — cognito/dynamodb/lambda topologies don't use
                    // shared services. Note it only when shared was explicitly requested.
                    if (layer == "shared")
                        Console.WriteLine($"Shared-services: not configured ({ex.Message}).");
                }
            }

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var (system, factory) = PrepareSystem(plugin!, config);
                var deployment = new SystemDeployment(factory, system, config, Cts.Token);

                if (doSystem)
                    await deployment.StatusFoundationAsync();

                if (doTenants)
                {
                    var tenants = ConfigResolver.ResolveTenantConfigs(
                        config.SystemKey, config.Environment, tenantKey);
                    foreach (var (tk, _) in tenants)
                        await deployment.StatusTenantAsync(tk);
                }
            }
        }, systemKeyOption, envOption, tenantKeyOption, layerOption);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    // verify — live-AWS interrogation (no Pulumi state involved)
    // ---------------------------------------------------------------

    private static void RegisterVerifyCommand(
        RootCommand root, ILzPlugin? plugin,
        Option<string?> systemKeyOption, Option<string?> envOption)
    {
        var cmd = new Command("verify",
            "Interrogate LIVE AWS for every resource the topology is expected to have " +
            "created, classified as stack (Pulumi-managed; gone after destroy) or " +
            "persistent (survives destroy). Derived from config naming conventions, " +
            "not Pulumi state — works after a destroy. Read-only.");

        var tenantKeyOption = new Option<string?>("--tenantkey",
            "Verify only this tenant (default: all tenants of the system)");
        tenantKeyOption.AddAlias("--tenant");
        var jsonOption = new Option<bool>("--json",
            "Machine-readable output (one JSON document on stdout)");
        var scopeOption = new Option<string>("--scope", () => "all",
            "Which categories to report: all | stack | persistent | smoke");
        var expectOption = new Option<string?>("--expect",
            "Assert an overall state and set the exit code: 'deployed' (every stack " +
            "resource present AND every runtime smoke probe passing) or 'destroyed' " +
            "(no stack resource present or tombstoned). Persistent resources are " +
            "always informational. --scope only filters the REPORT — the verdict " +
            "always covers every check that ran. --tenantkey, however, narrows which " +
            "tenants are checked at all: a verdict with --tenantkey attests ONLY that " +
            "tenant.");

        cmd.AddOption(systemKeyOption);
        cmd.AddOption(envOption);
        cmd.AddOption(tenantKeyOption);
        cmd.AddOption(jsonOption);
        cmd.AddOption(scopeOption);
        cmd.AddOption(expectOption);

        cmd.SetHandler(async (systemKey, env, tenantKey, json, scope, expect) =>
        {
            RequirePlugin(plugin, "verify");

            scope = scope.ToLowerInvariant();
            if (scope is not ("all" or "stack" or "persistent" or "smoke"))
            {
                Console.Error.WriteLine($"--scope must be all, stack, persistent, or smoke. Got '{scope}'.");
                Environment.ExitCode = 1;
                return;
            }
            expect = expect?.ToLowerInvariant();
            if (expect is not (null or "deployed" or "destroyed"))
            {
                Console.Error.WriteLine($"--expect must be deployed or destroyed. Got '{expect}'.");
                Environment.ExitCode = 1;
                return;
            }

            var resolvedEnv = ConfigResolver.ResolveEnvironment(env);
            var configs = ConfigResolver.ResolveSystemConfigs(resolvedEnv, systemKey);

            foreach (var config in configs)
            {
                var (system, _) = PrepareSystem(plugin!, config);
                var tenants = ConfigResolver.ResolveTenantConfigs(
                    config.SystemKey, config.Environment, tenantKey);

                List<Lz.Aws.Verification.ResourceCheckResult> results;
                try
                {
                    results = await Lz.Aws.Verification.AwsLiveVerifier.VerifyAsync(
                        config, tenants, system, Cts.Token);
                }
                catch (NotSupportedException ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    Environment.ExitCode = 1;
                    return;
                }

                // The --expect verdict is ALWAYS computed over the unfiltered
                // results — otherwise `--scope persistent --expect destroyed`
                // would be vacuously MET with the whole stack still deployed.
                // --scope only narrows what is REPORTED.
                var stack = results.Where(r =>
                    r.Category == Lz.Aws.Verification.ResourceCategory.Stack).ToList();
                var persistent = results.Where(r =>
                    r.Category == Lz.Aws.Verification.ResourceCategory.Persistent).ToList();
                var smoke = results.Where(r =>
                    r.Category == Lz.Aws.Verification.ResourceCategory.Smoke).ToList();
                var errors = results.Where(r =>
                    r.State == Lz.Aws.Verification.ResourceState.Error).ToList();

                var reported = scope switch
                {
                    "stack" => stack,
                    "persistent" => persistent,
                    "smoke" => smoke,
                    _ => results,
                };

                // The rules (deployed = stack AND smoke; destroyed = stack-only;
                // Error downgrades MET) live in the unit-tested VerifyVerdict.
                bool? expectMet = Lz.Aws.Verification.VerifyVerdict.Compute(expect, results);

                if (json)
                {
                    var doc = new
                    {
                        systemKey = config.SystemKey,
                        environment = config.Environment,
                        topology = config.Topology,
                        profile = config.Profile,
                        region = config.Region,
                        scope,
                        expect,
                        expectMet,
                        summary = new
                        {
                            stackPresent = stack.Count(r =>
                                r.State == Lz.Aws.Verification.ResourceState.Present),
                            stackAbsent = stack.Count(r =>
                                r.State == Lz.Aws.Verification.ResourceState.Absent),
                            stackTombstoned = stack.Count(r =>
                                r.State == Lz.Aws.Verification.ResourceState.ScheduledForDeletion),
                            persistentPresent = persistent.Count(r =>
                                r.State == Lz.Aws.Verification.ResourceState.Present),
                            persistentAbsent = persistent.Count(r =>
                                r.State == Lz.Aws.Verification.ResourceState.Absent),
                            smokePassed = smoke.Count(r =>
                                r.State == Lz.Aws.Verification.ResourceState.Present),
                            smokeFailed = smoke.Count(r =>
                                r.State != Lz.Aws.Verification.ResourceState.Present),
                            errors = errors.Count,
                        },
                        results = reported.Select(r => new
                        {
                            category = r.Category.ToString().ToLowerInvariant(),
                            service = r.Service,
                            kind = r.Kind,
                            name = r.Name,
                            state = r.State.ToString(),
                            detail = r.Detail,
                        }),
                    };
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(doc,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine($"System: {config.SystemKey}, Environment: {config.Environment}, " +
                                      $"Topology: {config.Topology}");
                    void Print(string title, List<Lz.Aws.Verification.ResourceCheckResult> rs)
                    {
                        if (rs.Count == 0) return;
                        Console.WriteLine();
                        Console.WriteLine($"  {title}");
                        foreach (var r in rs)
                        {
                            var (glyph, color) = r.State switch
                            {
                                Lz.Aws.Verification.ResourceState.Present =>
                                    ("+", ConsoleColor.Green),
                                Lz.Aws.Verification.ResourceState.Absent =>
                                    ("-", ConsoleColor.DarkGray),
                                Lz.Aws.Verification.ResourceState.ScheduledForDeletion =>
                                    ("!", ConsoleColor.Yellow),
                                _ => ("x", ConsoleColor.Red),
                            };
                            Console.ForegroundColor = color;
                            Console.Write($"    {glyph} ");
                            Console.ResetColor();
                            Console.WriteLine($"{r.Service,-16} {r.Kind,-34} {r.Name}" +
                                (r.Detail != null ? $"  [{r.Detail}]" : ""));
                        }
                    }
                    if (scope is "all" or "stack")
                        Print("Stack (Pulumi-managed — gone after destroy):", stack);
                    if (scope is "all" or "persistent")
                        Print("Persistent (survives destroy by design):", persistent);
                    if (scope is "all" or "smoke")
                        Print("Smoke (runtime probes of the deployed surfaces):", smoke);

                    Console.WriteLine();
                    Console.WriteLine($"  Stack: {stack.Count(r => r.State == Lz.Aws.Verification.ResourceState.Present)} present, " +
                        $"{stack.Count(r => r.State == Lz.Aws.Verification.ResourceState.Absent)} absent, " +
                        $"{stack.Count(r => r.State == Lz.Aws.Verification.ResourceState.ScheduledForDeletion)} tombstoned; " +
                        $"Persistent: {persistent.Count(r => r.State == Lz.Aws.Verification.ResourceState.Present)} present, " +
                        $"{persistent.Count(r => r.State == Lz.Aws.Verification.ResourceState.Absent)} absent; " +
                        $"Smoke: {smoke.Count(r => r.State == Lz.Aws.Verification.ResourceState.Present)}/{smoke.Count} passing; " +
                        $"Errors: {errors.Count}");
                    if (expect != null)
                    {
                        Console.ForegroundColor = expectMet == true
                            ? ConsoleColor.Green : ConsoleColor.Red;
                        Console.WriteLine($"  Expectation '{expect}': " +
                            (expectMet == true ? "MET" : "NOT MET"));
                        Console.ResetColor();
                    }
                }

                if (expect != null && expectMet != true)
                    Environment.ExitCode = 1;
            }
        }, systemKeyOption, envOption, tenantKeyOption, jsonOption, scopeOption, expectOption);

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

                    var manager = new AwsParkManager(
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

                    var manager = new AwsParkManager(
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
    // gettenants — print the tenants + subtenants discovered from
    // local YAML configs as JSON. Source-of-truth output: reads the
    // exact same systemconfig/tenantconfig/subtenantconfig files that
    // a deploy would, no AWS calls, no network. Intended for build
    // systems (e.g. WASMApp.csproj's Pre-Build target) that need the
    // tenant directory at compile time without depending on a live
    // CDN deployment.
    //
    // Schema (see also EventIt/WASMApp/wwwroot/index.local.html which
    // consumes this output):
    //   {
    //     "systemKey": "bcs",
    //     "env": "dev",
    //     "tenants": [
    //       {
    //         "key": "bcs",
    //         "rootDomain": "eventitdev.click",
    //         "subtenants": [
    //           { "key": "cerulean", "host": "cerulean.eventitdev.click",
    //             "displayName": "Cerulean Beach Resort",
    //             "requiresAuth": true, "includeOnVenuesPage": true },
    //           ...
    //         ]
    //       }
    //     ]
    //   }
    //
    // requiresAuth is the cascade-resolved value of WebApps[/].AuthConfig
    // (system → tenant → subtenant) — null/empty AuthConfig means public.
    // includeOnVenuesPage mirrors SubtenantEntry.IncludeOnVenuesPage; the
    // /venues/ page filters hidden ones, but the local-dev picker shows
    // them all so devs can QA hidden subtenants before public launch.
    //
    // No --tenant filter: a single system can have multiple tenants
    // (bcs is the only tenant today, but the schema is multi-tenant)
    // and the picker wants the whole directory.
    // ---------------------------------------------------------------
    // lz repos — per-repository status across a multi-repo workspace.
    //
    // Deliberately loads NO lz config: this works in any multi-repo checkout, which is what makes
    // it useful outside a deployed system. Note it is distinct from `lz status`, which reports
    // DEPLOYMENT state.
    // ---------------------------------------------------------------
    private static void RegisterReposCommand(RootCommand root)
    {
        var cmd = new Command("repos",
            "Report git status for every repository in the workspace: branch, latest commit, " +
            "working tree, release markers, and ahead/behind vs the tracked upstream. " +
            "Reads no lz config, so it works in any multi-repo checkout. Distinct from " +
            "'lz status', which reports DEPLOYMENT state. " +
            "By default it fetches each repo first — a network round trip per repo, and the only " +
            "part of this command that writes anything (it updates remote-tracking refs). " +
            "Use --no-fetch for a fully offline snapshot.");

        var rootOpt = new Option<string?>("--root",
            "Workspace root (default: nearest ancestor containing a repos/ folder, else the nearest git repo).");
        var noFetchOpt = new Option<bool>("--no-fetch", () => false,
            "Work entirely offline: no fetch and no remote marker lookup. Much faster, but " +
            "ahead/behind is then as of the last fetch and the Tags column reads (offline).");
        var jsonOpt = new Option<bool>("--json", () => false,
            "Machine-readable output (one JSON document on stdout).");
        var tagsOpt = new Option<string>("--tags", () => "prod,test",
            "Comma-separated release markers to report. Empty string disables marker lookup " +
            "(one fewer network round trip per repo).");
        var concurrencyOpt = new Option<int>("--concurrency", () => 8,
            "How many repositories to inspect at once.");

        cmd.AddOption(rootOpt);
        cmd.AddOption(noFetchOpt);
        cmd.AddOption(jsonOpt);
        cmd.AddOption(tagsOpt);
        cmd.AddOption(concurrencyOpt);

        cmd.SetHandler(async (string? rootPath, bool noFetch, bool json, string tags, int concurrency) =>
        {
            try
            {
                var workspaceRoot = rootPath is null
                    ? RepoDiscovery.FindWorkspaceRoot()
                    : Path.GetFullPath(rootPath);

                if (!Directory.Exists(workspaceRoot))
                {
                    Console.Error.WriteLine($"Workspace root not found: {workspaceRoot}");
                    Environment.ExitCode = 1;
                    return;
                }

                var options = new RepoStatusOptions
                {
                    Root = workspaceRoot,
                    Fetch = !noFetch,
                    Tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    MaxConcurrency = concurrency,
                };

                var rows = await new RepoStatusCollector().CollectAsync(options);

                if (rows.Count == 0)
                {
                    // Not an error: a directory with no repositories is a legitimate answer.
                    if (json) Console.Out.WriteLine(RepoStatusRenderer.ToJson(rows, workspaceRoot));
                    else Console.Out.WriteLine($"No git repositories found under {workspaceRoot}");
                    Environment.ExitCode = 0;
                    return;
                }

                if (json)
                {
                    Console.Out.WriteLine(RepoStatusRenderer.ToJson(rows, workspaceRoot));
                }
                else
                {
                    Console.Out.WriteLine($"workspace: {workspaceRoot}");
                    Console.Out.WriteLine();
                    Console.Out.Write(RepoStatusRenderer.ToTable(rows));
                    Console.Out.WriteLine();
                    Console.Out.WriteLine(RepoStatusRenderer.ToSummary(rows));
                }

                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"repos: {ex.Message}");
                Environment.ExitCode = 1;
            }
        }, rootOpt, noFetchOpt, jsonOpt, tagsOpt, concurrencyOpt);

        root.AddCommand(cmd);
    }

    // ---------------------------------------------------------------
    private static void RegisterGetTenantsCommand(RootCommand root)
    {
        var cmd = new Command("gettenants",
            "Print tenants + subtenants (from local YAML configs).");
        var envOpt = new Option<string?>("--env", "Environment override (otherwise auto-detected from cwd).");
        var prettyOpt = new Option<bool>("--pretty", () => false, "Pretty-print JSON with indent (no effect if --html-cards).");
        var htmlCardsOpt = new Option<bool>("--html-cards", () => false,
            "Emit HTML markup for the local-dev picker (anchors per subtenant) instead of JSON. " +
            "Each card is an <a href='/?sub={key}'> with name/host/badges. The picker template " +
            "in WASMApp/wwwroot is server-rendered with the output, eliminating dependency on " +
            "client-side JS to populate cards (works around Browser Refresh middleware response " +
            "stream encoding bugs that block inline-script execution in some VS dev configurations).");
        cmd.AddOption(envOpt);
        cmd.AddOption(prettyOpt);
        cmd.AddOption(htmlCardsOpt);

        cmd.SetHandler((string? envOverride, bool pretty, bool htmlCards) =>
        {
            try
            {
                var env = ConfigResolver.ResolveEnvironment(envOverride);
                var systems = ConfigResolver.ResolveSystemConfigs(env);

                // BCS today has one system; multi-system installs would
                // get multiple JSON outputs (one per system) if we
                // looped here. We emit one at a time, keyed by
                // systemKey — the single-system case is the expected
                // shape and the multi-system case becomes "run twice
                // with --systemkey", consistent with how other lz
                // commands disambiguate.
                if (systems.Count > 1)
                {
                    Console.Error.WriteLine(
                        $"Multiple systemconfigs found for env '{env}'. " +
                        "Use --systemkey to disambiguate (not yet implemented for gettenants).");
                    Environment.ExitCode = 2;
                    return;
                }
                var system = systems[0];

                var tenants = ConfigResolver.ResolveTenantConfigs(system.SystemKey, env);
                var output = new
                {
                    systemKey = system.SystemKey,
                    env,
                    tenants = tenants.Select(pair =>
                    {
                        var (tk, tc) = pair;
                        var subs = (tc.Subtenants ?? new Dictionary<string, SubtenantEntry>())
                            .Select(kv =>
                            {
                                var (stk, se) = (kv.Key, kv.Value);
                                var label = string.IsNullOrEmpty(se.SubDomain) ? stk : se.SubDomain;
                                var host = $"{label}.{tc.RootDomain}";
                                // Cascade-resolve AuthConfig for "/" — same logic
                                // BCPlugin uses for the venues page, kept consistent
                                // here so the picker's badge matches what CFRequest
                                // actually enforces.
                                var resolved = ConfigMerger.ResolveWebApps(system, tc, se.Behaviors);
                                var rootApp = resolved.FirstOrDefault(r => r.Path == "/");
                                var requiresAuth = !string.IsNullOrEmpty(rootApp?.AuthConfig);
                                return new
                                {
                                    key = stk,
                                    host,
                                    displayName = string.IsNullOrEmpty(se.DisplayName) ? null : se.DisplayName,
                                    requiresAuth,
                                    includeOnVenuesPage = se.IncludeOnVenuesPage,
                                };
                            }).ToList();
                        return new
                        {
                            key = tk,
                            rootDomain = tc.RootDomain,
                            subtenants = subs,
                        };
                    }).ToList(),
                };

                if (htmlCards)
                {
                    // Emit a single line of HTML — one anchor per
                    // subtenant. Designed to be substituted into
                    // wwwroot/index.local.template.html by the
                    // WASMApp.csproj Pre-Build target as a single-line
                    // block (MSBuild's WriteLinesToFile + escaping is
                    // happier with single lines). The template wraps
                    // the output in a #cards container so the CSS
                    // styling already in the picker applies as-is.
                    //
                    // Each card is a static <a> — no JS needed on the
                    // picker page. Click navigates to /?sub={key};
                    // indexinit.js's localhost branch handles the rest.
                    var html = new System.Text.StringBuilder();
                    foreach (var pair in tenants)
                    {
                        var (_, tc) = pair;
                        if (tc.Subtenants is null) continue;
                        foreach (var (stk, se) in tc.Subtenants)
                        {
                            var label = string.IsNullOrEmpty(se.SubDomain) ? stk : se.SubDomain;
                            var host = $"{label}.{tc.RootDomain}";
                            var displayName = string.IsNullOrEmpty(se.DisplayName) ? stk : se.DisplayName;
                            var resolved = ConfigMerger.ResolveWebApps(system, tc, se.Behaviors);
                            var rootApp = resolved.FirstOrDefault(r => r.Path == "/");
                            var requiresAuth = !string.IsNullOrEmpty(rootApp?.AuthConfig);

                            // System.Net.WebUtility.HtmlEncode covers
                            // <>&"' — sufficient for both element body
                            // and attribute values in the simple
                            // anchor structure we're emitting.
                            html.Append("<a class=\"sub-card\" href=\"/?sub=")
                                .Append(System.Net.WebUtility.UrlEncode(stk))
                                .Append("\" role=\"listitem\" aria-label=\"Use subtenant ")
                                .Append(System.Net.WebUtility.HtmlEncode(displayName))
                                .Append("\">")
                                .Append("<p class=\"sub-name\">")
                                .Append(System.Net.WebUtility.HtmlEncode(displayName))
                                .Append("</p>")
                                .Append("<p class=\"sub-host\">")
                                .Append(System.Net.WebUtility.HtmlEncode(host))
                                .Append("</p>");
                            if (requiresAuth || !se.IncludeOnVenuesPage)
                            {
                                html.Append("<div class=\"badges\">");
                                if (requiresAuth)
                                    html.Append("<span class=\"badge auth\">auth required</span>");
                                if (!se.IncludeOnVenuesPage)
                                    html.Append("<span class=\"badge hidden\">hidden from /venues/</span>");
                                html.Append("</div>");
                            }
                            html.Append("</a>");
                        }
                    }
                    Console.Out.WriteLine(html.ToString());
                }
                else
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(output,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = pretty });
                    Console.Out.WriteLine(json);
                }
                Environment.ExitCode = 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                Environment.ExitCode = 1;
            }
        }, envOpt, prettyOpt, htmlCardsOpt);

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
