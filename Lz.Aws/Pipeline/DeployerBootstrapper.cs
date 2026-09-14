using System.IO.Compression;
using Amazon.IdentityManagement;
using Amazon.IdentityManagement.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>
/// Creates the in-account deployer, in a TARGET account — <c>scu-dev</c>, not the build account.
///
/// <para>WHY THIS IS A SEPARATE COMMAND from <see cref="PipelineBootstrapper"/>, which §5.5's
/// wording did not anticipate: the two sides have different CARDINALITY. There is one build
/// account per system and one target account per ENVIRONMENT, so the build side is bootstrapped
/// once ever and this side once per environment. Folding them together would be harmless
/// mechanically — everything is idempotent — but it would require build-account write credentials
/// to stand up a test deployer, coupling two privilege domains for no benefit. Bootstrapping is
/// meant to be narrow and time-boxed (§5.5); two commands keep each one pointed at a single
/// account and a single set of rights.</para>
///
/// <para>IT STOPS SHORT OF MAKING THE DEPLOYER LIVE, deliberately. The roles, the evidence store, the
/// functions and the state machine are created; the EventBridge rule that starts an execution when a
/// build record lands is NOT (stage D), and the signature hook is attached to no service (C4's wiring,
/// which moves the deploy plan). Inert and inspectable beats live and broken.</para>
/// </summary>
public static class DeployerBootstrapper
{
    public static async Task BootstrapAsync(SystemConfig config, bool apply)
    {
        // GATED, exactly as bootstrappipeline is. The deployer is the half of the pipeline that
        // runs INSIDE this account and can roll its services; it is not created for a system that
        // has not opted in. Checked before anything is read from AWS.
        if (config.Pipeline is not { Enabled: true })
            throw new InvalidOperationException(
                $"lz bootstrapdeployer refuses: {config.SystemKey}/{config.Environment} has no " +
                "`Pipeline:` block with `Enabled: true`. This command creates a state machine and a " +
                "role that can roll this account's ECS services; it will not do that for a system " +
                "that has not opted in.");

        var profile = config.Profile;
        var region = config.Region;

        var accountId = await ResolveAccountAsync(profile, region);

        // THE TRIGGER'S INPUTS ARE READ ONLY FOR AN ENVIRONMENT THAT HAS ONE: the cluster that exists here, and the tenants
        // whose services a build rolls. Both are reads, so the dry run shows the real routes.
        var triggerInputs = DeployerPlanner.TriggerWanted(config)
            ? await ResolveTriggerInputsAsync(config, profile, region)
            : null;
        // THE DISTRIBUTIONS A CLIENT DEPLOY INVALIDATES, read only where a client repository names its web app (P4 stage C).
        var clientInputs = DeployerPlanner.ClientApps(config).Count > 0
            ? await ResolveClientInputsAsync(config, profile)
            : null;
        var plan = DeployerPlanner.Plan(config, accountId, triggerInputs, clientInputs);

        // THE PACKAGES ARE READ BEFORE ANYTHING IS CREATED, so a build without them fails the dry run
        // rather than half-way through an apply.
        var packages = LocatePackages(plan);

        Print(config, plan, accountId, profile, apply, packages);

        // WHO WOULD BE TOLD, read in the dry run too: a topic nobody confirmed is alerts that reach no one.
        if (plan.Alerts is { } plannedAlerts)
            await ReportSubscriptionsAsync(profile, region, plannedAlerts, config);

        // A VERIFIER-LESS HOOK IS REFUSED WHERE IT WOULD BE USED (DecoupledCd.md §14.2). Checked before the
        // dry-run return so a dry run says what the apply will do.
        var hookRefusal = RefusalForHookPackage(
            packages[DeployerPackages.SignatureHook].MissingVerifierFiles, config.Pipeline?.EnforceSignatures == true);
        if (hookRefusal != null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  REFUSED: {hookRefusal}");
            Console.ResetColor();
            if (apply)
                throw new InvalidOperationException($"lz bootstrapdeployer refuses: {hookRefusal}");
        }

        if (!apply)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("DRY RUN — nothing was created. Re-run with --apply to create it.");
            Console.ResetColor();
            return;
        }

        // THE MIRROR OF bootstrappipeline's GUARD, and it catches the likelier mistake of the two:
        // running this with the build profile out of habit. The deployer belongs in the account it
        // deploys INTO; creating it in the build account would put a role that can roll ECS services
        // in the one account that is supposed to run no workload.
        if (config.Pipeline?.ArtifactAccountId is { } build && build == accountId)
            throw new InvalidOperationException(
                $"this profile resolves to {accountId}, which is Pipeline.ArtifactAccountId — the " +
                "BUILD account. The deployer belongs in the account it deploys into. Pass the " +
                "target environment's profile (e.g. --profile scu-dev).");

        // AND IT MUST BE THIS ENVIRONMENT'S ACCOUNT, not merely not the build account: this run writes a
        // registry policy admitting the build account's images, and prod's profile against dev's config
        // would admit dev's replication into prod.
        CrossAccount.RequireTargetAccount(config.Pipeline?.TargetAccountId, accountId, "bootstrapdeployer");

        var creds = AwsCredentialsFactory.ResolveOrThrow(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);

        using var s3 = creds != null ? new AmazonS3Client(creds, endpoint) : new AmazonS3Client(endpoint);
        using var iam = creds != null
            ? new AmazonIdentityManagementServiceClient(creds, endpoint)
            : new AmazonIdentityManagementServiceClient(endpoint);
        using var sfn = creds != null
            ? new AmazonStepFunctionsClient(creds, endpoint)
            : new AmazonStepFunctionsClient(endpoint);
        using var lambda = creds != null
            ? new AmazonLambdaClient(creds, endpoint) : new AmazonLambdaClient(endpoint);
        using var logs = creds != null
            ? new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(creds, endpoint)
            : new Amazon.CloudWatchLogs.AmazonCloudWatchLogsClient(endpoint);

        await EnsureEvidenceStoreAsync(s3, plan.EvidenceStore, region);

        // WRITE-ONCE BEFORE ANY ROLE EXISTS THAT COULD WRITE EVIDENCE: the Record functions' roles, below.
        await BucketPolicies.MergeAsync(s3, plan.EvidenceStore, plan.EvidenceStorePolicy);

        // THE ALERTS TOPIC BEFORE ANYTHING THAT PUBLISHES TO IT (P2 stage D3): the alarms and the rule below name it.
        using var sns = creds != null
            ? new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient(creds, endpoint)
            : new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient(endpoint);
        if (plan.Alerts is { } alertsTopic)
            await AlertsApply.EnsureTopicAsync(sns, alertsTopic);

        // THE REPOSITORIES BEFORE THE PERMISSION, so there is no moment at which the build account may
        // replicate into a repository this account has not hardened. Without ecr:CreateRepository in the
        // policy, replication into a missing repository fails rather than creating an unhardened one.
        using (var ecr = creds != null ? new Amazon.ECR.AmazonECRClient(creds, endpoint) : new Amazon.ECR.AmazonECRClient(endpoint))
        {
            foreach (var repository in plan.ImageRepositories)
                await EcrRepositoryHardening.EnsureAsync(ecr, repository, config.Hygiene?.EcrUntaggedImageRetentionDays ?? 14);

            // THE SCAN RULE BEFORE THE PERMISSION TOO, merged into the registry's rules: this registry also
            // holds repositories the pipeline does not own. A replica counts as a push (measured 2026-09-12),
            // so an image arrives and is scanned without Verify doing anything but wait.
            await EcrRegistryScanning.ApplyAsync(ecr, plan.ImageRepositories);

            await ApplyReplicationPermissionAsync(ecr, plan.ReplicationPermission
                ?? throw new InvalidOperationException("the plan has no replication permission; it was planned without an account."));
        }

        var roleArn = await EnsureRoleAsync(iam, plan.RoleName, plan.StateMachineTrustPolicy,
            "lz decoupled-CD deployer. Rolls the service image by digest.",
            ($"{plan.RoleName}-deploy", plan.RolePolicy), plan.DenyPolicy);

        foreach (var fn in plan.Functions)
        {
            // THE LOG GROUP BEFORE THE FUNCTION, so its first line lands where retention already applies.
            if (plan.LogRetentionDays is int retention)
                await EnsureLogGroupAsync(logs, $"/aws/lambda/{fn.Name}", retention);

            var fnRole = await EnsureRoleAsync(iam, fn.RoleName, plan.FunctionTrustPolicy,
                $"lz decoupled-CD deployer function {fn.Name}.",
                ($"{fn.RoleName}-grant", fn.Policy), plan.DenyPolicy);

            await EnsureFunctionAsync(lambda, fn, fnRole, packages[fn.Package]);
        }

        await EnsureRoleAsync(iam, plan.HookInvokerRoleName, plan.HookInvokerTrustPolicy,
            "Lets ECS invoke the lz signature hook during a service deployment.",
            ($"{plan.HookInvokerRoleName}-invoke", plan.HookInvokerPolicy), plan.DenyPolicy);

        await EnsureStateMachineAsync(sfn, plan, roleArn, accountId, region);

        // THE TRIGGER, AFTER THE MACHINE IT STARTS (P2 stage D). Or, with the flag off, the start rule an earlier run
        // created is taken away — a trigger that could only be switched on would keep deploying every build.
        using (var events = creds != null
                   ? new Amazon.EventBridge.AmazonEventBridgeClient(creds, endpoint) : new Amazon.EventBridge.AmazonEventBridgeClient(endpoint))
        using (var cloudWatch = creds != null
                   ? new Amazon.CloudWatch.AmazonCloudWatchClient(creds, endpoint) : new Amazon.CloudWatch.AmazonCloudWatchClient(endpoint))
        {
            if (plan.Trigger is { } trigger)
            {
                using var sqs = creds != null ? new Amazon.SQS.AmazonSQSClient(creds, endpoint) : new Amazon.SQS.AmazonSQSClient(endpoint);

                await TriggerApply.StartOnRecordsAsync(events, sqs, lambda, cloudWatch, trigger);
            }
            else
            {
                await TriggerApply.RemoveRuleAsync(events, DeployerPlanner.TriggerBusName(config), DeployerPlanner.StartRuleName(config),
                    "Pipeline.DeployOnBuildRecord is off");
            }

            // THE ALERTS, AFTER THE MACHINE WHOSE FAILURES THEY REPORT AND THE FUNCTION THEY SCHEDULE (P2 stage D3). Or, with
            // the flag off, the rules and alarm an earlier run created are taken away.
            if (plan.Alerts is { } alerts)
                await AlertsApply.WireAsync(events, lambda, cloudWatch, alerts);
            else
                await AlertsApply.RemoveAsync(events, cloudWatch, DeployerPlanner.FailedDeployRuleName(config),
                    DeployerPlanner.CorroborateScheduleRuleName(config), DeployerPlanner.CorroborateErrorsAlarmName(config));
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Deployer bootstrap complete.");
        Console.ResetColor();

        Console.WriteLine();
        Console.WriteLine(plan.Trigger is null
            ? "NOTHING STARTS THE DEPLOYER: Pipeline.DeployOnBuildRecord is off, so executions are started by hand."
            : $"THIS ACCOUNT'S HALF OF THE TRIGGER IS WIRED: a record event arriving on {plan.Trigger.BusName} starts the deployer.");
        if (plan.Alerts is { } wired)
            await ReportSubscriptionsAsync(profile, region, wired, config);
        Console.WriteLine("Not built:");
        Console.WriteLine("  - a reconciler: an event that ends in a dead-letter queue is retried by nothing; start that deploy by hand");
        if (plan.Alerts is null)
            Console.WriteLine("  - alerts: Pipeline.Alerts is off, so failures and images without a record notify nobody");
        if (config.Pipeline?.EnforceSignatures != true)
            Console.WriteLine("  - the signature hook's attachment: it is attached by `lz deploytenant` under Pipeline.EnforceSignatures, which is off");
        Console.WriteLine();
        var verifyRole = plan.Functions.Single(f => f.Handler == DeployerHandlers.Verify).RoleName;
        Console.WriteLine("THE BUILD ACCOUNT'S HALF is a separate command, run with that account's profile:");
        Console.WriteLine(plan.Trigger is null
            ? $"  lz bootstrappipeline --apply   (replicates into this account; lets {verifyRole} read image/*)"
            : $"  lz bootstrappipeline --apply   (replicates into this account; lets {verifyRole} read image/*; forwards record events to {plan.Trigger.BusName})");
        if (plan.ClientTargets is { Count: > 0 })
            Console.WriteLine($"    and, for client bundles, lets {verifyRole} read client/* records and HEAD client/* bundles, and " +
                              $"{DeployerPlanner.DeployBundleRoleName(config)} read client/* bundles");
        Console.WriteLine("  Only images pushed AFTER it runs replicate here — ECR does not copy what is already there.");
    }

    /// <summary>
    /// A function's log group, with its retention (DecoupledCd.md §14.3) — created here rather than by Lambda on the first
    /// log line, which makes a group that never expires. Read back.
    /// </summary>
    private static async Task EnsureLogGroupAsync(Amazon.CloudWatchLogs.IAmazonCloudWatchLogs logs, string name, int retentionDays)
    {
        try
        {
            await logs.CreateLogGroupAsync(new Amazon.CloudWatchLogs.Model.CreateLogGroupRequest { LogGroupName = name });
        }
        catch (Amazon.CloudWatchLogs.Model.ResourceAlreadyExistsException)
        {
            // Lambda made it, or an earlier run did: only its retention is ours.
        }

        await logs.PutRetentionPolicyAsync(new Amazon.CloudWatchLogs.Model.PutRetentionPolicyRequest
        {
            LogGroupName = name,
            RetentionInDays = retentionDays,
        });

        var groups = await logs.DescribeLogGroupsAsync(new Amazon.CloudWatchLogs.Model.DescribeLogGroupsRequest { LogGroupNamePrefix = name });
        var group = (groups.LogGroups ?? new List<Amazon.CloudWatchLogs.Model.LogGroup>()).SingleOrDefault(g => g.LogGroupName == name);
        if (group?.RetentionInDays != retentionDays)
            throw new InvalidOperationException($"log group '{name}' reads back with retention {group?.RetentionInDays?.ToString() ?? "none"}; written {retentionDays} days.");

        Console.WriteLine($"  log group '{name}': logs kept {retentionDays} days.");
    }

    /// <summary>
    /// How many people the alerts reach, and how to add one. lz never subscribes an address: a subscription is a person's to
    /// make and to confirm.
    /// </summary>
    private static async Task ReportSubscriptionsAsync(string? profile, string region, DeployerAlertsPlan alerts, SystemConfig config)
    {
        var creds = AwsCredentialsFactory.ResolveOrThrow(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        using var sns = creds != null
            ? new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient(creds, endpoint)
            : new Amazon.SimpleNotificationService.AmazonSimpleNotificationServiceClient(endpoint);

        var subscriptions = await AlertsApply.SubscriptionsAsync(sns, alerts.TopicArn);
        if (subscriptions is { Confirmed: > 0 } found)
        {
            Console.WriteLine($"  alerts reach {found.Confirmed} confirmed subscription(s) of {alerts.TopicName}" +
                              (found.Pending > 0 ? $" ({found.Pending} pending confirmation)." : "."));
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(subscriptions is null
            ? $"  alerts topic {alerts.TopicName} does not exist yet; the apply creates it, and nobody is subscribed."
            : $"  NOBODY RECEIVES THESE ALERTS: {alerts.TopicName} has no confirmed subscription" +
              (subscriptions.Value.Pending > 0 ? $" ({subscriptions.Value.Pending} pending confirmation)." : "."));
        Console.WriteLine("  To receive them, subscribe an address and click the confirmation link AWS sends it:");
        Console.WriteLine($"    aws sns subscribe --topic-arn {alerts.TopicArn} --protocol email --notification-endpoint YOUR_EMAIL_ADDRESS" +
                          $"{(string.IsNullOrEmpty(profile) ? "" : $" --profile {profile}")} --region {config.Region}");
        Console.ResetColor();
    }

    /// <summary>
    /// The cluster and tenants the trigger's routes name. The tenants are the workspace's tenant configs — the set
    /// <c>lz updatecontainer</c> rolls — and the cluster is whichever of <c>updatecontainer</c>'s two naming conventions exists
    /// and is ACTIVE here.
    /// </summary>
    private static async Task<DeployerTriggerInputs> ResolveTriggerInputsAsync(SystemConfig config, string? profile, string region)
    {
        List<(string TenantKey, TenantConfig Config)> tenants;
        try
        {
            tenants = ConfigResolver.ResolveTenantConfigs(config.SystemKey, config.Environment);
        }
        catch (FileNotFoundException)
        {
            // The planner refuses an empty list, naming what it looked for.
            tenants = new List<(string, TenantConfig)>();
        }

        // THE DEPLOYER IS REGIONAL: a tenant deployed elsewhere is not a service this machine can roll.
        foreach (var (tenantKey, tenant) in tenants)
        {
            if (tenant.Region is { } tenantRegion && !string.Equals(tenantRegion, region, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"lz bootstrapdeployer refuses: tenant {tenantKey} deploys to {tenantRegion}, but this deployer and its " +
                    $"trigger are in {region}. A build could not roll that tenant's service from here.");
        }

        var creds = AwsCredentialsFactory.ResolveOrThrow(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        using var ecs = creds != null ? new Amazon.ECS.AmazonECSClient(creds, endpoint) : new Amazon.ECS.AmazonECSClient(endpoint);

        var candidates = new List<string> { $"{config.SystemKey}-{config.Environment}-cluster", $"{config.SystemKey}-cluster" };
        var found = await ecs.DescribeClustersAsync(new Amazon.ECS.Model.DescribeClustersRequest { Clusters = candidates });
        var cluster = candidates.FirstOrDefault(name =>
                (found.Clusters ?? new List<Amazon.ECS.Model.Cluster>()).Any(c => c.ClusterName == name && c.Status == "ACTIVE"))
            ?? throw new InvalidOperationException(
                $"lz bootstrapdeployer refuses: Pipeline.DeployOnBuildRecord is on, but neither {string.Join(" nor ", candidates)} is " +
                "an ACTIVE ECS cluster in this account, so a build would have no service to roll. Deploy the system first.");

        return new DeployerTriggerInputs(cluster, tenants.Select(t => t.TenantKey).OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// The CloudFront distributions that serve this environment's tenants: the ones <c>lz deploywebapp</c> invalidates, found
    /// the way it finds them — by the tenant's root domain among a distribution's aliases. A tenant with none is refused,
    /// since a client deploy there could clear no cached copy.
    /// </summary>
    private static async Task<DeployerClientInputs> ResolveClientInputsAsync(SystemConfig config, string? profile)
    {
        List<(string TenantKey, TenantConfig Config)> tenants;
        try
        {
            tenants = ConfigResolver.ResolveTenantConfigs(config.SystemKey, config.Environment);
        }
        catch (FileNotFoundException)
        {
            tenants = new List<(string, TenantConfig)>();
        }

        if (tenants.Count == 0)
            throw new InvalidOperationException(
                $"lz bootstrapdeployer refuses: client bundles deploy here, but no tenant config was found for {config.SystemKey}/" +
                $"{config.Environment}, so the distributions serving the apps are unknown. Run from the workspace that holds the " +
                "tenantconfig files.");

        var creds = AwsCredentialsFactory.ResolveOrThrow(profile);
        // CloudFront is global; its API is reached through us-east-1 whatever the system's region.
        using var cloudFront = creds != null
            ? new Amazon.CloudFront.AmazonCloudFrontClient(creds, Amazon.RegionEndpoint.USEast1)
            : new Amazon.CloudFront.AmazonCloudFrontClient(Amazon.RegionEndpoint.USEast1);

        var distributions = new List<Amazon.CloudFront.Model.DistributionSummary>();
        string? marker = null;
        do
        {
            var page = await cloudFront.ListDistributionsAsync(new Amazon.CloudFront.Model.ListDistributionsRequest { Marker = marker });
            distributions.AddRange(page.DistributionList?.Items ?? new List<Amazon.CloudFront.Model.DistributionSummary>());
            marker = page.DistributionList?.IsTruncated == true ? page.DistributionList.NextMarker : null;
        }
        while (marker != null);

        var ids = new List<string>();
        foreach (var (tenantKey, tenant) in tenants)
        {
            var domain = tenant.RootDomain;
            var id = distributions.FirstOrDefault(d => d.Aliases?.Items?.Contains(domain, StringComparer.OrdinalIgnoreCase) == true)?.Id
                ?? throw new InvalidOperationException(
                    $"lz bootstrapdeployer refuses: client bundles deploy here, but no CloudFront distribution in this account serves " +
                    $"tenant {tenantKey}'s root domain '{domain}', so a deploy could not clear its cached copies. Deploy the tenant first.");
            ids.Add(id);
        }

        return new DeployerClientInputs(ids.Distinct(StringComparer.Ordinal).OrderBy(i => i, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Why an apply may not write the signature-hook package as built, or null when it may.
    ///
    /// <para>REFUSED ONLY WHERE THE HOOK IS USED. Every CI-built Lz carries a hook package without the
    /// Notation verifier, which is fetched on a workstation and never committed; that hook answers FAILED to
    /// every deployment. Harmless while no service attaches it, so it is only warned about then — but under
    /// <c>Pipeline.EnforceSignatures</c> writing it would turn every deploy of the service into a rollback,
    /// so the apply stops before it writes anything.</para>
    /// </summary>
    public static string? RefusalForHookPackage(IReadOnlyList<string> missingVerifierFiles, bool enforceSignatures)
        => enforceSignatures && missingVerifierFiles.Count > 0
            ? "the signature-hook package has no Notation verifier (missing " +
              string.Join(", ", missingVerifierFiles) + ") and Pipeline.EnforceSignatures is on. Writing it " +
              "would replace the hook with one that answers FAILED to every deployment, so every deploy of " +
              "the service would roll back. Build Lz where Lz.Aws.Deployer/fetch-verifier.ps1 has run, or " +
              "turn EnforceSignatures off and run `lz deploytenant`, which takes the hook off the service, first."
            : null;

    /// <summary>
    /// The zips each function's code comes from, read into memory, or a refusal naming what is
    /// missing. Looked for next to Lz.Aws.dll, where the build puts them in every load scenario.
    /// </summary>
    private static Dictionary<string, PackageBytes> LocatePackages(PipelineDeployer plan)
    {
        var dir = Path.Combine(Path.GetDirectoryName(typeof(DeployerBootstrapper).Assembly.Location)!, "Lambda");
        var result = new Dictionary<string, PackageBytes>();

        foreach (var package in plan.Functions.Select(f => f.Package).Distinct())
        {
            var path = Path.Combine(dir, DeployerPackages.ZipFor(package));
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"the deployment package {path} does not exist. It is built with Lz.Aws " +
                    "(`dotnet build Lz.slnx`); a build that skipped it cannot create the deployer's functions.");

            var bytes = File.ReadAllBytes(path);
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var missing = NotationLayout.MissingFromPackage(zip.Entries.Select(e => e.FullName));
            result[package] = new PackageBytes(path, bytes, missing);
        }

        return result;
    }

    private sealed record PackageBytes(string Path, byte[] Bytes, IReadOnlyList<string> MissingVerifierFiles);

    private static async Task<string> ResolveAccountAsync(string? profile, string region)
    {
        var creds = AwsCredentialsFactory.ResolveOrThrow(profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        using var sts = creds != null
            ? new AmazonSecurityTokenServiceClient(creds, endpoint)
            : new AmazonSecurityTokenServiceClient(endpoint);

        return (await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest())).Account;
    }

    private static void Print(
        SystemConfig config, PipelineDeployer plan, string accountId, string? profile, bool apply,
        IReadOnlyDictionary<string, PackageBytes> packages)
    {
        Console.WriteLine($"=== Deployer bootstrap: {config.SystemKey} / {config.Environment} ===");
        Console.WriteLine($"  account:  {accountId}{(string.IsNullOrEmpty(profile) ? " (ambient credentials)" : $" (profile {profile})")}");
        Console.WriteLine($"  region:   {config.Region}");
        Console.WriteLine($"  mode:     {(apply ? "APPLY" : "dry run")}");
        Console.WriteLine();
        Console.WriteLine($"  state machine: {plan.StateMachineName}  (Standard)");
        Console.WriteLine($"  role:          {plan.RoleName}  + an explicit self-rewrite Deny");
        Console.WriteLine($"  evidence:      {plan.EvidenceStore}");
        if (plan.EvidenceStorePolicy.Any(p => p["Sid"]?.GetValue<string>() == WriteOnceStore.Sid))
            Console.WriteLine($"                 write-once: bucket policy {WriteOnceStore.Sid} refuses any PutObject without If-None-Match, from anyone");
        Console.WriteLine($"  approval:      {(plan.ApprovalRequired ? "REQUIRED — a waitForTaskToken gate" : "not required (this environment deploys on its own)")}");
        Console.WriteLine();
        Console.WriteLine(plan.LogRetentionDays is int days
            ? $"  functions (each with its own role and the same Deny; logs kept {days} days, Hygiene.LambdaLogRetentionDays):"
            : "  functions (each with its own role and the same Deny; logs never expire — Hygiene.LambdaLogRetentionDays is not set):");
        foreach (var fn in plan.Functions)
            Console.WriteLine($"    {fn.Name,-40} {fn.Package}.zip  {fn.TimeoutSeconds}s  {fn.MemoryMb} MB");
        Console.WriteLine($"  hook invoker:  {plan.HookInvokerRoleName}  (assumed by ECS; invokes the hook only)");
        Console.WriteLine();
        Console.WriteLine($"  replicated repositories (immutable tags, AES256, scan-on-push by the repository setting and a merged registry rule): {string.Join(", ", plan.ImageRepositories)}");
        Console.WriteLine($"  registry policy {CrossAccount.ReplicationSid}: ecr:ReplicateImage from {config.Pipeline?.ArtifactAccountId} into exactly those, never ecr:CreateRepository");
        var target = config.Pipeline?.TargetAccountId;
        Console.WriteLine(target == accountId
            ? $"  target account: {accountId} matches Pipeline.TargetAccountId"
            : $"  target account: THIS PROFILE RESOLVES TO {accountId}, BUT Pipeline.TargetAccountId IS {target ?? "not set"}; apply will refuse");
        Console.WriteLine();

        if (plan.Trigger is { } trigger)
        {
            Console.WriteLine("  trigger (Pipeline.DeployOnBuildRecord):");
            Console.WriteLine($"    bus {trigger.BusName}: only {config.Pipeline?.ArtifactAccountId}'s role {DeployerPlanner.RecordForwarderRoleName(config)} may put events");
            Console.WriteLine($"    rule {trigger.RuleName}: record events from account {config.Pipeline?.ArtifactAccountId} only -> {trigger.StartFunctionName}");
            foreach (var route in trigger.Routes)
                Console.WriteLine($"    {route.RecordPrefix} rolls {string.Join(", ", route.Targets.Select(t => $"{t.Target.Service} in {t.Target.Cluster} as req-{{stamp}}-{{run id}}-{t.TenantKey}-1"))}");
            Console.WriteLine($"    failures to queue {trigger.DeadLetterQueueName} after {trigger.MaximumRetryAttempts} retries; alarm {trigger.AlarmName}, " +
                              (trigger.AlarmActions.Count == 0 ? "notifying nobody" : $"notifying {string.Join(", ", trigger.AlarmActions)}"));
        }
        else
        {
            Console.WriteLine($"  trigger: off (Pipeline.DeployOnBuildRecord) — rule {DeployerPlanner.StartRuleName(config)} is removed if an earlier run created it");
        }
        Console.WriteLine();

        if (plan.Alerts is { } alerts)
        {
            Console.WriteLine("  alerts (Pipeline.Alerts):");
            Console.WriteLine($"    topic {alerts.TopicName}: published to only by the rule below and this pipeline's alarms, by ARN");
            Console.WriteLine($"    rule {alerts.FailedDeployRuleName}: executions of {plan.StateMachineName} that end FAILED, TIMED_OUT or ABORTED -> the topic");
            Console.WriteLine($"    {alerts.CorroborateFunctionName} runs {alerts.ScheduleExpression}: an image in this environment's pipeline repository " +
                              "that no build record names is recorded once under anomalies/ and alerted once");
            Console.WriteLine($"    alarm {alerts.CorroborateErrorsAlarmName}: a sweep that fails -> the topic");
        }
        else
        {
            Console.WriteLine($"  alerts: off (Pipeline.Alerts) — rules {DeployerPlanner.FailedDeployRuleName(config)} and " +
                              $"{DeployerPlanner.CorroborateScheduleRuleName(config)} are removed if an earlier run created them");
        }
        Console.WriteLine();

        if (plan.ClientTargets is { Count: > 0 } clientTargets)
        {
            Console.WriteLine("  client bundles (class 2), started by hand — nothing forwards client records yet:");
            foreach (var t in clientTargets)
                Console.WriteLine($"    {t.Repo} deploys as {t.App}: s3://{t.Bucket}/{t.KeyPrefix}, lease {BundleMarker.Key}, " +
                                  $"invalidates {t.InvalidationPath} on {string.Join(", ", t.Distributions)}");
            Console.WriteLine($"    the machine branches on the verified class after Verify{(plan.ApprovalRequired ? " and Approve" : "")}");
        }
        else
        {
            Console.WriteLine("  client bundles: none (no client repository names the web app it deploys as in Artifacts)");
        }
        Console.WriteLine();

        foreach (var (name, package) in packages.OrderBy(p => p.Key))
        {
            Console.WriteLine($"  package {name}.zip: {package.Bytes.Length / 1024} KB");
            if (name == DeployerPackages.SignatureHook && package.MissingVerifierFiles.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("    WITHOUT THE VERIFIER — missing " + string.Join(", ", package.MissingVerifierFiles));
                Console.WriteLine("    The hook will answer FAILED to every deployment until Notation is packaged.");
                Console.WriteLine(config.Pipeline?.EnforceSignatures == true
                    ? "    Pipeline.EnforceSignatures is on, so the service uses it: the apply is refused."
                    : "    Harmless while no service attaches it (Pipeline.EnforceSignatures is off).");
                Console.ResetColor();
            }
        }
        Console.WriteLine();
    }

    /// <summary>
    /// Merge the replication permission into this registry's policy by Sid, preserving every other
    /// statement: the registry has one policy, and it may already grant something else.
    /// </summary>
    private static async Task ApplyReplicationPermissionAsync(
        Amazon.ECR.IAmazonECR ecr, System.Text.Json.Nodes.JsonObject statement)
    {
        string? existing = null;
        try
        {
            var response = await ecr.GetRegistryPolicyAsync(new Amazon.ECR.Model.GetRegistryPolicyRequest());

            // S3's SDK hands back "no policy" as a 404 RESPONSE rather than an exception (measured on the
            // build account's first apply). ECR's raised RegistryPolicyNotFoundException on dev's first apply —
            // the registry policy written was clean — but a response that is not a success is refused here
            // all the same, rather than merged into as though it were a policy.
            if (response.HttpStatusCode != System.Net.HttpStatusCode.OK)
                throw new InvalidOperationException(
                    $"reading the registry policy returned {(int)response.HttpStatusCode}; refusing to write over it.");

            existing = response.PolicyText;
        }
        catch (Amazon.ECR.Model.RegistryPolicyNotFoundException)
        {
            // No registry policy yet: the merge starts from an empty document.
        }

        var merged = CrossAccount.MergeBySid(existing, new[] { statement });
        await ecr.PutRegistryPolicyAsync(new Amazon.ECR.Model.PutRegistryPolicyRequest { PolicyText = merged });

        var total = System.Text.Json.Nodes.JsonNode.Parse(merged)!["Statement"]!.AsArray().Count;
        Console.WriteLine($"  registry policy written: ecr:ReplicateImage for the build account; {total - 1} other statement(s) preserved.");
    }

    private static async Task EnsureEvidenceStoreAsync(IAmazonS3 s3, string bucket, string region)
    {
        try
        {
            await s3.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = bucket });
            Console.WriteLine($"  evidence store '{bucket}' already exists. Skipping creation.");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            await s3.PutBucketAsync(S3BucketRequests.Create(bucket, region));
            Console.WriteLine($"  evidence store '{bucket}' created.");
        }

        await s3.PutBucketVersioningAsync(new PutBucketVersioningRequest
        {
            BucketName = bucket,
            VersioningConfig = new S3BucketVersioningConfig { Status = VersionStatus.Enabled },
        });
        await s3.PutPublicAccessBlockAsync(new PutPublicAccessBlockRequest
        {
            BucketName = bucket,
            PublicAccessBlockConfiguration = new PublicAccessBlockConfiguration
            {
                BlockPublicAcls = true, BlockPublicPolicy = true,
                IgnorePublicAcls = true, RestrictPublicBuckets = true,
            },
        });
        Console.WriteLine("      versioning on, public access blocked.");
    }

    /// <summary>
    /// A role trusted by one service principal, with its grant and the self-rewrite Deny re-put on
    /// every run — so a policy that drifted from the plan is corrected, not skipped.
    ///
    /// <para>TWO POLICIES, NOT ONE. The Deny is separate so it is visible in the console rather than
    /// buried mid-document, and because an explicit Deny beats any Allow — including a future one
    /// nobody connects to this decision (§5.5).</para>
    /// </summary>
    private static async Task<string> EnsureRoleAsync(
        IAmazonIdentityManagementService iam, string roleName, string trust, string description,
        (string Name, string Document) grant, string denyPolicy)
    {
        string arn;
        try
        {
            arn = (await iam.GetRoleAsync(new GetRoleRequest { RoleName = roleName })).Role.Arn;
            Console.WriteLine($"  role '{roleName}' already exists — updating its policies.");
            await iam.UpdateAssumeRolePolicyAsync(new UpdateAssumeRolePolicyRequest
            {
                RoleName = roleName, PolicyDocument = trust,
            });
        }
        catch (NoSuchEntityException)
        {
            arn = (await iam.CreateRoleAsync(new CreateRoleRequest
            {
                RoleName = roleName,
                AssumeRolePolicyDocument = trust,
                Description = description,
                MaxSessionDuration = 3600,
            })).Role.Arn;
            Console.WriteLine($"  role '{roleName}' created.");
        }

        await RequireTrustAsync(iam, roleName, trust);

        await iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = roleName, PolicyName = grant.Name, PolicyDocument = grant.Document,
        });
        await iam.PutRolePolicyAsync(new PutRolePolicyRequest
        {
            RoleName = roleName, PolicyName = $"{roleName}-deny-self-rewrite", PolicyDocument = denyPolicy,
        });
        Console.WriteLine("      grant + explicit self-rewrite Deny applied.");

        return arn;
    }

    /// <summary>
    /// The role's trust policy, read back until it says what was written (DecoupledCd.md §14.3). IAM returns the document
    /// URL-encoded, and is eventually consistent — a read straight after the write can still see the old document, or for a
    /// new role no role at all — so the read is retried for about ten seconds before a difference is a failure.
    /// </summary>
    private static async Task RequireTrustAsync(IAmazonIdentityManagementService iam, string roleName, string trust)
    {
        const int attempts = 6;
        string? stored = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                var document = (await iam.GetRoleAsync(new GetRoleRequest { RoleName = roleName })).Role.AssumeRolePolicyDocument;
                stored = document is null ? null : Uri.UnescapeDataString(document);
                if (CrossAccount.SamePolicy(trust, stored))
                    return;
            }
            catch (NoSuchEntityException) when (attempt < attempts)
            {
                // Created a moment ago, and not readable yet.
            }

            if (attempt < attempts)
                await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new InvalidOperationException(
            $"role '{roleName}''s trust policy does not read back as written.\n  written:   {trust}\n  read back: {stored ?? "nothing"}");
    }

    /// <summary>
    /// Whether <c>CreateFunction</c> refused because the function's role, created moments before, is not yet usable — which
    /// time fixes — rather than because the role is wrong. IAM is eventually consistent, and Lambda reports that window
    /// two ways:
    /// <list type="bullet">
    ///   <item>"The role defined for the function cannot be assumed by Lambda.";</item>
    ///   <item>"Lambda was unable to configure access to your environment variables because the KMS key is invalid for
    ///   CreateGrant. … KMS Exception: InvalidArnException KMS Message: ARN does not refer to a valid principal:
    ///   arn:aws:sts::…:assumed-role/{role}/{function}" — measured 2026-09-14, creating
    ///   <c>scu-dev-deployer-verify-bundle</c> seconds after its role (DecoupledCd.md P4 stage C). Only the first
    ///   was retried then, so the apply stopped half-way, with the functions after it not updated.</item>
    /// </list>
    /// A KMS refusal of any other kind — a key the role may not use — is not this, and still fails at once.
    /// </summary>
    public static bool RoleNotYetUsableByLambda(string message)
        => message.Contains("cannot be assumed", StringComparison.OrdinalIgnoreCase)
           || (message.Contains("KMS key is invalid for CreateGrant", StringComparison.OrdinalIgnoreCase)
               && message.Contains("does not refer to a valid principal", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Create the function, or bring an existing one to the plan: code first, then configuration,
    /// waiting for each update to settle — Lambda refuses a configuration change while a code update
    /// is still in progress.
    /// </summary>
    private static async Task EnsureFunctionAsync(
        IAmazonLambda lambda, DeployerFunction fn, string roleArn, PackageBytes package)
    {
        var environment = new Amazon.Lambda.Model.Environment
        {
            Variables = new Dictionary<string, string>(fn.Environment),
        };

        bool exists;
        try
        {
            await lambda.GetFunctionConfigurationAsync(new GetFunctionConfigurationRequest { FunctionName = fn.Name });
            exists = true;
        }
        catch (Amazon.Lambda.Model.ResourceNotFoundException)
        {
            exists = false;
        }

        if (!exists)
        {
            // A ROLE CREATED SECONDS AGO IS NOT YET USABLE BY LAMBDA — IAM is eventually consistent,
            // and CreateFunction checks the role immediately. Retried on exactly those refusals and no
            // other (RoleNotYetUsableByLambda), so a genuinely bad role still fails within the minute.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await lambda.CreateFunctionAsync(new CreateFunctionRequest
                    {
                        FunctionName = fn.Name,
                        Runtime = Runtime.Dotnet10,
                        Handler = fn.Handler,
                        Role = roleArn,
                        Code = new FunctionCode { ZipFile = new MemoryStream(package.Bytes) },
                        Timeout = fn.TimeoutSeconds,
                        MemorySize = fn.MemoryMb,
                        Architectures = new List<string> { Architecture.X86_64 },
                        Environment = environment,
                        PackageType = PackageType.Zip,
                        Description = "lz decoupled-CD deployer (DecoupledCd.md §5).",
                    });
                    break;
                }
                catch (InvalidParameterValueException ex)
                    when (attempt < 20 && RoleNotYetUsableByLambda(ex.Message))
                {
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }

            await WaitForSettledAsync(lambda, fn.Name);
            Console.WriteLine($"  function '{fn.Name}' created.");
            return;
        }

        await lambda.UpdateFunctionCodeAsync(new UpdateFunctionCodeRequest
        {
            FunctionName = fn.Name,
            ZipFile = new MemoryStream(package.Bytes),
            Architectures = new List<string> { Architecture.X86_64 },
        });
        await WaitForSettledAsync(lambda, fn.Name);

        await lambda.UpdateFunctionConfigurationAsync(new UpdateFunctionConfigurationRequest
        {
            FunctionName = fn.Name,
            Runtime = Runtime.Dotnet10,
            Handler = fn.Handler,
            Role = roleArn,
            Timeout = fn.TimeoutSeconds,
            MemorySize = fn.MemoryMb,
            Environment = environment,
        });
        await WaitForSettledAsync(lambda, fn.Name);
        Console.WriteLine($"  function '{fn.Name}' already exists — code and configuration updated.");
    }

    private static async Task WaitForSettledAsync(IAmazonLambda lambda, string name)
    {
        for (var i = 0; i < 45; i++)
        {
            var c = await lambda.GetFunctionConfigurationAsync(new GetFunctionConfigurationRequest { FunctionName = name });

            if (c.State == State.Failed || c.LastUpdateStatus == LastUpdateStatus.Failed)
                throw new InvalidOperationException(
                    $"function '{name}' failed to settle: {c.StateReason ?? c.LastUpdateStatusReason}");

            if (c.State == State.Active && c.LastUpdateStatus != LastUpdateStatus.InProgress)
                return;

            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        throw new InvalidOperationException($"function '{name}' did not settle within 90 seconds.");
    }

    private static async Task EnsureStateMachineAsync(
        IAmazonStepFunctions sfn, PipelineDeployer plan, string roleArn, string accountId, string region)
    {
        var arn = $"arn:aws:states:{region}:{accountId}:stateMachine:{plan.StateMachineName}";

        try
        {
            await sfn.DescribeStateMachineAsync(new DescribeStateMachineRequest { StateMachineArn = arn });
            // UPDATED rather than skipped: the definition is the deploy procedure, so a machine that
            // drifted from the plan is exactly what a re-run should correct.
            await sfn.UpdateStateMachineAsync(new UpdateStateMachineRequest
            {
                StateMachineArn = arn,
                Definition = plan.Definition,
                RoleArn = roleArn,
            });
            Console.WriteLine($"  state machine '{plan.StateMachineName}' already exists — definition updated.");
        }
        catch (StateMachineDoesNotExistException)
        {
            await sfn.CreateStateMachineAsync(new CreateStateMachineRequest
            {
                Name = plan.StateMachineName,
                Definition = plan.Definition,
                RoleArn = roleArn,
                // STANDARD, not Express: .waitForTaskToken — which the approval gate is — does not
                // exist on Express, and StartExecution's name-based idempotency is Standard-only.
                // That idempotency is what absorbs S3's at-least-once delivery (§5.1).
                Type = StateMachineType.STANDARD,
            });
            Console.WriteLine($"  state machine '{plan.StateMachineName}' created (Standard).");
        }
    }
}
