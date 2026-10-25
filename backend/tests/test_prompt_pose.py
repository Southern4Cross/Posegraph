"""Portable pose math/validation tests, requiring no model or game process."""

import sys
import unittest
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph.prompt_pose import PoseEditError, apply_edits, build_context
from posegraph.retarget.brio_pose import BrioPose
from posegraph.retarget.ffxiv import load_ffxiv_skeleton


class PromptPoseTests(unittest.TestCase):
    def setUp(self):
        self.race = "c0101"
        self.sk = load_ffxiv_skeleton(self.race)
        self.base_result = apply_edits(None, self.race, [], ground=False)
        self.base = BrioPose.loads(self.base_result["pose"])

    def edit(self, edits, pose=None, ground=False):
        result = apply_edits(pose or self.base.dumps(), self.race, edits, ground)
        return BrioPose.loads(result["pose"]), result

    def test_standalone_context_and_pose_have_finite_standing_body(self):
        context = build_context(None, self.race)
        self.assertGreater(context["height_to_head"], 1)
        self.assertEqual(context["race"], self.race)
        self.assertEqual(len(context["bones"]), len({x["bone"] for x in context["bones"]}))
        self.assertTrue(self.base_result["stick"]["joints"])
        for side in "lr":
            hand, shoulder = self.base.get(f"j_te_{side}"), self.base.get(f"j_ude_a_{side}")
            self.assertLess(hand.position[1], shoulder.position[1] - 0.4)
        for value in self.base.raw["Bones"].values():
            self.assertTrue(np.isfinite([float(x) for x in value["Rotation"].split(",")]).all())

    def test_local_rotation_moves_descendants_preserves_other_arm_and_lengths(self):
        pose, _ = self.edit([dict(op="rotate", bone="j_ude_a_l", rotation=[0, 0, -35])])
        self.assertGreater(np.linalg.norm(pose.get("j_te_l").position - self.base.get("j_te_l").position), 0.1)
        for bone in ("j_te_r", "j_ude_a_r", "j_asi_d_l", "j_kao"):
            np.testing.assert_allclose(pose.get(bone).position, self.base.get(bone).position, atol=1e-7)
        for a, b in (("j_ude_a_l", "j_ude_b_l"), ("j_ude_b_l", "j_te_l")):
            length = lambda p: np.linalg.norm(p.get(a).position - p.get(b).position)
            self.assertAlmostEqual(length(pose), length(self.base), places=6)

    def test_multiturn_relative_changes_compose_and_set_is_idempotent(self):
        first, _ = self.edit([dict(op="rotate", bone="j_kao", rotation=[8, 0, 0])])
        second, _ = self.edit([dict(op="rotate", bone="j_kao", rotation=[8, 0, 0])], first.dumps())
        once, _ = self.edit([dict(op="rotate", bone="j_kao", rotation=[16, 0, 0])])
        np.testing.assert_allclose(second.get("j_kao").rotation, once.get("j_kao").rotation, atol=1e-7)
        set_edit = [dict(op="set_rotation", bone="j_kao", rotation=[0, 15, 0])]
        first, _ = self.edit(set_edit)
        second, _ = self.edit(set_edit, first.dumps())
        np.testing.assert_allclose(first.get("j_kao").rotation, second.get("j_kao").rotation, atol=1e-7)

    def test_hand_ik_hits_target_without_elbow_lateral_bend(self):
        for side, sign in (("l", 1), ("r", -1)):
            target = self.base.get("j_kao").position + [sign * 0.1, 0.1, 0]
            bone = f"j_te_{side}"
            pose, result = self.edit([dict(op="move", bone=bone, target=target.tolist())])
            np.testing.assert_allclose(pose.get(bone).position, target, atol=1e-6)
            self.assertFalse(any(w["bone"] == f"j_ude_b_{side}" and w["limit"] == 10 for w in result["warnings"]))
            other = "r" if side == "l" else "l"
            np.testing.assert_allclose(pose.get(f"j_te_{other}").position, self.base.get(f"j_te_{other}").position, atol=1e-7)

    def test_foot_ik_and_unreachable_target_preserve_bone_lengths(self):
        target = self.base.get("j_asi_d_l").position + [0.15, 0.1, 0.1]
        pose, _ = self.edit([dict(op="move", bone="j_asi_d_l", target=target.tolist())])
        np.testing.assert_allclose(pose.get("j_asi_d_l").position, target, atol=1e-6)
        pose, result = self.edit([dict(op="move", bone="j_te_l", target=[5, 5, 5])])
        self.assertTrue(any("outside limb reach" in w["message"] for w in result["warnings"]))
        for a, b in (("j_ude_a_l", "j_ude_b_l"), ("j_ude_b_l", "j_te_l")):
            expected = np.linalg.norm(self.base.get(a).position - self.base.get(b).position)
            self.assertAlmostEqual(np.linalg.norm(pose.get(a).position - pose.get(b).position), expected, places=6)

    def test_pelvis_move_moves_body_and_preserves_metadata_unknown_bones_and_scales(self):
        self.base.raw["CustomMetadata"] = {"name": "kept", "version": 7}
        self.base.raw["MainHand"] = {"weapon": {"Position": "1, 2, 3"}}
        self.base.raw["Bones"]["custom_accessory"] = {"Position": "1, 2, 3", "Rotation": "0, 0, 0, 1", "Scale": "2, 2, 2"}
        self.base.raw["Bones"]["j_te_l"]["Scale"] = "1.1, 0.9, 1.2"
        shift = np.array([0.1, -0.2, 0.3])
        target = self.base.get("j_kosi").position + shift
        pose, _ = self.edit([dict(op="move", bone="j_kosi", target=target.tolist())])
        np.testing.assert_allclose(pose.get("j_kosi").position, target, atol=1e-6)
        np.testing.assert_allclose(pose.get("j_te_l").position - self.base.get("j_te_l").position, shift, atol=1e-6)
        np.testing.assert_allclose(pose.get("j_te_l").scale, [1.1, 0.9, 1.2])
        for key in ("CustomMetadata", "MainHand"):
            self.assertEqual(pose.raw[key], self.base.raw[key])
        self.assertEqual(pose.raw["Bones"]["custom_accessory"], self.base.raw["Bones"]["custom_accessory"])

    def test_clarification_does_not_ground_or_shift_existing_pose(self):
        shifted, _ = self.edit([dict(op="move", bone="j_kosi", target=[0, 2, 0])])
        output = apply_edits(shifted.dumps(), self.race, [], ground=True)
        pose = BrioPose.loads(output["pose"])
        self.assertEqual(pose.raw, shifted.raw)
        for bone in shifted.bone_names:
            np.testing.assert_allclose(pose.get(bone).position, shifted.get(bone).position, atol=1e-7)

    def test_non_ivcs_reference_never_gets_invented_distal_bones(self):
        for name in list(self.base.bone_names):
            if name.startswith("iv_"):
                del self.base.raw["Bones"][name]
        context = build_context(self.base.dumps(), self.race)
        self.assertFalse(any(b["bone"].startswith("iv_") for b in context["bones"]))
        pose, _ = self.edit([dict(op="rotate", bone="j_hito_b_l", rotation=[0, 0, 20])])
        self.assertFalse(any(n.startswith("iv_") for n in pose.bone_names))
        with self.assertRaises(PoseEditError):
            self.edit([dict(op="rotate", bone="iv_hito_c_l", rotation=[0, 0, 20])])

    def test_grounding_returns_feet_to_existing_skeleton_floor(self):
        shifted, _ = self.edit([dict(op="move", bone="j_kosi", target=[0, 2, 0])])
        pose, result = self.edit([dict(op="rotate", bone="j_kao", rotation=[0, 5, 0])], shifted.dumps(), ground=True)
        floor = min(self.base.get(f"j_asi_e_{s}").position[1] for s in "lr")
        self.assertAlmostEqual(min(pose.get(f"j_asi_e_{s}").position[1] for s in "lr"), floor, places=6)
        self.assertGreater(result["compensation"]["ground_drop_cm"], 50)

    def test_invalid_edit_batches_fail_before_modifying_source(self):
        original = self.base.dumps()
        invalid = [
            dict(op="rotate", bone="n_root", rotation=[0, 1, 0]),
            dict(op="rotate", bone="j_kao", rotation=[0, float("nan"), 0]),
            dict(op="rotate", bone="j_kao", rotation=[0, "12", 0]),
            dict(op="rotate", bone="j_kao", rotation=[0, True, 0]),
            dict(op="rotate", bone="j_kao", rotation=[0, 181, 0]),
            dict(op="rotate", bone="j_kao", rotation=[0, 10**999, 0]),
            dict(op="rotate", bone="j_kao", rotation=[0, -10**999, 0]),
            dict(op="move", bone="j_te_l", target=[0, 10**999, 0]),
            dict(op="move", bone="j_te_l", target=[0, -10**999, 0]),
            dict(op="move", bone="j_kao", target=[0, 1, 0]),
            dict(op="run_python", bone="j_kao", rotation=[0, 1, 0]),
            dict(op="rotate", bone="j_kao", rotation=[0, 1, 0], code="evil"),
        ]
        for edit in invalid:
            with self.subTest(edit=edit), self.assertRaises(PoseEditError):
                self.edit([dict(op="rotate", bone="j_kao", rotation=[0, 5, 0]), edit])
        self.assertEqual(self.base.dumps(), original)
        with self.assertRaises(PoseEditError):
            self.edit([dict(op="rotate", bone="j_kao", rotation=[0, 0, 0])] * 65)

    def test_invalid_pose_and_race_are_rejected(self):
        for value in ("[]", "null", "{broken", '{"Bones":[]}', '{"Bones":{"j_kao":{"Position":"nan,0,0","Rotation":"0,0,0,1","Scale":"1,1,1"}}}',
                      '{"Bones":{"j_kao":{"Position":"0,1,0","Rotation":"1e999,0,0,1","Scale":"1,1,1"}}}',
                      '{"Bones":{"j_kao":{"Position":"1e999,1,0","Rotation":"0,0,0,1","Scale":"1,1,1"}}}'):
            with self.subTest(value=value), self.assertRaises(PoseEditError):
                build_context(value, self.race)
        with self.assertRaises(PoseEditError):
            build_context(None, "../../outside")

    def test_supported_races_produce_same_reachable_action_without_human_scale_assumption(self):
        for race in ("c0101", "c0201", "c0501", "c0901", "c1701"):
            context = build_context(None, race)
            target = np.array(context["landmarks"]["chest"]) + [0.05, 0.1, 0.2]
            result = apply_edits(None, race, [dict(op="move", bone="j_te_l", target=target.tolist())], False)
            pose = BrioPose.loads(result["pose"])
            np.testing.assert_allclose(pose.get("j_te_l").position, target, atol=1e-5)


if __name__ == "__main__":
    unittest.main()
