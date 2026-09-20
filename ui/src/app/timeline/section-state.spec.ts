import { CombinedGraphQLErrors, ServerError } from '@apollo/client';

import {
  TIMELINE_SOURCES,
  catalog,
  timelineBody,
  wireEvent,
} from '../../testing/timeline-test-data';
import { buildTimelineQuery } from './timeline-catalog';
import { extractErrors, sectionState, timelineView, type GqlError } from './section-state';

describe('sectionState', () => {
  it('distinguishes empty, denied, unavailable and missing responses for an arbitrary alias', () => {
    expect(sectionState('timelineSource9', { timelineSource9: [] }, [])).toEqual({
      kind: 'ok',
      events: [],
    });
    expect(
      sectionState('timelineSource9', { timelineSource9: null }, [
        {
          message: 'denied',
          path: ['device', 'timelineSource9'],
          extensions: { code: 'AUTH_NOT_AUTHORIZED' },
        },
      ]),
    ).toEqual({ kind: 'no-access' });
    expect(
      sectionState('timelineSource9', { timelineSource9: null }, [
        { message: 'down', path: ['device', 'timelineSource9', 0] },
      ]),
    ).toEqual({ kind: 'unavailable', message: 'down' });
    expect(sectionState('timelineSource9', {}, [])).toEqual({
      kind: 'unavailable',
      message: 'No data and no error returned for this section',
    });
  });
});

describe('extractErrors', () => {
  const errors: GqlError[] = [{ message: 'x', path: ['device', 'timelineSource0'] }];

  it('accepts Apollo v3 and v4 partial-error shapes', () => {
    expect(extractErrors({ errors })).toEqual(errors);
    expect(extractErrors({ error: new CombinedGraphQLErrors({ data: null, errors }) })).toEqual(
      errors,
    );
  });
});

describe('timelineView', () => {
  const built = buildTimelineQuery(catalog());

  it('assigns source metadata to uniform events and preserves per-source partial failure', () => {
    const body = timelineBody(
      TIMELINE_SOURCES,
      { patch: [wireEvent('same')], vulnerability: [wireEvent('same')] },
      { softwareinstall: { message: 'down' } },
    );
    const view = timelineView(
      {
        loading: false,
        data: body.data,
        error: new CombinedGraphQLErrors({ data: body.data, errors: body.errors! }),
      },
      built,
    );

    expect(view.kind).toBe('ok');
    if (view.kind !== 'ok') return;
    expect(view.sections['patch']).toMatchObject({
      kind: 'ok',
      events: [{ source: 'patch', sourceMeta: TIMELINE_SOURCES[0] }],
    });
    expect(view.sections['softwareinstall']).toEqual({ kind: 'unavailable', message: 'down' });
  });

  it('treats __proto__ as an ordinary source id without mutating the section record prototype', () => {
    const source = { ...TIMELINE_SOURCES[0], id: '__proto__', field: 'prototypeTimeline' };
    const query = buildTimelineQuery(catalog([source]));
    const values = Object.fromEntries([['__proto__', [wireEvent('safe')]]]);
    const view = timelineView({ loading: false, data: timelineBody([source], values).data }, query);

    expect(view.kind).toBe('ok');
    if (view.kind !== 'ok') return;
    expect(Object.getPrototypeOf(view.sections)).toBeNull();
    expect(view.sections['__proto__']).toMatchObject({
      kind: 'ok',
      events: [{ id: 'safe', source: '__proto__' }],
    });
  });

  it('handles empty-catalog identity data, not found and directory failure', () => {
    const empty = buildTimelineQuery(catalog([]));
    const ok = timelineView(
      { loading: false, data: { device: timelineBody([], {}).data.device } },
      empty,
    );
    expect(ok.kind === 'ok' && ok.sections).toEqual({});
    expect(timelineView({ loading: false, data: { device: null } }, empty)).toEqual({
      kind: 'not-found',
    });
    expect(
      timelineView(
        {
          loading: false,
          data: { device: null },
          error: new CombinedGraphQLErrors({
            data: { device: null },
            errors: [{ message: 'directory down' }],
          }),
        },
        empty,
      ),
    ).toEqual({ kind: 'directory-unavailable', message: 'directory down' });
  });

  it('keeps HTTP and connection errors as transport failures', () => {
    const response = new Response(null, { status: 401 });
    const server = new ServerError('unauthorized', { response, bodyText: '' });
    expect(timelineView({ loading: false, error: server }, built)).toMatchObject({
      kind: 'transport-error',
      status: 401,
    });
    expect(timelineView({ loading: false, error: new Error('offline') }, built)).toEqual({
      kind: 'transport-error',
      status: null,
      message: 'offline',
    });
  });
});
