using SoR.Shared.Seeding;
using SoR.SoftwareInstall.GraphQL;
using SoR.SoftwareInstall.Storage;

namespace SoR.SoftwareInstall.Seeding;

public sealed record CatalogProduct(string Name, string Publisher, IReadOnlyList<string> Versions);

/// <summary>
/// Pure, deterministic seed data (contracts/seeding.md): everything derives from <see cref="DeterministicRandom"/>
/// and <see cref="SeedConstants.Epoch"/>. No wall clock, no Guid, no unseeded Random.
/// </summary>
public static class InstallSeedData
{
    public const int CatalogSize = 150;
    public const int MinEvents = 3;
    public const int MaxEvents = 20;
    public const string Domain = "softwareinstall";
    public const string CatalogDomain = "softwareinstall-catalog";

    /// <summary>Real product / publisher pairs. Past the 40th product the names repeat with a suffix index.</summary>
    private static readonly (string Name, string Publisher)[] Products =
    [
        ("7-Zip", "Igor Pavlov"),
        ("Google Chrome", "Google LLC"),
        ("Mozilla Firefox", "Mozilla"),
        ("Microsoft Edge", "Microsoft Corporation"),
        ("Visual Studio Code", "Microsoft Corporation"),
        ("Microsoft Teams", "Microsoft Corporation"),
        ("Microsoft 365 Apps", "Microsoft Corporation"),
        ("PowerShell", "Microsoft Corporation"),
        ("Notepad++", "Notepad++ Team"),
        ("Git", "The Git Development Community"),
        ("Node.js", "OpenJS Foundation"),
        ("Python", "Python Software Foundation"),
        ("Adobe Acrobat Reader", "Adobe Inc."),
        ("Zoom Workplace", "Zoom Video Communications"),
        ("Slack", "Slack Technologies"),
        ("VLC media player", "VideoLAN"),
        ("PuTTY", "Simon Tatham"),
        ("WinSCP", "Martin Prikryl"),
        ("FileZilla Client", "Tim Kosse"),
        ("Wireshark", "The Wireshark developer community"),
        ("Docker Desktop", "Docker Inc."),
        ("Postman", "Postman, Inc."),
        ("JetBrains Rider", "JetBrains s.r.o."),
        ("IntelliJ IDEA", "JetBrains s.r.o."),
        ("Eclipse Temurin JDK", "Eclipse Adoptium"),
        ("OpenSSL", "OpenSSL Software Foundation"),
        ("KeePassXC", "KeePassXC Team"),
        ("Greenshot", "Greenshot"),
        ("GIMP", "The GIMP Team"),
        ("LibreOffice", "The Document Foundation"),
        ("Audacity", "Audacity Team"),
        ("OBS Studio", "OBS Project"),
        ("Cisco Secure Client", "Cisco Systems, Inc."),
        ("CrowdStrike Falcon Sensor", "CrowdStrike, Inc."),
        ("Tenable Nessus Agent", "Tenable, Inc."),
        ("Splunk Universal Forwarder", "Splunk Inc."),
        ("Zabbix Agent", "Zabbix SIA"),
        ("TeamViewer", "TeamViewer Germany GmbH"),
        ("Citrix Workspace", "Cloud Software Group"),
        ("Oracle VirtualBox", "Oracle Corporation"),
    ];

    private static readonly Lazy<IReadOnlyList<CatalogProduct>> CatalogInstance = new(BuildCatalog);

    public static IReadOnlyList<CatalogProduct> Catalog => CatalogInstance.Value;

    /// <summary>150 products, each with 3..6 strictly increasing <c>{major}.{minor}.{patch}</c> versions.</summary>
    public static IReadOnlyList<CatalogProduct> BuildCatalog()
    {
        var rng = DeterministicRandom.For(0, CatalogDomain);
        var catalog = new List<CatalogProduct>(CatalogSize);
        for (var i = 0; i < CatalogSize; i++)
        {
            var (name, publisher) = Products[i % Products.Length];
            var round = i / Products.Length;
            if (round > 0) name = $"{name} {round + 1}";

            var count = rng.Next(3, 7);
            int major = rng.Next(1, 25), minor = rng.Next(0, 10), patch = rng.Next(0, 20);
            var versions = new List<string>(count);
            for (var v = 0; v < count; v++)
            {
                if (v > 0)
                {
                    // Every step increases the version: a patch, minor or major bump.
                    switch (rng.Next(0, 10))
                    {
                        case < 6: patch += rng.Next(1, 5); break;
                        case < 9: minor++; patch = 0; break;
                        default: major++; minor = 0; patch = 0; break;
                    }
                }

                versions.Add($"{major}.{minor}.{patch}");
            }

            catalog.Add(new CatalogProduct(name, publisher, versions));
        }

        return catalog;
    }

    /// <summary>3..20 events for the device, ascending by <c>occurredAt</c>, ids <c>{deviceId}-i{n:D3}</c>.</summary>
    public static IReadOnlyList<StoredInstallEvent> BuildEventsFor(SeedDevice device)
    {
        var catalog = Catalog;
        var rng = DeterministicRandom.For(device.Index, Domain);
        var count = rng.Next(MinEvents, MaxEvents + 1);
        var events = new List<StoredInstallEvent>(count);
        for (var n = 0; n < count; n++)
        {
            // Draw order is part of the contract (contracts/seeding.md): product, action, version, result, time.
            var product = catalog[rng.Next(0, catalog.Count)];
            var action = rng.NextDouble() switch
            {
                < 0.55 => InstallAction.Install,
                < 0.85 => InstallAction.Upgrade,
                _ => InstallAction.Uninstall,
            };
            var version = product.Versions[rng.Next(0, product.Versions.Count)];
            var result = rng.NextDouble() < 0.92 ? InstallResult.Success : InstallResult.Failed;
            var occurredAt = DeterministicRandom.InstantInWindow(rng);
            events.Add(new StoredInstallEvent(
                $"{device.Id}-i{n:D3}", occurredAt, action, result, new Software(product.Name, version, product.Publisher)));
        }

        // Stable sort: equal timestamps keep generation order, so the blob bytes are deterministic.
        return [.. events.OrderBy(e => e.OccurredAt)];
    }

    public static DeviceInstallDocument BuildDocument(SeedDevice device) =>
        new(DeviceInstallDocument.CurrentSchemaVersion, device.TenantId, device.Id, BuildEventsFor(device));
}
