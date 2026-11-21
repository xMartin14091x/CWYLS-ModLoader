// Shared custom dropdown component and `data-motion="pop"` popup lifecycle (MOTION-CONTRACT.md).
// Replaces every native <select> in the UI with a styled button + ARIA listbox (`data-motion="pop"`),
// full keyboard support (ArrowDown, ArrowUp, Home, End, Enter, Escape, type-ahead), and outside-click close,
// while keeping a hidden synchronized <select> so existing programmatic `change` events work seamlessly.
import { esc } from '../util.js';
import { I } from './icons.js';

const popTimers = new WeakMap();

/** Reads `--motion-small-ms` from `:root` per MOTION-CONTRACT.md (defaults to 0 when unset). */
export function motionSmallMs() {
  if (typeof document === 'undefined' || typeof getComputedStyle !== 'function') return 0;
  return parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--motion-small-ms')) || 0;
}

/**
 * Opens a `data-motion="pop"` panel per MOTION-CONTRACT.md:
 * remove `hidden`, add `is-opening`, then after `--motion-small-ms` swap to `is-open`.
 */
export function openPop(panel) {
  if (!panel) return;
  const prev = popTimers.get(panel);
  if (prev) clearTimeout(prev);
  panel.hidden = false;
  panel.classList.remove('is-closing', 'is-open');
  panel.classList.add('is-opening');
  const ms = motionSmallMs();
  const tid = setTimeout(() => {
    popTimers.delete(panel);
    if (!panel.hidden) {
      panel.classList.remove('is-opening');
      panel.classList.add('is-open');
    }
  }, ms);
  popTimers.set(panel, tid);
}

/**
 * Closes a `data-motion="pop"` panel per MOTION-CONTRACT.md:
 * add `is-closing`, wait `--motion-small-ms`, then set `hidden` (and invoke optional `onDone`).
 */
export function closePop(panel, onDone) {
  if (!panel) { onDone?.(); return; }
  const prev = popTimers.get(panel);
  if (prev) clearTimeout(prev);
  panel.classList.remove('is-opening', 'is-open');
  panel.classList.add('is-closing');
  const ms = motionSmallMs();
  const tid = setTimeout(() => {
    popTimers.delete(panel);
    panel.classList.remove('is-closing');
    panel.hidden = true;
    onDone?.();
  }, ms);
  popTimers.set(panel, tid);
}

/**
 * Renders a custom dropdown with an ARIA listbox panel (`data-motion="pop"`) and a hidden synchronized `<select>`.
 * @param {{
 *   id: string,
 *   selectId?: string,
 *   dataCh?: string,
 *   value?: string,
 *   options: Array<{value: string, label: string}>,
 *   disabled?: boolean,
 *   ariaLabel?: string,
 *   className?: string
 * }} spec
 */
export function renderDropdown(spec) {
  const {
    id,
    selectId = '',
    dataCh = '',
    value = '',
    options = [],
    disabled = false,
    ariaLabel = '',
    className = ''
  } = spec;
  const strVal = String(value ?? '');
  const selIdx = Math.max(0, options.findIndex((o) => String(o.value) === strVal));
  const selOpt = options[selIdx] || options[0] || { value: '', label: '—' };
  const listId = `${id}-list`;

  const nativeAttrs = [
    'class="c-dd-native"',
    'hidden',
    'aria-hidden="true"',
    'tabindex="-1"',
    selectId ? `id="${esc(selectId)}"` : '',
    dataCh ? `data-ch="${esc(dataCh)}"` : '',
    ariaLabel ? `aria-label="${esc(ariaLabel)}"` : '',
    disabled ? 'disabled' : ''
  ].filter(Boolean).join(' ');

  const nativeOpts = options
    .map((o, i) => `<option value="${esc(o.value)}"${i === selIdx ? ' selected' : ''}>${esc(o.label)}</option>`)
    .join('');

  const listOpts = options
    .map((o, i) => {
      const isSel = i === selIdx;
      const optId = `${id}-opt-${i}`;
      return `<div class="c-dd-opt${isSel ? ' is-selected is-active' : ''}" id="${esc(optId)}" role="option" aria-selected="${isSel}" data-dd-opt="${esc(o.value)}" data-idx="${i}">` +
        `<span class="c-dd-opt-t">${esc(o.label)}</span>` +
        `<i class="ico ico-xs c-dd-chk" aria-hidden="true">${isSel ? I.check : ''}</i>` +
        `</div>`;
    })
    .join('');

  return `<span class="sel c-dd${disabled ? ' is-disabled' : ''}${className ? ` ${esc(className)}` : ''}" data-dd="${esc(id)}">` +
    `<select ${nativeAttrs}>${nativeOpts}</select>` +
    `<button type="button" class="c-dd-btn" data-dd-btn="${esc(id)}" aria-haspopup="listbox" aria-expanded="false" role="combobox" aria-controls="${esc(listId)}"${ariaLabel ? ` aria-label="${esc(ariaLabel)}"` : ''}${disabled ? ' disabled' : ''}>` +
    `<span class="c-dd-val">${esc(selOpt.label)}</span>` +
    `<i class="ico ico-xs c-dd-chev" aria-hidden="true">${I.chev}</i>` +
    `</button>` +
    `<div class="c-dd-list menu" id="${esc(listId)}" role="listbox" data-motion="pop" hidden tabindex="-1"${ariaLabel ? ` aria-label="${esc(ariaLabel)}"` : ''}>` +
    listOpts +
    `</div>` +
    `</span>`;
}

/** Synchronizes a `.c-dd` custom dropdown UI with its underlying `<select>` value. */
export function syncDropdownFromSelect(selectEl) {
  const wrap = selectEl?.closest?.('.c-dd');
  if (!wrap) return;
  const val = String(selectEl.value ?? '');
  const opts = [...wrap.querySelectorAll('.c-dd-opt')];
  let matchedLabel = null;
  let activeId = '';
  opts.forEach((opt) => {
    const isSel = opt.dataset.ddOpt === val;
    opt.classList.toggle('is-selected', isSel);
    opt.classList.toggle('is-active', isSel);
    opt.setAttribute('aria-selected', isSel ? 'true' : 'false');
    const chk = opt.querySelector('.c-dd-chk');
    if (chk) chk.innerHTML = isSel ? I.check : '';
    if (isSel) {
      matchedLabel = opt.querySelector('.c-dd-opt-t')?.textContent ?? '';
      activeId = opt.id;
    }
  });
  const valEl = wrap.querySelector('.c-dd-val');
  if (valEl && matchedLabel !== null) valEl.textContent = matchedLabel;
  // Focus stays on the button, so it (not the unfocused listbox) carries the active option for screen readers.
  const btnEl = wrap.querySelector('.c-dd-btn');
  if (btnEl && activeId && wrap.classList.contains('open')) btnEl.setAttribute('aria-activedescendant', activeId);
}

/** Closes one `.c-dd` custom dropdown with `data-motion="pop"` closing state. */
export function closeDropdown(wrap) {
  if (!wrap) return;
  const btn = wrap.querySelector('.c-dd-btn');
  const list = wrap.querySelector('.c-dd-list');
  wrap.classList.remove('open');
  if (btn) { btn.setAttribute('aria-expanded', 'false'); btn.removeAttribute('aria-activedescendant'); }
  if (list && !list.hidden) closePop(list);
}

/** Closes all open `.c-dd` custom dropdowns except `exceptWrap`. */
export function closeAllDropdowns(exceptWrap = null) {
  if (typeof document === 'undefined') return;
  document.querySelectorAll('.c-dd.open').forEach((w) => {
    if (w !== exceptWrap) closeDropdown(w);
  });
}

/** Opens a `.c-dd` custom dropdown and animates its listbox with `data-motion="pop"`. */
export function openDropdown(wrap) {
  if (!wrap || wrap.classList.contains('is-disabled')) return;
  const btn = wrap.querySelector('.c-dd-btn');
  const list = wrap.querySelector('.c-dd-list');
  if (!btn || !list || btn.disabled) return;
  closeAllDropdowns(wrap);
  wrap.classList.add('open');
  btn.setAttribute('aria-expanded', 'true');
  openPop(list);
  const selOpt = list.querySelector('.c-dd-opt.is-selected') || list.querySelector('.c-dd-opt');
  if (selOpt) setActiveOption(wrap, selOpt);
}

function setActiveOption(wrap, optEl) {
  if (!wrap || !optEl) return;
  const list = wrap.querySelector('.c-dd-list');
  list?.querySelectorAll('.c-dd-opt.is-active').forEach((el) => el.classList.remove('is-active'));
  optEl.classList.add('is-active');
  const btn = wrap.querySelector('.c-dd-btn');
  if (btn && optEl.id) btn.setAttribute('aria-activedescendant', optEl.id);
  optEl.scrollIntoView?.({ block: 'nearest' });
}

function commitOption(wrap, optEl) {
  if (!wrap || !optEl) return;
  const newVal = optEl.dataset.ddOpt ?? '';
  const selectEl = wrap.querySelector('select.c-dd-native');
  const btn = wrap.querySelector('.c-dd-btn');
  const unchanged = !selectEl || String(selectEl.value ?? '') === newVal;
  closeDropdown(wrap);
  btn?.focus?.();
  // Re-picking the current option is not a change: firing `change` would reload the catalog or reset what was typed.
  if (selectEl && !unchanged) {
    selectEl.value = newVal;
    syncDropdownFromSelect(selectEl);
    selectEl.dispatchEvent(new Event('change', { bubbles: true }));
  }
}

/**
 * Installs delegated click & keyboard handlers for `.c-dd` custom dropdowns on `document`.
 * Supports ArrowDown, ArrowUp, Home, End, Enter, Space, Escape, type-ahead, and outside click.
 */
export function bindDropdownEvents(onOpenCallback) {
  let typeBuffer = '';
  let typeTimer = null;
  // The callback may repaint the page (it closes an open menu), which replaces this dropdown's element: open the live one.
  function openWithCallback(wrap) {
    const id = wrap.dataset?.dd;
    onOpenCallback?.();
    const live = wrap.isConnected === false && id ? document.querySelector(`[data-dd="${id}"]`) : null;
    openDropdown(live || wrap);
  }

  document.addEventListener('click', (ev) => {
    const optEl = ev.target.closest?.('.c-dd-opt');
    if (optEl) {
      ev.preventDefault();
      ev.stopPropagation();
      const wrap = optEl.closest('.c-dd');
      commitOption(wrap, optEl);
      return;
    }
    const btnEl = ev.target.closest?.('.c-dd-btn');
    if (btnEl) {
      ev.preventDefault();
      ev.stopPropagation();
      const wrap = btnEl.closest('.c-dd');
      if (!wrap || btnEl.disabled) return;
      if (wrap.classList.contains('open')) {
        closeDropdown(wrap);
      } else {
        openWithCallback(wrap);
      }
      return;
    }
    if (!ev.target.closest?.('.c-dd')) {
      closeAllDropdowns();
    }
  });

  document.addEventListener('focusin', (ev) => closeAllDropdowns(ev.target.closest?.('.c-dd')));
  document.addEventListener('keydown', (ev) => {
    const wrap = ev.target.closest?.('.c-dd');
    if (!wrap) return;
    const isOpen = wrap.classList.contains('open');
    if (ev.key === 'Tab') { closeDropdown(wrap); return; }
    const btn = wrap.querySelector('.c-dd-btn');
    if (!btn || btn.disabled) return;
    const opts = [...wrap.querySelectorAll('.c-dd-opt')];
    if (!opts.length) return;
    const curIdx = Math.max(0, opts.findIndex((o) => o.classList.contains('is-active')));

    if (ev.key === 'Escape') {
      if (isOpen) {
        ev.preventDefault();
        ev.stopPropagation();
        closeDropdown(wrap);
        btn.focus();
      }
      return;
    }
    if (ev.key === 'ArrowDown') {
      ev.preventDefault();
      ev.stopPropagation();
      if (!isOpen) {
        openWithCallback(wrap);
      } else {
        setActiveOption(wrap, opts[Math.min(opts.length - 1, curIdx + 1)]);
      }
      return;
    }
    if (ev.key === 'ArrowUp') {
      ev.preventDefault();
      ev.stopPropagation();
      if (!isOpen) {
        openWithCallback(wrap);
      } else {
        setActiveOption(wrap, opts[Math.max(0, curIdx - 1)]);
      }
      return;
    }
    if (ev.key === 'Home' && isOpen) {
      ev.preventDefault();
      ev.stopPropagation();
      setActiveOption(wrap, opts[0]);
      return;
    }
    if (ev.key === 'End' && isOpen) {
      ev.preventDefault();
      ev.stopPropagation();
      setActiveOption(wrap, opts[opts.length - 1]);
      return;
    }
    if (ev.key === 'Enter' || (ev.key === ' ' && isOpen)) {
      ev.preventDefault();
      ev.stopPropagation();
      if (!isOpen) {
        openWithCallback(wrap);
      } else {
        commitOption(wrap, opts[curIdx] || opts[0]);
      }
      return;
    }
    // Type-ahead search when focused on dropdown button or open listbox
    if (ev.key.length === 1 && !ev.ctrlKey && !ev.metaKey && !ev.altKey && ev.key !== ' ') {
      ev.preventDefault();
      ev.stopPropagation();
      if (typeTimer) clearTimeout(typeTimer);
      typeBuffer += ev.key.toLowerCase();
      typeTimer = setTimeout(() => { typeBuffer = ''; typeTimer = null; }, 500);
      const match =
        opts.find((o) => (o.querySelector('.c-dd-opt-t')?.textContent || '').trim().toLowerCase().startsWith(typeBuffer)) ||
        opts.find((o) => (o.querySelector('.c-dd-opt-t')?.textContent || '').trim().toLowerCase().includes(typeBuffer));
      if (match) {
        if (isOpen) {
          setActiveOption(wrap, match);
        } else {
          commitOption(wrap, match);
        }
      }
    }
  }, true);
}
