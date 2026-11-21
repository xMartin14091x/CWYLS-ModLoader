// The loader injects a per-start token when serving /maker/. Static previews use mock.
export const config=Object.freeze({backend:document.querySelector('meta[name="cwyls-maker-token"]')?'http':'mock'});
