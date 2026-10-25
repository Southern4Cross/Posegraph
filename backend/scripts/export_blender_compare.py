"""导出 Blender 对比所需数据（FFXIV 骨骼姿势 + MHR 网格 + 拍摄相机）。

    .venv/Scripts/python scripts/export_blender_compare.py outputs/xxx.npz [-o outputs/xxx.blender.json]

输出 JSON（全部为 FFXIV/MHR 模型空间：y 上、z 前、米）：
- ffxiv: {骨骼名: {position, bind_position, delta}}，delta = 最终全局旋转 · 绑定全局旋转⁻¹（xyzw），
  蒙皮变换 S = 平移(position) · delta · 平移(-bind_position)；Blender pose 矩阵 = S · rest 矩阵
  （TexTools 导出的 IVCS 骨骼没有父骨骼、rest 也不对，必须这样算才与 rest 无关）
- ffxiv_offset: 把 FFXIV 髋关节中点对齐到 MHR 髋关节中点的平移
- mhr: {vertices, faces}；camera: {position, fov_y_deg, aspect}（相机朝 -z，上方向 +y）
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
sys.path.insert(0, str(ROOT / "src"))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("npz", type=Path)
    ap.add_argument("-o", "--output", type=Path)
    ap.add_argument("--neck", type=float, default=1.0, help="颈部幅度")
    args = ap.parse_args()

    import numpy as np
    from scipy.spatial.transform import Rotation as R

    from posegraph.retarget.ffxiv import forward_kinematics, load_ffxiv_skeleton, load_mhr_rest, retarget

    z = np.load(args.npz)
    flip = np.array([1.0, -1.0, -1.0])  # 相机系 ↔ 模型系
    rt = retarget(z["joint_global_rots"], z["joint_coords"], z["rest_joint_coords"], z["rest_joint_rots"],
                  vertices_cam=z["vertices"], photo=(float(z["focal_length"]), z["cam_t"], tuple(int(v) for v in z["image_size"])),
                  neck_scale=args.neck)
    # 站姿重力校正把身体转正了：MHR 网格与拍摄相机绕原点做同样的旋转，三者保持在同一坐标系
    up = R.from_quat(rt.upright_rotation) if rt.upright_rotation is not None else R.identity()
    sk = load_ffxiv_skeleton()
    G_bind, P_bind = forward_kinematics(sk)
    ffxiv = {
        n: {"position": rt.all_positions[n].tolist(), "bind_position": P_bind[i].tolist(),
            "delta": (R.from_quat(rt.all_rotations[n]) * G_bind[i].inv()).as_quat().tolist()}
        for i, n in enumerate(sk.names)
    }
    names = load_mhr_rest()[0]
    # 以两侧髋关节中点对齐（j_kosi 在腰部，比 MHR root 高约 11cm，用它对齐会让整个身体错位）
    mc = up.apply(z["joint_coords"] * flip)
    mhr_hip = (mc[names.index("l_upleg")] + mc[names.index("r_upleg")]) / 2
    ff_hip = (rt.all_positions["j_asi_a_l"] + rt.all_positions["j_asi_a_r"]) / 2
    offset = mhr_hip - ff_hip

    w, h = (int(v) for v in z["image_size"])
    f = float(z["focal_length"])
    data = {
        "ffxiv": ffxiv,
        "ffxiv_offset": offset.tolist(),
        "mhr": {"vertices": up.apply(z["vertices"] * flip).round(5).tolist(), "faces": z["faces"].astype(int).tolist()},
        "camera": {"position": up.apply(-z["cam_t"] * flip).tolist(), "rotation": up.as_quat().tolist(),
                   "fov_y_deg": float(np.degrees(2 * np.arctan(h / 2 / f))), "aspect": w / h},
    }
    out = args.output or args.npz.with_suffix(".blender.json")
    out.write_text(json.dumps(data), encoding="utf-8")
    print(f"已写入 {out}")


if __name__ == "__main__":
    main()
