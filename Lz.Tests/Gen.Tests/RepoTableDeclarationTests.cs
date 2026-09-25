using Lz.Gen;
using Microsoft.CodeAnalysis.CSharp;
using NSwag;

namespace Lz.Tests.Gen.Tests;

// =====================================================================================================
//  x-lz-tablelevel and x-lz-tablekind: the schema says which table its repo uses, and the repo says it.
//
//  Each schema that gets a repo may declare its table level (System, Tenant, Subtenant, Default, Local) and its
//  table kind (Lsi, Gsi) beside x-lz-genrepo. DotNetRepoProject writes both into every generated repo as overrides
//  of LazyMagic's get-only TableLevel and TableKind, defaults included, so the schema is the one place they are
//  set. What these pin:
//
//  - The keys are read, through both ways a schema directive loads its document (shared: the aggregate of every
//    shared spec; non-shared: its own spec merged with that aggregate).
//  - A value is matched literally against LazyMagic's member names: "tenant", "1" and "GSI" are refused, not
//    parsed into something.
//  - A key on a schema that gets no repo is refused, as is Gsi at a level lz makes no GSI table for.
//  - All of it is decided BEFORE any file is written: CopyProject deletes a project's *.g.* files first, so a
//    refusal after it would leave the project with its generated repos gone.
// =====================================================================================================

public class RepoTableDeclarationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lz-repo-tables-" + Guid.NewGuid().ToString("N"));

    public RepoTableDeclarationTests()
    {
        Directory.CreateDirectory(_root);
        LzLogger.SetLogger(new SilentLogger());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* temp dir */ }
    }

    private sealed class SilentLogger : Lz.Gen.ILogger
    {
        public void Info(string message) { }
        public Task InfoAsync(string message) => Task.CompletedTask;
        public void Error(Exception ex, string message) { }
        public Task ErrorAsync(Exception ex, string message) => Task.CompletedTask;
    }

    /// <summary>A schema spec; each entry is a schema name, whether it has an id, and its extension lines.</summary>
    private static string Spec(params (string Name, bool HasId, string[] Extensions)[] schemas)
    {
        var lines = new List<string>
        {
            "openapi: 3.0.1",
            "info:",
            "  title: test",
            "  version: 1.0.0",
            "paths: {}",
            "components:",
            "  schemas:",
        };
        foreach (var (name, hasId, extensions) in schemas)
        {
            lines.Add($"    {name}:");
            lines.Add("      type: object");
            lines.AddRange(extensions.Select(e => $"      {e}"));
            lines.Add("      properties:");
            lines.Add(hasId ? "        id:" : "        name:");
            lines.Add("          type: string");
        }
        return string.Join("\n", lines) + "\n";
    }

    private static readonly (string, bool, string[])[] Declared =
    {
        ("Lead", true, new[] { "x-lz-tablelevel: Default", "x-lz-tablekind: Gsi" }),
        ("TenantUser", true, new[] { "x-lz-tablelevel: Tenant", "x-lz-tablekind: Lsi" }),
        ("Plain", true, Array.Empty<string>()),
        ("Status", false, Array.Empty<string>()),
    };

    private async Task<OpenApiDocument> Load(string spec, string fileName = "openapi.test-schema.yaml")
    {
        await File.WriteAllTextAsync(Path.Combine(_root, fileName), spec);
        return await OpenApiUtils.LoadOpenApiFilesAsync(_root, new List<string> { fileName });
    }

    private static Dictionary<string, DotNetRepoProject.RepoTable> Read(OpenApiDocument document, params string[] repoEntities)
        => DotNetRepoProject.ReadRepoTables(
            document, document.Components.Schemas.Keys.ToList(), repoEntities.ToList());

    // ---- READING -------------------------------------------------------------------------------

    [Fact]
    public async Task BothKeysAreRead_AndAnAbsentKeyTakesLazyMagicsDefault()
    {
        var tables = Read(await Load(Spec(Declared)), "Lead", "TenantUser", "Plain");

        Assert.Equal(new DotNetRepoProject.RepoTable("Default", "Gsi"), tables["Lead"]);
        Assert.Equal(new DotNetRepoProject.RepoTable("Tenant", "Lsi"), tables["TenantUser"]);
        Assert.Equal(new DotNetRepoProject.RepoTable("Default", "Lsi"), tables["Plain"]);
        Assert.False(tables.ContainsKey("Status"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheKeysSurviveBothWaysADirectiveLoadsItsDocument(bool sharedSchemas)
    {
        // Shared: the directive's document IS the aggregate of the shared specs. Non-shared: its own spec merged
        // with the aggregate, which goes through NSwag's ToYaml and back.
        var spec = Spec(Declared);
        await File.WriteAllTextAsync(Path.Combine(_root, "own.yaml"), spec);
        var aggregate = sharedSchemas
            ? await Load(spec, "shared.yaml")
            : await Load(Spec(("Other", true, new[] { "x-lz-tablekind: Gsi" })), "shared.yaml");
        var yamlSpec = await OpenApiUtils.MergeApiFilesAsync(_root, new List<string> { "own.yaml" });

        var document = await DotNetRepoProject.LoadSchemaDocumentAsync(_root, yamlSpec, sharedSchemas, aggregate);
        var tables = DotNetRepoProject.ReadRepoTables(
            document, OpenApiUtils.GetSchemaNames(yamlSpec), new List<string> { "Lead", "TenantUser", "Plain" });

        Assert.Equal(new DotNetRepoProject.RepoTable("Default", "Gsi"), tables["Lead"]);
        Assert.Equal(new DotNetRepoProject.RepoTable("Tenant", "Lsi"), tables["TenantUser"]);
        Assert.Equal(new DotNetRepoProject.RepoTable("Default", "Lsi"), tables["Plain"]);
    }

    // ---- REFUSALS ------------------------------------------------------------------------------

    [Theory]
    [InlineData("x-lz-tablelevel: tenant", "x-lz-tablelevel 'tenant' is not one of")]
    [InlineData("x-lz-tablelevel: 1", "x-lz-tablelevel '1' is not one of")]
    [InlineData("x-lz-tablekind: GSI", "x-lz-tablekind 'GSI' is not one of")]
    [InlineData("x-lz-tablekind: ''", "x-lz-tablekind '' is not one of")]
    public async Task AValueThatIsNotAMemberName_IsRefused(string extension, string expected)
    {
        var document = await Load(Spec(("Thing", true, new[] { extension })));

        var refusal = Assert.Throws<Exception>(() => Read(document, "Thing"));

        Assert.Contains($"Thing: {expected}", refusal.Message);
    }

    [Theory]
    [InlineData("System")]
    [InlineData("Tenant")]
    [InlineData("Local")]
    public async Task GsiAtALevelWithNoGsiTable_IsRefused(string level)
    {
        var document = await Load(Spec(("Thing", true, new[] { $"x-lz-tablelevel: {level}", "x-lz-tablekind: Gsi" })));

        var refusal = Assert.Throws<Exception>(() => Read(document, "Thing"));

        Assert.Contains($"the {level} level has none", refusal.Message);
    }

    [Theory]
    [InlineData("Subtenant")]
    [InlineData("Default")]
    public async Task GsiBesideASubtenantTable_IsAccepted(string level)
    {
        var document = await Load(Spec(("Thing", true, new[] { $"x-lz-tablelevel: {level}", "x-lz-tablekind: Gsi" })));

        Assert.Equal(new DotNetRepoProject.RepoTable(level, "Gsi"), Read(document, "Thing")["Thing"]);
    }

    [Fact]
    public async Task AKeyOnASchemaThatGetsNoRepo_IsRefused()
    {
        var document = await Load(Spec(("Status", false, new[] { "x-lz-tablelevel: Default", "x-lz-tablekind: Lsi" })));

        var refusal = Assert.Throws<Exception>(() => Read(document));

        Assert.Contains("Status: declares x-lz-tablelevel and x-lz-tablekind, but gets no repo", refusal.Message);
    }

    [Fact]
    public async Task EveryProblemIsListedAtOnce()
    {
        var document = await Load(Spec(
            ("One", true, new[] { "x-lz-tablekind: gsi" }),
            ("Two", true, new[] { "x-lz-tablelevel: Tenant", "x-lz-tablekind: Gsi" })));

        var refusal = Assert.Throws<Exception>(() => Read(document, "One", "Two"));

        Assert.Contains("One: x-lz-tablekind 'gsi'", refusal.Message);
        Assert.Contains("Two: x-lz-tablekind Gsi needs a GSI table", refusal.Message);
    }

    // ---- WRITING -------------------------------------------------------------------------------

    [Fact]
    public void TheRepoOverridesBothProperties_AndParses()
    {
        var source = DotNetRepoProject.RepoClassSource("MatchSchemaRepo", "Lead", new DotNetRepoProject.RepoTable("Default", "Gsi"));

        Assert.Contains("public partial class LeadRepo : DYDBRepository<Lead>, ILeadRepo", source);
        Assert.Contains("protected override TableLevel TableLevel => TableLevel.Default;", source);
        Assert.Contains("protected override TableKind TableKind => TableKind.Gsi;", source);
        Assert.Empty(CSharpSyntaxTree.ParseText(source).GetDiagnostics());
    }

    // ---- THE WHOLE GENERATION ------------------------------------------------------------------

    private async Task<(SolutionBase Solution, Schema Directive)> Solution(string spec, bool sharedSchemas)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "openapi.test-schema.yaml"), spec);
        var template = Path.Combine(_root, "ProjectTemplates", "Repo");
        Directory.CreateDirectory(template);
        await File.WriteAllTextAsync(Path.Combine(template, "Repo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        await File.WriteAllTextAsync(Path.Combine(template, "GlobalUsing.t.cs"), "global using System;\n");

        var aggregate = await OpenApiUtils.LoadOpenApiFilesAsync(
            _root, new List<string> { sharedSchemas ? "openapi.test-schema.yaml" : "openapi.shared.yaml" });
        aggregate.Paths.Clear();
        var solution = new SolutionBase { SolutionRootFolderPath = _root, AggregateSchemas = aggregate };
        var directive = new Schema
        {
            Key = "TestSchema",
            OpenApiSpecs = new List<string> { "openapi.test-schema.yaml" },
            SharedSchemas = sharedSchemas,
            Artifacts = new Artifacts
            {
                ["DotNetSchemaProject"] = new DotNetSchemaProject { ExportedProjectPath = "Schemas/TestSchema/TestSchema.csproj" },
            },
        };
        return (solution, directive);
    }

    private string RepoFile(string entity) => Path.Combine(_root, "Schemas", "TestSchemaRepo", "Repos", $"{entity}Repo.g.cs");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GenerateWritesEachRepoWithItsSchemasTable(bool sharedSchemas)
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "openapi.shared.yaml"), Spec(("Shared", true, Array.Empty<string>())));
        var (solution, directive) = await Solution(Spec(Declared), sharedSchemas);

        await new DotNetRepoProject().GenerateAsync(solution, directive);

        var lead = await File.ReadAllTextAsync(RepoFile("Lead"));
        Assert.Contains("TableLevel.Default;", lead);
        Assert.Contains("TableKind.Gsi;", lead);
        var tenantUser = await File.ReadAllTextAsync(RepoFile("TenantUser"));
        Assert.Contains("TableLevel.Tenant;", tenantUser);
        Assert.Contains("TableKind.Lsi;", tenantUser);
        var plain = await File.ReadAllTextAsync(RepoFile("Plain"));
        Assert.Contains("TableLevel.Default;", plain);
        Assert.Contains("TableKind.Lsi;", plain);
        Assert.False(File.Exists(RepoFile("Status")));
    }

    [Fact]
    public async Task ARefusedDeclaration_LeavesTheProjectAsItWas()
    {
        // CopyProject deletes every *.g.* file before copying the template. Were the declarations read after it,
        // this refusal would leave the project with its generated repos deleted.
        await File.WriteAllTextAsync(Path.Combine(_root, "openapi.shared.yaml"), Spec(("Shared", true, Array.Empty<string>())));
        var (solution, directive) = await Solution(
            Spec(("Lead", true, new[] { "x-lz-tablekind: Gsi" }), ("Status", false, new[] { "x-lz-tablekind: Gsi" })),
            sharedSchemas: false);
        var existing = RepoFile("Lead");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "// the repo the last generation wrote");

        var refusal = await Assert.ThrowsAsync<Exception>(() => new DotNetRepoProject().GenerateAsync(solution, directive));

        Assert.Contains("Status: declares x-lz-tablekind, but gets no repo", refusal.Message);
        Assert.Equal("// the repo the last generation wrote", await File.ReadAllTextAsync(existing));
    }
}
