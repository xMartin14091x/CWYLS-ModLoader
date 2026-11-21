// The same QA issues used by the editor, with one concrete next step for the first issue.
export function warningHelp(issues, T) {
  const labels = issues.map((issue) => {
    const key = { ph: 'qa_placeholder', tag: 'qa_tag', mai: 'qa_mai', double: 'qa_double', lines: 'qa_lines', fast: 'qa_fast' }[issue.k];
    return T(key, issue);
  });
  return labels.join('\n') + '\n\n' + T(`warning_fix_${issues[0].k}`, issues[0]);
}
