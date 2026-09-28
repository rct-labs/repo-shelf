// Projects tab: register local projects, pause them, generate a profile from
// a fixed file whitelist (explicit click only, after a privacy note), and edit
// the need statements and search queries. Strings live here via extendStrings;
// styles in projects.css. Profile text is AI output and rendered as plain text.
import { t, extendStrings, getLanguage } from '/i18n.js';

// Mirrors the server whitelist (REQUIREMENTS §6, ProjectProfiler.cs).
const WHITELIST = [
  'README*', 'AGENTS.md', 'CONTEXT.md', 'TASK_QUEUE.md',
  'package.json', 'pyproject.toml', 'requirements*.txt', '*.csproj', 'go.mod', 'Cargo.toml',
];
const NEVER_READ = ['.env*', 'memory/**', 'data/**', 'config/**', '.git/**', 'node_modules/**'];
const MAX_QUERIES = 4;

extendStrings('en', {
  projectsTitle: 'Projects',
  projectsIntro: 'Recommendations are proposed per project, weekly, from the needs listed here.',
  projectName: 'Name',
  projectPath: 'Local folder',
  projectPathPlaceholder: 'D:\\code\\my-project',
  projectAdd: 'Add project',
  projectAddHint: 'Adding a project sends nothing off this machine.',
  projectAdded: 'Project added',
  projectsEmpty: 'No projects yet. Add one above, then generate its profile.',
  projectPaused: 'Paused',
  projectPausedOn: 'Project paused',
  projectPausedOff: 'Project resumed',
  projectProfiledAt: 'Profile generated {time}',
  projectNotProfiled: 'No profile yet',
  projectCounts: '{pending} pending · {later} later · {accepted} accepted · {dismissed} dismissed',
  projectGenerate: 'Generate profile',
  projectRegenerate: 'Regenerate profile',
  projectGenerating: 'Generating…',
  projectGenerated: 'Profile generated',
  privacyTitle: 'Send project files to DeepSeek?',
  privacyNote: 'Only these files from the project root are read, each truncated: {files}. TASK_QUEUE.md: only rows that are not DONE; manifests: dependency names only. Never read: {never}, databases or source files.',
  privacyLastRead: 'Last profile read exactly: {files}.',
  privacyFilesRead: 'Files read',
  projectNeeds: 'Needs (one per line)',
  projectNeedsHint: 'AI-generated needs are editable; recommendations must match one of them.',
  projectQueries: 'GitHub search queries (one per line, at most {n})',
  projectQueriesHint: 'Keywords only; the project language and freshness filters are added automatically.',
  projectLanguages: 'Languages',
  projectDependencies: 'Main dependencies',
  projectSave: 'Save',
  projectSaved: 'Project saved',
  projectTooManyQueries: 'At most {n} search queries.',
  projectDelete: 'Delete',
  projectDeleteTitle: 'Delete project?',
  projectDeleteMessage: 'Removes "{name}" and its recommendations. Repositories already in your library stay.',
  projectDeleted: 'Project deleted',
  profileEmpty: 'None of the whitelisted files exist in this folder.',
});

extendStrings('zh-CN', {
  projectsTitle: '项目',
  projectsIntro: '每周按项目、根据这里列出的需求推荐仓库。',
  projectName: '名称',
  projectPath: '本地目录',
  projectPathPlaceholder: 'D:\\code\\my-project',
  projectAdd: '添加项目',
  projectAddHint: '添加项目不会向本机以外发送任何内容。',
  projectAdded: '已添加项目',
  projectsEmpty: '还没有项目。先在上方添加，再生成画像。',
  projectPaused: '暂停',
  projectPausedOn: '已暂停项目',
  projectPausedOff: '已恢复项目',
  projectProfiledAt: '画像生成于 {time}',
  projectNotProfiled: '尚未生成画像',
  projectCounts: '待处理 {pending} · 稍后 {later} · 已收藏 {accepted} · 已忽略 {dismissed}',
  projectGenerate: '生成画像',
  projectRegenerate: '重新生成画像',
  projectGenerating: '生成中…',
  projectGenerated: '画像已生成',
  privacyTitle: '把项目文件发送给 DeepSeek？',
  privacyNote: '只读取项目根目录下的这些文件，每个都会截断：{files}。TASK_QUEUE.md 只取未 DONE 的行；清单文件只取依赖名称。绝不读取：{never}、数据库或源代码。',
  privacyLastRead: '上次画像实际读取：{files}。',
  privacyFilesRead: '已读取的文件',
  projectNeeds: '需求（每行一条）',
  projectNeedsHint: 'AI 生成的需求可以修改；推荐必须对应其中一条。',
  projectQueries: 'GitHub 搜索词（每行一条，最多 {n} 条）',
  projectQueriesHint: '只写关键词；项目语言和活跃度筛选会自动加上。',
  projectLanguages: '语言',
  projectDependencies: '主要依赖',
  projectSave: '保存',
  projectSaved: '已保存项目',
  projectTooManyQueries: '搜索词最多 {n} 条。',
  projectDelete: '删除',
  projectDeleteTitle: '删除项目？',
  projectDeleteMessage: '将删除“{name}”及其推荐；已在库中的仓库不受影响。',
  projectDeleted: '已删除项目',
  profileEmpty: '该目录中没有任何白名单文件。',
});

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text != null) node.textContent = String(text);
  return node;
}

const lines = (text) => text.split(/\r?\n/).map((s) => s.trim()).filter(Boolean);

/**
 * ctx: { api, toast(message, action?), confirmDialog({title, message}), fmtTime(iso) }
 */
export function mount(root, ctx) {
  const state = {
    active: false,
    projects: [],
    loaded: false,
    error: null,
    generating: new Set(), // project ids with a profile call in flight
    drafts: new Map(), // project id -> { needs, queries } unsaved textarea text
  };

  function explain(err) {
    if (err.code === 'ai_not_configured') return t('aiNotConfigured');
    if (err.code === 'profile_empty') return t('profileEmpty');
    return err.retryable ? `${err.message} ${t('retryableHint')}` : err.message;
  }

  async function load() {
    try {
      const { projects } = await ctx.api('/api/projects');
      state.projects = projects;
      state.error = null;
    } catch (err) {
      state.error = err;
    }
    state.loaded = true;
    render();
  }

  // ---- actions ------------------------------------------------------------

  async function addProject(form) {
    const name = form.querySelector('#project-name').value.trim();
    const path = form.querySelector('#project-path').value.trim();
    if (!name || !path) return;
    try {
      await ctx.api('/api/projects', { method: 'POST', body: { name, path } });
      ctx.toast(t('projectAdded'));
      form.reset();
      await load();
    } catch (err) {
      ctx.toast(explain(err));
    }
  }

  async function update(project, body, message) {
    try {
      await ctx.api(`/api/projects/${project.id}`, { method: 'PUT', body });
      if (message) ctx.toast(message);
      return true;
    } catch (err) {
      ctx.toast(explain(err));
      return false;
    }
  }

  async function togglePaused(project, paused) {
    await update(project, { paused }, t(paused ? 'projectPausedOn' : 'projectPausedOff'));
    await load();
  }

  async function saveProfile(project, card) {
    const needs = lines(card.querySelector('.project-needs').value);
    const queries = lines(card.querySelector('.project-queries').value);
    if (queries.length > MAX_QUERIES) {
      ctx.toast(t('projectTooManyQueries', { n: MAX_QUERIES }));
      return;
    }
    if (await update(project, { needs, queries }, t('projectSaved'))) {
      state.drafts.delete(project.id);
      await load();
    }
  }

  async function generate(project) {
    if (state.generating.has(project.id)) return;
    const files = project.profileFiles?.length
      ? `\n\n${t('privacyLastRead', { files: project.profileFiles.join(', ') })}`
      : '';
    const ok = await ctx.confirmDialog({
      title: t('privacyTitle'),
      message: t('privacyNote', { files: WHITELIST.join(', '), never: NEVER_READ.join(', ') }) + files,
    });
    if (!ok) return;
    state.generating.add(project.id);
    render();
    try {
      await ctx.api(`/api/projects/${project.id}/profile`, {
        method: 'POST',
        body: { lang: getLanguage() === 'zh-CN' ? 'zh' : 'en' },
      });
      state.drafts.delete(project.id);
      ctx.toast(t('projectGenerated'));
    } catch (err) {
      ctx.toast(explain(err));
    } finally {
      state.generating.delete(project.id);
    }
    await load();
  }

  async function remove(project) {
    const ok = await ctx.confirmDialog({
      title: t('projectDeleteTitle'),
      message: t('projectDeleteMessage', { name: project.name }),
    });
    if (!ok) return;
    try {
      await ctx.api(`/api/projects/${project.id}`, { method: 'DELETE' });
      state.drafts.delete(project.id);
      ctx.toast(t('projectDeleted'));
    } catch (err) {
      ctx.toast(explain(err));
    }
    await load();
  }

  // ---- rendering ----------------------------------------------------------

  function renderAddForm() {
    const form = el('form', 'project-add');
    form.id = 'project-add';
    const field = (id, label, placeholder) => {
      const wrap = el('label', 'field');
      const input = el('input');
      input.id = id;
      input.type = 'text';
      input.autocomplete = 'off';
      if (placeholder) input.placeholder = placeholder;
      wrap.append(el('span', null, label), input);
      return wrap;
    };
    const btn = el('button', 'btn btn-primary', t('projectAdd'));
    btn.type = 'submit';
    btn.id = 'btn-add-project';
    form.append(
      field('project-name', t('projectName')),
      field('project-path', t('projectPath'), t('projectPathPlaceholder')),
      btn,
    );
    form.addEventListener('submit', (e) => { e.preventDefault(); addProject(form); });
    const wrap = el('div', 'project-add-wrap');
    wrap.append(form, el('p', 'hint', t('projectAddHint')));
    return wrap;
  }

  function textarea(className, value, id) {
    const area = el('textarea', className);
    area.value = value;
    area.rows = Math.min(12, Math.max(3, value.split('\n').length + 1));
    area.spellcheck = false;
    area.addEventListener('input', () => {
      const draft = state.drafts.get(id) || {};
      draft[className === 'project-needs' ? 'needs' : 'queries'] = area.value;
      state.drafts.set(id, draft);
    });
    return area;
  }

  function renderCard(p) {
    const card = el('article', 'project-card' + (p.paused ? ' paused' : ''));
    card.dataset.projectId = p.id;
    const busy = state.generating.has(p.id);

    const head = el('div', 'project-head');
    head.append(el('span', 'project-name', p.name));
    const paused = el('label', 'project-paused');
    const box = el('input');
    box.type = 'checkbox';
    box.className = 'project-paused-toggle';
    box.checked = p.paused;
    box.addEventListener('change', () => togglePaused(p, box.checked));
    paused.append(box, document.createTextNode(' ' + t('projectPaused')));
    head.append(paused);
    card.append(head);

    card.append(el('div', 'project-path', p.path));
    const c = p.counts || {};
    card.append(el('div', 'result-meta', [
      p.profiledAt ? t('projectProfiledAt', { time: ctx.fmtTime(p.profiledAt) }) : t('projectNotProfiled'),
      t('projectCounts', { pending: c.pending ?? 0, later: c.later ?? 0, accepted: c.accepted ?? 0, dismissed: c.dismissed ?? 0 }),
    ].join(' · ')));

    // Privacy: the whitelist before a profile exists, then exactly what was read.
    const privacy = el('div', 'project-privacy hint');
    if (p.profileFiles?.length) {
      privacy.append(el('span', 'project-label', `${t('privacyFilesRead')}: `));
      for (const f of p.profileFiles) privacy.append(el('code', 'project-file', f));
    } else {
      privacy.textContent = t('privacyNote', { files: WHITELIST.join(', '), never: NEVER_READ.join(', ') });
    }
    card.append(privacy);

    const draft = state.drafts.get(p.id) || {};
    const needsField = el('label', 'field');
    const needsLabel = el('span');
    needsLabel.append(el('span', 'badge ai', 'AI'), document.createTextNode(' ' + t('projectNeeds')));
    needsField.append(needsLabel, textarea('project-needs', draft.needs ?? (p.needs || []).join('\n'), p.id),
      el('small', 'hint', t('projectNeedsHint')));
    const queriesField = el('label', 'field');
    queriesField.append(el('span', null, t('projectQueries', { n: MAX_QUERIES })),
      textarea('project-queries', draft.queries ?? (p.queries || []).join('\n'), p.id),
      el('small', 'hint', t('projectQueriesHint')));
    card.append(needsField, queriesField);

    const facts = [];
    if (p.languages?.length) facts.push(`${t('projectLanguages')}: ${p.languages.join(', ')}`);
    if (p.dependencies?.length) facts.push(`${t('projectDependencies')}: ${p.dependencies.join(', ')}`);
    if (facts.length) card.append(el('div', 'result-meta project-facts', facts.join(' · ')));

    const actions = el('div', 'project-actions');
    const button = (label, cls, fn) => {
      const b = el('button', `btn btn-sm ${cls}`, label);
      b.type = 'button';
      b.addEventListener('click', fn);
      return b;
    };
    const gen = button(busy ? t('projectGenerating') : t(p.profiledAt ? 'projectRegenerate' : 'projectGenerate'),
      'project-generate', () => generate(p));
    gen.disabled = busy;
    actions.append(
      gen,
      button(t('projectSave'), 'btn-primary project-save', () => saveProfile(p, card)),
      el('span', 'feed-spacer'),
      button(t('projectDelete'), 'btn-danger project-delete', () => remove(p)),
    );
    card.append(actions);
    return card;
  }

  function render() {
    // Keep what the owner is typing in the add form across re-renders.
    const name = root.querySelector('#project-name')?.value ?? '';
    const path = root.querySelector('#project-path')?.value ?? '';
    root.innerHTML = '';
    const wrap = el('div', 'projects');
    wrap.append(el('h2', 'projects-title', t('projectsTitle')), el('p', 'hint', t('projectsIntro')), renderAddForm());
    wrap.querySelector('#project-name').value = name;
    wrap.querySelector('#project-path').value = path;
    if (state.error) {
      wrap.append(el('div', 'state-block', explain(state.error)));
    } else if (state.loaded && state.projects.length === 0) {
      wrap.append(el('div', 'state-block', t('projectsEmpty')));
    } else {
      const list = el('div', 'project-list');
      for (const p of state.projects) list.append(renderCard(p));
      wrap.append(list);
    }
    root.append(wrap);
  }

  return {
    show() {
      state.active = true;
      render();
      load();
    },
    hide() {
      state.active = false;
    },
    rerender() {
      if (state.active) render();
    },
    refresh: load,
  };
}
