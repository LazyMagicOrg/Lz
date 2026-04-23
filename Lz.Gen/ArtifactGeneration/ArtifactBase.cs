using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace Lz.Gen
{
    public class ArtifactBase
    {
        public virtual string ProjectTemplatesFolder { get; set; } = "ProjectTemplates";
        public virtual string Template { get; set; }

        /// <summary>
        /// Gets the full template path (relative to solution root or bundled assets root)
        /// by combining ProjectTemplatesFolder with Template.
        /// </summary>
        protected virtual string TemplatePath =>
            string.IsNullOrEmpty(Template) ? "" :
            string.IsNullOrEmpty(ProjectTemplatesFolder) ? Template :
            Path.Combine(ProjectTemplatesFolder, Template);

        /// <summary>
        /// Resolve the absolute template source directory, preferring a copy in the
        /// user's solution (solutionRoot/ProjectTemplates/Template) over the copy
        /// bundled with Lz.Gen. Use this instead of manually combining paths so
        /// the local-over-bundled fallback is applied consistently.
        /// </summary>
        public virtual string ResolveTemplateSourceDir(SolutionBase solution) =>
            solution?.ResolveAssetPath(TemplatePath) ?? TemplatePath;
            
        public virtual string OutputFolder { get; set; } 
        public virtual string NameSuffix { get; set; } 
        public virtual string ExportedName { get; set; }

        //[YamlIgnore]
        public virtual string ProjectFilePath { get; set;  } = "";
        public virtual void AssignDefaults(ArtifactBase artifactBase)
        {
            ;
        }

        public virtual void Validate(ArtifactBase artifactBase)
        {
            ;
        }

        public async virtual Task GenerateAsync(SolutionBase solution, DirectiveBase directiveArg)
        {
            await Task.Delay(0);
        }
    }
}
