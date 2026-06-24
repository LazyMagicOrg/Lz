using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

/// <summary>
/// Per-pool auth details: pool ID, client ID, OIDC metadata URL, and
/// optional authority URL. Implemented by auth components that create
/// per-pool resources so the orchestrator can export them as stack outputs
/// for downstream use.
/// <para>
/// <see cref="ClientId"/> is still a deployment output — downstream tooling
/// like <c>lz deploywebapp</c> injects it into the webapp bundle at build
/// time. It is not, however, written to the <c>/config</c> runtime JSON.
/// </para>
/// </summary>
public class AuthPoolDetail
{
    public required Output<string> UserPoolId { get; init; }
    public required Output<string> ClientId { get; init; }
    public required Output<string> MetadataUrl { get; init; }
    public Output<string>? Authority { get; init; }

    /// <summary>
    /// Confidential BFF app-client id, when the pool provisioned one
    /// (<c>AwsAuthConfigEntry.ProvisionBffClient</c>). <c>null</c> for pools
    /// without a BFF client — the default. Exported as a foundation stack
    /// output for downstream BFF wiring.
    /// </summary>
    public Output<string>? BffClientId { get; init; }

    /// <summary>
    /// Confidential BFF app-client secret, when the pool provisioned one.
    /// <c>null</c> otherwise. The authoritative runtime copy is written into
    /// the per-tenant Secrets Manager secret; this output exists for
    /// completeness/auditing of the foundation stack.
    /// </summary>
    public Output<string>? BffClientSecret { get; init; }
}

/// <summary>
/// Optional interface for auth service outputs that provide per-pool details.
/// The orchestrator checks for this interface to export pool IDs as stack
/// outputs, enabling downstream components to auto-wire auth config.
/// </summary>
public interface IAuthPoolOutputs : IServiceOutputs
{
    /// <summary>
    /// Per-pool outputs keyed by auth type name (e.g., "tenantauth", "plannerauth").
    /// </summary>
    Dictionary<string, AuthPoolDetail> Pools { get; }
}
