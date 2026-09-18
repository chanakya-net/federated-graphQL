import { CombinedGraphQLErrors, ServerError } from '@apollo/client';

import type {
  InstallEvent,
  PatchEvent,
  TimelineDevice,
  VulnerabilityEvent,
} from '../graphql/types';

// Pure decision logic for the timeline page (contracts/errors.md). Everything the page shows about
// access and availability is decided here, from the GraphQL response only.

export type SectionKey = 'patchEvents' | 'vulnerabilityEvents' | 'installEvents';

export type SectionState<T> =
  { kind: 'ok'; events: T[] } | { kind: 'no-access' } | { kind: 'unavailable'; message: string };

/** Structurally compatible with graphql-js `GraphQLFormattedError` (what Apollo v4 exposes). */
export interface GqlError {
  readonly message: string;
  readonly path?: readonly (string | number)[];
  readonly extensions?: { readonly code?: unknown; readonly [key: string]: unknown };
}

export const AUTH_NOT_AUTHORIZED = 'AUTH_NOT_AUTHORIZED';

/**
 * Normalises Apollo v3 (`result.errors`) and v4 (`result.error`, a `CombinedGraphQLErrors` with
 * `.errors`) into a flat list. A transport error (no `errors` array) contributes nothing.
 */
export function extractErrors(result: {
  errors?: readonly GqlError[];
  error?: unknown;
}): GqlError[] {
  const nested = (result.error as { errors?: unknown } | null | undefined)?.errors;
  return [...(result.errors ?? []), ...(Array.isArray(nested) ? (nested as GqlError[]) : [])];
}

export function sectionState<T>(
  field: SectionKey,
  device: Partial<Record<SectionKey, readonly unknown[] | null>> | null | undefined,
  errors: readonly GqlError[],
): SectionState<T> {
  const value = device?.[field];
  if (value != null) return { kind: 'ok', events: value as T[] }; // [] is ok: no events
  // PREFIX match: ["device", field] and anything deeper (["device", field, 3, "patch"]).
  const error = errors.find(
    (e) => Array.isArray(e.path) && e.path[0] === 'device' && e.path[1] === field,
  );
  if (error?.extensions?.code === AUTH_NOT_AUTHORIZED) return { kind: 'no-access' };
  // Any other code, or none at all: outage errors carry no `extensions` (version-facts §7).
  if (error) return { kind: 'unavailable', message: error.message };
  return { kind: 'unavailable', message: 'No data and no error returned for this section' }; // should not happen; be honest
}

export interface TimelineSections {
  patchEvents: SectionState<PatchEvent>;
  vulnerabilityEvents: SectionState<VulnerabilityEvent>;
  installEvents: SectionState<InstallEvent>;
}

/** Everything the timeline page can show, decided before any section is rendered. */
export type TimelineView =
  | { kind: 'loading' }
  /** No usable GraphQL response: HTTP error (401 after a token change, 502 gateway down) or no connection. */
  | { kind: 'transport-error'; status: number | null; message: string }
  /** `device == null` without errors: not in the caller's tenant (tenant isolation leaks nothing). */
  | { kind: 'not-found' }
  /** `device == null` with errors: Device Directory failed, so the whole query failed by design. */
  | { kind: 'directory-unavailable'; message: string }
  | { kind: 'ok'; device: TimelineDevice; sections: TimelineSections };

/** The subset of an Apollo `ObservableQuery.Result` (v3 or v4) the timeline page needs. */
export interface TimelineResult {
  loading: boolean;
  data?: { device?: TimelineDevice | null } | null;
  errors?: readonly GqlError[];
  error?: unknown;
}

export function timelineView(result: TimelineResult): TimelineView {
  const error = result.error;
  const errors = extractErrors(result);
  const device = result.data?.device;

  if (device) {
    return {
      kind: 'ok',
      device,
      sections: {
        patchEvents: sectionState<PatchEvent>('patchEvents', device, errors),
        vulnerabilityEvents: sectionState<VulnerabilityEvent>(
          'vulnerabilityEvents',
          device,
          errors,
        ),
        installEvents: sectionState<InstallEvent>('installEvents', device, errors),
      },
    };
  }
  if (error != null && !CombinedGraphQLErrors.is(error)) {
    return {
      kind: 'transport-error',
      status: ServerError.is(error) ? error.statusCode : null,
      message: error instanceof Error ? error.message : String(error),
    };
  }
  if (result.loading) return { kind: 'loading' };
  if (errors.length > 0) return { kind: 'directory-unavailable', message: errors[0].message };
  if (result.data?.device === null) return { kind: 'not-found' };
  return {
    kind: 'transport-error',
    status: null,
    message: 'The gateway returned neither data nor errors.',
  };
}
