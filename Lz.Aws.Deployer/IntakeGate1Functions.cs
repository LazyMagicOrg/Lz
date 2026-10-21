using Amazon.CodeBuild;
using Amazon.CodeBuild.Model;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using Lz.Aws.Pipeline;

namespace Lz.Aws.Deployer;

// ---------------------------------------------------------------------------------------------
//  GATE 1'S HANDLERS (DecoupledCd.md §15.4, V-C), in the build account, thin as the others are. The decisions are in the
//  linked IntakeGate1.cs.
// ---------------------------------------------------------------------------------------------

public sealed class IntakeGate1StartFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var repositories = IntakeRepositories.Decode(IntakeClients.Required(IntakeEnvironment.Repositories));
        var result = await IntakeGate1StartStep.RunAsync(
            state, repositories,
            new S3ScratchPresigner(Clients.S3.Value, IntakeClients.Required(IntakeEnvironment.ScratchBucket)),
            new CodeBuildGate1(Gate1Clients.CodeBuild.Value),
            IntakeClients.Required(IntakeEnvironment.Gate1Project));
        context.Logger.LogInformation($"{executionName}: Gate 1 started, {result["buildId"]}");
        return Io.Write(result);
    }
}

public sealed class IntakeGate1VerdictFunction
{
    public async Task<Stream> HandleAsync(Stream input, ILambdaContext context)
    {
        var (executionName, state) = DeployerInput.Unwrap(await Io.ReadAsync(input));
        var result = await IntakeGate1VerdictStep.RunAsync(
            state, executionName, new CodeBuildGate1(Gate1Clients.CodeBuild.Value, Gate1Clients.Logs.Value),
            new S3IntakeEvidence(Clients.S3.Value, IntakeClients.Required(IntakeEnvironment.EvidenceStore)),
            DateTimeOffset.UtcNow);
        context.Logger.LogInformation($"{executionName}: Gate 1 {result["verdict"]}: {result["summary"]}");
        return Io.Write(result);
    }
}

internal static class Gate1Clients
{
    public static readonly Lazy<IAmazonCodeBuild> CodeBuild = new(() => new AmazonCodeBuildClient());
    public static readonly Lazy<IAmazonCloudWatchLogs> Logs = new(() => new AmazonCloudWatchLogsClient());
}

/// <summary>A GET URL for one scratch object, signed by the start function's role, which holds that read.</summary>
internal sealed class S3ScratchPresigner(IAmazonS3 s3, string bucket) : IScratchPresigner
{
    public string GetUrl(string key, TimeSpan lifetime) => s3.GetPreSignedURL(new GetPreSignedUrlRequest
    {
        BucketName = bucket, Key = key, Verb = HttpVerb.GET, Expires = DateTime.UtcNow.Add(lifetime), Protocol = Protocol.HTTPS,
    });
}

internal sealed class CodeBuildGate1(IAmazonCodeBuild codeBuild, IAmazonCloudWatchLogs? logs = null) : IGate1Builds
{
    public async Task<string> StartAsync(string project, IReadOnlyDictionary<string, string> variables)
        => (await codeBuild.StartBuildAsync(new StartBuildRequest
        {
            ProjectName = project,
            EnvironmentVariablesOverride = variables
                .Select(v => new EnvironmentVariable { Name = v.Key, Value = v.Value, Type = EnvironmentVariableType.PLAINTEXT })
                .ToList(),
        })).Build.Id;

    public async Task<Gate1Build> DescribeAsync(string buildId)
    {
        var build = (await codeBuild.BatchGetBuildsAsync(new BatchGetBuildsRequest { Ids = new List<string> { buildId } })).Builds?.SingleOrDefault()
            ?? throw new IntakeFault($"CodeBuild has no build {buildId}");
        // SDK v4: a collection with no members is null.
        var failed = (build.Phases ?? new List<BuildPhase>())
            .FirstOrDefault(p => p.PhaseStatus is { } s && s != StatusType.SUCCEEDED && s != StatusType.IN_PROGRESS);
        var phase = failed is null ? null
            : $"{failed.PhaseType}: {string.Join("; ", (failed.Contexts ?? new List<PhaseContext>()).Select(c => c.Message).Where(m => !string.IsNullOrWhiteSpace(m)))}";
        return new Gate1Build(build.BuildStatus?.Value ?? "", phase, build.ReportArns ?? new List<string>(),
            build.StartTime is { } s ? new DateTimeOffset(s, TimeSpan.Zero) : null,
            build.EndTime is { } e ? new DateTimeOffset(e, TimeSpan.Zero) : null,
            build.Logs?.GroupName, build.Logs?.StreamName);
    }

    public async Task<Gate1Report?> ReportAsync(IReadOnlyList<string> reportArns)
    {
        if (reportArns.Count == 0) return null;
        var reports = (await codeBuild.BatchGetReportsAsync(new BatchGetReportsRequest { ReportArns = reportArns.ToList() })).Reports ?? new List<Report>();
        if (reports.Count == 0) return null;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var r in reports.Where(r => r.TestSummary is not null))
        {
            total += r.TestSummary.Total ?? 0;
            foreach (var (status, n) in r.TestSummary.StatusCounts ?? new Dictionary<string, int>())
                counts[status] = counts.GetValueOrDefault(status) + n;
        }
        return new Gate1Report(total, counts);
    }

    public async Task<IReadOnlyList<string>> FailedCasesAsync(IReadOnlyList<string> reportArns, int max)
    {
        var names = new List<string>();
        foreach (var arn in reportArns)
        {
            var page = await codeBuild.DescribeTestCasesAsync(new DescribeTestCasesRequest
            {
                ReportArn = arn, MaxResults = Math.Min(100, max), Filter = new TestCaseFilter { Status = "FAILED" },
            });
            names.AddRange((page.TestCases ?? new List<TestCase>()).Select(t => t.Name));
            if (names.Count >= max) break;
        }
        return names.Take(max).ToList();
    }

    // From the head, page by page, until the forward token stops changing, which is how GetLogEvents says the end.
    public async Task<IReadOnlyList<string>> ListLinesAsync(Gate1Build build)
    {
        if (logs is null) throw new System.InvalidOperationException("this client was made without a logs client");
        if (build.LogGroup is null || build.LogStream is null) return Array.Empty<string>();
        var lines = new List<string>();
        string? token = null;
        while (true)
        {
            var page = await logs.GetLogEventsAsync(new GetLogEventsRequest
            {
                LogGroupName = build.LogGroup, LogStreamName = build.LogStream, StartFromHead = true, NextToken = token,
            });
            lines.AddRange((page.Events ?? new List<OutputLogEvent>()).Select(e => e.Message).Where(m => m.StartsWith(Gate2Lists.Prefix, StringComparison.Ordinal)));
            if (page.NextForwardToken is null || page.NextForwardToken == token) return lines;
            token = page.NextForwardToken;
        }
    }
}
