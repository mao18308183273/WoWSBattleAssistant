#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
frida_watch.py —— 用 Frida 注入 Start.exe，实时抓它的运行时行为。

【为什么不用 x64dbg】
实测在 x64dbg 里跑 Start.exe 会陷入死循环：Qt 的 windows.storage.dll
延迟加载反复重试，卡在 QPlatformIntegrationFactory::create，
60 秒后仍无窗口、内存停在 52MB。这是 x64dbg 对 delay-load 的处理问题。

Frida 不劫持调试流程 —— 程序正常跑，hook 只在旁边看数据。

用法：
  python frida_watch.py                 # 启动 + hook + 监控 120 秒
  python frida_watch.py --attach <pid>  # 附加到已在跑的实例
  python frida_watch.py --seconds 300
"""
import argparse
import os
import subprocess
import sys
import threading
import time

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

import frida

HERE = os.path.dirname(os.path.abspath(__file__))
HOOK_JS = os.path.join(HERE, 'hook_wows_aim.js')
TARGET = r'F:\预瞄工具\Start.exe'
CWD = r'F:\预瞄工具'

_lines = []
_lock = threading.Lock()


def on_message(msg, data):
    if msg.get('type') == 'send':
        m = msg['payload'].get('m', '')
    elif msg.get('type') == 'error':
        m = '[Frida 错误] ' + str(msg.get('stack', ''))[:200]
    else:
        m = str(msg)
    with _lock:
        _lines.append(m)
    print(m, flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--attach', type=int, help='附加到已有 PID')
    ap.add_argument('--seconds', type=int, default=120)
    ap.add_argument('--no-launch', action='store_true')
    a = ap.parse_args()

    if a.attach:
        pid = a.attach
        print('[*] 附加到 PID %d' % pid)
    else:
        # 先杀掉旧实例
        subprocess.run(['taskkill', '/F', '/IM', 'Start.exe'],
                       capture_output=True)
        time.sleep(1)
        print('[*] 启动 %s' % TARGET)
        proc = subprocess.Popen([TARGET], cwd=CWD)
        pid = proc.pid
        print('[*] PID = %d' % pid)
        # 等它起来
        time.sleep(1.5)

    try:
        session = frida.attach(pid)
    except Exception as e:
        print('[×] 附加失败：%s' % e)
        print('    试试用管理员身份运行本脚本')
        return 2

    with open(HOOK_JS, 'r', encoding='utf-8') as f:
        js = f.read()
    script = session.create_script(js)
    script.on('message', on_message)
    script.load()
    print('[*] hook 已注入，监控 %d 秒\n' % a.seconds)

    t_end = time.time() + a.seconds
    # 同时监控窗口，方便你知道何时能输卡密
    import ctypes
    import ctypes.wintypes as wt
    import subprocess as sp
    u = ctypes.windll.user32
    seen_win = set()
    try:
        while time.time() < t_end:
            res = []
            CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

            def cb(h, l):
                p = wt.DWORD()
                u.GetWindowThreadProcessId(h, ctypes.byref(p))
                if p.value == pid:
                    n = u.GetWindowTextLengthW(h)
                    b = ctypes.create_unicode_buffer(n + 1)
                    u.GetWindowTextW(h, b, n + 1)
                    if b.value.strip():
                        res.append(b.value)
                return True
            u.EnumWindows(CB(cb), 0)
            for t in res:
                if t not in seen_win:
                    seen_win.add(t)
                    print('[窗口] %r  ← 可以操作了' % t, flush=True)
            time.sleep(1.0)
    except KeyboardInterrupt:
        print('\n[!] 中断')

    print('\n=== 汇总（%d 条）===' % len(_lines))
    with _lock:
        for l in _lines:
            print(l)

    try:
        session.detach()
    except Exception:
        pass
    return 0


if __name__ == '__main__':
    sys.exit(main())
