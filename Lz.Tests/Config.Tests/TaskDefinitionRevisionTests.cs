using System.Reflection;
using Amazon.ECS.Model;
using Lz.Aws.Ops;

namespace Lz.Tests.Config.Tests;

/// <summary>
/// Re-registering a task definition with one image changed — the code `lz updatecontainer` and the
/// deployer's Prepare function share.
///
/// <para>RegisterTaskDefinition inherits nothing: every field not passed is silently dropped, and the
/// task that starts without its role, its volumes or its platform fails in a way that looks unrelated
/// to an image update. So the central test here is not about any one field — it is that there is no
/// field the copy does not account for, including fields the SDK has not added yet.</para>
/// </summary>
public class TaskDefinitionRevisionTests
{
    [Fact]
    public void EveryRegisterableField_IsCopied_IncludingOnesTheSdkAddsLater()
    {
        // Give every TaskDefinition property the request also has a distinct, non-default value, then
        // require the request to carry exactly that value. A property added to the request in a future
        // SDK has no counterpart here and fails the second assertion, which is the point.
        var definition = new TaskDefinition();
        var requestProperties = typeof(RegisterTaskDefinitionRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanWrite)
            .ToList();

        // Guard the guard: if reflection found nothing, every assertion below passes vacuously.
        Assert.True(requestProperties.Count >= 17, $"only {requestProperties.Count} request properties were found");

        var sentinels = new Dictionary<string, object>();
        foreach (var rp in requestProperties.Where(p => p.Name != "Tags"))
        {
            var dp = typeof(TaskDefinition).GetProperty(rp.Name);
            Assert.True(dp != null && dp.CanWrite,
                $"RegisterTaskDefinitionRequest.{rp.Name} has no counterpart on TaskDefinition, so a revision cannot " +
                "inherit it from the definition it copies. Decide what it should be and handle it in " +
                "TaskDefinitionRevision.RegisterRequestFor.");

            var value = Sentinel(dp!.PropertyType, rp.Name);
            dp.SetValue(definition, value);
            sentinels[rp.Name] = value;
        }

        var tags = new List<Tag> { new() { Key = "k", Value = "v" } };
        var request = TaskDefinitionRevision.RegisterRequestFor(definition, tags);

        foreach (var rp in requestProperties)
        {
            var actual = rp.GetValue(request);
            var expected = rp.Name == "Tags" ? tags : sentinels[rp.Name];
            Assert.True(Equals(expected, actual),
                $"RegisterTaskDefinitionRequest.{rp.Name} is not copied. RegisterTaskDefinition drops what it is " +
                "not given, so every image update would silently remove it from the service.");
        }
    }

    /// <summary>A value distinguishable from default for each property type the SDK uses.</summary>
    private static object Sentinel(Type type, string name)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        if (underlying == typeof(string)) return $"sentinel-{name}";
        if (underlying == typeof(bool)) return true;
        if (underlying == typeof(int)) return 42;
        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
            return Activator.CreateInstance(underlying)!;

        // AWS SDK constant classes (NetworkMode, PidMode, …) expose their values as static fields.
        var constant = underlying.GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(f => f.FieldType == underlying);
        if (constant != null) return constant.GetValue(null)!;

        return Activator.CreateInstance(underlying)!;
    }

    [Theory]
    [InlineData("503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-mp-aiphost:latest")]
    [InlineData("503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-mp-aiphost@sha256:b330844ac7c512956b92b2a0229ec14223119dbf0c075fbfef7df60894fa20eb")]
    [InlineData("503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-mp-aiphost")]
    public void Repinning_ReplacesWhateverTheImageIsPinnedTo(string current)
    {
        const string digest = "sha256:5e4f504e5ba9a0d90d25f1f111da78e1ce16ca5b40410332fb242a192a4f9e95";

        Assert.Equal(
            "503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-mp-aiphost@" + digest,
            TaskDefinitionRevision.RepinImage(current, digest));
    }

    [Fact]
    public void Repinning_DoesNotMistakeARegistryPortForATag()
    {
        Assert.Equal("localhost:5000/app@sha256:x", TaskDefinitionRevision.RepinImage("localhost:5000/app", "sha256:x"));
    }

    private static TaskDefinition WithContainers(params string[] names) => new()
    {
        Family = "scu-mp-aiphost",
        Revision = 41,
        ContainerDefinitions = names.Select(n => new ContainerDefinition { Name = n, Image = $"repo/{n}:1" }).ToList(),
    };

    [Fact]
    public void TheContainerIsFoundByName()
    {
        Assert.Equal("aiphost", TaskDefinitionRevision.SingleContainerNamed(WithContainers("sidecar", "aiphost"), "aiphost").Name);
    }

    [Fact]
    public void NoSuchContainer_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => TaskDefinitionRevision.SingleContainerNamed(WithContainers("sidecar"), "aiphost"));
    }

    [Fact]
    public void TwoContainersWithTheName_IsRefused_NotAGuess()
    {
        Assert.Throws<InvalidOperationException>(() => TaskDefinitionRevision.SingleContainerNamed(WithContainers("aiphost", "aiphost"), "aiphost"));
    }

    [Fact]
    public void AContainerlessDefinitionFromTheSdk_IsRefused_NotACrash()
    {
        // SDK v4: a collection with no members is null.
        Assert.Throws<InvalidOperationException>(() =>
            TaskDefinitionRevision.SingleContainerNamed(new TaskDefinition { Family = "f" }, "aiphost"));
    }

    [Fact]
    public void ThePinnedImage_IsRegistryRepositoryAndDigest()
    {
        Assert.Equal(
            "503947800380.dkr.ecr.us-west-2.amazonaws.com/scu-4df6-b9c6-aiphost@sha256:" + new string('a', 64),
            TaskDefinitionRevision.PinnedImage(
                "503947800380.dkr.ecr.us-west-2.amazonaws.com", "scu-4df6-b9c6-aiphost", "sha256:" + new string('a', 64)));
    }

    [Theory]
    [InlineData("docker.io", "scu-4df6-b9c6-aiphost", "sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("503947800380.dkr.ecr.us-west-2.amazonaws.com", "Scu-Upper", "sha256:" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("503947800380.dkr.ecr.us-west-2.amazonaws.com", "scu-4df6-b9c6-aiphost", "latest")]
    [InlineData("503947800380.dkr.ecr.us-west-2.amazonaws.com", "scu-4df6-b9c6-aiphost", "sha256:abc")]
    public void AnythingElse_IsRefused(string registry, string repository, string digest)
    {
        Assert.Throws<InvalidOperationException>(() => TaskDefinitionRevision.PinnedImage(registry, repository, digest));
    }
}
