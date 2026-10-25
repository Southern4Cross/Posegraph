"""显卡状态（nvidia-smi）。不初始化 CUDA：在 CPU 模式或模型未加载时调用 torch.cuda 会建出几百 MB 的 CUDA 上下文。"""

from __future__ import annotations

import subprocess
import time

_cache: tuple[float, dict | None] = (0.0, None)


def query_gpu(max_age: float = 2.0) -> dict | None:
    """{name, totalMB, usedMB, freeMB}；没有 N 卡或驱动时返回 None。结果缓存 max_age 秒。"""
    global _cache
    if time.monotonic() - _cache[0] < max_age:
        return _cache[1]
    info = None
    try:
        out = subprocess.run(
            ["nvidia-smi", "--query-gpu=name,memory.total,memory.used,memory.free", "--format=csv,noheader,nounits"],
            capture_output=True, text=True, timeout=5, creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        ).stdout.strip().splitlines()
        if out:
            name, total, used, free = (s.strip() for s in out[0].split(","))
            info = {"name": name, "totalMB": int(total), "usedMB": int(used), "freeMB": int(free)}
    except (OSError, ValueError, subprocess.SubprocessError):
        info = None
    _cache = (time.monotonic(), info)
    return info
