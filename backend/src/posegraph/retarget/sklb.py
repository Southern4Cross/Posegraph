"""FFXIV .sklb 骨架文件解析（只取 hkaSkeleton 的骨骼名、父索引、参考姿势）。

.sklb = 'blks' 头 + Havok 旧版二进制 tagfile（魔数 0xCAB00D1E 0xD011FACE）。
完整解析 tagfile 需要实现类型系统，这里只做针对 hkaSkeleton 的定位解析：
- 骨骼名：zigzag 变长整数长度前缀的字符串序列，第一个是 n_root
- 名字前面：骨骼数（zigzag varint）+ 1 个标记字节
- 再往前：parentIndices 数组 = 骨骼数 + 类型标记 + N 个 zigzag varint（根为负数）
- 名字后面：referencePose = 骨骼数 + N×12 个 float32（平移 xyzw、旋转 xyzw、缩放 xyzw）
每一步都做一致性校验，格式不符时抛异常而不是给出错误数据。
已验证：Illusio Vitae Custom Skeletons V2_2 的 skl_c0101b0001.sklb（167 根骨骼）。
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path

import numpy as np

_TAGFILE_MAGIC = b"\x1e\x0d\xb0\xca\xce\xfa\x11\xd0"


@dataclass
class HavokSkeleton:
    names: list[str]
    parents: np.ndarray       # (N,) int，根为 -1
    local_t: np.ndarray       # (N, 3) 相对父骨骼的平移（米）
    local_q: np.ndarray       # (N, 4) 相对父骨骼的旋转 xyzw
    local_s: np.ndarray       # (N, 3)

    def index(self, name: str) -> int:
        return self.names.index(name)


def _read_varint(buf: bytes, pos: int) -> tuple[int, int]:
    v = shift = 0
    while True:
        b = buf[pos]
        pos += 1
        v |= (b & 0x7F) << shift
        shift += 7
        if not b & 0x80:
            return (v >> 1) ^ -(v & 1), pos


def _encode_varint(value: int) -> bytes:
    v = (value << 1) ^ (value >> 63)
    out = bytearray()
    while True:
        b = v & 0x7F
        v >>= 7
        if v:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def parse_sklb(path: str | Path) -> HavokSkeleton:
    data = Path(path).read_bytes()
    if data[:4] != b"blks":
        raise ValueError("不是 .sklb 文件")
    hk_start = data.find(_TAGFILE_MAGIC)
    if hk_start < 0:
        raise ValueError("未找到 Havok 二进制 tagfile（可能是 packfile/TAG0 格式，暂不支持）")
    d = data[hk_start:]

    names_at = d.find(b"\x0cn_root")
    if names_at < 0:
        raise ValueError("未找到骨骼名 n_root")

    # 名字区：[count][1 字节标记][names...]
    names = []
    pos = names_at
    while True:
        n, p = _read_varint(d, pos)
        if not 0 < n <= 64 or not all(32 < c < 127 for c in d[p:p + n]):
            break
        names.append(d[p:p + n].decode("ascii"))
        pos = p + n
    count = len(names)
    count_bytes = _encode_varint(count)
    if d[names_at - 1 - len(count_bytes):names_at - 1] != count_bytes:
        raise ValueError(f"骨骼数校验失败：名字 {count} 个，但前缀不匹配")

    # 参考姿势：[count][N×12 float]
    n, p = _read_varint(d, pos)
    if n != count:
        raise ValueError(f"referencePose 数量 {n} != 骨骼数 {count}")
    pose = np.array(struct.unpack_from(f"<{count * 12}f", d, p), dtype=np.float64).reshape(count, 12)

    # 父索引：在名字区之前，最近一次出现的 [count][类型标记][N 个 varint]
    search_end = names_at - 1 - len(count_bytes)
    at = d.rfind(count_bytes, 0, search_end)
    while at >= 0:
        _, q = _read_varint(d, at)
        _, q = _read_varint(d, q)  # 类型标记
        parents, ok = [], True
        for i in range(count):
            v, q = _read_varint(d, q)
            if v >= i:
                ok = False
                break
            parents.append(max(v, -1))
        if ok and q == search_end:
            break
        at = d.rfind(count_bytes, 0, at)
    else:
        raise ValueError("未找到合法的 parentIndices 数组")

    return HavokSkeleton(
        names=names,
        parents=np.array(parents, dtype=np.int32),
        local_t=pose[:, 0:3].copy(),
        local_q=pose[:, 4:8].copy(),
        local_s=pose[:, 8:11].copy(),
    )
