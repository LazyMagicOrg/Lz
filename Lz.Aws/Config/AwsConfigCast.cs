using Lz.Core.Config;

namespace Lz.Aws.Config;

/// <summary>
/// Casts from the base config types to their AWS-derived counterparts. Used
/// throughout <c>Lz.Aws</c> and by plugins whose <c>Deploy</c> project targets
/// AWS. Under the AWS platform, <see cref="ConfigLoader"/> materialises
/// <see cref="AwsSystemConfig"/> / <see cref="AwsTenantConfig"/> /
/// <see cref="AwsSharedConfig"/> for the base property declarations, so the
/// cast always succeeds at runtime.
/// </summary>
public static class AwsConfigCast
{
    public static AwsSystemConfig Aws(this SystemConfig c) => (AwsSystemConfig)c;
    public static AwsTenantConfig Aws(this TenantConfig c) => (AwsTenantConfig)c;
    public static AwsSharedConfig Aws(this SharedConfig c) => (AwsSharedConfig)c;
}
