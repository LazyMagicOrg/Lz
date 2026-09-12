using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Xml.Linq;
using Lz.Aws.Pipeline;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// The Lambda packages Lz.Aws builds (DecoupledCd.md P2 stage C3), opened and inspected as built.
///
/// <para>A function whose handler string names a class that is not in its zip, or whose zip is missing
/// an assembly, is created successfully by <c>lz bootstrapdeployer</c> and fails on its first
/// invocation — inside a deploy. These tests move both failures to the build. The first one here was
/// not hypothetical: the first build of the package shipped four files and no AWS SDK, because the
/// SDK leaves PrivateAssets="all" packages out of publish.</para>
/// </summary>
public class DeployerPackageTests
{
    private static string LambdaDir =>
        Path.Combine(Path.GetDirectoryName(typeof(DeployerPlanner).Assembly.Location)!, "Lambda");

    private static readonly string[] RequiredFiles =
    {
        "Lz.Aws.Deployer.dll", "Lz.Aws.Deployer.deps.json", "Lz.Aws.Deployer.runtimeconfig.json",
        "Amazon.Lambda.Core.dll", "AWSSDK.Core.dll", "AWSSDK.S3.dll", "AWSSDK.ECR.dll", "AWSSDK.ECS.dll",
    };

    [Theory]
    [InlineData(DeployerPackages.Deployer)]
    [InlineData(DeployerPackages.SignatureHook)]
    public void ThePackage_CarriesTheHandlerAssemblyAndEveryDependency(string package)
    {
        using var zip = ZipFile.OpenRead(Path.Combine(LambdaDir, DeployerPackages.ZipFor(package)));
        var names = zip.Entries.Select(e => e.FullName.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);

        foreach (var file in RequiredFiles)
            Assert.True(names.Contains(file), $"{package}.zip has no {file}");

        // None of the CLI's weight: the functions compile linked files precisely so they do not ship
        // Pulumi and thirty SDKs.
        Assert.DoesNotContain(names, n => n.StartsWith("Pulumi", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Equals("Lz.Aws.dll", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheDeployerPackage_DoesNotCarryTheVerifier()
    {
        using var zip = ZipFile.OpenRead(Path.Combine(LambdaDir, DeployerPackages.ZipFor(DeployerPackages.Deployer)));

        Assert.DoesNotContain(zip.Entries, e => e.FullName.Replace('\\', '/').StartsWith(NotationLayout.PackageFolder + "/"));
    }

    [Fact]
    public void EveryHandlerThePlanNames_ResolvesInTheBuiltPackage()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lz-deployer-package-" + Guid.NewGuid().ToString("N"));
        ZipFile.ExtractToDirectory(Path.Combine(LambdaDir, DeployerPackages.ZipFor(DeployerPackages.Deployer)), dir);

        // Loaded in isolation, resolving dependencies ONLY from the package — the way the Lambda runtime
        // sees it, not the way this test process (which has the AWS SDK already) would.
        var context = new AssemblyLoadContext("deployer-package", isCollectible: true);
        context.Resolving += (ctx, name) =>
        {
            var path = Path.Combine(dir, name.Name + ".dll");
            return File.Exists(path) ? ctx.LoadFromAssemblyPath(path) : null;
        };

        try
        {
            var assembly = context.LoadFromAssemblyPath(Path.Combine(dir, "Lz.Aws.Deployer.dll"));

            foreach (var handler in DeployerHandlers.All)
            {
                var parts = handler.Split("::");
                Assert.Equal(3, parts.Length);
                Assert.Equal(assembly.GetName().Name, parts[0]);

                var type = assembly.GetType(parts[1], throwOnError: false);
                Assert.True(type != null, $"{handler}: no type {parts[1]} in the package");
                Assert.NotNull(type!.GetConstructor(Type.EmptyTypes));

                var method = type.GetMethod(parts[2], BindingFlags.Public | BindingFlags.Instance);
                Assert.True(method != null, $"{handler}: no public method {parts[2]}");

                var parameters = method!.GetParameters();
                Assert.Equal(new[] { "System.IO.Stream", "Amazon.Lambda.Core.ILambdaContext" },
                    parameters.Select(p => p.ParameterType.FullName));
                Assert.Equal("Task`1", method.ReturnType.Name);
                Assert.Equal("System.IO.Stream", method.ReturnType.GetGenericArguments().Single().FullName);
            }
        }
        finally
        {
            context.Unload();
        }
    }

    [Fact]
    public void ThePackageTargetsTheRuntimeTheFunctionsAreCreatedWith()
    {
        using var zip = ZipFile.OpenRead(Path.Combine(LambdaDir, DeployerPackages.ZipFor(DeployerPackages.Deployer)));
        using var reader = new StreamReader(zip.GetEntry("Lz.Aws.Deployer.runtimeconfig.json")!.Open());
        using var config = JsonDocument.Parse(reader.ReadToEnd());

        Assert.Equal("net10.0", config.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString());
        // DeployerBootstrapper creates the functions with this runtime.
        Assert.Equal("dotnet10", Amazon.Lambda.Runtime.Dotnet10.Value);
    }

    [Fact]
    public void TheDecisionsInThePackage_AreLinkedFromTheTestedSources_NotCopied()
    {
        // WHAT RUNS IN THE ACCOUNT IS WHAT THIS SUITE PINNED only if the deployer compiles the same
        // files. A copy would pass every test here while the functions ran whatever the copy said.
        var root = RepoRoot();
        var project = XDocument.Load(Path.Combine(root, "Lz.Aws.Deployer", "Lz.Aws.Deployer.csproj"));
        var linked = project.Descendants("Compile")
            .Select(c => c.Attribute("Include")?.Value)
            .Where(v => v != null)
            .Select(v => v!.Replace('\\', '/'))
            .ToList();

        foreach (var decision in new[]
                 {
                     "../Lz.Aws/Pipeline/BuildRecord.cs", "../Lz.Aws/Pipeline/DeployVerification.cs",
                     "../Lz.Aws/Pipeline/DeployerContract.cs", "../Lz.Aws/Pipeline/DeployerSteps.cs",
                     "../Lz.Aws/Pipeline/SignatureHook.cs", "../Lz.Aws/Ops/TaskDefinitionRevision.cs",
                 })
        {
            Assert.Contains(decision, linked);
            Assert.True(File.Exists(Path.Combine(root, "Lz.Aws.Deployer", decision)), $"{decision} does not exist");
        }

        // And no source in the deployer's own folder redeclares the pipeline namespace.
        var copies = Directory.EnumerateFiles(Path.Combine(root, "Lz.Aws.Deployer"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("namespace Lz.Aws.Pipeline", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(copies);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Lz.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
