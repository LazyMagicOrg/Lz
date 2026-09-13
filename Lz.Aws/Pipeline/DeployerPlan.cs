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
/// <param name="Definition">The Amazon States Language document.</param>
/// <param name="ApprovalRequired">Whether the machine contains a human gate at all.</param>
/// <param name="Functions">The Lambda functions: the five the definition invokes, and the signature hook.</param>
/// <param name="HookInvokerRoleName">
/// The role ECS assumes to invoke the signature hook — the <c>roleArn</c> of a lifecycle hook.
/// </param>
/// <param name="HookInvokerPolicy">That role's grant: invoke the hook function, nothing else.</param>
/// <param name="ImageRepositories">The repositories images replicate into, hardened here before
/// anything may replicate into them — same names as the build registry's, as replication requires.</param>
/// <param name="ReplicationPermission">This registry's policy statement letting the build account
/// replicate into exactly those repositories, merged by Sid at apply.</param>
public sealed record PipelineDeployer(
    string StateMachineName,
    string RoleName,
    string RolePolicy,
    string DenyPolicy,
    string EvidenceStore,
    string Definition,
    bool ApprovalRequired,
    IReadOnlyList<DeployerFunction> Functions,
    string HookInvokerRoleName,
    string HookInvokerPolicy,
    IReadOnlyList<string> ImageRepositories,
    System.Text.Json.Nodes.JsonObject? ReplicationPermission);

/// <summary>The deployment packages the functions are built from, shipped inside Lz.Aws.</summary>
public static class DeployerPackages
{
    /// <summary>Verify, Prepare, VerifyRollout and RecordFailure: .NET code and the AWS SDK.</summary>
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

    /// <summary>Every handler, for the packaging test.</summary>
    public static IReadOnlyList<string> All => new[] { Verify, Prepare, VerifyRollout, Record, RecordFailure, SignatureHook };

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
/// <para>CLASS 1 ONLY, deliberately (P2 says so). Bundles and infrastructure classes add a Plan state
/// and a tooling container; they are not modelled here rather than being modelled badly.</para>
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

    /// <summary>How long VerifyRollout waits for a roll: 30 s × 40 attempts, twenty minutes.</summary>
    public const int RolloutRetryIntervalSeconds = 30;
    public const int RolloutRetryMaxAttempts = 40;

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
    /// An execution name for one deploy request.
    ///
    /// <para>THE NAME IS THE IDEMPOTENCY KEY, which is what absorbs S3's at-least-once, unordered
    /// delivery: a duplicate event produces the same name and the same input, and
    /// <c>StartExecution</c> is idempotent for a Standard workflow in exactly that case. A reused
    /// name with DIFFERENT input returns <c>ExecutionAlreadyExists</c> instead — an error worth
    /// getting, because it means two different things claimed one request id.</para>
    ///
    /// <para>Step Functions forbids <c>:</c> and <c>/</c> in execution names, which is why a raw
    /// digest can never be one — <c>sha256:…</c> contains the first. This refuses rather than
    /// sanitising: silently rewriting an id would break the idempotency the name exists to provide,
    /// since two ids could sanitise to one name.</para>
    /// </summary>
    public static string ExecutionName(string requestId, int attempt)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("execution name needs a request id.");

        if (attempt < 1)
            throw new InvalidOperationException(
                $"attempt must be 1 or greater; got {attempt}. Attempt 0 and attempt 1 would be two " +
                "names for one try.");

        foreach (var c in new[] { ':', '/', ' ', '\\', '?', '*', '<', '>', '|', '"', '#' })
        {
            if (requestId.Contains(c))
                throw new InvalidOperationException(
                    $"request id '{requestId}' contains '{c}', which Step Functions forbids in an " +
                    "execution name. Refusing rather than sanitising: two ids that sanitised to one " +
                    "name would collapse into a single execution and the second deploy would " +
                    "silently never run.");
        }

        var name = $"req-{requestId}-{attempt}";
        if (name.Length > 80)
            throw new InvalidOperationException(
                $"execution name '{name}' is {name.Length} characters; Step Functions allows 80.");

        return name;
    }

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
    ///   <item>a <c>.waitForTaskToken</c> task hands its task token to whoever must answer it.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> ContractGaps(string definition, string rolePolicy)
    {
        var gaps = new List<string>();
        using var def = JsonDocument.Parse(definition);
        using var policy = JsonDocument.Parse(rolePolicy);
        var states = def.RootElement.GetProperty("States").EnumerateObject().ToList();

        var produced = new HashSet<string>(DeployerInput.InputFields, StringComparer.Ordinal);
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

        foreach (var s in states)
        {
            var type = s.Value.GetProperty("Type").GetString();
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
    public static PipelineDeployer Plan(SystemConfig config, string? accountId = null)
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
        var hook = $"{sk}-{env}-signature-hook";

        string FnArn(string name) => $"arn:aws:lambda:{region}:{acct}:function:{name}";

        var functions = new List<DeployerFunction>
        {
            new(verify, DeployerHandlers.Verify, $"{verify}-fn",
                Combine(Logs(region, acct, verify), VerifyGrants(region, acct, buildRecordStore, imageRepositories)),
                new Dictionary<string, string>
                {
                    [DeployerEnvironment.Classes] = DeployerEnvironment.Join(p.Classes ?? new List<string>()),
                    [DeployerEnvironment.ScanBlockOn] = DeployerEnvironment.Join(p.Scan?.BlockOn ?? new List<string>()),
                    [DeployerEnvironment.BuildRecordStore] = buildRecordStore,
                    [DeployerEnvironment.ImageRepositories] = DeployerEnvironment.Join(imageRepositories),
                },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),

            new(prepare, DeployerHandlers.Prepare, $"{prepare}-fn",
                Combine(Logs(region, acct, prepare), PrepareGrants(region, acct, sk)),
                new Dictionary<string, string> { [DeployerEnvironment.Registry] = registry },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),

            new(rollout, DeployerHandlers.VerifyRollout, $"{rollout}-fn",
                Combine(Logs(region, acct, rollout), RolloutGrants(region, acct)),
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

        var invoked = functions.Where(f => f.InvokedByStateMachine).Select(f => FnArn(f.Name)).ToList();

        return new PipelineDeployer(
            StateMachineName: $"{sk}-{env}-deployer",
            RoleName: $"{sk}-{env}-deployer",
            RolePolicy: RolePolicyFor(region, acct, sk, invoked,
                                      approvalRequired ? p.Approval?.NotifyTopicArn : null),
            DenyPolicy: DenyPolicyFor(),
            EvidenceStore: evidence,
            Definition: DefinitionFor(approvalRequired, FnArn(verify), FnArn(prepare),
                                      FnArn(rollout), FnArn(record), FnArn(failure),
                                      p.Approval?.HeartbeatSeconds ?? 86400, p.Approval?.NotifyTopicArn),
            ApprovalRequired: approvalRequired,
            Functions: functions,
            HookInvokerRoleName: $"{sk}-{env}-signature-hook-invoker",
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
                : CrossAccount.ReplicationPermission(artifactAccount, region, accountId, imageRepositories));
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
        string region, string accountId, string sk,
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
                Resource = $"arn:aws:ecs:{region}:{accountId}:service/*",
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
        string region, string accountId, string buildRecordStore, IReadOnlyList<string> repositories) => new object[]
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
            Resource = $"arn:aws:ecs:{region}:{accountId}:service/*",
        },
    };

    private static object[] PrepareGrants(string region, string accountId, string sk) => new object[]
    {
        new
        {
            Sid = "ReadTheCurrentRevision",
            Effect = "Allow",
            Action = new[] { "ecs:DescribeServices" },
            Resource = $"arn:aws:ecs:{region}:{accountId}:service/*",
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

    private static object[] RolloutGrants(string region, string accountId) => new object[]
    {
        new
        {
            Sid = "WatchTheRoll",
            Effect = "Allow",
            Action = new[] { "ecs:DescribeServices" },
            Resource = $"arn:aws:ecs:{region}:{accountId}:service/*",
        },
        new
        {
            Sid = "ReadWhatIsRunning",
            Effect = "Allow",
            Action = new[] { "ecs:ListTasks", "ecs:DescribeTasks" },
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
    private static string DefinitionFor(
        bool approvalRequired, string verifyFn, string prepareFn, string rolloutFn,
        string recordFn, string failureFn, int heartbeatSeconds, string? approvalTopicArn)
    {
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
                    new
                    {
                        ErrorEquals = new[] { nameof(ScanNotYetAvailable) },
                        IntervalSeconds = ScanRetryIntervalSeconds,
                        MaxAttempts = ScanRetryMaxAttempts,
                        BackoffRate = 1.0,
                    },
                    Transient(),
                },
                Next = approvalRequired ? "Approve" : "Prepare",
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
                Next = "Prepare",
                Catch = catchAll,
            };
        }

        return JsonSerializer.Serialize(new
        {
            Comment = "lz decoupled-CD deployer — class 1 (service image) only. DecoupledCd.md §5.",
            StartAt = "Verify",
            States = states,
        }, Asl);
    }
}
