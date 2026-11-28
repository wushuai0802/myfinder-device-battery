"""Bounded, data-driven reader diagnostic filtering and health projection.

Every string projected onto outgoing ReaderHealth.errorCode must come from an
explicit fixed allowlist or bounded numeric report proof — never raw paths,
serial numbers, product strings, or exception text.
"""
from __future__ import annotations

import re

A5V2_FAILURE_REASONS = frozenset({
    "A5V2 topology_unproven",
    "A5V2 collection_ambiguous",
    "A5V2 feature_unproven",
    "A5V2 set_failed",
    "A5V2 no_stable_reply",
    "A5V2 io_failed",
})
READER_DIAGNOSTIC_LIMIT = 1024  # Full diagnostic in engine JSON.
READER_HEALTH_LIMIT = 48  # Existing desktop ReaderHealth.ErrorCode bound.

FA6X_DIAGNOSTICS = {
    "missing or ambiguous passive input collection; not opened": "passive input collection missing or ambiguous",
    "input report 03 length unproven; not opened": "input report 03 length unproven",
    "no battery notification; no new measurement": "no valid battery notification",
    "USB parent unproven; collection uniqueness unknown; not opened": "USB parent unproven",
    "enumeration failed; retaining history": "enumeration failed; retaining history",
    "input read failed; no new measurement": "input read failed; no new measurement",
    "HID handle close failed": "HID handle close failed",
    "legacy FA65 companion missing or ambiguous; not opened": "legacy FA65 companion missing or ambiguous",
    "legacy FA65 state collection missing or ambiguous; not opened": "legacy FA65 state input missing or ambiguous",
    "legacy FA65 physical parent unproven; not opened": "legacy FA65 physical parent unproven",
    "legacy FA65 release unproven; not opened": "legacy FA65 release unproven",
    "legacy FA65 collection path unproven; not opened": "legacy FA65 collection path unproven",
}
CHERRY_DIAGNOSTICS = {
    "report IDs/lengths unknown or incompatible: nothing sent": "report sizes unproven",
    "single report 04/64 not proven: nothing sent": "single report 04/64 unproven",
    "unverified CHERRY interface/product: nothing sent": "CHERRY interface/product unverified",
    "physical parent unresolved; uniqueness unknown: nothing sent": "physical parent unresolved",
    "missing or ambiguous eligible collection: nothing sent": "collection missing or ambiguous",
    "enumeration failed: nothing sent": "enumeration failed",
    "battery I/O failed; connection unknown": "battery I/O failed",
    "short/failed battery query write; no retry": "battery query write short/failed",
    "rejected input report length": "input report length rejected",
    "rejected battery reply": "battery reply rejected",
    "no valid reply within deadline/read limit; connection unknown": "no valid battery reply",
    "HID handle close failed": "HID handle close failed",
}
LINGBAO_DIAGNOSTICS = {
    "HID handle close failed": "HID handle close failed",
    "HID backend unavailable": "HID backend unavailable",
    "enumeration failed": "enumeration failed",
    "physical parent unproven; not opened": "physical parent unproven",
    "collection missing or ambiguous; not opened": "collection missing or ambiguous",
    "identity/interface unverified; not opened": "identity/interface unverified",
    "collection path unproven; not opened": "collection path unproven",
    "Feature 0F/33 unproven; not opened": "Feature 0F/33 unproven",
    "battery I/O failed": "battery I/O failed",
    "charging placeholder; no new reading": "charging placeholder; no new reading",
    "invalid battery reply; no new reading": "invalid battery reply; no new reading",
}
ASUS_OMNI_DIAGNOSTICS = {
    "OMNI identity mismatch; not opened": "OMNI identity mismatch",
    "OMNI collection mismatch; not opened": "OMNI collection mismatch",
    "OMNI USB parent unproven; not opened": "OMNI USB parent unproven",
    "OMNI collection ambiguous; not opened": "OMNI collection ambiguous",
    "OMNI report 03/64 unproven; not opened": "OMNI report 03/64 unproven",
    "OMNI open failed": "OMNI open failed",
    "OMNI battery query write failed; no retry": "OMNI battery query write failed; no retry",
    "OMNI input read failed": "OMNI input read failed",
    "OMNI no echo within read limit": "OMNI no echo within read limit",
    "OMNI device error FF AA": "OMNI device error FF AA",
    "OMNI empty reply or asleep; no new reading": "OMNI empty reply or asleep; no new reading",
    "OMNI zero battery; no new reading": "OMNI zero battery; no new reading",
    "OMNI battery out of range": "OMNI battery out of range",
    "OMNI HID backend unavailable": "OMNI HID backend unavailable",
    "OMNI enumeration failed": "OMNI enumeration failed",
    "OMNI HID handle close failed": "OMNI HID handle close failed",
}
ZHUAUDIO_DIAGNOSTICS = frozenset({
    "ZG4 collection missing or ambiguous", "ZG4 parent/path unproven",
    "ZG4 interface/product unverified", "ZG4 CE/CD byte reports unproven",
    "ZG4 all 256 FE sequences unresolved",
    "ZG4 query already pending", "ZG4 inventory invalid",
    "ZG4 write short/failed; no retry", "ZG4 no valid reply within limits",
    "ZG4 headset disconnected", "ZG4 input rejected",
    "ZG4 parent unresolved", "ZG4 enumeration failed", "ZG4 battery I/O failed",
    "HID handle close failed",
})

# Providers whose safe diagnosticSummary may be projected onto outgoing
# ReaderHealth.errorCode when no real errorCode is set.
PROJECTED_DIAGNOSTIC_PROVIDERS = frozenset({
    "mchose_by_keyboard", "mchose_glw_keyboard", "mchose_k99v3", "mchose_qhw_keyboard",
    "mchose_headset", "atk_headset", "vgn_kc2", "am_infinity_97", "mchose",
    "rapoo", "rapoo_mt560", "rapoo_vt9", "moondrop", "pulsar", "fa6x_passive",
    "rainyrt", "inphic", "ajazz_ak980", "cherry", "cherry_mw5500",
    "hyperx_flight_s", "hyperx_stinger_core", "lingbao", "zhuaudio", "asus_omni", "lunafury",
})

FIXED_FIRST_DIAGNOSTIC_PROVIDERS = frozenset({
    "rapoo", "rapoo_mt560", "rapoo_vt9", "rainyrt", "inphic", "ajazz_ak980",
    "moondrop", "hyperx_cloud_flight", "hyperx_flight_s", "hyperx_stinger_core",
    "cherry_mw5500", "lunafury",
})

_MCHOSE_HEADSET_REASONS = (
    "接收器固件低于 0008，官方驱动也不支持电量",
    "接收器固件版本未知，按原方式查询",
)

_MCHOSE_V3_PREFIXES = (
    "V3 missing or ambiguous FF01:0001 collection;",
    "V3 collection path unresolved;",
    "V3 input report 4D/64 length unproven;",
    "V3 output report 4D/64 length unproven;",
    "V3 power query write failed or short;",
    "V3 power I/O failed;",
    "no valid V3 power reply;",
)

_MCHOSE_BY_RULES = (
    (("report IDs/lengths unknown or incompatible: nothing sent",
      "single byte-array report not proven: nothing sent"), "report sizes unproven"),
    (("unrecognized wireless receiver name: nothing sent",), "receiver name unrecognized"),
    (("physical parent unresolved; uniqueness unknown: nothing sent",), "physical parent unresolved"),
    (("missing or ambiguous eligible collection: nothing sent",), "collection missing or ambiguous"),
    (("battery I/O failed; connection unknown",), "battery I/O failed"),
)

_BATTERY_HID_RULES = (
    ("report IDs/lengths unknown or incompatible: nothing sent", "report sizes unproven"),
    ("physical parent unresolved; uniqueness unknown: nothing sent", "physical parent unresolved"),
    ("missing or ambiguous eligible collection: nothing sent", "collection missing or ambiguous"),
    ("enumeration failed: nothing sent", "enumeration failed"),
    ("battery I/O failed; connection unknown", "battery I/O failed"),
    ("short/failed battery query write; no retry", "battery query write short/failed"),
    ("device asleep/offline; no new reading; connection unknown", "device asleep/offline"),
    ("invalid GLW active profile; no automatic repair", "GLW active profile invalid"),
    ("rejected GLW percentage outside 0..100", "GLW percentage out of range"),
    ("rejected input report length", "input report length rejected"),
    ("rejected battery reply", "battery reply rejected"),
    ("no valid reply within deadline/read limit; connection unknown", "no valid battery reply"),
    ("HID handle close failed", "HID handle close failed"),
)

_AM_INFINITY_97_RULES = (
    ("target USB parent unresolved; uniqueness unknown: nothing sent", "physical parent unresolved"),
    ("ambiguous target collection: nothing sent", "collection ambiguous"),
    ("product string/path unproven: nothing sent", "product or path unproven"),
    ("input queue did not drain within bounds: nothing sent", "input queue did not drain"),
    ("short/failed battery query write; no retry", "battery query write short/failed"),
    ("battery I/O failed; connection unknown", "battery I/O failed"),
    ("receiver reports no mouse; level unavailable", "receiver reports no mouse"),
    ("battery answer rejected (out of range or malformed)", "battery reply rejected"),
    ("no valid battery answer within bounds", "no valid battery reply"),
    ("enumeration failed; retaining history", "enumeration failed"),
    ("HID handle close failed", "HID handle close failed"),
)

_MCHOSE_GLW_QHW_RULES = (
    ("exact product name mismatch or missing: nothing sent", "product name mismatch"),
    ("input/output descriptor lengths unknown or incompatible: nothing sent", "report sizes unproven"),
    ("report IDs unknown, ambiguous or incompatible: nothing sent", "report IDs unproven"),
    ("missing or ambiguous eligible collection: nothing sent", "collection missing or ambiguous"),
    ("missing collection path: nothing sent", "collection path unresolved"),
    ("physical parent unresolved; collection uniqueness unknown: nothing sent", "physical parent unresolved"),
    ("enumeration failed: nothing sent", "enumeration failed"),
    ("hidapi unavailable: nothing sent", "hidapi unavailable"),
    ("battery I/O failed; no measurement, connection unknown", "battery I/O failed"),
    ("short/failed output write; no retry", "battery query write short/failed"),
    ("rejected input report length", "input report length rejected"),
    ("rejected input report ID", "input report ID rejected"),
    ("no valid reply within deadline/read limit; connection unknown", "no valid battery reply"),
    ("HID handle close failed", "HID handle close failed"),
)

_RAZER_FAILURE_RE = re.compile(
    r"razer_(?:no_reply|no_usable_interface|interfaces_cooling_down|"
    r"enumerate_failed|open_failed|send_failed|get_failed|short_reply|"
    r"busy_timeout|device_timeout_04|foreign_reply|reply_status_[0-9a-f]{2})"
)


def reader_health_summary(value):
    """Fit the existing desktop bound; keep full details in diagnostics JSON."""
    text = str(value)
    if len(text) <= READER_HEALTH_LIMIT:
        return text
    failure = re.match(r"(?:(input|output|feature) )?report ([0-9A-F]{2}) (?:length )?unproven;", text)
    declared = re.search(r"; declared=([^;]+)", text)
    if failure and declared:
        direction, report_id = failure.groups()
        label = {"input": "in:", "output": "out:", "feature": "feature:", None: ""}[direction]
        prefix = label + report_id + " unproven;declared="
        entries = declared.group(1).split(",")
        # Show one declaration per direction before spending space on extras.
        first = [next((item for item in entries if item.startswith(d + ":")), None)
                 for d in ("out", "in", "feature")]
        entries = [item for item in first if item is not None] + [item for item in entries if item not in first]
        for count in range(len(entries), -1, -1):
            summary = prefix + ",".join(entries[:count] + (["..."] if count < len(entries) else []))
            if len(summary) <= READER_HEALTH_LIMIT:
                return summary
    # Retain whole reason clauses, never half a word or report declaration.
    clauses = text.split("; ")
    for count in range(len(clauses), 0, -1):
        summary = "; ".join(clauses[:count] + (["..."] if count < len(clauses) else []))
        if len(summary) <= READER_HEALTH_LIMIT:
            return summary
    return "reader unavailable; details in engine JSON"


def fa6x_diagnostic_summary(line):
    """Accept only fixed FA6x reasons and the bounded numeric proof grammar."""
    if not isinstance(line, str) or len(line) > READER_DIAGNOSTIC_LIMIT:
        return None
    if line in FA6X_DIAGNOSTICS:
        return FA6X_DIAGNOSTICS[line]
    from battery_hid import DESCRIPTOR_FAILURES
    reasons = "|".join(re.escape(reason) for reason in sorted(DESCRIPTOR_FAILURES | {"unknown"}))
    proof = (r"(?:; proof=(?:caps|descriptor)|; proof=caps\+descriptor failed; descriptor=(?:"
             + reasons + r"))?")
    entry = r"(?:out|in|feature):(?:[0-9A-F]{2}/[0-9]{1,4}|\+[0-9]{1,3}|none|unknown)"
    if re.fullmatch(r"input report 03 length unproven" + proof + r"; declared="
                    + entry + r"(?:," + entry + r"){2,29}; not opened", line) is None:
        return None
    return reader_health_summary(line)


def filter_provider_diagnostics(name, lines):
    """Filter raw diagnostics collected from a provider before returning over IPC."""
    if name == "zhuaudio":
        lines = [line for line in lines if isinstance(line, str) and line in ZHUAUDIO_DIAGNOSTICS]
    elif name == "cherry":
        lines = [reason for line in lines if isinstance(line, str)
                 for reason in CHERRY_DIAGNOSTICS if line == reason or line.startswith(reason + ";")]
    elif name == "fa6x_passive":
        lines = [line for line in lines if fa6x_diagnostic_summary(line) is not None]
    elif name == "lingbao":
        lines = [line for line in lines if isinstance(line, str) and line in LINGBAO_DIAGNOSTICS]
    elif name == "asus_omni":
        lines = [line for line in lines if isinstance(line, str) and line in ASUS_OMNI_DIAGNOSTICS]
    elif name == "mchose_headset":
        for reason in _MCHOSE_HEADSET_REASONS:
            if any(line.strip() == reason for line in lines):
                return [reason]
        return []
    return lines if len(lines) <= 16 else lines[:6] + lines[-10:]


def _match_prefix_rules(diagnostics, rules):
    for message, summary in rules:
        if any(line == message or line.startswith(message + ";")
               for line in diagnostics if isinstance(line, str)):
            return summary
    return None


def apply_reader_diagnostics(health_entry, name, measured, diagnostics, statuses, failure_code=""):
    """Populate bounded diagnostics and safe diagnosticSummary on a ReaderHealth entry."""
    if isinstance(diagnostics, (list, tuple)):
        if name == "zhuaudio":
            diagnostics = [line for line in diagnostics if isinstance(line, str) and line in ZHUAUDIO_DIAGNOSTICS]
            if not measured and diagnostics:
                health_entry["diagnosticSummary"] = diagnostics[0]
        elif name == "cherry":
            diagnostics = [reason for line in diagnostics if isinstance(line, str)
                           for reason in CHERRY_DIAGNOSTICS if line == reason or line.startswith(reason + ";")]
            if not measured:
                for reason, summary in CHERRY_DIAGNOSTICS.items():
                    if reason in diagnostics:
                        health_entry["diagnosticSummary"] = summary
                        break
        elif name == "fa6x_passive":
            diagnostics = [line for line in diagnostics if fa6x_diagnostic_summary(line) is not None]
            if not measured and diagnostics:
                health_entry["diagnosticSummary"] = fa6x_diagnostic_summary(diagnostics[0])
        elif name == "lingbao":
            diagnostics = [line for line in diagnostics if isinstance(line, str) and line in LINGBAO_DIAGNOSTICS]
            for reason, summary in LINGBAO_DIAGNOSTICS.items():
                if reason in diagnostics:
                    health_entry["diagnosticSummary"] = summary
                    break
        elif name == "asus_omni":
            diagnostics = [line for line in diagnostics if isinstance(line, str) and line in ASUS_OMNI_DIAGNOSTICS]
            if diagnostics and not measured:
                health_entry["diagnosticSummary"] = ASUS_OMNI_DIAGNOSTICS[diagnostics[0]]

        health_entry["diagnostics"] = [str(line)[:READER_DIAGNOSTIC_LIMIT] for line in diagnostics[-16:]]

        if name == "mchose":
            if not measured:
                for line in diagnostics:
                    if isinstance(line, str) and line.startswith(_MCHOSE_V3_PREFIXES):
                        if "; declared=" in line and line.startswith("V3 "):
                            compact = line[len("V3 "):].replace(
                                " report 4D/64 length unproven;", " report 4D length unproven;", 1)
                            health_entry["diagnosticSummary"] = reader_health_summary(compact)
                        else:
                            health_entry["diagnosticSummary"] = reader_health_summary(line.split(";", 1)[0])
                        break
                    if isinstance(line, str) and (
                            (line.startswith("K7ULTRA ") and line != "K7ULTRA stable_reply_C_pending_hardware")
                            or (line.startswith("A5ULTRA ") and line != "A5ULTRA stable_reply_C_pending_hardware")):
                        health_entry["diagnosticSummary"] = reader_health_summary(line)
                        break
            if "diagnosticSummary" not in health_entry:
                for line in diagnostics:
                    if isinstance(line, str) and line in A5V2_FAILURE_REASONS:
                        health_entry["diagnosticSummary"] = line
                        break
        elif name == "pulsar":
            for line in reversed(diagnostics):
                if line != "valid battery reply":
                    health_entry["diagnosticSummary"] = reader_health_summary(line)
                    break
        elif name == "mchose_headset" and not measured:
            for reason in _MCHOSE_HEADSET_REASONS:
                if reason in diagnostics:
                    health_entry["diagnosticSummary"] = reason
                    break
        elif name in FIXED_FIRST_DIAGNOSTIC_PROVIDERS and not measured and diagnostics:
            health_entry["diagnosticSummary"] = reader_health_summary(diagnostics[0])
        elif name == "mchose_by_keyboard" and not measured:
            for messages, summary in _MCHOSE_BY_RULES:
                if any(line == message or line.startswith(message + ";")
                       for message in messages for line in diagnostics if isinstance(line, str)):
                    health_entry["diagnosticSummary"] = summary
                    break
        elif name in ("atk_headset", "vgn_kc2", "mchose_k99v3") and not measured:
            summary = _match_prefix_rules(diagnostics, _BATTERY_HID_RULES)
            if summary:
                health_entry["diagnosticSummary"] = summary
        elif name == "am_infinity_97" and not measured:
            for line in diagnostics:
                if not isinstance(line, str):
                    continue
                if line.startswith("numbered OUT 6 / IN 7 report (62 bytes) unproven: nothing sent"):
                    if "; declared=" in line:
                        declared = line[line.index("; declared="):]
                        health_entry["diagnosticSummary"] = reader_health_summary(
                            "report 06 unproven" + declared)
                    else:
                        health_entry["diagnosticSummary"] = "report sizes unproven"
                    break
            else:
                for message, summary in _AM_INFINITY_97_RULES:
                    if any(line == message or line.startswith(message + ";")
                           for line in diagnostics if isinstance(line, str)):
                        if message == "no valid battery answer within bounds":
                            matched = next(
                                line for line in diagnostics
                                if isinstance(line, str) and (line == message or line.startswith(message + ";"))
                            )
                            if matched.startswith(message + "; seen="):
                                summary = reader_health_summary(
                                    "no valid battery reply" + matched[len(message):]
                                )
                        health_entry["diagnosticSummary"] = summary
                        break
        elif name in ("mchose_glw_keyboard", "mchose_qhw_keyboard") and not measured:
            summary = _match_prefix_rules(diagnostics, _MCHOSE_GLW_QHW_RULES)
            if summary:
                health_entry["diagnosticSummary"] = summary

    if (name == "razer" and not any(s.online for s in statuses)
            and isinstance(failure_code, str)
            and _RAZER_FAILURE_RE.fullmatch(failure_code)):
        health_entry["errorCode"] = failure_code
