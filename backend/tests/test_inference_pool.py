"""Concurrency/lifecycle tests using actual spawned model doubles, no GPU."""
import json
import multiprocessing as mp
import os
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph.inference_pool import InferenceFailure, InferencePool, ModelLifecycle
from posegraph import app as service
from inference_worker_fakes import FakeEstimator, fake_factory


def rgb(marker):
    return np.full((2, 2, 3), marker, dtype=np.uint8)


def alive(pid):
    if sys.platform == "win32":
        import ctypes
        from ctypes import wintypes
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
        kernel.OpenProcess.restype = wintypes.HANDLE
        handle = kernel.OpenProcess(0x1000, False, pid)
        if not handle:
            return False
        try:
            status = wintypes.DWORD()
            kernel.GetExitCodeProcess(handle, ctypes.byref(status))
            return status.value == 259
        finally:
            kernel.CloseHandle(handle)
    try:
        os.kill(pid, 0)
        stat = Path(f"/proc/{pid}/stat")
        return not (stat.exists() and stat.read_text().split()[2] == "Z")
    except ProcessLookupError:
        return False


class InferencePoolTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)

    def pool(self, parallelism=2, **config):
        pool = InferencePool(parallelism, {"probe_dir": self.temp.name, **config}, fake_factory)
        self.addCleanup(pool.close)
        return pool

    def events(self):
        return [json.loads(line) for path in Path(self.temp.name).glob("*.jsonl")
                for line in path.read_text(encoding="utf-8").splitlines()]

    def wait(self, condition, timeout=10):
        deadline = time.monotonic() + timeout
        while not condition():
            if time.monotonic() > deadline:
                self.fail("Timed out waiting for child model state")
            time.sleep(.02)

    def test_actual_overlap_is_bounded_and_single_slot_is_serial(self):
        for parallelism in (1, 2):
            with self.subTest(parallelism=parallelism):
                pool = self.pool(parallelism, delay=.2)
                pool.preload()
                started = time.monotonic()
                with ThreadPoolExecutor(max_workers=5) as executor:
                    results = list(executor.map(lambda marker: pool.estimate(rgb(marker), -1, "en"), range(5)))
                events = sorted((e for e in self.events() if e["time"] >= started), key=lambda event: event["time"])
                active = peak = 0
                for event in events:
                    if event["kind"] == "start":
                        active += 1
                        peak = max(peak, active)
                    elif event["kind"] == "end":
                        active -= 1
                self.assertEqual(peak, parallelism)
                self.assertEqual(len({e["pid"] for e in events if e["kind"] == "start"}), parallelism)
                self.assertEqual([r.marker for r in results], list(range(5)))
                self.assertLessEqual(len(pool.processes), parallelism)
                processes = pool.processes
                pool.close()
                self.assertTrue(all(not process.is_alive() for process in processes))

    def test_release_zero_unloads_each_worker_and_model_health_is_live(self):
        # Keep both inferences alive until their loaded states are observed.
        # Windows spawn/cold imports need not finish within the same sleep window.
        gate = mp.get_context("spawn").Event()
        pool = self.pool(delay=0, estimate_gate=gate)
        with ThreadPoolExecutor(max_workers=2) as executor:
            futures = [executor.submit(pool.estimate, rgb(i), 0, "en") for i in range(2)]
            try:
                self.wait(lambda: sum(s["loaded"] for s in pool.states) == 2)
                self.assertTrue(all(not f.done() for f in futures))
            finally:
                gate.set()
            for future in futures:
                future.result()
        self.assertTrue(all(not state["loaded"] for state in pool.states))
        self.assertEqual(sum(e["kind"] == "unload" for e in self.events()), 2)

    def test_close_waits_running_inference(self):
        pool = self.pool(delay=.4)
        with ThreadPoolExecutor(max_workers=2) as executor:
            estimate = executor.submit(pool.estimate, rgb(1), -1, "en")
            self.wait(lambda: any(e["kind"] == "start" for e in self.events()))
            close = executor.submit(pool.close)
            time.sleep(.05)
            self.assertFalse(close.done())
            self.assertEqual(estimate.result().marker, 1)
            close.result()
        self.assertEqual(pool.states, [])

    def test_failure_and_crashed_worker_recover_and_preload_replaces_idle_death(self):
        pool = self.pool(delay=.01)
        with self.assertRaisesRegex(InferenceFailure, "fake bad image"):
            pool.estimate(rgb(99), -1, "en")
        self.assertEqual(pool.estimate(rgb(1), -1, "en").marker, 1)
        with self.assertRaises(InferenceFailure) as caught:
            pool.estimate(rgb(253), -1, "en")
        self.assertEqual(caught.exception.status, 503)
        self.assertEqual(pool.estimate(rgb(2), -1, "en").marker, 2)
        dead = pool.processes[0]
        dead.terminate()
        dead.join()
        self.assertFalse(any(state["loaded"] for state in pool.states))
        pool.preload()
        self.assertEqual(len(pool.states), 2)
        self.assertTrue(all(state["loaded"] for state in pool.states))
        self.assertNotIn(dead.pid, [p.pid for p in pool.processes])

    def test_factory_and_load_failures_have_useful_503(self):
        for config, text in (({"factory_failure": True}, "fake factory failed"),
                             ({"load_failure": True}, "fake model load failed")):
            pool = self.pool(**config)
            with self.assertRaisesRegex(InferenceFailure, text) as caught:
                pool.estimate(rgb(1), -1, "en")
            self.assertEqual(caught.exception.status, 503)

    def test_service_default_serial_config_validation_and_unload_wait(self):
        estimator = FakeEstimator({"probe_dir": self.temp.name, "delay": .2})
        lifecycle = ModelLifecycle()
        with patch.object(service, "_estimator", estimator), patch.object(service, "_model_lifecycle", lifecycle), \
                patch.object(service, "_recognition_parallelism", 1), patch.object(service, "_parallel_pool", None), \
                patch.object(service, "_release_after", -1), patch.object(service, "_decode", side_effect=rgb):
            self.assertEqual(service.ModelConfig().recognition_parallelism, None)
            self.assertEqual(service._model_info()["config"]["recognitionParallelism"], 1)
            with ThreadPoolExecutor(max_workers=2) as executor:
                first = executor.submit(service._estimate, 1)
                self.wait(lambda: any(e["kind"] == "start" for e in self.events()))
                unload = executor.submit(service._unload_model)
                time.sleep(.03)
                self.assertFalse(unload.done())
                first.result()
                unload.result()
            self.assertFalse(estimator.loaded)
            for invalid in (0, 9, True, 1.5):
                with self.assertRaises(ValueError):
                    service.ModelConfig(recognition_parallelism=invalid)

    def test_service_parallel_config_and_unload_wait_for_all_models(self):
        estimator = FakeEstimator({})
        pools = []
        def create_pool(count, config):
            pool = self.pool(count, **config, delay=.3)
            pools.append(pool)
            return pool
        with patch.object(service, "_estimator", estimator), patch.object(service, "_model_lifecycle", ModelLifecycle()), \
                patch.object(service, "_recognition_parallelism", 1), patch.object(service, "_parallel_pool", None), \
                patch.object(service, "_release_after", -1), patch.object(service, "_decode", side_effect=rgb), \
                patch.object(service, "InferencePool", side_effect=create_pool):
            estimator.load()
            response = service.model_config(service.ModelConfig(recognition_parallelism=2))
            self.assertEqual(response["model"]["config"]["recognitionParallelism"], 2)
            self.assertFalse(estimator.loaded)  # never retain an extra parent model
            with ThreadPoolExecutor(max_workers=3) as executor:
                futures = [executor.submit(service._estimate, i) for i in (1, 2)]
                self.wait(lambda: sum(s["loaded"] for s in pools[0].states) == 2 if pools else False)
                change = executor.submit(service.model_config, service.ModelConfig(device="cuda"))
                time.sleep(.03)
                self.assertFalse(change.done())
                for future in futures:
                    future.result()
                change.result()
            self.assertEqual(pools[0].states, [])
            self.assertTrue(all(not p.is_alive() for p in pools[0].processes))
            service._estimate(3)
            self.assertEqual(pools[-1].config["device"], "cuda")
            self.assertTrue(service._model_info()["loaded"])
            service._unload_model()
            self.assertFalse(service._model_info()["loaded"])

    def test_parent_forced_exit_reaps_busy_workers(self):
        self._parent_exit_check(force=True)

    def test_idle_policy_releases_all_worker_models_and_preload_failure_is_visible(self):
        estimator = FakeEstimator({})
        pool = self.pool()
        pool.preload()
        processes = pool.processes
        with patch.object(service, "_estimator", estimator), patch.object(service, "_model_lifecycle", ModelLifecycle()), \
                patch.object(service, "_recognition_parallelism", 2), patch.object(service, "_parallel_pool", pool), \
                patch.object(service, "_release_after", .1), patch.object(service, "_last_used", 0), \
                patch.object(service, "_model_state", {"loading": False, "error": None}):
            self.assertTrue(service._model_info()["loaded"])
            service._release_idle_models()
            self.assertFalse(service._model_info()["loaded"])
            self.assertTrue(all(not p.is_alive() for p in processes))
            broken = self.pool(factory_failure=True)
            service._parallel_pool = broken
            with self.assertRaisesRegex(InferenceFailure, "fake factory failed"):
                service._load_model()
            self.assertIn("fake factory failed", service._model_info()["error"])
            self.assertFalse(service._model_info()["loading"])

    def test_http_shutdown_reaps_workers_before_exit(self):
        self._parent_exit_check(force=False)

    def _parent_exit_check(self, force):
        pid_file = Path(self.temp.name) / "worker-pids.json"
        script = f'''\
import json, multiprocessing as mp, time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
import numpy as np
from posegraph import app as service
from posegraph.inference_pool import InferencePool
from inference_worker_fakes import fake_factory
if __name__ == "__main__":
    context = mp.get_context("spawn")
    started, gate = context.Event(), context.Event()
    config = {{"delay": .01, "estimate_started": started, "estimate_gate": gate}} if {force!r} else {{"delay": .01}}
    pool = InferencePool(2, config, fake_factory)
    service._recognition_parallelism = 2
    service._parallel_pool = pool
    if {force!r}:
        executor = ThreadPoolExecutor(max_workers=1)
        inference = executor.submit(pool.estimate, np.full((2,2,3), 1, dtype=np.uint8), -1, "en")
        while not started.wait(.02):
            if inference.done(): inference.result()
    else:
        pool.preload()
    Path({str(pid_file)!r}).write_text(json.dumps([p.pid for p in pool.processes]))
    if not {force!r}: service.shutdown()
    time.sleep(30)
'''
        env = dict(os.environ)
        env["PYTHONPATH"] = os.pathsep.join([str(Path(__file__).resolve().parents[1] / "src"),
                                             str(Path(__file__).resolve().parent)])
        parent = subprocess.Popen([sys.executable, "-c", script], env=env,
                                  stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
        try:
            def ready():
                if parent.poll() is not None:
                    self.fail("Parent exited before inference was ready: " + parent.stderr.read().decode(errors="replace"))
                return pid_file.exists()
            # This includes a fresh service import and Windows process spawn;
            # synchronization is event-based rather than a transient loaded state.
            self.wait(ready, timeout=30)
            pids = json.loads(pid_file.read_text())
            self.assertTrue(pids)
            if force:
                parent.kill()
            parent.wait(timeout=10)
            self.wait(lambda: all(not alive(pid) for pid in pids))
        finally:
            if parent.poll() is None:
                parent.kill()
                parent.wait(timeout=5)
            parent.stderr.close()


if __name__ == "__main__":
    unittest.main()
