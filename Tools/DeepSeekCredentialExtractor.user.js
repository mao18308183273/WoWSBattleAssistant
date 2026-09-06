// ==UserScript==
// @name         DeepSeek 凭证一键提取器 (Token + Cookie)
// @namespace    http://tampermonkey.net/
// @version      4.0
// @description  从 chat.deepseek.com 一键提取 Token 和 Cookie，支持拦截式获取、验证有效性、分别复制
// @match        https://chat.deepseek.com/*
// @match        https://*.deepseek.com/*
// @grant        GM_xmlhttpRequest
// @grant        GM_registerMenuCommand
// @grant        GM_setClipboard
// @connect      chat.deepseek.com
// @run-at       document-start
// ==/UserScript==

(function () {
    'use strict';

    if (window.__ds_auth_extractor_injected) return;
    window.__ds_auth_extractor_injected = true;

    console.log('[DS Extractor v4.0] 已启动');

    let capturedToken = null;
    let captureStarted = false;

    // ========== 策略1: 拦截页面请求捕获 Token ==========
    function startInterception() {
        if (captureStarted) return;
        captureStarted = true;

        const originalFetch = window.fetch;
        window.fetch = function (...args) {
            try {
                const url = typeof args[0] === 'string' ? args[0] : args[0]?.url;
                const init = args[1] || {};
                const headers = init.headers || {};

                const authHeader = headers['Authorization'] || headers['authorization'];
                if (authHeader && authHeader.startsWith('Bearer ')) {
                    const token = authHeader.substring(7);
                    if (isValidTokenFormat(token)) {
                        console.log('[DS Extractor] 通过 fetch 拦截捕获到 Token');
                        capturedToken = token;
                    }
                }

                if (url && url.includes('/api/v0/users/current')) {
                    return originalFetch.apply(this, args).then(response => {
                        const clone = response.clone();
                        clone.json().then(data => {
                            if (data?.data?.biz_data?.token) {
                                console.log('[DS Extractor] 从 fetch 响应中提取到 Token');
                                capturedToken = data.data.biz_data.token;
                            }
                        }).catch(() => {});
                        return response;
                    }).catch(err => { throw err; });
                }
            } catch (e) {
                console.error('[DS Extractor] fetch 拦截错误:', e);
            }
            return originalFetch.apply(this, args);
        };

        const originalXHROpen = XMLHttpRequest.prototype.open;
        const originalXHRSend = XMLHttpRequest.prototype.send;

        XMLHttpRequest.prototype.open = function (method, url, ...rest) {
            this._ds_url = url;
            this._ds_method = method;
            return originalXHROpen.apply(this, [method, url, ...rest]);
        };

        XMLHttpRequest.prototype.send = function (body) {
            if (body && typeof body === 'string') {
                try {
                    const data = JSON.parse(body);
                    if (data?.token && isValidTokenFormat(data.token)) {
                        capturedToken = data.token;
                    }
                } catch (e) {}
            }
            return originalXHRSend.apply(this, [body]);
        };

        const originalSetRequestHeader = XMLHttpRequest.prototype.setRequestHeader;
        XMLHttpRequest.prototype.setRequestHeader = function (name, value) {
            if (name.toLowerCase() === 'authorization' && value.startsWith('Bearer ')) {
                const token = value.substring(7);
                if (isValidTokenFormat(token)) {
                    console.log('[DS Extractor] 通过 XHR 拦截捕获到 Token');
                    capturedToken = token;
                }
            }
            return originalSetRequestHeader.call(this, name, value);
        };

        console.log('[DS Extractor] 请求拦截已启动');
    }

    function isValidTokenFormat(token) {
        if (!token || token.length < 20) return false;
        return /^[A-Za-z0-9+/=_\-]+$/.test(token);
    }

    // ========== 策略2: 扫描存储 ==========
    function scanStorage() {
        const found = [];

        for (let i = 0; i < localStorage.length; i++) {
            const key = localStorage.key(i);
            const value = localStorage.getItem(key);
            if (isValidTokenFormat(value)) {
                found.push({ source: `localStorage["${key}"]`, value });
            }
            try {
                if (value && value.startsWith('{')) {
                    const obj = JSON.parse(value);
                    findTokensInObject(obj, `localStorage["${key}"]`, found);
                }
            } catch (e) {}
        }

        for (let i = 0; i < sessionStorage.length; i++) {
            const key = sessionStorage.key(i);
            const value = sessionStorage.getItem(key);
            if (isValidTokenFormat(value)) {
                found.push({ source: `sessionStorage["${key}"]`, value });
            }
            try {
                if (value && value.startsWith('{')) {
                    const obj = JSON.parse(value);
                    findTokensInObject(obj, `sessionStorage["${key}"]`, found);
                }
            } catch (e) {}
        }

        return found;
    }

    function findTokensInObject(obj, path, found) {
        if (obj && typeof obj === 'object') {
            for (const [key, value] of Object.entries(obj)) {
                const newPath = `${path}.${key}`;
                if (key.toLowerCase().includes('token') && typeof value === 'string' && isValidTokenFormat(value)) {
                    found.push({ source: newPath, value });
                }
                if (typeof value === 'object' && value !== null) {
                    findTokensInObject(value, newPath, found);
                }
            }
        }
    }

    // ========== Cookie 提取 ==========
    function extractCookie() {
        try {
            const cookie = document.cookie;
            if (cookie && cookie.trim().length > 0) {
                return cookie.trim();
            }
        } catch (e) {
            console.error('[DS Extractor] Cookie 提取失败:', e);
        }
        return null;
    }

    // ========== 主提取逻辑 ==========
    async function extractAll() {
        // --- Token ---
        let token = capturedToken;
        let tokenSource = '拦截请求';

        if (token && isValidTokenFormat(token)) {
            console.log('[DS Extractor] 使用已捕获的 Token');
        }

        if (!token) {
            console.log('[DS Extractor] 扫描 localStorage/sessionStorage...');
            const storageResults = scanStorage();
            if (storageResults.length > 0) {
                storageResults.sort((a, b) => b.value.length - a.value.length);
                token = storageResults[0].value;
                tokenSource = storageResults[0].source;
                console.log('[DS Extractor] 从存储找到 Token:', tokenSource);
            }
        }

        if (!token) {
            console.log('[DS Extractor] 等待页面请求拦截...');
            for (let i = 0; i < 10; i++) {
                await sleep(500);
                token = capturedToken;
                if (token && isValidTokenFormat(token)) {
                    tokenSource = '拦截请求';
                    break;
                }
            }
        }

        if (!token) {
            throw new Error('未能找到 Token。请确保已登录 chat.deepseek.com，然后刷新页面重试。');
        }

        // --- Cookie ---
        const cookie = extractCookie();

        // --- 验证 Token ---
        const valid = await verifyToken(token);

        return { token, tokenSource, cookie, valid };
    }

    function sleep(ms) {
        return new Promise(r => setTimeout(r, ms));
    }

    function verifyToken(token) {
        return new Promise((resolve) => {
            GM_xmlhttpRequest({
                method: 'GET',
                url: 'https://chat.deepseek.com/api/v0/users/current',
                headers: {
                    'Authorization': 'Bearer ' + token,
                    'Accept': 'application/json',
                    'User-Agent': navigator.userAgent
                },
                onload: (res) => {
                    if (res.status === 200) {
                        try {
                            const data = JSON.parse(res.responseText);
                            if (data.code === 0) {
                                resolve({
                                    valid: true,
                                    userId: data.data?.biz_data?.id || '?',
                                    userPlan: data.data?.biz_data?.plan || '?'
                                });
                                return;
                            }
                        } catch (e) {}
                    }
                    resolve({ valid: false, userId: null });
                },
                onerror: () => resolve({ valid: false, userId: null }),
                timeout: 8000
            });
        });
    }

    // ========== UI: 悬浮按钮 ==========
    function createFloatingButton() {
        if (document.getElementById('ds-auth-extractor-btn')) return;

        const btn = document.createElement('div');
        btn.id = 'ds-auth-extractor-btn';
        btn.innerHTML = '🔑 <span style="font-size:10px;">提取凭证</span>';

        Object.assign(btn.style, {
            position: 'fixed',
            top: '20px',
            right: '20px',
            zIndex: '2147483647',
            padding: '6px 12px',
            background: 'linear-gradient(135deg, #667eea 0%, #764ba2 100%)',
            color: 'white',
            borderRadius: '20px',
            cursor: 'pointer',
            fontSize: '12px',
            fontWeight: '600',
            fontFamily: 'system-ui, -apple-system, sans-serif',
            boxShadow: '0 4px 14px rgba(102,126,234,0.5)',
            transition: 'all 0.25s ease',
            userSelect: 'none',
            display: 'flex',
            alignItems: 'center',
            gap: '4px',
            opacity: '0.5',
            transform: 'scale(0.9)'
        });

        btn.onmouseenter = () => {
            btn.style.opacity = '1';
            btn.style.transform = 'scale(1.05)';
        };
        btn.onmouseleave = () => {
            if (!btn.dataset.interacted) {
                btn.style.opacity = '0.5';
                btn.style.transform = 'scale(0.9)';
            }
        };
        btn.onclick = () => {
            btn.dataset.interacted = 'true';
            btn.style.opacity = '1';
            extractAndShow();
        };

        document.body?.appendChild(btn) || document.documentElement.appendChild(btn);
        console.log('[DS Extractor] 按钮已创建');
    }

    // ========== UI: 提取并显示 ==========
    async function extractAndShow() {
        const btn = document.getElementById('ds-auth-extractor-btn');
        if (btn) {
            btn.innerHTML = '⏳ 提取中...';
            btn.style.opacity = '1';
        }

        try {
            const { token, tokenSource, cookie, valid } = await extractAll();
            showResult(token, tokenSource, cookie, valid);
        } catch (err) {
            console.error('[DS Extractor] 失败:', err);
            showError(err.message);
        } finally {
            if (btn) {
                setTimeout(() => {
                    btn.innerHTML = '🔑 <span style="font-size:10px;">提取凭证</span>';
                    btn.style.opacity = '0.5';
                    btn.dataset.interacted = '';
                }, 500);
            }
        }
    }

    // ========== UI: 结果面板 ==========
    function showResult(token, tokenSource, cookie, verification) {
        closePanel();
        const btn = document.getElementById('ds-auth-extractor-btn');
        if (btn) btn.style.display = 'none';

        const panel = document.createElement('div');
        panel.id = 'ds-auth-panel';

        const valid = verification?.valid;
        const statusColor = valid ? '#a6e3a1' : '#f38ba8';
        const statusText = valid
            ? `✅ Token 有效 (用户: ${verification.userId?.substring(0,10)}..., 套餐: ${verification.userPlan})`
            : '⚠️ Token 格式正确但验证失败（可能已过期）';

        const cookieStatus = cookie
            ? `✅ Cookie 已获取 (${cookie.length} 字符)`
            : '⚠️ 未获取到 Cookie（请确认已登录）';

        panel.innerHTML = `
            <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:10px;">
                <div style="font-size:14px; font-weight:bold; color:#cba6f7; display:flex; align-items:center; gap:8px;">
                    🔑 DeepSeek 凭证 (Token + Cookie)
                </div>
                <span id="ds-close" style="cursor:pointer; color:#6c7086; font-size:18px; padding:2px 6px; border-radius:4px;"
                    onmouseover="this.style.background='#45475a'" onmouseout="this.style.background='transparent'">✕</span>
            </div>

            <div style="margin-bottom:10px; padding:8px 10px; background:#313244; border-radius:6px; border:1px solid ${statusColor}; font-size:11px;">
                <div style="color:${statusColor}; font-weight:500;">${statusText}</div>
                <div style="color:#6c7086; margin-top:3px; font-size:10px;">Token 来源: ${tokenSource}</div>
            </div>

            <div style="margin-bottom:12px;">
                <div style="color:#a6adc8; margin-bottom:5px; font-size:11px; display:flex; justify-content:space-between;">
                    <span>Token (长度: ${token.length}):</span>
                </div>
                <textarea id="ds-token" readonly spellcheck="false"
                    style="width:100%; height:55px; background:#181825; color:#f9e2af; border:1px solid #45475a; border-radius:6px; padding:8px; font-family:'Consolas','Menlo',monospace; font-size:10.5px; resize:none; outline:none; box-sizing:border-box;">${token}</textarea>
                <button id="ds-copy-token" style="width:100%; margin-top:6px; padding:6px; background:#89b4fa; color:#1e1e2e; border:none; border-radius:6px; cursor:pointer; font-weight:bold; font-size:11px; transition:all 0.2s;">
                    📋 复制 Token
                </button>
            </div>

            <div style="margin-bottom:12px;">
                <div style="color:#a6adc8; margin-bottom:5px; font-size:11px;">
                    Cookie: <span style="color:${cookie ? '#a6e3a1' : '#f38ba8'};">${cookie ? cookie.length + ' 字符' : '未获取'}</span>
                </div>
                <textarea id="ds-cookie" readonly spellcheck="false"
                    style="width:100%; height:55px; background:#181825; color:#a6e3a1; border:1px solid #45475a; border-radius:6px; padding:8px; font-family:'Consolas','Menlo',monospace; font-size:10px; resize:none; outline:none; box-sizing:border-box;">${cookie || ''}</textarea>
                <button id="ds-copy-cookie" style="width:100%; margin-top:6px; padding:6px; background:#a6e3a1; color:#1e1e2e; border:none; border-radius:6px; cursor:pointer; font-weight:bold; font-size:11px; transition:all 0.2s;">
                    📋 复制 Cookie
                </button>
            </div>

            <button id="ds-copy-all" style="width:100%; padding:8px; background:#cba6f7; color:#1e1e2e; border:none; border-radius:6px; cursor:pointer; font-weight:bold; font-size:12px; transition:all 0.2s; margin-bottom:8px;">
              🚀 一键复制全部 (Token + Cookie)
            </button>

            <div style="padding:8px; background:#1e1e2e; border-radius:6px; font-size:10px; color:#6c7086; line-height:1.5;">
                <b style="color:#cba6f7;">💡 使用说明:</b><br>
                1. 打开 WoWSBattleAssistant → 设置 → 选择 DeepSeek<br>
                2. 点「复制 Token」粘贴到 Token 框<br>
                3. 点「复制 Cookie」粘贴到 Cookie 框<br>
                4. 保存即可。填了 Cookie 后 Token 过期会自动刷新<br>
                <span style="color:#a6e3a1;">安全提示:</span> 本脚本仅在本地读取凭证，不上传任何数据
            </div>
        `;

        Object.assign(panel.style, {
            position: 'fixed',
            top: '20px',
            right: '20px',
            zIndex: '2147483647',
            width: '400px',
            background: '#1e1e2e',
            borderRadius: '12px',
            padding: '14px',
            boxShadow: '0 8px 32px rgba(0,0,0,0.5), 0 0 0 1px #45475a',
            fontFamily: 'system-ui, -apple-system, sans-serif',
            fontSize: '13px',
            color: '#cdd6f4',
            maxHeight: '90vh',
            overflowY: 'auto'
        });

        injectStyles();
        document.body?.appendChild(panel) || document.documentElement.appendChild(panel);

        // 绑定事件
        document.getElementById('ds-close').onclick = () => {
            panel.remove();
            btn.style.display = 'flex';
        };

        document.getElementById('ds-copy-token').onclick = (e) => {
            copyText(token);
            flashButton(e.target, '✅ Token 已复制!');
        };

        document.getElementById('ds-copy-cookie').onclick = (e) => {
            if (cookie) {
                copyText(cookie);
                flashButton(e.target, '✅ Cookie 已复制!');
            } else {
                flashButton(e.target, '❌ 无 Cookie 可复制', '#f38ba8');
            }
        };

        document.getElementById('ds-copy-all').onclick = (e) => {
            // 一键复制：先 Token 后 Cookie，用户在设置面板分别粘贴
            // 这里复制一个组合文本，方便用户查看
            const combined = `===== Token =====\n${token}\n\n===== Cookie =====\n${cookie || '(未获取)'}`;
            copyText(combined);
            flashButton(e.target, '✅ 已复制全部!');
        };

        // 点击外部关闭
        setTimeout(() => {
            const handler = (e) => {
                if (!panel.contains(e.target) && !btn.contains(e.target)) {
                    panel.remove();
                    document.removeEventListener('click', handler, true);
                    btn.style.display = 'flex';
                }
            };
            document.addEventListener('click', handler, true);
        }, 100);
    }

    function flashButton(btn, text, color) {
        const orig = btn.innerHTML;
        const origBg = btn.style.background;
        btn.innerHTML = text;
        btn.style.background = color || '#a6e3a1';
        setTimeout(() => {
            btn.innerHTML = orig;
            btn.style.background = origBg;
        }, 1500);
    }

    function showError(msg) {
        closePanel();
        const panel = document.createElement('div');
        panel.id = 'ds-auth-panel';
        panel.innerHTML = `
            <div style="display:flex; align-items:center; gap:8px; margin-bottom:8px;">
                <span style="font-size:18px;">❌</span>
                <span style="color:#f38ba8; font-weight:bold; font-size:13px;">提取失败</span>
            </div>
            <div style="padding:8px; background:#313244; border-radius:6px; border:1px solid #f38ba8; margin-bottom:10px; font-size:11px; color:#f38ba8; word-break:break-all;">${msg}</div>
            <div style="font-size:10px; color:#a6adc8; line-height:1.6; margin-bottom:10px;">
                <b style="color:#cba6f7;">解决方法:</b><br>
                1. 确认已登录 chat.deepseek.com<br>
                2. 刷新页面 (F5) 后重试<br>
                3. 如仍不行,在页面上随便发一条消息触发请求后再点提取
            </div>
            <button id="ds-close" style="width:100%; padding:6px; background:#45475a; color:#cdd6f4; border:none; border-radius:6px; cursor:pointer; font-size:12px;">关闭</button>
        `;
        Object.assign(panel.style, {
            position: 'fixed',
            top: '20px',
            right: '20px',
            zIndex: '2147483647',
            width: '340px',
            background: '#1e1e2e',
            borderRadius: '12px',
            padding: '14px',
            boxShadow: '0 8px 32px rgba(0,0,0,0.5)',
            fontFamily: 'system-ui, sans-serif'
        });
        document.body?.appendChild(panel) || document.documentElement.appendChild(panel);
        document.getElementById('ds-close').onclick = () => panel.remove();
    }

    function closePanel() {
        document.getElementById('ds-auth-panel')?.remove();
    }

    function injectStyles() {
        if (document.getElementById('ds-styles')) return;
        const s = document.createElement('style');
        s.id = 'ds-styles';
        s.textContent = `
            @keyframes dsFadeIn { from { opacity:0; transform:translateY(-8px);} to { opacity:1; transform:translateY(0);} }
            #ds-token:focus, #ds-cookie:focus { border-color:#89b4fa !important; }
        `;
        document.head?.appendChild(s);
    }

    function copyText(text) {
        if (navigator.clipboard?.writeText) {
            navigator.clipboard.writeText(text).catch(fallback);
        } else {
            fallback();
        }
        function fallback() {
            const ta = document.createElement('textarea');
            ta.value = text;
            ta.style.cssText = 'position:fixed;left:-9999px;';
            document.body.appendChild(ta);
            ta.select();
            try { document.execCommand('copy'); } catch (e) {}
            ta.remove();
        }
    }

    // ========== 初始化 ==========
    GM_registerMenuCommand('🔑 提取 DeepSeek 凭证 (Token+Cookie)', () => extractAndShow());

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', () => {
            startInterception();
            setTimeout(createFloatingButton, 2000);
        });
    } else {
        startInterception();
        createFloatingButton();
    }

    // 页面加载完成后扫描一次存储
    window.addEventListener('load', () => {
        setTimeout(() => {
            if (!capturedToken) {
                const results = scanStorage();
                if (results.length > 0) {
                    capturedToken = results[0].value;
                    console.log('[DS Extractor] load 事件捕获到 Token');
                }
            }
        }, 3000);
    });

})();
