"""DPI stage switching for declarative families (architecture v2, C4).

Writes never live in family data. Each family that may switch DPI has one small,
reviewed adapter module ``families/dpi/<family_id>.py`` that is enabled only by
listing it in ``ADAPTERS`` below; dropping a file in is not enough, and a family
delivered by a device pack can never reach an adapter.

An adapter provides:

* ``select_stage(dev, info, index)`` - send the device's "activate stage
  ``index``" command (``index`` is 0-based into the stage table read just
  before). It may read replies; it may write at most ``MAX_WRITES`` reports.
* optionally ``read_stages(dev, info)`` -> ``{"current": int, "stages": [int]}``
  for encodings the JSON ``dpi`` block cannot express; without it the family's
  JSON ``dpi`` block is used. Reads must be read-only questions.
* optionally ``MAX_WRITES`` (1 or 2, default 1), ``MAX_QUERY_WRITES`` (writes
  ``read_stages`` may use to ask questions, 0..4, default 2) and ``REVISION``.

Review rules for adapters (the facade catches mistakes, review catches intent):
no ``import hid``/``ctypes``, no opening ``info["path"]`` again, no flash/profile/
lighting commands, writes only through the ``dev`` argument.

This module owns every safety rule, so adapters cannot weaken them:

1. only onboard stage tables with unique values are writable, and only to a
   value that is already one of the device's stages;
2. the target token binds device identity, adapter revision and the complete
   stage table; a request must carry the token of the latest read;
3. immediately before writing, the live table is re-read: it must still
   produce the same token and the current value must equal the caller's
   expectation;
4. the adapter gets a device wrapper that refuses any write beyond its budget;
5. after writing, the table is read back; success needs current == requested
   and an unchanged table, anything else is an unknown outcome;
6. nothing is ever retried, and the engine only calls this for a user click.
"""
from __future__ import annotations

import hashlib
import importlib
import json
from datetime import datetime, timezone

# family id -> adapter module name (families/dpi/<name>.py). Reviewed by Claude per PR.
ADAPTERS: dict = {
    "dareu_jm_24g": "dareu_jm_24g",
}

IDENTITY_KEYS = ("vendor_id", "product_id", "physical_id", "path", "serial_number")


class WriteBudgetExceeded(RuntimeError):
    pass


def _budgeted(dev, budget):
    """hidapi facade for adapter code: reads pass through, writes are counted and
    capped. The raw handle lives only in this closure, never as an attribute."""
    left = [budget]
    attempted = [False]

    def spend():
        if left[0] <= 0:
            raise WriteBudgetExceeded("adapter exceeded its write budget")
        left[0] -= 1
        attempted[0] = True   # even a failed or short write may have reached the device

    class Facade:
        __slots__ = ()

        @staticmethod
        def write(data):
            spend()
            return dev.write(data)

        @staticmethod
        def send_feature_report(data):
            spend()
            return dev.send_feature_report(data)

        @staticmethod
        def read(length, timeout_ms=0):
            return dev.read(length, timeout_ms)

        @staticmethod
        def get_feature_report(report_id, length):
            return dev.get_feature_report(report_id, length)

        @staticmethod
        def get_input_report(report_id, length):
            return dev.get_input_report(report_id, length)

    return Facade(), (lambda: attempted[0])


def adapter_for(family):
    """The reviewed adapter module of a *builtin* family, else None."""
    import families as registry
    name = ADAPTERS.get(getattr(family, "id", None))
    if name is None or registry.builtin_families().get(family.id) is not family:
        return None
    module = importlib.import_module(f"{__name__}.{name}")
    if not callable(getattr(module, "select_stage", None)):
        return None
    budget = getattr(module, "MAX_WRITES", 1)
    queries = getattr(module, "MAX_QUERY_WRITES", 2)
    if type(budget) is not int or not 1 <= budget <= 2 or type(queries) is not int or not 0 <= queries <= 4:
        return None
    return module


def vendor_ids():
    """Vendors of builtin families with an adapter; server.DPI_PROVIDERS['families']."""
    import families as registry
    return tuple(sorted({vid for fid in ADAPTERS
                         for family in [registry.builtin_families().get(fid)] if family is not None
                         for vid in family.vendor_ids}))


def identity(info):
    """The exact collection identity a token binds (paths as hex when bytes)."""
    result = {key: info.get(key) for key in IDENTITY_KEYS}
    if isinstance(result.get("path"), bytes):
        result["path"] = result["path"].hex()
    return result


def token(target):
    identity = dict(target)
    if isinstance(identity.get("path"), bytes):
        identity["path"] = identity["path"].hex()
    return hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def _now():
    return datetime.now(timezone.utc).isoformat(timespec="seconds").replace("+00:00", "Z")


def read(dev, family, info, clock=None):
    """Read the stage table: (public view, target or None, note). Never writes stages."""
    from families import runner
    adapter = adapter_for(family)
    if adapter is not None and callable(getattr(adapter, "read_stages", None)):
        facade, _ = _budgeted(dev, getattr(adapter, "MAX_QUERY_WRITES", 2))
        try:
            value = adapter.read_stages(facade, info)
        except Exception:
            return None, None, "dpi adapter read failed"
        source = "onboard"
        if (not isinstance(value, dict) or type(value.get("current")) is not int
                or not isinstance(value.get("stages"), list) or not 1 <= len(value["stages"]) <= 8
                or not all(type(v) is int and 50 <= v <= 65535 for v in value["stages"])
                or not 50 <= value["current"] <= 65535):
            return None, None, "dpi adapter returned invalid stages"
        current, stages = value["current"], list(value["stages"])
    elif family.dpi is not None:
        kwargs = {"clock": clock} if clock else {}
        value, note = runner.run_dpi(dev, family, **kwargs)
        if value is None:
            return None, None, note
        current, stages, source = value["current"], value["stages"], value["stageSource"]
    else:
        return None, None, "no dpi read"
    target = None
    writable = (adapter is not None and source == "onboard" and len(set(stages)) == len(stages)
                and current in stages)
    if writable:
        target = identity(info)
        target.update(provider="families", family=family.id, adapter=ADAPTERS[family.id],
                      revision=getattr(adapter, "REVISION", 1), stages=stages)
    view = {"supported": True, "canSet": writable, "current": current, "stages": stages,
            "stageSource": source, "checkedAtUtc": _now(),
            "targetToken": token(target) if writable else None,
            "error": None if writable or adapter is None else "dpi_not_switchable"}
    return view, target, ""


def select(dev, family, info, target, value, expected_dpi, target_token, clock=None):
    """One user-initiated stage switch with readback. Returns the provider DPI result dict."""
    state = {"view": None, "attempted": False}

    def result(error=None, success=False, unknown=False):
        answer = dict(state["view"]) if state["view"] else None
        if answer and error:
            answer.update(canSet=False, targetToken=None, error=error)
        if answer and unknown:
            answer.update(current=None, checkedAtUtc=None)
        return {"dpi": answer, "success": success, "outcomeUnknown": unknown, "error": error,
                "confirmedDpi": answer["current"] if answer and success else None}

    adapter = adapter_for(family)
    if adapter is None or not isinstance(target, dict) or target.get("family") != family.id:
        return result("dpi_invalid_target")
    if not isinstance(target_token, str) or token(target) != target_token:
        return result("dpi_stale_target")
    if type(value) is not int or type(expected_dpi) is not int:
        return result("dpi_invalid_value")
    stages = target.get("stages") or []
    if value not in stages or expected_dpi not in stages:
        return result("dpi_unsupported_value")
    try:
        view, fresh, note = read(dev, family, info, clock)
        state["view"] = view
        if view is None or fresh is None or token(fresh) != target_token:
            return result("dpi_target_changed")
        if view["current"] != expected_dpi:
            return result("dpi_current_changed")
        if value == expected_dpi:
            return result(success=True)
        guarded, attempted = _budgeted(dev, getattr(adapter, "MAX_WRITES", 1))
        try:
            adapter.select_stage(guarded, info, stages.index(value))
        finally:
            state["attempted"] = attempted()
        view, after, note = read(dev, family, info, clock)
        if view is None or after is None:
            state["view"] = None
            return result("dpi_readback_unconfirmed", unknown=True)
        state["view"] = view
        if view["current"] != value or after["stages"] != fresh["stages"]:
            return result("dpi_readback_mismatch", unknown=True)
        return result(success=True)
    except Exception:
        attempted = state["attempted"]
        return result("dpi_readback_unconfirmed" if attempted else "dpi_read_failed", unknown=attempted)
