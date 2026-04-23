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
/// This class is intentionally empty today — the plumbing lands first.
/// When AWS-derived config types are introduced (for example an
/// <c>AwsAuthConfigEntry : AuthConfigEntry</c> that carries Cognito
/// MFA/password policy / groups per
/// <c>Platform/CognitoHardeningPlan.md</c>), add the mapping here:
/// <code>
/// builder.WithTypeMapping&lt;AuthConfigEntry, AwsAuthConfigEntry&gt;();
/// </code>
/// </remarks>
public sealed class AwsConfigExtensions : IConfigExtensions
{
    public void Configure(DeserializerBuilder builder)
    {
        // No AWS-derived config types yet. Add WithTypeMapping<...> calls
        // here as AWS extensions are added under Lz.Aws/Config/.
    }
}
