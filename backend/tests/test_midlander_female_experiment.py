"""Portable invariants for the opt-in original-body calibration.

Run: backend/.venv/Scripts/python -m unittest discover -s backend/tests -v
Real animation and Blender comparisons live in the local calibration outputs.
"""

import sys
import unittest
from pathlib import Path

import numpy as np
from scipy.spatial.transform import Rotation as R

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))
from posegraph import tuning
from posegraph.retarget import ffxiv, races
from posegraph.retarget.brio_pose import BrioPose


class MidlanderFemaleExperimentTests(unittest.TestCase):
    def setUp(self):
        self.sk = ffxiv.load_ffxiv_skeleton("c0201")
        self.bind, self.positions = ffxiv.forward_kinematics(self.sk)
        self.profile = ffxiv._midlander_female_profile()

    def pose(self, bend=-60):
        overrides = [None] * len(self.sk.names)
        for side in "lr":
            i = self.sk.index(f"j_ude_b_{side}")
            p = self.sk.parents[i]
            overrides[i] = self.bind[p] * self.sk.local_r[i] * R.from_euler("z", bend, degrees=True)
        return ffxiv.forward_kinematics(self.sk, overrides)

    def reference(self, rotations, positions):
        fmt = lambda values: ", ".join(str(float(v)) for v in values)
        return BrioPose({"Bones": {name: {"Position": fmt(positions[i]), "Rotation": fmt(rotations[i].as_quat()),
                                         "Scale": "1, 1, 1"} for i, name in enumerate(self.sk.names)}})

    def scales(self, rotations, reference=None, strength=1):
        return ffxiv._midlander_female_elbow_scales(self.sk, rotations, self.bind, reference, strength, self.profile)

    def test_disabled_by_default_and_race_remains_unsupported(self):
        self.assertFalse(tuning.RetargetParams().midlander_female_experiment)
        self.assertFalse(races.RACES["c0201"].supported)

    def test_signed_bend_scale_and_equipment_helpers_untouched(self):
        rotations, _ = self.pose(-60)
        scales, info = self.scales(rotations)
        self.assertEqual(set(scales), {"n_hhiji_l", "n_hhiji_r"})
        for values in scales.values():
            np.testing.assert_allclose(values, [1, 4 / 3, 1.15], atol=1e-9)
        self.assertEqual(info["l"]["reference_mode"], "canonical")
        rotations, _ = self.pose(3)
        scales, _ = self.scales(rotations)
        self.assertLess(scales["n_hhiji_r"][1], 1)

    def test_strength_cycle_and_reference_recapture_do_not_accumulate(self):
        rotations, positions = self.pose(-60)
        reference = self.reference(rotations, positions)
        expected = {0: [1, 1, 1], 0.5: [1, 7 / 6, 1.075], 1: [1, 4 / 3, 1.15]}
        for strength in (0, 1, 0.5, 0, 1, 0, 1):
            scales, _ = self.scales(rotations, reference, strength)
            for helper, values in scales.items():
                np.testing.assert_allclose(values, expected[strength], atol=1e-9)
                reference.raw["Bones"][helper]["Scale"] = ", ".join(str(v) for v in values)

    def test_nonunit_parent_and_missing_reference_bone_fall_back(self):
        rotations, positions = self.pose()
        reference = self.reference(rotations, positions)
        reference.raw["Bones"]["j_ude_a_l"]["Scale"] = "1, 1.1, 1"
        del reference.raw["Bones"]["n_hhiji_r"]
        scales, info = self.scales(rotations, reference)
        self.assertFalse(scales)
        self.assertEqual(info["l"]["skipped"], "nonunit_or_missing_parent_scale")
        self.assertEqual(info["r"]["skipped"], "missing_reference_bone")

    def test_target_domain_limits_scale_without_changing_rotations(self):
        rotations, _ = self.pose(-160)
        original = np.array([q.as_quat() for q in rotations])
        scales, info = self.scales(rotations)
        self.assertTrue(info["l"]["clamped"])
        self.assertTrue(info["r"]["clamped"])
        self.assertTrue(all(np.isfinite(values).all() and (values > 0).all() for values in scales.values()))
        np.testing.assert_array_equal(original, np.array([q.as_quat() for q in rotations]))

    def test_experiment_only_changes_scoped_helpers_without_hand_compensation(self):
        _, _, mhr_rest = ffxiv.load_mhr_rest()
        inputs = mhr_rest.as_matrix()
        normal = ffxiv.retarget(inputs, race="c0201", ground=False)
        experimental = ffxiv.retarget(inputs, race="c0201", ground=False, midlander_female_experiment=True)
        self.assertTrue(experimental.hand_compensation["midlander_female_experiment"]["active"])
        for name, (position, rotation, scale) in normal.bones.items():
            actual = experimental.bones[name]
            if not name.startswith(("j_asi_c_", "j_asi_d_", "j_asi_e_", "iv_asi_")):
                np.testing.assert_array_equal(actual[0], position)
            if name not in ("j_asi_b_l", "j_asi_b_r"):
                np.testing.assert_array_equal(actual[1], rotation)
            if name not in ("n_hhiji_l", "n_hhiji_r"):
                np.testing.assert_array_equal(actual[2], scale)

    def test_knee_curve_is_zero_at_bind_monotone_and_bounded(self):
        for side in "lr":
            zero, _ = ffxiv._midlander_female_knee_angle(0, side, 1, self.profile)
            self.assertEqual(zero, 0)
            lo, hi = self.profile["knee_rotation"]["bend_domain_degrees"][side]
            angles = [ffxiv._midlander_female_knee_angle(float(x), side, 1, self.profile)[0]
                      for x in np.radians(np.linspace(lo, hi, 101))]
            self.assertTrue(np.all(np.diff(angles) > 0))
            beyond, diagnostic = ffxiv._midlander_female_knee_angle(np.radians(175), side, 1, self.profile)
            self.assertTrue(diagnostic["clamped"])
            self.assertEqual(beyond, angles[-1])
            disabled, _ = ffxiv._midlander_female_knee_angle(np.radians(120), side, 0, self.profile)
            self.assertEqual(disabled, 0)

    def test_other_races_ignore_experiment_for_pose_output(self):
        _, _, mhr_rest = ffxiv.load_mhr_rest()
        for race in races.RACES:
            if race == "c0201":
                continue
            with self.subTest(race=race):
                normal = ffxiv.retarget(mhr_rest.as_matrix(), race=race, ground=False)
                experimental = ffxiv.retarget(mhr_rest.as_matrix(), race=race, ground=False,
                                               midlander_female_experiment=True)
                self.assertEqual(ffxiv.to_brio_pose_json(normal), ffxiv.to_brio_pose_json(experimental))


if __name__ == "__main__":
    unittest.main()
