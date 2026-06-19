using System.IO;
using System.Text;
using Lz.Aws.Shared;
using Xunit;
using Xunit.Abstractions;

namespace Lz.Tests;

/// <summary>
/// Smoke test for <see cref="CfFunctionCodePrep"/>. Uses BCProjNew's CFRequest.js
/// (path is env-dependent; test is skipped if the file isn't present).
/// </summary>
public class CfFunctionCodePrepTest
{
    private readonly ITestOutputHelper _output;
    public CfFunctionCodePrepTest(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Minifies_BCProjNew_CFRequest_under_10KB()
    {
        var jsPath = @"C:\Users\TimothyMay\repos\_Dev2\BCProjNew\CloudFront\CFRequest.js";
        if (!File.Exists(jsPath)) return; // skip on CI / other machines

        var rawBytes = Encoding.UTF8.GetByteCount(File.ReadAllText(jsPath));
        _output.WriteLine($"raw bytes: {rawBytes:N0}");

        var minified = CfFunctionCodePrep.PrepareAndValidate(
            jsPath, "CFRequest.js",
            ("${KvsArn}", "arn:aws:cloudfront::123456789012:key-value-store/00000000-0000-0000-0000-000000000000"));

        var minBytes = Encoding.UTF8.GetByteCount(minified);
        _output.WriteLine($"minified bytes: {minBytes:N0}");

        Assert.True(minBytes <= CfFunctionCodePrep.MaxBytes,
            $"minified size {minBytes} should be <= {CfFunctionCodePrep.MaxBytes}");
        Assert.True(minBytes < rawBytes, "minification should reduce size");

        // Dump to a fixed path for inspection.
        File.WriteAllText(@"C:\Users\TimothyMay\repos\_Dev2\_lzdev\Lz.Tests\bin\CFRequest.min.js", minified);

        // Print slices around positions of interest.
        void Slice(int col, int radius = 80)
        {
            var start = Math.Max(0, col - radius);
            var end = Math.Min(minified.Length, col + radius);
            _output.WriteLine($"col {col}±{radius}:\n  {minified.Substring(start, end - start)}\n");
        }
        Slice(500);
        Slice(5232);
    }

    [Theory]
    [InlineData("CFAuthConfig.js")]
    [InlineData("CFAuthCallback.js")]
    [InlineData("CFExplore.js")]
    [InlineData("CFAuth.js")]
    public void Minifies_BCProjNew_CFFunction_under_10KB(string fileName)
    {
        var jsPath = Path.Combine(@"C:\Users\TimothyMay\repos\_Dev2\BCProjNew\CloudFront", fileName);
        if (!File.Exists(jsPath)) return;

        var raw = Encoding.UTF8.GetByteCount(File.ReadAllText(jsPath));
        var minified = CfFunctionCodePrep.PrepareAndValidate(jsPath, fileName);
        var min = Encoding.UTF8.GetByteCount(minified);

        _output.WriteLine($"{fileName}: raw={raw:N0}, min={min:N0}");
        Assert.True(min <= CfFunctionCodePrep.MaxBytes,
            $"{fileName} minified size {min} should be <= {CfFunctionCodePrep.MaxBytes}");
        Assert.True(min < raw, $"{fileName} minification should reduce size");
    }
}
