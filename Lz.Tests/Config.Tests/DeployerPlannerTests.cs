using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The in-account deployer's plan (DecoupledCd.md §5, punchlist P2 stages A and C).
///
/// <para>Every assertion here is about a DOCUMENT — an IAM policy or a state-machine definition —
/// and both fail in ways no applier test would catch: an unscoped PassRole is an escalation nobody
/// notices, and a missing Catch is an execution that dies leaving a request pending forever.</para>
///
/// <para>AND SHAPE IS NOT ENOUGH, which stage C proved: the stage-A definition passed every shape
/// assertion in this file and could not have completed one execution. The contract-gap tests below
/// read data flow and permission instead, and are held against that definition as deployed.</para>
/// </summary>
public class DeployerPlannerTests
{
    private const string TargetAccount = "503947800380";
    private const string BuildAccount = "147440642635";

    private static SystemConfig Config(bool approvalRequired) => new()
    {
        SystemKey = "scu", Environment = "dev", Region = "us-west-2", SystemSuffix = "4df6-b9c6",
        Rollback = new RollbackConfig { PinImageDigest = true },
        Pipeline = new PipelineConfig
        {
            Enabled = true,
            ArtifactAccountId = BuildAccount,
            TargetAccountId = TargetAccount,
            Classes = new List<string> { "image" },
            Repositories = new List<PipelineRepositoryConfig>
            {
                new() { Repo = "Scutara/ScutaraService", Class = "image",
                        Artifacts = new List<string> { "aiphost" } },
                new() { Repo = "Scutara/ScutaraSellerApp", Class = "client" },
            },
            Registry = new PipelineRegistryConfig { RepositoryNaming = "neutral" },
            Scan = new PipelineScanConfig { BlockOn = new List<string> { "CRITICAL" } },
            Approval = approvalRequired
                ? new PipelineApprovalConfig
                  { Required = true, NotifyTopicArn = "arn:aws:sns:us-west-2:1:deploys" }
                : new PipelineApprovalConfig { Required = false },
        },
    };

    private static PipelineDeployer Plan(bool approvalRequired = false) => DeployerPlanner.Plan(Config(approvalRequired), TargetAccount);

    private static JsonElement Definition(bool approvalRequired)
        => JsonDocument.Parse(Plan(approvalRequired).Definition).RootElement;

    private static DeployerFunction Function(PipelineDeployer plan, string handler)
        => plan.Functions.Single(f => f.Handler == handler);

    /// <summary>
    /// Does an IAM resource pattern match an ARN? IAM wildcards are <c>*</c> (any run of
    /// characters) and <c>?</c> (one), which is not regex — so the pattern is translated rather
    /// than used directly.
    /// </summary>
    private static bool Matches(string pattern, string arn) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            arn,
            "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$");

    private static IEnumerable<JsonElement> Statements(string policy)
        => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Select(x => x.GetString()!)
        : new[] { e.GetString()! };

    // ---------------------------------------------------------------------------------------
    //  Execution naming — the idempotency key
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheExecutionNameIsRequestIdAndAttempt()
    {
        Assert.Equal("req-abc123-1", DeployerPlanner.ExecutionName("abc123", 1));
    }

    [Theory]
    [InlineData("sha256:b330844a")]   // the obvious mistake: a raw digest
    [InlineData("a/b")]
    [InlineData("with space")]
    [InlineData("q?x")]
    public void AnIdStepFunctionsWouldRejectIsRefused_NotSanitised(string requestId)
    {
        // Sanitising would be worse than failing: two ids that cleaned up to one name would collapse
        // into a single execution, and the second deploy would silently never run. The name IS the
        // idempotency key, so mangling it defeats the mechanism it exists for.
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeployerPlanner.ExecutionName(requestId, 1));

        Assert.Contains("Refusing rather than sanitising", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AttemptMustStartAtOne(int attempt)
    {
        Assert.Throws<InvalidOperationException>(() => DeployerPlanner.ExecutionName("abc", attempt));
    }

    [Fact]
    public void ARetryGetsADifferentName_SoItIsNotSwallowedAsADuplicate()
    {
        Assert.NotEqual(
            DeployerPlanner.ExecutionName("abc123", 1),
            DeployerPlanner.ExecutionName("abc123", 2));
    }

    [Fact]
    public void AnOverlongNameIsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DeployerPlanner.ExecutionName(new string('x', 90), 1));

        Assert.Contains("80", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    //  The roles — where an unscoped grant would be an escalation
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void PassRoleIsScopedToTheServicesOwnRoles_WhereverItIsGranted()
    {
        // THE ONE THAT MATTERS. A principal that may pass ANY role can register a task definition
        // running as any role in the account — privilege escalation dressed as a deploy. Granted to
        // Prepare, which registers the revision, and kept on the state machine's role.
        var plan = Plan();

        foreach (var policy in new[] { plan.RolePolicy, Function(plan, DeployerHandlers.Prepare).Policy })
        {
            var pass = Statements(policy)
                .Single(s => Strings(s.GetProperty("Action")).Contains("iam:PassRole"));

            var resources = Strings(pass.GetProperty("Resource")).ToList();
            Assert.All(resources, r => Assert.DoesNotContain("role/*", r));

            // PINNED AGAINST THE ROLES THAT ACTUALLY EXIST, listed from scu-dev on 2026-09-12, because
            // the first version of this pattern was derived from the convention the names look like
            // they follow and matched NONE of them.
            Assert.All(
                new[] { "scu-mp-aiphost-task-0114517", "scu-mp-aiphost-exec-7a3f57c" },
                role => Assert.True(
                    resources.Any(r => Matches(r, $"arn:aws:iam::{TargetAccount}:role/{role}")),
                    $"no Resource pattern matches the live role {role}"));

            // ... and cannot pass roles that are not ECS task roles — including the functions' own.
            Assert.All(
                new[] { "scu-dev-deployer", "scu-website-ci", "scu-e2e-ci", "scu-tailscale-role",
                        "scu-dev-deployer-prepare-fn", "scu-dev-signature-hook-invoker" },
                role => Assert.False(
                    resources.Any(r => Matches(r, $"arn:aws:iam::{TargetAccount}:role/{role}")),
                    $"the deployer could pass {role}, which is not an ECS task role"));

            Assert.Equal("ecs-tasks.amazonaws.com",
                pass.GetProperty("Condition").GetProperty("StringEquals")
                    .GetProperty("iam:PassedToService").GetString());
        }
    }

    [Fact]
    public void OnlyPrepareAndTheStateMachine_MayPassARole()
    {
        var plan = Plan();
        var passers = plan.Functions
            .Where(f => Statements(f.Policy).Any(s => Strings(s.GetProperty("Action")).Contains("iam:PassRole")))
            .Select(f => f.Handler)
            .ToList();

        Assert.Equal(new[] { DeployerHandlers.Prepare }, passers);
    }

    [Fact]
    public void NoDeployerPolicyCanDeleteAnything()
    {
        var plan = Plan(approvalRequired: true);

        foreach (var policy in plan.Functions.Select(f => f.Policy).Append(plan.RolePolicy).Append(plan.HookInvokerPolicy))
            Assert.DoesNotContain("Delete", policy, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheSelfRewriteDenyIsAnExplicitDeny_AndSeparate()
    {
        // Separate from the grant so it is visible rather than buried, and a Deny because an
        // absence of grant can be undone by a later Allow that nobody connects to this decision.
        var stmt = Statements(Plan().DenyPolicy).Single();
        Assert.Equal("Deny", stmt.GetProperty("Effect").GetString());
        Assert.Equal(PipelineBootstrapPlanner.SelfRewriteDenied, Strings(stmt.GetProperty("Action")));
    }

    [Fact]
    public void EveryFunctionHasItsOwnRole()
    {
        var plan = Plan();

        Assert.Equal(plan.Functions.Count, plan.Functions.Select(f => f.RoleName).Distinct().Count());
        Assert.DoesNotContain(plan.RoleName, plan.Functions.Select(f => f.RoleName));
    }

    [Fact]
    public void TheStateMachineMayInvokeItsFunctions_ButNotTheHook()
    {
        // ECS invokes the hook, through its own role. A state machine that could invoke it could answer
        // an admission check on ECS's behalf.
        var plan = Plan();
        var invokable = Statements(plan.RolePolicy)
            .Where(s => Strings(s.GetProperty("Action")).Contains("lambda:InvokeFunction"))
            .SelectMany(s => Strings(s.GetProperty("Resource")))
            .ToList();

        foreach (var fn in plan.Functions)
        {
            var arn = $"arn:aws:lambda:us-west-2:{TargetAccount}:function:{fn.Name}";
            Assert.Equal(fn.InvokedByStateMachine, invokable.Any(p => Matches(p, arn)));
        }
    }

    [Fact]
    public void TheHookInvoker_MayInvokeTheHookAndNothingElse()
    {
        var plan = Plan();
        var hook = Function(plan, DeployerHandlers.SignatureHook);
        var stmt = Statements(plan.HookInvokerPolicy).Single();

        Assert.Equal(new[] { "lambda:InvokeFunction" }, Strings(stmt.GetProperty("Action")));
        Assert.Equal(new[] { $"arn:aws:lambda:us-west-2:{TargetAccount}:function:{hook.Name}" }, Strings(stmt.GetProperty("Resource")));
    }

    [Fact]
    public void VerifyReadsImageRecordsOnly()
    {
        var reads = Statements(Function(Plan(), DeployerHandlers.Verify).Policy)
            .Where(s => Strings(s.GetProperty("Action")).Contains("s3:GetObject"))
            .SelectMany(s => Strings(s.GetProperty("Resource")))
            .ToList();

        Assert.Equal(new[] { "arn:aws:s3:::scu-build-records-4df6-b9c6/image/*" }, reads);
    }

    [Fact]
    public void VerifyReadsScanFindings_OnItsRepositories_AndStartsNothing()
    {
        // DescribeImageScanFindings is where the current basic scanning puts results — with DescribeImages
        // alone, Verify refused every image at [scan] while each had a COMPLETE scan. And it stays read-only:
        // Verify waits for scan-on-push instead of starting a scan that would race it.
        var ecr = Statements(Function(Plan(), DeployerHandlers.Verify).Policy)
            .SelectMany(s => Strings(s.GetProperty("Action"))
                .Select(a => (Action: a, Resources: Strings(s.GetProperty("Resource")).ToList())))
            .Where(x => x.Action.StartsWith("ecr:", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(new[] { "ecr:DescribeImageScanFindings", "ecr:DescribeImages" },
            ecr.Select(x => x.Action).OrderBy(a => a, StringComparer.Ordinal));
        Assert.All(ecr, x => Assert.Equal(
            new[] { "arn:aws:ecr:us-west-2:503947800380:repository/scu-4df6-b9c6-aiphost" }, x.Resources));
    }

    [Fact]
    public void PrepareMayTagOnlyWhileRegistering()
    {
        var tag = Statements(Function(Plan(), DeployerHandlers.Prepare).Policy)
            .Single(s => Strings(s.GetProperty("Action")).Contains("ecs:TagResource"));

        Assert.Equal("RegisterTaskDefinition",
            tag.GetProperty("Condition").GetProperty("StringEquals").GetProperty("ecs:CreateAction").GetString());
    }

    [Fact]
    public void RecordFailureMayWriteOnlyFailures_AndTheMachineOnlyDeploys()
    {
        var plan = Plan();

        Assert.Contains($"{plan.EvidenceStore}/failures/*", Function(plan, DeployerHandlers.RecordFailure).Policy);
        Assert.Contains($"{plan.EvidenceStore}/deploys/*", plan.RolePolicy);
        Assert.DoesNotContain("/failures/", plan.RolePolicy);
    }

    // ---------------------------------------------------------------------------------------
    //  Configuration the handlers read
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void VerifysEnvironment_IsExactlyWhatItsHandlerReads()
    {
        var env = Function(Plan(), DeployerHandlers.Verify).Environment;
        var settings = VerifySettings.Read(n => env.GetValueOrDefault(n));

        Assert.Equal(new[] { "image" }, settings.Classes);
        Assert.Equal(new[] { "CRITICAL" }, settings.ScanBlockOn);
        Assert.Equal("scu-build-records-4df6-b9c6", settings.BuildRecordStore);
        // Image repositories only — SellerApp builds a client bundle, which no image can come from.
        Assert.Equal(new[] { "scu-4df6-b9c6-aiphost" }, settings.ImageRepositories);
    }

    [Fact]
    public void TheHooksEnvironment_ProducesAValidTrustPolicy_ForTheBuildAccountsImageProfile()
    {
        var env = Function(Plan(), DeployerHandlers.SignatureHook).Environment;
        var settings = HookSettings.Read(n => env.GetValueOrDefault(n));

        // The profile lives in the BUILD account; the images it admits live in THIS account's registry.
        Assert.Equal(new[] { $"arn:aws:signer:us-west-2:{BuildAccount}:/signing-profiles/scu_build_ci_scutaraservice" },
            settings.TrustedProfiles);
        Assert.Equal(new[] { $"{TargetAccount}.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-aiphost" },
            settings.RegistryScopes);

        SignatureHook.TrustPolicy(settings.TrustedProfiles, settings.RegistryScopes); // no throw
    }

    [Fact]
    public void PreparesRegistry_IsThisAccounts()
    {
        Assert.Equal($"{TargetAccount}.dkr.ecr.us-west-2.amazonaws.com",
            Function(Plan(), DeployerHandlers.Prepare).Environment[DeployerEnvironment.Registry]);
    }

    [Fact]
    public void WithoutTheBuildAccount_ThePlanIsRefused()
    {
        var c = Config(false);
        c.Pipeline!.ArtifactAccountId = null;

        Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(c, TargetAccount));
    }

    [Fact]
    public void WithoutAnImageRepository_ThePlanIsRefused()
    {
        var c = Config(false);
        c.Pipeline!.Repositories!.RemoveAll(r => r.Class == "image");

        Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(c, TargetAccount));
    }

    [Fact]
    public void EveryHandlerThePlanUses_IsOneThePackageTestResolves()
    {
        Assert.All(Plan().Functions, f => Assert.Contains(f.Handler, DeployerHandlers.All));
    }

    // ---------------------------------------------------------------------------------------
    //  The state machine — shape
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ItStartsAtVerify_SoNothingDeploysUnverified()
    {
        Assert.Equal("Verify", Definition(false).GetProperty("StartAt").GetString());
    }

    [Fact]
    public void EveryStateThatCanFail_Catches()
    {
        // A state without a Catch dies silently and leaves the request looking pending forever —
        // and the reconciler would keep finding it and starting it again.
        var states = Definition(true).GetProperty("States");

        foreach (var name in new[] { "Verify", "Approve", "Prepare", "Deploy", "VerifyRollout" })
        {
            var s = states.GetProperty(name);
            Assert.True(s.TryGetProperty("Catch", out var c), $"{name} has no Catch");
            Assert.Equal("RecordFailure", c[0].GetProperty("Next").GetString());
        }
    }

    [Fact]
    public void WithoutApproval_ThereIsNoApprovalState_NotADeadOne()
    {
        var states = Definition(false).GetProperty("States");

        Assert.False(states.TryGetProperty("Approve", out _));
        Assert.Equal("Prepare", states.GetProperty("Verify").GetProperty("Next").GetString());
    }

    [Fact]
    public void WithApproval_TheGateIsWaitForTaskToken_BeforeAnythingIsWritten()
    {
        // Prepare registers a revision — a write. It comes after the gate, so a rejected request leaves
        // nothing behind.
        var states = Definition(true).GetProperty("States");
        var approve = states.GetProperty("Approve");

        Assert.Equal("Approve", states.GetProperty("Verify").GetProperty("Next").GetString());
        Assert.Contains("waitForTaskToken", approve.GetProperty("Resource").GetString());
        Assert.Equal("Prepare", approve.GetProperty("Next").GetString());
        Assert.Equal("Deploy", states.GetProperty("Prepare").GetProperty("Next").GetString());
    }

    [Fact]
    public void TheApprovalTopicComesFromConfig_AndTheMessageCarriesTheToken()
    {
        var parameters = Definition(true).GetProperty("States").GetProperty("Approve").GetProperty("Parameters");

        Assert.Equal("arn:aws:sns:us-west-2:1:deploys", parameters.GetProperty("TopicArn").GetString());
        Assert.False(parameters.TryGetProperty("TopicArn.$", out _));
        Assert.Contains("$$.Task.Token", parameters.GetProperty("Message.$").GetString());
    }

    [Fact]
    public void TheApprovalHeartbeatComesFromConfig()
    {
        var c = Config(true);
        c.Pipeline!.Approval!.HeartbeatSeconds = 3600;

        using var doc = JsonDocument.Parse(DeployerPlanner.Plan(c, TargetAccount).Definition);
        Assert.Equal(3600, doc.RootElement.GetProperty("States").GetProperty("Approve")
            .GetProperty("HeartbeatSeconds").GetInt32());
    }

    [Fact]
    public void ThereIsNoPlanState_BecauseClassOneHasNothingToPreview()
    {
        Assert.False(Definition(false).GetProperty("States").TryGetProperty("Plan", out _));
    }

    [Fact]
    public void DeployNamesTheRevisionPrepareRegistered_NeverATag()
    {
        var deploy = Definition(false).GetProperty("States").GetProperty("Deploy");

        Assert.Equal("$.deploy.taskDefinitionArn", deploy.GetProperty("Parameters").GetProperty("TaskDefinition.$").GetString());
        Assert.Equal("$.deploy", Definition(false).GetProperty("States").GetProperty("Prepare").GetProperty("ResultPath").GetString());
        Assert.DoesNotContain(":latest", deploy.ToString());
    }

    [Fact]
    public void DeployKeepsOnlyTheRevisionFromTheUpdateServiceResponse()
    {
        // A whole service description, events included, would eat the execution's state budget.
        var selector = Definition(false).GetProperty("States").GetProperty("Deploy").GetProperty("ResultSelector");

        Assert.Equal("taskDefinition.$", Assert.Single(selector.EnumerateObject()).Name);
    }

    [Fact]
    public void RecordWritesTheEvidenceVerifyRolloutProduced_UnderAConditionalWrite()
    {
        var record = Definition(false).GetProperty("States").GetProperty("Record");
        var parameters = record.GetProperty("Parameters");

        Assert.Equal("*", parameters.GetProperty("IfNoneMatch").GetString());
        Assert.Equal("$.rollout.evidence.key", parameters.GetProperty("Key.$").GetString());
        Assert.True(record.GetProperty("End").GetBoolean());
    }

    [Fact]
    public void TheFailurePathEndsInAFailState_NotASuccess()
    {
        // An execution that recorded a failure and then succeeded would report green for a deploy
        // that did not happen.
        var states = Definition(false).GetProperty("States");

        Assert.Equal("Failed", states.GetProperty("RecordFailure").GetProperty("Next").GetString());
        Assert.Equal("Fail", states.GetProperty("Failed").GetProperty("Type").GetString());
    }

    [Fact]
    public void EveryLambdaState_ReceivesTheStateAndTheExecutionName()
    {
        var states = Definition(true).GetProperty("States");

        foreach (var name in new[] { "Verify", "Prepare", "VerifyRollout", "RecordFailure" })
        {
            var p = states.GetProperty(name).GetProperty("Parameters");
            Assert.Equal("$", p.GetProperty("state.$").GetString());
            Assert.Equal("$$.Execution.Name", p.GetProperty("executionName.$").GetString());
        }
    }

    [Fact]
    public void WaitingIsARetryOnANamedError_AndItIsBounded()
    {
        var states = Definition(false).GetProperty("States");

        void AssertRetry(string state, Type error, int interval, int attempts)
        {
            var retry = states.GetProperty(state).GetProperty("Retry").EnumerateArray()
                .Single(r => r.GetProperty("ErrorEquals").EnumerateArray().Any(e => e.GetString() == error.Name));
            Assert.Equal(interval, retry.GetProperty("IntervalSeconds").GetInt32());
            Assert.Equal(attempts, retry.GetProperty("MaxAttempts").GetInt32());
            Assert.Equal(1.0, retry.GetProperty("BackoffRate").GetDouble());
        }

        AssertRetry("Verify", typeof(ScanNotYetAvailable), DeployerPlanner.ScanRetryIntervalSeconds, DeployerPlanner.ScanRetryMaxAttempts);
        AssertRetry("VerifyRollout", typeof(RolloutStillRolling), DeployerPlanner.RolloutRetryIntervalSeconds, DeployerPlanner.RolloutRetryMaxAttempts);

        // A REFUSAL IS NEVER RETRIED. Nothing about it changes by asking again.
        Assert.DoesNotContain(nameof(DeployRefused), Plan().Definition);
        Assert.DoesNotContain(nameof(RolloutNotDeployed), Plan().Definition);
    }

    [Fact]
    public void EveryFunctionTheDefinitionInvokes_IsPlanned_AndTheHookIsNotInvokedByIt()
    {
        var plan = Plan(approvalRequired: true);

        foreach (var fn in plan.Functions)
            Assert.Equal(fn.InvokedByStateMachine, plan.Definition.Contains($":function:{fn.Name}\""));
    }

    [Fact]
    public void TheEvidenceStoreIsPerEnvironment()
    {
        // Unlike the three build-account stores, evidence records what happened in THIS account.
        var dev = Plan().EvidenceStore;

        var prodConfig = Config(false);
        prodConfig.Environment = "prod";
        var prod = DeployerPlanner.Plan(prodConfig, "982408502448").EvidenceStore;

        Assert.NotEqual(dev, prod);
        Assert.Contains("dev", dev);
    }

    // ---------------------------------------------------------------------------------------
    //  The state machine — data flow and permission
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThePlannedMachine_HasNoContractGaps(bool approvalRequired)
    {
        var plan = Plan(approvalRequired);

        Assert.Empty(DeployerPlanner.ContractGaps(plan.Definition, plan.RolePolicy));
    }

    /// <summary>
    /// The definition and role policy AS DEPLOYED to scu-dev by stage B on 2026-09-12, read back with
    /// <c>aws stepfunctions describe-state-machine</c> and <c>aws iam get-role-policy</c>. Verbatim
    /// except the Comment, whose non-ASCII the Windows console mangled on the way out.
    /// </summary>
    private const string StageADefinition = """
        {
          "Comment": "lz decoupled-CD deployer - class 1 (service image) only.",
          "StartAt": "Verify",
          "States": {
            "Verify": {
              "Type": "Task",
              "Resource": "arn:aws:lambda:us-west-2:503947800380:function:scu-dev-deployer-verify",
              "Next": "Deploy",
              "Catch": [ { "ErrorEquals": [ "States.ALL" ], "Next": "RecordFailure", "ResultPath": "$.error" } ]
            },
            "Deploy": {
              "Type": "Task",
              "Resource": "arn:aws:states:::aws-sdk:ecs:updateService",
              "Parameters": {
                "Cluster.$": "$.target.cluster",
                "Service.$": "$.target.service",
                "TaskDefinition.$": "$.deploy.taskDefinitionArn"
              },
              "ResultPath": "$.deployResult",
              "Next": "VerifyRollout",
              "Catch": [ { "ErrorEquals": [ "States.ALL" ], "Next": "RecordFailure", "ResultPath": "$.error" } ]
            },
            "VerifyRollout": {
              "Type": "Task",
              "Resource": "arn:aws:lambda:us-west-2:503947800380:function:scu-dev-deployer-verify-rollout",
              "ResultPath": "$.rollout",
              "Next": "Record",
              "Catch": [ { "ErrorEquals": [ "States.ALL" ], "Next": "RecordFailure", "ResultPath": "$.error" } ]
            },
            "Record": {
              "Type": "Task",
              "Resource": "arn:aws:states:::aws-sdk:s3:putObject",
              "Parameters": {
                "Bucket": "scu-dev-deploy-evidence-4df6-b9c6",
                "Key.$": "$.evidence.key",
                "Body.$": "$.evidence.body",
                "ContentType": "application/json",
                "IfNoneMatch": "*"
              },
              "End": true
            },
            "RecordFailure": {
              "Type": "Task",
              "Resource": "arn:aws:lambda:us-west-2:503947800380:function:scu-dev-deployer-record-failure",
              "Next": "Failed"
            },
            "Failed": {
              "Type": "Fail",
              "Error": "DeployFailed",
              "Cause": "see the evidence bucket and this execution's history"
            }
          }
        }
        """;

    private const string StageARolePolicy = """
        {
          "Version": "2012-10-17",
          "Statement": [
            { "Sid": "ReadTheRegistryAndItsSignatures", "Effect": "Allow",
              "Action": [ "ecr:GetAuthorizationToken", "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer",
                          "ecr:DescribeImages", "ecr:BatchGetRepositoryScanningConfiguration", "ecr:DescribeImageScanFindings" ],
              "Resource": "*" },
            { "Sid": "RollTheService", "Effect": "Allow",
              "Action": [ "ecs:RegisterTaskDefinition", "ecs:UpdateService", "ecs:DescribeServices",
                          "ecs:DescribeTasks", "ecs:ListTasks", "ecs:DescribeTaskDefinition" ],
              "Resource": "*" },
            { "Sid": "PassOnlyTheServicesOwnRoles", "Effect": "Allow", "Action": [ "iam:PassRole" ],
              "Resource": [ "arn:aws:iam::503947800380:role/scu-*-task-*", "arn:aws:iam::503947800380:role/scu-*-exec-*" ],
              "Condition": { "StringEquals": { "iam:PassedToService": "ecs-tasks.amazonaws.com" } } },
            { "Sid": "WriteEvidence", "Effect": "Allow", "Action": [ "s3:PutObject" ],
              "Resource": "arn:aws:s3:::scu-dev-deploy-evidence-4df6-b9c6/*" }
          ]
        }
        """;

    [Fact]
    public void THE_STAGE_A_MACHINE_AsDeployed_CouldNotHaveCompletedAnExecution()
    {
        // Held against the real artifact, so the checker is proven on what was actually wrong rather
        // than on examples written to satisfy it. Every gap below is one execution-killing defect that
        // passed all of this file's shape assertions at the time.
        var gaps = DeployerPlanner.ContractGaps(StageADefinition, StageARolePolicy);

        Assert.Contains(gaps, g => g.StartsWith("Verify has no ResultPath"));
        Assert.Contains(gaps, g => g.StartsWith("Deploy reads $.deploy,"));
        Assert.Contains(gaps, g => g.StartsWith("Record reads $.evidence,"));
        foreach (var fn in new[] { "verify", "verify-rollout", "record-failure" })
            Assert.Contains(gaps, g => g.Contains($"scu-dev-deployer-{fn}, and the role may not invoke it"));
    }

    [Fact]
    public void TheCheckerFindsAnApprovalAnyoneCouldRedirect_AndNobodyCouldAnswer()
    {
        // Stage A's approval state, which dev never deployed: topic from the input, no task token.
        const string definition = """
            { "StartAt": "Approve", "States": {
                "Approve": { "Type": "Task", "Resource": "arn:aws:states:::sns:publish.waitForTaskToken",
                             "Parameters": { "TopicArn.$": "$.approval.topicArn", "Message.$": "$.approval.summary" },
                             "ResultPath": "$.approval.result", "End": true } } }
            """;
        const string policy = """{ "Version": "2012-10-17", "Statement": [ { "Effect": "Allow", "Action": "sns:Publish", "Resource": "*" } ] }""";

        var gaps = DeployerPlanner.ContractGaps(definition, policy);

        Assert.Contains(gaps, g => g.Contains("takes its SNS topic from the execution state"));
        Assert.Contains(gaps, g => g.Contains("waits for a task token it never sends"));
    }

    // ---------------------------------------------------------------------------------------
    //  Receiving replicated images (MigrationPlan M5)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheRepositoriesImagesReplicateInto_AreTheBuildRegistrysNames()
    {
        // ECR keeps the repository name across accounts, so these must match the build side exactly.
        var build = PipelineBootstrapPlanner.Plan(Config(false), BuildAccount);

        Assert.Equal(build.Roles.Where(r => r.Class == "image").SelectMany(r => r.EcrRepositories), Plan().ImageRepositories);
    }

    [Fact]
    public void TheRegistryAdmitsTheBuildAccount_IntoThoseRepositoriesOnly_AndCannotBeAskedToCreateOne()
    {
        var statement = Plan().ReplicationPermission!;

        Assert.Equal("ecr:ReplicateImage", statement["Action"]!.GetValue<string>());
        Assert.DoesNotContain("CreateRepository", statement.ToJsonString());
        Assert.Equal(Plan().ImageRepositories.Select(r => $"arn:aws:ecr:us-west-2:{TargetAccount}:repository/{r}"),
            statement["Resource"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void APlanWithoutARealAccount_WritesNoRegistryPolicy()
    {
        // A statement naming a placeholder account is not something anyone could apply.
        Assert.Null(DeployerPlanner.Plan(Config(false)).ReplicationPermission);
    }

    [Fact]
    public void VerifyRoleName_IsTheVerifyFunctionsRole()
    {
        Assert.Equal(DeployerPlanner.VerifyRoleName(Config(false)), Function(Plan(), DeployerHandlers.Verify).RoleName);
    }
}
