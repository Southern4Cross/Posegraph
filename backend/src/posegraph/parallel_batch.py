"""批量识别的共享模型并行（实验功能，PR #2 的共享模型实现 + 我们的隔离与降级）。

与原串行识别完全分开，出问题时可以整块关掉或删掉：
- 只处理插件标了 parallel 的批量请求；单张识别、预加载、辅助视角等都走 app.py 里原来的串行路径。
- 任何问题都不让识别失败：共享模型加载失败 / 上游源码指纹不符 / 运行时校验不通过时，本进程内停用并行
  （health 里给出原因，插件提示一次），这一张交回串行重试；单张请求出错也交回串行重试。
- 实际路数在模型加载后按余量定：GPU 看剩余显存，CPU 看可用内存和核数（每路至少 2 个线程），
  CPU 模式下按路数分配 PyTorch 线程（每路 = 核数 ÷ 路数），并行结束后恢复。
- 总开关：环境变量 POSEGRAPH_DISABLE_PARALLEL=1，或插件里并行数设为 1。

每多一路的额外占用取自实测（RTX 4060 笔记本 / i7 20 线程，8 张图）：GPU 约 180MB 显存，CPU 约 330MB 内存，
这里按更保守的值估算，并给游戏和系统留出余量。
"""

from __future__ import annotations

import ctypes
import sys
import threading
import time

from .i18n import tr
from .shared_inference import InferenceFailure, SharedInferencePool

GPU_PER_WORKER_MB = 250
GPU_HEADROOM_MB = 1024
CPU_PER_WORKER_MB = 450
CPU_HEADROOM_MB = 3072
MIN_THREADS_PER_WORKER = 2


def available_ram_mb() -> int | None:
    """系统可用内存（MB）；拿不到返回 None。"""
    if sys.platform == "win32":
        class _Status(ctypes.Structure):
            _fields_ = [("length", ctypes.c_ulong), ("load", ctypes.c_ulong), ("total", ctypes.c_ulonglong),
                        ("avail", ctypes.c_ulonglong), ("page_total", ctypes.c_ulonglong), ("page_avail", ctypes.c_ulonglong),
                        ("virt_total", ctypes.c_ulonglong), ("virt_avail", ctypes.c_ulonglong), ("ext", ctypes.c_ulonglong)]
        status = _Status(length=ctypes.sizeof(_Status))
        if ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(status)):
            return int(status.avail // (1024 * 1024))
        return None
    try:
        import os
        return int(os.sysconf("SC_AVPHYS_PAGES") * os.sysconf("SC_PAGE_SIZE") // (1024 * 1024))
    except (ValueError, OSError, AttributeError):
        return None


def budget(requested: int, device: str, free_vram_mb: int | None, avail_ram_mb: int | None, cores: int) -> tuple[int, str]:
    """按余量算实际路数，返回 (路数, 说明)。拿不到余量时按请求值，但 CPU 仍受核数限制。"""
    limit, notes = requested, []
    if device == "cuda" and free_vram_mb is not None:
        fit = 1 + max(0, (free_vram_mb - GPU_HEADROOM_MB) // GPU_PER_WORKER_MB)
        if fit < limit:
            limit, notes = fit, [tr("剩余显存 {free} MB，只够 {n} 路", free=free_vram_mb, n=fit)]
    if device != "cuda":
        if avail_ram_mb is not None:
            fit = 1 + max(0, (avail_ram_mb - CPU_HEADROOM_MB) // CPU_PER_WORKER_MB)
            if fit < limit:
                limit, notes = fit, [tr("可用内存 {free} MB，只够 {n} 路", free=avail_ram_mb, n=fit)]
        by_cores = max(1, cores // MIN_THREADS_PER_WORKER)
        if by_cores < limit:
            limit, notes = by_cores, [tr("CPU {cores} 线程，最多 {n} 路", cores=cores, n=by_cores)]
    return max(1, min(requested, limit)), "；".join(notes)


def _is_runtime_problem(error: BaseException) -> bool:
    """共享模型本身不可用（而不是这张图的问题）：加载失败、上游源码不符、运行时校验不通过。"""
    cause = error.__cause__ or error
    return (isinstance(error, InferenceFailure) and error.status == 503) or type(cause).__name__ == "UnsupportedSharedRuntime"


class ParallelBatch:
    def __init__(self, requested: int, config: dict, disabled: str | None = None):
        self.requested = requested
        self.config = config          # 设备、精度等，与串行模型一致
        self.disabled = disabled      # 停用原因（本进程内有效）
        self.effective = 1
        self.note = ""
        self._pool: SharedInferencePool | None = None
        self._active = 0
        self._lock = threading.RLock()
        self._default_threads: int | None = None

    def wanted(self, parallel: bool) -> bool:
        return parallel and self.requested > 1 and self.disabled is None

    @property
    def loaded(self) -> bool:
        pool = self._pool
        return pool is not None and any(s["loaded"] for s in pool.states)

    @property
    def active(self) -> int:
        return self._active

    def _tune(self, owner) -> int:
        """模型加载后定实际路数；CPU 模式按路数分配 PyTorch 线程。"""
        import torch

        device = getattr(owner, "active_device", None) or "cpu"
        if self._default_threads is None:
            self._default_threads = torch.get_num_threads()
        cores = self._default_threads
        free_vram = None
        if device == "cuda":
            from .gpu import query_gpu
            gpu = query_gpu(max_age=0)
            free_vram = gpu["freeMB"] if gpu else None
        self.effective, self.note = budget(self.requested, device, free_vram, available_ram_mb(), cores)
        if device != "cuda":
            torch.set_num_threads(max(1, cores // self.effective))
        return self.effective

    def _restore_threads(self) -> None:
        if self._default_threads is not None:
            import torch
            torch.set_num_threads(self._default_threads)

    def estimate(self, rgb, language: str):
        """并行识别一张；返回 None 表示交回串行（共享模型不可用时已停用并行）。"""
        with self._lock:
            if self.disabled is not None:
                return None
            if self._pool is None:
                self._pool = SharedInferencePool(self.requested, self.config, on_loaded=self._tune)
            pool = self._pool
            self._active += 1
        try:
            return pool.estimate(rgb, language=language)
        except Exception as error:
            if _is_runtime_problem(error):
                with self._lock:
                    self.disabled = self.disabled or str(error)   # 已含原始异常类型，如「模型加载失败：UnsupportedSharedRuntime: …」
            return None
        finally:
            with self._lock:
                self._active -= 1
            if self.disabled is not None:
                self.close_if_idle()   # 停用后最后一个在途请求结束时关掉共享模型

    def disable(self, reason: str) -> None:
        with self._lock:
            if self.disabled is None:
                self.disabled = reason
        self.close_if_idle()

    def close_if_idle(self) -> bool:
        """没有在途并行请求时关掉共享模型（释放、换回串行前调用）。返回是否已经没有共享模型。"""
        with self._lock:
            if self._active or self._pool is None:
                return self._pool is None
            pool, self._pool = self._pool, None
        try:
            pool.close()
        finally:
            self._restore_threads()
            self.effective, self.note = 1, ""
        return True

    def configure(self, requested: int, config: dict) -> None:
        """插件改了并行数或设备：下次批量按新设置重建。"""
        with self._lock:
            self.requested, self.config = requested, config
        self.close_if_idle()

    def info(self) -> dict:
        pool = self._pool
        return {"requested": self.requested, "effective": self.effective if pool is not None else None,
                "note": self.note, "disabled": self.disabled, "active": self._active,
                "loaded": self.loaded, "workers": pool.states if pool is not None else []}
