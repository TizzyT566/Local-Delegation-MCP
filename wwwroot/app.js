(() => {
  const el = (id) => document.getElementById(id);
  const list = el('task-list');
  const count = el('task-count');
  const empty = el('empty-state');
  const view = el('task-view');
  const conversation = el('conversation');
  let tasks = [];
  let selectedTaskId = null;
  let filter = 'all';
  let refreshInProgress = false;
  let fallbackTimer = null;

  const activeStatus = (status) => status === 'running' || status === 'waiting_clarification';
  const statusLabel = (status) => ({
    running: 'Running', waiting_clarification: 'Needs clarification', completed: 'Completed', stopped: 'Stopped', failed: 'Failed'
  }[status] || status);

  function setConnection(state, label) {
    el('connection-dot').className = `connection-dot ${state}`;
    el('connection-label').textContent = label;
  }

  function formatRelative(value) {
    if (!value) return '';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '';
    const seconds = Math.max(0, Math.floor((Date.now() - date.getTime()) / 1000));
    if (seconds < 45) return 'just now';
    if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
    if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
    return date.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }

  function formatClock(value) {
    if (!value) return '';
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? '' : date.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  }

  function clearChildren(node) {
    while (node.firstChild) node.removeChild(node.firstChild);
  }

  function renderTaskList() {
    clearChildren(list);
    const visible = filter === 'active' ? tasks.filter((task) => activeStatus(task.status)) : tasks;
    count.textContent = String(filter === 'active' ? visible.length : tasks.length);

    if (!visible.length) {
      const note = document.createElement('div');
      note.className = 'task-list-empty';
      note.textContent = filter === 'active' ? 'No active tasks right now.' : 'Tasks will appear here when the frontier model delegates work.';
      list.append(note);
      return;
    }

    for (const task of visible) {
      const button = document.createElement('button');
      button.type = 'button';
      button.className = `task-item${task.taskId === selectedTaskId ? ' selected' : ''}`;
      button.setAttribute('aria-current', task.taskId === selectedTaskId ? 'page' : 'false');
      button.addEventListener('click', () => selectTask(task.taskId));

      const top = document.createElement('div');
      top.className = 'task-item-top';
      const id = document.createElement('span');
      id.className = 'task-item-id';
      id.textContent = `TASK ${String(task.taskId).padStart(4, '0')}`;
      const dot = document.createElement('span');
      dot.className = `status-dot ${task.status}`;
      dot.title = statusLabel(task.status);
      top.append(id, dot);

      const title = document.createElement('div');
      title.className = 'task-item-title';
      title.textContent = task.description || '(untitled task)';

      const bottom = document.createElement('div');
      bottom.className = 'task-item-bottom';
      const status = document.createElement('span');
      status.className = 'task-item-time';
      status.textContent = statusLabel(task.status);
      const time = document.createElement('time');
      time.className = 'task-item-time';
      time.dateTime = task.updatedAtUtc;
      time.textContent = formatRelative(task.updatedAtUtc);
      bottom.append(status, time);
      button.append(top, title, bottom);
      list.append(button);
    }
  }

  function addMessage(entry, container = conversation) {
    const row = document.createElement('article');
    const progressCall = entry.kind === 'tool_call' && entry.toolName === 'report_progress';
    const tool = (entry.kind === 'tool_call' || entry.kind === 'tool_result') && !progressCall;
    row.className = `message ${tool ? 'tool' : entry.speaker}${entry.kind === 'final' ? ' final' : ''}${entry.kind === 'tool_result' ? ' tool-result' : ''}`;

    const marker = document.createElement('div');
    marker.className = 'message-marker';
    marker.textContent = entry.speaker === 'frontier' ? 'F' : tool ? '↗' : 'L';
    marker.setAttribute('aria-hidden', 'true');

    const body = document.createElement('div');
    body.className = 'message-body';
    const top = document.createElement('div');
    top.className = 'message-topline';
    const speaker = document.createElement('span');
    speaker.className = 'speaker-name';
    speaker.textContent = entry.speaker === 'frontier' ? 'Frontier model' : 'Local model';
    const kind = document.createElement('span');
    kind.className = 'message-kind';
    const labels = {
      task: 'TASK REQUEST', message: 'RESPONSE', tool_call: 'TOOL CALL', tool_result: 'TOOL RESULT',
      final: 'FINAL RESULT', clarification_answer: 'CLARIFICATION'
    };
    kind.textContent = progressCall ? 'PROGRESS UPDATE' : (labels[entry.kind] || entry.kind.toUpperCase());
    const time = document.createElement('time');
    time.className = 'message-time';
    time.dateTime = entry.timestampUtc;
    time.textContent = formatClock(entry.timestampUtc);
    top.append(speaker, kind, time);
    body.append(top);

    if (progressCall) {
      let message = entry.content;
      try { message = JSON.parse(message).progress_description || message; } catch { /* show raw arguments */ }
      const content = document.createElement('div');
      content.className = 'message-content';
      content.textContent = message;
      body.append(content);
    } else if (entry.kind === 'tool_call') {
      const toolName = document.createElement('div');
      toolName.className = 'tool-name';
      toolName.textContent = entry.toolName || 'Tool';
      const args = document.createElement('pre');
      args.className = 'tool-arguments';
      args.textContent = entry.content || '{}';
      body.append(toolName, args);
    } else {
      const content = document.createElement('div');
      content.className = 'message-content';
      content.textContent = entry.content || (entry.kind === 'message' ? '(empty response)' : '');
      body.append(content);
    }

    row.append(marker, body);
    container.append(row);
  }

  function showTerminal(task) {
    const card = el('terminal-card');
    if (task.status === 'failed' && task.error) {
      card.className = 'terminal-card failed';
      card.textContent = `The task failed: ${task.error}`;
      card.hidden = false;
    } else if (task.status === 'stopped') {
      card.className = 'terminal-card stopped';
      card.textContent = 'This task was stopped.';
      card.hidden = false;
    } else {
      card.hidden = true;
      card.textContent = '';
    }
  }

  function renderTask(task) {
    const shouldStickToBottom = conversation.scrollHeight - conversation.scrollTop - conversation.clientHeight < 90;
    el('detail-task-id').textContent = `TASK ${String(task.taskId).padStart(4, '0')}`;
    const status = el('detail-status');
    status.className = `status-pill ${task.status}`;
    status.textContent = statusLabel(task.status);
    el('detail-title').textContent = task.description || '(untitled task)';
    el('detail-meta').textContent = `Created ${formatRelative(task.createdAtUtc)} · ${task.iterationsUsed} model ${task.iterationsUsed === 1 ? 'turn' : 'turns'}`;

    const progress = el('progress-banner');
    progress.hidden = !task.progress;
    el('progress-text').textContent = task.progress || '';
    el('progress-time').textContent = task.progressUpdatedAtUtc ? `UPDATED ${formatClock(task.progressUpdatedAtUtc)}` : '';

    const criteria = el('criteria-panel');
    criteria.hidden = !task.completionCriteria;
    el('criteria-text').textContent = task.completionCriteria || '';

    clearChildren(conversation);
    const timeline = document.createElement('div');
    timeline.className = 'timeline';
    for (const entry of task.conversation || []) addMessage(entry, timeline);
    conversation.append(timeline);
    showTerminal(task);
    if (shouldStickToBottom) conversation.scrollTop = conversation.scrollHeight;
  }

  async function fetchJson(url) {
    const response = await fetch(url, { cache: 'no-store' });
    if (!response.ok) throw new Error(`Server returned ${response.status}`);
    return response.json();
  }

  async function refreshTasks() {
    if (refreshInProgress) return;
    refreshInProgress = true;
    try {
      tasks = await fetchJson('/api/tasks');
      if (selectedTaskId !== null && !tasks.some((task) => task.taskId === selectedTaskId)) selectedTaskId = null;
      if (selectedTaskId === null && tasks.length) {
        const first = tasks.find((task) => activeStatus(task.status)) || tasks[0];
        selectedTaskId = first.taskId;
      }
      renderTaskList();
      if (selectedTaskId !== null) await refreshSelectedTask();
      else showEmptyState();
    } catch (error) {
      setConnection('offline', 'Server connection lost');
      showToast(`Could not load tasks. ${error.message}`);
    } finally {
      refreshInProgress = false;
    }
  }

  async function refreshSelectedTask() {
    const task = await fetchJson(`/api/tasks/${encodeURIComponent(selectedTaskId)}`);
    if (task.taskId !== selectedTaskId) return;
    el('empty-state').hidden = true;
    view.hidden = false;
    document.body.classList.add('task-open');
    renderTask(task);
  }

  function showEmptyState() {
    view.hidden = true;
    empty.hidden = false;
    document.body.classList.remove('task-open');
  }

  async function selectTask(taskId) {
    selectedTaskId = taskId;
    renderTaskList();
    try { await refreshSelectedTask(); }
    catch (error) { showToast(`Could not load task. ${error.message}`); }
  }

  function showToast(message) {
    let toast = document.querySelector('.error-toast');
    if (!toast) {
      toast = document.createElement('div');
      toast.className = 'error-toast';
      toast.setAttribute('role', 'status');
      document.body.append(toast);
    }
    toast.textContent = message;
    clearTimeout(showToast.timeout);
    showToast.timeout = setTimeout(() => toast.remove(), 4500);
  }

  function connectEvents() {
    if (!('EventSource' in window)) {
      setConnection('offline', 'Polling for updates');
      fallbackTimer = setInterval(refreshTasks, 3000);
      return;
    }
    const events = new EventSource('/api/events');
    events.addEventListener('ready', () => {
      setConnection('connected', 'Live updates connected');
      if (fallbackTimer) { clearInterval(fallbackTimer); fallbackTimer = null; }
    });
    events.addEventListener('tasks_changed', () => {
      setConnection('connected', 'Live updates connected');
      refreshTasks();
    });
    events.onerror = () => {
      setConnection('offline', 'Reconnecting…');
      if (!fallbackTimer) fallbackTimer = setInterval(refreshTasks, 3000);
    };
  }

  document.querySelectorAll('.filter-button').forEach((button) => {
    button.addEventListener('click', () => {
      filter = button.dataset.filter;
      document.querySelectorAll('.filter-button').forEach((item) => item.classList.toggle('active', item === button));
      renderTaskList();
    });
  });
  el('refresh-button').addEventListener('click', refreshTasks);
  el('mobile-back').addEventListener('click', () => document.body.classList.remove('task-open'));

  refreshTasks();
  connectEvents();
})();
