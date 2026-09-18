#!/usr/bin/env node
// Records raw gateway responses for the UI's two operations into src/testing/fixtures/gateway/.
// Needs the full compose stack (scripts/up.sh). The outage scenarios stop / pause a subgraph with
// scripts/demo-outage.sh and restore it afterwards.
//
//   node src/testing/record-fixtures.mjs            (from ui/; GATEWAY_PORT defaults to 5050)
//
// Each file is the response body exactly as the gateway returned it; the unit tests and the mock
// gateway (mock-gateway.mjs) read these files. The query is the one Apollo sends (with __typename),
// so the fixtures pass through the Apollo cache unchanged.

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

import { addTypenameToDocument } from '@apollo/client/utilities';
import { parse, print } from 'graphql';

const here = dirname(fileURLToPath(import.meta.url));
const repo = resolve(here, '../../..');
const out = join(here, 'fixtures/gateway');
const gateway = `http://localhost:${process.env.GATEWAY_PORT ?? '5050'}/graphql`;

const operations = readFileSync(join(here, '../app/graphql/operations.ts'), 'utf8');
const query = (name) => {
  const match = operations.match(new RegExp(`export const ${name} = gql<[^>]*>\`([^\`]*)\``));
  if (!match) throw new Error(`${name} not found in operations.ts`);
  // The document exactly as Apollo's InMemoryCache sends it: `__typename` in every selection set.
  return print(addTypenameToDocument(parse(match[1])));
};
const DEVICE_SEARCH = query('DEVICE_SEARCH');
const DEVICE_TIMELINE = query('DEVICE_TIMELINE');

const tokens = new Map();
function token(sub) {
  if (!tokens.has(sub)) {
    const jwt = execFileSync(
      'docker',
      ['compose', 'run', '--rm', '-T', 'token-generator', '--user', sub],
      {
        cwd: repo,
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'ignore'],
      },
    ).trim();
    tokens.set(sub, jwt);
  }
  return tokens.get(sub);
}

function outage(service, action) {
  execFileSync('scripts/demo-outage.sh', [service, action], { cwd: repo, stdio: 'inherit' });
}

async function record(file, user, operationName, text, variables) {
  const started = Date.now();
  const response = await fetch(gateway, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token(user)}` },
    body: JSON.stringify({ operationName, variables, query: text }),
  });
  const body = await response.text();
  if (response.status !== 200) throw new Error(`${file}: HTTP ${response.status} ${body}`);
  writeFileSync(join(out, file), body.endsWith('\n') ? body : `${body}\n`);
  console.log(`${file}: HTTP ${response.status}, ${body.length} bytes, ${Date.now() - started} ms`);
}

const timeline = (file, user, id = 'dev-00001', range = {}) =>
  record(file, user, 'DeviceTimeline', DEVICE_TIMELINE, { id, since: null, until: null, ...range });
const search = (file, user, variables) =>
  record(file, user, 'DeviceSearch', DEVICE_SEARCH, {
    search: null,
    first: 25,
    offset: 0,
    ...variables,
  });

async function withOutage(service, action, fn) {
  outage(service, action);
  try {
    await fn();
  } finally {
    outage(service, 'restore');
  }
}

await search('search-alice.json', 'alice', {});
await search('search-alice-dev-000.json', 'alice', { search: 'dev-000' });
await search('search-dave.json', 'dave', {});
await timeline('timeline-full-alice.json', 'alice');
await timeline('timeline-range-alice.json', 'alice', 'dev-00001', {
  since: '2026-08-01T00:00:00.000Z',
  until: '2026-08-31T23:59:59.999Z',
});
await timeline('timeline-denied-bob.json', 'bob');
await timeline('timeline-denied-carol.json', 'carol');
await timeline('timeline-cross-tenant-dave.json', 'dave');
await timeline('timeline-full-dave.json', 'dave', 'dev-07000');
await withOutage('patch', 'stop', () => timeline('timeline-outage-stop-patch.json', 'alice'));
await withOutage('patch', 'pause', () => timeline('timeline-outage-pause-patch.json', 'alice'));
await withOutage('software-install', 'stop', () =>
  timeline('timeline-denied-bob-software-install-stopped.json', 'bob'),
);
await withOutage('device-directory', 'stop', () =>
  timeline('timeline-directory-down.json', 'alice'),
);
