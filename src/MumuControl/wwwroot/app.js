/* MuMu Control: no framework, no external assets, no background image polling. */
(() => {
  'use strict';
  const $ = (id) => document.getElementById(id);
  const NS = 'http://www.w3.org/2000/svg';
  const paths = {
    grid: ['M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z'],
    calendar: ['M5 5h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2Z','M16 3v4M8 3v4M3 11h18M8 15h2M14 15h2M8 18h2'],
    activity: ['M3 12h4l3-8 4 16 3-8h4'],
    settings: ['m12 3 1.6 2.7 3.1-.1.7 3 2.6 1.6-1.5 2.7 1.5 2.7-2.6 1.6-.7 3-3.1-.1L12 23l-1.6-2.9-3.1.1-.7-3L4 15.6l1.5-2.7L4 10.2l2.6-1.6.7-3 3.1.1Z','M15 12.8a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z'],
    leaf: ['M20 3c0 10-3 16-10 16a6 6 0 0 1-6-6C4 6 12 4 20 3Z','M4 21 15 10'],
    refresh: ['M20 7v5h-5M4 17v-5h5','M6 7a7 7 0 0 1 11-2l3 7M4 12l3 7a7 7 0 0 0 11-2'],
    plus: ['M12 5v14M5 12h14'],
    layers: ['m12 3 10 5-10 5L2 8Z','m2 12 10 5 10-5M2 16l10 5 10-5'],
    play: ['m8 4 12 8-12 8Z'],
    clock: ['M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z','M12 7v5l3 2'],
    search: ['M17 10a7 7 0 1 1-14 0 7 7 0 0 1 14 0Z','m15 15 6 6'],
    list: ['M8 5h13M8 12h13M8 19h13M3 5h.01M3 12h.01M3 19h.01'],
    zap: ['m13 2-9 12h7l-1 8 10-12h-7Z'],
    'arrow-up-right': ['M5 19 19 5M5 5h14v14'],
    info: ['M21 12a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z','M12 11v6M12 7h.01'],
    'chevron-right': ['m9 5 7 7-7 7'],
    shield: ['M12 2 3 6v6c0 5 9 10 9 10s9-5 9-10V6Z','m8 12 3 3 5-6'],
    link: ['m10 13 4-4M9 15l-2 2a4 4 0 0 1-6-6l4-4a4 4 0 0 1 6 0M13 9l2-2a4 4 0 0 1 6 6l-4 4a4 4 0 0 1-6 0'],
    check: ['m5 12 4 4L20 5'], x: ['m6 6 12 12M6 18 18 6'],
    square: ['M6 5h12a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1Z'],
    monitor: ['M3 3h18v13H3zM8 21h8M12 16v5'],
    power: ['M12 2v10M5 5a9 9 0 1 0 14 0'],
    more: ['M5 12h.01M12 12h.01M19 12h.01'],
    trash: ['M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7'],
    minimize: ['M4 12h16'], home: ['m3 10 9-8 9 8M5 9v12h5v-7h4v7h5V9'],
    'arrow-left': ['M20 12H4m7-7-7 7 7 7'], pause: ['M8 4v16M16 4v16'],
    download: ['M12 3v12M7 10l5 5 5-5', 'M4 17v3h16v-3'],
  };
  function icon(name) {
    const svg = document.createElementNS(NS, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('aria-hidden', 'true');
    for (const d of paths[name] || paths.info) { const path = document.createElementNS(NS, 'path'); path.setAttribute('d', d); svg.append(path); }
    return svg;
  }
  document.querySelectorAll('[data-icon]').forEach((el) => el.replaceChildren(icon(el.dataset.icon)));
  function el(tag, className, text) { const node = document.createElement(tag); if (className) node.className = className; if (text !== undefined) node.textContent = text; return node; }
  function button(label, className, iconName, onClick) { const b = el('button', className); b.type = 'button'; if (iconName) b.append(icon(iconName)); if (label) b.append(document.createTextNode(label)); b.addEventListener('click', onClick); return b; }
  const demo = new URLSearchParams(location.search).get('demo') === '1';
  let state = { emulators: [], tasks: [], settings: {}, connection: {}, metrics: {} };
  let token = '', auth = { initialized: false, authenticated: false, local: false, username: '' }, pollTimer = 0, polling = false, page = 'overview', filter = 'all', view = 'grid', logs = [];
  let selectedDevice = null, previewDevice = null, previewTimer = 0, previewRequest = null, previewObjectUrl = null, previewGeneration = 0;
  const thumbs = new Map(); let thumbTimer = 0;
  let settingsDirty = false, firstSettings = true, accessDirty = false, startup = { supported: false, enabled: false };
  const pending = new Set();
  const statusLabels = { running: '运行中', stopped: '已停止', starting: '启动中', stopping: '停止中', unknown: '状态未知' };
  const dateFormat = new Intl.DateTimeFormat('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false });
  const timeFormat = new Intl.DateTimeFormat('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false });
  function safeDate(value) { if (value === null || value === undefined || value === '') return null; const d = new Date(value); return Number.isFinite(d.getTime()) ? d : null; }
  function dateText(value) { const d = safeDate(value); return d ? dateFormat.format(d) : '时间待定'; }
  function taskDate(task) { if (task.nextRunAt) return safeDate(task.nextRunAt); if (task.trigger === 'once') return safeDate(task.at); return null; }
  function nextTasks() { return state.tasks.filter((t) => t.enabled && (!t.lastRunAt || t.trigger === 'daily') && taskDate(t)).sort((a, b) => taskDate(a) - taskDate(b)); }
  function deviceName(index) { return state.emulators.find((d) => String(d.index) === String(index))?.name || `模拟器 #${index}`; }
  function runtime(seconds) { seconds = Math.max(0, Math.floor(Number(seconds) || 0)); const h = Math.floor(seconds / 3600), m = Math.floor(seconds % 3600 / 60), s = seconds % 60; return [h, m, s].map((v) => String(v).padStart(2, '0')).join(':'); }
  function uptime(device) {
    const reported = Number(device.uptimeSeconds);
    if (device.uptimeSeconds !== null && device.uptimeSeconds !== undefined && Number.isFinite(reported)) return Math.max(0, reported);
    const started = safeDate(device.startedAt);
    return device.status === 'running' && started ? Math.max(0, (Date.now() - started.getTime()) / 1000) : null;
  }
  function toast(message, error = false) { const node = el('div', `toast${error ? ' error' : ''}`); node.append(icon(error ? 'info' : 'check'), el('span', '', message)); $('toast-region').append(node); setTimeout(() => node.remove(), 5500); }
  async function api(path, options = {}) {
    const controller = new AbortController(); const timeout = setTimeout(() => controller.abort(), options.timeout || 25000);
    try {
      const mutation = options.method && options.method !== 'GET';
      const headers = { Accept: 'application/json', ...(options.body ? { 'Content-Type': 'application/json' } : {}), ...(mutation && token && !options.skipToken ? { 'X-Mumu-Token': token } : {}) };
      const response = await fetch(path, { ...options, headers, signal: controller.signal, cache: 'no-store', body: options.body === undefined ? undefined : JSON.stringify(options.body) });
      const data = await response.json().catch(() => ({}));
      if (!response.ok) { if (response.status === 401 && !path.startsWith('/api/auth/') && path !== '/api/bootstrap') { auth.authenticated = false; token = ''; setAuthGate('login', '会话已结束，请重新登录。'); } const error = new Error(data.error || `请求失败（${response.status}）`); error.status = response.status; throw error; }
      return data;
    } catch (error) { if (error.name === 'AbortError') throw new Error('本地服务响应超时，请稍后重试。'); throw error; }
    finally { clearTimeout(timeout); }
  }
  function setAuthGate(mode, message = '') {
    const gate = $('auth-gate'); if (!gate) return;
    clearTimeout(pollTimer); stopPreview(); stopThumbs(); document.querySelectorAll('dialog[open]').forEach((d) => d.close()); document.querySelector('.app-shell').inert = true; document.body.classList.add('auth-pending');
    gate.classList.remove('hidden'); $('login-form').classList.toggle('hidden', mode !== 'login'); $('setup-form').classList.toggle('hidden', mode !== 'setup');
    $('auth-title').textContent = mode === 'setup' ? '初始化控制台。' : mode === 'blocked' ? '等待本机初始化。' : '登录控制台。'; $('auth-subtitle').textContent = mode === 'setup' ? '创建本机账户后即可开始使用。' : mode === 'blocked' ? '请在运行 MuMu Control 的电脑上创建账户。' : '请输入账户密码继续。';
    $('auth-error').textContent = message; $('auth-error').classList.toggle('hidden', !message);
    if (mode !== 'blocked') setTimeout(() => (mode === 'setup' ? $('setup-username') : $('login-username'))?.focus(), 30);
  }
  function hideAuthGate() { $('auth-gate')?.classList.add('hidden'); document.querySelector('.app-shell').inert = false; document.body.classList.remove('auth-pending'); }
  function applyAuth(data) { auth = { ...auth, ...(data || {}), local: data?.local ?? data?.isLocal ?? auth.local }; if (data?.token) token = data.token; if (auth.authenticated) hideAuthGate(); $('access-label').textContent = auth.local ? '本机安全会话' : '远程安全会话'; renderAccess(); }
  function renderAccess() {
    const enabled = Boolean(auth.remoteEnabled); const details = $('access-details'); if (!details) return;
    if (!accessDirty) { $('access-enabled').checked = enabled; $('access-port').value = auth.remotePort || 17831; $('access-origin').value = auth.publicOrigin || ''; } details.classList.toggle('hidden', !auth.local); $('access-status').textContent = enabled ? '远程 HTTP 服务已启用，使用账户密码登录；可通过自己的反向代理提供 HTTPS。' : '默认关闭。启用后使用下方访问地址登录。';
    $('setup-remote-label').textContent = `同时启用远程 HTTP 访问（端口 ${auth.remotePort || 17831}）`;
    const urls = Array.isArray(auth.remoteUrls) ? auth.remoteUrls : auth.remoteUrls ? [auth.remoteUrls] : []; $('access-url').textContent = urls.join('  ·  ') || (enabled ? `http://${location.hostname || 'localhost'}:${auth.remotePort || 17831}` : '—');
    $('proxy-port').textContent = auth.remotePort || 17831; $('proxy-local-port').textContent = state.settings.port || 17830;
    if ($('credential-username') && !$('credential-username').value) $('credential-username').value = auth.username || '';
    document.querySelectorAll('#settings-form input, #settings-form select, #settings-form button').forEach((field) => {
      if (!auth.local && !demo && !field.id.startsWith('logout') && !field.disabled) { field.disabled = true; field.dataset.accessLocked = '1'; }
      else if ((auth.local || demo) && field.dataset.accessLocked) { field.disabled = false; delete field.dataset.accessLocked; }
    });
    $('access-status').textContent = auth.local ? $('access-status').textContent : '账户与服务设置仅可在运行本程序的电脑上修改。';
    if (auth.local && auth.remoteError) $('access-status').textContent = auth.remoteError;
    document.querySelector('.credential-panel').classList.toggle('hidden', !auth.local && !demo); document.querySelector('.startup-panel').classList.toggle('hidden', !auth.local && !demo); $('save-access').classList.toggle('hidden', !auth.local && !demo); document.querySelector('.local-proxy-note').classList.toggle('hidden', !auth.local && !demo);
  }
  async function mutation(path, body = {}, method = 'POST') {
    if (demo) { toast('当前是界面演示模式，操作不会控制真实模拟器。'); return null; }
    if (!token) token = (await api('/api/bootstrap')).token;
    return api(path, { method, body });
  }
  function setConnection(connected, message) {
    $('connection-dot').className = connected ? 'connected' : 'error';
    $('connection-label').textContent = demo ? '界面演示 · 无设备控制' : connected ? '已连接本地服务' : '连接需要配置';
    $('service-alert').classList.toggle('hidden', connected && !demo);
    $('service-alert').textContent = demo ? '界面演示模式 · 以下设备与任务仅用于预览界面，不会执行任何真实操作。删除地址中的 ?demo=1 可返回真实控制台。' : message || '尚未连接 MuMu。请在「偏好设置」中填写 MuMuManager.exe 路径，然后刷新状态。';
  }
  function normalize(data) {
    if (data?.authenticated !== undefined || data?.initialized !== undefined) applyAuth(data);
    state = { ...state, ...data, emulators: Array.isArray(data.emulators) ? data.emulators : [], tasks: Array.isArray(data.tasks) ? data.tasks : [], settings: data.settings || {}, connection: data.connection || {}, metrics: data.metrics || {} };
  }
  function fillDeviceSelect(id) {
    const select = $(id), previous = select.value;
    select.replaceChildren();
    if (!state.emulators.length) { const o = el('option', '', '暂无可用模拟器'); o.value = ''; select.append(o); }
    for (const d of state.emulators) { const option = el('option', '', `${d.name} · #${d.index}`); option.value = d.index; select.append(option); }
    if ([...select.options].some((o) => o.value === previous)) select.value = previous;
  }
  function render() {
    const running = state.emulators.filter((d) => d.status === 'running').length;
    const stopped = state.emulators.filter((d) => d.status === 'stopped').length;
    const next = nextTasks();
    $('stat-total').textContent = state.emulators.length; $('stat-running').textContent = running;
    $('stat-tasks').textContent = next.length; $('nav-task-count').textContent = next.length;
    $('stat-next').textContent = next.length ? `${dateText(taskDate(next[0]))} 执行` : '暂无任务';
    const total = state.emulators.reduce((sum, d) => sum + (uptime(d) ?? 0), 0);
    $('stat-uptime').replaceChildren(document.createTextNode(Math.floor(total / 60)), el('small', '', ' 分钟'));
    ['device-count', 'filter-all'].forEach((id) => $(id).textContent = state.emulators.length);
    $('filter-running').textContent = running; $('filter-stopped').textContent = stopped;
    $('version').textContent = `MuMu Control ${state.version ? 'v' + state.version : ''}`;
    $('local-url').textContent = `${location.hostname || 'localhost'}:${location.port || state.settings.port || 17830}`;
    const last = safeDate(state.connection.lastUpdatedAt);
    $('sync-note').textContent = last ? `同步于 ${timeFormat.format(last)}` : '状态已同步';
    setConnection(Boolean(state.connection.connected), state.connection.error);
    fillDeviceSelect('quick-device');
    if (!$('task-dialog').open) fillDeviceSelect('task-device');
    document.querySelectorAll('[data-quick]').forEach((b) => b.disabled = !state.emulators.length);
    renderDevices(); renderUpcoming(); renderTasks();
    if (firstSettings || !settingsDirty) populateSettings(); renderAccess();
  }
  function emptyState(title, message, actionLabel, action) {
    const n = el('div', 'empty-state'); const visual = el('span', 'empty-icon'); visual.append(icon('layers'));
    n.append(visual, el('h3', '', title), el('p', '', message));
    if (action) n.append(button(actionLabel, 'button secondary', 'settings', action)); return n;
  }
  function renderDevices() {
    const search = $('device-search').value.trim().toLocaleLowerCase();
    const devices = state.emulators.filter((d) => (filter === 'all' || d.status === filter) && (!search || String(d.name).toLocaleLowerCase().includes(search) || String(d.index).includes(search)));
    const container = $('emulator-grid'); container.classList.toggle('list-view', view === 'list');
    if (!devices.length) { container.replaceChildren(emptyState(state.emulators.length ? '没有找到符合条件的模拟器' : '准备好连接你的 MuMu', state.emulators.length ? '尝试其他名称、编号，或切换状态筛选。' : '安装并配置 MuMu 模拟器后，你的设备会出现在这里。', '配置 MuMu 连接', () => navigate('settings'))); return; }
    const fragment = document.createDocumentFragment();
    for (const d of devices) {
      const running = d.status === 'running', busy = pending.has(String(d.index)) || d.status === 'starting' || d.status === 'stopping';
      const card = el('article', 'device-card'); card.setAttribute('aria-label', `${d.name}，${statusLabels[d.status] || '状态未知'}`);
      const visual = el('div', `device-visual${running ? ' running' : ''}`);
      visual.append(el('span', `status-badge ${d.status}`, statusLabels[d.status] || '状态未知'));
      if (running) { const monogram = el('span', 'device-monogram', 'mumu'); monogram.setAttribute('aria-hidden', 'true'); visual.append(monogram, thumbFor(d)); }
      else { const off = el('div', 'device-visual-stopped'); off.append(icon('power'), el('span', '', busy ? '处理中' : '等待下一次出发')); visual.append(off); }
      if (running) {
        visual.append(button('查看实际画面', 'preview-launch', 'monitor', () => openPreview(d)));
        const save = button('', 'thumb-save', 'download', () => saveCapture(d, save)); save.title = '保存当前画面（原始分辨率 PNG）'; save.setAttribute('aria-label', `保存 ${d.name} 的当前画面`); visual.append(save);
      }
      const body = el('div', 'device-body'), info = el('div', 'device-info'), name = el('div');
      const heading = el('h3', 'device-name', d.name || `MuMu 模拟器 ${d.index}`); heading.title = d.name || '';
      name.append(heading, el('span', 'device-id', d.adbPort ? `ADB · 127.0.0.1:${d.adbPort}` : `MuMu Player · 实例 ${d.index}`));
      info.append(name, el('span', 'device-index', '#' + String(d.index).padStart(2, '0')));
      const runtimeRow = el('div', 'device-runtime'), runtimeLabel = el('span', 'runtime-label'); runtimeLabel.append(icon('clock'), document.createTextNode('本次运行'));
      const elapsed = uptime(d);
      runtimeRow.append(runtimeLabel, el('span', 'runtime-value', running ? elapsed === null ? '未知' : runtime(elapsed) : '— — : — — : — —'));
      body.append(info, runtimeRow);
      if (d.autoStopAt && running) { const stopAt = el('p', 'auto-stop'); stopAt.append(icon('clock'), document.createTextNode(`${dateText(d.autoStopAt)} 自动停止`)); body.append(stopAt); }
      const actions = el('div', 'device-actions');
      const main = button(running ? '停止运行' : busy ? '处理中…' : '启动模拟器', `button ${running ? 'stop' : 'start'}`, running ? 'square' : 'play', () => runDevice(d, running ? 'stop' : 'start'));
      main.disabled = busy; actions.append(main);
      const quick = button('30 分钟', 'button subtle quick30', 'clock', () => runDevice(d, 'start', 30)); quick.title = '启动并在 30 分钟后自动停止'; quick.disabled = busy; actions.append(quick);
      const schedule = button('', 'button subtle schedule', 'calendar', () => openTask(d.index)); schedule.title = '新建定时任务'; schedule.setAttribute('aria-label', `为 ${d.name} 创建定时任务`); actions.append(schedule);
      const more = button('', 'device-more', 'more', () => openDevice(d)); more.title = '更多操作'; more.setAttribute('aria-label', `${d.name} 的更多操作`); actions.append(more);
      body.append(actions); card.append(visual, body); fragment.append(card);
    }
    // Preserve keyboard focus through automatic state refreshes.
    const active = document.activeElement, cards = [...container.children];
    const activeCard = active?.closest('.device-card'), cardPosition = cards.indexOf(activeCard);
    const buttonPosition = activeCard ? [...activeCard.querySelectorAll('button')].indexOf(active) : -1;
    container.replaceChildren(fragment);
    pruneThumbs(); scheduleThumbs(0);
    if (cardPosition >= 0 && buttonPosition >= 0) container.children[cardPosition]?.querySelectorAll('button')[buttonPosition]?.focus({ preventScroll: true });
  }
  // Live card thumbnails. Image elements persist across re-renders so the state poll does not blank them.
  function thumbFor(d) {
    const key = String(d.index); let thumb = thumbs.get(key);
    if (!thumb) {
      const img = el('img', 'device-thumb'); img.alt = ''; img.decoding = 'async'; img.draggable = false;
      img.addEventListener('click', () => { const device = state.emulators.find((vm) => String(vm.index) === key); if (device) openPreview(device); });
      thumb = { img, url: null, busy: false, failures: 0 }; thumbs.set(key, thumb);
    }
    thumb.img.title = `${d.name} · 点击查看大图`; return thumb.img;
  }
  function dropThumb(key) { const thumb = thumbs.get(key); if (!thumb) return; thumb.img.remove(); if (thumb.url) URL.revokeObjectURL(thumb.url); thumbs.delete(key); }
  function pruneThumbs() { for (const key of [...thumbs.keys()]) if (!state.emulators.some((d) => String(d.index) === key && d.status === 'running')) dropThumb(key); }
  function thumbSeconds() { return Math.max(1, Number(state.settings.previewIntervalSeconds) || 3); }
  function scheduleThumbs(delay) { clearTimeout(thumbTimer); if (demo || !auth.authenticated) return; thumbTimer = setTimeout(refreshThumbs, delay ?? thumbSeconds() * 1000); }
  function thumbVisible(img) {
    if (!img.isConnected || page !== 'overview' || document.hidden) return false;
    const r = img.getBoundingClientRect(); return r.width > 0 && r.bottom > 0 && r.top < innerHeight;
  }
  async function loadThumb(key, thumb) {
    thumb.busy = true;
    try {
      const response = await fetch(`/api/emulators/${encodeURIComponent(key)}/thumbnail?size=320&t=${Date.now()}`, { cache: 'no-store' });
      if (response.status === 401) { auth.authenticated = false; token = ''; setAuthGate('login', '会话已结束，请重新登录。'); return; }
      if (!response.ok) throw new Error();
      const blob = await response.blob(); if (thumbs.get(key) !== thumb) return;
      const url = URL.createObjectURL(blob), old = thumb.url; thumb.url = url;
      thumb.img.onload = () => { thumb.img.classList.add('ready'); if (old) URL.revokeObjectURL(old); };
      thumb.img.src = url; thumb.failures = 0;
    } catch { thumb.failures++; if (thumb.failures > 2) thumb.img.classList.remove('ready'); }
    finally { thumb.busy = false; }
  }
  async function refreshThumbs() {
    if (document.hidden || page !== 'overview') return;
    // Back off on instances that keep failing (e.g. Android still booting) without stalling others.
    const work = [...thumbs].filter(([, t]) => !t.busy && thumbVisible(t.img) && (t.failures < 3 || Math.random() < 0.25)).map(([k, t]) => loadThumb(k, t));
    await Promise.allSettled(work); scheduleThumbs();
  }
  function stopThumbs() { clearTimeout(thumbTimer); thumbTimer = 0; for (const key of [...thumbs.keys()]) dropThumb(key); }
  function captureFileName(d) {
    const now = new Date(), pad = (n) => String(n).padStart(2, '0');
    const stamp = `${now.getFullYear()}${pad(now.getMonth() + 1)}${pad(now.getDate())}-${pad(now.getHours())}${pad(now.getMinutes())}${pad(now.getSeconds())}`;
    const name = String(d.name || 'MuMu').replace(/[\\/:*?"<>|\x00-\x1f]/g, '_').trim().slice(0, 60) || 'MuMu';
    return `${name}-#${d.index}-${stamp}.png`;
  }
  // Saves a fresh full-resolution frame, not the downscaled thumbnail on screen.
  async function saveCapture(d, trigger) {
    if (demo) { toast('演示模式不能保存模拟器画面。'); return; }
    if (trigger) trigger.disabled = true;
    try {
      const response = await fetch(`/api/emulators/${encodeURIComponent(d.index)}/capture?t=${Date.now()}`, { cache: 'no-store' });
      if (response.status === 401) { auth.authenticated = false; token = ''; setAuthGate('login', '会话已结束，请重新登录。'); return; }
      if (!response.ok) { const data = await response.json().catch(() => ({})); throw new Error(data.error || '无法获取画面，请确认模拟器已完成启动。'); }
      const blob = await response.blob(); if (!blob.type.startsWith('image/')) throw new Error('画面接口未返回图片。');
      const url = URL.createObjectURL(blob), link = el('a'); link.href = url; link.download = captureFileName(d);
      document.body.append(link); link.click(); link.remove(); setTimeout(() => URL.revokeObjectURL(url), 60000);
      toast(`已保存画面：${link.download}`);
    } catch (error) { toast(error.message, true); }
    finally { if (trigger) trigger.disabled = false; }
  }
  async function runDevice(d, action, durationMinutes) {
    const key = String(d.index); if (pending.has(key)) return;
    pending.add(key); renderDevices();
    try {
      const body = durationMinutes ? { durationMinutes } : {};
      await mutation(`/api/emulators/${encodeURIComponent(d.index)}/${action}`, body);
      if (!demo) toast(action === 'stop' ? `已发送停止指令：${d.name}` : durationMinutes ? `${d.name} 已启动，将在 ${durationMinutes} 分钟后停止。` : `已发送启动指令：${d.name}`);
      await refreshState();
    } catch (error) { toast(error.message, true); }
    finally { pending.delete(key); renderDevices(); }
  }
  function renderUpcoming() {
    const container = $('upcoming-tasks'), tasks = nextTasks().slice(0, 3); container.replaceChildren();
    if (!tasks.length) { container.append(el('p', 'muted-empty', '还没有定时任务')); return; }
    for (const task of tasks) {
      const row = el('div', 'upcoming-item'), visual = el('span', `task-time-icon ${task.action === 'stop' ? 'stop' : ''}`);
      visual.append(icon(task.action === 'stop' ? 'square' : 'play'));
      const detail = el('div', 'upcoming-detail'); detail.append(el('div', 'upcoming-name', `${task.action === 'stop' ? '停止' : '启动'} · ${deviceName(task.vmIndex)}`), el('div', 'upcoming-time', `${dateText(taskDate(task))}${task.trigger === 'daily' ? ' · 每天' : ''}`));
      row.append(visual, detail); if (task.durationMinutes) row.append(el('span', 'upcoming-duration', `${task.durationMinutes} min`)); container.append(row);
    }
  }
  function renderTasks() {
    $('task-summary').textContent = `${state.tasks.length} 项任务`;
    const container = $('task-list'); container.replaceChildren();
    if (!state.tasks.length) { container.append(emptyState('把下一次运行安排好', '创建定时启动或停止任务，支持每天重复，以及启动后按时停止。', '新建任务', () => openTask())); return; }
    for (const task of [...state.tasks].sort((a, b) => (taskDate(a)?.getTime() || Infinity) - (taskDate(b)?.getTime() || Infinity))) {
      const row = el('div', 'task-row'); const visual = el('span', `task-time-icon ${task.action === 'stop' ? 'stop' : ''}`); visual.append(icon(task.action === 'stop' ? 'square' : 'play'));
      const name = el('div', 'task-row-name', `${task.action === 'stop' ? '停止' : '启动'} · ${deviceName(task.vmIndex)}`);
      name.append(el('small', '', task.durationMinutes ? `${task.durationMinutes} 分钟后自动停止` : task.trigger === 'daily' ? '每天重复执行' : '仅执行一次'));
      const when = el('div', 'task-row-time', task.trigger === 'daily' ? `每天 ${task.at}` : dateText(task.at));
      if (task.lastRunAt) when.append(el('small', '', `上次 ${dateText(task.lastRunAt)}`));
      let status = task.lastRunAt && task.trigger !== 'daily' ? '已执行' : !task.enabled ? '已暂停' : '待执行';
      if (task.lastResult?.includes('已跳过')) status = '已跳过';
      const failed = task.lastResult && /失败|error|fail/i.test(task.lastResult); if (failed) status = '执行失败';
      const badge = el('span', `task-state${failed ? ' failed' : status === '已执行' ? ' completed' : ''}`, status); if (task.lastResult) badge.title = task.lastResult;
      const remove = button('', 'icon-button', 'trash', async () => { remove.disabled = true; try { await mutation(`/api/tasks/${encodeURIComponent(task.id)}`, undefined, 'DELETE'); if (!demo) toast('任务已删除'); await refreshState(); } catch (error) { toast(error.message, true); remove.disabled = false; } });
      remove.title = '删除任务'; remove.setAttribute('aria-label', `删除${deviceName(task.vmIndex)}的定时任务`);
      row.append(visual, name, when, badge, remove); container.append(row);
    }
  }
  function populateSettings() {
    const s = state.settings; $('setting-path').value = s.managerPath || '';
    $('setting-poll').value = String(s.pollIntervalSeconds || 5); $('setting-preview').value = String(s.previewIntervalSeconds || 3);
    if (!$('setting-preview').value) $('setting-preview').value = '2';
    $('setting-port').value = s.port || 17830; $('setting-tray').checked = s.closeToTray !== false;
    firstSettings = false;
  }
  async function refreshState() {
    if (polling || demo || !auth.authenticated) return;
    polling = true;
    try { const data = await api('/api/state', { timeout: 15000 }); normalize(data); render(); }
    catch (error) { setConnection(false, `无法连接本地服务：${error.message} 请确认 MuMu Control 正在运行。`); $('sync-note').textContent = '同步失败'; }
    finally { polling = false; }
  }
  function schedulePoll() { clearTimeout(pollTimer); if (!auth.authenticated || demo) return; const seconds = Math.max(3, Number(state.settings.pollIntervalSeconds) || 5); pollTimer = setTimeout(async () => { if (!document.hidden) await refreshState(); schedulePoll(); }, (document.hidden ? Math.max(seconds, 30) : seconds) * 1000); }
  function navigate(target) {
    page = target; document.querySelectorAll('.page').forEach((n) => n.classList.toggle('active', n.id === 'page-' + target));
    document.querySelectorAll('.nav-item').forEach((b) => { const active = b.dataset.nav === target; b.classList.toggle('active', active); if (active) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current'); });
    $('page-name').textContent = { overview: '控制台', tasks: '定时任务', logs: '运行记录', settings: '偏好设置' }[target];
    if (target === 'overview') scheduleThumbs(0); if (target === 'logs') loadLogs(); if (target === 'settings') loadStartup(); window.scrollTo({ top: 0, behavior: 'instant' });
  }
  function localInputDate(date) { return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, '0')}-${String(date.getDate()).padStart(2, '0')}T${String(date.getHours()).padStart(2, '0')}:${String(date.getMinutes()).padStart(2, '0')}`; }
  function openTask(index) {
    fillDeviceSelect('task-device'); if (index !== undefined) $('task-device').value = index;
    $('task-trigger').value = 'once'; $('task-time').type = 'datetime-local'; $('task-time').value = localInputDate(new Date(Date.now() + 5 * 60000));
    $('task-time').min = localInputDate(new Date()); $('task-dialog').showModal();
  }
  function openDevice(d) { selectedDevice = d; $('device-dialog-name').textContent = `${d.name} · #${d.index}`; const running = d.status === 'running'; document.querySelectorAll('[data-vm-action]').forEach((b) => b.disabled = !running); $('device-set-timer').disabled = !running; $('device-cancel-timer').disabled = !running || !d.autoStopAt; $('device-dialog').showModal(); }
  async function updateTimer(cancel) {
    if (!selectedDevice) return; const duration = Number($('device-stop-duration').value);
    if (!cancel && (!Number.isInteger(duration) || duration < 1 || duration > 10080)) { toast('请输入 1 至 10080 之间的整数分钟数。', true); return; }
    const b = cancel ? $('device-cancel-timer') : $('device-set-timer'); b.disabled = true;
    try { await mutation(`/api/emulators/${encodeURIComponent(selectedDevice.index)}/auto-stop`, { durationMinutes: cancel ? null : duration }); if (!demo) toast(cancel ? '自动停止已取消' : `将在 ${duration} 分钟后自动停止`); $('device-dialog').close(); await refreshState(); }
    catch (error) { toast(error.message, true); } finally { b.disabled = false; }
  }
  async function loadLogs() {
    try { if (!demo) logs = (await api('/api/logs')).entries || []; const container = $('log-list'); container.replaceChildren();
      if (!logs.length) { container.append(emptyState('这里会记录每一次操作', '启动、停止以及定时任务的执行结果将显示在这里。')); return; }
      for (const log of [...logs].sort((a, b) => (safeDate(b.at)?.getTime() || 0) - (safeDate(a.at)?.getTime() || 0))) { const row = el('div', 'log-row'), visual = el('span', `log-indicator${log.level === 'error' ? ' error' : ''}`); visual.append(icon(log.level === 'error' ? 'info' : 'check')); row.append(visual, el('div', 'log-message', log.message), el('time', 'log-time', dateText(log.at))); container.append(row); }
    } catch (error) { $('log-list').replaceChildren(emptyState('暂时无法读取记录', error.message)); }
  }
  function previewMessage(message) { const n = $('preview-message'); n.replaceChildren(icon('monitor'), el('p', '', message)); n.classList.remove('hidden'); }
  function clearPreviewImage() { const img = $('preview-image'); img.onload = null; img.onerror = null; img.removeAttribute('src'); img.classList.add('hidden'); if (previewObjectUrl) URL.revokeObjectURL(previewObjectUrl); previewObjectUrl = null; }
  function cancelPreview() {
    // Invalidate callbacks before aborting: a previous request may settle after a new preview opens.
    previewGeneration++; clearTimeout(previewTimer); previewTimer = 0;
    previewRequest?.controller.abort(); previewRequest = null; clearPreviewImage();
  }
  function stopPreview() { cancelPreview(); previewDevice = null; }
  function openPreview(d) { stopPreview(); previewDevice = d; $('preview-name').textContent = `${d.name} · 实际画面`; $('preview-update').textContent = '按需获取设备画面'; $('preview-dialog').showModal(); previewMessage('正在获取模拟器实际画面…'); if (demo) { previewMessage('演示模式不显示模拟器画面。连接真实 MuMu 后可在这里查看实际截图。'); return; } refreshPreview(); }
  async function refreshPreview() {
    clearTimeout(previewTimer); previewTimer = 0;
    if (!previewDevice || !$('preview-dialog').open || document.hidden || previewRequest || demo) return;
    const deviceIndex = previewDevice.index, generation = previewGeneration;
    const request = { controller: new AbortController(), timedOut: false }; previewRequest = request;
    const isCurrent = () => generation === previewGeneration && previewDevice?.index === deviceIndex && $('preview-dialog').open && !document.hidden;
    const timeout = setTimeout(() => { request.timedOut = true; request.controller.abort(); }, 12000);
    try {
      // Prefer the fast renderer interface; fall back to the ADB screenshot on older MuMu builds.
      let response = await fetch(`/api/emulators/${encodeURIComponent(deviceIndex)}/thumbnail?size=1280&t=${Date.now()}`, { cache: 'no-store', signal: request.controller.signal });
      if (!response.ok && response.status !== 401) response = await fetch(`/api/emulators/${encodeURIComponent(deviceIndex)}/screenshot?t=${Date.now()}`, { cache: 'no-store', signal: request.controller.signal });
      if (!response.ok) { const data = await response.json().catch(() => ({})); if (response.status === 401) { auth.authenticated = false; token = ''; setAuthGate('login', '会话已结束，请重新登录。'); } throw new Error(data.error || '无法获取画面，请确认模拟器已完成启动。'); }
      const blob = await response.blob();
      if (!blob.type.startsWith('image/')) throw new Error('画面接口未返回图片。');
      if (!isCurrent()) return;
      const old = previewObjectUrl, objectUrl = URL.createObjectURL(blob), image = $('preview-image'); previewObjectUrl = objectUrl;
      image.onload = () => { if (isCurrent() && previewObjectUrl === objectUrl) { $('preview-message').classList.add('hidden'); image.classList.remove('hidden'); } };
      image.onerror = () => { if (isCurrent() && previewObjectUrl === objectUrl) { clearPreviewImage(); previewMessage('图片加载失败，请重试。'); } };
      image.src = objectUrl; if (old) URL.revokeObjectURL(old);
      $('preview-update').textContent = `${timeFormat.format(new Date())} 更新 · ${Math.max(1, Number(state.settings.previewIntervalSeconds) || 3)} 秒刷新`;
    } catch (error) { if (isCurrent() && (error.name !== 'AbortError' || request.timedOut)) previewMessage(request.timedOut ? '获取画面超时，请稍后重试。' : error.message); }
    finally {
      clearTimeout(timeout);
      if (previewRequest === request) { previewRequest = null; if (isCurrent()) previewTimer = setTimeout(refreshPreview, Math.max(1, Number(state.settings.previewIntervalSeconds) || 3) * 1000); }
    }
  }
  document.querySelectorAll('[data-nav]').forEach((b) => b.addEventListener('click', () => navigate(b.dataset.nav)));
  document.querySelectorAll('[data-action="open-task"]').forEach((b) => b.addEventListener('click', () => openTask()));
  document.querySelectorAll('[data-close]').forEach((b) => b.addEventListener('click', () => $(b.dataset.close).close()));
  document.querySelectorAll('dialog').forEach((dialog) => dialog.addEventListener('click', (event) => { if (event.target !== dialog) return; const rect = dialog.getBoundingClientRect(); if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close(); }));
  document.querySelectorAll('[data-view]').forEach((b) => b.addEventListener('click', () => { view = b.dataset.view; document.querySelectorAll('[data-view]').forEach((n) => n.classList.toggle('active', n === b)); renderDevices(); }));
  document.querySelectorAll('[data-filter]').forEach((b) => b.addEventListener('click', () => { filter = b.dataset.filter; document.querySelectorAll('[data-filter]').forEach((n) => n.classList.toggle('active', n === b)); renderDevices(); }));
  $('device-search').addEventListener('input', renderDevices);
  document.querySelectorAll('[data-quick]').forEach((b) => b.addEventListener('click', () => { const d = state.emulators.find((vm) => String(vm.index) === $('quick-device').value); if (d) runDevice(d, 'start', Number(b.dataset.quick)); }));
  $('refresh-button').addEventListener('click', async () => { const b = $('refresh-button'); b.disabled = true; b.classList.add('spinning'); try { if (!demo) await mutation('/api/refresh'); await refreshState(); } catch (error) { toast(error.message, true); } finally { b.disabled = false; b.classList.remove('spinning'); } });
  $('refresh-logs').addEventListener('click', loadLogs);
  $('task-duration').addEventListener('change', () => $('custom-duration-wrap').classList.toggle('hidden', $('task-duration').value !== 'custom'));
  document.querySelectorAll('input[name="action"]').forEach((input) => input.addEventListener('change', () => $('task-duration-field').classList.toggle('hidden', input.value === 'stop' && input.checked)));
  $('task-trigger').addEventListener('change', () => { const daily = $('task-trigger').value === 'daily'; $('task-time').type = daily ? 'time' : 'datetime-local'; $('task-time').removeAttribute('min'); $('task-time').value = daily ? timeFormat.format(new Date(Date.now() + 5 * 60000)) : localInputDate(new Date(Date.now() + 5 * 60000)); });
  $('task-form').addEventListener('submit', async (event) => {
    event.preventDefault(); const form = event.currentTarget, submit = form.querySelector('[type="submit"]');
    if ($('task-device').value === '') { toast('请先连接 MuMu 并选择模拟器。', true); return; }
    const action = new FormData(form).get('action'), trigger = $('task-trigger').value;
    const date = trigger === 'once' ? safeDate($('task-time').value) : null;
    if (trigger === 'once' && (!date || date.getTime() <= Date.now())) { toast('请选择未来的执行时间。', true); return; }
    const duration = $('task-duration').value === 'custom' ? Number($('task-custom-duration').value) : Number($('task-duration').value);
    if (action === 'start' && (!Number.isInteger(duration) || duration < 0 || duration > 10080)) { toast('运行时长须为 1 至 10080 分钟。', true); return; }
    submit.disabled = true;
    try { await mutation('/api/tasks', { vmIndex: Number($('task-device').value), action, trigger, at: trigger === 'once' ? date.toISOString() : $('task-time').value, durationMinutes: action === 'start' && duration ? duration : null, enabled: true }); if (!demo) toast('定时任务已创建'); $('task-dialog').close(); await refreshState(); }
    catch (error) { toast(error.message, true); } finally { submit.disabled = false; }
  });
  $('settings-form').addEventListener('input', () => { settingsDirty = true; $('settings-feedback').textContent = ''; });
  $('settings-form').addEventListener('keydown', (event) => { if (event.key !== 'Enter' || event.target.tagName !== 'INPUT') return; if (event.target.closest('.credential-panel')) { event.preventDefault(); $('save-credentials').click(); } else if (event.target.closest('.access-panel')) { event.preventDefault(); $('save-access').click(); } });
  $('settings-form').addEventListener('submit', async (event) => {
    event.preventDefault(); const submit = event.currentTarget.querySelector('[type="submit"]'); submit.disabled = true;
    try {
      const saved = await mutation('/api/settings', { ...state.settings, managerPath: $('setting-path').value.trim(), port: Number($('setting-port').value), pollIntervalSeconds: Number($('setting-poll').value), previewIntervalSeconds: Number($('setting-preview').value), closeToTray: $('setting-tray').checked }, 'PUT');
      if (!demo) { settingsDirty = false; $('settings-feedback').textContent = saved?.restartRequired ? '已保存，端口更改将在重启后生效。' : '设置已保存'; toast('设置已保存'); await refreshState(); schedulePoll(); }
    } catch (error) { toast(error.message, true); } finally { submit.disabled = false; }
  });
  $('save-access').addEventListener('click', async () => {
    const remotePort = Number($('access-port').value); if (!Number.isInteger(remotePort) || remotePort < 1024 || remotePort > 65535) { toast('远程端口须为 1024 至 65535 之间的整数。', true); return; }
    const publicOrigin = $('access-origin').value.trim();
    if (publicOrigin) {
      try { const origin = new URL(publicOrigin); if (!/^https?:\/\//i.test(publicOrigin) || !['http:', 'https:'].includes(origin.protocol) || origin.username || origin.password || origin.pathname !== '/' || origin.search || origin.hash) throw new Error(); }
      catch { toast('公开地址须为完整 HTTP 或 HTTPS 地址，且不能包含路径、查询或账户信息。', true); return; }
    }
    const b = $('save-access'); b.disabled = true;
    try { const saved = await api('/api/access', { method: 'PUT', body: { remoteEnabled: $('access-enabled').checked, remotePort, publicOrigin } }); accessDirty = false; applyAuth(saved); toast(saved.remoteEnabled ? '远程 HTTP 设置已保存' : '远程访问已关闭'); }
    catch (error) { toast(error.message, true); } finally { b.disabled = false; }
  });
  $('access-enabled').addEventListener('change', () => { accessDirty = true; });
  $('access-port').addEventListener('input', () => { accessDirty = true; });
  $('access-origin').addEventListener('input', () => { accessDirty = true; });
  async function loadStartup() {
    if (!auth.local || demo) return;
    try { startup = await api('/api/startup'); $('startup-enabled').checked = Boolean(startup.enabled); $('startup-enabled').disabled = !startup.supported; $('save-startup').disabled = !startup.supported; $('startup-status').textContent = startup.reason || (startup.enabled ? '已启用。登录 Windows 后驻留托盘，自动执行已保存的任务。' : '登录 Windows 后驻留托盘，继续执行定时任务。'); }
    catch (error) { $('startup-status').textContent = error.message; }
  }
  $('save-startup').addEventListener('click', async () => {
    const b = $('save-startup'); b.disabled = true;
    try { startup = await api('/api/startup', { method: 'PUT', body: { enabled: $('startup-enabled').checked } }); await loadStartup(); toast(startup.enabled ? '开机启动已启用' : '开机启动已关闭'); }
    catch (error) { toast(error.message, true); } finally { b.disabled = !startup.supported; }
  });
  $('save-credentials').addEventListener('click', async () => {
    const currentPassword = $('credential-current').value, password = $('credential-password').value, username = $('credential-username').value.trim();
    if (!currentPassword || !password || !username) { toast('请填写当前密码、账户名和新密码。', true); return; }
    const b = $('save-credentials'); b.disabled = true;
    try { const saved = await api('/api/auth/credentials', { method: 'PUT', body: { username, password, currentPassword } }); applyAuth(saved); $('credential-username').value = saved.username || username; $('credential-current').value = ''; $('credential-password').value = ''; toast('账户凭据已更新'); }
    catch (error) { toast(error.message, true); } finally { b.disabled = false; }
  });
  async function logout() { try { await api('/api/auth/logout', { method: 'POST', body: {} }); } catch (error) { if (error.status !== 401) { toast(`退出登录失败：${error.message}`, true); return; } } token = ''; auth = { ...auth, authenticated: false }; setAuthGate('login', '已退出登录。'); }
  $('logout-button').addEventListener('click', logout); $('logout-settings').addEventListener('click', logout);
  $('login-form').addEventListener('submit', async (event) => {
    event.preventDefault(); const b = event.currentTarget.querySelector('[type="submit"]'); b.disabled = true; $('auth-error').classList.add('hidden');
    try { const data = await api('/api/auth/login', { method: 'POST', skipToken: true, body: { username: $('login-username').value.trim(), password: $('login-password').value } }); applyAuth(data); $('login-password').value = ''; await refreshState(); schedulePoll(); }
    catch (error) { $('auth-error').textContent = error.message; $('auth-error').classList.remove('hidden'); } finally { b.disabled = false; }
  });
  $('setup-form').addEventListener('submit', async (event) => {
    event.preventDefault(); const password = $('setup-password').value, confirmation = $('setup-password-confirm').value;
    if (password !== confirmation) { $('auth-error').textContent = '两次输入的密码不一致。'; $('auth-error').classList.remove('hidden'); return; }
    const b = event.currentTarget.querySelector('[type="submit"]'); b.disabled = true; $('auth-error').classList.add('hidden');
    try { const data = await api('/api/auth/setup', { method: 'POST', body: { username: $('setup-username').value.trim(), password, remoteEnabled: $('setup-remote').checked } }); applyAuth(data); $('setup-password').value = ''; $('setup-password-confirm').value = ''; await refreshState(); schedulePoll(); }
    catch (error) { $('auth-error').textContent = error.message; $('auth-error').classList.remove('hidden'); } finally { b.disabled = false; }
  });
  $('device-set-timer').addEventListener('click', () => updateTimer(false)); $('device-cancel-timer').addEventListener('click', () => updateTimer(true));
  document.querySelectorAll('[data-vm-action]').forEach((b) => b.addEventListener('click', async () => { if (!selectedDevice) return; b.disabled = true; try { await mutation(`/api/emulators/${encodeURIComponent(selectedDevice.index)}/action`, { action: b.dataset.vmAction }); if (!demo) toast('操作指令已发送'); } catch (error) { toast(error.message, true); } finally { b.disabled = false; } }));
  $('preview-dialog').addEventListener('close', () => { if (!$('preview-dialog').open) stopPreview(); }); $('preview-refresh').addEventListener('click', refreshPreview);
  $('preview-save').addEventListener('click', () => { if (previewDevice) saveCapture(previewDevice, $('preview-save')); });
  document.addEventListener('visibilitychange', () => { if (document.hidden) { cancelPreview(); clearTimeout(thumbTimer); } else { refreshState(); if (previewDevice) refreshPreview(); scheduleThumbs(0); } schedulePoll(); });
  window.addEventListener('pagehide', () => { clearTimeout(pollTimer); stopPreview(); stopThumbs(); });
  async function init() {
    if (demo) {
      auth = { ...auth, authenticated: true, initialized: true, local: true }; hideAuthGate();
      const now = Date.now(); normalize({ version: '1.0.0', connection: { connected: true, lastUpdatedAt: new Date().toISOString() }, settings: { pollIntervalSeconds: 5, previewIntervalSeconds: 3, port: 17830, closeToTray: true }, emulators: [
        { index: 0, name: 'MuMu 模拟器 12', status: 'running', adbPort: 16384, uptimeSeconds: 2527, startedAt: new Date(now - 2527000).toISOString(), autoStopAt: new Date(now + 1500000).toISOString() },
        { index: 1, name: '日常挂机', status: 'running', adbPort: 16416, uptimeSeconds: 1092, startedAt: new Date(now - 1092000).toISOString() },
        { index: 2, name: '测试专用', status: 'stopped', adbPort: 16448 },
        { index: 3, name: '备用模拟器', status: 'stopped', adbPort: 16480 },
      ], tasks: [{ id: 'demo-1', vmIndex: 0, action: 'stop', trigger: 'once', at: new Date(now + 1500000).toISOString(), nextRunAt: new Date(now + 1500000).toISOString(), enabled: true }, { id: 'demo-2', vmIndex: 2, action: 'start', trigger: 'daily', at: '20:00', durationMinutes: 60, nextRunAt: new Date(now + 3600000).toISOString(), enabled: true }] });
      logs = [{ at: new Date(now - 2527000).toISOString(), level: 'info', message: '演示记录 · MuMu 模拟器 12 启动成功' }, { at: new Date(now - 1092000).toISOString(), level: 'info', message: '演示记录 · 日常挂机启动成功' }]; render(); return;
    }
    try {
      const bootstrap = await api('/api/bootstrap', { timeout: 15000 }); applyAuth(bootstrap);
      if (!bootstrap.initialized) { setAuthGate(auth.local ? 'setup' : 'blocked'); return; }
      if (!bootstrap.authenticated) { setAuthGate('login'); return; }
    } catch (error) { setAuthGate('login', `无法连接认证服务：${error.message}`); return; }
    await refreshState(); schedulePoll();
  }
  init();
})();
