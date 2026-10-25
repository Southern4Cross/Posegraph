"""Brio .pose（PoseFile v3）JSON 的读写。

格式要点（来自 Brio 源码 Brio/Files/PoseFile.cs 与内置 BrioTPose.pose）：
- Bones: { 骨骼名: {Position, Rotation, Scale} }，均为 **模型空间** 的绝对值
- Vector3 序列化为 "x, y, z" 字符串，Quaternion 为 "x, y, z, w"
- 另有 MainHand / OffHand / Prop / Ornament（武器、道具骨骼），本项目不改动
"""

from __future__ import annotations

import copy
import json
from dataclasses import dataclass

import numpy as np


@dataclass
class BoneTransform:
    position: np.ndarray  # (3,)
    rotation: np.ndarray  # (4,) xyzw
    scale: np.ndarray     # (3,)


def _parse_vec(s: str) -> np.ndarray:
    return np.array([float(v) for v in s.split(",")], dtype=np.float64)


def _fmt_vec(v: np.ndarray) -> str:
    return ", ".join(f"{float(x):.9g}" for x in v)


class BrioPose:
    """包装一份 Brio 姿势 JSON。保留原文中所有字段，只替换被修改的骨骼。"""

    def __init__(self, raw: dict):
        self.raw = raw

    @classmethod
    def loads(cls, text: str) -> "BrioPose":
        return cls(json.loads(text.lstrip("﻿")))

    def dumps(self) -> str:
        return json.dumps(self.raw, ensure_ascii=False, indent=2)

    def copy(self) -> "BrioPose":
        return BrioPose(copy.deepcopy(self.raw))

    @property
    def bone_names(self) -> list[str]:
        return list(self.raw.get("Bones", {}).keys())

    def get(self, bone: str) -> BoneTransform | None:
        b = self.raw.get("Bones", {}).get(bone)
        if b is None:
            return None
        return BoneTransform(_parse_vec(b["Position"]), _parse_vec(b["Rotation"]), _parse_vec(b["Scale"]))

    def set(self, bone: str, t: BoneTransform) -> None:
        self.raw.setdefault("Bones", {})[bone] = {
            "Position": _fmt_vec(t.position),
            "Rotation": _fmt_vec(t.rotation),
            "Scale": _fmt_vec(t.scale),
        }
