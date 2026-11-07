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

    /// <summary>
    /// The /tmp a function is given, or null for Lambda's default (512 MB). The two that hold an image archive: the fetch, which
    /// downloads and checks it, and the publish, which reads the staged zip back before pushing it - left at the default until
    /// 2026-11-05, when the tooling image (795 MB, P3 B) was found not to fit where the aiphost (132 MB) always had.
    /// </summary>
    public static int? EphemeralStorageMbFor(string handler)
        => handler == DeployerHandlers.IntakeFetch || handler == DeployerHandlers.IntakePublish ? FetchEphemeralStorageMb : null;

    public const string Gate1Image = "aws/codebuild/standard:8.0";
    public const string Gate1ComputeType = "BUILD_GENERAL1_MEDIUM";
    public const int Gate1TimeoutMinutes = 60;

    // Gate 1's verdict waits while the build runs: 30 s × 130, past the build's own sixty-minute timeout, so a build that
    // runs too long ends TIMED_OUT, a fault, before the wait gives up.
    public const int Gate1PollSeconds = 30;
    public const int Gate1PollAttempts = 130;
    // A config bundle waits for the tooling image that carries its lz (P3 C): a minute apart, for half an hour.
    public const int Gate1WaitSeconds = 60;
    public const int Gate1WaitAttempts = 30;

    public static string EvidenceStoreFor(SystemConfig c) => $"{c.SystemKey}-intake-evidence-{PipelineBootstrapPlanner.BuildSuffixFor(c)}";
    public static string ScratchBucketFor(SystemConfig c) => $"{c.SystemKey}-intake-scratch-{PipelineBootstrapPlanner.BuildSuffixFor(c)}";
    public static string AppSecretFor(SystemConfig c) => $"{c.SystemKey}/cicd/intake-app";

    /// <summary>The record function's role: the one principal an environment's outcomes topic admits from the build account.</summary>
    public static string RecordRoleName(string systemKey) => $"{systemKey}-intake-record-fn";

    /// <summary>The publish function's role: the one intake principal that writes the record and artifact stores and pushes images
    /// (P3 H names it in their Denies).</summary>
    public static string PublishRoleName(string systemKey) => $"{systemKey}-intake-publish-fn";

    /// <summary>The repositories the intake takes: those with an <c>Intake</c> mode, all of whose ids the validator required.</summary>
    public static IReadOnlyList<IntakeRepository> RepositoriesFor(SystemConfig config)
        => (config.Pipeline?.Repositories ?? new List<PipelineRepositoryConfig>())
            .Where(r => !string.IsNullOrEmpty(r.Intake))
            .Select(r => new IntakeRepository(
                r.Repo!, r.RepositoryId!.Value, r.OwnerId!.Value, r.WorkflowPath!, r.Intake!, r.Class!,
                (IReadOnlyList<string>?)r.Artifacts ?? Array.Empty<string>(),
                // THE TOOLING IMAGE'S SMOKE CHECK (P3 B): its bundle is the image the run built.
                // AND THE CONFIG BUNDLE'S CHECK (P3 C): its bundle is the config bundle.
                r.Gate1 is { Kind: "smoke" or "config" } own && r.Artifacts is { Count: 1 } checkedArtifact
                    ? new IntakeGate1(checkedArtifact[0], "", "", "", "", Array.Empty<IntakeImage>(), null, null, Kind: own.Kind!)
                : r.TestBundle is { } collected && r.Gate1 is { Kind: "collection" } c
                    ? new IntakeGate1(collected, "", "", "", "", Array.Empty<IntakeImage>(), null, c.BuildRoot, Kind: "collection")
                : r.TestBundle is { } npmBundle && r.Gate1 is { Kind: "vitest" } v
                    ? new IntakeGate1(npmBundle, "", "", "", "", Array.Empty<IntakeImage>(), null, v.BuildRoot, Kind: "vitest")
                : r.TestBundle is { } bundle && r.Gate1 is { } g
                    ? new IntakeGate1(bundle, g.Run!, g.Filter!, g.SdkVersion!, g.SdkSha512!,
                        (g.Images ?? new()).Select(i => new IntakeImage(i.From!, i.As!)).ToList(),
                        g.Environment is { Count: > 0 } env ? new Dictionary<string, string>(env) : null,
                        g.BuildRoot)
                    : null,
                r.BuiltFrom, r.Sbom,
                r.Gate1 is { Kind: "site" } site
                    ? new IntakeSiteCheck(
                        (r.Artifacts ?? new()).ToDictionary(a => a, a => SiteArtifacts.EnvironmentOf(a)
                            ?? throw new InvalidOperationException($"{r.Repo}: '{a}' is not site-{{environment}}")),
                        (IReadOnlyList<string>?)site.Indexed ?? Array.Empty<string>(),
                        (IReadOnlyList<string>?)site.ThirdPartyHosts ?? Array.Empty<string>())
                    : null))
            .ToList();

    /// <summary>The intake's plan, or null when no repository has an <c>Intake</c> mode: a system that has not opted in gets none.</summary>
    public static IntakeBuildPlan? Plan(SystemConfig config, string accountId)
    {
        var repositories = RepositoriesFor(config);
        if (repositories.Count == 0) return null;
        if (repositories.FirstOrDefault(r => r.Mode is not ("dry" or "live")) is { } unknown)
            throw new InvalidOperationException($"{unknown.Repo} is Intake: {unknown.Mode}, not dry or live.");
        // NOTHING TO FETCH: no artifact and no test bundle. A tests repository (V-E) names no artifact; its test bundle is the fetch.
        if (repositories.FirstOrDefault(r => r.Artifacts.Count == 0 && r.Gate1 is null) is { } none)
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
        if ($"{record}-fn" != RecordRoleName(sk))
            throw new InvalidOperationException("the intake's record role diverged from RecordRoleName, which each outcomes topic admits.");

        // THE OUTCOMES LIST (the chain's first environment's), where it has one: the record function tells it of each build
        // refused or undecided, in that environment's account, which admits this one role.
        var outcomesTo = config.Pipeline?.IntakeOutcomesTo;
        var outcomesTopic = outcomesTo is null ? null : DeployOutcomes.TopicArn(region, outcomesTo.AccountId, sk, outcomesTo.Environment);

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
                }.Concat(outcomesTopic is null ? Array.Empty<object>() : new object[]
                {
                    new { Sid = "TellTheOutcomesList", Effect = "Allow", Action = new[] { "sns:Publish" }, Resource = outcomesTopic },
                }).ToArray()),
                outcomesTopic is null
                    ? new Dictionary<string, string>
                    {
                        [IntakeEnvironment.Repositories] = reposVariable,
                        [IntakeEnvironment.EvidenceStore] = evidence,
                        [IntakeEnvironment.AlertsTopic] = topicArn,
                    }
                    : new Dictionary<string, string>
                    {
                        [IntakeEnvironment.Repositories] = reposVariable,
                        [IntakeEnvironment.EvidenceStore] = evidence,
                        [IntakeEnvironment.AlertsTopic] = topicArn,
                        [IntakeEnvironment.OutcomesTopic] = outcomesTopic,
                        [IntakeEnvironment.OutcomesEnvironment] = outcomesTo!.Environment,
                    },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true),
        };

        // GATE 1 (V-C), only where a repository has one: a test bundle's in CodeBuild, a site's in a function (V-24).
        IntakeGate1Build? gate1 = null;
        string? gate1StartArn = null, gate1VerdictArn = null, siteCheckArn = null, publishArn = null;
        var tests = repositories.Any(r => r.Gate1 is not null);
        var sites = repositories.Any(r => r.Site is not null);
        if (tests)
        {
            var project = Fn("gate1");
            var projectArn = $"arn:aws:codebuild:{region}:{accountId}:project/{project}";
            var reportGroup = $"{project}-tests";
            var reportGroupArn = $"arn:aws:codebuild:{region}:{accountId}:report-group/{reportGroup}";
            var logGroup = $"/aws/codebuild/{project}";
            var (start, verdict) = (Fn("gate1-start"), Fn("gate1-verdict"));

            // A CONFIG BUNDLE'S CHECK RUNS IN THE TOOLING IMAGE (P3 C): Gate1Start reads the tooling records to choose the image
            // that carries the bundle's lz, and the build pulls it from this registry.
            var configCheck = repositories.Any(r => r.Gate1 is { Kind: "config" });
            var toolingEntry = repositories.FirstOrDefault(r => r.Class == "tooling" && r.Artifacts.Count == 1);
            if (configCheck && toolingEntry is null)
                throw new InvalidOperationException(
                    "lz bootstrappipeline refuses: a repository's Gate 1 is a config check, which runs in the tooling image, and the " +
                    "intake takes no tooling repository to choose that image from.");
            var toolingRepositoryName = toolingEntry is null ? null : EcrRepositoryNaming.For(config, toolingEntry.Artifacts[0]);
            var toolingRecordPrefix = toolingEntry is null ? null : BuildRecordFormat.PrefixFor("tooling", toolingEntry.Repo);
            var gate1RecordStore = PipelineBootstrapPlanner.BuildRecordStoreFor(sk, PipelineBootstrapPlanner.BuildSuffixFor(config));

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
                    },
                    // The config check pulls the tooling image to run lz in (P3 C); nothing else in the registry.
                    configCheck
                        ? new { Sid = "SignInToTheRegistry", Effect = "Allow", Action = new[] { "ecr:GetAuthorizationToken" }, Resource = (object)"*" }
                        : null,
                    configCheck
                        ? new
                        {
                            Sid = "PullTheToolingImage", Effect = "Allow",
                            Action = new[] { "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:BatchCheckLayerAvailability" },
                            Resource = (object)$"arn:aws:ecr:{region}:{accountId}:repository/{toolingRepositoryName}",
                        }
                        : null),
                DeployerPlanner.ServiceTrustPolicy("codebuild.amazonaws.com"),
                reportGroup, reportGroupArn, logGroup, BuildspecFor(reportGroupArn),
                Gate1Image, Gate1ComputeType, Gate1TimeoutMinutes);

            functions.Add(new(start, DeployerHandlers.IntakeGate1Start, $"{start}-fn",
                DeployerPlanner.Combine(DeployerPlanner.Logs(region, accountId, start), new object[]
                {
                    // To sign the one URL Gate 1 reads its bundle by; Gate 1's own role holds no S3 right.
                    new { Sid = "PresignTheTestBundle", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = $"arn:aws:s3:::{scratch}/*" },
                    new { Sid = "StartGate1", Effect = "Allow", Action = new[] { "codebuild:StartBuild" }, Resource = projectArn },
                }.Concat(configCheck
                    ? new object[]
                    {
                        // To choose the tooling image that carries a config bundle's lz (P3 C): the tooling records, nothing else.
                        new
                        {
                            Sid = "ListTheToolingRecords", Effect = "Allow", Action = new[] { "s3:ListBucket" }, Resource = $"arn:aws:s3:::{gate1RecordStore}",
                            Condition = new { StringLike = new Dictionary<string, string> { ["s3:prefix"] = $"{toolingRecordPrefix}*" } },
                        },
                        new { Sid = "ReadTheToolingRecords", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = $"arn:aws:s3:::{gate1RecordStore}/{toolingRecordPrefix}*" },
                    }
                    : Array.Empty<object>()).ToArray()),
                configCheck
                    ? new Dictionary<string, string>
                    {
                        [IntakeEnvironment.Repositories] = reposVariable,
                        [IntakeEnvironment.ScratchBucket] = scratch,
                        [IntakeEnvironment.Gate1Project] = project,
                        [IntakeEnvironment.BuildRecordStore] = gate1RecordStore,
                        [IntakeEnvironment.ToolingRecordPrefix] = toolingRecordPrefix!,
                        [IntakeEnvironment.ToolingRepository] = $"{accountId}.dkr.ecr.{region}.amazonaws.com/{toolingRepositoryName}",
                    }
                    : new Dictionary<string, string>
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
                    // Gate 2's lists, which Gate 1 prints into its own log (Gate2Lists).
                    new { Sid = "ReadItsLog", Effect = "Allow", Action = new[] { "logs:GetLogEvents" }, Resource = new[] { $"arn:aws:logs:{region}:{accountId}:log-group:{logGroup}:log-stream:*" } },
                    WriteEvidence("WriteTheVerdictOnce", "gate1/"),
                }),
                new Dictionary<string, string> { [IntakeEnvironment.EvidenceStore] = evidence },
                TimeoutSeconds: 30, MemoryMb: 512, DeployerPackages.Deployer, InvokedByStateMachine: true));

            (gate1StartArn, gate1VerdictArn) = (FnArn(start), FnArn(verdict));
        }

        if (sites)
        {
            // A SITE'S GATE 1 (V-24): lz's own check over the staged bundles, which runs no repository code, so a function
            // reads them itself; it writes only its verdict.
            var siteCheck = Fn("site-check");
            functions.Add(new(siteCheck, DeployerHandlers.IntakeSiteCheck, $"{siteCheck}-fn",
                DeployerPlanner.Combine(DeployerPlanner.Logs(region, accountId, siteCheck), new[]
                {
                    new { Sid = "ReadTheStagedSites", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = new[] { $"arn:aws:s3:::{scratch}/*" } },
                    WriteEvidence("WriteTheVerdictOnce", "gate1/"),
                }),
                new Dictionary<string, string>
                {
                    [IntakeEnvironment.Repositories] = reposVariable,
                    [IntakeEnvironment.ScratchBucket] = scratch,
                    [IntakeEnvironment.EvidenceStore] = evidence,
                },
                TimeoutSeconds: FetchTimeoutSeconds, MemoryMb: FetchMemoryMb, DeployerPackages.Deployer, InvokedByStateMachine: true));
            siteCheckArn = FnArn(siteCheck);
        }

        if (tests || sites)
        {

            // THE PUBLISH (V-D), after Gate 1's pass: a no-op for a dry repository, so the machine is one shape for all.
            var publish = Fn("publish");
            if ($"{publish}-fn" != PublishRoleName(sk))
                throw new InvalidOperationException("the intake's publish role diverged from PublishRoleName, which the stores' Deny admits.");
            var recordStore = PipelineBootstrapPlanner.BuildRecordStoreFor(sk, PipelineBootstrapPlanner.BuildSuffixFor(config));
            var artifactStore = PipelineBootstrapPlanner.ArtifactStoreFor(sk, PipelineBootstrapPlanner.BuildSuffixFor(config));
            var targets = PublishTargetsFor(config, accountId, repositories);
            var images = repositories.Where(r => BuildRecordFormat.IsImageClass(r.Class)).ToList();
            var publishGrants = new List<object>
            {
                new { Sid = "ReadTheStagedFiles", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = $"arn:aws:s3:::{scratch}/*" },
                new
                {
                    Sid = "ReadAndWriteItsRecordsOnce", Effect = "Allow", Action = new[] { "s3:GetObject", "s3:PutObject" },
                    Resource = repositories.SelectMany(r => r.PublishedClasses.Select(c => $"arn:aws:s3:::{recordStore}/{BuildRecordFormat.PrefixFor(c, r.Repo)}*")).Distinct().ToArray(),
                },
                // Existence is read by listing the exact key, in both stores (S3PublishStores), so each lists its own prefixes.
                new
                {
                    Sid = "ListItsRecords", Effect = "Allow", Action = new[] { "s3:ListBucket" }, Resource = $"arn:aws:s3:::{recordStore}",
                    Condition = new Dictionary<string, object>
                    {
                        ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = OwnPrefixes(repositories) },
                    },
                },
                new
                {
                    Sid = "ListItsArtifacts", Effect = "Allow", Action = new[] { "s3:ListBucket" }, Resource = $"arn:aws:s3:::{artifactStore}",
                    Condition = new Dictionary<string, object>
                    {
                        ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = OwnPrefixes(repositories) },
                    },
                },
                new
                {
                    Sid = "WriteItsArtifactsOnce", Effect = "Allow", Action = new[] { "s3:GetObject", "s3:GetObjectVersion", "s3:PutObject" },
                    Resource = repositories.SelectMany(r => r.PublishedClasses.Select(c => $"arn:aws:s3:::{artifactStore}/{BuildRecordFormat.PrefixFor(c, r.Repo)}*")).Distinct().ToArray(),
                },
            };
            if (targets.Count > 0)
            {
                publishGrants.Add(new
                {
                    Sid = "PushToItsPipelineRepositories", Effect = "Allow",
                    Action = new[] { "ecr:DescribeImages", "ecr:BatchCheckLayerAvailability", "ecr:InitiateLayerUpload", "ecr:UploadLayerPart", "ecr:CompleteLayerUpload", "ecr:PutImage", "ecr:DescribeImageSigningStatus" },
                    Resource = targets.Select(t => $"arn:aws:ecr:{region}:{accountId}:repository/{t.Repository}").Distinct().ToArray(),
                });
                publishGrants.Add(new
                {
                    Sid = "SignItsPushes", Effect = "Allow", Action = new[] { "signer:SignPayload" },
                    Resource = targets.Select(t => t.SigningProfileArn).Distinct().ToArray(),
                });
            }
            functions.Add(new(publish, DeployerHandlers.IntakePublish, $"{publish}-fn",
                DeployerPlanner.Combine(DeployerPlanner.Logs(region, accountId, publish), publishGrants.ToArray()),
                new Dictionary<string, string>
                {
                    [IntakeEnvironment.Repositories] = reposVariable,
                    [IntakeEnvironment.ScratchBucket] = scratch,
                    [IntakeEnvironment.PublishTargets] = PublishTargets.Encode(targets),
                    [IntakeEnvironment.BuildRecordStore] = recordStore,
                    [IntakeEnvironment.ArtifactStore] = artifactStore,
                },
                TimeoutSeconds: FetchTimeoutSeconds, MemoryMb: FetchMemoryMb, DeployerPackages.Deployer, InvokedByStateMachine: true));
            publishArn = FnArn(publish);
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
            Definition: DefinitionFor(FnArn(check), FnArn(fetch), FnArn(record), gate1StartArn, gate1VerdictArn, publishArn, siteCheckArn),
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
    /// the report group as TRX. A failing test fails the build; the report is processed either way. Before all of it, the
    /// log's header: what the build tests (<see cref="Gate1Header"/>), printed from its INTAKE_ variables and trusted for nothing.
    /// A COLLECTION CHECK (GATE1_KIND collection, the browser tests' Gate 1, §15.4) skips the SDK, the images and the test run:
    /// its specs' own dependencies installed before BUILD, then <c>playwright test --list --forbid-only</c>, all and @gate, and
    /// one GATE1-LIST-COLLECTED line with both counts, which <see cref="Gate1CollectionJudge"/> reads. It writes no report.
    /// A VITEST RUN (GATE1_KIND vitest, a Node repository's own tests) skips the SDK and the images too: npm ci before BUILD,
    /// then <c>vitest run</c> with a JSON report, and one GATE1-LIST-VITEST line with its counts - printed whether the tests passed
    /// or not - which <see cref="Gate1VitestJudge"/> reads; the build ends as vitest did. It writes no report either.
    /// A SMOKE CHECK (GATE1_KIND smoke, the tooling image's Gate 1, P3 B) skips the SDK and the images: its bundle is the run's
    /// image archive, which it loads and runs - first giving the OCI layout the Docker-style manifest.json CodeBuild's Docker needs to load it
    /// ("invalid archive: does not contain a manifest.json", the second live run, 2026-11-06), written from its own index, the
    /// blobs left as they are - - lz at the version the image says it carries, the plugin loaded and its commands
    /// present, the Pulumi aws provider, the AWS CLI, LZ_CREDENTIALS=ambient - and one GATE1-LIST-SMOKE line with the counts,
    /// each failed check named before it, which <see cref="Gate1SmokeJudge"/> reads. It writes no report.
    /// A CONFIG CHECK (GATE1_KIND config, the config bundle's Gate 1, P3 C) unzips the bundle, signs in to this registry and
    /// pulls the tooling image Gate1Start chose (GATE1_TOOLING_IMAGE, the one carrying the bundle's lz) - a failure there decided
    /// nothing, and is a fault - then runs the edge tests (node --test) and, for every environment the bundle holds a system
    /// config for, lz validateconfig in that image with the bundle as _Dev and LZ_CREDENTIALS unset, printing one
    /// GATE1-LIST-CONFIG line, each failed check named before it, which <see cref="Gate1SmokeJudge"/> reads.
    /// </summary>
    public static string BuildspecFor(string reportGroupArn) => $$"""
        version: 0.2
        env:
          shell: bash
        phases:
          install:
            commands:
              - printf 'GATE1 %s run %s attempt %s, commit %s (%s)\nGATE1 intake execution %s\nGATE1 tests %s %s (the bundle under test)\n' "$INTAKE_REPO" "$INTAKE_RUN_ID" "$INTAKE_RUN_ATTEMPT" "$INTAKE_COMMIT" "$INTAKE_WORKFLOW_REF" "$INTAKE_EXECUTION" "$INTAKE_TESTS" "$BUNDLE_SHA256" && printf '%s\n' "$INTAKE_ARTIFACTS" | while read -r name sha; do [ -z "$name" ] || echo "GATE1 staged $name $sha"; done
              - test "$GATE1_KIND" != tests || curl -fsSL --retry 3 -o /tmp/sdk.tgz "https://builds.dotnet.microsoft.com/dotnet/Sdk/${SDK_VERSION}/dotnet-sdk-${SDK_VERSION}-linux-x64.tar.gz"
              - test "$GATE1_KIND" != tests || echo "${SDK_SHA512}  /tmp/sdk.tgz" | sha512sum -c -
              - test "$GATE1_KIND" != tests || { mkdir -p /opt/gate1-dotnet && tar -xzf /tmp/sdk.tgz -C /opt/gate1-dotnet; }
              - test "$GATE1_KIND" != tests || printf '%s\n' "$GATE1_IMAGES" | while read -r from as; do [ -z "$from" ] || { docker pull "$from" && docker tag "$from" "$as"; } || exit 1; done
          pre_build:
            commands:
              - curl -fsSL --retry 3 -o /tmp/bundle.zip "$BUNDLE_URL"
              - echo "${BUNDLE_SHA256}  /tmp/bundle.zip" | sha256sum -c -
              - mkdir -p "$GATE1_ROOT" && unzip -q /tmp/bundle.zip -d "$GATE1_ROOT"
              - if [ "$GATE1_KIND" = collection ] || [ "$GATE1_KIND" = vitest ]; then cd "$GATE1_ROOT" && npm ci --no-audit --no-fund; fi
          build:
            commands:
              - test "$GATE1_KIND" != tests || { set -o pipefail; cd "$GATE1_ROOT/$(dirname "$GATE1_RUN")" && printf '%s\n' "$GATE1_LISTS" | while read -r name filter; do [ -z "$name" ] && continue; echo "GATE1-LIST-BEGIN $name"; DOTNET_ROOT=/opt/gate1-dotnet DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 /opt/gate1-dotnet/dotnet test "$(basename "$GATE1_RUN")" --list-tests --filter "$filter" | sed -n "s/^    /GATE1-LIST $name /p" || exit 1; echo "GATE1-LIST-END $name"; done; }
              - test "$GATE1_KIND" != tests || { cd "$GATE1_ROOT/$(dirname "$GATE1_RUN")" && DOTNET_ROOT=/opt/gate1-dotnet DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 /opt/gate1-dotnet/dotnet test "$(basename "$GATE1_RUN")" --filter "$GATE1_FILTER" --logger "trx;LogFileName=gate1.trx" --results-directory /tmp/gate1-results; }
              - if [ "$GATE1_KIND" = collection ]; then set -o pipefail; cd "$GATE1_ROOT" && total=$(npx playwright test --list --forbid-only | awk '/^Total/{print $2}') && gate=$(npx playwright test --list --forbid-only --grep @gate | awk '/^Total/{print $2}') && echo "GATE1-LIST-COLLECTED total ${total:-0} gate ${gate:-0}" || { echo "GATE1-LIST-COLLECTED failed"; exit 1; }; fi
              - if [ "$GATE1_KIND" = vitest ]; then cd "$GATE1_ROOT" && { npx vitest run --reporter=default --reporter=json --outputFile=/tmp/gate1-vitest.json; status=$?; node -e 'const r=JSON.parse(require("fs").readFileSync("/tmp/gate1-vitest.json","utf8"));console.log(["GATE1-LIST-VITEST","total",r.numTotalTests,"passed",r.numPassedTests,"failed",r.numFailedTests,"suites-failed",r.numFailedTestSuites].join(" "))' || echo "GATE1-LIST-VITEST unreadable"; exit $status; }; fi
              - if [ "$GATE1_KIND" = smoke ]; then cd "$GATE1_ROOT" && archive=$(ls *.tar | head -1) && mkdir -p /tmp/smoke && tar -xf "$GATE1_ROOT/$archive" -C /tmp/smoke && cd /tmp/smoke && python3 -c "import json;i=json.load(open('index.json'));b=lambda d:'blobs/'+d.replace(':','/');r=lambda m:r(json.load(open(b([x for x in m['manifests'] if x.get('platform',dict()).get('os')!='unknown'][0]['digest'])))) if 'manifests' in m else m;m=r(i);json.dump([dict(Config=b(m['config']['digest']),RepoTags=['gate1/smoke:image'],Layers=[b(l['digest']) for l in m['layers']])],open('manifest.json','w'))" && tar -cf /tmp/smoke.tar manifest.json blobs && id=$(docker load -i /tmp/smoke.tar | awk '/^Loaded image/{print $NF}' | tail -1) && [ -n "$id" ] && pin=$(docker run --rm --entrypoint cat "$id" /opt/lz-version 2>/dev/null || true) && checks=0 && failed=0 && c() { checks=$((checks+1)); [ "$2" = 0 ] || { failed=$((failed+1)); echo "GATE1-LIST-SMOKE-FAILED $1"; }; } && { [ -n "$pin" ] && docker run --rm "$id" --version | grep -Eq "lz[.]cli:[[:space:]]*${pin}[+]"; c lz-at-its-pin $?; docker run --rm "$id" --version | grep -Eq "loaded:[[:space:]]*/opt/scutara/plugin/Deploy[.]dll"; c plugin-loaded $?; docker run --rm "$id" --help | grep -q "System Specific"; c plugin-commands $?; docker run --rm --entrypoint pulumi "$id" plugin ls | grep -Eq "^aws +resource"; c pulumi-aws-provider $?; docker run --rm --entrypoint aws "$id" --version >/dev/null; c aws-cli $?; docker run --rm --entrypoint printenv "$id" LZ_CREDENTIALS | grep -qx ambient; c ambient-credentials $?; echo "GATE1-LIST-SMOKE checks $checks failed $failed"; }; fi
              - if [ "$GATE1_KIND" = config ]; then cd "$GATE1_ROOT" && rm -rf /tmp/config && mkdir -p /tmp/config && unzip -q -o bundle.zip -d /tmp/config && aws ecr get-login-password --region "$AWS_REGION" | docker login --username AWS --password-stdin "${GATE1_TOOLING_IMAGE%%/*}" >/dev/null && docker pull -q "$GATE1_TOOLING_IMAGE" >/dev/null && checks=0 && failed=0 && c() { checks=$((checks+1)); [ "$2" = 0 ] || { failed=$((failed+1)); echo "GATE1-LIST-CONFIG-FAILED $1"; }; } && { (cd /tmp/config/CloudFront && node --test test/*.test.mjs); c edge-tests $?; for env in $(ls /tmp/config | sed -n "s/^systemconfig[.][^.]*[.]\([^.]*\)[.]yaml$/\1/p" | sort -u); do docker run --rm -e LZ_CREDENTIALS= -v /tmp/config:/work/_Dev:ro "$GATE1_TOOLING_IMAGE" validateconfig --env "$env"; c "config-$env" $?; done; echo "GATE1-LIST-CONFIG checks $checks failed $failed"; }; fi
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
    /// <summary>Each repository's two prefixes in either store, its own class's and <c>tests/</c>, as list conditions.</summary>
    private static string[] OwnPrefixes(IReadOnlyList<IntakeRepository> repositories)
        => repositories.SelectMany(r => r.PublishedClasses.Select(c => BuildRecordFormat.PrefixFor(c, r.Repo) + "*")).Distinct().ToArray();

    /// <summary>
    /// Where each image-class repository's one image is pushed (V-D): its pipeline repository and its push role's signing
    /// profile, the same names the build-side plan gives them, so the intake signs under the profile dev's hook trusts.
    /// </summary>
    public static IReadOnlyList<PublishTarget> PublishTargetsFor(SystemConfig config, string accountId, IReadOnlyList<IntakeRepository> repositories)
        => repositories.Where(r => BuildRecordFormat.IsImageClass(r.Class))
            .SelectMany(r => r.Artifacts.Select(a => new PublishTarget(r.Repo, a, EcrRepositoryNaming.For(config, a),
                PipelineBootstrapPlanner.SigningProfileArn(config.Region, accountId, PipelineBootstrapPlanner.SigningProfileNameFor(config.SystemKey, r.Repo)))))
            .ToList();

    // The publish waits for the image's signature: 10 s × 60, ten minutes, well inside managed signing's measured seconds.
    public const int SigningPollSeconds = 10;
    public const int SigningPollAttempts = 60;

    public static string DefinitionFor(string checkFn, string fetchFn, string recordFn, string? gate1StartFn = null, string? gate1VerdictFn = null,
        string? publishFn = null, string? siteCheckFn = null)
    {
        var testsGate = gate1StartFn is not null && gate1VerdictFn is not null;
        var gate1 = testsGate || siteCheckFn is not null;
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
            // GATE 1 (V-C): only a repository whose fetch staged a test bundle, or a site's bundles (V-24), has one; the rest go
            // straight to the outcome. A site's verdict lands where a test bundle's does, so the outcome reads either.
            var choices = new List<object>();
            if (testsGate) choices.Add(new { Variable = "$.fetch.tests", IsPresent = true, Next = "Gate1Start" });
            if (siteCheckFn is not null) choices.Add(new { Variable = "$.fetch.sites", IsPresent = true, Next = "SiteCheck" });
            states["HasGate1"] = new { Type = "Choice", Choices = choices.ToArray(), Default = "MarkTakenIn" };
            if (testsGate)
            {
                states["Gate1Start"] = new { Type = "Task", Resource = gate1StartFn!, Parameters = payload, ResultPath = "$.gate1Start", Retry = new object[] { Transient(), new { ErrorEquals = new[] { nameof(Gate1Waiting) }, IntervalSeconds = Gate1WaitSeconds, MaxAttempts = Gate1WaitAttempts, BackoffRate = 1.0 } }, Next = "Gate1Verdict", Catch = toFault };
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
            }
            if (siteCheckFn is not null)
                states["SiteCheck"] = new { Type = "Task", Resource = siteCheckFn, Parameters = payload, ResultPath = "$.gate1", Retry = new[] { Transient() }, Next = "Gate1Outcome", Catch = toFault };
            states["Gate1Outcome"] = new
            {
                Type = "Choice",
                Choices = new object[] { new { Variable = "$.gate1.verdict", StringEquals = "passed", Next = publishFn is null ? "MarkTakenIn" : "Publish" } },
                Default = "MarkRefused",
            };
            if (publishFn is not null)
                // PUSH AND WRITE, ONLY ON A PASS (V-D): a dry repository's publish does nothing; a live one's pushes, waits for
                // the signature (retried while the image is unsigned), writes the zips and the records.
                states["Publish"] = new
                {
                    Type = "Task", Resource = publishFn, Parameters = payload, ResultPath = "$.publish",
                    Retry = new object[]
                    {
                        new { ErrorEquals = new[] { nameof(ImageNotYetSigned) }, IntervalSeconds = SigningPollSeconds, MaxAttempts = SigningPollAttempts, BackoffRate = 1.0 },
                        Transient(),
                    },
                    Next = "MarkTakenIn", Catch = toFault,
                };
        }

        return JsonSerializer.Serialize(new
        {
            Comment = publishFn is not null
                ? "lz intake (DecoupledCd.md §15.4, V-B to V-D): check, fetch and stage, Gate 1, then for a live repository push and write, record."
                : gate1
                ? "lz intake, dry (DecoupledCd.md §15.4, V-B, V-C): check, fetch and stage, Gate 1, record. Pushes nothing."
                : "lz intake, dry (DecoupledCd.md §15.4, V-B): check, fetch and stage, record. Pushes nothing.",
            StartAt = "CheckRun",
            States = states,
        }, DeployerPlanner.Asl);
    }
}
