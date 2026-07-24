using Lz.Core.Config;
using Pulumi;

namespace Lz.Aws.Ecs;

/// <summary>
/// Single source of truth for the Smartstore ⇄ Cognito runtime wiring — the
/// <c>SMARTSTORE_COGNITO_*</c> env set the storefront container's OpenID Connect
/// handler (the <c>Smartstore.Cognito.Auth</c> module) reads. Mirrors the BFF
/// wiring pattern (<see cref="Lz.Aws.AppRunner.BffWiring"/>): everything here is
/// gated by <see cref="IsEnabled"/>, so a tenant that opts out (the default)
/// adds NOTHING — no env vars, no stack reference — and its task definition is
/// byte-for-byte identical to a pre-Smartstore-Cognito deploy.
///
/// <para>
/// The confidential Smartstore app client lives on a system-scoped Cognito pool
/// (created in the foundation stack when the pool sets
/// <c>ProvisionSmartstoreClient</c>). The tenant stack reads the client id +
/// secret, the pool issuer, and the Hosted-UI domain across a
/// <see cref="StackReference"/> and injects them as the four env vars the module
/// consumes (env wins over DB settings, so no admin-UI step is needed).
/// </para>
/// </summary>
public static class SmartstoreCognitoWiring
{
    /// <summary>Smartstore-Cognito env injection active for this tenant?</summary>
    public static bool IsEnabled(TenantConfig tenantConfig) => tenantConfig.SmartstoreCognitoEnabled == true;

    /// <summary>The Cognito pool the confidential Smartstore client lives on (default consumerauth).</summary>
    public static string ResolvePool(TenantConfig tenantConfig) =>
        !string.IsNullOrWhiteSpace(tenantConfig.SmartstoreCognitoPool)
            ? tenantConfig.SmartstoreCognitoPool!
            : "consumerauth";

    /// <summary>
    /// Build the <c>SMARTSTORE_COGNITO_*</c> env-var pairs for a tenant's
    /// storefront container. Call ONLY when <see cref="IsEnabled"/> is true.
    /// Returns a list of <c>(name, Output&lt;string&gt;)</c>; callers resolve the
    /// outputs at apply time and serialise them into the container definition.
    /// </summary>
    public static List<(string Name, Output<string> Value)> BuildEnv(
        TenantConfig tenantConfig, ComponentResource parent)
    {
        var pool = ResolvePool(tenantConfig);
        var foundation = new SmartstoreCognitoStackOutputs(tenantConfig, parent);

        // The module wants the OIDC ISSUER as Authority
        // (https://cognito-idp.{region}.amazonaws.com/{poolId}) so standard
        // discovery resolves the Hosted-UI endpoints. Derive it from the
        // foundation metadataUrl (issuer = discovery URL minus the suffix) rather
        // than the auth_{pool}_authority output, which is the Hosted-UI DOMAIN
        // (used below for logout), NOT the issuer.
        const string DiscoverySuffix = "/.well-known/openid-configuration";
        var issuerAuthority = foundation.MetadataUrl(pool).Apply(url =>
            url.EndsWith(DiscoverySuffix, StringComparison.OrdinalIgnoreCase)
                ? url[..^DiscoverySuffix.Length]
                : url);

        return new List<(string, Output<string>)>
        {
            ("SMARTSTORE_COGNITO_AUTHORITY", issuerAuthority),
            ("SMARTSTORE_COGNITO_CLIENTID", foundation.ClientId(pool)),
            ("SMARTSTORE_COGNITO_CLIENTSECRET", foundation.ClientSecret(pool)),
            // The Hosted-UI domain URL (https://auth-{pool}.{systemDomain}); the
            // module needs it for RP-initiated sign-out ({domain}/logout), since
            // Cognito exposes no standard end_session_endpoint.
            ("SMARTSTORE_COGNITO_HOSTEDUIDOMAIN", foundation.HostedUiDomain(pool)),
        };
    }
}

/// <summary>
/// Cross-stack accessor for the confidential Smartstore-client outputs the
/// FOUNDATION stack exports (<c>auth_{pool}_smartstoreClientId</c> /
/// <c>_smartstoreClientSecret</c>) plus the pool's OIDC metadata URL and
/// Hosted-UI domain. Constructed ONLY on the enabled path; when Smartstore-Cognito
/// is off no <see cref="StackReference"/> is created. Absent keys resolve to "".
/// </summary>
internal sealed class SmartstoreCognitoStackOutputs
{
    private readonly StackReference _foundation;

    public SmartstoreCognitoStackOutputs(TenantConfig tenantConfig, ComponentResource parent)
    {
        var sk = tenantConfig.SystemKey;
        var env = tenantConfig.Environment;
        // Same project + backend; foundation stack is {sk}-{env}. Pulumi
        // self-managed backends require the "organization/{project}/{stack}" form.
        _foundation = new StackReference(
            $"{sk}-{tenantConfig.TenantKey}-smartstore-foundation-ref",
            new StackReferenceArgs { Name = $"organization/lz-{sk}/{sk}-{env}" },
            new CustomResourceOptions { Parent = parent });
    }

    /// <summary>Confidential Smartstore client id for <paramref name="pool"/>, or "" if absent.</summary>
    public Output<string> ClientId(string pool) =>
        _foundation.GetOutput($"auth_{pool}_smartstoreClientId").Apply(v => v as string ?? string.Empty);

    /// <summary>Confidential Smartstore client secret for <paramref name="pool"/>, or "" if absent.</summary>
    public Output<string> ClientSecret(string pool) =>
        _foundation.GetOutput($"auth_{pool}_smartstoreClientSecret").Apply(v => v as string ?? string.Empty);

    /// <summary>OpenID metadata (discovery) URL for <paramref name="pool"/>, or "" if absent.</summary>
    public Output<string> MetadataUrl(string pool) =>
        _foundation.GetOutput($"auth_{pool}_metadataUrl").Apply(v => v as string ?? string.Empty);

    /// <summary>Hosted-UI domain URL for <paramref name="pool"/> (auth_{pool}_authority), or "" if absent.</summary>
    public Output<string> HostedUiDomain(string pool) =>
        _foundation.GetOutput($"auth_{pool}_authority").Apply(v => v as string ?? string.Empty);
}
