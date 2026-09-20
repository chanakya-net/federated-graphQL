import { CombinedGraphQLErrors, ServerError } from '@apollo/client';

import catalogDeniedCarol from '../../testing/fixtures/gateway/catalog-patches-denied-carol.json';
import catalogPatches from '../../testing/fixtures/gateway/catalog-patches-alice.json';
import findAlice from '../../testing/fixtures/gateway/find-patches-alice.json';
import findDeniedCarol from '../../testing/fixtures/gateway/find-patches-denied-carol.json';
import findDirectoryDown from '../../testing/fixtures/gateway/find-patches-directory-down.json';
import findOutage from '../../testing/fixtures/gateway/find-patches-outage-stop.json';
import setsAlice from '../../testing/fixtures/gateway/sets-patches-alice.json';
import setsOutage from '../../testing/fixtures/gateway/sets-patches-outage-stop.json';
import type { QueryResult } from '../core/watch-query';
import type { PatchCatalogData } from '../graphql/types';
import type { GqlError } from '../timeline/section-state';
import { fieldView } from './field-state';

type Body = { data?: Record<string, unknown> | null; errors?: GqlError[] };

/** A recorded body as Apollo v4 exposes it (`error` is a `CombinedGraphQLErrors`). */
function v4<T extends object>(body: unknown): QueryResult<T> {
  const { data, errors } = body as Body;
  return {
    loading: false,
    data: data as T,
    error: errors?.length ? new CombinedGraphQLErrors({ data, errors }) : undefined,
  };
}

describe('fieldView', () => {
  it('ok with the page: a full reverse lookup', () => {
    const view = fieldView(v4<typeof findAlice.data>(findAlice), 'devicesWithPatches');
    expect(view.kind).toBe('ok');
    if (view.kind !== 'ok') return;
    expect(view.value.totalCount).toBeGreaterThan(0);
    expect(view.value.items.length).toBeGreaterThan(0);
    expect(view.value.items[0].device.hostname).toBeTruthy(); // completed by Device Directory
  });

  it('ok with the per-item device sets: one sorted set per selected patch', () => {
    const view = fieldView(v4<typeof setsAlice.data>(setsAlice), 'devicesWithPatches');
    expect(view.kind).toBe('ok');
    if (view.kind !== 'ok') return;
    const sets = view.value.matches;
    expect(sets.map((m) => m.patchId)).toEqual(['patch-0128', 'patch-0282']);
    for (const m of sets) {
      expect(m.deviceIds.length).toBeGreaterThan(0);
      expect(m.deviceIds).toEqual([...m.deviceIds].sort());
    }
  });

  it('ok for a catalog page', () => {
    const view = fieldView(v4<typeof catalogPatches.data>(catalogPatches), 'patches');
    expect(view.kind).toBe('ok');
  });

  it('no access: null plus AUTH_NOT_AUTHORIZED at the field, for a lookup and for a catalog', () => {
    expect(
      fieldView(v4<typeof findDeniedCarol.data>(findDeniedCarol), 'devicesWithPatches'),
    ).toEqual({ kind: 'no-access' });
    expect(fieldView(v4<typeof catalogDeniedCarol.data>(catalogDeniedCarol), 'patches')).toEqual({
      kind: 'no-access',
    });
  });

  it('unavailable: the subgraph is down (an error without a code), for the sets and for the page', () => {
    const view = fieldView(v4<typeof findOutage.data>(findOutage), 'devicesWithPatches');
    expect(view.kind).toBe('unavailable');
    expect(view.kind === 'unavailable' && view.message).toBe('Unexpected Execution Error');
    expect(fieldView(v4<typeof setsOutage.data>(setsOutage), 'devicesWithPatches').kind).toBe(
      'unavailable',
    );
  });

  it('unavailable: Device Directory is down, so no device stub can be completed', () => {
    const view = fieldView(
      v4<typeof findDirectoryDown.data>(findDirectoryDown),
      'devicesWithPatches',
    );
    expect(view.kind).toBe('unavailable');
  });

  it('loading, transport errors and the empty answer', () => {
    const lookup = (result: QueryResult<typeof findAlice.data>) =>
      fieldView(result, 'devicesWithPatches');
    const catalog = (result: QueryResult<PatchCatalogData>) => fieldView(result, 'patches');

    expect(lookup({ loading: true })).toEqual({ kind: 'loading' });
    const response = new Response(null, { status: 401 });
    expect(
      lookup({ loading: false, error: new ServerError('401', { response, bodyText: '' }) }),
    ).toMatchObject({ kind: 'transport-error', status: 401 });
    expect(catalog({ loading: false, error: new Error('offline') })).toMatchObject({
      kind: 'transport-error',
      status: null,
      message: 'offline',
    });
    expect(catalog({ loading: false, data: null })).toMatchObject({ kind: 'transport-error' });
    expect(catalog({ loading: false, data: { patches: null } })).toMatchObject({
      kind: 'unavailable',
    });
  });
});
