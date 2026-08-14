namespace Lz.Aws.Config;

/// <summary>
/// OPT-IN private-subnet hardening for the EcsExpress topology family
/// (ecs-fargate-cognito-dynamodb). Phase 1 of Platform/FargateHardening.md.
///
/// <para>ABSENT = OFF, DELIBERATELY. When this block is omitted — or
/// <see cref="Enabled"/> is false — NOTHING is emitted and the Pulumi plan is
/// byte-for-byte identical to a pre-hardening deploy: Fargate tasks stay in
/// PUBLIC subnets with public IPs, the ALB stays internet-facing, and no NAT
/// gateway, private route table, S3/DynamoDB gateway endpoint, CloudFront VPC
/// origin, or ECS-Exec/ssmmessages wiring is created. The flag gates EXTRA
/// resources; it never alters the baseline — the same opt-in-null contract that
/// keeps non-opting sibling systems unchanged for
/// <see cref="Lz.Core.Config.HygieneConfig"/> /
/// <see cref="Lz.Core.Config.DurabilityConfig"/>. Lives on
/// <see cref="AwsSystemConfig"/> (not the platform-neutral base) because every
/// field it governs is AWS-only, exactly like the ECS/AppRunner/Fargate blocks.</para>
///
/// <para>Phase 1 scope: NAT-only egress + FREE S3/DynamoDB GATEWAY endpoints
/// (no interface endpoints); CloudFront VPC ORIGIN → INTERNAL ALB; SSM Session
/// Manager / ECS Exec as the ops break-glass. NO Tailscale — that is Phase 2.</para>
/// </summary>
public class PrivateNetworkConfig
{
    /// <summary>
    /// Master switch for the private-subnet topology. Default <c>false</c>.
    /// When false (or the whole block absent) the EcsExpress network/service/
    /// CloudFront components emit their current public-subnet plan unchanged.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Phase 2 — add a Tailscale subnet-router ASG as the ops/admin plane.
    /// Requires <see cref="Enabled"/>. Default <c>false</c>: private subnets +
    /// SSM break-glass, no Tailscale. When true, deploysystem provisions the
    /// router (ports the existing Lz Tailscale components), seeds the Tailscale
    /// API key into the <c>{SystemKey}/system</c> secret (via <c>--tailscale-key</c>
    /// or an interactive prompt), and mints/approves the tailnet route. See
    /// Platform/FargateHardening.md §5 and Platform/TailscaleMultiEnv.md.
    /// </summary>
    public bool Tailscale { get; set; } = false;

    /// <summary>
    /// Subdomain labels registered as Tailscale split-DNS entries on every
    /// <c>lz deploytenant</c> — each label <c>h</c> becomes an entry
    /// <c>h.{tenant RootDomain}</c> → the VPC DNS resolver (CIDR base + 2), so
    /// tailnet users resolve those names to VPC-internal targets (per-name
    /// private hosted zones → internal ALB) while every other name in the
    /// domain keeps public resolution. Requires <see cref="Enabled"/> +
    /// <see cref="Tailscale"/>. Default EMPTY = no tailnet DNS calls at all —
    /// systems without the opt-in are byte-identical (the same discipline as
    /// the rest of this block). Ported from the Monro/Ecs topology's
    /// UpdateTenantSplitDnsAsync, which hardcoded its two subdomains; here the
    /// list is config so the framework stays product-neutral. Entries are
    /// applied with the Tailscale API's PATCH (merge) semantics and accumulate
    /// across tenants; removing a label from this list does NOT delete the
    /// tailnet entry (prune manually in the admin console if ever needed).
    /// </summary>
    public List<string> SplitDnsHosts { get; set; } = new();
}
