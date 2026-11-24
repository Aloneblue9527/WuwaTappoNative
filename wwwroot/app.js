/* Wuwatappo 主面板：本地 HTML 与原生层通过 postMessage 通信。
   所有接口都由 C# 代发，页面不接触 token。 */
(() => {
  const ICONS = {
    bag: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M6 8h12l1.5 12H4.5L6 8Z"/><path d="M9 8V6a3 3 0 0 1 6 0v2"/><path d="M9.5 13h5"/></svg>',
    atlas: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5.5A2.5 2.5 0 0 1 6.5 3H20v16H6.5A2.5 2.5 0 0 0 4 21.5Z"/><path d="M4 5.5v16"/><path d="M8 8h8M8 12h6"/></svg>',
    synth: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 3h6"/><path d="M10 3v5l-5 9.5A2.5 2.5 0 0 0 7.2 21h9.6a2.5 2.5 0 0 0 2.2-3.5L14 8V3"/><path d="M7.5 15h9"/></svg>',
    preset: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3.5l2.6 5.3 5.9.9-4.3 4.1 1 5.8-5.2-2.7-5.2 2.7 1-5.8L3.5 9.7l5.9-.9L12 3.5Z"/></svg>',
    room: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="9" cy="8" r="3.2"/><path d="M3.5 20a5.5 5.5 0 0 1 11 0"/><circle cx="17.5" cy="9.5" r="2.4"/><path d="M15 20a4.5 4.5 0 0 1 6 0"/></svg>',
    settings: '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3.2"/><path d="M19.4 15a1.7 1.7 0 0 0 .34 1.87l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06A1.7 1.7 0 0 0 15 19.4a1.7 1.7 0 0 0-1 1.55V21a2 2 0 1 1-4 0v-.09A1.7 1.7 0 0 0 9 19.4a1.7 1.7 0 0 0-1.87.34l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06A1.7 1.7 0 0 0 4.6 15a1.7 1.7 0 0 0-1.55-1H3a2 2 0 1 1 0-4h.09A1.7 1.7 0 0 0 4.6 9a1.7 1.7 0 0 0-.34-1.87l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06A1.7 1.7 0 0 0 9 4.6h.09A1.7 1.7 0 0 0 10.1 3.05V3a2 2 0 1 1 4 0v.09A1.7 1.7 0 0 0 15 4.6a1.7 1.7 0 0 0 1.87-.34l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06A1.7 1.7 0 0 0 19.4 9v.09A1.7 1.7 0 0 0 20.95 10.1H21a2 2 0 1 1 0 4h-.09A1.7 1.7 0 0 0 19.4 15Z"/></svg>'
  };

  const PAGES = [
    { key: 'bag', title: '我的背包', titleKey: 'nav.bag', icon: 'bag' },
    { key: 'atlas', title: '图鉴', titleKey: 'nav.atlas', icon: 'atlas' },
    { key: 'synth', title: '合成', titleKey: 'nav.synth', icon: 'synth' },
    { key: 'preset', title: '我的预设', titleKey: 'nav.preset', icon: 'preset' },
    { key: 'room', title: '联机', titleKey: 'nav.room', icon: 'room' },
    { key: 'companion', title: '自定义伙伴', titleKey: 'nav.companion', icon: 'companion' },
    { key: 'settings', title: '设置', titleKey: 'nav.settings', icon: 'settings' }
  ];

  const tr = (key, fallback, vars) => {
    try { return window.I18N ? window.I18N.t(key, fallback, vars) : (fallback ?? key); }
    catch { return fallback ?? key; }
  };
  function applyLocale(locale) {
    if (!locale) return;
    try {
      window.I18N?.setLocale?.(locale);
      window.I18N?.applyStatic?.(document);
    } catch { }
  }
  function pageTitleOf(page) {
    return page ? tr(page.titleKey, page.title) : '';
  }

  const RARITY_TEXT = { epic: '史诗', rare: '稀有', uncommon: '优良', common: '普通' };
  const RARITY_ORDER = { epic: 4, rare: 3, uncommon: 2, common: 1 };
  const TYPE_TEXT = { character_skin: '角色', accessory: '配件', emote: '表情' };
  const rarityText = r => tr('rarity.' + r, RARITY_TEXT[r] || r);
  const typeText = t => tr('type.' + t, TYPE_TEXT[t] || t);

  const state = {
    boot: null,
    page: 'bag',
    renderedIdentity: '',
    bag: null,
    bagTab: 'recent',
    bagSeries: '',
    bagRole: '',
    bagRarity: '',
    bagHidden: new Set(),
    bagSearch: '',
    bagSelected: null,
    bagNewIds: [],
    bagRecentIds: [],
    bagActionUntil: 0,
    disclaimerOpen: false,
    uacPromptOpen: false,
    update: null,
    updateDownload: null,
    updateDismissed: false,
    compatibility: null,
    announcements: [],
    announcementHistory: [],
    settingsOpenGroups: new Set(),
    settingsSearch: '',
    loginMode: 'uid',
    favors: new Set(),
    catalog: {},
    atlas: {},
    atlasType: 'all',
    atlasSeries: '',
    atlasRole: '',
    atlasRarity: '',
    atlasSearch: '',
    atlasLimit: 0,
    atlasHidden: new Set(),
    atlasAllCount: 0,
    atlasOwnedOnly: false,
    atlasEmoteSendMode: false,
    atlasSelected: null,
    roomError: null,
    themePresetPreviewId: '',
    synthMode: 'up',
    synthType: 'character_skin',
    synthRarity: 'all',
    synthSlots: [],
    synthCanCraft: false,
    synthBusy: false,
    presets: null,
    localPresets: null,
    companions: null,
    presetBusy: false,
    emoteNames: {},
    emoteCatalog: null
  };

  /* ---------------- Bridge ---------------- */
  let seq = 1;
  const pending = new Map();

  function call(cmd, data = {}) {
    return new Promise((resolve, reject) => {
      const id = seq++;
      pending.set(id, { resolve, reject });
      window.chrome.webview.postMessage({ id, cmd, data });
    });
  }

  // 纯浏览器预览不一定注入 WebView2 桥；真实 WebView2 中对象始终存在。
  window.chrome?.webview?.addEventListener('message', ev => {
    let msg = ev.data;
    if (typeof msg === 'string') { try { msg = JSON.parse(msg); } catch { return; } }
    if (!msg) return;
    if (msg.evt) { handleEvent(msg.evt, msg.data); return; }
    const p = pending.get(msg.id);
    if (!p) return;
    pending.delete(msg.id);
    if (msg.ok) p.resolve(msg.data);
    else p.reject(new Error(msg.error || '操作失败'));
  });

  async function tryCall(cmd, data) {
    try { return await call(cmd, data); }
    catch (e) { toast(e.message || String(e), 'err'); throw e; }
  }

  window.addEventListener('error', e => {
    try { toast(tr('error.scriptError', '脚本错误：{error}', { error: e.message || tr('error.unknown', '未知') }), 'err'); } catch { }
  });
  window.addEventListener('unhandledrejection', e => {
    try { toast(tr('error.scriptUnhandled', '脚本异常：{error}', { error: e.reason?.message || e.reason || tr('error.unknown', '未知') }), 'err'); } catch { }
  });

  /* ---------------- 基础 UI ---------------- */
  function toast(text, kind = 'info') {
    const host = document.getElementById('toastHost');
    const el = document.createElement('div');
    el.className = 'toast' + (kind === 'err' ? ' err' : '');
    el.textContent = text;
    host.appendChild(el);
    setTimeout(() => { el.style.opacity = '0'; el.style.transition = 'opacity .25s'; }, 2200);
    setTimeout(() => el.remove(), 2600);
  }

  function esc(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function modal(html, onBind) {
    const host = document.getElementById('modalHost');
    host.innerHTML = `<div class="modal">${html}</div>`;
    host.classList.remove('hidden');
    host.onclick = e => { if (e.target === host) closeModal(); };
    onBind && onBind(host);
  }
  function closeModal() {
    const host = document.getElementById('modalHost');
    host.classList.add('hidden');
    host.innerHTML = '';
  }

  function confirmOwnerRoomAction(message, onConfirm) {
    modal(`<div class="about">
      <div class="about-title">${tr('ownerRoom.title', '房主房间提醒')}</div>
      <div class="about-note about-note--warn"><p>${esc(message)}</p></div>
      <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px">
        <button class="btn ghost" id="ownerRoomCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn danger" id="ownerRoomContinue">${tr('ownerRoom.continue', '仍然继续')}</button>
      </div>
    </div>`, host => {
      host.querySelector('#ownerRoomCancel').onclick = closeModal;
      host.querySelector('#ownerRoomContinue').onclick = () => { closeModal(); onConfirm(); };
    });
  }

  function confirmRoomAction(title, message, confirmText, onConfirm) {
    modal(`<div class="about">
      <div class="about-title">${esc(title)}</div>
      <div class="about-note about-note--warn"><p>${esc(message)}</p></div>
      <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px">
        <button class="btn ghost" id="roomActionCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn danger" id="roomActionContinue">${esc(confirmText)}</button>
      </div>
    </div>`, host => {
      host.querySelector('#roomActionCancel').onclick = closeModal;
      host.querySelector('#roomActionContinue').onclick = () => { closeModal(); onConfirm(); };
    });
  }

  function rarityClass(r) { return 'rarity-' + (r || 'common'); }
  function effRarity(r) { return (r || 'common').toLowerCase(); }

  function img(url, cls = '', alt = '') {
    if (!url) return `<div class="${cls}"></div>`;
    return `<img class="${cls}" loading="lazy" src="${esc(url)}" alt="${esc(alt)}" onerror="this.style.visibility='hidden'">`;
  }

  const PARAMS = new URLSearchParams(location.search);
  const PREVIEW = PARAMS.get('preview');
  const DEMO = PARAMS.has('demo') || !!PREVIEW;
  // 真实面板在 Win11 上使用 DWM 原生圆角，外圆角半径要和 DWM 的 8px 对齐；
  // 预览页没有该参数，保持原来的 18px 外观。
  if (PARAMS.has('dwm')) document.documentElement.classList.add('dwm-rounded');

  function isLogged() {
    if (state.boot?.auth?.loggedIn === false) return false;
    return DEMO || !!(state.boot && state.boot.auth && state.boot.auth.loggedIn);
  }
  function authInfo() { return state.boot?.auth || {}; }
  function authState() {
    return authInfo().state || (isLogged() ? 'valid' : 'loggedOut');
  }
  function authOnlineAvailable() {
    const a = authInfo();
    if (a.onlineAvailable != null) return !!a.onlineAvailable;
    return DEMO;
  }
  function authPaused() {
    return isLogged() && !authOnlineAvailable();
  }
  function authStatusText(a = authInfo()) {
    const stateName = a.state || (a.loggedIn ? 'valid' : 'loggedOut');
    switch (stateName) {
      case 'checking': return tr('auth.checking', '正在确认登录状态…');
      case 'conflict': return a.credentialValid ? tr('auth.conflictValid', '凭据有效 · 账号功能仍暂停') : tr('auth.conflict', '未登录 · 登录态正在其他客户端使用');
      case 'invalid': return a.credentialValid ? tr('auth.invalidValid', '凭据已恢复有效 · 账号功能仍暂停') : tr('auth.invalid', '未登录 · 当前登录凭据已失效');
      case 'network': return a.stateMessage || tr('auth.network', '网络不可用，暂时无法确认登录状态');
      case 'valid': return tr('auth.valid', '正常');
      default: return tr('auth.loggedOut', '未登录');
    }
  }

  /* ---------------- 隐藏高级合成入口 ---------------- */
  const KONAMI_CODE = ['ArrowUp', 'ArrowUp', 'ArrowDown', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'ArrowLeft', 'ArrowRight', 'b', 'a', 'b', 'a'];
  let konamiIndex = 0;

  function lockKey(type, id) { return String(type || '').toLowerCase() + ':' + Number(id); }
  function isAssetLocked(type, id) { return state.locks.has(lockKey(type, id)); }
  function syncLocks(items) {
    state.locks = new Set((items || []).map(x => lockKey(x.type, x.id)));
  }
  function applySynthSettings(boot) {
    if (!boot) return;
    state.synthUnlocked = !!boot.settings?.anyItemSynthesisUnlocked;
    state.synthEnabled = !!boot.settings?.anyItemSynthesisEnabled;
    syncLocks(boot.locks || []);
  }
  async function toggleAssetLock(type, id) {
    try {
      const r = await tryCall('locks.toggle', { type, id });
      syncLocks(r?.items || []);
      if (state.page === 'bag') {
        const host = document.getElementById('bagGroups');
        const scroll = host?.scrollTop || 0;
        renderBagList();
        if (host) host.scrollTop = scroll;
      }
      else if (state.page === 'atlas') renderAtlas(true);
      else if (state.page === 'synth') renderSynth(true);
    } catch { }
  }
  function lockButtonHtml(type, id, extra = '') {
    if (!state.synthUnlocked) return '';
    const locked = isAssetLocked(type, id);
    const icon = locked
      ? '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="10" width="14" height="11" rx="2.5"/><path d="M8 10V7a4 4 0 0 1 8 0v3"/></svg>'
      : '<svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="10" width="14" height="11" rx="2.5"/><path d="M8 10V7a4 4 0 0 1 7.2-2.2"/></svg>';
    return `<button type="button" class="card-lock ${locked ? 'locked' : ''} ${extra}" data-lock-type="${esc(type)}" data-lock-id="${Number(id)}" title="${locked ? tr('bag.unlock', '解除锁定') : tr('bag.lock', '锁定，避免消耗最后一份')}">${icon}</button>`;
  }
  function showAdvancedSynthWarning() {
    modal(`<div class="about">
      <div class="about-title">${tr('synth.advancedTitle', '高级合成警告')}</div>
      <div class="about-note about-note--warn"><p>${tr('synth.advancedFirstBody', '此功能允许你使用所有库存物品进行合成，请务必看清后使用， 这将会损失你已解锁的图鉴！！！！每次重新打开操作面板后都会自动关闭，需要重新输入隐藏指令。')}</p></div>
      <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px">
        <button class="btn ghost" id="synthUnlockCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn danger" id="synthUnlockOk">${tr('synth.unlock', '我已知晓并解锁')}</button>
      </div>
    </div>`, host => {
      host.querySelector('#synthUnlockCancel').onclick = closeModal;
      host.querySelector('#synthUnlockOk').onclick = async () => {
        closeModal();
        try {
          const boot = await tryCall('settings.set', { anyItemSynthesisUnlocked: true, anyItemSynthesisEnabled: true });
          state.boot = boot;
          applySynthSettings(boot);
          state.synthPanelVisible = true;
          renderChrome();
          if (state.page === 'synth') renderSynth();
          toast(tr('synth.unlocked', '高级合成已解锁'));
        } catch { }
      };
    });
  }

  /** 每次从关闭切到开启都重新警告；关闭本身不警告。 */
  function confirmAdvancedSynthEnable(onConfirm) {
    const cancel = () => { closeModal(); renderSynth(); };
    modal(`<div class="about">
      <div class="about-title">${tr('synth.advancedTitle', '高级合成警告')}</div>
      <div class="about-note about-note--warn"><p>${tr('synth.advancedEnableBody', '开启后，任意物品合成会消耗你已解锁的图鉴，可能造成不可恢复的损失。每次实际合成前仍会再确认一次。')}</p></div>
      <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px">
        <button class="btn ghost" id="synthEnableCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn danger" id="synthEnableOk">${tr('synth.stillEnable', '仍要开启')}</button>
      </div>
    </div>`, host => {
      host.onclick = e => { if (e.target === host) cancel(); };
      host.querySelector('#synthEnableCancel').onclick = cancel;
      host.querySelector('#synthEnableOk').onclick = () => { closeModal(); onConfirm(); };
    });
  }

  async function showSynthPanel() {
    if (state.page !== 'synth') return;
    // 保留旧的“意外设计”：每次输入隐藏指令都重新弹出首次警告，而不是直接打开面板。
    showAdvancedSynthWarning();
  }

  document.addEventListener('keydown', e => {
    if (state.page !== 'synth') { konamiIndex = 0; return; }
    if (e.repeat) return;
    const target = e.target;
    if (target && target instanceof Element && target.closest('input, textarea, select')) { konamiIndex = 0; return; }
    const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
    if (key === KONAMI_CODE[konamiIndex]) {
      konamiIndex++;
      if (konamiIndex === KONAMI_CODE.length) {
        konamiIndex = 0;
        showSynthPanel();
      }
    } else {
      konamiIndex = key === KONAMI_CODE[0] ? 1 : 0;
    }
  });

  /* ---------------- 初始化 ---------------- */
  const HASH_PARAMS = new URLSearchParams(location.hash.replace(/^#/, ''));

  function applyPanelMaximized(on) {
    const maximized = !!on;
    document.body.classList.toggle('panel-maximized', maximized);
    const btn = document.getElementById('maxBtn');
    if (btn) {
      const label = maximized ? tr('common.restore', '还原') : tr('common.maximize', '最大化');
      btn.title = label;
      btn.setAttribute('aria-label', label);
    }
  }

  function normalizeHexColor(value, fallback) {
    return /^#[0-9a-f]{6}$/i.test(String(value || '')) ? String(value).toLowerCase() : fallback;
  }

  function hexToRgb(value) {
    const m = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(value);
    return m ? [parseInt(m[1], 16), parseInt(m[2], 16), parseInt(m[3], 16)] : [0, 0, 0];
  }

  function rgbToHsv(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
    let h = 0;
    if (d > 0) {
      if (max === r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
      else if (max === g) h = ((b - r) / d + 2) / 6;
      else h = ((r - g) / d + 4) / 6;
    }
    return [h * 360, max === 0 ? 0 : d / max, max];
  }

  function hsvToHex(h, s, v) {
    h = ((Number(h) % 360) + 360) % 360;
    s = Math.max(0, Math.min(1, Number(s) || 0));
    v = Math.max(0, Math.min(1, Number(v) || 0));
    const c = v * s, x = c * (1 - Math.abs((h / 60) % 2 - 1)), m = v - c;
    const rgb = h < 60 ? [c, x, 0] : h < 120 ? [x, c, 0] : h < 180 ? [0, c, x]
      : h < 240 ? [0, x, c] : h < 300 ? [x, 0, c] : [c, 0, x];
    return '#' + rgb.map(n => Math.round((n + m) * 255).toString(16).padStart(2, '0')).join('');
  }

  function rgbToHsl(r, g, b) {
    r /= 255; g /= 255; b /= 255;
    const max = Math.max(r, g, b), min = Math.min(r, g, b);
    const l = (max + min) / 2;
    let h = 0, s = 0;
    if (max !== min) {
      const d = max - min;
      s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
      if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
      else if (max === g) h = (b - r) / d + 2;
      else h = (r - g) / d + 4;
      h /= 6;
    }
    return [h, s, l];
  }

  function hslToHex(h, s, l) {
    h = ((h % 1) + 1) % 1;
    s = Math.max(0, Math.min(1, s));
    l = Math.max(0, Math.min(1, l));
    const q = l < 0.5 ? l * (1 + s) : l + s - l * s;
    const p = 2 * l - q;
    const hue2rgb = t => {
      t = ((t % 1) + 1) % 1;
      if (t < 1 / 6) return p + (q - p) * 6 * t;
      if (t < 1 / 2) return q;
      if (t < 2 / 3) return p + (q - p) * (2 / 3 - t) * 6;
      return p;
    };
    return '#' + [hue2rgb(h + 1 / 3), hue2rgb(h), hue2rgb(h - 1 / 3)]
      .map(v => Math.round(v * 255).toString(16).padStart(2, '0')).join('');
  }

  function mixHex(a, b, amount) {
    const x = hexToRgb(normalizeHexColor(a, '#e7b77c'));
    const y = hexToRgb(normalizeHexColor(b, '#ffffff'));
    const t = Math.max(0, Math.min(1, Number(amount) || 0));
    return '#' + x.map((v, i) => Math.round(v + (y[i] - v) * t).toString(16).padStart(2, '0')).join('');
  }

  /** 只影响桌宠底栏/远程昵称条的字段；拖动取色时以低频率发给原生层预览。 */
  const PET_THEME_PREVIEW_KEYS = new Set([
    'themeAccent', 'themeFont', 'themeBarFollowStandard',
    'themeBarColor', 'themeBarTextFollowTheme', 'themeBarTextColor', 'themeAdvanced',
  ]);
  let petThemePreviewPending = null;
  let petThemePreviewTimer = 0;
  function previewPetTheme(patch) {
    if (!patch || !window.chrome?.webview) return;
    const keys = Object.keys(patch).filter(key => PET_THEME_PREVIEW_KEYS.has(key));
    if (!keys.length) return;
    petThemePreviewPending = { ...(petThemePreviewPending || {}), ...patch };
    if (petThemePreviewTimer) return;
    petThemePreviewTimer = setTimeout(() => {
      petThemePreviewTimer = 0;
      const payload = petThemePreviewPending || {};
      petThemePreviewPending = null;
      call('theme.preview', payload).catch(() => { });
    }, 40);
  }
  function clearPetThemePreview() {
    petThemePreviewPending = null;
    if (petThemePreviewTimer) {
      clearTimeout(petThemePreviewTimer);
      petThemePreviewTimer = 0;
    }
    if (!window.chrome?.webview) return;
    call('theme.preview.clear').catch(() => { });
  }
  /** 自绘颜色选择器：饱和度/明度渐变区 + 色相滑条 + RGB/HEX + 快捷色板。 */
  function openColorPicker(initial, onApply, onPreview, onCancel) {
    let value = normalizeHexColor(initial, '#e7b77c');
    const [initialH, initialS, initialV] = rgbToHsv(...hexToRgb(value));
    const hsv = [initialH, initialS, initialV];
    const presets = [
      '#e7b77c', '#d99a5c', '#c25b4e', '#d67084', '#9669c8', '#6096d2',
      '#6eaa78', '#5ab7c2', '#454c73', '#f5e4cc', '#fff8e9', '#ffffff',
      '#000000', '#8e5e43', '#342d36', '#f0eef4', '#e8e7ef', '#c6cadc',
      '#ffd9c9', '#fed291', '#123456', '#1f5fa8', '#c0392b', '#27ae60',
    ];
    const recentKey = 'wuwa.colorPicker.recent.v1';
    let recent = [];
    try { recent = JSON.parse(localStorage.getItem(recentKey) || '[]') || []; } catch { recent = []; }
    recent = recent.filter(c => /^#[0-9a-f]{6}$/i.test(String(c))).slice(0, 8);
    const recentHtml = recent.length
      ? `<div class="color-recent-label">${tr('color.recent', '最近使用')}</div><div class="color-presets color-recent">${recent.map(c => `<button type="button" class="color-preset" data-cp-color="${c}" style="background:${c}" title="${c}"></button>`).join('')}</div>`
      : '';
    modal(`<div class="color-picker-dialog">
      <div class="color-picker-title">${tr('color.title', '选择颜色')}</div>
      <div class="color-sv" id="cpSV" title="${tr('color.svTitle', '拖动选择饱和度和明度')}"><span class="color-sv-cursor" id="cpCursor"></span></div>
      <label class="color-hue-row"><span>${tr('color.hue', '色相')}</span><input type="range" id="cpHue" min="0" max="360" step="1" /><output id="cpHueV"></output></label>
      <div class="color-picker-preview-row">
        <span class="color-picker-preview" id="cpPreview"></span>
        <input class="input color-hex-input" id="cpHex" maxlength="7" value="${value}" />
      </div>
      <div class="color-presets">${presets.map(c => `<button type="button" class="color-preset" data-cp-color="${c}" style="background:${c}" title="${c}"></button>`).join('')}</div>
      ${recentHtml}
      <div class="color-rgb-grid">
        <label class="color-slider-row"><span>R</span><input type="range" id="cpR" min="0" max="255" step="1" /><output id="cpRV"></output></label>
        <label class="color-slider-row"><span>G</span><input type="range" id="cpG" min="0" max="255" step="1" /><output id="cpGV"></output></label>
        <label class="color-slider-row"><span>B</span><input type="range" id="cpB" min="0" max="255" step="1" /><output id="cpBV"></output></label>
      </div>
      <div class="color-picker-actions">
        <button class="btn ghost" id="cpCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn" id="cpOk">${tr('common.confirm', '确定')}</button>
      </div>
    </div>`, host => {
      // 点击遮罩关闭时也必须走取消回调，否则桌宠可能停在临时预览色上。
      host.onclick = e => {
        if (e.target !== host) return;
        if (onCancel) onCancel();
        closeModal();
      };
      const sv = host.querySelector('#cpSV');
      const cursor = host.querySelector('#cpCursor');
      const hue = host.querySelector('#cpHue');
      const sync = (notify = true) => {
        const [r, g, b] = hexToRgb(value);
        const preview = host.querySelector('#cpPreview');
        const hex = host.querySelector('#cpHex');
        if (preview) preview.style.background = value;
        if (hex && hex !== document.activeElement) hex.value = value;
        if (sv) {
          sv.style.background = `linear-gradient(to top, #000, transparent), linear-gradient(to right, #fff, transparent), hsl(${hsv[0]} 100% 50%)`;
          if (cursor) {
            cursor.style.left = `${(hsv[1] * 100).toFixed(2)}%`;
            cursor.style.top = `${((1 - hsv[2]) * 100).toFixed(2)}%`;
          }
        }
        if (hue && hue !== document.activeElement) hue.value = String(hsv[0]);
        const hueOut = host.querySelector('#cpHueV');
        if (hueOut) hueOut.textContent = `${Math.round(hsv[0])}°`;
        for (const [id, val, label] of [['cpR', r, 'cpRV'], ['cpG', g, 'cpGV'], ['cpB', b, 'cpBV']]) {
          const input = host.querySelector('#' + id);
          const out = host.querySelector('#' + label);
          if (input && input !== document.activeElement) input.value = String(val);
          if (out) out.textContent = String(val);
        }
        if (notify && onPreview) onPreview(value);
      };
      const setValue = (next, notify = true) => {
        value = normalizeHexColor(next, value);
        const nextHsv = rgbToHsv(...hexToRgb(value));
        hsv[0] = nextHsv[0]; hsv[1] = nextHsv[1]; hsv[2] = nextHsv[2];
        sync(notify);
      };
      const setHsv = (h, s, v, notify = true) => {
        hsv[0] = ((Number(h) % 360) + 360) % 360;
        hsv[1] = Math.max(0, Math.min(1, Number(s)));
        hsv[2] = Math.max(0, Math.min(1, Number(v)));
        value = hsvToHex(hsv[0], hsv[1], hsv[2]);
        sync(notify);
      };
      const pickSv = e => {
        const rect = sv.getBoundingClientRect();
        const x = Math.max(0, Math.min(1, (e.clientX - rect.left) / Math.max(1, rect.width)));
        const y = Math.max(0, Math.min(1, (e.clientY - rect.top) / Math.max(1, rect.height)));
        setHsv(hsv[0], x, 1 - y);
      };
      sv.onpointerdown = e => {
        try { sv.setPointerCapture(e.pointerId); } catch { }
        pickSv(e);
      };
      sv.onpointermove = e => { if (e.buttons & 1) pickSv(e); };
      hue.oninput = e => setHsv(Number(e.target.value), hsv[1], hsv[2]);
      host.querySelectorAll('[data-cp-color]').forEach(btn => btn.onclick = () => setValue(btn.dataset.cpColor));
      host.querySelector('#cpR').oninput = e => { const [, g, b] = hexToRgb(value); setValue('#' + [e.target.value, g, b].map(v => Math.max(0, Math.min(255, Math.round(Number(v) || 0))).toString(16).padStart(2, '0')).join('')); };
      host.querySelector('#cpG').oninput = e => { const [r, , b] = hexToRgb(value); setValue('#' + [r, e.target.value, b].map(v => Math.max(0, Math.min(255, Math.round(Number(v) || 0))).toString(16).padStart(2, '0')).join('')); };
      host.querySelector('#cpB').oninput = e => { const [r, g] = hexToRgb(value); setValue('#' + [r, g, e.target.value].map(v => Math.max(0, Math.min(255, Math.round(Number(v) || 0))).toString(16).padStart(2, '0')).join('')); };
      host.querySelector('#cpHex').onchange = e => setValue(e.target.value);
      host.querySelector('#cpCancel').onclick = () => { if (onCancel) onCancel(); closeModal(); };
      host.querySelector('#cpOk').onclick = () => {
        try {
          const next = JSON.parse(localStorage.getItem(recentKey) || '[]');
          const list = [value, ...(Array.isArray(next) ? next : []).filter(c => String(c).toLowerCase() !== value)];
          localStorage.setItem(recentKey, JSON.stringify([...new Set(list)].slice(0, 8)));
        } catch { }
        onApply(value); closeModal();
      };
      sync(false);
    });
  }

  function bindColorTriggers(root = document) {
    root.querySelectorAll('[data-color-for]').forEach(btn => {
      const input = document.getElementById(btn.dataset.colorFor);
      if (!input) return;
      const sync = () => { btn.style.background = normalizeHexColor(input.value, '#e7b77c'); };
      sync();
      input.addEventListener('input', sync);
      const original = input.value;
      const preview = next => {
        input.value = next;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        sync();
      };
      const apply = next => {
        input.value = next;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
        sync();
      };
      const cancel = () => {
        input.value = original;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        sync();
        if (String(input.id || '').startsWith('theme')) clearPetThemePreview();
      };
      btn.onclick = () => openColorPicker(input.value, apply, preview, cancel);
    });
  }


  const THEME_BG_STORAGE_KEY = 'wuwa.theme.background.v1';
  const THEME_BG_SOURCE_KEY = 'wuwa.theme.background.source.v1';
  const THEME_AUTO_PALETTE_KEY = 'wuwa.theme.autoImagePalette.v1';
  const THEME_BG_MAX_BYTES = 20 * 1024 * 1024;
  const THEME_BG_COMPRESS_BYTES = 4 * 1024 * 1024;
  const THEME_BG_CHUNK_CHARS = 256 * 1024;
  let themeBgHydrating = false;
  // localStorage 配额不足时的本次会话兜底，避免随后到达的 boot 把刚上传的图片顶掉。
  let themeBgMemory = '';
  let themeBgSourceMemory = '';
  // 图片自动 palette 处于生效状态；手动改主题色/字体时据此清除整套覆盖。
  let themeImagePaletteActive = false;
  let themeImagePaletteInitialized = false;

  function autoImagePaletteEnabled() {
    try {
      const value = localStorage.getItem(THEME_AUTO_PALETTE_KEY);
      return value === null ? true : value === '1';
    } catch { return true; }
  }

  function setAutoImagePaletteEnabled(enabled) {
    try { localStorage.setItem(THEME_AUTO_PALETTE_KEY, enabled ? '1' : '0'); } catch { }
  }

  function readThemeAdvancedOverrides(settings) {
    try {
      const parsed = JSON.parse((settings || {}).themeAdvanced || '{}');
      return parsed && typeof parsed === 'object' && !Array.isArray(parsed) ? parsed : {};
    } catch { return {}; }
  }

  function hasManualAdvancedPalette(settings) {
    const overrides = readThemeAdvancedOverrides(settings);
    // __imagePalette 是自动提取留下的标记；用户改任意颜色后会被删除。
    if (overrides.__imagePalette === true || overrides.__imagePalette === 'true') return false;
    return Object.keys(overrides).some(key =>
      key !== '__imagePalette' && /^#[0-9a-f]{6}$/i.test(String(overrides[key] || '')));
  }

  function readThemeBackgroundLocal() {
    if (themeBgMemory) return themeBgMemory;
    try {
      const value = localStorage.getItem(THEME_BG_STORAGE_KEY) || '';
      return /^data:image\/(?:png|jpe?g|webp|gif);base64,/i.test(value) ? value : '';
    } catch { return ''; }
  }

  function writeThemeBackgroundLocal(value, sourceUrl = '') {
    themeBgMemory = value || '';
    themeBgSourceMemory = sourceUrl || '';
    try {
      if (value) localStorage.setItem(THEME_BG_STORAGE_KEY, value);
      else localStorage.removeItem(THEME_BG_STORAGE_KEY);
      if (value && sourceUrl) localStorage.setItem(THEME_BG_SOURCE_KEY, sourceUrl);
      else localStorage.removeItem(THEME_BG_SOURCE_KEY);
    } catch { /* 超过 localStorage 配额时仍可用 C# 文件 URL 显示 */ }
  }

  function readThemeBackgroundLocalSource() {
    if (themeBgMemory) return themeBgSourceMemory;
    try { return localStorage.getItem(THEME_BG_SOURCE_KEY) || ''; } catch { return ''; }
  }

  function themeBackgroundSource(settings) {
    const local = readThemeBackgroundLocal();
    if (local) return local;
    const s = settings || {};
    return typeof s.themeBackgroundImage === 'string' ? s.themeBackgroundImage : '';
  }

  function themeBackgroundIsSet(settings) {
    return !!readThemeBackgroundLocal()
      || !!(settings && settings.themeBackgroundImage)
      || !!(settings && settings.themeBackgroundImageSet);
  }

  function updateThemeBackgroundControls() {
    const s = state.boot?.settings || {};
    const has = themeBackgroundIsSet(s);
    const status = document.getElementById('themeBgStatus');
    if (status) status.textContent = has ? tr('settings.theme.bg.set', '已设置背景图') : tr('settings.theme.bg.unset', '未设置');
    const clear = document.getElementById('themeBgClear');
    if (clear) clear.disabled = !has;
    const extract = document.getElementById('themeExtract');
    if (extract) extract.disabled = !has;
  }

  function hydrateThemeBackground(settings) {
    if (!settings || !settings.themeBackgroundImageSet || settings.themeBackgroundImage) return;
    if (readThemeBackgroundLocal() || themeBgHydrating) return;
    themeBgHydrating = true;
    call('theme.image.get').then(r => {
      if (!r?.url || !state.boot?.settings) {
        if (state.boot?.settings) state.boot.settings.themeBackgroundImageSet = false;
        return;
      }
      state.boot.settings.themeBackgroundImage = r.url;
      state.boot.settings.themeBackgroundImageSet = true;
      applyTheme(state.boot.settings);
      updateThemeBackgroundControls();
    }).catch(() => { }).finally(() => { themeBgHydrating = false; });
  }

  function blobToDataUrl(blob) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ''));
      reader.onerror = () => reject(reader.error || new Error('读取图片失败'));
      reader.readAsDataURL(blob);
    });
  }

  function formatBytes(bytes) {
    const value = Number(bytes) || 0;
    if (value >= 1024 * 1024) return (value / (1024 * 1024)).toFixed(1) + 'MB';
    if (value >= 1024) return Math.round(value / 1024) + 'KB';
    return value + 'B';
  }

  // 面板拿到新的服务端背景图 URL 后清掉本次会话的 localStorage 预览，避免预设切换被旧图盖住。
  // source 记录 localStorage 预览对应的服务端 URL：同一张图保留（上传失败兜底），不同图才清理。
  function adoptBootThemeBackground(boot) {
    const next = boot?.settings?.themeBackgroundImage || '';
    const source = readThemeBackgroundLocalSource();
    if (!next) {
      // 当前主题已移除背景图：只有本地预览曾绑定过服务端图片时才清掉；没有 source 说明是上传失败兜底。
      if (source) writeThemeBackgroundLocal('');
      return;
    }
    if (!source || source !== next) writeThemeBackgroundLocal('');
  }

  function loadThemeImageBlob(file) {
    return new Promise((resolve, reject) => {
      const url = URL.createObjectURL(file);
      const image = new Image();
      let done = false;
      const finish = (fn, value) => {
        if (done) return;
        done = true;
        URL.revokeObjectURL(url);
        image.onload = null;
        image.onerror = null;
        fn(value);
      };
      const timer = setTimeout(() => finish(reject, new Error('图片解码超时')), 15000);
      image.onload = () => { clearTimeout(timer); finish(resolve, image); };
      image.onerror = () => { clearTimeout(timer); finish(reject, new Error('图片解码失败')); };
      image.src = url;
    });
  }

  // 超过 4MB 时用 Canvas 压缩为 JPEG，逐级降尺寸/质量直到缓存文件小于 4MB。
  async function compressThemeImage(file) {
    const image = await loadThemeImageBlob(file);
    const sourceW = image.naturalWidth || image.width;
    const sourceH = image.naturalHeight || image.height;
    if (!sourceW || !sourceH) throw new Error('图片尺寸无效');
    let maxSide = 3840;
    for (let pass = 0; pass < 6; pass++) {
      const scale = Math.min(1, maxSide / Math.max(sourceW, sourceH));
      const width = Math.max(1, Math.round(sourceW * scale));
      const height = Math.max(1, Math.round(sourceH * scale));
      const canvas = document.createElement('canvas');
      canvas.width = width;
      canvas.height = height;
      const ctx = canvas.getContext('2d');
      if (!ctx) throw new Error('无法创建压缩画布');
      ctx.imageSmoothingEnabled = true;
      ctx.imageSmoothingQuality = 'high';
      // JPEG 不支持透明，先铺白底，避免透明 PNG 压出黑块。
      ctx.fillStyle = '#ffffff';
      ctx.fillRect(0, 0, width, height);
      ctx.drawImage(image, 0, 0, width, height);
      for (let quality = 0.86; quality >= 0.54; quality -= 0.08) {
        const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/jpeg', quality));
        if (!blob) continue;
        if (blob.size <= THEME_BG_COMPRESS_BYTES - 64 * 1024) {
          return {
            dataUrl: await blobToDataUrl(blob),
            bytes: blob.size,
            width,
            height,
            quality,
          };
        }
      }
      maxSide = Math.round(maxSide * 0.78);
    }
    throw new Error('图片压缩后仍超过 4MB，请换一张尺寸更小的图片');
  }

  function buildThemePalette(settings) {
    const s = settings || {};
    const primary = normalizeHexColor(s.themeAccent, '#e7b77c');
    const font = normalizeHexColor(s.themeFont, '#8e5e43');
    const [h, sat, light] = rgbToHsl(...hexToRgb(primary));
    const tonal = (saturation, lightness) => hslToHex(h, saturation, lightness);
    // 默认字体色跟主题色联动；用户手动改过字体颜色时以用户值为准。
    const inkAuto = tonal(Math.min(0.42, Math.max(0.16, sat * 0.52)), 0.30);
    const ink = font === '#8e5e43' ? inkAuto : font;
    const cream4 = tonal(Math.min(0.32, sat), 0.988);
    const auto = {
      primary,
      cream: tonal(Math.min(0.42, sat), 0.963),
      cream2: tonal(Math.min(0.48, sat), 0.925),
      cream3: tonal(Math.min(0.46, sat), 0.865),
      cream4,
      ink,
      inkStrong: mixHex(ink, '#2b1710', 0.38),
      inkDim: mixHex(ink, cream4, 0.45),
      border: tonal(Math.min(0.62, Math.max(0.25, sat)), Math.min(0.68, Math.max(0.44, light * 0.78))),
      active: tonal(Math.min(0.72, Math.max(0.30, sat)), 0.82),
      accentStrong: tonal(Math.min(0.82, Math.max(0.35, sat)), Math.min(0.58, Math.max(0.30, light - 0.18))),
      accentSoft: tonal(Math.min(0.68, Math.max(0.25, sat)), 0.88),
    };
    const overrides = readThemeAdvancedOverrides(s);
    for (const key of Object.keys(auto)) {
      if (key !== 'primary' && /^#[0-9a-f]{6}$/i.test(String(overrides[key] || '')))
        auto[key] = String(overrides[key]).toLowerCase();
    }
    return auto;
  }

  function cssBackgroundUrl(value) {
    return `url("${String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/[\r\n]/g, '')}")`;
  }

  function rgbHex(color) {
    return '#' + [color.r, color.g, color.b]
      .map(v => Math.max(0, Math.min(255, Math.round(v))).toString(16).padStart(2, '0')).join('');
  }

  function colorDistance(a, b) {
    const dr = a.r - b.r, dg = a.g - b.g, db = a.b - b.b;
    return Math.sqrt(dr * dr + dg * dg + db * db);
  }

  function hueDistance(a, b) {
    const d = Math.abs(a - b) % 1;
    return Math.min(d, 1 - d);
  }

  function loadImageElement(src, anonymous) {
    return new Promise((resolve, reject) => {
      const img = new Image();
      if (anonymous) img.crossOrigin = 'anonymous';
      let done = false;
      const finish = (fn, value) => {
        if (done) return;
        done = true;
        clearTimeout(timer);
        img.onload = null;
        img.onerror = null;
        fn(value);
      };
      const timer = setTimeout(() => finish(reject, new Error('图片加载超时')), 8000);
      img.onload = () => finish(resolve, img);
      img.onerror = () => finish(reject, new Error('图片加载失败'));
      img.src = src;
    });
  }

  async function loadImageForSampling(src) {
    if (!src) throw new Error('没有可读取的背景图');
    if (/^data:image\//i.test(src)) return await loadImageElement(src, false);
    try {
      // 优先 fetch 成 Blob，避免虚拟主机图片污染 canvas。
      const response = await fetch(src, { cache: 'no-store' });
      if (!response.ok) throw new Error('HTTP ' + response.status);
      const blob = await response.blob();
      const objectUrl = URL.createObjectURL(blob);
      try { return await loadImageElement(objectUrl, false); }
      finally { URL.revokeObjectURL(objectUrl); }
    } catch {
      // 没有 CORS 时仍尝试直接加载；若 canvas 被污染会在 getImageData 处给出明确错误。
      return await loadImageElement(src, true);
    }
  }

  function sampleImageColors(img) {
    const sourceW = img.naturalWidth || img.width;
    const sourceH = img.naturalHeight || img.height;
    if (!sourceW || !sourceH) throw new Error('图片尺寸无效');
    const maxSide = 128;
    const scale = Math.min(1, maxSide / Math.max(sourceW, sourceH));
    const width = Math.max(1, Math.round(sourceW * scale));
    const height = Math.max(1, Math.round(sourceH * scale));
    const canvas = document.createElement('canvas');
    canvas.width = width;
    canvas.height = height;
    const ctx = canvas.getContext('2d', { willReadFrequently: true });
    if (!ctx) throw new Error('无法创建图片采样画布');
    ctx.drawImage(img, 0, 0, width, height);
    let data;
    try { data = ctx.getImageData(0, 0, width, height).data; }
    catch { throw new Error('图片跨域，无法读取像素'); }

    const bins = new Map();
    const light = { r: 0, g: 0, b: 0, weight: 0 };
    const dark = { r: 0, g: 0, b: 0, weight: 0 };
    let sampled = 0;
    let totalWeight = 0;
    for (let i = 0; i < data.length; i += 4) {
      const alpha = data[i + 3];
      if (alpha < 128) continue;
      const r = data[i], g = data[i + 1], b = data[i + 2];
      const [hue, sat, lightness] = rgbToHsl(r, g, b);
      sampled++;
      if (lightness >= 0.72) {
        const weight = 1 + (lightness - 0.72) * 4;
        light.r += r * weight; light.g += g * weight; light.b += b * weight; light.weight += weight;
      }
      if (lightness <= 0.32) {
        const weight = 1 + (0.32 - lightness) * 4;
        dark.r += r * weight; dark.g += g * weight; dark.b += b * weight; dark.weight += weight;
      }
      if (lightness < 0.01 || lightness > 0.995) continue;
      const hueBin = Math.min(23, Math.floor(hue * 24));
      const satBin = sat < 0.12 ? 0 : sat < 0.35 ? 1 : sat < 0.65 ? 2 : 3;
      const lightBin = lightness < 0.25 ? 0 : lightness < 0.45 ? 1 : lightness < 0.70 ? 2 : lightness < 0.88 ? 3 : 4;
      const key = `${hueBin}|${satBin}|${lightBin}`;
      let bin = bins.get(key);
      if (!bin) {
        bin = { r: 0, g: 0, b: 0, count: 0, weight: 0, satSum: 0, hueSin: 0, hueCos: 0 };
        bins.set(key, bin);
      }
      const weight = 1 + sat * 2.4 + (lightness > 0.12 && lightness < 0.9 ? 0.35 : 0);
      bin.r += r; bin.g += g; bin.b += b;
      bin.count++;
      bin.weight += weight;
      bin.satSum += sat;
      bin.hueSin += Math.sin(hue * Math.PI * 2);
      bin.hueCos += Math.cos(hue * Math.PI * 2);
      totalWeight += weight;
    }
    if (!bins.size) throw new Error('图片没有可用的颜色');
    const entries = [...bins.values()].map(bin => {
      const count = Math.max(1, bin.count);
      const avg = { r: bin.r / count, g: bin.g / count, b: bin.b / count };
      const hue = ((Math.atan2(bin.hueSin, bin.hueCos) / (Math.PI * 2)) + 1) % 1;
      const sat = bin.satSum / count;
      return { avg, hue, sat, weight: bin.weight, count: bin.count, score: bin.weight * (0.35 + sat * 1.7) };
    }).sort((a, b) => b.score - a.score);
    const primary = entries.find(item => item.sat >= 0.12 && item.weight >= totalWeight * 0.02) || entries[0];
    const secondary = entries.find(item =>
      item !== primary
      && item.count >= Math.max(4, sampled * 0.004)
      && (hueDistance(item.hue, primary.hue) > 0.07 || colorDistance(item.avg, primary.avg) > 45));
    return {
      primary: primary.avg,
      secondary: secondary ? secondary.avg : null,
      light: light.weight > 0 ? { r: light.r / light.weight, g: light.g / light.weight, b: light.b / light.weight } : null,
      dark: dark.weight > 0 ? { r: dark.r / dark.weight, g: dark.g / dark.weight, b: dark.b / dark.weight } : null,
    };
  }

  function buildPaletteFromImageColors(colors) {
    let [primaryHue, primarySat, primaryLight] = rgbToHsl(colors.primary.r, colors.primary.g, colors.primary.b);
    if (primarySat < 0.14) primarySat = 0.34;
    primarySat = Math.min(0.85, Math.max(0.25, primarySat));
    primaryLight = Math.min(0.68, Math.max(0.36, primaryLight));
    const primary = hslToHex(primaryHue, primarySat, primaryLight);
    // 先沿用现有主题算法生成一版基础色，再用图片的明暗和辅助色做混合。
    const base = buildThemePalette({ themeAccent: primary, themeFont: '#8e5e43' });

    const light = colors.light || { r: 250, g: 246, b: 238 };
    let [lightHue, lightSat, lightLight] = rgbToHsl(light.r, light.g, light.b);
    const lightBase = hslToHex(lightHue, Math.min(0.40, Math.max(0.08, lightSat)), Math.min(0.985, Math.max(0.90, lightLight)));

    let secondaryHex;
    if (colors.secondary) {
      const [sh, ss, sl] = rgbToHsl(colors.secondary.r, colors.secondary.g, colors.secondary.b);
      secondaryHex = hslToHex(sh, Math.min(0.72, Math.max(0.22, ss)), Math.min(0.68, Math.max(0.38, sl)));
    } else {
      secondaryHex = hslToHex((primaryHue + 0.08) % 1, Math.min(0.60, Math.max(0.25, primarySat * 0.7)), 0.55);
    }

    let ink;
    if (colors.dark) {
      const [dh, ds, dl] = rgbToHsl(colors.dark.r, colors.dark.g, colors.dark.b);
      ink = hslToHex(dh, Math.min(0.50, Math.max(0.12, ds)), Math.min(0.32, Math.max(0.18, dl)));
    } else {
      ink = hslToHex(primaryHue, Math.min(0.40, Math.max(0.18, primarySat * 0.55)), 0.26);
    }

    const cream = mixHex(base.cream, lightBase, 0.30);
    const cream2 = mixHex(base.cream2, lightBase, 0.24);
    const cream3 = mixHex(base.cream3, secondaryHex, 0.14);
    const cream4 = mixHex(base.cream4, lightBase, 0.18);
    const border = mixHex(base.border, secondaryHex, 0.30);
    const active = mixHex(base.active, secondaryHex, 0.26);
    return {
      accent: primary,
      font: ink,
      overrides: {
        cream,
        cream2,
        cream3,
        cream4,
        ink,
        inkStrong: mixHex(ink, '#2b1710', 0.42),
        inkDim: mixHex(ink, cream4, 0.45),
        border,
        active,
        accentStrong: mixHex(base.accentStrong, secondaryHex, 0.20),
        accentSoft: mixHex(base.accentSoft, lightBase, 0.35),
      },
    };
  }

  function applyTheme(settings) {
    const palette = buildThemePalette(settings);
    const root = document.documentElement;
    root.style.setProperty('--theme-accent', palette.primary);
    root.style.setProperty('--theme-accent-strong', palette.accentStrong);
    root.style.setProperty('--theme-accent-soft', palette.accentSoft);
    root.style.setProperty('--theme-font', palette.ink);
    root.style.setProperty('--cream', palette.cream);
    root.style.setProperty('--cream-2', palette.cream2);
    root.style.setProperty('--cream-3', palette.cream3);
    root.style.setProperty('--cream-4', palette.cream4);
    root.style.setProperty('--border', palette.border);
    root.style.setProperty('--active', palette.active);
    root.style.setProperty('--ink', palette.ink);
    root.style.setProperty('--ink-strong', palette.inkStrong);
    root.style.setProperty('--ink-dim', palette.inkDim);

    const s = settings || {};
    const image = themeBackgroundSource(s);
    const opacity = Math.max(0, Math.min(1, Number(s.themeBackgroundOpacity ?? 0.25)));
    const blur = Math.max(0, Math.min(24, Number(s.themeBackgroundBlur ?? 0)));
    const bg = document.getElementById('themeBg');
    if (bg) bg.style.backgroundImage = image ? cssBackgroundUrl(image) : 'none';
    document.body.classList.toggle('theme-has-bg', !!image && opacity > 0);
    root.style.setProperty('--theme-bg-opacity', image ? opacity : 0);
    root.style.setProperty('--theme-bg-blur', `${blur}px`);
    hydrateThemeBackground(s);
  }

  const THEME_ADVANCED_FIELDS = [
    ['cream', '背景色 1'],
    ['cream2', '背景色 2'],
    ['cream3', '背景色 3'],
    ['cream4', '背景色 4'],
    ['ink', '正文颜色'],
    ['inkStrong', '强调文字'],
    ['inkDim', '次要文字'],
    ['border', '边框颜色'],
    ['active', '选中背景'],
    ['accentStrong', '强调深色'],
    ['accentSoft', '强调浅色'],
  ];

  function scrollSettingsSectionIntoView(section, behavior = 'smooth') {
    const scroller = document.querySelector('.settings-scroll');
    if (!scroller || !section) return;
    // 不能对 section 调 scrollIntoView：它会连带滚动 html/body，把标题栏一起滚出窗口。
    const scrollerRect = scroller.getBoundingClientRect();
    const sectionRect = section.getBoundingClientRect();
    const target = scroller.scrollTop
      + (sectionRect.top - scrollerRect.top)
      - Math.max(8, (scroller.clientHeight - sectionRect.height) / 2);
    scroller.scrollTo({ top: Math.max(0, target), behavior });
    // 防御性复位：滚动只允许发生在 .settings-scroll 内。
    resetDocumentScroll();
  }

  function openThemeSettings() {
    if (state.page !== 'settings') navigate('settings');
    const reveal = () => {
      const group = document.getElementById('settings-group-theme');
      if (group) {
        if (group.tagName === 'DETAILS') group.open = true;
        group.setAttribute('open', '');
        state.settingsOpenGroups.add('theme');
        saveSettingsOpenGroups();
      }
      const section = document.getElementById('themeSection');
      if (!section) return;
      scrollSettingsSectionIntoView(section);
      section.classList.remove('theme-highlight');
      void section.offsetWidth;
      section.classList.add('theme-highlight');
      setTimeout(() => section.classList.remove('theme-highlight'), 1700);
    };
    setTimeout(reveal, 80);
    setTimeout(reveal, 360);
  }

  async function togglePanelMaximized() {
    try {
      const r = await call('win.max');
      applyPanelMaximized(r?.maximized);
    } catch { }
  }

  function edgeResizeDirection(x, y) {
    const grip = 6;
    const w = document.documentElement.clientWidth;
    const h = document.documentElement.clientHeight;
    const left = x <= grip;
    const right = x >= w - grip - 1;
    const top = y <= grip;
    const bottom = y >= h - grip - 1;
    if (top && left) return 'topleft';
    if (top && right) return 'topright';
    if (bottom && left) return 'bottomleft';
    if (bottom && right) return 'bottomright';
    if (left) return 'left';
    if (right) return 'right';
    if (top) return 'top';
    if (bottom) return 'bottom';
    return '';
  }

  const RESIZE_CURSORS = {
    left: 'ew-resize', right: 'ew-resize',
    top: 'ns-resize', bottom: 'ns-resize',
    topleft: 'nwse-resize', bottomright: 'nwse-resize',
    topright: 'nesw-resize', bottomleft: 'nesw-resize',
  };

  function init() {
    const hashParams = HASH_PARAMS;
    loadSettingsOpenGroups();
    const initialPage = hashParams.get('page');
    if (initialPage && PAGES.some(p => p.key === initialPage)) state.page = initialPage;
    const warningIcon = '<svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 3 2.8 20h18.4L12 3Z"/><path d="M12 9v4.5"/><path d="M12 17h.01"/></svg>';
    document.getElementById('tabs').innerHTML = PAGES.map(p =>
      `<button class="tab" data-page="${p.key}">${ICONS[p.icon]}<span>${esc(pageTitleOf(p))}</span>${p.key === 'synth' && state.synthEnabled ? `<span class="tab-warning" title="${esc(tr('nav.synthAdvancedOn', '高级合成已开启'))}">${warningIcon}</span>` : ''}${p.key === 'synth' ? `<span class="tab-dot" data-tab-badge="synth" title="${esc(tr('nav.synthItems', '有可合成物资'))}"></span>` : ''}</button>`).join('');
    document.querySelectorAll('.tab').forEach(btn => btn.onclick = () => navigate(btn.dataset.page));
    document.getElementById('closeBtn').onclick = () => call('win.close');
    document.getElementById('winCloseBtn').onclick = () => call('win.close');
    document.getElementById('themeBtn').onclick = openThemeSettings;
    document.getElementById('minBtn').onclick = () => call('win.min');
    document.getElementById('maxBtn').onclick = togglePanelMaximized;
    window.addEventListener('mousemove', e => {
      if (document.body.classList.contains('panel-maximized')) {
        document.body.style.cursor = '';
        return;
      }
      const dir = edgeResizeDirection(e.clientX, e.clientY);
      document.body.style.cursor = dir ? RESIZE_CURSORS[dir] : '';
    });
    let edgeResizeActive = false;
    window.addEventListener('mousedown', e => {
      if (e.button !== 0 || document.body.classList.contains('panel-maximized')) return;
      const dir = edgeResizeDirection(e.clientX, e.clientY);
      if (!dir) return;
      e.preventDefault();
      e.stopPropagation();
      edgeResizeActive = true;
      call('win.resizeStart', { dir });
    }, true);
    window.addEventListener('mouseup', () => {
      if (!edgeResizeActive) return;
      edgeResizeActive = false;
      call('win.resizeEnd');
    });
    const titlebar = document.getElementById('titlebar');
    let titleDragArmed = false;
    let titleDragStarted = false;
    let titleDragX = 0;
    let titleDragY = 0;
    titlebar.addEventListener('mousedown', e => {
      if (e.button !== 0 || e.target.closest('button')) return;
      if (document.body.classList.contains('panel-maximized')) return;
      titleDragArmed = true;
      titleDragStarted = false;
      titleDragX = e.clientX;
      titleDragY = e.clientY;
    });
    // 拖拽阈值：双击标题栏时不启动原生拖动，才能稳定触发最大化/还原。
    window.addEventListener('mousemove', e => {
      if (!titleDragArmed || titleDragStarted) return;
      if (Math.hypot(e.clientX - titleDragX, e.clientY - titleDragY) < 4) return;
      titleDragStarted = true;
      titleDragArmed = false;
      call('win.drag');
    });
    window.addEventListener('mouseup', () => {
      titleDragArmed = false;
      titleDragStarted = false;
    });
    titlebar.addEventListener('dblclick', e => {
      if (e.target.closest('button')) return;
      togglePanelMaximized();
    });
    navigate(state.page);
    setInterval(updateChestStatus, 1000);
  }

  function maybeOpenEmoteEditor() {
    if (HASH_PARAMS.get('editEmotes') === '1' && isLogged() && !state.editEmotesOpened) {
      state.editEmotesOpened = true;
      setTimeout(() => openEmoteEditor(), 400);
    }
  }

  async function boot() {
    try {
      const b = await call('boot');
      const prevRegion = state.boot?.settings?.serverRegion || 'cn';
      adoptBootThemeBackground(b);
      state.boot = b;
      if (Array.isArray(b.companions)) state.companions = b.companions;
      applyLocale(b.settings?.locale);
      migrateLegacyAssetStorage();
      applyTheme(b.settings);
      state.update = b.update || state.update;
      state.announcements = Array.isArray(b.announcements) ? b.announcements : [];
      state.announcementHistory = Array.isArray(b.announcementHistory) ? b.announcementHistory : [];
      mergePendingRecentChests(b.pendingRecentChestIds);
      applyPanelMaximized(b.panelMaximized);
      renderChrome();
      // WebView ready 事件和显式 boot() 可能拿到同一份数据；身份没变时不重复整页重建。
      const identity = authIdentityKey(b);
      if (state.renderedIdentity !== identity) {
        resetAccountScopedState(prevRegion !== (b?.settings?.serverRegion || 'cn'));
        state.renderedIdentity = identity;
        renderPage();
      }
      if (isLogged() && authOnlineAvailable()) ensureBag().catch(() => { });
      maybeShowDisclaimer();
      maybeShowUacPrompt();
      maybeOpenEmoteEditor();
    } catch (e) { toast(tr('error.initFailed', '初始化失败：{error}', { error: e.message }), 'err'); }
  }

  function disclaimerText() {
    return [
      tr('disclaimer.text1', '如果你花费了任何金额购买了此程序，那你绝对是被骗了，请你立刻退款并差评。'),
      tr('disclaimer.text2', '本程序所有素材版权属于库洛游戏所有，本项目与库洛无任何关联，如有侵权，请立刻联系我。'),
      tr('disclaimer.text3', '本程序只用于对新技术的学习与研究，任何下载了此项目的用户请在24h内删除本程序，谢谢配合。')
    ];
  }

  function maybeShowDisclaimer() {
    if (!state.boot || state.boot.disclaimerShown) return;
    if (state.disclaimerOpen) return;
    state.disclaimerOpen = true;
    modal(`
      <div class="about">
        <div class="about-title">${tr('disclaimer.title', '免责声明')}</div>
        <div class="about-note about-note--warn">
          ${disclaimerText().map(t => `<p>${esc(t)}</p>`).join('')}
        </div>
        <button class="btn" id="mAgree" style="align-self:center;min-width:160px">${tr('disclaimer.agree', '我已知晓')}</button>
      </div>`, host => {
      // 首次启动的声明必须点按钮确认，点遮罩不关闭。
      host.onclick = null;
      host.querySelector('#mAgree').onclick = async () => {
        state.disclaimerOpen = false;
        if (state.boot) state.boot.disclaimerShown = true;
        closeModal();
        try { await call('ui.disclaimerShown'); } catch { }
        // 免责声明之后再询问一次 UAC；使用 setTimeout 让当前弹窗先完成关闭。
        setTimeout(() => maybeShowUacPrompt(), 120);
      };
    });
  }

  function maybeShowUacPrompt() {
    if (!state.boot || state.boot.uacPromptShown) return;
    if (state.boot.settings?.isElevated) return;
    if (state.uacPromptOpen || state.disclaimerOpen) return;
    state.uacPromptOpen = true;
    modal(`
      <div class="about">
        <div class="about-title">${tr('uac.title', '管理员权限（UAC）')}</div>
        <div class="about-note">
          <p>${tr('uac.question', '是否授予该应用管理员 UAC 权限？')}</p>
          <p>${tr('uac.body1', '标准权限无法监听其他 UAC 程序的输入。本程序只把管理员权限用于触发键位，不会有任何敏感操作、上传键盘内容等行为；可自行使用 AI 等工具审查源代码。')}</p>
          <p>${tr('uac.body2', '如不授予，可能会影响部分程序内的键位统计，其他功能不受影响。')}</p>
        </div>
        <div style="display:flex;gap:10px;justify-content:center">
          <button class="btn" id="mUacYes">${tr('common.yes', '是')}</button>
          <button class="btn ghost" id="mUacNo">${tr('common.no', '否')}</button>
        </div>
      </div>`, host => {
      host.onclick = null;
      const yes = host.querySelector('#mUacYes');
      const no = host.querySelector('#mUacNo');
      const finish = (granted) => {
        yes.disabled = true; no.disabled = true;
        state.uacPromptOpen = false;
        if (state.boot) state.boot.uacPromptShown = true;
        closeModal();
        try { call('ui.uacPromptResult', { granted }); } catch { }
      };
      yes.onclick = async () => {
        yes.disabled = true; no.disabled = true;
        yes.textContent = tr('uac.requesting', '正在请求…');
        try {
          const r = await call('ui.uacPromptResult', { granted: true });
          state.uacPromptOpen = false;
          if (state.boot) {
            state.boot.uacPromptShown = true;
            if (state.boot.settings) state.boot.settings.runAsAdmin = !!r?.granted;
          }
          closeModal();
          if (r?.restarting) toast(tr('uac.grantedRestarting', '已授予管理员权限，正在重启…'));
          else if (r?.cancelled) toast(tr('uac.cancelled', '已取消 UAC，继续使用普通权限；可随时在设置中开启'), 'err');
          else if (r?.elevated) toast(tr('uac.elevated', '已启用管理员权限启动'));
        } catch {
          yes.disabled = false; no.disabled = false;
          yes.textContent = tr('common.yes', '是');
          toast(tr('uac.requestFailed', '请求管理员权限失败'), 'err');
        }
      };
      no.onclick = () => {
        if (state.boot?.settings) state.boot.settings.runAsAdmin = false;
        finish(false);
        toast(tr('uac.skipped', '已跳过管理员权限；可随时在设置中开启'));
      };
    });
  }

  function showAbout() {
    const version = state.boot?.displayVersion || state.boot?.version || '0.0.1';
    const link = (href, text) => `<a class="about-link" href="${esc(href)}" target="_blank" rel="noreferrer">${esc(text)}</a>`;
    modal(`
      <div class="about">
        <div class="about-head">
          <div class="about-badge">W</div>
          <div>
            <div class="about-name">Wuwatappo Native</div>
            <div class="about-ver">${esc(tr('about.version', '版本 {version}', { version }))}</div>
          </div>
        </div>
        <div class="about-note">
          ${disclaimerText().map(t => `<p>${esc(t)}</p>`).join('')}
        </div>
        <div class="about-sec">
          <div class="about-sec-title">${tr('about.author', '作者')}</div>
          <p>${tr('about.madeBy', '本程序由 {link} 参考官方 WuwaTappo 制作。').replace('{link}', link('http://danieluu.top', 'Daniel_兔兔'))}</p>
        </div>
        <div class="about-sec">
          <div class="about-sec-title">${tr('about.thanks', '特别鸣谢')}</div>
          <p>${link(state.boot?.settings?.serverRegion === 'global' ? 'https://download-wutheringwaves-wuwatappo.kurogames-global.com/download/en/' : 'https://download-mc-wuwatappo.kurogames-global.com/download/', tr('about.kuro', '库洛游戏'))}</p>
          <p>${link('https://github.com/Fantasicrap/', '@Fantasicrap')}</p>
        </div>
        <div class="about-sec">
          <div class="about-sec-title">${tr('about.demo', '演示视频')}</div>
          <p>${link('https://www.bilibili.com/video/BV1nRxk6dERS/', tr('about.demoLink', 'Bilibili 演示视频'))}</p>
          <p>${tr('about.demoNote', '如果可以请给我一键三连，这对我非常重要。')}</p>
        </div>
        <div class="about-sec">
          <div class="about-sec-title">${tr('about.project', '项目地址')}</div>
          <p>GitHub：${link('https://github.com/DanielToyama/WuwaTappoNative', 'DanielToyama/WuwaTappoNative')}</p>
          <p>${tr('about.star', '希望收到你的 Star！⭐')}</p>
        </div>
        <div class="about-sec">
          <div class="about-sec-title">${tr('about.dataTitle', '数据统计说明')}</div>
          <p>${tr('about.dataBody', '本工具第三方服务器（用于更新和读取公告）会接收请求来源的 IP 地址，仅用于聚合统计用户规模、在线情况及省级地区分布，用于改善与分析用户体验。原始 IP 不持久化存储，不与任何身份关联，不用于追踪或广告。地区信息仅精确到省级。')}</p>
        </div>
        <button class="btn ghost" id="mCloseAbout" style="align-self:center;min-width:120px">${tr('about.close', '关闭')}</button>
      </div>`, host => {
      host.querySelector('#mCloseAbout').onclick = closeModal;
    });
  }

  function announcementSeverity(a) {
    const severity = String(a?.severity || '').toLowerCase();
    return ['info', 'warning', 'critical'].includes(severity) ? severity : 'info';
  }

  function announcementTimestamp(a) {
    const value = a?.publishedAt;
    if (value == null || value === '') return -Infinity;
    const time = typeof value === 'number' ? value : Date.parse(String(value));
    return Number.isFinite(time) ? time : -Infinity;
  }

  function announcementDateText(value) {
    if (value == null || value === '') return '';
    const date = typeof value === 'number' ? new Date(value) : new Date(String(value));
    if (Number.isNaN(date.getTime())) return '';
    const pad = n => String(n).padStart(2, '0');
    return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
  }

  /** 优先使用服务器下发的完整公告牌；推送尚未到达时回退到未读公告，保证入口可用。 */
  function announcementHistoryList() {
    const source = Array.isArray(state.announcementHistory) && state.announcementHistory.length
      ? state.announcementHistory
      : (Array.isArray(state.announcements) ? state.announcements : []);
    return source
      .filter(a => a && typeof a === 'object')
      .map((a, index) => ({ a, index, time: announcementTimestamp(a) }))
      .sort((x, y) => x.time === y.time ? x.index - y.index : y.time - x.time)
      .map(entry => entry.a);
  }

  function showAnnouncementHistory() {
    const list = announcementHistoryList();
    const readIds = new Set((Array.isArray(state.announcements) ? state.announcements : [])
      .map(a => a?.id == null ? '' : String(a.id))
      .filter(Boolean));
    const metaOf = {
      info: { label: tr('ann.info', '公告'), icon: 'i' },
      warning: { label: tr('ann.warning', '注意'), icon: '!' },
      critical: { label: tr('ann.critical', '重要'), icon: '!' }
    };
    const items = list.map(a => {
      const severity = announcementSeverity(a);
      const meta = metaOf[severity];
      const dateText = announcementDateText(a.publishedAt);
      const action = a.action && a.action.url
        ? `<div class="announcement-history-actions"><a class="btn ghost" href="${esc(a.action.url)}" target="_blank" rel="noreferrer">${esc(a.action.label || tr('ann.viewDetails', '查看详情'))}</a></div>`
        : '';
      const unread = a.id != null && readIds.has(String(a.id))
        ? `<span class="announcement-history-unread">${tr('ann.unread', '未读')}</span>`
        : '';
      return `<article class="announcement-history-item ${severity}" data-ann-id="${esc(a.id)}">
        <div class="announcement-history-meta">
          <span class="announcement-history-badge"><span>${meta.icon}</span>${meta.label}</span>
          <time class="announcement-history-date"${dateText ? ` datetime="${dateText}"` : ''}>${dateText || tr('ann.timeUnknown', '时间未知')}</time>
          ${unread}
        </div>
        <div class="announcement-history-title">${esc(a.title || tr('ann.fallbackTitle', '公告'))}</div>
        <div class="announcement-history-body">${esc(a.body || '')}</div>
        ${action}
      </article>`;
    }).join('');
    const body = list.length
      ? `<div class="announcement-history-list">${items}</div>`
      : `<div class="announcement-history-empty">
          <div class="announcement-history-empty-mark">i</div>
          <div class="announcement-history-empty-title">${tr('ann.emptyTitle', '暂无公告历史')}</div>
          <div class="announcement-history-empty-sub">${tr('ann.emptySub', '服务器当前没有可显示的公告。')}</div>
        </div>`;
    modal(`
      <div class="about announcement-history">
        <div class="announcement-history-head">
          <div class="about-title">${tr('ann.historyTitle', '公告历史')}</div>
          <div class="announcement-history-sub">${list.length ? tr('ann.historyCount', '共 {count} 条 · 最新发布在前', { count: list.length }) : tr('ann.historyOrder', '按发布时间从新到旧排列')}</div>
        </div>
        ${body}
        <button class="btn ghost" id="mCloseAnnouncementHistory" style="align-self:center;min-width:120px">${tr('common.close', '关闭')}</button>
      </div>`, host => {
      host.querySelector('.modal')?.classList.add('announcement-history-modal');
      host.querySelector('#mCloseAnnouncementHistory').onclick = closeModal;
    });
  }

  function showInheritLogin() {
    modal(`
      <div class="about">
        <div class="about-title">${tr('inherit.title', '继承官方程序登录状态')}</div>
        <div class="about-note about-note--warn">
          <p>${tr('inherit.tip1', '建议在获取前先关闭本家程序的开机自动启动，以免下次开机因为 token 冲突导致掉线。')}</p>
          <p>${tr('inherit.tip2', '继续后会读取本机官方 WuwaTappo 客户端的登录数据；如果官方客户端正在运行，会被关闭。读取到的登录凭据只会发送给库洛官方服务器用于换取登录 token，不会发送到其他地址。')}</p>
          <p>${tr('inherit.tip3', '如果当前已经登录了其他账号，会被官方客户端的账号替换。请确认官方客户端已经在当前 Windows 用户下登录过一次。')}</p>
        </div>
        <div style="display:flex;gap:10px;justify-content:center">
          <button class="btn ghost" id="inheritCancel">${tr('common.cancel', '取消')}</button>
          <button class="btn" id="inheritGo">${tr('inherit.start', '开始继承')}</button>
        </div>
      </div>`, host => {
      const cancel = host.querySelector('#inheritCancel');
      const go = host.querySelector('#inheritGo');
      let busy = false;
      host.onclick = e => { if (e.target === host && !busy) closeModal(); };
      cancel.onclick = () => { if (!busy) closeModal(); };
      go.onclick = async () => {
        if (busy) return;
        busy = true;
        cancel.disabled = true;
        go.disabled = true;
        go.textContent = tr('inherit.reading', '正在读取…');
        try {
          const r = await tryCall('token.inherit');
          state.boot = await call('boot');
          closeModal();
          renderChrome();
          navigate('bag');
          toast(r?.nickname
            ? tr('inherit.successNick', '已继承官方程序登录状态：{nick}', { nick: r.nickname })
            : tr('inherit.success', '已继承官方程序登录状态'));
        } catch {
          busy = false;
          cancel.disabled = false;
          go.disabled = false;
          go.textContent = tr('inherit.start', '开始继承');
        }
      };
    });
  }

  function handleEvent(evt, data) {
    if (evt === 'boot') {
      const prev = state.boot;
      const prevAuthKey = authIdentityKey(prev);
      const prevAuthState = prev?.auth?.state || '';
      const prevOnline = !!prev?.auth?.onlineAvailable;
      const prevSettingsSig = bootSettingsSignature(prev);
      const prevBagSig = bootBagSignature(prev);
      const prevSynthSig = bootSynthSignature(prev);
      const prevRoomSig = roomSignature(prev?.room);
      adoptBootThemeBackground(data);
      state.boot = data;
      if (Array.isArray(data.companions)) state.companions = data.companions;
      applyLocale(data.settings?.locale);
      migrateLegacyAssetStorage();
      state.update = data.update || state.update;
      state.compatibility = data.compatibility || state.compatibility;
      state.announcements = Array.isArray(data.announcements) ? data.announcements : state.announcements;
      state.announcementHistory = Array.isArray(data.announcementHistory) ? data.announcementHistory : state.announcementHistory;
      mergePendingRecentChests(data.pendingRecentChestIds);
      applyPanelMaximized(data.panelMaximized);
      applyTheme(data.settings);
      applySynthSettings(data);
      renderChrome();
      applyAuthFields(data.auth);
      const nextAuthKey = authIdentityKey(data);
      const identityChanged = !prev || prevAuthKey !== nextAuthKey;
      if (identityChanged && state.renderedIdentity !== nextAuthKey) {
        const regionChanged = (prev?.settings?.serverRegion || 'cn') !== (data?.settings?.serverRegion || 'cn');
        resetAccountScopedState(regionChanged);
        state.renderedIdentity = nextAuthKey;
        renderPage();
        if (isLogged() && authOnlineAvailable()) ensureBag().catch(() => { });
      } else if (identityChanged) {
        // 同一身份已经由 boot() 渲染过，不再整页重建。
      } else if (prevAuthState !== (data?.auth?.state || '') || prevOnline !== !!data?.auth?.onlineAvailable) {
        // 登录态变化但账号身份没变：更新顶部条、设置状态行和当前页面的暂停态。
        if (state.page === 'settings') renderSettings();
        else if (state.page === 'room') renderRoom();
        else renderPage();
      } else if (state.page === 'settings') {
        // 只更新真正变化的设置；点击/宝箱倒计时等 auth 字段不会触发整页重建。
        if (prevSettingsSig !== bootSettingsSignature(data)) renderSettings();
      } else if (state.page === 'bag') {
        if (state.bag && prevBagSig !== bootBagSignature(data)) {
          const host = document.getElementById('bagGroups');
          const scroll = host?.scrollTop || 0;
          renderBagList();
          renderBagDetail();
          if (host) host.scrollTop = scroll;
        }
      } else if (state.page === 'atlas') {
        if (prevBagSig !== bootBagSignature(data)) {
          const host = document.getElementById('atlasGroups');
          const scroll = host?.scrollTop || 0;
          const snap = state.atlasSnapshot;
          if (snap) renderAtlasList(snap.items, snap.ownedNames);
          if (host) host.scrollTop = scroll;
        }
      } else if (state.page === 'synth') {
        if (prevSynthSig !== bootSynthSignature(data)) renderSynth(true);
      } else if (state.page === 'room') {
        if (prevRoomSig !== roomSignature(data.room)) renderRoom();
      } else if (state.page === 'companion') {
        renderCompanion();
      }
      maybeShowDisclaimer();
      maybeShowUacPrompt();
      maybeOpenEmoteEditor();
      return;
    }
    if (evt === 'navigate') { if (data?.page) navigate(data.page); return; }
    if (evt === 'announcements') {
      state.announcements = Array.isArray(data) ? data : [];
      renderNotices();
      return;
    }
    if (evt === 'announcementHistory') {
      // 公告牌是服务器当前下发的全部有效公告（含已读），不是本地历史归档。
      state.announcementHistory = Array.isArray(data) ? data : [];
      return;
    }
    if (evt === 'compatibility') {
      state.compatibility = data;
      renderNotices();
      if (state.page === 'settings') renderSettings();
      if (state.page === 'room') renderRoom();
      return;
    }
    if (evt === 'update') {
      const nextTag = data?.tag || data?.Tag || '';
      const prevTag = state.update?.tag || state.update?.Tag || '';
      if (nextTag && nextTag !== prevTag && state.updateDownload?.tag !== nextTag) state.updateDownload = null;
      state.update = data;
      if (state.page === 'settings') renderSettings();
      return;
    }
    if (evt === 'update.progress') {
      state.updateDownload = data;
      if (state.page === 'settings') {
        if (data?.phase === 'done' || data?.phase === 'error' || data?.phase === 'cancelled') renderSettings();
        else updateDownloadStatusUi();
      } else if (data?.phase === 'done') {
        toast(tr('toast.updateDownloaded', '更新安装包下载完成'));
      } else if (data?.phase === 'error') {
        toast(data.message || tr('toast.updateDownloadFailed', '更新下载失败'), 'err');
      }
      return;
    }
    if (evt === 'auth') {
      if (!state.boot) return;
      const prevKey = authIdentityKey(state.boot);
      const wasLogged = isLogged();
      const prevState = authState();
      const prevOnline = authOnlineAvailable();
      const nextAuth = { ...(state.boot.auth || {}), ...(data || {}) };
      state.boot.auth = nextAuth;
      const changed = prevKey !== authIdentityKey(state.boot) || wasLogged !== isLogged();
      renderChrome();
      applyAuthFields(nextAuth);
      if (changed) {
        const identity = authIdentityKey(state.boot);
        if (state.renderedIdentity !== identity) {
          resetAccountScopedState(false);
          migrateLegacyAssetStorage();
          state.renderedIdentity = identity;
          renderPage();
          if (isLogged() && authOnlineAvailable()) ensureBag().catch(() => { });
        }
      } else if (prevState !== authState() || prevOnline !== authOnlineAvailable()) {
        if (state.page === 'settings') renderSettings();
        else if (state.page === 'room') renderRoom();
        else renderPage();
      }
      return;
    }
    if (evt === 'room') {
      if (!state.boot) return;
      state.boot.room = data;
      if (data?.connected || !data?.lastError) state.roomError = null;
      renderChrome();
      if (state.page !== 'room') return;
      // 房间推送可能成批到达；内容没变就不重建整页（人数多时 DOM 重建很贵）。
      const sig = roomSignature(data);
      if (sig !== state.roomSig) { state.roomSig = sig; renderRoom(); }
      return;
    }
    if (evt === 'room.error') {
      state.roomError = data || null;
      if (state.page === 'room') renderRoom();
      // 登录态异常已经在顶部条说明，不再叠一条“连接断开”的 toast。
      if (data?.message && !(isLogged() && !authOnlineAvailable())) toast(data.message, 'err');
      return;
    }
    if (evt === 'toast') { toast(data.text, data.kind); return; }
    // 自动领宝箱发生在后台，面板收到后同步背包与「最近获得」。
    if (evt === 'chest') {
      const id = Number(data?.asset_id) || 0;
      if (id) call('recent.ack', { ids: [id] }).catch(() => { });
      refreshAfterAcquire(id);
      return;
    }
    // 自动升阶合成发生在后台，面板收到后同步背包并刷新合成/图鉴页
    if (evt === 'synth.auto') {
      const id = Number(data?.last_asset_id) || 0;
      (async () => {
        if (id) markRecentObtained(id);
        await refreshAfterAcquire(id);
        if (state.page === 'synth') renderSynth(true);
        else if (state.page === 'atlas') renderAtlas(true);
      })().catch(() => { });
      return;
    }
    if (evt === 'clicks') {
      applyClicks(typeof data === 'object' && data !== null ? data : { available: data });
      return;
    }
  }

  function authIdentityKey(boot) {
    return `${boot?.settings?.serverRegion || 'cn'}|${boot?.auth?.cuid || ''}|${boot?.auth?.loggedIn ? 1 : 0}`;
  }
  function bootSettingsSignature(boot) {
    return JSON.stringify([boot?.settings || {}, boot?.themePresets || [], boot?.themeRotation || {}]);
  }
  function bootBagSignature(boot) {
    return JSON.stringify([boot?.appearance || {}, boot?.locks || [], boot?.auth?.cuid || '', boot?.settings?.serverRegion || '']);
  }
  function bootSynthSignature(boot) {
    return JSON.stringify([boot?.locks || [], boot?.settings?.synthCheckMode || '', boot?.settings?.anyItemSynthesisUnlocked || false, boot?.settings?.anyItemSynthesisEnabled || false, boot?.settings?.autoSynthUp || false]);
  }
  /** auth 推送只做字段级刷新：昵称、UID、点击数、宝箱时间；不重建当前页面。 */
  function applyAuthFields(auth) {
    if (!auth) return;
    document.querySelectorAll('[data-auth-nickname]').forEach(el => {
      const next = auth.loggedIn ? (auth.nickname || '漂泊者') : '—';
      if (el.textContent !== next) el.textContent = next;
    });
    document.querySelectorAll('[data-auth-uid]').forEach(el => {
      const next = auth.loggedIn ? String(auth.uid || '—') : '—';
      if (el.textContent !== next) el.textContent = next;
    });
    applyClicks({ available: auth.clicks, total: auth.clickTotal, consume: auth.clickConsume });
    updateChestStatus();
  }

  // 点击数每按一次键都会变：只改对应文字节点，不整页重绘（房间里人多时重绘代价很高）。
  function applyClicks(v) {
    if (!state.boot?.auth) return;
    if (v.available != null) state.boot.auth.clicks = v.available;
    if (v.total != null) state.boot.auth.clickTotal = v.total;
    if (v.consume != null) state.boot.auth.clickConsume = v.consume;
    const setText = (sel, val) => {
      if (val == null) return;
      document.querySelectorAll(sel).forEach(el => { if (el.textContent !== String(val)) el.textContent = val; });
    };
    setText('[data-click-avail]', v.available);
    setText('[data-click-total]', v.total);
    setText('[data-click-consume]', v.consume);
  }

  function renderChrome() {
    const auth = state.boot?.auth;
    // 左下角只显示昵称，不再暴露 UID；冲突/失效时统一显示未登录。
    const showLoggedOut = !auth?.loggedIn || auth?.state === 'conflict' || auth?.state === 'invalid';
    document.getElementById('userChip').textContent = showLoggedOut
      ? tr('common.notLoggedIn', '未登录')
      : (auth.nickname || tr('common.defaultNickname', '漂泊者'));
    document.querySelectorAll('.tab').forEach(btn => btn.classList.toggle('active', btn.dataset.page === state.page));
    document.querySelectorAll('.tab').forEach(btn => {
      const pageInfo = PAGES.find(p => p.key === btn.dataset.page);
      const label = btn.querySelector('span');
      if (pageInfo && label) label.textContent = pageTitleOf(pageInfo);
    });
    const page = PAGES.find(p => p.key === state.page);
    document.getElementById('pageTitle').textContent = pageTitleOf(page);
    updateSynthBadge();
    renderNotices();
  }

  // WebView2 里 html/body 也可能被 scrollIntoView 等程序化滚动带偏，导致标题栏滚出窗口。
  function resetDocumentScroll() {
    if (window.scrollY || document.documentElement.scrollTop || document.body.scrollTop) {
      window.scrollTo(0, 0);
      document.documentElement.scrollTop = 0;
      document.body.scrollTop = 0;
    }
  }

  function navigate(key) {
    state.page = key;
    resetDocumentScroll();
    renderChrome();
    renderPage();
  }

  function renderPage() {
    resetDocumentScroll();
    const host = document.getElementById('pageHost');
    // 自定义伙伴是纯本地装饰，不依赖登录/联机；未登录或暂停时也可管理。
    if (!isLogged() && state.page !== 'settings' && state.page !== 'companion') {
      host.innerHTML = renderLogin();
      bindLogin();
      return;
    }
    // 账号 HTTP 已暂停时不要触发背包/图鉴/合成等页面自动请求。
    // 房间页仍渲染，让用户看到“联机已暂停”和上次房间号；设置/伙伴页始终可进。
    if (authPaused() && state.page !== 'settings' && state.page !== 'room' && state.page !== 'companion') {
      renderAuthPaused();
      return;
    }
    if (state.page === 'bag') renderBag();
    else if (state.page === 'atlas') renderAtlas();
    else if (state.page === 'synth') renderSynth();
    else if (state.page === 'preset') renderPreset();
    else if (state.page === 'room') renderRoom();
    else if (state.page === 'companion') renderCompanion();
    else renderSettings();
  }

  // 异步页面渲染可能在登录态变化后才完成；这里统一兜底，防止旧页面覆盖登录/暂停页。
  function pageAuthBlocked(pageKey) {
    if (state.page !== pageKey) return true;
    if (pageKey === 'companion') return false;
    if (!isLogged() && state.page !== 'settings') {
      const host = document.getElementById('pageHost');
      if (host) { host.innerHTML = renderLogin(); bindLogin(); }
      return true;
    }
    if (authPaused()) { renderAuthPaused(); return true; }
    return false;
  }

  function authPausedTitle() {
    const s = authState();
    if (s === 'checking') return tr('auth.pausedChecking', '正在确认登录状态');
    if (s === 'conflict') return tr('auth.pausedConflict', '登录态正在其他客户端使用');
    if (s === 'network') return tr('auth.pausedNetwork', '网络不可用');
    return tr('auth.pausedAccount', '账号功能已暂停');
  }

  function renderAuthPaused() {
    const a = authInfo();
    const s = authState();
    const canReconnect = s === 'conflict';
    const actions = `
      ${s !== 'checking' ? `<button class="btn ghost" data-auth-recheck>${tr('auth.recheck', '重新检测')}</button>` : ''}
      ${canReconnect ? `<button class="btn" data-auth-reconnect>${tr('auth.reconnect', '重新连接')}</button>` : ''}
      <button class="btn ghost" data-auth-account>${tr('auth.viewAccount', '查看账号')}</button>`;
    document.getElementById('pageHost').innerHTML = `<div class="login-wrap"><div class="login-card auth-paused-card">
      <h2>${esc(authPausedTitle())}</h2>
      <div class="hint">${esc(a.message || a.stateMessage || tr('auth.pausedDefault', '为避免影响其他客户端，账号相关功能已暂停。'))}</div>
      <div class="hint muted">${tr('auth.pausedLocal', '桌宠、键盘、主题和本地设置不受影响。本程序不会自动重新登录或自动重连。')}</div>
      <div class="auth-paused-actions">${actions}</div>
    </div></div>`;
    bindAuthActions(document.getElementById('pageHost'));
  }

  async function runAuthRecheck(button) {
    if (button?.disabled) return;
    const old = button?.textContent;
    if (button) { button.disabled = true; button.textContent = tr('auth.rechecking', '检测中…'); }
    try {
      const boot = await call('auth.recheck');
      if (boot) handleEvent('boot', boot);
      toast(authInfo().credentialValid === true
        ? tr('auth.recheckValid', '登录凭据有效，账号功能仍暂停')
        : tr('auth.recheckDone', '登录态检测完成'));
    } catch (e) {
      toast(e.message || tr('auth.recheckFailed', '登录态检测失败'), 'err');
    } finally {
      if (button && document.body.contains(button)) { button.disabled = false; button.textContent = old; }
    }
  }

  function openAuthReconnectConfirm() {
    modal(`<div class="about">
      <div class="about-title">${tr('auth.reconnectTitle', '重新连接联机')}</div>
      <div class="about-note about-note--warn"><p>${tr('auth.reconnectBody1', '重新连接会重新建立联机会话，可能使正在使用同一登录态的其他客户端下线。')}</p></div>
      <div class="about-note"><p>${tr('auth.reconnectBody2', '只有确认要由本程序接管联机会话时才继续。桌宠和本地功能不会受影响。')}</p></div>
      <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px">
        <button class="btn ghost" id="authReconnectCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn danger" id="authReconnectGo">${tr('auth.reconnectConfirm', '仍要重新连接')}</button>
      </div>
    </div>`, host => {
      host.querySelector('#authReconnectCancel').onclick = closeModal;
      const go = host.querySelector('#authReconnectGo');
      go.onclick = async () => {
        go.disabled = true;
        go.textContent = tr('auth.reconnecting', '正在连接…');
        try {
          const boot = await call('auth.reconnect');
          closeModal();
          if (boot) handleEvent('boot', boot);
          toast(tr('auth.reconnected', '已重新连接联机'));
        } catch (e) {
          closeModal();
          toast(e.message || tr('auth.reconnectFailed', '重新连接失败'), 'err');
        }
      };
    });
  }

  function bindAuthActions(root) {
    root?.querySelectorAll('[data-auth-recheck]').forEach(el => {
      el.onclick = () => void runAuthRecheck(el);
    });
    root?.querySelectorAll('[data-auth-reconnect]').forEach(el => {
      el.onclick = () => openAuthReconnectConfirm();
    });
    root?.querySelectorAll('[data-auth-account]').forEach(el => {
      el.onclick = () => navigate('settings');
    });
    root?.querySelectorAll('[data-auth-login]').forEach(el => {
      el.onclick = () => navigate('bag');
    });
  }

  function setPage(html) { document.getElementById('pageHost').innerHTML = `<div class="page">${html}</div>`; }

  /* ---------------- 登录 ---------------- */
  function renderLogin() {
    const reg = state.boot?.settings?.serverRegion || 'cn';
    const isGlobal = reg === 'global';
    const mode = !isGlobal && state.loginMode === 'phone' ? 'phone' : 'uid';
    return `<div class="login-wrap"><div class="login-card">
      <h2>${tr('login.title', '登录 Wuwa Tappo')}</h2>
      <div style="display:flex;align-items:center;gap:10px;margin-bottom:12px">
        <span style="font-size:13px;color:var(--text-sub,#888);white-space:nowrap">${tr('login.region', '区服：')}</span>
        <select class="select" id="loginRegion" style="flex:1">
          <option value="cn" ${!isGlobal ? 'selected' : ''}>${tr('login.regionCn', '国服（CN）')}</option>
          <option value="global" ${isGlobal ? 'selected' : ''}>${tr('login.regionGlobal', '国际服（Global）')}</option>
        </select>
      </div>

      ${isGlobal ? `
        <div class="hint" style="color:var(--text-sub,#888);line-height:1.6;margin-bottom:14px;background:rgba(0,0,0,0.03);padding:10px 12px;border-radius:6px;border:1px solid var(--border,#e0d4c3)">
          <strong>${tr('login.globalHintTitle', '国际服提示：')}</strong>${tr('login.globalHint', '国际服请使用浏览器验证登录。邮箱 / X / Google 已验证可用，其他方式未验证需要自测。也可使用下方按钮继承本机已登录的国际服官方客户端数据。')}
        </div>

        <button class="btn" id="loginWeb" style="margin-bottom:10px;width:100%">${tr('login.web', '使用官方网页登录（支持 Google / X 等）')}</button>
        <div class="hint" style="color:var(--text-sub,#888);font-size:12px;line-height:1.6;margin-bottom:14px">
          ${tr('login.webHint', '网页登录会在本程序内打开官方登录页，支持 Google / X / Apple / Facebook / LINE 等官方登录方式。登录回调只在本进程内处理，不会注册或抢占官方客户端的 wuwa-buddy 协议。')}
        </div>
        <button class="btn ghost" id="loginInherit" style="margin-bottom:14px;width:100%">${tr('login.inherit', '继承官方程序登录状态')}</button>

        <div style="font-size:12px;color:var(--text-sub,#888);margin-bottom:6px">${tr('login.orToken', '或手动输入 Token 登录：')}</div>
        <div style="display:flex;gap:10px">
          <input class="input" id="loginTokenInput" placeholder="${tr('login.tokenPlaceholder', '输入 kuro_infoToken / infoToken')}" style="flex:1" />
          <button class="btn ghost" id="loginTokenBtn">${tr('login.tokenButton', 'Token 登录')}</button>
        </div>
      ` : `
        <div class="pill-row" style="margin-bottom:12px">
          <button type="button" class="pill ${mode === 'uid' ? 'active' : ''}" data-login-mode="uid">${tr('login.uidMode', 'UID 验证码')}</button>
          <button type="button" class="pill ${mode === 'phone' ? 'active' : ''}" data-login-mode="phone">${tr('login.phoneMode', '手机号登录')}</button>
        </div>
        ${mode === 'phone' ? `
          <div class="hint">${tr('login.phoneHint', '使用库洛通行证手机号登录；验证码由库洛发送，可能需要人机验证。')}</div>
          <input class="input" id="loginPhone" placeholder="${tr('login.phonePlaceholder', '手机号（11 位）')}" inputmode="numeric" maxlength="11" autocomplete="tel" />
          <div style="display:flex;gap:10px">
            <input class="input" id="loginPhoneCode" placeholder="${tr('login.phoneCodePlaceholder', '短信验证码')}" style="flex:1" inputmode="numeric" maxlength="6" autocomplete="one-time-code" />
            <button class="btn ghost" id="phoneSendBtn">${tr('login.sendCode', '发送验证码')}</button>
          </div>
          <button class="btn" id="phoneLoginBtn">${tr('login.phoneLogin', '手机号登录')}</button>
          <div class="hint">${tr('login.phoneSuccessHint', '登录成功后会同步该通行证绑定的鸣潮角色。')}</div>
        ` : `
          <div class="hint">${tr('login.uidHint', '使用游戏 UID 登录；如果无法完成验证码登录，请使用官方登录状态。')}</div>
          <input class="input" id="loginUid" placeholder="${tr('login.uidPlaceholder', '游戏 UID（9-10 位）')}" inputmode="numeric" maxlength="10" />
          <div style="display:flex;gap:10px">
            <input class="input" id="loginCode" placeholder="${tr('login.codePlaceholder', '6 位验证码')}" style="flex:1" inputmode="numeric" maxlength="6" autocomplete="one-time-code" />
            <button class="btn ghost" id="sendCodeBtn">${tr('login.sendCode', '发送验证码')}</button>
          </div>
          <button class="btn" id="loginBtn">${tr('login.loginButton', '登录')}</button>
        `}
        <div class="hint">${tr('login.inheritHint', '如果无法完成验证码登录，请先在官方客户端登录，再点击下方按钮继承官方登录状态。')}</div>
        <button class="btn ghost" id="loginInherit">${tr('login.inherit', '继承官方程序登录状态')}</button>

        <div style="margin-top:12px;border-top:1px dashed var(--border,#e0d4c3);padding-top:10px">
          <div style="display:flex;gap:10px">
            <input class="input" id="loginTokenInput" placeholder="${tr('login.manualTokenPlaceholder', '手动输入 Token 登录')}" style="flex:1" />
            <button class="btn ghost" id="loginTokenBtn">${tr('login.tokenButton', 'Token 登录')}</button>
          </div>
        </div>
      `}
    </div></div>`;
  }

  function bindLogin() {
    const send = document.getElementById('sendCodeBtn');
    const go = document.getElementById('loginBtn');
    const uidEl = document.getElementById('loginUid');
    const codeEl = document.getElementById('loginCode');
    const phoneEl = document.getElementById('loginPhone');
    const phoneCodeEl = document.getElementById('loginPhoneCode');
    const phoneSend = document.getElementById('phoneSendBtn');
    const phoneLogin = document.getElementById('phoneLoginBtn');
    const regEl = document.getElementById('loginRegion');
    const startCooldown = (btn, seconds, label) => {
      if (!btn) return;
      let left = seconds;
      btn.disabled = true;
      btn.textContent = left + 's';
      const timer = setInterval(() => {
        left--;
        if (left <= 0) {
          clearInterval(timer);
          btn.disabled = false;
          btn.textContent = label;
        } else {
          btn.textContent = left + 's';
        }
      }, 1000);
    };
    document.querySelectorAll('[data-login-mode]').forEach(btn => {
      btn.onclick = () => {
        state.loginMode = btn.dataset.loginMode === 'phone' ? 'phone' : 'uid';
        renderPage();
      };
    });
    if (regEl) {
      regEl.onchange = async () => {
        const val = regEl.value;
        if (val !== (state.boot?.settings?.serverRegion || 'cn')) {
          if (!state.boot) state.boot = {};
          if (!state.boot.settings) state.boot.settings = {};
          state.boot.settings.serverRegion = val;
          if (val === 'global') state.loginMode = 'uid';
          // 登录页切换区服是登录前的准备动作，不显示“已切换，请重新登录”的提示。
          const boot = await tryCall('settings.set', { serverRegion: val, announce: false });
          if (boot?.settings) state.boot = boot;
          renderPage();
        }
      };
    }
    // 原版只允许输入数字，这里保持一致，避免把非数字发给服务端。
    const digitsOnly = el => el && el.addEventListener('input', () => {
      const v = el.value.replace(/[^0-9]/g, '');
      if (el.value !== v) el.value = v;
    });
    digitsOnly(uidEl); digitsOnly(codeEl); digitsOnly(phoneEl); digitsOnly(phoneCodeEl);

    // UID 登录只接受 9-10 位游戏 UID。
    const uidValid = v => /^[0-9]{9,10}$/.test(v);
    if (send) {
      send.onclick = async () => {
        const uid = uidEl?.value.trim();
        if (!uid) return toast(tr('login.toastUidRequired', '请输入游戏 UID'), 'err');
        if (!uidValid(uid)) return toast(tr('login.toastUidInvalid', '请输入 9-10 位游戏 UID'), 'err');
        try { await tryCall('login.sendCode', { uid }); toast(tr('login.toastCodeSent', '验证码已发送')); } catch { }
      };
    }
    if (go) {
      go.onclick = async () => {
        const uid = uidEl?.value.trim();
        const code = codeEl?.value.trim();
        if (!uid) return toast(tr('login.toastUidRequired', '请输入游戏 UID'), 'err');
        if (!uidValid(uid)) return toast(tr('login.toastUidInvalid', '请输入 9-10 位游戏 UID'), 'err');
        if (!/^[0-9]{6}$/.test(code)) return toast(tr('login.toastCodeInvalid', '请输入 6 位数字验证码'), 'err');
        try {
          await tryCall('login.submit', { uid, code });
          toast(tr('login.toastSuccess', '登录成功'));
          state.boot = await call('boot');
          renderChrome();
          navigate('bag');
        } catch { }
      };
    }
    if (phoneSend) {
      phoneSend.onclick = async () => {
        const phone = phoneEl?.value.trim();
        if (!phone) return toast(tr('login.toastPhoneRequired', '请输入手机号'), 'err');
        if (!/^1[3-9]\d{9}$/.test(phone)) return toast(tr('login.toastPhoneInvalid', '请输入 11 位手机号'), 'err');
        try {
          await tryCall('login.phone.sendCode', { phone });
          toast(tr('login.toastSmsSent', '短信验证码已发送'));
          startCooldown(phoneSend, 60, '发送验证码');
        } catch { }
      };
    }
    if (phoneLogin) {
      phoneLogin.onclick = async () => {
        const phone = phoneEl?.value.trim();
        const code = phoneCodeEl?.value.trim();
        if (!phone) return toast(tr('login.toastPhoneRequired', '请输入手机号'), 'err');
        if (!/^1[3-9]\d{9}$/.test(phone)) return toast(tr('login.toastPhoneInvalid', '请输入 11 位手机号'), 'err');
        if (!/^[0-9]{4,6}$/.test(code)) return toast(tr('login.toastSmsInvalid', '请输入 4-6 位短信验证码'), 'err');
        try {
          await tryCall('login.phone.submit', { phone, code });
          toast(tr('login.toastSuccess', '登录成功'));
          state.boot = await call('boot');
          renderChrome();
          navigate('bag');
        } catch { }
      };
    }
    const tokenBtn = document.getElementById('loginTokenBtn');
    const tokenInput = document.getElementById('loginTokenInput');
    if (tokenBtn && tokenInput) {
      tokenBtn.onclick = async () => {
        const tok = tokenInput.value.trim();
        if (!tok) return toast(tr('login.toastTokenRequired', '请输入 Token'), 'err');
        try {
          await tryCall('login.token', { token: tok });
          toast(tr('login.toastSuccess', '登录成功'));
          state.boot = await call('boot');
          renderChrome();
          navigate('bag');
        } catch { }
      };
    }
    document.getElementById('loginWeb')?.addEventListener('click', async () => {
      try {
        await tryCall('login.foreign.start');
        toast(tr('login.toastSuccess', '登录成功'));
        state.boot = await call('boot');
        renderChrome();
        navigate('bag');
      } catch { }
    });
    document.getElementById('loginInherit')?.addEventListener('click', showInheritLogin);
  }

  /* ---------------- 背包 ---------------- */
  // 所有 bag.list 串行执行：左右宝箱几乎同时领取时，两个请求并发返回可能乱序；
  // 旧快照覆盖新快照会让「最近获得」只显示其中一个奖励。
  let bagLoadChain = Promise.resolve();
  function fetchBagItems(quiet = false) {
    const task = async () => {
      const r = quiet ? await call('bag.list') : await tryCall('bag.list');
      state.bag = r.items || [];
      syncFavoritesFromBag();
      updateSynthBadge();
      return state.bag;
    };
    const p = bagLoadChain.then(task, task);
    bagLoadChain = p.catch(() => { });
    return p;
  }
  /** 收藏状态以 bag.list 的 is_collect 为准；重启/重开面板后不再显示成未收藏。 */
  function syncFavoritesFromBag() {
    state.favors = new Set((state.bag || [])
      .filter(x => x && x.isCollect)
      .map(x => Number(x.id))
      .filter(id => Number.isFinite(id) && id > 0));
  }
  async function ensureBag(force = false) {
    if (state.bag && !force) return state.bag;
    return await fetchBagItems();
  }

  const BAG_TABS = [
    ['recent', () => tr('bag.tab.recent', '最近获得')],
    ['character_skin', () => tr('bag.tab.character', '角色')],
    ['accessory', () => tr('bag.tab.accessory', '配件')],
    ['emote', () => tr('bag.tab.emote', '表情')]
  ];
  const BAG_TAB_COUNT = k => k === 'recent' ? (state.bagRecentIds || []).length : 0;

  function assetMeta(item) {
    if (!item) return { role: '0', series: '' };
    if (Number(item.gameRoleId) > 0) return { role: String(item.gameRoleId), series: item.series || '' };
    const name = String(item.assetName || '').replace(/\.png$/i, '');
    let m;
    if ((item.type === 'character_skin' || !item.type) && (m = name.match(/^role_(\d+)(?:_(\d+))?/i))) {
      return { role: m[1], series: item.series || m[2] || '' };
    }
    if ((item.type === 'emote' || !item.type) && (m = name.match(/^emote_(\d+)_/i))) {
      return { role: m[1], series: '' };
    }
    if ((item.type === 'emote' || !item.type) && name.startsWith('emote_')) {
      const n = String(item.name || '');
      if (name.startsWith('emote_2000_') || /^(?:angry|effort|happy|idle|pain|stunned|surprised|threaten|triumph)$/i.test(name.slice(6))) {
        return { role: '2000', series: '' };
      }
    }
    return { role: '0', series: item.series || '' };
  }
  function roleLabelInfo(item) {
    const raw = String(item?.name || '').trim();
    const fallbackLabel = () => {
      const r = assetMeta(item).role;
      return (!r || r === '0')
        ? { label: tr('role.generic', '通用'), fallback: true }
        : { label: tr('role.prefix', '角色 ') + (item?.roleId || r), fallback: true };
    };
    if (!raw || /^#?\d+$/.test(raw) || /^role_\d+/i.test(raw) || /^emote_\d+/i.test(raw)) return fallbackLabel();
    const first = raw.split(/[・·]/)[0].trim();
    return first ? { label: first, fallback: false } : fallbackLabel();
  }
  function roleLabelOf(item) {
    return roleLabelInfo(item).label;
  }
  const RECENT_KEY = 'wn_recent_assets';
  const UNSEEN_KEY = 'wn_unseen_assets';
  const KNOWN_KEY = 'wn_known_assets';
  const ASSET_SCOPE_MIGRATION_KEY = 'wn_asset_scope_migrated_v1';
  const RECENT_MAX = 80;

  /**
   * 「最近获得 / 新物品红点 / 已认识」都按 区服 + 账号 隔离。
   * 旧版本使用设备级固定 key，切换账号或国服/国际服时会串数据。
   */
  function assetStorageScope() {
    const region = state.boot?.settings?.serverRegion === 'global' ? 'global' : 'cn';
    const cuid = String(state.boot?.auth?.cuid || '').trim();
    return `${region}:${cuid || (DEMO ? 'demo' : 'guest')}`;
  }
  function scopedStorageKey(base) { return `${base}::${assetStorageScope()}`; }
  function readStoredNumberArray(key) {
    try {
      const arr = JSON.parse(localStorage.getItem(key) || '[]');
      return Array.isArray(arr) ? arr.map(Number).filter(x => Number.isFinite(x) && x > 0) : [];
    } catch { return []; }
  }
  function writeStoredNumberArray(key, list) {
    try { localStorage.setItem(key, JSON.stringify([...new Set((list || []).map(Number).filter(x => Number.isFinite(x) && x > 0))])); } catch { }
  }

  /**
   * 旧设备级 key 只在第一次登录后迁移到当前账号作用域；旧 key 保留作为回滚备份，
   * 之后不再被任何账号读取，避免第二个账号继承第一份数据。
   */
  function migrateLegacyAssetStorage() {
    try {
      if (localStorage.getItem(ASSET_SCOPE_MIGRATION_KEY) === '1') return;
      if (!isLogged()) return;
      const scope = assetStorageScope();
      if (scope.endsWith(':guest')) return;
      let migrated = 0;
      for (const [base, legacyKey] of [[RECENT_KEY, 'wn_recent_assets'], [UNSEEN_KEY, 'wn_unseen_assets'], [KNOWN_KEY, 'wn_known_assets']]) {
        const target = scopedStorageKey(base);
        if (localStorage.getItem(target) != null) continue;
        const value = localStorage.getItem(legacyKey);
        if (value != null) { localStorage.setItem(target, value); migrated++; }
      }
      const recentTarget = scopedStorageKey(RECENT_KEY);
      if (localStorage.getItem(recentTarget) == null) {
        const legacyNew = localStorage.getItem('wn_new_assets');
        if (legacyNew != null) { localStorage.setItem(recentTarget, legacyNew); migrated++; }
      }
      localStorage.setItem(ASSET_SCOPE_MIGRATION_KEY, '1');
      if (migrated) console.info(`已迁移 ${migrated} 项旧设备级收集记录到 ${scope}`);
    } catch { }
  }

  /** 登录/切换账号或区服后清空内存里的账号数据，防止新账号看到旧账号的背包/收藏。 */
  function resetAccountScopedState(regionChanged = false) {
    state.bag = null;
    state.bagNewReady = false;
    state.bagRecentIds = [];
    state.bagNewIds = [];
    state.bagSelected = null;
    state.favors = new Set();
    state.atlas = {};
    state.atlasAllCount = 0;
    state.atlasSnapshot = null;
    state.atlasLimit = ATLAS_PAGE_SIZE;
    state.presets = null;
    state.emoteCatalog = null;
    state.roomError = null;
    state.roomSig = '';
    if (regionChanged) state.catalog = {};
  }

  /**
   * 「最近获得」是一个常驻的有序列表：新获得的物品逐个插到最前面，旧的一直往后排。
   * 与「已认识」集合分开保存，所以切换页签、重启都不会丢，也不会整页清空。
   */
  function loadRecent() {
    return readStoredNumberArray(scopedStorageKey(RECENT_KEY));
  }
  function saveRecent(list) {
    writeStoredNumberArray(scopedStorageKey(RECENT_KEY), (list || []).slice(0, RECENT_MAX));
  }
  function loadUnseen() {
    return readStoredNumberArray(scopedStorageKey(UNSEEN_KEY));
  }
  function saveUnseen(list) {
    writeStoredNumberArray(scopedStorageKey(UNSEEN_KEY), list);
  }
  function loadKnownState() {
    try {
      const raw = localStorage.getItem(scopedStorageKey(KNOWN_KEY));
      return { known: new Set(readStoredNumberArray(scopedStorageKey(KNOWN_KEY))), firstRun: raw == null };
    } catch { return { known: new Set(), firstRun: true }; }
  }
  function saveKnown(known) {
    writeStoredNumberArray(scopedStorageKey(KNOWN_KEY), [...known]);
  }
  /** 宝箱响应只有 id；先把它前插到最近获得并标记小红点，不等下一次背包扫描。 */
  function markRecentObtained(id) {
    id = Number(id);
    if (!Number.isFinite(id) || id <= 0) return;
    const recent = [id, ...loadRecent().filter(x => x !== id)];
    saveRecent(recent);
    const unseen = [id, ...loadUnseen().filter(x => x !== id)];
    saveUnseen(unseen);
    const owned = new Set((state.bag || []).map(x => x.id));
    state.bagRecentIds = recent.filter(x => owned.has(x));
    state.bagNewIds = unseen.filter(x => owned.has(x));
  }
  /**
   * 合并原生层在面板关闭期间记录的宝箱奖励。
   * 这些奖励没有 chest 事件，背包扫描又只能发现“从未拥有过”的物品，
   * 所以重复获得的旧物品必须由这里补进「最近获得」。
   */
  function mergePendingRecentChests(ids) {
    // 未登录时不要消费待合并奖励：先留在磁盘，登录后再按账号归入正确的作用域。
    if (!isLogged()) return;
    if (!Array.isArray(ids) || !ids.length) return;
    const valid = ids.map(Number).filter(id => Number.isFinite(id) && id > 0);
    if (!valid.length) return;
    // 多次获得同一个物品时 recent 仍按物品去重，但 ack 要按获得次数全部确认。
    for (const id of new Set(valid)) markRecentObtained(id);
    call('recent.ack', { ids: valid }).catch(() => { });
  }
  /** 点击/查看后清除该物品的小红点，直到它再次被重新获取。 */
  function markNewSeen(id) {
    const unseen = loadUnseen().filter(x => x !== id);
    saveUnseen(unseen);
    state.bagNewIds = (state.bagNewIds || []).filter(x => x !== id);
  }
  function refreshNewAssets() {
    try {
      const { known, firstRun } = loadKnownState();
      const ids = state.bag.map(x => x.id);
      // 首次运行不把整包物品都标成“最近获得”，只把已知集合建立起来。
      const fresh = firstRun ? [] : ids.filter(id => !known.has(id));
      let recent = loadRecent();
      if (fresh.length) {
        // 逐个前插：每发现一个新物品就放到第 1 位，已有的旧记录顺次后移。
        for (const id of fresh) {
          recent = [id, ...recent.filter(x => x !== id)];
        }
        saveRecent(recent);
      }
      // 小红点单独持久化，面板关闭/重开后仍然可见，直到用户点开该物品。
      const unseen = [...new Set([...fresh, ...loadUnseen()])];
      saveUnseen(unseen);
      // 背包里已不存在的（例如被消耗掉）不再显示，但保留记录以免误删历史。
      const owned = new Set(ids);
      state.bagRecentIds = recent.filter(id => owned.has(id));
      state.bagNewIds = unseen.filter(id => owned.has(id));
      saveKnown(new Set([...known, ...ids]));
    } catch { state.bagRecentIds = []; state.bagNewIds = []; }
  }
  /** 保留记录：只在用户主动清空时调用。 */
  function dismissNewAssets() {
    saveRecent([]);
    saveUnseen([]);
    state.bagRecentIds = [];
    state.bagNewIds = [];
  }

  async function renderBag() {
    if (pageAuthBlocked('bag')) return;
    setPage(`<div class="loading">${tr('bag.loading', '正在读取背包…')}</div>`);
    try { await ensureBag(); } catch { setPage(`<div class="empty-state">${tr('bag.loadFailed', '背包读取失败')}</div>`); return; }
    if (!state.bagNewReady) { refreshNewAssets(); state.bagNewReady = true; }

    const counts = { character_skin: 0, accessory: 0, emote: 0 };
    state.bag.forEach(x => { if (counts[x.type] != null) counts[x.type]++; });
    const tab = state.bagTab;
    const isChar = tab === 'character_skin';
    const showRole = tab === 'character_skin' || tab === 'emote';
    const equippedAccessoryId = Number(state.boot?.appearance?.accessoryAssetId || 0);

    const seriesOptions = [...new Set(state.bag.filter(x => x.type === 'character_skin' && x.series).map(x => x.series))].sort((a, b) => String(a).localeCompare(String(b), 'zh'));
    const roleMap = new Map();
    const roleFallback = new Map();
    state.bag.filter(x => x.type === tab).forEach(x => {
      const r = assetMeta(x).role;
      if (!r || r === '0') return;
      const info = roleLabelInfo(x);
      if (!roleMap.has(r) || (roleFallback.get(r) && !info.fallback)) {
        roleMap.set(r, info.label || r);
        roleFallback.set(r, info.fallback);
      }
    });
    const roleOptions = [...roleMap.entries()].sort((a, b) => String(a[1]).localeCompare(String(b[1]), 'zh'));

    setPage(`
      <div class="bag-layout">
        <div class="bag-main">
          <div class="seg-tabs">
            ${BAG_TABS.map(([k, label]) => `<button class="seg-tab ${tab === k ? 'active' : ''}" data-btab="${k}">${label()} ${k === 'recent' ? BAG_TAB_COUNT(k) : (counts[k] || 0)}</button>`).join('')}
          </div>
          <div class="filter-row">
            ${isChar ? `<select class="select" id="bagSeries"><option value="">${tr('bag.seriesPrefix', '系列：')}${tr('common.all', '全部')}</option>${seriesOptions.map(s => `<option value="${esc(s)}" ${state.bagSeries === s ? 'selected' : ''}>${tr('bag.seriesPrefix', '系列：')}${esc(s)}</option>`).join('')}</select>` : ''}
            ${showRole ? `<select class="select" id="bagRole"><option value="">${tr('bag.rolePrefix', '角色：')}${tr('common.all', '全部')}</option>${roleOptions.map(([id, label]) => `<option value="${id}" ${state.bagRole === id ? 'selected' : ''}>${tr('bag.rolePrefix', '角色：')}${esc(label)}</option>`).join('')}</select>` : ''}
            <select class="select" id="bagRarity">
              <option value="">${tr('bag.rarityPrefix', '稀有度：')}${tr('common.all', '全部')}</option>
              ${['epic', 'rare', 'uncommon', 'common'].map(r => `<option value="${r}" ${state.bagRarity === r ? 'selected' : ''}>${tr('bag.rarityPrefix', '稀有度：')}${rarityText(r)}</option>`).join('')}
            </select>
            <input class="input" id="bagSearch" placeholder="${tr('bag.searchPlaceholder', '搜索名称 / 系列')}" value="${esc(state.bagSearch)}" style="flex:1;min-width:160px" />
            <button class="btn ghost" id="bagRefresh">${tr('bag.refresh', '刷新')}</button>
            <button class="btn ghost" id="bagClearNew" ${state.bagNewIds.length ? '' : 'disabled'} title="${tr('bag.clearNewTitle', '只清除新物品红点，不清空最近获得记录')}">${tr('bag.clearNew', '清除红点')}${state.bagNewIds.length ? ' (' + state.bagNewIds.length + ')' : ''}</button>
            ${tab === 'recent' ? `<button class="btn ghost" id="bagClearRecent">${tr('bag.clearRecent', '清空记录')}</button>` : ''}
            <button class="btn" id="bagSync">${tr('bag.sync', '同步角色/配件')}</button>
            ${tab === 'accessory' ? `<button class="btn ghost" id="bagRemoveAccessory" ${equippedAccessoryId ? '' : 'disabled'} title="${tr('bag.removeAccessoryTitle', '从本地和云端摘除当前头饰')}">${tr('bag.removeAccessory', '摘下头饰')}</button>` : ''}
          </div>
          <div class="bag-groups" id="bagGroups"></div>
        </div>
        <aside class="detail-panel" id="bagDetail"></aside>
      </div>`);

    document.querySelectorAll('[data-btab]').forEach(b => b.onclick = () => {
      state.bagTab = b.dataset.btab;
      state.bagSelected = null;
      state.bagSeries = ''; state.bagRole = ''; state.bagRarity = '';
      state.bagHidden = new Set();
      renderBag();
    });
    document.getElementById('bagSeries')?.addEventListener('change', e => { state.bagSeries = e.target.value; renderBagList(); });
    document.getElementById('bagRole')?.addEventListener('change', e => { state.bagRole = e.target.value; renderBagList(); });
    document.getElementById('bagRarity').addEventListener('change', e => { state.bagRarity = e.target.value; renderBagList(); });
    document.getElementById('bagSearch').oninput = e => { state.bagSearch = e.target.value; renderBagList(); };
    document.getElementById('bagRefresh').onclick = async () => { await ensureBag(true); state.bagNewReady = false; renderBag(); };
    document.getElementById('bagClearNew')?.addEventListener('click', () => {
      saveUnseen([]);
      state.bagNewIds = [];
      renderBag();
      toast(tr('bag.clearNewDone', '已清除所有新物品红点'));
    });
    document.getElementById('bagClearRecent')?.addEventListener('click', () => {
      dismissNewAssets();
      renderBag();
      toast(tr('bag.clearRecentDone', '已清空最近获得记录'));
    });
    document.getElementById('bagSync').onclick = async () => {
      try {
        const r = await tryCall('appearance.sync');
        state.boot = await call('boot');
        renderChrome();
        renderBagDetail();
        const blocked = [];
        if (r?.characterBlocked) blocked.push(tr('type.character_skin', '角色'));
        if (r?.accessoryBlocked) blocked.push(tr('type.accessory', '头饰'));
        toast(blocked.length
          ? tr('bag.syncBlocked', '已同步；未拥有的{items}保留了云端内容', { items: blocked.join(tr('common.listSeparator', '、')) })
          : tr('bag.synced', '角色/配件已同步到云端（表情栏仅本地）'));
      } catch { }
    };
    document.getElementById('bagRemoveAccessory')?.addEventListener('click', async () => {
      try {
        await tryCall('appearance.set', { type: 'accessory', id: 0 });
        const r = await tryCall('appearance.sync');
        state.boot = await call('boot');
        renderChrome();
        renderBag();
        toast(r?.characterBlocked
          ? tr('bag.accessoryRemovedBlocked', '已摘下头饰；未拥有的角色保留了云端内容')
          : tr('bag.accessoryRemoved', '已摘下头饰并同步云端'));
      } catch { }
    });
    renderBagList();
    renderBagDetail();
    ensureBagPreviewAssets();
  }

  function bagListItems() {
    const q = String(state.bagSearch || '').trim().toLowerCase();
    let items;
    if (state.bagTab === 'recent') {
      const order = new Map((state.bagRecentIds || []).map((id, i) => [id, i]));
      items = state.bag.filter(x => order.has(x.id));
      if (state.bagRarity) items = items.filter(x => effRarity(x.rarity) === state.bagRarity);
      if (q) items = items.filter(x => (x.name || '').toLowerCase().includes(q) || (x.series || '').toLowerCase().includes(q));
      // 最近获得按“最新的在最前”排，不再按稀有度打散。
      return items.sort((a, b) => (order.get(a.id) ?? 9999) - (order.get(b.id) ?? 9999));
    }
    items = state.bag.filter(x => x.type === state.bagTab);
    // 系列/角色下拉以前只改了 state，没有参与过滤，所以看起来“没反应”。
    if (state.bagSeries) items = items.filter(x => String(x.series || '') === state.bagSeries);
    if (state.bagRole) items = items.filter(x => assetMeta(x).role === state.bagRole);
    if (state.bagRarity) items = items.filter(x => effRarity(x.rarity) === state.bagRarity);
    if (q) items = items.filter(x => (x.name || '').toLowerCase().includes(q) || (x.series || '').toLowerCase().includes(q));
    // 角色/配件/表情分类：收藏物品优先；其余排序键保持不变，同组内原有相对顺序不变。
    const favRank = x => state.favors.has(Number(x.id)) ? 0 : 1;
    return items.sort((a, b) => favRank(a) - favRank(b)
      || (RARITY_ORDER[effRarity(b.rarity)] || 0) - (RARITY_ORDER[effRarity(a.rarity)] || 0)
      || String(a.series).localeCompare(String(b.series), 'zh')
      || String(a.name).localeCompare(String(b.name), 'zh'));
  }

  function renderBagList() {
    const host = document.getElementById('bagGroups');
    if (!host) return;
    const items = bagListItems();
    if (!items.length) {
      host.innerHTML = `<div class="empty-state">${tr('bag.empty', '没有符合条件的收集物')}</div>`;
      return;
    }
    if (state.bagTab === 'recent') {
      host.innerHTML = `<div class="bag-grid">${items.map(x => cardHtml(x, state.bagSelected?.id === x.id, true)).join('')}</div>`;
      bindBagCards(host);
      return;
    }
    const favoriteItems = items.filter(x => state.favors.has(Number(x.id)));
    const normalItems = items.filter(x => !state.favors.has(Number(x.id)));
    const groups = new Map();
    if (favoriteItems.length) groups.set('favorites', favoriteItems);
    for (const it of normalItems) {
      const r = effRarity(it.rarity);
      if (!groups.has(r)) groups.set(r, []);
      groups.get(r).push(it);
    }
    const order = ['favorites', 'epic', 'rare', 'uncommon', 'common'];
    host.innerHTML = order.filter(r => groups.has(r)).map(r => {
      const list = groups.get(r);
      const hidden = state.bagHidden.has(r);
      const title = r === 'favorites' ? tr('bag.favorites', '收藏') : (rarityText(r) || r);
      return `<section class="bag-group">
        <div class="bag-group-head" data-bgroup="${r}"><span>${title}</span><span class="count">${list.length}</span><span class="chev">${hidden ? '▸' : '▾'}</span></div>
        ${hidden ? '' : `<div class="bag-grid">${list.map(x => cardHtml(x, state.bagSelected?.id === x.id, true)).join('')}</div>`}
      </section>`;
    }).join('');
    host.querySelectorAll('[data-bgroup]').forEach(el => el.onclick = () => {
      const r = el.dataset.bgroup;
      if (state.bagHidden.has(r)) state.bagHidden.delete(r); else state.bagHidden.add(r);
      renderBagList();
    });
    bindBagCards(host);
  }

  function bindBagCards(host) {
    host.querySelectorAll('.card').forEach(el => {
      const id = Number(el.dataset.id);
      el.onclick = e => {
        if (e.detail > 1) return; // 双击的第二次 click 不再反选，交给 dblclick 装备
        markNewSeen(id);
        // 再次单击当前已选物品：取消选择，右侧回到在线/本地形态预览。
        state.bagSelected = Number(state.bagSelected?.id) === id ? null : itemOrId(id);
        // 只切换选中样式，不重建卡片 DOM，避免双击的第二次点击落到新节点上导致 dblclick 丢失。
        const selectedId = Number(state.bagSelected?.id || 0);
        host.querySelectorAll('.card').forEach(c => c.classList.toggle('selected', Number(c.dataset.id) === selectedId));
        renderBagDetail();
      };
      // 默认双击 = 装备并同步到云端；仅本地显示仍可通过详情按钮选择。
      // 右上角锁定/收藏按钮的快速连点不能冒泡成卡片的双击装备。
      el.ondblclick = e => {
        if (e.target.closest('[data-lock-type], .fav')) return;
        if (performance.now() < (state.bagActionUntil || 0)) return;
        equip(itemOrId(id), true);
      };
    });
    host.querySelectorAll('[data-lock-type]').forEach(el => {
      el.onclick = async e => {
        e.stopPropagation();
        state.bagActionUntil = performance.now() + 500;
        await toggleAssetLock(el.dataset.lockType, Number(el.dataset.lockId));
      };
    });
    host.querySelectorAll('.fav').forEach(el => {
      el.onclick = async e => {
        e.stopPropagation();
        state.bagActionUntil = performance.now() + 500;
        const id = Number(el.dataset.id);
        const on = !state.favors.has(id);
        try {
          await tryCall('favorite', { id, on });
          on ? state.favors.add(id) : state.favors.delete(id);
          const item = (state.bag || []).find(x => Number(x.id) === id);
          if (item) item.isCollect = on;
          // 只局部重排列表，保留当前滚动位置，不重建整个背包页。
          const host = document.getElementById('bagGroups');
          const scroll = host?.scrollTop || 0;
          renderBagList();
          if (host) host.scrollTop = scroll;
        }
        catch { }
      };
    });
  }

  function itemOrId(id) { return state.bag.find(v => v.id === id) || null; }

  function cardHtml(item, selected, withFav) {
    const fav = state.favors.has(item.id) ? '★' : '☆';
    const isNew = (state.bagNewIds || []).includes(item.id);
    return `<div class="card ${selected ? 'selected' : ''}" data-id="${item.id}">
      ${isNew ? `<span class="new-dot" title="${tr('bag.newItem', '最近获得')}"></span>` : ''}
      ${withFav ? `<div class="fav" data-id="${item.id}" title="${tr('bag.favorites', '收藏')}">${fav}</div>` : ''}
      ${lockButtonHtml(item.type, item.id)}
      ${img(item.thumb, 'thumb', item.name)}
      <div class="rarity-bar ${rarityClass(item.rarity)}"></div>
      <div class="name" title="${esc(item.name)}">${esc(item.name)}</div>
      <div class="sub">${esc(item.series || typeText(item.type) || '')}</div>
      ${item.quantity > 1 ? `<div class="qty">×${item.quantity}</div>` : ''}
    </div>`;
  }

  function isAccessoryEquipped(id) {
    return Number(state.boot?.appearance?.accessoryAssetId || 0) === Number(id);
  }

  function emoteUploadStatus(item) {
    if (!item || item.type !== 'emote') return { canUpload: false, reason: tr('bag.reasonSelectEmote', '请选择表情') };
    const appearance = state.boot?.appearance || {};
    const charId = Number(appearance.characterAssetId || 0);
    const charOwned = (state.bag || []).some(x => x.type === 'character_skin' && Number(x.id) === charId);
    const currentRole = Number(appearance.characterRoleId || 0);
    const emoteRole = Number(assetMeta(item).role || 0);
    const roleMatch = emoteRole <= 0 || emoteRole === 2000 || (currentRole > 0 && emoteRole === currentRole);
    const owned = (state.bag || []).some(x => x.type === 'emote' && Number(x.id) === Number(item.id));
    const reason = !owned ? tr('bag.reasonNotOwned', '未拥有该表情')
      : !charOwned ? tr('bag.reasonCharNotOwned', '当前角色未拥有')
      : !roleMatch ? tr('bag.reasonRoleMismatch', '表情属于其他角色')
      : '';
    return { canUpload: owned && charOwned && roleMatch, reason, currentRole, emoteRole, roleMatch, charOwned, owned };
  }

  function renderBagEmotePanel() {
    const box = document.getElementById('bagDetail');
    if (!box) return;
    if (!state.emoteCatalog?.length) {
      ensureEmotes().then(() => {
        if (state.page === 'bag' && state.bagTab === 'emote') renderBagEmotePanel();
      }).catch(() => { });
    }
    const appearance = state.boot?.appearance || {};
    const slots = [...(appearance.emoteAssetIds || [])];
    while (slots.length < 5) slots.push(0);
    const cloudSlots = [...(appearance.cloudEmoteAssetIds || [])];
    while (cloudSlots.length < 5) cloudSlots.push(0);
    const selected = state.bagSelected?.type === 'emote' ? state.bagSelected : null;
    const status = emoteUploadStatus(selected);
    const selectedHtml = selected ? `
      <div class="detail-preview">${img(selected.thumb, '', selected.name)}</div>
      <div class="detail-name">${esc(selected.name)}</div>
      <div class="detail-row"><span>${tr('detail.type', '类别')}</span><span>${tr('type.emote', '表情')}</span></div>
      <div class="detail-row"><span>${tr('detail.rarity', '稀有度')}</span><span>${rarityText(effRarity(selected.rarity)) || selected.rarity}</span></div>
      <div class="detail-row"><span>${tr('detail.series', '系列')}</span><span>${esc(selected.series || '—')}</span></div>
      <div class="detail-row"><span>${tr('detail.cloudCondition', '云端条件')}</span><span>${status.canUpload ? tr('detail.uploadable', '可上传') : esc(status.reason)}</span></div>`
      : `<div class="detail-empty" style="margin:20px auto">${tr('bag.emoteHintSelect', '先在左侧选择一个表情')}<br><span style="font-size:12px">${tr('bag.emoteHintSlot', '再点击下方槽位选择「仅本地」或「上传云端」')}</span></div>`;
    box.innerHTML = `
      ${selectedHtml}
      <div class="section-title" style="margin:16px 0 8px;">${tr('bag.emoteSlots', '表情槽位')}</div>
      <div class="emote-row bag-emote-slots">
        ${slots.map((id, i) => {
          const item = emoteInfo(id);
          const mode = id ? (Number(cloudSlots[i]) === Number(id) ? tr('bag.slotCloud', '云') : tr('bag.slotLocal', '本')) : '';
          return `<div class="emote-slot" data-eslot="${i}" title="${tr('bag.slotLabel', '槽位 {n}', { n: i + 1 })}">
            ${item ? img(item.thumb) : `<span class="muted">${tr('common.empty', '空')}</span>`}
            <span class="slot-no">${i + 1}${mode ? ' · ' + mode : ''}</span>
          </div>`;
        }).join('')}
      </div>
      <div class="hint muted" style="font-size:12px;margin-top:10px">${tr('bag.emoteSlotHint', '点击槽位后选择仅本地或上传云端；只有表情已拥有、当前角色已拥有且角色匹配时才允许上传。')}</div>`;
    box.querySelectorAll('[data-eslot]').forEach(el => {
      el.onclick = () => openEmoteSlotAction(Number(el.dataset.eslot), slots);
    });
  }

  function openEmoteSlotAction(slot, slots) {
    const selected = state.bagSelected?.type === 'emote' ? state.bagSelected : null;
    const candidate = selected || (state.bag || []).find(x => x.type === 'emote' && Number(x.id) === Number(slots[slot]));
    if (!candidate) return toast(tr('bag.selectEmoteFirst', '请先从左侧选择一个表情'), 'err');
    const status = emoteUploadStatus(candidate);
    const current = slots[slot] || 0;
    modal(`
      <div class="reward">
        ${img(candidate.thumb, '', candidate.name)}
        <div class="r-name">${esc(candidate.name)}</div>
        <div class="r-sub">${tr('bag.slotLabel', '槽位 {n}', { n: slot + 1 })}${current ? ' · ' + tr('bag.slotCurrent', '当前已有表情') : ' · ' + tr('bag.slotEmpty', '空槽位')}</div>
        <div class="r-sub" style="color:${status.canUpload ? 'var(--ink-dim)' : 'var(--danger)'}">${status.canUpload ? tr('bag.slotCanUpload', '满足云端条件') : esc(status.reason)}</div>
        <div style="display:flex;gap:10px;margin-top:10px;flex-wrap:wrap;justify-content:center">
          <button class="btn ghost" id="slotLocal">${tr('bag.slotLocalOnly', '仅本地')}</button>
          <button class="btn" id="slotCloud" ${status.canUpload ? '' : 'disabled title="' + esc(status.reason) + '"'}>${tr('bag.slotUpload', '上传云端')}</button>
          <button class="btn ghost" id="slotClear" ${current ? '' : 'disabled'}>${tr('bag.slotClear', '清空')}</button>
          <button class="btn ghost" id="slotCancel">${tr('common.cancel', '取消')}</button>
        </div>
      </div>`, h => {
      h.querySelector('#slotCancel').onclick = closeModal;
      h.querySelector('#slotLocal').onclick = async () => {
        try {
          state.boot = await tryCall('appearance.setSlot', { slot, id: candidate.id });
          closeModal();
          renderBagDetail();
          toast(tr('bag.slotApplied', '槽位 {n} 已应用（仅本地）', { n: slot + 1 }));
        } catch { }
      };
      h.querySelector('#slotCloud').onclick = async () => {
        if (!status.canUpload) return;
        try {
          state.boot = await tryCall('appearance.setSlot', { slot, id: candidate.id, upload: true });
          closeModal();
          renderBagDetail();
          toast(tr('bag.slotUploaded', '槽位 {n} 已上传云端', { n: slot + 1 }));
        } catch { }
      };
      h.querySelector('#slotClear').onclick = async () => {
        try {
          state.boot = await tryCall('appearance.setSlot', { slot, id: 0 });
          closeModal();
          renderBagDetail();
          toast(tr('bag.slotCleared', '槽位 {n} 已清空（仅本地）', { n: slot + 1 }));
        } catch { }
      };
    });
  }

  /** 背包页首次进入时补加载目录，让在线/本地形态预览使用和预设页一致的高清图。 */
  function ensureBagPreviewAssets() {
    const types = ['character_skin', 'accessory', 'emote'];
    if (types.every(t => (state.catalog[t] || []).length)) return;
    Promise.all(types.map(t => ensureCatalog(t).catch(() => []))).then(() => {
      if (state.page === 'bag' && !state.bagSelected) renderBagDetail();
    });
  }

  function bagAppearanceCard(title, note, p, includeLocalOffset) {
    return `<div class="bag-appearance-card">
      <div class="bag-appearance-head"><span>${title}</span><span>${note}</span></div>
      ${presetPreviewHtml(p, includeLocalOffset)}
    </div>`;
  }

  /** 未选择物品时，右侧展示当前在线形态与本地形态；预览复用预设页的 140×140 舞台。 */
  function bagAppearancePreviewHtml() {
    const a = state.boot?.appearance || {};
    const local = {
      characterAssetId: a.characterAssetId || 0,
      accessoryAssetId: a.accessoryAssetId || 0,
      emoteAssetIds: a.emoteAssetIds || [],
    };
    const cloud = {
      characterAssetId: a.cloudCharacterAssetId || 0,
      accessoryAssetId: a.cloudAccessoryAssetId || 0,
      emoteAssetIds: a.cloudEmoteAssetIds || [],
    };
    const cloudReady = cloud.characterAssetId > 0 || cloud.accessoryAssetId > 0
      || (cloud.emoteAssetIds || []).some(id => Number(id) > 0);
    return `<div class="bag-appearance-panel">
      ${bagAppearanceCard(tr('bag.onlineAppearance', '在线形态'), cloudReady ? tr('bag.cloudConfig', '云端配置') : tr('bag.notSynced', '未同步'), cloud, false)}
      ${bagAppearanceCard(tr('bag.localAppearance', '本地形态'), tr('bag.localOnly', '仅本机显示'), local, true)}
      <div class="bag-appearance-hint">${tr('bag.clickHint', '单击选择物品；再次单击已选物品可取消选择<br>双击装备并同步到云端')}</div>
    </div>`;
  }

  function renderBagDetail() {
    const box = document.getElementById('bagDetail');
    if (!box) return;
    if (state.bagTab === 'emote') {
      renderBagEmotePanel();
      return;
    }
    const x = state.bagSelected;
    if (!x) {
      box.innerHTML = bagAppearancePreviewHtml();
      return;
    }
    const accessoryEquipped = x.type === 'accessory' && isAccessoryEquipped(x.id);
    box.innerHTML = `
      <div class="detail-preview">${img(x.thumb, '', x.name)}</div>
      <div class="detail-name">${esc(x.name)}</div>
      <div class="detail-row"><span>${tr('detail.type', '类别')}</span><span>${typeText(x.type) || x.type}</span></div>
      <div class="detail-row"><span>${tr('detail.rarity', '稀有度')}</span><span>${rarityText(effRarity(x.rarity)) || x.rarity}</span></div>
      <div class="detail-row"><span>${tr('detail.series', '系列')}</span><span>${esc(x.series || '—')}</span></div>
      <div class="detail-row"><span>${tr('bag.quantity', '数量')}</span><span>${x.quantity ?? 1}</span></div>
      <div class="spacer"></div>
      <button class="btn" id="equipSyncBtn">${accessoryEquipped ? tr('bag.unequipSync', '取消头饰并同步云端') : tr('bag.equipSync', '装备并同步云端')}</button>
      <button class="btn ghost" id="equipBtn">${accessoryEquipped ? tr('bag.unequipLocal', '取消头饰（仅本地）') : tr('bag.equipLocal', '装备（仅本地）')}</button>`;
    document.getElementById('equipBtn').onclick = () => equip(x, false);
    document.getElementById('equipSyncBtn').onclick = () => equip(x, true);
  }

  async function equip(item, sync = false) {
    if (!item) return;
    try {
      const unequipAccessory = item.type === 'accessory' && isAccessoryEquipped(item.id);
      let syncResult = null;
      if (item.type === 'emote' && sync) {
        // 表情不能通过 appearance.sync 上传，必须走带 upload 标记的槽位接口。
        const slots = [...(state.boot?.appearance?.emoteAssetIds || [])];
        let slot = slots.findIndex(id => Number(id) === Number(item.id));
        if (slot < 0) slot = slots.findIndex(id => !Number(id));
        if (slot < 0) {
          toast(tr('bag.emoteFull', '表情栏已满，请先在编辑器中调整槽位'), 'err');
          return;
        }
        await tryCall('appearance.setSlot', { slot, id: item.id, upload: true });
        syncResult = { synced: true };
      } else {
        await tryCall('appearance.set', { type: item.type, id: unequipAccessory ? 0 : item.id });
        if (sync) syncResult = await tryCall('appearance.sync');
      }
      const blocked = [];
      if (syncResult?.characterBlocked) blocked.push(tr('type.character_skin', '角色'));
      if (syncResult?.accessoryBlocked) blocked.push(tr('type.accessory', '头饰'));
      const base = unequipAccessory
        ? (sync ? tr('bag.unequippedCloud', '已取消头饰并同步云端') : tr('bag.unequippedLocal', '已取消头饰（本地显示）'))
        : (sync ? tr('bag.equippedCloud', '已装备并同步到云端') : tr('bag.equippedLocal', '已装备到本地显示'));
      toast(blocked.length
        ? tr('bag.updatedBlocked', '已更新；未拥有的{items}保留了云端内容', { items: blocked.join(tr('common.listSeparator', '、')) })
        : base);
      state.boot = await call('boot');
      renderChrome();
      if (state.page === 'bag') {
        // 双击后的两次 click 可能先把选择切掉了；装备完成时保持该物品选中，方便继续操作。
        state.bagSelected = itemOrId(item.id) || item;
        const selectedId = Number(item.id);
        document.querySelectorAll('#bagGroups .card').forEach(c => c.classList.toggle('selected', Number(c.dataset.id) === selectedId));
        renderBagDetail();
      }
    } catch { }
  }

  /* ---------------- 图鉴 ---------------- */
  async function ensureCatalog(type) {
    if (state.catalog[type]) return state.catalog[type];
    const r = await tryCall('catalog.list', { type });
    state.catalog[type] = r.items || [];
    return state.catalog[type];
  }

  async function ensureAtlas(type) {
    if (type === 'all') {
      const lists = await Promise.all(['character_skin', 'accessory', 'emote'].map(t => ensureAtlasOne(t).catch(() => [])));
      const all = lists.flat();
      if (!all.length) throw new Error('目录为空');
      state.atlasAllCount = all.length;
      return all;
    }
    return ensureAtlasOne(type);
  }

  async function ensureAtlasOne(type, retry = true) {
    if (state.atlas[type] && state.atlas[type].length) return state.atlas[type];
    try {
      const r = await tryCall('atlas.list', { type });
      const items = r.items || [];
      if (items.length) state.atlas[type] = items;
      return items;
    } catch (e) {
      if (!retry) throw e;
      await new Promise(res => setTimeout(res, 600));
      return ensureAtlasOne(type, false);
    }
  }

  const ATLAS_TABS = [
    ['all', () => tr('atlas.tab.all', '全部')],
    ['character_skin', () => tr('atlas.tab.character', '角色')],
    ['accessory', () => tr('atlas.tab.accessory', '配件')],
    ['emote', () => tr('atlas.tab.emote', '表情')]
  ];
  const ATLAS_PAGE_SIZE = 120;

  async function renderAtlas(preserveScroll = false) {
    if (pageAuthBlocked('atlas')) return;
    const prevScroll = preserveScroll ? (document.getElementById('atlasGroups')?.scrollTop || 0) : 0;
    const type = state.atlasType;
    setPage(`<div class="loading">${tr('atlas.loading', '正在读取图鉴…')}</div>`);
    let items;
    try { items = await ensureAtlas(type); }
    catch {
      setPage(`<div class="empty-state">${tr('atlas.loadFailed', '图鉴读取失败（网络波动）')}<br><button class="btn ghost" id="atlasRetry" style="margin-top:10px">${tr('common.retry', '重试')}</button></div>`);
      document.getElementById('atlasRetry').onclick = renderAtlas;
      return;
    }
    // 图鉴的“已拥有”状态来自背包；必须先等背包加载完，否则首屏会把全部条目判成未拥有。
    await ensureBag().catch(() => { });
    const ownedNames = new Set((state.bag || []).map(x => x.assetName));
    ensureEmotes().catch(() => { });
    const showSeries = type === 'character_skin';
    const showRole = type !== 'accessory';

    const seriesOptions = [...new Set(items.filter(x => x.series).map(x => x.series))].sort((a, b) => String(a).localeCompare(String(b), 'zh'));
      const roleMap = new Map();
      const roleFallback = new Map();
      if (showRole) {
        for (const x of items) {
          const r = assetMeta(x).role;
          if (!r || r === '0') continue;
          const info = roleLabelInfo(x);
          if (!roleMap.has(r) || (roleFallback.get(r) && !info.fallback)) {
            roleMap.set(r, info.label || r);
            roleFallback.set(r, info.fallback);
          }
        }
      }

    const roleOptions = [...roleMap.entries()].sort((a, b) => String(a[1]).localeCompare(String(b[1]), 'zh'));

    setPage(`
      <div class="seg-tabs">
        ${ATLAS_TABS.map(([k, label]) => {
          const count = k === 'all' ? (state.atlasAllCount || '') : (state.atlas[k]?.length ?? '');
          return `<button class="seg-tab ${type === k ? 'active' : ''}" data-atab="${k}">${label()} ${count}</button>`;
        }).join('')}
      </div>
      <div class="filter-row">
        ${showSeries ? `<select class="select" id="atlasSeries"><option value="">${tr('bag.seriesPrefix', '系列：')}${tr('common.all', '全部')}</option>${seriesOptions.map(s => `<option value="${esc(s)}" ${state.atlasSeries === s ? 'selected' : ''}>${tr('bag.seriesPrefix', '系列：')}${esc(s)}</option>`).join('')}</select>` : ''}
        ${showRole ? `<select class="select" id="atlasRole"><option value="">${tr('bag.rolePrefix', '角色：')}${tr('common.all', '全部')}</option>${roleOptions.map(([id, label]) => `<option value="${id}" ${state.atlasRole === id ? 'selected' : ''}>${tr('bag.rolePrefix', '角色：')}${esc(label)}</option>`).join('')}</select>` : ''}
        <select class="select" id="atlasRarity">
          <option value="">${tr('bag.rarityPrefix', '稀有度：')}${tr('common.all', '全部')}</option>
          ${['epic', 'rare', 'uncommon', 'common'].map(r => `<option value="${r}" ${state.atlasRarity === r ? 'selected' : ''}>${tr('bag.rarityPrefix', '稀有度：')}${rarityText(r)}</option>`).join('')}
        </select>
        <input class="input" id="atlasSearch" placeholder="${tr('atlas.searchPlaceholder', '搜索名称 / 系列 / assetName')}" value="${esc(state.atlasSearch)}" style="flex:1;min-width:180px" autocomplete="off" />
        <button class="pill ${state.atlasOwnedOnly ? 'active' : ''}" id="atlasOwnedOnly" title="${tr('atlas.ownedOnlyTitle', '只显示已经拥有的图鉴')}">${tr('atlas.ownedOnly', '仅拥有')}</button>
        <span class="spacer"></span>
        <span class="muted" id="atlasCount" style="font-size:13px">${tr('atlas.count', '共 {count} 件 · 已拥有会显示标记', { count: items.length })}</span>
        ${type === 'emote' ? `<button class="pill ${state.atlasEmoteSendMode ? 'active' : ''}" id="atlasEmoteSendMode" title="${tr('atlas.sendModeTitle', '开启后点击表情直接发送，不再弹出详情确认')}">${tr('atlas.sendMode', '发送模式')}</button>` : ''}
      </div>
      <div class="bag-groups" id="atlasGroups"></div>`);

    document.querySelectorAll('[data-atab]').forEach(b => b.onclick = () => {
      state.atlasType = b.dataset.atab;
      state.atlasSeries = ''; state.atlasRole = ''; state.atlasRarity = ''; state.atlasSearch = ''; state.atlasHidden = new Set();
      state.atlasLimit = ATLAS_PAGE_SIZE;
      renderAtlas();
    });
    const resetAtlasLimit = () => { state.atlasLimit = ATLAS_PAGE_SIZE; };
    document.getElementById('atlasSeries')?.addEventListener('change', e => { state.atlasSeries = e.target.value; resetAtlasLimit(); renderAtlasList(items, ownedNames); });
    document.getElementById('atlasRole')?.addEventListener('change', e => { state.atlasRole = e.target.value; resetAtlasLimit(); renderAtlasList(items, ownedNames); });
    document.getElementById('atlasRarity').addEventListener('change', e => { state.atlasRarity = e.target.value; resetAtlasLimit(); renderAtlasList(items, ownedNames); });
    const atlasSearchInput = document.getElementById('atlasSearch');
    let atlasSearchTimer = 0;
    atlasSearchInput?.addEventListener('input', e => {
      const value = e.target.value;
      clearTimeout(atlasSearchTimer);
      atlasSearchTimer = setTimeout(() => {
        state.atlasSearch = value;
        resetAtlasLimit();
        renderAtlasList(items, ownedNames);
      }, 250);
    });
    document.getElementById('atlasOwnedOnly')?.addEventListener('click', e => {
      state.atlasOwnedOnly = !state.atlasOwnedOnly;
      e.currentTarget.classList.toggle('active', state.atlasOwnedOnly);
      resetAtlasLimit();
      renderAtlasList(items, ownedNames);
    });
    document.getElementById('atlasEmoteSendMode')?.addEventListener('click', e => {
      state.atlasEmoteSendMode = !state.atlasEmoteSendMode;
      e.currentTarget.classList.toggle('active', state.atlasEmoteSendMode);
      toast(state.atlasEmoteSendMode ? tr('atlas.sendModeOn', '发送模式已开启：点击表情直接发送') : tr('atlas.sendModeOff', '发送模式已关闭'));
    });
    if (!state.atlasLimit) state.atlasLimit = ATLAS_PAGE_SIZE;
    state.atlasSnapshot = { items, ownedNames };
    renderAtlasList(items, ownedNames);
    if (preserveScroll) {
      const host = document.getElementById('atlasGroups');
      if (host) host.scrollTop = prevScroll;
    }
  }

  function atlasFilteredItems(items, ownedNames) {
    let list = items;
    if (state.atlasSeries) list = list.filter(x => x.series === state.atlasSeries);
    if (state.atlasRole) list = list.filter(x => assetMeta(x).role === state.atlasRole);
    if (state.atlasRarity) list = list.filter(x => effRarity(x.rarity) === state.atlasRarity);
    if (state.atlasOwnedOnly) list = list.filter(x => ownedNames.has(x.assetName));
    const q = String(state.atlasSearch || '').trim().toLowerCase();
    if (q) list = list.filter(x =>
      String(x.name || '').toLowerCase().includes(q)
      || String(x.series || '').toLowerCase().includes(q)
      || String(x.assetName || '').toLowerCase().includes(q));
    return list.sort((a, b) => (RARITY_ORDER[effRarity(b.rarity)] || 0) - (RARITY_ORDER[effRarity(a.rarity)] || 0)
      || String(a.series).localeCompare(String(b.series), 'zh')
      || String(a.name).localeCompare(String(b.name), 'zh'));
  }

  function atlasCard(x, owned) {
    return `<div class="card ${owned ? '' : 'unowned'}" data-key="${x.type}:${x.mainId}" title="${esc(x.name)}">
      ${owned ? lockButtonHtml(x.type, x.mainId) : ''}
      ${img(x.thumb, 'thumb', x.name)}
      <div class="rarity-bar ${rarityClass(x.rarity)}"></div>
      <div class="name">${esc(x.name)}</div>
      <div class="sub">${esc(x.series || '')}</div>
      <div class="atlas-badge ${owned ? 'owned' : 'locked'}">${owned ? tr('atlas.owned', '已拥有') : tr('atlas.unowned', '未拥有')}</div>
    </div>`;
  }

  function renderAtlasList(items, ownedNames) {
    const host = document.getElementById('atlasGroups');
    if (!host) return;
    const full = atlasFilteredItems(items, ownedNames);
    const paged = state.atlasType === 'all';
    if (!state.atlasLimit) state.atlasLimit = ATLAS_PAGE_SIZE;
    const list = paged ? full.slice(0, Math.max(ATLAS_PAGE_SIZE, state.atlasLimit)) : full;
    const count = document.getElementById('atlasCount');
    if (count) {
      if (state.atlasOwnedOnly) count.textContent = tr('atlas.countOwned', '仅拥有 {count} 件', { count: full.length }) + (paged ? ' · ' + tr('atlas.shown', '已显示 {count}', { count: list.length }) : '');
      else count.textContent = tr('atlas.countAll', '共 {count} 件', { count: full.length }) + (paged ? ' · ' + tr('atlas.shown', '已显示 {count}', { count: list.length }) : '') + tr('atlas.marked', ' · 已拥有会显示标记');
    }
    if (!full.length) { host.innerHTML = `<div class="empty-state">${tr('atlas.empty', '没有符合条件的图鉴条目')}</div>`; return; }
    const byKey = new Map(list.map(x => [x.type + ':' + x.mainId, x]));
    if (state.atlasType !== 'all') {
      host.innerHTML = `<div class="bag-grid">${list.map(x => atlasCard(x, ownedNames.has(x.assetName))).join('')}</div>`;
    } else {
      const groups = new Map();
      for (const it of list) {
        const r = effRarity(it.rarity);
        if (!groups.has(r)) groups.set(r, []);
        groups.get(r).push(it);
      }
      const order = ['epic', 'rare', 'uncommon', 'common'];
      host.innerHTML = order.filter(r => groups.has(r)).map(r => {
        const arr = groups.get(r);
        const owned = arr.filter(x => ownedNames.has(x.assetName)).length;
        const hidden = state.atlasHidden.has(r);
        return `<section class="bag-group">
          <div class="bag-group-head" data-agroup="${r}"><span>${rarityText(r)}</span><span class="count">（${owned}/${arr.length}）</span><span class="chev">${hidden ? '▸' : '▾'}</span></div>
          ${hidden ? '' : `<div class="bag-grid">${arr.map(x => atlasCard(x, ownedNames.has(x.assetName))).join('')}</div>`}
        </section>`;
      }).join('');
      host.querySelectorAll('[data-agroup]').forEach(el => el.onclick = () => {
        const r = el.dataset.agroup;
        if (state.atlasHidden.has(r)) state.atlasHidden.delete(r); else state.atlasHidden.add(r);
        renderAtlasList(items, ownedNames);
      });
    }
    if (paged && list.length < full.length) {
      host.insertAdjacentHTML('beforeend', `<div class="atlas-more"><button class="btn ghost" id="atlasMore">${tr('atlas.more', '加载更多（已显示 {shown} / {total}）', { shown: list.length, total: full.length })}</button></div>`);
      document.getElementById('atlasMore').onclick = () => {
        const scroll = host.scrollTop;
        state.atlasLimit = Math.max(ATLAS_PAGE_SIZE, state.atlasLimit) + ATLAS_PAGE_SIZE;
        renderAtlasList(items, ownedNames);
        host.scrollTop = scroll;
      };
    }
    host.querySelectorAll('[data-lock-type]').forEach(el => {
      el.onclick = async e => { e.stopPropagation(); await toggleAssetLock(el.dataset.lockType, Number(el.dataset.lockId)); };
    });
    host.querySelectorAll('.card').forEach(el => {
      el.onclick = async () => {
        const x = byKey.get(el.dataset.key);
        if (!x) return;
        if (x.type === 'emote' && state.atlasEmoteSendMode) {
          try { await tryCall('emote.send', { id: x.mainId }); toast(tr('atlas.sent', '已发送表情')); } catch { }
          return;
        }
        const bagItem = (state.bag || []).find(v => v.assetName === x.assetName);
        const accessoryEquipped = x.type === 'accessory' && isAccessoryEquipped(x.mainId);
        const emoteStatus = x.type === 'emote' && bagItem ? emoteUploadStatus(bagItem) : null;
        const currentSlots = [...(state.boot?.appearance?.emoteAssetIds || [])];
        while (currentSlots.length < 5) currentSlots.push(0);
        let pickSlot = currentSlots.findIndex(v => !v);
        if (pickSlot < 0) pickSlot = 0;
        const slotPicker = x.type === 'emote'
          ? `<div class="muted" style="font-size:12px;margin-top:4px">${tr('atlas.slotPick', '选择要替换的表情槽位')}</div>
             <div class="emote-row" style="justify-content:center;margin-top:4px">
               ${currentSlots.map((id, i) => {
                 const it = emoteInfo(id);
                 return `<div class="emote-slot ${i === pickSlot ? 'selected' : ''}" data-aslot="${i}" title="${tr('bag.slotLabel', '槽位 {n}', { n: i + 1 })}">${it ? img(it.thumb) : `<span class="muted">${tr('common.empty', '空')}</span>`}<span class="slot-no">${i + 1}</span></div>`;
               }).join('')}
             </div>` : '';
        modal(`
          <div class="reward">
            ${img(x.thumb, '', x.name)}
            <div class="r-name">${esc(x.name)}</div>
            <div class="r-sub">${typeText(x.type) || x.type} · ${rarityText(effRarity(x.rarity)) || x.rarity} ${x.series ? '· ' + esc(x.series) : ''}</div>
            <div class="r-sub">${bagItem ? tr('atlas.ownedHint', '已拥有 · 可应用到本地，或装备并同步云端') : tr('atlas.unownedHint', '尚未拥有 · 可应用到本地（仅自己可见）')}</div>
            ${emoteStatus && !emoteStatus.canUpload ? `<div class="r-sub" style="color:var(--danger)">${tr('atlas.cannotUpload', '不能上传云端：{reason}', { reason: esc(emoteStatus.reason) })}</div>` : ''}
            ${slotPicker}
            <div style="display:flex;gap:10px;margin-top:8px;flex-wrap:wrap">
              <button class="btn ghost" id="mClose">${tr('common.close', '关闭')}</button>
              <button class="btn" id="mLocal">${accessoryEquipped ? tr('bag.unequipLocal', '取消头饰（本地）') : tr('atlas.applyLocal', '应用到本地')}</button>
              ${bagItem && x.type !== 'emote' ? `<button class="btn ghost" id="mEquip">${accessoryEquipped ? tr('bag.unequipSync', '取消头饰并同步云端') : tr('bag.equipSync', '装备并同步云端')}</button>` : ''}
              ${emoteStatus?.canUpload ? `<button class="btn ghost" id="mEquipEmote">${tr('atlas.equipOnline', '装备到线上')}</button>` : ''}
            </div>
          </div>`, h => {
          h.querySelector('#mClose').onclick = closeModal;
          h.querySelectorAll('[data-aslot]').forEach(el => el.onclick = () => {
            pickSlot = Number(el.dataset.aslot);
            h.querySelectorAll('[data-aslot]').forEach(e => e.classList.toggle('selected', e === el));
          });
          h.querySelector('#mLocal').onclick = async () => {
            try {
              if (accessoryEquipped && bagItem) {
                await equip(bagItem, false);
                closeModal();
                return;
              }
              const data = { type: x.type, id: x.mainId };
              if (x.type === 'emote') data.slot = pickSlot;
              state.boot = await tryCall('appearance.applyLocal', data);
              toast(x.type === 'emote'
                ? tr('atlas.emoteSlotApplied', '已替换本地表情槽位 {n}', { n: pickSlot + 1 })
                : tr('atlas.localApplied', '已应用到本地（不上传服务端）'));
              closeModal();
              renderChrome();
              if (state.page === 'room') renderRoom();
            } catch { }
          };
          const eq = h.querySelector('#mEquip');
          if (eq) eq.onclick = async () => { await equip(bagItem, true); closeModal(); };
          const eqEmote = h.querySelector('#mEquipEmote');
          if (eqEmote) eqEmote.onclick = async () => {
            try {
              state.boot = await tryCall('appearance.setSlot', { slot: pickSlot, id: x.mainId, upload: true });
              closeModal();
              renderChrome();
              toast(tr('atlas.emoteEquipped', '表情已装备到线上（槽位 {n}）', { n: pickSlot + 1 }));
            } catch { }
          };
        });
      };
    });
  }

  /* ---------------- 合成 ---------------- */
  function synthNeed() { return state.synthMode === 'same' ? 5 : 10; }
  function synthAnyMode() { return !!(state.synthUnlocked && state.synthEnabled); }
  function synthResetSlots() { state.synthSlots = Array(synthNeed()).fill(null); }
  function synthSlotCount(id) { return state.synthSlots.filter(x => x === id).length; }
  function synthBagById(id) { return (state.bag || []).find(x => x.id === id) || null; }
  function synthMaxRarity(type) {
    const order = { common: 1, uncommon: 2, rare: 3, epic: 4 };
    const list = state.atlas[type];
    if (!list || !list.length) return 'epic';
    let max = 'common';
    for (const it of list) if ((order[effRarity(it.rarity)] || 0) > (order[max] || 0)) max = effRarity(it.rarity);
    return max;
  }
  /** 未扣除已放入槽位的可用数量；锁只保留 1 份，普通模式永远保留 1 份。 */
  function synthBaseUsable(item) {
    const quantity = Math.max(0, Number(item.quantity) || 1);
    const locked = isAssetLocked(item.type, item.id);
    const reserved = (!synthAnyMode() || locked) ? 1 : 0;
    return Math.max(0, quantity - reserved);
  }
  function synthUsable(item) { return Math.max(0, synthBaseUsable(item) - synthSlotCount(item.id)); }
  function synthEligible(type) {
    const list = (state.bag || []).filter(x => ['character_skin', 'accessory', 'emote'].includes(x.type));
    if (synthAnyMode()) return type ? list.filter(x => x.type === type) : list;
    const maxR = synthMaxRarity(type);
    return list.filter(x => x.type === type)
      .filter(x => state.synthMode === 'same' || effRarity(x.rarity) !== maxR);
  }
  function synthAnchor() { const id = state.synthSlots.find(x => x !== null); return id ? synthBagById(id) : null; }

  function synthMaxRarityForCheck(type) {
    if ((state.atlas?.[type] || []).length) return synthMaxRarity(type);
    const order = { common: 1, uncommon: 2, rare: 3, epic: 4 };
    let max = 'common';
    for (const item of state.bag || []) {
      if (item.type !== type) continue;
      if ((order[effRarity(item.rarity)] || 0) > (order[max] || 0)) max = effRarity(item.rarity);
    }
    return max;
  }

  /** 按当前检查偏好判断是否有任何一组材料足够合成；不看当前页面筛选。 */
  function synthCanCraftAny() {
    const mode = state.boot?.settings?.synthCheckMode || 'up';
    if (mode === 'off' || !state.bag?.length) return false;
    const need = mode === 'same' ? 5 : 10;
    const groups = new Map();
    for (const item of state.bag) {
      if (!['character_skin', 'accessory', 'emote'].includes(item.type)) continue;
      // 合成提醒只统计正常规则下可合成的数量；高级合成额外允许的最后一份不参与计算。
      const usable = Math.max(0, (Number(item.quantity) || 1) - 1);
      if (usable <= 0) continue;
      const rarity = effRarity(item.rarity);
      if (mode === 'up' && rarity === synthMaxRarityForCheck(item.type)) continue;
      const key = item.type + '|' + rarity;
      groups.set(key, (groups.get(key) || 0) + usable);
    }
    return [...groups.values()].some(count => count >= need);
  }

  function updateSynthBadge() {
    const ready = synthCanCraftAny();
    state.synthCanCraft = ready;
    const badge = document.querySelector('[data-tab-badge="synth"]');
    if (badge) badge.classList.toggle('show', ready);
    return ready;
  }

  async function renderSynth(preserveScroll = false) {
    if (pageAuthBlocked('synth')) return;
    // 点击材料卡片会重建整个合成页；这里保留网格滚动位置，避免选择下方物品时弹回顶部。
    const prevScroll = preserveScroll ? (document.getElementById('synthGrid')?.scrollTop || 0) : 0;
    setPage(`<div class="loading">${tr('synth.loading', '正在读取背包…')}</div>`);
    try { await ensureBag(); } catch { setPage(`<div class="empty-state">${tr('synth.bagFailed', '背包读取失败')}</div>`); return; }
    await ensureAtlas(state.synthType).catch(() => { });
    if (state.synthSlots.length !== synthNeed()) synthResetSlots();

    const need = synthNeed(), type = state.synthType;
    // 清理已不合法/超量的槽位
    const eligibleAll = synthEligible(type);
    const anchorForCleanup = synthAnchor();
    const cleanupAll = synthAnyMode()
      ? synthEligible('').filter(x => !anchorForCleanup || (x.type === anchorForCleanup.type && effRarity(x.rarity) === effRarity(anchorForCleanup.rarity)))
      : eligibleAll;
    const kept = state.synthSlots.map((id, i) => {
      if (id == null) return null;
      const item = cleanupAll.find(x => x.id === id);
      if (!item) return null;
      const before = state.synthSlots.slice(0, i).filter(x => x === id).length;
      return before < synthBaseUsable(item) ? id : null;
    });
    state.synthSlots = kept.filter(x => x !== null).concat(Array(need - kept.filter(x => x !== null).length).fill(null));

    const rarity = state.synthRarity;
    const items = eligibleAll
      .filter(x => rarity === 'all' || effRarity(x.rarity) === rarity)
      .filter(x => synthUsable(x) > 0 || synthSlotCount(x.id) > 0)
      .sort((a, b) => (RARITY_ORDER[effRarity(b.rarity)] || 0) - (RARITY_ORDER[effRarity(a.rarity)] || 0)
        || String(a.series).localeCompare(String(b.series), 'zh')
        || String(a.name).localeCompare(String(b.name), 'zh'));

    const filled = state.synthSlots.filter(x => x !== null).length;
    const anchor = synthAnchor();
    const checkMode = state.boot?.settings?.synthCheckMode || 'up';
    const slotHtml = state.synthSlots.map((id, i) => {
      const item = id ? synthBagById(id) : null;
      return `<div class="synth-slot ${item ? 'filled' : ''}" data-slot="${i}" title="${item ? esc(item.name) : tr('synth.slotEmpty', '空槽位')}">
        ${item ? img(item.thumb) : ''}<span class="idx">${i + 1}</span></div>`;
    }).join('');
    const advancedReminderHtml = synthAnyMode() ? `<div class="synth-danger-note">${tr('synth.advancedReminder', '高级合成已开启，使用完毕务必及时关闭。')}</div>` : '';
    const advancedPanelHtml = state.synthPanelVisible ? `
      <div class="synth-advanced-panel">
        <div class="synth-advanced-head">
          <span>${tr('synth.advanced', '高级合成')} <span class="synth-advanced-state ${state.synthEnabled ? 'on' : ''}">${state.synthEnabled ? tr('synth.enabled', '已启用') : tr('synth.disabled', '已关闭')}</span></span>
          <label class="synth-advanced-toggle"><input type="checkbox" id="synthEnabled" ${state.synthEnabled ? 'checked' : ''}> ${tr('synth.anyItem', '任意物品')}</label>
          <span class="synth-advanced-meta">${tr('synth.lockedCount', '锁定 {count} 件', { count: state.locks.size })}</span>
          <button class="btn ghost" id="synthHide">${tr('synth.hide', '隐藏')}</button>
        </div>
        <div class="synth-advanced-note">${tr('synth.advancedNote', '锁定物资在高级合成中只保证保留最后 1 件，不会被消耗到 0。锁定记录单独保存在 locks.json；每次合成前会二次确认。重新打开操作面板后会自动关闭，需要重新输入隐藏指令。')}</div>
        <div class="synth-advanced-actions"><button class="btn" id="synthSave">${tr('synth.saveSettings', '保存设置')}</button></div>
      </div>` : '';

    setPage(`
      <div class="synth-top">
        <div class="synth-bar">
          <div class="synth-group">
            <span class="synth-label">${tr('synth.mode', '方式')}</span>
            ${[['up', tr('synth.up', '升阶合成（10）')], ['same', tr('synth.same', '平阶合成（5）')]].map(([k, label]) =>
              `<button class="pill ${state.synthMode === k ? 'active' : ''}" data-sm="${k}">${label}</button>`).join('')}
          </div>
          <div class="synth-group">
            <span class="synth-label">${tr('synth.type', '类型')}</span>
            ${['character_skin', 'accessory', 'emote'].map(t =>
              `<button class="pill ${type === t ? 'active' : ''}" data-st="${t}">${typeText(t)}</button>`).join('')}
          </div>
          <div class="synth-group synth-group-rarity">
            <span class="synth-label">${tr('detail.rarity', '稀有度')}</span>
            ${['all', 'common', 'uncommon', 'rare', 'epic'].map(r =>
              `<button class="pill ${rarity === r ? 'active' : ''}" data-sr="${r}">${r === 'all' ? tr('common.all', '全部') : rarityText(r)}</button>`).join('')}
          </div>
        </div>
        <div class="synth-check-card">
          <div class="setting-title">${tr('synth.settings', '合成设置')}</div>
          <div class="setting-sub">${tr('synth.settingsSub', '获得新物品后检查或自动合成')}</div>
          <div class="pill-row" style="margin-top:8px">
            <button class="pill ${checkMode === 'up' ? 'active' : ''}" data-scm="up">${tr('synth.upShort', '升阶')}</button>
            <button class="pill ${checkMode === 'same' ? 'active' : ''}" data-scm="same">${tr('synth.sameShort', '平阶')}</button>
            <button class="pill ${checkMode === 'off' ? 'active' : ''}" data-scm="off">${tr('synth.off', '关闭提醒')}</button>
          </div>
          <div style="margin-top:10px;padding-top:8px;border-top:1px dashed var(--border);display:flex;align-items:center;justify-content:space-between">
            <div>
              <div class="setting-title" style="font-size:12px">${tr('synth.auto', '自动升阶合成')}</div>
              <div class="setting-sub" style="font-size:10px;margin-top:2px">${tr('synth.autoSub', '满10件同类型同稀有度多余材料自动升阶')}</div>
            </div>
            <div class="switch ${state.boot?.settings?.autoSynthUp ? 'on' : ''}" id="synthAutoSwitch"></div>
          </div>
        </div>
      </div>
      <div class="synth-slots">${slotHtml}</div>
      <div class="card-grid" id="synthGrid"></div>
      <div class="synth-foot">
        <span class="synth-count">${tr('synth.selected', '已选 {filled} / {need}', { filled, need })}${anchor ? ' · ' + esc(anchor.name) : ''}</span>
        <span class="spacer"></span>
        <button class="btn ghost" id="synthClear">${tr('synth.clear', '清空')}</button>
        <button class="btn ghost" id="synthOne" ${synthAnyMode() ? `disabled title="${tr('synth.advancedForbidden', '高级合成禁止一键放置')}"` : ''}>${tr('synth.autoSelect', '一键选择')}</button>
        <button class="btn ghost" id="synthAutoAll" ${synthAnyMode() ? `disabled title="${tr('synth.advancedManual', '高级合成模式下请手动选择材料')}"` : ''}>${tr('synth.autoAll', '一键全部升阶')}</button>
        <button class="btn" id="synthSubmit" ${filled === need ? '' : 'disabled'}>${tr('synth.submit', '合成')}</button>
      </div>
      ${advancedPanelHtml}
      ${advancedReminderHtml}`);

    document.querySelectorAll('[data-sm]').forEach(b => b.onclick = () => {
      state.synthMode = b.dataset.sm; synthResetSlots(); renderSynth();
    });
    document.querySelectorAll('[data-st]').forEach(b => b.onclick = () => {
      state.synthType = b.dataset.st; synthResetSlots(); renderSynth();
    });
    document.querySelectorAll('[data-sr]').forEach(b => b.onclick = () => {
      state.synthRarity = b.dataset.sr; synthResetSlots(); renderSynth();
    });
    document.querySelectorAll('[data-scm]').forEach(b => b.onclick = async () => {
      const mode = b.dataset.scm;
      try {
        const boot = await tryCall('settings.set', { synthCheckMode: mode });
        if (boot?.settings) state.boot = boot;
        updateSynthBadge();
        renderSynth();
        toast(mode === 'off' ? tr('synth.reminderOff', '已关闭合成提醒') : (mode === 'same' ? tr('synth.reminderSame', '合成提醒改为平阶检查') : tr('synth.reminderUp', '合成提醒改为升阶检查')));
      } catch { }
    });
    document.querySelectorAll('[data-slot]').forEach(el => el.onclick = () => {
      const idx = Number(el.dataset.slot);
      const next = [...state.synthSlots];
      next[idx] = null;
      state.synthSlots = next.filter(x => x !== null).concat(Array(need - next.filter(x => x !== null).length).fill(null));
      renderSynth();
    });
    document.getElementById('synthClear').onclick = () => { synthResetSlots(); renderSynth(); };
    document.getElementById('synthOne').onclick = () => {
      synthOneClick().catch(e => toast(e.message || tr('synth.autoSelectFailed', '一键选择失败'), 'err'));
    };
    document.getElementById('synthSubmit').onclick = doSynth;
    const autoSwitch = document.getElementById('synthAutoSwitch');
    if (autoSwitch) {
      autoSwitch.onclick = async () => {
        const next = !autoSwitch.classList.contains('on');
        autoSwitch.classList.toggle('on', next);
        try {
          const boot = await tryCall('settings.set', { autoSynthUp: next });
          if (boot?.settings) state.boot = boot;
          toast(next ? tr('synth.autoOn', '已开启自动升阶合成') : tr('synth.autoOff', '已关闭自动升阶合成'));
          renderSynth(true);
        } catch {
          autoSwitch.classList.toggle('on', !next);
        }
      };
    }
    const autoAllBtn = document.getElementById('synthAutoAll');
    if (autoAllBtn) {
      autoAllBtn.onclick = async () => {
        if (synthAnyMode()) return toast(tr('synth.advancedManual', '高级合成模式下请手动选择材料'), 'err');
        if (state.synthBusy) return;
        try {
          state.synthBusy = true;
          toast(tr('synth.autoChecking', '正在检查并执行自动升阶合成…'));
          const res = await tryCall('synth.auto');
          if (res?.busy) {
            toast(tr('synth.busy', '正在进行其他合成操作，请稍候'), 'err');
            return;
          }
          const count = Number(res?.count) || 0;
          const processed = Number(res?.processed) || 0;
          if (count > 0 || processed > 0) {
            if (res?.success === false)
              toast(tr('synth.autoPartial', '自动升阶部分完成（已完成 {count} 次，请刷新背包确认）', { count }), 'err');
            else
              toast(tr('synth.autoDone', '自动升阶合成完成：共完成 {count} 次合成！', { count }), 'ok');
            synthResetSlots();
            await fetchBagItems(true);
            if (state.page === 'synth') renderSynth();
            else if (state.page === 'atlas') renderAtlas(true);
          } else {
            toast(res?.message || tr('synth.autoNone', '当前没有满足升阶条件的材料（需要 10 件同类型同稀有度多余材料）'));
          }
        } catch (e) {
          toast(tr('synth.autoFailed', '自动升阶合成失败：{error}', { error: e.message || e }), 'err');
        } finally {
          state.synthBusy = false;
        }
      };
    }
    if (state.synthPanelVisible) {
      document.getElementById('synthHide').onclick = () => {
        state.synthPanelVisible = false;
        renderSynth();
      };
      document.getElementById('synthSave').onclick = () => {
        const enabled = !!document.getElementById('synthEnabled')?.checked;
        const save = async () => {
          try {
            const boot = await tryCall('settings.set', { anyItemSynthesisEnabled: enabled });
            state.boot = boot;
            applySynthSettings(boot);
            state.synthEnabled = enabled;
            if (!enabled) synthResetSlots();
            renderChrome();
            renderSynth();
            toast(enabled ? tr('synth.advancedOn', '高级合成已开启') : tr('synth.advancedOff', '高级合成已关闭'));
          } catch { renderSynth(); }
        };
        // 每次从关闭切到开启都重新警告；已开启时重复保存不重复打扰。
        if (enabled && !state.synthEnabled) confirmAdvancedSynthEnable(save);
        else save();
      };
    }

    const grid = document.getElementById('synthGrid');
    if (!items.length) {
      grid.innerHTML = `<div class="empty-state">${tr('synth.noItems', '没有可用于合成的重复收集物')}<br><span style="font-size:12px">${tr('synth.noItemsHint', '合成会保留 1 份，只有第 2 份及以后可以被消耗')}</span></div>`;
      return;
    }
    grid.innerHTML = items.map(x => {
      const usable = synthUsable(x);
      const sel = synthSlotCount(x.id);
      return `<div class="card ${sel ? 'selected' : ''}" data-id="${x.id}">
        ${lockButtonHtml(x.type, x.id)}
        ${img(x.thumb, 'thumb', x.name)}
        <div class="rarity-bar ${rarityClass(x.rarity)}"></div>
        <div class="name" title="${esc(x.name)}">${esc(x.name)}</div>
        <div class="sub">${usable > 0 ? tr('synth.usable', '可用 ×{count}', { count: usable }) : tr('synth.selectedFull', '已选满')}</div>
        ${x.quantity > 1 ? `<div class="qty">×${x.quantity}</div>` : ''}
      </div>`;
    }).join('');
    grid.querySelectorAll('[data-lock-type]').forEach(el => {
      el.onclick = async e => { e.stopPropagation(); await toggleAssetLock(el.dataset.lockType, Number(el.dataset.lockId)); };
    });
    grid.querySelectorAll('.card').forEach(el => {
      el.onclick = () => {
        const item = synthBagById(Number(el.dataset.id));
        if (!item) return;
        if (synthUsable(item) <= 0) return toast(tr('synth.noExtra', '这个收集物没有多余份数可用于合成'), 'err');
        if (filled >= need) return toast(tr('synth.slotsFull', '槽位已满'), 'err');
        const anchorNow = synthAnchor();
        if (anchorNow && (anchorNow.type !== item.type || effRarity(anchorNow.rarity) !== effRarity(item.rarity)))
          return toast(tr('synth.sameType', '请选择相同类型和稀有度的收集物'), 'err');
        const idx = state.synthSlots.findIndex(v => v === null);
        if (idx < 0) return;
        state.synthSlots[idx] = item.id;
        renderSynth(true);
      };
    });
    // 恢复点击前的网格位置；对选择材料这种同列表重绘，用户不应该被拉回顶部。
    if (prevScroll > 0) grid.scrollTop = prevScroll;
    updateSynthBadge();
  }

  async function synthOneClick() {
    if (synthAnyMode()) return toast(tr('synth.advancedForbidden', '高级合成禁止使用一键放置'), 'err');
    const need = synthNeed();
    const filled = state.synthSlots.filter(x => x !== null).length;
    if (filled >= need) return;
    const anchor = synthAnchor();
    // 一键选择搜索全部三类材料，不再受当前“类型”页签限制。
    await Promise.all(['character_skin', 'accessory', 'emote']
      .map(t => ensureAtlas(t).catch(() => [])));
    const pool = (synthAnyMode() ? synthEligible('') : ['character_skin', 'accessory', 'emote'].flatMap(t => synthEligible(t)))
      .filter(x => synthUsable(x) > 0);

    if (synthAnyMode()) {
      const remaining = need - filled;
      if (pool.reduce((sum, x) => sum + synthUsable(x), 0) < remaining)
        return toast(tr('synth.notEnough', '库存中的可用材料不足'), 'err');
      const slots = [...state.synthSlots];
      for (const item of pool.sort((a, b) => synthUsable(b) - synthUsable(a))) {
        const take = Math.min(synthUsable(item), remaining - slots.filter(Boolean).length);
        for (let i = 0; i < take; i++) {
          const idx = slots.findIndex(v => v === null);
          if (idx < 0) break;
          slots[idx] = item.id;
        }
        if (slots.filter(Boolean).length >= need) break;
      }
      state.synthSlots = slots;
      renderSynth();
      return;
    }

    let group;
    let chosenType;
    let chosenRarity;
    if (anchor) {
      chosenType = anchor.type;
      chosenRarity = effRarity(anchor.rarity);
      group = pool.filter(x => x.type === chosenType && effRarity(x.rarity) === chosenRarity);
      if (group.reduce((s, x) => s + synthUsable(x), 0) < need - filled)
        return toast(tr('synth.anchorNotEnough', '已有槽位对应的类型/稀有度材料不足'), 'err');
    } else {
      const groups = new Map();
      for (const it of pool) {
        const k = it.type + '|' + effRarity(it.rarity);
        if (!groups.has(k)) groups.set(k, []);
        groups.get(k).push(it);
      }
      const orderT = { character_skin: 0, accessory: 1, emote: 2 };
      const orderR = { epic: 4, rare: 3, uncommon: 2, common: 1 };
      const candidates = [...groups.values()]
        .filter(g => g.reduce((s, x) => s + synthUsable(x), 0) >= need)
        .sort((a, b) =>
          orderR[effRarity(b[0].rarity)] - orderR[effRarity(a[0].rarity)]
          || orderT[a[0].type] - orderT[b[0].type]
          || b.reduce((s, x) => s + synthUsable(x), 0) - a.reduce((s, x) => s + synthUsable(x), 0));
      if (!candidates.length)
        return toast(tr('synth.noMaterials', '角色 / 配件 / 表情中都没有足够的多余材料'), 'err');
      group = candidates[0];
      chosenType = group[0].type;
      chosenRarity = effRarity(group[0].rarity);
    }

    let remaining = need - filled;
    const slots = [...state.synthSlots];
    for (const it of group) {
      const take = Math.min(synthUsable(it), remaining);
      for (let k = 0; k < take; k++) {
        const idx = slots.findIndex(v => v === null);
        if (idx < 0) break;
        slots[idx] = it.id;
      }
      remaining -= take;
      if (remaining <= 0) break;
    }
    state.synthSlots = slots;
    // 让类型/稀有度页签跟随实际选中的材料，否则 renderSynth 会把跨类材料当作无效槽位清掉。
    state.synthType = chosenType;
    state.synthRarity = chosenRarity;
    renderSynth();
  }

  /**
   * 开箱/合成/兑换后刷新背包，并把新物品前插到「最近获得」第一页。
   * 服务器背包列表可能比开箱响应晚一拍；带 acquiredId 时最多重试约 3 秒，
   * 确保面板不因为一次过早的 bag.list 而漏掉刚获得的物品。
   */
  async function refreshAfterAcquire(acquiredId) {
    acquiredId = Number(acquiredId);
    if (!Number.isFinite(acquiredId) || acquiredId <= 0) acquiredId = 0;
    if (acquiredId) markRecentObtained(acquiredId);
    let loaded = false;
    for (let attempt = 0; attempt < 10; attempt++) {
      try {
        const items = await fetchBagItems(true);
        loaded = true;
        if (!acquiredId || items.some(x => x.id === acquiredId)) break;
      } catch { }
      await new Promise(resolve => setTimeout(resolve, 300));
    }
    if (loaded) {
      state.bagNewReady = false;
      updateSynthBadge();
      refreshNewAssets();
      if (acquiredId) {
        const owned = new Set((state.bag || []).map(x => x.id));
        if (owned.has(acquiredId)) {
          markRecentObtained(acquiredId);
          state.bagRecentIds = loadRecent().filter(id => owned.has(id));
          state.bagNewIds = loadUnseen().filter(id => owned.has(id));
        }
      }
    }
    if (state.page === 'bag') renderBag();
    if (!loaded) toast(tr('synth.bagRefreshFailed', '背包刷新失败，请稍后手动刷新'), 'err');
  }

  function bagDelta(before, after) {
    const oldQty = new Map((before || []).map(x => [String(x.id), Number(x.quantity) || 1]));
    const gained = [];
    for (const item of after || []) {
      const qty = Number(item.quantity) || 1;
      const diff = qty - (oldQty.get(String(item.id)) || 0);
      if (diff > 0) gained.push({ ...item, gained: diff });
    }
    return gained;
  }

  function directRedeemItems(result) {
    const candidates = [result?.items, result?.data?.items, result?.assets, result?.data];
    for (const candidate of candidates) {
      if (!Array.isArray(candidate)) continue;
      return candidate.map(x => {
        const id = Number(x.asset_id || x.id || 0);
        const bagItem = (state.bag || []).find(v => v.id === id);
        return {
          ...(bagItem || {}),
          ...x,
          id: id || bagItem?.id || 0,
          name: x.name || x.asset_name || bagItem?.name || tr('synth.newItem', '新物品'),
          thumb: x.thumb || bagItem?.thumb || '',
          gained: Number(x.quantity || x.count || 1) || 1,
        };
      }).filter(x => x.id > 0 || x.name);
    }
    return [];
  }

  function showRedeemReward(items, code) {
    const list = (items || []).filter(Boolean);
    const body = list.length
      ? `<div class="redeem-items">${list.map(x => `
          <div class="redeem-item">
            ${x.thumb ? img(x.thumb, 'redeem-thumb', x.name) : '<div class="redeem-thumb">🎁</div>'}
            <div class="redeem-name">${esc(x.name || tr('synth.newItem', '新物品'))}</div>
            <div class="redeem-qty">×${x.gained || 1}</div>
          </div>`).join('')}</div>`
      : `<div class="r-sub">${tr('synth.redeemToBag', '奖励已发放到背包，请查看背包。')}</div>`;
    modal(`<div class="reward">
      <div class="r-name">${tr('synth.redeemSuccess', '兑换成功')}</div>
      <div class="r-sub">${esc(code)}</div>
      ${body}
      <button class="btn" id="mOk" style="margin-top:8px">${tr('synth.take', '收下')}</button>
    </div>`, host => host.querySelector('#mOk').onclick = closeModal);
  }

  async function redeemCode(code) {
    // 先取一份兑换前背包，用于兼容官方接口不返回奖励明细的情况。
    let before = [];
    let beforeOk = false;
    try {
      before = await fetchBagItems(true);
      before = before || [];
      beforeOk = true;
    } catch { }

    const result = await tryCall('redeem', { code });
    let gained = [];
    if (beforeOk) {
      for (let attempt = 0; attempt < 10; attempt++) {
        try {
          const items = await fetchBagItems(true);
          gained = bagDelta(before, items);
          updateSynthBadge();
          if (gained.length) break;
        } catch { }
        await new Promise(resolve => setTimeout(resolve, 300));
      }
    }
    if (!beforeOk) {
      try {
        await fetchBagItems(true);
      } catch { }
    }
    if (!gained.length) {
      const direct = directRedeemItems(result);
      if (direct.length) gained = direct;
    }
    // 兑换到已有物品时 bagDelta 也会包含它；和开箱/合成一样前插到最近获得。
    for (const item of gained) {
      const id = Number(item?.id);
      if (Number.isFinite(id) && id > 0) markRecentObtained(id);
    }
    state.bagNewReady = false;
    refreshNewAssets();
    if (gained.length) {
      const owned = new Set((state.bag || []).map(x => x.id));
      state.bagRecentIds = loadRecent().filter(id => owned.has(id));
      state.bagNewIds = loadUnseen().filter(id => owned.has(id));
    }
    if (state.page === 'bag') renderBag();
    if (gained.length) showRedeemReward(gained, code);
    else toast(tr('synth.redeemDone', '兑换成功，奖励已发放到背包'));
  }

  async function performSynth(ids, sameLevel) {
    try {
      state.synthBusy = true;
      const r = await tryCall('synthesize', { ids, sameLevel });
      const id = Number(r?.asset_id || r?.data?.asset_id) || 0;
      await refreshAfterAcquire(id);
      const found = (state.bag || []).find(v => v.id === id);
      let thumb = '', name = tr('synth.success', '合成成功');
      if (found) { thumb = found.thumb; name = found.name; }
      modal(`<div class="reward">
        <div class="rays"></div>
        ${thumb ? img(thumb, 'prize', name) : '<div style="font-size:64px">🎁</div>'}
        <div class="r-name">${esc(name)}</div>
        <div class="r-sub">${tr('synth.success', '合成成功')}</div>
        <button class="btn" id="mOk" style="margin-top:8px">${tr('synth.take', '收下')}</button>
      </div>`, host => host.querySelector('#mOk').onclick = closeModal);
      synthResetSlots();
      renderSynth();
    } catch (e) {
      // 后端互斥锁可能返回“正在进行其他合成操作”，这里不能静默吞掉。
      toast(e?.message || tr('synth.failed', '合成失败'), 'err');
    }
    finally { state.synthBusy = false; }
  }

  async function doSynth() {
    if (state.synthBusy) return;
    const need = synthNeed();
    const ids = state.synthSlots.filter(x => x !== null);
    if (ids.length !== need) return toast(tr('synth.needMaterials', '需要 {count} 个材料', { count: need }), 'err');
    const selectedItems = ids.map(id => synthBagById(id)).filter(Boolean);
    if (selectedItems.length !== ids.length)
      return toast(tr('synth.materialInvalid', '合成材料数据失效，请重新选择'), 'err');
    const first = selectedItems[0];
    if (selectedItems.some(x => x.type !== first.type || effRarity(x.rarity) !== effRarity(first.rarity)))
      return toast(tr('synth.crossType', '不能跨材料合成，请选择相同类型和稀有度的收集物'), 'err');
    const sameLevel = state.synthMode === 'same';
    if (synthAnyMode()) {
      state.synthBusy = true;
      modal(`<div class="about">
        <div class="about-title">${tr('synth.confirmTitle', '合成确认')}</div>
        <div class="about-note about-note--warn"><p>${tr('synth.confirmBody', '确认要开始此次合成吗？这将可能损失你已解锁的图鉴')}</p></div>
        <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px">
          <button class="btn ghost" id="synthCancel">${tr('common.cancel', '取消')}</button>
          <button class="btn danger" id="synthConfirm">${tr('synth.confirm', '确认合成')}</button>
        </div>
      </div>`, host => {
        const release = () => { state.synthBusy = false; };
        host.onclick = e => { if (e.target === host) { closeModal(); release(); } };
        host.querySelector('#synthCancel').onclick = () => { closeModal(); release(); };
        host.querySelector('#synthConfirm').onclick = async () => {
          closeModal();
          try { await performSynth(ids, sameLevel); }
          finally { release(); }
        };
      });
      return;
    }
    await performSynth(ids, sameLevel);
  }

  /* ---------------- 预设 ---------------- */
  async function ensurePresets(force = false) {
    if (state.presets && !force) return state.presets;
    const data = await tryCall('presets.list');
    state.presets = Array.isArray(data) ? data : (data?.list || []);
    await ensureBag().catch(() => { });
    return state.presets;
  }

  async function ensureLocalPresets(force = false) {
    if (state.localPresets && !force) return state.localPresets;
    const data = await tryCall('presets.local.list');
    state.localPresets = Array.isArray(data) ? data : [];
    return state.localPresets;
  }

  function catalogName(type, id) {
    const bagItem = (state.bag || []).find(v => v.id === id);
    if (bagItem) return bagItem.name;
    const item = (state.catalog[type] || []).find(v => Number(v.id) === Number(id));
    if (item?.name && !/^#?\d+$/.test(String(item.name))) return item.name;
    const atlasItem = (state.atlas[type] || []).find(v => Number(v.mainId) === Number(id));
    return atlasItem?.name || item?.name || (id ? '#' + id : '无');
  }

  function catalogThumb(type, id) {
    const item = (state.catalog[type] || []).find(v => Number(v.id) === Number(id));
    if (item?.thumb) return item.thumb;
    const atlasItem = (state.atlas[type] || []).find(v => Number(v.mainId) === Number(id));
    if (atlasItem?.thumb) return atlasItem.thumb;
    // 背包页不一定加载过图鉴目录；直接用背包里的缩略图兜底。
    const bagItem = (state.bag || []).find(v => v.type === type && Number(v.id) === Number(id));
    return bagItem?.thumb || '';
  }

  function catalogPreview(type, id) {
    const item = (state.catalog[type] || []).find(v => Number(v.id) === Number(id));
    if (item?.previewUrl || item?.thumb) return item.previewUrl || item.thumb;
    const bagItem = (state.bag || []).find(v => v.type === type && Number(v.id) === Number(id));
    return bagItem?.thumb || '';
  }

  function normalizeAccessoryScale(value) {
    const n = Number(value);
    // 25–175 以 100 为中点；滑条、native 钳制和 CSS 零刻度保持一致。
    return Number.isFinite(n) && n > 0 ? Math.max(25, Math.min(175, Math.round(n))) : 100;
  }
  function normalizeAccessoryRotation(value) {
    const n = Number(value);
    return Number.isFinite(n) ? Math.max(-180, Math.min(180, Math.round(n))) : 0;
  }

  function presetAccessoryOffset(accessoryId, includeLocal = true) {
    const id = Number(accessoryId) || 0;
    const item = (state.catalog.accessory || []).find(v => Number(v.id) === id);
    let x = Number(item?.offsetX || 0);
    let y = Number(item?.offsetY || 0);
    let scalePercent = 100;
    let rotationDegrees = 0;
    // 预设预览按“应用后实际看到的样子”叠加该头饰自己的本地微调。
    // 云端形态预览不应包含只在本机生效的微调，因此可以传 includeLocal=false。
    if (includeLocal) {
      const stored = state.boot?.appearance?.accessoryOffsets?.[String(id)];
      if (Array.isArray(stored) && stored.length >= 2) {
        x += Number(stored[0] || 0);
        y += Number(stored[1] || 0);
        // 旧配置只有 [x,y]，缺失的缩放/旋转按默认值处理。
        if (stored.length >= 3) scalePercent = normalizeAccessoryScale(stored[2]);
        if (stored.length >= 4) rotationDegrees = normalizeAccessoryRotation(stored[3]);
      } else if (id > 0 && id === Number(state.boot?.appearance?.accessoryAssetId || 0)) {
        x += Number(state.boot?.appearance?.accessoryOffsetX || 0);
        y += Number(state.boot?.appearance?.accessoryOffsetY || 0);
        scalePercent = normalizeAccessoryScale(state.boot?.appearance?.accessoryScalePercent);
        rotationDegrees = normalizeAccessoryRotation(state.boot?.appearance?.accessoryRotationDegrees);
      }
    }
    return { x, y, scalePercent, rotationDegrees };
  }

  function presetStageHtml(characterId, accessoryId, includeLocalOffset = true) {
    const charSrc = catalogPreview('character_skin', characterId);
    const accSrc = catalogPreview('accessory', accessoryId);
    const offset = presetAccessoryOffset(accessoryId, includeLocalOffset);
    // 桌宠里角色和头饰共用 200×200 的猫区域；预览统一缩放为 140×140，偏移按同比例缩放。
    const scale = 140 / 200;
    const accTransform = offset.scalePercent !== 100 || offset.rotationDegrees !== 0
      ? `;transform:rotate(${offset.rotationDegrees}deg) scale(${(offset.scalePercent / 100).toFixed(3)})`
      : '';
    return `<div class="preset-preview">
      <div class="preset-preview-stage">
        ${charSrc ? `<img class="preset-preview-char" src="${esc(charSrc)}" alt="">` : '<span class="muted preset-preview-empty">无角色</span>'}
        ${accSrc ? `<img class="preset-preview-acc" src="${esc(accSrc)}" alt="" style="left:${(offset.x * scale).toFixed(2)}px;top:${(offset.y * scale).toFixed(2)}px${accTransform}">` : ''}
      </div>
    </div>`;
  }

  function emoteKindLabel(x) {
    const name = String(x?.name || '');
    const parts = name.split(/[・·]/).map(s => s.trim()).filter(Boolean);
    if (parts.length > 1) return parts[parts.length - 1];
    const asset = String(x?.assetName || '').replace(/\.png$/i, '');
    const m = asset.match(/_([a-z0-9]+)$/i);
    return m ? m[1] : '';
  }

  function presetEmoteIds(p) {
    return (p.emotes || p.emoteAssetIds || []).map(e => Number(e.asset_id ?? e)).filter(id => id > 0).slice(0, 5);
  }
  /** 保留 5 个槽位的位置（0 表示空）；应用/编辑本地预设时必须保留空槽，不能把后面的表情挤到前面。 */
  function presetEmoteSlots(p) {
    const raw = p.emotes || p.emoteAssetIds || [];
    return Array.from({ length: 5 }, (_, i) => Number(raw[i]?.asset_id ?? raw[i] ?? 0) || 0);
  }
  function presetHasEmotes(p) { return presetEmoteSlots(p).some(id => id > 0); }

  function presetPreviewHtml(p, includeLocalOffset = true) {
    const charId = Number(p.character_asset_id ?? p.characterAssetId ?? 0);
    const accId = Number(p.accessory_asset_id ?? p.accessoryAssetId ?? 0);
    const emotes = presetEmoteSlots(p);
    const emoteHtml = [0, 1, 2, 3, 4].map(i => {
      const thumb = catalogThumb('emote', emotes[i]);
      return `<div class="preset-emote">${thumb ? `<img src="${esc(thumb)}" alt="">` : '<span class="muted">空</span>'}<span>${i + 1}</span></div>`;
    }).join('');
    return `${presetStageHtml(charId, accId, includeLocalOffset)}
    <div class="preset-emote-preview">${emoteHtml}</div>`;
  }

  async function saveLocalPresetDialog(existing = null) {
    const appearance = state.boot?.appearance || {};
    const source = existing || {
      id: '',
      name: '',
      characterAssetId: appearance.characterAssetId || 0,
      accessoryAssetId: appearance.accessoryAssetId || 0,
      emoteAssetIds: appearance.emoteAssetIds || [],
    };
    const emoteIds = [...(source.emoteAssetIds || [])];
    while (emoteIds.length < 5) emoteIds.push(0);
    modal(`<div class="preset-edit">
      <div class="preset-title" style="margin-bottom:10px">${existing ? tr('preset.editLocalTitle', '编辑本地外观') : tr('preset.saveLocalTitle', '保存为本地外观')}</div>
      <label class="setting-title">${tr('preset.name', '名称')}</label>
      <input class="input" id="localPresetName" value="${esc(source.name || '')}" placeholder="${tr('preset.namePlaceholder', '例如：日常搭配')}" style="margin:6px 0 12px">
      <div class="muted" style="font-size:12px;line-height:1.6">${tr('preset.currentCharacter', '保存当前角色：{name}', { name: esc(catalogName('character_skin', source.characterAssetId)) })}<br>${tr('preset.accessory', '配件：{name}', { name: esc(catalogName('accessory', source.accessoryAssetId)) })}<br>${tr('preset.emoteCount', '表情：{count} 个', { count: emoteIds.filter(Boolean).length })}</div>
      <div style="display:flex;gap:10px;margin-top:14px;justify-content:flex-end">
        <button class="btn ghost" id="localPresetCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn" id="localPresetSave">${tr('common.save', '保存')}</button>
      </div>
    </div>`, h => {
      h.querySelector('#localPresetCancel').onclick = closeModal;
      h.querySelector('#localPresetSave').onclick = async () => {
        const name = h.querySelector('#localPresetName').value.trim() || tr('preset.defaultLocalName', '本地预设');
        try {
          await tryCall('presets.local.save', {
            id: source.id || '',
            name,
            characterAssetId: source.characterAssetId || 0,
            accessoryAssetId: source.accessoryAssetId || 0,
            emoteAssetIds: emoteIds,
          });
          state.localPresets = null;
          closeModal();
          await ensureLocalPresets(true);
          renderPreset();
          toast(tr('preset.savedLocal', '已保存本地外观'));
        } catch { }
      };
    });
  }

  async function applyPreset(p, isLocal, ignoreEmotes) {
    if (!p) return;
    const charId = Number(p.character_asset_id ?? p.characterAssetId ?? 0);
    const accId = Number(p.accessory_asset_id ?? p.accessoryAssetId ?? 0);
    const emoteSlots = presetEmoteSlots(p);
    const hasEmotes = emoteSlots.some(id => id > 0);
    // 云端预设可能完全不包含表情：这时绝不写入 0，避免把本地 5 个表情槽静默清空。
    const writeEmotes = hasEmotes && !ignoreEmotes;
    try {
      await ensureBag();
      if (charId > 0) await tryCall('appearance.set', { type: 'character_skin', id: charId });
      await tryCall('appearance.set', { type: 'accessory', id: accId });
      if (writeEmotes) {
        for (let i = 0; i < 5; i++) await tryCall('appearance.setSlot', { slot: i, id: emoteSlots[i] || 0 });
      }
      const owned = new Set((state.bag || []).map(x => Number(x.id)));
      const charOwned = charId > 0 && owned.has(charId);
      const accOwned = accId === 0 || owned.has(accId);
      let uploaded = false;
      if (!isLocal && charOwned && accOwned) {
        try { await tryCall('appearance.sync'); uploaded = true; } catch { }
      }
      state.boot = await call('boot');
      renderChrome();
      renderPreset();
      const localOnly = [];
      if (!charOwned) localOnly.push(tr('preset.localRole', '角色 {name}', { name: catalogName('character_skin', charId) }));
      if (!accOwned) localOnly.push(tr('preset.localAccessory', '配件 {name}', { name: catalogName('accessory', accId) }));
      const suffix = !hasEmotes
        ? tr('preset.keepEmotesSuffix', '（预设不含表情，已保留本地表情栏）')
        : (ignoreEmotes ? tr('preset.ignoreEmotesSuffix', '（已忽略预设表情，本地表情栏未变）') : '');
      if (localOnly.length) toast(tr('preset.localOnlySuffix', '已应用；{items}仅本地生效', { items: localOnly.join(tr('common.listSeparator', '、')) }) + suffix);
      else if (isLocal) toast(tr('preset.appliedLocal', '已应用本地外观') + suffix);
      else toast((uploaded ? tr('preset.appliedCloud', '已应用并同步云端') : tr('preset.appliedLocal', '已应用本地外观')) + suffix);
    } catch (e) { toast(e.message || tr('preset.applyFailed', '应用预设失败'), 'err'); }
  }

  /** 「使用」按钮：非空表情集合覆盖前明确确认；空集合直接应用并说明保留本地表情。 */
  function confirmApplyPreset(p, isLocal) {
    if (!p) return;
    if (!presetHasEmotes(p)) {
      applyPreset(p, isLocal, true);
      return;
    }
    const count = presetEmoteSlots(p).filter(id => id > 0).length;
    modal(`<div class="about">
      <div class="about-title">${tr('preset.applyTitle', '应用预设')}</div>
      <div class="about-note"><p>${tr('preset.applyBody', '该预设包含 {count} 个表情，应用后会覆盖本地 5 个表情槽位（空槽也会被清空）。', { count })}</p></div>
      <div class="about-note about-note--warn"><p>${tr('preset.applyWarn', '如果只想切换角色和配件，请选择「使用（忽略表情）」，本地表情栏不会改变。')}</p></div>
      <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px;flex-wrap:wrap">
        <button class="btn ghost" id="presetApplyCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn ghost" id="presetApplyIgnore">${tr('preset.applyIgnore', '使用（忽略表情）')}</button>
        <button class="btn" id="presetApplyWithEmotes">${tr('preset.applyWithEmotes', '应用角色/配件与表情')}</button>
      </div>
    </div>`, host => {
      host.querySelector('#presetApplyCancel').onclick = closeModal;
      host.querySelector('#presetApplyIgnore').onclick = () => { closeModal(); applyPreset(p, isLocal, true); };
      host.querySelector('#presetApplyWithEmotes').onclick = () => { closeModal(); applyPreset(p, isLocal, false); };
    });
  }

  async function openPresetEditor(p, isLocal, companionMode = false) {
    await Promise.all([
      ...(companionMode ? ['character_skin', 'accessory'] : ['character_skin', 'accessory', 'emote'])
        .map(t => ensureCatalog(t).catch(() => [])),
      companionMode && !isLogged() ? Promise.resolve([]) : ensureBag().catch(() => []),
    ]);
    const temp = {
      characterAssetId: Number(p.character_asset_id ?? p.characterAssetId ?? 0),
      accessoryAssetId: Number(p.accessory_asset_id ?? p.accessoryAssetId ?? 0),
      emoteAssetIds: presetEmoteSlots(p),
    };
    while (temp.emoteAssetIds.length < 5) temp.emoteAssetIds.push(0);
    let tab = 'character_skin';
    let selectedSlot = 0;
    let search = '';
    let filter = '';
    let filter2 = '';
    let ownedOnly = false;
    const itemsOf = type => state.catalog[type] || [];
    const selectedItemId = () => tab === 'character_skin' ? temp.characterAssetId : tab === 'accessory' ? temp.accessoryAssetId : 0;

    modal(`<div class="preset-edit">
      <div class="preset-title" style="margin-bottom:10px">${companionMode ? (p.id ? tr('companion.editTitle', '编辑自定义伙伴') : tr('companion.addTitle', '添加自定义伙伴')) : (isLocal ? tr('preset.editingLocal', '编辑本地外观') : tr('preset.editingCloud', '修改云外观配置'))}</div>
      ${isLocal ? `<label class="setting-title">${tr('preset.name', '名称')}</label><input class="input" id="peName" value="${esc(p.name || '')}" placeholder="${companionMode ? tr('companion.namePlaceholder', '例如：菲比·Miratsu（显示为菲比）') : ''}" style="margin:6px 0 10px">` : ''}
      ${companionMode ? `<div class="companion-editor-options">
        <label class="setting-title">${tr('companion.clickFrequency', '点击频率')}</label>
        <div class="companion-option-row">
          <input class="input" id="peClickSeconds" type="number" min="0" max="3600" step="1" value="${Number(p.clickIntervalSeconds || 0)}" style="width:92px">
          <span class="muted">${tr('companion.secondsUnit', '秒/次；0 表示不自动点击')}</span>
        </div>
        <label class="companion-check"><input type="checkbox" id="peRandomClick" ${p.randomClick ? 'checked' : ''}> ${tr('companion.randomClickHint', '随机化点击间隔与左右手动作')}</label>
        <label class="companion-check"><input type="checkbox" id="peShowBar" ${p.showBar !== false ? 'checked' : ''}> ${tr('companion.showBarHint', '显示该伙伴的昵称底栏（全局隐藏联机底栏时仍会隐藏）')}</label>
      </div>` : ''}
      <div id="pePreview"></div>
      <div class="pe-tabs">
        <button type="button" class="pill active" data-pe-tab="character_skin">${tr('preset.tabCharacter', '角色')}</button>
        <button type="button" class="pill" data-pe-tab="accessory">${tr('preset.tabAccessory', '配件')}</button>
        ${companionMode ? '' : `<button type="button" class="pill" data-pe-tab="emote">${tr('preset.tabEmote', '表情')}</button>`}
        <span class="spacer"></span>
        <span class="muted" id="peSlotHint" style="font-size:11px"></span>
      </div>
      <div class="pe-filters">
        <input class="input" id="peSearch" placeholder="${tr('preset.searchPlaceholder', '搜索名称 / 系列')}" style="flex:1;min-width:120px">
        <span id="peFilterHost" style="display:flex;gap:8px"></span>
        <button type="button" class="pill" id="peOwnedOnly" title="${tr('preset.ownedOnlyTitle', '只显示当前账号已拥有的条目')}">${tr('preset.ownedOnly', '仅拥有')}</button>
      </div>
      <div class="pe-grid" id="peGrid"></div>
      <div class="muted" style="font-size:11px;margin-top:8px">${companionMode ? tr('companion.pickerHint', '自定义伙伴可使用任意角色与配件（未拥有也可选）；只保存在本机，不会上传服务器。') : (isLocal ? tr('preset.localHint', '本地预设可以任意搭配；云端修改会创建新预设并删除旧预设。') : tr('preset.cloudHint', '云端预设需要角色、配件和表情都满足拥有/角色匹配条件才能保存。'))}</div>
      <div style="display:flex;gap:10px;margin-top:14px;justify-content:flex-end">
        <button class="btn ghost" id="peCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn" id="peSave">${tr('common.save', '保存')}</button>
      </div>
    </div>`, h => {
      const escAttr = esc;
      const emoteSlotsHtml = () => [0, 1, 2, 3, 4].map(i => {
        const thumb = catalogThumb('emote', temp.emoteAssetIds[i]);
        return `<div class="preset-emote ${i === selectedSlot ? 'selected' : ''}" data-pe-slot="${i}" title="${tr('bag.slotLabel', '槽位 {n}', { n: i + 1 })}">${thumb ? `<img src="${escAttr(thumb)}" alt="">` : `<span class="muted">${tr('common.empty', '空')}</span>`}<span>${i + 1}</span></div>`;
      }).join('');
      const renderPreview = () => {
        h.querySelector('#pePreview').innerHTML = companionMode
          ? `${presetStageHtml(temp.characterAssetId, temp.accessoryAssetId, false)}<div class="companion-display-hint">${tr('companion.displayPreview', '桌面显示名：{name}', { name: esc(companionDisplayName(h.querySelector('#peName')?.value || p.name || '') || tr('companion.defaultName', '伙伴')) })}</div>`
          : `${presetStageHtml(temp.characterAssetId, temp.accessoryAssetId)}<div class="preset-emote-preview">${emoteSlotsHtml()}</div>`;
        h.querySelector('#peSlotHint').textContent = tabsEmote() ? (tab === 'emote' ? tr('preset.editSlot', '正在编辑槽位 {n}', { n: selectedSlot + 1 }) : tr('preset.clickEmote', '点击下方表情可放入当前槽位')) : '';
        h.querySelectorAll('[data-pe-slot]').forEach(el => {
          el.onclick = () => { selectedSlot = Number(el.dataset.peSlot); renderPreview(); renderGrid(); };
        });
      };
      const tabsEmote = () => !companionMode;

      const renderGrid = () => {
        const all = itemsOf(tab);
        const roleMap = new Map();
        const roleFallback = new Map();
        for (const x of all) {
          const r = String(assetMeta(x).role || 0);
          if (!r || r === '0') continue;
          const info = roleLabelInfo(x);
          if (!roleMap.has(r) || (roleFallback.get(r) && !info.fallback)) {
            roleMap.set(r, info.label || r);
            roleFallback.set(r, info.fallback);
          }
        }
        const roleOptions = [...roleMap.entries()].sort((a, b) => String(a[1]).localeCompare(String(b[1]), 'zh'));
        const seriesOptions = [...new Set(all.map(x => x.series).filter(Boolean))].sort((a, b) => String(a).localeCompare(String(b), 'zh'));
        const rarityOptions = [...new Set(all.map(x => effRarity(x.rarity)).filter(Boolean))].sort((a, b) => (RARITY_ORDER[b] || 0) - (RARITY_ORDER[a] || 0));
        const kindMap = new Map();
        if (tab === 'emote') {
          for (const x of all) {
            const label = emoteKindLabel(x);
            if (label && !kindMap.has(label)) kindMap.set(label, label);
          }
        }
        const defs = tab === 'character_skin'
          ? [{ key: 'role', label: tr('preset.role', '角色'), options: roleOptions }, { key: 'series', label: tr('detail.series', '系列'), options: seriesOptions.map(s => [s, s]) }]
          : tab === 'emote'
            ? [{ key: 'role', label: tr('preset.role', '角色'), options: roleOptions }, { key: 'kind', label: tr('preset.tabEmote', '表情'), options: [...kindMap.entries()].sort((a, b) => String(a[1]).localeCompare(String(b[1]), 'zh')) }]
            : [{ key: 'rarity', label: tr('detail.rarity', '稀有度'), options: rarityOptions.map(r => [r, rarityText(r) || r]) }, { key: 'series', label: tr('detail.series', '系列'), options: seriesOptions.map(s => [s, s]) }];
        const host = h.querySelector('#peFilterHost');
        host.innerHTML = defs.filter(d => d.options.length).map((d, i) =>
          `<select class="select" id="peFilter${i}" style="width:130px"><option value="">${d.label}：${tr('common.all', '全部')}</option>${d.options.map(([value, label]) => `<option value="${escAttr(value)}">${escAttr(d.label + '：' + label)}</option>`).join('')}</select>`
        ).join('');
        const applyFilter = (def, value) => {
          if (!value) return;
          if (def.key === 'role') list = list.filter(x => String(assetMeta(x).role || 0) === value);
          else if (def.key === 'series') list = list.filter(x => String(x.series || '') === value);
          else if (def.key === 'kind') list = list.filter(x => emoteKindLabel(x) === value);
          else if (def.key === 'rarity') list = list.filter(x => effRarity(x.rarity) === value);
        };
        let list = all;
        const shownDefs = defs.filter(d => d.options.length);
        const values = [filter, filter2];
        shownDefs.forEach((d, i) => {
          const el = h.querySelector('#peFilter' + i);
          if (!el) return;
          el.value = values[i] || '';
          el.onchange = () => {
            if (i === 0) filter = el.value; else filter2 = el.value;
            renderGrid();
          };
          applyFilter(d, el.value);
        });
        if (ownedOnly) {
          const ownedIds = new Set((state.bag || []).map(x => Number(x.id)));
          list = list.filter(x => ownedIds.has(Number(x.id)));
        }
        if (search) list = list.filter(x => (catalogName(tab, x.id) || '').toLowerCase().includes(search) || (x.series || '').toLowerCase().includes(search));
        const selectedEmoteIds = new Set(temp.emoteAssetIds.filter(Boolean).map(Number));
        const grid = h.querySelector('#peGrid');
        grid.innerHTML = list.length ? list.map(x => {
          const id = Number(x.id);
          const selected = tab === 'emote' ? selectedEmoteIds.has(id) : id === selectedItemId();
          return `<button type="button" class="pe-card ${selected ? 'selected' : ''}" data-pe-type="${tab}" data-pe-id="${id}" title="${escAttr(catalogName(tab, id))}">
            ${x.thumb ? `<img src="${escAttr(x.thumb)}" alt="">` : '<span class="muted">□</span>'}
            <span class="pe-name">${esc(catalogName(tab, id))}</span>
            <span class="pe-sub">${esc(x.series || (tab === 'emote' ? emoteKindLabel(x) : ''))}</span>
          </button>`;
        }).join('') : `<div class="empty-state" style="grid-column:1/-1">${tr('preset.noMatch', '没有符合条件的条目')}</div>`;
        h.querySelectorAll('[data-pe-type]').forEach(el => {
          el.onclick = () => {
            const type = el.dataset.peType;
            const id = Number(el.dataset.peId);
            if (type === 'character_skin') temp.characterAssetId = id;
            else if (type === 'accessory') temp.accessoryAssetId = temp.accessoryAssetId === id ? 0 : id;
            else {
              temp.emoteAssetIds[selectedSlot] = id;
              const next = temp.emoteAssetIds.findIndex((v, i) => i > selectedSlot && !v);
              if (next >= 0) selectedSlot = next;
            }
            renderPreview();
            renderGrid();
          };
        });
        h.querySelector('#peSlotHint').textContent = companionMode
          ? ''
          : (tab === 'emote' ? tr('preset.editSlot', '正在编辑槽位 {n}', { n: selectedSlot + 1 }) : tr('preset.clickEmote', '点击下方表情可放入当前槽位'));
      };

      h.querySelectorAll('[data-pe-tab]').forEach(btn => btn.onclick = () => {
        tab = btn.dataset.peTab;
        search = '';
        filter = '';
        filter2 = '';
        h.querySelector('#peSearch').value = '';
        h.querySelectorAll('[data-pe-tab]').forEach(b => b.classList.toggle('active', b === btn));
        renderPreview();
        renderGrid();
      });
      h.querySelector('#peSearch').oninput = e => { search = e.target.value.trim().toLowerCase(); renderGrid(); };
      const ownedBtn = h.querySelector('#peOwnedOnly');
      ownedBtn.onclick = () => {
        ownedOnly = !ownedOnly;
        ownedBtn.classList.toggle('active', ownedOnly);
        renderGrid();
      };
      h.querySelector('#peCancel').onclick = closeModal;
      const nameInput = h.querySelector('#peName');
      if (nameInput) nameInput.oninput = () => renderPreview();
      h.querySelector('#peSave').onclick = async () => {
        const emoteAssetIds = temp.emoteAssetIds.slice(0, 5);
        try {
          if (companionMode) {
            const next = await tryCall('companions.save', {
              id: p.id || '',
              name: h.querySelector('#peName')?.value.trim() || tr('companion.defaultName', '伙伴'),
              characterAssetId: temp.characterAssetId,
              accessoryAssetId: temp.accessoryAssetId,
              clickIntervalSeconds: Math.max(0, Math.min(3600, Number(h.querySelector('#peClickSeconds')?.value) || 0)),
              randomClick: !!h.querySelector('#peRandomClick')?.checked,
              showBar: h.querySelector('#peShowBar')?.checked !== false,
            });
            state.companions = Array.isArray(next) ? next : [];
            closeModal();
            await ensureCompanions(true);
            renderCompanion();
            toast(tr('companion.saved', '自定义伙伴已保存'));
            return;
          }
          if (isLocal) {
            await tryCall('presets.local.save', {
              id: p.id || '',
              name: h.querySelector('#peName')?.value.trim() || p.name || tr('preset.defaultLocalName', '本地预设'),
              characterAssetId: temp.characterAssetId,
              accessoryAssetId: temp.accessoryAssetId,
              emoteAssetIds,
            });
            state.localPresets = null;
            closeModal();
            await ensureLocalPresets(true);
          } else {
            await tryCall('presets.create', {
              characterAssetId: temp.characterAssetId,
              accessoryAssetId: temp.accessoryAssetId,
              emoteAssetIds,
              includeEmotes: true,
            });
            const oldId = p.preset_id ?? p.id;
            if (oldId) await tryCall('presets.delete', { id: oldId });
            closeModal();
            await ensurePresets(true);
          }
          renderPreset();
          toast(tr('preset.updated', '预设已更新'));
        } catch (e) { toast(e.message || tr('preset.saveFailed', '保存预设失败'), 'err'); }
      };
      renderPreview();
      renderGrid();
    });
  }

  function presetCardHtml(p, index, isLocal) {
    const charId = Number(p.character_asset_id ?? p.characterAssetId ?? 0);
    const accId = Number(p.accessory_asset_id ?? p.accessoryAssetId ?? 0);
    const emotes = presetEmoteIds(p);
    const title = isLocal
      ? (p.name || tr('preset.localDefault', '本地预设 {n}', { n: index + 1 }))
      : tr('preset.cloudDefault', '预设 {n}', { n: index + 1 });
    return `<div class="preset-card">
      <div class="preset-head"><span class="preset-emoji">${isLocal ? '🏠' : '⭐'}</span><span class="preset-title">${esc(title)}</span></div>
      ${presetPreviewHtml(p)}
      <div class="detail-row"><span>${tr('preset.role', '角色')}</span><span>${esc(catalogName('character_skin', charId))}</span></div>
      <div class="detail-row"><span>${tr('preset.tabAccessory', '配件')}</span><span>${esc(catalogName('accessory', accId))}</span></div>
      <div class="detail-row"><span>${tr('preset.tabEmote', '表情')}</span><span>${emotes.length ? tr('preset.emoteCountShort', '{count} 个', { count: emotes.length }) : tr('preset.noEmotes', '不含表情（应用时保留本地）')}</span></div>
      <div class="preset-actions">
        <button class="btn ghost" data-${isLocal ? 'l' : 'c'}apply="${index}" title="${emotes.length ? tr('preset.applyConfirmTitle', '应用前会确认是否覆盖本地表情槽') : tr('preset.applyIgnoreTitle', '预设不含表情，不会改变本地表情槽')}">${tr('preset.use', '使用')}</button>
        <button class="btn ghost" data-${isLocal ? 'l' : 'c'}ignore="${index}" title="${tr('preset.applyIgnoreTitle2', '只应用角色和配件，本地 5 个表情槽保持不变')}">${tr('preset.applyIgnore', '使用（忽略表情）')}</button>
        <button class="btn ghost" data-${isLocal ? 'l' : 'c'}edit="${index}">${tr('common.edit', '修改')}</button>
        ${isLocal ? `<button class="btn ghost" data-lupload="${index}">${tr('preset.upload', '上传云端')}</button>` : ''}
        <button class="btn danger" data-${isLocal ? 'l' : 'c'}del="${index}">${tr('common.delete', '删除')}</button>
      </div>
    </div>`;
  }

  async function renderPreset() {
    if (pageAuthBlocked('preset')) return;
    setPage(`<div class="loading">${tr('preset.loading', '正在读取预设…')}</div>`);
    try {
      await Promise.all([
        ensurePresets(),
        ensureLocalPresets(),
        ...['character_skin', 'accessory', 'emote'].map(t => ensureCatalog(t).catch(() => [])),
        ...['character_skin', 'accessory', 'emote'].map(t => ensureAtlasOne(t).catch(() => [])),
      ]);
    } catch { setPage(`<div class="empty-state">${tr('preset.loadFailed', '预设读取失败')}</div>`); return; }
    const list = state.presets || [];
    const localList = state.localPresets || [];
    setPage(`
      <div class="preset-page-scroll">
        <div class="pill-row" style="margin-bottom:14px;flex-wrap:wrap">
          <span class="muted" style="font-size:13px">${tr('preset.intro', '预设保存角色、配件与五个表情槽位；云端预设只在全部满足云端条件时上传。')}</span>
          <span class="spacer"></span>
          <label class="muted" style="display:flex;align-items:center;gap:6px;font-size:12px"><input type="checkbox" id="presetIncludeEmotes"> ${tr('preset.includeEmotes', '保存云端时包含表情')}</label>
          <button class="btn ghost" id="presetRefresh">${tr('common.refresh', '刷新')}</button>
          <button class="btn ghost" id="presetCreateLocal">${tr('preset.saveLocal', '保存为本地外观')}</button>
          <button class="btn" id="presetCreate">${tr('preset.saveCloud', '保存当前为云外观配置')}</button>
        </div>
        <div class="section">
          <div class="section-title">${tr('preset.cloudSection', '云外观配置')}</div>
          <div class="preset-list" id="presetList">${list.length ? list.map((p, i) => presetCardHtml(p, i, false)).join('') : `<div class="empty-state">${tr('preset.noCloud', '还没有云端预设')}</div>`}</div>
        </div>
        <div class="section">
          <div class="section-title">${tr('preset.localSection', '本地外观配置')}</div>
          <div class="preset-list" id="localPresetList">${localList.length ? localList.map((p, i) => presetCardHtml(p, i, true)).join('') : `<div class="empty-state">${tr('preset.noLocal', '还没有本地预设，点击「保存为本地外观」创建')}</div>`}</div>
        </div>
      </div>`);
    document.getElementById('presetRefresh').onclick = async () => { await Promise.all([ensurePresets(true), ensureLocalPresets(true)]); renderPreset(); };
    document.getElementById('presetCreate').onclick = async () => {
      try {
        await tryCall('presets.create', { includeEmotes: document.getElementById('presetIncludeEmotes').checked });
        await ensurePresets(true);
        renderPreset();
        toast(tr('preset.savedCloud', '已保存当前为云外观配置'));
      } catch (e) { toast(e.message || tr('preset.notCloudReady', '当前外观不满足云端条件'), 'err'); }
    };
    document.getElementById('presetCreateLocal').onclick = () => saveLocalPresetDialog();
    document.querySelectorAll('[data-capply]').forEach(b => b.onclick = () => confirmApplyPreset(list[Number(b.dataset.capply)], false));
    document.querySelectorAll('[data-cignore]').forEach(b => b.onclick = () => applyPreset(list[Number(b.dataset.cignore)], false, true));
    document.querySelectorAll('[data-cedit]').forEach(b => b.onclick = () => openPresetEditor(list[Number(b.dataset.cedit)], false));
    document.querySelectorAll('[data-cdel]').forEach(b => b.onclick = async () => {
      const p = list[Number(b.dataset.cdel)];
      const id = p.preset_id ?? p.id;
      if (!id) return toast(tr('preset.missingId', '该预设缺少 id'), 'err');
      try { await tryCall('presets.delete', { id }); await ensurePresets(true); renderPreset(); toast(tr('preset.deleted', '已删除')); } catch { }
    });
    document.querySelectorAll('[data-lapply]').forEach(b => b.onclick = () => confirmApplyPreset(localList[Number(b.dataset.lapply)], true));
    document.querySelectorAll('[data-lignore]').forEach(b => b.onclick = () => applyPreset(localList[Number(b.dataset.lignore)], true, true));
    document.querySelectorAll('[data-ledit]').forEach(b => b.onclick = () => openPresetEditor(localList[Number(b.dataset.ledit)], true));
    document.querySelectorAll('[data-lupload]').forEach(b => b.onclick = async () => {
      const p = localList[Number(b.dataset.lupload)];
      try {
        await tryCall('presets.create', {
          characterAssetId: p.characterAssetId,
          accessoryAssetId: p.accessoryAssetId,
          emoteAssetIds: p.emoteAssetIds,
          includeEmotes: false,
        });
        await ensurePresets(true);
        renderPreset();
        toast(tr('preset.localUploaded', '本地预设已上传为云外观配置'));
      } catch (e) { toast(e.message || tr('preset.localNotCloudReady', '本地预设不满足云端条件'), 'err'); }
    });
    document.querySelectorAll('[data-ldel]').forEach(b => b.onclick = async () => {
      const p = localList[Number(b.dataset.ldel)];
      if (!p.id) return;
      try { await tryCall('presets.local.delete', { id: p.id }); state.localPresets = null; await ensureLocalPresets(true); renderPreset(); toast(tr('preset.localDeleted', '已删除本地预设')); } catch { }
    });
  }

  /* ---------------- 自定义伙伴 ---------------- */
  /** 与 C# CompanionPets.DisplayName 一致：中点前为显示名（菲比·Miratsu → 菲比）。 */
  function companionDisplayName(name) {
    const full = String(name ?? '').trim();
    const idx = full.search(/[·・]/);
    if (idx > 0) {
      const head = full.slice(0, idx).trim();
      if (head) return head;
    }
    return full;
  }

  async function ensureCompanions(force = false) {
    if (Array.isArray(state.companions) && state.companions.length && !force) return state.companions;
    const data = await tryCall('companions.list');
    state.companions = Array.isArray(data) ? data : [];
    return state.companions;
  }

  function companionCardHtml(p, index) {
    const display = p.displayName || companionDisplayName(p.name);
    const name = p.name || display || tr('companion.defaultName', '伙伴');
    const charId = Number(p.characterAssetId || 0);
    const accId = Number(p.accessoryAssetId || 0);
    const seconds = Number(p.clickIntervalSeconds || 0);
    return `<div class="companion-card" data-companion-index="${index}">
      <div class="companion-preview">${presetStageHtml(charId, accId, false)}</div>
      <div class="companion-info">
        <div class="companion-name" title="${esc(display || name)}">${esc(display || name)}</div>
        <div class="detail-row"><span>${tr('companion.character', '角色')}</span><span>${esc(catalogName('character_skin', charId))}</span></div>
        <div class="detail-row"><span>${tr('companion.accessory', '配件')}</span><span>${esc(catalogName('accessory', accId))}</span></div>
        <div class="detail-row"><span>${tr('companion.clickFrequency', '点击频率')}</span><span>${seconds > 0 ? tr('companion.everySeconds', '每 {n} 秒', { n: seconds }) : tr('companion.clickOff', '不自动点击')}</span></div>
        <div class="detail-row"><span>${tr('companion.randomClick', '随机点击')}</span><span>${p.randomClick ? tr('common.on', '开') : tr('common.off', '关')}</span></div>
        <div class="detail-row"><span>${tr('companion.showBar', '显示底栏')}</span><span>${p.showBar ? tr('common.on', '开') : tr('common.off', '关')}</span></div>
        <div class="detail-row"><span>${tr('companion.visibility', '桌面显示')}</span><span>${p.hidden ? tr('companion.hidden', '已隐藏') : tr('companion.visible', '显示中')}</span></div>
      </div>
      <div class="companion-actions">
        <button class="btn ghost" data-companion-visibility="${index}">${p.hidden ? tr('companion.show', '显示') : tr('companion.hide', '隐藏')}</button>
        <button class="btn ghost" data-companion-edit="${index}">${tr('common.edit', '修改')}</button>
        <button class="btn danger" data-companion-delete="${index}">${tr('common.delete', '删除')}</button>
      </div>
    </div>`;
  }

  async function renderCompanion() {
    if (pageAuthBlocked('companion')) return;
    setPage(`<div class="loading">${tr('companion.loading', '正在读取自定义伙伴…')}</div>`);
    try {
      await Promise.all([
        ensureCompanions(),
        ensureCatalog('character_skin').catch(() => []),
        ensureCatalog('accessory').catch(() => []),
      ]);
    } catch {
      setPage(`<div class="empty-state">${tr('companion.loadFailed', '自定义伙伴读取失败')}</div>`);
      return;
    }
    const list = state.companions || [];
    setPage(`
      <div class="companion-page">
        <div class="pill-row" style="margin-bottom:12px;flex-wrap:wrap">
          <span class="muted" style="font-size:13px">${tr('companion.intro', '仅保存在本机的装饰伙伴：可任意选择角色和配件，不会上传服务器，也不影响联机房间、成员或点击统计。')}</span>
          <span class="spacer"></span>
          <button class="btn" id="companionAdd">${tr('companion.add', '添加伙伴')}</button>
        </div>
        <div class="companion-grid">
          ${list.length ? list.map((p, i) => companionCardHtml(p, i)).join('') : `<div class="empty-state">${tr('companion.empty', '还没有自定义伙伴，点击「添加伙伴」创建')}</div>`}
        </div>
      </div>`);
    document.getElementById('companionAdd').onclick = () => openCompanionEditor(null);
    document.querySelectorAll('[data-companion-visibility]').forEach(b => b.onclick = async () => {
      const p = list[Number(b.dataset.companionVisibility)];
      if (!p) return;
      try {
        const next = await tryCall('companions.visibility', { id: p.id, hidden: !p.hidden });
        state.companions = Array.isArray(next) ? next : [];
        renderCompanion();
        toast(p.hidden ? tr('companion.shown', '已显示自定义伙伴') : tr('companion.hiddenToast', '已隐藏自定义伙伴'));
      } catch { }
    });
    document.querySelectorAll('[data-companion-edit]').forEach(b => b.onclick = () => openCompanionEditor(list[Number(b.dataset.companionEdit)]));
    document.querySelectorAll('[data-companion-delete]').forEach(b => b.onclick = () => {
      const p = list[Number(b.dataset.companionDelete)];
      if (!p) return;
      confirmRoomAction(
        tr('companion.deleteTitle', '删除自定义伙伴'),
        tr('companion.deleteBody', '确定要删除「{name}」吗？本机位置记录也会一起清除。', { name: p.displayName || companionDisplayName(p.name) }),
        tr('common.delete', '删除'),
        async () => {
          try {
            const next = await tryCall('companions.delete', { id: p.id });
            state.companions = Array.isArray(next) ? next : [];
            renderCompanion();
            toast(tr('companion.deleted', '已删除自定义伙伴'));
          } catch { }
        });
    });
  }

  function openCompanionEditor(companion) {
    const initial = companion || {
      id: '',
      name: '',
      characterAssetId: state.boot?.appearance?.characterAssetId || 0,
      accessoryAssetId: state.boot?.appearance?.accessoryAssetId || 0,
      clickIntervalSeconds: 0,
      randomClick: false,
      showBar: true,
    };
    // 复用“本地外观”编辑器：隐藏表情页，只保留角色/配件选择 + 伙伴选项。
    openPresetEditor(initial, true, true);
  }

  /* ---------------- 联机 ---------------- */
  /** 房间页渲染签名：只有这些字段变化才值得重建 DOM。 */
  function roomSignature(room) {
    if (!room) return '';
    const ms = (room.members || [])
      .map(m => `${m.id}:${m.nickname || ''}:${m.online ? 1 : 0}:${m.visible ? 1 : 0}:${m.owner ? 1 : 0}:${m.mirrored ? 1 : 0}`)
      .join('|');
    const pub = room.roomIsPublic == null ? 'n' : (room.roomIsPublic ? 1 : 0);
    return `${room.state}|${room.roomNo || ''}|${room.connected ? 1 : 0}|${room.ready ? 1 : 0}|${room.serviceUnavailable ? 1 : 0}|${room.hiddenAll ? 1 : 0}|${room.selfIsOwner ? 1 : 0}|${room.canSetRoomPublic ? 1 : 0}|${pub}|${room.lastError || ''}|${(room.history || []).join(',')}|${ms}`;
  }

  function renderRoom() {
    if (state.page !== 'room') return;
    if (!isLogged()) {
      const host = document.getElementById('pageHost');
      if (host) { host.innerHTML = renderLogin(); bindLogin(); }
      return;
    }
    const roomScroll = {
      page: document.getElementById('pageHost')?.scrollTop || 0,
      left: document.querySelector('.room-left')?.scrollTop || 0,
      members: document.getElementById('memberList')?.scrollTop || 0,
    };
    const room = state.boot?.room || { state: 'idle', members: [], history: [] };
    state.roomSig = roomSignature(room);
    const joined = room.state === 'joined';
    const selfIsOwner = !!room.selfIsOwner || !!(room.members || []).find(m => m.self)?.owner;
    const serviceDown = !!room.serviceUnavailable;
    const auth = state.boot?.auth || {};
    const authPaused = !authOnlineAvailable();
    const authMessage = auth.message || auth.stateMessage || tr('auth.pausedDefault', '账号功能已暂停。');
    const hideOthersHotkey = String(state.boot?.settings?.hotkeys?.hideOtherPets || '').trim();
    const emotes = state.boot?.appearance?.emoteAssetIds || [];
    const blocked = onlineBlockedReason();
    const errorText = authPaused ? '' : (room.lastError || state.roomError?.message || '');
    const hideOthersLabel = hideOthersHotkey
      ? tr('settings.hotkeys.useKey', '使用 {key} 开关', { key: esc(hideOthersHotkey) })
      : tr('settings.hotkeys.notSet', '当前未设置快捷键');
    setPage(`
      <div class="room-layout">
        <div class="room-left">
          ${authPaused ? `
          <div class="panel-box room-error auth-room-paused" style="background:#fff8e8;border:1px solid #e8c98c;color:#7a5a1f;font-size:13px;line-height:1.6">
            <div class="setting-title" style="color:inherit">${tr('room.paused', '联机已暂停')}</div>
            <div class="setting-sub" style="color:inherit">${esc(authMessage)} ${tr('room.pausedNoReconnect', '本程序不会自动重新连接。')}</div>
            <div style="display:flex;gap:8px;margin-top:8px">
              <button class="btn ghost" data-auth-recheck>${tr('auth.recheck', '重新检测')}</button>
              ${authState() === 'conflict' ? `<button class="btn" data-auth-reconnect>${tr('auth.reconnect', '重新连接')}</button>` : ''}
            </div>
          </div>` : ''}
          ${blocked ? `
          <div class="panel-box room-error" style="background:#fff8e8;border:1px solid #e8c98c;color:#7a5a1f;font-size:13px;line-height:1.6">
            ${tr('room.blocked', '联网功能已暂停：{reason}恢复安全状态后才能加入房间、开宝箱、合成或兑换。', { reason: esc(blocked) })}
          </div>` : ''}
          ${errorText ? `
          <div class="panel-box room-error" style="background:#fff0ee;border:1px solid #f2b8ad;color:#9b3b2e;font-size:13px;line-height:1.6">
            <div class="setting-title" style="color:inherit">${tr('room.failedTitle', '房间连接失败')}</div>
            <div class="setting-sub" style="color:inherit">${esc(errorText)}</div>
            <button class="btn ghost" id="roomRetry" style="margin-top:8px">${tr('common.retry', '重试')}</button>
          </div>` : ''}
          ${serviceDown ? `
          <div class="panel-box" style="background:#fff0ee;border:1px solid #f2b8ad;color:#9b3b2e;font-size:13px;line-height:1.6">
            ${tr('room.serviceDown', '联机服务暂不可用：官方联机服务器暂时无法连接，正在自动重试。')}
          </div>` : ''}
          <div class="panel-box">
            <div class="detail-row" style="margin-bottom:6px"><span>${tr('room.current', '当前房间')}</span><span>${serviceDown ? tr('room.serviceUnavailable', '联机服务暂不可用') : (joined ? (room.connected ? tr('room.connected', '已连接') : tr('room.joining', '进入中…（等待房间确认）')) : tr('room.notJoined', '未加入'))}</span></div>
            <div class="room-no">${joined ? esc(room.roomNo) : '——'}<span class="copy" id="copyRoom" ${joined ? '' : 'style="display:none"'}>${tr('room.copy', '复制')}</span></div>
            <div class="detail-row" style="margin-top:8px"><span>${tr('room.availClicks', '可用点击数')}</span><span data-click-avail>${auth.clicks ?? 0}</span></div>
            <div class="detail-row"><span>${tr('room.totalClicks', '累计点击数')}</span><span data-click-total>${auth.clickTotal ?? auth.clicks ?? 0}</span></div>
            ${joined && room.canSetRoomPublic ? `
            <div class="setting-item" style="margin-top:10px;padding:6px 4px;background:transparent">
              <div><div class="setting-title">${tr('room.public.title', '公开房间')}</div><div class="setting-sub">${tr('room.public.sub', '允许其他玩家通过“随机加入房间”匹配到本房间')}</div></div>
              <div class="switch ${room.roomIsPublic ? 'on' : ''}" id="roomPublic"></div>
            </div>` : ''}
            <div style="display:flex;gap:10px;margin-top:14px;flex-wrap:wrap">
              <button class="btn" id="roomCreate" ${joined || authPaused || blocked ? 'disabled' : ''}>${tr('room.create', '创建房间')}</button>
              ${!joined ? `<button class="btn ghost" id="roomRandomJoin" ${authPaused || blocked ? 'disabled' : ''}>${tr('room.randomJoin', '随机加入')}</button>` : ''}
              <button class="btn danger" id="roomLeave" ${!joined || authPaused ? 'disabled' : ''}>${tr('room.leave', '离开房间')}</button>
            </div>
          </div>
          <div class="panel-box">
            <div class="section-title" style="margin-left:0">${joined ? tr('room.switchRoom', '切换房间') : tr('room.joinRoom', '加入房间')}</div>
            <div style="display:flex;gap:10px">
              <input class="input" id="joinNo" placeholder="${tr('room.roomNoPlaceholder', '输入房间号')}" style="flex:1" value="${esc(joined ? '' : (state.boot?.settings?.lastRoom || ''))}" ${authPaused ? 'disabled' : ''} />
              <button class="btn" id="roomJoin" ${authPaused ? 'disabled' : ''}>${joined ? tr('room.switch', '切换') : tr('room.join', '加入')}</button>
            </div>
            ${joined ? `<div class="hint muted" style="font-size:12px;margin-top:8px">${tr('room.switchHint', '切换会先退出当前房间，再从历史或输入框进入新房间。')}</div>` : ''}
          </div>
          <div class="panel-box">
            <div class="section-title" style="margin-left:0">${tr('room.history', '历史房间')}</div>
            ${(room.history || []).length
              ? `<div class="chips">${room.history.slice(0, 20).map(h => `<button class="chip ${h === room.roomNo ? 'active' : ''}" data-hist="${esc(h)}" title="${h === room.roomNo ? tr('room.currentRoom', '当前房间') : tr('room.clickEnter', '点击进入')}">${esc(h)}</button>`).join('')}</div>`
              : `<div class="hint muted" style="font-size:12px">${tr('room.noHistory', '还没有历史记录，加入或创建房间后会自动记录。')}</div>`}
          </div>
          <div class="panel-box">
            <div class="setting-item" style="margin-bottom:0;padding:6px 4px;background:transparent">
              <div><div class="setting-title">${tr('room.hideOthers.title', '隐藏其他成员桌宠')}</div><div class="setting-sub">${tr('room.hideOthers.sub', '只影响本机显示')}<br><span class="hide-others-hotkey-note">${hideOthersLabel}</span></div></div>
              <div class="switch ${state.boot?.settings?.hideOtherPets ? 'on' : ''}" id="hideOthers"></div>
            </div>
          </div>
        </div>
        <div class="room-right">
          <div class="panel-box" style="flex:1;display:flex;flex-direction:column;min-height:0">
            <div style="display:flex;align-items:center;gap:10px">
              <div class="section-title" style="margin-left:0;flex:1">${tr('room.members', '房间成员（{count}）', { count: room.members?.length || 0 })}</div>
              <div class="layout-switch" id="catLayout" style="margin-left:auto" title="${tr('room.layoutTitle', '切换排列方向并重新排序')}">
                <button type="button" class="layout-choice ${(state.boot?.settings?.catLayout || 'horizontal') !== 'vertical' ? 'active' : ''}" data-layout="horizontal">${tr('room.horizontal', '横向排列')}</button>
                <button type="button" class="layout-choice ${(state.boot?.settings?.catLayout || 'horizontal') === 'vertical' ? 'active' : ''}" data-layout="vertical">${tr('room.vertical', '纵向排列')}</button>
              </div>
            </div>
            <div class="member-list" id="memberList">
              ${(room.members || []).map(m => `
                <div class="member ${m.owner ? 'owner' : ''}">
                  <span class="dot ${m.online ? '' : 'off'}"></span>
                  <span class="m-name">${esc(m.nickname || tr('common.defaultNickname', '漂泊者'))}</span>
                  ${m.owner ? `<span class="m-role m-role-name">${tr('room.owner', '房主')}</span>` : ''}
                  ${m.self ? `<span class="m-role m-role-name">${tr('room.me', '我')}</span>` : ''}
                  ${!m.online ? `<span class="m-role m-role-name">${tr('room.offline', '离线')}</span>` : ''}
                  <span class="m-actions">
                    <button class="chip" style="padding:2px 8px" data-mirror="${m.id}" data-mirrored="${m.mirrored ? 1 : 0}" title="${tr('room.mirrorTitle', '水平翻转角色，底栏保持正方向')}">${m.mirrored ? tr('room.unmirror', '取消镜像') : tr('room.mirror', '镜像')}</button>
                    ${selfIsOwner && !m.self ? `
                      <button class="chip" style="padding:2px 8px" data-transfer="${m.id}" ${room.ready ? '' : 'disabled'}>${tr('room.transfer', '转让')}</button>
                      <button class="chip" style="padding:2px 8px" data-kick="${m.id}" ${room.ready ? '' : 'disabled'}>${tr('room.kick', '移出')}</button>` : ''}
                    ${!m.self ? (room.hiddenAll
                      ? `<span class="m-role" title="${tr('room.hiddenTitle', '已开启“隐藏其他成员桌宠”')}">${tr('room.hidden', '已隐藏')}</span>`
                      : `<button class="chip" style="padding:2px 10px" data-hide="${m.id}" data-visible="${m.visible ? 1 : 0}">${m.visible ? tr('room.hide', '隐藏') : tr('room.show', '显示')}</button>`) : ''}
                  </span>
                </div>`).join('') || `<div class="empty-state">${tr('room.noMembers', '还没有加入房间')}</div>`}
            </div>
          </div>
          <div class="panel-box">
            <div style="display:flex;align-items:center">
              <div class="section-title" style="margin-left:0;flex:1">${tr('room.emotes', '快捷表情（仅本地）')}</div>
              <button class="btn ghost" id="editEmotesBtn" style="height:30px;padding:0 12px;font-size:13px">${tr('room.editEmotes', '编辑表情栏')}</button>
            </div>
            <div class="emote-row" id="emoteRow">
              ${[0, 1, 2, 3, 4].map(i => {
                const id = emotes[i];
                const item = emoteInfo(id);
                return `<div class="emote-slot" data-emote="${id || ''}" title="${tr('bag.slotLabel', '槽位 {n}', { n: i + 1 })}">${item ? img(item.thumb) : `<span class="muted">${tr('common.empty', '空')}</span>`}<span class="slot-no">${i + 1}</span></div>`;
              }).join('')}
            </div>
            <div class="hint muted" style="font-size:12px;margin-top:8px">${tr('room.emoteHint', '槽位只保存在本机；发送时服务端不校验归属/角色，可直接发送任意表情。')}</div>
          </div>
        </div>
      </div>`);

    document.getElementById('copyRoom')?.addEventListener('click', () => {
      navigator.clipboard?.writeText(room.roomNo).then(() => toast(tr('room.copied', '房间号已复制'))).catch(() => toast(room.roomNo));
    });
    document.getElementById('roomCreate').onclick = async () => {
      if (authPaused) return toast(authMessage, 'err');
      try {
        const r = await tryCall('room.create');
        state.boot.room = r;
        state.roomError = null;
        renderRoom();
        toast(r?.connected ? tr('room.created', '房间已创建') : tr('room.creating', '正在创建房间，等待房间确认…'));
      } catch (e) { state.roomError = { message: e?.message || tr('room.createFailed', '创建房间失败') }; renderRoom(); }
    };
    document.getElementById('roomRandomJoin')?.addEventListener('click', async () => {
      if (authPaused) return toast(authMessage, 'err');
      try {
        const r = await tryCall('room.randomJoin');
        state.boot.room = r;
        state.roomError = null;
        renderRoom();
        toast(r?.connected ? tr('room.randomJoined', '已加入随机房间') : tr('room.randomJoining', '正在进入随机房间，等待房间确认…'));
      } catch (e) { state.roomError = { message: e?.message || tr('room.randomJoinFailed', '随机加入房间失败') }; renderRoom(); }
    });
    document.getElementById('roomPublic')?.addEventListener('click', async () => {
      if (authPaused) return toast(authMessage, 'err');
      const next = !room.roomIsPublic;
      try {
        const r = await tryCall('room.setPublic', { isPublic: next });
        state.boot.room = r;
        renderRoom();
        toast(next ? tr('room.publicOn', '已设为公开房间') : tr('room.publicOff', '已设为私有房间'));
      } catch (e) { toast(e?.message || tr('room.publicFailed', '修改房间公开状态失败'), 'err'); }
    });
    document.querySelectorAll('[data-kick]').forEach(b => {
      b.onclick = () => {
        const id = Number(b.dataset.kick);
        confirmRoomAction(tr('room.kickTitle', '移出成员'), tr('room.kickBody', '是否确认将该玩家移出房间？'), tr('room.kickConfirm', '移出'), async () => {
          try {
            const r = await tryCall('room.kick', { id });
            state.boot.room = r;
            state.roomError = null;
            renderRoom();
            toast(tr('room.kickSent', '移出请求已发送'));
          } catch (e) { state.roomError = { message: e?.message || tr('room.kickFailed', '移出成员失败') }; renderRoom(); }
        });
      };
    });
    document.querySelectorAll('[data-transfer]').forEach(b => {
      b.onclick = () => {
        const id = Number(b.dataset.transfer);
        confirmRoomAction(tr('room.transferTitle', '转让房主'), tr('room.transferBody', '是否转让房主给该玩家？'), tr('room.transferConfirm', '转让'), async () => {
          try {
            const r = await tryCall('room.transferOwner', { id });
            state.boot.room = r;
            state.roomError = null;
            renderRoom();
            toast(tr('room.transferSent', '转让请求已发送'));
          } catch (e) { state.roomError = { message: e?.message || tr('room.transferFailed', '转让房主失败') }; renderRoom(); }
        });
      };
    });
    const leaveRoom = async (ownerConfirmed = false) => {
      if (authPaused) return toast(authMessage, 'err');
      try {
        const r = await tryCall('room.leave', { ownerConfirmed });
        state.boot.room = r;
        state.roomError = null;
        renderRoom();
        toast(tr('room.left', '已离开房间'));
      } catch { }
    };
    const joinRoom = async (roomNo, ownerConfirmed = false) => {
      if (authPaused) return toast(authMessage, 'err');
      try {
        const r = await tryCall('room.join', { roomNo, ownerConfirmed });
        state.boot.room = r;
        state.roomError = null;
        renderRoom();
        // 只有房间成员快照确认后才算真正加入；否则只提示“进入中”，由 room 事件确认。
        toast(r?.connected ? tr('room.joined', '已加入房间') : tr('room.joiningWait', '正在进入房间，等待房间确认…'));
      } catch (e) {
        state.roomError = { message: e?.message || tr('room.joinFailed', '加入房间失败') };
        renderRoom();
      }
    };
    document.getElementById('roomRetry')?.addEventListener('click', () => {
      const no = room.roomNo || state.boot?.settings?.lastRoom || document.getElementById('joinNo')?.value.trim();
      if (!no) return toast(tr('room.enterNo', '请输入房间号'), 'err');
      joinRoom(no, false);
    });
    document.getElementById('roomLeave').onclick = () => {
      if (selfIsOwner) {
        confirmOwnerRoomAction(tr('room.leaveOwnerConfirm', '当前账号是房主。离开房间可能导致房间解散，其他成员会受到影响。确定要继续吗？'), () => leaveRoom(true));
        return;
      }
      leaveRoom(false);
    };
    document.getElementById('roomJoin').onclick = () => {
      const no = document.getElementById('joinNo').value.trim();
      if (!no) return toast(tr('room.enterNo', '请输入房间号'), 'err');
      if (selfIsOwner && no !== room.roomNo) {
        confirmOwnerRoomAction(tr('room.switchOwnerConfirm', '切换房间会先离开当前房主房间，可能导致房间解散。确定要切换到 {room} 吗？', { room: no }), () => joinRoom(no, true));
        return;
      }
      joinRoom(no, false);
    };
    document.querySelectorAll('[data-hist]').forEach(b => b.onclick = () => {
      const no = b.dataset.hist;
      if (selfIsOwner && no !== room.roomNo) {
        confirmOwnerRoomAction(tr('room.switchOwnerConfirm', '切换房间会先离开当前房主房间，可能导致房间解散。确定要切换到 {room} 吗？', { room: no }), () => joinRoom(no, true));
        return;
      }
      joinRoom(no, false);
    });
    document.querySelectorAll('#catLayout [data-layout]').forEach(btn => {
      btn.onclick = async () => {
        const val = btn.dataset.layout === 'vertical' ? 'vertical' : 'horizontal';
        try {
          await tryCall('settings.set', { catLayout: val });
          if (state.boot?.settings) state.boot.settings.catLayout = val;
          await tryCall('room.resetOrder');
          renderRoom();
          toast(val === 'vertical' ? tr('toast.layoutVertical', '已切换为纵向排列') : tr('toast.layoutHorizontal', '已切换为横向排列'));
        } catch { }
      };
    });
    document.getElementById('hideOthers').onclick = async () => {
      const hide = !state.boot?.settings?.hideOtherPets;
      try {
        const r = await tryCall('room.hideOthers', { hide });
        if (state.boot) state.boot.settings.hideOtherPets = r?.hiddenAll ?? hide;
        state.boot.room = r; renderRoom();
      } catch { }
    };
    document.querySelectorAll('[data-mirror]').forEach(b => {
      b.onclick = async () => {
        const id = Number(b.dataset.mirror);
        const mirrored = b.dataset.mirrored !== '1';
        try {
          const r = await tryCall('room.mirror', { id, mirrored });
          state.boot.room = r;
          renderRoom();
          toast(mirrored ? tr('room.mirrorOn', '已开启镜像') : tr('room.mirrorOff', '已关闭镜像'));
        } catch (e) { toast(e?.message || tr('room.mirrorFailed', '切换镜像失败'), 'err'); }
      };
    });
    document.querySelectorAll('[data-hide]').forEach(b => b.onclick = async () => {
      const id = Number(b.dataset.hide);
      const hide = b.dataset.visible === '1';
      try { const r = await tryCall('room.hideMember', { id, hide }); state.boot.room = r; renderRoom(); } catch { }
    });
    document.getElementById('editEmotesBtn').onclick = () => {
      openEmoteEditor().catch(e => toast(tr('room.editFailed', '打开编辑器失败：{error}', { error: e.message }), 'err'));
    };
    document.querySelectorAll('[data-emote]').forEach(el => el.onclick = async () => {
      if (authPaused) return toast(authMessage, 'err');
      const id = Number(el.dataset.emote);
      if (!id) return toast(tr('room.slotEmpty', '该槽位未装备表情'), 'err');
      try { await tryCall('emote.send', { id }); toast(tr('room.sent', '已发送表情')); } catch { }
    });
    bindAuthActions(document.getElementById('pageHost'));
    if (!authPaused && !state.emoteCatalog)
      ensureEmotes().then(() => { if (state.page === 'room') renderRoom(); }).catch(() => { });
    const pageHost = document.getElementById('pageHost');
    if (pageHost && roomScroll.page > 0) pageHost.scrollTop = roomScroll.page;
    const left = document.querySelector('.room-left');
    if (left && roomScroll.left > 0) left.scrollTop = roomScroll.left;
    const members = document.getElementById('memberList');
    if (members && roomScroll.members > 0) members.scrollTop = roomScroll.members;
  }

  function bindChestButtons() {
    document.querySelectorAll('[data-chest]').forEach(b => b.onclick = async () => {
      try {
        const r = await tryCall('chest.open', { place: b.dataset.chest, clickNum: 1000 });
        const id = Number(r?.asset_id) || 0;
        await refreshAfterAcquire(id);
        let thumb = '', name = tr('room.chestClaimed', '领取成功');
        if (id) {
          await ensureCatalog('character_skin').catch(() => []);
          const found = (state.catalog.character_skin || []).find(v => v.id === id)
            || (state.catalog.accessory || []).find(v => v.id === id)
            || (state.catalog.emote || []).find(v => v.id === id);
          if (found) { thumb = found.thumb; name = found.name; }
        }
        modal(`<div class="reward">
          <div class="rays"></div>
          ${thumb ? img(thumb, 'prize', name) : '<div style="font-size:64px">🎁</div>'}
          <div class="r-name">${esc(name)}</div>
          <div class="r-sub">${tr('room.chestReward', '宝箱奖励')}</div>
          <button class="btn" id="mOk" style="margin-top:8px">${tr('synth.take', '收下')}</button>
        </div>`, host => host.querySelector('#mOk').onclick = closeModal);
      } catch { }
    });
  }

  async function ensureEmotes(force = false) {
    if (state.emoteCatalog && state.emoteCatalog.length && !force) return state.emoteCatalog;
    const r = await tryCall('atlas.list', { type: 'emote' });
    const items = (r.items || []).filter(x => x.mainId > 0);
    if (items.length) state.emoteCatalog = items;
    return items;
  }

  function emoteInfo(id) {
    if (!id) return null;
    const fromCatalog = (state.emoteCatalog || []).find(x => x.mainId === id);
    if (fromCatalog) return fromCatalog;
    const fromBag = (state.bag || []).find(x => x.id === id);
    if (fromBag) return fromBag;
    return null;
  }

  async function openEmoteEditor() {
    let list = [];
    for (let attempt = 0; attempt < 6 && !list.length; attempt++) {
      try { list = await ensureEmotes(attempt > 0); } catch { list = []; }
      if (!list.length) await new Promise(r => setTimeout(r, 700));
    }
    if (!list.length) { toast(tr('room.emoteCatalogNotReady', '表情目录尚未就绪，请稍后再试'), 'err'); return; }
    let ids = [...(state.boot?.appearance?.emoteAssetIds || [])];
    while (ids.length < 5) ids.push(0);
    let slot = 0;

    const byId = new Map(list.map(x => [x.mainId, x]));
    const info = id => byId.get(id) || emoteInfo(id);

    modal(`<div style="display:flex;flex-direction:column;gap:10px">
      <div style="display:flex;align-items:center;gap:10px">
        <div style="font-size:18px;font-weight:800;color:var(--ink-strong)">${tr('room.emoteEditor', '编辑表情栏')}</div>
        <span class="muted" style="font-size:12px">${tr('room.emoteEditorSub', '仅本机 · 可拖拽 / 点选')}</span>
        <span class="spacer"></span>
        <button class="btn ghost" id="eeClose">${tr('common.close', '关闭')}</button>
      </div>
      <div class="emote-row" id="eeSlots" style="align-items:flex-start"></div>
      <div style="display:flex;gap:8px;align-items:center">
        <input class="input" id="eeSearch" placeholder="${tr('room.emoteSearch', '搜索表情 / 角色')}" style="flex:1" />
        <span class="muted" id="eeStatus" style="font-size:12px;white-space:nowrap"></span>
      </div>
      <div class="emote-editor-grid" id="eeGrid"></div>
      <div class="muted" style="font-size:12px">${tr('room.emoteEditorHint', '点一个槽位再点表情即可替换；也可以把表情直接拖到槽位，或把槽位拖到另一个槽位交换。')}</div>
    </div>`, host => {
      const slotsEl = host.querySelector('#eeSlots');
      const gridEl = host.querySelector('#eeGrid');
      const statusEl = host.querySelector('#eeStatus');
      const search = host.querySelector('#eeSearch');

      const paint = () => {
        slotsEl.innerHTML = ids.map((id, i) => {
          const it = info(id);
          return `<div class="emote-slot ${i === slot ? 'selected' : ''}" data-slot="${i}" draggable="${it ? 'true' : 'false'}" title="${tr('bag.slotLabel', '槽位 {n}', { n: i + 1 })}">
            ${it ? img(it.thumb) : `<span class="muted">${tr('common.empty', '空')}</span>`}
            <span class="slot-no">${i + 1}</span>
            ${it ? `<button class="slot-clear" data-clear="${i}" title="${tr('bag.slotClear', '清空')}">×</button>` : ''}
          </div>`;
        }).join('');
        statusEl.textContent = tr('room.currentSlot', '当前槽位 {n}', { n: slot + 1 });
        gridEl.querySelectorAll('.ee-card').forEach(el => el.classList.toggle('in-slot', ids.includes(Number(el.dataset.eid))));

        slotsEl.querySelectorAll('[data-slot]').forEach(el => {
          el.onclick = e => { if (e.target.closest('[data-clear]')) return; slot = Number(el.dataset.slot); paint(); };
          el.ondragstart = e => {
            const i = Number(el.dataset.slot);
            if (!info(ids[i])) { e.preventDefault(); return; }
            e.dataTransfer.setData('text/plain', 'slot:' + i);
            e.dataTransfer.effectAllowed = 'move';
          };
          el.ondragover = e => { e.preventDefault(); el.classList.add('drag-over'); };
          el.ondragleave = () => el.classList.remove('drag-over');
          el.ondrop = async e => {
            e.preventDefault();
            el.classList.remove('drag-over');
            const data = e.dataTransfer.getData('text/plain');
            const target = Number(el.dataset.slot);
            if (data.startsWith('slot:')) {
              const from = Number(data.slice(5));
              if (from === target) return;
              const a = ids[from], b = ids[target];
              await setSlot(from, b); await setSlot(target, a);
            } else if (data) {
              await setSlot(target, Number(data));
            }
          };
        });
        slotsEl.querySelectorAll('[data-clear]').forEach(b => b.onclick = async e => {
          e.stopPropagation();
          await setSlot(Number(b.dataset.clear), 0);
        });
      };

      const setSlot = async (i, id) => {
        try {
          state.boot = await tryCall('appearance.setSlot', { slot: i, id });
          ids[i] = id;
          slot = i;
          paint();
        } catch { }
      };

      const renderGrid = () => {
        const q = search.value.trim().toLowerCase();
        const items = list.filter(x => !q || (x.name || '').toLowerCase().includes(q) || (x.series || '').toLowerCase().includes(q));
        gridEl.innerHTML = items.map(x => `<div class="ee-card" draggable="true" data-eid="${x.mainId}" title="${esc(x.name)}">${img(x.thumb)}<div class="ee-name">${esc(x.name)}</div></div>`).join('');
        gridEl.querySelectorAll('.ee-card').forEach(el => {
          el.onclick = () => setSlot(slot, Number(el.dataset.eid));
          el.ondragstart = e => {
            e.dataTransfer.setData('text/plain', el.dataset.eid);
            e.dataTransfer.effectAllowed = 'copy';
          };
        });
        paint();
      };

      host.querySelector('#eeClose').onclick = () => { closeModal(); if (state.page === 'room') renderRoom(); };
      search.oninput = renderGrid;
      renderGrid();
    });
  }

  function serverNowMs() {
    return Date.now() + (Number(state.boot?.auth?.serverOffsetMs) || 0);
  }

  function chestTimeText(ts) {
    if (!ts) return tr('room.chestNotGenerated', '未生成');
    const left = ts - serverNowMs();
    if (left <= 0) return tr('room.chestAvailable', '可开启');
    const s = Math.ceil(left / 1000), m = Math.floor(s / 60), ss = s % 60;
    return tr('room.chestCountdown', '{minutes}分{seconds}秒后可开启', { minutes: m, seconds: String(ss).padStart(2, '0') });
  }

  function updateChestStatus() {
    const el = document.getElementById('chestStatus');
    if (!el) return;
    const a = state.boot?.auth || {};
    el.textContent = tr('room.chestStatus', '左侧宝箱：{left}　右侧宝箱：{right}', { left: chestTimeText(a.chestLeft), right: chestTimeText(a.chestRight) });
  }

  /* ---------------- 设置 ---------------- */
  function releaseNotesHtml(markdown) {
    const inline = s => esc(s)
      .replace(/`([^`]+)`/g, '<code>$1</code>')
      .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>');
    const lines = String(markdown || '').split(/\r?\n/);
    const out = [];
    let inList = false;
    let skipDownload = false;
    const closeList = () => { if (inList) { out.push('</ul>'); inList = false; } };
    for (const raw of lines) {
      const line = raw.trim();
      if (!line) { closeList(); continue; }
      let m;
      if ((m = line.match(/^#{1,4}\s+(.*)$/))) {
        // 下载说明由横幅底部统一提供，多个版本拼接时不要重复整段。
        if (/下载说明|ダウンロード|download/i.test(m[1])) { skipDownload = true; closeList(); continue; }
        skipDownload = false;
        closeList();
        out.push(`<div class="update-h">${inline(m[1])}</div>`);
      } else if (skipDownload) {
        continue;
      } else if ((m = line.match(/^[-*]\s+(.*)$/))) {
        if (!inList) { out.push('<ul>'); inList = true; }
        out.push(`<li>${inline(m[1])}</li>`);
      } else {
        closeList();
        out.push(`<p>${inline(line)}</p>`);
      }
    }
    closeList();
    return out.join('');
  }

  function compareVersions(left, right) {
    const a = String(left || '').replace(/^v/i, '').split(/[.+-]/).map(x => parseInt(x, 10) || 0);
    const b = String(right || '').replace(/^v/i, '').split(/[.+-]/).map(x => parseInt(x, 10) || 0);
    for (let i = 0; i < Math.max(a.length, b.length); i++) {
      const av = i < a.length ? a[i] : 0;
      const bv = i < b.length ? b[i] : 0;
      if (av !== bv) return av < bv ? -1 : 1;
    }
    return 0;
  }

  /** 强制更新是否属于“仅官方版本前进”的可豁免场景；维护者标记 unsafe/maintenance 时不可绕过。 */
  function forcedOnlineCanOptIn(c = state.compatibility) {
    if (c?.officialCheck?.state !== 'forced') return false;
    const status = String(c?.status || '').toLowerCase();
    return !(c?.killSwitch?.forceOffline || status === 'unsafe' || status === 'maintenance');
  }

  /** 用户是否已对“当前这个”官方强制更新版本确认自担风险联网。 */
  function forcedOnlineAccepted(c = state.compatibility, settings = state.boot?.settings) {
    if (!forcedOnlineCanOptIn(c)) return false;
    const officialVersion = String(c?.officialCheck?.officialVersion || '').trim().toLowerCase();
    const acceptedVersion = String(settings?.forcedOnlineAcceptedVersion || '').trim().toLowerCase();
    return !!officialVersion && officialVersion === acceptedVersion;
  }

  function openForcedOnlineConfirm(oc) {
    if (!forcedOnlineCanOptIn()) {
      toast(tr('forcedOnline.blocked', '当前状态由维护者强制暂停，无法通过风险确认绕过'), 'err');
      return;
    }
    const version = String(oc?.officialVersion || '').trim();
    const tested = String(oc?.testedVersion || '').trim();
    if (!version) {
      toast(tr('forcedOnline.notReady', '官方版本信息尚未就绪，请刷新版本状态后重试'), 'err');
      return;
    }
    modal(`<div class="about">
      <div class="about-title">${tr('forcedOnline.title', '强制更新后继续联网')}</div>
      <div class="about-note about-note--warn"><p>${tr('forcedOnline.body1', '官方客户端已强制更新至 {version}，当前 Native 只验证到 {tested}。', { version: esc(version), tested: esc(tested || tr('forcedOnline.earlier', '更早版本')) })}</p></div>
      <div class="about-note"><p>${tr('forcedOnline.body2', '继续后，房间、宝箱、合成、兑换、外观同步等会按旧协议尝试联网。可能出现请求失败、房间状态异常或账号风险；本程序无法保证与官方新版本兼容。')}</p><p>${tr('forcedOnline.body3', '建议优先更新 Native。只有确认愿意自担风险时才继续。')}</p></div>
      <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px;flex-wrap:wrap">
        <button class="btn ghost" id="forcedOnlineCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn danger" id="forcedOnlineConfirm">${tr('forcedOnline.confirm', '我已了解，仍要继续')}</button>
      </div>
    </div>`, host => {
      host.querySelector('#forcedOnlineCancel').onclick = closeModal;
      const confirm = host.querySelector('#forcedOnlineConfirm');
      confirm.onclick = async () => {
        confirm.disabled = true;
        confirm.textContent = tr('forcedOnline.enabling', '正在开启…');
        try {
          const boot = await call('compatibility.acceptForcedOnline', { version });
          closeModal();
          try {
            localStorage.removeItem(`wn_official_notice_forced_${version}`);
            localStorage.removeItem(`wn_official_notice_forced_v2_${version}`);
            localStorage.removeItem(`wn_official_notice_forced_accepted_${version}`);
          } catch { }
          if (boot && boot.settings) handleEvent('boot', boot);
          else { state.boot = await call('boot'); renderChrome(); }
          toast(tr('forcedOnline.accepted', '已允许在官方强制更新后继续联网（风险自负）'));
        } catch (e) {
          confirm.disabled = false;
          confirm.textContent = tr('forcedOnline.confirm', '我已了解，仍要继续');
          toast(e.message || tr('forcedOnline.enableFailed', '开启失败'), 'err');
        }
      };
    });
  }

  function confirmRestoreForcedOnline() {
    modal(`<div class="about">
      <div class="about-title">${tr('forcedOnline.restoreTitle', '撤销风险授权')}</div>
      <div class="about-note"><p>${tr('forcedOnline.restoreBody', '撤销后将立即恢复官方强制更新期间的联网拦截。正常情况下，授权只会在官方再次更新或维护者标记为不安全/维护中时自动失效；这里只是手动提前撤销。')}</p></div>
      <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px">
        <button class="btn ghost" id="forcedOnlineRestoreCancel">${tr('common.cancel', '取消')}</button>
        <button class="btn danger" id="forcedOnlineRestoreConfirm">${tr('forcedOnline.restoreConfirm', '确认撤销')}</button>
      </div>
    </div>`, host => {
      host.querySelector('#forcedOnlineRestoreCancel').onclick = closeModal;
      const confirm = host.querySelector('#forcedOnlineRestoreConfirm');
      confirm.onclick = async () => {
        confirm.disabled = true;
        try {
          const version = String(state.compatibility?.officialCheck?.officialVersion || '').trim();
          const boot = await call('compatibility.clearForcedOnline');
          closeModal();
          try {
            localStorage.removeItem(`wn_official_notice_forced_${version}`);
            localStorage.removeItem(`wn_official_notice_forced_v2_${version}`);
            localStorage.removeItem(`wn_official_notice_forced_accepted_${version}`);
          } catch { }
          if (boot && boot.settings) handleEvent('boot', boot);
          else { state.boot = await call('boot'); renderChrome(); }
          toast(tr('forcedOnline.restored', '已撤销风险授权，联网限制已恢复'));
        } catch (e) {
          confirm.disabled = false;
          toast(e.message || tr('forcedOnline.restoreFailed', '恢复失败'), 'err');
        }
      };
    });
  }

  /** 登录态异常固定在内容区顶部；桌宠计数器同时显示“未登录”。 */
  function authNoticeHtml() {
    const a = authInfo();
    const s = a.state || (a.loggedIn ? 'valid' : 'loggedOut');
    if (s === 'valid' || s === 'loggedOut') return '';
    const map = {
      checking: { cls: 'warning', title: tr('auth.noticeCheckingTitle', '正在确认登录状态'), text: tr('auth.noticeCheckingText', '账号功能暂时暂停，确认完成后会自动恢复。') },
      conflict: { cls: 'warning', title: tr('auth.noticeConflictTitle', '登录态正在其他客户端使用'), text: tr('auth.noticeConflictText', '为避免影响其他客户端，账号相关功能已暂停；本程序不会自动重新登录或重连。桌宠与本地功能仍可用。') },
      invalid: { cls: 'danger', title: tr('auth.noticeInvalidTitle', '当前登录凭据已失效'), text: tr('auth.noticeInvalidText', '账号相关功能已暂停。请重新登录或重新继承官方登录。') },
      network: { cls: 'warning', title: tr('auth.noticeNetworkTitle', '网络不可用，暂时无法确认登录状态'), text: tr('auth.noticeNetworkText', '账号功能已暂停，网络恢复后可重新检测。') },
    };
    const meta = map[s];
    if (!meta) return '';
    const noticeTitle = s === 'invalid' && a.credentialValid ? tr('auth.noticeRestoredTitle', '登录凭据已恢复有效') : meta.title;
    const noticeText = s === 'invalid' && a.credentialValid
      ? tr('auth.noticeRestoredText', '账号功能仍暂停；点击“重新连接”后再恢复联机。')
      : (s === 'network' && a.stateMessage ? a.stateMessage : meta.text);
    const actions = [];
    if (s !== 'checking') actions.push(`<button class="btn ghost" data-auth-recheck>${tr('auth.recheck', '重新检测')}</button>`);
    if (s === 'conflict') actions.push(`<button class="btn danger" data-auth-reconnect>${tr('auth.reconnect', '重新连接')}</button>`);
    if (s === 'invalid') actions.push(a.credentialValid
      ? `<button class="btn" data-auth-reconnect>${tr('auth.reconnect', '重新连接')}</button>`
      : `<button class="btn" data-auth-login>${tr('auth.loginAgain', '重新登录')}</button>`);
    actions.push(`<button class="btn ghost" data-auth-account>${tr('auth.viewAccount', '查看账号')}</button>`);
    return `<section class="official-notice ${meta.cls} auth-notice">
      <div class="official-notice-main">
        <div class="official-notice-icon">!</div>
        <div class="official-notice-copy">
          <div class="official-notice-title">${esc(noticeTitle)}</div>
          <div class="official-notice-text">${esc(noticeText)}</div>
          <div class="official-notice-meta">${tr('auth.statusLabel', '登录状态：{status}', { status: authStatusText(a) })}${a.lastCheckedAt ? ' · ' + tr('auth.checkedAt', '检测于 {time}', { time: new Date(Number(a.lastCheckedAt)).toLocaleTimeString('zh-CN', { hour12: false }) }) : ''}</div>
        </div>
      </div>
      <div class="official-notice-actions">${actions.join('')}</div>
    </section>`;
  }

  /// 官方版本提醒固定在内容区顶部；公告只在设置页显示，避免打扰其他功能。
  function renderNotices() {
    const host = document.getElementById('noticeHost');
    if (!host) return;

    const oc = state.compatibility?.officialCheck;
    let officialHtml = '';
    let officialKey = '';
    if (oc && (oc.state === 'unverified' || oc.state === 'forced')) {
      const danger = oc.state === 'forced';
      const accepted = danger && forcedOnlineAccepted();
      // 强制更新提示换过语义，使用 v2 key，避免旧版“暂时继续”的关闭记录把新入口一起隐藏。
      officialKey = accepted
        ? `wn_official_notice_forced_accepted_${oc.officialVersion || ''}`
        : (danger
          ? `wn_official_notice_forced_v2_${oc.officialVersion || ''}`
          : `wn_official_notice_unverified_${oc.officialVersion || ''}`);
      let dismissed = false;
      try { dismissed = localStorage.getItem(officialKey) === '1'; } catch { }
      if (!dismissed) {
        if (accepted) {
          officialHtml = `<section class="official-notice">
            <div class="official-notice-main">
              <div class="official-notice-icon">!</div>
              <div class="official-notice-copy">
                <div class="official-notice-title">${tr('official.acceptedTitle', '已允许在官方强制更新后继续联网')}</div>
                <div class="official-notice-text">${tr('official.acceptedText', '当前官方 {version} 超出 Native 验证基线 {tested}。所有联机功能已恢复正常，仅保留此提示；直到官方再次更新、维护者将当前版本标记为不安全/强制暂停，或验证基线更新到当前版本。', { version: esc(oc.officialVersion || tr('official.newVersion', '新版本')), tested: esc(oc.testedVersion || '—') })}</div>
                <div class="official-notice-meta">${tr('official.acceptedMeta', '风险自负 · 仅对官方 {version} 有效', { version: esc(oc.officialVersion || tr('official.currentVersion', '当前版本')) })}</div>
              </div>
              <button class="official-notice-close" data-official-dismiss title="${tr('common.close', '关闭')}">×</button>
            </div>
            <div class="official-notice-actions">
              <a class="btn ghost" href="https://github.com/DanielToyama/WuwaTappoNative/releases/latest" target="_blank" rel="noreferrer">${tr('official.viewStatus', '查看项目状态')}</a>
            </div>
          </section>`;
        } else {
          officialHtml = `<section class="official-notice ${danger ? 'danger' : 'warning'}">
            <div class="official-notice-main">
              <div class="official-notice-icon">${danger ? '!' : '?'}</div>
              <div class="official-notice-copy">
                <div class="official-notice-title">${tr('official.updatedTitle', '官方客户端已{force}更新至 {version}', { force: danger ? tr('official.forced', '强制') : '', version: esc(oc.officialVersion || tr('official.newVersion', '新版本')) })}</div>
                <div class="official-notice-text">${esc(oc.message || '')}</div>
                <div class="official-notice-meta">${tr('official.baseline', '当前 Native 验证基线：{version}', { version: esc(oc.testedVersion || '—') })}${oc.stale ? ' · ' + tr('official.stale', '显示上次检查结果') : ''}</div>
              </div>
              <button class="official-notice-close" data-official-dismiss title="${tr('common.close', '关闭')}">×</button>
            </div>
            <div class="official-notice-actions">
              <a class="btn ghost" href="https://github.com/DanielToyama/WuwaTappoNative/releases/latest" target="_blank" rel="noreferrer">${tr('official.viewStatus', '查看项目状态')}</a>
              ${danger
                ? (forcedOnlineCanOptIn()
                  ? `<button class="btn danger" data-official-allow>${tr('official.allowRisk', '仍要联网（自担风险）')}</button>`
                  : `<span class="muted" style="font-size:12px">${tr('official.blocked', '当前状态由维护者强制暂停，无法本地绕过')}</span>`)
                : `<button class="btn ghost" data-official-dismiss>${tr('official.continue', '继续使用')}</button>`}
            </div>
          </section>`;
        }
      }
    }

    const announcementHtml = state.page === 'settings' ? announcementCardHtml() : '';
    const authHtml = authNoticeHtml();
    host.innerHTML = authHtml + officialHtml + announcementHtml;
    if (officialHtml) {
      host.querySelectorAll('[data-official-dismiss]').forEach(el => {
        el.onclick = () => {
          try { localStorage.setItem(officialKey, '1'); } catch { }
          renderNotices();
        };
      });
      const allow = host.querySelector('[data-official-allow]');
      if (allow) allow.onclick = () => openForcedOnlineConfirm(oc);
    }
    bindAuthActions(host);
    bindAnnouncement(host);
  }

  function announcementCardHtml() {
    const list = Array.isArray(state.announcements) ? state.announcements : [];
    if (!list.length) return '';
    const a = list[0];
    const severity = ['info', 'warning', 'critical'].includes(String(a.severity || '').toLowerCase())
      ? String(a.severity).toLowerCase() : 'info';
    const meta = ({
      info: { label: tr('ann.info', '公告'), icon: 'i' },
      warning: { label: tr('ann.warning', '注意'), icon: '!' },
      critical: { label: tr('ann.critical', '重要'), icon: '!' }
    })[severity];
    const count = list.length > 1 ? `<span class="announcement-count">1 / ${list.length}</span>` : '';
    const dateValue = a.publishedAt ? new Date(a.publishedAt) : null;
    const dateText = dateValue && !Number.isNaN(dateValue.getTime()) ? dateValue.toLocaleDateString('zh-CN') : '';
    const action = a.action && a.action.url
      ? `<a class="btn ghost announcement-action" href="${esc(a.action.url)}" target="_blank" rel="noreferrer" data-ann-action>${esc(a.action.label || tr('ann.viewDetails', '查看详情'))}</a>`
      : '';
    const canClose = a.dismissible !== false;
    const close = canClose
      ? `<button class="announcement-close" data-ann-close title="${tr('common.close', '关闭')}">×</button>`
      : '';
    const confirm = canClose ? '' : `<button class="btn ghost" data-ann-close>${tr('ann.iKnow', '我知道了')}</button>`;
    return `<section class="announcement-card ${severity}" data-ann-id="${esc(a.id)}">
      <div class="announcement-head">
        <span class="announcement-badge"><span>${meta.icon}</span>${meta.label}</span>
        <span class="announcement-title">${esc(a.title || tr('ann.fallbackTitle', '公告'))}</span>
        ${count}
        ${close}
      </div>
      <div class="announcement-body">${esc(a.body || '')}</div>
      <button class="announcement-expand hidden" data-ann-expand>${tr('ann.expand', '展开全文')}</button>
      <div class="announcement-foot">
        <span class="announcement-meta">${dateText ? esc(dateText) + ' · ' : ''}${tr('ann.footer', 'Tappo 公告')}</span>
        <span class="announcement-actions">${action}${confirm}</span>
      </div>
    </section>`;
  }

  function bindAnnouncement(host) {
    const card = host.querySelector('.announcement-card');
    if (!card) return;
    const body = card.querySelector('.announcement-body');
    const expand = card.querySelector('[data-ann-expand]');
    if (body && expand) {
      if (body.scrollHeight > body.clientHeight + 2) expand.classList.remove('hidden');
      expand.onclick = () => {
        const expanded = body.classList.toggle('expanded');
        expand.textContent = expanded ? tr('ann.collapse', '收起') : tr('ann.expand', '展开全文');
      };
    }
    const id = card.dataset.annId || '';
    card.querySelectorAll('[data-ann-close]').forEach(el => {
      el.onclick = () => { void dismissAnnouncement(id); };
    });
    card.querySelectorAll('[data-ann-action]').forEach(el => {
      el.addEventListener('click', () => { void dismissAnnouncement(id); });
    });
  }

  let announcementBusy = false;
  async function dismissAnnouncement(id) {
    if (!id || announcementBusy) return;
    announcementBusy = true;
    try {
      const remaining = await call('announcement.read', { id });
      state.announcements = Array.isArray(remaining)
        ? remaining
        : (state.announcements || []).filter(a => a.id !== id);
      renderNotices();
    } catch { }
    finally { announcementBusy = false; }
  }

  /** 与原生层 CompatibilityGate 保持一致的联网门禁提示；空字符串表示允许联网。 */
  function onlineBlockedReason() {
    const c = state.compatibility;
    if (!c) return '';
    const sentence = (text, fallback) => {
      const value = String(text || '').trim() || fallback;
      return value.replace(/[。.!！?？]+$/g, '');
    };
    const kill = c.killSwitch || {};
    if (kill.forceOffline) {
      return tr('compat.blockedSafeMode', '安全模式已启用：{reason}。', { reason: sentence(kill.reason, tr('compat.maintainerOffline', '维护者要求暂停联网')) });
    }
    const status = String(c.status || '').toLowerCase();
    if (status === 'unsafe') return tr('compat.blockedUnsafe', '当前版本状态为不安全：{reason}。', { reason: sentence(c.message, tr('compat.updateFirst', '请更新后再联网')) });
    if (status === 'maintenance') return tr('compat.blockedMaintenance', '官方服务维护中：{reason}。', { reason: sentence(c.message, tr('compat.tryLater', '请稍后再试')) });
    if (c.officialCheck?.state === 'forced') {
      if (forcedOnlineAccepted(c)) return '';
      return tr('compat.blockedForced', '官方客户端已强制更新，当前 Native 尚未验证：{reason}。', { reason: sentence(c.officialCheck.message, tr('compat.pauseOnline', '建议暂停联机')) });
    }
    return '';
  }

  function compatibilityBannerHtml() {
    const c = state.compatibility;
    if (!c) return '';
    const native = c.native || {};
    const officialCheck = c.officialCheck;
    const currentNative = state.boot?.version || '';
    const belowMinSafe = !!(native.minSafe && currentNative && compareVersions(currentNative, native.minSafe) < 0);
    const forcedAccepted = officialCheck?.state === 'forced' && forcedOnlineAccepted(c);
    let status = String(c.status || 'unknown').toLowerCase();
    if (status === 'safe' && (belowMinSafe || officialCheck?.state === 'unverified' || officialCheck?.state === 'forced'))
      status = officialCheck?.state === 'forced' ? (forcedAccepted ? 'warning' : 'unsafe') : 'warning';
    const meta = ({
      safe: { cls: 'safe', icon: '✓', label: tr('compat.safe', '安全'), title: tr('compat.safeTitle', '当前版本可继续使用') },
      warning: { cls: 'warning', icon: '!', label: tr('compat.warning', '注意'), title: tr('compat.warningTitle', '当前版本需要关注') },
      unsafe: { cls: 'unsafe', icon: '×', label: tr('compat.unsafe', '不安全'), title: tr('compat.unsafeTitle', '当前版本不建议继续联网') },
      maintenance: { cls: 'maintenance', icon: '…', label: tr('compat.maintenance', '维护中'), title: tr('compat.maintenanceTitle', '官方服务可能正在维护') },
      unknown: { cls: 'unknown', icon: '?', label: tr('compat.unknown', '未知'), title: tr('compat.unknownTitle', '暂时无法确认版本状态') }
    })[status] || { cls: 'unknown', icon: '?', label: tr('compat.unknown', '未知'), title: tr('compat.unknownTitle', '暂时无法确认版本状态') };
    const official = c.official || {};
    const cn = official.cn || {};
    const global = official.global || {};
    const globalUsesCn = global.inheritCn === true && !global.latest;
    const globalLatest = global.latest || (globalUsesCn ? cn.latest : '');
    const protocol = c.protocol || {};
    const kill = c.killSwitch || {};
    const force = !!kill.forceOffline;
    const blocked = onlineBlockedReason();
    const updated = c.updatedAt ? new Date(c.updatedAt) : null;
    const updatedText = updated && !Number.isNaN(updated.getTime())
      ? updated.toLocaleString('zh-CN', { hour12: false })
      : '';
    const bits = [];
    if (native.latest) bits.push(tr('compat.latest', '最新 {v}', { v: esc(native.latest) }));
    if (native.minSafe) bits.push(tr('compat.minSafe', '最低兼容 {v}', { v: esc(native.minSafe) }));
    if (cn.latest) bits.push(tr('compat.officialCn', '官方 CN {v}', { v: esc(cn.latest) }));
    if (globalLatest) bits.push(tr('compat.officialGlobal', '官方 Global {v}', { v: esc(globalLatest) }) + (globalUsesCn ? tr('compat.usesCn', '（沿用 CN）') : ''));
    if (protocol.testedOfficial) bits.push(tr('compat.baseline', '验证基线 {v}', { v: esc(protocol.testedOfficial) }));
    if (officialCheck?.officialVersion) {
      const label = ({
        verified: tr('compat.verified', '已验证'),
        unverified: tr('compat.unverified', '未验证'),
        forced: tr('compat.forced', '强制更新'),
        unavailable: tr('compat.checkFailed', '检查失败')
      })[officialCheck.state] || tr('compat.unknown', '未知');
      bits.push(tr('compat.officialCheck', '官方检查 {v}（{label}）', { v: esc(officialCheck.officialVersion), label }));
    }
    if (updatedText) bits.push(tr('compat.updatedAt', '更新于 {time}', { time: esc(updatedText) }));
    return `<section class="compat-banner ${meta.cls}">
      <div class="compat-head">
        <span class="compat-badge"><span>${meta.icon}</span>${meta.label}</span>
        <span class="compat-title">${tr('compat.title', '版本状态：{title}', { title: meta.title })}</span>
        <a class="btn ghost compat-link" href="https://code.danieluu.top/WuwaTappoNative/" target="_blank" rel="noreferrer">${tr('compat.statusPage', '状态页')}</a>
      </div>
      <div class="compat-sub">${esc(c.message || tr('compat.noMessage', '暂无说明'))}</div>
      <div class="compat-meta">${bits.join(' · ')}</div>
      ${belowMinSafe ? `<div class="compat-force">${tr('compat.belowMin', '当前 Native {current} 低于最低兼容版本 {min}，不保证兼容当前官方服务，请更新后再使用联机。', { current: esc(currentNative), min: esc(native.minSafe) })}</div>` : ''}
      ${force ? `<div class="compat-force">${tr('compat.safeMode', '安全模式：{reason}', { reason: esc(kill.reason || tr('compat.maintainerOffline', '维护者要求暂停联网')) })}</div>` : ''}
      ${blocked ? `<div class="compat-force">${tr('compat.blocked', '已暂停联网操作：{reason}房间、宝箱、合成、兑换与外观同步会直接拒绝，直到状态恢复安全。', { reason: esc(blocked) })}</div>` : ''}
      ${forcedAccepted ? `<div class="compat-risk-note">${tr('compat.riskAccepted', '已按你的选择允许联网（风险自负）：官方 {version} 尚未验证，出现异常请恢复限制并更新 Native。', { version: esc(officialCheck.officialVersion || tr('update.newVersionShort', '新版本')) })}</div>` : ''}
      ${protocol.breaking ? `<div class="compat-force">${tr('compat.breaking', '检测到破坏性协议变更，请尽快更新。')}</div>` : ''}
    </section>`;
  }

  /** 更新横幅的日期跟随当前界面语言，不要固定使用中文格式。 */
  function updateDateLocale() {
    const raw = String(window.I18N?.locale || state.boot?.settings?.locale || 'zh').toLowerCase();
    if (raw.startsWith('en')) return 'en-US';
    if (raw.startsWith('ja') || raw.startsWith('jp')) return 'ja-JP';
    return 'zh-CN';
  }

  function formatUpdateBytes(value) {
    const bytes = Number(value) || 0;
    if (bytes >= 1024 * 1024 * 1024) return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
    if (bytes >= 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
    if (bytes >= 1024) return (bytes / 1024).toFixed(0) + ' KB';
    return bytes > 0 ? bytes + ' B' : tr('update.unknownSize', '未知大小');
  }

  function formatUpdateSpeed(value) {
    const bytes = Number(value) || 0;
    if (bytes >= 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + ' MB/s';
    if (bytes >= 1024) return (bytes / 1024).toFixed(1) + ' KB/s';
    return bytes.toFixed(0) + ' B/s';
  }

  function updateDownloadStatusUi() {
    const d = state.updateDownload;
    if (!d) return;
    const status = document.getElementById('updateDownloadStatus');
    if (status) {
      const progress = d.total > 0 ? ` · ${formatUpdateBytes(d.downloaded)} / ${formatUpdateBytes(d.total)}` : '';
      const speed = d.phase === 'downloading' && d.speed > 0 ? ` · ${formatUpdateSpeed(d.speed)}` : '';
      const source = d.source ? ` · ${esc(d.source)}` : '';
      status.textContent = (d.message || tr('update.downloading', '正在下载更新…')) + progress + speed + source;
      status.classList.toggle('error', d.phase === 'error');
    }
    const bar = document.getElementById('updateProgressBar');
    if (bar && d.total > 0) bar.style.width = Math.max(0, Math.min(100, Math.round(d.downloaded / d.total * 100))) + '%';
    const progressHost = document.getElementById('updateProgress');
    if (progressHost) progressHost.style.display = ['starting', 'downloading', 'verifying', 'fallback'].includes(d.phase) ? '' : 'none';
  }

  function updateBannerHtml() {
    const u = state.update;
    if (!u || state.updateDismissed) return '';
    // 兼容旧序列化：0.0.25 曾把 ReleaseInfo 的 PascalCase 属性直接推给前端，导致标题/内容为空。
    const tag = u.tag || u.Tag || u.name || u.Name || tr('update.newVersionShort', '新版本');
    const displayVersion = u.displayVersion || u.DisplayVersion || tag;
    const body = u.body || u.Body || '';
    const dateValue = u.publishedAt || u.PublishedAt;
    const date = dateValue ? new Date(dateValue).toLocaleDateString(updateDateLocale()) : '';
    const entries = Array.isArray(u.entries) ? u.entries : (Array.isArray(u.Entries) ? u.Entries : []);
    const multi = entries.length > 1;
    const installer = u.installer || u.Installer;
    const download = state.updateDownload && (!state.updateDownload.tag || state.updateDownload.tag === tag)
      ? state.updateDownload : null;
    const phase = download?.phase || '';
    const running = ['starting', 'downloading', 'verifying', 'fallback'].includes(phase);
    const canInstall = phase === 'done' && !!download?.path;
    const emptyNotes = `<p class="update-empty">${tr('update.noNotes', '此版本暂无当前语言的更新说明')}</p>`;
    const notes = multi
      ? entries.map(e => {
          const etag = e.tag || e.Tag || '';
          const edisplay = e.displayVersion || e.DisplayVersion || etag;
          const edate = e.publishedAt || e.PublishedAt;
          const edateText = edate ? new Date(edate).toLocaleDateString(updateDateLocale()) : '';
          const entryNotes = releaseNotesHtml(e.body || e.Body || '') || emptyNotes;
          return `<section class="update-entry">
            <div class="update-entry-head"><span>${esc(edisplay)}</span>${edateText ? `<span class="update-entry-date">${esc(edateText)}</span>` : ''}</div>
            ${entryNotes}
          </section>`;
        }).join('')
      : (releaseNotesHtml(body) || emptyNotes);
    const status = download?.message || (installer
      ? tr('update.installerSize', '安装包 {size}；下载后会自动校验 SHA-256', { size: formatUpdateBytes(installer.size) })
      : tr('update.noInstaller', '此版本没有可自动下载的安装包，请手动前往 Release 下载'));
    const meta = download?.source ? ' · ' + download.source : '';
    const downloadButton = !installer ? '' : canInstall
      ? `<button class="btn" id="updateInstallBtn">${tr('update.installNow', '立即安装')}</button>`
      : running
        ? `<button class="btn" id="updateDownloadBtn" disabled>${tr('update.downloadingBtn', '下载中…')}</button><button class="btn ghost" id="updateCancelBtn">${tr('common.cancel', '取消')}</button>`
        : `<button class="btn" id="updateDownloadBtn">${phase === 'error' ? tr('update.retryDownload', '重试下载') : tr('update.download', '下载更新')}</button>`;
    const progress = download && download.total > 0
      ? Math.max(0, Math.min(100, Math.round(download.downloaded / download.total * 100)))
      : 0;
    return `<section class="update-banner">
      <div class="update-banner-head">
        <div>
          <div class="update-banner-title">${tr('update.newVersion', '发现新版本 {version}', { version: esc(displayVersion) })}</div>
          <div class="update-banner-sub">${tr('update.currentVersion', '当前版本 {version}', { version: esc(state.boot?.displayVersion || state.boot?.version || '') })}${date ? ' · ' + esc(date) : ''}${multi ? ' · ' + tr('update.includesVersions', '包含 {count} 个版本更新', { count: entries.length }) : ''}</div>
        </div>
        ${running || canInstall ? '' : `<button class="update-dismiss" id="updateDismiss" title="${tr('update.dismissTitle', '本次启动不再显示')}">×</button>`}
      </div>
      <div class="update-notes">${notes}</div>
      <div class="update-banner-actions">
        ${downloadButton}
        <a class="btn ghost" href="${esc(u.url || u.Url || 'https://github.com/DanielToyama/WuwaTappoNative/releases/latest')}" target="_blank" rel="noreferrer">${tr('update.manualDownload', '手动下载')}</a>
        <span class="update-qq">${tr('update.qqGroup', '交流 QQ 群：<strong>1130030892</strong>，群文件也可下载')}</span>
      </div>
      <div class="update-download-status ${phase === 'error' ? 'error' : ''}" id="updateDownloadStatus">${esc(status + meta)}</div>
      <div class="update-progress" id="updateProgress" style="${running || canInstall ? '' : 'display:none'}">
        <div class="update-progress-bar" id="updateProgressBar" style="width:${canInstall ? 100 : progress}%"></div>
      </div>
    </section>`;
  }

  async function loadAutoEmoteOptions() {
    const sel = document.getElementById('autoEmoteId');
    if (!sel) return;
    try {
      const list = await ensureEmotes();
      if (!sel.isConnected) return;
      const current = String(state.boot?.settings?.autoEmoteId || 0);
      const options = [...(list || [])]
        .filter(x => Number(x.mainId) > 0)
        .sort((a, b) => String(a.name || '').localeCompare(String(b.name || ''), 'zh'));
      sel.innerHTML = '<option value="0">请选择表情</option>' +
        options.map(x => `<option value="${x.mainId}">${esc(x.name || x.mainId)}</option>`).join('');
      sel.value = current;
      if (sel.value !== current) sel.value = '0';
    } catch { }
  }

  const SETTINGS_OPEN_GROUPS_KEY = 'wn_settings_open_groups_v1';
  function loadSettingsOpenGroups() {
    try {
      const arr = JSON.parse(localStorage.getItem(SETTINGS_OPEN_GROUPS_KEY) || '[]');
      state.settingsOpenGroups = new Set(Array.isArray(arr) ? arr.filter(x => typeof x === 'string') : []);
    } catch { state.settingsOpenGroups = new Set(); }
  }
  function saveSettingsOpenGroups() {
    try { localStorage.setItem(SETTINGS_OPEN_GROUPS_KEY, JSON.stringify([...state.settingsOpenGroups])); } catch { }
  }

  /** 记录设置页当前滚动、聚焦输入和用户尚未提交的编辑值；boot 重推后原样恢复。 */
  function captureSettingsViewState() {
    const scroller = document.querySelector('.settings-scroll');
    if (!scroller) return null;
    const dirty = {};
    scroller.querySelectorAll('input[id],select[id],textarea[id]').forEach(el => {
      if (el.type === 'file' || el.dataset.userDirty !== '1') return;
      dirty[el.id] = { value: el.value, checked: !!el.checked, type: el.type };
    });
    const active = document.activeElement;
    const focus = active && scroller.contains(active) && active.id
      ? { id: active.id, start: active.selectionStart, end: active.selectionEnd }
      : null;
    return { scrollTop: scroller.scrollTop, dirty, focus };
  }
  function restoreSettingsViewState(view) {
    const scroller = document.querySelector('.settings-scroll');
    if (!scroller || !view) return;
    for (const [id, stored] of Object.entries(view.dirty || {})) {
      const el = document.getElementById(id);
      if (!el || !(el instanceof HTMLInputElement || el instanceof HTMLSelectElement || el instanceof HTMLTextAreaElement)) continue;
      if (stored.type === 'checkbox') el.checked = stored.checked;
      else el.value = stored.value;
      el.dataset.userDirty = '1';
    }
    if (view.scrollTop > 0) scroller.scrollTop = view.scrollTop;
    if (view.focus) {
      const el = document.getElementById(view.focus.id);
      if (el && typeof el.focus === 'function') {
        el.focus({ preventScroll: true });
        try {
          if (typeof view.focus.start === 'number' && typeof el.setSelectionRange === 'function')
            el.setSelectionRange(view.focus.start, view.focus.end ?? view.focus.start);
        } catch { }
      }
    }
  }
  function settingsItemText(el) {
    return String(el?.textContent || '').replace(/\s+/g, ' ').trim().toLowerCase();
  }
  /** 设置页搜索：按 .setting-item 文本过滤，命中组自动展开；清空后恢复用户保存的展开状态。 */
  function filterSettings(query) {
    const scroller = document.querySelector('.settings-scroll');
    if (!scroller) return;
    const q = String(query || '').trim().toLowerCase();
    const groups = [...scroller.querySelectorAll('.settings-group')];
    const empty = document.getElementById('settingsSearchEmpty');
    const countHost = document.getElementById('settingsSearchCount');
    if (!q) {
      for (const group of groups) {
        group.classList.remove('settings-search-hidden');
        group.querySelectorAll('.setting-item').forEach(item => item.classList.remove('settings-search-hidden'));
        group._searchAuto = true;
        group.open = state.settingsOpenGroups.has(group.dataset.groupId);
        setTimeout(() => { group._searchAuto = false; }, 60);
      }
      if (empty) empty.classList.add('hidden');
      if (countHost) countHost.textContent = '';
      return;
    }
    let total = 0;
    for (const group of groups) {
      let matches = 0;
      group.querySelectorAll('.setting-item').forEach(item => {
        const hit = settingsItemText(item).includes(q);
        item.classList.toggle('settings-search-hidden', !hit);
        if (hit) matches++;
      });
      group.classList.toggle('settings-search-hidden', matches === 0);
      if (matches > 0) {
        total += matches;
        if (!group.open) {
          group._searchAuto = true;
          group.open = true;
          setTimeout(() => { group._searchAuto = false; }, 60);
        }
      }
    }
    if (empty) empty.classList.toggle('hidden', total > 0);
    if (countHost) countHost.textContent = total > 0 ? tr('settings.searchCount', '匹配 {count} 项', { count: total }) : tr('settings.searchNoMatch', '无匹配');
  }

  function createSettingsGroup(id, title, subtitle, primary = false) {
    const node = document.createElement(primary ? 'section' : 'details');
    node.dataset.groupId = id;
    node.className = 'settings-group' + (primary ? ' settings-group-primary' : '');
    node.id = `settings-group-${id}`;
    const heading = `<span class="settings-group-heading"><span class="settings-group-title">${esc(title)}</span><span class="settings-group-sub">${esc(subtitle)}</span></span>`;
    if (primary) {
      node.innerHTML = `<div class="settings-group-head">${heading}</div><div class="settings-group-body"></div>`;
    } else {
      node.innerHTML = `<summary>${heading}</summary><div class="settings-group-body"></div>`;
      node.open = state.settingsOpenGroups.has(id);
      node.addEventListener('toggle', () => {
        if (node._searchAuto) return;
        if (node.open) state.settingsOpenGroups.add(id);
        else state.settingsOpenGroups.delete(id);
        saveSettingsOpenGroups();
      });
    }
    return node;
  }

  /// 把现有设置区按信息架构重新分组；只移动节点，不改变任何控件和事件。
  function groupSettingsDom() {
    const scroller = document.querySelector('.settings-scroll');
    if (!scroller) return;
    const sections = [...scroller.querySelectorAll(':scope > .section')];
    const byId = new Map();
    for (const section of sections) {
      const id = section.dataset.sectionId;
      if (id) byId.set(id, section);
    }
    const makeGroup = (id, title, subtitle, sectionIds) => {
      const group = createSettingsGroup(id, title, subtitle);
      const body = group.querySelector('.settings-group-body');
      for (const sectionId of sectionIds) {
        const section = byId.get(sectionId);
        if (section) body.appendChild(section);
      }
      return body.children.length ? group : null;
    };

    const groups = [];
    const basic = createSettingsGroup('basic', tr('settings.group.basic', '基础辅助'), tr('settings.group.basicSub', '点击、自动补贴与自动开箱'), true);
    const basicBody = basic.querySelector('.settings-group-body');
    const clickRows = [...scroller.querySelectorAll('.detail-row[data-click-row]')];
    if (clickRows.length) {
      const clickPanel = document.createElement('div');
      clickPanel.className = 'panel-box settings-basic-clicks';
      clickRows.forEach(row => clickPanel.appendChild(row));
      basicBody.appendChild(clickPanel);
    }
    const autoBoost = scroller.querySelector('[data-switch="autoClickBoost"]')?.closest('.setting-item');
    const autoChest = scroller.querySelector('[data-switch="autoChest"]')?.closest('.setting-item');
    const autoSynth = scroller.querySelector('[data-switch="autoSynthUp"]')?.closest('.setting-item');
    if (autoBoost) {
      autoBoost.removeAttribute('style');
      basicBody.appendChild(autoBoost);
    }
    if (autoChest) basicBody.appendChild(autoChest);
    if (autoSynth) basicBody.appendChild(autoSynth);
    if (basicBody.children.length) groups.push(basic);

    // 游戏模式规则属于桌面/穿透行为的补充设置，放在游戏模式开关正下方。
    const gameModeItem = scroller.querySelector('[data-switch="clickThrough"]')?.closest('.setting-item');
    const gameRuleItem = scroller.querySelector('#gameRuleInput')?.closest('.setting-item');
    if (gameModeItem && gameRuleItem) gameModeItem.insertAdjacentElement('afterend', gameRuleItem);

    groups.push(
      makeGroup('account', tr('settings.group.account', '账号与兑换'), tr('settings.group.accountSub', '区服、账号、点击与兑换码'), ['account', 'redeem']),
      makeGroup('input', tr('settings.group.input', '输入与录制'), tr('settings.group.inputSub', '桌面键盘与快捷键'), ['keyboard', 'hotkeys']),
      makeGroup('window', tr('settings.group.window', '桌宠与窗口'), tr('settings.group.windowSub', '大小、屏幕、不透明度与头饰微调'), ['window', 'accessory']),
      makeGroup('obs', tr('settings.group.obs', 'OBS 采集'), tr('settings.group.obsSub', '采集窗口、背景与兼容模式'), ['obs']),
      makeGroup('theme', tr('settings.group.theme', '主题与外观'), tr('settings.group.themeSub', '主题色、背景图与主题预设'), ['theme']),
      makeGroup('interaction', tr('settings.group.interaction', '联机与互动'), tr('settings.group.interactionSub', '联机桌宠、宝箱、自动化与通知'), ['room', 'auto', 'notify']),
      makeGroup('maintenance', tr('settings.group.maintenance', '程序与更新'), tr('settings.group.maintenanceSub', '启动、权限、规则、更新与日志'), ['other', 'update'])
    );

    const anchor = scroller.querySelector('.compat-banner') || scroller.querySelector('.settings-footer');
    const fragment = document.createDocumentFragment();
    for (const group of groups.filter(Boolean)) fragment.appendChild(group);
    scroller.insertBefore(fragment, anchor || null);
  }

  function themePresetPreviewHtml(preset, activeId) {
    if (!preset) return `<div class="muted" style="font-size:12px;padding:8px 4px">${tr('theme.previewHint', '点击或悬停预设，查看完整配色、底栏与背景图预览。')}</div>`;
    const active = String(activeId || '') === String(preset.id);
    const bg = preset.backgroundImageSet && preset.backgroundImage
      ? `background-image:${cssBackgroundUrl(preset.backgroundImage)};background-size:cover;background-position:center`
      : `background:${normalizeHexColor(preset.accentColor, '#e7b77c')}`;
    const swatches = [
      [tr('theme.accent', '主题色'), preset.accentColor],
      [tr('theme.font', '字体'), preset.fontColor],
      [tr('theme.bar', '底栏'), preset.barFollowStandard ? tr('theme.followTheme', '跟随主题') : (preset.barColor || tr('theme.autoDerived', '自动推导'))],
      [tr('theme.barText', '底栏文字'), preset.barTextFollowTheme ? tr('theme.followTheme', '跟随主题') : (preset.barTextColor || tr('theme.autoDerived', '自动推导'))],
    ].map(([label, value]) => {
      const solid = /^#[0-9a-f]{6}$/i.test(String(value || ''));
      return `<span class="theme-preview-chip">${solid ? `<i style="background:${value}"></i>` : ''}${esc(label)}：${esc(value)}</span>`;
    }).join('');
    return `<div class="theme-preset-preview-card">
      <div class="theme-preset-preview-bg" style='${bg}'></div>
      <div class="theme-preset-preview-body">
        <div class="theme-preset-preview-name">${esc(preset.name)}${active ? ' · ' + tr('theme.current', '当前') : ''}</div>
        <div class="theme-preset-preview-chips">${swatches}</div>
        <div class="theme-preset-preview-meta">${tr('theme.bg', '背景：')}${preset.backgroundImageSet ? tr('theme.independentImage', '独立图片') : tr('theme.pureColor', '纯配色')} · ${tr('theme.opacity', '透明度 {pct}%', { pct: Math.round((Number(preset.backgroundOpacity ?? 0.25)) * 100) })} · ${tr('theme.blur', '模糊 {px}px', { px: Number(preset.backgroundBlur) || 0 })}</div>
        <div class="theme-preset-preview-meta">${tr('theme.bar', '底栏')}：${preset.barFollowStandard ? tr('theme.barFollowStandard', '跟随标准主题') : tr('theme.barIndependent', '独立配色')}${preset.barTextFollowTheme ? ' · ' + tr('theme.textFollowTheme', '文字跟随主题') : ' · ' + tr('theme.textIndependent', '文字独立')}</div>
      </div>
    </div>`;
  }

  const CHEST_NOTIFY_RARITIES = ['common', 'uncommon', 'rare', 'epic'];
  const CHEST_NOTIFY_RARITY_EMOJI = { common: '🟢', uncommon: '🔵', rare: '🟣', epic: '🟡' };
  const CHEST_NOTIFY_CATEGORIES = ['character', 'accessory', 'emote'];
  function chestNotifyRuleOf(settings, rarity) {
    const rule = settings?.chestNotifyRules?.[rarity] || {};
    return {
      enabled: rule.enabled === true,
      character: rule.character !== false,
      accessory: rule.accessory !== false,
      emote: rule.emote !== false,
    };
  }
  function chestNotifyMatrixHtml(settings) {
    const rows = CHEST_NOTIFY_RARITIES.map(rarity => {
      const rule = chestNotifyRuleOf(settings, rarity);
      return `<tr data-chest-rarity="${rarity}">
        <td><span class="chest-notify-rarity">${CHEST_NOTIFY_RARITY_EMOJI[rarity]} ${rarityText(rarity)}</span></td>
        <td><input type="checkbox" data-chest-notify="enabled" ${rule.enabled ? 'checked' : ''}></td>
        ${CHEST_NOTIFY_CATEGORIES.map(category => `<td><input type="checkbox" data-chest-notify="${category}" ${rule[category] ? 'checked' : ''}></td>`).join('')}
      </tr>`;
    }).join('');
    return `<table class="chest-notify-matrix">
      <thead><tr><th>${tr('settings.notify.matrix.rarity', '稀有度')}</th><th>${tr('settings.notify.matrix.notify', '通知')}</th><th>${tr('type.character_skin', '角色')}</th><th>${tr('type.accessory', '配件')}</th><th>${tr('type.emote', '表情')}</th></tr></thead>
      <tbody>${rows}</tbody>
    </table>`;
  }

  function renderSettings() {
    const viewState = captureSettingsViewState();
    const s = state.boot?.settings || {};
    const currentLocale = (window.I18N?.locale) || s.locale || 'zh';
    applyTheme(s);
    const auth = state.boot?.auth || {};
    const currentAuthState = auth.state || (auth.loggedIn ? 'valid' : 'loggedOut');
    const authStatus = authStatusText(auth);
    const acc = state.boot?.appearance || {};
    const accId = Number(acc.accessoryAssetId || 0);
    const rhythmCount = Math.max(4, Math.min(6, Number(s.rhythmKeyCount) || 4));
    const rhythmDefaults = { 4: ['D', 'F', 'J', 'K'], 5: ['D', 'F', 'J', 'K', 'L'], 6: ['S', 'D', 'F', 'J', 'K', 'L'] };
    const rhythmKeys = Array.isArray(s.rhythmKeys) ? s.rhythmKeys : rhythmDefaults[rhythmCount];
    const rhythmDisplay = (key) => key === 'SPACE' ? '␣' : (key || '');
    const scalePct = Math.max(25, Math.min(200, Math.round((Number(s.scale) || 1) * 100)));
    const scalePreset = [25, 50, 75, 100, 125, 150, 200].includes(scalePct);
    const remoteFollowMain = s.remoteFollowMain !== false;
    const remoteCustomPct = Math.max(25, Math.min(200, Math.round((Number(s.remoteScale) || 1) * 100)));
    const remoteScalePct = remoteFollowMain ? scalePct : remoteCustomPct;
    const remoteScalePreset = [25, 50, 75, 100, 125, 150, 200].includes(remoteScalePct);
    const opacityPct = Math.max(20, Math.min(100, Math.round((Number(s.opacity) || 1) * 100)));
    const remoteFollowMainOpacity = s.remoteFollowMainOpacity !== false;
    const remoteCustomOpacityPct = Math.max(20, Math.min(100, Math.round((Number(s.remoteOpacity) || 1) * 100)));
    const remoteOpacityPct = remoteFollowMainOpacity ? opacityPct : remoteCustomOpacityPct;
    const remoteNameScalePct = Math.max(50, Math.min(200, Math.round((Number(s.remoteNameScale) || 1) * 100)));
    const hideOthersHotkey = String(s.hotkeys?.hideOtherPets || '').trim();
    const accX = Number(acc.accessoryOffsetX || 0);
    const accY = Number(acc.accessoryOffsetY || 0);
    const accScale = normalizeAccessoryScale(acc.accessoryScalePercent);
    const accRotation = normalizeAccessoryRotation(acc.accessoryRotationDegrees);
    const screens = Array.isArray(state.boot?.screens) ? state.boot.screens : [];
    const selectedScreen = s.petScreen || screens.find(x => x.primary)?.deviceName || '';
    const screenSwitchMode = s.screenSwitchMode || 'default';
    const forcedCheck = state.compatibility?.officialCheck;
    const forcedCanOptIn = forcedOnlineCanOptIn();
    const forcedAccepted = forcedCheck?.state === 'forced' && forcedOnlineAccepted();
    const forcedOnlineRow = forcedCheck?.state === 'forced' ? `
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.forcedOnline.title', '官方强制更新后联网')}</div><div class="setting-sub">${forcedCanOptIn
              ? (forcedAccepted
                ? tr('settings.forcedOnline.allowed', '当前官方 {version} 已允许按旧协议联网，所有功能正常运行。此授权仅在官方再次更新、维护者标记不安全/维护中，或验证基线更新到当前版本时自动失效；也可以在这里手动撤销。', { version: esc(forcedCheck.officialVersion || tr('update.newVersionShort', '新版本')) })
                : tr('settings.forcedOnline.paused', '当前官方 {version} 超出 Native 验证基线 {tested}。默认暂停联机；开启后按旧协议继续，可能出现功能异常或账号风险。', { version: esc(forcedCheck.officialVersion || tr('update.newVersionShort', '新版本')), tested: esc(forcedCheck.testedVersion || '—') }))
              : tr('settings.forcedOnline.blocked', '当前版本被维护者标记为不安全或维护中，无法通过风险确认绕过联网限制。')}</div></div>
            <div class="setting-content">
              <span class="muted" style="font-size:12px">${forcedAccepted ? tr('settings.forcedOnline.stateAllowed', '已允许（风险自负）') : (forcedCanOptIn ? tr('settings.forcedOnline.statePaused', '已暂停') : tr('settings.forcedOnline.stateBlocked', '不可绕过'))}</span>
              ${forcedCanOptIn ? `<button class="btn ghost" id="forcedOnlineSettingBtn">${forcedAccepted ? tr('settings.forcedOnline.restore', '撤销风险授权') : tr('settings.forcedOnline.continue', '仍要联网…')}</button>` : ''}
            </div>
          </div>` : '';
    const themePalette = buildThemePalette(s);
    const themeAccent = themePalette.primary;
    const themeFont = themePalette.ink;
    const themeBgSet = themeBackgroundIsSet(s);
    const themeAutoPalette = autoImagePaletteEnabled();
    const themeBgOpacityPct = Math.max(0, Math.min(100, Math.round((Number(s.themeBackgroundOpacity ?? 0.25)) * 100)));
    const themeBgBlur = Math.max(0, Math.min(24, Math.round(Number(s.themeBackgroundBlur) || 0)));
    const themeBarFollow = s.themeBarFollowStandard !== false;
    const themeBarColorValue = normalizeHexColor(
      themeBarFollow ? s.themeBarEffectiveColor : (s.themeBarColor || s.themeBarEffectiveColor),
      '#f5e4cc');
    const themeBarTextFollow = s.themeBarTextFollowTheme !== false;
    const themeBarTextColorValue = normalizeHexColor(
      s.themeBarTextColor || s.themeBarEffectiveTextColor, themeFont);
    const themePresets = Array.isArray(state.boot?.themePresets) ? state.boot.themePresets : [];
    const themeRotation = state.boot?.themeRotation || {};
    const rotationInterval = Math.max(1, Math.min(1440, Number(themeRotation.intervalMinutes) || 30));
    const rotationLast = Number(themeRotation.lastAppliedMs || 0);
    const rotationTime = rotationLast > 0
      ? new Date(rotationLast).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })
      : '';
    const themePresetRows = themePresets.map((p, index) => {
      const active = String(themeRotation.currentPresetId || '') === String(p.id);
      const swatchStyle = p.backgroundImageSet && p.backgroundImage
        ? `background-image:${cssBackgroundUrl(p.backgroundImage)};background-size:cover;background-position:center`
        : `background:${normalizeHexColor(p.accentColor, '#e7b77c')}`;
      return `<div class="theme-preset-row${active ? ' active' : ''}" data-theme-preset-id="${esc(p.id)}" title="${tr('theme.clickPreview', '点击预览；当前预设可被覆盖保存')}">
        <div class="theme-preset-swatch" style='${swatchStyle}'></div>
        <div class="theme-preset-info">
          <div class="theme-preset-name">${esc(p.name)}</div>
          <div class="theme-preset-meta">${p.backgroundImageSet ? tr('theme.hasBg', '含背景图') : tr('theme.pureColor', '纯配色')} · ${p.barFollowStandard ? tr('theme.barFollowTheme', '底栏跟随主题') : tr('theme.barIndependentShort', '底栏独立')}${active ? ' · ' + tr('theme.current', '当前') : ''}</div>
        </div>
        <div class="theme-preset-actions">
          <button class="btn ghost theme-preset-btn" data-theme-preset-up="${esc(p.id)}" ${index === 0 ? 'disabled' : ''} title="${tr('theme.moveUp', '上移')}">↑</button>
          <button class="btn ghost theme-preset-btn" data-theme-preset-down="${esc(p.id)}" ${index === themePresets.length - 1 ? 'disabled' : ''} title="${tr('theme.moveDown', '下移')}">↓</button>
          <button class="btn ghost theme-preset-btn" data-theme-preset-apply="${esc(p.id)}">${tr('common.apply', '应用')}</button>
          <button class="btn ghost theme-preset-btn" data-theme-preset-rename="${esc(p.id)}">${tr('theme.rename', '重命名')}</button>
          <button class="btn danger theme-preset-btn" data-theme-preset-delete="${esc(p.id)}">${tr('common.delete', '删除')}</button>
        </div>
      </div>`;
    }).join('');
    const currentThemePreset = themePresets.find(p => String(p.id) === String(themeRotation.currentPresetId || '')) || null;
    const previewPreset = themePresets.find(p => String(p.id) === String(state.themePresetPreviewId || '')) || currentThemePreset;
    // 快捷键/菜单改设置会触发 boot 重推；保留滚动位置和正在编辑的输入，别把用户顶回顶部。
    setPage(`
      <div class="settings-scroll">
        <div class="settings-search-bar">
          <input class="input" id="settingsSearch" placeholder="${tr('settings.searchPlaceholder', '搜索设置项（名称或说明）')}" value="${esc(state.settingsSearch)}" autocomplete="off" />
          <span class="muted" id="settingsSearchCount"></span>
        </div>
        <div class="empty-state hidden" id="settingsSearchEmpty">${tr('settings.searchEmpty', '没有匹配的设置项')}</div>
        ${updateBannerHtml()}
        <div class="section" data-section-id="language">
          <div class="section-title">${tr('settings.section.language', '界面语言')}</div>
          <div class="panel-box">
            <div class="setting-item">
              <div><div class="setting-title">${tr('settings.language.title', '界面语言')}</div><div class="setting-sub">${tr('settings.language.sub', '切换后立即生效；部分内容仍为中文。')}</div></div>
              <div class="setting-content">
                <select class="select" id="setLocale">
                  <option value="zh" ${currentLocale === 'zh' ? 'selected' : ''}>${tr('settings.language.zh', '中文')}</option>
                  <option value="en" ${currentLocale === 'en' ? 'selected' : ''}>${tr('settings.language.en', 'English (Preview)')}</option>
                  <option value="ja" ${currentLocale === 'ja' ? 'selected' : ''}>${tr('settings.language.ja', '日本語 (Preview)')}</option>
                </select>
              </div>
            </div>
          </div>
        </div>
        <div class="section" data-section-id="account">
          <div class="section-title">${tr('settings.section.account', '账号')}</div>
          <div class="panel-box" style="display:flex;flex-direction:column;gap:12px">
            <div class="setting-item" style="padding:0 0 10px 0;border-bottom:1px solid var(--border,#eee)">
              <div><div class="setting-title">${tr('settings.serverRegion.title', '服务器区服')}</div><div class="setting-sub">${tr('settings.serverRegion.sub', '选择官方桌宠所在的区服（切换后需重新登录）')}</div></div>
              <div class="setting-content">
                <select class="select" id="setServerRegion">
                  <option value="cn" ${(s.serverRegion || 'cn') === 'cn' ? 'selected' : ''}>${tr('login.regionCn', '国服（CN）')}</option>
                  <option value="global" ${(s.serverRegion || 'cn') === 'global' ? 'selected' : ''}>${tr('login.regionGlobal', '国际服（Global）')}</option>
                </select>
              </div>
            </div>
            <div class="setting-item auth-status-row">
              <div>
                <div class="setting-title">${tr('settings.auth.title', '登录态')}</div>
                <div class="setting-sub">${esc(authStatus)}${auth.lastCheckedAt ? ' · ' + tr('settings.auth.lastChecked', '上次检测 {time}', { time: new Date(Number(auth.lastCheckedAt)).toLocaleTimeString('zh-CN', { hour12: false }) }) : ''}</div>
              </div>
              <div class="setting-content">
                ${['conflict', 'invalid', 'network'].includes(currentAuthState) ? `<button class="btn ghost" data-auth-recheck>${tr('auth.recheck', '重新检测')}</button>` : ''}
                ${currentAuthState === 'conflict' || (currentAuthState === 'invalid' && auth.credentialValid) ? `<button class="btn" data-auth-reconnect>${tr('auth.reconnect', '重新连接')}</button>` : ''}
              </div>
            </div>
            ${isLogged() ? `
              <div class="detail-row"><span>${tr('settings.auth.nickname', '昵称')}</span><span data-auth-nickname>${esc(auth.nickname || '—')}</span></div>
              <div class="detail-row"><span>UID</span><span data-auth-uid>${auth.uid || '—'}</span></div>
              <div class="detail-row" data-click-row="avail"><span>${tr('settings.auth.availClicks', '可用点击')}</span><span data-click-avail>${auth.clicks || 0}</span></div>
              <div class="detail-row" data-click-row="total"><span>${tr('settings.auth.totalClicks', '累计点击')}</span><span class="click-total"><span data-click-total>${auth.clickTotal ?? auth.clicks ?? 0}</span>${authOnlineAvailable() ? `<button class="btn ghost click-boost" id="boostClicks" title="${tr('settings.auth.boostTitle', '本地增加 10000 次点击并立即上报；连续操作间隔至少 0.5 秒')}">+10000</button>` : ''}</span></div>
              <div class="detail-row" data-click-row="consume"><span>${tr('settings.auth.consumed', '已消耗')}</span><span data-click-consume>${auth.clickConsume || 0}</span></div>
              <div class="setting-item" style="padding:8px 0;border-top:1px solid var(--border,#eee);border-bottom:1px solid var(--border,#eee);background:transparent">
                <div><div class="setting-title">${tr('settings.autoBoost.title', '自动补贴点击')}</div><div class="setting-sub">${tr('settings.autoBoost.sub', '可用点击低于 1000 时自动补充 10000')}</div></div>
                <div class="switch ${s.autoClickBoost ? 'on' : ''}" data-switch="autoClickBoost"></div>
              </div>
              <div style="display:flex;gap:10px">
                ${authOnlineAvailable() ? `<button class="btn ghost" id="userRefresh">${tr('settings.auth.refresh', '刷新账号信息')}</button>` : ''}
                <button class="btn danger" id="logoutBtn">${tr('settings.auth.logout', '退出登录')}</button>
              </div>` : ((s.serverRegion || 'cn') === 'global' ? `
              <div class="detail-row"><span>${tr('settings.auth.login', '登录')}</span><span class="muted">${tr('settings.auth.globalHint', '国际服请使用下方继承官方登录状态，或导入 Token')}</span></div>
              <div style="display:flex;gap:10px">
                <input class="input" id="setToken" placeholder="${tr('settings.auth.pasteToken', '粘贴官方客户端 infoToken')}" style="flex:1" />
                <button class="btn ghost" id="setTokenBtn">${tr('settings.auth.importToken', '导入 token')}</button>
              </div>` : `
              <div class="detail-row"><span>${tr('settings.auth.login', '登录')}</span><span class="muted">${tr('settings.auth.uidHint', 'UID / 手机号 + 验证码（也可继承官方登录状态）')}</span></div>
              <div style="display:flex;gap:10px">
                <input class="input" id="setUid" placeholder="${tr('login.uidPlaceholder', '游戏 UID（9-10 位）')}" style="width:180px" />
                <input class="input" id="setCode" placeholder="${tr('login.codePlaceholder', '6 位验证码')}" style="width:140px" />
                <button class="btn ghost" id="setSend">${tr('login.sendCode', '发送验证码')}</button>
                <button class="btn" id="setLogin">${tr('login.loginButton', '登录')}</button>
              </div>
              <div style="display:flex;gap:10px;align-items:center">
                <button class="btn ghost" id="setPhoneLoginOpen">${tr('login.phoneMode', '手机号登录')}</button>
                <span class="muted" style="font-size:12px">${tr('settings.auth.phoneHint', '库洛通行证手机号 + 短信验证码')}</span>
              </div>
              <div style="display:flex;gap:10px">
                <input class="input" id="setToken" placeholder="${tr('settings.auth.pasteToken', '粘贴官方客户端 infoToken')}" style="flex:1" />
                <button class="btn ghost" id="setTokenBtn">${tr('settings.auth.importToken', '导入 token')}</button>
              </div>`)}
            <div class="inherit-login">
              <div class="setting-sub">${tr('settings.auth.inheritHint', '建议在获取前先关闭本家程序的开机自动启动，以免下次开机因为 token 冲突导致掉线。')}</div>
              <button class="btn ghost" id="inheritLogin">${tr('settings.auth.inherit', '继承官方程序登录状态')}</button>
            </div>
          </div>
        </div>

        <div class="section" data-section-id="window">
          <div class="section-title">${tr('settings.section.window', '窗口与桌宠')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.scale.title', '桌宠大小')}</div><div class="setting-sub">${tr('settings.window.scale.sub', '拖动滑条以 1% 细调，或选择常用档位；缩放以猫底部中心为锚点')}</div></div>
            <div class="setting-content">
              <select class="select" id="setScale" style="width:96px">
                ${[0.25, 0.5, 0.75, 1, 1.25, 1.5, 2].map(v => `<option value="${v}" ${Math.abs((s.scale || 1) - v) < 0.005 ? 'selected' : ''}>${Math.round(v * 100)}%</option>`).join('')}
                <option value="custom" disabled ${scalePreset ? '' : 'selected'}>${tr('settings.custom', '自定义')}</option>
              </select>
              <input type="range" min="25" max="200" step="1" value="${scalePct}" id="setScaleRange" style="width:180px" />
              <span id="scaleVal">${scalePct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.screen.title', '默认显示屏幕')}</div><div class="setting-sub">${tr('settings.window.screen.sub', '程序启动、切换区服或托盘重置位置时使用的默认屏幕；F8 始终从桌宠当前实际所在屏幕开始切换，不受此处影响')}</div></div>
            <div class="setting-content">
              <select class="select" id="setPetScreen" style="min-width:220px">
                ${screens.map(x => `<option value="${esc(x.deviceName)}" ${x.deviceName === selectedScreen ? 'selected' : ''}>${esc(x.label)}</option>`).join('')}
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.screenSwitch.title', '切换屏幕时的移动范围')}</div><div class="setting-sub">${tr('settings.window.screenSwitch.sub', '默认位置成员指最近一次重置排序后未手动拖动的成员；全部成员模式会在两屏都有成员时互相交换，分辨率不同时按屏幕比例换算')}</div></div>
            <div class="setting-content">
              <select class="select" id="setScreenSwitchMode" style="min-width:240px">
                <option value="default" ${screenSwitchMode === 'default' ? 'selected' : ''}>${tr('settings.window.screenSwitch.default', '仅移动默认位置成员')}</option>
                <option value="main" ${screenSwitchMode === 'main' ? 'selected' : ''}>${tr('settings.window.screenSwitch.main', '只移动主角')}</option>
                <option value="all" ${screenSwitchMode === 'all' ? 'selected' : ''}>${tr('settings.window.screenSwitch.all', '全部成员一起移动')}</option>
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.remoteScale.title', '联机桌宠大小')}</div><div class="setting-sub">${tr('settings.window.remoteScale.sub', '默认跟随主角；取消勾选后可单独调整所有联机角色')}</div></div>
            <div class="setting-content">
              <label style="display:flex;align-items:center;gap:6px;font-size:13px;color:var(--ink);cursor:pointer;white-space:nowrap">
                <input type="checkbox" id="remoteFollowMain" ${remoteFollowMain ? 'checked' : ''} style="width:16px;height:16px;accent-color:#e3a765" />
                ${tr('settings.followMain', '跟随主角')}
              </label>
              <select class="select" id="setRemoteScale" style="width:96px" ${remoteFollowMain ? 'disabled' : ''}>
                ${[0.25, 0.5, 0.75, 1, 1.25, 1.5, 2].map(v => `<option value="${v}" ${Math.abs(remoteScalePct / 100 - v) < 0.005 ? 'selected' : ''}>${Math.round(v * 100)}%</option>`).join('')}
                <option value="custom" disabled ${remoteScalePreset ? '' : 'selected'}>${tr('settings.custom', '自定义')}</option>
              </select>
              <input type="range" min="25" max="200" step="1" value="${remoteScalePct}" id="setRemoteScaleRange" style="width:150px" ${remoteFollowMain ? 'disabled' : ''} />
              <span id="remoteScaleVal">${remoteScalePct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.opacity.title', '桌宠不透明度')}</div><div class="setting-sub">${tr('settings.window.opacity.sub', '100% 为完全不透明')}</div></div>
            <div class="setting-content">
              <input type="range" min="20" max="100" value="${opacityPct}" id="setOpacity" style="width:180px" />
              <span id="opacityVal">${opacityPct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.remoteOpacity.title', '联机桌宠不透明度')}</div><div class="setting-sub">${tr('settings.window.remoteOpacity.sub', '默认跟随主角；取消勾选后可单独调整所有联机角色')}</div></div>
            <div class="setting-content">
              <label style="display:flex;align-items:center;gap:6px;font-size:13px;color:var(--ink);cursor:pointer;white-space:nowrap">
                <input type="checkbox" id="remoteFollowMainOpacity" ${remoteFollowMainOpacity ? 'checked' : ''} style="width:16px;height:16px;accent-color:#e3a765" />
                ${tr('settings.followMain', '跟随主角')}
              </label>
              <input type="range" min="20" max="100" step="1" value="${remoteOpacityPct}" id="setRemoteOpacityRange" style="width:180px" ${remoteFollowMainOpacity ? 'disabled' : ''} />
              <span id="remoteOpacityVal">${remoteOpacityPct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.remoteNameScale.title', '联机玩家名字大小')}</div><div class="setting-sub">${tr('settings.window.remoteNameScale.sub', '相对默认字号 50%–200%，只影响联机成员昵称')}</div></div>
            <div class="setting-content">
              <input type="range" min="50" max="200" step="1" value="${remoteNameScalePct}" id="setRemoteNameScale" style="width:180px" />
              <span id="remoteNameScaleVal">${remoteNameScalePct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.keyBubbles.title', '按键气泡')}</div><div class="setting-sub">${tr('settings.window.keyBubbles.sub', '按键时在桌宠上方冒出键帽，默认开启')}</div></div>
            <div class="switch ${s.keyBubbles !== false ? 'on' : ''}" data-switch="keyBubbles"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.fps.title', '动画帧率')}</div><div class="setting-sub">${tr('settings.window.fps.sub', '表情和按键气泡的刷新帧率，默认 60；帧率越低占用越少')}</div></div>
            <div class="setting-content">
              <div class="layout-switch" id="setAnimationFps">
                ${[30, 60, 120].map(fps => `<button type="button" class="layout-choice ${(s.animationFps || 60) === fps ? 'active' : ''}" data-fps="${fps}">${tr('settings.window.fps.option', '{fps} 帧', { fps })}</button>`).join('')}
              </div>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.topmost.title', '窗口置顶')}</div><div class="setting-sub">${tr('settings.window.topmost.sub', '桌宠始终显示在其他窗口上方')}</div></div>
            <div class="switch ${s.topmost ? 'on' : ''}" data-switch="topmost"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.gameMode.title', '游戏模式（鼠标穿透）')}</div><div class="setting-sub">${tr('settings.window.gameMode.sub', '开启后桌宠不响应鼠标，可从托盘菜单恢复')}</div></div>
            <div class="switch ${s.clickThrough ? 'on' : ''}" data-switch="clickThrough"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.autoGameMode.title', '全屏自动游戏模式')}</div><div class="setting-sub">${tr('settings.window.autoGameMode.sub', '检测到前台窗口全屏时自动开启鼠标穿透，退出全屏后恢复')}</div></div>
            <div class="switch ${s.autoGameModeOnFullscreen !== false ? 'on' : ''}" data-switch="autoGameModeOnFullscreen"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.hoverFade.title', '鼠标悬停时半透明')}</div><div class="setting-sub">${tr('settings.window.hoverFade.sub', '便于看清桌宠后的内容')}</div></div>
            <div class="switch ${s.hoverFade ? 'on' : ''}" data-switch="hoverFade"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.hoverOpacity.title', '悬停隐藏程度')}</div><div class="setting-sub">${tr('settings.window.hoverOpacity.sub', '鼠标停留后降到正常不透明度的百分比，默认 40%')}</div></div>
            <div class="setting-content">
              <input type="range" min="10" max="100" value="${Math.round((s.hoverFadeOpacity ?? 0.4) * 100)}" id="setHoverOpacity" style="width:180px" />
              <span id="hoverOpacityVal">${Math.round((s.hoverFadeOpacity ?? 0.4) * 100)}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.allowOverlap.title', '允许角色重叠')}</div><div class="setting-sub">${tr('settings.window.allowOverlap.sub', '默认开启；关闭后检测到重叠的联机桌宠会自动挪到旁边')}</div></div>
            <div class="switch ${s.allowOverlap !== false ? 'on' : ''}" data-switch="allowOverlap"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.hideMainBar.title', '隐藏主角底栏')}</div><div class="setting-sub">${tr('settings.window.hideMainBar.sub', '只隐藏计数条、工具栏和手部压条，角色本体保持完整；隐藏后仍可通过快捷键恢复')}</div></div>
            <div class="switch ${s.hideMainBar ? 'on' : ''}" data-switch="hideMainBar"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.window.hideRemoteBars.title', '隐藏远程角色底栏')}</div><div class="setting-sub">${tr('settings.window.hideRemoteBars.sub', '只隐藏联机角色的昵称条和手部压条，角色本体与表情气泡保持显示；隐藏后仍可通过快捷键恢复')}</div></div>
            <div class="switch ${s.hideRemoteBars ? 'on' : ''}" data-switch="hideRemoteBars"></div>
          </div>
        </div>

        <div class="section" data-section-id="obs">
          <div class="section-title">${tr('settings.section.obs', 'OBS 采集')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.obs.enable.title', 'OBS 采集窗口')}</div><div class="setting-sub">${tr('settings.obs.enable.sub', '主角完整 GUI（数字/计数条/宝箱/昵称）和键盘/手柄面板一起显示；可自定义背景色或设为透明')}</div></div>
            <div class="switch ${s.captureMode ? 'on' : ''}" data-switch="captureMode"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.obs.size.title', '采集窗口大小')}</div><div class="setting-sub">${tr('settings.obs.size.sub', '单位像素，指客户区大小；OBS 窗口采集选择 “Wuwatappo Capture”')}</div></div>
            <div class="setting-content">
              <input class="input" id="captureW" type="number" min="160" max="1600" step="10" value="${s.captureWidth ?? 400}" style="width:86px" />
              <span class="muted">×</span>
              <input class="input" id="captureH" type="number" min="160" max="1600" step="10" value="${s.captureHeight ?? 700}" style="width:86px" />
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.obs.bg.title', '采集背景颜色')}</div><div class="setting-sub">${tr('settings.obs.bg.sub', '支持 #RRGGBB 色号；透明模式下不使用此颜色')}</div></div>
            <div class="setting-content" style="flex-wrap:wrap;justify-content:flex-end">
              <button type="button" class="color-picker-trigger" data-color-for="captureColorHex" title="${tr('color.title', '选择颜色')}"></button>
              <input class="input" id="captureColorHex" maxlength="7" value="${esc(s.captureColor || '#00FF00')}" style="width:86px;text-transform:uppercase" />
              <div class="capture-palette" id="capturePalette">
                ${['#00FF00', '#0000FF', '#FF00FF', '#00FFFF', '#FFFFFF', '#000000'].map(c => `<button type="button" class="capture-swatch" data-capture-color="${c}" style="background:${c}" title="${c}"></button>`).join('')}
              </div>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.obs.transparent.title', '透明背景')}</div><div class="setting-sub">${tr('settings.obs.transparent.sub', 'OBS 窗口采集需勾选「允许透明」；兼容模式下忽略透明，使用纯色背景')}</div></div>
            <div class="switch ${s.captureTransparent ? 'on' : ''}" data-switch="captureTransparent"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.obs.compat.title', 'OBS 兼容模式')}</div><div class="setting-sub">${tr('settings.obs.compat.sub', '回退到 0.1.10 之前的普通窗口渲染；OBS 显示黑影/黑屏时开启')}</div></div>
            <div class="switch ${s.captureCompatibilityMode ? 'on' : ''}" data-switch="captureCompatibilityMode"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.obs.stopMain.title', '停止渲染桌面主角')}</div><div class="setting-sub">${tr('settings.obs.stopMain.sub', 'OBS 模式下隐藏桌面上的主角，采集窗口仍会显示主角')}</div></div>
            <div class="switch ${s.captureStopRenderingMain ? 'on' : ''}" data-switch="captureStopRenderingMain"></div>
          </div>
        </div>

        <div class="section" data-section-id="accessory">
          <div class="section-title">${tr('settings.section.accessory', '头饰位置微调')}</div>
          <div class="setting-item accessory-tuning">
            <div class="accessory-tuning-head">
              <div>
                <div class="setting-title">${tr('settings.accessory.title', '当前头饰')}</div>
                <div class="setting-sub">${accId ? tr('settings.accessory.sub', '拖动滑条调整 X/Y、自由缩放与旋转，方向按钮微调位置；每个头饰独立保存') : tr('settings.accessory.none', '当前未佩戴头饰，先在背包 → 配件中选择一件')}</div>
                <div class="setting-sub accessory-local-note">${tr('settings.accessory.localNote', '此调整仅本地生效，联机玩家看见的是默认样式')}</div>
              </div>
              <span class="accessory-coords" id="accessoryCoords">X ${accX} · Y ${accY} · ${accScale}% · ${accRotation}°</span>
            </div>
            <div class="accessory-tuning-body">
              <div class="accessory-sliders">
                <label class="axis-control">
                  <span class="axis-label">X</span>
                  <span class="axis-slider-wrap">
                    <input class="axis-range" id="accOffsetX" type="range" min="-160" max="160" step="1" value="${accX}" aria-label="${tr('settings.accessory.xAria', '头饰 X 轴偏移')}" ${accId ? '' : 'disabled'}>
                  </span>
                  <output class="axis-value" id="accOffsetXValue">${accX}</output>
                </label>
                <label class="axis-control">
                  <span class="axis-label">Y</span>
                  <span class="axis-slider-wrap">
                    <input class="axis-range" id="accOffsetY" type="range" min="-160" max="160" step="1" value="${accY}" aria-label="${tr('settings.accessory.yAria', '头饰 Y 轴偏移')}" ${accId ? '' : 'disabled'}>
                  </span>
                  <output class="axis-value" id="accOffsetYValue">${accY}</output>
                </label>
                <label class="axis-control">
                  <span class="axis-label axis-label-text">${tr('settings.accessory.scale', '缩放')}</span>
                  <span class="axis-slider-wrap" style="--axis-zero:50%">
                    <input class="axis-range" id="accScale" type="range" min="25" max="175" step="1" value="${accScale}" aria-label="${tr('settings.accessory.scaleAria', '头饰自由缩放百分比')}" ${accId ? '' : 'disabled'}>
                  </span>
                  <output class="axis-value axis-value-wide" id="accScaleValue">${accScale}%</output>
                </label>
                <label class="axis-control">
                  <span class="axis-label axis-label-text">${tr('settings.accessory.rotate', '旋转')}</span>
                  <span class="axis-slider-wrap">
                    <input class="axis-range" id="accRotation" type="range" min="-180" max="180" step="1" value="${accRotation}" aria-label="${tr('settings.accessory.rotateAria', '头饰自由旋转角度')}" ${accId ? '' : 'disabled'}>
                  </span>
                  <output class="axis-value axis-value-wide" id="accRotationValue">${accRotation}°</output>
                </label>
              </div>
              <div class="accessory-actions">
                <div class="nudge-pad">
                  <button class="btn ghost nudge-btn" data-nudge="0,-1" ${accId ? '' : 'disabled'} title="${tr('settings.accessory.up', '上移')}">↑</button>
                  <button class="btn ghost nudge-btn" data-nudge="-1,0" ${accId ? '' : 'disabled'} title="${tr('settings.accessory.left', '左移')}">←</button>
                  <button class="btn ghost nudge-btn" data-nudge="0,1" ${accId ? '' : 'disabled'} title="${tr('settings.accessory.down', '下移')}">↓</button>
                  <button class="btn ghost nudge-btn" data-nudge="1,0" ${accId ? '' : 'disabled'} title="${tr('settings.accessory.right', '右移')}">→</button>
                </div>
                <button class="btn ghost accessory-reset" id="resetAccessoryOffset" ${accId && (accX || accY || accScale !== 100 || accRotation !== 0) ? '' : 'disabled'}>${tr('settings.accessory.reset', '归零')}</button>
              </div>
            </div>
          </div>
        </div>

        <div class="section" id="themeSection" data-section-id="theme">
          <div class="section-title">${tr('settings.section.theme', '主题与外观')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.theme.accent.title', '主题色')}</div><div class="setting-sub">${tr('settings.theme.accent.sub', '作为主色自动生成整套面板配色，也用于桌宠底部信息条')}</div></div>
            <div class="setting-content">
              <button type="button" class="color-picker-trigger" data-color-for="themeAccent" title="${tr('color.title', '选择颜色')}"></button>
              <input class="input color-hex-input" id="themeAccent" maxlength="7" value="${themeAccent}" placeholder="#E7B77C" />
              <button class="btn ghost" id="themeReset" title="${tr('settings.theme.reset.title', '恢复默认主题色和高级配色')}">${tr('settings.theme.reset', '恢复默认')}</button>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.theme.font.title', '字体颜色')}</div><div class="setting-sub">${tr('settings.theme.font.sub', '应用于面板主要文字和桌宠底部信息条')}</div></div>
            <div class="setting-content">
              <button type="button" class="color-picker-trigger" data-color-for="themeFont" title="${tr('color.title', '选择颜色')}"></button>
              <input class="input color-hex-input" id="themeFont" maxlength="7" value="${themeFont}" placeholder="#8E5E43" />
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.theme.barFollow.title', '底栏跟随标准主题')}</div><div class="setting-sub">${tr('settings.theme.barFollow.sub', '关闭后可单独设置主桌宠底栏和所有联机角色昵称条，不影响面板配色')}</div></div>
            <div class="switch ${themeBarFollow ? 'on' : ''}" data-switch="themeBarFollowStandard"></div>
          </div>
          ${themeBarFollow ? '' : `
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.theme.barColor.title', '底栏背景色')}</div><div class="setting-sub">${tr('settings.theme.barColor.sub', '主桌宠信息条与所有 RemoteCatWindow 昵称条共用')}</div></div>
            <div class="setting-content">
              <button type="button" class="color-picker-trigger" data-color-for="themeBarColor" title="${tr('color.title', '选择颜色')}"></button>
              <input class="input color-hex-input" id="themeBarColor" maxlength="7" value="${themeBarColorValue}" placeholder="#F5E4CC" />
              <button class="btn ghost" id="themeBarColorReset" title="${tr('settings.theme.barColor.resetTitle', '清空独立底色，重新使用主题色推导')}">${tr('settings.theme.barColor.reset', '跟随主题推导')}</button>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.theme.barText.title', '底栏文字')}</div><div class="setting-sub">${tr('settings.theme.barText.sub', '默认跟随主题字体色；也可以单独指定')}</div></div>
            <div class="setting-content" style="display:flex;gap:10px;align-items:center;flex-wrap:wrap">
              <label class="muted" style="display:inline-flex;align-items:center;gap:5px;font-size:12px;white-space:nowrap">
                <input type="checkbox" id="themeBarTextFollowTheme" ${themeBarTextFollow ? 'checked' : ''} />
                ${tr('settings.theme.barText.follow', '跟随主题字体色')}
              </label>
              ${themeBarTextFollow
                ? `<span class="muted" style="font-size:12px">${esc(s.themeBarEffectiveTextColor || themeFont)}</span>`
                : `<button type="button" class="color-picker-trigger" data-color-for="themeBarTextColor" title="${tr('color.title', '选择颜色')}"></button>
              <input class="input color-hex-input" id="themeBarTextColor" maxlength="7" value="${themeBarTextColorValue}" placeholder="#8E5E43" />`}
            </div>
          </div>`}
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.theme.bg.title', '背景图像')}</div><div class="setting-sub">${tr('settings.theme.bg.sub', '支持 PNG/JPG/WebP/GIF，最大 20MB；超过 4MB 自动压缩并存入主题缓存')}</div></div>
            <div class="setting-content" style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
              <input type="file" id="themeBgFile" accept="image/png,image/jpeg,image/webp,image/gif" style="max-width:220px;font-size:12px" />
              <button class="btn ghost" id="themeBgClear" ${themeBgSet ? '' : 'disabled'}>${tr('common.remove', '移除')}</button>
              <span class="muted" id="themeBgStatus" style="font-size:12px">${themeBgSet ? tr('settings.theme.bg.set', '已设置背景图') : tr('settings.theme.bg.unset', '未设置')}</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.theme.extract.title', '从图片提取配色')}</div><div class="setting-sub">${tr('settings.theme.extract.sub', '采样背景图的主色、辅助色和明暗色，生成整套面板与桌宠底栏配色')}</div></div>
            <div class="setting-content" style="display:flex;gap:10px;align-items:center;flex-wrap:wrap">
              <button class="btn ghost" id="themeExtract" ${themeBgSet ? '' : 'disabled'}>${tr('settings.theme.extract.button', '从图片提取配色')}</button>
              <label class="muted" style="display:inline-flex;align-items:center;gap:5px;font-size:12px;white-space:nowrap">
                <input type="checkbox" id="themeAutoPalette" ${themeAutoPalette ? 'checked' : ''} />
                ${tr('settings.theme.extract.auto', '选图后自动应用')}
              </label>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.theme.opacity.title', '背景透明度')}</div><div class="setting-sub">${tr('settings.theme.opacity.sub', '背景图在面板底层的可见程度')}</div></div>
            <div class="setting-content">
              <input type="range" id="themeBgOpacity" min="0" max="100" step="1" value="${themeBgOpacityPct}" style="width:150px" />
              <span id="themeBgOpacityVal" class="muted" style="font-size:12px">${themeBgOpacityPct}%</span>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.theme.blur.title', '背景模糊')}</div><div class="setting-sub">${tr('settings.theme.blur.sub', '背景图模糊半径，0 表示不模糊')}</div></div>
            <div class="setting-content">
              <input type="range" id="themeBgBlur" min="0" max="24" step="1" value="${themeBgBlur}" style="width:150px" />
              <span id="themeBgBlurVal" class="muted" style="font-size:12px">${themeBgBlur}px</span>
            </div>
          </div>
          <details class="theme-advanced">
            <summary>${tr('settings.theme.advanced', '高级配色微调')} <span class="muted">${tr('settings.theme.advancedSub', '自动生成的整套颜色')}</span></summary>
            <div class="theme-advanced-note">${tr('settings.theme.advancedNote', '修改任意颜色后会覆盖该颜色；恢复默认会清除所有覆盖。')}</div>
            <div class="theme-advanced-grid">
              ${THEME_ADVANCED_FIELDS.map(([key, label]) => `
                <label class="theme-advanced-field">
                  <span>${tr('theme.field.' + key, label)}</span>
                  <button type="button" class="color-picker-trigger" data-color-for="themeAdv_${key}" title="${tr('color.title', '选择颜色')}"></button>
                  <input class="input color-hex-input" id="themeAdv_${key}" data-theme-key="${key}" value="${themePalette[key] || '#e7b77c'}" maxlength="7" />
                </label>`).join('')}
            </div>
          </details>

          <div class="theme-subsection">
            <div class="theme-subsection-title">${tr('settings.theme.presets', '主题预设')}</div>
            <div class="setting-item" style="border-top:0">
              <div><div class="setting-title">${tr('settings.theme.saveCurrent', '保存当前主题')}</div><div class="setting-sub">${tr('settings.theme.saveCurrent.sub', '保存主题色、字体色、高级 palette、底栏独立色和当前背景图；预设背景图会独立复制保存')}</div></div>
              <div class="setting-content" style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
                <input class="input" id="themePresetName" maxlength="40" placeholder="${tr('settings.theme.presetNamePlaceholder', '预设名称，例如：夜间森林')}" value="${currentThemePreset ? esc(currentThemePreset.name) : ''}" style="width:200px" />
                ${currentThemePreset ? `<label class="muted" style="display:inline-flex;align-items:center;gap:5px;font-size:12px;white-space:nowrap"><input type="checkbox" id="themePresetOverwrite" checked /> ${tr('settings.theme.overwritePreset', '覆盖「{name}」', { name: esc(currentThemePreset.name) })}</label>` : ''}
                <button class="btn" id="themePresetSave">${currentThemePreset ? tr('settings.theme.overwriteCurrent', '覆盖当前预设') : tr('settings.theme.saveAsPreset', '保存为预设')}</button>
              </div>
            </div>
            <div class="theme-preset-list" id="themePresetList">
              ${themePresetRows || `<div class="muted" style="font-size:12px;padding:6px 2px">${tr('settings.theme.noPresets', '还没有保存的主题预设。')}</div>`}
            </div>
            <div class="theme-preset-preview" id="themePresetPreview">${themePresetPreviewHtml(previewPreset, themeRotation.currentPresetId)}</div>
            <div class="setting-item">
              <div><div class="setting-title">${tr('settings.theme.rotation.title', '自动轮换')}</div><div class="setting-sub">${tr('settings.theme.rotation.sub', '按预设列表顺序应用；面板关闭或设置页重渲染不会中断。')}${rotationLast > 0 ? tr('settings.theme.rotation.last', '上次应用：{time}', { time: rotationTime }) : ''}</div></div>
              <div class="setting-content" style="display:flex;gap:10px;align-items:center;flex-wrap:wrap">
                <div class="switch ${themeRotation.enabled ? 'on' : ''}" id="themeRotationToggle"></div>
                <span class="muted" style="font-size:12px">${tr('settings.theme.rotation.interval', '间隔')}</span>
                <input class="input" id="themeRotationInterval" type="number" min="1" max="1440" step="1" value="${rotationInterval}" style="width:84px" />
                <span class="muted" style="font-size:12px">${tr('settings.theme.rotation.minutes', '分钟')}</span>
              </div>
            </div>
          </div>
        </div>

        <div class="section" data-section-id="keyboard">
          <div class="section-title">${tr('settings.section.keyboard', '桌面键盘')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.keyboard.controllerProtocol.title', '手柄协议扩展')}</div><div class="setting-sub">${tr('settings.keyboard.controllerProtocol.sub', '仅在未检测到 XInput 手柄时启用；兼容 PS / Switch 等 DirectInput 手柄，通过 WinMM 读取按键、十字键和扳机')}</div></div>
            <div class="switch ${s.controllerProtocolExtension ? 'on' : ''}" data-switch="controllerProtocolExtension"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.keyboard.rhythm.title', '音游模式')}</div><div class="setting-sub">${tr('settings.keyboard.rhythm.sub', '把鸣潮专属指示器替换音游指示器；开启后立即切换')}</div></div>
            <div class="switch ${s.rhythmMode ? 'on' : ''}" data-switch="rhythmMode"></div>
          </div>
          ${s.rhythmMode ? `<div class="setting-item">
            <div><div class="setting-title">${tr('settings.keyboard.rhythmCount.title', '音游键数')}</div><div class="setting-sub">${tr('settings.keyboard.rhythmCount.sub', '选择 4K、5K 或 6K 键位布局')}</div></div>
            <div class="setting-content">
              <select class="select" id="setRhythmKeyCount">
                ${[4, 5, 6].map(n => `<option value="${n}" ${rhythmCount === n ? 'selected' : ''}>${n}K</option>`).join('')}
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.keyboard.rhythmKeys.title', '音游触发键位')}</div><div class="setting-sub">${tr('settings.keyboard.rhythmKeys.sub', '从左到右 {count} 个键；支持字母、数字、空格和 ; \' , . / 等符号；空格显示为横过来的 [', { count: rhythmCount })}</div></div>
            <div class="setting-content">
              ${Array.from({ length: rhythmCount }, (_, i) => `<input class="input" data-rhythm-key="${i}" value="${esc(rhythmDisplay(rhythmKeys[i] || rhythmDefaults[rhythmCount][i]))}" style="width:42px;text-align:center;text-transform:uppercase;font-weight:700" inputmode="text" autocomplete="off" spellcheck="false" />`).join('')}
            </div>
          </div>` : ''}
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.keyboard.mapping.title', '键盘映射')}</div><div class="setting-sub">${tr('settings.keyboard.mapping.sub', '桌面键盘完全穿透，不拦截输入')}</div></div>
            <div class="setting-content">
              ${[['hidden', tr('tray.keyboardHidden', '不显示')], ['letters', tr('tray.keyboardLetters', '26 键')], s.rhythmMode ? ['rhythm', tr('tray.keyboardRhythm', '音游模式')] : ['wuwa', tr('tray.keyboardWuwa', '鸣潮专属')], ['controller', tr('tray.keyboardController', '手柄')]].map(([k, label]) =>
                `<button class="pill ${s.keyboardMode === k ? 'active' : ''}" data-kb="${k}">${label}</button>`).join('')}
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.keyboard.controls.title', '显示映射控制')}</div><div class="setting-sub">${tr('settings.keyboard.controls.sub', '关闭后仅保留键位图案')}</div></div>
            <div class="switch ${s.showMappingControls ? 'on' : ''}" data-switch="showMappingControls"></div>
          </div>
        </div>

        <div class="section" data-section-id="hotkeys">
          <div class="section-title">${tr('settings.section.hotkeys', '快捷键')}</div>
          <div class="hint muted" style="font-size:12px;margin:-4px 0 10px">${tr('settings.hotkeys.intro', '点击输入框后按下新组合键；Esc 取消，退格清除（禁用该快捷键）。支持 Ctrl / Alt / Shift + 普通按键。快捷键为被动监听，不会拦截其他程序的按键，也不影响按键延迟。')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.hotkeys.separate.title', '分开设置底栏快捷键')}</div><div class="setting-sub">${tr('settings.hotkeys.separate.sub', '关闭时用一个快捷键同时切换主角和远程角色底栏；开启后分别用两个快捷键控制。切换只影响当前显示和注册的行，另一模式已录制的绑定会保留。')}</div></div>
            <div class="switch ${s.separateBarHotkeys ? 'on' : ''}" data-switch="separateBarHotkeys"></div>
          </div>
          <div class="hotkey-list" id="hotkeyList">
            ${HOTKEY_ACTIONS.filter(a => !a.mode || a.mode === (s.separateBarHotkeys ? 'separate' : 'together')).map(a => `
              <div class="hotkey-row">
                <span class="hotkey-label">${esc(hotkeyLabel(a))}</span>
                <input class="input hotkey-input" readonly data-hotkey="${a.id}" data-value="${esc((s.hotkeys || {})[a.id] || '')}" value="${esc((s.hotkeys || {})[a.id] || tr('common.notSet', '未设置'))}" />
                <button class="btn ghost hotkey-clear" data-hotkey-clear="${a.id}">${tr('settings.hotkeys.clear', '清除')}</button>
              </div>`).join('')}
          </div>
          <div style="display:flex;gap:10px;margin-top:10px;align-items:center">
            <button class="btn ghost" id="hotkeysReset">${tr('settings.hotkeys.reset', '恢复默认')}</button>
            <span class="muted" style="font-size:12px">${tr('settings.hotkeys.autoSave', '修改后自动保存')}</span>
          </div>
        </div>

        <div class="section" data-section-id="room">
          <div class="section-title">${tr('settings.section.room', '联机与宝箱')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.room.layout.title', '联机桌宠排列')}</div><div class="setting-sub">${tr('settings.room.layout.sub', '横向贴底部排成一排；纵向贴屏幕右侧，排满后向左换列')}</div></div>
            <div class="setting-content">
              <div class="layout-switch" id="setCatLayout">
                <button type="button" class="layout-choice ${(s.catLayout || 'horizontal') !== 'vertical' ? 'active' : ''}" data-layout="horizontal">${tr('room.horizontal', '横向排列')}</button>
                <button type="button" class="layout-choice ${(s.catLayout || 'horizontal') === 'vertical' ? 'active' : ''}" data-layout="vertical">${tr('room.vertical', '纵向排列')}</button>
              </div>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('room.hideOthers.title', '隐藏其他成员桌宠')}</div><div class="setting-sub">${tr('room.hideOthers.sub', '只影响本机显示')}<br><span class="hide-others-hotkey-note">${hideOthersHotkey ? tr('settings.hotkeys.useKey', '使用 {key} 开关', { key: esc(hideOthersHotkey) }) : tr('settings.hotkeys.notSet', '当前未设置快捷键')}</span></div></div>
            <div class="switch ${s.hideOtherPets ? 'on' : ''}" data-switch="hideOtherPets"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.room.alwaysChest.title', '总是显示宝箱')}</div><div class="setting-sub">${tr('settings.room.alwaysChest.sub', '显示宝箱倒计时')}</div></div>
            <div class="switch ${s.alwaysShowChest ? 'on' : ''}" data-switch="alwaysShowChest"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.room.autoChest.title', '自动领取宝箱')}</div><div class="setting-sub">${tr('settings.room.autoChest.sub', '倒计时结束后至少等待 5 秒再领取')}</div></div>
            <div class="switch ${s.autoChest ? 'on' : ''}" data-switch="autoChest"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.room.autoSynth.title', '自动升阶合成')}</div><div class="setting-sub">${tr('settings.room.autoSynth.sub', '拥有 10 件以上相同类型和稀有度的多余材料时自动升阶合成（每种物品保留 1 份底物）')}</div></div>
            <div class="switch ${s.autoSynthUp ? 'on' : ''}" data-switch="autoSynthUp"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.room.manualChest.title', '手动开启宝箱')}</div><div class="setting-sub">${tr('settings.room.manualChest.sub', '每次消耗 1000 点击数，冷却 30 分钟，以服务器返回为准')}</div></div>
            <div class="setting-content">
              <button class="btn ghost" data-chest="left">${tr('settings.room.manualChest.left', '开启左宝箱')}</button>
              <button class="btn ghost" data-chest="right">${tr('settings.room.manualChest.right', '开启右宝箱')}</button>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.room.cooldown.title', '宝箱冷却')}</div><div class="setting-sub" id="chestStatus">${tr('settings.room.cooldown.reading', '正在读取…')}</div></div>
          </div>
          ${forcedOnlineRow}
        </div>

        <div class="section" data-section-id="auto">
          <div class="section-title">${tr('settings.section.auto', '自动化')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.auto.emote.title', '自动表情')}</div><div class="setting-sub">${tr('settings.auto.emote.sub', '每次产生敲击时自动发送一个表情；受下方每秒上限限制')}</div></div>
            <div class="switch ${s.autoEmoteEnabled ? 'on' : ''}" data-switch="autoEmoteEnabled"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.auto.emoteRate.title', '自动表情频率上限')}</div><div class="setting-sub">${tr('settings.auto.emoteRate.sub', '滚动一秒内最多发送数量，范围 1–20')}</div></div>
            <div class="setting-content"><input class="input" id="autoEmoteRate" type="number" min="1" max="20" step="1" value="${s.autoEmoteMaxPerSecond ?? 20}" style="width:86px" /><span class="muted">${tr('settings.unit.perSecond', '个/秒')}</span></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.auto.emoteSource.title', '表情来源')}</div><div class="setting-sub">${tr('settings.auto.emoteSource.sub', '快捷栏随机：只从自己的 5 个快捷表情中挑选')}</div></div>
            <div class="setting-content">
              <select class="select" id="autoEmoteMode">
                <option value="quickbar" ${(s.autoEmoteMode || 'quickbar') === 'quickbar' ? 'selected' : ''}>${tr('settings.auto.emoteSource.quickbar', '快捷栏随机')}</option>
                <option value="specific" ${s.autoEmoteMode === 'specific' ? 'selected' : ''}>${tr('settings.auto.emoteSource.specific', '指定表情')}</option>
                <option value="library" ${s.autoEmoteMode === 'library' ? 'selected' : ''}>${tr('settings.auto.emoteSource.library', '全部表情库随机')}</option>
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.auto.specificEmote.title', '指定表情')}</div><div class="setting-sub">${tr('settings.auto.specificEmote.sub', '「表情来源」选择指定表情时生效')}</div></div>
            <div class="setting-content">
              <select class="select" id="autoEmoteId" style="max-width:260px">
                <option value="0">${tr('settings.auto.specificEmote.placeholder', '请选择表情')}</option>
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.auto.click.title', '自动敲击')}</div><div class="setting-sub">${tr('settings.auto.click.sub', '自动产生敲击并上传；建议不超过 10 次/秒')}</div></div>
            <div class="switch ${s.autoClickEnabled ? 'on' : ''}" data-switch="autoClickEnabled"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.auto.clickRate.title', '自动敲击频率')}</div><div class="setting-sub">${tr('settings.auto.clickRate.sub', '每秒产生敲击次数，范围 1–10')}</div></div>
            <div class="setting-content"><input class="input" id="autoClickRate" type="number" min="1" max="10" step="1" value="${s.autoClickPerSecond ?? 10}" style="width:86px" /><span class="muted">${tr('settings.unit.timesPerSecond', '次/秒')}</span></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.auto.animate.title', '显示虚拟敲击动作')}</div><div class="setting-sub">${tr('settings.auto.animate.sub', '关闭后只响应真实敲击；键位显示器始终不响应虚拟敲击')}</div></div>
            <div class="switch ${s.autoClickAnimate !== false ? 'on' : ''}" data-switch="autoClickAnimate"></div>
          </div>
        </div>

        <div class="section" data-section-id="notify">
          <div class="section-title">${tr('settings.section.notify', '通知')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.notify.chest.title', '宝箱领取通知')}</div><div class="setting-sub">${tr('settings.notify.chest.sub', '自动领取成功后，按下方稀有度/分类过滤发送 Windows 通知')}</div></div>
            <div class="switch ${s.notifyChestClaimed !== false ? 'on' : ''}" data-switch="notifyChestClaimed"></div>
          </div>
          <div class="setting-item" style="align-items:flex-start">
            <div>
              <div class="setting-title">${tr('settings.notify.chestMatrix.title', '宝箱通知过滤')}</div>
              <div class="setting-sub">${tr('settings.notify.chestMatrix.sub', '只有稀有度和物品分类都勾选时才会通知；默认只通知稀有/史诗。')}</div>
            </div>
            <div class="setting-content" id="chestNotifyMatrix">${chestNotifyMatrixHtml(s)}</div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.notify.hotkeys.title', '快捷键操作通知')}</div><div class="setting-sub">${tr('settings.notify.hotkeys.sub', '触发刷新、游戏模式等快捷键时发送 Windows 通知')}</div></div>
            <div class="switch ${s.notifyHotkeys !== false ? 'on' : ''}" data-switch="notifyHotkeys"></div>
          </div>
        </div>

        <div class="section" data-section-id="redeem">
          <div class="section-title">${tr('settings.section.redeem', '兑换')}</div>
          <div class="panel-box" style="display:flex;gap:10px">
            <input class="input" id="redeemCode" placeholder="${tr('settings.redeem.placeholder', '输入兑换码')}" style="flex:1" />
            <button class="btn" id="redeemBtn">${tr('settings.redeem.button', '兑换')}</button>
          </div>
        </div>

        <div class="section" data-section-id="other">
          <div class="section-title">${tr('settings.section.other', '其他')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.other.launch.title', '开机自动启动')}</div><div class="setting-sub">${tr('settings.other.launch.sub', '登录 Windows 后自动运行桌宠（仅当前用户；管理员模式使用最高权限计划任务）')}</div></div>
            <div class="switch ${s.launchAtLogin ? 'on' : ''}" data-switch="launchAtLogin"></div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.other.admin.title', '以管理员权限启动')}</div><div class="setting-sub">${s.isElevated ? tr('settings.other.admin.elevated', '当前已是管理员权限') : tr('settings.other.admin.normal', '当前为普通权限')}${tr('settings.other.admin.hint', '；用于统计以管理员权限运行的游戏/程序中的按键。开启会请求 UAC 并重启；开机自启由最高权限计划任务完成，登录时不再提示')}</div></div>
            <div class="switch ${s.runAsAdmin ? 'on' : ''}" data-switch="runAsAdmin"></div>
          </div>
          <div class="setting-item" style="align-items:flex-start">
            <div><div class="setting-title">${tr('settings.other.rules.title', '游戏模式规则')}</div><div class="setting-sub">${tr('settings.other.rules.sub', '检测到以下进程启动时自动开启鼠标穿透，进程退出后恢复')}</div></div>
            <div class="setting-content" style="flex-direction:column;align-items:stretch;gap:8px;min-width:280px">
              <div style="display:flex;gap:8px">
                <input class="input" id="gameRuleInput" placeholder="${tr('settings.other.rules.placeholder', '进程名，如 WutheringWaves.exe')}" style="flex:1;min-width:180px" />
                <button class="btn ghost" id="gameRuleAdd">${tr('settings.other.rules.add', '添加')}</button>
              </div>
              <div id="gameRuleList" class="rule-list">
                ${(s.gameModeRules || []).length
                  ? (s.gameModeRules || []).map(name => `<span class="rule-chip">${esc(name)}<button type="button" data-remove-rule="${esc(name)}" title="${tr('settings.other.rules.delete', '删除规则')}">×</button></span>`).join('')
                  : `<span class="muted" style="font-size:12px">${tr('settings.other.rules.empty', '暂无规则')}</span>`}
              </div>
              <div class="setting-sub" style="margin-top:0">${tr('settings.other.rules.hint', '可填写任务管理器中的进程名，带不带 .exe 均可；匹配不区分大小写。')}</div>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.other.logs.title', '日志与数据')}</div><div class="setting-sub">%LOCALAPPDATA%\\WuwatappoNative</div></div>
            <div class="setting-content"><button class="btn ghost" id="openLog">${tr('settings.other.logs.open', '打开日志目录')}</button></div>
          </div>
        </div>

        <div class="section" data-section-id="update">
          <div class="section-title">${tr('settings.section.update', '更新')}</div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.update.channel.title', '更新通道')}</div><div class="setting-sub">${tr('settings.update.channel.sub', '正式版只接收正式版；测试版同时接收正式版和后续测试版')}</div></div>
            <div class="setting-content">
              <select class="select" id="updateChannel" style="width:180px">
                <option value="stable" ${(s.updateChannel || 'stable') === 'stable' ? 'selected' : ''}>${tr('settings.update.channel.stable', '正式版')}</option>
                <option value="beta" ${s.updateChannel === 'beta' ? 'selected' : ''}>${tr('settings.update.channel.beta', '测试版')}</option>
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.update.source.title', '更新下载源')}</div><div class="setting-sub">${tr('settings.update.source.sub', '自动模式固定按 GH-Proxy → GitHub 官方的顺序尝试，失败才回退；不会在下载中按速度中断')}</div></div>
            <div class="setting-content">
              <select class="select" id="updateDownloadSource">
                <option value="auto" ${(s.updateDownloadSource || 'auto') === 'auto' ? 'selected' : ''}>${tr('settings.update.source.auto', '自动（GH-Proxy → GitHub）')}</option>
                <option value="ghproxy" ${s.updateDownloadSource === 'ghproxy' ? 'selected' : ''}>${tr('settings.update.source.ghproxy', 'GH-Proxy（第三方加速）')}</option>
                <option value="github" ${s.updateDownloadSource === 'github' ? 'selected' : ''}>${tr('settings.update.source.github', 'GitHub 直连')}</option>
              </select>
            </div>
          </div>
          <div class="setting-item">
            <div><div class="setting-title">${tr('settings.update.proxy.title', '使用 HTTP 代理')}</div><div class="setting-sub">${tr('settings.update.proxy.sub', '仅用于检查更新和下载安装包；关闭时使用系统代理，已填写地址会保留')}</div></div>
            <div class="switch ${s.updateUseProxy ? 'on' : ''}" id="updateUseProxy"></div>
          </div>
          <div class="setting-item ${s.updateUseProxy ? '' : 'hidden'}" id="updateProxyItem" style="align-items:flex-start">
            <div><div class="setting-title">${tr('settings.update.proxyAddress.title', 'HTTP 代理地址')}</div><div class="setting-sub">${tr('settings.update.proxyAddress.sub', '修改后自动保存，例如 http://127.0.0.1:7890')}</div></div>
            <div class="setting-content" style="flex-wrap:nowrap;align-items:center;min-width:min(420px,100%)">
              <input class="input" id="updateHttpProxy" value="${esc(s.updateHttpProxy || '')}" placeholder="http://127.0.0.1:7890" style="flex:1;min-width:0" ${s.updateUseProxy ? '' : 'disabled'} />
            </div>
          </div>
        </div>
        ${compatibilityBannerHtml()}
        <div class="settings-footer">
          <button class="btn ghost" id="checkUpdateBtn">${tr('settings.footer.checkUpdate', '检查更新')}</button>
          <button class="btn ghost" id="compatRefreshBtn">${tr('settings.footer.compatRefresh', '刷新版本状态')}</button>
          <button class="btn ghost" id="announcementHistoryBtn">${tr('settings.footer.announcements', '公告历史')}</button>
          <button class="btn ghost" id="aboutBtn">${tr('settings.footer.about', '关于')}</button>
          <span class="muted" id="updateCheckStatus"></span>
        </div>
      </div>`);
    groupSettingsDom();

    document.getElementById('updateDismiss')?.addEventListener('click', () => {
      state.updateDismissed = true;
      renderSettings();
    });
    document.getElementById('forcedOnlineSettingBtn')?.addEventListener('click', () => {
      if (forcedOnlineAccepted()) confirmRestoreForcedOnline();
      else openForcedOnlineConfirm(forcedCheck);
    });
    bindSettings();
    bindColorTriggers(document);
    loadAutoEmoteOptions().catch(() => { });
    const scroller = document.querySelector('.settings-scroll');
    if (scroller) {
      // 记录用户正在编辑但尚未提交的输入；boot/主题重绘后恢复，不会被服务端值覆盖。
      const markDirty = e => { if (e.target?.id) e.target.dataset.userDirty = '1'; };
      scroller.addEventListener('input', markDirty);
      scroller.addEventListener('change', markDirty);
      const search = document.getElementById('settingsSearch');
      let searchTimer = 0;
      const runSearch = () => {
        state.settingsSearch = search?.value || '';
        filterSettings(state.settingsSearch);
      };
      if (search) {
        search.addEventListener('input', () => {
          clearTimeout(searchTimer);
          searchTimer = setTimeout(runSearch, 150);
        });
        search.addEventListener('keydown', e => {
          if (e.key === 'Enter') { e.preventDefault(); clearTimeout(searchTimer); runSearch(); }
          if (e.key === 'Escape') { search.value = ''; clearTimeout(searchTimer); runSearch(); }
        });
      }
      filterSettings(state.settingsSearch);
      restoreSettingsViewState(viewState);
      const searchAfterRestore = document.getElementById('settingsSearch');
      if (searchAfterRestore && searchAfterRestore.value !== state.settingsSearch) {
        state.settingsSearch = searchAfterRestore.value;
        filterSettings(state.settingsSearch);
      }
    }
  }

  // 与原版 Helper 一致的默认键位；实际值来自 boot.settings.hotkeys。
  const HOTKEY_ACTIONS = [
    { id: 'game', key: 'hotkey.game', label: '游戏模式（鼠标穿透）', def: 'F7' },
    { id: 'monitor', key: 'hotkey.monitor', label: '切换屏幕', def: 'F8' },
    { id: 'visibility', key: 'hotkey.visibility', label: '显示／隐藏桌宠', def: 'F9' },
    { id: 'barToggle', key: 'hotkey.barToggle', label: '隐藏／显示底栏（主角+远程）', def: '', mode: 'together' },
    { id: 'barToggleMain', key: 'hotkey.barToggleMain', label: '隐藏／显示主角底栏', def: '', mode: 'separate' },
    { id: 'barToggleRemote', key: 'hotkey.barToggleRemote', label: '隐藏／显示远程角色底栏', def: '', mode: 'separate' },
    { id: 'reload', key: 'hotkey.reload', label: '刷新界面', def: 'F5' },
    ...[1, 2, 3, 4, 5, 6].map(i => ({ id: 'emote' + i, key: 'hotkey.emote', label: '发送表情 · 槽位 ' + i, def: 'Ctrl+Alt+' + i, slot: i })),
    { id: 'capture', key: 'hotkey.capture', label: 'OBS 采集窗口', def: '' },
    { id: 'mainPanel', key: 'hotkey.mainPanel', label: '打开主面板', def: '' },
    { id: 'catLayout', key: 'hotkey.catLayout', label: '切换横纵排列', def: '' },
    { id: 'hideOtherPets', key: 'hotkey.hideOtherPets', label: '开关隐藏其他成员桌宠', def: '' },
    { id: 'keyBubbles', key: 'hotkey.keyBubbles', label: '开关按键气泡', def: '' },
    { id: 'rhythmMode', key: 'hotkey.rhythmMode', label: '开关音游模式', def: '' },
    { id: 'autoEmote', key: 'hotkey.autoEmote', label: '开关自动表情', def: '' },
    { id: 'autoClick', key: 'hotkey.autoClick', label: '开关自动敲击', def: '' },
  ];
  const hotkeyLabel = a => a.slot
    ? tr('hotkey.emoteSlot', '发送表情 · 槽位 {slot}', { slot: a.slot })
    : tr(a.key, a.label);

  function hotkeyFromEvent(e) {
    if (['Control', 'Alt', 'Shift', 'Meta', 'OS'].includes(e.key)) return null;
    if (e.metaKey) return null;
    let base = '';
    if (e.code && /^Key[A-Z]$/.test(e.code)) base = e.code.slice(3);
    else if (e.code && /^Digit\d$/.test(e.code)) base = e.code.slice(5);
    else if (/^F([1-9]|1[0-2])$/.test(e.key)) base = e.key.toUpperCase();
    else {
      const map = { ' ': 'Space', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', Escape: 'Escape', Enter: 'Enter', Tab: 'Tab', Backspace: 'Backspace', Delete: 'Delete', Insert: 'Insert', Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', '`': '`', '-': '-', '=': '=', '[': '[', ']': ']', ';': ';', "'": "'", ',': ',', '.': '.', '/': '/', '\\': '\\' };
      base = map[e.key] || '';
    }
    if (!base) return null;
    const parts = [];
    if (e.ctrlKey) parts.push('Ctrl');
    if (e.altKey) parts.push('Alt');
    if (e.shiftKey) parts.push('Shift');
    parts.push(base);
    return parts.join('+');
  }

  function bindHotkeys() {
    const rows = {};
    let saveTimer = 0;
    let savePending = false;
    let saveBusy = false;
    const collect = () => {
      const table = {};
      for (const a of HOTKEY_ACTIONS) {
        const input = rows[a.id];
        if (!input) continue;
        // 只读写 data-value，绝不把“未设置/请按下新快捷键”等可翻译显示文案当成逻辑值。
        table[a.id] = input.dataset.value ?? '';
      }
      return table;
    };
    const refreshHideOthersHotkeyNote = () => {
      const key = String(state.boot?.settings?.hotkeys?.hideOtherPets || '').trim();
      const text = key ? tr('settings.hotkeys.useKey', '使用 {key} 开关', { key }) : tr('settings.hotkeys.notSet', '当前未设置快捷键');
      document.querySelectorAll('.hide-others-hotkey-note').forEach(el => { el.textContent = text; });
    };
    const save = async () => {
      clearTimeout(saveTimer);
      if (!savePending || saveBusy) return;
      savePending = false;
      saveBusy = true;
      try {
        const r = await tryCall('hotkeys.save', { hotkeys: collect() });
        if (r?.ok === false) {
          savePending = false;
          return toast(r.error || tr('toast.hotkeySaveFailed', '快捷键保存失败'), 'err');
        }
        await syncBoot();
        refreshHideOthersHotkeyNote();
        toast(tr('toast.hotkeySaved', '快捷键已自动保存'));
      } catch {
        savePending = false;
      } finally {
        saveBusy = false;
        if (savePending) saveTimer = setTimeout(save, 300);
      }
    };
    const queueSave = () => {
      savePending = true;
      clearTimeout(saveTimer);
      saveTimer = setTimeout(save, 300);
    };
    document.querySelectorAll('[data-hotkey]').forEach(input => {
      const id = input.dataset.hotkey;
      rows[id] = input;
      let listening = false;
      const display = () => input.dataset.value || tr('common.notSet', '未设置');
      input.addEventListener('focus', () => { listening = true; input.value = tr('settings.hotkeys.pressNew', '请按下新快捷键…'); });
      input.addEventListener('blur', () => {
        if (listening) { listening = false; input.value = display(); }
        if (savePending) save();
      });
      input.addEventListener('keydown', e => {
        if (!listening) return;
        e.preventDefault(); e.stopPropagation();
        if (e.key === 'Escape') { input.blur(); return; }
        if (e.key === 'Backspace' || e.key === 'Delete') {
          input.dataset.value = '';
          queueSave();
          input.blur();
          return;
        }
        const chord = hotkeyFromEvent(e);
        if (!chord) return;
        input.dataset.value = chord;
        input.value = chord;
        queueSave();
        input.blur();
      });
    });
    document.querySelectorAll('[data-hotkey-clear]').forEach(btn => btn.onclick = () => {
      const input = rows[btn.dataset.hotkeyClear];
      if (input) {
        input.dataset.value = '';
        input.value = tr('common.notSet', '未设置');
        queueSave();
      }
    });
    document.getElementById('hotkeysReset')?.addEventListener('click', async () => {
      for (const a of HOTKEY_ACTIONS) {
        const input = rows[a.id];
        if (!input) continue;
        input.dataset.value = a.def || '';
        input.value = a.def || tr('common.notSet', '未设置');
      }
      queueSave();
      await save();
      toast(tr('toast.hotkeyReset', '已恢复默认快捷键'));
    });
  }

  function bindSettings() {
    const val = (id) => document.getElementById(id)?.value;
    if (!themeImagePaletteInitialized) {
      themeImagePaletteInitialized = true;
      themeImagePaletteActive = !!readThemeAdvancedOverrides(state.boot?.settings).__imagePalette;
    }
    bindChestButtons();
    const scaleSel = document.getElementById('setScale');
    const scaleRange = document.getElementById('setScaleRange');
    const scaleVal = document.getElementById('scaleVal');
    const scalePresets = [25, 50, 75, 100, 125, 150, 200];
    let lastScaleSent = 0;
    const syncScaleSelect = (pct) => {
      if (!scaleSel) return;
      scaleSel.value = scalePresets.includes(pct) ? String(pct / 100) : 'custom';
    };
    const applyScale = async (pct, final = false) => {
      pct = Math.max(25, Math.min(200, Math.round(Number(pct) || 100)));
      if (scaleVal) scaleVal.textContent = pct + '%';
      if (scaleRange) scaleRange.value = String(pct);
      syncScaleSelect(pct);
      if (state.boot?.settings) state.boot.settings.scale = pct / 100;
      if (document.getElementById('remoteFollowMain')?.checked) {
        const rr = document.getElementById('setRemoteScaleRange');
        const rv = document.getElementById('remoteScaleVal');
        const rs = document.getElementById('setRemoteScale');
        if (rr) rr.value = String(pct);
        if (rv) rv.textContent = pct + '%';
        if (rs) rs.value = scalePresets.includes(pct) ? String(pct / 100) : 'custom';
      }
      try {
        const boot = await tryCall('settings.set', { scale: pct / 100 });
        if (boot?.settings) state.boot = boot;
        if (final) renderChrome();
      } catch { }
    };
    scaleSel?.addEventListener('change', e => {
      if (e.target.value === 'custom') return;
      lastScaleSent = performance.now();
      applyScale(Number(e.target.value) * 100, true);
    });
    document.getElementById('setPetScreen')?.addEventListener('change', async e => {
      try {
        await tryCall('settings.set', { petScreen: e.target.value });
        await syncBoot();
        renderSettings();
        toast(tr('toast.screenChanged', '已切换默认屏幕并移动桌宠'));
      } catch { }
    });
    document.getElementById('setScreenSwitchMode')?.addEventListener('change', async e => {
      try {
        await tryCall('settings.set', { screenSwitchMode: e.target.value });
        await syncBoot();
        renderSettings();
        toast(tr('toast.screenSwitchUpdated', '已更新切换屏幕时的移动方式'));
      } catch { }
    });

    const previewTheme = patch => {
      if (!state.boot) return;
      state.boot.settings = { ...(state.boot.settings || {}), ...patch };
      applyTheme(state.boot.settings);
      previewPetTheme(patch);
    };
    const saveTheme = async (patch, refreshUi = false) => {
      try {
        state.boot = await tryCall('settings.set', patch);
        clearPetThemePreview();
        // 手动改任意主题字段后，原主题预设不再代表当前状态。
        const themePatch = Object.keys(patch || {}).some(key => key.startsWith('theme'));
        if (themePatch && state.boot?.themeRotation) state.boot.themeRotation.currentPresetId = '';
        applyTheme(state.boot.settings);
        // 主题色/字体变化后重渲染设置页，让高级配色输入框和“当前预设”标记显示新状态。
        if (state.page === 'settings' && (refreshUi || themePatch)) renderSettings();
        return true;
      } catch {
        clearPetThemePreview();
        return false;
      }
    };
    const themeAccentInput = document.getElementById('themeAccent');
    const previewThemeColor = (key, value) => {
      const overrides = readAdvancedOverrides();
      const patch = { [key]: value };
      // 图片自动配色是整套高级覆盖；手动改主题色/字体时先清掉它，让新主色重新生成整套颜色。
      if (themeImagePaletteActive || overrides.__imagePalette) patch.themeAdvanced = '';
      previewTheme(patch);
    };
    const saveThemeColor = (key, value) => {
      const overrides = readAdvancedOverrides();
      const patch = { [key]: value };
      // 这里必须显式把空 themeAdvanced 发给 C#：input 事件只会改内存里的预览状态，
      // change 事件才是持久化点，否则重启后图片自动配色会回来。
      if (themeImagePaletteActive || overrides.__imagePalette) {
        themeImagePaletteActive = false;
        patch.themeAdvanced = '';
      }
      return saveTheme(patch, true);
    };
    themeAccentInput?.addEventListener('input', e => previewThemeColor('themeAccent', e.target.value));
    themeAccentInput?.addEventListener('change', e => saveThemeColor('themeAccent', e.target.value));
    const themeFontInput = document.getElementById('themeFont');
    themeFontInput?.addEventListener('input', e => previewThemeColor('themeFont', e.target.value));
    themeFontInput?.addEventListener('change', e => saveThemeColor('themeFont', e.target.value));
    // 底栏独立配色：拖动取色弹窗时先本地预览，change 时由 C# 持久化并重绘。
    const themeBarColorInput = document.getElementById('themeBarColor');
    themeBarColorInput?.addEventListener('input', e => previewTheme({ themeBarColor: e.target.value }));
    themeBarColorInput?.addEventListener('change', async e => {
      await saveTheme({ themeBarColor: e.target.value });
    });
    document.getElementById('themeBarColorReset')?.addEventListener('click', async () => {
      await saveTheme({ themeBarColor: '' });
      renderSettings();
    });
    document.getElementById('themeBarTextFollowTheme')?.addEventListener('change', async e => {
      await saveTheme({ themeBarTextFollowTheme: !!e.target.checked }, true);
    });
    const themeBarTextColorInput = document.getElementById('themeBarTextColor');
    themeBarTextColorInput?.addEventListener('input', e => previewTheme({ themeBarTextColor: e.target.value }));
    themeBarTextColorInput?.addEventListener('change', async e => {
      await saveTheme({ themeBarTextColor: e.target.value });
    });
    document.getElementById('themePresetSave')?.addEventListener('click', async () => {
      const input = document.getElementById('themePresetName');
      const overwrite = !!document.getElementById('themePresetOverwrite')?.checked;
      const currentId = String(state.boot?.themeRotation?.currentPresetId || '');
      const current = (state.boot?.themePresets || []).find(p => String(p.id) === currentId);
      let name = String(input?.value || '').trim();
      if (!name && overwrite && current) name = current.name;
      if (!name) { toast(tr('toast.presetNameRequired', '请先输入预设名称'), 'err'); input?.focus(); return; }
      try {
        await call('theme.presets.save', { name, id: overwrite && currentId ? currentId : '' });
        await syncBoot();
        renderSettings();
        toast(overwrite && currentId ? tr('toast.presetOverwritten', '已覆盖当前主题预设') : tr('toast.presetSaved', '主题预设已保存'));
      } catch (err) { toast(tr('toast.presetSaveFailed', '保存预设失败：{error}', { error: err?.message || err }), 'err'); }
    });
    document.getElementById('themePresetList')?.addEventListener('click', async e => {
      const applyBtn = e.target.closest('[data-theme-preset-apply]');
      const renameBtn = e.target.closest('[data-theme-preset-rename]');
      const deleteBtn = e.target.closest('[data-theme-preset-delete]');
      const upBtn = e.target.closest('[data-theme-preset-up]');
      const downBtn = e.target.closest('[data-theme-preset-down]');
      const row = e.target.closest('[data-theme-preset-id]');
      if (row && !e.target.closest('.theme-preset-actions')) {
        // 点击行固定预览，不重建设置页，避免滚动位置跳动。
        state.themePresetPreviewId = row.dataset.themePresetId || '';
        const preset = (state.boot?.themePresets || []).find(p => String(p.id) === state.themePresetPreviewId);
        const host = document.getElementById('themePresetPreview');
        if (host) host.innerHTML = themePresetPreviewHtml(preset, state.boot?.themeRotation?.currentPresetId);
        return;
      }
      try {
        if (applyBtn) {
          await call('theme.presets.apply', { id: applyBtn.dataset.themePresetApply });
          // 预设切换必须清掉即时预览，否则 localStorage 旧图会盖住新预设背景。
          writeThemeBackgroundLocal('');
          await syncBoot();
          renderSettings();
          toast(tr('toast.presetApplied', '已应用主题预设'));
        } else if (upBtn || downBtn) {
          const id = (upBtn || downBtn).dataset.themePresetUp || (upBtn || downBtn).dataset.themePresetDown;
          const direction = upBtn ? -1 : 1;
          await call('theme.presets.move', { id, direction });
          await syncBoot();
          renderSettings();
          toast(direction < 0 ? tr('toast.presetMovedUp', '预设已上移') : tr('toast.presetMovedDown', '预设已下移'));
        } else if (renameBtn) {
          const preset = (state.boot?.themePresets || []).find(p => String(p.id) === String(renameBtn.dataset.themePresetRename));
          const next = window.prompt(tr('toast.presetRenamePrompt', '新的预设名称'), preset?.name || '');
          if (next == null) return;
          if (!String(next).trim()) { toast(tr('toast.presetNameEmpty', '预设名称不能为空'), 'err'); return; }
          await call('theme.presets.rename', { id: renameBtn.dataset.themePresetRename, name: String(next).trim() });
          await syncBoot();
          renderSettings();
          toast(tr('toast.presetRenamed', '预设已重命名'));
        } else if (deleteBtn) {
          const id = String(deleteBtn.dataset.themePresetDelete || '').trim();
          if (!id) {
            toast(tr('toast.presetMissingId', '主题预设缺少 id，已刷新列表'), 'err');
            await syncBoot();
            renderSettings();
            return;
          }
          const presets = Array.isArray(state.boot?.themePresets) ? state.boot.themePresets : [];
          const removed = presets.find(p => String(p.id) === id);
          const removedIndex = removed ? presets.indexOf(removed) : -1;
          const rotation = state.boot?.themeRotation;
          const previousRotationId = rotation ? rotation.currentPresetId : '';
          // 先本地移除并重绘，点击后立即消失；桥接异常时回滚，后端明确失败时以服务端状态覆盖。
          if (removed) {
            state.boot.themePresets = presets.filter(p => String(p.id) !== id);
            if (rotation && String(rotation.currentPresetId || '') === id) rotation.currentPresetId = '';
            renderSettings();
          }
          let result;
          try {
            result = await call('theme.presets.delete', { id });
          } catch (err) {
            if (removed) {
              state.boot.themePresets.splice(removedIndex, 0, removed);
              if (rotation) rotation.currentPresetId = previousRotationId;
              renderSettings();
            }
            throw err;
          }
          if (result?.ok === false) {
            // 以服务端真实状态为准：写盘失败时预设恢复，已被别处删除时保持消失。
            await syncBoot();
            renderSettings();
            toast(tr('toast.presetDeleteFailed', '主题预设删除失败'), 'err');
            return;
          }
          await syncBoot();
          renderSettings();
          toast(tr('toast.presetDeleted', '主题预设已删除'));
        }
      } catch (err) { toast(tr('toast.presetOpFailed', '主题预设操作失败：{error}', { error: err?.message || err }), 'err'); }
    });
    const presetListHost = document.getElementById('themePresetList');
    const showPresetPreview = id => {
      const preset = (state.boot?.themePresets || []).find(p => String(p.id) === String(id || ''));
      const host = document.getElementById('themePresetPreview');
      if (host) host.innerHTML = themePresetPreviewHtml(preset, state.boot?.themeRotation?.currentPresetId);
    };
    presetListHost?.addEventListener('mouseover', e => {
      const row = e.target.closest('[data-theme-preset-id]');
      if (!row) return;
      showPresetPreview(row.dataset.themePresetId);
    });
    presetListHost?.addEventListener('mouseleave', () => {
      const pinned = state.themePresetPreviewId || state.boot?.themeRotation?.currentPresetId || '';
      showPresetPreview(pinned);
    });
    document.getElementById('themeRotationToggle')?.addEventListener('click', async e => {
      const el = e.currentTarget;
      const next = !el.classList.contains('on');
      if (next && !(state.boot?.themePresets || []).length) {
        toast(tr('toast.needPresetForRotation', '请先保存一个主题预设再开启轮换'), 'err');
        return;
      }
      el.classList.toggle('on', next);
      const interval = Math.max(1, Math.min(1440, Math.round(Number(document.getElementById('themeRotationInterval')?.value) || 30)));
      try {
        await call('theme.rotation.set', { enabled: next, intervalMinutes: interval });
        await syncBoot();
        renderSettings();
        toast(next ? tr('toast.rotationOn', '已开启主题轮换') : tr('toast.rotationOff', '已关闭主题轮换'));
      } catch (err) {
        el.classList.toggle('on', !next);
        toast(tr('toast.rotationFailed', '主题轮换设置失败：{error}', { error: err?.message || err }), 'err');
      }
    });
    document.getElementById('themeRotationInterval')?.addEventListener('change', async e => {
      const interval = Math.max(1, Math.min(1440, Math.round(Number(e.target.value) || 30)));
      e.target.value = interval;
      const enabled = document.getElementById('themeRotationToggle')?.classList.contains('on') || false;
      try {
        await call('theme.rotation.set', { enabled, intervalMinutes: interval });
        await syncBoot();
        renderSettings();
        toast(tr('toast.rotationIntervalUpdated', '轮换间隔已更新'));
      } catch (err) { toast(tr('toast.rotationIntervalFailed', '轮换间隔设置失败：{error}', { error: err?.message || err }), 'err'); }
    });
    const readAdvancedOverrides = () => readThemeAdvancedOverrides(state.boot?.settings);
    const setAdvancedOverride = (key, value, persist) => {
      const overrides = readAdvancedOverrides();
      themeImagePaletteActive = false;
      delete overrides.__imagePalette;
      if (normalizeHexColor(value, null)) overrides[key] = normalizeHexColor(value, '#e7b77c');
      else delete overrides[key];
      const json = JSON.stringify(overrides);
      previewTheme({ themeAdvanced: json });
      if (persist) saveTheme({ themeAdvanced: json });
    };
    document.querySelectorAll('[data-theme-key]').forEach(input => {
      input.addEventListener('input', e => setAdvancedOverride(e.target.dataset.themeKey, e.target.value, false));
      input.addEventListener('change', e => setAdvancedOverride(e.target.dataset.themeKey, e.target.value, true));
    });
    document.getElementById('themeReset')?.addEventListener('click', async () => {
      const patch = { themeAccent: '#e7b77c', themeFont: '#8e5e43', themeAdvanced: '' };
      themeImagePaletteActive = false;
      previewTheme(patch);
      await saveTheme(patch);
      renderSettings();
      toast(tr('toast.themeReset', '已恢复默认主题'));
    });
    const themeOpacity = document.getElementById('themeBgOpacity');
    themeOpacity?.addEventListener('input', e => {
      const pct = Number(e.target.value);
      previewTheme({ themeBackgroundOpacity: pct / 100 });
      const label = document.getElementById('themeBgOpacityVal');
      if (label) label.textContent = pct + '%';
    });
    themeOpacity?.addEventListener('change', e => saveTheme({ themeBackgroundOpacity: Number(e.target.value) }));
    const themeBlur = document.getElementById('themeBgBlur');
    themeBlur?.addEventListener('input', e => {
      const px = Number(e.target.value);
      previewTheme({ themeBackgroundBlur: px });
      const label = document.getElementById('themeBgBlurVal');
      if (label) label.textContent = px + 'px';
    });
    themeBlur?.addEventListener('change', e => saveTheme({ themeBackgroundBlur: Number(e.target.value) }));
    const readThemeFile = file => new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result || ''));
      reader.onerror = () => reject(reader.error || new Error('读取图片失败'));
      reader.readAsDataURL(file);
    });
    // data URL 不经过 settings.set：分片传给 C# 落盘，BootPayload 只回传小体积文件 URL。
    const uploadThemeBackground = async dataUrl => {
      const match = /^data:(image\/[a-z0-9.+-]+);base64,/i.exec(dataUrl);
      if (!match) throw new Error('不支持的图片格式');
      const mime = match[1].toLowerCase();
      const payload = dataUrl.slice(match[0].length);
      const begin = await call('theme.image.begin', { mime });
      const uploadId = begin?.uploadId;
      if (!uploadId) throw new Error('无法开始保存背景图');
      try {
        for (let offset = 0; offset < payload.length; offset += THEME_BG_CHUNK_CHARS) {
          await call('theme.image.chunk', { uploadId, data: payload.slice(offset, offset + THEME_BG_CHUNK_CHARS) });
        }
        return await call('theme.image.commit', { uploadId });
      } catch (err) {
        call('theme.image.cancel', { uploadId }).catch(() => { });
        throw err;
      }
    };
    const applyImagePalette = async source => {
      const button = document.getElementById('themeExtract');
      const oldText = button?.textContent || tr('settings.theme.extract.button', '从图片提取配色');
      if (button) { button.disabled = true; button.textContent = tr('settings.theme.extract.busy', '提取中…'); }
      try {
        const image = await loadImageForSampling(source);
        const colors = sampleImageColors(image);
        const result = buildPaletteFromImageColors(colors);
        const overrides = { ...result.overrides, __imagePalette: true };
        themeImagePaletteActive = true;
        const patch = {
          themeAccent: result.accent,
          themeFont: result.font,
          themeAdvanced: JSON.stringify(overrides),
        };
        previewTheme(patch);
        const saved = await saveTheme(patch, true);
        if (!saved) return false;
        toast(tr('toast.imagePaletteApplied', '已根据背景图生成配色'));
        return true;
      } catch (err) {
        toast(tr('toast.imagePaletteFailed', '图片配色提取失败：{error}', { error: err?.message || err }), 'err');
        return false;
      } finally {
        const current = document.getElementById('themeExtract');
        if (current) {
          current.textContent = oldText;
          current.disabled = !themeBackgroundIsSet(state.boot?.settings || {});
        }
      }
    };
    const maybeAutoApplyImagePalette = async source => {
      if (!autoImagePaletteEnabled()) return false;
      if (hasManualAdvancedPalette(state.boot?.settings)) {
        toast(tr('toast.manualPaletteKept', '检测到手动高级配色，已保留；可点击「从图片提取配色」覆盖'));
        return false;
      }
      return await applyImagePalette(source);
    };
    document.getElementById('themeExtract')?.addEventListener('click', async () => {
      const source = themeBackgroundSource(state.boot?.settings || {});
      if (!source) { toast(tr('toast.chooseBgFirst', '请先选择背景图'), 'err'); return; }
      await applyImagePalette(source);
    });
    document.getElementById('themeAutoPalette')?.addEventListener('change', async e => {
      const enabled = !!e.target.checked;
      setAutoImagePaletteEnabled(enabled);
      if (!enabled) return;
      const source = themeBackgroundSource(state.boot?.settings || {});
      if (source) await maybeAutoApplyImagePalette(source);
    });
    document.getElementById('themeBgFile')?.addEventListener('change', async e => {
      const file = e.target.files?.[0];
      if (!file) return;
      if (file.size > THEME_BG_MAX_BYTES) {
        toast(tr('toast.bgTooLarge', '背景图不能超过 20MB'), 'err');
        e.target.value = '';
        return;
      }
      if (!/^image\/(?:png|jpe?g|webp|gif)$/i.test(file.type || '')) {
        toast(tr('toast.bgFormat', '仅支持 PNG / JPG / WebP / GIF 背景图'), 'err');
        e.target.value = '';
        return;
      }
      try {
        let dataUrl;
        let compressed = null;
        if (file.size > THEME_BG_COMPRESS_BYTES) {
          toast(tr('toast.bgCompressing', '图片超过 4MB，正在压缩到主题缓存…'));
          compressed = await compressThemeImage(file);
          dataUrl = compressed.dataUrl;
        } else {
          dataUrl = await readThemeFile(file);
        }
        // 先写 localStorage 让当前面板立即显示；大图只写压缩后的版本，绝不写原始大图。
        const previousServerImage = state.boot?.settings?.themeBackgroundImage || '';
        writeThemeBackgroundLocal(dataUrl, previousServerImage);
        applyTheme(state.boot?.settings || {});
        updateThemeBackgroundControls();
        try {
          const result = await uploadThemeBackground(dataUrl);
          if (state.boot?.settings) {
            state.boot.settings.themeBackgroundImage = result?.url || '';
            state.boot.settings.themeBackgroundImageSet = !!result?.url;
          }
          if (state.boot?.themeRotation) state.boot.themeRotation.currentPresetId = '';
          // 上传成功后记录这张预览对应的服务端 URL；后续 boot 看到相同 URL 不会误清。
          writeThemeBackgroundLocal(dataUrl, result?.url || previousServerImage);
          applyTheme(state.boot?.settings || {});
          updateThemeBackgroundControls();
          toast(compressed
            ? tr('toast.bgSavedCompressed', '背景图已压缩并保存（{from} → {to}）', { from: formatBytes(file.size), to: formatBytes(compressed.bytes) })
            : tr('toast.bgSaved', '背景图已保存'));
        } catch (err) {
          // C# 落盘失败时 localStorage 仍能保证本次/下次启动显示；提示但不回滚预览。
          toast(tr('toast.bgBackupFailed', '背景图已应用，但备份保存失败：{error}', { error: err?.message || err }), 'err');
        }
        // 没有手动高级配色时，选图后自动根据图片生成整套 palette。
        await maybeAutoApplyImagePalette(dataUrl);
      } catch (err) {
        toast(tr('toast.bgReadFailed', '读取背景图失败：{error}', { error: err?.message || err }), 'err');
      } finally {
        e.target.value = '';
      }
    });
    document.getElementById('themeBgClear')?.addEventListener('click', async () => {
      try {
        await call('theme.image.clear');
      } catch (err) {
        toast(tr('toast.bgRemoveFailed', '移除背景图失败：{error}', { error: err?.message || err }), 'err');
        return;
      }
      writeThemeBackgroundLocal('');
      if (state.boot?.settings) {
        state.boot.settings.themeBackgroundImage = '';
        state.boot.settings.themeBackgroundImageSet = false;
      }
      if (state.boot?.themeRotation) state.boot.themeRotation.currentPresetId = '';
      applyTheme(state.boot?.settings || {});
      updateThemeBackgroundControls();
      const input = document.getElementById('themeBgFile');
      if (input) input.value = '';
    });
    scaleRange?.addEventListener('input', () => {
      const pct = Number(scaleRange.value);
      if (scaleVal) scaleVal.textContent = pct + '%';
      syncScaleSelect(pct);
      // 拖动时约每 80ms 应用一次，既能实时缩放，也不会每移动 1% 就写一次配置。
      const now = performance.now();
      if (now - lastScaleSent >= 80) {
        lastScaleSent = now;
        applyScale(pct, false);
      }
    });
    scaleRange?.addEventListener('change', () => {
      lastScaleSent = performance.now();
      applyScale(Number(scaleRange.value), true);
    });
    const remoteFollow = document.getElementById('remoteFollowMain');
    const remoteScaleSel = document.getElementById('setRemoteScale');
    const remoteScaleRange = document.getElementById('setRemoteScaleRange');
    const remoteScaleVal = document.getElementById('remoteScaleVal');
    const remoteScalePresets = [25, 50, 75, 100, 125, 150, 200];
    let lastRemoteScaleSent = 0;
    const syncRemoteScaleSelect = (pct) => {
      if (!remoteScaleSel) return;
      remoteScaleSel.value = remoteScalePresets.includes(pct) ? String(pct / 100) : 'custom';
    };
    const setRemoteControlsEnabled = (follow) => {
      if (remoteScaleSel) remoteScaleSel.disabled = follow;
      if (remoteScaleRange) remoteScaleRange.disabled = follow;
    };
    const applyRemoteScale = async (pct, final = false) => {
      pct = Math.max(25, Math.min(200, Math.round(Number(pct) || 100)));
      if (remoteScaleVal) remoteScaleVal.textContent = pct + '%';
      if (remoteScaleRange) remoteScaleRange.value = String(pct);
      syncRemoteScaleSelect(pct);
      if (state.boot?.settings) {
        state.boot.settings.remoteScale = pct / 100;
        state.boot.settings.remoteFollowMain = false;
      }
      try {
        const boot = await tryCall('settings.set', { remoteScale: pct / 100, remoteFollowMain: false });
        if (boot?.settings) state.boot = boot;
        if (final) renderChrome();
      } catch { }
    };
    remoteFollow?.addEventListener('change', async () => {
      const follow = !!remoteFollow.checked;
      setRemoteControlsEnabled(follow);
      if (follow) {
        try {
          const boot = await tryCall('settings.set', { remoteFollowMain: true });
          if (boot?.settings) state.boot = boot;
          renderSettings();
        } catch { }
        return;
      }
      const mainPct = Math.max(25, Math.min(200, Math.round((Number(state.boot?.settings?.scale) || 1) * 100)));
      // 从“跟随主角”切到自定义时，以当前主角大小作为起点，避免滑条突然跳到旧值。
      if (remoteScaleRange) remoteScaleRange.value = String(mainPct);
      if (remoteScaleVal) remoteScaleVal.textContent = mainPct + '%';
      syncRemoteScaleSelect(mainPct);
      try {
        const boot = await tryCall('settings.set', { remoteFollowMain: false, remoteScale: mainPct / 100 });
        if (boot?.settings) state.boot = boot;
        renderSettings();
      } catch { }
    });
    remoteScaleSel?.addEventListener('change', e => {
      if (e.target.value === 'custom') return;
      lastRemoteScaleSent = performance.now();
      applyRemoteScale(Number(e.target.value) * 100, true);
    });
    remoteScaleRange?.addEventListener('input', () => {
      const pct = Number(remoteScaleRange.value);
      if (remoteScaleVal) remoteScaleVal.textContent = pct + '%';
      syncRemoteScaleSelect(pct);
      const now = performance.now();
      if (now - lastRemoteScaleSent >= 80) {
        lastRemoteScaleSent = now;
        applyRemoteScale(pct, false);
      }
    });
    remoteScaleRange?.addEventListener('change', () => {
      lastRemoteScaleSent = performance.now();
      applyRemoteScale(Number(remoteScaleRange.value), true);
    });
    const captureW = document.getElementById('captureW');
    const captureH = document.getElementById('captureH');
    const applyCaptureSize = async () => {
      if (!captureW || !captureH) return;
      const w = Math.min(1600, Math.max(160, Math.round(Number(captureW.value) || 400)));
      const h = Math.min(1600, Math.max(160, Math.round(Number(captureH.value) || 700)));
      captureW.value = w; captureH.value = h;
      try { await tryCall('settings.set', { captureWidth: w, captureHeight: h }); await syncBoot(); } catch { }
    };
    captureW?.addEventListener('change', applyCaptureSize);
    captureH?.addEventListener('change', applyCaptureSize);
    const captureColor = document.getElementById('captureColor');
    const captureColorHex = document.getElementById('captureColorHex');
    const normalizeCaptureColor = raw => /^#[0-9a-f]{6}$/i.test(String(raw || '').trim())
      ? String(raw).trim().toUpperCase() : '#00FF00';
    const applyCaptureColor = async raw => {
      const value = normalizeCaptureColor(raw);
      if (captureColor) captureColor.value = value.toLowerCase();
      if (captureColorHex) captureColorHex.value = value;
      try {
        await tryCall('settings.set', { captureColor: value });
        await syncBoot();
      } catch { }
    };
    captureColor?.addEventListener('change', e => applyCaptureColor(e.target.value));
    captureColorHex?.addEventListener('change', e => applyCaptureColor(e.target.value));
    document.querySelectorAll('[data-capture-color]').forEach(b => b.onclick = () => applyCaptureColor(b.dataset.captureColor));
    const remoteFollowOpacity = document.getElementById('remoteFollowMainOpacity');
    const remoteOpacityRange = document.getElementById('setRemoteOpacityRange');
    const remoteOpacityVal = document.getElementById('remoteOpacityVal');
    let lastRemoteOpacitySent = 0;
    const syncRemoteOpacityUi = (pct) => {
      if (remoteOpacityRange) remoteOpacityRange.value = String(pct);
      if (remoteOpacityVal) remoteOpacityVal.textContent = pct + '%';
    };
    const applyRemoteOpacity = async (pct, final = false) => {
      pct = Math.max(20, Math.min(100, Math.round(Number(pct) || 100)));
      syncRemoteOpacityUi(pct);
      if (state.boot?.settings) {
        state.boot.settings.remoteOpacity = pct / 100;
        state.boot.settings.remoteFollowMainOpacity = false;
      }
      try {
        const boot = await tryCall('settings.set', { remoteOpacity: pct / 100, remoteFollowMainOpacity: false });
        if (boot?.settings) state.boot = boot;
        if (final) renderChrome();
      } catch { }
    };
    remoteFollowOpacity?.addEventListener('change', async () => {
      const follow = !!remoteFollowOpacity.checked;
      if (remoteOpacityRange) remoteOpacityRange.disabled = follow;
      if (follow) {
        try {
          const boot = await tryCall('settings.set', { remoteFollowMainOpacity: true });
          if (boot?.settings) state.boot = boot;
          renderSettings();
        } catch { }
        return;
      }
      const mainPct = Math.max(20, Math.min(100, Math.round((Number(state.boot?.settings?.opacity) || 1) * 100)));
      syncRemoteOpacityUi(mainPct);
      try {
        const boot = await tryCall('settings.set', { remoteFollowMainOpacity: false, remoteOpacity: mainPct / 100 });
        if (boot?.settings) state.boot = boot;
        renderSettings();
      } catch { }
    });
    remoteOpacityRange?.addEventListener('input', () => {
      const pct = Math.max(20, Math.min(100, Math.round(Number(remoteOpacityRange.value) || 100)));
      if (remoteOpacityVal) remoteOpacityVal.textContent = pct + '%';
      const now = performance.now();
      if (now - lastRemoteOpacitySent >= 80) {
        lastRemoteOpacitySent = now;
        applyRemoteOpacity(pct, false);
      }
    });
    remoteOpacityRange?.addEventListener('change', () => {
      lastRemoteOpacitySent = performance.now();
      applyRemoteOpacity(Number(remoteOpacityRange.value), true);
    });
    const remoteNameScale = document.getElementById('setRemoteNameScale');
    const remoteNameScaleVal = document.getElementById('remoteNameScaleVal');
    remoteNameScale?.addEventListener('input', () => {
      if (remoteNameScaleVal) remoteNameScaleVal.textContent = remoteNameScale.value + '%';
    });
    remoteNameScale?.addEventListener('change', async () => {
      const pct = Math.max(50, Math.min(200, Math.round(Number(remoteNameScale.value) || 100)));
      if (remoteNameScaleVal) remoteNameScaleVal.textContent = pct + '%';
      try {
        const boot = await tryCall('settings.set', { remoteNameScale: pct / 100 });
        if (boot?.settings) state.boot = boot;
      } catch { }
    });
    const op = document.getElementById('setOpacity');
    op?.addEventListener('input', () => {
      const pct = Math.max(20, Math.min(100, Math.round(Number(op.value) || 100)));
      const label = document.getElementById('opacityVal');
      if (label) label.textContent = pct + '%';
      if (remoteFollowOpacity?.checked) syncRemoteOpacityUi(pct);
    });
    op?.addEventListener('change', async () => {
      try { await tryCall('settings.set', { opacity: Number(op.value) / 100 }); await syncBoot(); } catch { }
    });
    const hp = document.getElementById('setHoverOpacity');
    hp?.addEventListener('input', () => { document.getElementById('hoverOpacityVal').textContent = hp.value + '%'; });
    hp?.addEventListener('change', async () => {
      try { await tryCall('settings.set', { hoverFadeOpacity: Number(hp.value) / 100 }); await syncBoot(); } catch { }
    });
    const gameRuleInput = document.getElementById('gameRuleInput');
    const saveGameRules = async (rules) => {
      try {
        const boot = await tryCall('settings.set', { gameModeRules: rules });
        if (boot?.settings) state.boot = boot;
        renderSettings();
      } catch { }
    };
    const addGameRule = () => {
      const name = String(gameRuleInput?.value || '').trim();
      if (!name) return;
      const rules = [...(state.boot?.settings?.gameModeRules || []), name];
      if (gameRuleInput) gameRuleInput.value = '';
      saveGameRules(rules);
    };
    document.getElementById('gameRuleAdd')?.addEventListener('click', addGameRule);
    gameRuleInput?.addEventListener('keydown', e => {
      if (e.key === 'Enter') { e.preventDefault(); addGameRule(); }
    });
    document.querySelectorAll('[data-remove-rule]').forEach(el => {
      el.addEventListener('click', () => {
        const name = el.dataset.removeRule;
        saveGameRules((state.boot?.settings?.gameModeRules || []).filter(x => x !== name));
      });
    });

    document.querySelectorAll('[data-switch]').forEach(el => el.onclick = async () => {
      const key = el.dataset.switch;
      const next = !el.classList.contains('on');
      el.classList.toggle('on', next);
      if (key === 'runAsAdmin' && next) toast(tr('toast.adminRequesting', '正在请求管理员权限…'));
      try {
        await tryCall('settings.set', { [key]: next });
        await syncBoot();
        if (key === 'rhythmMode' || key === 'autoEmoteEnabled' || key === 'autoClickEnabled' || key === 'themeBarFollowStandard' || key === 'separateBarHotkeys') { renderSettings(); return; }
        if (key === 'runAsAdmin') {
          const saved = !!state.boot?.settings?.runAsAdmin;
          const elevated = !!state.boot?.settings?.isElevated;
          renderSettings();
          if (next && !saved) toast(tr('toast.adminCancelled', '已取消管理员授权，继续使用普通权限'), 'err');
          else if (!next && elevated) toast(tr('toast.adminDisabledRestart', '已关闭，重启后以普通权限运行'));
          else if (next && elevated) toast(tr('toast.adminEnabled', '已启用管理员权限启动'));
        }
      } catch { el.classList.toggle('on', !next); }
    });
    const chestMatrix = document.getElementById('chestNotifyMatrix');
    if (chestMatrix) {
      const collect = () => {
        const result = {};
        chestMatrix.querySelectorAll('[data-chest-rarity]').forEach(row => {
          const rarity = row.dataset.chestRarity;
          const rule = { enabled: false, character: false, accessory: false, emote: false };
          row.querySelectorAll('[data-chest-notify]').forEach(box => { rule[box.dataset.chestNotify] = !!box.checked; });
          result[rarity] = rule;
        });
        return result;
      };
      chestMatrix.querySelectorAll('[data-chest-notify]').forEach(box => {
        box.onchange = async () => {
          try {
            const boot = await tryCall('settings.set', { chestNotifyRules: collect() });
            if (boot?.settings) state.boot = boot;
          } catch { }
        };
      });
    }
    document.querySelectorAll('[data-kb]').forEach(el => el.onclick = async () => {
      try { await tryCall('settings.set', { keyboardMode: el.dataset.kb }); await syncBoot(); renderSettings(); } catch { }
    });
    const bindAutoRate = (id, max, key, fallback) => {
      const el = document.getElementById(id);
      if (!el) return;
      let timer = 0;
      const save = async () => {
        clearTimeout(timer);
        const value = Math.max(1, Math.min(max, Math.round(Number(el.value) || fallback)));
        el.value = value;
        try { await tryCall('settings.set', { [key]: value }); await syncBoot(); } catch { }
      };
      el.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(save, 400); });
      el.addEventListener('change', save);
      el.addEventListener('blur', save);
    };
    bindAutoRate('autoEmoteRate', 20, 'autoEmoteMaxPerSecond', 20);
    document.getElementById('autoEmoteMode')?.addEventListener('change', async e => {
      try { await tryCall('settings.set', { autoEmoteMode: e.target.value }); await syncBoot(); renderSettings(); } catch { }
    });
    document.getElementById('autoEmoteId')?.addEventListener('change', async e => {
      try { await tryCall('settings.set', { autoEmoteId: Number(e.target.value) || 0 }); await syncBoot(); } catch { }
    });
    bindAutoRate('autoClickRate', 10, 'autoClickPerSecond', 10);
    const accXRange = document.getElementById('accOffsetX');
    const accYRange = document.getElementById('accOffsetY');
    const accScaleRange = document.getElementById('accScale');
    const accRotationRange = document.getElementById('accRotation');
    const accReadout = document.getElementById('accessoryCoords');
    let accSliderTimer = 0;
    let accSaveGeneration = 0;
    const accClamp = (value, min, max, fallback) => {
      const n = Number(value);
      return Number.isFinite(n) ? Math.max(min, Math.min(max, Math.round(n))) : fallback;
    };
    const accControls = () => [accXRange, accYRange, accScaleRange, accRotationRange].filter(Boolean);
    const readAccSliders = () => ({
      x: accClamp(accXRange?.value, -160, 160, 0),
      y: accClamp(accYRange?.value, -160, 160, 0),
      scale: accClamp(accScaleRange?.value, 25, 175, 100),
      rotation: accClamp(accRotationRange?.value, -180, 180, 0),
    });
    const updateAccReadout = () => {
      if (!accXRange || !accYRange) return;
      const { x, y, scale, rotation } = readAccSliders();
      const xValue = document.getElementById('accOffsetXValue');
      const yValue = document.getElementById('accOffsetYValue');
      const scaleValue = document.getElementById('accScaleValue');
      const rotationValue = document.getElementById('accRotationValue');
      if (xValue) xValue.textContent = String(x);
      if (yValue) yValue.textContent = String(y);
      if (scaleValue) scaleValue.textContent = `${scale}%`;
      if (rotationValue) rotationValue.textContent = `${rotation}°`;
      if (accReadout) accReadout.textContent = `X ${x} · Y ${y} · ${scale}% · ${rotation}°`;
      const resetButton = document.getElementById('resetAccessoryOffset');
      if (resetButton) {
        const accId = Number(state.boot?.appearance?.accessoryAssetId || 0);
        resetButton.disabled = !(accId > 0 && (x !== 0 || y !== 0 || scale !== 100 || rotation !== 0));
      }
    };
    const applyAccSliders = (transform) => {
      if (!transform) return;
      if (accXRange && transform.x != null) accXRange.value = String(accClamp(transform.x, -160, 160, 0));
      if (accYRange && transform.y != null) accYRange.value = String(accClamp(transform.y, -160, 160, 0));
      if (accScaleRange && transform.scale != null) accScaleRange.value = String(accClamp(transform.scale, 25, 175, 100));
      if (accRotationRange && transform.rotation != null) accRotationRange.value = String(accClamp(transform.rotation, -180, 180, 0));
      updateAccReadout();
    };
    // 程序化复位/微调后必须清掉脏标记，否则 renderSettings 的旧值恢复会把滑条改回旧位置。
    const clearAccDirty = () => {
      for (const el of accControls()) delete el.dataset.userDirty;
    };
    const saveAccOffset = async () => {
      if (!accXRange || !accYRange || !accScaleRange || !accRotationRange) return;
      clearTimeout(accSliderTimer);
      const submitted = readAccSliders();
      const generation = ++accSaveGeneration;
      try {
        const r = await tryCall('appearance.offset', submitted);
        if (generation !== accSaveGeneration) return;
        if (r?.boot) state.boot = r.boot;
        const current = readAccSliders();
        if (current.x === submitted.x && current.y === submitted.y &&
            current.scale === submitted.scale && current.rotation === submitted.rotation) {
          applyAccSliders({ x: r?.x, y: r?.y, scale: r?.scale, rotation: r?.rotation });
          clearAccDirty();
        } else {
          updateAccReadout();
        }
      } catch { }
    };
    const queueAccOffset = () => {
      updateAccReadout();
      clearTimeout(accSliderTimer);
      accSliderTimer = setTimeout(saveAccOffset, 80);
    };
    for (const el of accControls()) {
      el.addEventListener('input', queueAccOffset);
      // range 的 change 在松手/按键结束时提交；不再额外监听 blur，避免点“归零”时先发一次旧值保存。
      el.addEventListener('change', saveAccOffset);
    }
    document.querySelectorAll('[data-nudge]').forEach(el => el.onclick = async () => {
      clearTimeout(accSliderTimer);
      ++accSaveGeneration;
      const [dx, dy] = String(el.dataset.nudge || '0,0').split(',').map(Number);
      try {
        const r = await tryCall('appearance.offset', { dx, dy });
        if (r?.boot) state.boot = r.boot;
        applyAccSliders({ x: r?.x, y: r?.y, scale: r?.scale, rotation: r?.rotation });
        clearAccDirty();
        renderChrome();
        renderSettings();
      } catch { }
    });
    document.getElementById('resetAccessoryOffset')?.addEventListener('click', async () => {
      clearTimeout(accSliderTimer);
      ++accSaveGeneration;
      try {
        const r = await tryCall('appearance.offset', { reset: true });
        if (r?.boot) state.boot = r.boot;
        // 先让当前 DOM 立即归零并清除脏标记，再按最新 BootPayload 重绘设置页。
        applyAccSliders({ x: r?.x ?? 0, y: r?.y ?? 0, scale: r?.scale ?? 100, rotation: r?.rotation ?? 0 });
        clearAccDirty();
        renderChrome();
        renderSettings();
      } catch { }
    });
    const rhythmInputs = [...document.querySelectorAll('[data-rhythm-key]')];
    const rhythmFullwidth = {
      '，': ',', '。': '.', '；': ';', '、': ',', '／': '/',
      '［': '[', '］': ']', '－': '-', '＝': '=', '＼': '\\',
      '‘': "'", '’': "'",
    };
    const rhythmNormalize = (value) => {
      let v = String(value || '').trim();
      if (v === ' ' || v === '␣' || /^space$/i.test(v)) return '␣';
      if (v.length > 1) v = v.slice(-1);
      v = rhythmFullwidth[v] || v;
      if (/^[A-Za-z0-9]$/.test(v)) return v.toUpperCase();
      if (";',./[]=-`\\".includes(v)) return v;
      return '';
    };
    const rhythmToken = (value) => rhythmNormalize(value) === '␣' ? 'SPACE' : rhythmNormalize(value);
    const rhythmCodeMap = {
      Space: '␣', Semicolon: ';', Quote: "'", Comma: ',', Period: '.', Slash: '/',
      BracketLeft: '[', BracketRight: ']', Minus: '-', Equal: '=', Backquote: '`', Backslash: '\\'
    };
    const rhythmFromEvent = (e) => {
      if (rhythmCodeMap[e.code]) return rhythmCodeMap[e.code];
      if (/^Key[A-Z]$/.test(e.code)) return e.code.slice(3);
      if (/^Digit[0-9]$/.test(e.code)) return e.code.slice(5);
      return null;
    };
    rhythmInputs.forEach(input => {
      let composing = false;
      input.addEventListener('focus', () => input.select());
      input.addEventListener('compositionstart', () => { composing = true; });
      input.addEventListener('compositionend', () => {
        composing = false;
        input.value = rhythmNormalize(input.value);
        input.dispatchEvent(new Event('change', { bubbles: true }));
      });
      input.addEventListener('keydown', e => {
        // 中文输入法组合期间不要抢按键，否则会被 e.code 误判成字母。
        if (e.isComposing || e.keyCode === 229) return;
        if (e.ctrlKey || e.altKey || e.metaKey) return;
        const value = rhythmFromEvent(e);
        if (value === null) return;
        // 直接按键替换当前键位，不需要先删除原值。
        e.preventDefault();
        input.value = value;
        input.dispatchEvent(new Event('input', { bubbles: true }));
        input.dispatchEvent(new Event('change', { bubbles: true }));
      });
      input.addEventListener('input', () => { if (!composing) input.value = rhythmNormalize(input.value); });
      input.addEventListener('change', async () => {
        const keys = rhythmInputs.map(x => rhythmToken(x.value));
        if (keys.some(k => !(k === 'SPACE' || /^[A-Z0-9]$/.test(k) || ";',./[]=-`\\".includes(k)))) return toast(tr('toast.rhythmKeyInvalid', '音游键位包含不支持的字符'), 'err');
        if (new Set(keys).size !== keys.length) return toast(tr('toast.rhythmKeyDuplicate', '触发键位不能重复'), 'err');
        try {
          await tryCall('settings.set', { rhythmKeys: keys });
          await syncBoot();
          renderSettings();
          toast(tr('toast.rhythmKeySaved', '音游键位已保存'));
        } catch { }
      });
    });
    document.getElementById('setRhythmKeyCount')?.addEventListener('change', async e => {
      try {
        await tryCall('settings.set', { rhythmKeyCount: Number(e.target.value) });
        await syncBoot();
        renderSettings();
      } catch { }
    });
    document.getElementById('openLog')?.addEventListener('click', () => call('open.log'));
    document.getElementById('announcementHistoryBtn')?.addEventListener('click', showAnnouncementHistory);
    document.getElementById('aboutBtn')?.addEventListener('click', showAbout);
    document.getElementById('updateChannel')?.addEventListener('change', async e => {
      const select = e.target;
      const value = select.value === 'beta' ? 'beta' : 'stable';
      try {
        const boot = await tryCall('settings.set', { updateChannel: value });
        if (boot?.settings) state.boot = boot;
        // 切通道后清掉上一个通道的更新横幅，并按新通道重新检查一次。
        state.update = null;
        state.updateDownload = null;
        state.updateDismissed = false;
        const check = await tryCall('update.check');
        if (check) state.update = check.update || null;
        renderSettings();
        toast(value === 'beta' ? tr('toast.channelBeta', '更新通道：测试版（含后续测试版）') : tr('toast.channelStable', '更新通道：正式版'));
      } catch {
        select.value = state.boot?.settings?.updateChannel || 'stable';
        renderSettings();
      }
    });
    document.getElementById('updateDownloadSource')?.addEventListener('change', async e => {
      const select = e.target;
      const value = select.value;
      try {
        const boot = await tryCall('settings.set', { updateDownloadSource: value });
        if (boot?.settings) state.boot = boot;
        toast(value === 'auto' ? tr('toast.sourceAuto', '下载源：自动（GH-Proxy → GitHub）') : (value === 'ghproxy' ? tr('toast.sourceGhproxy', '下载源：GH-Proxy') : tr('toast.sourceGithub', '下载源：GitHub 直连')));
      } catch {
        select.value = state.boot?.settings?.updateDownloadSource || 'auto';
      }
    });
    const proxyToggle = document.getElementById('updateUseProxy');
    const proxyInput = document.getElementById('updateHttpProxy');
    let proxySaveTimer = 0;
    const syncProxyInput = () => {
      const enabled = !!proxyToggle?.classList.contains('on');
      if (proxyInput) proxyInput.disabled = !enabled;
      document.getElementById('updateProxyItem')?.classList.toggle('hidden', !enabled);
    };
    const saveProxyValue = async () => {
      clearTimeout(proxySaveTimer);
      if (!proxyToggle?.classList.contains('on') || !proxyInput) return;
      const value = proxyInput.value.trim();
      if (value) {
        try {
          const parsed = new URL(value);
          if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') throw new Error('scheme');
        } catch {
          return toast(tr('toast.proxyFormat', '代理格式应为 http://host:port 或 https://host:port'), 'err');
        }
      }
      proxyInput.value = value;
      try {
        const boot = await tryCall('settings.set', { updateHttpProxy: value });
        if (boot?.settings) state.boot = boot;
        toast(value ? tr('toast.proxySaved', 'HTTP 代理已自动保存') : tr('toast.proxyCleared', 'HTTP 代理地址已清空'));
      } catch { }
    };
    if (proxyToggle) {
      syncProxyInput();
      proxyToggle.onclick = async () => {
        const next = !proxyToggle.classList.contains('on');
        proxyToggle.classList.toggle('on', next);
        syncProxyInput();
        try {
          const boot = await tryCall('settings.set', { updateUseProxy: next });
          if (boot?.settings) state.boot = boot;
        } catch {
          proxyToggle.classList.toggle('on', !next);
          syncProxyInput();
        }
      };
    }
    proxyInput?.addEventListener('input', () => {
      clearTimeout(proxySaveTimer);
      proxySaveTimer = setTimeout(saveProxyValue, 450);
    });
    proxyInput?.addEventListener('change', saveProxyValue);
    proxyInput?.addEventListener('blur', saveProxyValue);
    document.getElementById('updateDownloadBtn')?.addEventListener('click', async () => {
      const btn = document.getElementById('updateDownloadBtn');
      if (btn) { btn.disabled = true; btn.textContent = tr('toast.prepareDownload', '准备下载…'); }
      state.updateDownload = { phase: 'starting', tag: state.update?.tag || '', message: tr('toast.preparingDownload', '正在准备下载…') };
      renderSettings();
      try {
        await tryCall('update.download');
        toast(tr('toast.downloadStarted', '开始下载更新'));
      } catch {
        state.updateDownload = null;
        renderSettings();
      }
    });
    document.getElementById('updateCancelBtn')?.addEventListener('click', async () => {
      try { await tryCall('update.cancel'); } catch { }
    });
    document.getElementById('updateInstallBtn')?.addEventListener('click', () => {
      modal(`<div class="about">
        <div class="about-title">${tr('update.installTitle', '安装更新')}</div>
        <div class="about-note"><p>${tr('update.installBody', '安装包已下载并通过 SHA-256 校验。将启动安装程序，当前程序可能需要关闭。')}</p></div>
        <div style="display:flex;gap:10px;justify-content:flex-end;margin-top:12px">
          <button class="btn ghost" id="updateInstallCancel">${tr('update.later', '稍后')}</button>
          <button class="btn" id="updateInstallConfirm">${tr('update.installNow', '立即安装')}</button>
        </div>
      </div>`, host => {
        host.querySelector('#updateInstallCancel').onclick = closeModal;
        host.querySelector('#updateInstallConfirm').onclick = async () => {
          try {
            await tryCall('update.install');
            closeModal();
            toast(tr('toast.installStarting', '正在启动安装程序…'));
          } catch { }
        };
      });
    });
    document.getElementById('checkUpdateBtn')?.addEventListener('click', async () => {
      const btn = document.getElementById('checkUpdateBtn');
      btn.disabled = true;
      btn.textContent = tr('toast.checking', '检查中…');
      try {
        const r = await tryCall('update.check');
        state.update = r?.update || null;
        state.updateDismissed = false;
        renderSettings();
        if (state.update) {
          toast(tr('toast.updateFound', '发现新版本 {tag}', { tag: state.update.tag || '' }));
          requestAnimationFrame(() => { const scroller = document.querySelector('.settings-scroll'); if (scroller) scroller.scrollTop = 0; });
        } else {
          toast(tr('toast.upToDate', '当前已经是最新版本'));
        }
      } catch {
        btn.disabled = false;
        btn.textContent = tr('settings.footer.checkUpdate', '检查更新');
      }
    });
    document.getElementById('compatRefreshBtn')?.addEventListener('click', async () => {
      const btn = document.getElementById('compatRefreshBtn');
      btn.disabled = true;
      btn.textContent = tr('toast.refreshing', '刷新中…');
      try {
        const r = await tryCall('compatibility.check');
        if (r) {
          state.compatibility = r;
          renderNotices();
          renderSettings();
          toast(tr('toast.compatUpdated', '版本状态已更新'));
        } else {
          toast(tr('toast.compatUnavailable', '暂时无法获取版本状态'), 'err');
        }
      } catch {
        toast(tr('toast.compatFailed', '版本状态刷新失败'), 'err');
      } finally {
        btn.disabled = false;
        btn.textContent = tr('settings.footer.compatRefresh', '刷新版本状态');
      }
    });
    bindHotkeys();
    bindAuthActions(document.getElementById('pageHost'));
    document.getElementById('redeemBtn')?.addEventListener('click', async () => {
      const input = document.getElementById('redeemCode');
      const code = input.value.trim();
      if (!code) return toast(tr('toast.redeemEmpty', '请输入兑换码'), 'err');
      try {
        await redeemCode(code);
        input.value = '';
      } catch { }
    });
    document.getElementById('logoutBtn')?.addEventListener('click', async () => {
      try {
        await tryCall('logout');
        // 退出后必须重新加载 WebView，清空背包/图鉴/预设等上一个账号的页面缓存。
        window.location.reload();
      } catch { }
    });
    document.getElementById('userRefresh')?.addEventListener('click', async () => {
      try { state.boot = await tryCall('user.refresh'); renderChrome(); renderSettings(); toast(tr('toast.refreshed', '已刷新')); } catch { }
    });
    const boost = document.getElementById('boostClicks');
    if (boost) {
      let busy = false;
      boost.onclick = async () => {
        if (busy) return;
        busy = true;
        boost.disabled = true;
        const started = performance.now();
        try {
          const r = await tryCall('clicks.boost', { delta: 10000 });
          if (r?.accepted) {
            applyClicks(r);
            toast(tr('toast.clicksQueued', '已加入待上报，联网后自动同步'));
          } else {
            toast(tr('toast.tooFast', '操作太快，请稍后再试'), 'err');
          }
        } catch { }
        // 原生层也有 500ms 限流；按钮保持禁用相同时间，避免连点堆积。
        const wait = Math.max(0, 550 - (performance.now() - started));
        setTimeout(() => {
          busy = false;
          if (boost.isConnected) boost.disabled = false;
        }, wait);
      };
    }
    document.getElementById('setLocale')?.addEventListener('change', async e => {
      const val = ['zh', 'en', 'ja'].includes(e.target.value) ? e.target.value : 'zh';
      const previous = state.boot?.settings?.locale || 'zh';
      const previousBoot = state.boot;
      // 先立即切换界面，后端目录重载放到后台；失败时再回滚下拉框和界面。
      applyLocale(val);
      renderChrome();
      renderSettings();
      try {
        const boot = await tryCall('settings.set', { locale: val });
        if (!boot?.settings) throw new Error('locale save failed');
        state.boot = boot;
        // 图鉴/背包名称来自后端 Catalog，切换语言后必须丢掉旧快照重新拉取。
        state.bag = null;
        state.atlas = {};
        state.atlasSnapshot = null;
        state.atlasAllCount = 0;
        renderChrome();
        renderSettings();
        toast(tr('common.languageChanged', '界面语言已切换'));
      } catch {
        applyLocale(previous);
        if (previousBoot) state.boot = previousBoot;
        e.target.value = previous;
        renderChrome();
        renderSettings();
      }
    });
    document.getElementById('setServerRegion')?.addEventListener('change', async e => {
      const val = e.target.value;
      if (val !== (state.boot?.settings?.serverRegion || 'cn')) {
        const boot = await tryCall('settings.set', { serverRegion: val });
        if (boot?.settings) state.boot = boot;
        renderSettings();
      }
    });
    document.querySelectorAll('#setAnimationFps [data-fps]').forEach(btn => {
      btn.onclick = async () => {
        const fps = Number(btn.dataset.fps);
        try {
          await tryCall('settings.set', { animationFps: fps });
          if (state.boot?.settings) state.boot.settings.animationFps = fps;
          renderSettings();
          toast(tr('toast.animFps', '动画帧率已设为 {fps} 帧', { fps }));
        } catch { }
      };
    });
    document.querySelectorAll('#setCatLayout [data-layout]').forEach(btn => {
      btn.onclick = async () => {
        const val = btn.dataset.layout === 'vertical' ? 'vertical' : 'horizontal';
        try {
          await tryCall('settings.set', { catLayout: val });
          if (state.boot?.settings) state.boot.settings.catLayout = val;
          await tryCall('room.resetOrder');
          renderSettings();
          toast(val === 'vertical' ? tr('toast.layoutVertical', '已切换为纵向排列') : tr('toast.layoutHorizontal', '已切换为横向排列'));
        } catch { }
      };
    });
    document.getElementById('setSend')?.addEventListener('click', async () => {
      const uid = val('setUid');
      if (!uid) return toast(tr('login.toastUidRequired', '请输入游戏 UID'), 'err');
      if (!/^[0-9]{9,10}$/.test(uid)) return toast(tr('login.toastUidInvalid', '请输入 9-10 位游戏 UID'), 'err');
      try { await tryCall('login.sendCode', { uid }); toast(tr('login.toastCodeSent', '验证码已发送')); } catch { }
    });
    document.getElementById('setLogin')?.addEventListener('click', async () => {
      const uid = val('setUid'), code = val('setCode');
      if (!uid || !code) return toast(tr('toast.uidCodeRequired', '请输入游戏 UID 与验证码'), 'err');
      if (!/^[0-9]{9,10}$/.test(uid)) return toast(tr('login.toastUidInvalid', '请输入 9-10 位游戏 UID'), 'err');
      if (!/^[0-9]{6}$/.test(code)) return toast(tr('login.toastCodeInvalid', '请输入 6 位数字验证码'), 'err');
      try { await tryCall('login.submit', { uid, code }); state.boot = await call('boot'); renderChrome(); navigate('bag'); toast(tr('login.toastSuccess', '登录成功')); } catch { }
    });
    document.getElementById('setPhoneLoginOpen')?.addEventListener('click', () => {
      state.loginMode = 'phone';
      navigate('bag');
    });
    document.getElementById('inheritLogin')?.addEventListener('click', showInheritLogin);
    document.getElementById('setTokenBtn')?.addEventListener('click', async () => {
      const token = val('setToken');
      if (!token) return toast(tr('toast.tokenRequired', '请输入 token'), 'err');
      try { await tryCall('login.token', { token }); state.boot = await call('boot'); renderChrome(); navigate('bag'); toast(tr('toast.tokenImported', '已导入并登录')); } catch { }
    });
  }

  async function syncBoot() {
    const boot = await call('boot');
    adoptBootThemeBackground(boot);
    state.boot = boot;
    applyTheme(state.boot.settings);
    renderChrome();
  }

  // 仅供本地浏览器预览更新提示：?preview=verified|unverified|forced|minSafe|unavailable
  function previewThemeSettings() {
    let stored = {};
    try { stored = JSON.parse(localStorage.getItem('wuwa.preview.settings.v1') || '{}') || {}; } catch { stored = {}; }
    const settings = {
      themeAccent: '#e7b77c',
      themeFont: '#8e5e43',
      themeAdvanced: '',
      themeBackgroundImage: '',
      themeBackgroundImageSet: false,
      themeBackgroundOpacity: 0.25,
      themeBackgroundBlur: 0,
      themeBarFollowStandard: true,
      themeBarColor: '',
      themeBarTextFollowTheme: true,
      themeBarTextColor: '',
      themeBarEffectiveColor: '#f5e4cc',
      themeBarEffectiveTextColor: '#8e5e43',
      ...stored,
    };
    try {
      const image = localStorage.getItem('wuwa.preview.serverThemeImage.v1') || '';
      if (image) {
        settings.themeBackgroundImage = image;
        settings.themeBackgroundImageSet = true;
      }
    } catch { }
    return settings;
  }

  function previewBoot(mode) {
    return {
      version: '0.1.4',
      disclaimerShown: true,
      uacPromptShown: true,
      screens: [
        { deviceName: 'preview-display-1', label: '屏幕 1 · 1920×1080（主屏幕）', primary: true },
        { deviceName: 'preview-display-2', label: '屏幕 2 · 2560×1440', primary: false },
      ],
      auth: {
        loggedIn: true,
        nickname: 'Daniel兔兔',
        uid: '103242655',
        clicks: 114514,
        clickTotal: 120000,
        clickConsume: 5486,
      },
      room: mode === 'room'
        ? {
            state: 'joined',
            joined: true,
            roomNo: 'RYOlbDDRN',
            lastRoom: '',
            history: [],
            hiddenAll: false,
            members: [
              { id: 1, nickname: 'Daniel兔兔', owner: true, self: true, online: true, visible: true },
              { id: 2, nickname: '在线成员', owner: false, self: false, online: true, visible: true },
              { id: 3, nickname: '在线房主', owner: true, self: false, online: true, visible: false },
              { id: 4, nickname: '离线成员', owner: false, self: false, online: false, visible: true },
            ],
          }
        : { joined: false, members: [], history: [], roomNo: '', lastRoom: '' },
      settings: {
        scale: 0.83,
        opacity: 1,
        remoteScale: 1,
        remoteFollowMain: true,
        remoteOpacity: 1,
        remoteFollowMainOpacity: true,
        remoteNameScale: 1.19,
        topmost: true,
        clickThrough: false,
        hoverFade: true,
        keyboardMode: 'hidden',
        showMappingControls: true,
        hideOtherPets: false,
        hideMainBar: false,
        hideRemoteBars: false,
        separateBarHotkeys: false,
        allowOverlap: true,
        catLayout: 'vertical',
        captureMode: false,
        captureColor: '#00FF00',
        captureTransparent: false,
        captureCompatibilityMode: false,
        captureStopRenderingMain: false,
        autoClickBoost: false,
        autoChest: true,
        autoSynthUp: false,
        autoEmoteEnabled: false,
        autoClickEnabled: false,
        notifyChestClaimed: false,
        chestNotifyRules: {
          common: { enabled: false, character: true, accessory: true, emote: true },
          uncommon: { enabled: false, character: true, accessory: true, emote: true },
          rare: { enabled: true, character: true, accessory: true, emote: true },
          epic: { enabled: true, character: true, accessory: true, emote: true },
        },
        notifyHotkeys: false,
        launchAtLogin: true,
        runAsAdmin: false,
        serverRegion: 'cn',
        forcedOnlineAcceptedVersion: '',
        gameModeRules: [],
        autoGameModeOnFullscreen: true,
        hotkeys: {},
        ...previewThemeSettings(),
      },
      appearance: {
        characterAssetId: 3060,
        characterRoleId: 1407,
        accessoryAssetId: 1604,
        accessoryOffsetX: 0,
        accessoryOffsetY: 0,
        accessoryScalePercent: 100,
        accessoryRotationDegrees: 0,
        accessoryOffsets: {},
        emoteAssetIds: [1971, 1965, 1969, 1970, 1968],
        cloudCharacterAssetId: 3060,
        cloudAccessoryAssetId: 1604,
        cloudEmoteAssetIds: [1971, 1965, 1969, 1970, 1968],
      },
      rotation: {
        enabled: true,
        intervalMinutes: 30,
        characterIds: [3060, 3061],
        accessoryIds: [1604, 1605],
        lastAppliedUtcMs: 0,
      },
      catalog: {},
      companions: mode === 'companion'
        ? [
            { id: 'preview-companion-1', syntheticId: -1000, name: '菲比·Miratsu', displayName: '菲比', characterAssetId: 3061, accessoryAssetId: 1604, clickIntervalSeconds: 12, randomClick: true, showBar: true },
          ]
        : [],
      themePresets: [],
      themeRotation: { enabled: false, intervalMinutes: 30, currentPresetId: '', lastAppliedMs: 0 },
      update: null,
    };
  }

  function previewCompatibility(mode) {
    const checkedAt = new Date().toISOString();
    const base = {
      schema: 1,
      updatedAt: checkedAt,
      status: 'safe',
      message: 'Wuwatappo Native 0.1.4 已发布；最低兼容版本为 0.1.3。官方客户端 1.0.4 为非强制更新，当前版本可继续使用。',
      native: { latest: '0.1.4', minSafe: '0.1.3' },
      official: {
        cn: { latest: '1.0.4', minOta: '1.0.0' },
        global: { latest: '1.0.4', minOta: '1.0.0' },
      },
      protocol: { testedOfficial: '1.0.4', generation: 1, breaking: false },
      killSwitch: { forceOffline: false, reason: '' },
    };
    const official = (state, version, forced, message) => ({
      state,
      officialVersion: version,
      testedVersion: '1.0.4',
      forceManualDownload: forced,
      minOtaClientVersion: '1.0.0',
      message,
      checkedAt,
      stale: false,
    });
    if (mode === 'unverified') {
      base.status = 'warning';
      base.message = '官方客户端已更新至 1.0.5，当前 Native 尚未验证该版本。';
      base.official.cn.latest = '1.0.5';
      base.official.global.latest = '1.0.5';
      base.officialCheck = official('unverified', '1.0.5', false,
        '官方客户端已更新至 1.0.5，当前 Native 尚未验证该版本。大多数情况下，如果当前功能正常，不必立即更新；如果联机异常，建议暂停联机并等待 Native 更新。');
    } else if (mode === 'forced') {
      // 官方强制更新不等于维护者判定 unsafe；这里模拟“仅官方强更”的可豁免场景。
      base.status = 'safe';
      base.message = '官方客户端已强制更新至 1.0.5，旧版 Native 的联机功能可能已经失效。';
      base.official.cn.latest = '1.0.5';
      base.official.global.latest = '1.0.5';
      base.officialCheck = official('forced', '1.0.5', true,
        '官方客户端已强制更新至 1.0.5，旧版 Native 的联机功能可能已经失效。建议暂停联机并等待 Native 更新；单机功能仍可继续使用。');
    } else if (mode === 'minSafe') {
      base.status = 'warning';
      base.message = '当前 Native 0.1.4 低于最低兼容版本 0.1.5，不保证兼容当前官方服务。';
      base.native.minSafe = '0.1.5';
      base.officialCheck = official('verified', '1.0.4', false, '当前 Native 已验证官方 1.0.4，可继续使用。');
    } else if (mode === 'unavailable') {
      base.officialCheck = official('unavailable', '', false, '暂时无法检查官方版本，不影响当前使用。');
    } else {
      base.officialCheck = official('verified', '1.0.4', false, '当前 Native 已验证官方 1.0.4，可继续使用。');
    }
    return base;
  }

  function previewAnnouncements(mode) {
    const base = {
      publishedAt: '2026-11-07T00:00:00.000Z',
      dismissible: true,
      action: { label: '查看详情', url: 'https://github.com/DanielToyama/WuwaTappoNative' },
    };
    if (mode === 'announcement-warning') {
      return [{
        ...base,
        id: 'preview-warning',
        title: '今晚 23:00 服务维护',
        body: '维护期间联机功能可能短暂中断，单机与桌宠功能不受影响。\n预计持续 1 小时。',
        severity: 'warning',
      }];
    }
    if (mode === 'announcement-critical') {
      return [{
        ...base,
        id: 'preview-critical',
        title: '请更新后继续使用联机',
        body: '当前版本与官方服务存在兼容风险；单机桌宠仍可正常使用。',
        severity: 'critical',
        dismissible: false,
        action: { label: '前往下载', url: 'https://github.com/DanielToyama/WuwaTappoNative/releases/latest' },
      }];
    }
    return [{
      ...base,
      id: 'preview-info',
      title: '0.1.7 版本说明',
      body: '本次更新修复了手机号登录和动态 SDK 配置问题。\n公告只在设置页顶部显示，不打扰其他功能。',
      severity: 'info',
    }, {
      ...base,
      id: 'preview-info-2',
      title: '第二条公告',
      body: '关闭上一条后才会显示这一条。',
      severity: 'info',
      action: null,
    }];
  }

  function previewAnnouncementHistory() {
    return [{
      id: 'preview-history-018',
      severity: 'critical',
      publishedAt: '2026-11-17T09:30:00+08:00',
      title: '0.1.8 版本更新：联机与稳定性改进',
      body: '0.1.8 已发布，本次更新重点改善联机稳定性与桌宠资源占用。\n\n更新内容：\n· 修复部分场景下联机成员列表不同步的问题；\n· 优化背包图标的缓存策略，切换页签时不再重复闪烁；\n· 调整窗口贴边逻辑，多显示器切换后位置更可靠；\n· 降低长时间挂机时的内存占用。\n\n如果你正在使用 0.1.6 或更早版本，建议尽快更新；遇到问题可以在项目仓库反馈，并附上版本号与复现步骤。',
      action: { label: '查看更新详情', url: 'https://github.com/DanielToyama/WuwaTappoNative/releases/latest' },
    }, {
      id: 'preview-history-maintenance',
      severity: 'warning',
      publishedAt: '2026-11-10T18:00:00+08:00',
      title: '联机服务维护安排',
      body: '11 月 12 日 02:00–04:00 将进行联机服务维护。\n维护期间房间创建和加入可能短暂失败，单机桌宠、背包与合成功能不受影响。',
      action: { label: '查看维护公告', url: 'https://github.com/DanielToyama/WuwaTappoNative' },
    }, {
      id: 'preview-info',
      severity: 'info',
      publishedAt: '2026-10-28T12:00:00+08:00',
      title: '0.1.7 版本说明',
      body: '本次更新修复了手机号登录和动态 SDK 配置问题。\n公告只在设置页顶部显示，不打扰其他功能。',
      action: { label: '查看详情', url: 'https://github.com/DanielToyama/WuwaTappoNative' },
    }, {
      id: 'preview-history-atlas',
      severity: 'info',
      publishedAt: '2026-09-15T12:00:00+08:00',
      title: '表情与图鉴数据更新',
      body: '图鉴数据已同步至最新版本，新增多套角色表情与配件素材。\n如果列表没有立即刷新，可以重新打开图鉴页或重启面板。',
    }, {
      id: 'preview-history-login',
      severity: 'warning',
      publishedAt: '2026-08-02T12:00:00+08:00',
      title: '登录方式调整提醒',
      body: '官方登录流程已更新。旧版客户端继承的登录状态可能失效，请重新登录一次。\n账号数据不会受到影响。',
    }, {
      id: 'preview-history-old',
      severity: 'critical',
      publishedAt: '2026-06-21T12:00:00+08:00',
      title: '旧版本停止支持通知',
      body: '0.1.0–0.1.3 已停止支持，这些版本将无法继续使用联机功能。\n请更新到最新版本后再试。',
      action: { label: '前往下载', url: 'https://github.com/DanielToyama/WuwaTappoNative/releases/latest' },
    }];
  }

  if (PREVIEW) {
    window.__previewBoot = previewBoot;
    window.__previewMode = PREVIEW;
    state.boot = previewBoot(PREVIEW);
    applySynthSettings(state.boot);
    applyTheme(state.boot.settings);
    state.compatibility = previewCompatibility(PREVIEW);
    state.announcements = previewAnnouncements(PREVIEW);
    if (PREVIEW === 'announcement-history') state.announcementHistory = previewAnnouncementHistory();
    if (PREVIEW === 'synth') {
      state.bag = [
        { id: 101, type: 'character_skin', name: '测试角色材料', rarity: 'rare', quantity: 11, thumb: '' },
        { id: 102, type: 'accessory', name: '测试配件材料', rarity: 'uncommon', quantity: 7, thumb: '' },
        { id: 103, type: 'emote', name: '测试表情材料', rarity: 'common', quantity: 3, thumb: '' },
      ];
    }
    if (PREVIEW === 'bag-emote') state.bagTab = 'emote';
    state.page = PREVIEW === 'room' ? 'room'
      : PREVIEW === 'synth' ? 'synth'
      : PREVIEW === 'preset' ? 'preset'
      : PREVIEW === 'bag-emote' ? 'bag'
      : 'settings';
    init();
    renderChrome();
    renderPage();
    if (PREVIEW === 'announcement-history') {
      setTimeout(() => {
        const scroller = document.querySelector('.settings-scroll');
        if (scroller) scroller.scrollTop = scroller.scrollHeight;
        showAnnouncementHistory();
      }, 120);
    }
  } else {
    init();
    boot();
  }
})();
