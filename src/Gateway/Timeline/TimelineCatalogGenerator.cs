using System.Text.Json;
using HotChocolate.Language;

namespace SoR.Gateway.Timeline;

public static class TimelineCatalogGenerator
{
    private static readonly IReadOnlyDictionary<string, string> EventFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["id"] = "ID!",
        ["occurredAt"] = "DateTime!",
        ["label"] = "String!",
        ["title"] = "String!",
        ["subtitle"] = "String!",
        ["status"] = "String!",
        ["severity"] = "String",
        ["details"] = "[TimelineDetail!]!",
    };

    private static readonly IReadOnlyDictionary<string, string> DetailFields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["label"] = "String!",
        ["value"] = "String!",
        ["mono"] = "Boolean!",
    };

    public static IReadOnlyList<SourceProject> SourceProjects(string settingsDirectory) =>
        Directory.EnumerateFiles(settingsDirectory, "*-settings.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                var project = json.RootElement.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(project)) throw new InvalidDataException($"{path}: name is required");
                var file = Path.GetFileName(path);
                return new SourceProject(file[..^"-settings.json".Length], project);
            })
            .ToArray();

    public static TimelineSourceCatalog GenerateFromDirectories(
        string archivePath,
        string outputPath,
        string sourceRoot,
        string schemaDirectory)
    {
        var descriptors = Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly)
            .Select(directory => Path.Combine(directory, "timeline.json"))
            .Where(File.Exists)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var schemas = SourceProjects(schemaDirectory)
            .Select(source => Path.Combine(schemaDirectory, source.SchemaBaseName + ".graphqls"))
            .ToArray();
        return Generate(archivePath, outputPath, descriptors, schemas);
    }

    public static TimelineSourceCatalog Generate(
        string archivePath,
        string outputPath,
        IEnumerable<string> descriptorPaths,
        IEnumerable<string> schemaPaths)
    {
        var schemas = schemaPaths.Select(path => new ParsedSchema(path, Utf8GraphQLParser.Parse(File.ReadAllText(path)))).ToArray();
        var sources = descriptorPaths.Select(ReadDescriptor).ToArray();
        var catalog = new TimelineSourceCatalog(1, TimelineSourceCatalogLoader.HashFile(archivePath), sources);
        TimelineSourceCatalogLoader.Validate(catalog, outputPath);

        foreach (var source in sources)
        {
            var declarations = schemas
                .Where(s => DeviceFields(s.Document).Any(f => f.Name.Value == source.Field))
                .ToArray();
            if (declarations.Length != 1)
            {
                var inspected = string.Join(", ", schemas.Select(s => s.Path));
                throw new InvalidDataException(
                    $"Timeline field Device.{source.Field} from descriptor '{source.Id}' must be declared by exactly one exported schema; " +
                    $"found {declarations.Length}. Inspected: {inspected}");
            }

            ValidateTimelineSchema(declarations[0].Document, declarations[0].Path, source.Field);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (directory is not null) Directory.CreateDirectory(directory);
        var temp = outputPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(catalog, TimelineSourceCatalog.JsonOptions) + "\n");
            File.Move(temp, outputPath, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        return catalog;
    }

    private static TimelineSourceDescriptor ReadDescriptor(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<TimelineSourceDescriptor>(File.ReadAllBytes(path), TimelineSourceCatalog.JsonOptions)
                ?? throw new InvalidDataException("descriptor is JSON null");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException($"{path} is not a valid timeline source descriptor ({ex.Message}).", ex);
        }
    }

    internal static void ValidateTimelineSchema(DocumentNode schema, string path, string fieldName)
    {
        var fields = DeviceFields(schema).Where(f => f.Name.Value == fieldName).ToArray();
        if (fields.Length != 1)
            throw new InvalidDataException($"{path}: Device.{fieldName} must be declared exactly once, found {fields.Length}");
        var field = fields[0];
        if (field.Type.ToString() != "[TimelineEvent!]")
            throw new InvalidDataException($"{path}: Device.{fieldName} must return [TimelineEvent!], got {field.Type}");

        var arguments = field.Arguments.ToDictionary(a => a.Name.Value, StringComparer.Ordinal);
        if (arguments.Count != 2
            || !arguments.TryGetValue("since", out var since) || since.Type.ToString() != "DateTime" || since.DefaultValue is not null
            || !arguments.TryGetValue("until", out var until) || until.Type.ToString() != "DateTime" || until.DefaultValue is not null)
        {
            throw new InvalidDataException($"{path}: Device.{fieldName} arguments must be (since: DateTime, until: DateTime)");
        }

        ValidateObject(schema, path, "TimelineEvent", EventFields);
        ValidateObject(schema, path, "TimelineDetail", DetailFields);
    }

    private static void ValidateObject(DocumentNode schema, string path, string typeName, IReadOnlyDictionary<string, string> expected)
    {
        var fields = ObjectFields(schema, typeName).ToArray();
        if (fields.Length == 0) throw new InvalidDataException($"{path}: type {typeName} is missing");
        var actual = fields.ToDictionary(f => f.Name.Value, f => f.Type.ToString(), StringComparer.Ordinal);
        if (actual.Count != expected.Count || expected.Any(e => !actual.TryGetValue(e.Key, out var type) || type != e.Value))
        {
            throw new InvalidDataException(
                $"{path}: type {typeName} must define exactly " +
                string.Join(", ", expected.Select(e => $"{e.Key}: {e.Value}")));
        }
    }

    private static IEnumerable<FieldDefinitionNode> DeviceFields(DocumentNode document) => ObjectFields(document, "Device");

    private static IEnumerable<FieldDefinitionNode> ObjectFields(DocumentNode document, string typeName) =>
        document.Definitions.SelectMany(definition => definition switch
        {
            ObjectTypeDefinitionNode node when node.Name.Value == typeName => node.Fields,
            ObjectTypeExtensionNode node when node.Name.Value == typeName => node.Fields,
            _ => [],
        });

    private sealed record ParsedSchema(string Path, DocumentNode Document);
}

public sealed record SourceProject(string SchemaBaseName, string ProjectName);
