"""Thread/lifecycle proof for the shared pool, without Torch or a GPU."""
import sys
import threading
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph.i18n import _lang
from posegraph.shared_inference import InferenceFailure, ModelLifecycle, SharedInferencePool
from inference_fakes import FakeEstimator as ModelDouble, wait_until


class SharedInferencePoolTests(unittest.TestCase):
    def test_lifecycle_writer_waits_and_blocks_new_readers(self):
        lifecycle = ModelLifecycle()
        entered, release, read = threading.Event(), threading.Event(), threading.Event()

        def writer():
            with lifecycle.exclusive():
                entered.set()
                ModelDouble.wait(release)

        def reader():
            with lifecycle.inference():
                read.set()

        with ThreadPoolExecutor(max_workers=2) as executor:
            try:
                with lifecycle.inference():
                    changing = executor.submit(writer)
                    wait_until(lambda: lifecycle._writers == 1)
                    waiting = executor.submit(reader)
                    self.assertFalse(entered.is_set())
                    self.assertFalse(read.is_set())
                self.assertTrue(entered.wait(5))
                self.assertFalse(read.is_set())
            finally:
                release.set()
            changing.result(timeout=5)
            waiting.result(timeout=5)
        self.assertTrue(read.is_set())
        self.assertEqual(lifecycle.active, 0)

    def pool(self, count=2, owner=None):
        owner = owner or ModelDouble()
        calls = []
        def factory(config):
            calls.append(config)
            return owner
        pool = SharedInferencePool(count, {"cuda_streams": True}, factory)
        self.addCleanup(pool.close)
        return pool, owner, calls

    def started(self, owner, count=1):
        return [owner.started.get(timeout=5) for _ in range(count)]


    def test_one_factory_one_load_and_real_overlap_bounded_by_limit(self):
        for limit in range(1, 9):
            with self.subTest(limit=limit):
                pool, owner, factories = self.pool(limit)
                total = limit + 3
                owner.gates = {i: threading.Event() for i in range(total)}
                with ThreadPoolExecutor(max_workers=total) as executor:
                    futures = [executor.submit(pool.estimate, i, "en") for i in range(total)]
                    running = self.started(owner, limit)
                    self.assertEqual(len({thread for _, thread in running}), limit)
                    self.assertEqual(owner.active, limit)
                    self.assertEqual(pool.states[0]["active"], limit)
                    self.assertEqual(owner.load_calls, 1)
                    for _ in range(total - limit):
                        marker, _ = running.pop()
                        owner.gates[marker].set()
                        running += self.started(owner)
                        self.assertLessEqual(owner.peak, limit)
                    for marker, _ in running:
                        owner.gates[marker].set()
                    self.assertEqual([f.result(timeout=5).marker for f in futures], list(range(total)))
                self.assertEqual(owner.peak, limit)
                self.assertEqual(len(factories), 1)
                self.assertEqual(factories[0], {"cuda_streams": True})
                self.assertEqual(owner.load_calls, 1)
                self.assertEqual(pool.states[0]["active"], 0)
                self.assertEqual(pool.states[0]["modelCopies"], 1)


    def test_concurrent_preload_creates_one_copy_without_processes(self):
        pool, owner, factories = self.pool(8)
        owner.load_gate = threading.Event()
        with patch("multiprocessing.get_context", side_effect=AssertionError("must not spawn")):
            with ThreadPoolExecutor(max_workers=8) as executor:
                futures = [executor.submit(pool.preload, "en") for _ in range(8)]
                self.assertTrue(owner.load_started.wait(timeout=5))
                state = pool.states[0]
                self.assertTrue(state["loading"])
                self.assertEqual(state["active"], 0)
                owner.load_gate.set()
                for future in futures:
                    future.result(timeout=5)
            pool.preload()
        self.assertEqual(owner.load_calls, 1)
        self.assertEqual(len(factories), 1)
        state = pool.states[0]
        self.assertEqual((state["strategy"], state["modelCopies"], state["loadedModelCopies"]), ("shared", 1, 1))
        self.assertTrue(pool.diagnostics(include_pointers=True)["owner"]["includePointers"])
        pool.close()
        pool.close()
        self.assertEqual(owner.unloads, 1)
        self.assertFalse(pool.states[0]["loaded"])

    def test_close_rejects_waiters_and_new_work_but_drains_active(self):
        pool, owner, _ = self.pool(1)
        owner.gates["active"] = threading.Event()
        with ThreadPoolExecutor(max_workers=3) as executor:
            active = executor.submit(pool.estimate, "active", "en")
            self.started(owner)
            waiting = executor.submit(pool.estimate, "waiting", "en")
            closing = executor.submit(pool.close)
            wait_until(lambda: pool.states[0]["closing"])
            with self.assertRaisesRegex(RuntimeError, "closed"):
                waiting.result(timeout=5)
            self.assertFalse(closing.done())
            with self.assertRaisesRegex(RuntimeError, "closed"):
                pool.preload()
            owner.gates["active"].set()
            self.assertEqual(active.result(timeout=5).marker, "active")
            closing.result(timeout=5)
        self.assertEqual(owner.unloads, 1)
        self.assertEqual(pool.states[0]["active"], 0)
        with self.assertRaisesRegex(RuntimeError, "closed"):
            pool.estimate("new")

    def test_close_waits_in_progress_load_without_admitting_its_request(self):
        pool, owner, _ = self.pool()
        owner.load_gate = threading.Event()
        with ThreadPoolExecutor(max_workers=2) as executor:
            estimate = executor.submit(pool.estimate, "never-admitted", "en")
            self.assertTrue(owner.load_started.wait(timeout=5))
            closing = executor.submit(pool.close)
            wait_until(lambda: pool.states[0]["closing"])
            self.assertFalse(closing.done())
            owner.load_gate.set()
            with self.assertRaisesRegex(RuntimeError, "closed"):
                estimate.result(timeout=5)
            closing.result(timeout=5)
        self.assertTrue(owner.started.empty())
        self.assertEqual(owner.unloads, 1)


    def test_failed_request_frees_slot_without_poisoning_model_or_health(self):
        pool, owner, _ = self.pool()
        owner.fail.add("bad")
        owner.gates = {name: threading.Event() for name in ("bad", "good")}
        with ThreadPoolExecutor(max_workers=2) as executor:
            bad = executor.submit(pool.estimate, "bad", "en")
            good = executor.submit(pool.estimate, "good", "en")
            self.started(owner, 2)
            for gate in owner.gates.values():
                gate.set()
            with self.assertRaisesRegex(InferenceFailure, "fake request failure") as caught:
                bad.result(timeout=5)
            self.assertEqual(caught.exception.status, 500)
            self.assertEqual(good.result(timeout=5).marker, "good")
        self.assertEqual(pool.estimate("recovered").marker, "recovered")
        self.assertEqual(owner.load_calls, 1)
        self.assertIsNone(pool.states[0]["error"])
        self.assertEqual(pool.states[0]["active"], 0)

    def test_load_and_factory_failures_are_retryable_503_and_preserve_language(self):
        pool, owner, calls = self.pool()
        owner.load_failures = 1
        original = _lang.set("zh")
        try:
            with self.assertRaisesRegex(InferenceFailure, "fake load failure") as caught:
                pool.preload("en")
            self.assertEqual(caught.exception.status, 503)
            self.assertEqual(_lang.get(), "zh")
            self.assertIn("fake load failure", pool.states[0]["error"])
            pool.preload("zh")
            self.assertIsNone(pool.states[0]["error"])
            self.assertEqual(owner.loads_language, ["en", "zh"])
            self.assertEqual(owner.load_calls, 2)
            self.assertEqual(len(calls), 1)
        finally:
            _lang.reset(original)
        count = 0
        def factory(config):
            nonlocal count
            count += 1
            if count == 1:
                raise ValueError("fake factory failure")
            return ModelDouble()
        another = SharedInferencePool(2, {}, factory)
        self.addCleanup(another.close)
        with self.assertRaisesRegex(InferenceFailure, "fake factory failure"):
            another.preload()
        another.preload()
        self.assertEqual(count, 2)
        self.assertTrue(another.states[0]["loaded"])

    def test_thread_local_languages_do_not_leak_into_peer_or_reused_thread(self):
        pool, owner, _ = self.pool()
        owner.language_barrier = threading.Barrier(2)
        def request(language):
            original = _lang.set("zh" if language == "en" else "en")
            before = _lang.get()
            try:
                result = pool.estimate(language, language)
                return before, _lang.get(), result.language, result.after_language
            finally:
                _lang.reset(original)
        with ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(request, ["en", "zh"]))
        self.assertEqual(results, [("zh", "zh", "en", "en"), ("en", "en", "zh", "zh")])

    def test_shutdown_failure_can_be_retried_without_reopening_admission(self):
        pool, owner, _ = self.pool()
        pool.preload()
        owner.unload_failures = 1
        with self.assertRaisesRegex(InferenceFailure, "fake unload failure"):
            pool.close()
        with self.assertRaisesRegex(RuntimeError, "closed"):
            pool.estimate("rejected")
        pool.close()
        self.assertFalse(owner.loaded)
        self.assertEqual(owner.unloads, 1)

    def test_invalid_limits_and_config_copy(self):
        for value in (0, 9, True, 1.5, "2"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                SharedInferencePool(value, {})
        config = {"cuda_streams": False}
        seen = []
        def factory(snapshot):
            seen.append(snapshot)
            snapshot["factory_only"] = True
            return ModelDouble()
        pool = SharedInferencePool(2, config, factory)
        self.addCleanup(pool.close)
        config["cuda_streams"] = True
        pool.preload()
        self.assertFalse(seen[0]["cuda_streams"])
        self.assertNotIn("factory_only", pool.config)


if __name__ == "__main__":
    unittest.main()
