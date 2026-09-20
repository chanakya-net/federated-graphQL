// Hand-written result types for the operations in operations.ts (schemas/*.graphqls).
// `DateTime` values arrive as ISO-8601 strings.

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

// --- Find devices (device-finder).

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
