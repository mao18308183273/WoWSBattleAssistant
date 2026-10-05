# -*- coding: utf-8 -*-
"""
des_pure.py —— 纯标准库 DES 实现（CBC / PKCS5Padding）。

为什么不用 pycryptodome：
  这个爬虫要做到「换台机器、随便一个 Python 都能跑」，第三方库装不上就等于废掉。
  之前为 WoWS 回放写 Blowfish 时踩过同样的坑（pip 源全挂），所以这里直接手写。
  如果机器上碰巧有 pycryptodome 会自动优先用它（快得多），没有则退回本实现。

只实现了爬虫真正需要的部分：DES-CBC 解密（加解密对称，同一份代码两边都用）。
"""
import base64

# ---------------------------------------------------------------- 标准 DES 表

# 置换表用 1-based、从最高位开始编号（与 FIPS 46-3 文档一致）
_PC1 = [
    57, 49, 41, 33, 25, 17,  9,  1, 58, 50, 42, 34, 26, 18,
    10,  2, 59, 51, 43, 35, 27, 19, 11,  3, 60, 52, 44, 36,
    63, 55, 47, 39, 31, 23, 15,  7, 62, 54, 46, 38, 30, 22,
    14,  6, 61, 53, 45, 37, 29, 21, 13,  5, 28, 20, 12,  4,
]

_PC2 = [
    14, 17, 11, 24,  1,  5,  3, 28, 15,  6, 21, 10,
    23, 19, 12,  4, 26,  8, 16,  7, 27, 20, 13,  2,
    41, 52, 31, 37, 47, 55, 30, 40, 51, 45, 33, 48,
    44, 49, 39, 56, 34, 53, 46, 42, 50, 36, 29, 32,
]

# 每轮 C/D 两半各左移的位数
_SHIFTS = [1, 1, 2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 2, 2, 2, 1]

_IP = [
    58, 50, 42, 34, 26, 18, 10,  2, 60, 52, 44, 36, 28, 20, 12,  4,
    62, 54, 46, 38, 30, 22, 14,  6, 64, 56, 48, 40, 32, 24, 16,  8,
    57, 49, 41, 33, 25, 17,  9,  1, 59, 51, 43, 35, 27, 19, 11,  3,
    61, 53, 45, 37, 29, 21, 13,  5, 63, 55, 47, 39, 31, 23, 15,  7,
]

_FP = [
    40,  8, 48, 16, 56, 24, 64, 32, 39,  7, 47, 15, 55, 23, 63, 31,
    38,  6, 46, 14, 54, 22, 62, 30, 37,  5, 45, 13, 53, 21, 61, 29,
    36,  4, 44, 12, 52, 20, 60, 28, 35,  3, 43, 11, 51, 19, 59, 27,
    34,  2, 42, 10, 50, 18, 58, 26, 33,  1, 41,  9, 49, 17, 57, 25,
]

# E 位选择表（32 -> 48）
_E = [
    32,  1,  2,  3,  4,  5,  4,  5,  6,  7,  8,  9,
     8,  9, 10, 11, 12, 13, 12, 13, 14, 15, 16, 17,
    16, 17, 18, 19, 20, 21, 20, 21, 22, 23, 24, 25,
    24, 25, 26, 27, 28, 29, 28, 29, 30, 31, 32,  1,
]

# P 置换（32 -> 32）
_P = [
    16,  7, 20, 21, 29, 12, 28, 17,  1, 15, 23, 26,  5, 18, 31, 10,
     2,  8, 24, 14, 32, 27,  3,  9, 19, 13, 30,  6, 22, 11,  4, 25,
]

_SBOXES = [
    # S1
    [14, 4, 13, 1, 2, 15, 11, 8, 3, 10, 6, 12, 5, 9, 0, 7,
     0, 15, 7, 4, 14, 2, 13, 1, 10, 6, 12, 11, 9, 5, 3, 8,
     4, 1, 14, 8, 13, 6, 2, 11, 15, 12, 9, 7, 3, 10, 5, 0,
     15, 12, 8, 2, 4, 9, 1, 7, 5, 11, 3, 14, 10, 0, 6, 13],
    # S2
    [15, 1, 8, 14, 6, 11, 3, 4, 9, 7, 2, 13, 12, 0, 5, 10,
     3, 13, 4, 7, 15, 2, 8, 14, 12, 0, 1, 10, 6, 9, 11, 5,
     0, 14, 7, 11, 10, 4, 13, 1, 5, 8, 12, 6, 9, 3, 2, 15,
     13, 8, 10, 1, 3, 15, 4, 2, 11, 6, 7, 12, 0, 5, 14, 9],
    # S3
    [10, 0, 9, 14, 6, 3, 15, 5, 1, 13, 12, 7, 11, 4, 2, 8,
     13, 7, 0, 9, 3, 4, 6, 10, 2, 8, 5, 14, 12, 11, 15, 1,
     13, 6, 4, 9, 8, 15, 3, 0, 11, 1, 2, 12, 5, 10, 14, 7,
     1, 10, 13, 0, 6, 9, 8, 7, 4, 15, 14, 3, 11, 5, 2, 12],
    # S4
    [7, 13, 14, 3, 0, 6, 9, 10, 1, 2, 8, 5, 11, 12, 4, 15,
     13, 8, 11, 5, 6, 15, 0, 3, 4, 7, 2, 12, 1, 10, 14, 9,
     10, 6, 9, 0, 12, 11, 7, 13, 15, 1, 3, 14, 5, 2, 8, 4,
     3, 15, 0, 6, 10, 1, 13, 8, 9, 4, 5, 11, 12, 7, 2, 14],
    # S5
    [2, 12, 4, 1, 7, 10, 11, 6, 8, 5, 3, 15, 13, 0, 14, 9,
     14, 11, 2, 12, 4, 7, 13, 1, 5, 0, 15, 10, 3, 9, 8, 6,
     4, 2, 1, 11, 10, 13, 7, 8, 15, 9, 12, 5, 6, 3, 0, 14,
     11, 8, 12, 7, 1, 14, 2, 13, 6, 15, 0, 9, 10, 4, 5, 3],
    # S6
    [12, 1, 10, 15, 9, 2, 6, 8, 0, 13, 3, 4, 14, 7, 5, 11,
     10, 15, 4, 2, 7, 12, 9, 5, 6, 1, 13, 14, 0, 11, 3, 8,
     9, 14, 15, 5, 2, 8, 12, 3, 7, 0, 4, 10, 1, 13, 11, 6,
     4, 3, 2, 12, 9, 5, 15, 10, 11, 14, 1, 7, 6, 0, 8, 13],
    # S7
    [4, 11, 2, 14, 15, 0, 8, 13, 3, 12, 9, 7, 5, 10, 6, 1,
     13, 0, 11, 7, 4, 9, 1, 10, 14, 3, 5, 12, 2, 15, 8, 6,
     1, 4, 11, 13, 12, 3, 7, 14, 10, 15, 6, 8, 0, 5, 9, 2,
     6, 11, 13, 8, 1, 4, 10, 7, 9, 5, 0, 15, 14, 2, 3, 12],
    # S8
    [13, 2, 8, 4, 6, 15, 11, 1, 10, 9, 3, 14, 5, 0, 12, 7,
     1, 15, 13, 8, 10, 3, 7, 4, 12, 5, 6, 11, 0, 14, 9, 2,
     7, 11, 4, 1, 9, 12, 14, 2, 0, 6, 10, 13, 15, 3, 5, 8,
     2, 1, 14, 7, 4, 10, 8, 13, 15, 12, 9, 0, 3, 5, 6, 11],
]


def _permute(val, nbits, table):
    """按 DES 文档的表做位选择：输出第 i 位 = 输入第 table[i] 位（1-based，从高位算起）。"""
    out = 0
    for pos in table:
        out = (out << 1) | ((val >> (nbits - pos)) & 1)
    return out


def _key_schedule(key8):
    """8 字节密钥 -> 16 个 48 位子密钥"""
    k = int.from_bytes(key8, 'big')
    perm = _permute(k, 64, _PC1)          # 64 -> 56（丢掉校验位）
    c = perm >> 28
    d = perm & 0x0FFFFFFF
    subs = []
    for s in _SHIFTS:
        c = ((c << s) | (c >> (28 - s))) & 0x0FFFFFFF
        d = ((d << s) | (d >> (28 - s))) & 0x0FFFFFFF
        subs.append(_permute((c << 28) | d, 56, _PC2))
    return subs


def _f(r, k):
    """Feistel 轮函数：E 扩展 -> 异或子密钥 -> 8 个 S 盒 -> P 置换"""
    x = _permute(r, 32, _E) ^ k
    out = 0
    for i in range(8):
        six = (x >> (42 - 6 * i)) & 0x3F
        row = ((six >> 4) & 0x2) | (six & 1)      # 首尾两位拼成行号
        col = (six >> 1) & 0xF                    # 中间四位是列号
        out = (out << 4) | _SBOXES[i][(row << 4) | col]
    return _permute(out, 32, _P)


def _crypt_block(block8, subs):
    b = _permute(int.from_bytes(block8, 'big'), 64, _IP)
    l = b >> 32
    r = b & 0xFFFFFFFF
    for k in subs:
        l, r = r, l ^ _f(r, k)
    return _permute((r << 32) | l, 64, _FP).to_bytes(8, 'big')


def des_cbc_decrypt(key8, iv8, data):
    """DES-CBC 解密（不做去填充）。data 长度必须是 8 的倍数。"""
    if len(data) == 0 or len(data) % 8 != 0:
        raise ValueError('密文长度必须是 8 的倍数，实际 %d' % len(data))
    subs = _key_schedule(key8)
    subs_rev = subs[::-1]                 # 解密就是把子密钥倒过来用
    prev = int.from_bytes(iv8, 'big')
    out = bytearray()
    for i in range(0, len(data), 8):
        cur = int.from_bytes(data[i:i + 8], 'big')
        plain = int.from_bytes(_crypt_block(data[i:i + 8], subs_rev), 'big') ^ prev
        out.extend(plain.to_bytes(8, 'big'))
        prev = cur
    return bytes(out)


def strip_pkcs5(data):
    """去掉 PKCS5/PKCS7 填充。Java 的 DES/CBC/PKCS5Padding 与这个等价。"""
    if not data:
        return data
    n = data[-1]
    if 1 <= n <= 8 and data[-n:] == bytes([n]) * n:
        return data[:-n]
    return data


def des_cbc_decrypt_b64(key8, iv8, b64text):
    """一步到位：Base64 -> DES-CBC 解密 -> 去填充 -> UTF-8 字符串"""
    raw = base64.b64decode(b64text)
    return strip_pkcs5(des_cbc_decrypt(key8, iv8, raw)).decode('utf-8', 'replace')


# ---------------------------------------------------------------- 自检

if __name__ == '__main__':
    # FIPS/NIST 已知答案：DES 单块 ECB，密钥 0123456789abcdef，明文 "Now is t"
    # 这里没有 ECB，用 CBC + 全零 IV 等价验证（首块 CBC 解密 == ECB 解密 XOR IV）
    key = bytes.fromhex('0123456789abcdef')
    ct = bytes.fromhex('3fa40e8a984d4815')   # "Now is t" 的 DES 密文（公开向量）
    pt = des_cbc_decrypt(key, b'\x00' * 8, ct)
    assert pt == b'Now is t', 'DES 自检失败: %r' % pt

    # 再验填充剥离
    assert strip_pkcs5(b'hello' + bytes([3]) * 3) == b'hello'
    assert strip_pkcs5(b'hello') == b'hello'

    # 与 pycryptodome 交叉验证（如果装了的话）
    try:
        from Crypto.Cipher import DES as _D
        import os
        k = os.urandom(8)
        iv = os.urandom(8)
        plain = b'A' * 13 + b'B' * 5
        pad = plain + bytes([8 - len(plain) % 8]) * (8 - len(plain) % 8)
        ref = _D.new(k, _D.MODE_CBC, iv).encrypt(pad)
        mine = des_cbc_decrypt(k, iv, ref)
        assert strip_pkcs5(mine) == plain, '与 pycryptodome 不一致: %r' % mine
        print('DES 自检通过（含 pycryptodome 交叉验证）')
    except ImportError:
        print('DES 自检通过（未装 pycryptodome，跳过交叉验证）')
