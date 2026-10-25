"""Bounded RAM cache with temporary disk spill for recognized image sessions."""

from __future__ import annotations

import json
import threading
from collections import OrderedDict
from collections.abc import Iterator, MutableMapping
from dataclasses import dataclass
from tempfile import NamedTemporaryFile, TemporaryDirectory
from typing import BinaryIO

import numpy as np


@dataclass
class Session:
    id: str
    name: str
    image_size: tuple[int, int]
    preview_png: bytes
    humans: list[dict]
    inference_ms: float


class SessionCache(MutableMapping[str, Session]):
    """Keep a small LRU in RAM; preserve evictions until explicitly removed.

    Archives contain numeric arrays and JSON metadata only. The temporary directory
    belongs to this process; archive filenames never contain caller-supplied IDs.
    """

    def __init__(self, max_in_memory: int = 8):
        if max_in_memory < 1:
            raise ValueError("max_in_memory must be positive")
        self.max_in_memory = max_in_memory
        self._memory: OrderedDict[str, Session] = OrderedDict()
        self._disk: dict[str, BinaryIO] = {}
        self._directory = TemporaryDirectory(prefix="posegraph-sessions-")
        self._lock = threading.RLock()

    @property
    def in_memory_count(self) -> int:
        with self._lock:
            return len(self._memory)

    def _save(self, session: Session) -> BinaryIO:
        arrays = {"preview": np.frombuffer(session.preview_png, dtype=np.uint8)}
        humans = []
        for index, human in enumerate(session.humans):
            fields = {}
            for key, value in human.items():
                if value is None or isinstance(value, (str, bool, int, float)):
                    fields[key] = {"value": value}
                elif isinstance(value, np.generic):
                    fields[key] = {"value": value.item()}
                else:
                    array = np.asarray(value)
                    if array.dtype.hasobject:
                        raise ValueError("Session arrays cannot contain Python objects")
                    array_key = f"human_{index}_{len(fields)}"
                    arrays[array_key] = array
                    fields[key] = {"array": array_key}
            humans.append(fields)
        metadata = {"id": session.id, "name": session.name, "image_size": session.image_size,
                    "inference_ms": session.inference_ms, "humans": humans}
        arrays["metadata"] = np.frombuffer(json.dumps(metadata, ensure_ascii=False).encode("utf-8"), dtype=np.uint8)
        # Keep the OS delete-on-close handle open. On Windows this also removes
        # image data when the plugin forcibly terminates the backend process.
        archive = NamedTemporaryFile(mode="w+b", suffix=".npz", dir=self._directory.name)
        try:
            np.savez_compressed(archive, **arrays)
            archive.flush()
            archive.seek(0)
        except BaseException:
            archive.close()
            raise
        return archive

    @staticmethod
    def _load(stream: BinaryIO) -> Session:
        stream.seek(0)
        with np.load(stream, allow_pickle=False) as archive:
            metadata = json.loads(archive["metadata"].tobytes().decode("utf-8"))
            humans = [{key: archive[field["array"]].copy() if "array" in field else field["value"]
                       for key, field in fields.items()} for fields in metadata["humans"]]
            return Session(metadata["id"], metadata["name"], tuple(metadata["image_size"]),
                           archive["preview"].tobytes(), humans, metadata["inference_ms"])

    def _make_room(self) -> None:
        if len(self._memory) < self.max_in_memory:
            return
        sid, session = next(iter(self._memory.items()))
        # Keep the RAM copy if writing fails (disk full, permission change, etc.).
        archive = self._save(session)
        previous = self._disk.get(sid)
        self._disk[sid] = archive
        if previous is not None:
            previous.close()
        del self._memory[sid]

    def __getitem__(self, sid: str) -> Session:
        with self._lock:
            if sid in self._memory:
                self._memory.move_to_end(sid)
                return self._memory[sid]
            if sid not in self._disk:
                raise KeyError(sid)
            session = self._load(self._disk[sid])
            self._make_room()
            self._memory[sid] = session
            return session

    def __setitem__(self, sid: str, session: Session) -> None:
        if sid != session.id:
            raise ValueError("Session ID does not match cache key")
        with self._lock:
            if sid not in self._memory:
                self._make_room()
            self._memory[sid] = session
            self._memory.move_to_end(sid)

    def __delitem__(self, sid: str) -> None:
        with self._lock:
            if sid not in self._memory and sid not in self._disk:
                raise KeyError(sid)
            archive = self._disk.get(sid)
            if archive is not None:
                archive.close()
                del self._disk[sid]
            self._memory.pop(sid, None)

    def __iter__(self) -> Iterator[str]:
        with self._lock:
            return iter(list(dict.fromkeys([*self._disk, *self._memory])))

    def __len__(self) -> int:
        with self._lock:
            return len(self._memory.keys() | self._disk.keys())

    def discard(self, sid: str) -> None:
        """Remove without loading an archived session back into memory."""
        with self._lock:
            if sid in self._memory or sid in self._disk:
                del self[sid]

    def clear(self) -> None:
        with self._lock:
            for archive in self._disk.values():
                archive.close()
            self._disk.clear()
            self._memory.clear()

    def close(self) -> None:
        with self._lock:
            self.clear()
            self._directory.cleanup()
