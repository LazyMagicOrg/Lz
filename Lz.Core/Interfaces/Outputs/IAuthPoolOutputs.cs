using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

/// <summary>
/// Per-pool auth details (e.g., Cognito user pool ID, client ID, metadata URL).
/// Implemented by auth components that create per-pool resources
/// so the orchestrator can export them as stack outputs for downstream use.
/// </summary>
public class AuthPoolDetail
{
    public required Output<string> UserPoolId { get; init; }
    public required Output<string> ClientId { get; init; }
    public required Output<string> MetadataUrl { get; init; }
    public Output<string>? HostedUIDomain { get; init; }
}

/// <summary>
/// Optional interface for auth service outputs that provide per-pool details.
/// The orchestrator checks for this interface to export pool IDs as stack outputs,
/// enabling downstream components (e.g., CloudFront KVS) to auto-wire auth config.
/// </summary>
public interface IAuthPoolOutputs : IServiceOutputs
{
    /// <summary>
    /// Per-pool outputs keyed by auth type name (e.g., "tenantauth", "plannerauth").
    /// </summary>
    Dictionary<string, AuthPoolDetail> Pools { get; }
}
