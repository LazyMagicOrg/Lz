using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;

namespace Lz.Aws;

/// <summary>
/// The one place that turns a configured <c>Profile</c> into AWS credentials.
///
/// <para>WHY IT EXISTS. <c>lz</c> has always run on a workstation with an SSO profile, so
/// <c>Profile</c> was required and 32 sites hand-rolled the same resolution. The decoupled-CD
/// deployer runs INSIDE the target account with a task role and no profile at all
/// (Docs/specs/DecoupledCd.md §8 item 2), so "no profile" has to become an ordinary, supported
/// state rather than a configuration error.</para>
///
/// <para>NULL MEANS "LET THE CLIENT USE ITS OWN DEFAULT CHAIN", and that is a deliberate design
/// choice rather than a missing feature. It would be tidier to return resolved default-chain
/// credentials so every call site could pass them unconditionally — but constructing a client with
/// EXPLICIT credentials resolves the chain EAGERLY, at construction, while
/// <c>new AmazonXClient(region)</c> leaves the client to resolve lazily. Returning null preserves
/// the lazy path exactly as it is today. (That eager/lazy difference is not theoretical: it is the
/// same mechanism that made ten of Scutara's Http-tier tests fail offline on 2026-09-11, where
/// building a controller's DI closure constructed a DynamoDB client and resolved credentials
/// before any test code ran.)</para>
///
/// <para>A CONFIGURED PROFILE BEHAVES EXACTLY AS BEFORE. Non-empty profile means
/// <see cref="CredentialProfileStoreChain"/>, the same call the 32 sites made, and an unresolvable
/// profile yields null exactly as <c>TryGetAWSCredentials</c> returning false did. Nothing about an
/// existing config changes — which is what lets this ship ungated to every sibling system
/// (DecoupledCd.md §8 classes it "backward-compatible by construction").</para>
///
/// <para>WHAT IT DOES NOT DO: decide what a site should do when resolution fails. The 32 sites did
/// five different things — throw, return null, no-op, fall through to the default chain, or carry
/// on with null credentials — and flattening those into one policy here would change behaviour
/// invisibly at sites nobody was looking at. The factory answers "what credentials", the site keeps
/// answering "and what if there are none".</para>
/// </summary>
public static class AwsCredentialsFactory
{
    /// <summary>
    /// Credentials for <paramref name="profile"/>, or <c>null</c> meaning "use the SDK default
    /// chain" — which a caller expresses by constructing its client without credentials.
    /// </summary>
    /// <param name="profile">
    /// The configured profile name. Null or empty is the AMBIENT case (a task role, an instance
    /// role, environment variables) and is not an error.
    /// </param>
    public static AWSCredentials? Resolve(string? profile)
    {
        if (string.IsNullOrEmpty(profile))
            return null;

        var chain = new CredentialProfileStoreChain();
        return chain.TryGetAWSCredentials(profile, out var credentials) ? credentials : null;
    }

    /// <summary>
    /// As <see cref="Resolve"/>, but for the sites that must FAIL rather than silently fall back
    /// when a profile was named and could not be resolved.
    ///
    /// <para>The distinction matters: an EMPTY profile returning null is correct and means ambient
    /// credentials, while a NAMED profile that does not resolve is a typo or an expired SSO login,
    /// and continuing on ambient credentials there could act against the wrong account. So this
    /// throws for the second case only.</para>
    /// </summary>
    public static AWSCredentials? ResolveOrThrow(string? profile)
    {
        if (string.IsNullOrEmpty(profile))
            return null;

        var chain = new CredentialProfileStoreChain();
        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException(
                $"AWS profile '{profile}' not found. It is named in this system's config, so lz will " +
                "not fall back to ambient credentials — that could act against a different account. " +
                "Check the profile name, or run `aws sso login --profile " + profile + "`. To use " +
                "ambient credentials deliberately (a task role inside the account, for instance), " +
                "leave Profile empty.");

        return credentials;
    }

    /// <summary>
    /// The <c>--profile</c> argument for an <c>aws</c> CLI shell-out — <c>--profile "x"</c>, or an
    /// EMPTY STRING when there is no profile, which lets the CLI use its own default chain.
    ///
    /// <para>Centralised because the failure it prevents is opaque: a command built by
    /// interpolating an empty profile becomes <c>aws ... --profile  --region us-west-2</c>, and the
    /// CLI reads the next token as the profile name. Quoted to match the form
    /// <c>WebappDeployer</c> already used, which is the site that had this right first.</para>
    /// </summary>
    public static string CliProfileArg(string? profile)
        => string.IsNullOrEmpty(profile) ? string.Empty : $"--profile \"{profile}\"";
}
