// Help follows the visible Packs subpage, not just the top-level tab.
export function helpPage(s) {
  return s?.page === 'packs' && (s.packsSub === 'newpack' || s.modal === 'newpack') ? 'newpack' : s?.page || 'studio';
}

export const PAGE_TERMS = {
  studio: ['subtitle', 'take', 'reading', 'origin'],
  lines: ['scene', 'status', 'coverage'],
  packs: ['pack', 'editing', 'loadorder', 'manifest'],
  newpack: ['id', 'language', 'icon', 'manifest'],
  export: ['manifest', 'version', 'zip', 'symlink', 'coverage'],
  settings: ['default', 'origin', 'workflow', 'motion']
};

export const GUIDE_HINT_KEY = 'cwyls-maker-guide-hint';
export function claimGuideHint(storage = globalThis.localStorage) {
  try {
    if (storage?.getItem(GUIDE_HINT_KEY)) return false;
    storage?.setItem(GUIDE_HINT_KEY, '1');
    return true;
  } catch { return false; }
}
