using Lz.Aws.Ops;
using Lz.Aws.Orchestration;
using Lz.Core.Config;
using Pulumi.Automation;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Runtime config as foundation resources (DecoupledCd.md §12 P3 F, P-15): which parameters the config declares and under which
/// names; imported only where the account holds them (an import of a missing parameter fails the preview); never a secret, the
/// key ring or the pipeline's; and which changes a restart must follow.
/// </summary>
public class RuntimeConfigTests
{
    private static SystemConfig Dev(Dictionary<string, string>? parameters = null, List<string>? readAtStart = null) => new()
    {
        SystemKey = "scu", Environment = "dev", Region = "us-west-2",
        RuntimeConfig = new RuntimeConfigConfig
        {
            Parameters = parameters ?? new Dictionary<string, string>
            {
                ["DemandGate/GraceDays"] = "90",
                ["Mcp/Scope"] = "https://match.aiproxydev.click/mcp/invoke",
                ["Fees/Verticals/residential_solar/LeadSelection"] = "true",
            },
            ReadAtStart = readAtStart ?? new List<string> { "Mcp/" },
        },
    };

    [Fact]
    public void EachParameter_IsNamedUnderTheSystemsPath_AndImportedOnlyWhereTheAccountHoldsIt()
    {
        var plan = RuntimeConfigPlanner.Plan(Dev(), new HashSet<string> { "/scu/dev/DemandGate/GraceDays", "/scu/dev/Mcp/Scope" });
        Assert.Equal(new[] { "DemandGate/GraceDays", "Fees/Verticals/residential_solar/LeadSelection", "Mcp/Scope" }, plan.Select(p => p.Key));
        Assert.Equal("/scu/dev/Fees/Verticals/residential_solar/LeadSelection", plan[1].Name);
        Assert.Equal(new[] { true, false, true }, plan.Select(p => p.Import));
        Assert.Equal("runtime-config-Fees.Verticals.residential_solar.LeadSelection", plan[1].LogicalName);
        Assert.Equal("90", plan[0].Value);
    }

    [Fact]
    public void WithoutTheSection_NothingIsPlanned()
        => Assert.Empty(RuntimeConfigPlanner.Plan(new SystemConfig { SystemKey = "scu", Environment = "dev" }, new HashSet<string>()));

    private static PlannedStep Step(OperationType op, string logical, string type = RuntimeConfigPlanner.ParameterType, params string[] diffs)
        => new(op, type, $"urn:pulumi:scu-dev::scu-dev::{type}::{logical}", null, null, diffs.Length == 0 ? null : diffs);

    [Fact]
    public void AnUpdateOfTheDescriptionOrTagsAlone_ChangesNoValue_SoRestartsNothing()
    {
        var steps = new[]
        {
            Step(OperationType.Import, "runtime-config-Mcp.Scope"),
            Step(OperationType.Update, "runtime-config-Mcp.Scope", RuntimeConfigPlanner.ParameterType, "description", "tags", "tagsAll", "version"),
            Step(OperationType.Update, "runtime-config-Ses.SuppressionTable", RuntimeConfigPlanner.ParameterType, "value", "version"),
        };
        Assert.Equal(new[] { "Ses/SuppressionTable" }, RuntimeConfigPlanner.ChangedKeys(steps));
    }

    [Fact]
    public void TheChangedKeys_AreTheParametersCreatedUpdatedOrReplaced_NotImported_NorAnythingElse()
    {
        var steps = new[]
        {
            Step(OperationType.Update, "runtime-config-Mcp.Scope"),
            Step(OperationType.Import, "runtime-config-DemandGate.GraceDays"),
            Step(OperationType.Create, "runtime-config-Fees.Verticals.residential_solar.LeadSelection"),
            Step(OperationType.Update, "scu-dev-something", RuntimeConfigPlanner.ParameterType),       // not a runtime parameter
            Step(OperationType.Update, "runtime-config-Mcp.Scope", "aws:lambda/function:Function"),   // not a parameter
        };
        Assert.Equal(new[] { "Fees/Verticals/residential_solar/LeadSelection", "Mcp/Scope" }, RuntimeConfigPlanner.ChangedKeys(steps));
    }

    [Fact]
    public void ARestartFollows_OnlyAChangeUnderAReadAtStartPrefix_BySegment()
    {
        Assert.Equal(new[] { "Mcp/Scope" }, RuntimeConfigPlanner.ReadAtStart(new[] { "Mcp/Scope", "DemandGate/GraceDays" }, new[] { "Mcp/" }));
        Assert.Empty(RuntimeConfigPlanner.ReadAtStart(new[] { "McpExtra/Thing" }, new[] { "Mcp" }));
        Assert.Equal(new[] { "Ses" }, RuntimeConfigPlanner.ReadAtStart(new[] { "Ses" }, new[] { "Ses/" }));
        Assert.Empty(RuntimeConfigPlanner.ReadAtStart(new[] { "Mcp/Scope" }, null));
    }

    // ---- what the config may declare ----

    [Fact]
    public void AValidSection_HasNoProblems()
        => Assert.Empty(RuntimeConfigConfig.Problems(Dev().RuntimeConfig));

    [Theory]
    [InlineData("bff/dataprotection/key-1", "under bff/")]
    [InlineData("pipeline/hold", "under pipeline/")]
    [InlineData("Pipeline/hold", "under pipeline/")]
    [InlineData("/Mcp/Scope", "is not segments")]
    [InlineData("Mcp/../Scope", "is not segments")]
    [InlineData("Mcp.Scope", "is not segments")]
    public void TheKeyRingThePipelinesParametersAndMalformedKeys_AreRefused(string key, string expected)
        => Assert.Contains(RuntimeConfigConfig.Problems(Dev(new Dictionary<string, string> { [key] = "x" }, new List<string>()).RuntimeConfig),
            p => p.Contains(expected));

    [Fact]
    public void AnEmptyOrOversizeValue_AndAReadAtStartPrefixNamingNothing_AreRefused()
    {
        Assert.Contains(RuntimeConfigConfig.Problems(Dev(new Dictionary<string, string> { ["A"] = "" }, new List<string>()).RuntimeConfig), p => p.Contains("has no value"));
        Assert.Contains(RuntimeConfigConfig.Problems(Dev(new Dictionary<string, string> { ["A"] = new string('x', 4097) }, new List<string>()).RuntimeConfig), p => p.Contains("4096"));
        Assert.Contains(RuntimeConfigConfig.Problems(Dev(readAtStart: new List<string> { "Ses/" }).RuntimeConfig), p => p.Contains("restarts nothing"));
        Assert.Contains(RuntimeConfigConfig.Problems(new RuntimeConfigConfig()), p => p.Contains("names no Parameters"));
    }

    [Fact]
    public void TheValidator_RefusesABadSection()
    {
        var c = new SystemConfig
        {
            SystemKey = "med", Environment = "dev", Profile = "p", Region = "us-west-2", SystemSuffix = "abcd-1234",
            RuntimeConfig = new RuntimeConfigConfig { Parameters = new Dictionary<string, string> { ["bff/x"] = "y" } },
        };
        Assert.Contains("under bff/", Assert.Throws<InvalidOperationException>(() => ConfigValidator.Validate(c, "test.yaml")).Message);
    }
}
