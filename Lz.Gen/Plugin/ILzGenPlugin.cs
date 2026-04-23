namespace Lz.Gen
{
    /// <summary>
    /// Optional contract a plugin can implement alongside
    /// <c>Lz.Core.Plugin.ILzPlugin</c> to extend the code generator
    /// (<c>lz gen</c>) with custom directive and artifact types.
    /// </summary>
    /// <remarks>
    /// The plugin is detected at runtime via <c>is ILzGenPlugin</c> — a
    /// deployment-only plugin can omit this interface entirely and still work.
    /// Implementations typically call <see cref="GenExtensions.RegisterDirective{T}"/>
    /// / <see cref="GenExtensions.RegisterArtifact{T}"/> to wire up their types.
    /// </remarks>
    public interface ILzGenPlugin
    {
        /// <summary>
        /// Called before <c>lz gen</c> parses <c>LazyMagic.yaml</c>. Register
        /// custom directive and artifact types on <see cref="GenExtensions"/>.
        /// </summary>
        void RegisterGenExtensions();
    }
}
