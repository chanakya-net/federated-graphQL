#!/usr/bin/env node
// Stand-in for Nginx + the Fusion gateway, so the UI runs with no Docker and no .NET (`npm run mock`,
// then `npm start`, whose proxy.conf.json points /graphql and /tokens.json here). No dependencies.
//
//   GET  /tokens.json          src/testing/tokens.dev.json (fake tokens; the payload is read, never verified)
//   POST /graphql              answers built from the responses recorded from the real gateway
//                              (fixtures/gateway/, see record-fixtures.mjs), so the shapes are exact
//   GET  /__mock?mode=<mode>   switch the mode at runtime
//
// Modes (MOCK_MODE env var, the x-mock-mode request header, or /__mock):
//   auto (default)   like the real stack: no/invalid token -> 401; a device of another tenant ->
//                    {"device":null}; each service missing from the token -> that section null plus
//                    the recorded AUTH_NOT_AUTHORIZED error; $since/$until filter the events.
//   full | denied | denied-carol | outage-stop | outage-pause | cross-tenant | directory-down
//                    the recorded dev-00001 response verbatim (outage-pause answers after 5 s, like
//                    the gateway's subgraph timeout).
//   unauthenticated  401 with an empty body.
import { readFileSync } from 'node:fs';
import { createServer } from 'node:http';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const fixture = (name) => JSON.parse(readFileSync(join(here, 'fixtures/gateway', name), 'utf8'));
const port = Number(process.env.MOCK_PORT ?? 5001);
let mode = process.env.MOCK_MODE ?? 'auto';

const VERBATIM = {
  full: 'timeline-full-alice.json',
  denied: 'timeline-denied-bob.json',
  'denied-carol': 'timeline-denied-carol.json',
  'outage-stop': 'timeline-outage-stop-patch.json',
  'outage-pause': 'timeline-outage-pause-patch.json',
  'cross-tenant': 'timeline-cross-tenant-dave.json',
  'directory-down': 'timeline-directory-down.json',
};
const MODES = ['auto', 'unauthenticated', ...Object.keys(VERBATIM)];
const SECTIONS = {
  patchEvents: 'patch',
  vulnerabilityEvents: 'vulnerability',
  installEvents: 'softwareinstall',
};
const DENIED_ERROR = fixture('timeline-denied-bob.json').errors[0];
const FULL = {
  TenantA: fixture('timeline-full-alice.json'),
  TenantB: fixture('timeline-full-dave.json'),
};

// The device catalog: every device the recorded searches and timelines contain, per tenant.
const catalog = new Map();
for (const f of ['search-alice.json', 'search-alice-dev-000.json', 'search-dave.json']) {
  for (const d of fixture(f).data.devices.items) catalog.set(d.id, d);
}
for (const f of Object.values(FULL)) {
  const { patchEvents, vulnerabilityEvents, installEvents, ...device } = f.data.device;
  catalog.set(device.id, device);
}

function claims(req) {
  const match = /^Bearer (.+)$/.exec(req.headers.authorization ?? '');
  try {
    return match
      ? JSON.parse(Buffer.from(match[1].split('.')[1], 'base64url').toString('utf8'))
      : null;
  } catch {
    return null;
  }
}

function search(user, { search, first = 25, offset = 0 }) {
  const term = (search ?? '').toLowerCase();
  const items = [...catalog.values()]
    .filter((d) => d.tenantId === user.tenantId)
    .filter((d) => !term || [d.id, d.hostname, d.os].some((v) => v.toLowerCase().includes(term)))
    .sort((a, b) => a.hostname.localeCompare(b.hostname) || a.id.localeCompare(b.id));
  const take = Math.min(Math.max(first, 1), 100);
  return {
    data: { devices: { totalCount: items.length, items: items.slice(offset, offset + take) } },
  };
}

function timeline(user, { id, since, until }) {
  const device = catalog.get(id);
  if (!device || device.tenantId !== user.tenantId) return { data: { device: null } };
  const base = FULL[device.tenantId].data.device;
  const result = JSON.parse(JSON.stringify(base).replaceAll(base.id, id)); // event ids carry the device id
  Object.assign(result, device);
  const from = since ? Date.parse(since) : -Infinity;
  const to = until ? Date.parse(until) : Infinity;
  const errors = [];
  for (const [field, service] of Object.entries(SECTIONS)) {
    if (user.services?.includes(service)) {
      result[field] = result[field].filter(
        (e) => Date.parse(e.occurredAt) >= from && Date.parse(e.occurredAt) <= to,
      );
    } else {
      result[field] = null;
      errors.push({ ...DENIED_ERROR, path: ['device', field] });
    }
  }
  return errors.length ? { errors, data: { device: result } } : { data: { device: result } };
}

async function graphql(req, body) {
  const requestMode = req.headers['x-mock-mode'] ?? mode;
  const user = claims(req);
  if (!user || requestMode === 'unauthenticated') return { status: 401 };
  const { operationName, variables = {} } = body;
  if (operationName === 'DeviceSearch') return { status: 200, json: search(user, variables) };
  if (operationName !== 'DeviceTimeline') {
    return {
      status: 200,
      json: { errors: [{ message: `mock-gateway: unknown operation ${operationName}` }] },
    };
  }
  if (requestMode === 'auto') return { status: 200, json: timeline(user, variables) };
  if (requestMode === 'outage-pause') await new Promise((r) => setTimeout(r, 5000));
  return { status: 200, json: fixture(VERBATIM[requestMode]) };
}

createServer(async (req, res) => {
  const url = new URL(req.url ?? '/', 'http://localhost');
  try {
    if (req.method === 'GET' && url.pathname === '/tokens.json') {
      res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      return res.end(readFileSync(join(here, 'tokens.dev.json')));
    }
    if (req.method === 'GET' && url.pathname === '/__mock') {
      const next = url.searchParams.get('mode');
      if (next && !MODES.includes(next)) {
        res.writeHead(400, { 'Content-Type': 'text/plain' });
        return res.end(`unknown mode ${next}; one of ${MODES.join(', ')}\n`);
      }
      if (next) mode = next;
      res.writeHead(200, { 'Content-Type': 'text/plain' });
      return res.end(`mode: ${mode}\n`);
    }
    if (req.method === 'POST' && url.pathname === '/graphql') {
      let raw = '';
      for await (const chunk of req) raw += chunk;
      const { status, json } = await graphql(req, JSON.parse(raw));
      console.log(
        `${new Date().toISOString()} ${status} ${JSON.parse(raw).operationName} (${req.headers['x-mock-mode'] ?? mode})`,
      );
      if (!json) return res.writeHead(status, { 'Content-Length': '0' }).end();
      res.writeHead(status, { 'Content-Type': 'application/graphql-response+json; charset=utf-8' });
      return res.end(JSON.stringify(json));
    }
    res.writeHead(404).end();
  } catch (error) {
    res.writeHead(500, { 'Content-Type': 'text/plain' }).end(String(error));
  }
}).listen(port, () => {
  if (!MODES.includes(mode)) throw new Error(`MOCK_MODE=${mode}: one of ${MODES.join(', ')}`);
  console.log(
    `mock-gateway on http://localhost:${port} (mode: ${mode}; switch: /__mock?mode=<${MODES.join('|')}>)`,
  );
});
