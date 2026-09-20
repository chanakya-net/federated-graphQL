# Extensible device timeline

Approved in conversation: one-time generic UI; future compatible subgraphs require only server deployment, source metadata, and FAR composition. Keep Fusion routing and client-side merge. Remove obsolete timeline/domain UI branches and genuinely unused code; preserve domain APIs still used by search or external contracts.

## Wire contract

Each timeline source has a unique nullable Device field accepting `since: DateTime` and `until: DateTime`, returning `[TimelineEvent!]`. Fields: `patchTimeline`, `vulnerabilityTimeline`, `softwareInstallTimeline`.

`TimelineEvent`: `id: ID!`, `occurredAt: DateTime!`, `label: String!`, `title: String!`, `subtitle: String!`, `status: String!`, `severity: String`, `details: [TimelineDetail!]!`. `TimelineDetail`: `label: String!`, `value: String!`, `mono: Boolean!`. Shared output objects must compose safely across sources. Domain authorization and tenant filtering apply equally to normalized and existing event fields.

GET `/timeline-sources` returns `{version: 1, schemaHash: string, sources: [{id, field, name, icon, color, history, statuses: string[], contractVersion: 1}]}`. Schema hash is SHA256 of the paired FAR. Sources originate in subgraph-owned `timeline.json` descriptors, validated against exported SDL during composition; resulting `gateway/timeline-sources.json` deployed beside FAR. Do not infer arbitrary fields as timelines. Metadata is public presentation/schema information, contains no tenant data, and grants no permission. Cache-Control no-store; UI reloads on navigation and Retry, including user changes.

Gateway validates matching FAR/catalog at startup and serves the validated snapshot. Server updates use a coordinated gateway restart/deployment with FAR and catalog; live in-process arbitrary FAR replacement is not promised. Configuration-driven source clients retain forwarded authorization and bounded timeouts for every configured source. Unknown/unconfigured archive sources must not silently call anonymously.

UI validates catalog version, source uniqueness, GraphQL field syntax, supported contract, safe presentation values; builds an AST query with identical event selection for each descriptor. No source-specific switches, raw domain types, status allowlists, or static source metadata. Filters/legend/details derive from catalog/events. Source IDs are strings. Preserve denied/unavailable/empty distinction and device-level errors. Unsupported contracts fail visibly. Empty catalog still displays device identity without unused variables in GraphQL query.

## Acceptance

A synthetic fourth source in metadata must query/render/filter/select/show details without UI production edits. Addition/removal via refreshed metadata must rebuild query. Denied/unavailable new source preserves other results. Source-aware event keys prevent cross-domain ID collisions. Tenant/auth behavior and date filters remain correct. Existing domain API contract tests remain valid. Composition validates declared fields and artifact pairing. Configuration supports a new source without editing gateway C#.
