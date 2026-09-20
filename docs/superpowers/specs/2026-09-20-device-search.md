# Server-side device search

Approved in conversation: introduce a Device Search subgraph that calls domain subgraph APIs, evaluates the complete filter expression, paginates the combined IDs, and fetches the selected page's matching events. Fusion enriches Device references from Device Directory. The browser sends one FindDevices query and renders the final response.

## Contract

`findDevices(filters: [DeviceSearchFilterInput!]!, first: Int! = 25, offset: Int! = 0): FindDevicesResult`

Input fields: `category: String!`, `key: String!`, `connector: String! = "and"`. Categories: patch, vulnerability, softwareinstall. Key is patch ID, CVE ID, or software `name|version` (name alone means any version). Ordered expression: AND binds tighter than OR, matching the existing UI. Maximum 20 filters. First connector ignored. Reject malformed category/connector/key, invalid first (1..100), and negative offset.

Result: `items: [DeviceSearchItem!]!`, `totalCount: Int!`, `hasNextPage: Boolean!`.
Item: `device: Device!` (ID reference), `events: [DeviceSearchEvent!]!`.
Event: `id: ID!`, `source: String!`, `itemKey: String!`, `occurredAt: DateTime!`, `label: String!`, `title: String!`, `subtitle: String!`, `status: String!`, `severity: String`.

Server orders IDs ordinally, evaluates complete sets before offset/first, fetches event details only for final page (at most 100 IDs). Offset is retained for existing URL/page controls; each request is a fresh read, not a multi-service snapshot. No cursor/snapshot cache in this change.

## Ownership and correctness

DeviceSearch calls fixed configured domain API URLs directly, forwarding the caller JWT per request. It never reads domain databases or recursively calls the gateway. No cross-request auth state or cached tenant data. Validate all requested service permissions before querying. Required-source denied, failed, truncated, or malformed responses fail the entire search with a GraphQL error, not a misleading zero/partial total.

Retrieve complete per-filter IDs via existing paginated reverse lookup `items { device { id } } totalCount` if needed, avoiding capped matches. Defensive result/work limits must throw explicit errors instead of truncating. Parallelize independent categories/filters with bounded concurrency. Match semantics remain historical event/finding presence, as in existing reverse lookup APIs.

Domain resolvers skip event storage reads when ID-only discovery does not select events (including aliases/fragments).

Details normalize selected source events to DeviceSearchEvent, with software keys respecting any-version selection. Only source-local details requested; Device Directory properties are resolved by Fusion. UI source failures become one search error with Retry; catalogs remain independent.

## Delivery

Work in the current SoR checkout because it contains the user's uncommitted reverse search implementation. Preserve those changes; no commit/reset/stash. Add service project, tests, Docker service, schemas/settings/contracts, composition inputs, gateway transport config, UI query/mapping/tests, mock/demo support, and documentation. Validate focused tests, gateway composition/enrichment, UI suite/build, schema drift, and live Compose query if available.
