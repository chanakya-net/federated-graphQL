import { CombinedGraphQLErrors, ServerError } from '@apollo/client';

import findAlice from '../../testing/fixtures/gateway/find-devices-and-alice.json';
import findDeniedCarol from '../../testing/fixtures/gateway/find-devices-denied-carol.json';
import findDirectoryDown from '../../testing/fixtures/gateway/find-devices-directory-down.json';
import findOutage from '../../testing/fixtures/gateway/find-devices-outage-stop.json';
import type { QueryResult } from '../core/watch-query';
import type { FindDevicesData } from '../graphql/types';
import type { GqlError } from '../timeline/section-state';
import { fieldView } from './field-state';

type Body = { data?: Record<string, unknown> | null; errors?: GqlError[] };

function v4<T extends object>(body: unknown): QueryResult<T> {
  const { data, errors } = body as Body;
  return {
    loading: false,
    data: data as T,
    error: errors?.length ? new CombinedGraphQLErrors({ data, errors }) : undefined,
  };
}

const view = (result: QueryResult<FindDevicesData>) => fieldView(result, 'findDevices');

describe('fieldView', () => {
  it('accepts the current generic finder response', () => {
    const current = view(v4<FindDevicesData>(findAlice));
    expect(current.kind).toBe('ok');
    if (current.kind !== 'ok') return;
    expect(current.value.totalCount).toBeGreaterThan(0);
    expect(current.value.items[0].device.hostname).toBeTruthy();
    expect(current.value.items[0].events.length).toBeGreaterThan(0);
  });

  it('distinguishes denied, source outage and Device Directory outage', () => {
    expect(view(v4<FindDevicesData>(findDeniedCarol))).toEqual({ kind: 'no-access' });
    expect(view(v4<FindDevicesData>(findOutage))).toMatchObject({
      kind: 'unavailable',
      message: 'patch request timed out.',
    });
    expect(view(v4<FindDevicesData>(findDirectoryDown))).toMatchObject({
      kind: 'unavailable',
      message: 'Unexpected Execution Error',
    });
  });

  it('handles loading, transport errors and null without an error', () => {
    expect(view({ loading: true })).toEqual({ kind: 'loading' });
    const response = new Response(null, { status: 401 });
    expect(
      view({ loading: false, error: new ServerError('401', { response, bodyText: '' }) }),
    ).toMatchObject({ kind: 'transport-error', status: 401 });
    expect(view({ loading: false, error: new Error('offline') })).toMatchObject({
      kind: 'transport-error',
      status: null,
      message: 'offline',
    });
    expect(view({ loading: false, data: null })).toMatchObject({ kind: 'transport-error' });
    expect(view({ loading: false, data: { findDevices: null } })).toMatchObject({
      kind: 'unavailable',
    });
  });
});
