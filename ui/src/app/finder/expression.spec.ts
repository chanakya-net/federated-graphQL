import { describe as describeExpression } from './expression';
import type { Filter } from './finder.models';

const patch = (key: string, connector: Filter['connector'] = 'and'): Filter => ({
  category: 'patch',
  key,
  connector,
});
const cve = (key: string, connector: Filter['connector'] = 'and'): Filter => ({
  category: 'vulnerability',
  key,
  connector,
});
const sw = (key: string, connector: Filter['connector'] = 'and'): Filter => ({
  category: 'softwareinstall',
  key,
  connector,
});

describe('describe expression', () => {
  it('describe writes the expression as a sentence', () => {
    const label = (f: Filter) => f.key;
    expect(describeExpression([patch('A'), cve('C'), sw('D', 'or')], label)).toBe('A AND C OR D');
    expect(describeExpression([], label)).toBe('');
  });
});
