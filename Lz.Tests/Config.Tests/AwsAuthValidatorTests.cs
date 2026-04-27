using Lz.Aws.Config;
using Lz.Core.Config;

namespace Lz.Tests.Config.Tests;

public class AwsAuthValidatorTests
{
    private static SystemConfig WithPool(AwsAuthConfigEntry pool) => new AwsSystemConfig
    {
        Platform = "aws",
        AuthConfigs = new Dictionary<string, AuthConfigEntry> { ["tenantauth"] = pool },
    };

    [Fact]
    public void Validate_RejectsUnknownMfaValue()
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry { MfaConfiguration = "MAYBE" }), errs);
        Assert.Contains(errs, e => e.Contains("MfaConfiguration") && e.Contains("MAYBE"));
    }

    [Fact]
    public void Validate_RejectsUnknownAdvancedSecurityMode()
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry { AdvancedSecurityMode = "FULL" }), errs);
        Assert.Contains(errs, e => e.Contains("AdvancedSecurityMode") && e.Contains("FULL"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(100)]
    public void Validate_RejectsPasswordMinLengthOutOfRange(int len)
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry { PasswordMinLength = len }), errs);
        Assert.Contains(errs, e => e.Contains("PasswordMinLength"));
    }

    [Fact]
    public void Validate_RejectsSmsMfaWithoutSnsRole()
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry
        {
            MfaConfiguration = "ON",
            SmsMfa = true,
            SoftwareTokenMfa = false,
        }), errs);
        Assert.Contains(errs, e => e.Contains("SmsMfa") && e.Contains("SNS"));
    }

    [Fact]
    public void Validate_RejectsMfaRequiredWithNoFactor()
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry
        {
            MfaConfiguration = "ON",
            SmsMfa = false,
            SoftwareTokenMfa = false,
        }), errs);
        Assert.Contains(errs, e => e.Contains("no MFA factor"));
    }

    [Fact]
    public void Validate_AcceptsValidConfig()
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry
        {
            MfaConfiguration = "OPTIONAL",
            SoftwareTokenMfa = true,
            PasswordMinLength = 12,
            AdvancedSecurityMode = "AUDIT",
        }), errs);
        Assert.Empty(errs);
    }

    [Fact]
    public void Validate_RejectsEmptyGroupName()
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry
        {
            Groups = new List<CognitoGroup> { new() { Name = "" } },
        }), errs);
        Assert.Contains(errs, e => e.Contains("Groups[0].Name"));
    }

    [Fact]
    public void Validate_RejectsDuplicateGroupName()
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry
        {
            Groups = new List<CognitoGroup>
            {
                new() { Name = "admin" },
                new() { Name = "Admin" }, // case-insensitive duplicate
            },
        }), errs);
        Assert.Contains(errs, e => e.Contains("duplicate"));
    }

    [Fact]
    public void Validate_RejectsOverLengthGroupName()
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry
        {
            Groups = new List<CognitoGroup> { new() { Name = new string('x', 129) } },
        }), errs);
        Assert.Contains(errs, e => e.Contains("128-char"));
    }

    [Fact]
    public void Validate_FlagsBaseTypeInAuthConfigs()
    {
        var config = new AwsSystemConfig
        {
            Platform = "aws",
            AuthConfigs = new Dictionary<string, AuthConfigEntry>
            {
                ["tenantauth"] = new AuthConfigEntry(), // not AwsAuthConfigEntry
            },
        };
        var errs = new List<string>();
        AwsAuthValidator.Validate(config, errs);
        Assert.Contains(errs, e => e.Contains("did not resolve to AwsAuthConfigEntry"));
    }
}
