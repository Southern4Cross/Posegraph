"""把重定向后的 FFXIV 骨架按估计相机投影回原图，生成叠加预览图（快速目检，不需要游戏/Blender）。

    .venv/Scripts/python scripts/preview_overlay.py <图片> <image_to_pose 生成的 .npz> [-o out.png]

绿色：MHR 骨架（模型原始结果）；橙色：FFXIV 骨架（重定向结果，每段从对应 MHR 关节起画，只比较方向）。
左侧骨骼画实线、右侧画虚线点，用来检查左右是否颠倒。
"""

from __future__ import annotations

import argparse
import os
import sys
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
sys.path.insert(0, str(ROOT / "src"))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("image", type=Path)
    ap.add_argument("npz", type=Path)
    ap.add_argument("-o", "--output", type=Path)
    args = ap.parse_args()

    import cv2
    import numpy as np

    from posegraph.retarget.ffxiv import BONE_MAP, forward_kinematics, load_ffxiv_skeleton, load_mhr_rest, retarget

    z = np.load(args.npz)
    img = cv2.imdecode(np.fromfile(args.image, dtype=np.uint8), cv2.IMREAD_COLOR)
    h, w = img.shape[:2]
    f, cam_t = float(z["focal_length"]), z["cam_t"]
    flip = np.array([1.0, -1.0, -1.0])

    def project(p_cam: np.ndarray) -> tuple[int, int]:
        q = p_cam + cam_t
        return int(f * q[0] / q[2] + w / 2), int(f * q[1] / q[2] + h / 2)

    mhr_names, _, _ = load_mhr_rest()
    mhr_cam = z["joint_coords"]
    rt = retarget(z["joint_global_rots"], mhr_cam,
                  z["rest_joint_coords"] if "rest_joint_coords" in z else None,
                  z["rest_joint_rots"] if "rest_joint_rots" in z else None,
                  upright=False)  # 叠加到原照片上比较，不做重力校正
    sk = load_ffxiv_skeleton()
    G_new = [None] * len(sk.names)
    from scipy.spatial.transform import Rotation as R
    for name, (_, q, _) in rt.bones.items():
        G_new[sk.index(name)] = R.from_quat(q)
    _, P = forward_kinematics(sk, G_new)
    # 每段 FFXIV 骨骼都从对应 MHR 关节起画（只比较方向）：整体骨盆对齐时，
    # 两套骨架比例不同，近距离强透视下几厘米的位置差会被放大成几十像素，看起来像姿势错了
    mhr_of = {m.ffxiv: m.mhr for m in BONE_MAP}
    ff_cam = np.zeros_like(P)
    for i, n in enumerate(sk.names):
        p = sk.parents[i]
        anchor = mhr_of.get(sk.names[p]) if p >= 0 else None
        if anchor is not None:
            ff_cam[i] = mhr_cam[mhr_names.index(anchor)] + (P[i] - P[p]) * flip
        else:
            ff_cam[i] = (P[i] - P[sk.index("j_kosi")]) * flip + mhr_cam[mhr_names.index("root")]

    def draw(points: dict[str, tuple[int, int]], pairs, color):
        for a, b in pairs:
            if a in points and b in points:
                dashed = a.endswith("_r") or a.startswith("r_")
                cv2.line(img, points[a], points[b], color, 2 if not dashed else 1, cv2.LINE_AA)
                if dashed:
                    cv2.circle(img, points[b], 3, color, -1)

    ff_pts = {n: project(ff_cam[i]) for i, n in enumerate(sk.names)}
    mhr_pts = {n: project(mhr_cam[i]) for i, n in enumerate(mhr_names)}
    ff_pairs = [(sk.names[sk.parents[sk.index(m.ffxiv)]], m.ffxiv) for m in BONE_MAP] + \
               [(m.ffxiv, m.ffxiv_tip) for m in BONE_MAP if m.ffxiv_tip]
    mhr_pairs = [(m.mhr, m.mhr_tip) for m in BONE_MAP if m.mhr_tip]
    draw(mhr_pts, mhr_pairs, (80, 220, 80))
    draw(ff_pts, ff_pairs, (0, 140, 255))

    out = args.output or args.npz.with_suffix(".overlay.png")
    cv2.imencode(".png", img)[1].tofile(out)
    print(f"已写入 {out}")


if __name__ == "__main__":
    main()
