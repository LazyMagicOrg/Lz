using System.Text.Json;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>A CloudWatch alarm on one of the intake's functions, notifying the intake's alerts topic.</summary>
public sealed record IntakeAlarm(
    string Name, string Description, string FunctionName, string Metric, string Statistic, string Comparison, double Threshold,
    int PeriodSeconds, string TreatMissingData);

/// <summary>
/// The intake in the build account (DecoupledCd.md §15.4, §3; V-B, dry): its write-once evidence store, its scratch bucket
/// (one day, and written by the fetch function only), its alerts topic, the secret its App's key lives in, four functions
/// with a role each, the machine and its role, the one-minute schedule, and the alarms. Planned from configuration alone.
/// </summary>
/// <param name="AppSecretName">Created by nobody here: the owner writes the App's key into it (V-17), and the functions read it.</param>
/// <param name="ScratchBucketPolicy">Refuses a write by any principal but the fetch function's role, and an unencrypted transport.</param>
/// <param name="PollerReservedConcurrency">One: a tick that arrives while one runs waits for it (§5.3 rule 1).</param>
/// <param name="PollerMaximumEventAgeSeconds">A minute, so ticks never queue behind a slow one; a failed tick is not retried.</param>
public sealed record IntakeBuildPlan(
    string EvidenceStore,
    string EvidenceStorePolicyStatementSid,
    string ScratchBucket,
    string ScratchBucketPolicy,
    int ScratchExpirationDays,
    string AlertsTopicName,
    string AlertsTopicArn,
    string AlertsTopicPolicy,
    string AppSecretName,
    string AppSecretArnPattern,
    IReadOnlyList<DeployerFunction> Functions,
    string StateMachineName,
    string StateMachineArn,
    string StateMachineRoleName,
    string StateMachineRolePolicy,
    string StateMachineTrustPolicy,
    string FunctionTrustPolicy,
    string DenyPolicy,
    string Definition,
    string ScheduleRuleName,
    string ScheduleExpression,
    string PollerFunctionName,
    int PollerReservedConcurrency,
    int PollerMaximumEventAgeSeconds,
    IReadOnlyList<IntakeAlarm> Alarms,
    IReadOnlyList<IntakeRepository> Repositories);

public static class IntakeBuildPlanner
{
    public const int ScratchExpirationDays = 1;
    public const int FetchTimeoutSeconds = 900;
    public const int FetchMemoryMb = 2048;
    public const int FetchEphemeralStorageMb = 4096;

    public static string EvidenceStoreFor(SystemConfig c) => $"{c.SystemKey}-intake-evidence-{c.SystemSuffix}";
    public static string ScratchBucketFor(SystemConfig c) => $"{c.SystemKey}-intake-scratch-{c.SystemSuffix}";
    public static string AppSecretFor(SystemConfig c) => $"{c.SystemKey}/cicd/intake-app";

    /// <summary>The repositories the intake takes: those with an <c>Intake</c> mode, all of whose ids the validator required.</summary>
    public static IReadOnlyList<IntakeRepository> RepositoriesFor(SystemConfig config)
        => (config.Pipeline?.Repositories ?? new List<PipelineRepositoryConfig>())
            .Where(r => !string.IsNullOrEmpty(r.Intake))
            .Select(r => new IntakeRepository(
                r.Repo!, r.RepositoryId!.Value, r.OwnerId!.Value, r.WorkflowPath!, r.Intake!, r.Class!,
                (IReadOnlyList<string>?)r.Artifacts ?? Array.Empty<string>()))
            .ToList();

    /// <summary>The intake's plan, or null when no repository has an <c>Intake</c> mode: a system that has not opted in gets none.</summary>
    public static IntakeBuildPlan? Plan(SystemConfig config, string accountId)
    {
        var repositories = RepositoriesFor(config);
        if (repositories.Count == 0) return null;
        if (repositories.FirstOrDefault(r => r.Mode != "dry") is { } live)
            throw new InvalidOperationException(
                $"{live.Repo} is Intake: {live.Mode}; this intake is built dry only (V-B), and pushes nothing. Live takes V-C and V-D.");
        if (repositories.FirstOrDefault(r => r.Artifacts.Count == 0) is { } none)
            throw new InvalidOperationException($"{none.Repo} takes part in the intake and names no Artifacts, so there is nothing to fetch.");

        var sk = config.SystemKey;
        var region = config.Region;
        string Fn(string part) => $"{sk}-intake-{part}";
        string FnArn(string name) => $"arn:aws:lambda:{region}:{accountId}:function:{name}";

        var evidence = EvidenceStoreFor(config);
        var scratch = ScratchBucketFor(config);
        var topicName = $"{sk}-intake-alerts";
        var topicArn = $"arn:aws:sns:{region}:{accountId}:{topicName}";
        var secret = AppSecretFor(config);
        var secretArn = $"arn:aws:secretsmanager:{region}:{accountId}:secret:{secret}-*";   // Secrets Manager appends six characters
        var machine = $"{sk}-intake";
        var machineArn = $"arn:aws:states:{region}:{accountId}:stateMachine:{machine}";
        var executionsArn = $"arn:aws:states:{region}:{accountId}:execution:{machine}:*";

        var (poller, check, fetch, record) = (Fn("poller"), Fn("check-run"), Fn("fetch"), Fn("record"));
        var reposVariable = IntakeRepositories.Encode(repositories);

        object ReadTheAppKey() => new { Sid = "ReadTheIntakeAppKey", Effect = "Allow", Action = new[] { "secretsmanager:GetSecretValue" }, Resource = secretArn };
        object Publish() => new { Sid = "PublishIntakeAlerts", Effect = "Allow", Action = new[] { "sns:Publish" }, Resource = topicArn };
        object WriteEvidence(string sid, params string[] prefixes) => new
        {
            Sid = sid, Effect = "Allow", Action = new[] { "s3:PutObject" },
            Resource = prefixes.Select(p => $"arn:aws:s3:::{evidence}/{p}*").ToArray(),
        };

        var functions = new List<DeployerFunction>
        {
            new(poller, DeployerHandlers.IntakePoller, $"{poller}-fn",
                DeployerPlanner.Combine(DeployerPlanner.Logs(region, accountId, poller), new object[]
                {
                    ReadTheAppKey(),
                    new
                    {
                        Sid = "ListTheEvidence", Effect = "Allow", Action = new[] { "s3:ListBucket" }, Resource = $"arn:aws:s3:::{evidence}",
                        Condition = new Dictionary<string, object>
                        {
                            ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = IntakeEvidence.DecisionPrefixes.Select(p => p + "*").ToArray() },
                        },
                    },
                    WriteEvidence("WriteAlarmsOnce", "alarms/"),
                    new { Sid = "ListAndStartTheIntake", Effect = "Allow", Action = new[] { "states:ListExecutions", "states:StartExecution" }, Resource = machineArn },
                    Publish(),
                }),
                new Dictionary<string, string>
                {
                    [IntakeEnvironment.Repositories] = reposVariable,
                    [IntakeEnvironment.EvidenceStore] = evidence,
                    [IntakeEnvironment.StateMachine] = machineArn,
                    [IntakeEnvironment.AppSecret] = secret,
                    [IntakeEnvironment.AlertsTopic] = topicArn,
                },
                TimeoutSeconds: 50, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: false),

            new(check, DeployerHandlers.IntakeCheckRun, $"{check}-fn",
                DeployerPlanner.Combine(DeployerPlanner.Logs(region, accountId, check), new[] { ReadTheAppKey() }),
                new Dictionary<string, string> { [IntakeEnvironment.Repositories] = reposVariable, [IntakeEnvironment.AppSecret] = secret },
                TimeoutSeconds: 60, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),

            new(fetch, DeployerHandlers.IntakeFetch, $"{fetch}-fn",
                DeployerPlanner.Combine(DeployerPlanner.Logs(region, accountId, fetch), new[]
                {
                    ReadTheAppKey(),
                    new { Sid = "StageIntoTheScratchBucket", Effect = "Allow", Action = new[] { "s3:PutObject" }, Resource = $"arn:aws:s3:::{scratch}/*" },
                    WriteEvidence("RecordWhatWasStaged", "staged/"),
                }),
                new Dictionary<string, string>
                {
                    [IntakeEnvironment.Repositories] = reposVariable,
                    [IntakeEnvironment.AppSecret] = secret,
                    [IntakeEnvironment.ScratchBucket] = scratch,
                    [IntakeEnvironment.EvidenceStore] = evidence,
                },
                TimeoutSeconds: FetchTimeoutSeconds, MemoryMb: FetchMemoryMb, DeployerPackages.Deployer, InvokedByStateMachine: true),

            new(record, DeployerHandlers.IntakeRecord, $"{record}-fn",
                DeployerPlanner.Combine(DeployerPlanner.Logs(region, accountId, record), new[]
                {
                    WriteEvidence("WriteTheOutcomeOnce", "taken-in/", "refused/", "faults/"),
                    Publish(),
                }),
                new Dictionary<string, string>
                {
                    [IntakeEnvironment.Repositories] = reposVariable,
                    [IntakeEnvironment.EvidenceStore] = evidence,
                    [IntakeEnvironment.AlertsTopic] = topicArn,
                },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),
        };

        var invoked = functions.Where(f => f.InvokedByStateMachine).Select(f => FnArn(f.Name)).ToArray();
        var machineRolePolicy = DeployerPlanner.Policy(new { Sid = "InvokeTheIntakeFunctions", Effect = "Allow", Action = new[] { "lambda:InvokeFunction" }, Resource = invoked });

        // Only the fetch function's role writes the scratch bucket (§3), whatever an identity policy grants; and only over TLS.
        var scratchPolicy = DeployerPlanner.Policy(
            new
            {
                Sid = "OnlyTheFetchFunctionWrites", Effect = "Deny", Principal = "*", Action = "s3:PutObject",   // a string: S3 returns a one-element array as one, and the read-back compares
                Resource = $"arn:aws:s3:::{scratch}/*",
                Condition = new Dictionary<string, object>
                {
                    ["ArnNotEquals"] = new Dictionary<string, object> { ["aws:PrincipalArn"] = $"arn:aws:iam::{accountId}:role/{fetch}-fn" },
                },
            },
            new
            {
                Sid = "TlsOnly", Effect = "Deny", Principal = "*", Action = "s3:*",
                Resource = new[] { $"arn:aws:s3:::{scratch}", $"arn:aws:s3:::{scratch}/*" },
                Condition = new Dictionary<string, object> { ["Bool"] = new Dictionary<string, object> { ["aws:SecureTransport"] = "false" } },
            });

        var alarms = new[]
        {
            new IntakeAlarm($"{poller}-errors", "an intake poll failed", poller, "Errors", "Sum", "GreaterThanOrEqualToThreshold", 1, 300, "notBreaching"),
            // A poller that stopped, by hand or otherwise, invokes nothing; missing data is breaching, so silence alarms.
            new IntakeAlarm($"{poller}-silent", "the intake poller has not run for five minutes", poller, "Invocations", "Sum", "LessThanThreshold", 1, 300, "breaching"),
            new IntakeAlarm($"{fetch}-errors", "an intake fetch failed", fetch, "Errors", "Sum", "GreaterThanOrEqualToThreshold", 1, 300, "notBreaching"),
        };

        var topicPolicy = DeployerPlanner.Policy(new
        {
            Sid = "OnlyTheIntakesAlarmsPublish", Effect = "Allow", Principal = new { Service = "cloudwatch.amazonaws.com" },
            Action = "sns:Publish", Resource = topicArn,
            Condition = new Dictionary<string, object>
            {
                ["ArnLike"] = new Dictionary<string, object> { ["aws:SourceArn"] = alarms.Select(a => $"arn:aws:cloudwatch:{region}:{accountId}:alarm:{a.Name}").ToArray() },
            },
        });

        return new IntakeBuildPlan(
            EvidenceStore: evidence,
            EvidenceStorePolicyStatementSid: "LzWriteOnce",
            ScratchBucket: scratch,
            ScratchBucketPolicy: scratchPolicy,
            ScratchExpirationDays: ScratchExpirationDays,
            AlertsTopicName: topicName,
            AlertsTopicArn: topicArn,
            AlertsTopicPolicy: topicPolicy,
            AppSecretName: secret,
            AppSecretArnPattern: secretArn,
            Functions: functions,
            StateMachineName: machine,
            StateMachineArn: machineArn,
            StateMachineRoleName: $"{machine}-machine",
            StateMachineRolePolicy: machineRolePolicy,
            StateMachineTrustPolicy: DeployerPlanner.ServiceTrustPolicy("states.amazonaws.com", accountId, $"arn:aws:states:{region}:{accountId}:stateMachine:*"),
            FunctionTrustPolicy: DeployerPlanner.ServiceTrustPolicy("lambda.amazonaws.com"),
            DenyPolicy: DeployerPlanner.DenyPolicyFor(),
            Definition: DefinitionFor(FnArn(check), FnArn(fetch), FnArn(record)),
            ScheduleRuleName: $"{sk}-intake-poll",
            ScheduleExpression: "rate(1 minute)",
            PollerFunctionName: poller,
            PollerReservedConcurrency: 1,
            PollerMaximumEventAgeSeconds: 60,
            Alarms: alarms,
            Repositories: repositories);
    }

    /// <summary>
    /// THE DRY INTAKE'S MACHINE, the dry part of V-A's <see cref="IntakePlan.Default"/>: CheckRun, then FetchAndStage, then the
    /// outcome — taken in when staged, refused when either step refused — and a fault, from any step, recorded and the
    /// execution failed. No Gate 1, push or record state exists in it, so none can be reached (V-B pushes nothing).
    /// </summary>
    public static string DefinitionFor(string checkFn, string fetchFn, string recordFn)
    {
        var payload = new Dictionary<string, object> { ["state.$"] = "$", ["executionName.$"] = "$$.Execution.Name" };
        object Transient() => new
        {
            ErrorEquals = new[] { "Lambda.ServiceException", "Lambda.AWSLambdaException", "Lambda.SdkClientException", "Lambda.TooManyRequestsException" },
            IntervalSeconds = 2, MaxAttempts = 3, BackoffRate = 2.0,
        };
        object S3Write() => new { ErrorEquals = new[] { "AmazonS3Exception" }, IntervalSeconds = 2, MaxAttempts = 3, BackoffRate = 2.0 };
        var toFault = new object[] { new { ErrorEquals = new[] { "States.ALL" }, Next = "MarkFault", ResultPath = "$.error" } };

        object Mark(string record, string next) => new { Type = "Pass", Result = record, ResultPath = "$.record", Next = next };
        object Record(string? next, bool end = false, object[]? catches = null) => end
            ? new { Type = "Task", Resource = recordFn, Parameters = payload, ResultPath = "$.recorded", Retry = new[] { Transient(), S3Write() }, End = true, Catch = catches ?? toFault }
            : new { Type = "Task", Resource = recordFn, Parameters = payload, ResultPath = "$.recorded", Retry = new[] { Transient(), S3Write() }, Next = next!, Catch = catches ?? toFault };

        var states = new Dictionary<string, object>
        {
            ["CheckRun"] = new { Type = "Task", Resource = checkFn, Parameters = payload, ResultPath = "$.check", Retry = new[] { Transient() }, Next = "RunVerdict", Catch = toFault },
            ["RunVerdict"] = new
            {
                Type = "Choice",
                Choices = new object[] { new { Variable = "$.check.verdict", StringEquals = "passed", Next = "FetchAndStage" } },
                Default = "MarkRefused",
            },
            ["FetchAndStage"] = new { Type = "Task", Resource = fetchFn, Parameters = payload, ResultPath = "$.fetch", Retry = new[] { Transient() }, Next = "FetchVerdict", Catch = toFault },
            ["FetchVerdict"] = new
            {
                Type = "Choice",
                Choices = new object[] { new { Variable = "$.fetch.verdict", StringEquals = "staged", Next = "MarkTakenIn" } },
                Default = "MarkRefused",
            },
            ["MarkTakenIn"] = Mark("taken-in", "RecordTakenIn"),
            ["RecordTakenIn"] = Record(null, end: true),
            ["MarkRefused"] = Mark("refused", "RecordRefused"),
            ["RecordRefused"] = Record(null, end: true),
            ["MarkFault"] = Mark("fault", "RecordFault"),
            ["RecordFault"] = Record("Faulted", catches: new object[] { new { ErrorEquals = new[] { "States.ALL" }, Next = "FaultNotRecorded", ResultPath = "$.recordError" } }),
            ["Faulted"] = new { Type = "Fail", Error = "IntakeFaulted", Cause = "recorded under faults/; the poller retries it" },
            ["FaultNotRecorded"] = new { Type = "Fail", Error = "FaultNotRecorded", Cause = "the fault could not be written; the poller alarms on an execution that closed with nothing recorded" },
        };

        return JsonSerializer.Serialize(new
        {
            Comment = "lz intake, dry (DecoupledCd.md §15.4, V-B): check, fetch and stage, record. Pushes nothing.",
            StartAt = "CheckRun",
            States = states,
        }, DeployerPlanner.Asl);
    }
}
