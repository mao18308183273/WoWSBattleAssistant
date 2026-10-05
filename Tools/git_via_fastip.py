#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
git_via_fastip.py —— 绕开 github.com 域名被阻断的问题，把 git 命令跑通。

【问题】
国内网络经常出现：DNS 把 github.com 解析到某个被阻断的 IP（实测 20.205.243.166
连 443 超时 12 秒），而 GitHub 其它 IP 完全正常。api.github.com 恰好解析到隔壁的
IP 就没事，于是表现为"有些 GitHub 能用、git push 不行"。

【思路】
不改 hosts、不要管理员权限：自己挑一个能连通的 GitHub IP，在本地起一个迷你
CONNECT 代理，把 git 的 HTTPS 流量引到那个 IP 上。
因为是隧道转发，**TLS 仍是端到端**——SNI 还是 github.com、证书照常校验，
所以既解决问题又不降低安全性。

【用法】
  python git_via_fastip.py push origin main
  python git_via_fastip.py fetch
  python git_via_fastip.py -C 其它仓库路径 push
  python git_via_fastip.py --test         只看哪个 IP 通、延迟多少
"""
import argparse
import os
import socket
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

# 已知的 GitHub 入口 IP（会变，所以运行时实测挑选，不写死）
CANDIDATE_IPS = [
    '20.27.177.113', '140.82.112.3', '140.82.113.3', '140.82.114.3',
    '4.237.22.38', '20.205.243.166', '20.200.245.247', '20.233.83.145',
]
HOST = 'github.com'


def probe_once(ip, timeout=6):
    """用指定 IP 连一次 github.com，返回耗时秒数；不通返回 None。

    必须带 SNI（server_hostname=github.com），否则测的不是真实可用性 ——
    这也顺带验证了该 IP 提供的确实是 github.com 的合法证书。
    """
    import ssl
    ctx = ssl.create_default_context()
    t0 = time.time()
    try:
        with socket.create_connection((ip, 443), timeout=timeout) as raw:
            with ctx.wrap_socket(raw, server_hostname=HOST) as s:
                s.send(b'HEAD / HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n'
                       % HOST.encode())
                if not s.recv(64):
                    return None
        return time.time() - t0
    except Exception:
        return None


def probe(ip, timeout=6, samples=2):
    """多次采样取最好值 —— 国内网络抖动大，单次结果很不可靠。"""
    best = None
    for _ in range(samples):
        t = probe_once(ip, timeout)
        if t is not None and (best is None or t < best):
            best = t
    return best


def pick_all(verbose=True):
    """返回所有能连通的 IP，按延迟升序（延迟小的更稳）。"""
    found = []
    for ip in CANDIDATE_IPS:
        t = probe(ip)
        if verbose:
            print('    %-18s %s' % (ip, ('%.2fs' % t) if t else '不通'))
        if t is not None:
            found.append((ip, t))
    found.sort(key=lambda x: x[1])
    return found


class Tunnel:
    """迷你 CONNECT 代理：把 github.com:443 的隧道转发到指定 IP。"""

    def __init__(self, target_ip, port=0):
        self.target_ip = target_ip
        self.srv = socket.socket()
        self.srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.srv.bind(('127.0.0.1', port))
        self.port = self.srv.getsockname()[1]
        self.srv.listen(32)
        self._stop = False

    def start(self):
        threading.Thread(target=self._serve, daemon=True).start()
        return self

    def _serve(self):
        while not self._stop:
            try:
                conn, _ = self.srv.accept()
            except OSError:
                return
            threading.Thread(target=self._handle, args=(conn,), daemon=True).start()

    def _handle(self, conn):
        try:
            header = b''
            while b'\r\n\r\n' not in header:
                chunk = conn.recv(4096)
                if not chunk:
                    conn.close()
                    return
                header += chunk
                if len(header) > 65536:
                    conn.close()
                    return

            first = header.split(b'\r\n', 1)[0].decode('latin-1')
            parts = first.split()
            if len(parts) < 2:
                conn.close()
                return
            target = parts[1]                 # 形如 github.com:443
            host, _, port = target.rpartition(':')
            port = int(port or 443)

            ip = self.target_ip if host == HOST else host
            upstream = socket.create_connection((ip, port), timeout=20)
            conn.sendall(b'HTTP/1.1 200 Connection established\r\n\r\n')

            threading.Thread(target=self._pipe, args=(conn, upstream),
                             daemon=True).start()
            self._pipe(upstream, conn)
        except Exception:
            try:
                conn.close()
            except Exception:
                pass

    @staticmethod
    def _pipe(src, dst):
        try:
            while True:
                data = src.recv(65536)
                if not data:
                    break
                dst.sendall(data)
        except Exception:
            pass
        finally:
            for s in (src, dst):
                try:
                    s.shutdown(socket.SHUT_RDWR)
                except Exception:
                    pass
                try:
                    s.close()
                except Exception:
                    pass

    def stop(self):
        self._stop = True
        try:
            self.srv.close()
        except Exception:
            pass


def main(argv):
    ap = argparse.ArgumentParser(add_help=True)
    ap.add_argument('-C', dest='repo', default=None, help='仓库路径')
    ap.add_argument('--test', action='store_true', help='只测速，不执行 git')
    ap.add_argument('--ip', default=None, help='强制用这个 IP')
    known, rest = ap.parse_known_args(argv[1:])
    git_args = [a for a in rest if not a.startswith('--ip')]

    print('[*] 实测 GitHub 入口 IP（各测 2 次取最好）：')
    if known.ip:
        t = probe(known.ip)
        cands = [(known.ip, t)] if t is not None else []
        print('    指定 %-18s %s' % (known.ip, ('%.2fs' % t) if t else '不通'))
    else:
        cands = pick_all()

    if not cands:
        print('\n[×] 所有候选 IP 都不通 —— 可能是网络整体不通，或需要代理/VPN。')
        return 3
    for ip, t in cands:
        print('[√] 可用 %s（%.2fs）' % (ip, t))

    if known.test:
        return 0
    if not git_args:
        print('[!] 没给 git 子命令。例如：python git_via_fastip.py push origin main')
        return 1

    # 逐个 IP 重试：国内网络会中途掐连接（实测过 schannel: server closed abruptly），
    # 单个 IP 失败不代表命令失败，换一个往往就过了。所有 git 子命令都是幂等的，重试安全。
    for idx, (ip, t) in enumerate(cands, start=1):
        print('-' * 60)
        print('[*] 第 %d/%d 次尝试，走 %s（%.2fs）' % (idx, len(cands), ip, t))
        tun = Tunnel(ip).start()
        proxy = 'http://127.0.0.1:%d' % tun.port
        print('[*] 隧道 %s -> %s:443' % (proxy, ip))
        print('[*] 执行：git %s' % ' '.join(git_args))
        env = dict(os.environ)
        env['HTTPS_PROXY'] = proxy
        env['HTTP_PROXY'] = proxy
        try:
            cmd = ['git', '-c', 'http.proxy=' + proxy, '-c', 'https.proxy=' + proxy]
            if known.repo:
                cmd += ['-C', known.repo]
            cmd += git_args
            rc = subprocess.call(cmd, env=env)
        finally:
            tun.stop()
        if rc == 0:
            print('-' * 60)
            print('[√] 成功（走 %s）' % ip)
            return 0
        print('[!] 走 %s 失败（退出码 %d），换下一个 IP 重试…' % (ip, rc))

    print('-' * 60)
    print('[×] 所有可用 IP 都试过了，仍然失败。稍后重试，或改用代理/VPN。')
    return 1


if __name__ == '__main__':
    sys.exit(main(sys.argv))
