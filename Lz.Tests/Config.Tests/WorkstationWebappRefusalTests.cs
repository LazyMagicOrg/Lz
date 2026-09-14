using Lz.Aws.Pipeline;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// <c>lz deploywebapp</c> under the block (DecoupledCd.md P-8): an app the pipeline deploys from its client bundles has one
/// writer, the deployer, so the workstation command is refused for it — and for nothing else.
/// </summary>
public class WorkstationWebappRefusalTests
{
    private static SystemConfig Dev() => DeployerClientPlanTests.Config();

    [Theory]
    [InlineData("SellerApp", "Scutara/ScutaraSellerApp", "scu---webapp-sellerapp-4df6-b9c6")]
    [InlineData("sellerapp", "Scutara/ScutaraSellerApp", "scu---webapp-sellerapp-4df6-b9c6")]
    [InlineData("AdminApp", "Scutara/ScutaraAdminApp", "scu---webapp-adminapp-4df6-b9c6")]
    public void AnAppThePipelineDeploys_IsRefused_NamingItsRepositoryAndBucket(string folder, string repo, string bucket)
    {
        // The folder name is what the command derives the bucket from, as `Path.GetFileName(webappFolder)`.
        var refusal = DeployerPlanner.RefusalForWorkstationWebapp(Dev(), folder);

        Assert.NotNull(refusal);
        Assert.Contains(repo, refusal);
        Assert.Contains(bucket, refusal);
        Assert.Contains("scu/dev", refusal);
    }

    [Fact]
    public void AnAppThePipelineDoesNotDeploy_IsNotRefused()
    {
        Assert.Null(DeployerPlanner.RefusalForWorkstationWebapp(Dev(), "StoreApp"));
        Assert.Null(DeployerPlanner.RefusalForWorkstationWebapp(Dev(), "SellerAppStaging"));
    }

    [Fact]
    public void WithoutTheBlock_OrTheClientClass_OrAnAppNamed_NothingIsRefused()
    {
        var noBlock = Dev();
        noBlock.Pipeline = null;
        Assert.Null(DeployerPlanner.RefusalForWorkstationWebapp(noBlock, "SellerApp"));

        var disabled = Dev();
        disabled.Pipeline!.Enabled = false;
        Assert.Null(DeployerPlanner.RefusalForWorkstationWebapp(disabled, "SellerApp"));

        Assert.Null(DeployerPlanner.RefusalForWorkstationWebapp(DeployerClientPlanTests.Config(classes: new List<string> { "image" }), "SellerApp"));

        var unnamed = Dev();
        foreach (var r in unnamed.Pipeline!.Repositories!.Where(r => r.Class == "client")) r.Artifacts = null;
        Assert.Null(DeployerPlanner.RefusalForWorkstationWebapp(unnamed, "SellerApp"));
    }

    [Fact]
    public void OnlyTheAppsRepositoryNames_AreRefused()
    {
        // AdminApp's entry names no app: the pipeline deploys SellerApp only, so AdminApp's workstation deploy stays open.
        var sellerOnly = Dev();
        sellerOnly.Pipeline!.Repositories!.Single(r => r.Repo == "Scutara/ScutaraAdminApp").Artifacts = null;

        Assert.NotNull(DeployerPlanner.RefusalForWorkstationWebapp(sellerOnly, "SellerApp"));
        Assert.Null(DeployerPlanner.RefusalForWorkstationWebapp(sellerOnly, "AdminApp"));
    }

    [Fact]
    public void AClientConfigThePipelineCannotRead_IsRefused_RatherThanGuessed()
    {
        // Two apps for one repository: which buckets the pipeline owns cannot be known, so no bucket is assumed free.
        var refusal = DeployerPlanner.RefusalForWorkstationWebapp(
            DeployerClientPlanTests.Config(sellerArtifacts: new List<string> { "sellerapp", "adminapp" }), "StoreApp");

        Assert.NotNull(refusal);
        Assert.Contains("names 2 web apps", refusal);
    }

    [Fact]
    public void ACentralAuthTopology_WritesPerTenantBuckets_TheBundleDeployNeverOwns_AndIsNotRefused()
    {
        // deploywebapp writes {sk}-{tk}--webapp-storeapp-{suffix} there, and ClientApps refuses the topology outright.
        Assert.Null(DeployerPlanner.RefusalForWorkstationWebapp(DeployerClientPlanTests.Config(topology: "ecs-fargate-keycloak"), "SellerApp"));
    }
}
