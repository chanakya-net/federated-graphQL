#!/usr/bin/env node
// Records raw gateway responses for the UI's operations into src/testing/fixtures/gateway/.
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
// Capture only the new search responses without stopping services or re-recording legacy fixtures.
const searchOnly = process.argv.includes('--search-only');

const operations = readFileSync(join(here, '../app/graphql/operations.ts'), 'utf8');
const query = (name) => {
  const match = operations.match(new RegExp('export const ' + name + ' = gql[^`]*`([^`]*)`'));
  if (!match) throw new Error(`${name} not found in operations.ts`);
  // The document exactly as Apollo's InMemoryCache sends it: `__typename` in every selection set.
  return print(addTypenameToDocument(parse(match[1])));
};
const DEVICE_SEARCH = query('DEVICE_SEARCH');
const DEVICE_TIMELINE = query('DEVICE_TIMELINE');
const PATCH_CATALOG = query('PATCH_CATALOG');
const CVE_CATALOG = query('CVE_CATALOG');
const SOFTWARE_CATALOG = query('SOFTWARE_CATALOG');
const FIND_DEVICES = query('FIND_DEVICES');
const SEARCH_CAPABILITIES = query('SEARCH_CAPABILITIES');
const SEARCH_CATALOG = query('SEARCH_CATALOG');

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
  return JSON.parse(body);
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

// Record complete server search results; never compute device sets in the recorder.
const catalog = (file, user, operationName, text, search = null) =>
  record(file, user, operationName, text, { search, first: 25 });
const find = (file, user, filters, offset = 0) =>
  record(file, user, 'FindDevices', FIND_DEVICES, { filters, first: 25, offset });
const PATCH = { category: 'patch', key: 'patch-0128', connector: 'and' };
const CVE = { category: 'vulnerability', key: 'CVE-2026-10166', connector: 'and' };
const SOFTWARE = { category: 'softwareinstall', key: 'Git', connector: 'or' };

async function withOutage(service, action, fn) {
  outage(service, action);
  try {
    await fn();
  } finally {
    outage(service, 'restore');
  }
}

if (!searchOnly) {
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

  await catalog('catalog-patches-alice.json', 'alice', 'PatchCatalog', PATCH_CATALOG);
  await catalog(
    'catalog-patches-apple-alice.json',
    'alice',
    'PatchCatalog',
    PATCH_CATALOG,
    'apple',
  );
  await catalog('catalog-cves-alice.json', 'alice', 'CveCatalog', CVE_CATALOG);
  await catalog('catalog-software-alice.json', 'alice', 'SoftwareCatalog', SOFTWARE_CATALOG);
  await catalog('catalog-patches-denied-carol.json', 'carol', 'PatchCatalog', PATCH_CATALOG);
}

// Common provider contract: captured in search-only mode as well, with no outages.
for (const user of ['alice', 'bob', 'carol']) {
  await record(
    `search-capabilities-${user}.json`,
    user,
    'SearchCapabilities',
    SEARCH_CAPABILITIES,
    {},
  );
}
for (const category of ['patch', 'vulnerability', 'softwareinstall']) {
  await record(`search-catalog-${category}-alice.json`, 'alice', 'SearchCatalog', SEARCH_CATALOG, {
    category,
    search: null,
    first: 25,
  });
}
await record('search-catalog-patch-apple-alice.json', 'alice', 'SearchCatalog', SEARCH_CATALOG, {
  category: 'patch',
  search: 'apple',
  first: 25,
});
await record('search-catalog-patch-denied-carol.json', 'carol', 'SearchCatalog', SEARCH_CATALOG, {
  category: 'patch',
  search: null,
  first: 25,
});

await find('find-devices-and-alice.json', 'alice', [PATCH, CVE]);
await find('find-devices-or-alice.json', 'alice', [PATCH, { ...CVE, connector: 'or' }, SOFTWARE]);
await find('find-devices-page-alice.json', 'alice', [PATCH], 25);
await find('find-devices-dave.json', 'dave', [PATCH]);
await find('find-devices-denied-carol.json', 'carol', [PATCH, SOFTWARE]);
await find('find-devices-denied-bob.json', 'bob', [SOFTWARE]);
if (!searchOnly) {
  await withOutage('patch', 'stop', () =>
    find('find-devices-outage-stop.json', 'alice', [PATCH, CVE]),
  );
  await withOutage('device-directory', 'stop', () =>
    find('find-devices-directory-down.json', 'alice', [PATCH]),
  );
}
