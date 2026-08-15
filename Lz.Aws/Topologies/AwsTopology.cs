using Lz.Aws.Interfaces;
using Lz.Core.Config;
using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Config;
using Lz.Aws.Interfaces.Outputs;

namespace Lz.Aws.Topologies;

/// <summary>
/// Declarative descriptor for a named AWS topology. A topology is a fixed
/// bundle of correlated AWS component choices — compute primitive, database,
/// file storage, auth service, network shape, VPN presence, etc. — that
/// deploy as a coherent unit.
/// </summary>
/// <remarks>
/// <para>
/// Topologies are not composed from orthogonal axes; each descriptor pins
/// every axis, and callers select a topology by name (<c>SystemConfig.Topology</c>).
/// The axis properties on this class are for introspection — a validator or
/// the CLI can ask "does this topology have a private network?" without
/// pattern-matching the topology name.
/// </para>
/// <para>
/// Axis-independent behavior (factory construction, config validation) is
/// carried as delegates so the registry in <see cref="AwsTopologies"/> is
/// the single source of truth for what each topology is and how it builds.
/// </para>
/// </remarks>
public sealed class AwsTopology
{
    /// <summary>
    /// Machine-readable topology identifier. Matches <c>SystemConfig.Topology</c>
    /// in YAML. Case-insensitive for lookup.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>One-line human summary suitable for <c>lz status</c> output.</summary>
    public required string Summary { get; init; }

    /// <summary>Multi-line description of what the topology bundles and when to pick it.</summary>
    public string Description { get; init; } = "";

    // --- Axis flags ---

    public required AwsComputeKind Compute { get; init; }
    public required AwsDataKind Data { get; init; }
    public required AwsFileStorageKind FileStorage { get; init; }
    public required AwsAuthKind Auth { get; init; }

    /// <summary>True when the topology provisions a VPC with private subnets.</summary>
    public required bool HasPrivateNetwork { get; init; }

    /// <summary>True when the topology includes a Tailscale subnet-router ASG.</summary>
    public required bool UsesVpn { get; init; }

    /// <summary>
    /// True when the topology delegates auth to a centralized Keycloak in the
    /// shared-services account (requires <c>CentralAuthDomain</c> in systemconfig).
    /// </summary>
    public required bool UsesCentralAuth { get; init; }

    /// <summary>
    /// True when the topology deploys a Lambda for in-VPC gate checks (EFS/RDS
    /// readiness). Implies <see cref="HasPrivateNetwork"/>.
    /// </summary>
    public required bool UsesInVpcGateChecker { get; init; }

    /// <summary>
    /// True when the topology deploys an ECS task definition for cross-account
    /// seed-data export/import.
    /// </summary>
    public required bool UsesSeedTask { get; init; }

    // --- Behavior ---

    /// <summary>
    /// Constructs the platform factory for this topology. Invoked once per
    /// deployment after config has been loaded.
    /// </summary>
    public required Func<SystemConfig, IAwsPlatformFactory> CreateFactory { get; init; }

    /// <summary>
    /// Optional config-validation hook. Called by the CLI after loading
    /// systemconfig and before constructing the factory; errors accumulate
    /// and are reported together.
    /// </summary>
    public Action<SystemConfig, List<string>>? ValidateConfig { get; init; }
}
