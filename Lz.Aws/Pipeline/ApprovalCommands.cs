using System.Text.Json.Nodes;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.StepFunctions;
using Amazon.StepFunctions.Model;
using Lz.Core.Config;

namespace Lz.Aws.Pipeline;

/// <summary>
/// <b><c>lz approve</c> AND <c>lz reject</c></b> (ScutaraDocs specs/Platform/ProdApproval.md A3): find the executions of this environment's
/// deployer that wait at Approve, show what each offers, and answer one. The token is read from the execution's own history - the task
/// Approve scheduled - never typed and never taken from a message; the answer is built from what Approve was given
/// (<see cref="ApprovalAnswer.AnswerFor"/>), so CheckApproval finds it repeats the offer. Who answers is whoever these credentials are:
/// IAM decides that (A4, A5), not this command.
/// </summary>
public static class ApprovalCommands
{
    private static (IAmazonStepFunctions Sfn, IAmazonSecurityTokenService Sts, string Machine) Clients(SystemConfig config, string account)
    {
        var creds = AwsCredentialsFactory.ResolveOrThrow(config.Profile);
        var endpoint = Amazon.RegionEndpoint.GetBySystemName(config.Region);
        IAmazonStepFunctions sfn = creds != null ? new AmazonStepFunctionsClient(creds, endpoint) : new AmazonStepFunctionsClient(endpoint);
        IAmazonSecurityTokenService sts = creds != null ? new AmazonSecurityTokenServiceClient(creds, endpoint) : new AmazonSecurityTokenServiceClient(endpoint);
        return (sfn, sts, $"arn:aws:states:{config.Region}:{account}:stateMachine:{config.SystemKey}-{config.Environment}-deployer");
    }

    /// <summary>Every RUNNING execution of the deployer that waits at Approve now, oldest first.</summary>
    public static async Task<IReadOnlyList<ApprovalAnswer.Waiting>> WaitingAsync(IAmazonStepFunctions sfn, string machineArn)
    {
        var waiting = new List<ApprovalAnswer.Waiting>();
        string? next = null;
        var running = new List<ExecutionListItem>();
        do
        {
            var page = await sfn.ListExecutionsAsync(new ListExecutionsRequest { StateMachineArn = machineArn, StatusFilter = ExecutionStatus.RUNNING, NextToken = next });
            running.AddRange(page.Executions ?? new List<ExecutionListItem>());
            next = page.NextToken;
        } while (next is not null);

        foreach (var e in running.OrderBy(e => e.StartDate))
        {
            var events = new List<(string, JsonObject)>();
            string? token = null;
            do
            {
                var page = await sfn.GetExecutionHistoryAsync(new GetExecutionHistoryRequest { ExecutionArn = e.ExecutionArn, NextToken = token, MaxResults = 1000 });
                foreach (var h in page.Events ?? new List<HistoryEvent>())
                {
                    if (h.StateEnteredEventDetails is { } entered)
                        events.Add(("TaskStateEntered", new JsonObject { ["name"] = entered.Name, ["input"] = entered.Input }));
                    else if (h.StateExitedEventDetails is { } exited)
                        events.Add(("TaskStateExited", new JsonObject { ["name"] = exited.Name }));
                    else if (h.TaskScheduledEventDetails is { } scheduled)
                        events.Add(("TaskScheduled", new JsonObject { ["parameters"] = scheduled.Parameters }));
                }
                token = page.NextToken;
            } while (token is not null);
            if (ApprovalAnswer.WaitingIn(e.Name, events) is { } w) waiting.Add(w);
        }
        return waiting;
    }

    /// <summary>The one waiting execution to answer: the named one, or the only one; refused when that is ambiguous or absent.</summary>
    public static ApprovalAnswer.Waiting Choose(IReadOnlyList<ApprovalAnswer.Waiting> waiting, string? execution, string machine)
    {
        if (execution is not null)
            return waiting.SingleOrDefault(w => w.Execution == execution)
                ?? throw new InvalidOperationException($"{execution} is not waiting at {ApprovalAnswer.StateName} in {machine}: nothing was answered.");
        return waiting.Count switch
        {
            0 => throw new InvalidOperationException($"nothing waits at {ApprovalAnswer.StateName} in {machine}."),
            1 => waiting[0],
            _ => throw new InvalidOperationException($"{waiting.Count} executions wait at {ApprovalAnswer.StateName}: name one with --execution ({string.Join(", ", waiting.Select(w => w.Execution))})."),
        };
    }

    private static void Show(ApprovalAnswer.Waiting w)
    {
        Console.WriteLine($"execution {w.Execution} waits at {ApprovalAnswer.StateName}:");
        foreach (var line in ApprovalAnswer.Summary(w.Offered)) Console.WriteLine("  " + line);
    }

    /// <summary><c>lz approve</c>: show the waiting set, confirm, and send the answer CheckApproval requires.</summary>
    public static async Task ApproveAsync(SystemConfig config, string? execution, Func<string, bool> confirm)
    {
        var account = await DeployerBootstrapper.ResolveAccountAsync(config.Profile, config.Region);
        var (sfn, sts, machine) = Clients(config, account);
        using (sfn) using (sts)
        {
            var w = Choose(await WaitingAsync(sfn, machine), execution, machine);
            Show(w);
            var approver = (await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest())).Arn;
            var answer = ApprovalAnswer.AnswerFor(w.Offered, approver, DateTimeOffset.UtcNow);
            if (!confirm($"Approve {w.Execution} as {approver}?"))
            {
                Console.WriteLine("Not approved; the execution still waits.");
                return;
            }
            await sfn.SendTaskSuccessAsync(new SendTaskSuccessRequest { TaskToken = w.Token, Output = answer.ToJsonString() });
            Console.WriteLine($"Approved {w.Execution}: {answer.ToJsonString()}");
        }
    }

    /// <summary><c>lz reject</c>: send ApprovalRejected with the reason; the set is decided <c>rejected</c>.</summary>
    public static async Task RejectAsync(SystemConfig config, string? execution, string reason, Func<string, bool> confirm)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new InvalidOperationException("a rejection needs a --reason; it is recorded in the decision.");
        var account = await DeployerBootstrapper.ResolveAccountAsync(config.Profile, config.Region);
        var (sfn, sts, machine) = Clients(config, account);
        using (sfn) using (sts)
        {
            var w = Choose(await WaitingAsync(sfn, machine), execution, machine);
            Show(w);
            var who = (await sts.GetCallerIdentityAsync(new GetCallerIdentityRequest())).Arn;
            if (!confirm($"Reject {w.Execution} as {who}?"))
            {
                Console.WriteLine("Not rejected; the execution still waits.");
                return;
            }
            // Step Functions caps a task failure's cause at 32768 characters; the reason is a person's sentence.
            var cause = $"rejected by {who}: {reason}";
            await sfn.SendTaskFailureAsync(new SendTaskFailureRequest
            {
                TaskToken = w.Token, Error = ApprovalAnswer.RejectedError, Cause = cause.Length > 32000 ? cause[..32000] : cause,
            });
            Console.WriteLine($"Rejected {w.Execution}.");
        }
    }
}
