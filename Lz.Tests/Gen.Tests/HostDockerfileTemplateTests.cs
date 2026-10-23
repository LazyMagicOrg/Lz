namespace Lz.Tests.Gen.Tests;

// =====================================================================================================
//  The host template's Dockerfile is what `lz gen` writes over a container project's own, every run.
//
//  CopyProject copies the template folder with overwrite, so the template is the only place a host's Dockerfile can
//  be changed and stay changed. Until this template carried the package lane, a consumer that had added it by hand
//  (the PackageLane build arg, a restore config per lane, the credential as a BuildKit secret) lost all of it on
//  the next generation pass and had to restore the file from git. What these pin:
//
//  - The template refuses a build that names no lane, and restores through the config its lane names.
//  - Both lane configs ship beside it: a Dockerfile naming a config the generator does not write fails every
//    build of every host, at restore.
//  - Neither config is named NuGet.Config. The folder is on the host project's config-discovery chain, so one that
//    was would be read by every ordinary build of the project, and its <clear/> would drop the workspace's feeds.
// =====================================================================================================

public class HostDockerfileTemplateTests
{
    private static readonly string Template =
        Path.Combine(AppContext.BaseDirectory, "ProjectTemplates", "AspDotNetHost");

    private static string Dockerfile => File.ReadAllText(Path.Combine(Template, "Dockerfile"));

    [Fact]
    public void TheDockerfileRequiresALane_AndHasNoDefault()
    {
        Assert.Contains("ARG PackageLane=\"\"", Dockerfile);
        Assert.Contains("REFUSED: PackageLane is required and has no default.", Dockerfile);
    }

    [Fact]
    public void TheRestoreReadsTheConfigItsLaneNames()
        => Assert.Contains(
            "-p:RestoreConfigFile=/src/Containers/${ContainerName}/docker.${PackageLane}.NuGet.Config", Dockerfile);

    [Theory]
    [InlineData("local")]
    [InlineData("published")]
    public void EveryLaneTheDockerfileAccepts_HasItsConfigInTheTemplate(string lane)
    {
        Assert.Contains("published|local) echo", Dockerfile);   // the two lanes the case statement admits
        Assert.True(File.Exists(Path.Combine(Template, $"docker.{lane}.NuGet.Config")),
            $"the template accepts PackageLane={lane} and ships no docker.{lane}.NuGet.Config");
    }

    [Fact]
    public void NoConfigInTheTemplateIsOnTheDiscoveryChain()
        => Assert.DoesNotContain(
            Directory.GetFiles(Template).Select(Path.GetFileName),
            name => string.Equals(name, "NuGet.Config", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TheImageLabels_NameNoSystem()
    {
        // The template is every system's: a label in one system's namespace would be stamped on all their images.
        Assert.Contains("LABEL org.lazymagic.package-lane=\"${PackageLane}\"", Dockerfile);
        Assert.Contains("LABEL org.lazymagic.container-name=\"${ContainerName}\"", Dockerfile);
        Assert.DoesNotContain("LABEL org.scutara.", Dockerfile);
    }

    [Fact]
    public void TheLambdaAdapter_IsPinnedByDigest_AndABuildMayNameItsMirror_ButTheDefaultNamesNoSystem()
    {
        // A system whose builds cannot pull public ECR reliably names its own copy of the same image (Scutara, 2026-10-23:
        // anonymous pulls from GitHub's runners met "429 Data limit exceeded"). The digest keeps that copy the same bytes.
        Assert.Matches(@"(?m)^ARG LambdaAdapter=public\.ecr\.aws/awsguru/aws-lambda-adapter:[\d.]+@sha256:[0-9a-f]{64}\r?$", Dockerfile);
        Assert.Contains("FROM ${LambdaAdapter} AS lambda-adapter", Dockerfile);
        Assert.Contains("COPY --from=lambda-adapter /lambda-adapter /opt/extensions/lambda-adapter", Dockerfile);
        Assert.DoesNotContain("COPY --from=public.ecr.aws", Dockerfile);
        Assert.DoesNotContain("ghcr.io/", Dockerfile);
        // A global ARG is seen by a FROM only when it comes before the first FROM.
        Assert.True(Dockerfile.IndexOf("ARG LambdaAdapter=", StringComparison.Ordinal) < Dockerfile.IndexOf("FROM ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCredentialIsABuildSecret_NeverABuildArg()
    {
        Assert.Contains("--mount=type=secret,id=nuget_creds", Dockerfile);
        Assert.DoesNotContain("ARG NuGetPackageSourceCredentials", Dockerfile);
    }
}
