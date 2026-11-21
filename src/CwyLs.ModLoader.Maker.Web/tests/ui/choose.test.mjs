import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { chooseUi } from '../../ui/choose.js';

test('before the flip v1 is the default and ?ui=2 opens Studio v2', () => {
  assert.equal(chooseUi('', false), 1);
  assert.equal(chooseUi('?backend=http', false), 1);
  assert.equal(chooseUi('?ui=2', false), 2);
  assert.equal(chooseUi('?ui=2&backend=http&lang=th', false), 2);
  assert.equal(chooseUi('?ui=1', false), 1);
  assert.equal(chooseUi('?ui=3', false), 1, 'an unknown value follows the default');
});

test('after the flip v2 is the default and ?ui=1 keeps v1 reachable', () => {
  const boot = readFileSync(new URL('../../ui/boot.js', import.meta.url), 'utf8');
  assert.match(boot, /export const FLIPPED = true;/);
  assert.equal(chooseUi('', true), 2);
  assert.equal(chooseUi('?backend=http', true), 2);
  assert.equal(chooseUi('?ui=1', true), 1);
  assert.equal(chooseUi('?ui=2', true), 2);
  assert.equal(chooseUi('?ui=x', true), 2);
});
