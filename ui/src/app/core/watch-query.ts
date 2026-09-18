import { Signal, computed, inject } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import type { OperationVariables, TypedDocumentNode } from '@apollo/client';
import { Apollo } from 'apollo-angular';
import { map, of, startWith, switchMap } from 'rxjs';

/** The part of an Apollo `ObservableQuery.Result` the pages use (v4: GraphQL errors in `error`). */
export interface QueryResult<TData> {
  loading: boolean;
  data?: TData | null;
  error?: unknown;
}

/**
 * A request to run, or `null` to run nothing. Build it with `computed()` and put everything that
 * must re-run the query into it (the selected user, a retry counter): each new object is a new query.
 */
export interface QueryRequest<TVars> {
  variables: TVars;
  readonly [key: string]: unknown;
}

const LOADING: QueryResult<never> = { loading: true };

/**
 * Runs `apollo.watchQuery` for the current request and returns its latest result as a signal. The
 * subscription follows the request (`switchMap`) and ends with the calling component (`toSignal`).
 *
 * Each result is tagged with the request that produced it; a result that belongs to an older
 * request (for example one cancelled by the user switch's `clearStore()`) reads as loading, so a
 * stale error never flashes up.
 */
export function watchQuery<TData, TVars extends OperationVariables>(
  query: TypedDocumentNode<TData, TVars>,
  request: Signal<QueryRequest<TVars> | null>,
): Signal<QueryResult<TData>> {
  const apollo = inject(Apollo);
  const tagged = toSignal(
    toObservable(request).pipe(
      switchMap((req) =>
        req == null
          ? of({ req, result: LOADING as QueryResult<TData> })
          : apollo.watchQuery<TData, TVars>({ query, variables: req.variables }).valueChanges.pipe(
              map((result) => ({ req, result: result as QueryResult<TData> })),
              startWith({ req, result: LOADING as QueryResult<TData> }),
            ),
      ),
    ),
  );
  return computed(() => {
    const current = tagged();
    return current && current.req === request() ? current.result : LOADING;
  });
}
