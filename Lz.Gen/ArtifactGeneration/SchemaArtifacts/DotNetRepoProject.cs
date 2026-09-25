using NSwag.CodeGeneration.CSharp;
using System.Threading.Tasks;
using System;   
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Lz.Gen.DotNetUtils;
using static Lz.Gen.LzLogger;
using static Lz.Gen.OpenApiUtils;

using NSwag;
using Microsoft.CodeAnalysis;
using NSwag.CodeGeneration;
using NJsonSchema;
using Newtonsoft.Json.Linq;

namespace Lz.Gen
{

    public class DotNetRepoProject : DotNetProjectBase  
    {

        #region Properties
        public override string ProjectFilePath
        {
            get => ExportedProjectPath;
            set => ExportedProjectPath = value;
        }
        public override string NameSuffix { get; set; } = "Repo";
        public override string Template { get; set; } = "Repo";
        public override string OutputFolder { get; set; } = "Schemas";
        public List<string> ExportedEntities { get; set; } = new List<string>();
        public string RepoLifetime { get; set; } = "Transient";
        #endregion
        /// <summary>
        /// This process generates a repo project from an OpenApi document.
        /// In general, any files from the propsfilecontent project will be copied to the 
        /// target project, overwriting files in the target project. Then the process
        /// will generate repo classes for each schema defined in the OpenApi document.
        /// 
        /// A service registration class is also written that will register each repo 
        /// for DI.
        /// 
        /// The Repo.csproj file has special handling:
        /// - Renamed to match the project name. ex: EmployeeRepo.csproj
        /// - Overwrites the existing .csproj file if it exists.
        /// The csroj file imports `<Import Project="Repo.g.props" />` which contains 
        /// generated csproj properties. 
        /// 
        /// The csproj file also imports `<Import Project="User.props" />` which contains
        /// user defined csproj properties. The project template may contain a User.props file, 
        /// but it will not be copied to the target project. The user should make changes to the
        /// User.props file in the target project. An empty User.props file is created in the 
        /// target project if one does not exist.
        /// 
        /// In addition, the process generates repository classes from the OpenApi document.
        /// Each of these generated classes have a *.g.cs extension and are partial classes. The user 
        /// is expcted to create a non-generated class with the same name and add additional code to the
        /// class where needed.
        ///
        /// Each generated repo overrides LazyMagic's TableLevel and TableKind with its schema's
        /// x-lz-tablelevel and x-lz-tablekind, defaults (Default, Lsi) included, so the schema is the one place
        /// they are set. The keys are read and checked before any file is written: an unknown value, a key on a
        /// schema that gets no repo, or Gsi at a level with no GSI table stops generation.
        /// 
        /// Limitations: When the OpenApi document is updated such that a schema object is renamed or 
        /// removed, the previously generated classes will be removed. However, any user created 
        /// classes will not be removed. This prevents the loss of user code when the schema changes. 
        /// Such "user created" classes are easily identified as they will not have a corresponding 
        /// generated class.
        /// 
        /// </summary>
        /// <param name="solution"></param>
        /// <param name="directiveArg"></param>
        /// <returns></returns>
        public override async Task GenerateAsync(SolutionBase solution, DirectiveBase directiveArg)
        {
            try
            {
                Schema directive = (Schema)directiveArg;

                // Read OpenApi specifications
                var openApiSpecs = directive.OpenApiSpecs ?? new List<string>();
                var yamlSpec = await MergeApiFilesAsync(solution.SolutionRootFolderPath, openApiSpecs);
                var schemaItems = GetSchemaNames(yamlSpec);

                var openApiDocument = await LoadSchemaDocumentAsync(
                    solution.SolutionRootFolderPath, yamlSpec, directive.SharedSchemas, solution.AggregateSchemas);

                // Set project name and namespace
                var projectName =  directive.Key + NameSuffix ?? "";
                var nameSpace = projectName;

                // Get Schema Project - each DotNetRepo project is paired with a DotNetSchema project.
                var dotnetSchemaProject = directive.Artifacts.Values.Where(x => x is DotNetSchemaProject).FirstOrDefault() as DotNetSchemaProject;
                if(dotnetSchemaProject == null)
                    throw new Exception($"SchemaProject not found for {directive.Key} {projectName}");

                ProjectReferences.Add(dotnetSchemaProject.ExportedProjectPath);
                GlobalUsings.AddRange(dotnetSchemaProject.ExportedGlobalUsings);
                ExportedEntities.AddRange(dotnetSchemaProject.ExportedEntities);

                // Decide which schemas get a repo, and read each one's table, BEFORE any file is touched: a schema
                // that declares an invalid table must stop generation with the project as it was.
                var classDeclarations = RepoClassDeclarations(openApiDocument, projectName, schemaItems);
                var tables = ReadRepoTables(
                    openApiDocument, schemaItems, classDeclarations.Select(x => x.Key).ToList());

                // Copy the template project to the target project. Removes *.g.* files.
                var sourceProjectDir = ResolveTemplateSourceDir(solution);
                await InfoAsync($"Generating {directive.Key} {projectName} from {sourceProjectDir}");
                var targetProjectDir = CombinePath(solution.SolutionRootFolderPath, Path.Combine(OutputFolder, projectName));
                var csprojFileName = GetCsprojFile(sourceProjectDir);
                var filesToExclude = new List<string> { csprojFileName, "User.props", "SRCREADME.md" };
                CopyProject(sourceProjectDir, targetProjectDir, filesToExclude);

                // Create/Update the Repo.csproj file.
                File.Copy(
                    Path.Combine(sourceProjectDir, csprojFileName),
                    Path.Combine(targetProjectDir, projectName + ".csproj"),
                    overwrite: true);

                GenerateCommonProjectFiles(sourceProjectDir, targetProjectDir);
                RenameTemplateFiles(targetProjectDir);

                Directory.CreateDirectory(Path.Combine(targetProjectDir, "Repos"));

                var classes = new List<string>();
                var interfaces = new List<string>();
                foreach (var classDeclaration in classDeclarations)
                {
                    var className = classDeclaration.Key + NameSuffix;
                    classes.Add(className);
                    interfaces.Add("I" + className);
                    var filePath = Path.Combine(targetProjectDir, "Repos", className + ".g.cs");
                    WriteGeneratedFile(
                        filePath, RepoClassSource(projectName, classDeclaration.Key, tables[classDeclaration.Key]));
                }

                // Generate Service Registrations class
                GenerateServiceRegistrations(classes, ServiceRegistrations, nameSpace, projectName, Path.Combine(targetProjectDir, "ServiceRepoExtensions.g.cs"), RepoLifetime);

                // Exports
                ExportedProjectPath = Path.Combine(OutputFolder, projectName, projectName) + ".csproj";
                GlobalUsings.Add(nameSpace);
                ExportedGlobalUsings = GlobalUsings;
                ExportedInterfaces = interfaces;
                ExportedServiceRegistrations = new List<string> { $"Add{projectName}" };
                ExportedEntities = ExportedEntities.Distinct().ToList();
                ExportedPackages = PackageReferences;
            } catch (Exception ex) 
            { 
                throw new Exception($"Error generating {GetType().Name} {ex.Message}", ex);   
            }
        }
        private static void GenerateServiceRegistrations(List<string> repos, List<string> services, string nameSpace, string projectName, string filePath, string repoLifetime)
        {
            var repoRegistrations = $@"
        services.TryAddAWSService<Amazon.DynamoDBv2.IAmazonDynamoDB>();
"; 

            foreach (var repo in repos)
                repoRegistrations += $"\t\tservices.TryAdd{repoLifetime}<I{repo}, {repo}>();\r\n";

            var serviceRegistrations = "";
            foreach(var service in services)
                serviceRegistrations += $"\t\tservices.{service}();\r\n";

            var classbody = $@"
//----------------------
// <auto-generated>
//     Generated by LazyMagic. Do not modify, your changes will be overwritten.
//     If you need to register additional services, do it in a separate class.
//     Also, if you need to register a service with a different lifetime, do it in a seprate class.
//     Note that we are using Try* so if you register a service with the same interface first, that 
//     registration will be used.   
// </auto-generated>
//----------------------
namespace {nameSpace};
public static partial class {projectName}Extensions
{{
    public static IServiceCollection Add{projectName}(this IServiceCollection services)
    {{
        AddCustom(services);    
{repoRegistrations}
{serviceRegistrations}
        return services;
    }}
    // Implement this partial method in a separate file to add custom service registrations
    // Note that this method doesn't return services as partial methods don't allow return 
    // values other than void. Returning the collection is normally implemented to support 
    // method chaining, but that is not required here.
    static partial void AddCustom(IServiceCollection services);

}}
";
            WriteGeneratedFile(filePath, classbody);
        }
        /// <summary>
        /// For shared schemas, AggregateSchemas (all shared specs merged). For non-shared schemas, the shared
        /// aggregate merged with the directive's own spec, to avoid cross-contamination from other non-shared
        /// schemas.
        /// </summary>
        internal static async Task<OpenApiDocument> LoadSchemaDocumentAsync(
            string solutionRootFolderPath, string yamlSpec, bool sharedSchemas, OpenApiDocument aggregateSchemas)
        {
            if (sharedSchemas)
                return aggregateSchemas;
            var mergedYaml = await MergeYamlAsync(
                solutionRootFolderPath,
                new List<string> { yamlSpec, aggregateSchemas.ToYaml() }
            );
            return await ParseOpenApiYamlContent(mergedYaml);
        }

        /// <summary>
        /// The classes that get a repo: each of the directive's own schemas whose NSwag class has a public Id.
        /// </summary>
        private static List<IGrouping<string, ClassDeclarationSyntax>> RepoClassDeclarations(
            OpenApiDocument openApiDocument, string projectName, List<string> schemaItems)
        {
            // Generate classes using NSwag
            var nswagSettings = new CSharpClientGeneratorSettings
            {
                ClassName = projectName,
                UseBaseUrl = false,
                HttpClientType = "ILzHttpClient",
                GenerateClientInterfaces = true,
                GenerateDtoTypes = true,
                CSharpGeneratorSettings =
                {
                    Namespace = projectName,
                    GenerateDataAnnotations = false,
                    ClassStyle = NJsonSchema.CodeGeneration.CSharp.CSharpClassStyle.Inpc,
                    //HandleReferences = true
                },
                OperationNameGenerator = new LzOperationNameGenerator()
            };

            var nswagGenerator = new CSharpClientGenerator(openApiDocument, nswagSettings);
            var code = nswagGenerator.GenerateFile();
            var root = CSharpSyntaxTree.ParseText(code).GetCompilationUnitRoot();

            // Remove the API class so we only have schema classes
            root = RemoveClass(root, projectName); // The API class is named the same as the projectName.

            return root
                .DescendantNodes().OfType<NamespaceDeclarationSyntax>()
                .First()
                    ?.DescendantNodes().OfType<ClassDeclarationSyntax>()
                    .Where(x => schemaItems.Contains(x.Identifier.ValueText) &&
                                x.Members
                                    .OfType<PropertyDeclarationSyntax>()
                                    .Any(p => p.Identifier.ValueText == "Id" &&
                                              p.Modifiers.Any(SyntaxKind.PublicKeyword)))
                    .GroupBy(x => x.Identifier.ValueText)
                    .ToList();
        }

        /// <summary>The table a repo reads and writes: LazyMagic's TableLevel and TableKind member names.</summary>
        internal sealed record RepoTable(string Level, string Kind);

        internal const string TableLevelKey = "x-lz-tablelevel";
        internal const string TableKindKey = "x-lz-tablekind";
        // Matched literally, never parsed: each value is written into the repo as TableLevel.{value} or
        // TableKind.{value}, and Enum.TryParse would accept "1" or "gsi".
        private static readonly string[] TableLevels = { "System", "Tenant", "Subtenant", "Default", "Local" };
        private static readonly string[] TableKinds = { "Lsi", "Gsi" };
        // lz makes a "_gsi" twin only beside a subtenant's table. Default is allowed because the caller's DefaultDB
        // is normally that table; a DefaultDB that is not has no twin, and the repo's first read says so.
        private static readonly string[] GsiLevels = { "Subtenant", "Default" };

        /// <summary>
        /// Reads x-lz-tablelevel and x-lz-tablekind for each of the directive's own schemas. A schema that gets
        /// a repo takes LazyMagic's defaults, Default and Lsi, for a key it leaves out. Refuses, listing every
        /// problem at once: a value that is not a TableLevel or TableKind member name, either key on a schema that
        /// gets no repo, and Gsi at the System, Tenant or Local level, which have no GSI table.
        /// </summary>
        internal static Dictionary<string, RepoTable> ReadRepoTables(
            OpenApiDocument openApiDocument, IEnumerable<string> directiveSchemas, ICollection<string> repoEntities)
        {
            var errors = new List<string>();
            var tables = new Dictionary<string, RepoTable>();
            foreach (var name in directiveSchemas.Distinct())
            {
                if (!openApiDocument.Components.Schemas.TryGetValue(name, out var schema))
                    continue;
                var level = ExtensionValue(schema, TableLevelKey);
                var kind = ExtensionValue(schema, TableKindKey);
                if (!repoEntities.Contains(name))
                {
                    var declared = new[] { (TableLevelKey, level), (TableKindKey, kind) }
                        .Where(k => k.Item2 != null).Select(k => k.Item1).ToList();
                    if (declared.Count > 0)
                        errors.Add($"{name}: declares {string.Join(" and ", declared)}, but gets no repo "
                            + "(a repo is generated only for a schema with an id property)");
                    continue;
                }
                level ??= "Default";
                kind ??= "Lsi";
                if (!TableLevels.Contains(level))
                    errors.Add($"{name}: {TableLevelKey} '{level}' is not one of {string.Join(", ", TableLevels)}");
                if (!TableKinds.Contains(kind))
                    errors.Add($"{name}: {TableKindKey} '{kind}' is not one of {string.Join(", ", TableKinds)}");
                if (kind == "Gsi" && TableLevels.Contains(level) && !GsiLevels.Contains(level))
                    errors.Add($"{name}: {TableKindKey} Gsi needs a GSI table, and the {level} level has none "
                        + $"(lz creates one only beside a subtenant's table: use {string.Join(" or ", GsiLevels)})");
                tables[name] = new RepoTable(level, kind);
            }
            foreach (var name in repoEntities.Where(e => !tables.ContainsKey(e)))
                tables[name] = new RepoTable("Default", "Lsi");
            if (errors.Count > 0)
                throw new Exception($"Invalid table declarations:\n  {string.Join("\n  ", errors)}");
            return tables;
        }

        /// <summary>A schema extension's value as written, "" for an empty one, null when the key is absent.</summary>
        private static string ExtensionValue(JsonSchema schema, string key)
        {
            if (schema.ExtensionData == null || !schema.ExtensionData.TryGetValue(key, out var value))
                return null;
            return value switch
            {
                null => "",
                JValue jValue => jValue.Value?.ToString() ?? "",
                _ => value.ToString()
            };
        }

        /// <summary>A generated repo: the class, its interface, and the table its schema declares.</summary>
        internal static string RepoClassSource(string nameSpace, string entityName, RepoTable table)
        {
            return $@"
//----------------------
// <auto-generated>
//  Generated by LazyMagic. Create overrides and partial method implementations in a separate file.
//  See the README.g.md file for best practices for extending these generated classes.
// </auto-generated>
//----------------------
namespace {nameSpace};
public partial interface I{entityName}Repo : IDocumentRepo<{entityName}> {{}}
public partial class {entityName}Repo : DYDBRepository<{entityName}>, I{entityName}Repo
{{
    public {entityName}Repo(IAmazonDynamoDB client) : base(client) {{}}

    // From the {entityName} schema's {TableLevelKey} and {TableKindKey}; LazyMagic's defaults when absent.
    protected override TableLevel TableLevel => TableLevel.{table.Level};
    protected override TableKind TableKind => TableKind.{table.Kind};
}}
";
        }
    }
}
