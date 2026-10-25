"""一键流程：照片 → 识别 → 叠加预览 → 推送姿势 →（可选）复刻镜头 / 导出 Blender 对比数据。

    .venv/Scripts/python scripts/photo_to_game.py <照片> [--name 名称] [--actor N]
                                                  [--camera] [--offline] [--blender]

产物都在 outputs/ 下：<名称>.npz（识别结果）、<名称>.pose、<名称>.overlay.png、<名称>.sent.pose，
推送前的姿势备份在 outputs/backups/。--offline 只识别不连游戏；--blender 额外生成 <名称>.blender.json。
"""

from __future__ import annotations

import argparse
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
SCRIPTS = ROOT / "scripts"


def run(step: str, *args: str) -> None:
    print(f"\n== {step}", flush=True)
    result = subprocess.run([sys.executable, str(SCRIPTS / args[0]), *args[1:]], cwd=ROOT,
                            env={**os.environ, "PYTHONIOENCODING": "utf-8"})
    if result.returncode != 0:
        sys.exit(f"{step} 失败（退出码 {result.returncode}）")


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("image", type=Path)
    ap.add_argument("--name", help="产物名称，缺省为照片文件名")
    ap.add_argument("--actor", type=int, help="ObjectIndex，缺省为 GPose 当前选中目标")
    ap.add_argument("--camera", action="store_true", help="同时复刻照片镜头（默认不动 GPose 镜头）")
    ap.add_argument("--offline", action="store_true", help="只识别和生成预览，不连接游戏")
    ap.add_argument("--no-ground", action="store_true", help="不自动贴地（跳跃、腾空的照片用）")
    ap.add_argument("--neck", type=float, default=1.0, help="颈部幅度 0~1，缩小颈/头转动（插画可用 0.5）")
    ap.add_argument("--blender", action="store_true", help="额外导出 Blender 对比数据")
    args = ap.parse_args()

    image = args.image.resolve()
    if not image.exists():
        sys.exit(f"找不到照片：{image}")
    name = args.name or image.stem
    out = ROOT / "outputs"
    npz = out / f"{name}.npz"

    ground = (["--no-ground"] if args.no_ground else []) + (["--neck", str(args.neck)] if args.neck != 1.0 else [])
    run("1/4 识别姿势", "image_to_pose.py", str(image), "-o", str(out / f"{name}.pose"), "--npz", str(npz), *ground)
    run("2/4 叠加预览", "preview_overlay.py", str(image), str(npz), "-o", str(out / f"{name}.overlay.png"))
    if args.blender:
        run("Blender 数据", "export_blender_compare.py", str(npz), "-o", str(out / f"{name}.blender.json"))
    if args.offline:
        print(f"\n完成（离线）。预览图：{out / f'{name}.overlay.png'}")
        return

    push = ["push_pose.py", "apply", str(npz), *ground]
    if args.actor is not None:
        push += ["--actor", str(args.actor)]
    if args.camera:
        push.append("--camera")
    run("3/4 推送姿势" + (" + 4/4 复刻镜头" if args.camera else ""), *push)
    print(f"\n完成。预览图：{out / f'{name}.overlay.png'}；还原：push_pose.py restore outputs/backups/<最早的备份>.pose")


if __name__ == "__main__":
    main()
