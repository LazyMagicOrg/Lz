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
}
