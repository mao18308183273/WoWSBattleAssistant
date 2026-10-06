#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
launch_watch.py —— 不经调试器启动 Start.exe，并在旁边监控它的一举一动。

【为什么要这个】
在 x64dbg 里跑 Qt 程序会被调试器严重拖慢：它每个文件访问、每次异常都要
单步，导致 Qt 平台插件探测阶段迟迟走不完 —— 实测跑 20+ 秒仍然
**一个窗口都没创建**，自然也到不了输卡密那一步。

所以拆成两条线：
  · 启动 = 正常双击那样直接跑（不拖慢，GUI 秒出）
  · 监控 = 本脚本在旁边盯着它读了哪些文件、连了哪些 IP、开了哪些窗口

【能拿到什么】
  1. 它打开的所有文件路径（含 config.ini、卡密存储位置）
  2. 它的对外 TCP 连接目标 IP:端口 —— 激活时就能抓到云端地址
  3. 窗口标题（界面文案、错误提示）
  4. 新建/修改的文件（它往哪写配置）

用法：
  python launch_watch.py                 # 启动 + 监控 60 秒
  python launch_watch.py --seconds 300
  python launch_watch.py --no-launch    # 只监控已在跑的实例
"""
import argparse
import ctypes
import ctypes.wintypes as wt
import os
import re
import subprocess
import sys
import time

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

TARGET = r'F:\预瞄工具\Start.exe'
CWD = r'F:\预瞄工具'
u = ctypes.windll.user32
shell = ctypes.windll.shell32

# 监控前 N 秒内出现的新文件（它写了什么）
_watch_dir = CWD
_seen_files = {}
_eventlog = []


def snapshot_files():
    out = {}
    for root, dirs, files in os.walk(_watch_dir):
        for f in files:
            p = os.path.join(root, f)
            try:
                st = os.stat(p)
                out[p] = (st.st_mtime, st.st_size)
            except OSError:
                pass
    return out


def pid_net_table():
    """返回 {pid: [(laddr, raddr, state)]}"""
    raw = subprocess.run(['netstat', '-ano'], capture_output=True).stdout
    txt = raw.decode('gbk', 'replace')
    res = {}
    for line in txt.splitlines()[2:]:
        parts = line.split()
        if len(parts) < 4:
            continue
        proto, la, ra, st = parts[0], parts[1], parts[2], parts[3]
        pid = parts[-1]
        if not pid.isdigit() or proto.upper() != 'TCP':
            continue
        res.setdefault(int(pid), []).append((la, ra, st))
    return res


def windows_of(pid):
    res = []
    CB = ctypes.WINFUNCTYPE(ctypes.c_bool, wt.HWND, wt.LPARAM)

    def cb(h, l):
        p = wt.DWORD()
        u.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid:
            n = u.GetWindowTextLengthW(h)
            b = ctypes.create_unicode_buffer(n + 1)
            u.GetWindowTextW(h, b, n + 1)
            cls = ctypes.create_unicode_buffer(256)
            u.GetClassNameW(h, cls, 256)
            res.append((h, b.value, cls.value, bool(u.IsWindowVisible(h))))
        return True
    u.EnumWindows(CB(cb), 0)
    return res


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--seconds', type=int, default=60)
    ap.add_argument('--no-launch', action='store_true')
    a = ap.parse_args()

    before = snapshot_files()
    proc = None
    if not a.no_launch:
        print('[*] 直接启动（不经调试器）：%s' % TARGET)
        proc = subprocess.Popen([TARGET], cwd=CWD)
        print('[*] PID = %d' % proc.pid)
    else:
        # 找已在跑的
        out = subprocess.run(['tasklist', '/FI', 'IMAGENAME eq Start.exe', '/FO', 'CSV'],
                             capture_output=True).stdout.decode('gbk', 'replace')
        for line in out.splitlines()[1:]:
            p = line.split(',')[1].strip('"')
            if p.isdigit():
                proc = type('X', (), {'pid': int(p)})()
                print('[*] 监控已有实例 PID = %s' % p)
                break
        if proc is None:
            print('[×] 没找到运行中的 Start.exe')
            return 2

    pid = proc.pid
    seen_conn = set()
    seen_win = set()
    t_end = time.time() + a.seconds
    print('[*] 监控 %d 秒，按 Ctrl+C 结束\n' % a.seconds)

    try:
        while time.time() < t_end:
            # 1) 网络
            for la, ra, st in pid_net_table().get(pid, []):
                if '127.0.0.1' in ra or '0.0.0.0:0' in ra:
                    continue
                key = (la, ra)
                if key in seen_conn:
                    continue
                seen_conn.add(key)
                ip = ra.rsplit(':', 1)[0]
                port = ra.rsplit(':', 1)[1]
                print('[网络] %s → %s:%s  [%s]' % (la, ip, port, st))
                # 反查域名
                try:
                    import socket
                    name = socket.gethostbyaddr(ip)[0]
                    print('       → 域名 %s' % name)
                except Exception:
                    print('       → （域名解析失败）')
            # 2) 窗口
            for h, t, c, vis in windows_of(pid):
                k = (h, t)
                if k in seen_win:
                    continue
                seen_win.add(k)
                print('[窗口] hwnd=%s 可见=%s 类=%s 标题=%r' % (h, vis, c[:30], t[:80]))
            # 3) 新写文件
            now = snapshot_files()
            for p, (mt, sz) in now.items():
                if p in before:
                    continue
                print('[新文件] %s  (%d 字节)' % (p, sz))
            before = now
            time.sleep(0.7)
    except KeyboardInterrupt:
        print('\n[!] 用户中断')

    print('\n=== 汇总 ===')
    print('窗口标题：')
    for h, t, c, vis in windows_of(pid):
        print('  %r' % t)
    print('对外连接：%d 个' % len(seen_conn))
    for la, ra in sorted(seen_conn):
        print('  %s → %s' % (la, ra))
    if proc and hasattr(proc, 'poll') and proc.poll() is None:
        print('\n[*] 程序仍在运行 (PID %d)，可以继续操作它的界面' % pid)
    return 0


if __name__ == '__main__':
    sys.exit(main())