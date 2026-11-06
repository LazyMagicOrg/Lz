using Lz.Aws.Auth;
using Lz.Aws.Config;
using Lz.Aws.Tailscale;

namespace Lz.Tests.Compute.Tests;

/// <summary>
/// A value Pulumi compares against state must not depend on the checkout that built lz. On 2026-11-06 the pipeline's Plan
/// task (Linux, LF) planned two foundation changes a Windows workstation (core.autocrlf, CRLF) did not: the Tailscale
/// launch template's userData and the custom-auth Lambda's code. Each would have rewritten the other's deploy. Both tests
/// fail on a CRLF checkout without the fix (they are red on Windows, where the bug lived).
/// </summary>
public class CheckoutIndependentDeployInputsTests
{
    [Theory]
    [InlineData("")]
    [InlineData("fs-0123456789abcdef0")]
    public void TailscaleUserData_HasOnlyLfLineEndings(string fileSystemId)
    {
        var config = new AwsSystemConfig { SystemKey = "scu", Environment = "dev", Region = "us-west-2", VpcCidr = "10.0.0.0/16" };

        var script = AwsTailscaleAsgComponent.GenerateUserData(config, fileSystemId);

        Assert.StartsWith("#!/bin/bash\n", script);
        Assert.DoesNotContain('\r', script);
    }

    [Fact]
    public void CustomAuthLambdaSource_AsShippedNextToTheAssembly_HasOnlyLfLineEndings()
    {
        // The exact path AwsCognitoComponent hands to FileArchive.
        var dir = Path.Combine(Path.GetDirectoryName(typeof(AwsCognitoComponent).Assembly.Location)!, "CognitoCustomAuth");
        var files = Directory.GetFiles(dir);

        Assert.NotEmpty(files);
        foreach (var file in files)
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(file));
    }
}
