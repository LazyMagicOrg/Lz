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
    public void EvidenceIsWrittenOnlyByTheTwoRecordingFunctions_EachUnderItsOwnPrefix()
    {
        // Record writes deploys/, RecordFailure writes failures/, and the state machine writes nothing — its
        // S3 write moved into the Record function on 2026-09-12, so a write that landed retries as success.
        var plan = Plan();
        var record = Function(plan, DeployerHandlers.Record).Policy;
        var failure = Function(plan, DeployerHandlers.RecordFailure).Policy;

        Assert.Contains($"{plan.EvidenceStore}/deploys/*", record);
        Assert.DoesNotContain("/failures/", record);
        Assert.Contains($"{plan.EvidenceStore}/failures/*", failure);
        Assert.DoesNotContain("/deploys/", failure);
        Assert.DoesNotContain("s3:PutObject", plan.RolePolicy);
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
    public void AToolingRole_WidensNothingTheServiceDeploysTrust()
    {
        // The tooling image gets a signing profile of its own (P4 stage A), and that must not make it deployable as the
        // service: the hook admits the service's profile and repository alone, and Verify reads image records from the
        // service's repository alone. The tooling image is verified by its own step (P-11), never by this hook.
        var config = Config(false);
        config.Pipeline!.Repositories!.Add(
            new PipelineRepositoryConfig { Repo = "Scutara/Scutara", Class = "tooling", Artifacts = new List<string> { "tooling" } });
        var plan = DeployerPlanner.Plan(config, TargetAccount);

        var hook = HookSettings.Read(n => Function(plan, DeployerHandlers.SignatureHook).Environment.GetValueOrDefault(n));
        var verify = VerifySettings.Read(n => Function(plan, DeployerHandlers.Verify).Environment.GetValueOrDefault(n));

        Assert.Equal(new[] { $"arn:aws:signer:us-west-2:{BuildAccount}:/signing-profiles/scu_build_ci_scutaraservice" },
            hook.TrustedProfiles);
        Assert.Equal(new[] { $"{TargetAccount}.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-aiphost" }, hook.RegistryScopes);
        Assert.Equal(new[] { "scu-4df6-b9c6-aiphost" }, verify.ImageRepositories);
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

        // Record is in the list since 2026-09-12. It had no Catch, so a deploy that landed and could not be
        // recorded ended the execution with nothing written anywhere.
        foreach (var name in new[] { "Verify", "Approve", "Prepare", "Deploy", "VerifyRollout", "Record" })
        {
            var s = states.GetProperty(name);
            Assert.True(s.TryGetProperty("Catch", out var c), $"{name} has no Catch");
            Assert.Equal("RecordFailure", c[0].GetProperty("Next").GetString());
        }
    }

    [Fact]
    public void EveryTaskState_Catches_WithNoneLeftOut()
    {
        // Enumerated from the definition rather than listed, so a state added later is held to it too.
        foreach (var approval in new[] { false, true })
        {
            foreach (var state in Definition(approval).GetProperty("States").EnumerateObject()
                         .Where(s => s.Value.GetProperty("Type").GetString() == "Task"))
                Assert.True(state.Value.TryGetProperty("Catch", out _), $"{state.Name} has no Catch");
        }
    }

    [Fact]
    public void RecordFailure_WithNowhereLeftToRecord_EndsInItsOwnFailState()
    {
        var states = Definition(false).GetProperty("States");
        var failure = states.GetProperty("RecordFailure");

        var catcher = Assert.Single(failure.GetProperty("Catch").EnumerateArray());
        Assert.Equal("EvidenceNotWritten", catcher.GetProperty("Next").GetString());
        Assert.Equal("Fail", states.GetProperty("EvidenceNotWritten").GetProperty("Type").GetString());
        Assert.Contains(failure.GetProperty("Retry").EnumerateArray(), RetriesEvidenceFaults);
    }

    [Fact]
    public void DeployRetriesEcsServerFaultsAndThrottling_ButNothingACallerCaused()
    {
        var deploy = Definition(false).GetProperty("States").GetProperty("Deploy");
        var retry = Assert.Single(deploy.GetProperty("Retry").EnumerateArray());
        var errors = retry.GetProperty("ErrorEquals").EnumerateArray().Select(e => e.GetString()!).ToList();

        Assert.Contains("Ecs.ServerException", errors);
        Assert.Contains("Ecs.ThrottlingException", errors);
        // Step Functions' naming rule for SDK integrations: the service prefix, and the Exception suffix always.
        Assert.All(errors, e => Assert.Matches(@"^Ecs\.[A-Za-z]+Exception$", e));
        Assert.DoesNotContain("States.ALL", errors);
        Assert.DoesNotContain("Ecs.ClientException", errors);
        Assert.DoesNotContain("Ecs.ServiceNotActiveException", errors);
        Assert.InRange(retry.GetProperty("MaxAttempts").GetInt32(), 1, 5);
        Assert.Equal("RecordFailure", deploy.GetProperty("Catch")[0].GetProperty("Next").GetString());
    }

    private static bool RetriesEvidenceFaults(JsonElement retry)
        => retry.GetProperty("ErrorEquals").EnumerateArray().Any(e => e.GetString() == nameof(Amazon.S3.AmazonS3Exception));

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
    public void RecordIsTheRecordFunction_RetriedOnEvidenceFaults_AndTheLastState()
    {
        // Was aws-sdk:s3:putObject with neither Retry nor Catch. The conditional write moved into RecordStep,
        // where an object already at the key is success rather than a 412 the definition could only fail on.
        var plan = Plan();
        var record = JsonDocument.Parse(plan.Definition).RootElement.GetProperty("States").GetProperty("Record");

        Assert.Equal($"arn:aws:lambda:us-west-2:{TargetAccount}:function:{Function(plan, DeployerHandlers.Record).Name}",
            record.GetProperty("Resource").GetString());
        Assert.Contains(record.GetProperty("Retry").EnumerateArray(), RetriesEvidenceFaults);
        Assert.True(record.GetProperty("End").GetBoolean());
        Assert.DoesNotContain("aws-sdk:s3", plan.Definition);
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

        foreach (var name in new[] { "Verify", "Prepare", "VerifyRollout", "Record", "RecordFailure" })
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
        AssertRetry("Verify", typeof(ImageNotYetReplicated), DeployerPlanner.ReplicationRetryIntervalSeconds, DeployerPlanner.ReplicationRetryMaxAttempts);
        AssertRetry("VerifyRollout", typeof(RolloutStillRolling), DeployerPlanner.RolloutRetryIntervalSeconds, DeployerPlanner.RolloutRetryMaxAttempts);

        // A REFUSAL IS NEVER RETRIED. Nothing about it changes by asking again.
        Assert.DoesNotContain(nameof(DeployRefused), Plan().Definition);
        Assert.DoesNotContain(nameof(RolloutNotDeployed), Plan().Definition);
    }

    [Fact]
    public void VerifyRollout_MayReadWhyARollDidNotLand_AndChangeNothing()
    {
        // The deployment records NotDeployedReason reads: read-only, on top of watching the roll.
        var actions = Statements(Function(Plan(), DeployerHandlers.VerifyRollout).Policy)
            .Where(st => st.GetProperty("Effect").GetString() == "Allow")
            .SelectMany(st => Strings(st.GetProperty("Action")))
            .ToHashSet();

        Assert.Contains("ecs:ListServiceDeployments", actions);
        Assert.Contains("ecs:DescribeServiceRevisions", actions);
        Assert.DoesNotContain(actions, a => a.StartsWith("ecs:Update", StringComparison.Ordinal)
                                         || a.StartsWith("ecs:Register", StringComparison.Ordinal)
                                         || a == "ecs:*");
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

    [Fact]
    public void TheEvidenceStore_IsWriteOnce_ByItsOwnBucketPolicy()
    {
        // Evidence is written once and never replaced; the writer's conditional put is no longer the only thing saying so.
        var plan = Plan();

        var statement = Assert.Single(plan.EvidenceStorePolicy);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(WriteOnceStore.Deny(plan.EvidenceStore), statement),
            statement.ToJsonString());
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

    // ---------------------------------------------------------------------------------------
    //  The signature hook as a service attaches it (C4c)
    // ---------------------------------------------------------------------------------------

    private static SystemConfig Enforcing()
    {
        var c = Config(false);
        c.Pipeline!.EnforceSignatures = true;
        return c;
    }

    [Fact]
    public void WithoutEnforceSignatures_NoServiceAttachesTheHook()
    {
        // THE ABSENT PATH, and it is the one every existing system takes: the block enabled, the hook
        // created by bootstrapdeployer, and still nothing attached, because nobody asked for enforcement.
        Assert.False(Config(false).Pipeline!.EnforceSignatures);
        Assert.Null(DeployerPlanner.SignatureHookFor(Config(false), "aiphost"));

        var noBlock = Config(false);
        noBlock.Pipeline = null;
        Assert.Null(DeployerPlanner.SignatureHookFor(noBlock, "aiphost"));
    }

    [Fact]
    public void EachConditionAlone_KeepsTheHookOff()
    {
        var disabled = Enforcing(); disabled.Pipeline!.Enabled = false;
        var noTarget = Enforcing(); noTarget.Pipeline!.TargetAccountId = null;
        var noImages = Enforcing(); noImages.Pipeline!.Classes = new List<string> { "client" };

        Assert.Null(DeployerPlanner.SignatureHookFor(disabled, "aiphost"));
        Assert.Null(DeployerPlanner.SignatureHookFor(noTarget, "aiphost"));
        Assert.Null(DeployerPlanner.SignatureHookFor(noImages, "aiphost"));
        // A service the pipeline does not build could never have a signed image to deploy.
        Assert.Null(DeployerPlanner.SignatureHookFor(Enforcing(), "worker"));
    }

    [Fact]
    public void TheServiceAttachesExactlyTheHookAndInvokerThePlanCreates()
    {
        var plan = DeployerPlanner.Plan(Enforcing(), TargetAccount);
        var hook = DeployerPlanner.SignatureHookFor(Enforcing(), "aiphost")!;

        Assert.Equal(
            $"arn:aws:lambda:us-west-2:{TargetAccount}:function:{Function(plan, DeployerHandlers.SignatureHook).Name}",
            hook.FunctionArn);
        Assert.Equal($"arn:aws:iam::{TargetAccount}:role/{plan.HookInvokerRoleName}", hook.InvokerRoleArn);
        // And the invoker's grant names that same function.
        Assert.Contains(hook.FunctionArn, plan.HookInvokerPolicy);
    }

    [Fact]
    public void AServiceThePipelineBuilds_NamesItsHooksExplicitly_EvenWhenThereAreNone()
    {
        var on = DeployerPlanner.SignatureHooksFor(Enforcing(), "aiphost");
        Assert.NotNull(on);
        Assert.Equal(DeployerPlanner.SignatureHookFor(Enforcing(), "aiphost"), Assert.Single(on));

        // EMPTY, NEVER NULL, with enforcement off. Null leaves DeploymentConfiguration out of the plan, and Pulumi
        // then keeps the hook ECS already has — measured 2026-09-12: deleting the flag from dev's config planned
        // no change while the hook stayed attached.
        var off = DeployerPlanner.SignatureHooksFor(Config(false), "aiphost");
        Assert.NotNull(off);
        Assert.Empty(off);
    }

    [Fact]
    public void AServiceThePipelineDoesNotBuild_NeverMentionsHooks()
    {
        // THE ABSENT PATH for every other system and service: null, so the plan is what it was before the hook.
        var noBlock = Enforcing(); noBlock.Pipeline = null;
        var disabled = Enforcing(); disabled.Pipeline!.Enabled = false;
        var noTarget = Enforcing(); noTarget.Pipeline!.TargetAccountId = null;
        var noImages = Enforcing(); noImages.Pipeline!.Classes = new List<string> { "client" };

        Assert.Null(DeployerPlanner.SignatureHooksFor(noBlock, "aiphost"));
        Assert.Null(DeployerPlanner.SignatureHooksFor(disabled, "aiphost"));
        Assert.Null(DeployerPlanner.SignatureHooksFor(noTarget, "aiphost"));
        Assert.Null(DeployerPlanner.SignatureHooksFor(noImages, "aiphost"));
        Assert.Null(DeployerPlanner.SignatureHooksFor(Enforcing(), "worker"));

        // And without enforcement too: turning the flag off is not what makes these null.
        var notBuilt = Config(false);
        Assert.Null(DeployerPlanner.SignatureHooksFor(notBuilt, "worker"));
    }

    // ---------------------------------------------------------------------------------------
    //  deploycontainer, refused where its image could not run (DecoupledCd §8 item 4)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AWorkstationImage_IsRefusedWhereTheHookWouldRollItBack()
    {
        var refusal = DeployerPlanner.RefusalForWorkstationImage(Enforcing(), "aiphost");

        Assert.NotNull(refusal);
        // It names the repository whose workflow ships the service instead, and the way back to a workstation image.
        Assert.Contains("Scutara/ScutaraService's pipeline build workflow", refusal);
        Assert.Contains("turn Pipeline.EnforceSignatures off first", refusal);
        Assert.Contains("run `lz deploytenant` to take the hook off", refusal);
    }

    [Fact]
    public void AWorkstationImage_IsNotRefusedWhereItCouldRun()
    {
        // ENFORCEMENT OFF, under an enabled block: no hook, so the image runs. Refusing here — §8's literal "under the
        // block" — would also leave a new tenant no first deploy, because deploytenant's image gate reads the
        // workstation repository.
        Assert.Null(DeployerPlanner.RefusalForWorkstationImage(Config(false), "aiphost"));

        // Every system and service with no hook: the command is what it was before the block.
        var noBlock = Enforcing(); noBlock.Pipeline = null;
        var disabled = Enforcing(); disabled.Pipeline!.Enabled = false;
        var noTarget = Enforcing(); noTarget.Pipeline!.TargetAccountId = null;
        var noImages = Enforcing(); noImages.Pipeline!.Classes = new List<string> { "client" };

        Assert.Null(DeployerPlanner.RefusalForWorkstationImage(noBlock, "aiphost"));
        Assert.Null(DeployerPlanner.RefusalForWorkstationImage(disabled, "aiphost"));
        Assert.Null(DeployerPlanner.RefusalForWorkstationImage(noTarget, "aiphost"));
        Assert.Null(DeployerPlanner.RefusalForWorkstationImage(noImages, "aiphost"));
        Assert.Null(DeployerPlanner.RefusalForWorkstationImage(Enforcing(), "worker"));
    }

    [Fact]
    public void TheRefusal_IsExactlyWhereTheServiceCarriesTheHook()
    {
        // ONE DECISION, NOT TWO: a refusal keyed on anything else either outlives the hook — refusing an image that would
        // run — or lets a build through that the hook then rolls back with no warning in front of it.
        var noBlock = Enforcing(); noBlock.Pipeline = null;
        var noTarget = Enforcing(); noTarget.Pipeline!.TargetAccountId = null;
        var twoImageRepositories = Enforcing();
        twoImageRepositories.Pipeline!.Repositories!.Add(
            new PipelineRepositoryConfig { Repo = "Scutara/ScutaraWorkers", Class = "image", Artifacts = new List<string> { "worker" } });

        foreach (var config in new[] { Enforcing(), Config(false), noBlock, noTarget, twoImageRepositories })
        foreach (var service in new[] { "aiphost", "worker" })
        {
            Assert.Equal(
                DeployerPlanner.SignatureHooksFor(config, service) is { Count: > 0 },
                DeployerPlanner.RefusalForWorkstationImage(config, service) is not null);
        }

        // And the repository it names is the one that builds that service, not merely the first image entry.
        Assert.Contains("Build worker with Scutara/ScutaraWorkers's", DeployerPlanner.RefusalForWorkstationImage(twoImageRepositories, "worker"));
        Assert.Contains("Build aiphost with Scutara/ScutaraService's", DeployerPlanner.RefusalForWorkstationImage(twoImageRepositories, "aiphost"));
    }

    // ---------------------------------------------------------------------------------------
    //  updateedge, refused where the pipeline lists the config classes (DecoupledCd P-14)
    // ---------------------------------------------------------------------------------------

    private static SystemConfig ListingConfig()
    {
        var c = Config(false);
        c.Pipeline!.Classes = new List<string> { "image", "client", "site", "assets", "config" };
        return c;
    }

    [Fact]
    public void UpdateEdge_IsRefusedWhereThePipelineListsConfig()
    {
        var refusal = DeployerPlanner.RefusalForWorkstationEdge(ListingConfig());

        Assert.NotNull(refusal);
        // It names the one publisher that remains, and the way back to updateedge with what that costs.
        Assert.Contains("scu/dev lists config in Pipeline.Classes", refusal);
        Assert.Contains("Run `lz deploytenant` instead", refusal);
        Assert.Contains("take config out of Pipeline.Classes first, which also stops the deployer accepting config records", refusal);
    }

    [Fact]
    public void UpdateEdge_RunsWhereThePipelineDoesNotListConfig()
    {
        // Every system with no block, a disabled one, or classes without config: the command is what it was before.
        var noBlock = ListingConfig(); noBlock.Pipeline = null;
        var disabled = ListingConfig(); disabled.Pipeline!.Enabled = false;
        var noClasses = ListingConfig(); noClasses.Pipeline!.Classes = null;
        var withoutConfig = ListingConfig(); withoutConfig.Pipeline!.Classes = new List<string> { "image", "client", "site", "assets" };

        Assert.Null(DeployerPlanner.RefusalForWorkstationEdge(noBlock));
        Assert.Null(DeployerPlanner.RefusalForWorkstationEdge(disabled));
        Assert.Null(DeployerPlanner.RefusalForWorkstationEdge(noClasses));
        Assert.Null(DeployerPlanner.RefusalForWorkstationEdge(withoutConfig));
        Assert.Null(DeployerPlanner.RefusalForWorkstationEdge(Config(false)));
    }

    // ---------------------------------------------------------------------------------------
    //  Taking the hook off — the removal Pulumi cannot make (measured 2026-09-13)
    // ---------------------------------------------------------------------------------------

    private static readonly string OurHook = $"arn:aws:lambda:us-west-2:{TargetAccount}:function:scu-dev-signature-hook";
    private const string ForeignHook = "arn:aws:lambda:us-west-2:111111111111:function:someone-elses-check";

    [Fact]
    public void TheHookArn_IsTheOneTheServiceAttaches()
    {
        Assert.Equal(DeployerPlanner.SignatureHookFor(Enforcing(), "aiphost")!.FunctionArn, DeployerPlanner.SignatureHookFunctionArn(Config(false)));
        Assert.Equal(OurHook, DeployerPlanner.SignatureHookFunctionArn(Config(false)));

        var noTarget = Config(false); noTarget.Pipeline!.TargetAccountId = null;
        Assert.Null(DeployerPlanner.SignatureHookFunctionArn(noTarget));
    }

    [Fact]
    public void AnUndeclaredHook_IsTakenOff_AndOnlyOurs()
    {
        var none = Array.Empty<SignatureHookAttachment>();

        // The measured case: the flag deleted, lz's hook still attached — keep nothing.
        var keepNothing = DeployerPlanner.HooksToKeepAfterRemoval(none, new[] { OurHook }, OurHook);
        Assert.NotNull(keepNothing);
        Assert.Empty(keepNothing);

        // Someone else's hook survives, in its place.
        Assert.Equal(new[] { 0, 2 }, DeployerPlanner.HooksToKeepAfterRemoval(none, new[] { ForeignHook, OurHook, ForeignHook }, OurHook)!.ToArray());
    }

    [Fact]
    public void NothingIsRemoved_UnlessThePlanDeclaresNoneAndOursIsAttached()
    {
        var none = Array.Empty<SignatureHookAttachment>();
        var declared = DeployerPlanner.SignatureHooksFor(Enforcing(), "aiphost");

        // Enforcing: attaching is Pulumi's job, and removing the hook it just declared would be the defect's mirror.
        Assert.Null(DeployerPlanner.HooksToKeepAfterRemoval(declared, new[] { OurHook }, OurHook));
        // A service outside the pipeline is not lz's to change, whatever it has attached.
        Assert.Null(DeployerPlanner.HooksToKeepAfterRemoval(null, new[] { OurHook }, OurHook));
        // Nothing of ours attached: no call at all.
        Assert.Null(DeployerPlanner.HooksToKeepAfterRemoval(none, Array.Empty<string?>(), OurHook));
        Assert.Null(DeployerPlanner.HooksToKeepAfterRemoval(none, new[] { ForeignHook }, OurHook));
        // No target account, so no ARN to recognise: touch nothing.
        Assert.Null(DeployerPlanner.HooksToKeepAfterRemoval(none, new[] { OurHook }, null));
    }

    [Fact]
    public void AVerifierlessHookPackage_IsRefusedOnlyWhereAServiceUsesIt()
    {
        var missing = new[] { "notation", "trustpolicy.json" };

        Assert.NotNull(DeployerBootstrapper.RefusalForHookPackage(missing, enforceSignatures: true));
        Assert.Null(DeployerBootstrapper.RefusalForHookPackage(missing, enforceSignatures: false));
        Assert.Null(DeployerBootstrapper.RefusalForHookPackage(Array.Empty<string>(), enforceSignatures: true));
        Assert.Contains("EnforceSignatures", DeployerBootstrapper.RefusalForHookPackage(missing, true));
    }

    [Fact]
    public void VerifyRoleName_IsTheVerifyFunctionsRole()
    {
        Assert.Equal(DeployerPlanner.VerifyRoleName(Config(false)), Function(Plan(), DeployerHandlers.Verify).RoleName);
    }

    // ---------------------------------------------------------------------------------------
    //  The trigger (P2 stage D)
    // ---------------------------------------------------------------------------------------

    private static readonly DeployerTriggerInputs DevInputs = new("scu-dev-cluster", new[] { "mp" });

    private static SystemConfig Triggered()
    {
        var c = Config(false);
        c.Pipeline!.DeployOnBuildRecord = true;
        return c;
    }

    private static DeployerTriggerPlan Trigger(SystemConfig? config = null)
        => DeployerPlanner.Plan(config ?? Triggered(), TargetAccount, DevInputs).Trigger!;

    [Fact]
    public void WithoutDeployOnBuildRecord_ThereIsNoTrigger_NoStartFunction_AndNothingIsResolvedForOne()
    {
        var plan = DeployerPlanner.Plan(Config(false), TargetAccount, triggerInputs: null);

        Assert.False(Config(false).Pipeline!.DeployOnBuildRecord);
        Assert.False(DeployerPlanner.TriggerWanted(Config(false)));
        Assert.Null(plan.Trigger);
        Assert.DoesNotContain(plan.Functions, f => f.Handler == DeployerHandlers.Start);
        Assert.Equal(6, plan.Functions.Count);
    }

    [Fact]
    public void DeployOnBuildRecord_PlannedWithoutTheClusterAndTenants_IsRefused()
    {
        Assert.True(DeployerPlanner.TriggerWanted(Triggered()));

        var ex = Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(Triggered(), TargetAccount));
        Assert.Contains("bootstrapdeployer", ex.Message);
    }

    [Fact]
    public void DeployOnBuildRecord_WithNoTenants_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DeployerPlanner.Plan(Triggered(), TargetAccount, new DeployerTriggerInputs("scu-dev-cluster", Array.Empty<string>())));
        Assert.Contains("tenant", ex.Message);
    }

    [Fact]
    public void TheTrigger_RollsEachTenantsServiceFromItsImageRepositorysRecords_InARepositoryVerifyAccepts()
    {
        var plan = DeployerPlanner.Plan(Triggered(), TargetAccount, new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp", "zz" }));

        // The client repository writes records too, and has no route: nothing deploys a bundle.
        var route = Assert.Single(plan.Trigger!.Routes);
        Assert.Equal("image/scutara/scutaraservice/", route.RecordPrefix);
        Assert.Equal(
            new[]
            {
                new TriggerTarget("mp", new DeployTarget("scu-dev-cluster", "scu-mp-aiphost", "aiphost", "scu-4df6-b9c6-aiphost")),
                new TriggerTarget("zz", new DeployTarget("scu-dev-cluster", "scu-zz-aiphost", "aiphost", "scu-4df6-b9c6-aiphost")),
            },
            route.Targets);

        // Verify refuses a target repository that is not one of this environment's pipeline repositories.
        Assert.All(route.Targets, t => Assert.Contains(t.Target.Repository, plan.ImageRepositories));
    }

    [Fact]
    public void TheTargets_AreTheOnesTheHandStartedExecutionsUsed()
    {
        // The first execution, started by hand on 2026-09-12, rolled exactly this service with this input.
        var target = Trigger().Routes.Single().Targets.Single().Target;
        Assert.Equal(new DeployTarget("scu-dev-cluster", "scu-mp-aiphost", "aiphost", "scu-4df6-b9c6-aiphost"), target);
    }

    [Fact]
    public void AnImageRepositoryWithTwoArtifacts_IsRefusedOnlyWhenTheTriggerIsOn()
    {
        var two = Triggered();
        two.Pipeline!.Repositories![0].Artifacts = new List<string> { "aiphost", "worker" };

        var ex = Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(two, TargetAccount, DevInputs));
        Assert.Contains("2 image artifacts", ex.Message);

        two.Pipeline.DeployOnBuildRecord = false;
        Assert.Null(DeployerPlanner.Plan(two, TargetAccount).Trigger);
    }

    [Fact]
    public void TheStartFunction_MayStartOnlyThisDeployer_ReadOnlyItsExecutions_AndDeadLetterOnlyItsQueue()
    {
        var plan = DeployerPlanner.Plan(Triggered(), TargetAccount, DevInputs);
        var start = Function(plan, DeployerHandlers.Start);

        var grants = Statements(start.Policy)
            .Where(s => !s.GetProperty("Sid").GetString()!.Contains("Log"))
            .Select(s => (Actions: string.Join(",", Strings(s.GetProperty("Action"))), Resource: string.Join(",", Strings(s.GetProperty("Resource")))))
            .ToList();

        Assert.Equal(
            new[]
            {
                // D2: the record, for its branch — image records only, no list.
                ("s3:GetObject", "arn:aws:s3:::scu-build-records-4df6-b9c6/image/*"),
                ("states:StartExecution", "arn:aws:states:us-west-2:503947800380:stateMachine:scu-dev-deployer"),
                ("states:DescribeExecution", "arn:aws:states:us-west-2:503947800380:execution:scu-dev-deployer:*"),
                ("sqs:SendMessage", "arn:aws:sqs:us-west-2:503947800380:scu-dev-deployer-start-dlq"),
            },
            grants);

        Assert.Equal($"arn:aws:states:us-west-2:{TargetAccount}:stateMachine:{plan.StateMachineName}",
            grants.Single(g => g.Actions == "states:StartExecution").Resource);
        Assert.Equal(plan.Trigger!.DeadLetterQueueArn, grants.Single(g => g.Actions == "sqs:SendMessage").Resource);
        Assert.Equal(DeployerPlanner.StartFunctionRoleName(Triggered()), start.RoleName);
        Assert.False(start.InvokedByStateMachine);
        Assert.DoesNotContain("Delete", start.Policy, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Function(plan, DeployerHandlers.Start).RoleName, plan.Functions.Where(f => f != start).Select(f => f.RoleName));
    }

    [Fact]
    public void TheStateMachineRole_CannotInvokeTheStartFunction()
    {
        // EventBridge invokes it; the machine it starts has no business starting itself.
        var plan = DeployerPlanner.Plan(Triggered(), TargetAccount, DevInputs);
        Assert.DoesNotContain(plan.Trigger!.StartFunctionName, plan.RolePolicy);
    }

    [Fact]
    public void TheStartFunction_IsConfiguredWithExactlyWhatItReads()
    {
        var plan = DeployerPlanner.Plan(Triggered(), TargetAccount, DevInputs);
        var env = Function(plan, DeployerHandlers.Start).Environment;

        var settings = TriggerSettings.Read(k => env.GetValueOrDefault(k));

        Assert.Equal(BuildAccount, settings.ArtifactAccountId);
        Assert.Equal("scu-build-records-4df6-b9c6", settings.BuildRecordStore);
        Assert.Equal($"arn:aws:states:us-west-2:{TargetAccount}:stateMachine:scu-dev-deployer", settings.StateMachineArn);
        Assert.Equal(plan.Trigger!.Routes.Single().RecordPrefix, settings.Routes.Single().RecordPrefix);
        Assert.Equal(plan.Trigger.Routes.Single().Targets, settings.Routes.Single().Targets);
        Assert.Equal(new[] { "refs/heads/main" }, settings.AllowedRefs);
        Assert.Equal(5, env.Count);
    }

    [Fact]
    public void TheBus_AdmitsOnlyTheBuildAccountsForwardingRole_ByTheNameTheBuildSideCreates()
    {
        var config = Triggered();
        var trigger = Trigger(config);
        var build = PipelineBootstrapPlanner.Plan(config, BuildAccount).RecordForwarding!;

        var statement = Statements(trigger.BusPolicy).Single();
        Assert.Equal("Allow", statement.GetProperty("Effect").GetString());
        Assert.Equal($"arn:aws:iam::{BuildAccount}:root", statement.GetProperty("Principal").GetProperty("AWS").GetString());
        Assert.Equal("events:PutEvents", statement.GetProperty("Action").GetString());
        Assert.Equal(trigger.BusArn, statement.GetProperty("Resource").GetString());
        Assert.Equal(
            $"arn:aws:iam::{BuildAccount}:role/{build.RoleName}",
            statement.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:PrincipalArn").GetString());
    }

    [Fact]
    public void BothRules_UseOnePattern_AndTheBuildSideTargetsThisBus()
    {
        var config = Triggered();
        var trigger = Trigger(config);
        var build = PipelineBootstrapPlanner.Plan(config, BuildAccount).RecordForwarding!;

        Assert.Equal(trigger.EventPattern, build.EventPattern);
        Assert.Equal(trigger.BusArn, build.TargetBusArn);
        Assert.Equal("arn:aws:events:us-west-2:503947800380:event-bus/scu-dev-deployer-trigger", trigger.BusArn);
    }

    [Fact]
    public void TheRuleOnTheTriggerBus_CarriesTheBusInItsArn_AndTheQueueAdmitsOnlyThatRule()
    {
        var trigger = Trigger();

        Assert.Equal("arn:aws:events:us-west-2:503947800380:rule/scu-dev-deployer-trigger/scu-dev-deployer-start", trigger.RuleArn);

        var statement = Statements(trigger.DeadLetterQueuePolicy).Single();
        Assert.Equal("events.amazonaws.com", statement.GetProperty("Principal").GetProperty("Service").GetString());
        Assert.Equal("sqs:SendMessage", statement.GetProperty("Action").GetString());
        Assert.Equal(trigger.DeadLetterQueueArn, statement.GetProperty("Resource").GetString());
        Assert.Equal(trigger.RuleArn, statement.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn").GetString());
    }

    [Fact]
    public void AFailingEvent_IsRetriedTwiceWithinAnHour_ThenQueued_AndTheQueueHasAnAlarm()
    {
        var trigger = Trigger();

        Assert.Equal(2, trigger.MaximumRetryAttempts);
        Assert.Equal(3600, trigger.MaximumEventAgeSeconds);
        Assert.Equal("scu-dev-deployer-start-dlq", trigger.DeadLetterQueueName);
        Assert.Equal("scu-dev-deployer-start-dlq-not-empty", trigger.AlarmName);
    }

    [Fact]
    public void TheSelfRewriteDeny_CoversTheTrigger_AndTheFunctions()
    {
        var actions = Strings(Statements(Plan().DenyPolicy).Single().GetProperty("Action")).ToList();

        foreach (var action in new[]
                 {
                     "events:PutRule", "events:PutTargets", "events:RemoveTargets", "events:DeleteRule", "events:DisableRule",
                     "events:PutPermission", "events:RemovePermission",
                     "lambda:UpdateFunctionCode", "lambda:UpdateFunctionConfiguration", "lambda:AddPermission",
                     "lambda:RemovePermission", "lambda:PutFunctionEventInvokeConfig", "lambda:UpdateFunctionEventInvokeConfig",
                 })
            Assert.Contains(action, actions);

        // Nothing a deployer role does is denied: invoking, starting, reading.
        Assert.DoesNotContain(actions, a => a is "lambda:InvokeFunction" or "states:StartExecution" or "states:DescribeExecution");
    }

    // ---------------------------------------------------------------------------------------
    //  The alerts (P2 stage D3)
    // ---------------------------------------------------------------------------------------

    private const string AlertsTopic = "arn:aws:sns:us-west-2:503947800380:scu-dev-pipeline-alerts";

    private static SystemConfig Alerting(bool trigger)
    {
        var c = trigger ? Triggered() : Config(false);
        c.Pipeline!.Alerts = true;
        return c;
    }

    private static PipelineDeployer AlertsPlan(bool trigger)
        => DeployerPlanner.Plan(Alerting(trigger), TargetAccount, trigger ? DevInputs : null);

    private static JsonElement BySid(IEnumerable<JsonElement> statements, string sid)
        => statements.Single(s => s.GetProperty("Sid").GetString() == sid);

    [Fact]
    public void WithoutAlerts_ThereIsNoTopic_NoSweep_AndNoAlarmNotifiesAnyone()
    {
        var plan = DeployerPlanner.Plan(Triggered(), TargetAccount, DevInputs);

        Assert.Null(plan.Alerts);
        Assert.DoesNotContain(plan.Functions, f => f.Handler == DeployerHandlers.Corroborate);
        Assert.Empty(plan.Trigger!.AlarmActions);
        Assert.DoesNotContain(plan.Functions, f => f.Policy.Contains("sns:", StringComparison.Ordinal));
    }

    [Fact]
    public void WithAlerts_ThisMachinesFailedExecutions_GoToTheTopic()
    {
        var alerts = AlertsPlan(trigger: false).Alerts!;

        Assert.Equal("scu-dev-pipeline-alerts", alerts.TopicName);
        Assert.Equal(AlertsTopic, alerts.TopicArn);
        Assert.Equal("arn:aws:events:us-west-2:503947800380:rule/scu-dev-deployer-failed", alerts.FailedDeployRuleArn);

        var pattern = JsonDocument.Parse(alerts.FailedDeployPattern).RootElement;
        Assert.Equal(new[] { "aws.states" }, Strings(pattern.GetProperty("source")));
        Assert.Equal(new[] { "Step Functions Execution Status Change" }, Strings(pattern.GetProperty("detail-type")));
        Assert.Equal(new[] { "FAILED", "TIMED_OUT", "ABORTED" }, Strings(pattern.GetProperty("detail").GetProperty("status")));
        Assert.Equal(
            new[] { "arn:aws:states:us-west-2:503947800380:stateMachine:scu-dev-deployer" },
            Strings(pattern.GetProperty("detail").GetProperty("stateMachineArn")));
    }

    [Fact]
    public void TheTopic_AdmitsExactlyThisPipelinesPublishers()
    {
        // Without the trigger: the failed-deploy rule, and the sweep's errors alarm.
        var alone = Statements(AlertsPlan(trigger: false).Alerts!.TopicPolicy).ToList();
        Assert.Equal(2, alone.Count);

        var rule = BySid(alone, CrossAccount.AlertsFailedDeploySid);
        Assert.Equal("events.amazonaws.com", rule.GetProperty("Principal").GetProperty("Service").GetString());
        Assert.Equal("sns:Publish", rule.GetProperty("Action").GetString());
        Assert.Equal(AlertsTopic, rule.GetProperty("Resource").GetString());
        Assert.Equal("arn:aws:events:us-west-2:503947800380:rule/scu-dev-deployer-failed",
            rule.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn").GetString());

        var local = BySid(alone, CrossAccount.AlertsLocalAlarmsSid);
        Assert.Equal("cloudwatch.amazonaws.com", local.GetProperty("Principal").GetProperty("Service").GetString());
        Assert.Equal(
            new[] { "arn:aws:cloudwatch:us-west-2:503947800380:alarm:scu-dev-deployer-corroborate-errors" },
            Strings(local.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn")));
        Assert.Equal(TargetAccount, local.GetProperty("Condition").GetProperty("StringEquals").GetProperty("aws:SourceAccount").GetString());

        // With it: the start queue's alarm here, and the build account's forwarding alarm, by ARN and by account.
        var withTrigger = Statements(AlertsPlan(trigger: true).Alerts!.TopicPolicy).ToList();
        Assert.Equal(3, withTrigger.Count);
        Assert.Equal(
            new[] { "arn:aws:cloudwatch:us-west-2:503947800380:alarm:scu-dev-deployer-corroborate-errors",
                    "arn:aws:cloudwatch:us-west-2:503947800380:alarm:scu-dev-deployer-start-dlq-not-empty" },
            Strings(BySid(withTrigger, CrossAccount.AlertsLocalAlarmsSid).GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn")));

        var build = BySid(withTrigger, CrossAccount.AlertsBuildAlarmSid);
        Assert.Equal("arn:aws:cloudwatch:us-west-2:147440642635:alarm:scu-dev-forward-build-records-dlq-not-empty",
            build.GetProperty("Condition").GetProperty("ArnEquals").GetProperty("aws:SourceArn").GetString());
        Assert.Equal(BuildAccount, build.GetProperty("Condition").GetProperty("StringEquals").GetProperty("aws:SourceAccount").GetString());

        // Every statement names a service, never any principal at all.
        Assert.All(withTrigger, s => Assert.Equal(JsonValueKind.Object, s.GetProperty("Principal").ValueKind));
    }

    [Fact]
    public void TheBuildAccountsAlarm_IsTheOneItsPlanCreates_AndItNotifiesThisTopic()
    {
        // ONE DEFINITION: the alarm this topic admits is the alarm the build account's plan creates.
        var forwarding = PipelineBootstrapPlanner.Plan(Alerting(trigger: true), BuildAccount).RecordForwarding!;

        Assert.Equal(DeployerPlanner.ForwardDeadLetterAlarmName(Alerting(trigger: true)), forwarding.AlarmName);
        Assert.Contains($":alarm:{forwarding.AlarmName}\"", AlertsPlan(trigger: true).Alerts!.TopicPolicy);
        Assert.Equal(new[] { AlertsTopic }, forwarding.AlarmActions);
    }

    [Fact]
    public void WithAlerts_TheTriggersQueueAlarm_NotifiesTheTopic()
    {
        Assert.Equal(new[] { AlertsTopic }, AlertsPlan(trigger: true).Trigger!.AlarmActions);
    }

    [Fact]
    public void TheSweep_RunsEveryFifteenMinutes_AndItsFailuresAlarm()
    {
        var alerts = AlertsPlan(trigger: false).Alerts!;

        Assert.Equal("rate(15 minutes)", alerts.ScheduleExpression);
        Assert.Equal("arn:aws:events:us-west-2:503947800380:rule/scu-dev-deployer-corroborate-schedule", alerts.ScheduleRuleArn);
        Assert.Equal("scu-dev-deployer-corroborate-errors", alerts.CorroborateErrorsAlarmName);
        Assert.Equal("arn:aws:lambda:us-west-2:503947800380:function:scu-dev-deployer-corroborate", alerts.CorroborateFunctionArn);
    }

    [Fact]
    public void TheSweep_IsConfiguredWithExactlyWhatItReads()
    {
        var fn = Function(AlertsPlan(trigger: false), DeployerHandlers.Corroborate);

        Assert.Equal(DeployerPlanner.CorroborateFunctionRoleName(Config(false)), fn.RoleName);
        Assert.False(fn.InvokedByStateMachine);
        Assert.Equal(
            new[] { DeployerEnvironment.AlertsTopic, DeployerEnvironment.BuildRecordStore, DeployerEnvironment.CorroborateSources,
                    DeployerEnvironment.EvidenceStore },
            fn.Environment.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("scu-4df6-b9c6-aiphost=image/scutara/scutaraservice/", fn.Environment[DeployerEnvironment.CorroborateSources]);
        Assert.Equal(AlertsTopic, fn.Environment[DeployerEnvironment.AlertsTopic]);

        // What it is configured with is what it can read.
        var settings = CorroborateSettings.Read(n => fn.Environment.GetValueOrDefault(n));
        Assert.Equal(new[] { new CorroborateSource("scu-4df6-b9c6-aiphost", "image/scutara/scutaraservice/") }, settings.Sources);

        // And the state machine may not invoke it.
        Assert.DoesNotContain(fn.Name, AlertsPlan(trigger: false).RolePolicy);
    }

    [Fact]
    public void TheSweep_MayReadImagesAndRecords_RecordAnAnomaly_AndAlert_AndNothingElse()
    {
        var plan = AlertsPlan(trigger: false);
        var statements = Statements(Function(plan, DeployerHandlers.Corroborate).Policy)
            .Where(s => !s.GetProperty("Sid").GetString()!.Contains("Log", StringComparison.Ordinal))
            .ToList();

        void Expect(string sid, string[] actions, string[] resources)
        {
            var s = BySid(statements, sid);
            Assert.Equal(actions, Strings(s.GetProperty("Action")));
            Assert.Equal(resources, Strings(s.GetProperty("Resource")));
        }

        Expect("ListTheImagesThatArrived", new[] { "ecr:DescribeImages" }, new[] { "arn:aws:ecr:us-west-2:503947800380:repository/scu-4df6-b9c6-aiphost" });
        Expect("FindImageRecords", new[] { "s3:ListBucket" }, new[] { "arn:aws:s3:::scu-build-records-4df6-b9c6" });
        Expect("ReadImageRecords", new[] { "s3:GetObject" }, new[] { "arn:aws:s3:::scu-build-records-4df6-b9c6/image/*" });
        Expect("RecordAnAnomalyOnce", new[] { "s3:PutObject" }, new[] { $"arn:aws:s3:::{plan.EvidenceStore}/anomalies/*" });
        Expect("FindRecordedAnomalies", new[] { "s3:ListBucket" }, new[] { $"arn:aws:s3:::{plan.EvidenceStore}" });
        Expect("AlertAPerson", new[] { "sns:Publish" }, new[] { AlertsTopic });
        Assert.Equal(6, statements.Count);

        // Both lists stay under their prefix.
        Assert.Equal("image/*", BySid(statements, "FindImageRecords").GetProperty("Condition").GetProperty("StringLike").GetProperty("s3:prefix").GetString());
        Assert.Equal("anomalies/*", BySid(statements, "FindRecordedAnomalies").GetProperty("Condition").GetProperty("StringLike").GetProperty("s3:prefix").GetString());
    }

    [Fact]
    public void TheSelfRewriteDeny_CoversTheAlerts_ButNotPublishing()
    {
        var actions = Strings(Statements(Plan().DenyPolicy).Single().GetProperty("Action")).ToList();

        foreach (var action in new[]
                 {
                     "sns:SetTopicAttributes", "sns:AddPermission", "sns:RemovePermission", "sns:DeleteTopic", "sns:Unsubscribe",
                     "cloudwatch:PutMetricAlarm", "cloudwatch:DeleteAlarms", "cloudwatch:DisableAlarmActions",
                 })
            Assert.Contains(action, actions);

        Assert.DoesNotContain("sns:Publish", actions);
    }

    // ---------------------------------------------------------------------------------------
    //  The review's fix batch (DecoupledCd.md §14.3): the trusts, the ECS scope, the functions' logs
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheMachinesRole_TrustsStepFunctions_OnlyForAStateMachineInThisAccountAndRegion()
    {
        var plan = Plan();
        var statement = Statements(plan.StateMachineTrustPolicy).Single();

        Assert.Equal("Allow", statement.GetProperty("Effect").GetString());
        Assert.Equal("states.amazonaws.com", statement.GetProperty("Principal").GetProperty("Service").GetString());
        Assert.Equal("sts:AssumeRole", statement.GetProperty("Action").GetString());

        // The confused-deputy conditions, in the form Step Functions documents for a state machine's role.
        var condition = statement.GetProperty("Condition");
        var sourceArn = condition.GetProperty("ArnLike").GetProperty("aws:SourceArn").GetString()!;
        Assert.Equal($"arn:aws:states:us-west-2:{TargetAccount}:stateMachine:*", sourceArn);
        Assert.Equal(TargetAccount, condition.GetProperty("StringEquals").GetProperty("aws:SourceAccount").GetString());

        // The machine this plan creates is inside it; one in another account or region is not.
        Assert.True(Matches(sourceArn, $"arn:aws:states:us-west-2:{TargetAccount}:stateMachine:{plan.StateMachineName}"));
        Assert.False(Matches(sourceArn, $"arn:aws:states:us-west-2:111111111111:stateMachine:{plan.StateMachineName}"));
        Assert.False(Matches(sourceArn, $"arn:aws:states:us-east-1:{TargetAccount}:stateMachine:{plan.StateMachineName}"));
    }

    [Fact]
    public void TheFunctionsAndTheHookInvokersTrusts_KeepTheDocumentedForm()
    {
        // Neither Lambda's execution-role documentation nor ECS's lifecycle-hook role documentation shows a source
        // condition, and a condition the service does not send would stop every function or every deployment.
        var plan = Plan();

        foreach (var (policy, principal) in new[]
                 {
                     (plan.FunctionTrustPolicy, "lambda.amazonaws.com"),
                     (plan.HookInvokerTrustPolicy, "ecs.amazonaws.com"),
                 })
        {
            var statement = Statements(policy).Single();
            Assert.Equal(principal, statement.GetProperty("Principal").GetProperty("Service").GetString());
            Assert.Equal("sts:AssumeRole", statement.GetProperty("Action").GetString());
            Assert.False(statement.TryGetProperty("Condition", out _), $"{principal}'s trust has a condition");
        }
    }

    [Fact]
    public void EveryServiceGrant_ReachesOnlyThisSystemsServices_InThisEnvironmentsCluster()
    {
        var plan = Plan();
        var serviceActions = new[] { "ecs:UpdateService", "ecs:DescribeServices" };

        var grants = plan.Functions.Select(f => (f.Name, f.Policy)).Append((Name: plan.RoleName, Policy: plan.RolePolicy))
            .SelectMany(p => Statements(p.Policy).Select(s => (p.Name, Statement: s)))
            .Where(x => Strings(x.Statement.GetProperty("Action")).Any(serviceActions.Contains))
            .ToList();

        // The machine rolls the service; Verify, Prepare and VerifyRollout read it.
        Assert.Equal(
            new[] { "scu-dev-deployer", "scu-dev-deployer-prepare", "scu-dev-deployer-verify", "scu-dev-deployer-verify-rollout" },
            grants.Select(g => g.Name).OrderBy(n => n, StringComparer.Ordinal));

        foreach (var (name, statement) in grants)
        {
            var resources = Strings(statement.GetProperty("Resource")).ToList();
            Assert.Equal(DeployerPlanner.ServiceArns("us-west-2", TargetAccount, "scu", "dev"), resources);

            // The live service, by the ARN the hook's events carry in scu-dev ...
            Assert.True(resources.Any(r => Matches(r, $"arn:aws:ecs:us-west-2:{TargetAccount}:service/scu-dev-cluster/scu-mp-aiphost")),
                $"{name} cannot reach the live service");
            // ... and under the legacy cluster name lz also deploys to.
            Assert.True(resources.Any(r => Matches(r, $"arn:aws:ecs:us-west-2:{TargetAccount}:service/scu-cluster/scu-mp-aiphost")),
                $"{name} cannot reach a service in the legacy cluster");

            foreach (var foreign in new[]
                     {
                         $"arn:aws:ecs:us-west-2:{TargetAccount}:service/other-cluster/scu-mp-aiphost",
                         $"arn:aws:ecs:us-west-2:{TargetAccount}:service/scu-prod-cluster/scu-mp-aiphost",
                         $"arn:aws:ecs:us-west-2:{TargetAccount}:service/scu-dev-cluster/another-systems-service",
                         $"arn:aws:ecs:us-east-1:{TargetAccount}:service/scu-dev-cluster/scu-mp-aiphost",
                         $"arn:aws:ecs:us-west-2:111111111111:service/scu-dev-cluster/scu-mp-aiphost",
                     })
                Assert.False(resources.Any(r => Matches(r, foreign)), $"{name} may reach {foreign}");
        }
    }

    [Fact]
    public void VerifyMayReadTheRunningRevision_ToFindTheTargetContainer()
    {
        var statement = Statements(Function(Plan(), DeployerHandlers.Verify).Policy)
            .Single(s => s.GetProperty("Sid").GetString() == "ConfirmTheTargetContainer");

        Assert.Equal(new[] { "ecs:DescribeTaskDefinition" }, Strings(statement.GetProperty("Action")));
    }

    [Fact]
    public void TheFunctionsLogRetention_IsHygieneLambdaLogRetentionDays()
    {
        Assert.Null(Plan().LogRetentionDays);

        var config = Config(approvalRequired: false);
        config.Hygiene = new HygieneConfig { LambdaLogRetentionDays = 14 };
        Assert.Equal(14, DeployerPlanner.Plan(config, TargetAccount).LogRetentionDays);
    }

    [Fact]
    public void TheSelfRewriteDeny_CoversTheFunctionsLogs()
    {
        // A role that could delete a log group, or shorten how long it keeps logs, could erase what a failure left.
        var actions = Strings(Statements(Plan().DenyPolicy).Single().GetProperty("Action")).ToList();

        foreach (var action in new[] { "logs:DeleteLogGroup", "logs:PutRetentionPolicy", "logs:DeleteRetentionPolicy" })
            Assert.Contains(action, actions);

        // Writing them is every function's job.
        Assert.DoesNotContain("logs:PutLogEvents", actions);
        Assert.DoesNotContain("logs:CreateLogStream", actions);
    }
}
