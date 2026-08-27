// ========== 页面就绪通知 ==========
// 独立窗口：前端页面加载完成后通知后端显示窗口（避免首帧纯色闪烁）
try { fetch('/api/ready', { method: 'POST' }).catch(() => {}); } catch (e) {}

// ========== 全局变量 ==========
let versions = [];
let globalPacks = [];
let modalInstance = null;
let statusInterval = null;
let backendErrorShown = false;
let errorModalInstance = null;
let currentView = 'versions';  // 'versions' | 'packs'
let specialStatus = { global_active: false, packs: {} };  // 特殊加载状态
let specialSelection = new Set();  // 特殊加载弹窗中选中的模组
let specialTotal = 0;              // 特殊加载弹窗中本地 Mods 模组总数
let specialMode = 'pack';  // 'pack' = 整合包独立配置 | 'global' = 全局设置
let modMeta = { mods: [], needs_refresh: false };  // 模组元数据（本名/图标）
let modMetaRefreshing = false;
let currentModsPackId = null;  // 当前打开的 📋 模组列表对应的整合包（版本模式为 null）
let currentModsVersionId = null;  // 该整合包所在版本（workshop 检测用）
let currentModsSource = 'workshop';  // 模组列表检测源: 'workshop'（默认）| 'folder'
let currentModsMode = 'pack';  // 模组列表模式: 'pack'（整合包）| 'version'（版本）
// 存在性检测（实时）：版本/整合包被移动或删除时，id -> 名称
let missingVersionInfo = {};
let missingPackInfo = {};
// 模组列表弹窗：搜索 + 标签筛选
let modSearchText = '';
let modFilterTags = [];
let modFilterDropdownOpen = false;
// 显示开关默认开；实际值由后端 data.json 持久化（localStorage 在 WebView2 强杀下不可靠）
let showTags = true;
let showStats = true;
let showHeat = true;
let showSize = true;
let showUpdate = true;
let showSource = true;   // 版本模组列表：本地 + Workshop 同时存在标记（橙色 chip）显示
let showCreated = true;  // 模组列表：模组创建时间显示
// Tooltip 信息设置（悬浮模组时浮动框显示的内容）
let showTooltip = true;          // Tooltip 总开关（全局设置置顶，关闭则完全不显示悬浮框）
let showTooltipVersion = true;   // 显示版本号
let showTooltipName = false;     // 显示模组名（含 id，置顶蓝色）
let showTooltipTags = true;      // 显示标签（紫色 chip）
let showTooltipHeat = true;      // 显示热度（🔥订阅 ⭐收藏 👁️浏览）
let showTooltipSize = true;      // 显示大小（💾）
let showTooltipCreated = true;   // 显示创建时间（📅）
// 模组列表弹窗界面级状态
let mergedViewFilter = 'all';    // 合并列表显示过滤：'all' | 'enabled' | 'disabled'
// Tooltip 开关已移至全局设置（show_tooltip），弹窗内不再提供按钮
// tModLoader 更新检测（全局，meta_settings 持久化）
let tmodUpdate = null;              // /api/tmod_update 返回的最新版本信息
let tmodUpdateCheck = true;         // 「更新对比」开关：关闭则不显示任何更新提示
let tmodUpdateIgnorePreview = true; // 「忽略 Preview 版」开关：只对比 stable

// ========== 工具函数 ==========
// 转义用户输入/文件系统来源的字符串，防止 HTML/JS 注入
function escapeHtml(str) {
    if (!str) return '';
    return String(str).replace(/[&<>"']/g, c => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[c]);
}

// 收藏（持久化到后端 data.json，跨会话可靠；localStorage 在 WebView2 强杀下会丢）
const FAV_VERSIONS_KEY = 'tmod_manager_fav_versions';
const FAV_PACKS_KEY = 'tmod_manager_fav_packs';
let favVersions = [];
let favPacks = [];

function getFavs(key) {
    return key === FAV_VERSIONS_KEY ? favVersions : favPacks;
}

// 读 localStorage（仅用于一次性迁移旧数据）
function getLocalFavs(key) {
    try { return JSON.parse(localStorage.getItem(key)) || []; }
    catch (e) { return []; }
}

// 立即写入 + 请求序号：后端只接受更大的 seq，快速连点也能保证最后一次生效，
// 且点完立刻关闭程序时数据也能及时落盘
let _favSeq = 0;
function persistFavorites() {
    try {
        fetch('/api/favorites', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ versions: favVersions, packs: favPacks, seq: ++_favSeq })
        }).catch(() => {});
    } catch (e) {}
}

function toggleFav(key, id) {
    const arr = key === FAV_VERSIONS_KEY ? favVersions : favPacks;
    const idx = arr.indexOf(id);
    if (idx >= 0) arr.splice(idx, 1);
    else arr.push(id);
    persistFavorites();
    return arr.includes(id);
}

// 启动时从后端加载收藏，并把 localStorage 旧数据一次性迁移过去
async function loadFavorites() {
    let localV = getLocalFavs(FAV_VERSIONS_KEY);
    let localP = getLocalFavs(FAV_PACKS_KEY);
    try {
        const res = await fetch('/api/favorites');
        const data = await res.json();
        favVersions = [...new Set([...(data.versions || []), ...localV])];
        favPacks = [...new Set([...(data.packs || []), ...localP])];
        if (localV.length || localP.length) {
            persistFavorites();  // 迁移写入后端
            try { localStorage.removeItem(FAV_VERSIONS_KEY); localStorage.removeItem(FAV_PACKS_KEY); } catch (e) {}
        }
    } catch (e) {
        // 后端不可用：退回 localStorage
        favVersions = localV;
        favPacks = localP;
    }
}

// 收藏项置顶，其余保持原顺序
function sortFavFirst(items, favs, idFn) {
    return [...items].sort((a, b) => {
        const af = favs.includes(idFn(a)), bf = favs.includes(idFn(b));
        if (af !== bf) return af ? -1 : 1;
        return 0;
    });
}

// ========== 加载数据 ==========
// 启动遮罩：首次数据就绪后淡出（至少展示一小段时间，避免一闪而过）
const _bootShownAt = Date.now();
function hideBootOverlay() {
    const ov = document.getElementById('bootOverlay');
    if (!ov || ov.dataset.done) return;
    ov.dataset.done = '1';
    const delay = Math.max(0, 400 - (Date.now() - _bootShownAt));  // 至少展示 400ms
    setTimeout(() => {
        ov.style.transition = 'opacity 0.4s ease';
        ov.style.opacity = '0';
        setTimeout(() => ov.remove(), 450);
    }, delay);
}

async function loadData() {
    try {
        const [verRes, packRes, specialRes, metaRes, favRes] = await Promise.all([
            fetch('/api/versions'),
            fetch('/api/global_packs'),
            fetch('/api/special_status'),
            fetch('/api/meta_settings'),
            loadFavorites()
        ]);
        versions = await verRes.json();
        globalPacks = await packRes.json();
        specialStatus = await specialRes.json() || { global_active: false, packs: {} };
        applyMetaSettings(await metaRes.json());
        // 更新全局设置按钮颜色（全局开启 → 紫色）
        const gBtn = document.getElementById('globalSpecialBtn');
        if (gBtn) gBtn.classList.toggle('special-active', !!specialStatus.global_active);
        renderVersions();
        renderPacksView();
        loadModMeta();  // 后台预取模组元数据（首次自动联网刷新）
        loadTmodUpdate();  // 后台拉取 tModLoader 最新版，完成后刷新更新标记
        hideBootOverlay();
    } catch (e) {
        showErrorModal('加载数据失败: ' + e.message);
        hideBootOverlay();  // 失败也淡出，避免遮罩挡住错误提示
    }
}

// ========== 错误模态框 ==========
function showErrorModal(message) {
    if (typeof bootstrap === 'undefined') {
        // 离线环境（CDN 未加载）兜底：退化为 alert，避免 JS 崩溃
        alert(message || '后端服务异常，请确保程序正在运行。');
        return;
    }
    if (errorModalInstance) {
        errorModalInstance.dispose();
        errorModalInstance = null;
    }
    document.getElementById('errorMessage').textContent = message || '后端服务异常，请确保程序正在运行。';
    const modalEl = document.getElementById('errorModal');
    errorModalInstance = new bootstrap.Modal(modalEl, {
        backdrop: true,
        keyboard: true,
        focus: true
    });
    errorModalInstance.show();
}

// ---------- 通用提示 / 确认模态框（替代原生 alert/confirm） ----------
const _nativeAlert = window.alert.bind(window);   // 保留原生 alert 用于离线兜底
let _appAlertModal = null;
let _appConfirmModal = null;
let _appConfirmCb = null;

function showAppAlert(message, title = '提示') {
    if (typeof bootstrap === 'undefined') { _nativeAlert(message); return; }
    document.getElementById('appAlertTitle').textContent = title;
    document.getElementById('appAlertMsg').textContent = message || '';
    if (_appAlertModal) _appAlertModal.dispose();
    _appAlertModal = new bootstrap.Modal(document.getElementById('appAlertModal'), {
        backdrop: true, keyboard: true
    });
    _appAlertModal.show();
}

// 顶部下拉 Toast：向下淡入，5s 递减进度条（从右到左耗尽）结束后向上淡出。
// 用于启动成功 / 设置已应用等轻量成功提示；错误仍走 showAppAlert 弹窗。
function showToast(message, title = '提示') {
    const container = document.getElementById('toastContainer');
    if (!container) { showAppAlert(message, title); return; }
    const card = document.createElement('div');
    card.className = 'toast-card';
    card.innerHTML = `<div class="toast-body">
        <i class="fas fa-info-circle toast-ico"></i>
        <span class="toast-title">${escapeHtml(title)}</span>
        <span class="toast-msg">${escapeHtml(message || '')}</span>
    </div>
    <div class="toast-track"><div class="toast-progress"></div></div>`;
    container.appendChild(card);
    // 进度条动画用 @keyframes 显式从 scaleX(1)→scaleX(0)，与 rAF/帧时序无关，
    // 不会因并发动画抢帧导致进度条直接跳到消失态。
    // 5s 后向上淡出并移除：仅响应卡片自身的 toastOut 动画结束（过滤子元素进度条 animationend 冒泡）
    setTimeout(() => {
        card.classList.add('toast-leave');
        const onToastOut = (e) => {
            if (e.target !== card) return;
            card.remove();
            card.removeEventListener('animationend', onToastOut);
        };
        card.addEventListener('animationend', onToastOut);
    }, 5000);
}

function showAppConfirm(message, onConfirm, title = '确认') {
    if (typeof bootstrap === 'undefined') {
        if (window._nativeConfirm ? window._nativeConfirm(message) : confirm(message)) onConfirm();
        return;
    }
    _appConfirmCb = onConfirm;
    document.getElementById('appConfirmTitle').textContent = title;
    document.getElementById('appConfirmMsg').textContent = message || '';
    if (_appConfirmModal) _appConfirmModal.dispose();
    _appConfirmModal = new bootstrap.Modal(document.getElementById('appConfirmModal'), {
        backdrop: true, keyboard: true
    });
    _appConfirmModal.show();
}

// ========== 剪贴板复制（描述/简介） ==========
function copyToClipboard(text, tip) {
    if (!text) { showAppAlert('没有可复制的内容', '提示'); return; }
    const ok = () => showAppAlert('已复制到剪贴板', tip || '复制成功');
    const fail = () => showAppAlert('复制失败，请手动选中复制', '提示');
    if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).then(ok).catch(() => legacyCopy(text, ok, fail));
    } else {
        legacyCopy(text, ok, fail);
    }
}
// 兜底：隐藏 textarea + execCommand('copy')
function legacyCopy(text, ok, fail) {
    try {
        const ta = document.createElement('textarea');
        ta.value = text;
        ta.setAttribute('readonly', '');
        ta.style.position = 'fixed';
        ta.style.left = '-9999px';
        document.body.appendChild(ta);
        ta.select();
        ta.setSelectionRange(0, text.length);
        const done = document.execCommand('copy');
        document.body.removeChild(ta);
        done ? ok() : fail();
    } catch (e) { fail(); }
}

// 复制整合包描述（预览模式的纯文本）
function copyPackDesc() {
    const el = document.getElementById('descPreviewText');
    copyToClipboard(el ? (el.textContent || '').trim() : '', '整合包描述');
}

// 复制模组简介（详情弹窗中的纯文本，去 HTML 标签）
function copyModDesc() {
    const el = document.querySelector('#modDetailBody .mod-detail-desc');
    copyToClipboard(el ? (el.innerText || '').trim() : '', '模组简介');
}

document.getElementById('appConfirmOkBtn').addEventListener('click', () => {
    if (_appConfirmModal) _appConfirmModal.hide();
    const cb = _appConfirmCb;
    _appConfirmCb = null;
    if (cb) cb();
});

// 全局 alert 统一改为模态框（所有 alert(...) 自动走模态）
window.alert = (message) => showAppAlert(message);

// ========== 状态获取 ==========
async function fetchStatus() {
    try {
        const res = await fetch('/api/status');
        if (!res.ok) throw new Error('HTTP ' + res.status);
        const data = await res.json();
        updateTmodStatus(data.tmodloader_running);
        updateLastLaunch(data);
        missingVersionInfo = {};
        (data.missing_versions || []).forEach(x => { if (x && x.id) missingVersionInfo[x.id] = x.name || x.id; });
        missingPackInfo = {};
        (data.missing_packs || []).forEach(x => { if (x && x.id) missingPackInfo[x.id] = x.name || x.id; });
        updateMissingMarkers();
        backendErrorShown = false;
    } catch (e) {
        updateTmodStatus(false);
        document.getElementById('lastLaunchText').textContent = '无法连接后端';
        if (!backendErrorShown) {
            backendErrorShown = true;
            showErrorModal('后端服务异常，请确保程序正在运行。\n错误信息: ' + e.message);
        }
    }
}

// 更新已渲染卡片上的缺失红字（版本卡片 / 整合包两视图）
function updateMissingMarkers() {
    document.querySelectorAll('[data-version-marker]').forEach(el => {
        const id = el.dataset.versionMarker;
        const name = missingVersionInfo[id];
        el.textContent = name ? `${name} 版本已被移动或删除` : '';
        el.style.display = name ? '' : 'none';
    });
    document.querySelectorAll('[data-pack-marker]').forEach(el => {
        const id = el.dataset.packMarker;
        const name = missingPackInfo[id];
        el.textContent = name ? `${name} 整合包已被移动或删除` : '';
        el.style.display = name ? '' : 'none';
    });
}

function updateTmodStatus(running) {
    const dot = document.getElementById('tmodIndicator');
    const text = document.getElementById('tmodText');
    const killBtn = document.getElementById('killGameBtn');
    if (running) {
        dot.style.backgroundColor = '#28a745';
        dot.classList.add('blink-green');
        text.textContent = '运行中';
        text.style.color = '#28a745';
        killBtn.style.display = 'inline-block';
        // 禁用所有启动和还原按钮（运行中改写 enabled.json 无意义且易混淆）
        document.querySelectorAll('.launch-btn, .restore-btn').forEach(btn => {
            btn.disabled = true;
            btn.title = '游戏正在运行，无法操作';
        });
    } else {
        dot.style.backgroundColor = '#6c757d';
        dot.classList.remove('blink-green');
        text.textContent = '未运行';
        text.style.color = '#6c757d';
        killBtn.style.display = 'none';
        document.querySelectorAll('.launch-btn, .restore-btn').forEach(btn => {
            btn.disabled = false;
            btn.title = '';
        });
    }
}

function updateLastLaunch(data) {
    const text = document.getElementById('lastLaunchText');
    if (data.version_name && data.pack_name) {
        let info = `${data.version_name} → ${data.pack_name}`;
        if (data.version_path) {
            info += ` (路径: ${data.version_path})`;
        }
        text.textContent = info;
    } else {
        text.textContent = '暂无启动记录';
    }
}

// ========== 强制关闭游戏 ==========
async function killGame() {
    showAppConfirm('确定要强制关闭正在运行的 tModLoader 吗？\n未保存的游戏进度可能会丢失。', async () => {
        try {
            const res = await fetch('/api/kill_tmodloader', { method: 'POST' });
            const data = await res.json();
            if (res.ok) {
                showAppAlert('游戏已强制关闭');
                fetchStatus();
            } else {
                showAppAlert('关闭失败: ' + data.error);
            }
        } catch (e) {
            showAppAlert('请求失败: ' + e.message);
        }
    });
}

// ========== 渲染版本卡片 ==========
function renderVersions() {
    const container = document.getElementById('versionList');
    if (versions.length === 0) {
        container.innerHTML = `<div class="alert alert-info text-center glass-card border-0">
            <i class="fas fa-info-circle me-2"></i>暂无版本，请添加。
        </div>`;
        return;
    }

    const favs = getFavs(FAV_VERSIONS_KEY);
    // 收藏的版本置顶，其余保持原顺序
    const sorted = sortFavFirst(versions, favs, v => v.id);

    let html = '';
    for (const v of sorted) {
        const label = `${v.name} ${v.version_info ? '（' + v.version_info + '）' : ''}`;
        const updateBadge = hasTmodUpdate(v.version_info)
            ? ' <span class="tmod-update-rainbow"><span class="tur-inner"><span class="tur-text">有可用更新</span></span></span>'
            : '';
        const starIcon = favs.includes(v.id) ? 'fas fa-star faved' : 'fa-regular fa-star';
        html += `
        <div class="card glass-card mb-4 border-0 version-card" data-version="${v.id}">
            <div class="card-header bg-transparent d-flex justify-content-between align-items-center">
                <div><i class="fas fa-folder-open text-warning me-2"></i><strong>${escapeHtml(label)}</strong>${updateBadge}</div>
                <div class="d-flex align-items-center">
                    <button class="btn btn-sm btn-success me-1" data-action="launchDirect" data-version="${v.id}" title="直接启动：使用当前 Mods 目录的 enabled.json 启动该版本，不做任何修改（不覆盖 enabled.json）">
                        <i class="fas fa-play me-1"></i>直接启动
                    </button>
                    <button class="icon-btn me-1" data-action="viewVersionMods" data-version="${v.id}" title="查看该版本模组列表（Workshop + 本地，对照游戏加载时的 enabled.json）">
                        <i class="fas fa-th-list"></i>
                    </button>
                    <button class="fav-btn me-1" data-action="toggleFavVersion" data-version="${v.id}" title="收藏 / 取消收藏">
                        <i class="${starIcon}"></i>
                    </button>
                    <button class="btn btn-sm btn-outline-warning restore-btn" data-action="restoreVersion" data-version="${v.id}">
                        <i class="fas fa-undo-alt"></i> 还原
                    </button>
                </div>
            </div>
            <div class="card-body">
                <div class="text-danger small fw-bold mb-2" data-version-marker="${v.id}" style="display:none;"></div>
                <div class="mb-3">
                    <label class="fw-bold">已添加的整合包</label>
                    <div id="packList-${v.id}" class="mt-2">
                        ${renderPackList(v)}
                    </div>
                </div>
                <button class="btn btn-success neon-btn" onclick="openModal('${v.id}')">
                    <i class="fas fa-plus me-1"></i>添加整合包
                </button>
            </div>
        </div>
        `;
    }
    container.innerHTML = html;
    updateMissingMarkers();
}

// 渲染整合包列表（纵向）
function renderPackList(version) {
    if (!version.packs || version.packs.length === 0) {
        return `<div class="text-secondary"><i class="fas fa-inbox me-1"></i>尚未添加任何整合包</div>`;
    }

    let html = '<div class="list-group">';
    version.packs.forEach(packId => {
        const info = globalPacks.find(p => p.id === packId);
        const name = info ? info.name : packId;
        html += `
            <div class="list-group-item bg-transparent border-0 d-flex justify-content-between align-items-center pack-item">
                <span class="d-flex align-items-center flex-wrap gap-2"><i class="fas fa-cube text-info me-1"></i>${escapeHtml(name)}<span class="text-danger small fw-bold" data-pack-marker="${escapeHtml(packId)}" style="display:none;"></span></span>
                <div>
                    <button class="btn btn-sm btn-primary me-1 launch-btn" data-action="launch" data-version="${version.id}" data-pack="${escapeHtml(packId)}">
                        <i class="fas fa-play"></i> 启动
                    </button>
                    <button class="btn btn-sm btn-outline-danger" data-action="removePack" data-version="${version.id}" data-pack="${escapeHtml(packId)}">
                        <i class="fas fa-times"></i>
                    </button>
                </div>
            </div>
        `;
    });
    html += '</div>';
    return html;
}

// ========== 版本列表事件委托（替代内联 onclick，避免目录名/用户输入中的引号破坏 JS） ==========
document.getElementById('versionList').addEventListener('click', async (e) => {
    const btn = e.target.closest('button[data-action]');
    if (!btn) return;
    const action = btn.dataset.action;
    const versionId = btn.dataset.version;
    const packId = btn.dataset.pack;
    if (action === 'deleteVersion') await deleteVersion(versionId);
    else if (action === 'launchDirect') await launchDirect(versionId);
    else if (action === 'viewVersionMods') await openVersionMods(versionId);
    else if (action === 'restoreVersion') await restoreVersion(versionId);
    else if (action === 'launch') await launchPack(versionId, packId);
    else if (action === 'removePack') await removePack(versionId, packId);
    else if (action === 'toggleFavVersion') {
        toggleFav(FAV_VERSIONS_KEY, versionId);
        renderVersions();
    }
    else if (action === 'editVersion') {
        openVersionEdit(versionId);
    }
});

// ========== 整合包视图（以整合包显示） ==========
// 聚合：把所有版本中已添加的整合包按 packId 去重，记录各自归属的版本
function buildPackMap() {
    const map = new Map();
    for (const v of versions) {
        for (const packId of v.packs) {
            if (!map.has(packId)) map.set(packId, { versions: [] });
            map.get(packId).versions.push(v);
        }
    }
    return map;
}

function renderPacksView() {
    const container = document.getElementById('packListView');
    const favPacks = getFavs(FAV_PACKS_KEY);
    const map = buildPackMap();
    // 收藏的整合包置顶，其余按名称排序（单一比较器，避免二次排序破坏置顶）
    const entries = [...map.entries()].sort((a, b) => {
        const af = favPacks.includes(a[0]), bf = favPacks.includes(b[0]);
        if (af !== bf) return af ? -1 : 1;
        return a[0].localeCompare(b[0], 'zh');
    });

    if (entries.length === 0) {
        container.innerHTML = `<div class="alert alert-info text-center glass-card border-0">
            <i class="fas fa-inbox me-2"></i>尚未添加任何整合包，请先在「以版本显示」中添加。
        </div>`;
        return;
    }

    // 每个整合包一个横向长条卡片：左侧名称、中间归属版本（每版本一个 chip 含启动按钮）、最右收藏
    let html = '';
    for (const [packId, info] of entries) {
        const pack = globalPacks.find(p => p.id === packId);
        const name = pack ? pack.name : packId;
        info.versions.sort((a, b) => a.name.localeCompare(b.name, 'zh'));
        const starIcon = favPacks.includes(packId) ? 'fas fa-star faved' : 'fa-regular fa-star';
        const desc = pack && pack.desc ? pack.desc : '';
        // 特殊加载按钮状态：启用独立配置→橙色发光；否则全局开启→紫色；都没→灰色
        const pState = specialStatus.packs && specialStatus.packs[packId];
        const spIndep = !!(pState && pState.independent);
        const spEff = spIndep || !!(pState && pState.effective) || !!specialStatus.global_active;
        const spClass = spIndep ? 'special-independent' : (spEff ? 'special-active' : '');
        html += `
        <div class="pack-bar glass-card mb-2">
            <div class="pack-bar-row">
            <div class="pack-bar-name"><i class="fas fa-cube text-info me-2"></i>${escapeHtml(name)}<span class="text-danger small fw-bold ms-2" data-pack-marker="${escapeHtml(packId)}" style="display:none;"></span></div>
            <div class="pack-bar-versions">`;
        for (const v of info.versions) {
            const vlabel = `${v.name}${v.version_info ? '（' + v.version_info + '）' : ''}`;
            html += `
                <div class="pack-bar-version">
                    <span><i class="fas fa-folder-open text-warning me-1"></i>${escapeHtml(vlabel)}</span>
                </div>`;
        }
        html += `</div>
            <div class="pack-bar-actions">`;
        for (const v of info.versions) {
            const vlabel = `${v.name}${v.version_info ? '（' + v.version_info + '）' : ''}`;
            html += `
                <button class="btn btn-sm btn-primary launch-btn" data-action="launch" data-version="${v.id}" data-pack="${escapeHtml(packId)}" title="使用 ${escapeHtml(vlabel)} 启动">
                    <i class="fas fa-play"></i> 启动
                </button>`;
        }
        html += `
                <button class="icon-btn" data-action="viewMods" data-pack="${escapeHtml(packId)}" data-version="${escapeHtml((info.versions[0] || {}).id || '')}" title="查看模组列表">
                    <i class="fas fa-th-list"></i>
                </button>
                <button class="icon-btn" data-action="editPackDesc" data-pack="${escapeHtml(packId)}" title="查看整合包描述">
                    <i class="fas fa-align-left"></i>
                </button>
                <button class="btn btn-sm special-btn ${spClass}" data-action="specialLoad" data-pack="${escapeHtml(packId)}" title="特殊加载项：控制本地 / workshop 模组加载">
                    <i class="fas fa-sliders-h me-1"></i>特殊加载项
                </button>
                <button class="fav-btn" data-action="toggleFavPack" data-pack="${escapeHtml(packId)}" title="收藏 / 取消收藏"><i class="${starIcon}"></i></button>
            </div>
            </div>
            ${desc ? `<div class="pack-bar-desc" title="${escapeHtml(desc)}">${escapeHtml(desc)}</div>` : ''}
        </div>`;
    }
    container.innerHTML = html;
    updateMissingMarkers();
}

// ========== 视图切换 ==========
function switchView(view) {
    currentView = view;
    const versionList = document.getElementById('versionList');
    const packList = document.getElementById('packListView');
    if (view === 'versions') {
        versionList.style.display = '';
        packList.style.display = 'none';
    } else {
        versionList.style.display = 'none';
        packList.style.display = '';
        renderPacksView();
    }
    document.getElementById('btnViewVersions').classList.toggle('active', view === 'versions');
    document.getElementById('btnViewPacks').classList.toggle('active', view === 'packs');
    // 刷新运行状态，让新渲染的启动按钮禁用/启用与当前游戏状态一致
    fetchStatus();
}

// 整合包视图事件委托：启动按钮（按归属版本 bat 启动）+ 收藏切换
document.getElementById('packListView').addEventListener('click', async (e) => {
    const btn = e.target.closest('button[data-action]');
    if (!btn) return;
    const action = btn.dataset.action;
    if (action === 'launch') {
        await launchPack(btn.dataset.version, btn.dataset.pack);
    } else if (action === 'toggleFavPack') {
        toggleFav(FAV_PACKS_KEY, btn.dataset.pack);
        renderPacksView();
    } else if (action === 'editPackDesc') {
        openPackDesc(btn.dataset.pack);
    } else if (action === 'viewMods') {
        openPackMods(btn.dataset.pack, btn.dataset.version);
    } else if (action === 'specialLoad') {
        openSpecial(btn.dataset.pack);
    }
});

// ---------- 编辑版本 / 重命名整合包 ----------
function hideModal(modalId) {
    const modalEl = document.getElementById(modalId);
    const inst = bootstrap.Modal.getInstance(modalEl);
    if (inst) inst.hide();
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
}

function openVersionEdit(versionId) {
    if (typeof bootstrap === 'undefined') { alert('无法编辑：Bootstrap 未加载。'); return; }
    const v = versions.find(x => x.id === versionId);
    if (!v) return;
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
    document.getElementById('editVersionId').value = v.id;
    document.getElementById('editVersionName').value = v.name;
    document.getElementById('editBatPath').value = v.bat_path || '';
    new bootstrap.Modal(document.getElementById('versionEditModal')).show();
}

async function saveVersionEdit() {
    const id = document.getElementById('editVersionId').value;
    const name = document.getElementById('editVersionName').value.trim();
    const bat_path = document.getElementById('editBatPath').value.trim();
    if (!name) { alert('版本名称不能为空'); return; }
    if (!bat_path) { alert('启动文件路径不能为空'); return; }
    try {
        const res = await fetch(`/api/versions/${id}/rename`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ name, bat_path })
        });
        const data = await res.json();
        if (res.ok) {
            hideModal('versionEditModal');
            loadData();
        } else {
            alert('保存失败: ' + data.error);
        }
    } catch (e) {
        alert('请求失败: ' + e.message);
    }
}

function openPackDesc(packId) {
    if (typeof bootstrap === 'undefined') { alert('无法查看描述：Bootstrap 未加载。'); return; }
    const pack = globalPacks.find(p => p.id === packId);
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
    document.getElementById('descPackId').value = packId;
    // 默认进入只读预览模式
    const desc = pack && pack.desc ? pack.desc : '';
    document.getElementById('descPreviewText').textContent = desc || '暂无描述';
    document.getElementById('packDescInput').value = desc;
    showDescMode('preview');
    new bootstrap.Modal(document.getElementById('packDescModal')).show();
}

// 切换描述弹窗的预览/编辑模式
// 编辑模式：只保留「取消 + 保存」；关闭/修改按钮隐藏（避免出现两个取消按钮）
function showDescMode(mode) {
    const isEdit = mode === 'edit';
    document.getElementById('descPreviewMode').style.display = isEdit ? 'none' : '';
    document.getElementById('descEditMode').style.display = isEdit ? '' : 'none';
    document.getElementById('descEditBtn').style.display = isEdit ? 'none' : '';
    document.getElementById('descCancelBtn').style.display = isEdit ? '' : 'none';
    document.getElementById('descSaveBtn').style.display = isEdit ? '' : 'none';
    document.getElementById('descCloseBtn').style.display = isEdit ? 'none' : '';
}

function enterDescEdit() {
    const pack = globalPacks.find(p => p.id === document.getElementById('descPackId').value);
    document.getElementById('packDescInput').value = pack && pack.desc ? pack.desc : '';
    showDescMode('edit');
    document.getElementById('packDescInput').focus();
}

function exitDescEdit() {
    // 放弃未保存修改，回到预览
    const pack = globalPacks.find(p => p.id === document.getElementById('descPackId').value);
    const desc = pack && pack.desc ? pack.desc : '';
    document.getElementById('descPreviewText').textContent = desc || '暂无描述';
    document.getElementById('packDescInput').value = desc;
    showDescMode('preview');
}

async function savePackDesc() {
    const packId = document.getElementById('descPackId').value;
    const desc = document.getElementById('packDescInput').value.trim();
    try {
        const res = await fetch(`/api/packs/${encodeURIComponent(packId)}/desc`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ desc })
        });
        const data = await res.json();
        if (res.ok) {
            hideModal('packDescModal');
            loadData();
        } else {
            alert('保存失败: ' + data.error);
        }
    } catch (e) {
        alert('请求失败: ' + e.message);
    }
}

// ---------- 特殊加载项（本地模组禁用 → 优先加载 workshop） ----------
async function openSpecial(packId) {
    specialMode = 'pack';
    if (typeof bootstrap === 'undefined') { alert('无法打开：Bootstrap 未加载。'); return; }
    await ensureModMeta();
    document.getElementById('metaSettingsWrap').style.display = 'none';
    document.getElementById('specialPackId').value = packId;
    document.getElementById('specialModalTitle').textContent = '特殊加载项';
    document.getElementById('specialModalDesc').innerHTML = `<i class="fas fa-info-circle me-1" style="color:#8a5dff;"></i>
        当 <strong>workshop 文件夹</strong> 和 <strong>本地 Mods 文件夹</strong> 存在同名模组时，tModLoader 默认<strong>优先加载本地模组</strong>。<br>
        <strong>启用独立配置</strong>后，本整合包使用自己的禁用列表，<strong>不受全局设置影响</strong>；不启用则跟随全局设置。<br>
        本配置在<strong>启动该整合包时生效</strong>，退出 tModLoader 时自动还原。`;
    document.getElementById('specialSwitchLabel').textContent = '启用独立配置';
    document.getElementById('specialSwitchHint').textContent = '开启后本整合包使用自己的禁用列表，不受全局设置影响；启动该整合包时生效';
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
    new bootstrap.Modal(document.getElementById('specialModal')).show();
    document.getElementById('specialModsList').innerHTML = '<span class="text-secondary">加载中...</span>';
    try {
        const res = await fetch(`/api/special/${encodeURIComponent(packId)}`);
        const data = await res.json();
        if (!res.ok) { alert('获取失败: ' + data.error); return; }
        document.getElementById('specialActive').checked = data.independent;
        const pLocal = new Set((data.mods || []).map(m => m.name));
        specialSelection = new Set((data.disabled_mods || []).filter(n => pLocal.has(n)));
        renderSpecialMods(data.mods);
        updateSpecialListState();
    } catch (e) { alert('请求失败: ' + e.message); }
}

async function openGlobalSpecial() {
    specialMode = 'global';
    if (typeof bootstrap === 'undefined') { alert('无法打开：Bootstrap 未加载。'); return; }
    await ensureModMeta();
    document.getElementById('metaSettingsWrap').style.display = '';
    document.getElementById('specialPackId').value = '';
    document.getElementById('specialModalTitle').textContent = '全局特殊加载设置';
    document.getElementById('specialModalDesc').innerHTML = `<i class="fas fa-info-circle me-1" style="color:#8a5dff;"></i>
        全局控制模组的优先加载：开启后，下方选中的本地模组会被禁用（<code>.tmod.disabled</code>，文件不删除），
        所有<strong>未启用独立配置</strong>的整合包在启动时都会应用此设置；启用独立配置的整合包使用各自配置，<strong>不受全局影响</strong>。<br>
        <strong>退出 tModLoader 时自动还原</strong>。`;
    document.getElementById('specialSwitchLabel').textContent = '启用全局优先加载 workshop 模组';
    document.getElementById('specialSwitchHint').textContent = '开启后对所有未启用独立配置的整合包生效；启动整合包时应用，退出还原';
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
    new bootstrap.Modal(document.getElementById('specialModal')).show();
    document.getElementById('specialModsList').innerHTML = '<span class="text-secondary">加载中...</span>';
    try {
        const res = await fetch('/api/global_special');
        const data = await res.json();
        if (!res.ok) { alert('获取失败: ' + data.error); return; }
        document.getElementById('specialActive').checked = data.active;
        const gLocal = new Set((data.mods || []).map(m => m.name));
        specialSelection = new Set((data.disabled_mods || []).filter(n => gLocal.has(n)));
        renderSpecialMods(data.mods);
        updateSpecialListState();
    } catch (e) { alert('请求失败: ' + e.message); }
}

// ---------- 全局设置（分板块弹窗，参考 ImageViewer_c 左侧导航风格） ----------

// 打开全局设置弹窗：读取 meta_settings → 同步开关 → 显示，默认第一个板块
async function openSettings() {
    if (typeof bootstrap === 'undefined') { alert('无法打开：Bootstrap 未加载。'); return; }
    await ensureModMeta();
    const res = await fetch('/api/meta_settings');
    if (res.ok) applyMetaSettings(await res.json());
    // 每次打开都重置：下次切到「全局特殊配置」板块时重新加载后端配置，
    // 使上次未点「保存」的改动被丢弃（配置只在点击保存时才更新）
    globalSpecialLoaded = false;
    switchSettingsPane('update');
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
    new bootstrap.Modal(document.getElementById('settingsModal')).show();
}

// 切换全局设置板块（左侧导航按钮 → 右侧内容）
function switchSettingsPane(tab) {
    document.querySelectorAll('#settingsNav .settings-nav-item').forEach(b => {
        b.classList.toggle('active', b.dataset.stab === tab);
    });
    document.querySelectorAll('.settings-body .settings-pane').forEach(p => {
        p.style.display = p.id === 'sp-' + tab ? '' : 'none';
    });
    // 全局特殊配置板块：首次进入时懒加载模组列表
    if (tab === 'global' && !globalSpecialLoaded) {
        globalSpecialLoaded = true;
        loadGlobalSpecialPane();
    }
}

// ---------- 全局特殊配置板块（独立实现于设置弹窗，不复用旧 specialModal 模板） ----------
let globalSpecialSelection = new Set();
let globalSpecialLoaded = false;
let globalSpecialTotal = 0;   // 本地 Mods 模组总数

// 加载全局特殊加载配置并渲染模组列表
async function loadGlobalSpecialPane() {
    const listEl = document.getElementById('s_globalSpecialMods');
    if (listEl) listEl.innerHTML = '<span class="text-secondary">加载中...</span>';
    try {
        const res = await fetch('/api/global_special');
        const data = await res.json();
        if (!res.ok) { alert('获取失败: ' + data.error); return; }
        document.getElementById('s_globalSpecialActive').checked = data.active;
        // 过滤掉已选列表里本地不存在的幽灵条目（旧 workshop ID / 已删除模组），避免已选计数 +1；保存时会一并清理
        const localNames = new Set((data.mods || []).map(m => m.name));
        globalSpecialSelection = new Set((data.disabled_mods || []).filter(n => localNames.has(n)));
        renderGlobalSpecialMods(data.mods);
        updateGlobalSpecialState();
    } catch (e) { alert('请求失败: ' + e.message); }
}

function renderGlobalSpecialMods(mods) {
    const container = document.getElementById('s_globalSpecialMods');
    document.getElementById('s_globalSpecialEmpty').style.display = (!mods || mods.length === 0) ? '' : 'none';
    globalSpecialTotal = mods ? mods.length : 0;
    if (!mods || mods.length === 0) { container.innerHTML = ''; updateGlobalSpecialCount(); return; }
    let html = '';
    for (const m of mods) {
        const selected = globalSpecialSelection.has(m.name);
        const d = modDisplay(m.name);
        html += `<div class="special-mod ${selected ? 'selected' : ''}" data-name="${escapeHtml(m.name)}" onclick="toggleGlobalSpecialMod(this)">
            <span title="${escapeHtml(d.label)}">${d.icon ? `<img class="mod-icon" src="${d.icon}" loading="lazy" decoding="async" onerror="this.style.display='none'" alt="">` : `<i class="fas fa-cube me-1"></i>`}${d.labelHtml}</span>
            <span class="special-mod-badge">${selected ? '本地已禁用' : ''}</span>
        </div>`;
    }
    container.innerHTML = html;
    updateGlobalSpecialCount();
}

function toggleGlobalSpecialMod(el) {
    if (!document.getElementById('s_globalSpecialActive').checked) {
        alert('请先开启「是否优先加载 workshop 模组」');
        return;
    }
    const name = el.dataset.name;
    if (globalSpecialSelection.has(name)) globalSpecialSelection.delete(name);
    else globalSpecialSelection.add(name);
    el.classList.toggle('selected');
    el.querySelector('.special-mod-badge').textContent = globalSpecialSelection.has(name) ? '本地已禁用' : '';
    updateGlobalSpecialCount();
}

function updateGlobalSpecialCount() {
    const el = document.getElementById('s_globalSpecialCount');
    if (!el) return;
    el.textContent = `共 ${globalSpecialTotal} 个模组 · 已选 ${globalSpecialSelection.size} 个`;
}

function updateGlobalSpecialState() {
    const active = document.getElementById('s_globalSpecialActive').checked;
    const list = document.getElementById('s_globalSpecialMods');
    list.classList.toggle('locked', !active);
    list.querySelectorAll('.special-mod').forEach(el => { el.style.cursor = active ? 'pointer' : 'not-allowed'; });
}

async function saveGlobalSpecial() {
    // 未进入过「全局特殊配置」板块时先加载当前配置，避免用空 Selection 覆盖已保存数据
    if (!globalSpecialLoaded) {
        await loadGlobalSpecialPane();
        globalSpecialLoaded = true;
    }
    const active = document.getElementById('s_globalSpecialActive').checked;
    const mods = [...globalSpecialSelection];
    try {
        const res = await fetch('/api/global_special', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ active, mods })
        });
        const data = await res.json();
        if (res.ok) {
            loadData();  // 刷新版本卡片按钮紫色状态
            // 保存成功后关闭设置弹窗
            const modal = bootstrap.Modal.getInstance(document.getElementById('settingsModal'));
            if (modal) modal.hide();
            if (data.errors && data.errors.length) alert(data.message);
            else showToast('全局特殊配置已保存', '设置已应用');
        } else {
            alert('保存失败: ' + data.error);
        }
    } catch (e) { alert('请求失败: ' + e.message); }
}

// 通用开关绑定：绑定所有带 data-mskey 的开关（当前用于「全局设置」弹窗，与特殊加载弹窗里的旧开关并行互不干扰）
function bindMetaSwitch(key, getVal, setVal) {
    document.querySelectorAll(`[data-mskey="${key}"]`).forEach(t => {
        t.checked = getVal();
        t.addEventListener('change', () => {
            setVal(t.checked);
            saveMetaSetting(key, t.checked);
            rerenderCurrentMods();
        });
    });
}

// 初始化全局设置弹窗：板块切换 + 各开关绑定
function initSettingsModal() {
    const nav = document.getElementById('settingsNav');
    if (nav) nav.addEventListener('click', (e) => {
        const btn = e.target.closest('.settings-nav-item');
        if (btn) switchSettingsPane(btn.dataset.stab);
    });
    const ga = document.getElementById('s_globalSpecialActive');
    if (ga) ga.addEventListener('change', updateGlobalSpecialState);
    bindMetaSwitch('tmod_update', () => tmodUpdateCheck, v => { tmodUpdateCheck = v; });
    bindMetaSwitch('tmod_update_ignore_preview', () => tmodUpdateIgnorePreview, v => { tmodUpdateIgnorePreview = v; });
    bindMetaSwitch('show_tags', () => showTags, v => { showTags = v; });
    bindMetaSwitch('show_stats', () => showStats, v => { showStats = v; updateModlistSubToggles(); });
    bindMetaSwitch('show_heat', () => showHeat, v => { showHeat = v; });
    bindMetaSwitch('show_size', () => showSize, v => { showSize = v; });
    bindMetaSwitch('show_update', () => showUpdate, v => { showUpdate = v; });
    bindMetaSwitch('show_source', () => showSource, v => { showSource = v; });
    bindMetaSwitch('show_created', () => showCreated, v => { showCreated = v; });
    bindMetaSwitch('show_tooltip', () => showTooltip, v => { showTooltip = v; updateTooltipSubToggles(); });
    bindMetaSwitch('show_tooltip_version', () => showTooltipVersion, v => { showTooltipVersion = v; });
    bindMetaSwitch('show_tooltip_name', () => showTooltipName, v => { showTooltipName = v; });
    bindMetaSwitch('show_tooltip_tags', () => showTooltipTags, v => { showTooltipTags = v; });
    bindMetaSwitch('show_tooltip_heat', () => showTooltipHeat, v => { showTooltipHeat = v; });
    bindMetaSwitch('show_tooltip_size', () => showTooltipSize, v => { showTooltipSize = v; });
    bindMetaSwitch('show_tooltip_created', () => showTooltipCreated, v => { showTooltipCreated = v; });
}

// 关于板块：打开项目 GitHub 主页
function openAboutGithub() {
    fetch('/api/open_url', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url: 'https://github.com/HopingStar/tMod-Manager' })
    })
        .then(res => res.json())
        .then(data => { if (!data.ok && data.error) showErrorModal(data.error); })
        .catch(err => showErrorModal('打开失败: ' + err.message));
}

function renderSpecialMods(mods) {
    const container = document.getElementById('specialModsList');
    document.getElementById('specialEmpty').style.display = (!mods || mods.length === 0) ? '' : 'none';
    specialTotal = mods ? mods.length : 0;
    if (!mods || mods.length === 0) {
        container.innerHTML = '';
        updateSpecialCount();
        return;
    }
    let html = '';
    for (const m of mods) {
        const selected = specialSelection.has(m.name);
        const d = modDisplay(m.name);
        html += `<div class="special-mod ${selected ? 'selected' : ''}" data-name="${escapeHtml(m.name)}" onclick="toggleSpecialMod(this)">
            <span title="${escapeHtml(d.label)}">${d.icon ? `<img class="mod-icon" src="${d.icon}" loading="lazy" decoding="async" onerror="this.style.display='none'" alt="">` : `<i class="fas fa-cube me-1"></i>`}${d.labelHtml}</span>
            <span class="special-mod-badge">${selected ? '本地已禁用' : ''}</span>
        </div>`;
    }
    container.innerHTML = html;
    updateSpecialCount();
}

function toggleSpecialMod(el) {
    if (!document.getElementById('specialActive').checked) {
        alert('请先开启「是否优先加载 workshop 模组」');
        return;
    }
    const name = el.dataset.name;
    if (specialSelection.has(name)) specialSelection.delete(name);
    else specialSelection.add(name);
    el.classList.toggle('selected');
    el.querySelector('.special-mod-badge').textContent = specialSelection.has(name) ? '本地已禁用' : '';
    updateSpecialCount();
}

function updateSpecialCount() {
    const el = document.getElementById('specialCount');
    el.textContent = `共 ${specialTotal} 个模组 · 已选 ${specialSelection.size} 个`;
}

function updateSpecialListState() {
    const active = document.getElementById('specialActive').checked;
    const list = document.getElementById('specialModsList');
    list.classList.toggle('locked', !active);
    list.querySelectorAll('.special-mod').forEach(el => { el.style.cursor = active ? 'pointer' : 'not-allowed'; });
}

document.getElementById('specialActive').addEventListener('change', updateSpecialListState);

async function saveSpecial() {
    const packId = document.getElementById('specialPackId').value;
    const active = document.getElementById('specialActive').checked;
    const mods = [...specialSelection];
    // 全局模式保存 {active, mods}；整合包模式保存 {independent, mods}
    const url = specialMode === 'global' ? '/api/global_special' : `/api/special/${encodeURIComponent(packId)}`;
    const body = specialMode === 'global'
        ? JSON.stringify({ active, mods })
        : JSON.stringify({ independent: active, mods });
    try {
        const res = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body
        });
        const data = await res.json();
        if (res.ok) {
            hideModal('specialModal');
            loadData();  // 刷新卡片按钮紫色状态
            if (data.errors && data.errors.length) alert(data.message);
            else showToast(specialMode === 'global' ? '全局特殊配置已保存' : '整合包特殊加载配置已保存', '设置已应用');
        } else {
            alert('保存失败: ' + data.error);
        }
    } catch (e) { alert('请求失败: ' + e.message); }
}

// ---------- 模组元数据（本名/图标/作者） ----------

// tML 富文本（模组本名里的 [c/HEX:文本] 颜色、[i/xxx] 物品等）→ 纯文本
function stripTmlTags(text) {
    if (!text) return '';
    return text
        .replace(/\[c\/([0-9a-fA-F]{3,6}):([\s\S]*?)\]/g, '$2')
        // 颜色非法时的兜底：也保留文本（不套颜色）
        .replace(/\[c\/[^\]]*:([\s\S]*?)\]/g, '$1')
        .replace(/\[[a-zA-Z][a-zA-Z0-9_]*(\/|:)[^\]]*\]/g, '');
}

// tML 富文本 → 安全 HTML（先转义文本防注入，再解析颜色标签）
function tmlRichToHtml(text) {
    if (!text) return '';
    let html = escapeHtml(text);
    // [c/HEX:文本] → 彩色 span（颜色仅放行 3~6 位 hex，防注入）
    html = html.replace(/\[c\/([0-9a-fA-F]{3,6}):([\s\S]*?)\]/g, '<span style="color:#$1">$2</span>');
    // 颜色非法时的兜底：也保留文本（不带颜色）
    html = html.replace(/\[c\/[^\]]*:([\s\S]*?)\]/g, '$1');
    // 其它 tML 标签（[i/xxx] [s/xxx] [t:xxx] 等）剥离
    html = html.replace(/\[[a-zA-Z][a-zA-Z0-9_]*(\/|:)[^\]]*\]/g, '');
    return html;
}

function modDisplay(name) {
    const m = (modMeta.mods || []).find(x => x.name === name);
    if (m && m.display_name) {
        // 本名在外，文件名（内部名）在括号内（浅色小字）
        return {
            label: `${stripTmlTags(m.display_name)}（${name}）`,
            labelHtml: `${tmlRichToHtml(m.display_name)}<span class="text-secondary">（${escapeHtml(name)}）</span>`,
            icon: m.icon || ''
        };
    }
    return { label: name, labelHtml: escapeHtml(name), icon: m && m.icon ? m.icon : '' };
}

// 数字格式化：万 / 亿
function formatNum(n) {
    if (n == null) return '';
    if (n >= 100000000) return (n / 100000000).toFixed(1) + '亿';
    if (n >= 10000) return (n / 10000).toFixed(1) + '万';
    return String(n);
}

// 文件大小：字节 → MB / GB（不足 1MB 显示 <1MB）
function fmtSize(bytes) {
    if (!bytes) return '';
    const mb = bytes / 1024 / 1024;
    if (mb >= 1024) return (mb / 1024).toFixed(1) + 'GB';
    if (mb < 1) return '<1MB';
    return mb.toFixed(0) + 'MB';
}

// 时间戳 → 年-月
function fmtDate(ts) {
    if (!ts) return '';
    const d = new Date(ts * 1000);
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`;
}

// 距今天数格式化：<1月→N天前；1月~1年→x月x天前；>1年→x年x月x天前
function fmtDaysAgo(days) {
    if (days == null) return '';
    if (days === 0) return '今天';
    if (days < 30) return `${days} 天前`;
    if (days < 365) {
        return `${Math.floor(days / 30)} 月 ${days % 30} 天前`;
    }
    const y = Math.floor(days / 365);
    const m = Math.floor((days % 365) / 30);
    const d = (days % 365) % 30;
    return `${y} 年 ${m} 月 ${d} 天前`;
}

// Steam BBCode 描述 → 安全的 HTML（先转义文本防注入，再解析标签）
function bbcodeToHtml(text) {
    if (!text) return '';
    let html = escapeHtml(text);
    // 标题
    for (let h = 1; h <= 4; h++) {
        html = html.replace(new RegExp(`\\[h${h}\\]([\\s\\S]*?)\\[\\/h${h}\\]`, 'g'), `<h${h}>$1</h${h}>`);
    }
    // 粗体 / 斜体 / 下划线
    html = html.replace(/\[b\]([\s\S]*?)\[\/b\]/g, '<b>$1</b>');
    html = html.replace(/\[i\]([\s\S]*?)\[\/i\]/g, '<i>$1</i>');
    html = html.replace(/\[u\]([\s\S]*?)\[\/u\]/g, '<u>$1</u>');
    // 链接（只放行 http/https，防 javascript: 注入）
    html = html.replace(/\[url=([^\]]*)\]([\s\S]*?)\[\/url\]/g, (m, url, text) => {
        return /^(https?:)?\/\//i.test(url) ? `<a href="${url}" target="_blank" rel="noopener">${text}</a>` : text;
    });
    html = html.replace(/\[url\]([\s\S]*?)\[\/url\]/g, (m, url) => {
        return /^(https?:)?\/\//i.test(url) ? `<a href="${url}" target="_blank" rel="noopener">${url}</a>` : url;
    });
    // 居中
    html = html.replace(/\[center\]([\s\S]*?)\[\/center\]/g, '<div style="text-align:center;">$1</div>');
    // 引用
    html = html.replace(/\[quote=([^\]]*)\]([\s\S]*?)\[\/quote\]/g, '<blockquote><div class="bb-quote-author">$1</div>$2</blockquote>');
    html = html.replace(/\[quote\]([\s\S]*?)\[\/quote\]/g, '<blockquote>$1</blockquote>');
    // 分隔线
    html = html.replace(/\[hr\]/g, '<hr>');
    // 有序 / 无序列表（[*] 列表项）
    html = html.replace(/\[olist\]([\s\S]*?)\[\/olist\]/g, (m, content) => {
        const items = content.split(/\[\*\]/g).map(s => s.trim()).filter(Boolean);
        return items.length ? '<ol>' + items.map(i => `<li>${i}</li>`).join('') + '</ol>' : '';
    });
    html = html.replace(/\[list\]([\s\S]*?)\[\/list\]/g, (m, content) => {
        const items = content.split(/\[\*\]/g).map(s => s.trim()).filter(Boolean);
        return items.length ? '<ul>' + items.map(i => `<li>${i}</li>`).join('') + '</ul>' : '';
    });
    // 残留的 [*] 兜底 + 清理未知 BBCode 标签
    html = html.replace(/\[\*\]/g, '<br>• ');
    html = html.replace(/\[[a-zA-Z0-9=_/]*\]/g, '');
    // 换行保留
    html = html.replace(/\n/g, '<br>');
    return html;
}

async function loadModMeta() {
    try {
        const res = await fetch('/api/mod_meta');
        const data = await res.json();
        modMeta = data;
        // 有 workshop ID 但还没本名 → 自动联网拉取一次
        if (data.needs_refresh && !modMetaRefreshing) {
            modMetaRefreshing = true;
            try {
                await fetch('/api/mod_meta/refresh', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{}' });
                const res2 = await fetch('/api/mod_meta');
                modMeta = await res2.json();
            } catch (e) {}
            modMetaRefreshing = false;
        }
    } catch (e) {}
}

async function ensureModMeta() {
    if (!modMeta.mods.length && !modMetaRefreshing) {
        await loadModMeta();
    }
}

// 刷新后显示成功/失败提示（📋 弹窗内联）
function showModMetaHint(data) {
    const el = document.getElementById('modMetaHint');
    if (!el) return;
    const failed = data.failed || {};
    const failCount = Object.keys(failed).length;
    let html = `<div class="alert ${failCount ? 'alert-warning' : 'alert-success'} border-0 py-2 mb-2" style="font-size:0.85rem;">`;
    html += `<i class="fas ${failCount ? 'fa-exclamation-triangle' : 'fa-check-circle'} me-1"></i>${escapeHtml(data.message || '刷新完成')}（成功 ${data.success_count ?? 0} 个${failCount ? '，失败 ' + failCount + ' 个' : ''}）`;
    if (failCount > 0) {
        html += `<ul class="mb-0 mt-1 ps-3" style="max-height:120px;overflow-y:auto;">`;
        for (const [pid, reason] of Object.entries(failed)) {
            html += `<li>ID ${escapeHtml(pid)}：${escapeHtml(reason)}</li>`;
        }
        html += `</ul>`;
    }
    html += `</div>`;
    el.innerHTML = html;
    el.style.display = '';
}

// 📋 模组列表弹窗里的刷新按钮：刷新 → 重新渲染当前列表 → 内联提示
async function refreshModMetaFromMods() {
    const btn = document.getElementById('modMetaRefreshBtn');
    if (btn) btn.disabled = true;
    try {
        const res = await fetch('/api/mod_meta/refresh', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ force: true })
        });
        const data = await res.json();
        await loadModMeta();
        // 重新渲染当前打开的模组列表
        try {
            const d2 = await refetchCurrentMods();
            if (d2) renderPackModsData(d2);
        } catch (e) {}
        showModMetaHint(data);
    } catch (e) {
        showModMetaHint({ message: '刷新失败: ' + e.message, failed: {} });
    }
    if (btn) btn.disabled = false;
}

// 全局设置弹窗里的刷新按钮：刷新 → alert 提示（含失败详情）
async function refreshModMetaFrontend() {
    const btns = document.querySelectorAll('#metaSettingsWrap button, [data-ms-refresh]');
    btns.forEach(b => b.disabled = true);
    try {
        const res = await fetch('/api/mod_meta/refresh', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ force: true })
        });
        const data = await res.json();
        await loadModMeta();
        const failed = data.failed || {};
        const failCount = Object.keys(failed).length;
        let msg = `${data.message || '刷新完成'}，成功 ${data.success_count ?? 0} 个${failCount ? '，失败 ' + failCount + ' 个' : ''}`;
        if (failCount > 0) {
            msg += '\n失败详情：\n' + Object.entries(failed).map(([pid, r]) => `ID ${pid}: ${r}`).join('\n');
        }
        alert(msg);
    } catch (e) {
        alert('刷新失败: ' + e.message);
    }
    btns.forEach(b => b.disabled = false);
}

// ---------- 查看模组列表（整合包模式 / 版本模式，共用弹窗） ----------
async function openPackMods(packId, versionId) {
    await openModsList('pack', packId, versionId);
}
async function openVersionMods(versionId) {
    await openModsList('version', null, versionId);
}

// 打开列表前按模式固定布局，避免打开后「3列↔2列」切换瞬间（版本=2列，整合包=3列）
function applyModsLayout(mode) {
    const show = (id, v) => { const el = document.getElementById(id); if (el) el.style.display = v; };
    const set = (id, txt) => { const el = document.getElementById(id); if (el) el.innerHTML = `<span class="text-secondary">${txt}</span>`; };
    const mw = document.getElementById('missingModsWrap');
    if (mode === 'version') {
        // 2 列：合并列表（启用上移+禁用下移）+ 缺失列表，等宽 col-md-6
        show('mergedModsWrap', '');
        show('enabledModsWrap', 'none');
        show('disabledModsWrap', 'none');
        if (mw) { mw.classList.remove('col-md-4'); mw.classList.add('col-md-6'); }
        set('mergedModsList', '加载中...');
    } else {
        // 3 列：已启用 + 未启用 + 缺失，各 col-md-4
        show('mergedModsWrap', 'none');
        show('enabledModsWrap', '');
        show('disabledModsWrap', '');
        if (mw) { mw.classList.remove('col-md-6'); mw.classList.add('col-md-4'); }
        set('enabledModsList', '加载中...');
        set('disabledModsList', '加载中...');
    }
    set('missingModsList', '加载中...');
}

async function openModsList(mode, packId, versionId) {
    if (typeof bootstrap === 'undefined') { alert('无法查看：Bootstrap 未加载。'); return; }
    currentModsMode = mode;
    currentModsPackId = packId;
    currentModsVersionId = versionId || '';
    currentModsSource = 'workshop';  // 每次打开默认检测该版本 Workshop 的模组
    // 每次打开重置搜索与标签筛选
    modSearchText = '';
    modFilterTags = [];
    modFilterDropdownOpen = false;
    const _si = document.getElementById('modSearchInput');
    if (_si) _si.value = '';
    const _fc = document.getElementById('modFilterCount');
    if (_fc) _fc.style.display = 'none';
    const _fb = document.getElementById('modFilterBtn');
    if (_fb) _fb.classList.remove('mod-filter-on');
    const _dd = document.getElementById('modFilterDropdown');
    if (_dd) _dd.style.display = 'none';
    document.getElementById('modMetaHint').style.display = 'none';
    mergedViewFilter = 'all';   // 每次打开重置为「全部」
    updatePackModsSourceUI();
    updateModsToolbar();
    await ensureModMeta();
    let title;
    if (mode === 'version') {
        const v = versions.find(x => x.id === versionId);
        title = (v ? v.name : versionId) + ' 的模组列表';
    } else {
        const pack = globalPacks.find(p => p.id === packId);
        title = (pack ? pack.name : packId) + ' 的模组列表';
    }
    document.getElementById('packModsTitle').textContent = title;
    applyModsLayout(mode);   // 打开前就固定好列布局与「加载中」占位，避免 3列↔2列 切换瞬间
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
    new bootstrap.Modal(document.getElementById('packModsModal')).show();

    try {
        const data = await refetchCurrentMods();
        if (data) renderPackModsData(data);
    } catch (e) {
        alert('获取失败: ' + e.message);
    }
}

// 按当前模式重新拉取模组列表数据
async function refetchCurrentMods() {
    if (!currentModsPackId && !currentModsVersionId) return null;
    if (currentModsMode === 'version') {
        const res = await fetch(`/api/versions/${encodeURIComponent(currentModsVersionId)}/mods`);
        const d = await res.json();
        if (!res.ok) throw new Error(d.error || '加载失败');
        return d;
    }
    return await fetchPackMods(currentModsPackId, currentModsVersionId);
}

// 拉取整合包模组列表（统一检测整合包文件夹 + 本地）
async function fetchPackMods(packId, versionId) {
    const url = `/api/packs/${encodeURIComponent(packId)}/mods?version=${encodeURIComponent(versionId || '')}`;
    const res = await fetch(url);
    const data = await res.json();
    if (!res.ok) throw new Error(data.error || '加载失败');
    return data;
}

// 渲染列表 + 计数（版本模式：合并「启用+禁用」+缺失列；整合包模式：三列）
function renderPackModsData(data) {
    buildModFilterTags(data);
    const enabled = data.enabled || [];
    const disabled = data.disabled || [];
    const missing = data.missing || [];
    const show = (id, v) => { const el = document.getElementById(id); if (el) el.style.display = v; };
    const mw = document.getElementById('missingModsWrap');
    if (currentModsMode === 'version') {
        // 合并：默认全部（启用绿上移 + 禁用灰下移）；工具栏可切换只显示启用/禁用；缺失单独一列，两列等宽（col-md-6）
        show('mergedModsWrap', '');
        show('enabledModsWrap', 'none');
        show('disabledModsWrap', 'none');
        if (mw) { mw.classList.remove('col-md-4'); mw.classList.add('col-md-6'); }
        let merged;
        if (mergedViewFilter === 'enabled') {
            merged = enabled.map(m => Object.assign({}, m, { _mtype: 'enabled' }));
        } else if (mergedViewFilter === 'disabled') {
            merged = disabled.map(m => Object.assign({}, m, { _mtype: 'disabled' }));
        } else {
            merged = [...enabled.map(m => Object.assign({}, m, { _mtype: 'enabled' })),
                      ...disabled.map(m => Object.assign({}, m, { _mtype: 'disabled' }))];
        }
        const mc = document.getElementById('mergedCount');
        if (mc) mc.textContent = renderModsList('mergedModsList', merged, 'enabled', true, '暂无模组').length;
        document.getElementById('missingCount').textContent = renderModsList('missingModsList', missing, 'missing', true).length;
    } else {
        show('mergedModsWrap', 'none');
        show('enabledModsWrap', '');
        show('disabledModsWrap', '');
        if (mw) { mw.classList.remove('col-md-6'); mw.classList.add('col-md-4'); }
        document.getElementById('enabledCount').textContent = renderModsList('enabledModsList', enabled, 'enabled', true).length;
        document.getElementById('disabledCount').textContent = renderModsList('disabledModsList', disabled, 'disabled', true).length;
        document.getElementById('missingCount').textContent = renderModsList('missingModsList', missing, 'missing', true).length;
    }
}

// 更新检测源提示（橙色加粗）：仅版本列表显示（整合包是打包分发用，不做来源检测）
function updatePackModsSourceUI() {
    const row = document.getElementById('packModsSourceRow');
    if (!row) return;
    if (currentModsMode === 'version') {
        row.style.display = '';
        const text = document.getElementById('packModsSourceText');
        if (text) text.textContent = '检测源：Workshop + 本地';
    } else {
        row.style.display = 'none';   // 整合包列表不显示检测源
    }
}

// ========== 模组列表弹窗工具栏（启用/禁用切换 + Tooltip 开关） ==========
// 循环切换合并列表显示过滤：全部 → 已启用 → 未启用 → 全部
function cycleMergedFilter() {
    mergedViewFilter = mergedViewFilter === 'all' ? 'enabled' : (mergedViewFilter === 'enabled' ? 'disabled' : 'all');
    updateModsToolbar();
    rerenderCurrentMods();
}

// 同步工具栏「合并显示」按钮状态与样式（仅版本模式显示）
function updateModsToolbar() {
    const b = document.getElementById('modsMergedBtn');
    if (b) {
        if (currentModsMode === 'version') {
            b.style.display = '';
            const map = {
                all: ['全部', 'btn-outline-info neon-btn'],
                enabled: ['已启用', 'btn-success'],
                disabled: ['未启用', 'btn-outline-secondary']
            };
            const [txt, cls] = map[mergedViewFilter] || map.all;
            b.innerHTML = `<i class="fas fa-layer-group me-1"></i>${txt}`;
            b.className = 'btn btn-sm ' + cls;
            b.title = '切换显示已启用 / 未启用模组（不影响缺失列）';
        } else {
            b.style.display = 'none';
        }
    }
    const hint = document.getElementById('mergedHint');
    if (hint) {
        hint.textContent = ({ all: '（绿色=已启用 / 灰色=未启用）', enabled: '（仅显示已启用）', disabled: '（仅显示未启用）' })[mergedViewFilter] || '';
    }
}

// 该模组是否有可加载的图标（用于排序：有图标上移）
function hasModIcon(name) {
    const m = (modMeta.mods || []).find(x => x.name === name);
    return !!(m && m.icon);
}

// 搜索 + 标签筛选（搜索匹配名称；标签取交集——模组必须包含所有选中标签）
function applyModFilter(mods) {
    const q = modSearchText.trim().toLowerCase();
    return (mods || []).filter(m => {
        const name = (typeof m === 'string') ? m : (m.name || '');
        if (q && name.toLowerCase().indexOf(q) < 0) return false;
        if (modFilterTags.length > 0) {
            const tags = ((m && m.tags) || []).map(t => String(t).toLowerCase());
            // 交集：每个选中标签都必须命中
            if (!modFilterTags.every(t => tags.indexOf(t.toLowerCase()) >= 0)) return false;
        }
        return true;
    });
}

function renderModsList(elId, mods, type, doFilter = false, emptyOverride) {
    const el = document.getElementById(elId);
    // 搜索 + 标签筛选（仅模组列表弹窗调用时开启 doFilter）
    let list = mods || [];
    if (doFilter) list = applyModFilter(list);
    if (!list.length) {
        // 有数据但被搜索/筛选过滤掉 → 「无匹配模组」；本来为空 → 分组空提示
        const hadData = (mods && mods.length > 0);
        const emptyText = hadData
            ? '无匹配模组'
            : (emptyOverride || ({ enabled: '暂无已启用的模组', disabled: '暂无未启用的模组', missing: '无缺失模组' }[type] || '（无）'));
        el.innerHTML = `<span class="text-secondary">${emptyText}</span>`;
        return [];
    }
    // 兼容字符串/对象输入；能加载到图标的模组排前面
    const sorted = [...list].sort((a, b) => {
        const ia = hasModIcon(a.name || a), ib = hasModIcon(b.name || b);
        if (ia !== ib) return ia ? -1 : 1;
        return 0;
    });
    el.innerHTML = sorted.map(m => {
        const name = m.name || m;
        const d = modDisplay(name);
        // 列表内信息统一以「在列表中显示统计数据」(showStats) 为总开关：
        // 需同时勾选 showStats 与对应细分开关，才在列表中显示；否则仅在模组详情弹窗显示。
        // 同时存在标记（仅版本模组列表可用）：Workshop 与本地都有同名模组 → 橙色 chip
        const conflictBadge = (currentModsMode === 'version' && showStats && showSource && m.conflict)
            ? `<div class="mod-conflict-badge">本地 + Workshop</div>` : '';
        // 更新（蓝色）：Steam 最近一次更新距今天数（不对比本地文件）
        const updBadge = (showStats && showUpdate && m.update_days_ago != null) ? `<div class="mod-update-badge">🔄 更新于 ${fmtDaysAgo(m.update_days_ago)}</div>` : '';
        // 标签（紫色，独立一行）
        const tags = (m.tags || []).filter(Boolean);
        const tagsHtml = (showStats && showTags && tags.length) ? `<div class="mod-tags-line">${tags.map(t => `<span class="mod-tag">${escapeHtml(t)}</span>`).join('')}</div>` : '';
        // 统计行（细分开关控制显示哪些项：showHeat=订阅/收藏/浏览，showSize=大小，showCreated=创建时间）
        const stats = [];
        if (showHeat && m.subscriptions) stats.push(`🔥 ${formatNum(m.subscriptions)}`);
        if (showHeat && m.favorited) stats.push(`⭐ ${formatNum(m.favorited)}`);
        if (showHeat && m.views) stats.push(`👁️ ${formatNum(m.views)}`);
        if (showCreated && m.time_created) stats.push(`📅 ${fmtDate(m.time_created)}`);
        if (showSize && m.file_size) stats.push(`💾 ${fmtSize(m.file_size)}`);
        if (m.banned) stats.push('🚫 已下架');
        const statsHtml = (showStats && stats.length) ? `<div class="mod-stats-line">${stats.map(s => `<span class="mod-stat">${s}</span>`).join('')}</div>` : '';
        // Tooltip 内容（受「Tooltip 信息设置」控制：模组名置顶蓝色 / 版本号 / 热度/大小/创建时间 / 标签紫色chip）
        const _mm = (modMeta.mods || []).find(x => x.name === name);
        const _ver = (_mm && _mm.version) ? _mm.version : '';
        const ttParts = [];
        if (showTooltipName) ttParts.push(`<span class="tt-name">${escapeHtml(d.label)}</span>`);   // 模组名置顶（蓝色，不加粗）
        if (showTooltipVersion && _ver) ttParts.push(`<span class="tt-ver">v${escapeHtml(_ver)}</span>`);
        const _ttStats = [];
        if (_mm) {
            if (showTooltipHeat && _mm.subscriptions) _ttStats.push(`🔥 ${formatNum(_mm.subscriptions)}`);
            if (showTooltipHeat && _mm.favorited) _ttStats.push(`⭐ ${formatNum(_mm.favorited)}`);
            if (showTooltipHeat && _mm.views) _ttStats.push(`👁️ ${formatNum(_mm.views)}`);
            if (showTooltipCreated && _mm.time_created) _ttStats.push(`📅 ${fmtDate(_mm.time_created)}`);
            if (showTooltipSize && _mm.file_size) _ttStats.push(`💾 ${fmtSize(_mm.file_size)}`);
        }
        if (_ttStats.length) ttParts.push(`<span class="tt-stat">${_ttStats.join(' · ')}</span>`);
        if (showTooltipTags && tags.length) ttParts.push(`<span class="tt-tags">${tags.map(t => `<span class="tt-tag">${escapeHtml(t)}</span>`).join(' ')}</span>`);
        const tooltipHtml = ttParts.join('<br>') || d.label;  // 全部关闭时兜底显示名称
        return `<div class="mod-item ${m._mtype || type}" data-tt="${escapeHtml(tooltipHtml)}" data-action="modDetail" data-name="${escapeHtml(name)}" data-pack="${escapeHtml(currentModsPackId || '')}" data-version="${escapeHtml(currentModsVersionId || '')}">
            <div class="mod-item-main">
                ${d.icon ? `<img class="mod-icon" src="${d.icon}" loading="lazy" decoding="async" onerror="this.style.display='none'" alt="">` : `<i class="fas fa-cube me-1"></i>`}
                <div class="mod-item-text">
                    <div class="mod-item-label">${d.labelHtml}</div>
                    ${conflictBadge}
                    ${updBadge}
                    ${tagsHtml}
                    ${statsHtml}
                </div>
            </div>
        </div>`;
    }).join('');
    return list;
}

// 重新渲染当前打开的模组列表（标签/开关变化时用）
async function rerenderCurrentMods() {
    try {
        const d2 = await refetchCurrentMods();
        if (d2) renderPackModsData(d2);
    } catch (e) {}
}

// ========== 模组列表：搜索 + 标签筛选 ==========
// 展开 / 收起标签下拉
function toggleModFilterDropdown() {
    modFilterDropdownOpen = !modFilterDropdownOpen;
    const dd = document.getElementById('modFilterDropdown');
    if (dd) dd.style.display = modFilterDropdownOpen ? '' : 'none';
}

// 收集当前整合包所有模组已有的标签（去重排序），渲染下拉选项
function buildModFilterTags(data) {
    const set = new Set();
    ['enabled', 'disabled', 'missing'].forEach(k => {
        (data[k] || []).forEach(m => {
            ((m && m.tags) || []).forEach(t => { if (t) set.add(String(t)); });
        });
    });
    const tags = [...set].sort((a, b) => a.localeCompare(b, 'zh'));
    const list = document.getElementById('modFilterTags');
    const btn = document.getElementById('modFilterBtn');
    if (!list || !btn) return;
    if (!tags.length) {
        list.innerHTML = '<div class="text-secondary small p-2">当前无模组标签</div>';
        return;
    }
    list.innerHTML = '';
    tags.forEach(t => {
        const on = modFilterTags.indexOf(t) >= 0;
        const label = document.createElement('label');
        label.className = 'mod-filter-tag' + (on ? ' on' : '');
        const cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.checked = on;
        cb.dataset.tag = t;   // 从 dataset 读取，天然防注入
        label.appendChild(cb);
        label.appendChild(document.createTextNode(' ' + t));
        list.appendChild(label);
    });
}

// 勾选 / 取消勾选标签
function toggleModFilterTag(tag, checked) {
    const i = modFilterTags.indexOf(tag);
    if (checked && i < 0) modFilterTags.push(tag);
    if (!checked && i >= 0) modFilterTags.splice(i, 1);
    document.querySelectorAll('#modFilterTags .mod-filter-tag').forEach(l => {
        const cb = l.querySelector('input');
        if (cb && cb.dataset.tag === tag) l.classList.toggle('on', !!cb.checked);
    });
    applyModFilterState();
}

// 清空全部选中标签
function clearModFilter() {
    modFilterTags = [];
    document.querySelectorAll('#modFilterTags input[type=checkbox]').forEach(cb => { cb.checked = false; });
    document.querySelectorAll('#modFilterTags .mod-filter-tag').forEach(l => l.classList.remove('on'));
    applyModFilterState();
}

// 更新筛选按钮状态（橙色 + 计数徽章）并重新渲染列表
function applyModFilterState() {
    const btn = document.getElementById('modFilterBtn');
    const countEl = document.getElementById('modFilterCount');
    const has = modFilterTags.length > 0;
    if (btn) btn.classList.toggle('mod-filter-on', has);
    if (countEl) {
        countEl.textContent = modFilterTags.length;
        countEl.style.display = has ? '' : 'none';
    }
    if (currentModsPackId) rerenderCurrentMods();
}

// 绑定搜索框 / 标签下拉 / 点击外部关闭
function initModFilterEvents() {
    const input = document.getElementById('modSearchInput');
    if (input) input.addEventListener('input', () => {
        modSearchText = input.value;
        if (currentModsPackId) rerenderCurrentMods();
    });
    const list = document.getElementById('modFilterTags');
    if (list) list.addEventListener('change', (e) => {
        const cb = e.target;
        if (cb && cb.type === 'checkbox' && cb.dataset.tag) {
            toggleModFilterTag(cb.dataset.tag, cb.checked);
        }
    });
    document.addEventListener('click', (e) => {
        if (modFilterDropdownOpen && !e.target.closest('#modFilterDropdown') && !e.target.closest('#modFilterBtn')) {
            modFilterDropdownOpen = false;
            const dd = document.getElementById('modFilterDropdown');
            if (dd) dd.style.display = 'none';
        }
    });
}

// 开关状态持久化：保存到后端 data.json（跨会话可靠）
async function saveMetaSetting(key, value) {
    try {
        await fetch('/api/meta_settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ [key]: value })
        });
    } catch (e) {}
}

// 从后端读取开关设置并同步变量 + 开关 UI
// 注意：不能 window[varName]=... 赋值——showTags 等是顶层 let 变量，不在 window 上
function applyMetaSettings(ms) {
    if (!ms) return;
    if (typeof ms.show_tags === 'boolean') showTags = ms.show_tags;
    if (typeof ms.show_stats === 'boolean') showStats = ms.show_stats;
    if (typeof ms.show_heat === 'boolean') showHeat = ms.show_heat;
    if (typeof ms.show_size === 'boolean') showSize = ms.show_size;
    if (typeof ms.show_update === 'boolean') showUpdate = ms.show_update;
    if (typeof ms.show_source === 'boolean') showSource = ms.show_source;
    if (typeof ms.show_created === 'boolean') showCreated = ms.show_created;
    if (typeof ms.show_tooltip === 'boolean') showTooltip = ms.show_tooltip;
    if (typeof ms.show_tooltip_version === 'boolean') showTooltipVersion = ms.show_tooltip_version;
    if (typeof ms.show_tooltip_name === 'boolean') showTooltipName = ms.show_tooltip_name;
    if (typeof ms.show_tooltip_tags === 'boolean') showTooltipTags = ms.show_tooltip_tags;
    if (typeof ms.show_tooltip_heat === 'boolean') showTooltipHeat = ms.show_tooltip_heat;
    if (typeof ms.show_tooltip_size === 'boolean') showTooltipSize = ms.show_tooltip_size;
    if (typeof ms.show_tooltip_created === 'boolean') showTooltipCreated = ms.show_tooltip_created;
    if (typeof ms.tmod_update === 'boolean') tmodUpdateCheck = ms.tmod_update;
    if (typeof ms.tmod_update_ignore_preview === 'boolean') tmodUpdateIgnorePreview = ms.tmod_update_ignore_preview;
    const sync = (id, val) => { const el = document.getElementById(id); if (el) el.checked = val; };
    // 全局设置弹窗里的开关（data-mskey 驱动，与特殊加载弹窗里的旧 ID 开关并行）
    const syncMs = (key, val) => { document.querySelectorAll(`[data-mskey="${key}"]`).forEach(el => { el.checked = val; }); };
    sync('showTagsToggle', showTags); syncMs('show_tags', showTags);
    sync('showStatsToggle', showStats); syncMs('show_stats', showStats);
    sync('showHeatToggle', showHeat); syncMs('show_heat', showHeat);
    sync('showSizeToggle', showSize); syncMs('show_size', showSize);
    sync('showUpdateToggle', showUpdate); syncMs('show_update', showUpdate);
    sync('showSourceToggle', showSource); syncMs('show_source', showSource);
    sync('showCreatedToggle', showCreated); syncMs('show_created', showCreated);
    syncMs('show_tooltip', showTooltip);
    sync('showTooltipVersionToggle', showTooltipVersion); syncMs('show_tooltip_version', showTooltipVersion);
    sync('showTooltipNameToggle', showTooltipName); syncMs('show_tooltip_name', showTooltipName);
    sync('showTooltipTagsToggle', showTooltipTags); syncMs('show_tooltip_tags', showTooltipTags);
    sync('showTooltipHeatToggle', showTooltipHeat); syncMs('show_tooltip_heat', showTooltipHeat);
    sync('showTooltipSizeToggle', showTooltipSize); syncMs('show_tooltip_size', showTooltipSize);
    sync('showTooltipCreatedToggle', showTooltipCreated); syncMs('show_tooltip_created', showTooltipCreated);
    sync('tmodUpdateToggle', tmodUpdateCheck); syncMs('tmod_update', tmodUpdateCheck);
    sync('tmodIgnorePreviewToggle', tmodUpdateIgnorePreview); syncMs('tmod_update_ignore_preview', tmodUpdateIgnorePreview);
    // 总开关同步子开关可用性（关闭总开关时置灰子开关）
    updateTooltipSubToggles();
    updateModlistSubToggles();
    updateModsToolbar();  // 同步模组列表弹窗 Tooltip 按钮（全局禁用时置灰）
}

// Tooltip 总开关 → 子开关可用性（关闭总开关时子开关置灰）
function updateTooltipSubToggles() {
    const on = showTooltip;
    document.querySelectorAll('#sp-tooltip [data-mskey]').forEach(t => {
        if (t.dataset.mskey !== 'show_tooltip') t.disabled = !on;
    });
}

// 模组列表「显示统计数据」总开关 → 子开关可用性
function updateModlistSubToggles() {
    const on = showStats;
    document.querySelectorAll('#sp-modlist [data-mskey]').forEach(t => {
        if (t.dataset.mskey !== 'show_stats') t.disabled = !on;
    });
}

// 标签显示开关
function initTagToggle() {
    const t = document.getElementById('showTagsToggle');
    if (!t) return;
    t.checked = showTags;
    t.addEventListener('change', () => {
        showTags = t.checked;
        saveMetaSetting('show_tags', showTags);
        rerenderCurrentMods();
    });
}

// 统计信息显示开关
function initStatsToggle() {
    const t = document.getElementById('showStatsToggle');
    if (!t) return;
    t.checked = showStats;
    t.addEventListener('change', () => {
        showStats = t.checked;
        saveMetaSetting('show_stats', showStats);
        rerenderCurrentMods();
    });
}

// 热度（订阅/收藏/浏览）显示开关
function initHeatToggle() {
    const t = document.getElementById('showHeatToggle');
    if (!t) return;
    t.checked = showHeat;
    t.addEventListener('change', () => {
        showHeat = t.checked;
        saveMetaSetting('show_heat', showHeat);
        rerenderCurrentMods();
    });
}

// 模组大小显示开关
function initSizeToggle() {
    const t = document.getElementById('showSizeToggle');
    if (!t) return;
    t.checked = showSize;
    t.addEventListener('change', () => {
        showSize = t.checked;
        saveMetaSetting('show_size', showSize);
        rerenderCurrentMods();
    });
}

// 更新信息显示开关
function initUpdateToggle() {
    const t = document.getElementById('showUpdateToggle');
    if (!t) return;
    t.checked = showUpdate;
    t.addEventListener('change', () => {
        showUpdate = t.checked;
        saveMetaSetting('show_update', showUpdate);
        rerenderCurrentMods();
    });
}

// 同时存在标记（仅版本模组列表）显示开关
function initSourceToggle() {
    const t = document.getElementById('showSourceToggle');
    if (!t) return;
    t.checked = showSource;
    t.addEventListener('change', () => {
        showSource = t.checked;
        saveMetaSetting('show_source', showSource);
        rerenderCurrentMods();
    });
}

// 模组创建时间显示开关
function initCreatedToggle() {
    const t = document.getElementById('showCreatedToggle');
    if (!t) return;
    t.checked = showCreated;
    t.addEventListener('change', () => {
        showCreated = t.checked;
        saveMetaSetting('show_created', showCreated);
        rerenderCurrentMods();
    });
}

// Tooltip 信息设置开关（版本号 / 模组名 / 标签）
function initTooltipToggles() {
    const bind = (id, getVal, setVal, key) => {
        const t = document.getElementById(id);
        if (!t) return;
        t.checked = getVal();
        t.addEventListener('change', () => {
            setVal(t.checked);
            saveMetaSetting(key, t.checked);
            rerenderCurrentMods();
        });
    };
    bind('showTooltipVersionToggle', () => showTooltipVersion, v => { showTooltipVersion = v; }, 'show_tooltip_version');
    bind('showTooltipNameToggle', () => showTooltipName, v => { showTooltipName = v; }, 'show_tooltip_name');
    bind('showTooltipTagsToggle', () => showTooltipTags, v => { showTooltipTags = v; }, 'show_tooltip_tags');
    bind('showTooltipHeatToggle', () => showTooltipHeat, v => { showTooltipHeat = v; }, 'show_tooltip_heat');
    bind('showTooltipSizeToggle', () => showTooltipSize, v => { showTooltipSize = v; }, 'show_tooltip_size');
    bind('showTooltipCreatedToggle', () => showTooltipCreated, v => { showTooltipCreated = v; }, 'show_tooltip_created');
}

// ========== tModLoader 更新检测 ==========
// 逐段数值比较版本号（容忍 'v' 前缀等非数字字符）。返回 1 / -1 / 0
function compareVersions(a, b) {
    const pa = String(a).replace(/[^0-9.]/g, '').split('.').map(s => parseInt(s, 10) || 0);
    const pb = String(b).replace(/[^0-9.]/g, '').split('.').map(s => parseInt(s, 10) || 0);
    for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
        const x = pa[i] || 0, y = pb[i] || 0;
        if (x !== y) return x > y ? 1 : -1;
    }
    return 0;
}

// 当前生效的最新版本（忽略 preview 时只取 stable）
function tmodLatest() {
    if (!tmodUpdate || !tmodUpdate.ok) return null;
    if (tmodUpdateIgnorePreview && tmodUpdate.latest_stable) return tmodUpdate.latest_stable;
    return tmodUpdate.latest_preview || tmodUpdate.latest_stable || null;
}

// 本地版本是否有可用更新
function hasTmodUpdate(localVer) {
    if (!tmodUpdateCheck) return false;
    const latest = tmodLatest();
    if (!latest || !localVer) return false;
    return compareVersions(latest.version, localVer) > 0;
}

// 更新状态栏的 tModLoader 更新信息
// 成功(ok) → 蓝色版本号+天数，点击跳转 GitHub Releases
// 离线(offline) → 灰色「离线状态」；多次失败(fetch_failed) → 红色「无法获取 tModLoader 更新信息」；后两者点击重新检测
function updateGlobalInfo() {
    const wrap = document.getElementById('tmodUpdateWrap');
    const text = document.getElementById('tmodUpdateText');
    if (!wrap || !text) return;
    wrap.onclick = null;
    wrap.title = '';
    wrap.style.cursor = '';
    if (!tmodUpdateCheck || !tmodUpdate || !tmodUpdate.ok) {
        // 开关关闭 / 无数据 / 正在拉取 → 隐藏
        if (!tmodUpdateCheck || !tmodUpdate || tmodUpdate.reason === 'checking') {
            wrap.style.display = 'none';
            return;
        }
        // offline / fetch_failed：显示状态文案，点击重试
        const offline = tmodUpdate.reason === 'offline';
        wrap.className = 'tmod-update-wrap ' + (offline ? 'tmod-update-offline' : 'tmod-update-failed');
        text.textContent = offline ? '离线状态' : '无法获取 tModLoader 更新信息';
        wrap.style.display = 'flex';
        wrap.style.cursor = 'pointer';
        wrap.title = '点击重新检测';
        wrap.onclick = () => retryTmodUpdate();
        return;
    }
    // 成功：取当前生效通道的最新版本（忽略 preview 时用 stable）
    const latest = tmodLatest();
    if (!latest) { wrap.style.display = 'none'; return; }
    const days = (typeof latest.days === 'number') ? latest.days : null;
    let s = '⬆️ tModLoader v' + latest.version;
    if (days !== null) s += ' · ' + (days <= 0 ? '今天' : '更新于 ' + days + ' 天前');
    text.textContent = s;
    wrap.className = 'tmod-update-wrap tmod-update-ok';
    wrap.style.display = 'flex';
    wrap.style.cursor = 'pointer';
    wrap.title = '点击查看 GitHub Releases';
    wrap.onclick = () => openTmodRelease(latest.url);
}

// 加载 tModLoader 更新信息；区分状态：
// checking/fetch_failed → 每 30s 自动重试（最多 10 次）；offline → 停止自动重试，显示离线状态，点击可重试
function loadTmodUpdate(retries = 10) {
    fetch('/api/tmod_update').then(r => r.json()).then(data => {
        tmodUpdate = data;
        if (!data.ok && retries > 0 && data.reason !== 'offline') {
            setTimeout(() => loadTmodUpdate(retries - 1), 30000);
        }
    }).catch(() => { tmodUpdate = null; }).then(() => {
        updateGlobalInfo();
        if (currentView === 'versions') renderVersions();
    });
}

// 打开对应 GitHub Release 页（系统默认浏览器）
function openTmodRelease(url) {
    const target = (url && url.indexOf('http') === 0) ? url : 'https://github.com/tModLoader/tModLoader/releases';
    fetch('/api/open_url', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url: target })
    }).catch(() => {});
}

// 手动重新检测（离线 / 失败状态点击徽章时）
function retryTmodUpdate() {
    loadTmodUpdate(10);
}

// 「更新对比」开关
function initTmodUpdateToggle() {
    const t = document.getElementById('tmodUpdateToggle');
    if (!t) return;
    t.checked = tmodUpdateCheck;
    t.addEventListener('change', () => {
        tmodUpdateCheck = t.checked;
        saveMetaSetting('tmod_update', tmodUpdateCheck);
        updateGlobalInfo();
        renderVersions();
    });
}

// 「忽略 Preview 版」开关
function initTmodIgnorePreviewToggle() {
    const t = document.getElementById('tmodIgnorePreviewToggle');
    if (!t) return;
    t.checked = tmodUpdateIgnorePreview;
    t.addEventListener('change', () => {
        tmodUpdateIgnorePreview = t.checked;
        saveMetaSetting('tmod_update_ignore_preview', tmodUpdateIgnorePreview);
        updateGlobalInfo();
        renderVersions();
    });
}

// 打开模组详情弹窗，并确保其浮在所有其它弹窗（如「当前 enabled.json」）之上
function showModDetailModal() {
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
    new bootstrap.Modal(document.getElementById('modDetailModal')).show();
    // 详情弹窗的 backdrop 也提到其它弹窗之上，保证暗层正常覆盖背后的列表弹窗
    const bd = document.querySelectorAll('.modal-backdrop');
    if (bd.length) bd[bd.length - 1].style.zIndex = '2055';
}

// 点击模组查看详情：🔥订阅数 + 描述 + 其它统计
function openModDetail(name) {
    if (typeof bootstrap === 'undefined') { alert('无法查看详情：Bootstrap 未加载。'); return; }
    const meta = (modMeta.mods || []).find(x => x.name === name);
    if (!meta) {
        // 兜底：元数据里没有该模组（如未扫描到的 Workshop 模组）也打开详情，保证点击始终有响应
        const d = modDisplay(name);
        document.getElementById('modDetailTitle').textContent = name;
        document.getElementById('modDetailBody').innerHTML =
            `<div class="d-flex align-items-center gap-2"><i class="fas fa-cube me-2"></i><span>${escapeHtml(d.label)}</span></div>` +
            `<div class="text-secondary mt-2"><i class="fas fa-info-circle me-1"></i>暂无详细数据：本地 / Workshop 元数据中未找到该模组，可点击「刷新模组元数据」后重试。</div>`;
        showModDetailModal();
        return;
    }
    document.getElementById('modDetailTitle').textContent = meta.display_name ? `${stripTmlTags(meta.display_name)}（${name}）` : name;
    let html = '';
    // 图标 + 标签：标签显示在图标右侧的空白处（受"显示标签"开关控制）
    const modTags = showTags ? (meta.tags || []) : [];
    const tagsHtml = modTags.length ? `<div class="mod-detail-tags">${modTags.map(t => `<span class="mod-tag">${escapeHtml(t)}</span>`).join('')}</div>` : '';
    if (meta.icon) {
        html += `<div class="mod-detail-head"><img src="${meta.icon}" class="mod-detail-icon" decoding="async" onerror="this.style.display='none'" alt="">${tagsHtml}</div>`;
    } else if (tagsHtml) {
        html += tagsHtml;
    }
    html += `<div class="mod-detail-stats">`;
    if (meta.version) html += `<span class="mod-stat">🏷️ v${escapeHtml(meta.version)}</span>`;
    if (showHeat && meta.subscriptions != null) html += `<span class="mod-stat">🔥 订阅 ${formatNum(meta.subscriptions)}</span>`;
    if (showUpdate && meta.update_days_ago != null) html += `<span class="mod-stat">🔄 更新于 ${fmtDaysAgo(meta.update_days_ago)}</span>`;
    if (showHeat && meta.favorited) html += `<span class="mod-stat">⭐ 收藏 ${formatNum(meta.favorited)}</span>`;
    if (showHeat && meta.views) html += `<span class="mod-stat">👁️ 浏览 ${formatNum(meta.views)}</span>`;
    if (meta.time_created) html += `<span class="mod-stat">📅 创建于 ${fmtDate(meta.time_created)}</span>`;
    if (showSize && meta.file_size) html += `<span class="mod-stat">💾 ${fmtSize(meta.file_size)}</span>`;
    if (meta.banned) html += `<span class="mod-stat">🚫 已下架</span>`;
    html += `</div>`;
    if (meta.description) {
        html += `<div class="d-flex align-items-center justify-content-between mb-1">
            <span class="text-secondary small">简介</span>
            <button class="btn btn-sm btn-outline-info neon-btn" onclick="copyModDesc()"><i class="fas fa-copy me-1"></i>复制</button>
        </div>`;
        html += `<div class="mod-detail-desc">${bbcodeToHtml(meta.description)}</div>`;
    } else {
        html += `<div class="text-secondary">暂无描述</div>`;
    }
    document.getElementById('modDetailBody').innerHTML = html;
    showModDetailModal();
}

// 📋 模组列表弹窗：Tooltip 悬浮 + 点击详情委托（packModsModal / currentEnabledModal 通用）
function bindModsModalInteractions(modalId) {
    const modal = document.getElementById(modalId);
    if (!modal) return;
    modal.addEventListener('mousemove', (e) => {
        const item = e.target.closest('.mod-item');
        if (showTooltip && item && item.dataset.tt) {
            const tip = document.getElementById('modTooltip');
            if (tip) {
                // 移到另一个模组 → 立即更新内容（否则 tooltip 会残留上一个模组的信息）
                if (item !== _lastTooltipItem) {
                    tip.innerHTML = item.dataset.tt;
                    _lastTooltipItem = item;
                }
                if (tip.style.display !== 'block') tip.style.display = 'block';
                positionModTooltip(e);
            }
        } else {
            hideModTooltip();
        }
    });
    modal.addEventListener('click', (e) => {
        const item = e.target.closest('[data-action="modDetail"]');
        if (!item) return;
        hideModTooltip();
        openModDetail(item.dataset.name);
    });
    modal.addEventListener('mouseleave', hideModTooltip);
}
bindModsModalInteractions('packModsModal');
bindModsModalInteractions('currentEnabledModal');

// ---------- 模组悬浮 Tooltip（自定义浮动框，显示版本号；取不到版本号时显示名称） ----------
function positionModTooltip(e) {
    const tip = document.getElementById('modTooltip');
    if (!tip) return;
    const pad = 12;
    let x = e.clientX + pad, y = e.clientY + pad;
    const r = tip.getBoundingClientRect();
    if (x + r.width > window.innerWidth - 8) x = e.clientX - r.width - pad;
    if (y + r.height > window.innerHeight - 8) y = e.clientY - r.height - pad;
    tip.style.left = x + 'px';
    tip.style.top = y + 'px';
}
let _lastTooltipItem = null;  // 当前 tooltip 正在显示的模组元素（用于内容实时更新）
function hideModTooltip() {
    _lastTooltipItem = null;
    const tip = document.getElementById('modTooltip');
    if (tip) tip.style.display = 'none';
}

// ---------- 文件浏览（启动 .bat 选择） ----------
// 调用后端 tkinter 弹系统文件框，选中后自动填充输入框
async function pickBatFile(inputId) {
    const el = document.getElementById(inputId);
    if (!el) return;
    try {
        const res = await fetch('/api/pick_bat', { method: 'POST' });
        const data = await res.json();
        if (data && data.path) el.value = data.path;
    } catch (e) {
        showAppAlert('无法打开文件选择框: ' + e.message, '错误');
    }
}

// ---------- 直接启动某版本（不做任何修改） ----------
// 「直接启动」：用该版本的 .bat 启动，使用当前 Mods 目录的 enabled.json，不覆盖 enabled.json、不应用特殊加载
async function launchDirect(versionId) {
    try {
        const res = await fetch(`/api/versions/${encodeURIComponent(versionId)}/launch_direct`, { method: 'POST' });
        const data = await res.json();
        if (res.ok) {
            showToast(data.message, '启动成功');
            fetchStatus();
        } else {
            showAppAlert(data.error || '启动失败', '无法启动');
        }
    } catch (e) {
        showAppAlert('请求失败: ' + e.message, '无法启动');
    }
}

// 「以特殊加载配置启动」：应用全局特殊加载配置后启动该版本（禁用列表内本地模组，退出自动还原）
async function launchSpecial(versionId) {
    try {
        const res = await fetch(`/api/versions/${encodeURIComponent(versionId)}/launch_special`, { method: 'POST' });
        let data = {};
        try { data = await res.json(); } catch { /* 空 body / 非 JSON：用 HTTP 状态兜底 */ }
        if (res.ok) {
            showToast(data.message || '启动成功', '启动成功');
            fetchStatus();
        } else {
            showAppAlert(data.error || ('启动失败（HTTP ' + res.status + '）'), '无法启动');
        }
    } catch (e) {
        showAppAlert('请求失败: ' + e.message, '无法启动');
    }
}

// ---------- 启动当前配置（不做任何修改） ----------
// 「启动游戏（当前配置）」：用上次启动的 .bat 直接启动，不动 enabled.json
async function launchCurrentNow() {
    try {
        const res = await fetch('/api/launch_current', { method: 'POST' });
        const data = await res.json();
        if (res.ok) {
            showToast(data.message, '启动成功');
            fetchStatus();
        } else {
            showAppAlert(data.error || '启动失败', '无法启动');
        }
    } catch (e) {
        showAppAlert('请求失败: ' + e.message, '无法启动');
    }
}

// ---------- 当前 enabled.json 模组列表 ----------
async function openCurrentEnabled() {
    if (typeof bootstrap === 'undefined') { alert('无法查看：Bootstrap 未加载。'); return; }
    await ensureModMeta();
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
    new bootstrap.Modal(document.getElementById('currentEnabledModal')).show();
    document.getElementById('currentEnabledList').innerHTML = '<span class="text-secondary">加载中...</span>';
    try {
        const res = await fetch('/api/current_enabled');
        const d = await res.json();
        if (!res.ok) { alert('获取失败: ' + d.error); return; }
        document.getElementById('currentEnabledCount').textContent = `已启用 ${d.count} 个模组`;
        renderModsList('currentEnabledList', (d.mods || []).map(n => ({ name: n })), 'enabled');
    } catch (e) { alert('请求失败: ' + e.message); }
}

// ---------- 版本操作 ----------
function openAddVersionModal() {
    if (typeof bootstrap === 'undefined') { alert('无法打开：Bootstrap 未加载。'); return; }
    document.getElementById('versionName').value = '';
    document.getElementById('batPath').value = '';
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');
    new bootstrap.Modal(document.getElementById('addVersionModal')).show();
}

document.getElementById('saveAddVersionBtn').addEventListener('click', async () => {
    const name = document.getElementById('versionName').value.trim();
    const bat_path = document.getElementById('batPath').value.trim();

    if (!name || !bat_path) {
        alert('请填写版本名称和启动文件路径');
        return;
    }

    const res = await fetch('/api/versions', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, bat_path })
    });
    if (res.ok) {
        hideModal('addVersionModal');
        loadData();
    } else {
        const err = await res.json();
        alert('添加失败: ' + err.error);
    }
});

async function deleteVersion(versionId) {
    showAppConfirm('确定删除该版本吗？', async () => {
        const res = await fetch(`/api/versions/${versionId}`, { method: 'DELETE' });
        if (res.ok) loadData();
        else showAppAlert('删除失败');
    });
}

async function restoreVersion(versionId) {
    showAppConfirm('将还原该版本的 enabled.json 到上次启动前的状态，确定？', async () => {
        const res = await fetch(`/api/versions/${versionId}/restore`, { method: 'POST' });
        const data = await res.json();
        showAppAlert(data.message || data.error);
    });
}

// ---------- 整合包操作 ----------
function openModal(versionId) {
    if (typeof bootstrap === 'undefined') {
        // 离线环境（CDN 未加载）兜底
        alert('无法打开添加整合包窗口：Bootstrap 未加载（可能是离线环境）。');
        return;
    }
    if (modalInstance) {
        modalInstance.dispose();
        modalInstance = null;
    }
    document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
    document.body.classList.remove('modal-open');

    const container = document.getElementById('modalPackList');
    if (globalPacks.length === 0) {
        container.innerHTML = `<div class="text-secondary">暂无整合包</div>`;
    } else {
        const version = versions.find(v => v.id === versionId);
        const added = version ? version.packs : [];
        let html = '';
        globalPacks.forEach(p => {
            const isAdded = added.includes(p.id);
            html += `
                <div class="list-group-item bg-transparent border-0 d-flex justify-content-between align-items-center">
                    <span>${escapeHtml(p.name)}</span>
                    ${isAdded ? '<span class="badge bg-secondary">已添加</span>' :
                    `<button class="btn btn-sm btn-primary neon-btn" data-action="addPack" data-version="${versionId}" data-pack="${escapeHtml(p.id)}">添加</button>`}
                </div>
            `;
        });
        container.innerHTML = html;
    }

    const modalEl = document.getElementById('addPackModal');
    modalInstance = new bootstrap.Modal(modalEl);
    modalInstance.show();
}

// ========== 添加整合包模态框事件委托 ==========
document.getElementById('modalPackList').addEventListener('click', (e) => {
    const btn = e.target.closest('button[data-action="addPack"]');
    if (!btn) return;
    addPack(btn.dataset.version, btn.dataset.pack);
});

async function addPack(versionId, packId) {
    try {
        const res = await fetch(`/api/versions/${versionId}/packs`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ pack_id: packId })
        });
        const data = await res.json();
        if (res.ok) {
            if (modalInstance) {
                modalInstance.hide();
            }
            setTimeout(() => {
                if (modalInstance) {
                    modalInstance.dispose();
                    modalInstance = null;
                }
                document.querySelectorAll('.modal-backdrop').forEach(el => el.remove());
                document.body.classList.remove('modal-open');
                loadData();
            }, 300);
        } else {
            alert(data.error);
        }
    } catch (e) {
        alert('添加失败: ' + e.message);
    }
}

async function removePack(versionId, packId) {
    showAppConfirm('确定移除该整合包吗？', async () => {
        const res = await fetch(`/api/versions/${versionId}/packs/${packId}`, { method: 'DELETE' });
        if (res.ok) {
            loadData();
        } else {
            const data = await res.json();
            showAppAlert(data.error);
        }
    });
}

async function launchPack(versionId, packId) {
    showAppConfirm('启动将覆盖该版本的 enabled.json，是否继续？', async () => {
        try {
            const res = await fetch(`/api/versions/${versionId}/launch/${packId}`, { method: 'POST' });
            const data = await res.json();
            if (res.ok) {
                showToast(data.message, '启动成功');
                // 刷新状态以更新运行中标志
                fetchStatus();
            } else {
                showAppAlert('启动失败: ' + data.error);
            }
        } catch (e) {
            showAppAlert('请求失败: ' + e.message);
        }
    });
}

async function openModPacks() {
    const res = await fetch('/api/open_modpacks', { method: 'POST' });
    const data = await res.json();
    if (data.error) alert(data.error);
}

// ========== 启动状态轮询 ==========
function startStatusPolling() {
    fetchStatus();
    if (statusInterval) clearInterval(statusInterval);
    // 后端进程检测带 2s 缓存，轮询频率降到 2s 减少无效请求
    statusInterval = setInterval(fetchStatus, 2000);
}

// ---------- pywebview 独立窗口：自绘标题栏 ----------
// 显示标题栏 + 绑定最小化/全屏/关闭；关闭走后端 /api/shutdown（进程退出 → 窗口销毁 → 后端停止）
function initDesktopTitlebar() {
    if (document.body.dataset.desktop !== 'true') return;
    const bar = document.getElementById('winTitlebar');
    if (!bar) return;
    bar.style.display = '';
    // pywebview 的 JS API 注入可能晚于页面脚本执行 → 每次点击实时取，不缓存
    const getPy = () => (typeof window.pywebview !== 'undefined') ? window.pywebview : null;
    // 标题栏按下（非按钮区域）→ 原生窗口拖动（系统级流畅，无鼠标滞后）
    bar.addEventListener('mousedown', (e) => {
        if (e.button !== 0) return;                  // 仅左键
        if (e.target.closest('.win-btn')) return;     // 窗口按钮不触发拖动
        const py = getPy();
        if (py && py.api && py.api.start_drag) py.api.start_drag();
    });
    const minBtn = document.getElementById('winMinBtn');
    const maxBtn = document.getElementById('winMaxBtn');
    const closeBtn = document.getElementById('winCloseBtn');
    // 窗口控制（最小化/全屏/关闭）一律走 js_api（pywebview 6.x 的 window.pywebview.window 不存在）
    if (minBtn) minBtn.addEventListener('click', () => {
        const py = getPy();
        try { if (py && py.api && py.api.minimize) py.api.minimize(); } catch (e) {}
    });
    // 按钮图标按真实窗口状态：最大化 → 还原(🗗)，普通窗口 → 全屏(⛶)
    const setMaxIcon = (maxed) => {
        if (!maxBtn) return;
        const icon = maxBtn.querySelector('i');
        if (icon) icon.className = maxed ? 'fas fa-compress' : 'fas fa-expand';
    };
    if (maxBtn) maxBtn.addEventListener('click', () => {
        const py = getPy();
        if (py && py.api && py.api.toggle_fullscreen) {
            try { Promise.resolve(py.api.toggle_fullscreen()).then(s => setMaxIcon(!!s)).catch(() => {}); } catch (e) {}
        }
    });
    // js_api 注入可能晚（2-4s），启动后延迟查询一次真实状态以同步初始图标
    setTimeout(() => {
        const py = getPy();
        if (py && py.api && py.api.is_fullscreen) {
            try { Promise.resolve(py.api.is_fullscreen()).then(s => setMaxIcon(!!s)).catch(() => {}); } catch (e) {}
        }
    }, 4000);
    if (closeBtn) closeBtn.addEventListener('click', () => {
        const py = getPy();
        // 1) 优先 js_api 直接终止进程（不依赖 fetch，可靠且瞬间关闭）
        if (py && py.api && typeof py.api.shutdown === 'function') {
            try { py.api.shutdown(); } catch (e) {}
            return;
        }
        // 2) 兜底：fetch shutdown / 关闭页面
        try { fetch('/api/shutdown', { method: 'POST' }).catch(() => {}); } catch (e) {}
        try { window.close(); } catch (e) {}
    });
}

// ========== 自定义右键菜单（替换 Chromium 默认菜单） ==========
let ctxData = null;   // 当前菜单上下文：{ kind, versionId | name }
let ctxMenuEl = null;

function hideContextMenu() {
    if (ctxMenuEl) { ctxMenuEl.remove(); ctxMenuEl = null; }
    ctxData = null;
}

// 通用菜单渲染：items = [{ action, label, icon? } | { sep: true }]
function showContextMenu(items, x, y) {
    hideContextMenu();
    ctxMenuEl = document.createElement('div');
    ctxMenuEl.id = 'ctx-menu';
    ctxMenuEl.innerHTML = items.map(it => it.sep
        ? '<div class="ctx-sep"></div>'
        : `<div class="ctx-item" data-ctx-action="${it.action}">${it.icon || ''}${escapeHtml(it.label)}</div>`
    ).join('');
    document.body.appendChild(ctxMenuEl);
    const r = ctxMenuEl.getBoundingClientRect();
    ctxMenuEl.style.left = Math.max(4, Math.min(x, window.innerWidth - r.width - 8)) + 'px';
    ctxMenuEl.style.top = Math.max(4, Math.min(y, window.innerHeight - r.height - 8)) + 'px';
}

// ---- 版本卡片菜单（编辑功能已整合进右键菜单；「以特殊加载配置启动」置顶） ----
function showVersionMenu(versionId, x, y) {
    showContextMenu([
        { action: 'launchSpecial', label: '以「特殊加载配置」启动', icon: '<i class="fas fa-play me-2 text-success"></i>' },
        { sep: true },
        { action: 'openFolder', label: '打开当前版本文件夹', icon: '<i class="fas fa-folder-open me-2 text-warning"></i>' },
        { action: 'editVersion', label: '编辑版本', icon: '<i class="fas fa-pen me-2"></i>' },
        { action: 'deleteVersion', label: '删除版本', icon: '<i class="fas fa-trash-alt me-2 text-danger"></i>' },
    ], x, y);
    ctxData = { kind: 'version', versionId };
}

function openVersionFolder() {
    const v = versions.find(vv => vv.id === (ctxData ? ctxData.versionId : null));
    if (!v || !v.bat_path) {
        showErrorModal('该版本没有启动脚本路径，无法打开文件夹');
        return;
    }
    fetch('/api/open_version_folder', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ bat_path: v.bat_path })
    })
        .then(res => res.json())
        .then(data => { if (!data.ok && data.error) showErrorModal(data.error); })
        .catch(err => showErrorModal('打开失败: ' + err.message));
}

// ---- 模组列表项菜单 ----
function showModMenu(name, x, y, packId, versionId) {
    showContextMenu([
        { action: 'openModFolder', label: '打开本地 .tmod 所在文件夹', icon: '<i class="fas fa-folder-open me-2 text-info"></i>' },
        { action: 'openSteamPage', label: '打开 Steam Workshop 页面', icon: '<i class="fas fa-external-link-alt me-2 text-primary"></i>' },
        { sep: true },
        { action: 'copyName', label: '复制模组名', icon: '<i class="fas fa-copy me-2"></i>' },
        { action: 'copyWsid', label: '复制 Workshop ID', icon: '<i class="fas fa-copy me-2"></i>' },
    ], x, y);
    // 记录当前模组列表上下文：优先用模组项自身携带的 data-pack/data-version（严格对应右键所在列表），
    // 缺省时回退到当前弹窗的全局上下文；后端据此精确定位 .tmod
    ctxData = {
        kind: 'mod',
        name,
        packId: packId || (currentModsMode === 'pack' ? (currentModsPackId || '') : ''),
        versionId: versionId || (currentModsMode === 'version' ? (currentModsVersionId || '') : ''),
    };
}

function getModMeta(name) {
    return (modMeta.mods || []).find(x => x.name === name) || null;
}

function openModFolder(name, packId, versionId) {
    fetch('/api/open_mod_folder', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, pack_id: packId || '', version_id: versionId || '' })
    })
        .then(res => res.json())
        .then(data => { if (!data.ok && data.error) showErrorModal(data.error); })
        .catch(err => showErrorModal('打开失败: ' + err.message));
}

function openModSteamPage(name) {
    const mm = getModMeta(name);
    if (!mm || !mm.workshop_id) {
        showErrorModal('该模组没有 Workshop ID（未从 Steam 获取到元数据）');
        return;
    }
    fetch('/api/open_url', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url: `https://steamcommunity.com/sharedfiles/filedetails/?id=${mm.workshop_id}` })
    })
        .then(res => res.json())
        .then(data => { if (!data.ok && data.error) showErrorModal(data.error); })
        .catch(err => showErrorModal('打开失败: ' + err.message));
}

function handleCtxAction(action) {
    if (!ctxData) return;
    if (ctxData.kind === 'version') {
        if (action === 'launchSpecial') launchSpecial(ctxData.versionId);
        else if (action === 'openFolder') openVersionFolder();
        else if (action === 'editVersion') openVersionEdit(ctxData.versionId);
        else if (action === 'deleteVersion') deleteVersion(ctxData.versionId);
    } else if (ctxData.kind === 'mod') {
        if (action === 'openModFolder') openModFolder(ctxData.name, ctxData.packId, ctxData.versionId);
        else if (action === 'openSteamPage') openModSteamPage(ctxData.name);
        else if (action === 'copyName') copyToClipboard(ctxData.name, '模组名');
        else if (action === 'copyWsid') {
            const mm = getModMeta(ctxData.name);
            copyToClipboard(mm && mm.workshop_id ? mm.workshop_id : '', 'Workshop ID');
        }
    }
}

function initContextMenu() {
    document.addEventListener('contextmenu', (e) => {
        const card = e.target.closest('.version-card');
        if (card) {
            e.preventDefault();
            showVersionMenu(card.dataset.version, e.clientX, e.clientY);
            return;
        }
        const mod = e.target.closest('.mod-item');
        if (mod) {
            e.preventDefault();
            const name = mod.dataset.name;
            if (name) showModMenu(name, e.clientX, e.clientY, mod.dataset.pack, mod.dataset.version);
            return;
        }
        hideContextMenu();
    });
    document.addEventListener('click', (e) => {
        if (ctxMenuEl) {
            const item = e.target.closest('.ctx-item');
            if (item) handleCtxAction(item.dataset.ctxAction);
            hideContextMenu();
        }
    });
    // 滚动 / Esc 关闭菜单
    document.addEventListener('scroll', hideContextMenu, true);
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideContextMenu(); });
}

// ========== 初始化 ==========
loadData();
startStatusPolling();
initTagToggle();
initStatsToggle();
initHeatToggle();
initSizeToggle();
initUpdateToggle();
initSourceToggle();
initCreatedToggle();
initTooltipToggles();
initTmodUpdateToggle();
initTmodIgnorePreviewToggle();
initSettingsModal();
initModFilterEvents();
initDesktopTitlebar();
initContextMenu();
// 首次启动：版本号在后台预热检测（PowerShell ~4s），完成后再静默刷新一次显示
setTimeout(loadData, 6000);