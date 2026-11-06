using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>The plan the Plan task wrote is destructive. NOT RETRIED: RecordFailure records it, and nothing was applied.</summary>
public sealed class DestructivePlanRefused(string message) : Exception(message);

/// <summary>The Plan task left no plan under <c>plans/</c>: it stopped before writing one. NOT RETRIED.</summary>
public sealed class PlanMissing(string message) : Exception(message);

/// <summary>The Deploy task refused before applying anything: the plan it would apply is not the one that was read. NOT RETRIED.</summary>
public sealed class InfraDeployRefused(string message) : Exception(message);

/// <summary>The Deploy task stopped or failed, or left no result: something may have been applied. NOT RETRIED.</summary>
public sealed class InfraDeployFailed(string message) : Exception(message);

/// <summary>The Deploy task applied, and its second preview is not empty: the stacks are not what was applied. NOT RETRIED.</summary>
public sealed class InfraRolloutIncomplete(string message) : Exception(message);

/// <summary>What VerifyTooling is configured with (DecoupledCd.md P3 D).</summary>
public sealed record ToolingSettings(
    IReadOnlyList<string> TrustedProfiles, string Registry, string Repository,
    string Family, string ExecutionRoleArn, string TaskRoleArn, string LogGroup, string Region,
    IReadOnlyList<string> Subnets, IReadOnlyList<string> SecurityGroups,
    string? DeployFamily = null, string? DeployTaskRoleArn = null)
{
    public const string TrustedProfilesVariable = DeployerEnvironment.TrustedProfiles;
    public const string RegistryVariable = DeployerEnvironment.Registry;
    public const string RepositoryVariable = "LZ_TOOLING_REPOSITORY";
    public const string FamilyVariable = "LZ_TOOLING_FAMILY";
    public const string ExecutionRoleVariable = "LZ_TOOLING_EXECUTION_ROLE";
    public const string TaskRoleVariable = "LZ_TOOLING_TASK_ROLE";
    public const string LogGroupVariable = "LZ_TOOLING_LOG_GROUP";
    public const string RegionVariable = "LZ_TOOLING_REGION";
    public const string SubnetsVariable = "LZ_TOOLING_SUBNETS";
    public const string SecurityGroupsVariable = "LZ_TOOLING_SECURITY_GROUPS";
    /// <summary>Set only under <c>Pipeline.Infrastructure: deploy</c>: the Deploy task's family and the infrastructure role.</summary>
    public const string DeployFamilyVariable = "LZ_TOOLING_DEPLOY_FAMILY";
    public const string DeployTaskRoleVariable = "LZ_TOOLING_DEPLOY_TASK_ROLE";

    /// <summary>Whether this function also registers the Deploy task: both variables, or neither.</summary>
    public bool Deploys => DeployFamily is not null;

    public string Scope => $"{Registry}/{Repository}";

    public static ToolingSettings Read(Func<string, string?> env) => new(
        DeployerEnvironment.List(env, TrustedProfilesVariable),
        DeployerEnvironment.Required(env, RegistryVariable),
        DeployerEnvironment.Required(env, RepositoryVariable),
        DeployerEnvironment.Required(env, FamilyVariable),
        DeployerEnvironment.Required(env, ExecutionRoleVariable),
        DeployerEnvironment.Required(env, TaskRoleVariable),
        DeployerEnvironment.Required(env, LogGroupVariable),
        DeployerEnvironment.Required(env, RegionVariable),
        DeployerEnvironment.List(env, SubnetsVariable),
        DeployerEnvironment.List(env, SecurityGroupsVariable),
        Optional(env, DeployFamilyVariable, DeployTaskRoleVariable).Family,
        Optional(env, DeployFamilyVariable, DeployTaskRoleVariable).Role);

    private static (string? Family, string? Role) Optional(Func<string, string?> env, string family, string role)
    {
        var (f, r) = (env(family), env(role));
        if (string.IsNullOrWhiteSpace(f) != string.IsNullOrWhiteSpace(r))
            throw new InvalidOperationException($"{family} and {role} are set together or not at all; one is '{f}', the other '{r}'.");
        return string.IsNullOrWhiteSpace(f) ? (null, null) : (f, r);
    }
}

/// <summary>
/// THE INFRASTRUCTURE BRANCH, READ-ONLY SLICE (DecoupledCd.md P3 D): Verify's config branch, VerifyTooling and ReadPlan. The Plan
/// state between the last two is the tooling image running <c>lz infraplan</c> as an ECS task, so nothing here applies anything.
/// </summary>
public static class InfraPlanSteps
{
    /// <summary>The container in the Plan task definition, which the state machine's command override names.</summary>
    public const string Container = "lz";

    /// <summary>How many tooling records Verify reads, newest first, to find the one carrying the bundle's lz.</summary>
    public const int ToolingRecordsRead = 30;

    /// <summary>The plan's key in the evidence store: what the state machine passes the task, and what ReadPlan reads.</summary>
    public static string PlanKeyFor(string executionName)
        => $"{InfraPlan.PlansPrefix}{DeployEvidence.RequireSafeExecutionName(executionName)}.json";

    /// <summary>
    /// VERIFY, A CONFIG RECORD: the bundle the record names, by version and S3's checksum, and the tooling image that will plan
    /// from it - the newest whose record carries exactly the Lz.Cli the bundle pins (<see cref="ToolingImageChoice"/>, Gate 1's
    /// rule), so the bundle is planned by the lz it was checked with. Nothing is downloaded and nothing is written.
    /// </summary>
    public static async Task<JsonObject> VerifyConfigAsync(
        VerifySettings settings, IArtifactObjects artifacts, IToolingRecords? tooling, BuildRecord record, StoredRecord stored,
        List<VerifyRefusal> refusals)
    {
        if (tooling is null)
            refusals.Add(new VerifyRefusal("class",
                "this environment's Pipeline.Infrastructure is off, so it plans no config record (DecoupledCd.md P3 D)."));
        if (!record.BuiltFrom.Packages.TryGetValue("Lz.Cli", out var lz) || string.IsNullOrWhiteSpace(lz))
            refusals.Add(new VerifyRefusal("builtFrom.packages",
                "the config record names no Lz.Cli, so no tooling image can be chosen to plan it."));
        if (refusals.Count > 0)
            throw new DeployRefused(refusals);

        var identity = record.Identity;
        var head = await artifacts.HeadAsync(identity.Bucket!, identity.Key!, identity.VersionId!);
        var artifactRefusals = DeployVerification.BundleArtifact(identity, head, InfraPlanLimits.MaxBundleBytes);
        if (artifactRefusals.Count > 0)
            throw new DeployRefused(artifactRefusals);

        var records = await tooling!.NewestAsync(ToolingRecordsRead);
        var digest = ToolingImageChoice.DigestFor(records, lz!)
            ?? throw new DeployRefused("tooling",
                $"none of the newest {records.Count} tooling records carries Lz.Cli {lz}, the version this config bundle pins. " +
                "A push that raises the pin builds both; plan this bundle once its tooling image is taken in.");
        var toolingKey = records.Where(r => r.Record["identity"]?["digest"]?.GetValue<string>() == digest)
            .OrderByDescending(r => r.Key, StringComparer.Ordinal).First().Key;

        var commit = record.BuiltFrom.Commit.Length > 12 ? record.BuiltFrom.Commit[..12] : record.BuiltFrom.Commit;
        return new JsonObject
        {
            ["class"] = record.Class,
            ["identity"] = new JsonObject
            {
                ["bucket"] = identity.Bucket,
                ["key"] = identity.Key,
                ["versionId"] = identity.VersionId,
                ["sha256"] = identity.Sha256,
                ["bytes"] = head!.ContentLength,
            },
            ["tooling"] = new JsonObject
            {
                ["digest"] = digest,
                ["record"] = toolingKey,
                ["lz"] = lz,
            },
            ["recordVersionId"] = stored.VersionId,
            ["builtFrom"] = VerifyStep.BuiltFrom(record),
            ["builtAt"] = record.BuiltAt,
            ["workflowRunId"] = record.WorkflowRunId,
            ["summary"] =
                $"Plan config bundle s3://{identity.Bucket}/{identity.Key} version {identity.VersionId} (sha256 {identity.Sha256}) with the " +
                $"tooling image {digest} (Lz {lz}). Read-only: nothing is applied. Built from {record.BuiltFrom.Repo} {commit}, workflow run " +
                $"{record.WorkflowRunId}.",
        };
    }

    /// <summary>
    /// VERIFYTOOLING (P-11): the chosen tooling digest, in this account's replica of the tooling repository, verified by Notation
    /// against the tooling repository's signing profile alone, as the signature hook verifies a service image (no hook runs for a
    /// task). Then a revision of the Plan task definition pinning exactly that digest, which the Plan state runs: a task
    /// definition names its image, and an override cannot.
    /// </summary>
    public static async Task<JsonObject> VerifyToolingAsync(
        JsonObject state, string executionName, ToolingSettings settings, Func<Task<(string Username, string Password)>> credentials,
        INotation notation, NotationLayout layout, ITaskDefinitions definitions)
    {
        var verified = state["verified"] as JsonObject;
        if (verified?["class"]?.GetValue<string>() != "config")
            throw new DeployRefused("verified.class", "VerifyTooling runs only for a verified config record.");
        var digest = verified["tooling"]?["digest"]?.GetValue<string>()
            ?? throw new DeployRefused("verified.tooling", "Verify chose no tooling digest.");

        var reference = $"{settings.Scope}@{digest}";
        var images = SignatureHook.ImagesFrom(new string?[] { reference }, new[] { settings.Scope });
        if (images.Count != 1 || images[0].Refusal is { } refusal)
            throw new DeployRefused("tooling", images.FirstOrDefault()?.Refusal ?? $"{reference} is not a digest reference in {settings.Scope}.");

        var notReady = await notation.InstallAsync(layout, SignatureHook.TrustPolicy(settings.TrustedProfiles, new[] { settings.Scope }));
        if (notReady is not null)
            throw new DeployRefused("tooling", $"the verifier is not usable: {notReady}");
        var (username, password) = await credentials();
        var (exit, stdout, stderr) = await notation.VerifyAsync(layout, reference, layout.Environment(username, password));
        var verdict = SignatureHook.InterpretNotation(reference, exit, stdout, stderr);
        if (!verdict.Verified)
            throw new DeployRefused("tooling", $"{reference}: {verdict.Detail}");

        // THE NETWORK THE BOOTSTRAP MADE (P-13), handed to the Plan state: exactly one subnet and one group, or nothing runs.
        var (subnets, groups) = (settings.Subnets, settings.SecurityGroups);
        if (subnets.Count != 1 || groups.Count != 1 || subnets.Concat(groups).Any(id => id.StartsWith('<')))
            throw new DeployRefused("tooling.network",
                $"the Plan task runs in the one subnet and one security group `lz bootstrapdeployer --apply` creates; this function is " +
                $"configured with {string.Join(",", subnets)} and {string.Join(",", groups)}. Run the apply.");

        var arn = await definitions.RegisterAsync(PlanTaskDefinition(settings, reference, executionName));
        // THE DEPLOY TASK PINS THE SAME DIGEST, so what deploys is exactly what planned, checked by the same verification.
        var deployArn = settings.Deploys
            ? await definitions.RegisterAsync(DeployTaskDefinition(settings, reference, executionName))
            : null;
        return new JsonObject
        {
            ["reference"] = reference,
            ["verified"] = verdict.Detail,
            ["taskDefinitionArn"] = arn,
            ["deployTaskDefinitionArn"] = deployArn,
            ["container"] = Container,
            ["subnets"] = new JsonArray(subnets.Select(x => (JsonNode?)x).ToArray()),
            ["securityGroups"] = new JsonArray(groups.Select(x => (JsonNode?)x).ToArray()),
        };
    }

    /// <summary>The Plan task's definition: the tooling image by digest, on the read-only task role, logging to its own group.</summary>
    public static Amazon.ECS.Model.RegisterTaskDefinitionRequest PlanTaskDefinition(ToolingSettings settings, string reference, string executionName) => new()
    {
        Family = settings.Family,
        RequiresCompatibilities = new List<string> { "FARGATE" },
        NetworkMode = Amazon.ECS.NetworkMode.Awsvpc,
        // Pulumi and two .NET processes; a foundation and a tenant preview measured well under this on a workstation.
        Cpu = "1024",
        Memory = "4096",
        ExecutionRoleArn = settings.ExecutionRoleArn,
        TaskRoleArn = settings.TaskRoleArn,
        RuntimePlatform = new Amazon.ECS.Model.RuntimePlatform
        {
            CpuArchitecture = Amazon.ECS.CPUArchitecture.X86_64,
            OperatingSystemFamily = Amazon.ECS.OSFamily.LINUX,
        },
        EphemeralStorage = new Amazon.ECS.Model.EphemeralStorage { SizeInGiB = 30 },
        ContainerDefinitions = new List<Amazon.ECS.Model.ContainerDefinition>
        {
            new()
            {
                Name = Container,
                Image = reference,
                Essential = true,
                Environment = new List<Amazon.ECS.Model.KeyValuePair>
                {
                    new() { Name = "AWS_REGION", Value = settings.Region },
                    new() { Name = "LZ_CREDENTIALS", Value = "ambient" },
                },
                LogConfiguration = new Amazon.ECS.Model.LogConfiguration
                {
                    LogDriver = Amazon.ECS.LogDriver.Awslogs,
                    Options = new Dictionary<string, string>
                    {
                        ["awslogs-group"] = settings.LogGroup,
                        ["awslogs-region"] = settings.Region,
                        ["awslogs-stream-prefix"] = "plan",
                    },
                },
            },
        },
        Tags = new List<Amazon.ECS.Model.Tag> { new() { Key = "lz:execution", Value = executionName } },
    };

    /// <summary>
    /// The Deploy task's definition: the Plan task's, on the infrastructure role, in its own family, logging under <c>deploy</c>.
    /// </summary>
    public static Amazon.ECS.Model.RegisterTaskDefinitionRequest DeployTaskDefinition(ToolingSettings settings, string reference, string executionName)
    {
        if (!settings.Deploys) throw new InvalidOperationException("this function is not configured with the Deploy task.");
        var request = PlanTaskDefinition(settings, reference, executionName);
        request.Family = settings.DeployFamily;
        request.TaskRoleArn = settings.DeployTaskRoleArn;
        request.ContainerDefinitions[0].LogConfiguration.Options["awslogs-stream-prefix"] = "deploy";
        return request;
    }

    /// <summary>
    /// READPLAN: the plan the task wrote, read back from the evidence store, decides - never the task's exit code alone, which
    /// says only that the task stopped. Absent: the task stopped before writing one (<see cref="PlanMissing"/>). Destructive:
    /// refused (<see cref="DestructivePlanRefused"/>); this slice has no override, since nothing is applied either way. An exit
    /// code that disagrees with the plan (2 for a plan that is not destructive, or the reverse) is refused as well. Otherwise
    /// the execution's result, which the Planned state ends on.
    /// </summary>
    public static async Task<JsonObject> ReadPlanAsync(JsonObject state, string executionName, string evidenceStore, IRecordStore evidence)
    {
        // READ FIRST, so an override that is not a JSON boolean is refused before anything else is said.
        var allowDestructive = DeployerInput.AllowsDestructivePlan(state);
        var key = PlanKeyFor(executionName);
        var exitCode = state["plan"]?["Containers"]?[0]?["ExitCode"]?.GetValueKind() == JsonValueKind.Number
            ? state["plan"]!["Containers"]![0]!["ExitCode"]!.GetValue<int>()
            : (int?)null;
        var stopped = state["plan"]?["StoppedReason"]?.GetValue<string>() ?? state["planError"]?["Cause"]?.GetValue<string>();

        var stored = await evidence.ReadAsync(evidenceStore, key)
            ?? throw new PlanMissing(
                $"the Plan task wrote no plan at s3://{evidenceStore}/{key} (exit code {exitCode?.ToString() ?? "unknown"}" +
                $"{(stopped is null ? "" : $", {stopped}")}). Its log group has why.");

        JsonObject plan;
        try
        {
            plan = JsonNode.Parse(stored.Json) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            throw new PlanMissing($"s3://{evidenceStore}/{key} is not a plan: {ex.Message}");
        }
        if (plan["schema"]?.GetValue<int>() != 1 || plan["plans"] is not JsonArray stacks || plan["destructive"]?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
            throw new PlanMissing($"s3://{evidenceStore}/{key} is not a schema-1 plan this deployer reads.");

        var destructive = plan["destructive"]!.GetValue<bool>();
        if (exitCode is { } code && code != (destructive ? InfraPlan.DestructiveExitCode : 0))
            throw new PlanMissing($"the Plan task exited {code}, which does not match its plan (destructive: {destructive}); the plan is not trusted.");

        var summary = stacks.OfType<JsonObject>().Select(s => new JsonObject
        {
            ["stack"] = s["stack"]?.GetValue<string>(),
            ["changes"] = s["changes"]?.DeepClone(),
            ["destructive"] = s["destructive"]?.GetValue<bool>(),
            ["destructiveSteps"] = new JsonArray((s["destructiveSteps"] as JsonArray ?? new JsonArray())
                .Select(d => (JsonNode?)JsonValue.Create($"{d?["op"]} {d?["urn"]}")).ToArray()),
        }).ToList();

        var named = string.Join("; ", summary.Where(s => s["destructive"]?.GetValue<bool>() == true)
            .Select(s => $"{s["stack"]}: {string.Join(", ", (s["destructiveSteps"] as JsonArray)!.Select(d => d!.GetValue<string>()))}"));
        // P-12: ONLY A PERSON STARTING THE EXECUTION BY HAND writes allowDestructivePlan; no trigger does. Recorded either way.
        if (destructive && !allowDestructive)
            throw new DestructivePlanRefused(
                $"the plan at s3://{evidenceStore}/{key} replaces or deletes: {named}. Nothing was applied. To apply it on purpose, start " +
                "the execution by hand with \"allowDestructivePlan\": true.");

        return new JsonObject
        {
            ["verdict"] = destructive ? "PlannedDestructiveAllowed" : "Planned",
            ["key"] = key,
            ["versionId"] = stored.VersionId,
            ["hasChanges"] = plan["hasChanges"]?.GetValue<bool>() ?? false,
            ["destructive"] = destructive,
            ["destructiveAllowed"] = destructive ? named : null,
            ["exitCode"] = exitCode,
            ["stacks"] = new JsonArray(summary.Select(s => (JsonNode?)s).ToArray()),
        };
    }

    /// <summary>
    /// READAPPLY, VerifyRollout for a config record (P3 D's deploy half): the Deploy task's result, read back from the evidence
    /// store, decides - never the task's exit code alone. Absent, unreadable or <c>failed</c>: <see cref="InfraDeployFailed"/>,
    /// since something may have been applied. <c>refused</c>: <see cref="InfraDeployRefused"/>, nothing applied. <c>deployed</c>
    /// with a second preview that is not empty: <see cref="InfraRolloutIncomplete"/>. Otherwise the evidence Record writes, under
    /// <c>deploys/config/...</c>, carrying the plan, the approval, what was applied and the empty second preview.
    /// </summary>
    public static async Task<JsonObject> ReadApplyAsync(
        JsonObject state, string executionName, string evidenceStore, IRecordStore evidence, DateTimeOffset now)
    {
        var key = InfraPlan.ApplyKeyFor(executionName);
        var exitCode = state["deploy"]?["Containers"]?[0]?["ExitCode"]?.GetValueKind() == JsonValueKind.Number
            ? state["deploy"]!["Containers"]![0]!["ExitCode"]!.GetValue<int>()
            : (int?)null;
        var stopped = state["deploy"]?["StoppedReason"]?.GetValue<string>() ?? state["deployError"]?["Cause"]?.GetValue<string>();

        var stored = await evidence.ReadAsync(evidenceStore, key)
            ?? throw new InfraDeployFailed(
                $"the Deploy task wrote no result at s3://{evidenceStore}/{key} (exit code {exitCode?.ToString() ?? "unknown"}" +
                $"{(stopped is null ? "" : $", {stopped}")}). It may have applied part of the plan; its log has how far it got.");

        JsonObject result;
        try
        {
            result = JsonNode.Parse(stored.Json) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (JsonException ex)
        {
            throw new InfraDeployFailed($"s3://{evidenceStore}/{key} is not a deploy result: {ex.Message}");
        }
        if (result["schema"]?.GetValueKind() != JsonValueKind.Number || result["schema"]!.GetValue<int>() != 1)
            throw new InfraDeployFailed($"s3://{evidenceStore}/{key} is not a schema-1 deploy result this deployer reads.");

        var outcome = result["outcome"]?.GetValue<string>();
        var reason = result["reason"]?.GetValue<string>();
        var applied = result["applied"] as JsonArray ?? new JsonArray();
        switch (outcome)
        {
            case "refused":
                throw new InfraDeployRefused(
                    $"the Deploy task applied nothing: {reason}" +
                    (result["differences"] is JsonArray { Count: > 0 } d ? $" ({string.Join("; ", d.Select(x => x?.GetValue<string>()))})" : "") + ".");
            case "failed":
                throw new InfraDeployFailed(
                    $"the Deploy task failed after applying [{string.Join(", ", applied.Select(x => x?.GetValue<string>()))}]: {reason}");
            case "deployed":
                break;
            default:
                throw new InfraDeployFailed($"s3://{evidenceStore}/{key} has outcome '{outcome}', which this deployer does not read.");
        }

        if (exitCode is { } code && code != 0)
            throw new InfraDeployFailed($"the Deploy task exited {code}, but its result says deployed; the result is not trusted.");
        if (result["after"]?["plans"] is not JsonArray after)
            throw new InfraDeployFailed($"s3://{evidenceStore}/{key} says deployed and carries no second preview.");
        var unsettled = after.OfType<JsonObject>()
            .Where(p => p["changes"] is JsonObject { Count: > 0 })
            .Select(p => $"{p["stack"]}: {p["changes"]!.ToJsonString()}")
            .ToList();
        if (unsettled.Count > 0)
            throw new InfraRolloutIncomplete(
                $"the Deploy task applied [{string.Join(", ", applied.Select(x => x?.GetValue<string>()))}], and its second preview still plans " +
                $"{string.Join("; ", unsettled)}. A stack is not what was applied.");

        var verified = state["verified"] as JsonObject
            ?? throw new DeployRefused("verified", "the state has no Verify result; ReadApply cannot run first.");
        var repo = verified["builtFrom"]?["repo"]?.GetValue<string>()
            ?? throw new DeployRefused("verified.builtFrom.repo", "the state's Verify result names no repository.");
        var evidenceKey = DeployEvidence.DeployedKey("config", repo, executionName);
        var body = new JsonObject
        {
            ["schema"] = 1,
            ["execution"] = executionName,
            ["recordedAt"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["class"] = "config",
            ["verified"] = verified.DeepClone(),
            ["tooling"] = state["tooling"]?["reference"]?.DeepClone(),
            ["plan"] = state["planned"]?.DeepClone(),
            ["approval"] = state["approval"]?.DeepClone(),
            ["apply"] = new JsonObject
            {
                ["key"] = key,
                ["versionId"] = stored.VersionId,
                ["applied"] = applied.DeepClone(),
                ["runtime"] = result["runtime"]?.DeepClone(),
            },
            ["secondPreview"] = "empty",
        };
        return new JsonObject
        {
            ["verdict"] = "Deployed",
            ["key"] = key,
            ["versionId"] = stored.VersionId,
            ["applied"] = applied.DeepClone(),
            ["evidence"] = new JsonObject { ["key"] = evidenceKey, ["body"] = body.ToJsonString() },
        };
    }
}

/// <summary>Bounds on the config bundle (P3 D).</summary>
public static class InfraPlanLimits
{
    /// <summary>The bundle is the root's tracked tree, about 220 KB today; 50 MB leaves room and refuses anything absurd.</summary>
    public const long MaxBundleBytes = 50L * 1024 * 1024;
}
