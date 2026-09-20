import { CombinedGraphQLErrors, ServerError } from '@apollo/client';

import type { QueryResult } from '../core/watch-query';
import { AUTH_NOT_AUTHORIZED, extractErrors } from '../timeline/section-state';

// The state of one nullable root field (a catalog or a reverse lookup), decided from the GraphQL response
// only, with the same rules as the timeline's sections (contracts/errors.md): data (even an empty list)
// is ok; null plus an error at ["<field>"] is no access (AUTH_NOT_AUTHORIZED) or unavailable (anything
// else, including no code at all); no GraphQL response at all is a transport error.

export type FieldView<T> =
  | { kind: 'loading' }
  | { kind: 'transport-error'; status: number | null; message: string }
  | { kind: 'no-access' }
  | { kind: 'unavailable'; message: string }
  | { kind: 'ok'; value: T };

export function fieldView<TData extends object, K extends keyof TData & string>(
  result: QueryResult<TData>,
  field: K,
): FieldView<NonNullable<TData[K]>> {
  const value = result.data?.[field];
  if (value != null) return { kind: 'ok', value };

  const error = result.error;
  if (error != null && !CombinedGraphQLErrors.is(error)) {
    return {
      kind: 'transport-error',
      status: ServerError.is(error) ? error.statusCode : null,
      message: error instanceof Error ? error.message : String(error),
    };
  }

  const errors = extractErrors(result);
  // PREFIX match: ["devicesWithPatches"] and anything deeper (an item whose device could not be completed).
  const at = errors.find((e) => Array.isArray(e.path) && e.path[0] === field);
  if (at?.extensions?.code === AUTH_NOT_AUTHORIZED) return { kind: 'no-access' };
  if (at) return { kind: 'unavailable', message: at.message };
  if (result.loading) return { kind: 'loading' };
  if (errors.length > 0) return { kind: 'unavailable', message: errors[0].message };
  if (result.data && field in result.data) {
    return { kind: 'unavailable', message: 'No data and no error returned for this field' }; // should not happen; be honest
  }
  return {
    kind: 'transport-error',
    status: null,
    message: 'The gateway returned neither data nor errors.',
  };
}
