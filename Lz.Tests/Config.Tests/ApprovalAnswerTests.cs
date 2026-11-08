using System.Text.Json.Nodes;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// ProdApproval.md A2-A3: the execution waiting at Approve is found from its history, the token from the task's message attribute,
/// and the answer repeats what was offered - never what a person types.
/// </summary>
public class ApprovalAnswerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-08T02:00:00Z");

    private static JsonObject Offered(bool config = false)
    {
        var o = new JsonObject
        {
            ["marker"] = new JsonObject { ["key"] = "passed/sets/x.json", ["versionId"] = "mv-7" },
            ["set"] = new JsonObject { ["markerId"] = "sets/x" },
        };
        if (config)
        {
            o["set"]!["config"] = new JsonObject();
            o["planned"] = new JsonObject { ["key"] = "plans/p.json", ["versionId"] = "pv-3" };
        }
        return o;
    }

    private static (string, JsonObject) Entered(string name, JsonObject input) =>
        ("TaskStateEntered", new JsonObject { ["name"] = name, ["input"] = input.ToJsonString() });

    private static (string, JsonObject) Scheduled(string? token) =>
        ("TaskScheduled", new JsonObject
        {
            ["parameters"] = new JsonObject
            {
                ["MessageAttributes"] = token is null ? new JsonObject() : new JsonObject
                {
                    [ApprovalAnswer.TokenAttribute] = new JsonObject { ["DataType"] = "String", ["StringValue"] = token },
                },
            }.ToJsonString(),
        });

    private static (string, JsonObject) Exited(string name) => ("TaskStateExited", new JsonObject { ["name"] = name });

    [Fact]
    public void AnExecutionWaitingAtApprove_IsFound_WithItsTokenAndWhatItWasOffered()
    {
        var w = ApprovalAnswer.WaitingIn("exec-1", new[] { Entered("Verify", new JsonObject()), Exited("Verify"), Entered("Approve", Offered()), Scheduled("tok-1") });
        Assert.NotNull(w);
        Assert.Equal(("exec-1", "tok-1", "sets/x"), (w!.Execution, w.Token, w.Offered["set"]!["markerId"]!.GetValue<string>()));
    }

    [Fact]
    public void AnExecutionPastApprove_OrNeverThere_IsNotWaiting()
    {
        Assert.Null(ApprovalAnswer.WaitingIn("e", new[] { Entered("Approve", Offered()), Scheduled("t"), Exited("Approve"), Entered("ImageMember", new JsonObject()) }));
        Assert.Null(ApprovalAnswer.WaitingIn("e", new[] { Entered("Verify", new JsonObject()), Scheduled("t") }));   // another task's schedule
    }

    [Fact]
    public void AWaitingApproveWithNoToken_IsRefused_NeverGuessed()
        => Assert.Contains(ApprovalAnswer.TokenAttribute, Assert.Throws<InvalidOperationException>(
            () => ApprovalAnswer.WaitingIn("e", new[] { Entered("Approve", Offered()), Scheduled(null) })).Message);

    [Fact]
    public void TheAnswer_RepeatsTheMarker_AndWithAConfigThePlan()
    {
        var plain = ApprovalAnswer.AnswerFor(Offered(), "arn:aws:sts::1:assumed-role/Approver/me", Now);
        Assert.Equal(("sets/x", "mv-7", "arn:aws:sts::1:assumed-role/Approver/me", "2026-11-08T02:00:00Z"),
            (plain["markerId"]!.GetValue<string>(), plain["markerVersionId"]!.GetValue<string>(), plain["approver"]!.GetValue<string>(), plain["approvedAt"]!.GetValue<string>()));
        Assert.False(plain.ContainsKey("planVersionId"));

        var withPlan = ApprovalAnswer.AnswerFor(Offered(config: true), "a", Now);
        Assert.Equal(("plans/p.json", "pv-3"), (withPlan["planKey"]!.GetValue<string>(), withPlan["planVersionId"]!.GetValue<string>()));
    }

    [Fact]
    public void AConfigSetWithNoPlan_CannotBeAnswered()
    {
        var o = Offered(config: true);
        o.Remove("planned");
        Assert.Throws<InvalidOperationException>(() => ApprovalAnswer.AnswerFor(o, "a", Now));
    }

    [Fact]
    public void TheCommandAnswersTheNamedOrOnlyWaitingExecution_AndRefusesAGuess()
    {
        var a = new ApprovalAnswer.Waiting("a", "ta", Offered());
        var b = new ApprovalAnswer.Waiting("b", "tb", Offered());
        Assert.Equal("a", ApprovalCommands.Choose(new[] { a }, null, "m").Execution);
        Assert.Equal("b", ApprovalCommands.Choose(new[] { a, b }, "b", "m").Execution);
        Assert.Contains("--execution", Assert.Throws<InvalidOperationException>(() => ApprovalCommands.Choose(new[] { a, b }, null, "m")).Message);
        Assert.Throws<InvalidOperationException>(() => ApprovalCommands.Choose(new[] { a }, "c", "m"));
        Assert.Throws<InvalidOperationException>(() => ApprovalCommands.Choose(Array.Empty<ApprovalAnswer.Waiting>(), null, "m"));
    }
}
