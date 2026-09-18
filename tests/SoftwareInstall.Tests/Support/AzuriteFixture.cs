using System.Diagnostics;
using System.Net;
using Azure.Storage.Blobs;
using Testcontainers.Azurite;

namespace SoR.SoftwareInstall.Tests.Support;

[CollectionDefinition(Name)]
public sealed class AzuriteCollection : ICollectionFixture<AzuriteFixture>
{
    public const string Name = "Azurite";
}

/// <summary>
/// One Azurite per test run (the compose image, with compose's <c>--skipApiVersionCheck</c>) and one shared app on
/// container <c>install-events-test</c>, seeded once in <see cref="InitializeAsync"/> (12 000 PUTs is the slow
/// part). Every integration class shares it through the collection, so the tests run one after another; a test
/// that changes blobs puts them back.
/// </summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/azure-storage/azurite:latest";
    public const string ContainerName = "install-events-test";

    private readonly AzuriteContainer _azurite = new AzuriteBuilder(Image)
        .WithInMemoryPersistence()
        .WithCommand("--skipApiVersionCheck")
        .Build();

    private SoftwareInstallApp? _app;

    /// <summary>Explicit <c>BlobEndpoint=http://127.0.0.1:{port}/devstoreaccount1</c> form, like compose.</summary>
    public string ConnectionString => _azurite.GetConnectionString();

    public BlobContainerClient Container => new(ConnectionString, ContainerName);

    public SoftwareInstallApp App => _app ?? throw new InvalidOperationException("fixture not initialised");

    /// <summary>Every <c>/health</c> status the shared app returned from its cold start until the first 200.</summary>
    public IReadOnlyList<HttpStatusCode> ColdStartHealth { get; private set; } = [];

    /// <summary>Wall time from app start to <c>/health</c> 200 on an empty container (seed included).</summary>
    public TimeSpan ColdStartDuration { get; private set; }

    public async Task InitializeAsync()
    {
        await _azurite.StartAsync();
        var sw = Stopwatch.StartNew();
        _app = NewApp();
        ColdStartHealth = await _app.WaitUntilHealthyAsync();
        ColdStartDuration = sw.Elapsed;
    }

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        await _azurite.DisposeAsync();
    }

    /// <summary>Another app instance on the same Azurite (the default container unless told otherwise).</summary>
    public SoftwareInstallApp NewApp(string container = ContainerName, bool withSigningKey = true, string? connectionString = null) =>
        new(connectionString ?? ConnectionString, container, withSigningKey ? Tokens.SigningKey : null);
}
