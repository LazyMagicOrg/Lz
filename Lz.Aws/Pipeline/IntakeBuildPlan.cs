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
    IReadOnlyList<IntakeRepository> Repositories,
    IntakeGate1Build? Gate1 = null);

/// <summary>
/// Gate 1's CodeBuild project (DecoupledCd.md §15.4, §3; V-C): privileged, so the docker and workflow tiers have a daemon; its
/// buildspec fixed here, never taken from the bundle; its role holding its own logs and its report group's reports and
/// nothing else - no S3, no secret, no push, no sign, no pipeline store.
/// </summary>
public sealed record IntakeGate1Build(
    string ProjectName, string ProjectArn, string RoleName, string RolePolicy, string TrustPolicy,
    string ReportGroupName, string ReportGroupArn, string LogGroupName, string Buildspec,
    string Image, string ComputeType, int TimeoutMinutes);

public static class IntakeBuildPlanner
{
    public const int ScratchExpirationDays = 1;
    public const int FetchTimeoutSeconds = 900;
    public const int FetchMemoryMb = 2048;
    public const int FetchEphemeralStorageMb = 4096;

    public const string Gate1Image = "aws/codebuild/standard:8.0";
    public const string Gate1ComputeType = "BUILD_GENERAL1_MEDIUM";
    public const int Gate1TimeoutMinutes = 60;

    // Gate 1's verdict waits while the build runs: 30 s × 130, past the build's own sixty-minute timeout, so a build that
    // runs too long ends TIMED_OUT, a fault, before the wait gives up.
    public const int Gate1PollSeconds = 30;
    public const int Gate1PollAttempts = 130;

    public static string EvidenceStoreFor(SystemConfig c) => $"{c.SystemKey}-intake-evidence-{c.SystemSuffix}";
    public static string ScratchBucketFor(SystemConfig c) => $"{c.SystemKey}-intake-scratch-{c.SystemSuffix}";
    public static string AppSecretFor(SystemConfig c) => $"{c.SystemKey}/cicd/intake-app";

    /// <summary>The repositories the intake takes: those with an <c>Intake</c> mode, all of whose ids the validator required.</summary>
    public static IReadOnlyList<IntakeRepository> RepositoriesFor(SystemConfig config)
        => (config.Pipeline?.Repositories ?? new List<PipelineRepositoryConfig>())
            .Where(r => !string.IsNullOrEmpty(r.Intake))
            .Select(r => new IntakeRepository(
                r.Repo!, r.RepositoryId!.Value, r.OwnerId!.Value, r.WorkflowPath!, r.Intake!, r.Class!,
                (IReadOnlyList<string>?)r.Artifacts ?? Array.Empty<string>(),
                r.TestBundle is { } bundle && r.Gate1 is { } g
                    ? new IntakeGate1(bundle, g.Run!, g.Filter!, g.SdkVersion!, g.SdkSha512!,
                        (g.Images ?? new()).Select(i => new IntakeImage(i.From!, i.As!)).ToList(),
                        g.Environment is { Count: > 0 } env ? new Dictionary<string, string>(env) : null,
                        g.BuildRoot)
                    : null))
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

        // GATE 1 (V-C), only where a repository has one.
        IntakeGate1Build? gate1 = null;
        string? gate1StartArn = null, gate1VerdictArn = null;
        if (repositories.Any(r => r.Gate1 is not null))
        {
            var project = Fn("gate1");
            var projectArn = $"arn:aws:codebuild:{region}:{accountId}:project/{project}";
            var reportGroup = $"{project}-tests";
            var reportGroupArn = $"arn:aws:codebuild:{region}:{accountId}:report-group/{reportGroup}";
            var logGroup = $"/aws/codebuild/{project}";
            var (start, verdict) = (Fn("gate1-start"), Fn("gate1-verdict"));

            gate1 = new IntakeGate1Build(
                project, projectArn, $"{project}-build",
                DeployerPlanner.Policy(
                    new
                    {
                        Sid = "WriteItsOwnLogs", Effect = "Allow",
                        Action = new[] { "logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents" },
                        Resource = new[] { $"arn:aws:logs:{region}:{accountId}:log-group:{logGroup}", $"arn:aws:logs:{region}:{accountId}:log-group:{logGroup}:*" },
                    },
                    new
                    {
                        Sid = "WriteItsOwnReports", Effect = "Allow",
                        Action = new[] { "codebuild:CreateReport", "codebuild:UpdateReport", "codebuild:BatchPutTestCases" },
                        Resource = reportGroupArn,
                    }),
                DeployerPlanner.ServiceTrustPolicy("codebuild.amazonaws.com"),
                reportGroup, reportGroupArn, logGroup, BuildspecFor(reportGroupArn),
                Gate1Image, Gate1ComputeType, Gate1TimeoutMinutes);

            functions.Add(new(start, DeployerHandlers.IntakeGate1Start, $"{start}-fn",
                DeployerPlanner.Combine(DeployerPlanner.Logs(region, accountId, start), new object[]
                {
                    // To sign the one URL Gate 1 reads its bundle by; Gate 1's own role holds no S3 right.
                    new { Sid = "PresignTheTestBundle", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = $"arn:aws:s3:::{scratch}/*" },
                    new { Sid = "StartGate1", Effect = "Allow", Action = new[] { "codebuild:StartBuild" }, Resource = projectArn },
                }),
                new Dictionary<string, string>
                {
                    [IntakeEnvironment.Repositories] = reposVariable,
                    [IntakeEnvironment.ScratchBucket] = scratch,
                    [IntakeEnvironment.Gate1Project] = project,
                },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true));

            functions.Add(new(verdict, DeployerHandlers.IntakeGate1Verdict, $"{verdict}-fn",
                DeployerPlanner.Combine(DeployerPlanner.Logs(region, accountId, verdict), new[]
                {
                    new { Sid = "ReadGate1", Effect = "Allow", Action = new[] { "codebuild:BatchGetBuilds" }, Resource = new[] { projectArn } },
                    new { Sid = "ReadItsReports", Effect = "Allow", Action = new[] { "codebuild:BatchGetReports", "codebuild:DescribeTestCases" }, Resource = new[] { reportGroupArn } },
                    WriteEvidence("WriteTheVerdictOnce", "gate1/"),
                }),
                new Dictionary<string, string> { [IntakeEnvironment.EvidenceStore] = evidence },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true));

            (gate1StartArn, gate1VerdictArn) = (FnArn(start), FnArn(verdict));
        }

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
            Definition: DefinitionFor(FnArn(check), FnArn(fetch), FnArn(record), gate1StartArn, gate1VerdictArn),
            ScheduleRuleName: $"{sk}-intake-poll",
            ScheduleExpression: "rate(1 minute)",
            PollerFunctionName: poller,
            PollerReservedConcurrency: 1,
            PollerMaximumEventAgeSeconds: 60,
            Alarms: alarms,
            Repositories: repositories,
            Gate1: gate1);
    }

    /// <summary>
    /// GATE 1'S BUILDSPEC, fixed in the project (§15.4), never from the bundle. What it runs comes in the build's variables,
    /// from the build-side file: the .NET SDK by exact version, checked against Microsoft's published SHA-512 before use;
    /// the images the tests start, pulled by digest from a mirror and tagged as the tests expect (no Docker Hub limit); the
    /// bundle by its presigned URL, checked against its SHA-256 before it is unzipped; then the filtered tests, reported to
    /// the report group as TRX. A failing test fails the build; the report is processed either way.
    /// </summary>
    public static string BuildspecFor(string reportGroupArn) => $$"""
        version: 0.2
        env:
          shell: bash
        phases:
          install:
            commands:
              - curl -fsSL --retry 3 -o /tmp/sdk.tgz "https://builds.dotnet.microsoft.com/dotnet/Sdk/${SDK_VERSION}/dotnet-sdk-${SDK_VERSION}-linux-x64.tar.gz"
              - echo "${SDK_SHA512}  /tmp/sdk.tgz" | sha512sum -c -
              - mkdir -p /opt/gate1-dotnet && tar -xzf /tmp/sdk.tgz -C /opt/gate1-dotnet
              - printf '%s\n' "$GATE1_IMAGES" | while read -r from as; do [ -z "$from" ] || { docker pull "$from" && docker tag "$from" "$as"; } || exit 1; done
          pre_build:
            commands:
              - curl -fsSL --retry 3 -o /tmp/bundle.zip "$BUNDLE_URL"
              - echo "${BUNDLE_SHA256}  /tmp/bundle.zip" | sha256sum -c -
              - mkdir -p "$GATE1_ROOT" && unzip -q /tmp/bundle.zip -d "$GATE1_ROOT"
          build:
            commands:
              - cd "$GATE1_ROOT/$(dirname "$GATE1_RUN")" && DOTNET_ROOT=/opt/gate1-dotnet DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 /opt/gate1-dotnet/dotnet test "$(basename "$GATE1_RUN")" --filter "$GATE1_FILTER" --logger "trx;LogFileName=gate1.trx" --results-directory /tmp/gate1-results
        reports:
          {{reportGroupArn}}:
            files:
              - gate1.trx
            base-directory: /tmp/gate1-results
            file-format: VISUALSTUDIOTRX
        """;

    /// <summary>
    /// THE DRY INTAKE'S MACHINE, the dry part of V-A's <see cref="IntakePlan.Default"/>: CheckRun, then FetchAndStage, then the
    /// outcome — taken in when staged, refused when either step refused — and a fault, from any step, recorded and the
    /// execution failed. No Gate 1, push or record state exists in it, so none can be reached (V-B pushes nothing).
    /// </summary>
    public static string DefinitionFor(string checkFn, string fetchFn, string recordFn, string? gate1StartFn = null, string? gate1VerdictFn = null)
    {
        var gate1 = gate1StartFn is not null && gate1VerdictFn is not null;
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
                Choices = new object[] { new { Variable = "$.fetch.verdict", StringEquals = "staged", Next = gate1 ? "HasGate1" : "MarkTakenIn" } },
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

        if (gate1)
        {
            // GATE 1 (V-C): only a repository whose fetch staged a test bundle has one; the rest go straight to the outcome.
            states["HasGate1"] = new
            {
                Type = "Choice",
                Choices = new object[] { new { Variable = "$.fetch.tests", IsPresent = true, Next = "Gate1Start" } },
                Default = "MarkTakenIn",
            };
            states["Gate1Start"] = new { Type = "Task", Resource = gate1StartFn!, Parameters = payload, ResultPath = "$.gate1Start", Retry = new[] { Transient() }, Next = "Gate1Verdict", Catch = toFault };
            states["Gate1Verdict"] = new
            {
                Type = "Task", Resource = gate1VerdictFn!, Parameters = payload, ResultPath = "$.gate1",
                Retry = new object[]
                {
                    new { ErrorEquals = new[] { nameof(Gate1StillRunning) }, IntervalSeconds = Gate1PollSeconds, MaxAttempts = Gate1PollAttempts, BackoffRate = 1.0 },
                    Transient(),
                },
                Next = "Gate1Outcome", Catch = toFault,
            };
            states["Gate1Outcome"] = new
            {
                Type = "Choice",
                Choices = new object[] { new { Variable = "$.gate1.verdict", StringEquals = "passed", Next = "MarkTakenIn" } },
                Default = "MarkRefused",
            };
        }

        return JsonSerializer.Serialize(new
        {
            Comment = gate1
                ? "lz intake, dry (DecoupledCd.md §15.4, V-B, V-C): check, fetch and stage, Gate 1, record. Pushes nothing."
                : "lz intake, dry (DecoupledCd.md §15.4, V-B): check, fetch and stage, record. Pushes nothing.",
            StartAt = "CheckRun",
            States = states,
        }, DeployerPlanner.Asl);
    }
}
