namespace Lz.Aws.Pipeline;

/// <summary>
/// What the deployer's image branch needs to deploy THROUGH PULUMI (DecoupledCd.md §12 P3 J, stage J3; ImageThroughPulumi.md
/// §3b): the Deploy task's family on the targeted-update role, and the function that names the configuration the deploy runs with.
/// The task runs on the infrastructure branch's cluster, network, log group and execution role.
/// </summary>
public sealed record ImageDeployResources(
    string Family,
    string TaskRoleName,
    string TaskRolePolicy,
    IReadOnlyList<string> TaskRoleManagedPolicies,
    string TaskTrustPolicy,
    string ChooseConfigFunctionName,
    string ConfigDeploysPrefix);

/// <summary>
/// THE IMAGE BRANCH THROUGH PULUMI (P3 J3), planned only under <c>Pipeline.ImageDeploy: pulumi</c>; under the default every output
/// is what it was. Prepare keeps the lease, the older-build check and the image input, and stops registering; ChooseConfig names the
/// config bundle LAST DEPLOYED here (owner, 2026-11-06) and the tooling image it ran with; VerifyTooling verifies that image and
/// registers this family; Deploy runs <c>lz updateimage</c> as an ECS task; VerifyRollout reads the revision off the service.
/// </summary>
public static class ImageDeployPlanner
{
    public static string FamilyFor(string sk, string env) => $"{sk}-{env}-deployer-image";

    /// <summary>ONE DEFINITION FOR BOTH ACCOUNTS: the build account's config-bundle grant names this role.</summary>
    public static string TaskRoleNameFor(string sk, string env) => $"{sk}-{env}-deployer-image-task";

    public static string ChooseConfigFunctionName(string sk, string env) => $"{sk}-{env}-deployer-choose-config";

    /// <summary>How long a Deploy task may run: a task start, a refresh, a full preview, a targeted up and the rollout wait.</summary>
    public const int DeployTimeoutSeconds = 1800;

    /// <summary>
    /// The targeted-update role (DecoupledCd.md §3): AWS's ReadOnlyAccess, since the refresh and the full preview read the whole
    /// tenant program; writes only to each tenant stack's state objects and lock, with the state key; the ECS writes of a revision
    /// and a service update, and the PassRole of the services' roles; the image inputs; the leases. Denied the data-plane reads the
    /// program never makes - every table's items but the system table's lease items, and every secret's value.
    /// </summary>
    public static ImageDeployResources Resources(
        Lz.Core.Config.SystemConfig config, string region, string acct, string artifactStore, string configPrefix,
        string stateBucket, string stateKeyAlias, IReadOnlyList<string> tenantKeys)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var project = $"lz-{sk}";
        var stateObjects = tenantKeys.SelectMany(tk =>
        {
            var stack = $"{sk}-{tk}-{env}";
            return new[]
            {
                $"arn:aws:s3:::{stateBucket}/.pulumi/stacks/{project}/{stack}.json",
                $"arn:aws:s3:::{stateBucket}/.pulumi/stacks/{project}/{stack}.json.bak",
                $"arn:aws:s3:::{stateBucket}/.pulumi/history/{project}/{stack}/*",
                $"arn:aws:s3:::{stateBucket}/.pulumi/backups/{project}/{stack}/*",
                $"arn:aws:s3:::{stateBucket}/.pulumi/locks/{project}/{stack}/*",
            };
        }).ToArray();

        var statements = new List<object>
        {
            new
            {
                Sid = "ReadConfigBundles",
                Effect = "Allow",
                Action = new[] { "s3:GetObject", "s3:GetObjectVersion" },
                Resource = $"arn:aws:s3:::{artifactStore}/{configPrefix}*",
            },
            new { Sid = "WriteOnlyTheTenantStacksState", Effect = "Allow", Action = new[] { "s3:PutObject", "s3:DeleteObject" }, Resource = stateObjects },
            new
            {
                Sid = "UseTheStateKey",
                Effect = "Allow",
                Action = new[] { "kms:Decrypt", "kms:Encrypt", "kms:GenerateDataKey" },
                Resource = $"arn:aws:kms:{region}:{acct}:key/*",
                Condition = new Dictionary<string, object>
                {
                    ["ForAnyValue:StringEquals"] = new Dictionary<string, object> { ["kms:ResourceAliases"] = stateKeyAlias },
                },
            },
            new { Sid = "RegisterAndRetireRevisions", Effect = "Allow", Action = new[] { "ecs:RegisterTaskDefinition", "ecs:DeregisterTaskDefinition" }, Resource = "*" },
            new
            {
                Sid = "TagOnlyWhileRegistering",
                Effect = "Allow",
                Action = new[] { "ecs:TagResource" },
                Resource = "*",
                Condition = new Dictionary<string, object>
                {
                    ["StringEquals"] = new Dictionary<string, object> { ["ecs:CreateAction"] = "RegisterTaskDefinition" },
                },
            },
            new { Sid = "UpdateTheServices", Effect = "Allow", Action = new[] { "ecs:UpdateService" }, Resource = DeployerPlanner.ServiceArns(region, acct, sk, env) },
            DeployerPlanner.PassRoleStatement(acct, sk, "PassOnlyTheServicesOwnRoles"),
            new
            {
                Sid = "ReadAndWriteTheImageInputs",
                Effect = "Allow",
                Action = new[] { "ssm:GetParameter", "ssm:PutParameter" },
                Resource = $"arn:aws:ssm:{region}:{acct}:parameter{ImageInputFormat.PrefixFor(sk, env)}*",
            },
        };
        statements.AddRange(DeployerPlanner.LeaseGrants(region, acct, sk, "ClaimTheServiceLeaseReentrantly", "dynamodb:PutItem", "dynamodb:GetItem", "dynamodb:UpdateItem"));
        statements.Add(new
        {
            Sid = "NoTableItemsButTheLeases",
            Effect = "Deny",
            Action = new[] { "dynamodb:GetItem", "dynamodb:BatchGetItem", "dynamodb:Query", "dynamodb:Scan", "dynamodb:PartiQLSelect", "dynamodb:GetRecords", "dynamodb:ExportTableToPointInTime" },
            NotResource = $"arn:aws:dynamodb:{region}:{acct}:table/{sk}",
        });
        statements.Add(new { Sid = "NoSecretValues", Effect = "Deny", Action = new[] { "secretsmanager:GetSecretValue" }, Resource = "*" });

        return new ImageDeployResources(
            Family: FamilyFor(sk, env),
            TaskRoleName: TaskRoleNameFor(sk, env),
            TaskRolePolicy: DeployerPlanner.Policy(statements.ToArray()),
            TaskRoleManagedPolicies: new[] { InfraPlanPlanner.ReadOnlyAccess },
            TaskTrustPolicy: DeployerPlanner.ServiceTrustPolicy("ecs-tasks.amazonaws.com"),
            ChooseConfigFunctionName: ChooseConfigFunctionName(sk, env),
            ConfigDeploysPrefix: $"deploys/{configPrefix}");
    }

    public static string TaskRoleArn(ImageDeployResources d, string acct) => $"arn:aws:iam::{acct}:role/{d.TaskRoleName}";

    /// <summary>ChooseConfig: the config deploys recorded here, listed and read; nothing else.</summary>
    public static object[] ChooseConfigGrants(string evidence, ImageDeployResources d) => new object[]
    {
        new { Sid = "ReadTheConfigDeploys", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = $"arn:aws:s3:::{evidence}/{d.ConfigDeploysPrefix}*" },
        new
        {
            Sid = "ListTheConfigDeploys",
            Effect = "Allow",
            Action = new[] { "s3:ListBucket" },
            Resource = $"arn:aws:s3:::{evidence}",
            Condition = new Dictionary<string, object>
            {
                ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = $"{d.ConfigDeploysPrefix}*" },
            },
        },
    };

    private static object PassTheImageTaskRole(ImageDeployResources d, string acct, string sid) => new
    {
        Sid = sid,
        Effect = "Allow",
        Action = new[] { "iam:PassRole" },
        Resource = TaskRoleArn(d, acct),
        Condition = new Dictionary<string, object>
        {
            ["StringEquals"] = new Dictionary<string, object> { ["iam:PassedToService"] = "ecs-tasks.amazonaws.com" },
        },
    };

    public static object[] MachineGrants(InfraPlanResources r, ImageDeployResources d, string region, string acct) => new object[]
    {
        new
        {
            Sid = "RunTheImageTaskInItsCluster",
            Effect = "Allow",
            Action = new[] { "ecs:RunTask" },
            Resource = $"arn:aws:ecs:{region}:{acct}:task-definition/{d.Family}:*",
            Condition = new Dictionary<string, object>
            {
                ["ArnEquals"] = new Dictionary<string, object> { ["ecs:cluster"] = r.ClusterArn },
            },
        },
        PassTheImageTaskRole(d, acct, "PassTheImageTasksRole"),
    };

    public static object[] VerifyToolingGrants(ImageDeployResources d, string region, string acct) => new object[]
    {
        new
        {
            Sid = "TagTheImageTaskAsItIsRegistered",
            Effect = "Allow",
            Action = new[] { "ecs:TagResource" },
            Resource = $"arn:aws:ecs:{region}:{acct}:task-definition/{d.Family}:*",
            Condition = new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object> { ["ecs:CreateAction"] = "RegisterTaskDefinition" },
            },
        },
        PassTheImageTaskRole(d, acct, "PassOnlyTheImageTasksRole"),
    };

    /// <summary>
    /// The states after Prepare: ChooseConfig -> VerifyImageTooling (the existing function, given the chosen config as its
    /// <c>verified</c>) -> Deploy (<c>lz updateimage</c> by <c>runTask.sync</c>) -> VerifyRollout. Deploy is not retried: a roll that
    /// stopped half-way is not run again unseen; its task wrote the input back, and RecordFailure records it.
    /// </summary>
    public static Dictionary<string, object> States(
        InfraPlanResources r, string environment, string chooseConfigArn, string verifyToolingArn, object payload, object[] catchAll, Func<object> transient)
    {
        var command = "States.Array('updateimage', '--env', '" + environment + "', " +
                      "'--service', $.target.service, '--digest', $.verified.digest, '--repository', $.target.repository, " +
                      "'--built-at', $.verified.builtAt, '--lease-holder', States.Format('deployer:{}', $$.Execution.Name), " +
                      "'--bundle-bucket', $.config.identity.bucket, '--bundle-key', $.config.identity.key, " +
                      "'--bundle-version', $.config.identity.versionId, '--bundle-sha256', $.config.identity.sha256)";

        return new Dictionary<string, object>
        {
            ["ChooseConfig"] = new
            {
                Type = "Task",
                Resource = chooseConfigArn,
                Parameters = payload,
                ResultPath = "$.config",
                Retry = new[] { transient() },
                Next = "VerifyImageTooling",
                Catch = catchAll,
            },
            ["VerifyImageTooling"] = new
            {
                Type = "Task",
                Resource = verifyToolingArn,
                // THE CHOSEN CONFIG AS VerifyTooling's `verified`: it reads the class and the tooling digest from there.
                Parameters = new Dictionary<string, object>
                {
                    ["state"] = new Dictionary<string, object> { ["verified.$"] = "$.config" },
                    ["executionName.$"] = "$$.Execution.Name",
                },
                ResultPath = "$.tooling",
                Retry = new[] { transient() },
                Next = "Deploy",
                Catch = catchAll,
            },
            ["Deploy"] = new
            {
                Type = "Task",
                Resource = "arn:aws:states:::ecs:runTask.sync",
                Parameters = new Dictionary<string, object>
                {
                    ["Cluster"] = r.ClusterArn,
                    ["TaskDefinition.$"] = "$.tooling.imageTaskDefinitionArn",
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
                ResultSelector = new Dictionary<string, object> { ["stoppedReason.$"] = "$.StoppedReason", ["containers.$"] = "$.Containers" },
                ResultPath = "$.deployResult",
                TimeoutSeconds = DeployTimeoutSeconds,
                Next = "VerifyRollout",
                Catch = catchAll,
            },
        };
    }
}
