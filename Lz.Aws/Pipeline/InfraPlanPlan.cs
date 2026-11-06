using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>
/// The Plan task's network (P-13): a small VPC the deployer bootstrap creates, which no stack of the system manages, so
/// <c>deploysystem</c> cannot take away the way out of the task that plans it. One public subnet, an internet gateway, a route
/// to it, and a security group with no inbound rule; the task leaves with a public IP, and there is no NAT gateway to pay for.
/// Every piece carries the tag <see cref="Tag"/> with the value <see cref="Name"/>, which is how the bootstrap finds it again.
/// </summary>
public sealed record InfraPlanNetwork(string Name, string VpcCidr, string SubnetCidr, string AvailabilityZone)
{
    public const string Tag = "lz:deployer-tooling";
}

/// <summary>What the branch needs from the account, which the planner cannot read: the subnet and group the bootstrap created.</summary>
public sealed record DeployerInfraInputs(string SubnetId, string SecurityGroupId)
{
    /// <summary>Before the apply has created them (a dry run on a new account): planned with placeholders VerifyTooling refuses.</summary>
    public static readonly DeployerInfraInputs NotYetCreated = new("<subnet-created-by-apply>", "<security-group-created-by-apply>");
}

/// <summary>
/// What the read-only infrastructure branch needs besides its two functions (DecoupledCd.md P3 D), all in the target account:
/// the network, the cluster the task runs in, its log group, the task-execution role (pull the tooling image, write the log)
/// and the task's own role, which plans: AWS's ReadOnlyAccess, the state key's Decrypt, the config bundles' read and a write
/// under <c>plans/</c>, and an explicit Deny of every data-plane read the preview has no use for.
/// </summary>
public sealed record InfraPlanResources(
    InfraPlanNetwork Network,
    string ClusterName,
    string ClusterArn,
    string Family,
    string LogGroup,
    int LogRetentionDays,
    string ExecutionRoleName,
    string ExecutionRolePolicy,
    string TaskRoleName,
    string TaskRolePolicy,
    IReadOnlyList<string> TaskRoleManagedPolicies,
    string TaskTrustPolicy,
    string ToolingRepository,
    string ConfigRepo,
    string ToolingRepo,
    string StateBucket,
    string StateKeyAlias);

/// <summary>
/// THE INFRASTRUCTURE BRANCH, PLAN ONLY (DecoupledCd.md P3 D), as a pure function of config: names, grants and states. The
/// deployer planner calls it only where <c>Pipeline.Infrastructure</c> is <c>plan</c>, so every other environment's plan is
/// byte for byte what it was.
/// </summary>
public static class InfraPlanPlanner
{
    /// <summary>The managed policy the Plan task's role reads the account with. A Pulumi preview reads whatever its program declares.</summary>
    public const string ReadOnlyAccess = "arn:aws:iam::aws:policy/ReadOnlyAccess";

    /// <summary>How long a Plan task may run before Step Functions stops waiting: a preview of both stacks measured 30 s on a workstation.</summary>
    public const int PlanTimeoutSeconds = 1800;

    public static string ClusterNameFor(string sk, string env) => $"{sk}-{env}-deployer-tooling";
    public static string FamilyFor(string sk, string env) => $"{sk}-{env}-deployer-plan";
    public static string ExecutionRoleNameFor(string sk, string env) => $"{sk}-{env}-deployer-tooling-exec";
    /// <summary>ONE DEFINITION FOR BOTH ACCOUNTS: the build account's config-bundle grant names this role.</summary>
    public static string TaskRoleNameFor(string sk, string env) => $"{sk}-{env}-deployer-plan-task";
    public static string VerifyToolingFunctionName(string sk, string env) => $"{sk}-{env}-deployer-verify-tooling";
    public static string ReadPlanFunctionName(string sk, string env) => $"{sk}-{env}-deployer-read-plan";

    /// <summary>The rule Step Functions manages for <c>ecs:runTask.sync</c> in this account and region.</summary>
    public static string EcsSyncRuleArn(string region, string accountId)
        => $"arn:aws:events:{region}:{accountId}:rule/StepFunctionsGetEventsForECSTaskRule";

    /// <summary>The one repository whose records are config, and the one whose are tooling: what the branch reads.</summary>
    public static (string ConfigRepo, string ToolingRepo) Repositories(PipelineConfig p)
    {
        var config = p.AcceptedRepositories.Where(r => r.Class == "config").Select(r => r.Repo!).Distinct().ToList();
        var tooling = p.AcceptedRepositories.Where(r => r.Class == "tooling").Select(r => r.Repo!).Distinct().ToList();
        if (config.Count != 1 || tooling.Count != 1)
            throw new InvalidOperationException(
                $"Pipeline.Infrastructure is plan, which reads one config repository's records and one tooling repository's; " +
                $"Pipeline.Repositories names {config.Count} of class config and {tooling.Count} of class tooling.");
        return (config[0], tooling[0]);
    }

    public static InfraPlanResources Resources(SystemConfig config, string region, string acct, string evidence, string artifactStore)
    {
        var p = config.Pipeline!;
        var sk = config.SystemKey;
        var env = config.Environment;
        var (configRepo, toolingRepo) = Repositories(p);
        var toolingRole = p.AcceptedRepositories.First(r => r.Class == "tooling" && r.Repo == toolingRepo);
        var toolingRepository = toolingRole.Artifacts is { Count: 1 } artifacts
            ? EcrRepositoryNaming.For(config, artifacts[0])
            : throw new InvalidOperationException($"the tooling repository {toolingRepo} must name exactly one artifact, the tooling image.");

        var (stateBucket, keyAlias) = StateOf(config);
        var cluster = ClusterNameFor(sk, env);
        var logGroup = $"/lz/{FamilyFor(sk, env)}";
        var configPrefix = BuildRecordFormat.PrefixFor("config", configRepo);

        var execution = DeployerPlanner.Policy(
            new { Sid = "AuthenticateToTheRegistry", Effect = "Allow", Action = new[] { "ecr:GetAuthorizationToken" }, Resource = "*" },
            new
            {
                Sid = "PullTheToolingImageOnly",
                Effect = "Allow",
                Action = new[] { "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:BatchCheckLayerAvailability" },
                Resource = $"arn:aws:ecr:{region}:{acct}:repository/{toolingRepository}",
            },
            new
            {
                Sid = "WriteThePlanLog",
                Effect = "Allow",
                Action = new[] { "logs:CreateLogStream", "logs:PutLogEvents" },
                Resource = $"arn:aws:logs:{region}:{acct}:log-group:{logGroup}:*",
            });

        var task = DeployerPlanner.Policy(
            new
            {
                // The config bundle, by version, in the BUILD account: this is half the grant; that bucket's policy is the other.
                Sid = "ReadConfigBundles",
                Effect = "Allow",
                Action = new[] { "s3:GetObject", "s3:GetObjectVersion" },
                Resource = $"arn:aws:s3:::{artifactStore}/{configPrefix}*",
            },
            new
            {
                // The Pulumi state's secrets: ReadOnlyAccess grants no kms:Decrypt. Only the state key, by its alias.
                Sid = "DecryptTheStateWithItsKey",
                Effect = "Allow",
                Action = new[] { "kms:Decrypt" },
                Resource = $"arn:aws:kms:{region}:{acct}:key/*",
                Condition = new Dictionary<string, object>
                {
                    ["ForAnyValue:StringEquals"] = new Dictionary<string, object> { ["kms:ResourceAliases"] = keyAlias },
                },
            },
            new
            {
                // The plan, once, under plans/ - the evidence store's write-once policy demands If-None-Match, and its writer pin
                // names this role as plans/'s only writer.
                Sid = "WriteThePlan",
                Effect = "Allow",
                Action = new[] { "s3:PutObject" },
                Resource = $"arn:aws:s3:::{evidence}/{InfraPlan.PlansPrefix}*",
            },
            new
            {
                // READ-ONLY MEANS THE DEFINITION, NOT THE DATA. ReadOnlyAccess reads tables and secrets' metadata; a preview reads
                // neither's contents, so they are denied outright. NOT YET MEASURED: should a preview need one, the first live
                // plan fails on it, naming the action, and nothing is applied either way.
                Sid = "NoDataPlaneReads",
                Effect = "Deny",
                Action = new[]
                {
                    "dynamodb:GetItem", "dynamodb:BatchGetItem", "dynamodb:Query", "dynamodb:Scan", "dynamodb:PartiQLSelect",
                    "dynamodb:GetRecords", "dynamodb:ExportTableToPointInTime",
                    "secretsmanager:GetSecretValue",
                },
                Resource = "*",
            });

        return new InfraPlanResources(
            Network: NetworkFor(config),
            ClusterName: cluster,
            ClusterArn: $"arn:aws:ecs:{region}:{acct}:cluster/{cluster}",
            Family: FamilyFor(sk, env),
            LogGroup: logGroup,
            LogRetentionDays: config.Hygiene?.LambdaLogRetentionDays ?? 30,
            ExecutionRoleName: ExecutionRoleNameFor(sk, env),
            ExecutionRolePolicy: execution,
            TaskRoleName: TaskRoleNameFor(sk, env),
            TaskRolePolicy: task,
            TaskRoleManagedPolicies: new[] { ReadOnlyAccess },
            TaskTrustPolicy: DeployerPlanner.ServiceTrustPolicy("ecs-tasks.amazonaws.com"),
            ToolingRepository: toolingRepository,
            ConfigRepo: configRepo,
            ToolingRepo: toolingRepo,
            StateBucket: stateBucket,
            StateKeyAlias: keyAlias);
    }

    /// <summary>The Plan task's network: a /24 of its own, one /25 public subnet in the region's first zone.</summary>
    public static InfraPlanNetwork NetworkFor(SystemConfig config)
        => new($"{config.SystemKey}-{config.Environment}-deployer-tooling", "10.254.0.0/24", "10.254.0.0/25", $"{config.Region}a");

    /// <summary>The Pulumi state bucket and the alias of its secrets key, from the derived state config.</summary>
    public static (string Bucket, string KeyAlias) StateOf(SystemConfig config)
    {
        var backend = config.State?.Backend;
        var secrets = config.State?.SecretsProvider;
        if (backend is null || !backend.StartsWith("s3://", StringComparison.Ordinal) || secrets is null || !secrets.StartsWith("awskms://alias/", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Pipeline.Infrastructure is plan, and the Plan task reads the Pulumi state: it needs an s3:// backend and an awskms://alias/ " +
                $"secrets provider, and the config has '{backend}' and '{secrets}'.");
        var bucket = backend["s3://".Length..].Split('?', '/')[0];
        var alias = secrets["awskms://".Length..].Split('?')[0];
        return (bucket, alias);
    }

    /// <summary>What Verify reads besides image records: the config records, the tooling records, and the bundle's HEAD.</summary>
    public static object[] VerifyGrants(InfraPlanResources r, string buildRecordStore, string artifactStore)
    {
        var configPrefix = BuildRecordFormat.PrefixFor("config", r.ConfigRepo);
        var toolingPrefix = BuildRecordFormat.PrefixFor("tooling", r.ToolingRepo);
        return new object[]
        {
            new
            {
                Sid = "ReadConfigAndToolingRecords",
                Effect = "Allow",
                Action = new[] { "s3:GetObject" },
                Resource = new[] { $"arn:aws:s3:::{buildRecordStore}/{configPrefix}*", $"arn:aws:s3:::{buildRecordStore}/{toolingPrefix}*" },
            },
            new
            {
                // Listing the tooling records is how the newest carrying the bundle's lz is found; and, as for image records,
                // a missing record is a 404 only to a principal that may list.
                Sid = "ListConfigAndToolingRecords",
                Effect = "Allow",
                Action = new[] { "s3:ListBucket" },
                Resource = $"arn:aws:s3:::{buildRecordStore}",
                Condition = new Dictionary<string, object>
                {
                    ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = new[] { $"{configPrefix}*", $"{toolingPrefix}*" } },
                },
            },
            new
            {
                Sid = "HeadTheConfigBundleByVersion",
                Effect = "Allow",
                Action = new[] { "s3:GetObjectVersion" },
                Resource = $"arn:aws:s3:::{artifactStore}/{configPrefix}*",
            },
        };
    }

    /// <summary>VerifyTooling: Notation's reads of the tooling image, the network lookup, and the Plan task's revision.</summary>
    public static object[] VerifyToolingGrants(InfraPlanResources r, string region, string acct) => new object[]
    {
        new { Sid = "AuthenticateToTheRegistry", Effect = "Allow", Action = new[] { "ecr:GetAuthorizationToken" }, Resource = "*" },
        new
        {
            Sid = "ReadTheToolingImageAndItsSignature",
            Effect = "Allow",
            Action = new[] { "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:BatchCheckLayerAvailability" },
            Resource = $"arn:aws:ecr:{region}:{acct}:repository/{r.ToolingRepository}",
        },
        new { Sid = "CheckRevocation", Effect = "Allow", Action = new[] { "signer:GetRevocationStatus" }, Resource = "*" },
        new { Sid = "FindThePlanNetwork", Effect = "Allow", Action = new[] { "ec2:DescribeSubnets", "ec2:DescribeSecurityGroups" }, Resource = "*" },
        new
        {
            // RegisterTaskDefinition takes no resource-level permission; the tag it writes needs TagResource, for that call only.
            Sid = "RegisterThePlanTask",
            Effect = "Allow",
            Action = new[] { "ecs:RegisterTaskDefinition" },
            Resource = "*",
        },
        new
        {
            Sid = "TagThePlanTaskAsItIsRegistered",
            Effect = "Allow",
            Action = new[] { "ecs:TagResource" },
            Resource = $"arn:aws:ecs:{region}:{acct}:task-definition/{r.Family}:*",
            Condition = new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object> { ["ecs:CreateAction"] = "RegisterTaskDefinition" },
            },
        },
        PassTheTaskRoles(r, acct, "PassOnlyThePlanTasksRoles"),
    };

    /// <summary>ReadPlan: the plans, and a list of their prefix so a missing plan reads as missing.</summary>
    public static object[] ReadPlanGrants(string evidence) => new object[]
    {
        new { Sid = "ReadThePlans", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = $"arn:aws:s3:::{evidence}/{InfraPlan.PlansPrefix}*" },
        new
        {
            Sid = "TellAMissingPlanFromADeniedOne",
            Effect = "Allow",
            Action = new[] { "s3:ListBucket" },
            Resource = $"arn:aws:s3:::{evidence}",
            Condition = new Dictionary<string, object>
            {
                ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = $"{InfraPlan.PlansPrefix}*" },
            },
        },
    };

    /// <summary>The state machine's own rights for the Plan state: run the Plan family in its cluster, follow it, stop it.</summary>
    public static object[] MachineGrants(InfraPlanResources r, string region, string acct) => new object[]
    {
        new
        {
            Sid = "RunThePlanTaskInItsCluster",
            Effect = "Allow",
            Action = new[] { "ecs:RunTask" },
            Resource = $"arn:aws:ecs:{region}:{acct}:task-definition/{r.Family}:*",
            Condition = new Dictionary<string, object>
            {
                ["ArnEquals"] = new Dictionary<string, object> { ["ecs:cluster"] = r.ClusterArn },
            },
        },
        new
        {
            Sid = "FollowAndStopThePlanTask",
            Effect = "Allow",
            Action = new[] { "ecs:DescribeTasks", "ecs:StopTask" },
            Resource = $"arn:aws:ecs:{region}:{acct}:task/{r.ClusterName}/*",
        },
        new
        {
            // What runTask.sync needs: Step Functions puts and reads its own rule, which the Deny leaves open by name.
            Sid = "TheRunTaskSyncRule",
            Effect = "Allow",
            Action = new[] { "events:PutTargets", "events:PutRule", "events:DescribeRule" },
            Resource = EcsSyncRuleArn(region, acct),
        },
        PassTheTaskRoles(r, acct, "PassThePlanTasksRoles"),
    };

    private static object PassTheTaskRoles(InfraPlanResources r, string acct, string sid) => new
    {
        Sid = sid,
        Effect = "Allow",
        Action = new[] { "iam:PassRole" },
        Resource = new[] { $"arn:aws:iam::{acct}:role/{r.ExecutionRoleName}", $"arn:aws:iam::{acct}:role/{r.TaskRoleName}" },
        Condition = new Dictionary<string, object>
        {
            ["StringEquals"] = new Dictionary<string, object> { ["iam:PassedToService"] = "ecs-tasks.amazonaws.com" },
        },
    };

    /// <summary>
    /// The branch's states: VerifyTooling → Plan (the tooling image running <c>lz infraplan</c>, <c>ecs:runTask.sync</c>) →
    /// ReadPlan → Planned. A Plan task that fails still goes to ReadPlan, which decides from the plan it wrote or the absence of
    /// one; every other fault goes to RecordFailure, as everywhere in the machine.
    /// </summary>
    public static Dictionary<string, object> States(
        InfraPlanResources r, string environment, string evidence, string verifyToolingArn, string readPlanArn,
        object payload, object[] catchAll, Func<object> transient)
    {
        var command = "States.Array('infraplan', '--env', '" + environment + "', " +
                      "'--bundle-bucket', $.verified.identity.bucket, '--bundle-key', $.verified.identity.key, " +
                      "'--bundle-version', $.verified.identity.versionId, '--bundle-sha256', $.verified.identity.sha256, " +
                      "'--plan-bucket', '" + evidence + "', '--plan-key', States.Format('" + InfraPlan.PlansPrefix + "{}.json', $$.Execution.Name))";

        return new Dictionary<string, object>
        {
            ["VerifyTooling"] = new
            {
                Type = "Task",
                Resource = verifyToolingArn,
                Parameters = payload,
                ResultPath = "$.tooling",
                Retry = new[] { transient() },
                Next = "Plan",
                Catch = catchAll,
            },
            ["Plan"] = new
            {
                Type = "Task",
                Resource = "arn:aws:states:::ecs:runTask.sync",
                Parameters = new Dictionary<string, object>
                {
                    ["Cluster"] = r.ClusterArn,
                    ["TaskDefinition.$"] = "$.tooling.taskDefinitionArn",
                    ["LaunchType"] = "FARGATE",
                    ["NetworkConfiguration"] = new Dictionary<string, object>
                    {
                        ["AwsvpcConfiguration"] = new Dictionary<string, object>
                        {
                            ["Subnets.$"] = "$.tooling.subnets",
                            ["SecurityGroups.$"] = "$.tooling.securityGroups",
                            ["AssignPublicIp"] = "ENABLED",
                        },
                    },
                    ["Overrides"] = new Dictionary<string, object>
                    {
                        ["ContainerOverrides"] = new object[]
                        {
                            new Dictionary<string, object> { ["Name"] = InfraPlanSteps.Container, ["Command.$"] = command },
                        },
                    },
                },
                ResultPath = "$.plan",
                TimeoutSeconds = PlanTimeoutSeconds,
                Next = "ReadPlan",
                // A task that stopped badly may still have written its plan (a destructive one exits 2), so ReadPlan decides.
                Catch = new object[]
                {
                    new { ErrorEquals = new[] { "States.TaskFailed" }, Next = "ReadPlan", ResultPath = "$.planError" },
                }.Concat(catchAll).ToArray(),
            },
            ["ReadPlan"] = new
            {
                Type = "Task",
                Resource = readPlanArn,
                Parameters = payload,
                ResultPath = "$.planned",
                Retry = new[] { transient() },
                Next = "Planned",
                Catch = catchAll,
            },
            ["Planned"] = new { Type = "Succeed" },
        };
    }
}
