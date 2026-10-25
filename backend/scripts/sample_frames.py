"""连续采样游戏内角色的骨骼姿势（用于拟合辅助骨联动规律）。

    .venv/Scripts/python scripts/sample_frames.py [--actor N] [--seconds 30] [--hz 10]

角色需处于播放动画状态（未冻结）。采样期间让角色做抬手、转腕、弯肘等动作。
输出 outputs/samples/<时间>.jsonl，每行一帧 Brio pose JSON 的 Bones 字段。
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
from pathlib import Path

ROOT = Path(os.path.abspath(__file__)).parents[1]
sys.path.insert(0, str(ROOT / "src"))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--actor", type=int)
    ap.add_argument("--seconds", type=float, default=30)
    ap.add_argument("--hz", type=float, default=10)
    args = ap.parse_args()

    from posegraph.bridge import BridgeClient

    client = BridgeClient()
    out = ROOT / "outputs" / "samples" / f"{time.strftime('%Y%m%d-%H%M%S')}.jsonl"
    out.parent.mkdir(parents=True, exist_ok=True)
    frames, last = 0, None
    end = time.monotonic() + args.seconds
    with out.open("w", encoding="utf-8") as f:
        while time.monotonic() < end:
            t0 = time.monotonic()
            bones = json.loads(client.get_pose(args.actor))["Bones"]
            if bones != last:  # 冻结时帧不变，跳过重复帧
                f.write(json.dumps(bones, ensure_ascii=False) + "\n")
                frames += 1
                last = bones
            time.sleep(max(0.0, 1 / args.hz - (time.monotonic() - t0)))
    print(f"采样 {frames} 个不同帧 → {out}")
    if frames < 10:
        print("帧数过少：角色可能处于冻结状态，请在 Brio 里恢复动画播放后重试")


if __name__ == "__main__":
    main()
