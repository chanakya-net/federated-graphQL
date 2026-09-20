import type { SoftwareKeyInput } from '../graphql/types';
import type { SearchCapability } from '../graphql/types';

// Pure model of the "Find devices" page: an ordered list of filters joined by AND / OR, and how that
// lives in the URL.

/** The subgraphs cap a selection at 50 keys per query; the expression is capped lower to stay readable. */
export const MAX_FILTERS = 20;

export const PAGE_SIZE = 25;

/** Provider categories are an extensible server contract, separate from timeline sources. */
export type Category = string;

/** Unknown URL providers stay visible until the server reports the invalid category. */
export function providerMeta(
  capabilities: readonly SearchCapability[],
  category: string,
): SearchCapability {
  return (
    capabilities.find((capability) => capability.category === category) ?? {
      category,
      name: category,
      icon: 'filter_alt',
      color: '#616161',
      placeholder: 'Type to search',
      filterKind: 'catalog',
      available: false,
    }
  );
}

export type Connector = 'and' | 'or';

/** One filter of the expression: an item of a category, joined to the previous filter by `connector`. */
export interface Filter {
  category: Category;
  /** The query argument: a patch id, a CVE id, or a software key (`Name` or `Name|version`). */
  key: string;
  /** Ignored on the first filter. */
  connector: Connector;
}

/** A catalog item as a picker offers it: the key is what the query receives. */
export interface CatalogPick {
  key: string;
  label: string;
  detail?: string;
}

/** `?sw=Name` matches any version; `?sw=Name|1.2.3` one version. No catalog name contains the separator. */
export const SOFTWARE_SEPARATOR = '|';

export function softwareKeyOf(param: string): SoftwareKeyInput {
  const at = param.indexOf(SOFTWARE_SEPARATOR);
  if (at < 0) return { name: param.trim(), version: null };
  const version = param.slice(at + 1).trim();
  return { name: param.slice(0, at).trim(), version: version || null };
}

export function softwareParamOf(key: SoftwareKeyInput): string {
  return key.version ? `${key.name}${SOFTWARE_SEPARATOR}${key.version}` : key.name;
}

/** The label of a software key as the chips and the result cells show it. */
export function softwareLabelOf(key: SoftwareKeyInput): string {
  return key.version ? `${key.name} ${key.version}` : `${key.name} (any version)`;
}

/** A query parameter as the router binds it: absent, one value, or one per repetition. */
export type ParamValue = string | readonly string[] | undefined | null;

export function paramList(value: ParamValue): string[] {
  const raw = value == null ? [] : Array.isArray(value) ? value : [value as string];
  return raw.map((v) => String(v).trim()).filter((v) => v.length > 0);
}

// One `f` parameter per filter, in order: `<category>:<key>` for the first, `<and|or>:<category>:<key>`
// after it, e.g. ?f=cve:CVE-2026-10166&f=and:patch:patch-0128&f=or:sw:Git|20.7.8
const CATEGORY_TOKENS: Record<string, Category> = {
  patch: 'patch',
  cve: 'vulnerability',
  sw: 'softwareinstall',
};
const TOKEN_OF: Record<Category, string> = {
  patch: 'patch',
  vulnerability: 'cve',
  softwareinstall: 'sw',
};

export function parseFilter(param: string, first: boolean): Filter | null {
  let rest = param;
  let connector: Connector = 'and';
  if (!first) {
    const at = rest.indexOf(':');
    const head = at < 0 ? '' : rest.slice(0, at);
    if (head === 'and' || head === 'or') {
      connector = head;
      rest = rest.slice(at + 1);
    }
  }
  const at = rest.indexOf(':');
  if (at < 0) return null;
  const token = rest.slice(0, at).trim();
  const category = Object.hasOwn(CATEGORY_TOKENS, token) ? CATEGORY_TOKENS[token] : token;
  const key = rest.slice(at + 1).trim();
  return category && key ? { category, key, connector } : null;
}

/** Duplicates (same category and key) are dropped; the first occurrence keeps its place. */
export function parseFilters(value: ParamValue): Filter[] {
  const filters: Filter[] = [];
  const seen = new Set<string>();
  for (const param of paramList(value)) {
    const filter = parseFilter(param, filters.length === 0);
    if (!filter || seen.has(filterKey(filter))) continue;
    seen.add(filterKey(filter));
    filters.push(filter);
    if (filters.length === MAX_FILTERS) break;
  }
  return filters;
}

export function filterParams(filters: readonly Filter[]): string[] {
  return filters.map((f, i) => {
    const item = `${Object.hasOwn(TOKEN_OF, f.category) ? TOKEN_OF[f.category] : f.category}:${f.key}`;
    return i === 0 ? item : `${f.connector}:${item}`;
  });
}

/** Identity of a filter's item: category + key. The connector is not part of it. */
export function filterKey(f: Pick<Filter, 'category' | 'key'>): string {
  return `${f.category}:${f.key}`;
}

/** The distinct keys of one category, in expression order (what that category's query receives). */
export function keysOf(filters: readonly Filter[], category: Category): string[] {
  const keys: string[] = [];
  for (const f of filters) if (f.category === category && !keys.includes(f.key)) keys.push(f.key);
  return keys;
}

export function categoriesOf(filters: readonly Filter[]): Category[] {
  const out: Category[] = [];
  for (const f of filters) if (!out.includes(f.category)) out.push(f.category);
  return out;
}

export function filtersEqual(a: readonly Filter[], b: readonly Filter[]): boolean {
  return filterParams(a).join('\n') === filterParams(b).join('\n');
}

/** `?page=2` -> 2; anything else -> 0. */
export function pageParam(value: ParamValue): number {
  const first = Array.isArray(value) ? value[0] : value;
  return Math.max(0, Number.parseInt(String(first ?? '0'), 10) || 0);
}
