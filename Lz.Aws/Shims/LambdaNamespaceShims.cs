// Lz 0.11.0 axis restructure — [Obsolete] compile-compat shims for the retired
// Lz.Aws.Lambda namespace (types moved to Lz.Aws.Compute.Lambda, Lz.Aws.Edge,
// Lz.Aws.Ops, and Lz.Aws.Topologies; "Lambda" is a capability name and survives).
// NOT shimmed (hard-but-mechanical breaks, see Lz/Migrations/AxisRestructure.md):
//   - sealed AwsLambdaApiOriginHolder -> Lz.Aws.Compute.Lambda.AwsLambdaApiOriginHolder
using Lz.Core.Config;
using Lz.Core.Definitions;

namespace Lz.Aws.Lambda;

[Obsolete("Renamed to Lz.Aws.Topologies.AwsLambdaCognitoDynamodbPlatformFactory (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaPlatformFactory : Lz.Aws.Topologies.AwsLambdaCognitoDynamodbPlatformFactory
{
    public AwsLambdaPlatformFactory(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Topologies.AwsLambdaPostDeployAction (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaPostDeployAction : Lz.Aws.Topologies.AwsLambdaPostDeployAction
{
    public AwsLambdaPostDeployAction(
        SystemConfig config, IReadOnlyList<ServiceDefinition> services,
        string? tenantKey, TenantConfig? tenantConfig)
        : base(config, services, tenantKey, tenantConfig) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaComputeComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaComputeComponent : Lz.Aws.Compute.Lambda.AwsLambdaComputeComponent
{
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaServiceComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaServiceComponent : Lz.Aws.Compute.Lambda.AwsLambdaServiceComponent
{
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaTenantServiceComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaTenantServiceComponent : Lz.Aws.Compute.Lambda.AwsLambdaTenantServiceComponent
{
    public AwsLambdaTenantServiceComponent(
        Lz.Aws.Compute.Lambda.AwsLambdaApiOriginHolder originHolder, SystemConfig systemConfig)
        : base(originHolder, systemConfig) { }
}

[Obsolete("Renamed to Lz.Aws.Edge.AwsCloudFrontKvsLambdaComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaCloudFrontComponent : Lz.Aws.Edge.AwsCloudFrontKvsLambdaComponent
{
    public AwsLambdaCloudFrontComponent(Lz.Aws.Compute.Lambda.AwsLambdaApiOriginHolder originHolder)
        : base(originHolder) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaContainerUpdater (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaContainerUpdater : Lz.Aws.Compute.Lambda.AwsLambdaContainerUpdater
{
    public AwsLambdaContainerUpdater(string profile, string region) : base(profile, region) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaConfigInitRunner (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaConfigInitRunner : Lz.Aws.Compute.Lambda.AwsLambdaConfigInitRunner
{
    public AwsLambdaConfigInitRunner(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaAdminSetupRunner (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaAdminSetupRunner : Lz.Aws.Compute.Lambda.AwsLambdaAdminSetupRunner
{
    public AwsLambdaAdminSetupRunner(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaPostSeedRunner (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaPostSeedRunner : Lz.Aws.Compute.Lambda.AwsLambdaPostSeedRunner
{
    public AwsLambdaPostSeedRunner(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaThemeDeployRunner (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsLambdaThemeDeployRunner : Lz.Aws.Compute.Lambda.AwsLambdaThemeDeployRunner
{
    public AwsLambdaThemeDeployRunner(SystemConfig config, string themesBucket) : base(config, themesBucket) { }
}

[Obsolete("Renamed to Lz.Aws.Ops.AwsGateCheckerLambdaComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsGateCheckerLambdaComponent : Lz.Aws.Ops.AwsGateCheckerLambdaComponent
{
}
