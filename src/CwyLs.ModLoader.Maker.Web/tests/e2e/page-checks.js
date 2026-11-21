// Injected into the page by the CDP harness (Runtime.evaluate). Not shipped. Ported from mockup A's `runChecks`
// (research/2026-11-08/studio-v2/mockup-a/app.js), which found no problem in the mockup's 44 shots, and made reusable.
//   window.__pmChecks.run(opts)       -> {noPageScroll, clipped[], overflowing[], menus[]}
//   window.__pmChecks.negative()      -> proof that the clip check can fail: a box at line-height 1.0 must be flagged
(() => {
  const THAI = /[\u0E00-\u0E7F]/;

  /** Thai ink check: canvas measureText gives the real ink box of each text run (stacked tone marks included); the ink must
   *  sit inside every ancestor that clips (overflow hidden/clip). Scroll containers are skipped on purpose. */
  function clippedThai(root) {
    const out = [], cv = document.createElement('canvas').getContext('2d');
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    let n;
    while ((n = walker.nextNode())) {
      if (!THAI.test(n.nodeValue)) continue;
      const el = n.parentElement;
      if (!el || el.closest('[hidden]') || el.closest('option') || el.closest('select')) continue;
      const cs = getComputedStyle(el);
      if (cs.visibility === 'hidden' || cs.display === 'none') continue;
      const range = document.createRange(); range.selectNodeContents(n);
      const rects = Array.from(range.getClientRects());
      if (!rects.length) continue;
      cv.font = `${cs.fontStyle} ${cs.fontWeight} ${cs.fontSize} ${cs.fontFamily}`;
      const m = cv.measureText(n.nodeValue.trim() || n.nodeValue);
      const clips = [];
      for (let a = el; a && a !== document.body; a = a.parentElement) {
        const acs = getComputedStyle(a);
        if (/(auto|scroll)/.test(acs.overflowY)) break;
        if (/(hidden|clip)/.test(acs.overflowY) || /(hidden|clip)/.test(acs.overflowX)) clips.push(a);
      }
      if (!clips.length) continue;
      for (const r of rects) {
        const base = r.top + m.fontBoundingBoxAscent; // the rect top is the content-area top
        const inkTop = base - m.actualBoundingBoxAscent, inkBot = base + m.actualBoundingBoxDescent;
        for (const c of clips) {
          const cr = c.getBoundingClientRect();
          if (r.right < cr.left || r.left > cr.right) continue; // ellipsised or scrolled out sideways
          if (inkTop < cr.top - 0.5 || inkBot > cr.bottom + 0.5) {
            out.push({ text: n.nodeValue.slice(0, 40), clipBy: String(c.className || c.id || c.tagName), inkTopGap: +(inkTop - cr.top).toFixed(1), inkBottomGap: +(cr.bottom - inkBot).toFixed(1) });
          }
        }
      }
    }
    return out;
  }

  function run() {
    const de = document.documentElement;
    const out = {
      vw: innerWidth, vh: innerHeight, scrollH: de.scrollHeight, scrollW: de.scrollWidth,
      noPageScroll: de.scrollHeight <= innerHeight && de.scrollWidth <= innerWidth,
      clipped: clippedThai(document.body), overflowing: [], menus: [], overlaps: []
    };
    // the top bar's blocks must not sit on top of each other (the moon once slid over the title in English with a long status chip)
    const blocks = [...document.querySelectorAll('#top > .brand, #top > .top-pages, #top > .moon, #top > .top-r')].map((n) => [String(n.className), n.getBoundingClientRect()]);
    for (let i = 0; i < blocks.length; i++) for (let j = i + 1; j < blocks.length; j++) {
      const [na, a] = blocks[i], [nb, b] = blocks[j];
      if (a.left < b.right - 0.5 && b.left < a.right - 0.5 && a.top < b.bottom - 0.5 && b.top < a.bottom - 0.5) out.overlaps.push(`${na} / ${nb}`);
    }
    // open menus and modal boxes are overlays on purpose: they must fit the viewport instead
    for (const mn of document.querySelectorAll('.menu, .modal-box')) {
      const r = mn.getBoundingClientRect();
      const fits = r.top >= 0 && r.left >= 0 && r.bottom <= innerHeight && r.right <= innerWidth;
      out.menus.push({ menu: String(mn.className), fits });
      if (!fits) out.overflowing.push({ zone: `menu ${mn.className}`, bottom: r.bottom, right: r.right });
    }
    // zones whose content is taller than their box would hide content
    for (const z of document.querySelectorAll('.card, .hero, .deck, .left, .top, .ground, .banner, .d-meta, .d-mic, .page-view, .origin, .og')) {
      if (z.hidden || z.querySelector('.menu')) continue;
      if (z.scrollHeight > z.clientHeight + 1 || z.scrollWidth > z.clientWidth + 1) out.overflowing.push({ zone: String(z.className), scrollH: z.scrollHeight, clientH: z.clientHeight, scrollW: z.scrollWidth, clientW: z.clientWidth });
    }
    return out;
  }

  /** A box with stacked Thai tone marks at three line-heights: 1.0, 1.15 and 1.3 must be flagged, 1.45 must pass. */
  function negative() {
    const results = {};
    for (const lh of [1.0, 1.15, 1.3, 1.45]) {
      const box = document.createElement('div');
      box.style.cssText = `position:fixed;left:0;top:0;width:400px;overflow:hidden;font:700 28px/${lh} Kanit, 'Dusk Read', sans-serif;background:#000;color:#fff;z-index:9999`;
      box.style.height = `${Math.round(28 * lh)}px`;
      box.textContent = 'ที่นี่ ปู่ๆ ผู้นั้น เก็บเกี่ยวสิ่งที่มี';
      document.body.appendChild(box);
      results[lh] = clippedThai(box).length;
      box.remove();
    }
    return { results, flagsTight: results[1.0] > 0, passesLoose: results[1.45] === 0 };
  }

  window.__pmChecks = { run, negative };
})();
