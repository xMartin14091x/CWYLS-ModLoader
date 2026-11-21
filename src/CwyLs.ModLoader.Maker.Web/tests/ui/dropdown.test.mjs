import test from 'node:test';
import assert from 'node:assert/strict';
import { renderDropdown, openPop, closePop } from '../../ui/zones/dropdown.js';

test('renderDropdown renders custom button, ARIA listbox with data-motion="pop", and hidden synchronized select', () => {
  const html = renderDropdown({
    id: 'dd-test',
    dataCh: 'scnfilter',
    value: 'pomodoro',
    ariaLabel: 'Scenes',
    options: [
      { value: '', label: 'All scenes' },
      { value: 'pomodoro', label: 'Pomodoro (6)' },
      { value: 'night', label: 'Late night (4)' }
    ]
  });
  assert.match(html, /class="sel c-dd"/);
  assert.match(html, /<select class="c-dd-native" hidden aria-hidden="true" tabindex="-1" data-ch="scnfilter"/);
  assert.match(html, /<option value="pomodoro" selected>Pomodoro \(6\)<\/option>/);
  assert.match(html, /class="c-dd-btn" data-dd-btn="dd-test" aria-haspopup="listbox" aria-expanded="false"/);
  assert.match(html, /<span class="c-dd-val">Pomodoro \(6\)<\/span>/);
  assert.match(html, /class="c-dd-list menu" id="dd-test-list" role="listbox" data-motion="pop" hidden/);
  assert.match(html, /id="dd-test-opt-1" role="option" aria-selected="true" data-dd-opt="pomodoro"/);
});

test('openPop and closePop transition through is-opening -> is-open -> is-closing -> hidden', async () => {
  const classes = new Set();
  const panel = {
    hidden: true,
    classList: {
      add: (...c) => c.forEach((x) => classes.add(x)),
      remove: (...c) => c.forEach((x) => classes.delete(x)),
      contains: (x) => classes.has(x)
    }
  };
  openPop(panel);
  assert.equal(panel.hidden, false);
  assert.equal(classes.has('is-opening'), true);
  await new Promise((r) => setTimeout(r, 15));
  assert.equal(classes.has('is-opening'), false);
  assert.equal(classes.has('is-open'), true);

  let closed = false;
  closePop(panel, () => { closed = true; });
  assert.equal(classes.has('is-open'), false);
  assert.equal(classes.has('is-closing'), true);
  await new Promise((r) => setTimeout(r, 15));
  assert.equal(classes.has('is-closing'), false);
  assert.equal(panel.hidden, true);
  assert.equal(closed, true);
});
