using HotChocolate.Fusion.Packaging;
using HotChocolate.Buffers;
using HotChocolate.Language;
using System.Security.Cryptography;
using System.Text.Json;

namespace SoR.Gateway;

/// <summary>
/// Startup check of the composed archive. Fusion's file-system configuration does not fail on a bad archive: it
/// watches the path and waits for a usable one, so a missing or corrupt <c>gateway.far</c> would leave the gateway
/// starting forever (Kestrel never listens). Checked here instead, so the process exits with a clear message.
/// </summary>
public static class GatewayArchive
{
    public static async Task<GatewayArchiveSnapshot> LoadSnapshotAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Composed gateway archive not found at {path}. Run scripts/compose-schema.sh or set {GatewaySettings.ArchiveVariable}.", path);
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            var schemaHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            using var stream = new MemoryStream(bytes, writable: false);
            using var archive = FusionArchive.Open(stream, FusionArchiveMode.Read, leaveOpen: false, new FusionArchiveOptions());
            var format = await archive.GetLatestSupportedGatewayFormatAsync(cancellationToken);
            using var configuration = await archive.TryGetGatewayConfigurationAsync(format, cancellationToken);
            if (configuration is null)
            {
                throw new InvalidDataException($"no gateway configuration for format {format}");
            }
            await using var schemaStream = await configuration.OpenReadSchemaAsync(cancellationToken);
            using var reader = new StreamReader(schemaStream);
            var schema = Utf8GraphQLParser.Parse(await reader.ReadToEndAsync(cancellationToken));
            var names = (await archive.GetSourceSchemaNamesAsync(cancellationToken)).ToArray();
            if (names.Length == 0 || names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
                throw new InvalidDataException("archive sourceSchemas must be non-empty and unique");
            var settings = new JsonDocumentOwner(JsonDocument.Parse(configuration.Settings.RootElement.GetRawText()));
            return new(schema, settings, names, schemaHash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException(
                $"{path} is not a usable Fusion archive ({ex.Message}). Recompose it with scripts/compose-schema.sh.", ex);
        }
    }
}

public sealed record GatewayArchiveSnapshot(
    DocumentNode Schema,
    JsonDocumentOwner Settings,
    IReadOnlyList<string> SourceSchemaNames,
    string SchemaHash);
