using Lz.Core.Interfaces.Outputs;
using Pulumi;

namespace Lz.Aws.Compute.FargateAlb;

/// <summary>
/// AWS-specific service outputs for the FargateAlb (ex-Ecs, keycloak) lineage.
/// Moved out of the Keycloak component file in the 0.11.0 axis restructure —
/// it is the IServiceOutputs shape returned by both
/// <see cref="AwsFargateAlbServiceComponent"/> and
/// <see cref="Lz.Aws.Auth.AwsKeycloakServiceComponent"/>.
/// </summary>
public class AwsFargateAlbServiceOutputs : IServiceOutputs
{
    public required Output<string> ServiceId { get; init; }
    public required Output<string> Endpoint { get; init; }
}
