using System.Text.Json;
using SoR.DeviceSearch.GraphQL;
using SoR.DeviceSearch.Search;
using static SoR.DeviceSearch.Providers.SearchResponse;

namespace SoR.DeviceSearch.Providers;

public abstract class CatalogSearchProvider : ISearchProvider
{
    public abstract string Category { get; }
    public abstract string RequiredPermission { get; }
    protected abstract string EndpointSetting { get; }
    protected abstract string SearchRoot { get; }
    protected abstract string SelectionArgument { get; }
    protected virtual string SelectionType => "ID!";
    protected abstract string EventFields { get; }
    protected abstract string CatalogRoot { get; }
    protected abstract string CatalogFields { get; }
    public abstract SearchCapability Capability(bool available);
    public virtual string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512 || key.Any(char.IsControl))
            throw DeviceSearchEngine.Error("BAD_USER_INPUT", "Each filter needs a nonempty key of at most 512 characters without control characters.");
        return key.Trim();
    }
    protected virtual object Selection(string key) => new[] { key };
    public SearchSourceRequest SearchRequest(string key, IReadOnlyList<string>? ids, int first, int offset, bool details)
    {
        var eventSelection = details ? $"events {{ id occurredAt {EventFields} }}" : "";
        var query = $"query DomainDeviceSearch($selection: [{SelectionType}]!, $deviceIds: [ID!], $first: Int!, $offset: Int!) {{ result: {SearchRoot}({SelectionArgument}: $selection, deviceIds: $deviceIds, first: $first, offset: $offset) {{ totalCount items {{ device {{ id }} {eventSelection} }} }} }}";
        return new(EndpointSetting, query, new { selection = Selection(key), deviceIds = ids, first, offset });
    }
    public SearchSourceRequest CatalogRequest(string? search, int first) => new(EndpointSetting,
        $"query SearchCatalog($search: String, $first: Int!) {{ result: {CatalogRoot}(search: $search, first: $first) {{ {CatalogFields} }} }}", new { search, first });
    public abstract DeviceSearchEvent MapEvent(string key, JsonElement source);
    public abstract IEnumerable<SearchCatalogItem> MapCatalogItem(JsonElement source);
}

public sealed class PatchSearchProvider : CatalogSearchProvider
{
    public override string Category => "patch";
    public override string RequiredPermission => "patch";
    protected override string EndpointSetting => "SUBGRAPH_PATCH_URL";
    protected override string SearchRoot => "devicesWithPatches";
    protected override string SelectionArgument => "patchIds";
    protected override string EventFields => "status patch { id kbId title severity vendor }";
    protected override string CatalogRoot => "patches";
    protected override string CatalogFields => "id kbId title severity vendor";
    public override SearchCapability Capability(bool available) => new(Category, "Patches", "build_circle", "#2563eb", "Search patches…", "catalog", available);
    public override DeviceSearchEvent MapEvent(string key, JsonElement source)
    {
        var patch = Property(source, "patch");
        if (Text(patch, "id") != key) throw Malformed(Category, "unselected patch event");
        return new(Text(source, "id"), Category, key, Timestamp(source), Text(patch, "kbId"), Text(patch, "title"), Text(patch, "vendor"), Text(source, "status"), Text(patch, "severity"));
    }
    public override IEnumerable<SearchCatalogItem> MapCatalogItem(JsonElement source) =>
        [new(Text(source, "id"), Text(source, "kbId"), Text(source, "title"))];
}

public sealed class VulnerabilitySearchProvider : CatalogSearchProvider
{
    public override string Category => "vulnerability";
    public override string RequiredPermission => "vulnerability";
    protected override string EndpointSetting => "SUBGRAPH_VULNERABILITY_URL";
    protected override string SearchRoot => "devicesWithCves";
    protected override string SelectionArgument => "cveIds";
    protected override string EventFields => "kind findingState findingId cve { id title severity cvssScore }";
    protected override string CatalogRoot => "cves";
    protected override string CatalogFields => "id title severity";
    public override SearchCapability Capability(bool available) => new(Category, "Vulnerabilities", "security", "#dc2626", "Search CVEs…", "catalog", available);
    public override DeviceSearchEvent MapEvent(string key, JsonElement source)
    {
        var cve = Property(source, "cve");
        if (Text(cve, "id") != key) throw Malformed(Category, "unselected CVE event");
        return new(Text(source, "id"), Category, key, Timestamp(source), key, Text(cve, "title"), $"{Text(source, "kind")} · {Text(source, "findingId")}", Text(source, "findingState"), Text(cve, "severity"));
    }
    public override IEnumerable<SearchCatalogItem> MapCatalogItem(JsonElement source) =>
        [new(Text(source, "id"), Text(source, "id"), Text(source, "title"))];
}

public sealed class SoftwareInstallSearchProvider : CatalogSearchProvider
{
    public override string Category => "softwareinstall";
    public override string RequiredPermission => "softwareinstall";
    protected override string EndpointSetting => "SUBGRAPH_SOFTWAREINSTALL_URL";
    protected override string SearchRoot => "devicesWithSoftware";
    protected override string SelectionArgument => "software";
    protected override string SelectionType => "SoftwareKeyInput!";
    protected override string EventFields => "action result software { name version publisher }";
    protected override string CatalogRoot => "software";
    protected override string CatalogFields => "name version publisher";
    public override SearchCapability Capability(bool available) => new(Category, "Software", "apps", "#7c3aed", "Search software…", "catalog", available);
    public override string NormalizeKey(string key)
    {
        var parts = base.NormalizeKey(key).Split('|');
        if (parts.Length > 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw DeviceSearchEngine.Error("BAD_USER_INPUT", "Software keys must be name or name|version with nonempty components.");
        return string.Join('|', parts.Select(p => p.Trim()));
    }
    protected override object Selection(string key)
    {
        var parts = key.Split('|');
        return new[] { new { name = parts[0], version = parts.Length == 2 ? parts[1] : null } };
    }
    public override DeviceSearchEvent MapEvent(string key, JsonElement source)
    {
        var software = Property(source, "software");
        var name = Text(software, "name");
        var version = Text(software, "version");
        var parts = key.Split('|');
        if (name != parts[0] || (parts.Length == 2 && version != parts[1])) throw Malformed(Category, "unselected software event");
        return new(Text(source, "id"), Category, key, Timestamp(source), name, $"{name} {version}", $"{Text(source, "action")} · {Text(software, "publisher")}", Text(source, "result"), null);
    }
    public override IEnumerable<SearchCatalogItem> MapCatalogItem(JsonElement source)
    {
        var name = Text(source, "name");
        var version = Text(source, "version");
        var publisher = Text(source, "publisher");
        return [new(name, name, $"Any version · {publisher}"), new($"{name}|{version}", $"{name} {version}", publisher)];
    }
}
