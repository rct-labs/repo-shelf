// GitHub REST API client with bounded retries and rate-limit awareness.
// All requests are read-only; this app never stars, forks or mutates GitHub.

const DEFAULT_API_VERSION = '2022-11-28';
const MAX_README_CHARS = 1_000_000;

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export class GitHubError extends Error {
  constructor(code, message, { status = null, retryable = false, retryAfterMs = null, rateLimitResetAt = null } = {}) {
    super(message);
    this.name = 'GitHubError';
    this.code = code;
    this.status = status;
    this.retryable = retryable;
    this.retryAfterMs = retryAfterMs;
    this.rateLimitResetAt = rateLimitResetAt;
  }
}

export function mapRepo(json) {
  const spdx = json.license && typeof json.license.spdx_id === 'string' ? json.license.spdx_id : null;
  return {
    githubId: json.id,
    owner: json.owner?.login ?? '',
    name: json.name ?? '',
    fullName: json.full_name ?? '',
    htmlUrl: json.html_url ?? '',
    description: json.description ?? '',
    topics: Array.isArray(json.topics) ? json.topics.filter((t) => typeof t === 'string') : [],
    language: json.language ?? null,
    licenseId: spdx && spdx !== 'NOASSERTION' ? spdx : spdx,
    archived: Boolean(json.archived),
    pushedAt: json.pushed_at ?? null,
    stars: typeof json.stargazers_count === 'number' ? json.stargazers_count : null,
    defaultBranch: json.default_branch ?? null,
  };
}

export function createGitHubClient({
  getToken = () => null,
  baseUrl = 'https://api.github.com',
  fetchImpl = globalThis.fetch?.bind(globalThis),
  maxTransientRetries = 3,
  userAgent = 'repo-shelf/0.1 (+https://github.com/rct-labs/repo-shelf)',
} = {}) {
  if (!fetchImpl) throw new Error('A fetch implementation is required');

  function raiseForStatus(res, context) {
    if (res.status < 400) return;
    const remaining = res.headers.get('x-ratelimit-remaining');
    const reset = res.headers.get('x-ratelimit-reset');
    const retryAfter = res.headers.get('retry-after');
    const resetAt = reset ? Number(reset) * 1000 : null;
    if (res.status === 404) {
      throw new GitHubError('not_found', `${context} not found (or not accessible without authentication)`, { status: 404 });
    }
    if ((res.status === 403 || res.status === 429) && (remaining === '0' || retryAfter !== null || res.status === 429)) {
      const waitMs = retryAfter !== null
        ? Number(retryAfter) * 1000
        : resetAt ? Math.max(0, resetAt - Date.now()) : null;
      throw new GitHubError('rate_limited',
        `GitHub rate limit reached${resetAt ? `; quota resets at ${new Date(resetAt).toISOString()}` : ''}`,
        { status: res.status, retryable: true, retryAfterMs: waitMs, rateLimitResetAt: resetAt });
    }
    if (res.status === 401) {
      throw new GitHubError('unauthorized', 'GitHub rejected the configured access token', { status: 401 });
    }
    if (res.status === 403) {
      throw new GitHubError('forbidden', `GitHub refused access to ${context}`, { status: 403 });
    }
    if (res.status === 451) {
      throw new GitHubError('unavailable_451', `${context} is unavailable (HTTP 451)`, { status: 451 });
    }
    throw new GitHubError('github_error', `GitHub responded with HTTP ${res.status} for ${context}`, {
      status: res.status,
      retryable: res.status >= 500,
    });
  }

  async function rawRequest(pathname, { accept = 'application/vnd.github+json' } = {}) {
    const headers = {
      accept,
      'x-github-api-version': DEFAULT_API_VERSION,
      'user-agent': userAgent,
    };
    const token = getToken();
    if (token) headers.authorization = `Bearer ${token}`;

    let lastError = null;
    for (let attempt = 0; attempt <= maxTransientRetries; attempt += 1) {
      let res;
      try {
        res = await fetchImpl(`${baseUrl}${pathname}`, { headers, redirect: 'follow' });
      } catch (err) {
        lastError = err;
        await sleep(400 * 2 ** attempt);
        continue;
      }
      if (res.status >= 500) {
        lastError = new GitHubError('upstream_error', `GitHub responded with HTTP ${res.status}`, { status: res.status, retryable: true });
        // Drain the body so the connection can be reused.
        await res.text().catch(() => {});
        await sleep(400 * 2 ** attempt);
        continue;
      }
      return res;
    }
    throw new GitHubError('upstream_unavailable',
      `GitHub is unreachable after ${maxTransientRetries + 1} attempts: ${lastError?.message || 'network error'}`,
      { retryable: true });
  }

  async function getJson(pathname, context) {
    const res = await rawRequest(pathname);
    raiseForStatus(res, context);
    return res.json();
  }

  function parseNextPage(linkHeader) {
    if (!linkHeader) return null;
    for (const part of linkHeader.split(',')) {
      const match = part.match(/<[^>]*[?&]page=(\d+)[^>]*>;\s*rel="next"/);
      if (match) return Number(match[1]);
    }
    return null;
  }

  return {
    async fetchRepo(owner, repo) {
      const json = await getJson(`/repos/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}`, `repository ${owner}/${repo}`);
      return mapRepo(json);
    },

    // GET /repositories/{id} resolves the current location of a repo id,
    // which lets refresh follow renames and ownership transfers.
    async fetchRepoById(githubId) {
      const json = await getJson(`/repositories/${encodeURIComponent(String(githubId))}`, `repository id ${githubId}`);
      return mapRepo(json);
    },

    /** @returns {Promise<{text: string, truncated: boolean} | null>} null when the repo has no README */
    async fetchReadme(owner, repo) {
      const res = await rawRequest(
        `/repos/${encodeURIComponent(owner)}/${encodeURIComponent(repo)}/readme`,
        { accept: 'application/vnd.github.raw+json' },
      );
      if (res.status === 404) return null;
      raiseForStatus(res, `README of ${owner}/${repo}`);
      let text = await res.text();
      let truncated = false;
      if (text.length > MAX_README_CHARS) {
        text = text.slice(0, MAX_README_CHARS);
        truncated = true;
      }
      return { text, truncated };
    },

    /** @returns {Promise<{items: object[], nextPage: number | null}>} */
    async fetchStarredPage(username, page, perPage = 100) {
      const pathname = `/users/${encodeURIComponent(username)}/starred?per_page=${perPage}&page=${page}`;
      const res = await rawRequest(pathname);
      raiseForStatus(res, `starred list of user "${username}"`);
      const json = await res.json();
      return {
        items: Array.isArray(json) ? json.map(mapRepo) : [],
        nextPage: parseNextPage(res.headers.get('link')),
      };
    },
  };
}
