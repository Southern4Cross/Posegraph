"""Dynamic facial rig math and exact preservation, independent of any LLM/game."""

import copy
import json
import sys
import unittest
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph.prompt_pose import PoseEditError, apply_edits, build_context
from posegraph.retarget.brio_pose import BrioPose


def fixture():
    source = json.loads(apply_edits(None, "c0101", [], False)["pose"])
    source["Custom"] = {"keep": ["metadata", 42]}
    # Body-only refs may be augmented with real facial bones; all fields still
    # receive strict validation and may only target supplied loaded names.
    metadata = {"bones": []}
    fmt = lambda a: ", ".join(format(float(v), ".17g") for v in a)
    for side, sign in (("l", 1), ("r", -1)):
        for name, parent, translation, degrees in (
            (f"j_f_mab_{side}", "j_kao", [0.02 * sign, 0.01, 0.02], [0, 0, 7]),
            (f"j_f_mabup_01_{side}", f"j_f_mab_{side}", [0, 0.005, 0], [0, 0, 25]),
            (f"j_f_mabdn_01_{side}", f"j_f_mab_{side}", [0, -0.005, 0], [0, 0, -20]),
            (f"j_f_eye_{side}", "j_kao", [0.02 * sign, 0.01, 0.02], [0, 15, 0]),
            (f"j_f_mayu_{side}", "j_kao", [0.02 * sign, 0.03, 0.02], [6, 0, 0]),
        ):
            metadata["bones"].append({"name": name, "parent": parent, "reference_position": translation,
                                      "reference_rotation": [0, 0, 0, 1], "reference_scale": [1, 1, 1]})
            p = source["Bones"][parent]
            g = R.from_quat(np.fromstring(p["Rotation"], sep=","))
            position = np.fromstring(p["Position"], sep=",") + g.apply(translation)
            source["Bones"][name] = {"Position": fmt(position),
                                     "Rotation": fmt((g * R.from_euler("xyz", degrees, degrees=True)).as_quat()),
                                     "Scale": "1, 1, 1"}
    return source, metadata


class FacialPoseTests(unittest.TestCase):
    def setUp(self):
        self.source, self.metadata = fixture()

    def apply(self, edits, source=None, ground=True):
        source = source or self.source
        return json.loads(apply_edits(json.dumps(source), "c0101", edits, ground, self.metadata, json.dumps(self.source))["pose"])

    def test_eyes_restore_neutral_local_transforms_and_are_idempotent(self):
        result = self.apply([{"op": "eyes", "state": "open", "side": "both"}])
        parsed = BrioPose(result)
        for record in self.metadata["bones"]:
            if not record["name"].startswith("j_f_mab"):
                continue
            child, parent = parsed.get(record["name"]), parsed.get(record["parent"])
            local = R.from_quat(parent.rotation).inv() * R.from_quat(child.rotation)
            np.testing.assert_allclose(local.as_matrix(), np.eye(3), atol=1e-8)
            position = R.from_quat(parent.rotation).inv().apply(child.position - parent.position)
            np.testing.assert_allclose(position, record["reference_position"], atol=2e-8)
        repeated = self.apply([{"op": "eyes", "state": "open", "side": "both"}], result)
        self.assertEqual(result, repeated)

    def test_eye_only_change_preserves_every_other_raw_transform_and_metadata(self):
        result = self.apply([{"op": "eyes", "state": "open", "side": "left"}])
        self.assertEqual(set(result["Bones"]), set(self.source["Bones"]))
        for name, value in self.source["Bones"].items():
            if name.startswith("j_f_mab") and name.endswith("_l"):
                continue
            self.assertEqual(result["Bones"][name], value, name)
        self.assertEqual(result["Custom"], self.source["Custom"])

    def test_face_apply_patch_contains_only_changed_lids(self):
        self.source["MainHand"] = {"unrelated_weapon": {"Position": "1, 2, 3"}}
        self.source["OffHand"] = {"unrelated_shield": {"Scale": "1, 1, 1"}}
        response = apply_edits(json.dumps(self.source), "c0101",
                               [{"op": "eyes", "state": "open", "side": "both"}],
                               True, self.metadata, json.dumps(self.source))
        full, patch = json.loads(response["pose"]), json.loads(response["applyPose"])
        changed = {name: value for name, value in full["Bones"].items()
                   if self.source["Bones"].get(name) != value}
        self.assertTrue(changed)
        self.assertEqual(patch["Bones"], changed)
        self.assertTrue(all(name.startswith("j_f_mab") for name in patch["Bones"]))
        self.assertTrue(response["facialOnly"])
        self.assertEqual(patch["Custom"], self.source["Custom"])
        for channel in ("MainHand", "OffHand", "Prop", "Ornament"):
            self.assertEqual(patch[channel], {})
            if channel in self.source:
                self.assertEqual(full[channel], self.source[channel])

    def test_body_apply_patch_is_not_facial_and_noop_patch_is_empty(self):
        response = apply_edits(json.dumps(self.source), "c0101",
                               [{"op": "rotate", "bone": "j_kao", "rotation": [0, 10, 0]}],
                               False, self.metadata, json.dumps(self.source))
        self.assertFalse(response["facialOnly"])
        self.assertIn("j_kao", json.loads(response["applyPose"])["Bones"])
        noop = apply_edits(json.dumps(self.source), "c0101", [], True, self.metadata, json.dumps(self.source))
        self.assertEqual(json.loads(noop["applyPose"])["Bones"], {})
        self.assertTrue(noop["facialOnly"])

    def test_head_rotation_carries_actual_face_descendants(self):
        result = self.apply([{"op": "rotate", "bone": "j_kao", "rotation": [0, 10, 0]}], ground=False)
        before, after = BrioPose(self.source), BrioPose(result)
        head_before, head_after = before.get("j_kao"), after.get("j_kao")
        delta = R.from_quat(head_after.rotation) * R.from_quat(head_before.rotation).inv()
        for name in ("j_f_eye_l", "j_f_mabup_01_r", "j_f_mayu_l"):
            expected = head_after.position + delta.apply(before.get(name).position - head_before.position)
            np.testing.assert_allclose(after.get(name).position, expected, atol=1e-7)
        for name in ("j_kosi", "j_te_l", "j_te_r", "j_asi_d_l"):
            self.assertEqual(result["Bones"][name], self.source["Bones"][name])

    def test_facial_translation_is_local_and_bounded(self):
        name = "j_f_mayu_l"
        result = self.apply([{"op": "translate", "bone": name, "translation": [0.001, 0, 0]}])
        before, after = BrioPose(self.source), BrioPose(result)
        head = before.get("j_kao")
        expected = before.get(name).position + R.from_quat(head.rotation).apply([0.001, 0, 0])
        np.testing.assert_allclose(after.get(name).position, expected, atol=1e-8)
        for bone, value in self.source["Bones"].items():
            if bone != name:
                self.assertEqual(result["Bones"][bone], value, bone)
        with self.assertRaises(PoseEditError):
            self.apply([{"op": "translate", "bone": name, "translation": [0.151, 0, 0]}])

    def test_real_face_inventory_is_present_without_unverified_capabilities(self):
        context = build_context(json.dumps(self.source), "c0101")
        inventory = {row["bone"]: row for row in context["bones"]}
        self.assertIn("j_f_mabup_01_l", inventory)
        self.assertFalse(inventory["j_f_mabup_01_l"]["editable"])
        self.assertIsNone(context["capabilities"]["eyes"])
        with self.assertRaises(PoseEditError):
            apply_edits(json.dumps(self.source), "c0101", [{"op": "eyes", "state": "open", "side": "both"}])

    def test_invalid_reference_metadata_and_edit_schemas_fail_atomically(self):
        variants = []
        duplicate = copy.deepcopy(self.metadata)
        duplicate["bones"].append(copy.deepcopy(duplicate["bones"][0]))
        variants.append(duplicate)
        for parent in ("missing_parent", self.metadata["bones"][0]["name"]):
            bad = copy.deepcopy(self.metadata)
            bad["bones"][0]["parent"] = parent
            variants.append(bad)
        for key, value in (("reference_rotation", [0, 0, 0, 0]),
                           ("reference_position", [0, float("nan"), 0]),
                           ("reference_scale", [-1, 1, 1])):
            bad = copy.deepcopy(self.metadata)
            bad["bones"][0][key] = value
            variants.append(bad)
        for metadata in variants:
            with self.subTest(metadata=metadata), self.assertRaises(PoseEditError):
                build_context(json.dumps(self.source), "c0101", metadata)
        for edit in ({"op": "eyes", "state": "closed", "side": "both"},
                     {"op": "eyes", "state": "open", "side": "invented"},
                     {"op": "reset", "bone": "j_f_mab_l", "extra": True},
                     {"op": "reset", "bone": "j_f_invented_l"}):
            with self.subTest(edit=edit), self.assertRaises(PoseEditError):
                self.apply([edit])


if __name__ == "__main__":
    unittest.main()
