using System.Text.Json;
using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Lz.Aws;

/// <summary>
/// Resolves cross-account AWS information using STS and EC2 APIs.
/// Used at CLI startup to discover shared account ID and endpoint service names.
/// </summary>
public static class AwsAccountResolver
{
    /// <summary>
    /// Resolve the AWS account ID for the given profile using sts:GetCallerIdentity.
    /// </summary>
    public static async Task<string> ResolveAccountIdAsync(string profile, string region)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        var chain = new CredentialProfileStoreChain();

        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            throw new InvalidOperationException(
                $"Could not resolve AWS credentials for profile '{profile}'. " +
                "Ensure the profile exists and you are authenticated.");

        var client = new AmazonSecurityTokenServiceClient(credentials, regionEndpoint);
        var response = await client.GetCallerIdentityAsync(new GetCallerIdentityRequest());
        return response.Account;
    }

    /// <summary>
    /// Discover the VPC Endpoint Service name from the shared account.
    /// Looks for an endpoint service tagged with System=shared.
    /// </summary>
    public static async Task<string?> ResolveEndpointServiceNameAsync(string profile, string region)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        var chain = new CredentialProfileStoreChain();

        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            return null;

        var client = new AmazonEC2Client(credentials, regionEndpoint);
        var response = await client.DescribeVpcEndpointServiceConfigurationsAsync(
            new DescribeVpcEndpointServiceConfigurationsRequest());

        // Find the service tagged with System=shared
        foreach (var svc in response.ServiceConfigurations)
        {
            if (svc.Tags.Any(t => t.Key == "System" && t.Value == "shared"))
                return svc.ServiceName;
        }

        return null;
    }

    /// <summary>
    /// Resolve the actual KMS key ARN for a given alias in the shared account.
    /// Alias ARNs cannot be used as IAM policy resources for key operations.
    /// </summary>
    public static async Task<string?> ResolveKmsKeyArnAsync(string profile, string region, string aliasName)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        var chain = new CredentialProfileStoreChain();

        if (!chain.TryGetAWSCredentials(profile, out var credentials))
            return null;

        var client = new AmazonKeyManagementServiceClient(credentials, regionEndpoint);
        try
        {
            var response = await client.DescribeKeyAsync(new DescribeKeyRequest
            {
                KeyId = aliasName,
            });
            return response.KeyMetadata.Arn;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Read specific entries from a Secrets Manager JSON secret.
    /// Returns a dictionary of requested keys and their values.
    /// Missing keys are returned with null values.
    /// </summary>
    public static async Task<Dictionary<string, string?>> ReadSecretEntriesAsync(
        string profile, string region, string secretId, params string[] keys)
    {
        var result = new Dictionary<string, string?>();
        foreach (var key in keys)
            result[key] = null;

        try
        {
            var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
            var chain = new CredentialProfileStoreChain();

            AmazonSecretsManagerClient client;
            if (chain.TryGetAWSCredentials(profile, out var credentials))
                client = new AmazonSecretsManagerClient(credentials, regionEndpoint);
            else
                client = new AmazonSecretsManagerClient(regionEndpoint);

            var response = await client.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = secretId });

            using var doc = JsonDocument.Parse(response.SecretString);
            foreach (var key in keys)
            {
                if (doc.RootElement.TryGetProperty(key, out var value))
                    result[key] = value.GetString();
            }
        }
        catch
        {
            // Secret not accessible — return nulls
        }

        return result;
    }

    /// <summary>
    /// Write a string value to an SSM Parameter Store parameter.
    /// Creates the parameter if it doesn't exist, or overwrites it.
    /// </summary>
    public static async Task WriteSsmParameterAsync(
        string profile, string region, string parameterName, string value,
        string? description = null)
    {
        var regionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region);
        var chain = new CredentialProfileStoreChain();

        AmazonSimpleSystemsManagementClient client;
        if (chain.TryGetAWSCredentials(profile, out var credentials))
            client = new AmazonSimpleSystemsManagementClient(credentials, regionEndpoint);
        else
            client = new AmazonSimpleSystemsManagementClient(regionEndpoint);

        using (client)
        {
            await client.PutParameterAsync(new PutParameterRequest
            {
                Name = parameterName,
                Value = value,
                Type = ParameterType.String,
                Overwrite = true,
                Description = description,
                Tier = ParameterTier.IntelligentTiering,
            });
        }
    }
}
