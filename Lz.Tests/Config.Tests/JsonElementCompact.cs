using System.Text.Json;

namespace Lz.Tests.Config.Tests;

internal static class JsonElementCompact
{
    /// <summary>The element as compact JSON - no whitespace - so a test can look for a fragment however the definition was indented.</summary>
    public static string Compact(this JsonElement e) => JsonSerializer.Serialize(e);
}
