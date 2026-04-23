using System;
using System.Collections.Generic;

namespace Lz.Gen
{
    /// <summary>
    /// Static registry of plugin-supplied directive and artifact types. Plugins
    /// populate this once at startup (typically from <see cref="ILzGenPlugin.RegisterGenExtensions"/>),
    /// and the YAML converters consult it in addition to the built-in types.
    /// </summary>
    /// <remarks>
    /// The registry is intentionally static — a lz process generates one
    /// solution per invocation and the plugin DLL is loaded once, so threading
    /// a registry instance through every converter adds complexity without
    /// benefit. Tests can call <see cref="Clear"/> between runs.
    /// </remarks>
    public static class GenExtensions
    {
        private static readonly Dictionary<string, Type> _directiveTypes =
            new Dictionary<string, Type>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Type> _artifactTypes =
            new Dictionary<string, Type>(StringComparer.Ordinal);

        /// <summary>
        /// Plugin-registered directive types, keyed by the value used in a
        /// <c>Type:</c> field in <c>LazyMagic.yaml</c>.
        /// </summary>
        public static IReadOnlyDictionary<string, Type> DirectiveTypes => _directiveTypes;

        /// <summary>
        /// Plugin-registered artifact types, keyed by the property name used
        /// inside a directive's <c>Artifacts:</c> block.
        /// </summary>
        public static IReadOnlyDictionary<string, Type> ArtifactTypes => _artifactTypes;

        /// <summary>
        /// Register a custom directive type. The <paramref name="name"/> is the
        /// value users will write in the <c>Type:</c> field.
        /// </summary>
        public static void RegisterDirective<T>(string name) where T : DirectiveBase
            => RegisterDirective(name, typeof(T));

        public static void RegisterDirective(string name, Type directiveType)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("name must not be null or empty", nameof(name));
            if (directiveType == null)
                throw new ArgumentNullException(nameof(directiveType));
            if (!typeof(DirectiveBase).IsAssignableFrom(directiveType))
                throw new ArgumentException(
                    $"{directiveType} must derive from {nameof(DirectiveBase)}",
                    nameof(directiveType));
            _directiveTypes[name] = directiveType;
        }

        /// <summary>
        /// Register a custom artifact type. The <paramref name="name"/> is the
        /// key users will write under a directive's <c>Artifacts:</c> block.
        /// </summary>
        public static void RegisterArtifact<T>(string name) where T : ArtifactBase
            => RegisterArtifact(name, typeof(T));

        public static void RegisterArtifact(string name, Type artifactType)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException("name must not be null or empty", nameof(name));
            if (artifactType == null)
                throw new ArgumentNullException(nameof(artifactType));
            if (!typeof(ArtifactBase).IsAssignableFrom(artifactType))
                throw new ArgumentException(
                    $"{artifactType} must derive from {nameof(ArtifactBase)}",
                    nameof(artifactType));
            _artifactTypes[name] = artifactType;
        }

        internal static Type ResolveDirectiveType(string name)
            => _directiveTypes.TryGetValue(name, out var t) ? t : null;

        internal static Type ResolveArtifactType(string name)
            => _artifactTypes.TryGetValue(name, out var t) ? t : null;

        /// <summary>Reset the registry. Intended for tests.</summary>
        public static void Clear()
        {
            _directiveTypes.Clear();
            _artifactTypes.Clear();
        }
    }
}
