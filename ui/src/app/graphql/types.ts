// Hand-written result types for the two operations in operations.ts (schemas/*.graphqls).
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
