using System.IO.Compression;

namespace Lz.Core.PackageLane;

/// <summary>
/// Reads the packages a publish step is about to push. Thin on purpose - every decision lives in
/// <see cref="PublishGuards"/>, which takes facts rather than paths and is therefore testable without
/// a filesystem.
/// </summary>
public static class PublishCandidates
{
    /// <summary>
    /// Every <c>.nupkg</c> in a feed directory, with what its own nuspec says.
    ///
    /// <para><c>.snupkg</c> files are not read. A symbol package is emitted beside its package with
    /// the same id and version by construction, so checking it would restate the same facts - and the
    /// push of the package it belongs to is refused first anyway.</para>
    /// </summary>
    public static IReadOnlyList<PackageFacts> Scan(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"no such package directory: {directory}");

        var found = new List<PackageFacts>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.nupkg").OrderBy(f => f, StringComparer.Ordinal))
            found.Add(Read(file));
        return found;
    }

    /// <summary>The facts one <c>.nupkg</c> states about itself.</summary>
    public static PackageFacts Read(string nupkgPath)
    {
        using var zip = ZipFile.OpenRead(nupkgPath);
        // The nuspec sits at the archive root; entries deeper in the tree are content, and a
        // package can legitimately ship a .nuspec as content (Lz's own ProjectTemplates do).
        var entry = zip.Entries.FirstOrDefault(e =>
                        e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
                        !e.FullName.Contains('/'))
                    ?? throw new InvalidDataException($"{Path.GetFileName(nupkgPath)}: no nuspec at the archive root.");

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        return PublishGuards.ParseNuspec(reader.ReadToEnd(), Path.GetFileName(nupkgPath));
    }
}
