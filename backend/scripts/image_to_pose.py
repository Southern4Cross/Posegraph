"""命令行：图片 → Brio .pose（不经过游戏，便于离线检查）。

    .venv/Scripts/python scripts/image_to_pose.py <图片> [-o outputs/xxx.pose] [--npz outputs/xxx.npz]

同时打印重定向自检：每根骨段 FFXIV 方向与 MHR 方向的夹角（应接近 0°）。
--npz 额外保存 SAM 3D Body 原始输出，供 Blender 预览/调试使用。
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
    ap.add_argument("-o", "--output", type=Path)
    ap.add_argument("--npz", type=Path)
    ap.add_argument("--no-ground", action="store_true", help="不自动贴地")
    ap.add_argument("--neck", type=float, default=1.0, help="颈部幅度 0~1（插画可用 0.5）")
    ap.add_argument("--race", default="c0101", help="骨架编号，如 c0901（鲁加男），见 retarget/races.py")
    args = ap.parse_args()

    import cv2
    import numpy as np

    from posegraph.estimators import create_estimator
    from posegraph.retarget.ffxiv import retarget, to_brio_pose_json

    bgr = cv2.imdecode(np.fromfile(args.image, dtype=np.uint8), cv2.IMREAD_COLOR)
    est = create_estimator()
    res = est.estimate(cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB))
    if not res.humans:
        sys.exit("未检测到人物")
    h = res.humans[0]

    rt = retarget(h.joint_global_rots, h.joint_coords, h.rest_joint_coords, h.rest_joint_rots, rest_vertices=h.extra["rest_vertices"],
                  vertices_cam=h.extra["vertices"], photo=(h.focal_length, h.cam_t, res.image_size),
                  ground=not args.no_ground, neck_scale=args.neck, race=args.race)
    errs = rt.segment_error_deg
    print(f"推理 {res.timings_ms['inference']:.0f} ms；输出骨骼 {len(rt.bones)} 根；末端补偿 {rt.hand_compensation}")
    print(f"骨段方向误差：最大 {max(errs.values()):.3f}°，平均 {np.mean(list(errs.values())):.3f}°")
    for name, e in sorted(errs.items(), key=lambda kv: -kv[1])[:5]:
        print(f"  {name:14s} {e:.3f}°")

    out = args.output or ROOT / "outputs" / f"{args.image.stem}.pose"
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(to_brio_pose_json(rt), encoding="utf-8")
    print(f"已写入 {out}")

    if args.npz:
        args.npz.parent.mkdir(parents=True, exist_ok=True)
        np.savez(args.npz, joint_global_rots=h.joint_global_rots, joint_coords=h.joint_coords,
                 rest_joint_coords=h.rest_joint_coords, rest_joint_rots=h.rest_joint_rots,
                 keypoints_2d=h.keypoints_2d, focal_length=h.focal_length, cam_t=h.cam_t, bbox=h.bbox,
                 image_size=np.array(res.image_size),
                 vertices=h.extra["vertices"], faces=h.extra["faces"], rest_vertices=h.extra["rest_vertices"])
        print(f"已写入 {args.npz}")


if __name__ == "__main__":
    main()
