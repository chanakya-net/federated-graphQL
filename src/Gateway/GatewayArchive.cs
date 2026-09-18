using HotChocolate.Fusion.Packaging;

namespace SoR.Gateway;

/// <summary>
/// Startup check of the composed archive. Fusion's file-system configuration does not fail on a bad archive: it
/// watches the path and waits for a usable one, so a missing or corrupt <c>gateway.far</c> would leave the gateway
/// starting forever (Kestrel never listens). Checked here instead, so the process exits with a clear message.
/// </summary>
public static class GatewayArchive
{
    public static async Task EnsureUsableAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Composed gateway archive not found at {path}. Run scripts/compose-schema.sh or set {GatewaySettings.ArchiveVariable}.", path);
        }

        try
        {
            using var archive = FusionArchive.Open(path, FusionArchiveMode.Read);
            var format = await archive.GetLatestSupportedGatewayFormatAsync(cancellationToken);
            using var configuration = await archive.TryGetGatewayConfigurationAsync(format, cancellationToken);
            if (configuration is null)
            {
                throw new InvalidDataException($"no gateway configuration for format {format}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidDataException(
                $"{path} is not a usable Fusion archive ({ex.Message}). Recompose it with scripts/compose-schema.sh.", ex);
        }
    }
}
