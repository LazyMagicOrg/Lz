using Lz.Aws.Auth;
using Lz.Core.Config;

namespace Lz.Tests.Auth.Tests;

/// <summary>
/// The pure decision behind Cognito user-pool deletion protection. No AWS — the
/// component only translates this into a <c>UserPoolArgs.DeletionProtection</c>
/// assignment, so pinning the decision here pins the behaviour.
/// </summary>
public class CognitoDurabilityPolicyTests
{
    [Fact]
    public void NullConfig_ReturnsNull_SoThePropertyIsLeftUNSET()
    {
        // The distinction this test exists for: null means "do not assign the property
        // at all", which is NOT the same as assigning "INACTIVE" even though INACTIVE is
        // the service default. Six workspaces share this component and the compatibility
        // guarantee is that an un-opted-in system emits the plan it emitted before.
        Assert.Null(CognitoDurabilityPolicy.ForUserPool(null));
    }

    [Fact]
    public void OptedOut_ReturnsInactive_NotNull()
    {
        // A present section that says false is an explicit choice, and worth stating on
        // the resource — distinct from having no opinion.
        Assert.Equal(
            CognitoDurabilityPolicy.Inactive,
            CognitoDurabilityPolicy.ForUserPool(new DurabilityConfig { DeletionProtection = false }));
    }

    [Fact]
    public void OptedIn_ReturnsActive()
    {
        Assert.Equal(
            CognitoDurabilityPolicy.Active,
            CognitoDurabilityPolicy.ForUserPool(new DurabilityConfig { DeletionProtection = true }));
    }

    [Fact]
    public void PointInTimeRecovery_DoesNotAffectThePoolDecision()
    {
        // Cognito has no PITR concept. If the pool decision ever starts varying with this
        // flag, something has been wired that cannot exist.
        var a = CognitoDurabilityPolicy.ForUserPool(
            new DurabilityConfig { DeletionProtection = true, PointInTimeRecovery = false });
        var b = CognitoDurabilityPolicy.ForUserPool(
            new DurabilityConfig { DeletionProtection = true, PointInTimeRecovery = true });

        Assert.Equal(a, b);
    }

    [Fact]
    public void ReturnsCognitosOwnWireValues_NotBooleansOrLowercase()
    {
        // The Pulumi member is Input<string> over exactly these two literals; a bool or a
        // lowercased value would be rejected by the provider's enum validation at deploy
        // time rather than at compile time.
        Assert.Equal("ACTIVE", CognitoDurabilityPolicy.Active);
        Assert.Equal("INACTIVE", CognitoDurabilityPolicy.Inactive);
    }

    [Fact]
    public void ScutarasLiveConfig_ProtectsThePools()
    {
        // Pins the outcome the 2026-09-04 live audit demanded: scu-dev already sets
        // DeletionProtection, so the next deploysystem must move all three pools from
        // INACTIVE to ACTIVE.
        var scutaraDev = new DurabilityConfig { DeletionProtection = true, PointInTimeRecovery = true };

        Assert.Equal(CognitoDurabilityPolicy.Active, CognitoDurabilityPolicy.ForUserPool(scutaraDev));
    }
}
