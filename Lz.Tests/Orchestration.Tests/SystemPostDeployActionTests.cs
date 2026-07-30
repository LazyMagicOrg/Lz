using Lz.Aws.AppRunner;
using Lz.Aws.Ecs;
using Lz.Aws.EcsExpress;
using Lz.Aws.Lambda;
using Lz.Core.Config;
using Lz.Core.Interfaces;

namespace Lz.Tests.Orchestration.Tests;

/// <summary>
/// Pins the deploysystem-phase post-deploy hook wiring. The hook exists because
/// GetFoundationPostDeployAction means two different things across topologies:
/// on the Keycloak topology it is the Keycloak DB init that `lz deployshared`
/// runs in the shared-services account, while the Cognito topologies used it
/// for the {SystemKey} system-table ensure — which NOTHING invoked, so a fresh
/// lambda/ecsexpress/apprunner system ended up with no system table (observed
/// live: the scu account was missing the `scu` table after a full deploy).
/// GetSystemPostDeployAction is the deploysystem-phase hook: table-ensure on
/// the Cognito factories, default-null elsewhere so the Keycloak topology's
/// deploysystem behaviour is unchanged.
/// </summary>
public class SystemPostDeployActionTests
{
    private static SystemConfig Config() => new()
    {
        SystemKey = "tst",
        Environment = "dev",
        Region = "us-west-2",
        Profile = "tst-dev",
    };

    [Fact]
    public void LambdaFactory_SystemPostDeploy_IsTheSystemTableEnsure()
    {
        IPlatformFactory factory = new AwsLambdaPlatformFactory(Config());
        Assert.IsType<AwsEcsExpressFoundationPostDeployAction>(factory.GetSystemPostDeployAction());
    }

    [Fact]
    public void EcsExpressFactory_SystemPostDeploy_IsTheSystemTableEnsure()
    {
        IPlatformFactory factory = new AwsEcsExpressPlatformFactory(Config());
        Assert.IsType<AwsEcsExpressFoundationPostDeployAction>(factory.GetSystemPostDeployAction());
    }

    [Fact]
    public void AppRunnerFactory_SystemPostDeploy_IsTheSystemTableEnsure()
    {
        IPlatformFactory factory = new AwsAppRunnerPlatformFactory(Config());
        Assert.IsType<AwsEcsExpressFoundationPostDeployAction>(factory.GetSystemPostDeployAction());
    }

    [Fact]
    public void KeycloakEcsFactory_SystemPostDeploy_IsNull_SoDeploysystemIsUnchanged()
    {
        // The Keycloak topology's GetFoundationPostDeployAction (DB init/seed)
        // belongs to deployshared; deploysystem must NOT gain a hook for it.
        IPlatformFactory factory = new AwsEcsPlatformFactory(Config());
        Assert.Null(factory.GetSystemPostDeployAction());
    }
}
