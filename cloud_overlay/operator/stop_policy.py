"""Offline proposal only: never deploys, authenticates, applies, or retries."""
import copy

MAX_SAFE = 2**53 - 1


def stage_emergency_stop(configuration, status):
    """Return a new complete configuration and apply expectation for owner review.

    Inputs must be the reviewed effective configuration and separately acquired
    status. They are not authority to execute. Mismatch/ambiguity requires fresh
    reconciliation rather than choosing another revision.
    """
    variables = configuration["vars"]
    if (variables.get("PROVISIONING_CONFIGURATION_VERSION") != "3" or
            variables.get("PROVISIONING_CEILING") != "100" or variables.get("PROVISIONING_HARD_MAXIMUM") != "100"):
        raise ValueError("authority configuration requires review")
    policy = variables.get("PROVISIONING_ALLOCATION_POLICY")
    fields = {"v", "revision", "enabled", "windowSeconds", "maxNewAllocations"}
    if (not isinstance(policy, dict) or set(policy) != fields or type(policy["v"]) is not int or policy["v"] != 1 or
            policy["enabled"] is not True or
            any(type(policy[k]) is not int or not 0 < policy[k] <= MAX_SAFE for k in ("revision", "windowSeconds", "maxNewAllocations")) or
            policy["revision"] >= MAX_SAFE or policy["windowSeconds"] * 1000 > MAX_SAFE):
        raise ValueError("policy requires review")
    report = status.get("report", {})
    if (status.get("ok") is not True or report.get("report_schema_version") != 1 or
            report.get("activation_state") != "active" or report.get("configured_policy_equal") is not True or
            report.get("configured_enabled") is not True or report.get("clock_valid") is not True or
            report.get("unavailable_reason") != "none" or type(report.get("persisted_revision")) is not int or
            report["persisted_revision"] != policy["revision"]):
        raise ValueError("readback requires reconciliation")
    proposed = copy.deepcopy(configuration)
    proposed["vars"]["PROVISIONING_ALLOCATION_POLICY"] = {**policy, "revision": policy["revision"] + 1, "enabled": False}
    return {"configuration": proposed, "expectedRevision": policy["revision"], "requires_separate_deployment_approval": True,
            "requires_explicit_apply": True, "requires_independent_readback": True}
