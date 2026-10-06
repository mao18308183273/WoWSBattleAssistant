// hook_wows_aim.js —— Frida 注入 Start.exe，抓运行时解密后的关键数据。
//
// 为什么用 Frida 而不是 x64dbg：
//   x64dbg 调试时 Qt 的 windows.storage.dll 延迟加载反复重试，
//   死循环在 QPlatformIntegrationFactory::create，GUI 永远建不出来
//   （实测跑 60 秒，内存从 9MB 涨到 52MB 仍无窗口）。
//   Frida 不劫持调试流程，只 hook 目标函数 → 程序正常跑，数据照样能拿。
//
// 抓什么：
//   1. WinHttpConnect / WinHttpOpenRequest  → 云端域名 + 请求路径
//   2. WSAStartup / connect                → 直接拿到 sockaddr 里的 IP
//   3. CreateFileW / ReadFile               → 它读哪些文件
//   4. QtNetwork 层的 QHostAddress 相关     → 域名（备选）
//   5. card/license/code 相关字符串         → 卡密校验逻辑

'use strict';

const out = [];
function log(s) {
  const line = String(s);
  out.push(line);
  console.log(line);
  send({ t: 'log', m: line });
}

function readWStr(ptr, max) {
  if (!ptr || ptr.isNull()) return null;
  try {
    return ptr.readUtf16String(max || 260);
  } catch (e) {
    return null;
  }
}

function readAnsi(ptr, max) {
  if (!ptr || ptr.isNull()) return null;
  try {
    return ptr.readAnsiString(max || 260);
  } catch (e) {
    return null;
  }
}

// ---------------------------------------------------------- 网络
function hookWinHttp() {
  const m = Process.findModuleByName('winhttp.dll');
  if (!m) { log('[!] winhttp.dll 未加载'); return; }
  log('[+] winhttp.dll @ ' + m.base);

  const c = m.getExportByName('WinHttpConnect');
  if (c) {
    Interceptor.attach(c, {
      onEnter(args) {
        // WinHttpConnect(hSession, pswzServerName, nServerPort, dwReserved)
        const host = readWStr(args[1]);
        const port = args[2].toUInt32();
        log('[★] WinHttpConnect  域名=' + host + '  端口=' + port);
        this._h = host;
      },
      onLeave(retval) {
        log('    → 返回 ' + retval);
      }
    });
    log('[+] hook WinHttpConnect');
  } else log('[!] WinHttpConnect 未导出');

  const o = m.getExportByName('WinHttpOpenRequest');
  if (o) {
    Interceptor.attach(o, {
      onEnter(args) {
        // (hConnect, pwszVerb, pwszObjectName, pwszVersion, pwszReferrer,...)
        log('[★] WinHttpOpenRequest  方法=' + readWStr(args[1]) +
            '  路径=' + readWStr(args[2]) + '  版本=' + readWStr(args[3]));
      }
    });
    log('[+] hook WinHttpOpenRequest');
  }
}

function hookWinsock() {
  // ws2_32!connect(SOCKET s, const sockaddr* name, int namelen)
  const m = Process.findModuleByName('ws2_32.dll');
  if (!m) { log('[!] ws2_32.dll 未加载'); return; }
  const c = m.getExportByName('connect');
  if (!c) return;
  Interceptor.attach(c, {
    onEnter(args) {
      if (args[0].toInt32() === 0xFFFFFFFF) return;  // 忽略无效 socket
      const sa = args[1];
      const fam = sa.readU16();
      if (fam === 2) {   // AF_INET
        const port = sa.add(2).readU16();
        const ip = [sa.add(4).readU8(), sa.add(5).readU8(),
                    sa.add(6).readU8(), sa.add(7).readU8()].join('.');
        const hp = (port >> 8) & 0xFF;
        const lp = port & 0xFF;
        log('[★] connect() → ' + ip + ':' + (hp * 256 + lp));
      }
    }
  });
  log('[+] hook connect (ws2_32)');
}

// ---------------------------------------------------------- 文件
function hookFiles() {
  const k = Process.findModuleByName('kernel32.dll');
  if (!k) return;
  const cf = k.getExportByName('CreateFileW');
  if (cf) {
    Interceptor.attach(cf, {
      onEnter(args) {
        const p = readWStr(args[0]);
        if (!p) return;
        // 只打印有意思的路径
        if (/\.(ini|cfg|json|txt|dat|log|lic|key)$/i.test(p) ||
            /config|license|token|card|auth/i.test(p)) {
          log('[文件] 打开 ' + p);
        }
      }
    });
    log('[+] hook CreateFileW（只打印配置文件类）');
  }
  const gw = k.getExportByName('GetWindowsDirectoryW');
  if (gw) {
    Interceptor.attach(gw, {
      onEnter(args) { this.b = args[0]; },
      onLeave(ret) {
        try { this.b.readUtf16String(260); } catch (e) {}
      }
    });
  }
}

// ---------------------------------------------------------- 加密/授权
function hookCrypto() {
  // OpenSSL EVP_EncryptInit / BIO — 看它是否在做自研加密
  const m = Process.findModuleByName('libcrypto-3-x64.dll');
  if (!m) { log('[!] libcrypto-3-x64.dll 未加载'); return; }
  log('[+] libcrypto-3-x64.dll @ ' + m.base + '（OpenSSL，确认在用）');
  const enc = m.getExportByName('BIO_new_connect');
  if (enc) {
    Interceptor.attach(enc, {
      onEnter(args) {
        const host = readAnsi(args[0]);
        log('[★] BIO_new_connect → ' + host);
      }
    });
    log('[+] hook BIO_new_connect（OpenSSL 连接）');
  }
}


// ---------------------------------------------------------- DNS（关键补充）
// 实测它可能先 getaddrinfo 解析域名，再 connect 到 IP ——
// 只 hook connect 只能拿到 IP，拿不到域名。这里补上。
function hookDns() {
  const hooked = [];
  // ws2_32!getaddrinfo  —— 拿到被查询的域名
  const w = Process.findModuleByName('ws2_32.dll');
  if (w) {
    const g = w.getExportByName('getaddrinfo');
    if (g) {
      Interceptor.attach(g, { onEnter(args) {
        const n = readAnsi(args[0]);
        if (n) log('[★ DNS] getaddrinfo 查询: ' + n);
      }});
      hooked.push('ws2_32!getaddrinfo');
    }
    const gh = w.getExportByName('gethostbyname');
    if (gh) {
      Interceptor.attach(gh, { onEnter(args) {
        const n = readAnsi(args[0]);
        if (n) log('[★ DNS] gethostbyname: ' + n);
      }});
      hooked.push('ws2_32!gethostbyname');
    }
  }
  // dnsapi!DnsQuery_A —— 系统解析路径
  const d = Process.findModuleByName('dnsapi.dll');
  if (d) {
    const dq = d.getExportByName('DnsQuery_A');
    if (dq) {
      Interceptor.attach(dq, { onEnter(args) {
        const n = readAnsi(args[0]);
        if (n && n.indexOf('.') > 0) log('[★ DNS] DnsQuery_A: ' + n);
      }});
      hooked.push('dnsapi!DnsQuery_A');
    }
  }
  // Qt 自己的解析：QHostInfo 用 QtNetwork 的内部实现，抓不到就算了
  if (hooked.length) log('[+] hook DNS: ' + hooked.join(', '));
  else log('[!] 没找到可 hook 的 DNS 入口');
}

// ---------------------------------------------------------- 读内存里的字符串
// 兜底：直接扫进程内存找已解密的域名/URL（比 hook 更全面）
function dumpStrings(tag) {
  const ranges = Process.enumerateRanges({ protection: 'r--', coalesce: true })
    .concat(Process.enumerateRanges({ protection: 'rw-', coalesce: true }))
    .concat(Process.enumerateRanges({ protection: 'r-x', coalesce: true }));
  const found = {};
  const pat = /(?:https?:\/\/[ -~]{6,200})|(?:[\w\-]{2,40}\.(?:com|net|cn|org|io|cc|top|xyz|ru|tk))/g;
  let total = 0;
  for (const r of ranges) {
    if (r.size < 0x1000) continue;
    try {
      const buf = Memory.readByteArray(r.base, Math.min(r.size, 0x800000));
      if (!buf) continue;
      const s = Buffer.from(buf).toString('latin1');
      let m;
      const re = new RegExp(pat.source, 'g');
      while ((m = re.exec(s)) !== null) {
        const v = m[0];
        if (v.length > 5 && !found[v]) found[v] = r.base.toString(16);
        total++;
        if (m.index > 0x7ff000) break;
      }
    } catch (e) {}
  }
  const keys = Object.keys(found);
  if (keys.length) {
    log('[★ 内存扫描 ' + tag + '] 找到 ' + keys.length + ' 个疑似域名/URL');
    keys.slice(0, 40).forEach(k => log('    ' + k + '  @' + found[k]));
  } else {
    log('[内存扫描 ' + tag + '] 没找到明文域名/URL');
  }
}

// ---------------------------------------------------------- 导出
rpc.exports = {
  // 供 Python 侧随时读取已捕获的日志
  getlog() { return out.join('\n'); },
};

setTimeout(() => {
  log('=== hook 安装中 ===');
  hookWinHttp();
  hookWinsock();
  hookFiles();
  hookCrypto();
  hookDns();
  log('=== hook 完成，等待程序活动 ===');
  // 启动 3 秒 / 8 秒各扫一次内存
  setTimeout(() => { try { dumpStrings('t+3s'); } catch (e) { log('扫描失败: ' + e); } }, 3000);
  setTimeout(() => { try { dumpStrings('t+8s'); } catch (e) { log('扫描失败: ' + e); } }, 8000);
  setTimeout(() => { try { dumpStrings('t+20s'); } catch (e) { log('扫描失败: ' + e); } }, 20000);
  setTimeout(() => { try { dumpStrings('t+45s'); } catch (e) { log('扫描失败: ' + e); } }, 45000);
}, 200);
