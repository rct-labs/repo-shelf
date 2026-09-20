// Test helpers: an in-memory fake GitHub API (fetch-compatible) and builders.

export function makeRepo({
  id,
  owner = 'octocat',
  name = 'repo',
  description = 'A sample repository',
  topics = [],
  language = 'JavaScript',
  license = 'MIT',
  archived = false,
  pushedAt = '2026-09-01T00:00:00Z',
  stars = 42,
  defaultBranch = 'main',
} = {}) {
  return {
    id,
    owner: { login: owner },
    name,
    full_name: `${owner}/${name}`,
    html_url: `https://github.com/${owner}/${name}`,
    description,
    topics,
    language,
    license: license ? { spdx_id: license } : null,
    archived,
    pushed_at: pushedAt,
    stargazers_count: stars,
    default_branch: defaultBranch,
  };
}

function fakeResponse(status, body, headers = {}) {
  const map = new Map(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), String(v)]));
  return {
    status,
    headers: { get: (name) => (map.has(name.toLowerCase()) ? map.get(name.toLowerCase()) : null) },
    json: async () => (typeof body === 'string' ? JSON.parse(body) : body),
    text: async () => (typeof body === 'string' ? body : JSON.stringify(body ?? '')),
  };
}

export class FakeGitHub {
  constructor() {
    this.repos = new Map(); // 'owner/repo' lowercase -> raw repo json
    this.readmes = new Map(); // 'owner/repo' lowercase -> string
    this.starred = new Map(); // username -> [raw repo json]
    this.failureQueue = []; // [{ match(url), respond() | throws }]
    this.requests = []; // url log
    this.onRequest = null; // optional hook(url)
  }

  key(owner, repo) {
    return `${owner}/${repo}`.toLowerCase();
  }

  addRepo(raw, { readme = null } = {}) {
    this.repos.set(this.key(raw.owner.login, raw.name), raw);
    if (readme !== null) this.readmes.set(this.key(raw.owner.login, raw.name), readme);
    return raw;
  }

  moveRepo(raw, newOwner, newName) {
    this.repos.delete(this.key(raw.owner.login, raw.name));
    const moved = {
      ...raw,
      owner: { login: newOwner },
      name: newName,
      full_name: `${newOwner}/${newName}`,
      html_url: `https://github.com/${newOwner}/${newName}`,
    };
    this.repos.set(this.key(newOwner, newName), moved);
    return moved;
  }

  deleteRepo(raw) {
    this.repos.delete(this.key(raw.owner.login, raw.name));
  }

  setStarred(username, rawList) {
    this.starred.set(username, rawList);
  }

  failOnce(match, respond) {
    this.failureQueue.push({ match, respond });
  }

  fetch = async (url) => {
    this.requests.push(url);
    if (this.onRequest) this.onRequest(url);

    const failureIndex = this.failureQueue.findIndex((f) => f.match(url));
    if (failureIndex !== -1) {
      const [failure] = this.failureQueue.splice(failureIndex, 1);
      return failure.respond();
    }

    const parsed = new URL(url);
    const path = parsed.pathname;

    let m = path.match(/^\/repos\/([^/]+)\/([^/]+)\/readme$/);
    if (m) {
      const text = this.readmes.get(this.key(decodeURIComponent(m[1]), decodeURIComponent(m[2])));
      if (text === undefined) return fakeResponse(404, { message: 'Not Found' });
      return fakeResponse(200, text);
    }
    m = path.match(/^\/repos\/([^/]+)\/([^/]+)$/);
    if (m) {
      const raw = this.repos.get(this.key(decodeURIComponent(m[1]), decodeURIComponent(m[2])));
      if (!raw) return fakeResponse(404, { message: 'Not Found' });
      return fakeResponse(200, raw);
    }
    m = path.match(/^\/repositories\/(\d+)$/);
    if (m) {
      const id = Number(m[1]);
      for (const raw of this.repos.values()) {
        if (raw.id === id) return fakeResponse(200, raw);
      }
      return fakeResponse(404, { message: 'Not Found' });
    }
    m = path.match(/^\/users\/([^/]+)\/starred$/);
    if (m) {
      const username = decodeURIComponent(m[1]);
      const list = this.starred.get(username);
      if (!list) return fakeResponse(404, { message: 'Not Found' });
      const perPage = Number(parsed.searchParams.get('per_page') || 100);
      const page = Number(parsed.searchParams.get('page') || 1);
      const slice = list.slice((page - 1) * perPage, page * perPage);
      const headers = {};
      if (page * perPage < list.length) {
        headers.link = `<http://fake/users/${username}/starred?per_page=${perPage}&page=${page + 1}>; rel="next"`;
      }
      return fakeResponse(200, slice, headers);
    }
    return fakeResponse(404, { message: 'Not Found' });
  };
}

export async function waitForJob(jobs, id, { timeoutMs = 10_000 } = {}) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const job = jobs.getJob(id);
    if (job && job.status !== 'running') return job;
    await new Promise((resolve) => setTimeout(resolve, 10));
  }
  throw new Error(`Job ${id} did not finish within ${timeoutMs} ms`);
}

export function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
  return { promise, resolve, reject };
}
