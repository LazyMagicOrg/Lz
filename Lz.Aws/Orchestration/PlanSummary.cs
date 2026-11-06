using System.Text.Json;
using System.Text.Json.Serialization;
using Pulumi.Automation;

namespace Lz.Aws.Orchestration;

/// <summary>One step a plan summary names: what it does, to which resource, and for a counted task-definition replace, why.</summary>
public sealed record PlanSummaryStep(string Op, string Type, string Urn, string? Why = null);

/// <summary>
/// WHAT A PREVIEW PLANNED, AS DATA (DecoupledCd.md P3 D): the same counts and the same destructive verdict the preview prints,
/// so the deployer's Plan state, which runs the preview where nobody reads its output, can write it to the evidence store under
/// <c>plans/</c> and RefuseDestructivePlan can read it back. Built from the preview's change summary and its captured steps by
/// <see cref="From"/>, which is the only place the verdict is decided: <see cref="SystemDeployment.PrintPreviewSummary"/> prints
/// what it returns.
/// </summary>
public sealed record PlanSummary(
    string Stack,
    IReadOnlyDictionary<string, int> Changes,
    int Replaces,
    int Deletes,
    int ImageOnlyReplaces,
    bool Destructive,
    IReadOnlyList<PlanSummaryStep> DestructiveSteps,
    IReadOnlyList<PlanSummaryStep> CountedTaskDefinitionReplaces,
    IReadOnlyList<string> DigestRefusals)
{
    /// <summary>The version of this shape, so a reader can refuse one it does not know.</summary>
    public int Schema { get; init; } = 1;

    /// <summary>Whether the plan has any change at all.</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>
    /// The verdict from the change summary and the steps. Replaces and deletes are counted from the change summary; an
    /// image-only task-definition replace (<see cref="TenantPlanCheck.IsImageOnlyTaskDefinitionReplace"/>) is taken out, since
    /// every image roll makes one. Without steps (the shared stack's preview captures none) nothing is taken out.
    /// </summary>
    public static PlanSummary From(
        string stack, IReadOnlyDictionary<OperationType, int>? changeSummary,
        IReadOnlyList<PlannedStep>? steps = null, IReadOnlyList<string>? digestRefusals = null)
    {
        var changes = (changeSummary ?? new Dictionary<OperationType, int>())
            .Where(kv => kv.Key != OperationType.Same && kv.Value > 0)
            .OrderBy(kv => kv.Key)
            .ToDictionary(kv => OpName(kv.Key), kv => kv.Value, StringComparer.Ordinal);

        var replaces = 0;
        var deletes = 0;
        changeSummary?.TryGetValue(OperationType.Replace, out replaces);
        changeSummary?.TryGetValue(OperationType.Delete, out deletes);

        var imageOnly = 0;
        var counted = new List<PlanSummaryStep>();
        var destructiveSteps = new List<PlanSummaryStep>();
        if (steps is not null)
        {
            imageOnly = steps.Count(TenantPlanCheck.IsImageOnlyTaskDefinitionReplace);
            foreach (var td in steps.Where(s => s.Type == TenantPlanCheck.TaskDefinitionType && s.Op == OperationType.Replace))
                if (TenantPlanCheck.WhyNotImageOnly(td) is { } why)
                    counted.Add(new PlanSummaryStep(OpName(td.Op), td.Type, td.Urn, why));
            destructiveSteps.AddRange(TenantPlanCheck.Destructive(steps).Select(s => new PlanSummaryStep(OpName(s.Op), s.Type, s.Urn)));
            replaces = Math.Max(0, replaces - imageOnly);
        }

        return new PlanSummary(stack, changes, replaces, deletes, imageOnly, replaces + deletes > 0,
            destructiveSteps, counted, digestRefusals ?? []);
    }

    private static string OpName(OperationType op) => op.ToString().ToLowerInvariant();

    /// <summary>How a summary is written for the evidence store: camelCase, indented.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>
/// WHAT ONE PREVIEW COMMAND PLANNED, every stack it previewed (<c>--plan-json</c>): the file the deployer's Plan state copies to
/// the evidence store under <c>plans/</c>. <see cref="Destructive"/> is true when any stack's plan is.
/// </summary>
public sealed record PlanFile(string Command, IReadOnlyList<PlanSummary> Plans)
{
    public int Schema { get; init; } = 1;
    public bool Destructive => Plans.Any(p => p.Destructive);
    public bool HasChanges => Plans.Any(p => p.HasChanges);

    public string ToJson() => JsonSerializer.Serialize(this, PlanSummary.Json);

    /// <summary>Write it to a path, its directory made if missing.</summary>
    public void WriteTo(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToJson());
    }
}
