using System.Text.Encodings.Web;
using System.Text.Json;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

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
/// console rather than buried in the middle of a grant.
/// </param>
/// <param name="EvidenceStore">Versioned bucket the Record state writes to.</param>
/// <param name="Definition">The Amazon States Language document.</param>
/// <param name="ApprovalRequired">Whether the machine contains a human gate at all.</param>
/// <param name="Functions">Lambda functions the definition references, which must exist before it runs.</param>
public sealed record PipelineDeployer(
    string StateMachineName,
    string RoleName,
    string RolePolicy,
    string DenyPolicy,
    string EvidenceStore,
    string Definition,
    bool ApprovalRequired,
    IReadOnlyList<string> Functions);

/// <summary>
/// Decides what the in-account deployer looks like, as a pure function of config.
///
/// <para>STAGE A OF P2: this DEFINES and creates nothing. The state-machine document, the role
/// policies and the execution-name rule are all decisions, and decisions are the part of an
/// applier that can be asserted before anything exists — which is not a stylistic preference here
/// but the lesson of 2026-09-12, when every defect landed in AWS-calling code written from memory
/// and none in the planner beside it.</para>
///
/// <para>CLASS 1 ONLY, deliberately (P2 says so). An image roll is <c>RegisterTaskDefinition</c> +
/// <c>UpdateService</c>, which Step Functions can perform through SDK integrations with no tooling
/// image — so this stage needs neither the deployer image nor the config bundle, both of which are
/// P3. Bundles and infrastructure classes add a Plan state and a tooling container; they are not
/// modelled here rather than being modelled badly.</para>
/// </summary>
public static class DeployerPlanner
{
    private static readonly JsonSerializerOptions Asl = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
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

        // The evidence bucket lives in the TARGET account, unlike the three build-account stores:
        // it records what happened HERE, and §4.7 wants it replicated rather than shared.
        var evidence = $"{sk}-{env}-deploy-evidence-{config.SystemSuffix}";

        var verify = $"{sk}-{env}-deployer-verify";
        var rollout = $"{sk}-{env}-deployer-verify-rollout";
        var failure = $"{sk}-{env}-deployer-record-failure";

        return new PipelineDeployer(
            StateMachineName: $"{sk}-{env}-deployer",
            RoleName: $"{sk}-{env}-deployer",
            RolePolicy: RolePolicyFor(region, acct, sk, env, evidence),
            DenyPolicy: DenyPolicyFor(),
            EvidenceStore: evidence,
            Definition: DefinitionFor(region, acct, evidence, approvalRequired, verify, rollout, failure,
                                      p.Approval?.HeartbeatSeconds ?? 86400),
            ApprovalRequired: approvalRequired,
            Functions: new[] { verify, rollout, failure });
    }

    /// <summary>
    /// What the deployer may do for a CLASS 1 roll, and nothing more.
    ///
    /// <para><c>iam:PassRole</c> is the statement to read twice: it is scoped to the service's task
    /// and execution roles by name. Unscoped, it is privilege escalation — a principal that may
    /// pass any role can register a task definition running as any role in the account.</para>
    /// </summary>
    private static string RolePolicyFor(
        string region, string accountId, string sk, string env, string evidence)
    {
        var statements = new object[]
        {
            new
            {
                Sid = "ReadTheRegistryAndItsSignatures",
                Effect = "Allow",
                Action = new[]
                {
                    "ecr:GetAuthorizationToken", "ecr:BatchGetImage",
                    "ecr:GetDownloadUrlForLayer", "ecr:DescribeImages",
                    "ecr:BatchGetRepositoryScanningConfiguration", "ecr:DescribeImageScanFindings",
                },
                Resource = "*",
            },
            new
            {
                Sid = "RollTheService",
                Effect = "Allow",
                Action = new[]
                {
                    "ecs:RegisterTaskDefinition", "ecs:UpdateService",
                    "ecs:DescribeServices", "ecs:DescribeTasks", "ecs:ListTasks",
                    "ecs:DescribeTaskDefinition",
                },
                Resource = "*",
            },
            new
            {
                // SCOPED BY NAME, and this is the one that would be an escalation if it were not.
                Sid = "PassOnlyTheServicesOwnRoles",
                Effect = "Allow",
                Action = new[] { "iam:PassRole" },
                Resource = new[]
                {
                    $"arn:aws:iam::{accountId}:role/{sk}-{env}-*-task",
                    $"arn:aws:iam::{accountId}:role/{sk}-{env}-*-execution",
                },
                Condition = new Dictionary<string, object>
                {
                    ["StringEquals"] = new Dictionary<string, object>
                    {
                        ["iam:PassedToService"] = "ecs-tasks.amazonaws.com",
                    },
                },
            },
            new
            {
                // Write-once evidence. No delete, exactly as the build roles hold none.
                Sid = "WriteEvidence",
                Effect = "Allow",
                Action = new[] { "s3:PutObject" },
                Resource = $"arn:aws:s3:::{evidence}/*",
            },
        };

        return JsonSerializer.Serialize(
            new { Version = "2012-10-17", Statement = statements }, Asl);
    }

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
    /// The state machine: Verify → [Approve] → Deploy → VerifyRollout → Record.
    ///
    /// <para>NO PLAN STATE, because class 1 has nothing to preview — Plan is the Pulumi preview
    /// that classes 5–7 need (§4.5). Adding an empty one would imply a check that is not happening.</para>
    ///
    /// <para>THE APPROVAL STATE IS BAKED IN OR ABSENT, not present-but-skipped. Dev sets
    /// <c>Required: false</c> and gets a machine with no human gate; prod gets one with a
    /// <c>.waitForTaskToken</c> that cannot be bypassed. A machine carrying a dead branch reads as
    /// though approval might happen, and the plan output would have to be trusted over the
    /// document.</para>
    ///
    /// <para>EVERY STATE CATCHES. A failure anywhere records what happened and marks the request
    /// decided — an execution that died silently would leave a request that looks pending forever,
    /// and the reconciler would keep finding it.</para>
    /// </summary>
    private static string DefinitionFor(
        string region, string accountId, string evidence, bool approvalRequired,
        string verifyFn, string rolloutFn, string failureFn, int heartbeatSeconds)
    {
        string Fn(string name) => $"arn:aws:lambda:{region}:{accountId}:function:{name}";

        var catchAll = new object[]
        {
            new
            {
                ErrorEquals = new[] { "States.ALL" },
                Next = "RecordFailure",
                ResultPath = "$.error",
            },
        };

        var states = new Dictionary<string, object>
        {
            // Loads the build record, checks class, builtFrom, lane, the -g<hex> discriminator,
            // resolves the identity, verifies the Notation signature and reads the scan verdict.
            // Composite, so a Lambda rather than an SDK integration.
            ["Verify"] = new
            {
                Type = "Task",
                Resource = Fn(verifyFn),
                Next = approvalRequired ? "Approve" : "Deploy",
                Catch = catchAll,
            },
            ["Deploy"] = new
            {
                Type = "Task",
                // BY DIGEST. The task definition names {repo}@sha256:…, never a tag — a tag is a
                // pointer someone with push rights can move, and the point of the whole design is
                // that what was verified is what runs.
                Resource = "arn:aws:states:::aws-sdk:ecs:updateService",
                Parameters = new Dictionary<string, object>
                {
                    ["Cluster.$"] = "$.target.cluster",
                    ["Service.$"] = "$.target.service",
                    ["TaskDefinition.$"] = "$.deploy.taskDefinitionArn",
                },
                ResultPath = "$.deployResult",
                Next = "VerifyRollout",
                Catch = catchAll,
            },
            // Waits for steady state and reads containers[].imageDigest from the RUNNING task,
            // failing unless it equals what was deployed. §4.7: exit codes are not evidence, these
            // reads are.
            ["VerifyRollout"] = new
            {
                Type = "Task",
                Resource = Fn(rolloutFn),
                ResultPath = "$.rollout",
                Next = "Record",
                Catch = catchAll,
            },
            ["Record"] = new
            {
                Type = "Task",
                Resource = "arn:aws:states:::aws-sdk:s3:putObject",
                Parameters = new Dictionary<string, object>
                {
                    ["Bucket"] = evidence,
                    ["Key.$"] = "$.evidence.key",
                    ["Body.$"] = "$.evidence.body",
                    ["ContentType"] = "application/json",
                    // Conditional write: evidence is written once and can never be replaced.
                    ["IfNoneMatch"] = "*",
                },
                End = true,
            },
            ["RecordFailure"] = new
            {
                Type = "Task",
                Resource = Fn(failureFn),
                Next = "Failed",
            },
            ["Failed"] = new
            {
                Type = "Fail",
                Error = "DeployFailed",
                Cause = "see the evidence bucket and this execution's history",
            },
        };

        if (approvalRequired)
        {
            states["Approve"] = new
            {
                Type = "Task",
                Resource = "arn:aws:states:::sns:publish.waitForTaskToken",
                // The approver's SendTaskSuccess output MUST repeat the identities; Record fails
                // the execution if it differs (§4.5). That comparison is the Lambda's job, not the
                // state machine's.
                HeartbeatSeconds = heartbeatSeconds,
                Parameters = new Dictionary<string, object>
                {
                    ["TopicArn.$"] = "$.approval.topicArn",
                    ["Message.$"] = "$.approval.summary",
                },
                ResultPath = "$.approval.result",
                Next = "Deploy",
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
