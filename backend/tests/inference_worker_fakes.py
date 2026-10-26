"""Importable model doubles for Windows spawn regression checks."""
import json
import os
import time
from pathlib import Path
from types import SimpleNamespace


class FakeEstimator:
    name = "fake"
    def __init__(self, config):
        self.config = config
        self.device = config.get("device", "cpu")
        self.precision = "fp32"
        self.share_mhr = True
        self.mhr_on_cpu = True
        self.min_free_vram_mb = 3000
        self.active_device = self.active_precision = None
        self.device_reason = ("手动指定",)
        self._loaded = False

    @property
    def loaded(self):
        return self._loaded

    def event(self, kind, marker=None):
        if self.config.get("probe_dir"):
            path = Path(self.config["probe_dir"]) / f"{os.getpid()}.jsonl"
            with path.open("a", encoding="utf-8") as stream:
                stream.write(json.dumps({"kind": kind, "marker": marker, "pid": os.getpid(),
                                         "time": time.monotonic()}) + "\n")

    def load(self):
        self.event("load")
        if self.config.get("load_failure"):
            raise RuntimeError("fake model load failed")
        self._loaded = True
        self.active_device, self.active_precision = self.device, self.precision

    def unload(self):
        if self._loaded:
            self.event("unload")
        self._loaded = False
        self.active_device = self.active_precision = None

    def estimate(self, rgb):
        marker = int(rgb.flat[0])
        self.event("start", marker)
        if marker == 253:
            os._exit(7)
        if started := self.config.get("estimate_started"):
            started.set()
        if gate := self.config.get("estimate_gate"):
            if not gate.wait(timeout=30):
                raise RuntimeError("test inference gate was not released")
        time.sleep(self.config.get("delay", .15))
        self.event("end", marker)
        if marker == 99:
            raise RuntimeError("fake bad image")
        return SimpleNamespace(marker=marker, humans=[marker], device=self.active_device)


def fake_factory(config):
    if config.get("factory_failure"):
        raise RuntimeError("fake factory failed")
    return FakeEstimator(config)
