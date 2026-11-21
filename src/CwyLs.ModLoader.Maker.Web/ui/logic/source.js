// Which pack's subtitle and dub the game actually uses for the selected line, when it is not the selected pack.
// Wire meaning (CONTRACT-v2, STUDIO-JS v2.1): `owned*` describes the selected pack; `subtitlePack` / `dubPack` name the WINNING loaded pack
// (null: nothing from any pack). The backend never claims an edit is audible when another pack wins, so neither may the page.
// Input is the view-model line (api/model.js): {text (own subtitle, '' if none), effectiveText, effectivePack, ownedDub, dubPack}.

/**
 * @returns {{sub: null|{kind:'wins'|'only', packId:string, text:string}, dub: null|{kind:'wins'|'only', packId:string}}}
 *   `only`: this pack contributes nothing, another pack does. `wins`: this pack contributes, but another pack wins.
 */
export function sourceNotes(line, packId) {
  const out = { sub: null, dub: null };
  if (!line) return out;
  if (line.effectivePack && line.effectivePack !== packId) {
    out.sub = { kind: line.text && line.text.trim() ? 'wins' : 'only', packId: line.effectivePack, text: line.effectiveText || '' };
  }
  if (line.dubPack && line.dubPack !== packId) {
    out.dub = { kind: line.ownedDub ? 'wins' : 'only', packId: line.dubPack };
  }
  return out;
}

/** A pack's display name: the loader's name when the pack is listed, else its id. */
export const packName = (packs, id) => (packs || []).find((p) => p.id === id)?.name || id;

/** A long subtitle is cut for the note (the full text is one click away in the pack that owns it). */
export const shortText = (t, max = 60) => {
  const flat = String(t || '').replace(/\s+/g, ' ').trim();
  return flat.length > max ? `${flat.slice(0, max - 1)}…` : flat;
};
