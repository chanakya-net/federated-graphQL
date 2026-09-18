import { CombinedGraphQLErrors, ServerError } from '@apollo/client';

import phase0Denied from '../../testing/fixtures/denied.json';
import phase0OutagePause from '../../testing/fixtures/outage-pause.json';
import phase0OutageStop from '../../testing/fixtures/outage-stop.json';
import crossTenant from '../../testing/fixtures/gateway/timeline-cross-tenant-dave.json';
import deniedBob from '../../testing/fixtures/gateway/timeline-denied-bob.json';
import deniedBobInstallStopped from '../../testing/fixtures/gateway/timeline-denied-bob-software-install-stopped.json';
import deniedCarol from '../../testing/fixtures/gateway/timeline-denied-carol.json';
import directoryDown from '../../testing/fixtures/gateway/timeline-directory-down.json';
import fullAlice from '../../testing/fixtures/gateway/timeline-full-alice.json';
import outagePause from '../../testing/fixtures/gateway/timeline-outage-pause-patch.json';
import outageStop from '../../testing/fixtures/gateway/timeline-outage-stop-patch.json';
import rangeAlice from '../../testing/fixtures/gateway/timeline-range-alice.json';
import {
  extractErrors,
  sectionState,
  timelineView,
  type GqlError,
  type SectionKey,
  type TimelineResult,
  type TimelineView,
} from './section-state';

type Body = { data?: { device?: Record<string, unknown> | null } | null; errors?: GqlError[] };

/** A recorded body as Apollo v3 exposed it (`errors` next to `data`). */
const v3 = (body: unknown): TimelineResult =>
  ({ loading: false, ...(body as object) }) as TimelineResult;

/** A recorded body as Apollo v4 exposes it (`error` is a `CombinedGraphQLErrors`). */
function v4(body: unknown): TimelineResult {
  const { data, errors } = body as Body;
  return {
    loading: false,
    data: data as TimelineResult['data'],
    error: errors?.length ? new CombinedGraphQLErrors({ data, errors }) : undefined,
  };
}

/** Phase 0 recorded the spike's `notes` field; the shapes are the same for the real fields. */
function asField(body: unknown, field: SectionKey): Body {
  const json = JSON.stringify(body).replaceAll('"notes"', JSON.stringify(field));
  return JSON.parse(json) as Body;
}

function sections(view: TimelineView) {
  if (view.kind !== 'ok') throw new Error(`expected ok, got ${view.kind}`);
  return view.sections;
}

const device = (body: unknown) =>
  (body as Body).data!.device as Record<SectionKey, unknown[] | null>;
const errorsOf = (body: unknown) => (body as Body).errors ?? [];

describe('sectionState', () => {
  it('ok with events: the full response', () => {
    for (const key of ['patchEvents', 'vulnerabilityEvents', 'installEvents'] as const) {
      const state = sectionState(key, device(fullAlice), errorsOf(fullAlice));
      expect(state.kind).toBe('ok');
      expect(state.kind === 'ok' && state.events.length).toBeGreaterThan(0);
    }
  });

  it('ok with an empty list: [] means no events, not degraded', () => {
    expect(device(rangeAlice).patchEvents).toEqual([]);
    expect(sectionState('patchEvents', device(rangeAlice), errorsOf(rangeAlice))).toEqual({
      kind: 'ok',
      events: [],
    });
  });

  it('denied: no-access for the denied field, ok for the others (bob)', () => {
    const s = sections(timelineView(v4(deniedBob)));
    expect(s.installEvents).toEqual({ kind: 'no-access' });
    expect(s.patchEvents.kind).toBe('ok');
    expect(s.vulnerabilityEvents.kind).toBe('ok');
  });

  it('denied: two sections at once (carol)', () => {
    const s = sections(timelineView(v4(deniedCarol)));
    expect(s.patchEvents).toEqual({ kind: 'no-access' });
    expect(s.vulnerabilityEvents).toEqual({ kind: 'no-access' });
    expect(s.installEvents.kind).toBe('ok');
  });

  it('denied: the Phase 0 fixture, whatever the field', () => {
    for (const field of ['patchEvents', 'vulnerabilityEvents', 'installEvents'] as const) {
      const body = asField(phase0Denied, field);
      expect(sectionState(field, device(body), errorsOf(body))).toEqual({ kind: 'no-access' });
    }
  });

  it('outage stop: unavailable with the error message (the error has no extensions)', () => {
    expect(errorsOf(outageStop)[0].extensions).toBeUndefined();
    const s = sections(timelineView(v4(outageStop)));
    expect(s.patchEvents).toEqual({ kind: 'unavailable', message: 'Unexpected Execution Error' });
    expect(s.vulnerabilityEvents.kind).toBe('ok');
    expect(s.installEvents.kind).toBe('ok');

    const body = asField(phase0OutageStop, 'installEvents');
    expect(sectionState('installEvents', device(body), errorsOf(body))).toEqual({
      kind: 'unavailable',
      message: 'Unexpected Execution Error',
    });
  });

  it('outage pause: unavailable', () => {
    expect(sections(timelineView(v4(outagePause))).patchEvents.kind).toBe('unavailable');
    const body = asField(phase0OutagePause, 'vulnerabilityEvents');
    expect(sectionState('vulnerabilityEvents', device(body), errorsOf(body)).kind).toBe(
      'unavailable',
    );
  });

  it('denied user whose subgraph is down: unavailable, because only the subgraph can deny', () => {
    const s = sections(timelineView(v4(deniedBobInstallStopped)));
    expect(s.installEvents).toEqual({ kind: 'unavailable', message: 'Unexpected Execution Error' });
  });

  it('prefix match: a deeper path still maps to the section', () => {
    const errors: GqlError[] = [
      {
        message: 'nope',
        path: ['device', 'patchEvents', 0, 'patch'],
        extensions: { code: 'AUTH_NOT_AUTHORIZED' },
      },
      { message: 'boom', path: ['device', 'installEvents', 3, 'software', 'name'] },
    ];
    const d = { patchEvents: null, vulnerabilityEvents: [], installEvents: null };
    expect(sectionState('patchEvents', d, errors)).toEqual({ kind: 'no-access' });
    expect(sectionState('installEvents', d, errors)).toEqual({
      kind: 'unavailable',
      message: 'boom',
    });
    expect(sectionState('vulnerabilityEvents', d, errors).kind).toBe('ok');
  });

  it('matches only ["device", <field>, ...]: other paths do not count', () => {
    const errors: GqlError[] = [
      { message: 'root', path: ['patchEvents'], extensions: { code: 'AUTH_NOT_AUTHORIZED' } },
      { message: 'device', path: ['device'], extensions: { code: 'AUTH_NOT_AUTHORIZED' } },
      {
        message: 'other',
        path: ['device', 'patchEventsX'],
        extensions: { code: 'AUTH_NOT_AUTHORIZED' },
      },
      { message: 'no path' },
    ];
    expect(sectionState('patchEvents', { patchEvents: null }, errors)).toEqual({
      kind: 'unavailable',
      message: 'No data and no error returned for this section',
    });
  });

  it('any code other than AUTH_NOT_AUTHORIZED is unavailable', () => {
    const errors: GqlError[] = [
      {
        message: 'x',
        path: ['device', 'patchEvents'],
        extensions: { code: 'SUBGRAPH_UNAVAILABLE' },
      },
    ];
    expect(sectionState('patchEvents', { patchEvents: null }, errors)).toEqual({
      kind: 'unavailable',
      message: 'x',
    });
  });

  it('data wins: a value next to an error for the same field is still ok', () => {
    const errors: GqlError[] = [{ message: 'x', path: ['device', 'patchEvents', 1] }];
    expect(sectionState('patchEvents', { patchEvents: [1] }, errors)).toEqual({
      kind: 'ok',
      events: [1],
    });
  });

  it('null without error: unavailable with the fallback message', () => {
    expect(sectionState('installEvents', { installEvents: null }, [])).toEqual({
      kind: 'unavailable',
      message: 'No data and no error returned for this section',
    });
    expect(sectionState('installEvents', null, []).kind).toBe('unavailable');
  });
});

describe('extractErrors', () => {
  const a: GqlError = { message: 'a', path: ['device', 'patchEvents'] };
  const b: GqlError = {
    message: 'b',
    path: ['device', 'installEvents'],
    extensions: { code: 'AUTH_NOT_AUTHORIZED' },
  };

  it('flattens the Apollo v3 shape (result.errors)', () => {
    expect(extractErrors({ errors: [a, b] })).toEqual([a, b]);
  });

  it('flattens the Apollo v4 shape (result.error.errors), plain and as a CombinedGraphQLErrors', () => {
    expect(extractErrors({ error: { errors: [a, b] } })).toEqual([a, b]);
    const combined = new CombinedGraphQLErrors({ data: null, errors: [a, b] });
    expect(extractErrors({ error: combined })).toEqual([a, b]);
  });

  it('takes both when both are present', () => {
    expect(extractErrors({ errors: [a], error: { errors: [b] } })).toEqual([a, b]);
  });

  it('a transport error or nothing contributes no errors', () => {
    expect(extractErrors({})).toEqual([]);
    expect(extractErrors({ error: new Error('offline') })).toEqual([]);
    expect(extractErrors({ error: serverError(401) })).toEqual([]);
  });
});

describe('timelineView', () => {
  it('loading before any result', () => {
    expect(timelineView({ loading: true })).toEqual({ kind: 'loading' });
  });

  it('ok: the same decisions from the v3 and the v4 shape', () => {
    for (const body of [fullAlice, deniedBob, deniedCarol, outageStop]) {
      expect(timelineView(v3(body))).toEqual(timelineView(v4(body)));
    }
    const view = timelineView(v4(fullAlice));
    expect(view.kind === 'ok' && view.device.id).toBe('dev-00001');
  });

  it('device null without errors: not found (another tenant looks the same as nothing)', () => {
    expect(timelineView(v4(crossTenant))).toEqual({ kind: 'not-found' });
  });

  it('device null with errors: the device directory is unavailable', () => {
    expect(timelineView(v4(directoryDown))).toEqual({
      kind: 'directory-unavailable',
      message: 'Unexpected Execution Error',
    });
    expect(timelineView(v3(directoryDown)).kind).toBe('directory-unavailable');
  });

  it('HTTP errors are transport errors with their status', () => {
    expect(timelineView({ loading: false, error: serverError(401) })).toMatchObject({
      kind: 'transport-error',
      status: 401,
    });
    expect(timelineView({ loading: false, error: serverError(502) })).toMatchObject({
      kind: 'transport-error',
      status: 502,
    });
  });

  it('a connection error is a transport error without status', () => {
    expect(
      timelineView({ loading: false, error: new Error('Http failure response for /graphql: 0') }),
    ).toEqual({
      kind: 'transport-error',
      status: null,
      message: 'Http failure response for /graphql: 0',
    });
  });

  it('never blank: no data, no error, not loading', () => {
    expect(timelineView({ loading: false }).kind).toBe('transport-error');
  });
});

function serverError(status: number): ServerError {
  const response = new Response(null, { status });
  return new ServerError(`Response not successful: Received status code ${status}`, {
    response,
    bodyText: '',
  });
}
