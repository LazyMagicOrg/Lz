using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

public interface IFileStorageOutputs
{
    Output<string> FileSystemId { get; }
}
