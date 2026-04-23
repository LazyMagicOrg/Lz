using YamlDotNet.Serialization;

namespace Lz.Core.Config;

/// <summary>
/// Platform-specific hook for extending config deserialization. A platform
/// library (e.g. Lz.Aws, Lz.Azure) implements this to contribute YAML type
/// mappings so its derived config types — e.g. an Aws-specific subclass of
/// <see cref="AuthConfigEntry"/> — are materialised when
/// <see cref="ConfigLoader"/> parses systemconfig/tenantconfig YAML.
/// </summary>
/// <remarks>
/// This keeps platform-specific vocabulary (e.g. Cognito groups, App Runner
/// advanced security mode) out of <c>Lz.Core</c>. The base config types stay
/// platform-agnostic; platform libraries derive from them and register a
/// <see cref="YamlDotNet.Serialization.BuilderSkeleton{TBuilder}.WithTypeMapping"/>
/// so deserialisation yields the derived instance that downstream AWS/Azure
/// components cast to when they need the extra fields.
/// </remarks>
public interface IConfigExtensions
{
    /// <summary>
    /// Invoked by <see cref="ConfigLoader"/> while building the PascalCase
    /// deserializer used for systemconfig/tenantconfig YAML. Implementations
    /// typically call <c>builder.WithTypeMapping&lt;TBase, TDerived&gt;()</c>
    /// for each derived config type they introduce.
    /// </summary>
    void Configure(DeserializerBuilder builder);
}
