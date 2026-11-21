import test from 'node:test';
import assert from 'node:assert/strict';
import { createDraft, reduce, wireText, DEBOUNCE_MS } from '../../ui/logic/draft.js';

// A tiny driver: feeds events through the reducer and records every effect, like main.js will.
function driver(text = 'old', revision = 'r1') {
  let state = createDraft('p/th/line1', text, revision);
  const effects = [];
  const send = (e) => { const r = reduce(state, e); state = r.state; effects.push(...r.effects); return r.effects; };
  return { get state() { return state; }, effects, send };
}
const op = (n) => String(n).padStart(32, '0');

test('typing arms the debounce once per change and does not save yet', () => {
  const d = driver();
  assert.deepEqual(d.send({ type: 'edit', text: 'new' }), [{ type: 'arm-timer', ms: DEBOUNCE_MS }]);
  assert.equal(d.state.status, 'typing');
  assert.deepEqual(d.send({ type: 'edit', text: 'new' }), [], 'same text is a no-op');
});

test('typing back to the saved text cancels the pending save', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'x' });
  assert.deepEqual(d.send({ type: 'edit', text: 'old' }), [{ type: 'cancel-timer' }]);
  assert.equal(d.state.status, 'saved');
  assert.deepEqual(d.send({ type: 'flush', opId: op(1) }), [], 'nothing dirty, nothing sent');
});

test('flush sends one save with the base revision; a second flush while in flight sends nothing', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'new' });
  assert.deepEqual(d.send({ type: 'flush', opId: op(1) }), [{ type: 'save', opId: op(1), text: 'new', expectedRevision: 'r1' }]);
  assert.equal(d.state.status, 'saving');
  assert.deepEqual(d.send({ type: 'flush', opId: op(2) }), []);
});

test('text typed while a save is in flight is coalesced and saved right after, with the new revision', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'one' });
  d.send({ type: 'flush', opId: op(1) });
  assert.deepEqual(d.send({ type: 'edit', text: 'one two' }), [], 'no timer while saving');
  assert.deepEqual(d.send({ type: 'saved', opId: op(1), revision: 'r2' }), [{ type: 'arm-timer', ms: 0 }]);
  assert.equal(d.state.status, 'typing');
  assert.equal(d.state.baseRevision, 'r2');
  assert.deepEqual(d.send({ type: 'flush', opId: op(2) }), [{ type: 'save', opId: op(2), text: 'one two', expectedRevision: 'r2' }]);
});

test('a response for another operation is ignored', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'new' });
  d.send({ type: 'flush', opId: op(1) });
  const before = d.state;
  d.send({ type: 'saved', opId: op(9), revision: 'rX' });
  d.send({ type: 'failed', opId: op(9), code: 'internal' });
  assert.equal(d.state, before);
});

test('conflict keeps the creator draft, asks for the server version and waits for a choice', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'mine' });
  d.send({ type: 'flush', opId: op(1) });
  assert.deepEqual(d.send({ type: 'failed', opId: op(1), code: 'conflict' }), [{ type: 'fetch-line' }]);
  assert.equal(d.state.status, 'conflict');
  assert.equal(d.state.text, 'mine');
  d.send({ type: 'edit', text: 'mine!' });
  assert.deepEqual(d.send({ type: 'flush', opId: op(2) }), [], 'no save while in conflict');
  d.send({ type: 'server', text: 'theirs', revision: 'r5' });
  assert.deepEqual(d.state.theirs, { text: 'theirs', revision: 'r5' });
  assert.equal(d.state.text, 'mine!', 'still not overwritten');
});

test('conflict resolved with "keep mine" saves my text on top of their revision', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'mine' });
  d.send({ type: 'flush', opId: op(1) });
  d.send({ type: 'failed', opId: op(1), code: 'conflict' });
  d.send({ type: 'server', text: 'theirs', revision: 'r5' });
  assert.deepEqual(d.send({ type: 'resolve', choice: 'mine' }), [{ type: 'arm-timer', ms: 0 }]);
  assert.deepEqual(d.send({ type: 'flush', opId: op(2) }), [{ type: 'save', opId: op(2), text: 'mine', expectedRevision: 'r5' }]);
});

test('conflict resolved with "use theirs" adopts the server text', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'mine' });
  d.send({ type: 'flush', opId: op(1) });
  d.send({ type: 'failed', opId: op(1), code: 'conflict' });
  d.send({ type: 'server', text: 'theirs', revision: 'r5' });
  d.send({ type: 'resolve', choice: 'theirs' });
  assert.deepEqual([d.state.text, d.state.baseText, d.state.baseRevision, d.state.status], ['theirs', 'theirs', 'r5', 'saved']);
});

test('a conflict whose server text equals mine settles without asking', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'same' });
  d.send({ type: 'flush', opId: op(1) });
  d.send({ type: 'failed', opId: op(1), code: 'conflict' });
  d.send({ type: 'server', text: 'same', revision: 'r7' });
  assert.deepEqual([d.state.status, d.state.baseRevision], ['saved', 'r7']);
});

test('an uncertain failure asks the server about the operation before any retry, and reuses the operationId', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'new' });
  d.send({ type: 'flush', opId: op(1) });
  assert.deepEqual(d.send({ type: 'failed', opId: op(1), code: 'unavailable', uncertain: true }), [{ type: 'check-operation', opId: op(1) }]);
  assert.equal(d.state.status, 'saving');
  assert.deepEqual(d.send({ type: 'operation', opId: op(1), saved: false }), [{ type: 'save', opId: op(1), text: 'new', expectedRevision: 'r1' }]);
});

test('an uncertain failure that actually committed is treated as saved, not replayed', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'new' });
  d.send({ type: 'flush', opId: op(1) });
  d.send({ type: 'failed', opId: op(1), uncertain: true });
  assert.deepEqual(d.send({ type: 'operation', opId: op(1), saved: true, revision: 'r2' }), []);
  assert.deepEqual([d.state.status, d.state.baseRevision, d.state.baseText], ['saved', 'r2', 'new']);
});

test('transient failures retry with growing backoff; permanent ones stop and keep the text', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'new' });
  d.send({ type: 'flush', opId: op(1) });
  const first = d.send({ type: 'failed', opId: op(1), code: 'unavailable' });
  assert.equal(first[0].type, 'arm-retry');
  d.send({ type: 'retry', opId: op(2) });
  const second = d.send({ type: 'failed', opId: op(2), code: 'unavailable' });
  assert.ok(second[0].ms > first[0].ms);
  d.send({ type: 'retry', opId: op(3) });
  assert.deepEqual(d.send({ type: 'failed', opId: op(3), code: 'read_only_pack' }), []);
  assert.deepEqual([d.state.status, d.state.error, d.state.text], ['failed', 'read_only_pack', 'new']);
});

test('a refetched server version replaces a clean draft but never a dirty one', () => {
  const clean = driver();
  clean.send({ type: 'server', text: 'from another tab', revision: 'r9' });
  assert.deepEqual([clean.state.text, clean.state.baseRevision, clean.state.status], ['from another tab', 'r9', 'saved']);
  const dirty = driver();
  dirty.send({ type: 'edit', text: 'typing' });
  dirty.send({ type: 'server', text: 'from another tab', revision: 'r9' });
  assert.deepEqual([dirty.state.text, dirty.state.baseRevision], ['typing', 'r1']);
});

test('clearing the text sends a removal; wireText maps empty to null', () => {
  const d = driver();
  d.send({ type: 'edit', text: '' });
  const [save] = d.send({ type: 'flush', opId: op(1) });
  assert.equal(wireText(save.text), null);
  assert.equal(wireText('x'), 'x');
});

test('draft keys isolate lines: each key has its own state and a late result cannot cross over', () => {
  const a = driver('A', 'ra'), b = createDraft('p/th/line2', 'B', 'rb');
  a.send({ type: 'edit', text: 'A!' });
  a.send({ type: 'flush', opId: op(1) });
  const r = reduce(b, { type: 'saved', opId: op(1), revision: 'rZ' });
  assert.equal(r.state, b, 'line2 ignores the response meant for line1');
});

test('while typing, a refetched pack text that differs is a conflict at once (before any save), and the draft is kept', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'typing' });
  const effects = d.send({ type: 'server', text: 'someone else', revision: 'r7' });
  assert.deepEqual(effects, [{ type: 'cancel-timer' }]);
  assert.equal(d.state.status, 'conflict');
  assert.equal(d.state.text, 'typing');
  assert.deepEqual(d.state.theirs, { text: 'someone else', revision: 'r7' });
  assert.deepEqual(d.send({ type: 'flush', opId: op(1) }), [], 'no save while in conflict');
});

test('while typing, a refetch with the same pack text only moves the revision; with my text it settles', () => {
  const a = driver();
  a.send({ type: 'edit', text: 'typing' });
  a.send({ type: 'server', text: 'old', revision: 'r8' });
  assert.deepEqual([a.state.status, a.state.baseRevision], ['typing', 'r8']);
  const b = driver();
  b.send({ type: 'edit', text: 'same' });
  b.send({ type: 'server', text: 'same', revision: 'r9' });
  assert.deepEqual([b.state.status, b.state.baseRevision, b.state.baseText], ['saved', 'r9', 'same']);
});

test('a refetch that arrives while our save is in flight is ignored: the save\'s own answer decides', () => {
  const d = driver();
  d.send({ type: 'edit', text: 'mine' });
  d.send({ type: 'flush', opId: op(1) });
  const before = d.state;
  d.send({ type: 'server', text: 'theirs', revision: 'r5' });
  assert.equal(d.state, before);
});

test('a revision event moves the base revision (status or star changed on the same line) but never during a conflict', () => {
  const d = driver();
  d.send({ type: 'revision', revision: 'r4' });
  assert.equal(d.state.baseRevision, 'r4');
  d.send({ type: 'edit', text: 'x' });
  d.send({ type: 'flush', opId: op(1) });
  d.send({ type: 'failed', opId: op(1), code: 'conflict' });
  d.send({ type: 'revision', revision: 'r9' });
  assert.equal(d.state.baseRevision, 'r4');
});
