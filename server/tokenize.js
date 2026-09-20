// Tokenizer for mixed Chinese/English text.
//
// Strategy (verified in tests/search.test.js):
// - Latin runs ([A-Za-z0-9_]) become lowercase whole-word tokens.
// - CJK runs (Unified Ideographs + Extension A + Compatibility Ideographs)
//   become bigrams; a single isolated CJK character stays a unigram.
// - Everything else (punctuation, whitespace, emoji) is a separator.
//
// The same tokenizer runs at index time and at query time, and the tokenized
// text is fed to SQLite FTS5 (unicode61 treats our space-joined tokens as
// terms). This gives correct Chinese matching without relying on FTS5's
// default tokenizer, which does not segment Chinese.

const CJK_RE = /[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]/;
const WORD_RE = /[A-Za-z0-9_]/;

export function isCjkChar(ch) {
  return CJK_RE.test(ch);
}

function charType(ch) {
  if (CJK_RE.test(ch)) return 'cjk';
  if (WORD_RE.test(ch)) return 'word';
  return null;
}

/**
 * @param {string} text
 * @returns {string[]} tokens in order (duplicates kept for term frequency)
 */
export function tokenize(text) {
  if (!text) return [];
  const tokens = [];
  let run = '';
  let runType = null;

  const flush = () => {
    if (!run) return;
    if (runType === 'word') {
      tokens.push(run.toLowerCase());
    } else if (run.length === 1) {
      tokens.push(run);
    } else {
      for (let i = 0; i < run.length - 1; i += 1) {
        tokens.push(run.slice(i, i + 2));
      }
    }
    run = '';
    runType = null;
  };

  for (const ch of String(text)) {
    const type = charType(ch);
    if (type === null) {
      flush();
      continue;
    }
    if (type !== runType) {
      flush();
      runType = type;
    }
    run += ch;
  }
  flush();
  return tokens;
}

/** Index-time: tokenized text joined with spaces for FTS5. */
export function tokenizeForIndex(text) {
  return tokenize(text).join(' ');
}

/** Query-time: unique tokens (AND semantics do not need duplicates). */
export function tokenizeQuery(query) {
  return [...new Set(tokenize(query))];
}
