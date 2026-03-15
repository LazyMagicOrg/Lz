using Lz.Core.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Iam.Inputs;
using Pulumi.Aws.Route53;
using Pulumi.Aws.SecretsManager;
using Pulumi.Aws.Ses;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS SES component — verifies the system domain for email sending,
/// configures DKIM, creates an IAM user with SES send permissions,
/// and stores SMTP credentials in Secrets Manager.
/// </summary>
public class AwsSesComponent : ComponentResource, IEmailComponent
{
    public AwsSesComponent()
        : base("lz:aws:Ses", "email", ResourceArgs.Empty, null)
    {
    }

    public IEmailOutputs Deploy(SystemConfig config, INetworkOutputs network)
    {
        var prefix = config.SystemKey;
        var domain = config.SystemDomain;
        var awsNetwork = (AwsNetworkOutputs)network;

        // =====================================================================
        // SES DOMAIN IDENTITY
        // =====================================================================

        var domainIdentity = new DomainIdentity($"{prefix}-ses-identity", new DomainIdentityArgs
        {
            Domain = domain,
        }, new CustomResourceOptions { Parent = this });

        // TXT verification record in Route 53
        var verificationRecord = new Record($"{prefix}-ses-verification", new RecordArgs
        {
            ZoneId = awsNetwork.PublicDnsZoneId,
            Name = Output.Format($"_amazonses.{domain}"),
            Type = "TXT",
            Ttl = 300,
            Records = { domainIdentity.VerificationToken },
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // DKIM
        // =====================================================================

        var dkim = new DomainDkim($"{prefix}-ses-dkim", new DomainDkimArgs
        {
            Domain = domain,
        }, new CustomResourceOptions { Parent = this });

        // Create 3 DKIM CNAME records
        var dkimRecords = new List<Record>();
        for (var i = 0; i < 3; i++)
        {
            var idx = i; // capture for closure
            var dkimRecord = new Record($"{prefix}-ses-dkim-{i}", new RecordArgs
            {
                ZoneId = awsNetwork.PublicDnsZoneId,
                Name = dkim.DkimTokens.Apply(tokens => $"{tokens[idx]}._domainkey.{domain}"),
                Type = "CNAME",
                Ttl = 300,
                Records = { dkim.DkimTokens.Apply(tokens => $"{tokens[idx]}.dkim.amazonses.com") },
            }, new CustomResourceOptions { Parent = this });
            dkimRecords.Add(dkimRecord);
        }

        // =====================================================================
        // MAIL FROM DOMAIN (SPF)
        // =====================================================================

        var mailFrom = new MailFrom($"{prefix}-ses-mailfrom", new MailFromArgs
        {
            Domain = domain,
            MailFromDomain = Output.Format($"bounce.{domain}"),
        }, new CustomResourceOptions { Parent = this });

        // MX record for bounce subdomain
        var mailFromMx = new Record($"{prefix}-ses-mailfrom-mx", new RecordArgs
        {
            ZoneId = awsNetwork.PublicDnsZoneId,
            Name = Output.Format($"bounce.{domain}"),
            Type = "MX",
            Ttl = 300,
            Records = { Output.Format($"10 feedback-smtp.{config.Region}.amazonses.com") },
        }, new CustomResourceOptions { Parent = this });

        // SPF record for bounce subdomain
        var mailFromSpf = new Record($"{prefix}-ses-mailfrom-spf", new RecordArgs
        {
            ZoneId = awsNetwork.PublicDnsZoneId,
            Name = Output.Format($"bounce.{domain}"),
            Type = "TXT",
            Ttl = 300,
            Records = { "v=spf1 include:amazonses.com ~all" },
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // IAM USER FOR SMTP
        // =====================================================================

        var smtpUser = new User($"{prefix}-ses-smtp-user", new UserArgs
        {
            Name = $"{prefix}-ses-smtp",
            Tags = Tags(prefix),
        }, new CustomResourceOptions { Parent = this });

        var smtpUserPolicy = new UserPolicy($"{prefix}-ses-smtp-policy", new UserPolicyArgs
        {
            User = smtpUser.Name,
            Name = "SesSendEmail",
            Policy = Output.Format($@"{{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{{
                    ""Effect"": ""Allow"",
                    ""Action"": [
                        ""ses:SendEmail"",
                        ""ses:SendRawEmail""
                    ],
                    ""Resource"": ""*"",
                    ""Condition"": {{
                        ""StringEquals"": {{
                            ""ses:FromAddress"": ""*@{domain}""
                        }}
                    }}
                }}]
            }}"),
        }, new CustomResourceOptions { Parent = this });

        var smtpAccessKey = new AccessKey($"{prefix}-ses-smtp-key", new AccessKeyArgs
        {
            User = smtpUser.Name,
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // STORE SMTP CREDENTIALS IN SECRETS MANAGER
        // =====================================================================

        var smtpSecret = new Secret($"{prefix}-ses-smtp-secret", new SecretArgs
        {
            Name = $"{prefix}/ses-smtp",
            Description = $"SES SMTP credentials for {domain}",
            Tags = Tags(prefix),
        }, new CustomResourceOptions
        {
            Parent = this,
            RetainOnDelete = true, // Always retain — avoids AWS scheduled-deletion conflicts on recreate
        });

        // Note: The IAM access key secret is NOT the SMTP password directly.
        // AWS SES SMTP requires a derived password (HMAC-SHA256 signing).
        // We store the raw access key/secret; the post-deploy action or
        // consuming application derives the SMTP password at runtime using
        // the standard SES SMTP credential conversion algorithm.
        var smtpSecretVersion = new SecretVersion($"{prefix}-ses-smtp-secret-version", new SecretVersionArgs
        {
            SecretId = smtpSecret.Id,
            SecretString = Output.Tuple(smtpAccessKey.Id, smtpAccessKey.Secret).Apply(t =>
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["smtp_host"] = $"email-smtp.{config.Region}.amazonaws.com",
                    ["smtp_port"] = "587",
                    ["access_key_id"] = t.Item1,
                    ["secret_access_key"] = t.Item2,
                    ["from_domain"] = domain,
                })),
        }, new CustomResourceOptions { Parent = this });

        return new AwsSesOutputs(
            smtpHost: Output.Create($"email-smtp.{config.Region}.amazonaws.com"),
            smtpPort: Output.Create(587),
            smtpCredentialSecretId: smtpSecret.Id,
            fromDomain: Output.Create(domain));
    }

    private static InputMap<string> Tags(string prefix) => new()
    {
        { "System", prefix },
        { "Component", "ses" },
        { "ManagedBy", "lz-pulumi" },
    };
}

internal class AwsSesOutputs : IEmailOutputs
{
    public Output<string> SmtpHost { get; }
    public Output<int> SmtpPort { get; }
    public Output<string> SmtpCredentialSecretId { get; }
    public Output<string> FromDomain { get; }

    public AwsSesOutputs(
        Output<string> smtpHost,
        Output<int> smtpPort,
        Output<string> smtpCredentialSecretId,
        Output<string> fromDomain)
    {
        SmtpHost = smtpHost;
        SmtpPort = smtpPort;
        SmtpCredentialSecretId = smtpCredentialSecretId;
        FromDomain = fromDomain;
    }
}
