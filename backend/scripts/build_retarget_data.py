"""生成重定向用的静态数据（src/posegraph/retarget/data/）。

    .venv/Scripts/python scripts/build_retarget_data.py --sklb "<IVCS 目录>/skeleton/chara/human/c0101/skeleton/base/b0001/skl_c0101b0001.sklb"

输出：
- mhr_rest.json：MHR 全零参数（平均体型、零姿态）下 127 个关节的模型空间位置（米）与全局旋转（xyzw）
- ffxiv_<race>.json：FFXIV 骨骼名、父索引、相对父骨骼的参考变换（来自 .sklb）
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
sys.path.insert(0, str(ROOT / "src"))
DATA = ROOT / "src" / "posegraph" / "retarget" / "data"


def build_mhr_rest() -> dict:
    import roma
    import torch

    from posegraph.config import settings
    from posegraph.estimators.sam3d_body import Sam3dBodyEstimator
    from posegraph.retarget.mhr import MHR_JOINT_NAMES

    est = Sam3dBodyEstimator(settings.sam3d_repo, settings.sam3d_checkpoint_dir)
    est.load()
    head = est._estimator.model.head_pose
    dev = head.scale_mean.device
    z = lambda n: torch.zeros(1, n, device=dev)
    with torch.no_grad():
        _, coords, rots = head.mhr_forward(
            global_trans=z(3), global_rot=z(3), body_pose_params=z(133), hand_pose_params=None,
            scale_params=z(head.scale_comps.shape[0]), shape_params=z(head.num_shape_comps), expr_params=z(72),
            return_joint_coords=True, return_joint_rotations=True,
        )
    quats = roma.rotmat_to_unitquat(rots[0]).cpu().numpy()  # roma 为 xyzw
    parents = head.mhr.character_torch.skeleton.joint_parents.cpu().numpy().tolist()
    assert len(parents) == len(MHR_JOINT_NAMES) == coords.shape[1]
    return {
        "source": "SAM 3D Body vith MHR head, all-zero params",
        "names": MHR_JOINT_NAMES,
        "parents": parents,
        "rest_position": coords[0].cpu().numpy().round(6).tolist(),
        "rest_rotation": quats.round(7).tolist(),
    }


def build_ffxiv(sklb: Path) -> dict:
    from posegraph.retarget.sklb import parse_sklb

    sk = parse_sklb(sklb)
    return {
        "source": sklb.name,
        "names": sk.names,
        "parents": sk.parents.tolist(),
        "local_translation": sk.local_t.round(7).tolist(),
        "local_rotation": sk.local_q.round(7).tolist(),
        "local_scale": sk.local_s.round(7).tolist(),
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--sklb", type=Path, help="FFXIV 基础骨架 skl_cXXXXb0001.sklb")
    ap.add_argument("--skip-mhr", action="store_true")
    args = ap.parse_args()
    DATA.mkdir(parents=True, exist_ok=True)

    if not args.skip_mhr:
        (DATA / "mhr_rest.json").write_text(json.dumps(build_mhr_rest()), encoding="utf-8")
        print("写入 mhr_rest.json")
    if args.sklb:
        race = re.search(r"skl_(c\d{4})b", args.sklb.name).group(1)
        out = DATA / f"ffxiv_{race}.json"
        out.write_text(json.dumps(build_ffxiv(args.sklb)), encoding="utf-8")
        print(f"写入 {out.name}")


if __name__ == "__main__":
    main()
