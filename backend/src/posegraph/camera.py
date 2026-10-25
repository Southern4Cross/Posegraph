"""照片镜头 → GPose 相机。

1. photo_camera_in_world：SAM 3D Body 给出拍摄相机相对人物的位置（cam_t）与焦距，
   经站姿重力校正、两套骨架的髋部对齐与体型比例缩放，再经角色的模型世界变换，得到 FFXIV 世界中的目标机位
2. solve_gpose_camera：GPose 相机是绕注视点的轨道相机，内部公式（俯仰随距离变化等）不透明，
   因此用 Posegraph 读回的视图/投影矩阵做闭环求解：
   - Angle(水平, 俯仰) + Distance 决定相机位置
   - Pan(水平, 垂直) + Roll 让相机原地转动视线（实测只带来几厘米位移）
   - Zoom 只改视角（非线性）
   三组分别用有限差分高斯-牛顿迭代，交替两轮。
"""

from __future__ import annotations

import time
from dataclasses import dataclass

import numpy as np
from scipy.spatial.transform import Rotation as R

from .bridge import BridgeClient

_FLIP = np.array([1.0, -1.0, -1.0])  # 相机系 ↔ MHR 模型系


@dataclass
class CameraTarget:
    position: np.ndarray  # 世界坐标
    forward: np.ndarray   # 视线方向（单位向量）
    up: np.ndarray        # 上方向（单位向量）
    fov_y_deg: float


@dataclass
class CameraState:
    position: np.ndarray
    forward: np.ndarray
    up: np.ndarray
    fov_y_deg: float
    raw: dict


def photo_camera_in_world(z, rt, model_position: np.ndarray, model_rotation: np.ndarray,
                          model_scale: np.ndarray, mhr_names: list[str]) -> CameraTarget:
    """z：image_to_pose 生成的 npz；rt：retarget 结果（需含 all_positions / upright_rotation）；
    model_*：角色模型的世界变换（Brio 姿势 JSON 的 ModelAbsoluteValues，旋转为 xyzw）。"""
    up_rot = R.from_quat(rt.upright_rotation) if rt.upright_rotation is not None else R.identity()
    mc = up_rot.apply(z["joint_coords"] * _FLIP)
    J = lambda n: mc[mhr_names.index(n)]
    cam_m = up_rot.apply(-z["cam_t"] * _FLIP)
    P = rt.all_positions
    mhr_hip, ff_hip = (J("l_upleg") + J("r_upleg")) / 2, (P["j_asi_a_l"] + P["j_asi_a_r"]) / 2
    # 体型比例：两套骨架的髋→颈长度之比，让机位按 FFXIV 角色的大小缩放、构图保持一致
    scale = np.linalg.norm(P["j_kubi"] - ff_hip) / np.linalg.norm(J("c_neck") - mhr_hip)
    cam_ff = ff_hip + scale * (cam_m - mhr_hip)

    q = R.from_quat(model_rotation)
    w, h = (int(v) for v in z["image_size"])
    return CameraTarget(
        position=model_position + q.apply(model_scale * cam_ff),
        forward=q.apply(up_rot.apply([0.0, 0.0, -1.0])),
        up=q.apply(up_rot.apply([0.0, 1.0, 0.0])),
        fov_y_deg=float(np.degrees(2 * np.arctan(h / 2 / float(z["focal_length"])))),
    )


def read_state(client: BridgeClient) -> CameraState:
    c = client.get_camera()["camera"]
    V = np.array(c["view"])  # 行向量约定：3×3 的列为相机轴（右、上、后）在世界中的方向
    fov = float(np.degrees(2 * np.arctan(1 / c["projection"][1][1])))
    return CameraState(np.array(c["position"]), -V[:3, 2], V[:3, 1], fov, c)


def _angle_between(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.arccos(np.clip(a @ b / np.linalg.norm(a) / np.linalg.norm(b), -1, 1)))


def solve_gpose_camera(client: BridgeClient, target: CameraTarget, rounds: int = 2,
                       settle: float = 0.15, log=print) -> dict:
    state = read_state(client)
    params = {"ax": state.raw["angle"][0], "ay": state.raw["angle"][1], "dist": state.raw["distance"],
              "px": state.raw["pan"][0], "py": state.raw["pan"][1], "roll": state.raw["roll"], "zoom": state.raw["zoom"]}

    def apply(p: dict) -> CameraState:
        client.set_camera(angle=[p["ax"], p["ay"]], distance=p["dist"], pan=[p["px"], p["py"]],
                          roll=p["roll"], zoom=p["zoom"], delimit=True)
        time.sleep(settle)
        return read_state(client)

    # 初值：从当前注视点指向目标位置的水平方位与距离
    look_at = np.array(state.raw["lookAt"])
    d = target.position - look_at
    params["ax"] = float(np.arctan2(d[0], d[2]))
    params["dist"] = float(np.linalg.norm(d))
    params.update(px=0.0, py=0.0, roll=0.0)

    def residual_position(s: CameraState):
        return s.position - target.position

    def residual_orientation(s: CameraState):
        return np.concatenate([s.forward - target.forward, (s.up - target.up) * 0.5])

    def residual_fov(s: CameraState):
        return np.array([s.fov_y_deg - target.fov_y_deg])

    def gauss_newton(keys, residual_fn, steps, iters=6, tol=1e-3):
        nonlocal params
        s = apply(params)
        r = residual_fn(s)
        for _ in range(iters):
            if np.linalg.norm(r) < tol:
                break
            J = np.zeros((len(r), len(keys)))
            for j, k in enumerate(keys):
                probe = dict(params)
                probe[k] += steps[j]
                J[:, j] = (residual_fn(apply(probe)) - r) / steps[j]
            delta, *_ = np.linalg.lstsq(J, -r, rcond=None)
            trial = dict(params)
            for j, k in enumerate(keys):
                trial[k] += float(delta[j])
            s_trial = apply(trial)
            r_trial = residual_fn(s_trial)
            if np.linalg.norm(r_trial) < np.linalg.norm(r):
                params, s, r = trial, s_trial, r_trial
            else:  # 步长过大：减半再试一次
                for j, k in enumerate(keys):
                    trial[k] = params[k] + float(delta[j]) / 2
                s_half = apply(trial)
                r_half = residual_fn(s_half)
                if np.linalg.norm(r_half) < np.linalg.norm(r):
                    params, s, r = trial, s_half, r_half
                else:
                    break
        apply(params)
        return s

    # 俯仰初值：Angle.y 到实际俯仰的映射非线性，且某些区间相机高度会饱和（梯度为零，迭代推不动），先粗扫
    best = None
    for ay in np.arange(-1.4, 1.41, 0.1):
        params["ay"] = float(ay)
        err = np.linalg.norm(residual_position(apply(params)))
        if best is None or err < best[0]:
            best = (err, float(ay))
    params["ay"] = best[1]

    for i in range(rounds):
        s = gauss_newton(["ax", "ay", "dist"], residual_position, [0.02, 0.02, 0.05])
        s = gauss_newton(["px", "py", "roll"], residual_orientation, [0.02, 0.02, 0.02])
        s = gauss_newton(["zoom"], residual_fov, [0.02], iters=8, tol=0.05)
        log(f"第 {i + 1} 轮：位置误差 {np.linalg.norm(s.position - target.position) * 100:.1f}cm，"
            f"视线误差 {np.degrees(_angle_between(s.forward, target.forward)):.2f}°，"
            f"上方向误差 {np.degrees(_angle_between(s.up, target.up)):.2f}°，"
            f"视角 {s.fov_y_deg:.1f}°/{target.fov_y_deg:.1f}°")
    final = read_state(client)
    return {
        "params": params,
        "position_error_cm": float(np.linalg.norm(final.position - target.position) * 100),
        "forward_error_deg": float(np.degrees(_angle_between(final.forward, target.forward))),
        "up_error_deg": float(np.degrees(_angle_between(final.up, target.up))),
        "fov_deg": final.fov_y_deg,
        "target_fov_deg": target.fov_y_deg,
    }


_FRAMING_PAIRS = [("j_kao", "c_head"), ("j_ude_a_l", "l_uparm"), ("j_ude_a_r", "r_uparm"), ("j_te_l", "l_wrist"),
                  ("j_te_r", "r_wrist"), ("j_asi_a_l", "l_upleg"), ("j_asi_a_r", "r_upleg"), ("j_asi_c_l", "l_lowleg"),
                  ("j_asi_c_r", "r_lowleg"), ("j_asi_d_l", "l_foot"), ("j_asi_d_r", "r_foot")]


def framing_error(client: BridgeClient, z, pose, mhr_names: list[str], actor: int | None = None) -> float:
    """构图检查：游戏相机下角色关节的屏幕位置 vs 照片中对应关节，返回平均偏差（占画面高度的比例）。
    只统计照片画面内的关节（半身照里画面外的腿是模型猜的）。
    坐标以画面半高为单位、原点在中心，与窗口/照片的宽高比无关。pose 为游戏当前姿势（BrioPose）。"""
    cam = client.get_camera()["camera"]
    V, Pm = np.array(cam["view"]), np.array(cam["projection"])
    aspect = Pm[1][1] / Pm[0][0]
    vec = lambda s: np.array([float(x) for x in s.split(",")])
    m = pose.raw["ModelAbsoluteValues"]
    mp, mq, ms = vec(m["Position"]), R.from_quat(vec(m["Rotation"])), vec(m["Scale"])
    W, H = (int(v) for v in z["image_size"])
    f = float(z["focal_length"])
    jc = z["joint_coords"] + z["cam_t"]
    diffs = []
    for ff, mh in _FRAMING_PAIRS:
        clip = np.append(mp + mq.apply(ms * pose.get(ff).position), 1) @ V @ Pm
        game = np.array([clip[0] / clip[3] * aspect, clip[1] / clip[3]])
        p = jc[mhr_names.index(mh)]
        photo = np.array([f * p[0] / p[2], -f * p[1] / p[2]]) / (H / 2)
        if abs(photo[0]) > W / H or abs(photo[1]) > 1:
            continue
        diffs.append(np.linalg.norm(game - photo))
    return float(np.mean(diffs) / 2)
