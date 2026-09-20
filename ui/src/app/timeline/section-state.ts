import { CombinedGraphQLErrors, ServerError } from '@apollo/client';

import type { QueryResult } from '../core/watch-query';
import type {
  BuiltTimelineQuery,
  DeviceTimelineData,
  TimelineDevice,
  TimelineEventWire,
} from './timeline-catalog';
import type { TimelineEvent } from './timeline.models';

export type SectionState<T> =
  { kind: 'ok'; events: T[] } | { kind: 'no-access' } | { kind: 'unavailable'; message: string };

export interface GqlError {
  readonly message: string;
  readonly path?: readonly (string | number)[];
  readonly extensions?: { readonly code?: unknown; readonly [key: string]: unknown };
}

export const AUTH_NOT_AUTHORIZED = 'AUTH_NOT_AUTHORIZED';

export function extractErrors(result: {
  errors?: readonly GqlError[];
  error?: unknown;
}): GqlError[] {
  const nested = (result.error as { errors?: unknown } | null | undefined)?.errors;
  return [...(result.errors ?? []), ...(Array.isArray(nested) ? (nested as GqlError[]) : [])];
}

export function sectionState<T>(
  field: string,
  device: Record<string, unknown> | null | undefined,
  errors: readonly GqlError[],
): SectionState<T> {
  const value = device?.[field];
  if (Array.isArray(value)) return { kind: 'ok', events: value as T[] };
  const error = errors.find(
    (candidate) =>
      Array.isArray(candidate.path) &&
      candidate.path[0] === 'device' &&
      candidate.path[1] === field,
  );
  if (error?.extensions?.code === AUTH_NOT_AUTHORIZED) return { kind: 'no-access' };
  if (error) return { kind: 'unavailable', message: error.message };
  return { kind: 'unavailable', message: 'No data and no error returned for this section' };
}

export type TimelineSections = Readonly<Record<string, SectionState<TimelineEvent>>>;

export type TimelineView =
  | { kind: 'loading' }
  | { kind: 'transport-error'; status: number | null; message: string }
  | { kind: 'not-found' }
  | { kind: 'directory-unavailable'; message: string }
  | { kind: 'ok'; device: TimelineDevice; sections: TimelineSections };

export function timelineView(
  result: QueryResult<DeviceTimelineData>,
  query: BuiltTimelineQuery | null,
): TimelineView {
  const error = result.error;
  const errors = extractErrors(result);
  const device = result.data?.device;

  if (device && query) {
    const sections: Record<string, SectionState<TimelineEvent>> = Object.create(null);
    for (const { alias, source } of query.fields) {
      const state = sectionState<TimelineEventWire>(alias, device, errors);
      sections[source.id] =
        state.kind === 'ok'
          ? {
              kind: 'ok',
              events: state.events.map((event) => ({
                ...event,
                source: source.id,
                sourceMeta: source,
              })),
            }
          : state;
    }
    return { kind: 'ok', device, sections };
  }
  if (error != null && !CombinedGraphQLErrors.is(error)) {
    return {
      kind: 'transport-error',
      status: ServerError.is(error) ? error.statusCode : null,
      message: error instanceof Error ? error.message : String(error),
    };
  }
  if (result.loading || query == null) return { kind: 'loading' };
  if (errors.length > 0) return { kind: 'directory-unavailable', message: errors[0].message };
  if (result.data?.device === null) return { kind: 'not-found' };
  return {
    kind: 'transport-error',
    status: null,
    message: 'The gateway returned neither data nor errors.',
  };
}
