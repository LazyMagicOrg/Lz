using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>
/// What the deploy half of the infrastructure branch adds (DecoupledCd.md P3 D), all in the target account: the Deploy task's
/// family and role - the infrastructure role of §3 - and the function that reads the task's result back. The task runs on the
/// Plan task's cluster, network, log group and execution role.
/// </summary>
/// <param name="TaskRolePolicy">Its grants beyond <see cref="TaskRoleManagedPolicies"/>: the config bundles in the build account,
/// the approved plan, and its result under <c>applies/</c>.</param>
public sealed record InfraDeployResources(
    string Family,
    string TaskRoleName,
    string TaskRolePolicy,
    IReadOnlyList<string> TaskRoleManagedPolicies,
    string TaskTrustPolicy,
    string ReadApplyFunctionName);

/// <summary>
/// THE INFRASTRUCTURE BRANCH'S DEPLOY HALF (DecoupledCd.md P3 D), as a pure function of config and of the deployer's own plan:
/// names, the infrastructure role's grants and its Deny, and the states after ReadPlan. Planned only under
/// <c>Pipeline.Infrastructure: deploy</c>; under <c>plan</c> every output is what it was.
/// </summary>
public static class InfraDeployPlanner
{
    /// <summary>
    /// What the infrastructure role applies with (§3: "what lz needs for the topology"). A topology's program creates roles and
    /// their policies, functions, buckets, distributions and an IAM user for SES, so nothing narrower is the topology's need;
    /// the pipeline is fenced off by <see cref="PipelineDeny"/>, which names its resources.
    /// </summary>
    public const string AdministratorAccess = "arn:aws:iam::aws:policy/AdministratorAccess";

    /// <summary>Where the Deploy task writes its result in the evidence store, once; ReadApply reads it.</summary>
    public const string AppliesPrefix = InfraPlan.AppliesPrefix;

    /// <summary>How long a Deploy task may run: a foundation deploy that recycles the Tailscale router, then a tenant, then two
    /// previews. Step Functions stops waiting after this; the task's own work is not interrupted by it.</summary>
    public const int DeployTimeoutSeconds = 3600;

    public static string FamilyFor(string sk, string env) => $"{sk}-{env}-deployer-infra";

    /// <summary>ONE DEFINITION FOR BOTH ACCOUNTS: the build account's config-bundle grant names this role.</summary>
    public static string TaskRoleNameFor(string sk, string env) => $"{sk}-{env}-deployer-infra-task";

    public static string ReadApplyFunctionName(string sk, string env) => $"{sk}-{env}-deployer-read-apply";

    /// <summary>Why an apply key is refused, or null: under <see cref="AppliesPrefix"/>, a <c>.json</c> name, no <c>..</c>.</summary>
    public static string? WhyNotAnApplyKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "no apply key";
        if (!key.StartsWith(AppliesPrefix, StringComparison.Ordinal)) return $"the apply key {key} is not under {AppliesPrefix}";
        if (key.Contains("..", StringComparison.Ordinal) || key.Contains('\\')) return $"the apply key {key} is not a plain key";
        if (!key.EndsWith(".json", StringComparison.Ordinal)) return $"the apply key {key} is not a .json name";
        return null;
    }

    public static InfraDeployResources Resources(string sk, string env, string evidence, string artifactStore, string configPrefix)
    {
        var task = DeployerPlanner.Policy(
            new
            {
                // The config bundle, by version, in the BUILD account: this half of the grant; that bucket's policy is the other.
                // AdministratorAccess reaches this account's buckets only.
                Sid = "ReadConfigBundles",
                Effect = "Allow",
                Action = new[] { "s3:GetObject", "s3:GetObjectVersion" },
                Resource = $"arn:aws:s3:::{artifactStore}/{configPrefix}*",
            },
            new
            {
                Sid = "ReadTheApprovedPlan",
                Effect = "Allow",
                Action = new[] { "s3:GetObject", "s3:GetObjectVersion" },
                Resource = $"arn:aws:s3:::{evidence}/{InfraPlan.PlansPrefix}*",
            },
            new
            {
                Sid = "WriteTheResult",
                Effect = "Allow",
                Action = new[] { "s3:PutObject" },
                Resource = $"arn:aws:s3:::{evidence}/{AppliesPrefix}*",
            },
            new
            {
                // P3 E: the config deploys already recorded, so a bundle older than one deployed is refused (InfraDeploy.SupersededBy).
                Sid = "ReadTheDeploysAlreadyRecorded",
                Effect = "Allow",
                Action = new[] { "s3:GetObject" },
                Resource = $"arn:aws:s3:::{evidence}/deploys/{configPrefix}*",
            },
            new
            {
                Sid = "ListTheDeploysAlreadyRecorded",
                Effect = "Allow",
                Action = new[] { "s3:ListBucket" },
                Resource = $"arn:aws:s3:::{evidence}",
                Condition = new Dictionary<string, object>
                {
                    ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = $"deploys/{configPrefix}*" },
                },
            });

        return new InfraDeployResources(
            Family: FamilyFor(sk, env),
            TaskRoleName: TaskRoleNameFor(sk, env),
            TaskRolePolicy: task,
            TaskRoleManagedPolicies: new[] { AdministratorAccess },
            TaskTrustPolicy: DeployerPlanner.ServiceTrustPolicy("ecs-tasks.amazonaws.com"),
            ReadApplyFunctionName: ReadApplyFunctionName(sk, env));
    }

    /// <summary>
    /// THE INFRASTRUCTURE ROLE'S DENY (§3, §5.5): "modify pipeline resources", read off the deployer's own plan, so every
    /// resource the bootstrap creates is in it by construction - a function, role, rule or topic added to the plan later is
    /// fenced off without anyone remembering to. Each name is also covered by the <c>{sk}-{env}-deployer</c> prefix, which
    /// every deployer-made function and role carries.
    ///
    /// <para>NOT the deployer's own Deny (<see cref="PipelineDeployer.DenyPolicy"/>): that one refuses <c>iam:*Policy*</c> on
    /// every resource, and a topology's program writes its roles' policies. This one refuses IAM on the pipeline's roles only.</para>
    ///
    /// <para>WHAT IT DOES NOT STOP: a principal that may create roles and functions may create one this Deny does not bind and
    /// act through it. What it applies is lz's program from the signed tooling image, configured by the bundle's YAML; the
    /// bundle cannot carry code. The Deny keeps that program, and a mistake in its config, off the pipeline.</para>
    /// </summary>
    public static string PipelineDeny(PipelineDeployer plan, string region, string acct, string sk, string env)
    {
        var prefix = $"{sk}-{env}-deployer";
        string Fn(string name) => $"arn:aws:lambda:{region}:{acct}:function:{name}";
        string Role(string name) => $"arn:aws:iam::{acct}:role/{name}";
        string Rule(string name) => $"arn:aws:events:{region}:{acct}:rule/{name}";

        // THE PREFIX, AND EVERY NAME OUTSIDE IT: a wildcard covers most of the plan in one entry (IAM counts a role's inline
        // policies together, up to 10,240 characters), and each name it does not cover is listed - the signature hook's, for one.
        var outsideFunctions = plan.Functions.Select(f => f.Name).Where(n => !n.StartsWith(prefix, StringComparison.Ordinal)).Distinct().ToList();
        var roleNames = plan.Functions.Select(f => f.RoleName).Append(plan.RoleName).Append(plan.HookInvokerRoleName);
        if (plan.Infrastructure is { } infra) roleNames = roleNames.Append(infra.ExecutionRoleName).Append(infra.TaskRoleName);
        if (plan.InfrastructureDeploy is { } deploy) roleNames = roleNames.Append(deploy.TaskRoleName);
        var roles = roleNames.Where(n => !n.StartsWith(prefix, StringComparison.Ordinal)).Prepend(prefix + "*").Distinct().ToList();
        var functionArns = outsideFunctions.SelectMany(f => new[] { Fn(f), Fn(f) + ":*" }).Prepend(Fn(prefix + "*")).ToArray();

        var rules = new List<string> { Rule(prefix + "*"), Rule("StepFunctionsGetEventsFor*") };
        var buses = new List<string>();
        var topics = new List<string>();
        var queues = new List<string>();
        var alarms = new List<string>();
        var parameters = new List<string> { $"arn:aws:ssm:{region}:{acct}:parameter/{sk}/{env}/pipeline/*" };
        if (plan.Trigger is { } t)
        {
            rules.Add(t.RuleArn);
            buses.Add(t.BusArn);
            queues.Add(t.DeadLetterQueueArn);
            alarms.Add(t.AlarmName);
        }
        if (plan.Alerts is { } a)
        {
            rules.Add(a.FailedDeployRuleArn);
            rules.Add(a.ScheduleRuleArn);
            if (a.HoldChangedRuleArn is { } held) rules.Add(held);
            topics.Add(a.TopicArn);
            alarms.Add(a.CorroborateErrorsAlarmName);
        }
        if (plan.Outcomes is { } o)
        {
            rules.Add(o.RuleArn);
            topics.Add(o.TopicArn);
        }
        if (plan.Promotion is { } pr)
        {
            rules.Add(Rule(pr.ScheduleRuleName));
            alarms.AddRange(pr.Alarms.Select(x => x.Name));
            parameters.Add($"arn:aws:ssm:{region}:{acct}:parameter{(pr.HoldParameter.StartsWith('/') ? "" : "/")}{pr.HoldParameter}");
        }
        if (plan.HoldParameter is { } hold)
            parameters.Add($"arn:aws:ssm:{region}:{acct}:parameter{(hold.StartsWith('/') ? "" : "/")}{hold}");

        var repositories = plan.ImageRepositories.Select(r => $"arn:aws:ecr:{region}:{acct}:repository/{r}").ToList();
        if (plan.Infrastructure is { } i2) repositories.Add($"arn:aws:ecr:{region}:{acct}:repository/{i2.ToolingRepository}");

        var logGroups = plan.Functions.Select(f => $"arn:aws:logs:{region}:{acct}:log-group:/aws/lambda/{f.Name}*").ToList();
        if (plan.Infrastructure is { } i3) logGroups.Add($"arn:aws:logs:{region}:{acct}:log-group:{i3.LogGroup}*");

        var statements = new List<object>
        {
            new { Sid = "NoStateMachines", Effect = "Deny", Action = new[] { "states:*" }, Resource = "*" },
            new { Sid = "NoPipelineFunctions", Effect = "Deny", Action = new[] { "lambda:*" }, Resource = functionArns },
            new { Sid = "NoPipelineRoles", Effect = "Deny", Action = new[] { "iam:*" }, Resource = roles.Select(Role).ToArray() },
            new
            {
                // Its own result is the only write; the bucket's policy holds the key to applies/ (EvidenceConfinement).
                Sid = "NoEvidenceStoreChangesButItsResult",
                Effect = "Deny",
                NotAction = new[] { "s3:PutObject", "s3:GetObject", "s3:GetObjectVersion", "s3:ListBucket" },
                Resource = new[] { $"arn:aws:s3:::{plan.EvidenceStore}", $"arn:aws:s3:::{plan.EvidenceStore}/*" },
            },
            new { Sid = "NoPipelineRules", Effect = "Deny", Action = new[] { "events:*" }, Resource = rules.Concat(buses).Distinct().ToArray() },
            new { Sid = "NoPipelineParameters", Effect = "Deny", Action = new[] { "ssm:PutParameter", "ssm:DeleteParameter", "ssm:DeleteParameters", "ssm:LabelParameterVersion", "ssm:AddTagsToResource", "ssm:RemoveTagsFromResource" }, Resource = parameters.Distinct().ToArray() },
            new
            {
                // No topology deploy uses these (lz's programs, 2026-11-06): the pipeline does.
                Sid = "NoSchedulesSigningOrRegistrySettings",
                Effect = "Deny",
                Action = new[] { "scheduler:*", "signer:*", "ecr:PutRegistryPolicy", "ecr:DeleteRegistryPolicy", "ecr:PutReplicationConfiguration", "ecr:PutRegistryScanningConfiguration" },
                Resource = "*",
            },
            new { Sid = "NoOtherRoles", Effect = "Deny", Action = new[] { "sts:AssumeRole", "sts:AssumeRoleWithWebIdentity", "sts:AssumeRoleWithSAML" }, Resource = "*" },
            new
            {
                Sid = "NoPipelineRepositoryWrites",
                Effect = "Deny",
                NotAction = new[] { "ecr:BatchGetImage", "ecr:GetDownloadUrlForLayer", "ecr:BatchCheckLayerAvailability", "ecr:Describe*", "ecr:List*", "ecr:Get*" },
                Resource = repositories.Distinct().ToArray(),
            },
            new { Sid = "NoPipelineLogChanges", Effect = "Deny", Action = new[] { "logs:DeleteLogGroup", "logs:DeleteLogStream", "logs:PutRetentionPolicy", "logs:DeleteRetentionPolicy", "logs:PutSubscriptionFilter", "logs:DeleteSubscriptionFilter", "logs:AssociateKmsKey", "logs:DisassociateKmsKey" }, Resource = logGroups.Distinct().ToArray() },
        };
        if (topics.Count > 0)
            statements.Add(new { Sid = "NoPipelineTopics", Effect = "Deny", Action = new[] { "sns:*" }, Resource = topics.Distinct().ToArray() });
        if (queues.Count > 0)
            statements.Add(new { Sid = "NoPipelineQueues", Effect = "Deny", Action = new[] { "sqs:*" }, Resource = queues.Distinct().ToArray() });
        if (alarms.Count > 0)
            statements.Add(new { Sid = "NoPipelineAlarms", Effect = "Deny", Action = new[] { "cloudwatch:PutMetricAlarm", "cloudwatch:DeleteAlarms", "cloudwatch:DisableAlarmActions", "cloudwatch:SetAlarmState", "cloudwatch:TagResource", "cloudwatch:UntagResource" }, Resource = alarms.Distinct().Select(n => $"arn:aws:cloudwatch:{region}:{acct}:alarm:{n}").ToArray() });
        if (plan.Gate2 is { } g)
        {
            statements.Add(new { Sid = "NoGate2Project", Effect = "Deny", Action = new[] { "codebuild:*" }, Resource = g.ProjectArn });
            statements.Add(new { Sid = "NoGate2Results", Effect = "Deny", Action = new[] { "s3:*" }, Resource = new[] { $"arn:aws:s3:::{g.ResultsBucket}", $"arn:aws:s3:::{g.ResultsBucket}/*" } });
        }
        if (plan.Infrastructure is { } i4)
        {
            statements.Add(new
            {
                Sid = "NoToolingClusterOrTaskFamilies",
                Effect = "Deny",
                Action = new[] { "ecs:DeleteCluster", "ecs:UpdateCluster", "ecs:UpdateClusterSettings", "ecs:PutClusterCapacityProviders", "ecs:DeregisterTaskDefinition", "ecs:DeleteTaskDefinitions", "ecs:TagResource", "ecs:UntagResource" },
                Resource = new[] { i4.ClusterArn, $"arn:aws:ecs:{region}:{acct}:task-definition/{prefix}*" },
            });
            statements.Add(new
            {
                // The Plan and Deploy tasks' network (P-13), by its tag: the way out of the task that deploys.
                Sid = "NoToolingNetworkChanges",
                Effect = "Deny",
                NotAction = new[] { "ec2:Describe*", "ec2:Get*" },
                Resource = "*",
                Condition = new Dictionary<string, object>
                {
                    ["StringEquals"] = new Dictionary<string, object> { [$"aws:ResourceTag/{InfraPlanNetwork.Tag}"] = i4.Network.Name },
                },
            });
        }
        return DeployerPlanner.Policy(statements.ToArray());
    }

    /// <summary>
    /// THE EVIDENCE STORE HOLDS THE INFRASTRUCTURE ROLE TO <c>applies/</c>, in its own policy: whatever its identity policy
    /// allows, it puts nothing anywhere else in the store. The writer pin covers the prefixes it names; this covers the rest.
    /// </summary>
    public static JsonObject EvidenceConfinement(string evidence, string acct, string taskRoleName) => new()
    {
        ["Sid"] = "LzInfrastructureRolePutsOnlyApplies",
        ["Effect"] = "Deny",
        ["Principal"] = "*",
        ["Action"] = "s3:PutObject",
        ["NotResource"] = $"arn:aws:s3:::{evidence}/{AppliesPrefix}*",
        ["Condition"] = new JsonObject
        {
            ["ArnEquals"] = new JsonObject { ["aws:PrincipalArn"] = $"arn:aws:iam::{acct}:role/{taskRoleName}" },
        },
    };

    /// <summary>ReadApply: the results, and a list of their prefix so a missing result reads as missing.</summary>
    public static object[] ReadApplyGrants(string evidence) => new object[]
    {
        new { Sid = "ReadTheResults", Effect = "Allow", Action = new[] { "s3:GetObject" }, Resource = $"arn:aws:s3:::{evidence}/{AppliesPrefix}*" },
        new
        {
            Sid = "TellAMissingResultFromADeniedOne",
            Effect = "Allow",
            Action = new[] { "s3:ListBucket" },
            Resource = $"arn:aws:s3:::{evidence}",
            Condition = new Dictionary<string, object>
            {
                ["StringLike"] = new Dictionary<string, object> { ["s3:prefix"] = $"{AppliesPrefix}*" },
            },
        },
    };

    /// <summary>The roles the machine and VerifyTooling pass, beyond the Plan task's: the Deploy task's.</summary>
    public static string TaskRoleArn(InfraDeployResources d, string acct) => $"arn:aws:iam::{acct}:role/{d.TaskRoleName}";

    /// <summary>
    /// The states after ReadPlan: ApproveInfrastructure (a pass-through: the validator refuses <c>deploy</c> where approval is
    /// required) → DeployInfrastructure (the same tooling image running <c>lz infradeploy</c> on the infrastructure role) →
    /// VerifyInfrastructure (ReadApply: the result, whose second preview must be empty) → RecordInfrastructure (the Record
    /// function, writing <c>deploys/config/...</c>) → InfrastructureDeployed. A Deploy task that fails still goes to
    /// VerifyInfrastructure, which decides from the result it wrote or the absence of one.
    /// </summary>
    /// <param name="gated">Under <c>Pipeline.Gate2: on</c> (P3 I1): RecordInfrastructure goes on to GatesThisClass, so a config
    /// execution runs Gate 2 and, with a successor, MarkPassed, as an image's and a client's do; InfrastructureDeployed is then left out.</param>
    public static Dictionary<string, object> States(
        InfraPlanResources r, string environment, string evidence, string readApplyArn, string recordArn,
        object payload, object[] catchAll, Func<object> transient, bool gated = false)
    {
        var command = "States.Array('infradeploy', '--env', '" + environment + "', " +
                      "'--bundle-bucket', $.verified.identity.bucket, '--bundle-key', $.verified.identity.key, " +
                      "'--bundle-version', $.verified.identity.versionId, '--bundle-sha256', $.verified.identity.sha256, " +
                      "'--evidence-bucket', '" + evidence + "', " +
                      "'--plan-key', States.Format('" + InfraPlan.PlansPrefix + "{}.json', $$.Execution.Name), " +
                      "'--apply-key', States.Format('" + AppliesPrefix + "{}.json', $$.Execution.Name), " +
                      "'--allow-destructive', States.Format('{}', $.planned.destructive), " +
                      "'--built-at', $.verified.builtAt, '--repo', $.verified.builtFrom.repo)";

        var states = new Dictionary<string, object>
        {
            ["ApproveInfrastructure"] = new
            {
                Type = "Pass",
                Result = new Dictionary<string, object>
                {
                    ["approvedBy"] = "pass-through",
                    ["why"] = $"Pipeline.Approval is not required in {environment}, and the infrastructure branch has no approval of its own yet.",
                },
                ResultPath = "$.approval",
                Next = "DeployInfrastructure",
            },
            ["DeployInfrastructure"] = new
            {
                Type = "Task",
                Resource = "arn:aws:states:::ecs:runTask.sync",
                Parameters = new Dictionary<string, object>
                {
                    ["Cluster"] = r.ClusterArn,
                    ["TaskDefinition.$"] = "$.tooling.deployTaskDefinitionArn",
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
                ResultPath = "$.deploy",
                TimeoutSeconds = DeployTimeoutSeconds,
                Next = "VerifyInfrastructure",
                // NOT RETRIED: a deploy that stopped half-way is not run again unseen. Its result says how far it got.
                Catch = new object[]
                {
                    new { ErrorEquals = new[] { "States.TaskFailed" }, Next = "VerifyInfrastructure", ResultPath = "$.deployError" },
                }.Concat(catchAll).ToArray(),
            },
            ["VerifyInfrastructure"] = new
            {
                Type = "Task",
                Resource = readApplyArn,
                Parameters = payload,
                ResultPath = "$.rollout",
                Retry = new[] { transient() },
                Next = "RecordInfrastructure",
                Catch = catchAll,
            },
            ["RecordInfrastructure"] = new
            {
                Type = "Task",
                Resource = recordArn,
                Parameters = payload,
                ResultPath = "$.recorded",
                Retry = new[] { transient() },
                Next = gated ? "GatesThisClass" : "InfrastructureDeployed",
                Catch = catchAll,
            },
        };
        if (!gated) states["InfrastructureDeployed"] = new { Type = "Succeed" };
        return states;
    }

    /// <summary>The machine's rights for DeployInfrastructure: run the Deploy family in the tooling cluster, and pass its role.</summary>
    public static object[] MachineGrants(InfraPlanResources r, InfraDeployResources d, string region, string acct) => new object[]
    {
        new
        {
            Sid = "RunTheDeployTaskInItsCluster",
            Effect = "Allow",
            Action = new[] { "ecs:RunTask" },
            Resource = $"arn:aws:ecs:{region}:{acct}:task-definition/{d.Family}:*",
            Condition = new Dictionary<string, object>
            {
                ["ArnEquals"] = new Dictionary<string, object> { ["ecs:cluster"] = r.ClusterArn },
            },
        },
        PassTheDeployTaskRole(d, acct, "PassTheDeployTasksRole"),
    };

    /// <summary>VerifyTooling's rights for the Deploy family: tag its revisions as they are registered, and pass its role.</summary>
    public static object[] VerifyToolingGrants(InfraDeployResources d, string region, string acct) => new object[]
    {
        new
        {
            Sid = "TagTheDeployTaskAsItIsRegistered",
            Effect = "Allow",
            Action = new[] { "ecs:TagResource" },
            Resource = $"arn:aws:ecs:{region}:{acct}:task-definition/{d.Family}:*",
            Condition = new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, object> { ["ecs:CreateAction"] = "RegisterTaskDefinition" },
            },
        },
        PassTheDeployTaskRole(d, acct, "PassOnlyTheDeployTasksRole"),
    };

    private static object PassTheDeployTaskRole(InfraDeployResources d, string acct, string sid) => new
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
}
