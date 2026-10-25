"""Facial chat contract and preservation checks; no model service or game needed."""

import copy
import json
import sys
import unittest
from pathlib import Path
from unittest.mock import patch

import httpx
import numpy as np
from fastapi.testclient import TestClient
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import app as service
from posegraph import pose_chat
from posegraph.retarget.ffxiv import forward_kinematics, load_ffxiv_skeleton


def facial_fixture():
    """A loaded head with non-neutral lids and deliberately elevated body."""
    sk = load_ffxiv_skeleton("c0101")
    rotations, positions = forward_kinematics(sk)
    fmt = lambda values: ", ".join(format(float(v), ".17g") for v in values)
    pose = {
        "Bones": {}, "FileVersion": 3, "TypeName": "Brio Pose", "FileExtension": ".pose",
        "ModelAbsoluteValues": {"Position": "5, 8, 13", "Rotation": "0, 0, 0, 1", "Scale": "1, 1, 1"},
        "MainHand": {"custom_weapon": {"Position": "1, 2, 3"}},
        "CustomMetadata": {"must": "survive"},
    }
    rig = {"bones": []}
    global_r, global_p = {}, {}
    for i, name in enumerate(sk.names):
        global_r[name] = rotations[i]
        global_p[name] = positions[i] + [0, 0.35, 0]
        pose["Bones"][name] = {"Position": fmt(global_p[name]), "Rotation": fmt(rotations[i].as_quat()), "Scale": "1, 1, 1"}
        rig["bones"].append({
            "name": name, "parent": sk.names[sk.parents[i]] if sk.parents[i] >= 0 else None,
            "reference_position": sk.local_t[i].tolist(), "reference_rotation": sk.local_r[i].as_quat().tolist(),
            "reference_scale": [1, 1, 1],
        })

    def extra(name, parent, position, closed_rotation=None, closed_shift=None):
        reference = {"name": name, "parent": parent, "reference_position": position,
                     "reference_rotation": [0, 0, 0, 1], "reference_scale": [1, 1, 1]}
        rig["bones"].append(reference)
        local_r = R.from_euler("xyz", closed_rotation or [0, 0, 0], degrees=True)
        local_p = np.array(position) + np.array(closed_shift or [0, 0, 0])
        global_r[name] = global_r[parent] * local_r
        global_p[name] = global_p[parent] + global_r[parent].apply(local_p)
        pose["Bones"][name] = {"Position": fmt(global_p[name]), "Rotation": fmt(global_r[name].as_quat()), "Scale": "1, 1, 1"}

    extra("j_f_face", "j_kao", [0.01, 0, 0])
    for side, sign in (("l", 1), ("r", -1)):
        extra(f"j_f_eye_{side}", "j_f_face", [sign * 0.03, 0.015, 0.02], [0, 12, 0])
        extra(f"j_f_mayu_{side}", "j_f_face", [sign * 0.03, 0.035, 0.01], [8, 0, 0])
        extra(f"j_f_mab_{side}", "j_f_face", [sign * 0.03, 0.015, 0.02], [0, 0, 8])
        extra(f"j_f_mabup_01_{side}", f"j_f_mab_{side}", [0, 0.005, 0.002], [0, 0, 20], [0, -0.004, 0])
        extra(f"j_f_mabdn_01_{side}", f"j_f_mab_{side}", [0, -0.005, 0.002], [0, 0, -15], [0, 0.004, 0])
    extra("custom_hair_tip", "j_kao", [0.01, 0.08, -0.03], [5, 0, 0])
    return pose, rig


def find_bone_record(value, name):
    if isinstance(value, dict):
        if value.get("bone", value.get("name")) == name:
            return value
        for child in value.values():
            found = find_bone_record(child, name)
            if found is not None:
                return found
    elif isinstance(value, list):
        for child in value:
            found = find_bone_record(child, name)
            if found is not None:
                return found
    return None


class FacialChatApiTests(unittest.TestCase):
    def setUp(self):
        self.source, self.rig = facial_fixture()
        self.requests = []
        self.edits = [{"op": "eyes", "state": "open", "side": "both"}]
        self.client = TestClient(service.app)
        original_client = httpx.AsyncClient

        def handler(request):
            self.requests.append(request)
            plan = {"reply": "只调整眼睑，其他部位保持不变。", "edits": self.edits}
            return httpx.Response(200, json={"choices": [{"message": {"content": json.dumps(plan)}, "finish_reason": "stop"}]})

        transport_patch = patch.object(pose_chat.httpx, "AsyncClient", side_effect=lambda **kwargs:
            original_client(transport=httpx.MockTransport(handler), **kwargs))
        transport_patch.start()
        self.addCleanup(transport_patch.stop)
        estimator_patch = patch.object(service._estimator, "load", side_effect=AssertionError("Facial chat loaded SAM"))
        estimator_patch.start()
        self.addCleanup(estimator_patch.stop)
        self.addCleanup(self.client.close)
        self.payload = {
            "prompt": "保持其他部分不变，让眼睛睁开", "race": "c0101", "ground": True,
            "pose": json.dumps(self.source), "actor_pose": json.dumps(self.source), "rig": self.rig,
            "provider": {"endpoint": "https://model.example/v1", "model": "text-model", "api_key": "facial-test-secret"},
        }

    def send(self, **updates):
        payload = copy.deepcopy(self.payload)
        payload.update(updates)
        return self.client.post("/chat/pose", json=payload, headers={"Accept-Language": "en"})

    def context(self):
        body = json.loads(self.requests[-1].content)
        message = body["messages"][-1]["content"]
        self.assertIsInstance(message, str)
        context = message.split("\nUser instruction:\n", 1)[0].split("\n", 1)[1]
        return json.loads(context)

    def assert_only_lids_changed(self, result, sides=("l", "r"), base=None):
        before = base or self.source
        changed = {name for name in before["Bones"] if before["Bones"][name] != result["Bones"][name]}
        allowed = {name for name in before["Bones"] if name.startswith("j_f_mab") and name.endswith(tuple("_" + side for side in sides))}
        self.assertTrue(changed)
        self.assertLessEqual(changed, allowed)
        for key in before:
            if key != "Bones":
                self.assertEqual(result[key], before[key])
        for name in before["Bones"]:
            if name not in allowed:
                self.assertEqual(result["Bones"][name], before["Bones"][name], name)

    def test_model_receives_loaded_face_and_parent_hierarchy(self):
        response = self.send()
        self.assertEqual(response.status_code, 200, response.text)
        context = self.context()
        for name, parent in (("j_f_face", "j_kao"), ("j_f_mab_l", "j_f_face"),
                             ("j_f_mabup_01_l", "j_f_mab_l"), ("custom_hair_tip", "j_kao")):
            record = find_bone_record(context, name)
            self.assertIsNotNone(record, name)
            self.assertEqual(record.get("parent"), parent)
        self.assertIn("eyes", json.dumps(context).lower())

    def test_eyes_open_does_not_ground_or_reserialize_unrelated_body_and_gaze(self):
        response = self.send()
        self.assertEqual(response.status_code, 200, response.text)
        preview = response.json()["result"]
        result = json.loads(preview["pose"])
        self.assert_only_lids_changed(result)
        self.assertEqual(set(result["Bones"]), set(self.source["Bones"]))
        changed = {name: value for name, value in result["Bones"].items() if value != self.source["Bones"][name]}
        self.assertEqual(json.loads(preview["applyPose"])["Bones"], changed)
        self.assertTrue(preview["facialOnly"])

    def test_left_eye_only_preserves_right_lids(self):
        self.edits = [{"op": "eyes", "state": "open", "side": "left"}]
        response = self.send()
        self.assertEqual(response.status_code, 200, response.text)
        self.assert_only_lids_changed(json.loads(response.json()["result"]["pose"]), sides=("l",))

    def test_body_preview_merges_face_from_actor_before_model_request(self):
        preview = copy.deepcopy(self.source)
        for name in list(preview["Bones"]):
            if name.startswith("j_f_") or name == "custom_hair_tip":
                del preview["Bones"][name]
        response = self.send(pose=json.dumps(preview))
        self.assertEqual(response.status_code, 200, response.text)
        self.assertIsNotNone(find_bone_record(self.context(), "j_f_mabup_01_l"))
        result = json.loads(response.json()["result"]["pose"])
        self.assert_only_lids_changed(result)

    def test_missing_face_follows_the_preview_head_instead_of_staying_at_live_head(self):
        preview = copy.deepcopy(self.source)
        sk = load_ffxiv_skeleton("c0101")
        head = sk.index("j_kao")
        pivot = np.fromstring(self.source["Bones"]["j_kao"]["Position"], sep=",")
        delta = R.from_euler("y", 17, degrees=True)
        fmt = lambda values: ", ".join(format(float(v), ".17g") for v in values)
        for name in list(preview["Bones"]):
            if name.startswith("j_f_") or name == "custom_hair_tip":
                del preview["Bones"][name]
                continue
            ancestor = sk.index(name)
            while ancestor >= 0 and ancestor != head:
                ancestor = sk.parents[ancestor]
            if ancestor != head:
                continue
            transform = preview["Bones"][name]
            position = np.fromstring(transform["Position"], sep=",")
            rotation = R.from_quat(np.fromstring(transform["Rotation"], sep=","))
            transform["Position"] = fmt(pivot + delta.apply(position - pivot))
            transform["Rotation"] = fmt((delta * rotation).as_quat())
        self.edits = []
        response = self.send(pose=json.dumps(preview))
        self.assertEqual(response.status_code, 200, response.text)
        result = json.loads(response.json()["result"]["pose"])
        for name in ("j_f_face", "j_f_eye_l", "j_f_mabup_01_r", "custom_hair_tip"):
            before = np.fromstring(self.source["Bones"][name]["Position"], sep=",")
            after = np.fromstring(result["Bones"][name]["Position"], sep=",")
            np.testing.assert_allclose(after, pivot + delta.apply(before - pivot), atol=1e-7)
        for name, value in preview["Bones"].items():
            self.assertEqual(result["Bones"][name], value, name)

    def test_second_turn_keeps_unapplied_face_preview_instead_of_old_actor_face(self):
        first = self.send()
        self.assertEqual(first.status_code, 200, first.text)
        first = first.json()
        opened = json.loads(first["result"]["pose"])
        self.edits = []
        second = self.send(prompt="保持刚才的睁眼状态，不需要再修改", pose=first["result"]["pose"], history=first["history"])
        self.assertEqual(second.status_code, 200, second.text)
        self.assertEqual(json.loads(second.json()["result"]["pose"]), opened)
        self.assertEqual(len(second.json()["history"]), 4)

    def test_default_standing_start_can_still_describe_loaded_face(self):
        self.edits = []
        response = self.send(pose=None)
        self.assertEqual(response.status_code, 200, response.text)
        self.assertIsNotNone(find_bone_record(self.context(), "j_f_mabup_01_l"))
        result = json.loads(response.json()["result"]["pose"])
        original_head = np.fromstring(self.source["Bones"]["j_kao"]["Position"], sep=",")
        standing_head = np.fromstring(result["Bones"]["j_kao"]["Position"], sep=",")
        self.assertGreater(np.linalg.norm(original_head - standing_head), 0.1)
        self.assertIn("j_f_eye_l", result["Bones"])
        self.assertEqual(result["ModelAbsoluteValues"], self.source["ModelAbsoluteValues"])

    def test_missing_rig_cannot_invent_eye_control(self):
        response = self.send(rig=None)
        self.assertEqual(response.status_code, 502, response.text)
        self.assertNotIn("facial-test-secret", response.text)

    def test_missing_face_bone_cannot_be_invented_by_model(self):
        self.edits = [{"op": "rotate", "bone": "j_f_invented_l", "rotation": [0, 5, 0]}]
        response = self.send()
        self.assertEqual(response.status_code, 502, response.text)

    def test_invalid_rig_schema_does_not_echo_provider_credentials(self):
        response = self.send(rig={"bones": [{"name": "j_f_mab_l", "reference_position": [0]}]})
        self.assertEqual(response.status_code, 422)
        self.assertNotIn("facial-test-secret", response.text)
        self.assertFalse(self.requests)


if __name__ == "__main__":
    unittest.main()
