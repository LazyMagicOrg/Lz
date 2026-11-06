using System.Text.Json;
using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// A service's image input (DecoupledCd.md §12 P3 J, ImageThroughPulumi.md J1): its format, parsed fail-closed; the image reference
/// it is read from; deploytenant's rule that it must exist and name what runs; and the deployer's functions configured and granted
/// for it only where the pipeline signs the images.
/// </summary>
public class ImageInputTests
{
    private const string Digest = "sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95";
    private const string Other = "sha256:b330844ac7c512956b92b2a0229ec14223119dbf0c075fbfef7df60894fa20eb";

    private static readonly ImageInput Input = new(Digest, "scu-4df6-b9c6-aiphost", "2026-11-06T14:53:29Z", "deployer:req-1", "2026-11-06T15:00:00Z");

    [Fact]
    public void AnInput_RoundTrips_AndItsNameIsTheServicesUnderPipelineImage()
    {
        Assert.Equal(Input, ImageInputFormat.Parse(ImageInputFormat.Write(Input), "x"));
        Assert.Equal(Input with { BuiltAt = null }, ImageInputFormat.Parse(ImageInputFormat.Write(Input with { BuiltAt = null }), "x"));
        Assert.Equal("/scu/dev/pipeline/image/scu-mp-aiphost", ImageInputFormat.NameFor("scu", "dev", "scu-mp-aiphost"));
    }

    [Theory]
    [InlineData("""{"schema":2,"digest":"sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95","repository":"r","writer":"w","writtenAt":"t"}""", "schema")]
    [InlineData("""{"schema":1,"digest":"latest","repository":"r","writer":"w","writtenAt":"t"}""", "not a sha256 digest")]
    [InlineData("""{"schema":1,"digest":"sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95","repository":"r","writer":"w","writtenAt":"t","builtAt":"yesterday"}""", "not a UTC time")]
    [InlineData("""{"schema":1,"digest":"sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95","repository":"r","writtenAt":"t"}""", "no writer")]
    [InlineData("""{"schema":1,"digest":"sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95","repository":"r","writer":"w","writtenAt":"t","tag":"latest"}""", "'tag'")]
    [InlineData("not json", "not valid JSON")]
    public void AnInputThatIsNotOneThisLzWrites_IsRefusedByName(string json, string expected)
        => Assert.Contains(expected, Assert.Throws<InvalidOperationException>(() => ImageInputFormat.Parse(json, "/scu/dev/pipeline/image/x")).Message);

    [Theory]
    [InlineData("503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-aiphost@" + Digest, "scu-4df6-b9c6-aiphost")]
    [InlineData("503947800380.dkr.ecr.us-west-2.amazonaws.com/team/app@" + Digest, "team/app")]
    public void AnImageReference_NamesItsRepositoryAndDigest(string image, string repository)
        => Assert.Equal((repository, Digest), ImageInputFormat.ParseImage(image));

    [Theory]
    [InlineData("503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-aiphost:latest")]
    [InlineData("scu-4df6-b9c6-aiphost@" + Digest)]
    [InlineData("")]
    public void ATagOrARegistrylessReference_NamesNoInput(string image)
        => Assert.Null(ImageInputFormat.ParseImage(image));

    [Fact]
    public void UpdatecontainersRevision_DropsThePreviousBuildTime_AndKeepsEveryOtherTag()
    {
        var tags = new List<Amazon.ECS.Model.Tag>
        {
            new() { Key = "System", Value = "scu" }, new() { Key = DeployOrdering.BuiltAtTag, Value = "2026-11-06T14:53:29Z" }, new() { Key = "lz:defsHash", Value = "h" },
        };
        Assert.Equal(new[] { "System", "lz:defsHash" }, Lz.Aws.Ops.AwsContainerUpdater.TagsWithoutBuiltAt(tags)!.Select(t => t.Key));
        Assert.Null(Lz.Aws.Ops.AwsContainerUpdater.TagsWithoutBuiltAt(null));
    }

    // ---- deploytenant's rule ----

    [Fact]
    public void Deploytenant_ProceedsOnlyWithAnInputNamingTheRunningDigest()
    {
        const string name = "/scu/dev/pipeline/image/scu-mp-aiphost";
        Assert.Null(ImageInputEcs.WhyNotProceed(name, Input, Digest));
        Assert.Contains("bootstrapdeployer --apply", ImageInputEcs.WhyNotProceed(name, null, Digest));
        var disagree = ImageInputEcs.WhyNotProceed(name, Input, Other);
        Assert.Contains("in flight", disagree);
        Assert.Contains("deployer:req-1", disagree);
    }

    // ---- the plan ----

    private static PipelineDeployer Plan(bool enforce)
    {
        var c = DeployerClientPlanTests.Config(classes: new List<string> { "image", "client" });
        c.Pipeline!.EnforceSignatures = enforce;
        return DeployerPlanner.Plan(c, DeployerClientPlanTests.TargetAccount, clientInputs: DeployerClientPlanTests.Distributions);
    }

    private static IEnumerable<JsonElement> Statements(string policy) => JsonDocument.Parse(policy).RootElement.GetProperty("Statement").EnumerateArray();

    [Fact]
    public void WhereThePipelineSignsTheImages_PrepareAndRecordFailure_AreConfiguredAndGrantedTheInputsAlone()
    {
        var plan = Plan(enforce: true);
        foreach (var handler in new[] { DeployerHandlers.Prepare, DeployerHandlers.RecordFailure })
        {
            var f = plan.Functions.Single(x => x.Handler == handler);
            Assert.Equal("/scu/dev/pipeline/image/", f.Environment[DeployerEnvironment.ImageInputPrefix]);
            var grant = Statements(f.Policy).Single(s => s.GetProperty("Sid").GetString() == "ReadAndWriteTheImageInputs");
            Assert.Equal($"arn:aws:ssm:us-west-2:{DeployerClientPlanTests.TargetAccount}:parameter/scu/dev/pipeline/image/*", grant.GetProperty("Resource").GetString());
            Assert.Equal(new[] { "ssm:GetParameter", "ssm:PutParameter" }, grant.GetProperty("Action").EnumerateArray().Select(a => a.GetString()));
        }
        // ONLY RecordFailure reads what the service runs, to write it back; Prepare already could.
        var failure = plan.Functions.Single(x => x.Handler == DeployerHandlers.RecordFailure);
        Assert.Contains(Statements(failure.Policy), s => s.GetProperty("Sid").GetString() == "ReadWhatTheServiceRuns");
    }

    [Fact]
    public void WhereItDoesNot_NothingOfTheInputsIsPlanned()
    {
        var plan = Plan(enforce: false);
        foreach (var f in plan.Functions)
        {
            Assert.DoesNotContain(DeployerEnvironment.ImageInputPrefix, f.Environment.Keys);
            Assert.DoesNotContain("pipeline/image", f.Policy);
        }
    }
}
