// Resolve README resources against the upstream repository, never localhost.
export function readmeUrl(value, repo, image = false) {
  if (!value || value.startsWith('#')) return value;
  const root = `https://github.com/${encodeURIComponent(repo.owner)}/${encodeURIComponent(repo.name)}`;
  const branch = encodeURIComponent(repo.defaultBranch || 'HEAD');
  let url;
  try {
    if (/^(?:[a-z][a-z\d+.-]*:|\/\/)/i.test(value)) {
      url = new URL(value, root);
    } else {
      url = new URL(value.replace(/^\/+/, ''), `${root}/blob/${branch}/`);
    }
    if (image && url.hostname === 'github.com' && /^\/[^/]+\/[^/]+\/blob\//.test(url.pathname)) {
      url.hostname = 'raw.githubusercontent.com';
      url.pathname = url.pathname.replace(/^(\/[^/]+\/[^/]+)\/blob\//, '$1/');
    }
    return ['https:', 'http:'].includes(url.protocol) ? url.href : null;
  } catch {
    return null;
  }
}

export function resolveReadmeResources(fragment, repo) {
  for (const node of fragment.querySelectorAll('img[src], a[href]')) {
    const attr = node.tagName === 'IMG' ? 'src' : 'href';
    const url = readmeUrl(node.getAttribute(attr), repo, attr === 'src');
    if (url) node.setAttribute(attr, url);
    else node.removeAttribute(attr);
  }
  // Avoid relative responsive candidates overriding the resolved src.
  for (const node of fragment.querySelectorAll('[srcset]')) node.removeAttribute('srcset');
}
