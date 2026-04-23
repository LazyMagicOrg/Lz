using Lz.Gen;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json;
using NSwag;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.Serialization;

namespace Lz.Gen
{
    public class SolutionBase
    {
        public string LazyMagicDirectivesVersion { get; set; }
        public Directives Directives { get; set; }
        public string SolutionRootFolderPath { get; set; }
        public OpenApiDocument AggregateSchemas { get; set; }

        /// <summary>
        /// Filesystem root for templates shipped with the Lz.Gen assembly
        /// (e.g. AppContext.BaseDirectory when hosted inside the lz global tool).
        /// Layout mirrors the solution: ProjectTemplates/... and AWSTemplates/Snippets/...
        /// </summary>
        public string BundledAssetsRoot { get; set; }

        /// <summary>
        /// Resolve a solution-relative asset path (e.g. "ProjectTemplates/Schema",
        /// "AWSTemplates/Snippets/sam.service.apprunner.yaml"). Prefers a copy in
        /// the user's solution when present; otherwise falls back to the bundled
        /// copy shipped with Lz.Gen. Returns the local path if neither exists, so
        /// callers still surface a meaningful "not found" error.
        /// </summary>
        /// <remarks>
        /// Silent by design — callers that want the resolved path in their
        /// "Generating X Y from ..." log line should read the return value and
        /// include it themselves, keeping the log to one line per artifact.
        /// </remarks>
        public virtual string ResolveAssetPath(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return relativePath;

            var local = Path.Combine(SolutionRootFolderPath ?? "", relativePath);
            if (File.Exists(local) || Directory.Exists(local)) return local;

            if (!string.IsNullOrEmpty(BundledAssetsRoot))
            {
                var bundled = Path.Combine(BundledAssetsRoot, relativePath);
                if (File.Exists(bundled) || Directory.Exists(bundled)) return bundled;
            }

            return local;
        }
    }
}
