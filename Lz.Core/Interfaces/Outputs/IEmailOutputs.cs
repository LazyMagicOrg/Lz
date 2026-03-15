using Pulumi;

namespace Lz.Core.Interfaces.Outputs;

public interface IEmailOutputs
{
    Output<string> SmtpHost { get; }
    Output<int> SmtpPort { get; }
    Output<string> SmtpCredentialSecretId { get; }
    Output<string> FromDomain { get; }
}
