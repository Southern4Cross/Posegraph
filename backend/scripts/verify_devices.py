"""验证不同设备 / 精度 / MHR 位置下的识别输出是否一致，并测显存与速度。

    .venv/Scripts/python scripts/verify_devices.py <图片1> [<图片2> ...]

每种配置在独立子进程里跑（CPU 配置不会初始化 CUDA，显存测量互不干扰），结果存 outputs/verify/<配置>.npz，最后两两比较：
关节坐标 / 顶点（mm）、2D 关键点（px）、关节全局旋转（°），以及重定向到 FF14 后各骨骼旋转差（°）。
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
OUT = ROOT / "outputs" / "verify"

CONFIGS = {
    "gpu_bf16":        {"device": "cuda", "precision": "bf16", "mhr_on_cpu": False},   # 改动前的默认
    "gpu_bf16_rerun":  {"device": "cuda", "precision": "bf16", "mhr_on_cpu": False},   # GPU 自身噪声参照
    "gpu_bf16_mhrcpu": {"device": "cuda", "precision": "bf16", "mhr_on_cpu": True},    # 新默认（GPU）
    "gpu_fp32":        {"device": "cuda", "precision": "fp32", "mhr_on_cpu": False},
    "cpu_fp32":        {"device": "cpu", "precision": "fp32", "mhr_on_cpu": False},
    "cpu_bf16":        {"device": "cpu", "precision": "bf16", "mhr_on_cpu": False},
}

COMPARISONS = [
    ("gpu_bf16", "gpu_bf16_rerun", "GPU 自身噪声（参照）"),
    ("gpu_bf16", "gpu_bf16_mhrcpu", "MHR 放 CPU vs 放 GPU"),
    ("gpu_fp32", "cpu_fp32", "CPU vs GPU（同为 fp32）"),
    ("gpu_bf16", "gpu_fp32", "bf16 vs fp32（同为 GPU）"),
    ("gpu_bf16", "cpu_fp32", "CPU fp32 vs GPU bf16"),
    ("gpu_bf16", "cpu_bf16", "CPU bf16 vs GPU bf16"),
]


def worker(name: str, images: list[str]) -> None:
    sys.path.insert(0, str(ROOT / "src"))
    import time

    import cv2
    import numpy as np

    from posegraph.config import settings
    from posegraph.estimators.sam3d_body import Sam3dBodyEstimator
    from posegraph.gpu import query_gpu

    cfg = CONFIGS[name]
    before = query_gpu(max_age=0)
    est = Sam3dBodyEstimator(settings.sam3d_repo, settings.sam3d_checkpoint_dir, **cfg)
    t0 = time.perf_counter()
    est.load()
    load_s = time.perf_counter() - t0
    after_load = query_gpu(max_age=0)
    out, times = {}, []
    for i, path in enumerate(images):
        rgb = cv2.cvtColor(cv2.imdecode(np.fromfile(path, dtype=np.uint8), cv2.IMREAD_COLOR), cv2.COLOR_BGR2RGB)
        r = est.estimate(rgb)
        times.append(r.timings_ms["inference"])
        h = r.humans[0]
        out.update({f"{i}_joint_coords": h.joint_coords, f"{i}_joint_global_rots": h.joint_global_rots,
                    f"{i}_vertices": h.extra["vertices"], f"{i}_keypoints_2d": h.keypoints_2d,
                    f"{i}_rest_joint_coords": h.rest_joint_coords, f"{i}_rest_joint_rots": h.rest_joint_rots,
                    f"{i}_focal_length": h.focal_length, f"{i}_cam_t": h.cam_t,
                    f"{i}_image_size": np.array(r.image_size)})
    after_infer = query_gpu(max_age=0)
    stats = {"load_s": round(load_s, 1), "infer_ms": [round(t) for t in times], "device": est.active_device}
    import torch

    if torch.cuda.is_initialized():
        stats["torch_peak_reserved_mb"] = torch.cuda.max_memory_reserved() // 2**20
        stats["torch_allocated_mb"] = torch.cuda.memory_allocated() // 2**20
    est.unload()
    after_unload = query_gpu(max_age=0)
    if before:
        used = lambda g: g["usedMB"] - before["usedMB"]
        stats.update(vram_after_load_mb=used(after_load), vram_after_infer_mb=used(after_infer),
                     vram_after_unload_mb=used(after_unload))
    stats["cuda_initialized"] = torch.cuda.is_initialized()
    OUT.mkdir(parents=True, exist_ok=True)
    np.savez(OUT / f"{name}.npz", **out)
    (OUT / f"{name}.json").write_text(json.dumps(stats, ensure_ascii=False), encoding="utf-8")


def _angle_deg(a, b):
    import numpy as np

    rel = np.einsum("...ji,...jk->...ik", a, b)
    cos = (np.trace(rel, axis1=-2, axis2=-1) - 1) / 2
    return np.degrees(np.arccos(np.clip(cos, -1, 1)))


def compare(a: str, b: str, n: int) -> dict:
    import numpy as np
    from scipy.spatial.transform import Rotation as R

    sys.path.insert(0, str(ROOT / "src"))
    from posegraph.retarget.ffxiv import retarget

    A, B = np.load(OUT / f"{a}.npz"), np.load(OUT / f"{b}.npz")
    worst = {"joints_mm": 0.0, "vertices_mm": 0.0, "keypoints_px": 0.0, "rots_deg": 0.0, "ffxiv_bone_deg": 0.0,
             "ffxiv_bone_mean_deg": 0.0}
    for i in range(n):
        g = lambda z, k: z[f"{i}_{k}"]
        worst["joints_mm"] = max(worst["joints_mm"], float(np.abs(g(A, "joint_coords") - g(B, "joint_coords")).max() * 1000))
        worst["vertices_mm"] = max(worst["vertices_mm"], float(np.abs(g(A, "vertices") - g(B, "vertices")).max() * 1000))
        worst["keypoints_px"] = max(worst["keypoints_px"], float(np.abs(g(A, "keypoints_2d") - g(B, "keypoints_2d")).max()))
        worst["rots_deg"] = max(worst["rots_deg"], float(_angle_deg(g(A, "joint_global_rots"), g(B, "joint_global_rots")).max()))

        def rt(z):
            return retarget(g(z, "joint_global_rots"), g(z, "joint_coords"), g(z, "rest_joint_coords"), g(z, "rest_joint_rots"),
                            vertices_cam=g(z, "vertices"),
                            photo=(float(g(z, "focal_length")), g(z, "cam_t"), tuple(int(v) for v in g(z, "image_size"))))
        ra, rb = rt(A), rt(B)
        diffs = [np.degrees((R.from_quat(ra.bones[k][1]).inv() * R.from_quat(rb.bones[k][1])).magnitude()) for k in ra.bones]
        worst["ffxiv_bone_deg"] = max(worst["ffxiv_bone_deg"], float(max(diffs)))
        worst["ffxiv_bone_mean_deg"] = max(worst["ffxiv_bone_mean_deg"], float(np.mean(diffs)))
    return {k: round(v, 3) for k, v in worst.items()}


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("images", nargs="*")
    ap.add_argument("--worker")
    ap.add_argument("--only", help="只跑这些配置（逗号分隔）")
    args = ap.parse_args()
    if args.worker:
        worker(args.worker, args.images)
        return

    env = {**os.environ, "PYTHONIOENCODING": "utf-8"}
    for name in args.only.split(",") if args.only else CONFIGS:
        print(f"== {name}", flush=True)
        r = subprocess.run([sys.executable, __file__, "--worker", name, *args.images], cwd=ROOT, env=env)
        if r.returncode != 0:
            sys.exit(f"{name} 失败")
        print("  ", (OUT / f"{name}.json").read_text(encoding="utf-8"), flush=True)
    for a, b, label in COMPARISONS:
        if (OUT / f"{a}.npz").exists() and (OUT / f"{b}.npz").exists():
            print(f"{label}：{compare(a, b, len(args.images))}", flush=True)


if __name__ == "__main__":
    main()
