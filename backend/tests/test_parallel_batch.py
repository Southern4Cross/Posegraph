"""parallel_batch：按余量定路数、CPU 线程分配、出问题交回串行。"""
import sys
import unittest
from functools import partial
from pathlib import Path
from unittest.mock import patch

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import parallel_batch
from posegraph.parallel_batch import ParallelBatch, budget
from posegraph.shared_inference import SharedInferencePool
from inference_fakes import FakeEstimator

IMAGE = np.zeros((2, 2, 3), dtype=np.uint8)


class BudgetTests(unittest.TestCase):
    def test_gpu_is_limited_by_free_vram(self):
        self.assertEqual(budget(4, "cuda", 8000, 1000, 20)[0], 4)
        limit, note = budget(4, "cuda", 1024 + 250 * 2 + 10, None, 20)
        self.assertEqual(limit, 3)
        self.assertIn("3", note)
        self.assertEqual(budget(4, "cuda", 500, None, 20)[0], 1)

    def test_gpu_ignores_cpu_core_and_ram_limits(self):
        self.assertEqual(budget(8, "cuda", 20000, 100, 2)[0], 8)

    def test_cpu_is_limited_by_ram_and_cores(self):
        self.assertEqual(budget(4, "cpu", None, 3072 + 450 + 1, 20)[0], 2)
        self.assertEqual(budget(8, "cpu", None, 64000, 6)[0], 3)
        self.assertEqual(budget(4, "cpu", None, 1000, 20)[0], 1)

    def test_unknown_headroom_keeps_request(self):
        self.assertEqual(budget(4, "cuda", None, None, 20), (4, ""))
        self.assertEqual(budget(4, "cpu", None, None, 20), (4, ""))

    def test_never_exceeds_request(self):
        self.assertEqual(budget(2, "cpu", None, 64000, 64)[0], 2)


class ParallelBatchTests(unittest.TestCase):
    def batch(self, requested, owner):
        self.stack = patch.object(parallel_batch, "SharedInferencePool", partial(SharedInferencePool, factory=lambda _: owner))
        self.stack.start()
        self.addCleanup(self.stack.stop)
        batch = ParallelBatch(requested, {})
        self.addCleanup(batch.close_if_idle)
        return batch

    def test_cpu_load_limits_slots_and_splits_threads_then_restores(self):
        import torch

        default = torch.get_num_threads()
        owner = FakeEstimator()
        batch = self.batch(8, owner)
        with patch.object(parallel_batch, "available_ram_mb", return_value=3072 + 450 * 2 + 1):
            self.assertEqual(batch.estimate(IMAGE, "zh").humans, [1])
        expected = min(3, max(1, default // 2))
        self.assertEqual(batch.effective, expected)
        self.assertEqual(batch._pool.parallelism, expected)
        self.assertEqual(torch.get_num_threads(), max(1, default // expected))
        info = batch.info()
        self.assertEqual((info["requested"], info["effective"], info["loaded"]), (8, expected, True))
        self.assertTrue(batch.close_if_idle())
        self.assertEqual(torch.get_num_threads(), default)
        self.assertIsNone(batch.info()["effective"])

    def test_load_failure_disables_and_returns_none(self):
        owner = FakeEstimator()
        owner.load_failures = 1
        batch = self.batch(2, owner)
        self.assertIsNone(batch.estimate(IMAGE, "zh"))
        self.assertIn("fake load failure", batch.disabled)
        self.assertIsNone(batch._pool)
        self.assertFalse(batch.wanted(True))
        self.assertIsNone(batch.estimate(IMAGE, "zh"))
        self.assertEqual(owner.load_calls, 1)

    def test_request_failure_returns_none_but_keeps_parallel(self):
        owner = FakeEstimator()
        owner.fail.add(0)
        batch = self.batch(2, owner)
        self.assertIsNone(batch.estimate(IMAGE, "zh"))
        self.assertIsNone(batch.disabled)
        self.assertTrue(batch.wanted(True))
        owner.fail.clear()
        self.assertEqual(batch.estimate(IMAGE, "zh").humans, [1])

    def test_wanted_needs_flag_and_more_than_one_slot(self):
        self.assertFalse(ParallelBatch(1, {}).wanted(True))
        self.assertFalse(ParallelBatch(4, {}).wanted(False))
        self.assertTrue(ParallelBatch(4, {}).wanted(True))
        self.assertFalse(ParallelBatch(4, {}, disabled="off").wanted(True))


if __name__ == "__main__":
    unittest.main()
