// UI internationalization: English (default) and Simplified Chinese.
// Default follows the browser preference; a manual choice persists in
// localStorage. Repository names and source text are never translated.

export const LANGUAGES = [
  { id: 'en', label: 'English' },
  { id: 'zh-CN', label: '简体中文' },
];

const en = {
  appTitle: 'Repo Shelf',
  searchPlaceholder: 'Search by purpose, name, notes, README…',
  addRepo: 'Add repository',
  importStars: 'Import stars',
  settings: 'Settings',
  language: 'Language',
  filterStatus: 'Status',
  filterTag: 'Tag',
  filterLanguage: 'Language',
  filterProject: 'Project',
  filterArchived: 'Archived',
  archivedAny: 'Any',
  archivedYes: 'Archived',
  archivedNo: 'Active',
  clearFilters: 'Clear',
  resultsCount: '{count} saved',
  loading: 'Loading…',
  emptyLibrary: 'No repositories yet. Add one or import your GitHub stars.',
  emptyResults: 'Nothing matches. Try different keywords or filters.',
  loadError: 'Could not load data. Is the local service running?',

  status_inbox: 'Inbox',
  status_to_investigate: 'To investigate',
  status_tried: 'Tried',
  status_adopted: 'Adopted',
  status_dismissed: 'Dismissed',

  refresh_ok: 'Refreshed',
  refresh_not_found: 'Missing upstream',
  refresh_inaccessible: 'Inaccessible',
  refresh_error: 'Refresh failed',
  stale: 'Stale',
  unstarred: 'Unstarred upstream',
  starred: 'Starred',

  detailReason: 'Why I saved it',
  detailReasonPlaceholder: 'What problem does this solve for you?',
  detailNotes: 'Notes',
  detailNotesPlaceholder: 'Evaluation, usage experience, gotchas…',
  detailTags: 'Tags',
  detailProjects: 'Related projects',
  commaSeparated: 'Separate with commas',
  savedAt: 'Saved',
  fetchedAt: 'Synced',
  pushedAt: 'Last push',
  stars: 'Stars',
  license: 'License',
  branch: 'Branch',
  openOnGithub: 'Open on GitHub',
  readme: 'README',
  readmeTruncated: 'README snapshot truncated at 1 MB',
  noReadme: 'No README snapshot stored.',
  saveAnnotation: 'Save notes',
  refresh: 'Refresh',
  delete: 'Delete',
  deleteConfirm: 'Delete this record? Your notes and tags will be removed permanently.',
  deleteConfirmTitle: 'Delete record',
  cancel: 'Cancel',
  confirm: 'Delete',
  close: 'Close',
  copy: 'Copy',
  copied: 'Copied',

  addTitle: 'Add a repository',
  addUrlLabel: 'GitHub repository URL',
  addUrlPlaceholder: 'https://github.com/owner/repo',
  addSubmit: 'Save',
  addCreated: 'Saved to your shelf.',
  addAlready: 'Already on your shelf (annotations preserved).',
  addRetryable: 'GitHub is unreachable right now. Nothing was saved — please retry.',

  importTitle: 'Import GitHub stars',
  importUsername: 'GitHub username',
  importIncludeReadme: 'Fetch README snapshots (slower, enables offline search)',
  importStart: 'Start import',
  importRunning: 'Importing…',
  importCancel: 'Cancel import',
  importResume: 'Resume',
  importProgress: 'Processed {processed} · added {added} · updated {updated} · failed {failed}',
  importDone: 'Import finished.',
  importFailed: 'Import failed: {error}',
  importCancelled: 'Import cancelled. You can resume it.',
  importFailures: 'Recent failures',
  refreshAll: 'Refresh all metadata',
  refreshAllStarted: 'Refresh started.',

  settingsTitle: 'Settings',
  settingsVersion: 'Version',
  settingsDataDir: 'Data directory',
  settingsPairing: 'Extension pairing token',
  settingsPairingHelp: 'Paste this token into the browser extension options to pair it with this app.',
  settingsGithubToken: 'GitHub token (optional)',
  settingsGithubTokenHelp: 'A personal access token raises the API quota for imports. Stored only in the local data directory, never exported.',
  settingsGithubTokenSet: 'A token is currently stored.',
  settingsGithubTokenUnset: 'No token stored.',
  settingsTokenPlaceholder: 'ghp_… or github_pat_…',
  settingsTokenSave: 'Save token',
  settingsTokenClear: 'Clear token',
  settingsExport: 'Export library (JSON)',
  settingsRestore: 'Restore from export',
  settingsRestoreMode: 'If a record already exists:',
  restoreMerge: 'Keep existing',
  restoreOverwrite: 'Overwrite with backup',
  restoreButton: 'Choose file and restore',
  restoreResult: 'Restore finished: {added} added, {skipped} skipped, {overwritten} overwritten.',

  toastSaved: 'Notes saved.',
  toastDeleted: 'Record deleted.',
  toastRefreshed: 'Metadata refreshed.',
  errorGeneric: 'Something went wrong',
  retryableHint: 'You can retry safely.',

  matchedIn: 'Matched in: {fields}',
  detailEmpty: 'Select a repository to see details.',
  field_name: 'name',
  field_owner: 'owner',
  field_description: 'description',
  field_topics: 'topics',
  field_reason: 'reason',
  field_notes: 'notes',
  field_readme: 'README',
};

const zhCN = {
  appTitle: 'Repo Shelf',
  searchPlaceholder: '按用途、名称、笔记、README 搜索…',
  addRepo: '收藏仓库',
  importStars: '导入 Stars',
  settings: '设置',
  language: '语言',
  filterStatus: '状态',
  filterTag: '标签',
  filterLanguage: '语言',
  filterProject: '相关项目',
  filterArchived: '归档',
  archivedAny: '全部',
  archivedYes: '已归档',
  archivedNo: '未归档',
  clearFilters: '清除筛选',
  resultsCount: '共 {count} 条',
  loading: '加载中…',
  emptyLibrary: '还没有收藏。添加一个仓库，或导入你的 GitHub Stars。',
  emptyResults: '没有匹配结果，换个关键词或筛选条件试试。',
  loadError: '无法加载数据。本地服务是否在运行？',

  status_inbox: '收件箱',
  status_to_investigate: '待调研',
  status_tried: '已试用',
  status_adopted: '已采用',
  status_dismissed: '已放弃',

  refresh_ok: '已同步',
  refresh_not_found: '上游缺失',
  refresh_inaccessible: '无法访问',
  refresh_error: '刷新失败',
  stale: '数据过期',
  unstarred: '上游已取消收藏',
  starred: '已收藏',

  detailReason: '收藏理由',
  detailReasonPlaceholder: '它解决了你的什么问题？',
  detailNotes: '笔记',
  detailNotesPlaceholder: '评估记录、使用体验、坑点…',
  detailTags: '标签',
  detailProjects: '相关项目',
  commaSeparated: '用逗号分隔',
  savedAt: '收藏时间',
  fetchedAt: '同步时间',
  pushedAt: '最近推送',
  stars: 'Stars 数',
  license: '许可证',
  branch: '默认分支',
  openOnGithub: '在 GitHub 打开',
  readme: 'README',
  readmeTruncated: 'README 快照已在 1 MB 处截断',
  noReadme: '尚未保存 README 快照。',
  saveAnnotation: '保存笔记',
  refresh: '刷新',
  delete: '删除',
  deleteConfirm: '确定删除这条记录吗？你的笔记和标签将被永久删除。',
  deleteConfirmTitle: '删除记录',
  cancel: '取消',
  confirm: '删除',
  close: '关闭',
  copy: '复制',
  copied: '已复制',

  addTitle: '收藏仓库',
  addUrlLabel: 'GitHub 仓库 URL',
  addUrlPlaceholder: 'https://github.com/owner/repo',
  addSubmit: '保存',
  addCreated: '已保存到你的货架。',
  addAlready: '已在货架中（已保留原有标注）。',
  addRetryable: '暂时无法连接 GitHub，未保存任何内容——请稍后重试。',

  importTitle: '导入 GitHub Stars',
  importUsername: 'GitHub 用户名',
  importIncludeReadme: '抓取 README 快照（较慢，但支持离线搜索）',
  importStart: '开始导入',
  importRunning: '导入中…',
  importCancel: '取消导入',
  importResume: '继续导入',
  importProgress: '已处理 {processed} · 新增 {added} · 更新 {updated} · 失败 {failed}',
  importDone: '导入完成。',
  importFailed: '导入失败：{error}',
  importCancelled: '导入已取消，可以继续。',
  importFailures: '最近的失败',
  refreshAll: '刷新全部元数据',
  refreshAllStarted: '刷新已开始。',

  settingsTitle: '设置',
  settingsVersion: '版本',
  settingsDataDir: '数据目录',
  settingsPairing: '扩展配对令牌',
  settingsPairingHelp: '将此令牌粘贴到浏览器扩展的选项页，即可与本应用配对。',
  settingsGithubToken: 'GitHub 令牌（可选）',
  settingsGithubTokenHelp: '个人访问令牌可提高导入时的 API 配额。只保存在本地数据目录，不会被导出。',
  settingsGithubTokenSet: '当前已保存令牌。',
  settingsGithubTokenUnset: '未保存令牌。',
  settingsTokenPlaceholder: 'ghp_… 或 github_pat_…',
  settingsTokenSave: '保存令牌',
  settingsTokenClear: '清除令牌',
  settingsExport: '导出数据库（JSON）',
  settingsRestore: '从导出文件恢复',
  settingsRestoreMode: '记录已存在时：',
  restoreMerge: '保留现有记录',
  restoreOverwrite: '用备份覆盖',
  restoreButton: '选择文件并恢复',
  restoreResult: '恢复完成：新增 {added}，跳过 {skipped}，覆盖 {overwritten}。',

  toastSaved: '笔记已保存。',
  toastDeleted: '记录已删除。',
  toastRefreshed: '元数据已刷新。',
  errorGeneric: '出错了',
  retryableHint: '可以安全重试。',

  matchedIn: '匹配字段：{fields}',
  detailEmpty: '选择一个仓库查看详情。',
  field_name: '名称',
  field_owner: '所有者',
  field_description: '描述',
  field_topics: '主题',
  field_reason: '收藏理由',
  field_notes: '笔记',
  field_readme: 'README',
};

const DICTS = { en, 'zh-CN': zhCN };
const STORAGE_KEY = 'repo-shelf-lang';

export function detectLanguage() {
  const stored = localStorage.getItem(STORAGE_KEY);
  if (stored && DICTS[stored]) return stored;
  const nav = (navigator.language || 'en').toLowerCase();
  return nav.startsWith('zh') ? 'zh-CN' : 'en';
}

let current = detectLanguage();

export function getLanguage() {
  return current;
}

export function setLanguage(lang) {
  if (!DICTS[lang]) return;
  current = lang;
  localStorage.setItem(STORAGE_KEY, lang);
  document.documentElement.lang = lang === 'zh-CN' ? 'zh-CN' : 'en';
}

export function t(key, params = {}) {
  let text = DICTS[current][key] ?? DICTS.en[key] ?? key;
  for (const [k, v] of Object.entries(params)) {
    text = text.replaceAll(`{${k}}`, String(v));
  }
  return text;
}
