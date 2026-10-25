"""把识别结果推送到游戏（经 Posegraph → Brio IPC）。

    .venv/Scripts/python scripts/push_pose.py status
    .venv/Scripts/python scripts/push_pose.py apply outputs/dancing.npz [--actor N]
    .venv/Scripts/python scripts/push_pose.py restore outputs/backups/xxx.pose [--actor N]
    .venv/Scripts/python scripts/push_pose.py offline outputs/dancing.npz <参考.pose>   # 不连游戏，用参考姿势离线生成

apply 流程：读取角色当前姿势 → 备份到 outputs/backups → 以游戏实际骨长/缩放重定向 → 推送。
--actor 为 ObjectIndex（见 status 输出），省略时用 GPose 当前选中目标。
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
BACKUPS = ROOT / "outputs" / "backups"


def build_pose(npz: Path, reference_json: str | None, ground: bool = True, neck: float = 1.0):
    """返回 (Brio 姿势 JSON, 重定向结果)。"""
    import numpy as np

    from posegraph.retarget.brio_pose import BrioPose
    from posegraph.retarget.ffxiv import retarget, to_brio_pose_json
    from posegraph.retarget.races import DEFAULT_RACE, RACES, guess_race

    z = np.load(npz)
    ref = BrioPose.loads(reference_json) if reference_json else None
    race = guess_race(ref)[0] if ref is not None else DEFAULT_RACE   # 按游戏内角色骨长判断骨架
    print(f"骨架：{RACES[race].group}（{race}）")
    rt = retarget(z["joint_global_rots"], z["joint_coords"], z["rest_joint_coords"], z["rest_joint_rots"], reference=ref, race=race,
                  vertices_cam=z["vertices"] if "vertices" in z else None,
                  rest_vertices=z["rest_vertices"] if "rest_vertices" in z else None,
                  photo=(float(z["focal_length"]), z["cam_t"], tuple(int(v) for v in z["image_size"])), ground=ground,
                  neck_scale=neck)
    errs = list(rt.segment_error_deg.values())
    print(f"重定向：{len(rt.bones)} 根骨骼，骨段方向误差平均 {sum(errs) / len(errs):.2f}°，最大 {max(errs):.2f}°（末端补偿会让手臂偏离照片角度）")
    print(f"末端补偿：{rt.hand_compensation}")
    if ref is not None:
        hara = ref.get("n_hara")
        if hara is not None:
            print(f"参考姿势：{len(ref.bone_names)} 根骨骼，n_hara 高度 {hara.position[1]:.3f} m")
    return to_brio_pose_json(rt), rt


def main() -> None:
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("status")
    a = sub.add_parser("apply")
    a.add_argument("npz", type=Path)
    a.add_argument("--actor", type=int)
    a.add_argument("--camera", action="store_true", help="同时复刻照片镜头")
    a.add_argument("--no-ground", action="store_true", help="不自动贴地（跳跃、腾空的照片用）")
    a.add_argument("--neck", type=float, default=1.0, help="颈部幅度 0~1（插画可用 0.5）")
    c = sub.add_parser("camera", help="只复刻照片镜头（使用当前游戏姿势作参考）")
    c.add_argument("npz", type=Path)
    c.add_argument("--actor", type=int)
    r = sub.add_parser("restore")
    r.add_argument("pose", type=Path)
    r.add_argument("--actor", type=int)
    o = sub.add_parser("offline")
    o.add_argument("npz", type=Path)
    o.add_argument("reference", type=Path)
    o.add_argument("-o", "--output", type=Path)
    args = ap.parse_args()

    if args.cmd == "offline":
        pose, _ = build_pose(args.npz, args.reference.read_text(encoding="utf-8-sig"))
        out = args.output or args.npz.with_suffix(".ref.pose")
        out.write_text(pose, encoding="utf-8")
        print(f"已写入 {out}")
        return

    import httpx

    from posegraph.bridge import BridgeClient

    client = BridgeClient()
    try:
        status = client.status()
    except httpx.ConnectError:
        sys.exit("连不上 Posegraph（127.0.0.1:42429）：请确认插件已加载，或在游戏里输入 /posegraph on")
    print("状态：", json.dumps(status, ensure_ascii=False))

    if args.cmd == "status":
        print("角色：", json.dumps(client.actors(), ensure_ascii=False))
        return
    if not status.get("inGPose"):
        sys.exit("当前不在 GPose 中")
    if not status.get("brioAvailable"):
        sys.exit("Brio IPC 不可用：请确认 Brio 已启用")

    if args.cmd == "camera":
        _, rt = build_pose(args.npz, client.get_pose(args.actor))
        push_camera(client, args.npz, rt, client.get_pose(args.actor))
        return

    if args.cmd == "restore":
        print("结果：", client.apply_pose(args.pose.read_text(encoding="utf-8-sig"), args.actor))
        return

    current = client.get_pose(args.actor)
    BACKUPS.mkdir(parents=True, exist_ok=True)
    backup = BACKUPS / f"{time.strftime('%Y%m%d-%H%M%S')}_actor{args.actor if args.actor is not None else 'target'}.pose"
    backup.write_text(current, encoding="utf-8")
    print(f"已备份当前姿势：{backup}")

    pose, rt = build_pose(args.npz, current, ground=not args.no_ground, neck=args.neck)
    sent = ROOT / "outputs" / f"{args.npz.stem}.sent.pose"
    sent.write_text(pose, encoding="utf-8")
    print("结果：", client.apply_pose(pose, args.actor))
    # 从差异很大的姿势切换时，Brio 首次应用会残留最多约 1° 偏差（头颈、手指），再应用一次即收敛到 0
    time.sleep(0.5)
    client.apply_pose(pose, args.actor)
    print(f"推送内容已保存：{sent}")
    if args.camera:
        time.sleep(0.5)
        push_camera(client, args.npz, rt, client.get_pose(args.actor))


def push_camera(client, npz: Path, rt, pose_json: str) -> None:
    """按照片的拍摄机位设置 GPose 相机（闭环求解）。"""
    import numpy as np

    from posegraph.camera import framing_error, photo_camera_in_world, solve_gpose_camera
    from posegraph.retarget.brio_pose import BrioPose
    from posegraph.retarget.ffxiv import load_mhr_rest

    model = BrioPose.loads(pose_json).raw["ModelAbsoluteValues"]
    vec = lambda s: np.array([float(x) for x in s.split(",")])
    target = photo_camera_in_world(np.load(npz), rt, vec(model["Position"]), vec(model["Rotation"]),
                                   vec(model["Scale"]), load_mhr_rest()[0])
    print(f"目标机位：位置 {target.position.round(3)}，视角 {target.fov_y_deg:.1f}°")
    result = solve_gpose_camera(client, target)
    print(f"相机结果：位置误差 {result['position_error_cm']:.1f}cm，视线误差 {result['forward_error_deg']:.2f}°，"
          f"上方向误差 {result['up_error_deg']:.2f}°，视角 {result['fov_deg']:.1f}°（目标 {result['target_fov_deg']:.1f}°）")
    err = framing_error(client, np.load(npz), BrioPose.loads(client.get_pose()), load_mhr_rest()[0])
    print(f"构图检查：游戏画面中关节位置与照片平均相差 {err * 100:.1f}% 画面高度")


if __name__ == "__main__":
    main()
