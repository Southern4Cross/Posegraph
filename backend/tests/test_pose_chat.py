"""Chat contract tests with a fake provider; no API key, GPU or SAM weights."""

import copy
import base64
import io
import json
import random
import sys
import unittest
import warnings
from pathlib import Path
from unittest.mock import patch

import httpx
from fastapi.testclient import TestClient
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import app as service
from posegraph import pose_chat


class PoseChatTests(unittest.TestCase):
    def setUp(self):
        self.client = TestClient(service.app)
        self.requests = []
        self.plan = {"reply": "抬起左手", "edits": [
            {"op": "rotate", "bone": "j_ude_a_l", "rotation": [0, 0, 15]}
        ]}
        self.status = 200
        self.raw_content = None
        self.envelope = None
        self.raw_envelope = None
        self.finish = "stop"
        self.throw = None
        original_client = httpx.AsyncClient

        def handler(request):
            self.requests.append(request)
            if self.throw:
                raise self.throw("secret-key-must-not-leak", request=request)
            if self.status != 200:
                return httpx.Response(self.status, json={"error": "secret-key-must-not-leak"})
            if self.envelope is not None:
                return httpx.Response(200, json=self.envelope)
            if self.raw_envelope is not None:
                return httpx.Response(200, content=self.raw_envelope)
            return httpx.Response(200, json={"choices": [{
                "message": {"content": self.raw_content if self.raw_content is not None else json.dumps(self.plan)},
                "finish_reason": self.finish,
            }]})

        self.transport_patch = patch.object(pose_chat.httpx, "AsyncClient", side_effect=lambda **kwargs:
            original_client(transport=httpx.MockTransport(handler), **kwargs))
        self.transport_patch.start()
        self.addCleanup(self.transport_patch.stop)
        self.addCleanup(self.client.close)
        self.payload = {"prompt": "抬起左手", "race": "c0101", "provider": {
            "endpoint": "https://model.example/v1", "api_key": "secret-key-must-not-leak", "model": "text-model"
        }}

    def send(self, **updates):
        payload = copy.deepcopy(self.payload)
        payload.update(updates)
        return self.client.post("/chat/pose", json=payload, headers={"Accept-Language": "en"})

    def test_text_only_no_estimator_and_multi_turn_uses_current_pose(self):
        with patch.object(service._estimator, "load", side_effect=AssertionError("SAM loaded")):
            first = self.send()
            self.assertEqual(first.status_code, 200, first.text)
            result = first.json()
            self.assertTrue(result["result"]["stick"]["joints"])
            self.assertEqual(result["result"]["race"]["code"], "c0101")
            body = json.loads(self.requests[0].content)
            self.assertIsInstance(body["messages"][-1]["content"], str)
            self.assertNotIn("image_url", self.requests[0].content.decode())
            self.assertEqual(self.requests[0].headers["Authorization"], "Bearer secret-key-must-not-leak")
            self.assertEqual(str(self.requests[0].url), "https://model.example/v1/chat/completions")
            second = self.send(prompt="再高一点，其他不变", pose=result["result"]["pose"], history=result["history"])
            self.assertEqual(second.status_code, 200, second.text)
            self.assertEqual(len(second.json()["history"]), 4)
            messages = json.loads(self.requests[1].content)["messages"]
            self.assertEqual(messages[1:3], result["history"])
            self.assertNotEqual(second.json()["result"]["pose"], result["result"]["pose"])

    def test_image_only_sent_on_explicit_opt_in(self):
        sid = "chat-test-session"
        preview = io.BytesIO()
        Image.new("RGB", (32, 32), "red").save(preview, format="PNG")
        original = preview.getvalue()
        service._sessions[sid] = service.Session(sid, "private", (32, 32), original, [], 0)
        self.addCleanup(lambda: service._sessions.pop(sid, None))
        plain = self.send(session_id=sid)
        self.assertEqual(plain.status_code, 200, plain.text)
        self.assertIsInstance(json.loads(self.requests[-1].content)["messages"][-1]["content"], str)
        image = self.send(session_id=sid, include_image=True)
        self.assertEqual(image.status_code, 200, image.text)
        parts = json.loads(self.requests[-1].content)["messages"][-1]["content"]
        self.assertEqual(parts[1]["type"], "image_url")
        prefix, encoded = parts[1]["image_url"]["url"].split(",", 1)
        self.assertEqual(prefix, "data:image/jpeg;base64")
        with Image.open(io.BytesIO(base64.b64decode(encoded))) as transmitted:
            self.assertEqual(transmitted.format, "JPEG")
            self.assertEqual(transmitted.size, (32, 32))
        self.assertEqual(service._sessions[sid].preview_png, original)

    def test_invalid_image_does_not_reach_provider(self):
        sid = "chat-test-invalid-image"
        service._sessions[sid] = service.Session(sid, "invalid", (32, 32), b"invalid-image", [], 0)
        self.addCleanup(lambda: service._sessions.pop(sid, None))
        self.assertEqual(self.send(session_id=sid, include_image=True).status_code, 400)
        self.assertFalse(self.requests)

    def test_image_opt_in_without_session_fails_before_provider(self):
        self.assertEqual(self.send(include_image=True).status_code, 400)
        self.assertEqual(self.send(include_image=True, session_id="expired").status_code, 404)
        self.assertFalse(self.requests)
        # A restarted/evicted image session does not block text editing.
        self.assertEqual(self.send(session_id="expired").status_code, 200)

    def test_provider_failure_never_echoes_credentials(self):
        for status in (401, 403, 429, 400, 500, 302):
            with self.subTest(status=status):
                self.status = status
                response = self.send()
                self.assertEqual(response.status_code, 502)
                self.assertNotIn("secret-key", response.text)
        self.status = 200
        for error, expected in ((httpx.ReadTimeout, 504), (httpx.ConnectError, 502)):
            self.throw = error
            response = self.send()
            self.assertEqual(response.status_code, expected)
            self.assertNotIn("secret-key", response.text)

    def test_request_validation_hides_input_and_limits_roles_and_history(self):
        for update in ({"provider": {"api_key": "secret-key-must-not-leak"}},
                       {"provider": {**self.payload["provider"], "api_key": "secret-key-must-not-leak\r\n"}},
                       {"history": [{"role": "system", "content": "secret-key-must-not-leak"}]},
                       {"history": [{"role": "user", "content": "hi"}] * 21},
                       {"prompt": "   "}):
            with self.subTest(update=update):
                response = self.send(**update)
                self.assertEqual(response.status_code, 422)
                self.assertNotIn("secret-key", response.text)
        self.assertFalse(self.requests)

    def test_invalid_pose_and_race_rejected_before_provider(self):
        for pose in ("not json", "[]", '{"Bones":{"j_ude_a_l":{"Position":"nan, 0, 0"}}}'):
            self.assertEqual(self.send(pose=pose).status_code, 400)
        self.assertEqual(self.send(race="unknown").status_code, 400)
        self.assertFalse(self.requests)

    def test_overflowing_rig_rejected_before_provider(self):
        bones = [{"name": f"custom_{i}", "parent": f"custom_{i-1}" if i else "j_kao",
                  "reference_position": [0, 0.01, 0], "reference_rotation": [0, 0, 0, 1],
                  "reference_scale": [100, 100, 100]} for i in range(180)]
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", RuntimeWarning)
            response = self.send(rig={"bones": bones})
        self.assertEqual(response.status_code, 400)
        self.assertFalse(self.requests)

    def test_bad_model_plan_and_unsafe_edits_are_not_applied(self):
        for answer in ("not json", '{"reply":"ok","edits":"code"}',
                       '{"reply":"ok","edits":[],"python":"evil"}'):
            self.raw_content = answer
            self.assertEqual(self.send().status_code, 502)
        self.raw_content = None
        for edits in ([{"op": "exec", "code": "evil"}],
                      [{"op": "rotate", "bone": "unknown", "rotation": [0, 0, 10]}],
                      [{"op": "rotate", "bone": "j_kao", "rotation": [0, 10**999, 0]}],
                      [{"op": "move", "bone": "j_te_l", "target": [0, -10**999, 0]}],
                      [{"op": "rotate", "bone": "j_ude_a_l", "rotation": [0, 0, float("nan")]}]):
            self.plan["edits"] = edits
            self.assertEqual(self.send().status_code, 502)
        self.finish = "length"
        self.assertEqual(self.send().status_code, 502)

    def test_malformed_provider_envelopes_return_safe_errors(self):
        for envelope in ({"choices": [None]}, {"choices": [[]]}, {"choices": ["bad"]},
                         {"choices": []}, {"choices": [{"message": None}]},
                         {"choices": [{"message": {"content": [{"text": "bad"}]}}]}):
            with self.subTest(envelope=envelope):
                self.envelope = envelope
                self.assertEqual(self.send().status_code, 502)

    def test_excessively_nested_provider_json_returns_safe_error(self):
        nested = "[" * 1200 + "0" + "]" * 1200
        self.raw_envelope = nested.encode()
        self.assertEqual(self.send().status_code, 502)
        self.raw_envelope = None
        self.raw_content = '{"reply":"ok","edits":' + nested + '}'
        self.assertEqual(self.send().status_code, 502)

    def test_history_is_bounded_and_compatible_providers_need_no_key(self):
        self.payload["provider"]["api_key"] = ""
        self.plan["edits"] = []
        history = [{"role": "user" if i % 2 == 0 else "assistant", "content": str(i)} for i in range(20)]
        response = self.send(history=history)
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(len(response.json()["history"]), 20)
        self.assertNotIn("Authorization", self.requests[-1].headers)


class ReferenceImageCompressionTests(unittest.TestCase):
    def test_large_detailed_image_stays_within_size_and_byte_budget(self):
        # Incompressible input exercises both JPEG encoding and the byte budget.
        source = Image.frombytes("RGB", (1800, 1200), random.Random(42).randbytes(1800 * 1200 * 3))
        preview = io.BytesIO()
        source.save(preview, format="PNG")
        compressed = pose_chat.compress_reference_image(preview.getvalue())
        self.assertLessEqual(len(compressed), pose_chat.REFERENCE_MAX_BYTES)
        self.assertLess(len(compressed), len(preview.getvalue()) // 4)
        with Image.open(io.BytesIO(compressed)) as image:
            self.assertEqual(image.format, "JPEG")
            self.assertLessEqual(max(image.size), pose_chat.REFERENCE_MAX_SIDE)
            self.assertAlmostEqual(image.width / image.height, 1.5, delta=0.01)

    def test_small_transparent_image_is_not_upscaled_and_has_white_background(self):
        preview = io.BytesIO()
        Image.new("RGBA", (32, 24), (255, 0, 0, 0)).save(preview, format="PNG")
        with Image.open(io.BytesIO(pose_chat.compress_reference_image(preview.getvalue()))) as image:
            self.assertEqual(image.size, (32, 24))
            self.assertEqual(image.mode, "RGB")
            self.assertTrue(all(channel >= 250 for channel in image.getpixel((16, 12))))

    def test_reference_metadata_is_not_sent(self):
        preview = io.BytesIO()
        metadata = Image.Exif()
        metadata[270] = "private-description"
        Image.new("RGB", (32, 24), "blue").save(preview, format="PNG", exif=metadata)
        with Image.open(io.BytesIO(pose_chat.compress_reference_image(preview.getvalue()))) as image:
            self.assertFalse(image.getexif())
            self.assertNotIn("private-description", image.info.values())


class CompletionUrlTests(unittest.TestCase):
    def test_normalization(self):
        for endpoint, expected in (
            ("https://model.example", "https://model.example/v1/chat/completions"),
            ("https://model.example/v1/", "https://model.example/v1/chat/completions"),
            ("http://127.0.0.1:1234/v1/chat/completions", "http://127.0.0.1:1234/v1/chat/completions"),
            ("http://localhost:8080/custom/v1", "http://localhost:8080/custom/v1/chat/completions"),
        ):
            self.assertEqual(pose_chat.completion_url(endpoint), expected)

    def test_unsafe_or_wrong_protocol(self):
        for endpoint in ("file:///secrets", "https://user:password@model.example/v1", "bad",
                         "https://model.example/v1?api-key=private", "https://model.example/v1/responses",
                         "https://bad host.example/v1", "https://model.example:999999/v1"):
            with self.assertRaises(Exception):
                pose_chat.completion_url(endpoint)

    def test_fenced_json(self):
        self.assertEqual(pose_chat.parse_plan('```json\n{"reply":"ok","edits":[]}\n```'), ("ok", []))


if __name__ == "__main__":
    unittest.main()
