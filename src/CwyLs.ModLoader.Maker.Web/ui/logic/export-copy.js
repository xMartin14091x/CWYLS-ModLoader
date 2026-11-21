// TD-13: the loader sends each check as code + params, worded here in the UI language (key xc_<code>). An older loader sends
// diagnostic prose only: its known English variants are still matched for Thai. Counts, codes and paths stay data, never HTML.
export function exportCheckDetail(check, T, lang) {
  const coded = codedDetail(check, T);
  if (coded != null) return coded;
  const message = String(check.message || '');
  if (lang !== 'th') return message;
  const rules = [
    [/^manifest\.json is missing\.$/, 'manifestMissing'],
    [/^manifest\.json is (?:not valid:|invalid)\s*(.*)$/, 'manifestInvalid', ['detail']],
    [/^manifest\.json names pack '([^']*)', not '([^']*)'\.$/, 'manifestId', ['a', 'b']],
    [/^(\d+) subtitle table\(s\) have entries the game refuses\.$/, 'subBad', ['n']],
    [/^(\d+) subtitle table\(s\) are empty\.$/, 'subEmpty', ['n']],
    [/^No subtitle tables\.$/, 'subNone'],
    [/^(\d+) subtitle\(s\) in (\d+) table\(s\)\.$/, 'subCount', ['n', 'm']],
    [/^Not checked until the size limits pass\.$/, 'notChecked'],
    [/^Every subtitle table parses\.$/, 'subOk'],
    [/^(\d+) voice file\(s\) are empty, broken or in a format the game cannot play\.$/, 'audioBad', ['n']],
    [/^(\d+) file\(s\) under audio\/voice\/ are ignored by the game or longer than 60 s\.$/, 'audioOdd', ['n']],
    [/^(\d+) voice file\(s\)\.$/, 'audioCount', ['n']],
    [/^Every voice file reads\.$/, 'audioOk'],
    [/^No dubs, so no voice disclosure is needed\.$/, 'provNone'],
    [/^(\d+) dub\(s\) have no voice disclosure \(made outside Studio\)\.$/, 'provMissing', ['n']],
    [/^voice-provenance\.json is not valid:\s*(.*)$/, 'provInvalid', ['detail']],
    [/^(\d+) published dub\(s\) are missing their audio file\.$/, 'provAudio', ['n']],
    [/^(\d+) published dub\(s\) have no confirmed voice statement for their origin; confirm it for the published take\.$/, 'provConsent', ['n']],
    [/^(\d+) dub\(s\) changed after they were published; publish them again\.$/, 'provChanged', ['n']],
    [/^(\d+) dub\(s\) disclosed\.$/, 'provCount', ['n']],
    [/^Every published dub has a matching disclosure\.$/, 'provOk'],
    [/^(\d+) file\(s\) are over 512 MiB\.$/, 'sizeFile', ['n']],
    [/^The pack is (.+); an export may be at most 1984 MiB\.$/, 'sizeLimit', ['size']],
    [/^The pack is (.+): too big to download in the browser; the zip is still saved in CWYLS Mods\/\.maker-exports\.$/, 'sizeBrowser', ['size']],
    [/^(.+ MiB) in (\d+) file\(s\)\.$/, 'sizeCount', ['size', 'n']],
    [/^The pack fits in a browser download\.$/, 'sizeOk'],
    [/^No languages to compare with the game's lines\.$/, 'progressNone'],
    [/^The game's lines are not loaded yet, so progress was not compared\.$/, 'progressWaiting'],
    [/^Every line has its subtitle and dub\.$/, 'progressOk'],
    [/^([a-zA-Z0-9-]+): (\d+) voiced line\(s\) without a dub, (\d+) without a subtitle\.$/, 'progressDev', ['lang', 'dub', 'sub']],
    [/^(\d+) link\(s\) are skipped; copy the real files into the pack instead\.$/, 'linksSkipped', ['n']],
    [/^No links (?:inside the pack|in the pack)\.$/, 'linksOk'],
    [/^(\d+) file\(s\) have content for paid DLC lines that Pack Maker cannot confirm you own, so the pack is not exported\.$/, 'paidFound', ['n']],
    [/^Pack Maker could not check the pack for paid DLC lines /, 'paidUnknown'],
    [/links.*(?:export|pack)|(?:symbolic|symlink)/i, 'linksBad']
  ];
  // A valid manifest summary is just the pack's own name and version, not English UI copy.
  if (check.id === 'manifest' && check.severity === 'ok') return message === 'manifest.json is valid.' ? T('chk_manifest') + ': ' + T('sev_ok') : message;
  if (check.id === 'progress') {
    const parts = message.replace(/\.$/, '').split('; ');
    const matches = parts.map(p => p.match(/^([a-zA-Z0-9-]+): (\d+) of (\d+) voiced line\(s\) without a dub, (\d+) of (\d+) line\(s\) without a subtitle$/));
    if (matches.length && matches.every(Boolean)) return matches.map(m => T('xc_progress', { lang:m[1], dub:m[2], voiced:m[3], sub:m[4], total:m[5] })).join('; ');
  }
  for (const [pattern, key, names = []] of rules) {
    const match = message.match(pattern);
    if (match) return T('xc_' + key, Object.fromEntries(names.map((name, i) => [name, match[i+1]])));
  }
  return T('xc_unknown_' + (['ok','warning','error'].includes(check.severity) ? check.severity : 'error'), { n:check.count ?? 0 });
}

/** The check worded from its code and params, or null when it has none or this page does not know the code. */
function codedDetail(check, T) {
  const code = check.code, params = check.params || {};
  if (!code) return null;
  if (code === 'progress' && Array.isArray(params.langs)) return params.langs.map((l) => T('xc_progress', l)).join('; ');
  const key = 'xc_' + code, text = T(key, params);
  return text === key ? null : text;
}
