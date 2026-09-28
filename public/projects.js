// Projects tab (stub). DISC-8 replaces this module with project management;
// it keeps its strings here via extendStrings and its styles in projects.css.
import { t, extendStrings } from '/i18n.js';

extendStrings('en', { projectsStub: 'Project management arrives in the next update.' });
extendStrings('zh-CN', { projectsStub: '项目管理将在下一次更新中提供。' });

export function mount(el) {
  const render = () => {
    el.innerHTML = '';
    const note = document.createElement('div');
    note.className = 'state-block';
    note.textContent = t('projectsStub');
    el.append(note);
  };
  return {
    show: render,
    hide() {},
    rerender: render,
  };
}
