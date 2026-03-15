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

public static class TopologyValidator
{
    public static ValidationResult Validate(SystemDefinition system, string topology)
    {
        var errors = new List<string>();

        foreach (var service in system.Services)
        {
            if (topology == "lambda" && service.Volumes.Any())
                errors.Add($"Service '{service.Name}' defines volumes, which are not supported in the Lambda topology.");

            if (topology == "lambda" && service.Container != null && service.Lambda == null)
                errors.Add($"Service '{service.Name}' has ContainerOptions but no LambdaOptions. Lambda topology requires LambdaOptions.");

            if (topology == "lambda" && service.IngressType == IngressType.Internal)
                errors.Add($"Service '{service.Name}' uses Internal ingress, which is not directly supported in Lambda topology (no internal ALB).");
        }

        return new ValidationResult(errors);
    }
}
