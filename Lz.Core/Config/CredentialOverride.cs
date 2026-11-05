namespace Lz.Core.Config;

/// <summary>
/// <c>LZ_CREDENTIALS=ambient</c>: run on the ambient credential chain - a task role, an instance role, environment
/// variables - while the config still names a <c>Profile</c> (DecoupledCd.md P-10, settled 2026-09-14; P3 stage A).
///
/// <para><b>WHY AN OVERRIDE AND NOT A STAND-IN PROFILE.</b> The tooling image runs <c>lz</c> on a role inside the target
/// account, from the same root tree a workstation runs from, whose configs say <c>Profile: scu-dev</c>. A profile of that
/// name planted in the image would make the run look like a workstation's and hide which credentials acted. Instead the
/// loaders empty <c>Profile</c> - which every site already reads as "the ambient chain" (<c>AwsCredentialsFactory</c>,
/// the <c>aws</c> shell-outs' <c>CliProfileArg</c>, the four <c>AWS_PROFILE</c> sites) - and the CLI says at startup which
/// account it is acting in.</para>
///
/// <para><b>THE ACCOUNT IS CHECKED, because the profile no longer ties the run to one.</b> A named profile resolves to one
/// account; ambient credentials are whatever the process was given. So under the override a system config must declare
/// <c>AccountId</c>, and it must equal <see cref="AmbientAccountId"/>, which the CLI resolves with STS before any config is
/// loaded (the platform's <see cref="IConfigExtensions.ValidateLoaded"/> compares them).</para>
///
/// <para><b>ONE VALUE, AND ANYTHING ELSE IS REFUSED.</b> Unset or empty is the default (the configured profile).
/// <c>ambient</c> is the override. A misspelling is not read as "unset", because that would run on the workstation's
/// profile when the caller asked for the opposite.</para>
/// </summary>
public static class CredentialOverride
{
    public const string Variable = "LZ_CREDENTIALS";
    public const string AmbientValue = "ambient";

    /// <summary>Test seam: what the environment variable holds. Null means read the real environment.</summary>
    internal static Func<string?>? ReadForTests { get; set; }

    /// <summary>
    /// The account the ambient credentials belong to, resolved by the host (the CLI, with sts:GetCallerIdentity) before
    /// any config is loaded. Null until then; a config loaded under the override with this still null is refused.
    /// </summary>
    public static string? AmbientAccountId { get; set; }

    /// <summary>
    /// How the host finds <see cref="AmbientAccountId"/> when a config first needs it - the CLI registers
    /// sts:GetCallerIdentity on the ambient chain, so a command that loads no config (<c>--help</c>) makes no call.
    /// </summary>
    public static Func<string>? ResolveAmbientAccount { get; set; }

    /// <summary>The ambient account, resolved once through <see cref="ResolveAmbientAccount"/>; null if nothing can say.</summary>
    public static string? AmbientAccount()
        => AmbientAccountId ??= ResolveAmbientAccount?.Invoke();

    /// <summary>True when <c>LZ_CREDENTIALS=ambient</c>. Throws for any other non-empty value.</summary>
    public static bool Ambient
        => Parse(ReadForTests != null ? ReadForTests() : Environment.GetEnvironmentVariable(Variable));

    /// <summary>PURE: the variable's value read as the override (true), the default (false), or refused.</summary>
    public static bool Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (string.Equals(value.Trim(), AmbientValue, StringComparison.OrdinalIgnoreCase)) return true;
        throw new InvalidOperationException(
            $"{Variable}={value.Trim()} is not understood. Set {Variable}={AmbientValue} to run on the ambient credentials " +
            "(a task or instance role, or environment variables) in place of the configured Profile, or unset it.");
    }

    internal static void ResetForTests()
    {
        ReadForTests = null;
        AmbientAccountId = null;
        ResolveAmbientAccount = null;
    }
}
