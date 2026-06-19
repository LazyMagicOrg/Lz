using System.Text.RegularExpressions;
using Lz.Core.Config;

namespace Lz.Aws.Config;

/// <summary>
/// Validate that user-supplied keys (SystemKey, TenantKey, SubtenantKey,
/// Environment) are safe to interpolate into every downstream AWS resource
/// name the tool constructs. Catches AWS-API rejections (mixed case on S3,
/// forbidden charsets on Cognito custom domains, over-length bucket names)
/// at config load rather than at Pulumi apply.
/// </summary>
/// <remarks>
/// The single charset here is the intersection of the rules across S3 buckets,
/// Cognito domain prefixes, ECR repositories, IAM role names, and DynamoDB
/// tables — so any key that passes <see cref="ValidateKey"/> is safe for all
/// of them. Upper bound on key length (20) leaves headroom for the combined
/// S3 bucket name (≤63 chars); <see cref="ValidateCombinedBucketLength"/>
/// does the precise check once all three keys + suffix are known.
/// </remarks>
public static class AwsNamingValidator
{
    private static readonly Regex _keyPattern = new(
        @"^[a-z]([a-z0-9-]{0,18}[a-z0-9])?$", RegexOptions.Compiled);

    public static void ValidateKey(string? key, string fieldName, List<string> errs)
    {
        if (string.IsNullOrEmpty(key))
        {
            errs.Add($"{fieldName} is required.");
            return;
        }
        if (!_keyPattern.IsMatch(key))
            errs.Add(
                $"{fieldName} '{key}' is invalid. Must be 1-20 chars, lowercase " +
                "letters/digits/hyphens, start with a letter, end alphanumeric. " +
                "This constraint keeps every derived AWS resource name (S3 buckets, " +
                "Cognito domain prefixes, ECR repos, IAM roles) within AWS rules.");
    }

    /// <summary>
    /// System-scope naming checks: SystemKey + Environment character set.
    /// Called from each topology's ValidateConfig delegate.
    /// </summary>
    public static void ValidateSystemKeys(SystemConfig config, List<string> errs)
    {
        ValidateKey(config.SystemKey, "SystemKey", errs);
        ValidateKey(config.Environment, "Environment", errs);
    }

    /// <summary>
    /// Tenant-scope naming checks: TenantKey charset, subtenant keys, and
    /// combined S3 bucket-name length (subtenant buckets are the longest
    /// derived name, so validate against the 63-char S3 limit here).
    /// </summary>
    public static void ValidateTenantKeys(
        SystemConfig system, string tenantKey, TenantConfig tenant, List<string> errs)
    {
        ValidateKey(tenantKey, "TenantKey", errs);

        if (tenant.Subtenants != null)
        {
            foreach (var stk in tenant.Subtenants.Keys)
            {
                ValidateKey(stk, $"SubtenantKey '{stk}'", errs);
                ValidateCombinedBucketLength(system, tenantKey, stk, errs);
            }
        }
    }

    /// <summary>
    /// Verify the subtenant asset-bucket name
    /// <c>{sk}-{tk}-{stk}-assets-{systemSuffix}</c> fits in S3's 63-char limit.
    /// </summary>
    private static void ValidateCombinedBucketLength(
        SystemConfig system, string tenantKey, string subtenantKey, List<string> errs)
    {
        if (string.IsNullOrEmpty(system.SystemKey) || string.IsNullOrEmpty(tenantKey)
            || string.IsNullOrEmpty(subtenantKey))
            return; // upstream checks already recorded the missing field

        var suffix = system.SystemSuffix ?? "";
        var bucketName = $"{system.SystemKey}-{tenantKey}-{subtenantKey}-assets-{suffix}";

        if (bucketName.Length > 63)
            errs.Add(
                $"Subtenant bucket name '{bucketName}' is {bucketName.Length} chars " +
                "(S3 limit is 63). Shorten SystemKey, TenantKey, or SubtenantKey.");

        // S3 requires bucket names to end in an alphanumeric character. An
        // empty SystemSuffix produces '...assets-' which is rejected at
        // CreateBucket time with a vague "InvalidBucketName".
        var last = bucketName[^1];
        if (!(char.IsLetterOrDigit(last) && char.IsLower(last) || char.IsDigit(last)))
            errs.Add(
                $"Subtenant bucket name '{bucketName}' must end with a lowercase letter or " +
                "digit (S3 naming rule). Check that SystemSuffix is non-empty.");
    }
}
