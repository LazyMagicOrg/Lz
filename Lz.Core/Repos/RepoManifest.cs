using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Lz.Core.Repos;

/// <summary>
/// The environments a manifest declares a branch for. These are lz's three, matching the
/// <c>_Dev</c>/<c>_Test</c>/<c>_Prod</c> folder convention that
/// <c>ConfigLoader.ResolveEnvironment</c> reads — so a manifest cannot describe an environment
/// lz has no notion of.
/// </summary>
public enum RepoEnvironment
{
    Dev,
    Test,
    Prod,
}

/// <summary>One repository's branch per environment. All three are required — see the remarks.</summary>
/// <remarks>
/// <para><b>NO DEFAULTING, DELIBERATELY.</b> An omitted environment could plausibly mean "same as
/// Dev", and that is exactly the guess that goes wrong quietly: a repo silently cloned from the
/// wrong branch produces a build or deploy failure several steps later, with nothing pointing back
/// here. A missing key is an error the manifest author can fix in one line.</para>
/// </remarks>
public sealed class RepoBranches
{
    public string? Dev { get; set; }
    public string? Test { get; set; }
    public string? Prod { get; set; }

    /// <summary>The branch for one environment, or null when the manifest did not declare it.</summary>
    public string? For(RepoEnvironment env) => env switch
    {
        RepoEnvironment.Dev => Blank(Dev),
        RepoEnvironment.Test => Blank(Test),
        RepoEnvironment.Prod => Blank(Prod),
        _ => null,
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>One repository in the manifest.</summary>
public sealed class RepoEntry
{
    /// <summary>Display name. Used in output and to address a single repo with <c>--repo</c>.</summary>
    public string? Name { get; set; }

    /// <summary>Target folder, RELATIVE to the workspace root. <c>.</c> means the root repo itself.</summary>
    public string? Path { get; set; }

    /// <summary>Clone URL exactly as git should receive it — SSH and HTTPS are not interchangeable.</summary>
    public string? Url { get; set; }

    public RepoBranches? Branches { get; set; }
}

/// <summary>
/// <c>repos.yaml</c> — every repository that makes up a system, and the branch each environment
/// uses. Read by <c>lz clonerepos</c>.
///
/// <para>Parsed with its OWN deserializer rather than through <c>ConfigLoader</c>: that loader
/// carries platform extensions and an active-platform notion for systemconfig, none of which a flat
/// repo list needs. The naming convention is kept identical (PascalCase) so the file reads like
/// every other lz config.</para>
/// </summary>
public sealed class RepoManifest
{
    /// <summary>The conventional filename, at the workspace root.</summary>
    public const string FileName = "repos.yaml";

    public List<RepoEntry> Repos { get; set; } = new();

    private static IDeserializer Deserializer { get; } = new DeserializerBuilder()
        .WithNamingConvention(PascalCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>The manifest's full path under <paramref name="workspaceRoot"/>, or null if absent.</summary>
    public static string? Locate(string workspaceRoot)
    {
        var path = System.IO.Path.Combine(workspaceRoot, FileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Parse manifest YAML. Throws <see cref="InvalidOperationException"/> with a message naming the
    /// file and the YAML position — a raw <see cref="YamlException"/> reads like a crash to an
    /// operator who mistyped an indent.
    /// </summary>
    public static RepoManifest Parse(string yaml, string? sourcePath = null)
    {
        var where = sourcePath is null ? FileName : sourcePath;
        try
        {
            var manifest = Deserializer.Deserialize<RepoManifest>(yaml);
            if (manifest is null)
                throw new InvalidOperationException($"{where} is empty.");
            manifest.Repos ??= new List<RepoEntry>();
            return manifest;
        }
        catch (YamlException ex)
        {
            throw new InvalidOperationException(
                $"{where} could not be parsed (line {ex.Start.Line}, column {ex.Start.Column}): {ex.Message}", ex);
        }
    }

    /// <summary>Read and parse the manifest at <paramref name="path"/>.</summary>
    public static RepoManifest Load(string path) => Parse(File.ReadAllText(path), path);
}
