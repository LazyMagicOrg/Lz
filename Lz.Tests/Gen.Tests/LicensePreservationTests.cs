using System;
using System.Collections.Generic;
using System.IO;
using Lz.Gen;
using Xunit;

namespace Lz.Tests.Gen.Tests;

// =====================================================================================================
//  CopyProject must never replace a consuming project's LICENSE with the template's.
//
//  THE BUG THIS PINS. Project templates ship a placeholder LICENSE.txt so a NEW project starts with
//  something. CopyProject copied every non-excluded template file with overwrite: true, and LICENSE was
//  not in any caller's exclude list — so every `lz gen` silently replaced a consuming project's real
//  licence with the template's, changing both the licence terms and the copyright holder. Observed in
//  the wild: 15 proprietary licences reverted to the template's MIT in a single generation pass, buried
//  in a diff otherwise full of expected generated output.
//
//  GenerateLicenseFile already had an `if (File.Exists) return;` guard, which is why this looked
//  impossible on a reading of the code. That guard runs AFTER the copy, by which time the file exists
//  with the template's content — so it never fired. The guard was correct and unreachable.
// =====================================================================================================

public class LicensePreservationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lz-license-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _dest;

    public LicensePreservationTests()
    {
        _source = Path.Combine(_root, "template");
        _dest = Path.Combine(_root, "project");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_dest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private static readonly List<string> NoExclusions = new();

    [Fact]
    public void AnExistingLicenseIsNotReplaced()
    {
        File.WriteAllText(Path.Combine(_source, "LICENSE.txt"), "MIT License\nCopyright (c) 2024 Template Author");
        File.WriteAllText(Path.Combine(_dest, "LICENSE.txt"), "PROPRIETARY\nCopyright (c) 2026 Real Owner");

        DotNetUtils.CopyProject(_source, _dest, NoExclusions);

        Assert.Contains("PROPRIETARY", File.ReadAllText(Path.Combine(_dest, "LICENSE.txt")), StringComparison.Ordinal);
    }

    /// <summary>
    /// The case that makes a File.Exists check on the template's own name insufficient: on Windows these
    /// are one file, on Linux two. A directory scan gives the same answer on both.
    /// </summary>
    [Fact]
    public void AnExistingLicenseIsNotReplaced_EvenWhenTheExtensionCaseDiffers()
    {
        File.WriteAllText(Path.Combine(_source, "LICENSE.TXT"), "MIT License\nCopyright (c) 2024 Template Author");
        File.WriteAllText(Path.Combine(_dest, "LICENSE.txt"), "PROPRIETARY\nCopyright (c) 2026 Real Owner");

        DotNetUtils.CopyProject(_source, _dest, NoExclusions);

        var licences = Directory.GetFiles(_dest, "LICENSE.*");
        Assert.All(licences, f => Assert.DoesNotContain("MIT License", File.ReadAllText(f), StringComparison.Ordinal));
    }

    /// <summary>A NEW project must still get the template's licence — the fix must not break bootstrap.</summary>
    [Fact]
    public void ANewProjectStillReceivesTheTemplateLicense()
    {
        File.WriteAllText(Path.Combine(_source, "LICENSE.txt"), "MIT License\nCopyright (c) 2024 Template Author");

        DotNetUtils.CopyProject(_source, _dest, NoExclusions);

        Assert.True(File.Exists(Path.Combine(_dest, "LICENSE.txt")));
        Assert.Contains("MIT License", File.ReadAllText(Path.Combine(_dest, "LICENSE.txt")), StringComparison.Ordinal);
    }

    /// <summary>Everything that is not a licence still copies over — the fix is narrow by design.</summary>
    [Fact]
    public void NonLicenseFilesAreStillOverwritten()
    {
        File.WriteAllText(Path.Combine(_source, "Template.cs"), "// from template");
        File.WriteAllText(Path.Combine(_dest, "Template.cs"), "// stale");

        DotNetUtils.CopyProject(_source, _dest, NoExclusions);

        Assert.Equal("// from template", File.ReadAllText(Path.Combine(_dest, "Template.cs")));
    }

    [Theory]
    [InlineData("LICENSE", true)]
    [InlineData("LICENSE.txt", true)]
    [InlineData("LICENSE.TXT", true)]
    [InlineData("license.md", true)]
    [InlineData("LICENCE.txt", true)]
    [InlineData("COPYING", true)]
    [InlineData("Licensing.cs", false)]
    [InlineData("LicenseManager.cs", false)]
    [InlineData("Program.cs", false)]
    [InlineData("", false)]
    public void IsLicenseFileName_RecognisesTheConventionalNamesOnly(string fileName, bool expected)
        => Assert.Equal(expected, DotNetUtils.IsLicenseFileName(fileName));

    [Fact]
    public void DirectoryHasLicense_FalseWithoutOne_TrueWithOne()
    {
        Assert.False(DotNetUtils.DirectoryHasLicense(_dest));
        File.WriteAllText(Path.Combine(_dest, "LICENSE.md"), "x");
        Assert.True(DotNetUtils.DirectoryHasLicense(_dest));
    }

    [Fact]
    public void DirectoryHasLicense_FalseForAMissingDirectory()
        => Assert.False(DotNetUtils.DirectoryHasLicense(Path.Combine(_root, "does-not-exist")));
}
