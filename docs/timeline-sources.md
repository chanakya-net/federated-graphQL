# Timeline source deployment

Each compatible domain owns a `src/<Domain>/timeline.json` descriptor and exports a nullable `Device` field with
the normalized timeline contract. `scripts/compose-schema.sh` validates every descriptor against exported SDL,
composes `gateway/gateway.far`, then writes `gateway/timeline-sources.json` with the FAR's SHA-256 hash. Both files
are one deployment unit.

For example:

```json
{
  "id": "compliance",
  "field": "complianceTimeline",
  "name": "Compliance",
  "icon": "verified",
  "color": "#7c3aed",
  "history": "compliance",
  "statuses": ["PASS", "FAIL"],
  "contractVersion": 1
}
```

The exported schema must declare this common shape. `Device.<field>` and both date arguments are nullable, while
the item and detail fields retain the nullability shown here. Every source uses identical, shareable output types.

```graphql
type Device {
  complianceTimeline(since: DateTime, until: DateTime): [TimelineEvent!]
}

type TimelineEvent @shareable {
  id: ID!
  occurredAt: DateTime!
  label: String!
  title: String!
  subtitle: String!
  status: String!
  severity: String
  details: [TimelineDetail!]!
}

type TimelineDetail @shareable {
  label: String!
  value: String!
  mono: Boolean!
}
```

The gateway validates both artifacts before listening and keeps that catalog snapshot for the process lifetime.
Deploy a changed FAR and catalog together, then restart the gateway. In-process FAR or catalog replacement is not
supported. `GET /timeline-sources` is public presentation/schema metadata and always returns `Cache-Control:
no-store`; it contains no tenant data and grants no permission.

Gateway clients are discovered from the FAR. Configure each source schema with
`SUBGRAPH_<SOURCE_SCHEMA_NAME_UPPER>_URL`. Its timeout defaults to `SUBGRAPH_TIMEOUT_SECONDS` and can be overridden
with `SUBGRAPH_<SOURCE_SCHEMA_NAME_UPPER>_TIMEOUT_SECONDS`. Authorization is forwarded unchanged for every client.
If any FAR source has no URL, gateway startup fails with the missing variable name, so no source can fall through
to Fusion's anonymous default transport.

To deploy a source:

1. Add its source project settings/schema export, normalized resolver, and `timeline.json` descriptor.
2. Set its gateway URL and optional timeout variables.
3. Run `scripts/compose-schema.sh`; review the exported SDL, FAR, and generated catalog together.
4. Deploy both gateway artifacts and restart the gateway. The validated schema, source registrations, and catalog
   stay fixed for that process lifetime.
5. Navigate to the timeline or press Retry so the UI reloads `/timeline-sources` and rebuilds its query.
