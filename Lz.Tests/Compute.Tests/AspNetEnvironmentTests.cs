using Lz.Aws.Shared;
using Lz.Tests.Build.Tests;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// Pins the ASP.NET environment a deployed service runs under (DecoupledCd V-21): Development in dev and Production
/// everywhere else - and that every component defining a container or function takes it from
/// <see cref="AspNetEnvironment"/>, so no topology can drift back to "Development unless prod".
/// </summary>
public class AspNetEnvironmentTests
{
    [Theory]
    [InlineData("dev", "Development")]
    [InlineData("DEV", "Development")]
    [InlineData("test", "Production")]
    [InlineData("prod", "Production")]
    [InlineData("staging", "Production")]
    [InlineData("", "Production")]
    [InlineData(null, "Production")]
    public void OnlyDevRunsAsDevelopment(string? environment, string expected)
        => Assert.Equal(expected, AspNetEnvironment.For(environment));

    [Fact]
    public void TheVariableIsAspNetCoresOwn()
        => Assert.Equal("ASPNETCORE_ENVIRONMENT", AspNetEnvironment.Variable);

    private static string LzAws() => Path.Combine(PackageHandlingScratchBuild.FindLzRepoRoot(), "Lz.Aws");

    private static IEnumerable<string> Sources()
        => Directory.EnumerateFiles(LzAws(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void NoComponentNamesTheVariableItself()
    {
        var offenders = Sources()
            .Where(f => Path.GetFileName(f) != "AspNetEnvironment.cs")
            .Where(f => File.ReadAllText(f).Contains("\"ASPNETCORE_ENVIRONMENT\"", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(LzAws(), f))
            .ToList();
        Assert.True(offenders.Count == 0,
            $"set ASPNETCORE_ENVIRONMENT through AspNetEnvironment, not by name: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("Compute/Fargate/AwsFargateTenantServiceComponent.cs")]
    [InlineData("Compute/Lambda/AwsLambdaTenantServiceComponent.cs")]
    [InlineData("Compute/FargateAlb/AwsFargateAlbServiceComponent.cs")]
    [InlineData("Compute/FargateAlb/AwsFargateAlbTenantServiceComponent.cs")]
    public void EveryServiceComponentTakesTheRule(string relative)
    {
        var path = Path.Combine(LzAws(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"{relative} has moved; this test names it");
        var src = File.ReadAllText(path);
        Assert.Contains("AspNetEnvironment.Variable", src);
        Assert.Contains("AspNetEnvironment.For(", src);
    }
}
