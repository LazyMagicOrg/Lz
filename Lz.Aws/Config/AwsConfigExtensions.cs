using Lz.Core.Config;
using YamlDotNet.Serialization;

namespace Lz.Aws.Config;

/// <summary>
/// AWS-specific config-deserialization hook. Register via
/// <see cref="ConfigLoader.RegisterExtensions"/> at process startup
/// (done by Lz.Cli's <c>Program.Main</c>) so systemconfig/tenantconfig
/// YAML can materialise AWS-derived config types without Lz.Core
/// learning AWS vocabulary.
/// </summary>
/// <remarks>
/// Add more <c>WithTypeMapping&lt;TBase, TDerived&gt;()</c> calls here as
/// further AWS-derived config types are introduced under
/// <c>Lz.Aws/Config/</c>.
/// </remarks>
public sealed class AwsConfigExtensions : IConfigExtensions
{
    public string Platform => "aws";

    public void Configure(DeserializerBuilder builder)
    {
        builder.WithTypeMapping<AuthConfigEntry, AwsAuthConfigEntry>();
        builder.WithTypeMapping<SystemConfig, AwsSystemConfig>();
        builder.WithTypeMapping<TenantConfig, AwsTenantConfig>();
        builder.WithTypeMapping<SharedConfig, AwsSharedConfig>();
    }

    /// <summary>
    /// UNDER <c>LZ_CREDENTIALS=ambient</c>, THE CONFIG SAYS WHICH ACCOUNT IT IS FOR, and the credentials must be in it
    /// (DecoupledCd.md P-10). A named profile tied the run to one account; ambient credentials are whatever the process was
    /// given, so without this a tooling task started in the wrong account would deploy that account's stacks from this
    /// config. The same comparison <c>bootstrapwebsiteci</c> has made since 2026-09-03, made for every command.
    /// </summary>
    public void ValidateLoaded(SystemConfig config, string sourceFile)
    {
        if (!CredentialOverride.Ambient) return;

        var file = Path.GetFileName(sourceFile);
        var declared = (config as AwsSystemConfig)?.AccountId?.Trim();
        if (string.IsNullOrEmpty(declared))
            throw new InvalidOperationException(
                $"{file} declares no AccountId, and {CredentialOverride.Variable}={CredentialOverride.AmbientValue} is set: " +
                "with no profile tying the run to an account, the config has to say which account it is for. Add " +
                "`AccountId: \"<12 digits>\"`.");

        var ambient = CredentialOverride.AmbientAccount();
        if (string.IsNullOrEmpty(ambient))
            throw new InvalidOperationException(
                $"{CredentialOverride.Variable}={CredentialOverride.AmbientValue} is set, but the account of the ambient " +
                $"credentials was not resolved before {file} was loaded, so it cannot be checked against AccountId {declared}.");

        if (!string.Equals(declared, ambient, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{file} is for account {declared}, but the ambient credentials ({CredentialOverride.Variable}=" +
                $"{CredentialOverride.AmbientValue}) are in account {ambient}. Nothing was done.");
    }
}
