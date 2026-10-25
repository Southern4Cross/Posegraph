"""Process-isolated inference slots and lifecycle coordination.

SAM's upstream estimator changes per-image model state, and CPU inference patches
process-wide Torch functions. Each concurrent slot therefore owns its own process
and model; sharing a model between inference threads is intentionally avoided.
"""

from __future__ import annotations

import multiprocessing as mp
import os
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from typing import Callable


class InferenceFailure(RuntimeError):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


class ModelLifecycle:
    """Concurrent readers (inference), exclusive writers (unload/configure)."""

    def __init__(self):
        self._condition = threading.Condition(threading.RLock())
        self._active = 0
        self._writers = 0
        self._changing = False

    @property
    def active(self) -> int:
        with self._condition:
            return self._active

    @contextmanager
    def inference(self):
        with self._condition:
            self._condition.wait_for(lambda: not self._changing and self._writers == 0)
            self._active += 1
        try:
            yield
        finally:
            with self._condition:
                self._active -= 1
                self._condition.notify_all()

    @contextmanager
    def exclusive(self):
        with self._condition:
            self._writers += 1
            try:
                self._condition.wait_for(lambda: not self._changing and self._active == 0)
                self._changing = True
            finally:
                self._writers -= 1
        try:
            yield
        finally:
            with self._condition:
                self._changing = False
                self._condition.notify_all()


def create_worker_estimator(config: dict):
    from .estimators import create_estimator

    estimator = create_estimator()
    for key, value in config.items():
        setattr(estimator, key, value)
    return estimator


def estimator_info(estimator, *, loading=False, error=None) -> dict:
    return {"loaded": estimator.loaded, "loading": loading, "error": error,
            "device": getattr(estimator, "active_device", None),
            "precision": getattr(estimator, "active_precision", None),
            "device_reason": getattr(estimator, "device_reason", ("",))}


def _worker_main(connection, config: dict, factory: Callable):
    # This function must remain module-level for Windows multiprocessing spawn.
    from .i18n import set_language, tr

    def watch_parent():
        parent = mp.parent_process()
        if parent is not None:
            while parent.is_alive():
                time.sleep(0.2)
            # Also reap a worker that is busy inside a native model call when
            # the service is force-killed; pipe EOF alone only covers idle ones.
            os._exit(0)

    threading.Thread(target=watch_parent, daemon=True).start()
    estimator = None
    try:
        estimator = factory(config)
        while True:
            command, payload, release_after, language = connection.recv()
            set_language(language)
            if command == "shutdown":
                estimator.unload()
                connection.send(("state", estimator_info(estimator)))
                connection.send(("result", None))
                return
            try:
                if not estimator.loaded:
                    connection.send(("state", estimator_info(estimator, loading=True)))
                    try:
                        estimator.load()
                    except Exception as error:
                        message = tr("模型加载失败：{error}", error=f"{type(error).__name__}: {error}")
                        connection.send(("state", estimator_info(estimator, error=message)))
                        connection.send(("error", (503, message)))
                        continue
                connection.send(("state", estimator_info(estimator)))
                if command == "load":
                    connection.send(("result", None))
                    continue
                try:
                    result = estimator.estimate(payload)
                finally:
                    if release_after == 0:
                        estimator.unload()
                    connection.send(("state", estimator_info(estimator)))
                connection.send(("result", result))
            except Exception as error:
                message = f"{type(error).__name__}: {error}"
                connection.send(("state", estimator_info(estimator, error=message)))
                connection.send(("error", (500, message)))
    except (EOFError, BrokenPipeError, OSError):
        # The parent has closed or exited; never keep an idle orphan model alive.
        pass
    except Exception as error:
        try:
            connection.send(("fatal", (503, f"{type(error).__name__}: {error}")))
        except (EOFError, BrokenPipeError, OSError):
            pass
    finally:
        if estimator is not None:
            estimator.unload()
        connection.close()


class _WorkerSlot:
    def __init__(self, config: dict, factory: Callable):
        context = mp.get_context("spawn")
        self.connection, child = context.Pipe()
        self.process = context.Process(target=_worker_main, args=(child, config, factory), daemon=True,
                                       name="Posegraph recognition")
        self._state_lock = threading.Lock()
        self._state = {"loaded": False, "loading": False, "error": None, "device": None,
                       "precision": None, "device_reason": ("",)}
        self.process.start()
        child.close()
        self.closed = False

    @property
    def state(self) -> dict:
        with self._state_lock:
            if not self.closed and not self.process.is_alive():
                self._state = {**self._state, "loaded": False, "loading": False,
                               "error": "Recognition worker exited unexpectedly"}
            return dict(self._state)

    def _update(self, state):
        with self._state_lock:
            self._state = state

    def call(self, command: str, payload=None, release_after=-1, language="zh"):
        try:
            self.connection.send((command, payload, release_after, language))
            while True:
                if not self.connection.poll(0.1):
                    if not self.process.is_alive():
                        raise EOFError()
                    continue
                kind, value = self.connection.recv()
                if kind == "state":
                    self._update(value)
                elif kind == "result":
                    return value
                elif kind == "error":
                    raise InferenceFailure(*value)
                elif kind == "fatal":
                    self._update({**self.state, "loaded": False, "loading": False, "error": value[1]})
                    self.close(graceful=False)
                    raise InferenceFailure(*value)
        except (EOFError, BrokenPipeError, OSError) as error:
            self._update({**self.state, "loaded": False, "loading": False,
                          "error": "Recognition worker exited unexpectedly"})
            self.close(graceful=False)
            raise InferenceFailure(503, "Recognition worker exited unexpectedly") from error

    def close(self, *, graceful=True):
        if self.closed:
            return
        self.closed = True
        if self.process.is_alive():
            if graceful:
                try:
                    self.connection.send(("shutdown", None, -1, "zh"))
                except (EOFError, BrokenPipeError, OSError):
                    pass
                self.process.join(timeout=5)
            if self.process.is_alive():
                self.process.terminate()
                self.process.join(timeout=5)
            if self.process.is_alive():
                self.process.kill()
                self.process.join()
        else:
            self.process.join()
        self.connection.close()
        self._update({**self.state, "loaded": False, "loading": False})


class InferencePool:
    """At most N models, one request per isolated worker, lazy allocation."""

    def __init__(self, parallelism: int, config: dict, factory: Callable = create_worker_estimator):
        if not 1 <= parallelism <= 8:
            raise ValueError("parallelism must be in 1..8")
        self.parallelism = parallelism
        self.config = dict(config)
        self.factory = factory
        self._condition = threading.Condition(threading.RLock())
        self._slots: list[_WorkerSlot] = []
        self._busy: set[_WorkerSlot] = set()
        self._closing = False

    @property
    def states(self) -> list[dict]:
        with self._condition:
            return [slot.state for slot in self._slots if not slot.closed]

    @property
    def processes(self):
        with self._condition:
            return [slot.process for slot in self._slots]

    def _lease(self) -> _WorkerSlot:
        with self._condition:
            while True:
                if self._closing:
                    raise RuntimeError("Recognition pool is closed")
                self._remove_dead_slots()
                slot = next((slot for slot in self._slots if slot not in self._busy), None)
                if slot is None and len(self._slots) < self.parallelism:
                    slot = _WorkerSlot(self.config, self.factory)
                    self._slots.append(slot)
                if slot is not None:
                    self._busy.add(slot)
                    return slot
                self._condition.wait()

    def estimate(self, rgb, release_after: float, language: str):
        slot = self._lease()
        try:
            return slot.call("estimate", rgb, release_after, language)
        finally:
            with self._condition:
                self._busy.discard(slot)
                self._condition.notify_all()

    def preload(self, language="zh"):
        # The service lifecycle gate makes preloading exclusive with inference.
        with self._condition:
            if self._closing:
                raise RuntimeError("Recognition pool is closed")
            self._remove_dead_slots()
            while len(self._slots) < self.parallelism:
                self._slots.append(_WorkerSlot(self.config, self.factory))
            slots = list(self._slots)
        with ThreadPoolExecutor(max_workers=self.parallelism) as executor:
            list(executor.map(lambda slot: slot.call("load", language=language), slots))

    def _remove_dead_slots(self):
        for slot in self._slots:
            if not slot.closed and not slot.process.is_alive():
                slot.close(graceful=False)
        self._slots = [slot for slot in self._slots if not slot.closed]

    def close(self):
        with self._condition:
            self._closing = True
            self._condition.notify_all()
            self._condition.wait_for(lambda: not self._busy)
            slots, self._slots = self._slots, []
        for slot in slots:
            slot.close()
