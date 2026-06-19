using Lz.Aws.Interfaces.Outputs;
using Lz.Aws.Interfaces;
using Lz.Core.Config;
using Lz.Aws.Config;
using Lz.Core.Interfaces;
using Lz.Core.Interfaces.Outputs;
using Pulumi;
using Pulumi.Aws.Ec2;
using Pulumi.Aws.Iam;
using Pulumi.Aws.Iam.Inputs;
using AsgGroup = Pulumi.Aws.AutoScaling.Group;
using AsgGroupArgs = Pulumi.Aws.AutoScaling.GroupArgs;

namespace Lz.Aws.Ecs;

/// <summary>
/// AWS Tailscale subnet router component — deploys an Auto Scaling Group of
/// EC2 instances running Tailscale as subnet routers. Provides private mesh
/// network access to VPC resources (RDS, EFS, ECS services).
///
/// Each instance also mounts EFS and runs SSH, acting as an SFTP gateway
/// for file access via WinSCP. Connect to the MagicDNS name "{prefix}-efs".
///
/// Prerequisites:
///   - tailscale-api-key in shared/system secret (manually created in Tailscale admin)
///   - tailscale-auth-key in shared/system secret (auto-managed by ITailscaleKeyManager)
///   - tailscale-ssh-public-key in shared/system secret (auto-managed by ITailscaleKeyManager)
///   - shared/system secret encrypted with CMK (alias/shared-secrets-key) for cross-account access
///   - Tailscale OIDC client configured in Keycloak (adminsauth realm)
/// </summary>
public class AwsTailscaleAsgComponent : ComponentResource, ITailscaleComponent
{
    public AwsTailscaleAsgComponent()
        : base("lz:aws:TailscaleAsg", "tailscale", ResourceArgs.Empty, null)
    {
    }

    public ITailscaleOutputs Deploy(SystemConfig config, INetworkOutputs network, IFileStorageOutputs fileStorage)
    {
        var prefix = config.SystemKey;
        var awsNetwork = (AwsNetworkOutputs)network;
        var ecs = config.Aws().ECS ?? new EcsConfig();
        var instanceType = ecs.TailscaleInstanceType;
        var desiredCapacity = ecs.TailscaleDesiredCapacity;

        // =====================================================================
        // IAM INSTANCE PROFILE
        // =====================================================================

        // Resolve the KMS key ARN for Secrets Manager decrypt access.
        // - Tenant deployments: SharedKmsKeyArn is set by CLI at startup.
        // - Shared deployments: the system secret uses a custom KMS key
        //   (alias/shared-secrets-key); look it up via data source.
        Input<string> iamPolicy;
        if (!string.IsNullOrEmpty(config.Aws().SharedKmsKeyArn))
        {
            iamPolicy = BuildTailscaleIamPolicy(prefix, config.Aws().SharedSecretArn, config.Aws().SharedKmsKeyArn);
        }
        else if (config.Aws().TrustedAccountIds.Count > 0)
        {
            iamPolicy = Pulumi.Aws.Kms.GetAlias.Invoke(new Pulumi.Aws.Kms.GetAliasInvokeArgs
            {
                Name = "alias/shared-secrets-key",
            }).Apply(a => BuildTailscaleIamPolicy(prefix, config.Aws().SharedSecretArn, a.TargetKeyArn));
        }
        else
        {
            iamPolicy = BuildTailscaleIamPolicy(prefix, config.Aws().SharedSecretArn, null);
        }

        var role = new Role($"{prefix}-tailscale-role", new RoleArgs
        {
            Name = $"{prefix}-tailscale-role",
            AssumeRolePolicy = @"{
                ""Version"": ""2012-10-17"",
                ""Statement"": [{
                    ""Effect"": ""Allow"",
                    ""Principal"": { ""Service"": ""ec2.amazonaws.com"" },
                    ""Action"": ""sts:AssumeRole""
                }]
            }",
            ManagedPolicyArns =
            {
                // SSM for remote management
                "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore",
            },
            InlinePolicies =
            {
                new RoleInlinePolicyArgs
                {
                    Name = "TailscaleSecretsAndEfs",
                    Policy = iamPolicy,
                },
            },
            Tags = Tags(prefix),
        }, new CustomResourceOptions { Parent = this });

        var instanceProfile = new InstanceProfile($"{prefix}-tailscale-profile", new InstanceProfileArgs
        {
            Name = $"{prefix}-tailscale-profile",
            Role = role.Name,
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // LOOKUP LATEST ARM64 AL2023 AMI
        // =====================================================================

        var ami = Pulumi.Aws.Ec2.GetAmi.Invoke(new GetAmiInvokeArgs
        {
            MostRecent = true,
            Owners = { "amazon" },
            Filters =
            {
                new Pulumi.Aws.Ec2.Inputs.GetAmiFilterInputArgs
                {
                    Name = "name",
                    Values = { "al2023-ami-*-arm64" },
                },
                new Pulumi.Aws.Ec2.Inputs.GetAmiFilterInputArgs
                {
                    Name = "state",
                    Values = { "available" },
                },
            },
        });

        // =====================================================================
        // USER DATA — install Tailscale, mount EFS, configure SSH
        // =====================================================================

        // Resolve EFS filesystem ID at deployment time (Pulumi Output)
        var userDataOutput = fileStorage.FileSystemId.Apply(fsId =>
            GenerateUserData(config, fsId));

        // =====================================================================
        // LAUNCH TEMPLATE
        // =====================================================================

        // Description references the NAT gateway ID to create an implicit Pulumi
        // dependency — ensures instances don't launch until NAT is provisioned.
        var ltDescription = awsNetwork.NatGatewayId.Apply(
            natId => $"Tailscale subnet router (nat: {natId})");

        var launchTemplate = new LaunchTemplate($"{prefix}-tailscale-lt", new LaunchTemplateArgs
        {
            Name = $"{prefix}-tailscale-lt",
            Description = ltDescription,
            ImageId = ami.Apply(a => a.Id),
            InstanceType = instanceType,
            UserData = userDataOutput.Apply(ud =>
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(ud))),
            IamInstanceProfile = new Pulumi.Aws.Ec2.Inputs.LaunchTemplateIamInstanceProfileArgs
            {
                Arn = instanceProfile.Arn,
            },
            NetworkInterfaces =
            {
                new Pulumi.Aws.Ec2.Inputs.LaunchTemplateNetworkInterfaceArgs
                {
                    AssociatePublicIpAddress = "false",
                    SecurityGroups = { awsNetwork.TailscaleSecurityGroupId },
                },
            },
            TagSpecifications =
            {
                new Pulumi.Aws.Ec2.Inputs.LaunchTemplateTagSpecificationArgs
                {
                    ResourceType = "instance",
                    Tags = Tags(prefix),
                },
            },
            Tags = Tags(prefix),
        }, new CustomResourceOptions { Parent = this });

        // =====================================================================
        // AUTO SCALING GROUP
        // =====================================================================

        var asg = new AsgGroup($"{prefix}-tailscale-asg", new AsgGroupArgs
        {
            Name = $"{prefix}-tailscale-asg",
            DesiredCapacity = desiredCapacity,
            MinSize = 0,
            MaxSize = desiredCapacity * 2,
            VpcZoneIdentifiers = network.PrivateSubnetIds.Apply(ids => ids.AsEnumerable().ToList()),
            LaunchTemplate = new Pulumi.Aws.AutoScaling.Inputs.GroupLaunchTemplateArgs
            {
                Id = launchTemplate.Id,
                Version = "$Latest",
            },
            HealthCheckType = "EC2",
            HealthCheckGracePeriod = 120,
            Tags =
            {
                new Pulumi.Aws.AutoScaling.Inputs.GroupTagArgs
                {
                    Key = "Name",
                    Value = $"{prefix}-tailscale",
                    PropagateAtLaunch = true,
                },
                new Pulumi.Aws.AutoScaling.Inputs.GroupTagArgs
                {
                    Key = "ManagedBy",
                    Value = "lz-pulumi",
                    PropagateAtLaunch = true,
                },
            },
        }, new CustomResourceOptions { Parent = this });

        return new AwsTailscaleOutputs
        {
            AutoScalingGroupId = asg.Id,
        };
    }

    /// <summary>
    /// Build IAM policy for Tailscale instances — local secret + optional cross-account shared secret.
    /// </summary>
    private static string BuildTailscaleIamPolicy(string prefix, string? sharedSecretArn, string? sharedKmsKeyArn)
    {
        var resources = $@"""arn:aws:secretsmanager:*:*:secret:{prefix}/system*""";
        if (!string.IsNullOrEmpty(sharedSecretArn))
            resources += $@", ""{sharedSecretArn}*""";

        var kmsStatement = "";
        if (!string.IsNullOrEmpty(sharedKmsKeyArn))
        {
            kmsStatement = $@",
                {{
                    ""Effect"": ""Allow"",
                    ""Action"": [""kms:Decrypt"", ""kms:DescribeKey""],
                    ""Resource"": ""{sharedKmsKeyArn}""
                }}";
        }

        return $@"{{
            ""Version"": ""2012-10-17"",
            ""Statement"": [
                {{
                    ""Effect"": ""Allow"",
                    ""Action"": [""secretsmanager:GetSecretValue""],
                    ""Resource"": [{resources}]
                }},
                {{
                    ""Effect"": ""Allow"",
                    ""Action"": [
                        ""elasticfilesystem:ClientMount"",
                        ""elasticfilesystem:ClientWrite"",
                        ""elasticfilesystem:ClientRootAccess""
                    ],
                    ""Resource"": ""*""
                }}{kmsStatement}
            ]
        }}";
    }

    /// <summary>
    /// Generate the user data script that installs Tailscale as a subnet
    /// router, mounts EFS for file access, and configures SSH for SFTP
    /// (WinSCP) access to EFS content.
    /// </summary>
    private static string GenerateUserData(SystemConfig config, string fileSystemId)
    {
        var prefix = config.SystemKey;
        var vpcCidr = config.VpcCidr;
        var efsRegion = config.Region; // EFS is always in the deployment region

        // Tailscale keys always live in shared/system — use ARN for cross-account access
        var secretId = !string.IsNullOrEmpty(config.Aws().SharedSecretArn)
            ? config.Aws().SharedSecretArn
            : "shared/system";
        var secretRegion = !string.IsNullOrEmpty(config.Aws().SharedRegion)
            ? config.Aws().SharedRegion
            : config.Region;

        return $@"#!/bin/bash
set -euo pipefail

# Wait for outbound connectivity (NAT gateway may still be provisioning)
for i in 1 2 3 4 5; do
    if curl -fsS --max-time 10 https://tailscale.com/install.sh -o /dev/null 2>/dev/null; then
        break
    fi
    echo ""Waiting for internet connectivity (attempt $i/5)...""
    sleep $(( i * 15 ))
done

# ===================================================================
# SSM Agent — enables AWS Console -> Connect -> SSM Session Manager.
# AL2023 minimal does NOT include amazon-ssm-agent by default and the
# package isn't always in the minimal AMI's enabled dnf repos, so we
# install from the official AWS RPM URL (works on any AL/RHEL variant).
# Architecture is detected so the same script runs on x86_64 or arm64.
# The IAM role already carries AmazonSSMManagedInstanceCore; once the
# agent starts it registers and the SSM tab goes Online within ~60s.
# Failure here is non-fatal — Tailscale + EFS setup must still proceed.
# ===================================================================
ARCH=$(uname -m)
case ""$ARCH"" in
    x86_64) SSM_RPM_ARCH=linux_amd64 ;;
    aarch64) SSM_RPM_ARCH=linux_arm64 ;;
    *) SSM_RPM_ARCH= ;;
esac

if [ -n ""$SSM_RPM_ARCH"" ]; then
    SSM_RPM_URL=""https://s3.{efsRegion}.amazonaws.com/amazon-ssm-{efsRegion}/latest/$SSM_RPM_ARCH/amazon-ssm-agent.rpm""
    if dnf install -y ""$SSM_RPM_URL""; then
        systemctl enable --now amazon-ssm-agent && echo 'SSM agent installed and started'
    else
        echo 'WARNING: SSM agent install failed; continuing without it'
    fi
else
    echo ""WARNING: unknown arch $ARCH; skipping SSM agent install""
fi

# Install Tailscale
curl -fsSL https://tailscale.com/install.sh | sh

# Enable IP forwarding (required for subnet routing)
echo 'net.ipv4.ip_forward = 1' >> /etc/sysctl.d/99-tailscale.conf
echo 'net.ipv6.conf.all.forwarding = 1' >> /etc/sysctl.d/99-tailscale.conf
sysctl -p /etc/sysctl.d/99-tailscale.conf

# Retrieve secrets from Secrets Manager (auth key + SSH public key)
SECRET_JSON=$(aws secretsmanager get-secret-value \
    --secret-id '{secretId}' \
    --region '{secretRegion}' \
    --query 'SecretString' \
    --output text)

AUTHKEY=$(echo ""$SECRET_JSON"" | python3 -c ""import sys,json; print(json.load(sys.stdin).get('tailscale-auth-key',''))"")

if [ -z ""$AUTHKEY"" ]; then
    echo 'ERROR: tailscale-auth-key not found in Secrets Manager'
    exit 1
fi

# Start Tailscale as subnet router
tailscale up \
    --authkey=""$AUTHKEY"" \
    --advertise-routes={vpcCidr} \
    --accept-dns=false \
    --hostname=""{prefix}-{config.Environment}-efs""

echo 'Tailscale subnet router started successfully'

# ===================================================================
# EFS — mount the shared filesystem for SFTP access
# ===================================================================

yum install -y amazon-efs-utils nfs-utils

mkdir -p /efs
mount -t efs -o tls,iam {fileSystemId}:/ /efs
echo '{fileSystemId}:/ /efs efs _netdev,tls,iam 0 0' >> /etc/fstab

# Symlink EFS into ec2-user home for easy WinSCP browsing
ln -sf /efs /home/ec2-user/efs

echo 'EFS mounted at /efs'

# ===================================================================
# SSH — configure key-based access for SFTP (WinSCP)
# ===================================================================

SSH_PUB_KEY=$(echo ""$SECRET_JSON"" | python3 -c ""import sys,json; print(json.load(sys.stdin).get('tailscale-ssh-public-key',''))"")

if [ -n ""$SSH_PUB_KEY"" ]; then
    mkdir -p /home/ec2-user/.ssh
    echo ""$SSH_PUB_KEY"" >> /home/ec2-user/.ssh/authorized_keys
    chmod 700 /home/ec2-user/.ssh
    chmod 600 /home/ec2-user/.ssh/authorized_keys
    chown -R ec2-user:ec2-user /home/ec2-user/.ssh
    echo 'SSH public key configured for ec2-user'
fi

# Ensure sshd is enabled and running
systemctl enable sshd
systemctl start sshd

echo 'Instance setup complete: Tailscale + EFS + SSH'
";
    }

    private static InputMap<string> Tags(string prefix) => new()
    {
        { "System", prefix },
        { "Component", "tailscale" },
        { "ManagedBy", "lz-pulumi" },
    };
}

internal class AwsTailscaleOutputs : ITailscaleOutputs
{
    public required Output<string> AutoScalingGroupId { get; init; }
}
