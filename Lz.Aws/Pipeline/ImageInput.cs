using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lz.Aws.Pipeline;

/// <summary>
/// A SERVICE'S IMAGE INPUT (DecoupledCd.md §12 P3 J, V-22; ImageThroughPulumi.md J1): the digest the service is to run, the
/// repository it comes from, the build's time where known, and who wrote it when. An SSM String parameter per service at
/// <c>/{sk}/{env}/pipeline/image/{service}</c>, created by <c>bootstrapdeployer</c> and never by Pulumi, so no deploy resets it.
/// <para>J1: every writer that moves the image also writes this, and <c>deploytenant</c> reads it and refuses when it does not
/// match what runs. From J2 the tenant program declares the image from it.</para>
/// </summary>
/// <param name="BuiltAt">The build's time (UTC, <c>Z</c>) where the writer knows it - the deployer always does, from the record;
/// null where it does not, never guessed.</param>
public sealed record ImageInput(string Digest, string Repository, string? BuiltAt, string Writer, string WrittenAt);

public static class ImageInputFormat
{
    public const int Schema = 1;

    /// <summary>The input's name for one ECS service.</summary>
    public static string NameFor(string systemKey, string environment, string service) => $"{PrefixFor(systemKey, environment)}{service}";

    /// <summary>Where every input of an environment lives: what the functions are configured with, and their grant's resource.</summary>
    public static string PrefixFor(string systemKey, string environment) => $"/{systemKey}/{environment}/pipeline/image/";

    private static readonly Regex Digest = new("^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex Repository = new(@"^[a-z0-9][a-z0-9._/-]{0,255}$", RegexOptions.CultureInvariant);

    public static string Write(ImageInput input)
    {
        Check(input, "the input being written");
        return new JsonObject
        {
            ["schema"] = Schema,
            ["digest"] = input.Digest,
            ["repository"] = input.Repository,
            ["builtAt"] = input.BuiltAt,
            ["writer"] = input.Writer,
            ["writtenAt"] = input.WrittenAt,
        }.ToJsonString();
    }

    /// <summary>Reads an input FAILING CLOSED: a value another format wrote, or a field missing or malformed, is refused by name.</summary>
    public static ImageInput Parse(string json, string name)
    {
        JsonObject o;
        try { o = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException($"{name} is not a JSON object."); }
        catch (JsonException ex) { throw new InvalidOperationException($"{name} is not valid JSON: {ex.Message}", ex); }

        if (o["schema"]?.GetValueKind() != JsonValueKind.Number || o["schema"]!.GetValue<int>() != Schema)
            throw new InvalidOperationException($"{name} declares schema {o["schema"]?.ToJsonString() ?? "(none)"}; this lz reads schema {Schema}.");
        foreach (var key in o.Select(p => p.Key))
            if (key is not ("schema" or "digest" or "repository" or "builtAt" or "writer" or "writtenAt"))
                throw new InvalidOperationException($"{name} names '{key}', a field an image input does not have.");

        string Required(string field) => o[field] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)
            ? s : throw new InvalidOperationException($"{name} has no {field}.");
        var builtAt = o["builtAt"] is null ? null
            : o["builtAt"] is JsonValue b && b.TryGetValue<string>(out var t) ? t
            : throw new InvalidOperationException($"{name}'s builtAt is not a string.");

        var input = new ImageInput(Required("digest"), Required("repository"), builtAt, Required("writer"), Required("writtenAt"));
        Check(input, name);
        return input;
    }

    private static void Check(ImageInput input, string what)
    {
        if (!Digest.IsMatch(input.Digest)) throw new InvalidOperationException($"{what}: digest '{input.Digest}' is not a sha256 digest.");
        if (!Repository.IsMatch(input.Repository)) throw new InvalidOperationException($"{what}: repository '{input.Repository}' is not a repository name.");
        if (input.BuiltAt is { } at && DeployOrdering.ParseUtc(at) is null)
            throw new InvalidOperationException($"{what}: builtAt '{at}' is not a UTC time.");
        if (string.IsNullOrWhiteSpace(input.Writer)) throw new InvalidOperationException($"{what}: no writer.");
    }

    /// <summary>
    /// The repository and digest an image reference names, <c>{registry}/{repository}@{digest}</c>; null for a tag, which no input
    /// can name. The registry is the part before the first <c>/</c>.
    /// </summary>
    public static (string Repository, string Digest)? ParseImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        var at = image.LastIndexOf('@');
        var slash = image.IndexOf('/');
        if (at < 0 || slash < 0 || slash > at) return null;
        var digest = image[(at + 1)..];
        var repository = image[(slash + 1)..at];
        return Digest.IsMatch(digest) && Repository.IsMatch(repository) ? (repository, digest) : null;
    }
}

/// <summary>Where the inputs are kept: SSM, behind an interface so the steps are tested without AWS.</summary>
public interface IImageInputs
{
    /// <summary>The input, or null where the parameter does not exist.</summary>
    Task<ImageInput?> ReadAsync(string name);

    /// <summary>Overwrites the input: only a writer holding the service's lease calls it.</summary>
    Task WriteAsync(string name, ImageInput input);

    /// <summary>Creates the input where none exists; false, writing nothing, where one does.</summary>
    Task<bool> CreateAsync(string name, ImageInput input);
}

public sealed class SsmImageInputs(Amazon.SimpleSystemsManagement.IAmazonSimpleSystemsManagement ssm) : IImageInputs
{
    public async Task<ImageInput?> ReadAsync(string name)
    {
        try
        {
            var p = await ssm.GetParameterAsync(new Amazon.SimpleSystemsManagement.Model.GetParameterRequest { Name = name });
            return ImageInputFormat.Parse(p.Parameter.Value, name);
        }
        catch (Amazon.SimpleSystemsManagement.Model.ParameterNotFoundException)
        {
            return null;
        }
    }

    public Task WriteAsync(string name, ImageInput input)
        => ssm.PutParameterAsync(new Amazon.SimpleSystemsManagement.Model.PutParameterRequest
        {
            Name = name, Value = ImageInputFormat.Write(input), Type = Amazon.SimpleSystemsManagement.ParameterType.String, Overwrite = true,
        });

    public async Task<bool> CreateAsync(string name, ImageInput input)
    {
        try
        {
            await ssm.PutParameterAsync(new Amazon.SimpleSystemsManagement.Model.PutParameterRequest
            {
                Name = name, Value = ImageInputFormat.Write(input), Type = Amazon.SimpleSystemsManagement.ParameterType.String, Overwrite = false,
                Description = "lz image input (DecoupledCd P3 J): the digest this service is to run. Written by its image writers under the service's lease; never by Pulumi.",
            });
            return true;
        }
        catch (Amazon.SimpleSystemsManagement.Model.ParameterAlreadyExistsException)
        {
            return false;
        }
    }
}

/// <summary>
/// THE INPUT FROM WHAT THE SERVICE IS SET TO: its current revision's container, read through the deployer's adapters. Used to seed
/// an input and to write one back after a deploy that failed, so the input names what runs rather than what was attempted.
/// </summary>
public static class ImageInputFromService
{
    public static async Task<ImageInput?> ReadAsync(
        IServices services, ITaskDefinitions definitions, string cluster, string service, string container, string writer, DateTimeOffset now)
    {
        var current = await services.DescribeAsync(cluster, service);
        if (current is null) return null;
        var (definition, tags) = await definitions.DescribeAsync(current.TaskDefinitionArn);
        var image = Lz.Aws.Ops.TaskDefinitionRevision.SingleContainerNamed(definition, container).Image;
        var parsed = ImageInputFormat.ParseImage(image)
            ?? throw new InvalidOperationException($"{service}'s current revision runs '{image}', which names no digest; an input names one.");
        var builtAt = tags?.FirstOrDefault(t => t.Key == DeployOrdering.BuiltAtTag)?.Value;
        return new ImageInput(parsed.Digest, parsed.Repository, builtAt is not null && DeployOrdering.ParseUtc(builtAt) is not null ? builtAt : null,
            writer, now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }
}
