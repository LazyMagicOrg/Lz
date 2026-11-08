using System.Text.Json.Nodes;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>
/// <b>WHO MAY ANSWER AN APPROVAL</b> (ScutaraDocs specs/Platform/ProdApproval.md A4-A5). Step Functions has no resource policy, so a state
/// machine cannot refuse an answerer: who may answer is decided by the answerers' own permissions. Lz writes the two Identity Center
/// permission sets' policies for an environment and the owner creates them (Identity Center lives in the management account, and a
/// person's account is the owner's to create):
/// <list type="bullet">
/// <item><b>The approver set</b> (A4): answer a task, read the deployer's executions and history (where <c>lz approve</c> finds the token),
/// read this environment's plans and the predecessor's markers, start a revert - nothing else.</item>
/// <item><b>The daily set</b> (A5, owner's Q2 (a)): AWS's ReadOnlyAccess, plus setting this environment's hold, with an explicit Deny on
/// answering a task - so the identity used every day cannot approve, whatever else is later added to it.</item>
/// </list>
/// Every deployer role carries the same Deny (<see cref="PipelineBootstrapPlanner.SelfRewriteDenied"/>).
/// </summary>
public static class ApproverPolicies
{
    /// <summary>What answers a task. Denied to the daily set and to every deployer role; allowed to the approver set alone.</summary>
    public static readonly string[] AnswerActions = { "states:SendTaskSuccess", "states:SendTaskFailure", "states:SendTaskHeartbeat" };

    /// <summary>The AWS managed policy the daily set starts from.</summary>
    public const string DailyManagedPolicyArn = "arn:aws:iam::aws:policy/ReadOnlyAccess";

    /// <param name="Approver">The approver permission set's inline policy.</param>
    /// <param name="DailyInline">The daily permission set's inline policy, beside <see cref="DailyManagedPolicyArn"/>.</param>
    public sealed record Policies(string Environment, string Account, JsonObject Approver, JsonObject DailyInline);

    /// <summary>
    /// The two policies for <paramref name="config"/>'s environment. Refused where the environment has no Pipeline block naming its
    /// account and evidence store: a policy over names guessed for an environment the pipeline does not yet know would be a policy
    /// over nothing.
    /// </summary>
    public static Policies For(SystemConfig config)
    {
        var p = config.Pipeline;
        var (sk, env, region) = (config.SystemKey, config.Environment, config.Region);
        if (p?.TargetAccountId is not { Length: > 0 } account)
            throw new InvalidOperationException($"{env} has no Pipeline.TargetAccountId: its Pipeline block (ProdBringUp.md PB-23) comes first.");
        if (p.ChainEvidenceStore is not { Length: > 0 } store)
            throw new InvalidOperationException($"{env} names no evidence store in the promotion chain (pipelineconfig.{sk}.yaml).");
        if (p.Approval?.Required != true)
            throw new InvalidOperationException($"{env} requires no approval (Pipeline.Approval.Required): there is nothing for an approver to answer.");

        var machine = $"arn:aws:states:{region}:{account}:stateMachine:{sk}-{env}-deployer";
        var statements = new JsonArray
        {
            // The task-token actions name no state machine: the token is the authority. Assigned only in this account, the set
            // answers only this account's tasks.
            Statement("AnswerApprovals", "Allow", AnswerActions.Take(2), "*"),
            Statement("FindWaitingExecutions", "Allow", new[] { "states:ListExecutions" }, machine),
            Statement("ReadTheirHistory", "Allow", new[] { "states:DescribeExecution", "states:GetExecutionHistory" },
                $"arn:aws:states:{region}:{account}:execution:{sk}-{env}-deployer:*"),
            Statement("ReadThePlans", "Allow", new[] { "s3:GetObject", "s3:GetObjectVersion" }, $"arn:aws:s3:::{store}/plans/*"),
            Statement("StartARevert", "Allow", new[] { "lambda:InvokeFunction" },
                $"arn:aws:lambda:{region}:{account}:function:{sk}-{env}-deployer-revert-start"),
        };
        // The predecessor's markers: what was tested before it reached here. Its bucket policy must admit this set too.
        if (p.Predecessor?.EvidenceStore is { Length: > 0 } before)
            statements.Add(Statement("ReadThePredecessorsMarkers", "Allow", new[] { "s3:GetObject", "s3:GetObjectVersion" }, $"arn:aws:s3:::{before}/passed/*"));

        var daily = new JsonArray
        {
            Statement("NeverAnswerAnApproval", "Deny", AnswerActions, "*"),
            Statement("SetTheHold", "Allow", new[] { "ssm:PutParameter" },
                $"arn:aws:ssm:{region}:{account}:parameter{DeployerPlanner.HoldParameterName(config)}"),
        };

        return new Policies(env, account,
            new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = statements },
            new JsonObject { ["Version"] = "2012-10-17", ["Statement"] = daily });
    }

    private static JsonObject Statement(string sid, string effect, IEnumerable<string> actions, string resource) => new()
    {
        ["Sid"] = sid,
        ["Effect"] = effect,
        ["Action"] = new JsonArray(actions.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        ["Resource"] = resource,
    };
}
