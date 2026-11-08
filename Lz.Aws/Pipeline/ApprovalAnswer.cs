using System.Text.Json.Nodes;

namespace Lz.Aws.Pipeline;

/// <summary>
/// <b>THE APPROVAL'S ANSWER</b> (ScutaraDocs specs/Platform/ProdApproval.md A1-A3): the names the promoted machine's Approve state and
/// <c>lz approve</c> / <c>lz reject</c> share, and the pure parts of answering - finding the execution that waits, what it offers, and
/// the answer CheckApproval requires. The answer must name what was offered: the set's marker id and version, and with a config member
/// the plan's version. It is built from what the waiting state was given, never typed by a person.
/// </summary>
public static class ApprovalAnswer
{
    /// <summary>The SNS message attribute the task token travels in - never the body, which an email shows.</summary>
    public const string TokenAttribute = "lz-task-token";

    /// <summary>The error <c>lz reject</c> sends; the decision reads it as <c>rejected</c>.</summary>
    public const string RejectedError = "ApprovalRejected";

    /// <summary>What the machine names an unanswered approval; the decision reads it as <c>unapproved</c>.</summary>
    public const string TimedOutError = "ApprovalTimedOut";

    /// <summary>What CheckApproval names an answer that does not repeat the offer.</summary>
    public const string DiffersError = "ApprovalDiffers";

    /// <summary>The state the approval waits in.</summary>
    public const string StateName = "Approve";

    /// <summary>One execution waiting at Approve: its name, the token, and the state it was given there.</summary>
    public sealed record Waiting(string Execution, string Token, JsonObject Offered);

    /// <summary>
    /// From an execution's history, oldest first, the approval it waits in now, or null: the last <c>Approve</c> entry with no exit
    /// after it, and the token from the task it scheduled. A history that names an Approve with no token is refused, never guessed.
    /// </summary>
    /// <param name="events">(type, details JSON) per event, in order: TaskStateEntered carries <c>name</c> and <c>input</c>;
    /// TaskScheduled carries <c>parameters</c>; TaskStateExited carries <c>name</c>.</param>
    public static Waiting? WaitingIn(string execution, IReadOnlyList<(string Type, JsonObject Details)> events)
    {
        JsonObject? offered = null;
        string? token = null;
        foreach (var (type, details) in events)
        {
            switch (type)
            {
                case "TaskStateEntered" when details["name"]?.GetValue<string>() == StateName:
                    offered = JsonNode.Parse(details["input"]?.GetValue<string>() ?? "{}") as JsonObject;
                    token = null;
                    break;
                case "TaskScheduled" when offered is not null && token is null:
                    var parameters = JsonNode.Parse(details["parameters"]?.GetValue<string>() ?? "{}");
                    token = parameters?["MessageAttributes"]?[TokenAttribute]?["StringValue"]?.GetValue<string>();
                    break;
                case "TaskStateExited" when details["name"]?.GetValue<string>() == StateName:
                    offered = null;
                    token = null;
                    break;
            }
        }
        if (offered is null) return null;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException($"{execution} waits at {StateName}, and the task it scheduled carries no {TokenAttribute}: nothing here can answer it.");
        return new Waiting(execution, token!, offered);
    }

    /// <summary>
    /// The answer CheckApproval requires, from what Approve was given: the marker id and version, and with a config member the plan's
    /// key and version; with who answered and when, for the evidence.
    /// </summary>
    public static JsonObject AnswerFor(JsonObject offered, string approverArn, DateTimeOffset now)
    {
        var markerId = offered["set"]?["markerId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("the waiting state names no set marker; there is nothing to approve.");
        var markerVersion = offered["marker"]?["versionId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("the waiting state names no marker version.");
        var answer = new JsonObject
        {
            ["markerId"] = markerId,
            ["markerVersionId"] = markerVersion,
            ["approver"] = approverArn,
            ["approvedAt"] = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        };
        if (offered["set"]?["config"] is not null)
        {
            answer["planKey"] = offered["planned"]?["key"]?.GetValue<string>()
                ?? throw new InvalidOperationException("the set has a config member and the waiting state names no plan; a plan is what is approved.");
            answer["planVersionId"] = offered["planned"]?["versionId"]?.GetValue<string>()
                ?? throw new InvalidOperationException("the waiting state's plan names no version.");
        }
        return answer;
    }

    /// <summary>What the approver is shown: one line per member and the plan, from the waiting state.</summary>
    public static IReadOnlyList<string> Summary(JsonObject offered)
    {
        var lines = new List<string> { $"set {offered["set"]?["markerId"]} (marker version {offered["marker"]?["versionId"]})" };
        void Member(string what, JsonNode? m)
        {
            var built = m?["verified"]?["builtFrom"];
            if (built is null) return;
            var version = m!["verified"]?["serviceVersion"] ?? m["verified"]?["identity"]?["versionId"] ?? m["verified"]?["digest"];
            lines.Add($"  {what,-7} {built["repo"]} {built["commit"]?.GetValue<string>()?[..Math.Min(12, built["commit"]!.GetValue<string>().Length)]} ({version})");
        }
        Member("config", offered["set"]?["config"]);
        Member("image", offered["set"]?["image"]);
        foreach (var b in offered["set"]?["bundles"]?.AsArray() ?? new JsonArray()) Member("client", b);
        Member("site", offered["set"]?["site"]);
        if (offered["planned"] is JsonObject plan)
            lines.Add($"  plan    {plan["key"]} (version {plan["versionId"]}): {(plan["destructive"]?.GetValue<bool>() == true ? "DESTRUCTIVE, " : "")}{plan["summary"] ?? plan["changes"]}");
        return lines;
    }
}
