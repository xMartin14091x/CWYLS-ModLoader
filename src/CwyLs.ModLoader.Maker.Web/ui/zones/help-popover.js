// One delegated, text-only popover for help and line warnings, including virtualized rows.
import { esc } from '../util.js';
import { I } from './icons.js';

export function helpButton(text, label, { warning = false, className = '' } = {}) {
  return `<button type="button" class="help-trigger ${warning ? 'm-warn' : 'cnote'} ${esc(className)}" data-help="${esc(text)}" aria-label="${esc(label)}" aria-expanded="false" aria-controls="studio-help"><i class="ico ico-xs">${warning ? I.warn : I.info}</i></button>`;
}

/** Hover/focus previews; click/tap pins; Escape dismisses without also invoking Studio shortcuts. */
export function bindHelpPopovers() {
  const panel = document.createElement('div');
  panel.id = 'studio-help'; panel.className = 'help-popover panel';
  panel.setAttribute('role', 'tooltip'); panel.hidden = true;
  document.body.appendChild(panel);
  let owner = null, pinned = false, dismissed = null, closeTimer;
  const trigger = (node) => node?.closest?.('[data-help]');
  function position() {
    if (!owner || panel.hidden) return;
    const r = owner.getBoundingClientRect(), p = panel.getBoundingClientRect(), gap = 8;
    const left = Math.max(gap, Math.min(innerWidth - p.width - gap, r.left));
    const below = r.bottom + gap;
    const top = below + p.height <= innerHeight - gap ? below : Math.max(gap, r.top - p.height - gap);
    panel.style.left = `${left}px`; panel.style.top = `${top}px`;
  }
  function close({ restore = false } = {}) {
    clearTimeout(closeTimer);
    const previous = owner;
    owner = null; pinned = false; panel.hidden = true;
    if (previous) {
      previous.setAttribute('aria-expanded', 'false');
      previous.removeAttribute('aria-describedby');
      if (restore && previous.isConnected) { dismissed = previous; previous.focus({ preventScroll: true }); }
    }
  }
  function show(button, pin = false) {
    clearTimeout(closeTimer);
    if (button === dismissed && !pin) return;
    if (pinned && !pin && owner !== button) return;
    if (owner !== button) close();
    owner = button; pinned = pin || pinned; dismissed = null;
    panel.textContent = button.dataset.help;
    panel.hidden = false; panel.classList.toggle('is-pinned', pinned);
    button.setAttribute('aria-expanded', 'true'); button.setAttribute('aria-describedby', panel.id);
    position();
  }
  function scheduleClose() {
    clearTimeout(closeTimer);
    closeTimer = setTimeout(() => {
      if (!pinned && !panel.matches(':hover') && owner !== document.activeElement && !owner?.matches(':hover')) close();
    }, 120);
  }
  document.addEventListener('pointerover', (event) => {
    const button = trigger(event.target);
    if (panel.contains(event.target)) clearTimeout(closeTimer);
    else if (button && !button.contains(event.relatedTarget)) show(button);
  });
  document.addEventListener('pointerout', (event) => {
    const button = trigger(event.target);
    if (button && !button.contains(event.relatedTarget)) { if (dismissed === button) dismissed = null; scheduleClose(); }
    if (panel.contains(event.target) && !panel.contains(event.relatedTarget)) scheduleClose();
  });
  document.addEventListener('focusin', (event) => { const button = trigger(event.target); if (button) show(button); });
  document.addEventListener('focusout', (event) => { if (trigger(event.target)) { dismissed = null; scheduleClose(); } });
  document.addEventListener('click', (event) => {
    const button = trigger(event.target);
    if (button) {
      event.preventDefault(); event.stopImmediatePropagation();
      if (owner === button && pinned) { dismissed = button; close(); }
      else show(button, true);
    } else if (!panel.contains(event.target)) close();
  }, true);
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && owner) {
      event.preventDefault(); event.stopImmediatePropagation(); close({ restore: true });
    }
  }, true);
  addEventListener('resize', position);
  document.addEventListener('scroll', position, true);
  // A changed line, language or virtual-list window must not leave help for a detached control on screen.
  new MutationObserver(() => { if (owner && !owner.isConnected) close(); }).observe(document.getElementById('studio'), { childList: true, subtree: true });
}
