"""FFXIV 种族形变文件 human.pbd（chara/xls/boneDeformer/human.pbd）解析。

游戏里多个种族共用一套装备模型（例如精灵 / 猫魅 / 敖龙 / 维埃拉男穿中原男 c0101 的模型），
载入时按"形变树"把网格从原种族逐级变到目标种族：每级给出每根骨骼一个 3×4 矩阵，
顶点按蒙皮权重混合这些矩阵（v' = Σ wᵢ·Mᵢ·v），再按目标种族骨架蒙皮。

格式（与 Meddle / Penumbra 的解析一致，字段含义经实测核对）：
- int32 条目数 N
- N × {u16 种族编号（如 501）, u16 形变树节点号, u32 形变数据偏移（0 表示无）, f32 缩放}
- N × 形变树节点 {u16 父节点, u16 第一个子节点, u16 下一个兄弟, u16 条目序号}（0xFFFF 表示无）
- 形变数据：int32 骨骼数 M、M × i16 骨骼名偏移（相对形变数据起点）、按 4 字节对齐、M × 12 个 float32（3×4，行优先）
"""

from __future__ import annotations

import struct
from dataclasses import dataclass
from functools import cache
from pathlib import Path

import numpy as np

NONE = 0xFFFF


@dataclass
class Deformer:
    race: int                       # 101、501 等
    parent: int | None              # 父节点的种族编号（形变从父种族变到本种族）
    matrices: dict[str, np.ndarray]  # 骨骼名 → 4×4


def parse_pbd(path: str | Path) -> dict[int, Deformer]:
    d = Path(path).read_bytes()
    n = struct.unpack_from("<i", d, 0)[0]
    headers = [struct.unpack_from("<HHIf", d, 4 + 12 * i) for i in range(n)]
    links_at = 4 + 12 * n
    links = [struct.unpack_from("<HHHH", d, links_at + 8 * i) for i in range(n)]
    race_of_node = {}
    for node, (_, _, _, entry) in enumerate(links):
        race_of_node[node] = headers[entry][0]

    out = {}
    for race, node, offset, _scale in headers:
        parent_node = links[node][0]
        parent = race_of_node.get(parent_node) if parent_node != NONE else None
        mats = {}
        if offset:
            count = struct.unpack_from("<i", d, offset)[0]
            name_offsets = struct.unpack_from(f"<{count}h", d, offset + 4)
            mat_at = offset + 4 + 2 * count
            mat_at += (-mat_at) % 4
            raw = np.array(struct.unpack_from(f"<{count * 12}f", d, mat_at)).reshape(count, 3, 4)
            for i, rel in enumerate(name_offsets):
                start = offset + rel
                name = d[start:d.index(b"\0", start)].decode("ascii")
                m = np.eye(4)
                m[:3, :] = raw[i]
                mats[name] = m
        out[race] = Deformer(race, parent, mats)
    return out


def chain(deformers: dict[int, Deformer], source: int, target: int) -> list[Deformer]:
    """从 source 变到 target 需要依次施加的形变（target 必须是 source 的后代）。"""
    path = []
    cur = deformers.get(target)
    while cur is not None and cur.race != source:
        path.append(cur)
        cur = deformers.get(cur.parent) if cur.parent is not None else None
    if cur is None:
        raise ValueError(f"{target} 不在 {source} 的形变树下")
    return path[::-1]


def bone_matrix(deformers: dict[int, Deformer], source: int, target: int, bone: str,
                parents: dict[str, str | None]) -> np.ndarray:
    """某根骨骼从 source 到 target 的累计形变矩阵。某级没有这根骨骼时用最近的有数据的祖先骨骼（与游戏一致）。"""
    m = np.eye(4)
    for df in chain(deformers, source, target):
        b = bone
        while b is not None and b not in df.matrices:
            b = parents.get(b)
        if b is not None:
            m = df.matrices[b] @ m
    return m


@cache
def load_ivcs_pbd(path: str | None = None) -> dict[int, Deformer]:
    """IVCS 自带的 human.pbd（"racial scaling file (pbd)/chara/xls/boneDeformer/human.pbd"）。
    路径取参数或环境变量 POSEGRAPH_PBD。只有 Blender 对比工具用到，后端运行时不需要。"""
    import os

    path = path or os.environ.get("POSEGRAPH_PBD")
    if not path:
        raise FileNotFoundError("请用 --pbd 或环境变量 POSEGRAPH_PBD 指定 IVCS 的 human.pbd")
    return parse_pbd(Path(path))
