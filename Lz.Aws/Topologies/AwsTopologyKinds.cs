using Lz.Aws.Auth;
using Lz.Aws.Compute.Fargate;
using Lz.Aws.Compute.FargateAlb;
using Lz.Aws.Compute.Lambda;
using Lz.Aws.Data;
using Lz.Aws.Edge;
using Lz.Aws.Ops;
using Lz.Aws.Shared;
using Lz.Aws.Storage;
using Lz.Aws.Tailscale;
using Lz.Aws.Config;
using Lz.Aws.Interfaces;
using Lz.Aws.Interfaces.Outputs;
namespace Lz.Aws.Topologies;

/// <summary>
/// Compute primitive an AWS topology uses for long-running application code.
/// </summary>
public enum AwsComputeKind
{
    /// <summary>ECS Fargate tasks in private subnets — needs VPC + NAT for internet egress.</summary>
    FargatePrivate,

    /// <summary>ECS Fargate tasks in public subnets — no NAT; tasks get public IPs.</summary>
    FargatePublic,

    /// <summary>AWS Lambda from a container image, fronted by a CloudFront-private Function URL — serverless, scales to zero, no VPC.</summary>
    Lambda,
}

/// <summary>
/// Primary application database choice for an AWS topology.
/// </summary>
public enum AwsDataKind
{
    /// <summary>Amazon RDS (PostgreSQL) — relational, runs in a VPC.</summary>
    Rds,

    /// <summary>Amazon DynamoDB — managed document store, no VPC required.</summary>
    DynamoDb,
}

/// <summary>
/// Shared per-tenant file-storage choice for an AWS topology.
/// </summary>
public enum AwsFileStorageKind
{
    /// <summary>Amazon EFS — POSIX filesystem mounted into containers. Requires VPC.</summary>
    Efs,

    /// <summary>Amazon S3 — object storage, accessed via SDK or presigned URLs.</summary>
    S3,
}

/// <summary>
/// Auth service choice for an AWS topology.
/// </summary>
public enum AwsAuthKind
{
    /// <summary>Self-hosted Keycloak on ECS (usually in the shared-services account).</summary>
    Keycloak,

    /// <summary>Amazon Cognito user pools — managed per-environment.</summary>
    Cognito,
}
