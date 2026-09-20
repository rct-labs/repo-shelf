// URL normalization for GitHub repository URLs.
// Input is treated strictly as data: it is parsed, never executed or fetched here.

export class NormalizeError extends Error {
  constructor(message) {
    super(message);
    this.name = 'NormalizeError';
    this.code = 'invalid_url';
  }
}

// GitHub-owned top-level paths that look like an "owner" segment but are not users.
const RESERVED_OWNERS = new Set([
  'about', 'apps', 'collections', 'contact', 'customer-stories', 'enterprise',
  'events', 'explore', 'features', 'gist', 'import', 'login', 'logout',
  'marketplace', 'new', 'notifications', 'organizations', 'pricing', 'readme',
  'search', 'security', 'settings', 'signup', 'sponsors', 'stars', 'team',
  'topics', 'trending', 'users',
]);

const OWNER_RE = /^[a-z0-9](?:[a-z0-9-]{0,38})$/i;
const REPO_RE = /^[a-z0-9._-]{1,100}$/i;

function invalid(message) {
  return new NormalizeError(message);
}

/**
 * Parse a GitHub repository URL into { owner, repo, fullName, url }.
 * Accepts subpage URLs (/tree/..., /issues, ...), fragments, query strings and
 * trailing slashes. Rejects non-GitHub hosts and non-repository paths.
 * @param {string} input
 */
export function parseGitHubRepoUrl(input) {
  if (typeof input !== 'string' || !input.trim()) {
    throw invalid('A repository URL is required');
  }
  let text = input.trim();
  // Allow pasting "github.com/owner/repo" without a scheme.
  if (!/^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(text)) {
    text = `https://${text}`;
  }
  let url;
  try {
    url = new URL(text);
  } catch {
    throw invalid('Not a valid URL');
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw invalid('Only http(s) URLs are supported');
  }
  const host = url.hostname.toLowerCase();
  if (host !== 'github.com' && host !== 'www.github.com') {
    throw invalid('Only github.com repository URLs are supported');
  }

  let segments;
  try {
    segments = url.pathname.split('/').filter(Boolean).map((s) => decodeURIComponent(s));
  } catch {
    throw invalid('Malformed URL path');
  }
  if (segments.length < 2) {
    throw invalid('URL does not point to a repository (expected /owner/repo)');
  }
  const owner = segments[0];
  const repo = segments[1].replace(/\.git$/i, '');

  if (!OWNER_RE.test(owner)) {
    throw invalid(`"${owner}" is not a valid GitHub owner name`);
  }
  if (RESERVED_OWNERS.has(owner.toLowerCase())) {
    throw invalid(`"${owner}" is a GitHub system path, not a repository owner`);
  }
  if (!REPO_RE.test(repo) || repo === '.' || repo === '..') {
    throw invalid(`"${repo}" is not a valid GitHub repository name`);
  }
  return {
    owner,
    repo,
    fullName: `${owner}/${repo}`,
    url: `https://github.com/${owner}/${repo}`,
  };
}
