using Lz.Core.Definitions;

namespace Lz.Core.Validation;

public class ValidationResult
{
    public List<string> Errors { get; }
    public bool IsValid => Errors.Count == 0;

    public ValidationResult(List<string> errors)
    {
        Errors = errors;
    }
}

/// <summary>
/// Platform-neutral topology validation. Today this only runs the common
/// cross-topology checks (there are none at present). Topology-specific
/// service constraints (e.g. "this topology doesn't support shared
/// filesystem volumes") are the platform library's responsibility — see
/// <c>Lz.Aws.Topologies.AwsTopology.ValidateConfig</c> for the AWS side.
/// </summary>
public static class TopologyValidator
{
    public static ValidationResult Validate(SystemDefinition system, string topology)
    {
        var errors = new List<string>();
        // No cross-platform checks today. Leave the hook in place so callers
        // have a consistent entry point as topologies evolve.
        return new ValidationResult(errors);
    }
}
