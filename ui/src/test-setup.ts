// Node 25+ defines its own experimental `localStorage` global, which is undefined unless Node runs
// with --localstorage-file, and it hides jsdom's. The specs need a working one.
if (typeof globalThis.localStorage === 'undefined') {
  const jsdom = (globalThis as { jsdom?: { window: { localStorage: Storage } } }).jsdom;
  Object.defineProperty(globalThis, 'localStorage', {
    value: jsdom?.window.localStorage,
    configurable: true,
    writable: true,
  });
}
