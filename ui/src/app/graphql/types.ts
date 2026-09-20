// Hand-written result types for the operations in operations.ts (schemas/*.graphqls).
// `DateTime` values arrive as ISO-8601 strings.

export type Severity = 'CRITICAL' | 'HIGH' | 'MEDIUM' | 'LOW';
export type PatchStatus = 'APPLIED' | 'FAILED' | 'PENDING';
export type VulnerabilityEventKind = 'DETECTED' | 'REMEDIATED';
export type FindingState = 'OPEN' | 'REMEDIATED';
export type InstallAction = 'INSTALL' | 'UNINSTALL' | 'UPGRADE';
export type InstallResult = 'SUCCESS' | 'FAILED';

export interface Device {
  id: string;
  hostname: string;
  os: string;
  ipAddress: string;
  lastSeenAt: string;
  tenantId: string;
}

export interface DeviceSearchData {
  devices: { totalCount: number; items: Device[] };
}

export interface DeviceSearchVars {
  search: string | null;
  first: number;
  offset: number;
}

export interface PatchEvent {
  id: string;
  occurredAt: string;
  status: PatchStatus;
  patch: { id: string; kbId: string; title: string; severity: Severity; vendor: string };
}

export interface VulnerabilityEvent {
  id: string;
  occurredAt: string;
  kind: VulnerabilityEventKind;
  findingState: FindingState;
  findingId: string;
  cve: { id: string; title: string; cvssScore: number; severity: Severity };
}

export interface InstallEvent {
  id: string;
  occurredAt: string;
  action: InstallAction;
  result: InstallResult;
  software: { name: string; version: string; publisher: string };
}

/** The three extension fields are nullable lists: `null` means degraded or denied, `[]` means no events. */
export interface TimelineDevice extends Device {
  patchEvents: PatchEvent[] | null;
  vulnerabilityEvents: VulnerabilityEvent[] | null;
  installEvents: InstallEvent[] | null;
}

export interface DeviceTimelineData {
  device: TimelineDevice | null;
}

export interface DeviceTimelineVars {
  id: string;
  since: string | null;
  until: string | null;
}

// --- Find devices (device-finder).

export interface CatalogVars {
  search: string | null;
  first: number;
}

export interface PatchInfo {
  id: string;
  kbId: string;
  title: string;
  severity: Severity;
  vendor: string;
}

export interface CveInfo {
  id: string;
  title: string;
  cvssScore: number;
  severity: Severity;
}

export interface SoftwareInfo {
  name: string;
  version: string;
  publisher: string;
}

/** The catalogs are nullable lists: `null` means degraded or denied (contracts/errors.md). */
export interface PatchCatalogData {
  patches: PatchInfo[] | null;
}

export interface CveCatalogData {
  cves: CveInfo[] | null;
}

export interface SoftwareCatalogData {
  software: SoftwareInfo[] | null;
}

/** A software catalog option can select every version of the product. */
export interface SoftwareKeyInput {
  name: string;
  version: string | null;
}

/** Final server-filtered and paginated result, enriched by Device Directory through Fusion. */
export interface DeviceSearchFilterInput {
  category: string;
  key: string;
  connector: 'and' | 'or';
}
export interface DeviceSearchEvent {
  id: string;
  source: DeviceSearchFilterInput['category'];
  itemKey: string;
  occurredAt: string | null;
  label: string;
  title: string;
  subtitle: string;
  status: string;
  severity: string | null;
}
export interface FindDevicesResult {
  items: { device: Device; events: DeviceSearchEvent[] }[];
  totalCount: number;
  hasNextPage: boolean;
}
export interface FindDevicesData {
  findDevices: FindDevicesResult | null;
}
export interface FindDevicesVars {
  filters: DeviceSearchFilterInput[];
  first: number;
  offset: number;
}

/** Metadata and catalog keys are supplied by the registered device search providers. */
export interface SearchCapability {
  category: string;
  name: string;
  icon: string;
  color: string;
  placeholder: string;
  filterKind: string;
  available: boolean;
}
export interface SearchCapabilitiesData {
  searchCapabilities: SearchCapability[];
}
export interface SearchCatalogItem {
  key: string;
  label: string;
  detail: string;
}
export interface SearchCatalogData {
  searchCatalog: SearchCatalogItem[] | null;
}
export interface SearchCatalogVars {
  category: string;
  search: string | null;
  first: number;
}
