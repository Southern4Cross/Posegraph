"""照片镜头 → GPose 相机。

1. photo_camera_in_world：SAM 3D Body 给出拍摄相机相对人物的位置（cam_t）与焦距，
   经站姿重力校正、两套骨架的髋部对齐与体型比例缩放，再经角色的模型世界变换，得到 FFXIV 世界中的目标机位
2. solve_gpose_camera：GPose 相机是绕注视点的轨道相机，内部公式（俯仰随距离变化等）不透明，
   因此用 Posegraph 读回的视图/投影矩阵做闭环求解：
   - Angle(水平, 俯仰) + Distance 决定相机位置
   - Pan(水平, 垂直) + Roll 让相机原地转动视线（实测只带来几厘米位移）
   - Zoom 只改视角（非线性）
   三组分别用有限差分高斯-牛顿迭代，交替两轮。
   视角按"包含"照片画面取：游戏窗口（多为 16:9 / 16:10 横屏）比照片宽时按照片高度对齐，比照片窄时按宽度对齐。
3. fit_in_frame：照片里在画面内的部位（头顶、手指、脚尖等）在游戏里也必须在画面内，否则先拉宽视角、
   视角到上限后再沿视线后退（照片构图很满、机位误差、窗口比例不同时会出画）。
"""

from __future__ import annotations

import time
from dataclasses import dataclass, replace

import numpy as np
from scipy.spatial.transform import Rotation as R

from .bridge import BridgeClient

_FLIP = np.array([1.0, -1.0, -1.0])  # 相机系 ↔ MHR 模型系


@dataclass
class CameraTarget:
    position: np.ndarray  # 世界坐标
    forward: np.ndarray   # 视线方向（单位向量）
    up: np.ndarray        # 上方向（单位向量）
    fov_y_deg: float      # 照片的垂直视角
    fov_x_deg: float | None = None   # 照片的水平视角（按窗口比例决定按高还是按宽对齐）

    def fov_for_aspect(self, aspect: float) -> float:
        """能完整包含照片画面的垂直视角（aspect 为游戏窗口宽/高）。"""
        tan_y = np.tan(np.radians(self.fov_y_deg) / 2)
        if self.fov_x_deg is not None:
            tan_y = max(tan_y, np.tan(np.radians(self.fov_x_deg) / 2) / aspect)
        return float(np.degrees(2 * np.arctan(tan_y)))


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
        fov_x_deg=float(np.degrees(2 * np.arctan(w / 2 / float(z["focal_length"])))),
    )


def read_state(client: BridgeClient) -> CameraState:
    c = client.get_camera()["camera"]
    V = np.array(c["view"])  # 行向量约定：3×3 的列为相机轴（右、上、后）在世界中的方向
    fov = float(np.degrees(2 * np.arctan(1 / c["projection"][1][1])))
    return CameraState(np.array(c["position"]), -V[:3, 2], V[:3, 1], fov, c)


def _angle_between(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.arccos(np.clip(a @ b / np.linalg.norm(a) / np.linalg.norm(b), -1, 1)))


def _aspect(cam: dict) -> float:
    return float(cam["projection"][1][1] / cam["projection"][0][0])


def solve_gpose_camera(client: BridgeClient, target: CameraTarget, rounds: int = 2,
                       settle: float = 0.15, log=print, init: dict | None = None,
                       stages: tuple[str, ...] = ("position", "orientation", "fov")) -> dict:
    """init：从上次求解的参数继续（跳过初值估计与俯仰粗扫）；stages：只跑其中几组。"""
    state = read_state(client)
    want_fov = target.fov_for_aspect(_aspect(state.raw))
    params = {"ax": state.raw["angle"][0], "ay": state.raw["angle"][1], "dist": state.raw["distance"],
              "px": state.raw["pan"][0], "py": state.raw["pan"][1], "roll": state.raw["roll"], "zoom": state.raw["zoom"]}

    def apply(p: dict) -> CameraState:
        client.set_camera(angle=[p["ax"], p["ay"]], distance=p["dist"], pan=[p["px"], p["py"]],
                          roll=p["roll"], zoom=p["zoom"], delimit=True)
        time.sleep(settle)
        return read_state(client)

    if init is not None:
        params.update(init)
    else:
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
        return np.array([s.fov_y_deg - want_fov])

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

    if init is None:
        # 俯仰初值：Angle.y 到实际俯仰的映射非线性，且某些区间相机高度会饱和（梯度为零，迭代推不动），先粗扫
        best = None
        for ay in np.arange(-1.4, 1.41, 0.1):
            params["ay"] = float(ay)
            err = np.linalg.norm(residual_position(apply(params)))
            if best is None or err < best[0]:
                best = (err, float(ay))
        params["ay"] = best[1]

    s = state
    for i in range(rounds):
        if "position" in stages:
            s = gauss_newton(["ax", "ay", "dist"], residual_position, [0.02, 0.02, 0.05])
        if "orientation" in stages:
            s = gauss_newton(["px", "py", "roll"], residual_orientation, [0.02, 0.02, 0.02])
        if "fov" in stages:
            s = gauss_newton(["zoom"], residual_fov, [0.02], iters=8, tol=0.05)
        log(f"第 {i + 1} 轮：位置误差 {np.linalg.norm(s.position - target.position) * 100:.1f}cm，"
            f"视线误差 {np.degrees(_angle_between(s.forward, target.forward)):.2f}°，"
            f"上方向误差 {np.degrees(_angle_between(s.up, target.up)):.2f}°，"
            f"视角 {s.fov_y_deg:.1f}°/{want_fov:.1f}°")
    final = read_state(client)
    return {
        "params": params,
        "position_error_cm": float(np.linalg.norm(final.position - target.position) * 100),
        "forward_error_deg": float(np.degrees(_angle_between(final.forward, target.forward))),
        "up_error_deg": float(np.degrees(_angle_between(final.up, target.up))),
        "fov_deg": final.fov_y_deg,
        "target_fov_deg": want_fov,
    }


_FRAMING_PAIRS = [("j_kao", "c_head"), ("j_ude_a_l", "l_uparm"), ("j_ude_a_r", "r_uparm"), ("j_te_l", "l_wrist"),
                  ("j_te_r", "r_wrist"), ("j_asi_a_l", "l_upleg"), ("j_asi_a_r", "r_upleg"), ("j_asi_c_l", "l_lowleg"),
                  ("j_asi_c_r", "r_lowleg"), ("j_asi_d_l", "l_foot"), ("j_asi_d_r", "r_foot")]


def _world_points(pose, names) -> np.ndarray:
    """BrioPose 骨骼的世界坐标（经 ModelAbsoluteValues 的模型变换）。"""
    vec = lambda s: np.array([float(x) for x in s.split(",")])
    m = pose.raw["ModelAbsoluteValues"]
    mp, mq, ms = vec(m["Position"]), R.from_quat(vec(m["Rotation"])), vec(m["Scale"])
    return np.array([mp + mq.apply(ms * pose.get(n).position) for n in names]).reshape(-1, 3)


def _camera_space(cam: dict, points: np.ndarray):
    """返回 (x, y, 深度, tan(垂直半视角), 宽高比)；x 右、y 上，深度沿视线为正。"""
    V = np.array(cam["view"])
    v = np.c_[points, np.ones(len(points))] @ V
    return v[:, 0], v[:, 1], -v[:, 2], 1 / float(cam["projection"][1][1]), _aspect(cam)


def _photo_tan(z, p: np.ndarray) -> np.ndarray:
    """照片相机系的点 → 视线正切 (右, 上)。"""
    return np.array([p[0] / p[2], -p[1] / p[2]])


def _in_photo(z, p: np.ndarray) -> bool:
    W, H = (int(v) for v in z["image_size"])
    t = _photo_tan(z, p) * float(z["focal_length"])
    return p[2] > 0 and abs(t[0]) <= W / 2 and abs(t[1]) <= H / 2


def framing_error(client: BridgeClient, z, pose, mhr_names: list[str], actor: int | None = None) -> float:
    """构图检查：游戏相机下角色关节的视线方向 vs 照片中对应关节，返回平均偏差（占照片画面高度的比例）。
    只统计照片画面内的关节（半身照里画面外的腿是模型猜的）。
    按视线正切比较，与窗口比例、为完整入画拉宽的视角无关。pose 为游戏当前姿势（BrioPose）。"""
    cam = client.get_camera()["camera"]
    H = int(z["image_size"][1])
    f = float(z["focal_length"])
    jc = z["joint_coords"] + z["cam_t"]
    x, y, d, _, _ = _camera_space(cam, _world_points(pose, [ff for ff, _ in _FRAMING_PAIRS]))
    diffs = []
    for i, (_, mh) in enumerate(_FRAMING_PAIRS):
        p = jc[mhr_names.index(mh)]
        if not _in_photo(z, p) or d[i] <= 0:
            continue
        diffs.append(np.linalg.norm(np.array([x[i], y[i]]) / d[i] - _photo_tan(z, p)))
    return float(np.mean(diffs) * f / H) if diffs else 0.0


# 完整入画：照片里在画面内的 MHR 关节 → 游戏里必须在画面内的骨骼（前缀 * 表示同侧所有手指骨）
_FIT_GROUPS = [("c_head", ["j_kao"]),
               ("l_uparm", ["j_ude_a_l"]), ("r_uparm", ["j_ude_a_r"]),
               ("l_lowarm", ["j_ude_b_l"]), ("r_lowarm", ["j_ude_b_r"]),
               ("l_wrist", ["j_te_l", "*_l"]), ("r_wrist", ["j_te_r", "*_r"]),
               ("l_upleg", ["j_asi_a_l"]), ("r_upleg", ["j_asi_a_r"]),
               ("l_lowleg", ["j_asi_c_l"]), ("r_lowleg", ["j_asi_c_r"]),
               ("l_foot", ["j_asi_d_l", "j_asi_e_l"]), ("r_foot", ["j_asi_d_r", "j_asi_e_r"])]
_FINGERS = ("j_hito_", "j_ko_", "j_kusu_", "j_naka_", "j_oya_", "iv_")
# 头顶在 j_kao 上方约 1.6 倍颈长（MHR 静止网格实测：头关节→头顶 19cm，颈→头 11.8cm）
HEAD_TOP_PER_NECK = 1.6
FIT_MARGIN = 0.04   # 离画面边缘至少留半高/半宽的 4%


def fit_points(z, pose, mhr_names: list[str]) -> np.ndarray:
    """照片里在画面内的部位对应的游戏世界坐标（含估算的头顶）。"""
    jc = z["joint_coords"] + z["cam_t"]
    names: list[str] = []
    for mh, bones in _FIT_GROUPS:
        if not _in_photo(z, jc[mhr_names.index(mh)]):
            continue
        for b in bones:
            if b.startswith("*"):
                names += [n for n in pose.raw["Bones"] if n.endswith(b[1:]) and n.startswith(_FINGERS)]
            elif pose.get(b) is not None:
                names.append(b)
    points = list(_world_points(pose, names))
    # 头顶：照片里头顶在画面内才要求（取 MHR 静止网格最高点对应的顶点）
    has_mesh = "rest_vertices" in z and "vertices" in z
    top = int(np.argmax(z["rest_vertices"][:, 1])) if has_mesh else -1
    head_in = _in_photo(z, z["vertices"][top] + z["cam_t"]) if has_mesh else _in_photo(z, jc[mhr_names.index("c_head")])
    if head_in and pose.get("j_kao") is not None and pose.get("j_kubi") is not None:
        kubi, kao = _world_points(pose, ["j_kubi", "j_kao"])
        points.append(kao + (kao - kubi) * HEAD_TOP_PER_NECK)
    return np.array(points).reshape(-1, 3)


def frame_overflow(cam: dict, points: np.ndarray, margin: float = FIT_MARGIN) -> float:
    """所需点的最大屏幕范围 / 允许范围：≤1 都在画面内；有点在相机后方时为 inf。"""
    if len(points) == 0:
        return 0.0
    x, y, d, tan_y, aspect = _camera_space(cam, points)
    if (d <= 0.05).any():
        return float("inf")
    return float(np.max(np.maximum(np.abs(x) / (d * tan_y * aspect), np.abs(y) / (d * tan_y))) / (1 - margin))


def _dolly_distance(cam: dict, points: np.ndarray, margin: float) -> float:
    """沿视线后退多少米能让所有点进入画面（后退只增加深度，屏幕坐标按 1/深度 缩小）。"""
    x, y, d, tan_y, aspect = _camera_space(cam, points)
    need = np.maximum(np.abs(x) / (tan_y * aspect), np.abs(y) / tan_y) / (1 - margin)
    return float(max(0.0, (need - d).max()))


def fit_in_frame(client: BridgeClient, target: CameraTarget, solved: dict, points: np.ndarray,
                 margin: float = FIT_MARGIN, settle: float = 0.15, log=print) -> dict:
    """照片里在画面内的部位在游戏里出画时：先拉宽视角（机位、透视不变），到上限后沿视线后退。"""
    cam = client.get_camera()["camera"]
    before = frame_overflow(cam, points, margin)
    result = {"action": "none", "overflow": round(before, 3), "dolly_cm": 0.0}
    if before <= 1:
        return result
    params = solved["params"]
    if np.isfinite(before):
        tan_y = 1 / float(cam["projection"][1][1])
        fov = float(np.degrees(2 * np.arctan(tan_y * before * 1.02)))
        wider = replace(target, fov_y_deg=fov, fov_x_deg=None)
        params = solve_gpose_camera(client, wider, rounds=1, settle=settle, log=log, init=params, stages=("fov",))["params"]
        cam = client.get_camera()["camera"]
        result.update(action="zoom", fov_deg=round(float(np.degrees(2 * np.arctan(1 / cam["projection"][1][1]))), 1))
        log(f"完整入画：视角拉宽到 {result['fov_deg']}°")
    # 视角到上限仍出画：沿视线后退（求解有误差，最多三次）
    for _ in range(3):
        if frame_overflow(cam, points, margin) <= 1:
            break
        state = read_state(client)
        step = _dolly_distance(cam, points, margin) * 1.05 + 0.02
        back = replace(target, position=state.position - state.forward * step,
                       fov_y_deg=state.fov_y_deg, fov_x_deg=None)
        params = solve_gpose_camera(client, back, rounds=1, settle=settle, log=log, init=params)["params"]
        cam = client.get_camera()["camera"]
        result["action"] = "dolly"
        result["dolly_cm"] = round(result["dolly_cm"] + step * 100, 1)
        log(f"完整入画：后退 {step * 100:.0f}cm")
    result["overflow_after"] = round(frame_overflow(cam, points, margin), 3)
    return result
