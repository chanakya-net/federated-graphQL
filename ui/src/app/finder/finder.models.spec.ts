import {
  categoriesOf,
  filterKey,
  filterParams,
  filtersEqual,
  keysOf,
  pageParam,
  paramList,
  parseFilter,
  parseFilters,
  softwareKeyOf,
  softwareLabelOf,
  softwareParamOf,
} from './finder.models';

describe('finder.models', () => {
  it('paramList accepts one value or many and trims', () => {
    expect(paramList(undefined)).toEqual([]);
    expect(paramList(null)).toEqual([]);
    expect(paramList('patch:patch-0001')).toEqual(['patch:patch-0001']);
    expect(paramList([' a ', 'b', ''])).toEqual(['a', 'b']);
  });

  it('software keys round-trip through the key text; no version means any version', () => {
    expect(softwareKeyOf('Git|2.40.0')).toEqual({ name: 'Git', version: '2.40.0' });
    expect(softwareKeyOf('Git')).toEqual({ name: 'Git', version: null });
    expect(softwareKeyOf('Git|')).toEqual({ name: 'Git', version: null });
    expect(softwareParamOf({ name: 'Notepad++', version: '8.5.0' })).toBe('Notepad++|8.5.0');
    expect(softwareParamOf({ name: 'Git', version: null })).toBe('Git');
    expect(softwareLabelOf({ name: 'Git', version: '2.40.0' })).toBe('Git 2.40.0');
    expect(softwareLabelOf({ name: 'Git', version: null })).toBe('Git (any version)');
  });

  it('parses one f parameter: category:key, with a connector after the first', () => {
    expect(parseFilter('patch:patch-0128', true)).toEqual({
      category: 'patch',
      key: 'patch-0128',
      connector: 'and',
    });
    expect(parseFilter('or:cve:CVE-2026-10166', false)).toEqual({
      category: 'vulnerability',
      key: 'CVE-2026-10166',
      connector: 'or',
    });
    expect(parseFilter('and:sw:Git|20.7.8', false)).toEqual({
      category: 'softwareinstall',
      key: 'Git|20.7.8',
      connector: 'and',
    });
    // No connector after the first: AND. A connector on the first: ignored as a category.
    expect(parseFilter('sw:Git', false)?.connector).toBe('and');
    expect(parseFilter('or:patch:patch-0128', true)?.category).toBe('or');
    expect(parseFilter('nope:x', true)).toEqual({ category: 'nope', key: 'x', connector: 'and' });
    expect(parseFilter('patch:', true)).toBeNull();
  });

  it('parses the URL into an expression and back, dropping duplicates', () => {
    const filters = parseFilters([
      'cve:CVE-2026-10166',
      'and:patch:patch-0128',
      'or:sw:Git|20.7.8',
      'or:patch:patch-0128',
      'bogus',
    ]);
    expect(filters).toEqual([
      { category: 'vulnerability', key: 'CVE-2026-10166', connector: 'and' },
      { category: 'patch', key: 'patch-0128', connector: 'and' },
      { category: 'softwareinstall', key: 'Git|20.7.8', connector: 'or' },
    ]);
    expect(filterParams(filters)).toEqual([
      'cve:CVE-2026-10166',
      'and:patch:patch-0128',
      'or:sw:Git|20.7.8',
    ]);
    expect(filtersEqual(filters, parseFilters(filterParams(filters)))).toBe(true);
    expect(filtersEqual(filters, filters.slice(1))).toBe(false);
  });

  it('keys and categories follow expression order', () => {
    const filters = parseFilters([
      'sw:Git',
      'and:patch:patch-0128',
      'or:patch:patch-0282',
      'or:patch:patch-0128',
    ]);
    expect(filterKey(filters[0])).toBe('softwareinstall:Git');
    expect(keysOf(filters, 'patch')).toEqual(['patch-0128', 'patch-0282']);
    expect(keysOf(filters, 'vulnerability')).toEqual([]);
    expect(categoriesOf(filters)).toEqual(['softwareinstall', 'patch']);
  });

  it('pageParam is a non-negative integer, 0 by default', () => {
    expect(pageParam(undefined)).toBe(0);
    expect(pageParam('3')).toBe(3);
    expect(pageParam('-2')).toBe(0);
    expect(pageParam('abc')).toBe(0);
    expect(pageParam(['2', '5'])).toBe(2);
  });
  it('preserves canonical and unknown provider tokens without a client allowlist', () => {
    const filters = parseFilters([
      'vulnerability:CVE-1',
      'or:softwareinstall:Git',
      'and:future-provider:key:part',
      'and:__proto__:safe',
    ]);
    expect(filters.map((filter) => filter.category)).toEqual([
      'vulnerability',
      'softwareinstall',
      'future-provider',
      '__proto__',
    ]);
    expect(parseFilters(filterParams(filters))).toEqual(filters);
  });
});
