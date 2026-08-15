// Lz 0.11.0 axis restructure — [Obsolete] compile-compat shims for the retired
// Lz.Aws.AppRunner namespace. Only the CROSS-TOPOLOGY components that other
// topologies reused are shimmed; the five apprunner-topology-only types
// (AwsAppRunnerPlatformFactory, AwsAppRunnerComputeComponent,
// AwsAppRunnerCloudFrontComponent, AwsAppRunnerTenantServiceComponent,
// AwsAppRunnerPostDeployAction) were DELETED with the topology and get no shim
// (fleet audit: nobody references them).
// NOT shimmed (hard-but-mechanical breaks, see Lz/Migrations/AxisRestructure.md):
//   - static AwsAppRunnerFoundationLookup -> Lz.Aws.Topologies.AwsLambdaCognitoDynamodbFoundationLookup
//   - internal BffWiring / BffStackOutputs -> Lz.Aws.Shared (internal; no external surface)
using Lz.Core.Config;

namespace Lz.Aws.AppRunner;

[Obsolete("Renamed to Lz.Aws.Auth.AwsCognitoComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerCognitoComponent : Lz.Aws.Auth.AwsCognitoComponent
{
}

[Obsolete("Renamed to Lz.Aws.Auth.CognitoPoolOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class CognitoPoolOutputs : Lz.Aws.Auth.CognitoPoolOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Auth.AwsCognitoOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerCognitoOutputs : Lz.Aws.Auth.AwsCognitoOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Data.AwsDynamoDbComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerDynamoDbComponent : Lz.Aws.Data.AwsDynamoDbComponent
{
}

[Obsolete("Renamed to Lz.Aws.Data.AwsDynamoDbOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerDatabaseOutputs : Lz.Aws.Data.AwsDynamoDbOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Data.AwsTenantDataComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerTenantDataComponent : Lz.Aws.Data.AwsTenantDataComponent
{
}

[Obsolete("Renamed to Lz.Aws.Data.AwsTenantDataOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerTenantDataOutputs : Lz.Aws.Data.AwsTenantDataOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Storage.AwsS3FileStorageComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerFileStorageComponent : Lz.Aws.Storage.AwsS3FileStorageComponent
{
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaNetworkComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerNetworkComponent : Lz.Aws.Compute.Lambda.AwsLambdaNetworkComponent
{
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaNetworkOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerNetworkOutputs : Lz.Aws.Compute.Lambda.AwsLambdaNetworkOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Compute.Lambda.AwsLambdaComputeOutputs (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerComputeOutputs : Lz.Aws.Compute.Lambda.AwsLambdaComputeOutputs
{
}

[Obsolete("Renamed to Lz.Aws.Compute.Fargate.AwsFargateServiceComponent (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerServiceComponent : Lz.Aws.Compute.Fargate.AwsFargateServiceComponent
{
    public AwsAppRunnerServiceComponent(SystemConfig config) : base(config) { }
}

[Obsolete("Renamed to Lz.Aws.Ops.AwsTransitionChecker (Lz 0.11.0). See Lz/Migrations/AxisRestructure.md; this shim will be removed in a future release.")]
public class AwsAppRunnerTransitionChecker : Lz.Aws.Ops.AwsTransitionChecker
{
    public AwsAppRunnerTransitionChecker(SystemConfig config) : base(config) { }
}
