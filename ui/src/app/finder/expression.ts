import type { Filter } from './finder.models';

/** The expression as one line: `KB5000128 AND CVE-2026-10166 OR Git 20.7.8`. */
export function describe(filters: readonly Filter[], label: (f: Filter) => string): string {
  return filters
    .map((f, i) => (i === 0 ? label(f) : `${f.connector.toUpperCase()} ${label(f)}`))
    .join(' ');
}
