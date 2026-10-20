import copy
import unittest
from stop_policy import stage_emergency_stop, MAX_SAFE


class StopPolicyTests(unittest.TestCase):
    def setUp(self):
        self.config = {"vars": {"PROVISIONING_CONFIGURATION_VERSION": "3", "PROVISIONING_CEILING": "100",
            "PROVISIONING_HARD_MAXIMUM": "100", "PROVISIONING_ALLOCATION_POLICY": {
                "v": 1, "revision": 4, "enabled": True, "windowSeconds": 60, "maxNewAllocations": 2}},
            "durable_objects": {"bindings": [{"name": "OVERLAYS", "class_name": "OverlayState"}]}, "migrations": ["synthetic"]}
        self.status = {"ok": True, "report": {"report_schema_version": 1, "activation_state": "active", "persisted_revision": 4,
            "configured_policy_equal": True, "configured_enabled": True, "clock_valid": True, "unavailable_reason": "none"}}

    def test_only_next_stop_fields_change_and_inputs_are_not_mutated(self):
        before = copy.deepcopy(self.config)
        proposal = stage_emergency_stop(self.config, self.status)
        expected = copy.deepcopy(before)
        expected["vars"]["PROVISIONING_ALLOCATION_POLICY"].update(revision=5, enabled=False)
        self.assertEqual(proposal["configuration"], expected)
        self.assertEqual(proposal["expectedRevision"], 4)
        self.assertTrue(proposal["requires_explicit_apply"])
        self.assertTrue(proposal["requires_independent_readback"])
        self.assertTrue(proposal["requires_separate_deployment_approval"])
        self.assertEqual(self.config, before)

    def test_repeated_offline_proposals_do_not_advance_again(self):
        self.assertEqual(stage_emergency_stop(self.config, self.status), stage_emergency_stop(self.config, self.status))

    def test_unapplied_or_unknown_configuration_requires_reconciliation(self):
        for field, value in [("configured_policy_equal", False), ("clock_valid", False), ("persisted_revision", 3),
            ("activation_state", "unavailable"), ("unavailable_reason", "read_failure"), ("configured_enabled", False)]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                stage_emergency_stop(self.config, {"ok": True, "report": {**self.status["report"], field: value}})

    def test_rejects_already_stopped_absent_and_malformed_policy(self):
        for policy in [None, {}, {**self.config["vars"]["PROVISIONING_ALLOCATION_POLICY"], "enabled": False},
            {**self.config["vars"]["PROVISIONING_ALLOCATION_POLICY"], "revision": MAX_SAFE},
            {**self.config["vars"]["PROVISIONING_ALLOCATION_POLICY"], "revision": True}]:
            config = copy.deepcopy(self.config)
            config["vars"]["PROVISIONING_ALLOCATION_POLICY"] = policy
            with self.subTest(policy=policy), self.assertRaises(ValueError):
                stage_emergency_stop(config, self.status)

    def test_never_proposes_a_ceiling_or_authority_change(self):
        for name in ["PROVISIONING_CONFIGURATION_VERSION", "PROVISIONING_CEILING", "PROVISIONING_HARD_MAXIMUM"]:
            config = copy.deepcopy(self.config)
            config["vars"][name] = "101"
            with self.subTest(name=name), self.assertRaises(ValueError):
                stage_emergency_stop(config, self.status)

    def test_report_failure_or_missing_fields_is_not_approval(self):
        for status in [{}, {"ok": False}, {"ok": True, "report": {}}, {"ok": True, "report": {**self.status["report"], "persisted_revision": True}}]:
            with self.subTest(status=status), self.assertRaises(ValueError):
                stage_emergency_stop(self.config, status)


if __name__ == "__main__":
    unittest.main()
