using Lz.Core.Config;
using Pulumi;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Cross-stack accessor for the Backend-For-Frontend (BFF) confidential-client
/// outputs that the FOUNDATION stack exports (see
/// <see cref="Lz.Aws.Orchestration"/> / <c>SystemDeployment.DeployFoundation</c>).
///
/// <para>
/// The Cognito pools — and therefore the BFF confidential clients and their
/// secrets — are system-scoped and created in the foundation stack
/// (<c>{sk}-{env}</c>). The tenant stack (<c>{sk}-{tk}-{env}</c>) needs the
/// client id + secret to (a) seed the per-tenant Secrets Manager secret and
/// (b) inject the <c>LZ_BFF_*</c> container env vars. Both stacks live in the
/// same Pulumi project (<c>lz-{sk}</c>) and backend, so a
/// <see cref="StackReference"/> is the idiomatic bridge.
/// </para>
///
/// <para>
/// This type is constructed ONLY on the BFF-enabled path. When BFF is off (the
/// default), no <see cref="StackReference"/> is created and nothing here runs,
/// so the tenant plan is unchanged. The foundation exports keys
/// <c>auth_{pool}_bffClientId</c> / <c>auth_{pool}_bffClientSecret</c> only for
/// pools that set <c>ProvisionBffClient</c>; absent keys resolve to empty.
/// </para>
/// </summary>
internal sealed class BffStackOutputs
{
    private readonly StackReference _foundation;

    public BffStackOutputs(TenantConfig tenantConfig, ComponentResource parent)
    {
        var sk = tenantConfig.SystemKey;
        var env = tenantConfig.Environment;
        // Same project + backend; foundation stack is {sk}-{env}.
        var projectName = $"lz-{sk}";
        var foundationStack = $"{sk}-{env}";

        // Pulumi self-managed (DIY/S3) backends require the fully-qualified
        // "organization/{project}/{stack}" form, and the org segment MUST be the
        // literal "organization" (Pulumi convention for non-cloud backends).
        // A 2-segment "{project}/{stack}" name is parsed as "{org}/{stack}" and
        // fails with "organization name must be 'organization'".
        _foundation = new StackReference(
            $"{sk}-{tenantConfig.TenantKey}-bff-foundation-ref",
            new StackReferenceArgs { Name = $"organization/{projectName}/{foundationStack}" },
            new CustomResourceOptions { Parent = parent });
    }

    /// <summary>BFF confidential client id for <paramref name="pool"/>, or "" if absent.</summary>
    public Output<string> ClientId(string pool) =>
        _foundation.GetOutput($"auth_{pool}_bffClientId").Apply(v => v as string ?? string.Empty);

    /// <summary>BFF confidential client secret for <paramref name="pool"/>, or "" if absent.</summary>
    public Output<string> ClientSecret(string pool) =>
        _foundation.GetOutput($"auth_{pool}_bffClientSecret").Apply(v => v as string ?? string.Empty);

    /// <summary>BFF authority (OIDC issuer) for <paramref name="pool"/>, or "" if absent.</summary>
    public Output<string> Authority(string pool) =>
        _foundation.GetOutput($"auth_{pool}_authority").Apply(v => v as string ?? string.Empty);

    /// <summary>BFF OpenID metadata URL for <paramref name="pool"/>, or "" if absent.</summary>
    public Output<string> MetadataUrl(string pool) =>
        _foundation.GetOutput($"auth_{pool}_metadataUrl").Apply(v => v as string ?? string.Empty);
}
