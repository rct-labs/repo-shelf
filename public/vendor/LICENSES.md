# Vendored UI libraries

These files are the unmodified ESM builds of the npm packages, vendored so the
web UI can be embedded into the desktop app and served fully offline:

- `marked.esm.js` — [marked](https://github.com/markedjs/marked), MIT License.
- `purify.es.mjs` — [DOMPurify](https://github.com/cure53/DOMPurify), Apache-2.0 OR MPL-2.0.

Regenerate with:

```sh
cp node_modules/marked/lib/marked.esm.js public/vendor/
cp node_modules/dompurify/dist/purify.es.mjs public/vendor/
```

(after `pnpm install`; versions are pinned by `pnpm-lock.yaml`)
