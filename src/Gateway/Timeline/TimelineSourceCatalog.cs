using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using HotChocolate.Language;

namespace SoR.Gateway.Timeline;

public sealed record TimelineSourceDescriptor(
    string Id,
    string Field,
    string Name,
    string Icon,
    string Color,
    string History,
    IReadOnlyList<string> Statuses,
    int ContractVersion);

public sealed record TimelineSourceCatalog(int Version, string SchemaHash, IReadOnlyList<TimelineSourceDescriptor> Sources)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

public sealed record TimelineSourceCatalogSnapshot(TimelineSourceCatalog Catalog, byte[] Json);

public static partial class TimelineSourceCatalogLoader
{
    private static readonly Regex HashPattern = Sha256Pattern();
    private static readonly Regex FieldPattern = GraphQlNamePattern();
    private static readonly Regex IdPattern = SourceIdPattern();
    private static readonly Regex IconPattern = MaterialIconPattern();
    private static readonly Regex ColorPattern = HexColorPattern();

    public static TimelineSourceCatalogSnapshot Load(
        string catalogPath,
        string expectedSchemaHash,
        string archiveDisplayPath,
        DocumentNode archiveSchema)
    {
        if (!File.Exists(catalogPath))
        {
            throw new FileNotFoundException(
                $"Timeline source catalog not found at {catalogPath}. Run scripts/compose-schema.sh or set {GatewaySettings.CatalogVariable}.",
                catalogPath);
        }

        byte[] json;
        TimelineSourceCatalog catalog;
        try
        {
            json = File.ReadAllBytes(catalogPath);
            catalog = JsonSerializer.Deserialize<TimelineSourceCatalog>(json, TimelineSourceCatalog.JsonOptions)
                ?? throw new InvalidDataException("catalog is JSON null");
            Validate(catalog, catalogPath);
            foreach (var source in catalog.Sources)
                TimelineCatalogGenerator.ValidateTimelineSchema(archiveSchema, archiveDisplayPath, source.Field);
        }
        catch (Exception ex) when (ex is not FileNotFoundException and not OperationCanceledException)
        {
            throw new InvalidDataException($"{catalogPath} is not a valid timeline source catalog ({ex.Message}).", ex);
        }

        if (!string.Equals(catalog.SchemaHash, expectedSchemaHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Timeline source catalog {catalogPath} does not match archive {archiveDisplayPath}: catalog schemaHash " +
                $"{catalog.SchemaHash}, loaded archive SHA-256 {expectedSchemaHash}. Recompose both artifacts with scripts/compose-schema.sh.");
        }

        return new(catalog, json);
    }

    internal static void Validate(TimelineSourceCatalog catalog, string path)
    {
        if (catalog.Version != 1) throw new InvalidDataException($"{path}: version must be 1");
        if (!HashPattern.IsMatch(catalog.SchemaHash)) throw new InvalidDataException($"{path}: schemaHash must be a lowercase SHA-256 hex digest");
        if (catalog.Sources is null) throw new InvalidDataException($"{path}: sources is required");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in catalog.Sources)
        {
            if (!IdPattern.IsMatch(source.Id)) throw new InvalidDataException($"{path}: invalid source id '{source.Id}'");
            if (!ids.Add(source.Id)) throw new InvalidDataException($"{path}: duplicate source id '{source.Id}'");
            if (!FieldPattern.IsMatch(source.Field)) throw new InvalidDataException($"{path}: invalid GraphQL field '{source.Field}'");
            if (!fields.Add(source.Field)) throw new InvalidDataException($"{path}: duplicate timeline field '{source.Field}'");
            if (string.IsNullOrWhiteSpace(source.Name)) throw new InvalidDataException($"{path}: {source.Id}.name is required");
            if (!IconPattern.IsMatch(source.Icon)) throw new InvalidDataException($"{path}: {source.Id}.icon is invalid");
            if (!ColorPattern.IsMatch(source.Color)) throw new InvalidDataException($"{path}: {source.Id}.color must be a six-digit hex color");
            if (string.IsNullOrWhiteSpace(source.History)) throw new InvalidDataException($"{path}: {source.Id}.history is required");
            if (source.ContractVersion != 1) throw new InvalidDataException($"{path}: {source.Id}.contractVersion must be 1");
            if (source.Statuses is null) throw new InvalidDataException($"{path}: {source.Id}.statuses is required");
            if (source.Statuses.Any(string.IsNullOrWhiteSpace)) throw new InvalidDataException($"{path}: {source.Id}.statuses contains an empty value");
            if (source.Statuses.Distinct(StringComparer.Ordinal).Count() != source.Statuses.Count)
                throw new InvalidDataException($"{path}: {source.Id}.statuses contains duplicates");
        }
    }

    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();

    [GeneratedRegex("^[_A-Za-z][_0-9A-Za-z]*$", RegexOptions.CultureInvariant)]
    private static partial Regex GraphQlNamePattern();

    [GeneratedRegex("^[a-z][a-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceIdPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex MaterialIconPattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColorPattern();
}
