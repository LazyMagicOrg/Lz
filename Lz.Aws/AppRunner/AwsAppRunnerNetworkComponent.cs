using System.Collections.Immutable;
using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Acm;

namespace Lz.Aws.AppRunner;

/// <summary>
/// Minimal network for AppRunner topology.
/// No VPC — AppRunner, DynamoDB, S3, Cognito are all public AWS services
/// accessible via IAM. Only creates an ACM certificate and looks up
/// the Route 53 hosted zone for domain management.
/// </summary>
public class AwsAppRunnerNetworkComponent : ComponentResource, ISystemNetworkComponent
{
    public AwsAppRunnerNetworkComponent()
        : base("lz:aws:AppRunnerNetwork", "network", ResourceArgs.Empty, null)
    {
    }

    public INetworkOutputs Deploy(SystemConfig config)
    {
        var sk = config.SystemKey;
        var env = config.Environment;
        var prefix = $"{sk}-{env}";
        var domain = config.SystemDomain;

        // =====================================================================
        // ACM CERTIFICATE (regional — for AppRunner custom domain if needed)
        // =====================================================================

        var cert = new Certificate($"{prefix}-cert", new CertificateArgs
        {
            DomainName = domain,
            SubjectAlternativeNames = { $"*.{domain}" },
            ValidationMethod = "DNS",
            Tags =
            {
                { "Name", $"{sk}-cert" },
                { "System", sk },
                { "ManagedBy", "lz-pulumi" },
            },
        }, new CustomResourceOptions { Parent = this });

        // DNS validation via Route 53
        var publicZone = Pulumi.Aws.Route53.GetZone.Invoke(
            new Pulumi.Aws.Route53.GetZoneInvokeArgs { Name = domain });

        var validationRecord = new Pulumi.Aws.Route53.Record($"{prefix}-cert-validation",
            new Pulumi.Aws.Route53.RecordArgs
            {
                ZoneId = publicZone.Apply(z => z.ZoneId),
                Name = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordName!),
                Type = cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordType!),
                Records = { cert.DomainValidationOptions.Apply(o => o[0].ResourceRecordValue!) },
                Ttl = 300,
                AllowOverwrite = true,
            }, new CustomResourceOptions { Parent = this });

        var certValidation = new CertificateValidation($"{prefix}-cert-validated",
            new CertificateValidationArgs
            {
                CertificateArn = cert.Arn,
                ValidationRecordFqdns = { validationRecord.Fqdn },
            }, new CustomResourceOptions { Parent = this });

        // Empty immutable arrays for the interface stubs
        var emptySubnets = Output.Create(ImmutableArray<string>.Empty);

        return new AwsAppRunnerNetworkOutputs
        {
            NetworkId = Output.Create(""),
            PrivateSubnetIds = emptySubnets,
            PublicSubnetIds = emptySubnets,
            PrivateDnsZoneId = Output.Create(""),
            PublicDnsZoneId = publicZone.Apply(z => z.ZoneId),
            CertificateArn = certValidation.CertificateArn,
        };
    }
}
