using System.Text.Encodings.Web;
using System.Text.Json;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>
/// One Lambda function the deployer needs, with everything required to create it.
/// </summary>
/// <param name="Handler">The .NET handler string, <c>Assembly::Type::Method</c>.</param>
/// <param name="RoleName">Each function has its own role, holding only what that function does.</param>
/// <param name="Environment">Read back by the handler through <see cref="DeployerEnvironment"/>.</param>
/// <param name="Package">Which zip the function's code comes from; see <see cref="DeployerPackages"/>.</param>
/// <param name="InvokedByStateMachine">
/// False for the signature hook, which ECS invokes — the state machine's role may not invoke it.
/// </param>
public sealed record DeployerFunction(
    string Name,
    string Handler,
    string RoleName,
    string Policy,
    IReadOnlyDictionary<string, string> Environment,
    int TimeoutSeconds,
    int MemoryMb,
    string Package,
    bool InvokedByStateMachine);

/// <summary>
/// The deployer that runs INSIDE a target account — the half of the design GitHub cannot reach.
/// See DecoupledCd.md §5.
/// </summary>
/// <param name="StateMachineName">One Standard state machine per account.</param>
/// <param name="RoleName">The role the state machine assumes to deploy.</param>
/// <param name="RolePolicy">What that role may do — an image roll and nothing else, for class 1.</param>
/// <param name="DenyPolicy">
/// The §5.5 explicit Deny. Separate from <paramref name="RolePolicy"/> on purpose: an explicit Deny
/// cannot be undone by a later Allow, so keeping it in its own document makes it visible in the
/// console rather than buried in the middle of a grant. Attached to EVERY deployer role, the
/// functions' included.
/// </param>
/// <param name="EvidenceStore">Versioned bucket the Record state writes to.</param>
/// <param name="EvidenceStorePolicy">What that bucket's policy must hold, merged by Sid as soon as it exists: the
/// write-once Deny (<see cref="WriteOnceStore"/>). Evidence is written once and never replaced.</param>
/// <param name="Definition">The Amazon States Language document.</param>
/// <param name="ApprovalRequired">Whether the machine contains a human gate at all.</param>
/// <param name="Functions">The Lambda functions: the five the definition invokes, the signature hook, and — with the
/// trigger — the start function.</param>
/// <param name="HookInvokerRoleName">
/// The role ECS assumes to invoke the signature hook — the <c>roleArn</c> of a lifecycle hook.
/// </param>
/// <param name="HookInvokerPolicy">That role's grant: invoke the hook function, nothing else.</param>
/// <param name="ImageRepositories">The repositories images replicate into, hardened here before
/// anything may replicate into them — same names as the build registry's, as replication requires.</param>
/// <param name="ReplicationPermission">This registry's policy statement letting the build account
/// replicate into exactly those repositories, merged by Sid at apply.</param>
/// <param name="StateMachineTrustPolicy">Who may assume the machine's role: Step Functions, for a state machine in this
/// account and region (<see cref="DeployerPlanner.ServiceTrustPolicy"/>).</param>
/// <param name="FunctionTrustPolicy">Who may assume a function's role: Lambda.</param>
/// <param name="HookInvokerTrustPolicy">Who may assume the hook invoker's role: ECS.</param>
/// <param name="LogRetentionDays">How long each function's log group keeps its logs — <c>Hygiene.LambdaLogRetentionDays</c>;
/// null leaves the groups as Lambda creates them, never expiring.</param>
/// <param name="Trigger">The trigger (stage D), when <c>Pipeline.DeployOnBuildRecord</c> is on; null otherwise, and
/// then the apply removes the start rule if an earlier run created it.</param>
/// <param name="Alerts">The alerts (stage D3), when <c>Pipeline.Alerts</c> is on; null otherwise, and then the apply removes
/// the failed-deploy rule, the sweep's schedule and its errors alarm if an earlier run created them.</param>
public sealed record PipelineDeployer(
    string StateMachineName,
    string RoleName,
    string RolePolicy,
    string DenyPolicy,
    string EvidenceStore,
    IReadOnlyList<System.Text.Json.Nodes.JsonObject> EvidenceStorePolicy,
    string Definition,
    bool ApprovalRequired,
    IReadOnlyList<DeployerFunction> Functions,
    string HookInvokerRoleName,
    string HookInvokerPolicy,
    IReadOnlyList<string> ImageRepositories,
    System.Text.Json.Nodes.JsonObject? ReplicationPermission,
    string StateMachineTrustPolicy,
    string FunctionTrustPolicy,
    string HookInvokerTrustPolicy,
    int? LogRetentionDays,
    DeployerTriggerPlan? Trigger = null,
    DeployerAlertsPlan? Alerts = null,
    IReadOnlyList<ClientTarget>? ClientTargets = null);

/// <summary>
/// A web app a client repository's bundles deploy as, from configuration alone (P4 stage C): the build account's grants
/// need it, and it needs no read of any account.
/// </summary>
/// <param name="BasePath">From <c>Behaviors.WebApps[].Path</c>: <c>seller/</c>.</param>
public sealed record ClientApp(string Repo, string App, string Bucket, string BasePath);

/// <summary>What the client targets need from the account, which the planner cannot read: the distributions that serve the tenants.</summary>
public sealed record DeployerClientInputs(IReadOnlyList<string> DistributionIds);

/// <summary>
/// What the trigger's plan needs from the account and the workspace, which the planner cannot read: the ECS
/// cluster that exists, and the tenants whose services a build rolls.
/// </summary>
public sealed record DeployerTriggerInputs(string Cluster, IReadOnlyList<string> TenantKeys);

/// <summary>
/// The trigger in the target account (DecoupledCd.md §4.3, P2 stage D): the bus the build account forwards record
/// events to, the rule that invokes the start function, and the queue where what fails ends up.
/// </summary>
/// <param name="BusPolicy">The bus's whole policy: only the build account's forwarding role may put events on it.</param>
/// <param name="EventPattern">The rule's pattern, <see cref="DeployerTrigger.EventPattern"/>.</param>
/// <param name="StartFunctionName">Also in <see cref="PipelineDeployer.Functions"/>, which creates it.</param>
/// <param name="DeadLetterQueuePolicy">Lets EventBridge dead-letter what the rule could not deliver. The function's own
/// failures reach the same queue through its asynchronous-invocation destination, under its role.</param>
/// <param name="MaximumRetryAttempts">Lambda's retries of a failed asynchronous invocation before the queue.</param>
/// <param name="MaximumEventAgeSeconds">How long Lambda keeps retrying an event before the queue.</param>
/// <param name="AlarmActions">What the queue's alarm notifies: the alerts topic under <c>Pipeline.Alerts</c>, and nothing
/// otherwise — an alarm update replaces its actions, so an empty list is how turning alerts off reaches it.</param>
public sealed record DeployerTriggerPlan(
    string BusName,
    string BusArn,
    string BusPolicy,
    string RuleName,
    string RuleArn,
    string EventPattern,
    string StartFunctionName,
    string StartFunctionArn,
    string DeadLetterQueueName,
    string DeadLetterQueueArn,
    string DeadLetterQueuePolicy,
    string AlarmName,
    int MaximumRetryAttempts,
    int MaximumEventAgeSeconds,
    IReadOnlyList<TriggerRoute> Routes,
    IReadOnlyList<string> AlarmActions);

/// <summary>
/// The alerts in the target account (P2 stage D3): the topic a person subscribes to, the rule that sends it the deployer's
/// failed executions, and the sweep that finds images no build record names, with the alarm that says the sweep failed.
/// </summary>
/// <param name="TopicPolicy">The topic's whole policy: EventBridge for the failed-deploy rule, and CloudWatch for this
/// pipeline's alarms by ARN — the build account's forwarding alarm among them when the trigger is on. The sweep publishes
/// under its own role.</param>
/// <param name="FailedDeployPattern"><see cref="DeployerPlanner.FailedDeployPattern"/>.</param>
/// <param name="CorroborateFunctionName">Also in <see cref="PipelineDeployer.Functions"/>, which creates it.</param>
/// <param name="ScheduleExpression">How often the sweep runs.</param>
/// <param name="CorroborateErrorsAlarmName">ALARM when a sweep fails, so a sweep that stopped working is itself an alert.</param>
public sealed record DeployerAlertsPlan(
    string TopicName,
    string TopicArn,
    string TopicPolicy,
    string FailedDeployRuleName,
    string FailedDeployRuleArn,
    string FailedDeployPattern,
    string CorroborateFunctionName,
    string CorroborateFunctionArn,
    string ScheduleRuleName,
    string ScheduleRuleArn,
    string ScheduleExpression,
    string CorroborateErrorsAlarmName);

/// <summary>
/// The signature hook as an ECS service attaches it: the function ECS invokes at <c>PRE_SCALE_UP</c>, and the
/// role ECS assumes to invoke it. Both are created by <c>lz bootstrapdeployer</c>.
/// </summary>
public sealed record SignatureHookAttachment(string FunctionArn, string InvokerRoleArn);

/// <summary>The deployment packages the functions are built from, shipped inside Lz.Aws.</summary>
public static class DeployerPackages
{
    /// <summary>Verify, Prepare, VerifyRollout, Record, RecordFailure and Start: .NET code and the AWS SDK.</summary>
    public const string Deployer = "deployer";

    /// <summary>The signature hook: the same code, plus the Notation verifier when it has been packaged.</summary>
    public const string SignatureHook = "signature-hook";

    /// <summary>The zip's file name, under <c>Lambda/</c> next to Lz.Aws.dll.</summary>
    public static string ZipFor(string package) => $"{package}.zip";
}

/// <summary>
/// The handler strings. The packaging test opens the built zips and resolves each of these, so a
/// renamed class fails a test rather than a function's first invocation.
/// </summary>
public static class DeployerHandlers
{
    public const string Assembly = "Lz.Aws.Deployer";

    public static readonly string Verify = For("VerifyFunction");
    public static readonly string Prepare = For("PrepareFunction");
    public static readonly string VerifyRollout = For("VerifyRolloutFunction");
    public static readonly string Record = For("RecordFunction");
    public static readonly string RecordFailure = For("RecordFailureFunction");
    public static readonly string SignatureHook = For("SignatureHookFunction");
    public static readonly string Start = For("StartFunction");
    public static readonly string Corroborate = For("CorroborateFunction");
    public static readonly string DeployBundle = For("DeployBundleFunction");
    public static readonly string VerifyBundle = For("VerifyBundleFunction");

    /// <summary>Every handler, for the packaging test.</summary>
    public static IReadOnlyList<string> All => new[]
        { Verify, Prepare, VerifyRollout, Record, RecordFailure, SignatureHook, Start, Corroborate, DeployBundle, VerifyBundle };

    private static string For(string type) => $"{Assembly}::{Assembly}.{type}::HandleAsync";
}

/// <summary>
/// Decides what the in-account deployer looks like, as a pure function of config.
///
/// <para>STAGE A OF P2 DEFINED THIS; STAGE C MADE IT RUNNABLE. Writing the functions exposed five
/// gaps in the stage-A definition, none of which a test of the document alone could see, because each
/// was a missing PRODUCER rather than a malformed state:</para>
/// <list type="number">
///   <item>nothing produced <c>$.deploy.taskDefinitionArn</c>, which Deploy reads — §4.6 says a class-1
///   roll registers a revision pinning the digest, and no state did. Prepare now does;</item>
///   <item>nothing produced <c>$.evidence</c>, which Record reads. VerifyRollout now does;</item>
///   <item>the state machine's role could not invoke a single Lambda — it had no
///   <c>lambda:InvokeFunction</c>, so the first execution would have died at Verify and then again at
///   RecordFailure;</item>
///   <item>the approval gate took its SNS topic from the EXECUTION INPUT, so whoever started an
///   execution chose who was asked to approve it. It now comes from config;</item>
///   <item>and the approval message carried no task token, so an approver had nothing to answer
///   <c>SendTaskSuccess</c> with — the gate could only ever time out.</item>
/// </list>
///
/// <para>CLASS 1, AND CLASS 2 WHERE A CLIENT TARGET EXISTS (P4 stage C). The client branch is two functions after a
/// Choice on the verified class, and an environment with no client target is planned exactly as before it existed.
/// Site and asset bundles, and the infrastructure classes with their Plan state and tooling container, are not modelled
/// here rather than being modelled badly.</para>
/// </summary>
public static class DeployerPlanner
{
    private static readonly JsonSerializerOptions Asl = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>How long Verify waits for a scan: 30 s × 20 attempts, ten minutes.</summary>
    public const int ScanRetryIntervalSeconds = 30;
    public const int ScanRetryMaxAttempts = 20;

    /// <summary>
    /// How long Verify waits for a replica to arrive: 15 s × 20 attempts, five minutes. Replication was measured at
    /// six to seven seconds after the push (2026-09-12), so the bound is about fifty times what it needs.
    /// </summary>
    public const int ReplicationRetryIntervalSeconds = 15;
    public const int ReplicationRetryMaxAttempts = 20;

    /// <summary>How long VerifyRollout waits for a roll: 30 s × 40 attempts, twenty minutes.</summary>
    public const int RolloutRetryIntervalSeconds = 30;
    public const int RolloutRetryMaxAttempts = 40;

    /// <summary>
    /// How long DeployBundle waits for another execution's lease on the app: 30 s × 40 attempts, twenty minutes — longer
    /// than <see cref="BundleMarker.Lease"/>, so a lease left by a deploy that died is outlasted rather than failed on.
    /// </summary>
    public const int BundleLeaseRetryIntervalSeconds = 30;
    public const int BundleLeaseRetryMaxAttempts = 40;

    /// <summary>How long VerifyBundle waits for its invalidations: 15 s × 40 attempts, ten minutes.</summary>
    public const int InvalidationRetryIntervalSeconds = 15;
    public const int InvalidationRetryMaxAttempts = 40;

    /// <summary>
    /// S3's and CloudFront's faults inside the bundle functions, after the SDK's own retries, by the exception's class name,
    /// which is what the .NET runtime reports and Step Functions matches — exactly, so a subclass is named as itself.
    /// Retrying a whole bundle step is safe: its writes are idempotent and its lease is its own.
    /// </summary>
    public static readonly IReadOnlyList<string> BundleTransientErrors = new[]
    {
        nameof(Amazon.S3.AmazonS3Exception),
        nameof(Amazon.CloudFront.AmazonCloudFrontException),
        nameof(Amazon.CloudFront.Model.TooManyInvalidationsInProgressException),
    };

    /// <summary>
    /// The errors the Deploy state retries before its Catch: ECS's server-side faults and throttling.
    ///
    /// <para>NAMED FROM THE DOCUMENTATION, NOT YET SEEN IN AN EXECUTION. Step Functions names an AWS SDK
    /// integration's error <c>{Service}.{Error}</c> and always with the <c>Exception</c> suffix, even where the
    /// service's own reference omits it; ECS's prefix is <c>Ecs</c>. <c>ServerException</c> is UpdateService's
    /// documented 500, and <c>ThrottlingException</c>, <c>InternalFailure</c> and <c>ServiceUnavailable</c> are
    /// ECS's common errors. Nothing a caller did wrong is here: a missing service or an invalid revision is
    /// not going to change by asking again.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> DeployTransientErrors = new[]
    {
        "Ecs.ServerException", "Ecs.ThrottlingException", "Ecs.InternalFailureException", "Ecs.ServiceUnavailableException",
    };

    /// <summary>
    /// An execution name for one deploy request. The definition lives in <see cref="DeployExecution.Name"/>,
    /// which the start function compiles too, so the name it gives an execution is the one pinned here.
    /// </summary>
    public static string ExecutionName(string requestId, int attempt) => DeployExecution.Name(requestId, attempt);

    /// <summary>
    /// What a definition and the role that runs it cannot actually do — read from the two documents,
    /// without running anything.
    ///
    /// <para>WHY THIS EXISTS: the stage-A definition passed every assertion about its SHAPE (it started
    /// at Verify, every state caught, Record wrote conditionally) and still could not have completed a
    /// single execution, because the gaps were in DATA FLOW and PERMISSION, which no shape assertion
    /// looks at. Measured on the definition and role policy deployed to scu-dev, this finds all of them;
    /// the test holds that fixture, so the checker is proven against the real artifact rather than
    /// against examples written to satisfy it.</para>
    ///
    /// <para>The rules, each a way an execution dies after a successful deploy of the machine:</para>
    /// <list type="number">
    ///   <item>every Task has an explicit <c>ResultPath</c> — without one its result REPLACES the state,
    ///   and every later read of the input finds nothing;</item>
    ///   <item>every path a state reads starts at a field the input carries or some state's
    ///   <c>ResultPath</c> writes. A first-segment check, deliberately simple: it proves a producer
    ///   exists, not that it runs first;</item>
    ///   <item>the role may invoke every Lambda the definition invokes;</item>
    ///   <item>an approval topic is never read from the execution state;</item>
    ///   <item>a <c>.waitForTaskToken</c> task hands its task token to whoever must answer it;</item>
    ///   <item>every transition names a state of the machine (P4 stage C);</item>
    ///   <item>a Choice branches on a field some state writes — otherwise every execution takes its default.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> ContractGaps(string definition, string rolePolicy)
    {
        var gaps = new List<string>();
        using var def = JsonDocument.Parse(definition);
        using var policy = JsonDocument.Parse(rolePolicy);
        var states = def.RootElement.GetProperty("States").EnumerateObject().ToList();

        var produced = new HashSet<string>(DeployerInput.InputFields.Concat(DeployerInput.OptionalInputFields), StringComparer.Ordinal);
        foreach (var s in states)
        {
            if (s.Value.TryGetProperty("ResultPath", out var rp) && rp.GetString() is { } path)
                produced.Add(FirstSegment(path));
            if (s.Value.TryGetProperty("Catch", out var catches))
                foreach (var c in catches.EnumerateArray())
                    if (c.TryGetProperty("ResultPath", out var crp) && crp.GetString() is { } cpath)
                        produced.Add(FirstSegment(cpath));
        }

        var invokable = policy.RootElement.GetProperty("Statement").EnumerateArray()
            .Where(st => st.GetProperty("Effect").GetString() == "Allow" && Strings(st.GetProperty("Action"))
                .Any(a => a is "lambda:InvokeFunction" or "lambda:*" or "*"))
            .SelectMany(st => Strings(st.GetProperty("Resource")))
            .ToList();

        var names = states.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var s in states)
        {
            var type = s.Value.GetProperty("Type").GetString();

            // 6. Every transition names a state that exists (P4 stage C). Step Functions refuses such a definition at
            //    create, which is later than a test.
            foreach (var target in Transitions(s.Value).Where(t => !names.Contains(t)))
                gaps.Add($"{s.Name} goes to {target}, which is not a state of this machine.");

            // 7. A Choice branches on something a state wrote. One reading a field nothing produces takes its default on
            //    every execution, which here is a failure — a branch no deploy could reach.
            if (type == "Choice" && s.Value.TryGetProperty("Choices", out var choices))
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    if (choice.TryGetProperty("Variable", out var variable) && variable.GetString() is { } path
                        && path.StartsWith("$.", StringComparison.Ordinal) && !produced.Contains(FirstSegment(path)))
                        gaps.Add($"{s.Name} branches on {path}, which no state writes and the input does not carry, so it always takes its default.");
                }
            }

            if (type != "Task") continue;

            if (!s.Value.TryGetProperty("ResultPath", out _))
                gaps.Add($"{s.Name} has no ResultPath, so its result replaces the execution state and every later read of the input finds nothing.");

            var reads = new SortedSet<string>(StringComparer.Ordinal);
            CollectReads(s.Value, reads);
            foreach (var segment in reads.Where(r => !produced.Contains(r)))
                gaps.Add($"{s.Name} reads $.{segment}, which no state writes and the input does not carry.");

            var resource = s.Value.GetProperty("Resource").GetString() ?? "";
            if (resource.StartsWith("arn:aws:lambda:", StringComparison.Ordinal)
                && !invokable.Any(pattern => IamMatches(pattern, resource)))
                gaps.Add($"{s.Name} invokes {resource}, and the role may not invoke it.");

            var parameters = s.Value.TryGetProperty("Parameters", out var p) ? p : default;
            if (resource.Contains(":sns:publish", StringComparison.Ordinal)
                && parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("TopicArn.$", out _))
                gaps.Add($"{s.Name} takes its SNS topic from the execution state, so whoever starts an execution chooses who is asked.");

            if (resource.EndsWith(".waitForTaskToken", StringComparison.Ordinal)
                && (parameters.ValueKind != JsonValueKind.Object || !parameters.GetRawText().Contains("$$.Task.Token", StringComparison.Ordinal)))
                gaps.Add($"{s.Name} waits for a task token it never sends, so nothing can answer it and it can only time out.");
        }

        return gaps;

        static IEnumerable<string> Transitions(JsonElement state)
        {
            if (state.TryGetProperty("Next", out var next) && next.GetString() is { } n) yield return n;
            if (state.TryGetProperty("Default", out var fallback) && fallback.GetString() is { } d) yield return d;
            foreach (var list in new[] { "Choices", "Catch" })
            {
                if (!state.TryGetProperty(list, out var items)) continue;
                foreach (var item in items.EnumerateArray())
                    if (item.TryGetProperty("Next", out var itemNext) && itemNext.GetString() is { } target) yield return target;
            }
        }

        static string FirstSegment(string path) => path.StartsWith("$.", StringComparison.Ordinal) ? path[2..].Split('.', '[')[0] : path;

        static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Select(x => x.GetString() ?? "")
            : new[] { e.GetString() ?? "" };

        static void CollectReads(JsonElement e, SortedSet<string> reads)
        {
            if (e.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in e.EnumerateObject())
                {
                    // Catch blocks write; they do not read state. A ResultSelector's paths are into the
                    // TASK'S RESULT, not the state — `$.Service.TaskDefinition` there is UpdateService's
                    // response, which no state was ever meant to produce.
                    if (prop.Name is "Catch" or "ResultSelector") continue;

                    if (prop.Name.EndsWith(".$", StringComparison.Ordinal) && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        // `$.a.b`, including inside an intrinsic like States.Format(…, $.a.b) — but not the
                        // context object `$$.`, and not the bare `$` that passes the whole state.
                        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                                     prop.Value.GetString()!, @"(?<!\$)\$\.([A-Za-z0-9_]+)"))
                            reads.Add(m.Groups[1].Value);
                    }
                    else
                    {
                        CollectReads(prop.Value, reads);
                    }
                }
            }
            else if (e.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in e.EnumerateArray()) CollectReads(item, reads);
            }
        }

        static bool IamMatches(string pattern, string arn) =>
            System.Text.RegularExpressions.Regex.IsMatch(arn,
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$");
    }

    /// <summary>Build the deployer plan for one environment.</summary>
    /// <param name="triggerInputs">The cluster and tenants the trigger's routes name. Required when
    /// <c>Pipeline.DeployOnBuildRecord</c> is on, and ignored otherwise.</param>
    /// <param name="clientInputs">The distributions a client deploy invalidates. Required when a client repository names
    /// the web app it deploys as (<see cref="ClientApps"/>), and ignored otherwise.</param>
    public static PipelineDeployer Plan(
        SystemConfig config, string? accountId = null, DeployerTriggerInputs? triggerInputs = null, DeployerClientInputs? clientInputs = null)
    {
        var p = config.Pipeline
            ?? throw new InvalidOperationException("no Pipeline block; nothing to plan.");

        var sk = config.SystemKey;
        var env = config.Environment;
        var region = config.Region;
        var acct = accountId ?? "<target-account-id>";
        var approvalRequired = p.Approval?.Required ?? false;

        // THE BUILD ACCOUNT IS NAMED, not assumed: the hook trusts signing profiles that live there,
        // and the Verify function reads records from the store there. Without it neither can be
        // configured, and a placeholder would configure them to trust nothing real.
        var artifactAccount = p.ArtifactAccountId
            ?? throw new InvalidOperationException(
                "Pipeline.ArtifactAccountId is not set. The deployer's Verify function reads build " +
                "records from the build account, and its signature hook trusts the signing profiles " +
                "there; neither can be configured without naming that account.");

        // The build side's own plan is the one source of every name the two sides share.
        var build = PipelineBootstrapPlanner.Plan(config, artifactAccount);
        var imageRoles = build.Roles.Where(r => r.Class == "image").ToList();
        var imageRepositories = imageRoles.SelectMany(r => r.EcrRepositories).Distinct().ToList();
        var trustedProfiles = imageRoles
            .Where(r => r.SigningProfile != null)
            .Select(r => PipelineBootstrapPlanner.SigningProfileArn(region, artifactAccount, r.SigningProfile!))
            .ToList();
        var buildRecordStore = PipelineBootstrapPlanner.BuildRecordStoreFor(sk, config.SystemSuffix);

        if (imageRepositories.Count == 0)
            throw new InvalidOperationException(
                "the class-1 deployer rolls service images, and Pipeline.Repositories names no " +
                "repository of class 'image' — there is nothing it could deploy, and its policies " +
                "would have no repository to scope to.");

        // THIS account's registry — where replication delivers images, and what tasks here pull.
        var registry = $"{acct}.dkr.ecr.{region}.amazonaws.com";
        var scopes = imageRepositories.Select(r => $"{registry}/{r}").ToList();
        SignatureHook.ValidateScopes(scopes);

        // The evidence bucket lives in the TARGET account, unlike the three build-account stores:
        // it records what happened HERE, and §4.7 wants it replicated rather than shared.
        var evidence = $"{sk}-{env}-deploy-evidence-{config.SystemSuffix}";

        var verify = $"{sk}-{env}-deployer-verify";
        if ($"{verify}-fn" != VerifyRoleName(config))
            throw new InvalidOperationException("the Verify role name diverged from VerifyRoleName, which the build account's grant names.");
        var prepare = $"{sk}-{env}-deployer-prepare";
        var rollout = $"{sk}-{env}-deployer-verify-rollout";
        var record = $"{sk}-{env}-deployer-record";
        var failure = $"{sk}-{env}-deployer-record-failure";
        var hook = SignatureHookFunctionName(config);

        string FnArn(string name) => $"arn:aws:lambda:{region}:{acct}:function:{name}";

        // CLIENT BUNDLES (P4 stage C), only where a client repository names the web app it deploys as. Without one the plan
        // is what it was before class 2 existed, byte for byte — the Verify function's environment and grants included.
        var clientTargets = ClientTargetsFor(config, clientInputs);
        var artifactStore = PipelineBootstrapPlanner.ArtifactStoreFor(sk, config.SystemSuffix);

        var verifyEnvironment = new Dictionary<string, string>
        {
            [DeployerEnvironment.Classes] = DeployerEnvironment.Join(p.Classes ?? new List<string>()),
            [DeployerEnvironment.ScanBlockOn] = DeployerEnvironment.Join(p.Scan?.BlockOn ?? new List<string>()),
            [DeployerEnvironment.BuildRecordStore] = buildRecordStore,
            [DeployerEnvironment.ImageRepositories] = DeployerEnvironment.Join(imageRepositories),
        };
        var verifyGrants = VerifyGrants(region, acct, sk, env, buildRecordStore, imageRepositories);
        if (clientTargets.Count > 0)
        {
            verifyEnvironment[DeployerEnvironment.ArtifactStore] = artifactStore;
            verifyEnvironment[DeployerEnvironment.ClientTargets] = ClientTargets.Encode(clientTargets);
            verifyGrants = verifyGrants.Concat(VerifyClientGrants(buildRecordStore, artifactStore)).ToArray();
        }

        var functions = new List<DeployerFunction>
        {
            new(verify, DeployerHandlers.Verify, $"{verify}-fn",
                Combine(Logs(region, acct, verify), verifyGrants),
                verifyEnvironment,
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),

            new(prepare, DeployerHandlers.Prepare, $"{prepare}-fn",
                Combine(Logs(region, acct, prepare), PrepareGrants(region, acct, sk, env)),
                new Dictionary<string, string> { [DeployerEnvironment.Registry] = registry },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),

            new(rollout, DeployerHandlers.VerifyRollout, $"{rollout}-fn",
                Combine(Logs(region, acct, rollout), RolloutGrants(region, acct, sk, env)),
                new Dictionary<string, string>(),
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),

            new(record, DeployerHandlers.Record, $"{record}-fn",
                Combine(Logs(region, acct, record), RecordGrants(evidence)),
                new Dictionary<string, string> { [DeployerEnvironment.EvidenceStore] = evidence },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),

            new(failure, DeployerHandlers.RecordFailure, $"{failure}-fn",
                Combine(Logs(region, acct, failure), FailureGrants(evidence)),
                new Dictionary<string, string> { [DeployerEnvironment.EvidenceStore] = evidence },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),

            new(hook, DeployerHandlers.SignatureHook, $"{hook}-fn",
                Combine(Logs(region, acct, hook), HookGrants(region, acct, imageRepositories)),
                new Dictionary<string, string>
                {
                    [DeployerEnvironment.TrustedProfiles] = DeployerEnvironment.Join(trustedProfiles),
                    [DeployerEnvironment.RegistryScopes] = DeployerEnvironment.Join(scopes),
                },
                // Notation runs once per image and checks revocation over the network; the margin is
                // for that, not for the .NET code.
                TimeoutSeconds: 120, MemoryMb: 1024, DeployerPackages.SignatureHook, InvokedByStateMachine: false),
        };

        (string DeployBundle, string VerifyBundle)? bundleFunctions = null;
        if (clientTargets.Count > 0)
        {
            var deployBundle = DeployBundleFunctionName(config);
            var verifyBundle = VerifyBundleFunctionName(config);
            if ($"{deployBundle}-fn" != DeployBundleRoleName(config))
                throw new InvalidOperationException("the DeployBundle role name diverged from DeployBundleRoleName, which the build account's grant names.");

            var durability = Lz.Aws.Storage.BucketDurabilityPolicy.ForContentBucket(config.Durability, config.Hygiene);
            var deployEnvironment = new Dictionary<string, string>
            {
                [DeployerEnvironment.TargetAccount] = acct,
                [DeployerEnvironment.BucketVersioning] = durability.Versioning ? "true" : "false",
            };
            if (durability is { Versioning: true, NoncurrentExpirationDays: int days })
                deployEnvironment[DeployerEnvironment.NoncurrentExpirationDays] = days.ToString(System.Globalization.CultureInfo.InvariantCulture);

            functions.Add(new(deployBundle, DeployerHandlers.DeployBundle, DeployBundleRoleName(config),
                Combine(Logs(region, acct, deployBundle), DeployBundleGrants(acct, artifactStore, clientTargets, durability)),
                deployEnvironment,
                // Ten minutes: a full rewrite of today's 573 files is a few hundred requests; the lease outlasts this.
                TimeoutSeconds: 600, MemoryMb: 1024, DeployerPackages.Deployer, InvokedByStateMachine: true));

            functions.Add(new(verifyBundle, DeployerHandlers.VerifyBundle, $"{verifyBundle}-fn",
                Combine(Logs(region, acct, verifyBundle), VerifyBundleGrants(acct, clientTargets)),
                new Dictionary<string, string>(),
                TimeoutSeconds: 120, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true));

            if (BundleMarker.Lease <= TimeSpan.FromSeconds(600))
                throw new InvalidOperationException("the bundle lease must outlast a DeployBundle invocation, or a live deploy's lease could be taken.");

            bundleFunctions = (FnArn(deployBundle), FnArn(verifyBundle));
        }

        var machine = $"{sk}-{env}-deployer";
        var machineArn = $"arn:aws:states:{region}:{acct}:stateMachine:{machine}";

        // What an alarm of this pipeline notifies: the alerts topic when there is one, and nothing otherwise.
        var alarmActions = p.Alerts ? new[] { AlertsTopicArn(region, acct, config) } : Array.Empty<string>();

        // THE TRIGGER, only when this environment deploys on every build record. Its function joins the list above,
        // so it is created exactly as the others are: its own role, the same Deny.
        DeployerTriggerPlan? trigger = null;
        if (p.DeployOnBuildRecord)
        {
            var (triggerPlan, startFunction) = TriggerFor(
                config, p, region, acct, artifactAccount, buildRecordStore, machineArn, triggerInputs, alarmActions);
            trigger = triggerPlan;
            functions.Add(startFunction);
        }

        // THE ALERTS (P2 stage D3), and the sweep's function with the others.
        DeployerAlertsPlan? alerts = null;
        if (p.Alerts)
        {
            var (alertsPlan, corroborate) = AlertsFor(
                config, p, region, acct, artifactAccount, buildRecordStore, evidence, imageRepositories, machineArn, trigger);
            alerts = alertsPlan;
            functions.Add(corroborate);
        }

        var invoked = functions.Where(f => f.InvokedByStateMachine).Select(f => FnArn(f.Name)).ToList();

        return new PipelineDeployer(
            StateMachineName: machine,
            RoleName: $"{sk}-{env}-deployer",
            RolePolicy: RolePolicyFor(region, acct, sk, env, invoked,
                                      approvalRequired ? p.Approval?.NotifyTopicArn : null),
            DenyPolicy: DenyPolicyFor(),
            EvidenceStore: evidence,
            EvidenceStorePolicy: new[] { WriteOnceStore.Deny(evidence) },
            Definition: DefinitionFor(approvalRequired, FnArn(verify), FnArn(prepare),
                                      FnArn(rollout), FnArn(record), FnArn(failure),
                                      p.Approval?.HeartbeatSeconds ?? 86400, p.Approval?.NotifyTopicArn, bundleFunctions),
            ApprovalRequired: approvalRequired,
            Functions: functions,
            HookInvokerRoleName: SignatureHookInvokerRoleName(config),
            HookInvokerPolicy: Policy(new
            {
                Sid = "InvokeTheSignatureHookOnly",
                Effect = "Allow",
                Action = new[] { "lambda:InvokeFunction" },
                Resource = FnArn(hook),
            }),
            ImageRepositories: imageRepositories,
            // Only with a real account: a policy statement naming a placeholder account is not a plan
            // anyone could apply, and the applier always resolves the account first.
            ReplicationPermission: accountId is null
                ? null
                : CrossAccount.ReplicationPermission(artifactAccount, region, accountId, imageRepositories),
            // SOURCE CONDITIONS WHERE AWS DOCUMENTS THEM (DecoupledCd.md §14.3): Step Functions shows a trust policy with
            // aws:SourceArn and aws:SourceAccount for a state machine's role. Lambda's execution-role and ECS's hook-role
            // documentation show none, and a condition either service does not send would stop every function or every
            // deployment — so those two keep the documented form.
            StateMachineTrustPolicy: ServiceTrustPolicy("states.amazonaws.com", acct, $"arn:aws:states:{region}:{acct}:stateMachine:*"),
            FunctionTrustPolicy: ServiceTrustPolicy("lambda.amazonaws.com"),
            HookInvokerTrustPolicy: ServiceTrustPolicy("ecs.amazonaws.com"),
            LogRetentionDays: config.Hygiene?.LambdaLogRetentionDays,
            Trigger: trigger,
            Alerts: alerts,
            ClientTargets: clientTargets);
    }

    /// <summary>
    /// The web apps this environment's client bundles deploy as (P4 stage C), from configuration alone: every
    /// <c>client</c> repository whose <c>Artifacts</c> names one, where <c>Pipeline.Classes</c> accepts the class.
    ///
    /// <para><b>ONE APP PER REPOSITORY.</b> A record names one zip, which is one app's publish output; a repository naming
    /// two apps could not say which bucket its bundle belongs in. <b>A CONFIGURED APP ONLY:</b> the name must be a
    /// <c>Behaviors.WebApps</c> entry, whose path the bundle must be built for. <b>SYSTEM-SCOPED BUCKETS ONLY:</b> a
    /// central-auth topology keeps a web app per tenant, which no single target names.</para>
    /// </summary>
    public static IReadOnlyList<ClientApp> ClientApps(SystemConfig config)
    {
        if (config.Pipeline is not { Enabled: true } p || p.Classes?.Contains("client") != true)
            return Array.Empty<ClientApp>();

        var named = (p.Repositories ?? new List<PipelineRepositoryConfig>())
            .Where(r => string.Equals(r.Class, "client", StringComparison.Ordinal) && r.Artifacts is { Count: > 0 })
            .ToList();
        if (named.Count == 0)
            return Array.Empty<ClientApp>();

        if (Lz.Aws.Topologies.AwsTopologies.Get(config.Topology).UsesCentralAuth)
            throw new InvalidOperationException(
                $"Pipeline.Repositories names web apps for client bundles, but topology '{config.Topology}' keeps a web app per " +
                "tenant, and a client target names one system-scoped bucket. Remove Artifacts from the client entries.");

        var webApps = config.Behaviors?.WebApps ?? new List<WebAppBehavior>();
        var apps = new List<ClientApp>();
        foreach (var r in named)
        {
            if (r.Artifacts!.Count != 1)
                throw new InvalidOperationException(
                    $"Pipeline.Repositories entry '{r.Repo}' names {r.Artifacts.Count} web apps. A client bundle is one app's " +
                    "publish output, so it deploys as exactly one.");

            var app = r.Artifacts[0];
            var behavior = webApps.FirstOrDefault(w => string.Equals(w.AppName, app, StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"Pipeline.Repositories entry '{r.Repo}' deploys as web app '{app}', which Behaviors.WebApps does not name, so " +
                    "the path its bundle is served under is unknown.");

            apps.Add(new ClientApp(r.Repo!, app, Lz.Aws.Webapp.WebappSyncRules.SystemBucketName(config.SystemKey, app, config.SystemSuffix),
                Lz.Aws.Webapp.WebappSyncRules.BasePathFromBehaviorPath(behavior.Path)));
        }

        var shared = apps.GroupBy(a => a.Bucket, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (shared != null)
            throw new InvalidOperationException(
                $"{string.Join(" and ", shared.Select(a => a.Repo))} both deploy as web app '{shared.First().App}'. Each would " +
                "delete the other's files, since a deploy mirrors the app's whole prefix.");

        return apps;
    }

    private static IReadOnlyList<ClientTarget> ClientTargetsFor(SystemConfig config, DeployerClientInputs? inputs)
    {
        var apps = ClientApps(config);
        if (apps.Count == 0)
            return Array.Empty<ClientTarget>();

        var distributions = inputs?.DistributionIds is { Count: > 0 } ids
            ? ids.Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList()
            : throw new InvalidOperationException(
                "Pipeline.Repositories names web apps that client bundles deploy as, but the deployer was planned without the " +
                "CloudFront distributions that serve them, so a deploy could clear no cached copy. `lz bootstrapdeployer` finds " +
                "them before it plans.");

        return apps.Select(a => new ClientTarget(a.Repo, a.App, a.Bucket, a.BasePath, distributions)).ToList();
    }

    /// <summary>The function that mirrors a client bundle into its app's bucket.</summary>
    public static string DeployBundleFunctionName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-deployer-deploy-bundle";

    /// <summary>
    /// DeployBundle's role. ONE DEFINITION FOR BOTH ACCOUNTS, like <see cref="VerifyRoleName"/>: this planner creates it,
    /// and the build account's artifact store lets it read client bundles.
    /// </summary>
    public static string DeployBundleRoleName(SystemConfig config) => $"{DeployBundleFunctionName(config)}-fn";

    /// <summary>The function that reads a deployed bundle back.</summary>
    public static string VerifyBundleFunctionName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-deployer-verify-bundle";

    /// <summary>
    /// A role's trust policy for one AWS service principal, with the confused-deputy conditions when they are given: the
    /// assuming service must act for a resource in <paramref name="sourceAccount"/> whose ARN matches
    /// <paramref name="sourceArnLike"/>.
    /// </summary>
    public static string ServiceTrustPolicy(string servicePrincipal, string? sourceAccount = null, string? sourceArnLike = null)
    {
        var statement = new Dictionary<string, object>
        {
            ["Effect"] = "Allow",
            ["Principal"] = new Dictionary<string, object> { ["Service"] = servicePrincipal },
            ["Action"] = "sts:AssumeRole",
        };

        if (sourceAccount != null || sourceArnLike != null)
        {
            var condition = new Dictionary<string, object>();
            if (sourceArnLike != null)
                condition["ArnLike"] = new Dictionary<string, object> { ["aws:SourceArn"] = sourceArnLike };
            if (sourceAccount != null)
                condition["StringEquals"] = new Dictionary<string, object> { ["aws:SourceAccount"] = sourceAccount };
            statement["Condition"] = condition;
        }

        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["Version"] = "2012-10-17",
            ["Statement"] = new[] { statement },
        }, Asl);
    }

    /// <summary>
    /// The ECS services this deployer may describe and roll: this system's services — <c>{sk}-*</c> — in this environment's
    /// cluster, under either name <c>lz updatecontainer</c> finds it by, <c>{sk}-{env}-cluster</c> or <c>{sk}-cluster</c>
    /// (DecoupledCd.md §14.3). The target still comes from the execution input; this bounds what an input can name.
    ///
    /// <para>The legacy name carries no environment, so there the account is what separates one environment's services
    /// from another's — as it does for every other grant here. Service ARNs in the long form, which names the cluster: the
    /// form ECS issues today, and the one the hook's events carry in scu-dev.</para>
    /// </summary>
    public static string[] ServiceArns(string region, string accountId, string sk, string env) => new[]
    {
        $"arn:aws:ecs:{region}:{accountId}:service/{sk}-{env}-cluster/{sk}-*",
        $"arn:aws:ecs:{region}:{accountId}:service/{sk}-cluster/{sk}-*",
    };

    /// <summary>
    /// The alerts' resources and the sweep's function (P2 stage D3).
    ///
    /// <para>ONE SOURCE PER IMAGE ARTIFACT: the repository its image replicates into here, and the prefix its repository's
    /// records are written under — the same pairing the trigger routes on, read from the same config.</para>
    /// </summary>
    private static (DeployerAlertsPlan Plan, DeployerFunction Function) AlertsFor(
        SystemConfig config, PipelineConfig p, string region, string acct, string artifactAccount, string buildRecordStore,
        string evidence, IReadOnlyList<string> imageRepositories, string stateMachineArn, DeployerTriggerPlan? trigger)
    {
        var sources = (p.Repositories ?? new List<PipelineRepositoryConfig>())
            .Where(r => string.Equals(r.Class, DeployerTrigger.RecordClass, StringComparison.Ordinal))
            .SelectMany(r => (r.Artifacts ?? new List<string>()).Select(artifact => new CorroborateSource(
                EcrRepositoryNaming.For(config, artifact), BuildRecordFormat.PrefixFor(DeployerTrigger.RecordClass, r.Repo!))))
            .ToList();

        var topicArn = AlertsTopicArn(region, acct, config);
        var failedRule = FailedDeployRuleName(config);
        var failedRuleArn = $"arn:aws:events:{region}:{acct}:rule/{failedRule}";
        var function = CorroborateFunctionName(config);
        var schedule = CorroborateScheduleRuleName(config);
        var errorsAlarm = CorroborateErrorsAlarmName(config);

        string AlarmArn(string account, string alarm) => $"arn:aws:cloudwatch:{region}:{account}:alarm:{alarm}";

        // THE ALARMS THAT MAY PUBLISH, BY ARN: this account's sweep and trigger queue, and the build account's forwarding
        // queue — the last two only where the trigger exists.
        var localAlarms = new List<string> { AlarmArn(acct, errorsAlarm) };
        if (trigger != null)
            localAlarms.Add(AlarmArn(acct, trigger.AlarmName));
        var buildAlarm = trigger != null ? AlarmArn(artifactAccount, ForwardDeadLetterAlarmName(config)) : null;

        var plan = new DeployerAlertsPlan(
            TopicName: AlertsTopicName(config),
            TopicArn: topicArn,
            TopicPolicy: CrossAccount.AlertsTopicPolicy(topicArn, failedRuleArn, acct, localAlarms, artifactAccount, buildAlarm),
            FailedDeployRuleName: failedRule,
            FailedDeployRuleArn: failedRuleArn,
            FailedDeployPattern: FailedDeployPattern(stateMachineArn),
            CorroborateFunctionName: function,
            CorroborateFunctionArn: $"arn:aws:lambda:{region}:{acct}:function:{function}",
            ScheduleRuleName: schedule,
            ScheduleRuleArn: $"arn:aws:events:{region}:{acct}:rule/{schedule}",
            // The sweep's period is its grace: an image is looked at by at least one run once it is old enough.
            ScheduleExpression: $"rate({(int)DeployerAlerts.Grace.TotalMinutes} minutes)",
            CorroborateErrorsAlarmName: errorsAlarm);

        var corroborate = new DeployerFunction(
            function, DeployerHandlers.Corroborate, CorroborateFunctionRoleName(config),
            Combine(Logs(region, acct, function), CorroborateGrants(region, acct, buildRecordStore, evidence, topicArn, imageRepositories)),
            new Dictionary<string, string>
            {
                [DeployerEnvironment.BuildRecordStore] = buildRecordStore,
                [DeployerEnvironment.EvidenceStore] = evidence,
                [DeployerEnvironment.AlertsTopic] = topicArn,
                [DeployerEnvironment.CorroborateSources] = DeployerAlerts.EncodeSources(sources),
            },
            TimeoutSeconds: 60, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: false);

        return (plan, corroborate);
    }

    /// <summary>
    /// The event pattern for this deployer's executions that ended without deploying: FAILED — every refusal and error,
    /// after RecordFailure — TIMED_OUT and ABORTED. Step Functions sends these to the default bus for a Standard workflow,
    /// best effort.
    /// </summary>
    public static string FailedDeployPattern(string stateMachineArn)
        => JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["source"] = new[] { "aws.states" },
            ["detail-type"] = new[] { "Step Functions Execution Status Change" },
            ["detail"] = new Dictionary<string, object>
            {
                ["status"] = new[] { "FAILED", "TIMED_OUT", "ABORTED" },
                ["stateMachineArn"] = new[] { stateMachineArn },
            },
        });

    /// <summary>
    /// The trigger's resources and its start function (DecoupledCd.md §4.3, P2 stage D).
    ///
    /// <para>ONE ROUTE PER IMAGE REPOSITORY, ONE TARGET PER TENANT. A record's key says which repository wrote it,
    /// and each tenant runs its own copy of the service, so a build rolls <c>{sk}-{tenant}-{artifact}</c> in every
    /// tenant found. A repository with two artifacts is refused: a record names a digest, not the ECR repository it
    /// was pushed to, so its image could not be matched to a service.</para>
    /// </summary>
    private static (DeployerTriggerPlan Plan, DeployerFunction Function) TriggerFor(
        SystemConfig config, PipelineConfig p, string region, string acct, string artifactAccount,
        string buildRecordStore, string stateMachineArn, DeployerTriggerInputs? inputs, IReadOnlyList<string> alarmActions)
    {
        if (inputs is null)
            throw new InvalidOperationException(
                "Pipeline.DeployOnBuildRecord is on, but the deployer was planned without the ECS cluster and the tenants " +
                "whose services a build rolls. `lz bootstrapdeployer` resolves both before it plans.");

        if (string.IsNullOrWhiteSpace(p.TargetAccountId))
            throw new InvalidOperationException(
                "Pipeline.DeployOnBuildRecord needs Pipeline.TargetAccountId: the build account forwards records to a bus in that account.");

        if (inputs.TenantKeys.Count == 0)
            throw new InvalidOperationException(
                $"Pipeline.DeployOnBuildRecord is on, but no tenant config was found for {config.SystemKey}/{config.Environment}, " +
                "so a build would roll no service. Run from the workspace that holds the tenantconfig files.");

        var sk = config.SystemKey;

        var routes = (p.Repositories ?? new List<PipelineRepositoryConfig>())
            .Where(r => string.Equals(r.Class, DeployerTrigger.RecordClass, StringComparison.Ordinal))
            .Select(r =>
            {
                var artifacts = r.Artifacts ?? new List<string>();
                if (artifacts.Count != 1)
                    throw new InvalidOperationException(
                        $"Pipeline.Repositories entry '{r.Repo}' builds {artifacts.Count} image artifacts. A build record names a " +
                        "digest, not the repository it was pushed to, so the trigger could not tell which service its image " +
                        "belongs to. Give the repository one artifact, or turn Pipeline.DeployOnBuildRecord off.");

                var artifact = artifacts[0];
                return new TriggerRoute(
                    BuildRecordFormat.PrefixFor(DeployerTrigger.RecordClass, r.Repo!),
                    inputs.TenantKeys
                        .Select(tk => new TriggerTarget(tk, new DeployTarget(
                            inputs.Cluster, $"{sk}-{tk}-{artifact}", artifact, EcrRepositoryNaming.For(config, artifact))))
                        .ToList());
            })
            .ToList();

        var busName = TriggerBusName(config);
        var busArn = TriggerBusArn(region, acct, config);
        var ruleName = StartRuleName(config);
        // A rule on a custom bus has the bus in its ARN.
        var ruleArn = $"arn:aws:events:{region}:{acct}:rule/{busName}/{ruleName}";
        var function = StartFunctionName(config);
        var queue = $"{function}-dlq";
        var queueArn = $"arn:aws:sqs:{region}:{acct}:{queue}";

        var plan = new DeployerTriggerPlan(
            BusName: busName,
            BusArn: busArn,
            BusPolicy: CrossAccount.TriggerBusPolicy(busArn, artifactAccount, RecordForwarderRoleName(config)),
            RuleName: ruleName,
            RuleArn: ruleArn,
            EventPattern: DeployerTrigger.EventPattern(artifactAccount, buildRecordStore),
            StartFunctionName: function,
            StartFunctionArn: $"arn:aws:lambda:{region}:{acct}:function:{function}",
            DeadLetterQueueName: queue,
            DeadLetterQueueArn: queueArn,
            DeadLetterQueuePolicy: CrossAccount.DeadLetterQueuePolicy(queueArn, ruleArn),
            AlarmName: StartDeadLetterAlarmName(config),
            // Lambda's own defaults, written down: two retries, and an hour before an event that keeps failing is queued.
            MaximumRetryAttempts: 2,
            MaximumEventAgeSeconds: 3600,
            Routes: routes,
            AlarmActions: alarmActions);

        if (StartDeadLetterAlarmName(config) != $"{queue}-not-empty")
            throw new InvalidOperationException("the start queue's alarm name diverged from StartDeadLetterAlarmName, which the alerts topic's policy names.");

        if (StartFunctionRoleName(config) != $"{function}-fn")
            throw new InvalidOperationException("the start function's role name diverged from StartFunctionRoleName, which the build account's grant names.");

        var startFunction = new DeployerFunction(
            function, DeployerHandlers.Start, StartFunctionRoleName(config),
            Combine(Logs(region, acct, function), StartGrants(stateMachineArn, queueArn, buildRecordStore)),
            new Dictionary<string, string>
            {
                [DeployerEnvironment.ArtifactAccount] = artifactAccount,
                [DeployerEnvironment.BuildRecordStore] = buildRecordStore,
                [DeployerEnvironment.StateMachine] = stateMachineArn,
                [DeployerEnvironment.TriggerRoutes] = DeployerTrigger.EncodeRoutes(routes),
                // Main only (P2 stage D2). A build from any other branch is skipped, and can be deployed by hand.
                [DeployerEnvironment.TriggerRefs] = DeployerEnvironment.Join(DeployerTrigger.DefaultRefs),
            },
            TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: false);

        return (plan, startFunction);
    }

    /// <summary>
    /// True when this environment deploys on every build record. The bootstrapper asks before resolving the cluster
    /// and tenants the trigger needs, so an environment without the trigger never reads them.
    /// </summary>
    public static bool TriggerWanted(SystemConfig config) => config.Pipeline is { Enabled: true, DeployOnBuildRecord: true };

    /// <summary>
    /// The trigger bus's name. ONE DEFINITION FOR BOTH ACCOUNTS, like <see cref="VerifyRoleName"/>: this planner creates the
    /// bus, and the build account's forwarding rule targets it by ARN.
    /// </summary>
    public static string TriggerBusName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-deployer-trigger";

    /// <summary>The trigger bus's ARN, in <paramref name="targetAccountId"/>.</summary>
    public static string TriggerBusArn(string region, string targetAccountId, SystemConfig config)
        => $"arn:aws:events:{region}:{targetAccountId}:event-bus/{TriggerBusName(config)}";

    /// <summary>The rule on the trigger bus that invokes the start function. The apply removes it when the trigger is off.</summary>
    public static string StartRuleName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-deployer-start";

    /// <summary>The function that names and starts executions.</summary>
    public static string StartFunctionName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-deployer-start";

    /// <summary>
    /// The start function's role. ONE DEFINITION FOR BOTH ACCOUNTS, like <see cref="VerifyRoleName"/>: this planner creates
    /// it, and the build account's bucket policy lets it read image records to learn their branch.
    /// </summary>
    public static string StartFunctionRoleName(SystemConfig config) => $"{StartFunctionName(config)}-fn";

    /// <summary>The start queue's alarm, which the alerts topic's policy names.</summary>
    public static string StartDeadLetterAlarmName(SystemConfig config) => $"{StartFunctionName(config)}-dlq-not-empty";

    /// <summary>
    /// The build account's forwarding-queue alarm. ONE DEFINITION FOR BOTH ACCOUNTS: the build account creates it, and this
    /// account's alerts topic admits it by ARN.
    /// </summary>
    public static string ForwardDeadLetterAlarmName(SystemConfig config) => $"{ForwardRuleName(config)}-dlq-not-empty";

    /// <summary>True when this environment's pipeline alerts a person (P2 stage D3).</summary>
    public static bool AlertsWanted(SystemConfig config) => config.Pipeline is { Enabled: true, Alerts: true };

    /// <summary>The alerts topic. ONE DEFINITION FOR BOTH ACCOUNTS: this planner creates it, and the build account's alarm targets it.</summary>
    public static string AlertsTopicName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-pipeline-alerts";

    /// <summary>The alerts topic's ARN, in <paramref name="targetAccountId"/>.</summary>
    public static string AlertsTopicArn(string region, string targetAccountId, SystemConfig config)
        => $"arn:aws:sns:{region}:{targetAccountId}:{AlertsTopicName(config)}";

    /// <summary>The default-bus rule that sends this deployer's failed executions to the topic.</summary>
    public static string FailedDeployRuleName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-deployer-failed";

    /// <summary>The sweep for images no build record names.</summary>
    public static string CorroborateFunctionName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-deployer-corroborate";

    /// <summary>
    /// The sweep's role. ONE DEFINITION FOR BOTH ACCOUNTS, like <see cref="VerifyRoleName"/>: this planner creates it, and the
    /// build account's bucket policy lets it list and read image records.
    /// </summary>
    public static string CorroborateFunctionRoleName(SystemConfig config) => $"{CorroborateFunctionName(config)}-fn";

    /// <summary>The default-bus rule that runs the sweep on a schedule.</summary>
    public static string CorroborateScheduleRuleName(SystemConfig config) => $"{CorroborateFunctionName(config)}-schedule";

    /// <summary>ALARM when a sweep fails.</summary>
    public static string CorroborateErrorsAlarmName(SystemConfig config) => $"{CorroborateFunctionName(config)}-errors";

    /// <summary>
    /// The build account's role that puts forwarded record events on the trigger bus. ONE DEFINITION FOR BOTH ACCOUNTS:
    /// the build account creates it, and this account's bus policy admits it by name.
    /// </summary>
    public static string RecordForwarderRoleName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-record-forwarder";

    /// <summary>The build account's rule that forwards record events to this environment. Removed when the trigger is off.</summary>
    public static string ForwardRuleName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-forward-build-records";

    /// <summary>
    /// Why <c>lz deploycontainer</c> must not build tenant service <paramref name="serviceName"/> in this environment, or null
    /// when it may (DecoupledCd.md §8 item 4).
    ///
    /// <para>REFUSED WHERE THE IMAGE COULD NOT RUN, AND ONLY THERE — which is narrower than §8's "refuses under the block".
    /// Under <c>Pipeline.EnforceSignatures</c> the service carries the signature hook, and the hook fails any deployment whose
    /// image the pipeline did not sign — which a workstation build never is — so the build would be pushed and then rolled back
    /// (measured twice on 2026-09-12). Keyed on the hook list the tenant service attaches (<see cref="SignatureHooksFor"/>), so
    /// the refusal and the hook cannot disagree. With the flag off there is no hook, and this says nothing: a workstation image
    /// still runs, and a new tenant's first deploy still needs one, because <c>deploytenant</c>'s image gate reads the
    /// workstation repository.</para>
    /// </summary>
    public static string? RefusalForWorkstationImage(SystemConfig config, string serviceName)
    {
        if (SignatureHooksFor(config, serviceName) is not { Count: > 0 }) return null;

        // The hook is attached only to a service the pipeline builds, so a repository entry names it.
        var builtBy = config.Pipeline!.Repositories!.First(r =>
            string.Equals(r.Class, "image", StringComparison.Ordinal)
            && (r.Artifacts ?? new List<string>()).Contains(serviceName, StringComparer.Ordinal)).Repo;

        return $"{config.SystemKey}/{config.Environment} enforces signatures on {serviceName} (Pipeline.EnforceSignatures): its " +
               "signature hook rolls back any deployment of an image the pipeline did not sign, and a workstation build is never " +
               $"signed, so this one would be pushed and then refused. Build {serviceName} with {builtBy}'s pipeline build " +
               "workflow instead; the deployer rolls out what it signs. To deploy a workstation image anyway, turn " +
               "Pipeline.EnforceSignatures off first, and for a service that is already running, run `lz deploytenant` to take " +
               "the hook off before deploying the image.";
    }

    /// <summary>
    /// Why <c>lz updateedge</c> must not publish for this environment, or null when it may (DecoupledCd.md P-14).
    ///
    /// <para>UNDER THE BLOCK A CLOUDFRONT FUNCTION HAS ONE PUBLISHER. <c>updateedge</c> publishes <c>CloudFront/*.js</c>
    /// straight to LIVE through the SDK; <c>lz deploytenant</c> publishes the same files through Pulumi, refreshing from
    /// AWS first. Where <see cref="PipelineConfig.Classes"/> lists <c>config</c>, the class that carries the edge
    /// functions, the second path is closed, as <c>deploycontainer</c>'s is for a signed service, and
    /// <c>deploytenant</c> is the path that remains until the config bundle deploys them (P3 stage E).</para>
    ///
    /// <para>Keyed on the class, not the topology. <c>updateedge</c> exists to avoid the interruption a
    /// <c>deploytenant</c> causes on ecs-fargate-keycloak, whose tenant service Pulumi sets to zero tasks, and it is
    /// refused there too under the block, because two publishers of one function is what the entry closes. A system
    /// with no block, a disabled one, or no config class keeps the command as it was.</para>
    /// </summary>
    public static string? RefusalForWorkstationEdge(SystemConfig config)
    {
        if (config.Pipeline is not { Enabled: true } p) return null;
        if (p.Classes is not { } classes || !classes.Contains("config", StringComparer.Ordinal)) return null;

        return $"{config.SystemKey}/{config.Environment} lists config in Pipeline.Classes, so its CloudFront functions have one " +
               "publisher: `lz deploytenant`, whose Pulumi program refreshes from AWS before it publishes CloudFront/*.js. " +
               "updateedge would publish them to LIVE around it, a second publisher of each function. Run " +
               "`lz deploytenant` instead. To use updateedge anyway, take config out of Pipeline.Classes first, which also " +
               "stops the deployer accepting config records.";
    }

    /// <summary>The signature hook's function name — one definition for the planner and the service that attaches it.</summary>
    public static string SignatureHookFunctionName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-signature-hook";

    /// <summary>The role ECS assumes to invoke the hook — one definition for the planner and the service.</summary>
    public static string SignatureHookInvokerRoleName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-signature-hook-invoker";

    /// <summary>
    /// The signature hook a tenant service attaches, or null — and null is what every system without
    /// <c>Pipeline.EnforceSignatures</c> gets. The Fargate component reads it through
    /// <see cref="SignatureHooksFor"/>, which turns that null into an explicit empty list for a service the
    /// pipeline builds, so that turning enforcement off also takes the hook off.
    ///
    /// <para><b>THE FIRST READ OF THE BLOCK THAT MOVES A SERVICE'S PLAN</b> (DecoupledCd.md §4.4.1, unverified 4).
    /// Null unless ALL of: the block is enabled; <c>EnforceSignatures</c> is on; <c>TargetAccountId</c> names the
    /// account the hook lives in; and the pipeline builds this service as an image
    /// (<see cref="EcrRepositoryNaming.PipelineBuildsImage"/>). A service the pipeline does not build is not
    /// refused images it could never get signed.</para>
    /// </summary>
    public static SignatureHookAttachment? SignatureHookFor(SystemConfig config, string serviceName)
    {
        if (config.Pipeline is not { Enabled: true, EnforceSignatures: true } p) return null;
        if (string.IsNullOrWhiteSpace(p.TargetAccountId)) return null;
        if (!EcrRepositoryNaming.PipelineBuildsImage(config, serviceName)) return null;

        return new SignatureHookAttachment(
            SignatureHookFunctionArn(config)!,
            $"arn:aws:iam::{p.TargetAccountId}:role/{SignatureHookInvokerRoleName(config)}");
    }

    /// <summary>
    /// The signature hook function's ARN in the environment's account, or null without a
    /// <c>TargetAccountId</c> — one definition for attaching the hook and for taking it off.
    /// </summary>
    public static string? SignatureHookFunctionArn(SystemConfig config)
        => config.Pipeline is { TargetAccountId: { } account } && !string.IsNullOrWhiteSpace(account)
            ? $"arn:aws:lambda:{config.Region}:{account}:function:{SignatureHookFunctionName(config)}"
            : null;

    /// <summary>
    /// The attached lifecycle hooks to KEEP, by index, when a service's plan declares no signature hook but ECS still
    /// has lz's — or null when there is nothing to take off.
    ///
    /// <para><b>THE REMOVAL PULUMI CANNOT MAKE.</b> Measured 2026-09-13 against dev, with the hook attached and
    /// <see cref="SignatureHooksFor"/> declaring an explicit empty list: `lz deploytenant` applied
    /// `update Service [deploymentConfiguration]` and exited 0, and the hook stayed. CloudTrail shows why. The
    /// provider's UpdateService sent `{"strategy":"ROLLING","bakeTimeInMinutes":0}` with no `lifecycleHooks` key
    /// at all — an empty list is dropped — and ECS keeps whatever a request leaves out. The circuit breaker,
    /// alarms and percentages, also absent, were kept too. The SDK call this decides then sent
    /// `{"lifecycleHooks":[]}` and the hook was gone. It was re-attached by the next deploy with the flag back,
    /// and neither direction started an ECS deployment.</para>
    ///
    /// <para>Null unless ALL of: the plan declares an EMPTY list (not null — a service outside the pipeline is not
    /// lz's to change; not one hook — attaching is Pulumi's, and works); the hook's ARN is known; and lz's hook is
    /// among those attached. Only lz's hook is removed: any other hook is kept, in order.</para>
    /// </summary>
    public static IReadOnlyList<int>? HooksToKeepAfterRemoval(
        IReadOnlyList<SignatureHookAttachment>? declared, IReadOnlyList<string?> attachedTargets, string? signatureHookFunctionArn)
    {
        if (declared is not { Count: 0 } || signatureHookFunctionArn is null) return null;

        var keep = new List<int>();
        for (var i = 0; i < attachedTargets.Count; i++)
        {
            if (!string.Equals(attachedTargets[i], signatureHookFunctionArn, StringComparison.Ordinal))
                keep.Add(i);
        }

        return keep.Count == attachedTargets.Count ? null : keep;
    }

    /// <summary>
    /// The lifecycle hooks a tenant service's plan NAMES — or null, when its plan must not mention them.
    ///
    /// <para><b>NULL</b> for every service the pipeline does not build: no enabled block, no
    /// <c>TargetAccountId</c>, or not one of an image repository's artifacts. Those plans never set a
    /// <c>DeploymentConfiguration</c>, exactly as before the hook existed.</para>
    ///
    /// <para><b>EMPTY, NOT NULL, WHEN <c>EnforceSignatures</c> IS OFF</b> for a service the pipeline does build.
    /// A plan that leaves <c>DeploymentConfiguration</c> unset takes ECS's value for it. Measured
    /// 2026-09-12: with the hook attached and the flag deleted from dev's config, <c>lz previewtenant</c>
    /// planned NO CHANGES. Turning enforcement off would have left the hook refusing every workstation image,
    /// while the config said nothing was enforced. The explicit empty list makes the plan say so, but applying
    /// it does not remove the hook — the provider drops an empty list from its request (2026-09-13, CloudTrail).
    /// The tenant post-deploy step removes it, through <see cref="HooksToKeepAfterRemoval"/>.</para>
    /// </summary>
    public static IReadOnlyList<SignatureHookAttachment>? SignatureHooksFor(SystemConfig config, string serviceName)
    {
        if (config.Pipeline is not { Enabled: true } p) return null;
        if (string.IsNullOrWhiteSpace(p.TargetAccountId)) return null;
        if (!EcrRepositoryNaming.PipelineBuildsImage(config, serviceName)) return null;

        return SignatureHookFor(config, serviceName) is { } hook
            ? new[] { hook }
            : Array.Empty<SignatureHookAttachment>();
    }

    /// <summary>
    /// The Verify function's role name. ONE DEFINITION FOR BOTH ACCOUNTS: this planner creates the role
    /// in the target account, and the build account's bucket policy names it — a rename on one side
    /// alone would leave Verify unable to read a single record, with an access denial that names neither.
    /// </summary>
    public static string VerifyRoleName(SystemConfig config) => $"{config.SystemKey}-{config.Environment}-deployer-verify-fn";

    /// <summary>
    /// The state machine's role: invoke its functions, roll the service, and — only when approval is
    /// required — publish to the approval topic. It writes no evidence: since 2026-09-12 the Record
    /// function does, under its own role, as RecordFailure always did.
    ///
    /// <para><c>iam:PassRole</c> is the statement to read twice: it is scoped to the service's task
    /// and execution roles by name. Unscoped, it is privilege escalation — a principal that may pass
    /// any role can register a task definition running as any role in the account.</para>
    /// </summary>
    private static string RolePolicyFor(
        string region, string accountId, string sk, string env,
        IReadOnlyList<string> functionArns, string? approvalTopicArn)
    {
        var statements = new List<object>
        {
            new
            {
                // The gap stage A shipped with: without this the first execution dies at Verify.
                Sid = "InvokeItsOwnFunctions",
                Effect = "Allow",
                Action = new[] { "lambda:InvokeFunction" },
                Resource = functionArns,
            },
            new
            {
                Sid = "RollTheService",
                Effect = "Allow",
                Action = new[] { "ecs:UpdateService" },
                Resource = ServiceArns(region, accountId, sk, env),
            },
            PassRoleStatement(accountId, sk,
                // KEPT HERE as well as on Prepare, which registers the revision: whether UpdateService
                // re-checks PassRole for the roles a revision names is not measured. It is scoped
                // identically, so keeping it widens nothing.
                "PassOnlyTheServicesOwnRoles"),
        };

        if (approvalTopicArn != null)
        {
            statements.Add(new
            {
                Sid = "AskForApproval",
                Effect = "Allow",
                Action = new[] { "sns:Publish" },
                Resource = approvalTopicArn,
            });
        }

        return JsonSerializer.Serialize(new { Version = "2012-10-17", Statement = statements }, Asl);
    }

    /// <summary>
    /// The PassRole grant, shared by the state machine and Prepare.
    ///
    /// <para>THE PATTERN COMES FROM THE CODE THAT CREATES THESE ROLES, not from the naming convention
    /// it looks like it should follow. AwsFargateTenantServiceComponent builds
    /// <c>prefix = {sk}-{tk}-{svc}</c> and then <c>{prefix}-task</c> / <c>{prefix}-exec</c>, and Pulumi
    /// auto-naming appends a random suffix — so the live roles are <c>scu-mp-aiphost-task-0114517</c>
    /// and <c>scu-mp-aiphost-exec-7a3f57c</c>.</para>
    ///
    /// <para>The first version of this was wrong three ways at once: it had an environment segment
    /// these names do not carry (one account per environment, so the env is not in the name), spelled
    /// it <c>-execution</c> instead of <c>-exec</c>, and had no trailing wildcard for the Pulumi suffix.
    /// It matched nothing. Found by listing the account's actual roles after applying, not by review.</para>
    ///
    /// <para>Still scoped: it cannot pass <c>scu-dev-deployer</c>, <c>scu-website-ci</c>,
    /// <c>scu-e2e-ci</c> or <c>scu-tailscale-role</c>. Narrowing further would mean the deployer knowing
    /// the tenant list, which it does not and should not. The PassedToService condition is the backstop
    /// that keeps these usable only as ECS task roles.</para>
    /// </summary>
    private static object PassRoleStatement(string accountId, string sk, string sid) => new
    {
        Sid = sid,
        Effect = "Allow",
        Action = new[] { "iam:PassRole" },
        Resource = new[]
        {
            $"arn:aws:iam::{accountId}:role/{sk}-*-task-*",
            $"arn:aws:iam::{accountId}:role/{sk}-*-exec-*",
        },
        Condition = new Dictionary<string, object>
        {
            ["StringEquals"] = new Dictionary<string, object>
            {
                ["iam:PassedToService"] = "ecs-tasks.amazonaws.com",
            },
        },
    };

    /// <summary>
    /// The log grant AWS's own per-function basic execution role uses: create the log group anywhere
    /// in the account's region, write only to this function's.
    /// </summary>
    private static object[] Logs(string region, string accountId, string function) => new object[]
    {
        new
        {
            Sid = "CreateItsLogGroup",
            Effect = "Allow",
            Action = new[] { "logs:CreateLogGroup" },
            Resource = $"arn:aws:logs:{region}:{accountId}:*",
        },
        new
        {
            Sid = "WriteItsOwnLogs",
            Effect = "Allow",
            Action = new[] { "logs:CreateLogStream", "logs:PutLogEvents" },
            Resource = $"arn:aws:logs:{region}:{accountId}:log-group:/aws/lambda/{function}:*",
        },
    };

    private static object[] VerifyGrants(
        string region, string accountId, string sk, string env, string buildRecordStore, IReadOnlyList<string> repositories) => new object[]
    {
        new
        {
            // IMAGE RECORDS ONLY. This deployer rolls class 1, so it reads nothing else. The store is
            // in the BUILD account: this grant is half of it, and that bucket's policy is the other.
            Sid = "ReadImageBuildRecords",
            Effect = "Allow",
            Action = new[] { "s3:GetObject" },
            Resource = $"arn:aws:s3:::{buildRecordStore}/image/*",
        },
        new
        {
            // WITHOUT LIST, A MISSING RECORD IS A 403, not a 404 — S3 will not say whether a key exists
            // to a principal that may not list — and "access denied" would send whoever reads the
            // evidence after an IAM problem that is not there. Scoped to the same prefix.
            Sid = "TellAMissingRecordFromADeniedOne",
            Effect = "Allow",
            Action = new[] { "s3:ListBucket" },
            Resource = $"arn:aws:s3:::{buildRecordStore}",
            Condition = new Dictionary<string, object>
            {
                ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = "image/*" },
            },
        },
        new
        {
            Sid = "FindTheImageAndItsScan",
            Effect = "Allow",
            // DescribeImageScanFindings, because the current basic scanning leaves DescribeImages' scan
            // attributes empty — with only DescribeImages, Verify refused every image at [scan] while
            // every one of them had a COMPLETE scan. Still read-only: Verify waits for scan-on-push, and
            // never starts a scan.
            Action = new[] { "ecr:DescribeImages", "ecr:DescribeImageScanFindings" },
            Resource = RepositoryArns(region, accountId, repositories),
        },
        new
        {
            Sid = "ConfirmTheTargetService",
            Effect = "Allow",
            Action = new[] { "ecs:DescribeServices" },
            Resource = ServiceArns(region, accountId, sk, env),
        },
        new
        {
            // The target's container, in the revision the service runs — checked before any approval gate rather than by
            // Prepare after one. On "*", as Prepare's read of the same revision is.
            Sid = "ConfirmTheTargetContainer",
            Effect = "Allow",
            Action = new[] { "ecs:DescribeTaskDefinition" },
            Resource = "*",
        },
    };

    private static object[] PrepareGrants(string region, string accountId, string sk, string env) => new object[]
    {
        new
        {
            Sid = "ReadTheCurrentRevision",
            Effect = "Allow",
            Action = new[] { "ecs:DescribeServices" },
            Resource = ServiceArns(region, accountId, sk, env),
        },
        new
        {
            Sid = "RegisterTheNewRevision",
            Effect = "Allow",
            Action = new[] { "ecs:DescribeTaskDefinition", "ecs:RegisterTaskDefinition" },
            Resource = "*",
        },
        new
        {
            // The revision is registered WITH the current revision's tags, and tagging on create is
            // authorized separately. Conditioned so this cannot tag anything after the fact.
            Sid = "TagOnlyWhileRegistering",
            Effect = "Allow",
            Action = new[] { "ecs:TagResource" },
            Resource = "*",
            Condition = new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object> { ["ecs:CreateAction"] = "RegisterTaskDefinition" },
            },
        },
        PassRoleStatement(accountId, sk, "PassOnlyTheServicesOwnRoles"),
    };

    private static object[] RolloutGrants(string region, string accountId, string sk, string env) => new object[]
    {
        new
        {
            Sid = "WatchTheRoll",
            Effect = "Allow",
            Action = new[] { "ecs:DescribeServices" },
            Resource = ServiceArns(region, accountId, sk, env),
        },
        new
        {
            Sid = "ReadWhatIsRunning",
            Effect = "Allow",
            Action = new[] { "ecs:ListTasks", "ecs:DescribeTasks" },
            Resource = "*",
        },
        new
        {
            // Read only when a roll did not land, to put ECS's reason — a lifecycle hook's refusal, the circuit
            // breaker — into the failure evidence instead of "superseded" (VerifyRolloutStep.NotDeployedReason).
            Sid = "ReadWhyARollDidNotLand",
            Effect = "Allow",
            Action = new[] { "ecs:ListServiceDeployments", "ecs:DescribeServiceRevisions" },
            Resource = "*",
        },
    };

    private static object[] RecordGrants(string evidence) => new object[]
    {
        new
        {
            // Write-once evidence. No delete, exactly as the build roles hold none.
            Sid = "WriteDeployEvidence",
            Effect = "Allow",
            Action = new[] { "s3:PutObject" },
            Resource = $"arn:aws:s3:::{evidence}/deploys/*",
        },
    };

    private static object[] FailureGrants(string evidence) => new object[]
    {
        new
        {
            Sid = "WriteFailureEvidence",
            Effect = "Allow",
            Action = new[] { "s3:PutObject" },
            Resource = $"arn:aws:s3:::{evidence}/failures/*",
        },
    };

    /// <summary>
    /// What Verify adds for client bundles (P4 stage C): read client records, and HEAD a bundle by version. Both stores are
    /// in the BUILD account, so each grant is half of one; that account's bucket policies are the other half.
    /// </summary>
    private static object[] VerifyClientGrants(string buildRecordStore, string artifactStore) => new object[]
    {
        new
        {
            Sid = "ReadClientBuildRecords",
            Effect = "Allow",
            Action = new[] { "s3:GetObject" },
            Resource = $"arn:aws:s3:::{buildRecordStore}/client/*",
        },
        new
        {
            Sid = "TellAMissingClientRecordFromADeniedOne",
            Effect = "Allow",
            Action = new[] { "s3:ListBucket" },
            Resource = $"arn:aws:s3:::{buildRecordStore}",
            Condition = new Dictionary<string, object>
            {
                ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = "client/*" },
            },
        },
        new
        {
            // A HEAD of a version, never a download: Verify compares S3's own checksum with the record's.
            Sid = "HeadClientBundleVersions",
            Effect = "Allow",
            Action = new[] { "s3:GetObjectVersion" },
            Resource = $"arn:aws:s3:::{artifactStore}/client/*",
        },
        new
        {
            Sid = "TellAMissingBundleVersionFromADeniedOne",
            Effect = "Allow",
            Action = new[] { "s3:ListBucket", "s3:ListBucketVersions" },
            Resource = $"arn:aws:s3:::{artifactStore}",
            Condition = new Dictionary<string, object>
            {
                ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = "client/*" },
            },
        },
    };

    /// <summary>
    /// The bundle-deploy role (§3), for class 2: read client bundles; mirror, create and harden the configured web apps'
    /// buckets — their names, not a pattern (P-7); invalidate on the distributions that serve them. Nothing of ECS, Pulumi's
    /// state or IAM, and the §5.5 Deny beside it, as on every deployer role.
    /// </summary>
    private static object[] DeployBundleGrants(
        string accountId, string artifactStore, IReadOnlyList<ClientTarget> targets, Lz.Aws.Storage.BucketDurabilityDecision durability)
    {
        var buckets = targets.Select(t => $"arn:aws:s3:::{t.Bucket}").Distinct().ToArray();

        // P-7: the deploy creates its bucket and writes its public-access block and policy on every run, as deploywebapp
        // does, and applies the durability decision — which asks only for what the decision needs.
        var bucketActions = new List<string> { "s3:CreateBucket", "s3:PutBucketPublicAccessBlock", "s3:PutBucketPolicy" };
        if (durability.Versioning) bucketActions.Add("s3:PutBucketVersioning");
        if (durability is { Versioning: true, NoncurrentExpirationDays: not null }) bucketActions.Add("s3:PutLifecycleConfiguration");

        return new object[]
        {
            new
            {
                Sid = "ReadClientBundleVersions",
                Effect = "Allow",
                Action = new[] { "s3:GetObjectVersion" },
                Resource = $"arn:aws:s3:::{artifactStore}/client/*",
            },
            new
            {
                // HeadBucket, and the listing a mirror deletes from.
                Sid = "ListTheAppBuckets",
                Effect = "Allow",
                Action = new[] { "s3:ListBucket" },
                Resource = buckets,
            },
            new
            {
                // A mirror deletes what the bundle lacks, so delete is load-bearing (§4.6); GetObject reads the deploy marker
                // and each object's checksum.
                Sid = "MirrorTheAppObjects",
                Effect = "Allow",
                Action = new[] { "s3:GetObject", "s3:PutObject", "s3:DeleteObject" },
                Resource = buckets.Select(b => $"{b}/*").ToArray(),
            },
            new
            {
                Sid = "CreateAndHardenTheAppBuckets",
                Effect = "Allow",
                Action = bucketActions.ToArray(),
                Resource = buckets,
            },
            new
            {
                Sid = "InvalidateTheAppPaths",
                Effect = "Allow",
                Action = new[] { "cloudfront:CreateInvalidation" },
                Resource = DistributionArns(accountId, targets),
            },
        };
    }

    /// <summary>What VerifyBundle may do: list and read the apps' objects, and watch its invalidations. It writes nothing.</summary>
    private static object[] VerifyBundleGrants(string accountId, IReadOnlyList<ClientTarget> targets)
    {
        var buckets = targets.Select(t => $"arn:aws:s3:::{t.Bucket}").Distinct().ToArray();
        return new object[]
        {
            new
            {
                Sid = "ListTheAppBuckets",
                Effect = "Allow",
                Action = new[] { "s3:ListBucket" },
                Resource = buckets,
            },
            new
            {
                Sid = "ReadTheAppObjects",
                Effect = "Allow",
                Action = new[] { "s3:GetObject" },
                Resource = buckets.Select(b => $"{b}/*").ToArray(),
            },
            new
            {
                Sid = "WatchTheInvalidations",
                Effect = "Allow",
                Action = new[] { "cloudfront:GetInvalidation" },
                Resource = DistributionArns(accountId, targets),
            },
        };
    }

    // CloudFront is global: a distribution's ARN names no region.
    private static string[] DistributionArns(string accountId, IReadOnlyList<ClientTarget> targets)
        => targets.SelectMany(t => t.Distributions).Distinct(StringComparer.Ordinal)
            .Select(d => $"arn:aws:cloudfront::{accountId}:distribution/{d}").ToArray();

    /// <summary>
    /// What the start function may do: read an image record for its branch, start THIS deployer, read back an execution
    /// of it whose name is taken, and dead-letter its own failures. It judges nothing about the record but its ref —
    /// Verify does the rest — and rolls nothing.
    /// </summary>
    private static object[] StartGrants(string stateMachineArn, string queueArn, string buildRecordStore) => new object[]
    {
        new
        {
            // IMAGE RECORDS, READ ONLY, and no list: the event names the key, so there is nothing to find. The store's
            // bucket policy in the build account is the other half of this grant.
            Sid = "ReadTheRecordsBranch",
            Effect = "Allow",
            Action = new[] { "s3:GetObject" },
            Resource = $"arn:aws:s3:::{buildRecordStore}/{DeployerTrigger.RecordClass}/*",
        },
        new
        {
            Sid = "StartTheDeployer",
            Effect = "Allow",
            Action = new[] { "states:StartExecution" },
            Resource = stateMachineArn,
        },
        new
        {
            // A duplicate that arrives after its execution finished gets ExecutionAlreadyExists; the input tells a
            // duplicate from a conflict (StartStep).
            Sid = "ReadAnExecutionWhoseNameIsTaken",
            Effect = "Allow",
            Action = new[] { "states:DescribeExecution" },
            Resource = DeployerTrigger.ExecutionArn(stateMachineArn, "*"),
        },
        new
        {
            // Lambda sends a failed asynchronous invocation's record to its destination under the function's role.
            Sid = "DeadLetterItsFailures",
            Effect = "Allow",
            Action = new[] { "sqs:SendMessage" },
            Resource = queueArn,
        },
    };

    /// <summary>
    /// What the sweep may do (P2 stage D3): list this environment's pipeline images, find and read image records, record an
    /// anomaly once, and publish to the alerts topic. It deploys nothing and changes no record.
    /// </summary>
    private static object[] CorroborateGrants(
        string region, string accountId, string buildRecordStore, string evidence, string topicArn, IReadOnlyList<string> repositories)
        => new object[]
        {
            new
            {
                Sid = "ListTheImagesThatArrived",
                Effect = "Allow",
                Action = new[] { "ecr:DescribeImages" },
                Resource = RepositoryArns(region, accountId, repositories),
            },
            new
            {
                // The records are found by listing: a record's key carries its build time, not the digest it names. The
                // store's bucket policy in the build account is the other half of both grants.
                Sid = "FindImageRecords",
                Effect = "Allow",
                Action = new[] { "s3:ListBucket" },
                Resource = $"arn:aws:s3:::{buildRecordStore}",
                Condition = new Dictionary<string, object>
                {
                    ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = $"{DeployerTrigger.RecordClass}/*" },
                },
            },
            new
            {
                Sid = "ReadImageRecords",
                Effect = "Allow",
                Action = new[] { "s3:GetObject" },
                Resource = $"arn:aws:s3:::{buildRecordStore}/{DeployerTrigger.RecordClass}/*",
            },
            new
            {
                // Write-once, and nothing outside anomalies/. No read: whether one is recorded is learned by listing below.
                Sid = "RecordAnAnomalyOnce",
                Effect = "Allow",
                Action = new[] { "s3:PutObject" },
                Resource = $"arn:aws:s3:::{evidence}/anomalies/*",
            },
            new
            {
                // Whether an anomaly is already recorded, by listing its exact key — which is why the write above needs no
                // read beside it.
                Sid = "FindRecordedAnomalies",
                Effect = "Allow",
                Action = new[] { "s3:ListBucket" },
                Resource = $"arn:aws:s3:::{evidence}",
                Condition = new Dictionary<string, object>
                {
                    ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = "anomalies/*" },
                },
            },
            new
            {
                Sid = "AlertAPerson",
                Effect = "Allow",
                Action = new[] { "sns:Publish" },
                Resource = topicArn,
            },
        };

    /// <summary>
    /// What the signature hook reads. The ECS and ECR actions are the ones AWS's own admission
    /// controller is documented to need; of its Signer actions only revocation is kept, because that is
    /// the call the Signer plugin makes during verification.
    /// </summary>
    private static object[] HookGrants(string region, string accountId, IReadOnlyList<string> repositories) => new object[]
    {
        new
        {
            Sid = "ReadTheRevisionsImages",
            Effect = "Allow",
            Action = new[] { "ecs:DescribeServiceRevisions", "ecs:DescribeTaskDefinition" },
            Resource = "*",
        },
        new
        {
            Sid = "AuthenticateToTheRegistry",
            Effect = "Allow",
            Action = new[] { "ecr:GetAuthorizationToken" },
            Resource = "*",
        },
        new
        {
            Sid = "ReadImagesAndTheirSignatures",
            Effect = "Allow",
            Action = new[] { "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:BatchCheckLayerAvailability" },
            Resource = RepositoryArns(region, accountId, repositories),
        },
        new
        {
            Sid = "CheckRevocation",
            Effect = "Allow",
            Action = new[] { "signer:GetRevocationStatus" },
            Resource = "*",
        },
    };

    private static string[] RepositoryArns(string region, string accountId, IReadOnlyList<string> repositories)
        => repositories.Select(r => $"arn:aws:ecr:{region}:{accountId}:repository/{r}").ToArray();

    private static string Policy(params object[] statements)
        => JsonSerializer.Serialize(new { Version = "2012-10-17", Statement = statements }, Asl);

    private static string Combine(object[] first, object[] second)
        => Policy(first.Concat(second).ToArray());

    /// <summary>
    /// §5.5 — the pipeline cannot rewrite itself. A config bundle can change what the deployer
    /// deploys; it cannot change the deployer.
    ///
    /// <para>An explicit Deny beats any Allow anywhere, including a future one nobody remembers
    /// adding, which is the whole point of stating it rather than relying on the absence of a
    /// grant.</para>
    /// </summary>
    private static string DenyPolicyFor()
        => JsonSerializer.Serialize(new
        {
            Version = "2012-10-17",
            Statement = new[]
            {
                new
                {
                    Sid = "TheDeployerMayNotRewriteThePipeline",
                    Effect = "Deny",
                    Action = PipelineBootstrapPlanner.SelfRewriteDenied,
                    Resource = "*",
                },
            },
        }, Asl);

    /// <summary>
    /// The state machine: Verify → [Approve] → Prepare → Deploy → VerifyRollout → Record.
    ///
    /// <para>NO PLAN STATE, because class 1 has nothing to preview — Plan is the Pulumi preview
    /// that classes 5–7 need (§4.5). Adding an empty one would imply a check that is not happening.</para>
    ///
    /// <para>PREPARE COMES AFTER APPROVE. Registering a revision is a write; doing it before a human
    /// said yes would leave a revision behind for every rejected request. Verify, before the gate,
    /// only reads.</para>
    ///
    /// <para>THE APPROVAL STATE IS BAKED IN OR ABSENT, not present-but-skipped. Dev sets
    /// <c>Required: false</c> and gets a machine with no human gate; prod gets one with a
    /// <c>.waitForTaskToken</c> that cannot be bypassed.</para>
    ///
    /// <para>WAITING IS RETRYING. A scan still running and a roll still rolling are thrown as named
    /// errors and retried on a fixed interval; the attempt limit is the timeout, and exhausting it
    /// falls through to the Catch like any other failure. Nothing polls inside a Lambda, so nothing
    /// bills for sleeping.</para>
    ///
    /// <para>EVERY STATE CATCHES. A failure anywhere records what happened and ends in a Fail state
    /// — an execution that died silently would leave a request that looks pending forever. Record catches
    /// too, since 2026-09-12: a deploy that landed and could not be recorded is still a failure, written
    /// down as one with the rollout that landed. RecordFailure, with nowhere left to record, ends in its own
    /// Fail state, so the execution's error says which of the two happened.</para>
    /// </summary>
    /// <param name="bundleFunctions">DeployBundle and VerifyBundle, where client targets exist (P4 stage C). With them the
    /// machine branches on the verified class after Verify — after Approve, where there is one — and without them it is
    /// the class-1 machine unchanged.</param>
    private static string DefinitionFor(
        bool approvalRequired, string verifyFn, string prepareFn, string rolloutFn,
        string recordFn, string failureFn, int heartbeatSeconds, string? approvalTopicArn,
        (string DeployBundle, string VerifyBundle)? bundleFunctions = null)
    {
        // Where a verified (and, in prod, approved) execution goes next: straight to Prepare on the class-1 machine, or to
        // the branch on its class.
        var afterVerification = bundleFunctions is null ? "Prepare" : "ByClass";
        var catchAll = new object[]
        {
            new
            {
                ErrorEquals = new[] { "States.ALL" },
                Next = "RecordFailure",
                ResultPath = "$.error",
            },
        };

        // AWS's recommended retry for Lambda's own transient faults — not for anything a function
        // decided. A refusal is never retried.
        object Transient() => new
        {
            ErrorEquals = new[]
            {
                "Lambda.ServiceException", "Lambda.AWSLambdaException",
                "Lambda.SdkClientException", "Lambda.TooManyRequestsException",
            },
            IntervalSeconds = 2,
            MaxAttempts = 3,
            BackoffRate = 2.0,
        };

        // An S3 fault inside a function that writes evidence, after the SDK's own retries. Safe to retry: the
        // write is conditional and an object already there counts as written, so a retry after a write that
        // landed changes nothing. The name is the exception's class name, which is what the .NET runtime
        // reports as the error (measured on this deployer's own refusals).
        object EvidenceWrite() => new
        {
            ErrorEquals = new[] { nameof(Amazon.S3.AmazonS3Exception) },
            IntervalSeconds = 2,
            MaxAttempts = 3,
            BackoffRate = 2.0,
        };

        // Every function receives the state plus the execution name, which keys its evidence.
        var payload = new Dictionary<string, object>
        {
            ["state.$"] = "$",
            ["executionName.$"] = "$$.Execution.Name",
        };

        var states = new Dictionary<string, object>
        {
            // Reads the record, checks provenance, class, lane, the -g<hex> discriminator and the
            // target; finds the image in this registry and applies the scan policy. Read-only.
            ["Verify"] = new
            {
                Type = "Task",
                Resource = verifyFn,
                Parameters = payload,
                ResultPath = "$.verified",
                Retry = new[]
                {
                    // The replica first: the image must exist before its scan can.
                    new
                    {
                        ErrorEquals = new[] { nameof(ImageNotYetReplicated) },
                        IntervalSeconds = ReplicationRetryIntervalSeconds,
                        MaxAttempts = ReplicationRetryMaxAttempts,
                        BackoffRate = 1.0,
                    },
                    new
                    {
                        ErrorEquals = new[] { nameof(ScanNotYetAvailable) },
                        IntervalSeconds = ScanRetryIntervalSeconds,
                        MaxAttempts = ScanRetryMaxAttempts,
                        BackoffRate = 1.0,
                    },
                    Transient(),
                },
                Next = approvalRequired ? "Approve" : afterVerification,
                Catch = catchAll,
            },
            // Registers a revision of the service's CURRENT task definition with one container's image
            // pinned to {registry}/{repository}@{digest}, and hands back only its ARN — the definition
            // itself holds secrets and never enters execution state.
            ["Prepare"] = new
            {
                Type = "Task",
                Resource = prepareFn,
                Parameters = payload,
                ResultPath = "$.deploy",
                Retry = new[] { Transient() },
                Next = "Deploy",
                Catch = catchAll,
            },
            ["Deploy"] = new
            {
                Type = "Task",
                // BY DIGEST. The revision names {repo}@sha256:…, never a tag — a tag is a pointer
                // someone with push rights can move, and the point of the whole design is that what
                // was verified is what runs.
                Resource = "arn:aws:states:::aws-sdk:ecs:updateService",
                Parameters = new Dictionary<string, object>
                {
                    ["Cluster.$"] = "$.target.cluster",
                    ["Service.$"] = "$.target.service",
                    ["TaskDefinition.$"] = "$.deploy.taskDefinitionArn",
                },
                // The full UpdateService response is a whole service description, events included;
                // execution state is capped, and the revision is all the evidence needs from it.
                ResultSelector = new Dictionary<string, object>
                {
                    ["taskDefinition.$"] = "$.Service.TaskDefinition",
                },
                ResultPath = "$.deployResult",
                // Before the Catch, so a throttled or 5xx UpdateService does not fail a verified — in prod,
                // approved — deploy outright (DecoupledCd.md §14.2). A retry names the same revision.
                Retry = new[]
                {
                    new
                    {
                        ErrorEquals = DeployTransientErrors,
                        IntervalSeconds = 2,
                        MaxAttempts = 3,
                        BackoffRate = 2.0,
                    },
                },
                Next = "VerifyRollout",
                Catch = catchAll,
            },
            // One look per invocation: the running tasks' containers[].imageDigest against the deployed
            // digest. §4.7: exit codes are not evidence, these reads are.
            ["VerifyRollout"] = new
            {
                Type = "Task",
                Resource = rolloutFn,
                Parameters = payload,
                ResultPath = "$.rollout",
                Retry = new[]
                {
                    new
                    {
                        ErrorEquals = new[] { nameof(RolloutStillRolling) },
                        IntervalSeconds = RolloutRetryIntervalSeconds,
                        MaxAttempts = RolloutRetryMaxAttempts,
                        BackoffRate = 1.0,
                    },
                    Transient(),
                },
                Next = "Record",
                Catch = catchAll,
            },
            // Writes the evidence VerifyRollout produced, once, under a conditional write. A function rather
            // than aws-sdk:s3:putObject so that a retry after a write that landed is success (RecordStep).
            ["Record"] = new
            {
                Type = "Task",
                Resource = recordFn,
                Parameters = payload,
                ResultPath = "$.recorded",
                Retry = new[] { Transient(), EvidenceWrite() },
                End = true,
                Catch = catchAll,
            },
            ["RecordFailure"] = new
            {
                Type = "Task",
                Resource = failureFn,
                Parameters = payload,
                ResultPath = "$.failure",
                Retry = new[] { Transient(), EvidenceWrite() },
                Next = "Failed",
                Catch = new[]
                {
                    new
                    {
                        ErrorEquals = new[] { "States.ALL" },
                        Next = "EvidenceNotWritten",
                        ResultPath = "$.recordFailureError",
                    },
                },
            },
            ["Failed"] = new
            {
                Type = "Fail",
                Error = "DeployFailed",
                Cause = "see the evidence bucket and this execution's history",
            },
            ["EvidenceNotWritten"] = new
            {
                Type = "Fail",
                Error = "EvidenceNotWritten",
                Cause = "the failure could not be written to the evidence bucket; this execution's history holds both errors",
            },
        };

        if (approvalRequired)
        {
            states["Approve"] = new
            {
                Type = "Task",
                Resource = "arn:aws:states:::sns:publish.waitForTaskToken",
                HeartbeatSeconds = heartbeatSeconds,
                Parameters = new Dictionary<string, object>
                {
                    // FROM CONFIG, NEVER FROM THE INPUT. A topic taken from execution input would let
                    // whoever started the execution choose who is asked to approve it.
                    ["TopicArn"] = approvalTopicArn
                        ?? throw new InvalidOperationException(
                            "approval is required but Pipeline.Approval.NotifyTopicArn is not set."),
                    ["Message.$"] = "States.Format('{} Task token: {}', $.verified.summary, $$.Task.Token)",
                },
                ResultPath = "$.approval",
                Next = afterVerification,
                Catch = catchAll,
            };
        }

        if (bundleFunctions is { } bundle)
        {
            // THE BRANCH ON THE CLASS VERIFY ESTABLISHED — the record's, never the input's. Verify refuses every class but
            // these two, so the default is a belt: a class with no branch is a failure, recorded as one.
            states["ByClass"] = new
            {
                Type = "Choice",
                Choices = new object[]
                {
                    new { Variable = "$.verified.class", StringEquals = "image", Next = "Prepare" },
                    new { Variable = "$.verified.class", StringEquals = "client", Next = "DeployBundle" },
                },
                Default = "NoBranchForClass",
            };
            states["NoBranchForClass"] = new
            {
                Type = "Pass",
                Result = new
                {
                    Error = nameof(DeployRefused),
                    Cause = "Verify passed a class this machine has no branch for; nothing was deployed.",
                },
                ResultPath = "$.error",
                Next = "RecordFailure",
            };

            // Mirrors the verified bundle into its app's bucket under the app's lease, then invalidates the app's path.
            // Every write is idempotent and the lease is re-entrant for the execution holding it, so the whole step may run
            // again: after an S3 or CloudFront fault, or once another execution's lease is released.
            states["DeployBundle"] = new
            {
                Type = "Task",
                Resource = bundle.DeployBundle,
                Parameters = payload,
                ResultPath = "$.deploy",
                Retry = new[]
                {
                    new
                    {
                        ErrorEquals = new[] { nameof(BundleDeployInProgress) },
                        IntervalSeconds = BundleLeaseRetryIntervalSeconds,
                        MaxAttempts = BundleLeaseRetryMaxAttempts,
                        BackoffRate = 1.0,
                    },
                    Transient(),
                    new
                    {
                        ErrorEquals = BundleTransientErrors.ToArray(),
                        IntervalSeconds = 5,
                        MaxAttempts = 3,
                        BackoffRate = 2.0,
                    },
                },
                Next = "VerifyBundle",
                Catch = catchAll,
            };

            // Reads every object under the app's prefix back — S3's SHA-256 and the headers — once its invalidations have
            // completed, and hands Record the evidence. §4.7: exit codes are not evidence, these reads are.
            states["VerifyBundle"] = new
            {
                Type = "Task",
                Resource = bundle.VerifyBundle,
                Parameters = payload,
                ResultPath = "$.rollout",
                Retry = new[]
                {
                    new
                    {
                        ErrorEquals = new[] { nameof(BundleInvalidationInProgress) },
                        IntervalSeconds = InvalidationRetryIntervalSeconds,
                        MaxAttempts = InvalidationRetryMaxAttempts,
                        BackoffRate = 1.0,
                    },
                    Transient(),
                    new
                    {
                        ErrorEquals = BundleTransientErrors.ToArray(),
                        IntervalSeconds = 5,
                        MaxAttempts = 3,
                        BackoffRate = 2.0,
                    },
                },
                Next = "Record",
                Catch = catchAll,
            };
        }

        return JsonSerializer.Serialize(new
        {
            Comment = bundleFunctions is null
                ? "lz decoupled-CD deployer — class 1 (service image) only. DecoupledCd.md §5."
                : "lz decoupled-CD deployer — class 1 (service image) and class 2 (client bundles). DecoupledCd.md §5.",
            StartAt = "Verify",
            States = states,
        }, Asl);
    }
}
