// Lz 0.11.0 axis restructure — [Obsolete] compile-compat shims for the retired
// Lz.Aws.EcsExpress namespace. Every renamed/moved public instance type keeps an
// empty derived class here so existing consumers get warnings, not breaks.
// NOT shimmed (hard-but-mechanical breaks, see Lz/Migrations/AxisRestructure.md):
//   - static AwsEcsExpressFoundationLookup -> Lz.Aws.Topologies.AwsEcsFargateCognitoDynamodbFoundationLookup
// This file is exempt from the "no EcsExpress outside Topologies/" grammar invariant.
using Lz.Core.Config;
using Lz.Core.Definitions;

namespace Lz.Aws.EcsExpress;

[Obsolete("Renamed to Lz.Aws.Topologies.AwsEcsFargateCognitoDynamodbPlatformFactory (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsExpressPlatformFactory : Lz.Aws.Topologies.AwsEcsFargateCognitoDynamodbPlatformFactory
{
    public AwsEcsExpressPlatformFactory(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Topologies.AwsEcsFargateCognitoDynamodbFoundationPostDeployAction (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsExpressFoundationPostDeployAction : Lz.Aws.Topologies.AwsEcsFargateCognitoDynamodbFoundationPostDeployAction
{
    public AwsEcsExpressFoundationPostDeployAction(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Topologies.AwsEcsFargateCognitoDynamodbPostDeployAction (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsExpressPostDeployAction : Lz.Aws.Topologies.AwsEcsFargateCognitoDynamodbPostDeployAction
{
    public AwsEcsExpressPostDeployAction(
        SystemConfig config,
        IReadOnlyList<ServiceDefinition> services,
        string? tenantKey = null,
        TenantConfig? tenantConfig = null)
        : base(config, services, tenantKey, tenantConfig) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.Fargate.AwsFargateNetworkComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsExpressNetworkComponent : Lz.Aws.Compute.Fargate.AwsFargateNetworkComponent
{
}

[Obsolete("Renamed to Lz.Aws.Compute.Fargate.AwsFargateComputeComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsExpressComputeComponent : Lz.Aws.Compute.Fargate.AwsFargateComputeComponent
{
}

[Obsolete("Renamed to Lz.Aws.Compute.Fargate.AwsFargateTenantServiceComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsExpressTenantServiceComponent : Lz.Aws.Compute.Fargate.AwsFargateTenantServiceComponent
{
    public AwsEcsExpressTenantServiceComponent(SystemConfig? systemConfig = null) : base(systemConfig) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.Fargate.AwsFargateNetworkOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsExpressNetworkOutputs : Lz.Aws.Compute.Fargate.AwsFargateNetworkOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Compute.Fargate.AwsFargateComputeOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsExpressComputeOutputs : Lz.Aws.Compute.Fargate.AwsFargateComputeOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Edge.AwsCloudFrontKvsComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsEcsExpressCloudFrontComponent : Lz.Aws.Edge.AwsCloudFrontKvsComponent
{
}
