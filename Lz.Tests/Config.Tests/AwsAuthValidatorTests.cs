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

    // ---- UserPoolTier (cost-optimization: unpin pools from PLUS) -------------

    [Fact]
    public void Validate_RejectsUnknownUserPoolTier()
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(WithPool(new AwsAuthConfigEntry { UserPoolTier = "PREMIUM" }), errs);
        Assert.Contains(errs, e => e.Contains("UserPoolTier") && e.Contains("PREMIUM"));
    }

    [Theory]
    [InlineData("LITE", "AUDIT")]
    [InlineData("ESSENTIALS", "AUDIT")]
    [InlineData("ESSENTIALS", "ENFORCED")]
    public void Validate_RejectsNonPlusTierWithThreatProtection(string tier, string asm)
    {
        // Threat protection (any non-OFF AdvancedSecurityMode) is PLUS-only; the
        // contradictory combination must fail HERE, not mid-deploy inside Cognito.
        var errs = new List<string>();
        AwsAuthValidator.Validate(
            WithPool(new AwsAuthConfigEntry { UserPoolTier = tier, AdvancedSecurityMode = asm }), errs);
        Assert.Contains(errs, e => e.Contains("UserPoolTier") && e.Contains("PLUS"));
    }

    [Theory]
    [InlineData("ESSENTIALS", "OFF")]   // the unpin-from-PLUS shape (consumerauth)
    [InlineData("PLUS", "AUDIT")]        // explicit PLUS with threat protection
    [InlineData(null, "AUDIT")]          // tier unmanaged — the pre-existing baseline
    public void Validate_AcceptsCoherentTierCombinations(string? tier, string asm)
    {
        var errs = new List<string>();
        AwsAuthValidator.Validate(
            WithPool(new AwsAuthConfigEntry { UserPoolTier = tier, AdvancedSecurityMode = asm }), errs);
        Assert.DoesNotContain(errs, e => e.Contains("UserPoolTier"));
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
    public void Validate_RejectsWebAppAuthConfigReferencingUndeclaredPool()
    {
        var cfg = new AwsSystemConfig
        {
            Platform = "aws",
            AuthConfigs = new Dictionary<string, AuthConfigEntry> { ["plannerauth"] = new AwsAuthConfigEntry() },
            Behaviors = new BehaviorsConfig
            {
                WebApps = new List<WebAppBehavior>
                {
                    new() { Path = "/", AppName = "eventit", AuthConfig = "noSuchPool" },
                },
            },
        };
        var errs = new List<string>();
        AwsAuthValidator.Validate(cfg, errs);
        Assert.Contains(errs, e => e.Contains("WebApps[0]") && e.Contains("noSuchPool") && e.Contains("plannerauth"));
    }

    [Fact]
    public void Validate_AllowsNullAuthConfigAsPublic()
    {
        var cfg = new AwsSystemConfig
        {
            Platform = "aws",
            AuthConfigs = new Dictionary<string, AuthConfigEntry> { ["plannerauth"] = new AwsAuthConfigEntry() },
            Behaviors = new BehaviorsConfig
            {
                WebApps = new List<WebAppBehavior>
                {
                    new() { Path = "/free/", AppName = "freeapp", AuthConfig = null },
                    new() { Path = "/free2/", AppName = "freeapp2", AuthConfig = "" },
                },
            },
        };
        var errs = new List<string>();
        AwsAuthValidator.Validate(cfg, errs);
        Assert.DoesNotContain(errs, e => e.Contains("WebApps"));
    }

    [Fact]
    public void Validate_AllowsKnownAuthConfig()
    {
        var cfg = new AwsSystemConfig
        {
            Platform = "aws",
            AuthConfigs = new Dictionary<string, AuthConfigEntry> { ["plannerauth"] = new AwsAuthConfigEntry() },
            Behaviors = new BehaviorsConfig
            {
                WebApps = new List<WebAppBehavior>
                {
                    new() { Path = "/", AppName = "eventit", AuthConfig = "plannerauth" },
                },
            },
        };
        var errs = new List<string>();
        AwsAuthValidator.Validate(cfg, errs);
        Assert.DoesNotContain(errs, e => e.Contains("WebApps"));
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
