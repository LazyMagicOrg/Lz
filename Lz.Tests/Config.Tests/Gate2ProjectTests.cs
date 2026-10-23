using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Gate 2's CodeBuild project (DecoupledCd.md §15.5, V-E): a buildspec fixed in the project, never taken from a bundle, that
/// checks both bundles before it unzips them, runs the e2e tiers one at a time under a fixed FailSkips .runsettings and the
/// gate's fixed filters, then the browser @gate tests, and uploads every result file whatever happened; and a role that holds
/// the test logins and the e2e tier's Cognito calls, and may write nowhere but its results.
/// </summary>
public class Gate2ProjectTests
{
    private static readonly DeployerGate2Inputs Inputs = new("match.aiproxydev.click", "aiproxydev.click");

    private static SystemConfig Config()
    {
        var config = DeployerClientPlanTests.Config();
        config.Pipeline!.DeployOnBuildRecord = true;
        config.Pipeline.Alerts = true;
        config.Pipeline.Gate2 = true;
        config.Pipeline.Repositories!.Single(r => r.Repo == "Scutara/ScutaraService").Gate1 = new PipelineGate1Config
        {
            Run = "ServiceTest/bin/Debug/net10.0/ServiceTest.dll", BuildRoot = "/home/runner/work/ScutaraService/ScutaraService",
            Filter = "Category!=Aws&Category!=E2E", SdkVersion = "10.0.301", SdkSha512 = new string('a', 128),
        };
        return config;
    }

    private static DeployerGate2Project Project() => DeployerPlanner.Plan(Config(), DeployerClientPlanTests.TargetAccount,
        triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions,
        gate2Inputs: Inputs).Gate2!.Project;

    private static int At(string spec, string text)
    {
        var i = spec.IndexOf(text, StringComparison.Ordinal);
        Assert.True(i >= 0, $"the buildspec has no '{text}'");
        return i;
    }

    // ---- the buildspec ----

    [Fact]
    public void BothBundles_AreCheckedBeforeTheyAreUnzipped()
    {
        var spec = Project().Buildspec;
        Assert.True(At(spec, "\"${GATE2_E2E_BUNDLE_SHA256}  /tmp/e2e.zip\" | sha256sum -c -") < At(spec, "unzip -q /tmp/e2e.zip"));
        Assert.True(At(spec, "\"${GATE2_BROWSER_BUNDLE_SHA256}  /tmp/browser.zip\" | sha256sum -c -") < At(spec, "unzip -q /tmp/browser.zip"));
        Assert.True(At(spec, "sha512sum -c -") < At(spec, "tar -xzf /tmp/sdk.tgz"));
    }

    [Fact]
    public void TheE2eTiers_RunUnderAFixedFailSkipsRunsettings_OneAtATime_BeforeTheBrowserTier()
    {
        var spec = Project().Buildspec;
        Assert.Contains("<RunSettings><xUnit><FailSkips>true</FailSkips></xUnit></RunSettings>", spec);
        Assert.Contains("--settings /tmp/gate2.runsettings", spec);
        Assert.Contains("--logger \"trx;LogFileName=$tier.trx\"", spec);
        Assert.True(At(spec, "--settings /tmp/gate2.runsettings") < At(spec, "npx playwright test --grep @gate --forbid-only --retries=1 --reporter=json"));
        Assert.DoesNotContain("--parallel", spec);
    }

    [Fact]
    public void TheTiersFilters_AreGate2sFixedOnes_NotConfigs()
        => Assert.Equal(Gate2Lists.Variable(), Project().Environment["GATE2_TIERS"]);

    [Fact]
    public void TheFilesItUploads_AreTheOnesTheResultStateReads()
    {
        var spec = Project().Buildspec;
        foreach (var (tier, _) in Gate2Lists.Filters) Assert.Contains(Gate2ResultStep.Files, f => f.File == $"{tier}.trx");
        Assert.Contains(Gate2ResultStep.Files, f => f.File == "browser.json");
        Assert.Contains("> /tmp/gate2-results/browser.json", spec);
        Assert.Contains("--results-directory /tmp/gate2-results", spec);
    }

    /// <summary>A phase's command list as CodeBuild reads it, the buildspec parsed as YAML.</summary>
    private static IReadOnlyList<string> Commands(string spec, string phase, string list)
    {
        var yaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(spec);
        var phases = (Dictionary<object, object>)yaml["phases"];
        var p = (Dictionary<object, object>)phases[phase];
        return p.TryGetValue(list, out var l) ? ((List<object>)l).Select(c => (string)c).ToList() : Array.Empty<string>();
    }

    [Fact]
    public void TheResults_AreUploadedWhateverHappened_AndNoExitCodeDecides()
    {
        var spec = Project().Buildspec;
        Assert.Contains(Commands(spec, "build", "finally"), c => c.StartsWith("aws s3 cp /tmp/gate2-results/ \"$GATE2_RESULTS\" --recursive", StringComparison.Ordinal));
        Assert.Contains(Commands(spec, "build", "commands"), c => c.Contains("--settings /tmp/gate2.runsettings"));
        // Both runners exit 0 when tests skip, and non-zero when one fails: neither decides; the files do.
        Assert.Contains("--results-directory /tmp/gate2-results || true", spec);
        Assert.Contains("--reporter=json > /tmp/gate2-results/browser.json || true", spec);
    }

    [Fact]
    public void ALoginThatCannotBeFetched_FailsTheBuild_ThroughCodeBuildsOwnSecretsMapping()
    {
        var spec = Project().Buildspec;
        Assert.Contains("secrets-manager:", spec);
        foreach (var (env, key) in new[] { ("BFF_USER", "TenantUser"), ("BFF_PASSWORD", "TenantPassword"), ("ADMIN_USER", "AdminUser"),
                     ("ADMIN_PASSWORD", "AdminPassword"), ("BUYER_USER", "ConsumerUser"), ("BUYER_PASSWORD", "ConsumerPassword") })
            Assert.Contains($"{env}: \"scu/e2e-users:{key}\"", spec);
    }

    [Fact]
    public void TheE2eTier_RunsAsAGate_OnTheProjectsRole_AgainstTheEnvironmentsHost_WithTheStagedCache()
    {
        var env = Project().Environment;
        Assert.Equal("true", env["E2E__Gate"]);
        Assert.Equal("Ambient", env["AWS__CredentialSource"]);
        Assert.Equal("match.aiproxydev.click", env["E2E__BaseHost"]);
        Assert.Equal("aiproxydev.click", env["E2E__ApexHost"]);
        Assert.Equal("https://match.aiproxydev.click", env["BFF_HOST"]);
        Assert.Equal("https://match.aiproxydev.click", env["SELLER_HOST"]);
        Assert.Equal("/tmp/icecat", env["E2E__FixtureCacheDir"]);
        Assert.Equal("s3://scu-dev-gate2-results-4df6-b9c6/fixtures/icecat/", env["GATE2_FIXTURES"]);
        Assert.Equal(("ServiceTest/bin/Debug/net10.0/ServiceTest.dll", "/home/runner/work/ScutaraService/ScutaraService"), (env["GATE2_RUN"], env["GATE2_ROOT"]));
        Assert.Equal(("10.0.301", new string('a', 128)), (env["SDK_VERSION"], env["SDK_SHA512"]));
    }

    [Fact]
    public void ItsTimeout_IsTheMachinesWait()
        => Assert.Equal(DeployerPlanner.Gate2BuildTimeoutSeconds / 60, Project().TimeoutMinutes);

    // ---- the role ----

    private static IEnumerable<JsonElement> Statements(string policy) => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    private static IEnumerable<string> Strings(JsonElement e)
        => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!) : new[] { e.GetString()! };

    [Fact]
    public void TheRole_WritesOnlyResults_AndCannotOverwriteTheStagedCache()
    {
        var statements = Statements(Project().RolePolicy).ToList();
        var puts = statements.Where(s => s.GetProperty("Effect").GetString() == "Allow" && Strings(s.GetProperty("Action")).Contains("s3:PutObject")).ToList();
        Assert.Equal(new[] { "arn:aws:s3:::scu-dev-gate2-results-4df6-b9c6/*" }, puts.SelectMany(s => Strings(s.GetProperty("Resource"))));
        var deny = statements.Single(s => s.GetProperty("Effect").GetString() == "Deny");
        Assert.Contains("s3:PutObject", Strings(deny.GetProperty("Action")));
        Assert.Equal(new[] { "arn:aws:s3:::scu-dev-gate2-results-4df6-b9c6/fixtures/*" }, Strings(deny.GetProperty("Resource")));
        // Nothing in the evidence store: tests/ has one writer, Gate2Result (§15.5).
        Assert.DoesNotContain("deploy-evidence", Project().RolePolicy);
    }

    [Fact]
    public void TheRole_ReadsTheTestLogins_AndMakesTheE2eTiersCognitoCalls_InThisAccountOnly()
    {
        var statements = Statements(Project().RolePolicy).ToList();
        var secrets = statements.Single(s => Strings(s.GetProperty("Action")).Contains("secretsmanager:GetSecretValue"));
        Assert.Equal(new[] { "arn:aws:secretsmanager:us-west-2:503947800380:secret:scu/e2e-users-*", "arn:aws:secretsmanager:us-west-2:503947800380:secret:scu/party-members-*" },
            Strings(secrets.GetProperty("Resource")));
        var cognito = statements.Single(s => Strings(s.GetProperty("Action")).Contains("cognito-idp:AdminCreateUser"));
        Assert.Equal(new[] { "arn:aws:cognito-idp:us-west-2:503947800380:userpool/*" }, Strings(cognito.GetProperty("Resource")));
        Assert.Contains("cognito-idp:ListUserPoolClients", Strings(cognito.GetProperty("Action")));
    }

    [Fact]
    public void Gate2On_WithoutItsInputs_OrWithoutTheImagesGate1_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(Config(), DeployerClientPlanTests.TargetAccount,
            triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions));
        var noGate1 = Config();
        noGate1.Pipeline!.Repositories!.Single(r => r.Repo == "Scutara/ScutaraService").Gate1 = null;
        Assert.Throws<InvalidOperationException>(() => DeployerPlanner.Plan(noGate1, DeployerClientPlanTests.TargetAccount,
            triggerInputs: new DeployerTriggerInputs("scu-dev-cluster", new[] { "mp" }), clientInputs: DeployerClientPlanTests.Distributions, gate2Inputs: Inputs));
    }
}
