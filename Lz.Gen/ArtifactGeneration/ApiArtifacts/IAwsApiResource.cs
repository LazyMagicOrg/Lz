using System;
using System.Collections.Generic;
using System.Text;

namespace Lz.Gen
{
    public interface IAwsApiResource
    {
        string ExportedAwsResourceName { get; set; }
        string ExportedAwsResourceDefinition { get; set; }
        string ExportedPath { get; set; }
        string ExportedPrefix { get; set; }
        string ExportedResourceType { get; set; }
    }
}
