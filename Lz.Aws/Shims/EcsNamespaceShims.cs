// Lz 0.11.0 axis restructure — [Obsolete] compile-compat shims for the retired
// Lz.Aws.Ecs namespace (the FargateAlb / keycloak lineage). Every renamed/moved
// public instance type keeps an empty derived class here.
// NOT shimmed (hard-but-mechanical breaks, see Lz/Migrations/AxisRestructure.md):
//   - static AwsFoundationLookup      -> Lz.Aws.Topologies.AwsEcsFargateKeycloakFoundationLookup
//   - static AwsEcsPostDeployHelper   -> Lz.Aws.Topologies.AwsEcsPostDeployHelper
//   - static AwsPrivateZoneCleanup    -> Lz.Aws.Ops.AwsPrivateZoneCleanup
//   - static AwsTenantConfigPublisher -> Lz.Aws.Ops.AwsTenantConfigPublisher
//   - static SmartstoreCognitoWiring  -> Lz.Aws.Auth.SmartstoreCognitoWiring
//   - enum UpdateOutcome              -> Lz.Aws.Ops.UpdateOutcome
//   - enum EdgeUpdateOutcome          -> Lz.Aws.Edge.EdgeUpdateOutcome
using Lz.Core.Config;
using Lz.Core.Definitions;

namespace Lz.Aws.Ecs;

[Obsolete("Renamed to Lz.Aws.Topologies.AwsEcsFargateKeycloakPlatformFactory (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsPlatformFactory : Lz.Aws.Topologies.AwsEcsFargateKeycloakPlatformFactory
{
    public AwsEcsPlatformFactory(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Topologies.AwsEcsFargateKeycloakFoundationPostDeployAction (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsFoundationPostDeployAction : Lz.Aws.Topologies.AwsEcsFargateKeycloakFoundationPostDeployAction
{
    public AwsFoundationPostDeployAction(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Topologies.AwsServicesPostDeployAction (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsServicesPostDeployAction : Lz.Aws.Topologies.AwsServicesPostDeployAction
{
    public AwsServicesPostDeployAction(
        SystemConfig config,
        SystemDefinition system,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
        : base(config, system, services, tenantKey, tenantConfig) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.FargateAlb.AwsFargateAlbNetworkComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsNetworkComponent : Lz.Aws.Compute.FargateAlb.AwsFargateAlbNetworkComponent
{
}

[Obsolete("Renamed to Lz.Aws.Compute.FargateAlb.AwsFargateAlbClusterComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsClusterComponent : Lz.Aws.Compute.FargateAlb.AwsFargateAlbClusterComponent
{
}

[Obsolete("Renamed to Lz.Aws.Compute.FargateAlb.AwsFargateAlbTenantServiceComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsTenantServiceComponent : Lz.Aws.Compute.FargateAlb.AwsFargateAlbTenantServiceComponent
{
}

[Obsolete("Renamed to Lz.Aws.Compute.FargateAlb.AwsFargateAlbServiceComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsServiceComponent : Lz.Aws.Compute.FargateAlb.AwsFargateAlbServiceComponent
{
    public AwsEcsServiceComponent(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.FargateAlb.AwsFargateAlbNetworkOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsNetworkOutputs : Lz.Aws.Compute.FargateAlb.AwsFargateAlbNetworkOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Compute.FargateAlb.AwsFargateAlbComputeOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsComputeOutputs : Lz.Aws.Compute.FargateAlb.AwsFargateAlbComputeOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Compute.FargateAlb.AwsFargateAlbServiceOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsServiceOutputs : Lz.Aws.Compute.FargateAlb.AwsFargateAlbServiceOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Compute.FargateAlb.AwsFargateAlbTransitionChecker (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsTransitionChecker : Lz.Aws.Compute.FargateAlb.AwsFargateAlbTransitionChecker
{
    public AwsTransitionChecker(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.FargateAlb.AwsTenantDnsAndCertComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsTenantDnsAndCertComponent : Lz.Aws.Compute.FargateAlb.AwsTenantDnsAndCertComponent
{
}

[Obsolete("Renamed to Lz.Aws.Edge.AwsCloudFrontStaticComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsCloudFrontComponent : Lz.Aws.Edge.AwsCloudFrontStaticComponent
{
}

[Obsolete("Renamed to Lz.Aws.Edge.AwsEdgeUpdater (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEdgeUpdater : Lz.Aws.Edge.AwsEdgeUpdater
{
    public AwsEdgeUpdater(string systemKey, string profile, string region) : base(systemKey, profile, region) { }
}

[Obsolete("Renamed to Lz.Aws.Edge.EdgeFunctionResult (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public record EdgeFunctionResult(
    string FunctionType,
    string? FunctionName,
    Lz.Aws.Edge.EdgeUpdateOutcome Outcome,
    string? Detail = null)
    : Lz.Aws.Edge.EdgeFunctionResult(FunctionType, FunctionName, Outcome, Detail);

[Obsolete("Renamed to Lz.Aws.Ops.AwsContainerUpdater (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsContainerUpdater : Lz.Aws.Ops.AwsContainerUpdater
{
    public AwsContainerUpdater(string profile, string region) : base(profile, region) { }
}

[Obsolete("Renamed to Lz.Aws.Ops.ContainerUpdateResult (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public record ContainerUpdateResult(string Service, Lz.Aws.Ops.UpdateOutcome Outcome, string Detail)
    : Lz.Aws.Ops.ContainerUpdateResult(Service, Outcome, Detail);

[Obsolete("Renamed to Lz.Aws.Ops.AwsSesComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsSesComponent : Lz.Aws.Ops.AwsSesComponent
{
}

[Obsolete("Renamed to Lz.Aws.Ops.AwsSeedTaskComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsSeedTaskComponent : Lz.Aws.Ops.AwsSeedTaskComponent
{
}

[Obsolete("Renamed to Lz.Aws.Ops.AwsSeedRunner (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsSeedRunner : Lz.Aws.Ops.AwsSeedRunner
{
    public AwsSeedRunner(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Ops.AwsParkManager (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsParkManager : Lz.Aws.Ops.AwsParkManager
{
    public AwsParkManager(string systemKey, string profile, string region) : base(systemKey, profile, region) { }
}

[Obsolete("Renamed to Lz.Aws.Auth.AwsKeycloakServiceComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsKeycloakEcsComponent : Lz.Aws.Auth.AwsKeycloakServiceComponent
{
}

[Obsolete("Renamed to Lz.Aws.Auth.AwsTenantKeycloakSeeder (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsTenantKeycloakSeeder : Lz.Aws.Auth.AwsTenantKeycloakSeeder
{
    public AwsTenantKeycloakSeeder(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Data.AwsFargateAlbTenantDataComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsTenantDataComponent : Lz.Aws.Data.AwsFargateAlbTenantDataComponent
{
}

[Obsolete("Renamed to Lz.Aws.Data.AwsRdsComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsRdsComponent : Lz.Aws.Data.AwsRdsComponent
{
}

[Obsolete("Renamed to Lz.Aws.Storage.AwsEfsComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEfsComponent : Lz.Aws.Storage.AwsEfsComponent
{
}

[Obsolete("Renamed to Lz.Aws.Tailscale.AwsTailscaleAsgComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsTailscaleAsgComponent : Lz.Aws.Tailscale.AwsTailscaleAsgComponent
{
}
