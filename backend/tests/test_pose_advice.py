"""Read-only advice contracts and actual action context; no game, keys or estimator."""

import base64
import copy
import io
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

import httpx
import numpy as np
from fastapi.testclient import TestClient
from PIL import Image
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import app as service, pose_chat
from posegraph.pose_advice import PoseAdviceRequest, build_advice_context
from posegraph.prompt_pose import apply_edits
from posegraph.retarget.brio_pose import BrioPose
from posegraph.retarget.races import describe


class AdviceActionContextTests(unittest.TestCase):
    def setUp(self):
        self.race = "c0101"
        self.pose = apply_edits(None, self.race, [], False)["pose"]
        self.provider = {"endpoint": "https://model.example/v1", "model": "text-model"}

    def context(self, pose=None, race=None, **kwargs):
        race = race or self.race
        req = PoseAdviceRequest(pose=pose or self.pose, race=race, provider=self.provider, **kwargs)
        return build_advice_context(req, describe(race))

    def test_body_correspondence_explains_real_bones_side_chain_and_axes(self):
        context = self.context()
        bones = {b["bone"]: b for b in context["bones"]}
        self.assertEqual(context["body_correspondence"]["j_ude_b_l"]["label"], "left forearm / elbow")
        self.assertIn("left index finger", context["body_correspondence"]["j_hito_a_l"]["regions"])
        self.assertIn("distal finger", context["body_correspondence"]["iv_hito_c_l"]["label"])
        self.assertIn("short knee intermediate", context["body_correspondence"]["j_asi_b_l"]["label"])
        left_arm = next(c for c in context["anatomical_chains"] if c["region"] == "left arm")
        self.assertEqual(left_arm["bones_proximal_to_distal"], ["j_sako_l", "j_ude_a_l", "j_ude_b_l", "j_te_l"])
        self.assertTrue(left_arm["complete"])
        self.assertIn("+x character left", context["axes"])
        self.assertEqual(bones["j_te_l"]["parent"], "j_ude_b_l")
        np.testing.assert_allclose(np.linalg.det(bones["j_te_l"]["current_local_axes_model"]), 1, atol=1e-3)
        self.assertEqual(len(bones["j_te_l"]["bind_relative_xyz_degrees"]), 3)

    def test_raised_hand_has_measured_own_side_offsets_and_changed_action(self):
        before = self.context()
        head = np.array(next(b for b in before["bones"] if b["bone"] == "j_kao")["position"])
        target = head + [0.12, 0.10, 0]
        edited = apply_edits(self.pose, self.race, [{"op": "move", "bone": "j_te_l", "target": target.tolist()}], False)["pose"]
        after = self.context(edited)
        relationships = after["motion_relationships"]
        np.testing.assert_allclose(relationships["arms"]["left"]["delta_to_head_m"], [0.12, 0.1, 0], atol=2e-6)
        self.assertLess(before["motion_relationships"]["arms"]["left"]["delta_to_head_m"][1], 0)
        self.assertGreater(relationships["arms"]["left"]["delta_to_head_m"][1], 0)
        self.assertGreater(relationships["arms"]["left"]["elbow_flexion_deg"], 20)
        self.assertEqual(relationships["arms"]["right"], before["motion_relationships"]["arms"]["right"])
        self.assertEqual(relationships["skeletal_chain_height_m"], before["motion_relationships"]["skeletal_chain_height_m"])

    def test_measurements_scale_with_character_and_do_not_use_fixed_human_height(self):
        normal = self.context()
        scaled = BrioPose.loads(self.pose)
        for name in scaled.bone_names:
            t = scaled.get(name)
            t.position *= 0.5
            scaled.set(name, t)
        half = self.context(scaled.dumps())
        for side in ("left", "right"):
            a = normal["motion_relationships"]["arms"][side]
            b = half["motion_relationships"]["arms"][side]
            np.testing.assert_allclose(b["delta_to_head_normalized"], a["delta_to_head_normalized"], atol=3e-6)
            self.assertAlmostEqual(b["limb_length_m"] * 2, a["limb_length_m"], places=5)
        self.assertAlmostEqual(half["motion_relationships"]["skeletal_chain_height_m"] * 2,
                               normal["motion_relationships"]["skeletal_chain_height_m"], places=5)
        small = self.context(apply_edits(None, "c1101", [], False)["pose"], "c1101")
        self.assertLess(small["motion_relationships"]["skeletal_chain_height_m"], normal["motion_relationships"]["skeletal_chain_height_m"])

    def test_no_missing_ivcs_or_custom_bone_anatomy_is_invented(self):
        pose = BrioPose.loads(self.pose)
        for name in list(pose.bone_names):
            if name.startswith("iv_"):
                del pose.raw["Bones"][name]
        pose.raw["Bones"]["custom_accessory"] = {"Position": "1,2,3", "Rotation": "0,0,0,1", "Scale": "1,1,1"}
        context = self.context(pose.dumps())
        self.assertFalse(any(n.startswith("iv_") for n in context["body_correspondence"]))
        self.assertNotIn("custom_accessory", context["body_correspondence"])
        custom = next(b for b in context["bones"] if b["bone"] == "custom_accessory")
        self.assertFalse(custom["editable"])
        self.assertEqual(custom["label"], "custom_accessory")
        self.assertIn("unavailable", custom["status"])

    def test_supplied_dynamic_face_reference_is_preserved_and_visible(self):
        actor = BrioPose.loads(self.pose)
        head = actor.get("j_kao")
        # A real actor descriptor can provide face controls absent from the
        # bundled race skeleton; advice must use that same inventory as chat.
        eyelid = "j_f_mabup_l"
        t = copy.deepcopy(head)
        t.position += [0, 0.01, 0.04]
        actor.set(eyelid, t)
        rig = {"bones": [{"name": eyelid, "parent": "j_kao", "reference_position": [0, 0.01, 0.04],
                           "reference_rotation": [0, 0, 0, 1], "reference_scale": [1, 1, 1]}]}
        before = actor.dumps()
        context = self.context(rig=rig, actor_pose=before)
        row = next(b for b in context["bones"] if b["bone"] == eyelid)
        self.assertEqual(row["label"], "left upper eyelid")
        self.assertEqual(row["parent"], "j_kao")
        self.assertTrue(row["editable"])
        self.assertIn(eyelid, context["capabilities"]["eyes"]["bones"])
        self.assertEqual(actor.dumps(), before)

    def test_partial_pose_does_not_get_default_body_landmarks_or_reach(self):
        pose = BrioPose.loads(self.pose)
        pose.raw["Bones"] = {"j_te_l": pose.raw["Bones"]["j_te_l"]}
        context = self.context(pose.dumps())
        self.assertEqual(context["landmarks"], {})
        self.assertIsNone(context["height_to_head"])
        self.assertIsNone(context["motion_relationships"]["skeletal_chain_height_m"])
        self.assertEqual(context["motion_relationships"]["arms"], {})
        self.assertEqual(set(context["body_correspondence"]), {"j_te_l"})

    def test_head_anatomical_direction_follows_actual_rotation_in_model_space(self):
        before = self.context()
        pose = BrioPose.loads(self.pose)
        yaw = R.from_euler("y", 45, degrees=True)
        for name in pose.bone_names:
            t = pose.get(name)
            t.position = yaw.apply(t.position)
            t.rotation = (yaw * R.from_quat(t.rotation)).as_quat()
            pose.set(name, t)
        context = self.context(pose.dumps())
        for region in ("head", "chest", "pelvis"):
            old = before["motion_relationships"]["anatomical_orientation"][region]
            new = context["motion_relationships"]["anatomical_orientation"][region]
            np.testing.assert_allclose(new["forward_model"], yaw.apply(old["forward_model"]), atol=1.5e-4)
            np.testing.assert_allclose(new["up_model"], yaw.apply(old["up_model"]), atol=1.5e-4)


class PoseAdviceApiTests(unittest.TestCase):
    def setUp(self):
        self.client = TestClient(service.app)
        self.addCleanup(self.client.close)
        self.pose = apply_edits(None, "c0101", [], False)["pose"]
        self.payload = {"pose": self.pose, "race": "c0101", "provider": {
            "endpoint": "https://model.example/v1", "api_key": "advice-secret-key", "model": "text-model"}}
        self.plan = {"summary": "Left arm is bent near the head.", "suggestions": [
            {"bone": "j_te_l", "issue": "Check the wrist angle", "evidence": "Current wrist bind-relative angle",
             "recommendation": "If you want a relaxed gesture, reduce wrist bend slightly."}],
            "limitations": ["There is no current game rendering or collision mesh."]}
        self.requests = []
        self.raw_content = None
        self.envelope = None
        self.status = 200
        self.throw = None
        self.finish = "stop"
        original_client = httpx.AsyncClient

        def handler(request):
            self.requests.append(request)
            if self.throw:
                raise self.throw("advice-secret-key", request=request)
            if self.status != 200:
                return httpx.Response(self.status, json={"error": "advice-secret-key"})
            if self.envelope is not None:
                return httpx.Response(200, json=self.envelope)
            return httpx.Response(200, json={"choices": [{"finish_reason": self.finish, "message": {
                "content": self.raw_content if self.raw_content is not None else json.dumps(self.plan)}}]})

        self.transport_patch = patch.object(pose_chat.httpx, "AsyncClient", side_effect=lambda **kwargs:
            original_client(transport=httpx.MockTransport(handler), **kwargs))
        self.transport_patch.start()
        self.addCleanup(self.transport_patch.stop)

    def send(self, language="en", **updates):
        payload = copy.deepcopy(self.payload)
        payload.update(updates)
        return self.client.post("/chat/advice", json=payload, headers={"Accept-Language": language})

    def sent_context(self):
        body = json.loads(self.requests[-1].content)
        text = body["messages"][-1]["content"]
        if isinstance(text, list):
            text = text[0]["text"]
        return json.loads(text.split("\n", 1)[1].split("\nUser focus (optional data):\n", 1)[0])

    def test_explicit_request_is_read_only_and_transmits_action_and_rules_not_credentials(self):
        original = self.payload["pose"]
        warning = {"bone": "j_kubi", "level": "warn", "value": 40, "limit": 35,
                   "message": "Neck turn is large", "group": "neck", "suggest": 0.7}
        with patch.object(service._estimator, "load", side_effect=AssertionError("SAM loaded")), \
             patch("posegraph.prompt_pose.apply_edits", side_effect=AssertionError("Pose edited")):
            response = self.send(focus="Keep the raised hand", params={"amplitude": {"neck": 0.9}}, warnings=[warning])
        self.assertEqual(response.status_code, 200, response.text)
        self.assertEqual(response.json(), self.plan)
        self.assertEqual(self.payload["pose"], original)
        body = json.loads(self.requests[-1].content)
        self.assertEqual(len(body["messages"]), 2)
        self.assertIn("read-only", body["messages"][0]["content"])
        self.assertNotIn("advice-secret-key", self.requests[-1].content.decode())
        self.assertEqual(self.requests[-1].headers["Authorization"], "Bearer advice-secret-key")
        context = self.sent_context()
        self.assertEqual(context["rule_warnings"], [warning])
        self.assertEqual(context["retarget_params"]["amplitude"], {"neck": 0.9})
        self.assertEqual(context["response_language"], "English")
        self.assertEqual(context["analysis_mode"], "read_only_current_pose")
        self.assertIn("motion_relationships", context)
        self.assertIn("body_correspondence", context)
        self.assertNotIn("image_url", self.requests[-1].content.decode())
        self.assertNotIn("pose", response.json())
        self.assertNotIn("history", response.json())

    def test_empty_suggestions_and_whole_body_null_bone_are_valid(self):
        self.plan["suggestions"] = []
        self.plan["limitations"] = []
        self.assertEqual(self.send().status_code, 200)
        self.plan["suggestions"] = [{"bone": None, "issue": "Optional torso refinement", "evidence": "Torso angles",
                                     "recommendation": "Keep the intentional lean if desired."}]
        self.assertEqual(self.send().status_code, 200)

    def test_reference_is_opt_in_and_original_session_is_unchanged(self):
        sid = "advice-test-image"
        stream = io.BytesIO()
        Image.new("RGB", (64, 48), "green").save(stream, format="PNG")
        image = stream.getvalue()
        service._sessions[sid] = service.Session(sid, "reference", (64, 48), image, [], 0)
        self.addCleanup(lambda: service._sessions.pop(sid, None))
        self.assertEqual(self.send(session_id=sid).status_code, 200)
        self.assertFalse(self.sent_context()["reference_image"]["included"])
        self.assertEqual(self.send(session_id=sid, include_image=True).status_code, 200)
        parts = json.loads(self.requests[-1].content)["messages"][-1]["content"]
        self.assertEqual(parts[1]["type"], "image_url")
        encoded = parts[1]["image_url"]["url"].split(",", 1)[1]
        with Image.open(io.BytesIO(base64.b64decode(encoded))) as sent:
            self.assertEqual(sent.format, "JPEG")
        self.assertEqual(self.sent_context()["reference_image"], {
            "included": True, "source": "original imported reference", "current_game_render": False})
        self.assertEqual(service._sessions[sid].preview_png, image)

    def test_image_absence_missing_session_and_invalid_image_are_safe(self):
        self.assertEqual(self.send(include_image=True).status_code, 400)
        self.assertEqual(self.send(include_image=True, session_id="expired").status_code, 404)
        self.assertFalse(self.requests)
        self.assertEqual(self.send(session_id="expired").status_code, 200)
        sid = "advice-invalid-image"
        service._sessions[sid] = service.Session(sid, "invalid", (1, 1), b"invalid", [], 0)
        self.addCleanup(lambda: service._sessions.pop(sid, None))
        count = len(self.requests)
        self.assertEqual(self.send(include_image=True, session_id=sid).status_code, 400)
        self.assertEqual(len(self.requests), count)

    def test_request_validation_redacts_secrets_and_rejects_invalid_pose_and_rig(self):
        for update in ({"pose": " "}, {"provider": {"api_key": "advice-secret-key"}},
                       {"provider": {**self.payload["provider"], "api_key": "advice-secret-key\r\n"}},
                       {"focus": "x" * 1001}, {"warnings": [{"message": "advice-secret-key"}]},
                       {"params": {"amplitude": {"wrong": 0.8}}}, {"params": {"offsets": {"j_kubi": [1, 2]}}}):
            with self.subTest(update=update):
                response = self.send(**update)
                self.assertEqual(response.status_code, 422, response.text)
                self.assertNotIn("advice-secret-key", response.text)
        for pose in ("not-json", "[]", '{"Bones":[]}', '{"Bones":{}}', '{"Bones":{"j_kubi":{"Position":"nan,0,0"}}}'):
            self.assertEqual(self.send(pose=pose).status_code, 400)
        self.assertEqual(self.send(race="unknown").status_code, 400)
        self.assertEqual(self.send(rig={"bones": [{"name": "custom", "parent": "custom", "reference_position": [0, 0, 0],
                                                  "reference_rotation": [0, 0, 0, 1], "reference_scale": [1, 1, 1]}]}).status_code, 400)
        self.assertFalse(self.requests)

    def test_response_schema_unknown_bones_oversized_and_executable_payloads_rejected(self):
        suggestion = self.plan["suggestions"][0]
        bad = ["not-json", {**self.plan, "edits": []}, {**self.plan, "summary": " "},
               {**self.plan, "summary": 12}, {**self.plan, "suggestions": [suggestion] * 6},
               {**self.plan, "suggestions": [{**suggestion, "bone": "invented_bone"}]},
               {**self.plan, "suggestions": [{**suggestion, "recommendation": "x" * 801}]},
               {**self.plan, "suggestions": [{**suggestion, "op": "exec"}]},
               {**self.plan, "limitations": ["x"] * 5}, {**self.plan, "limitations": [1]}]
        for value in bad:
            self.raw_content = value if isinstance(value, str) else json.dumps(value)
            response = self.send()
            self.assertEqual(response.status_code, 502, response.text)
            self.assertNotIn("advice-secret-key", response.text)
        self.raw_content = "```json\n" + json.dumps(self.plan) + "\n```"
        self.assertEqual(self.send().status_code, 200)

    def test_provider_errors_redacted_envelopes_bounded_and_local_auth_optional(self):
        for status in (302, 400, 401, 403, 429, 500):
            self.status = status
            response = self.send()
            self.assertEqual(response.status_code, 502)
            self.assertNotIn("advice-secret-key", response.text)
        self.status = 200
        for envelope in ({"choices": [None]}, {"choices": []}, {"choices": [{"message": None}]},
                         {"choices": [{"message": {"content": []}}]}):
            self.envelope = envelope
            self.assertEqual(self.send().status_code, 502)
        self.envelope = None
        self.finish = "length"
        self.assertEqual(self.send().status_code, 502)
        self.finish = "stop"
        self.raw_content = "x" * (pose_chat.MAX_PROVIDER_BYTES + 1)
        self.assertEqual(self.send().status_code, 502)
        self.raw_content = None
        for error, expected in ((httpx.ReadTimeout, 504), (httpx.ConnectError, 502)):
            self.throw = error
            response = self.send()
            self.assertEqual(response.status_code, expected)
            self.assertNotIn("advice-secret-key", response.text)
        self.throw = None
        self.payload["provider"]["api_key"] = ""
        self.assertEqual(self.send().status_code, 200)
        self.assertNotIn("Authorization", self.requests[-1].headers)

    def test_language_and_null_params_for_current_actor_analysis(self):
        self.assertEqual(self.send(language="zh", params=None).status_code, 200)
        context = self.sent_context()
        self.assertEqual(context["response_language"], "简体中文")
        self.assertIsNone(context["retarget_params"])
        self.assertFalse(context["evidence_limits"]["mesh"])
        self.assertFalse(context["evidence_limits"]["camera"])
        self.raw_content = "bad-json"
        self.assertIn("模型未返回有效的姿势建议", self.send(language="zh").json()["detail"])
        self.assertIn("valid pose advice", self.send(language="en").json()["detail"])


if __name__ == "__main__":
    unittest.main()
