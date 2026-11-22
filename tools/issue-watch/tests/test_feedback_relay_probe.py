"""Test the daily relay probe against fake HTTP responses."""
import base64
import io
import json
from pathlib import Path
import sys
import unittest
from unittest.mock import Mock
import urllib.error
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from issue_digest import FEEDBACK_PROBE_URL, NoProbeRedirect, feedback_relay_summary, probe_feedback_relay


def http_error(status, body):
    return urllib.error.HTTPError(FEEDBACK_PROBE_URL, status, "fixture", {}, io.BytesIO(body))


class FeedbackRelayProbeTest(unittest.TestCase):
    def test_expected_denial_uses_an_isolated_missing_issue(self):
        opener = Mock()
        opener.open.side_effect = http_error(403, b'{"error":"denied"}')
        result = probe_feedback_relay(opener)
        self.assertEqual(result, {"ok": True, "status": 403, "error": "denied"})
        request = opener.open.call_args.args[0]
        self.assertEqual((request.get_method(), request.full_url), ("POST", FEEDBACK_PROBE_URL))
        self.assertEqual(request.get_header("User-agent"), "MyFinderDeviceBattery-RelayHealth/1")
        body = json.loads(request.data)
        self.assertEqual(body["number"], 999999)
        self.assertIsNone(body["diagnostics"])
        self.assertEqual(len(base64.urlsafe_b64decode(body["replyKey"] + "=")), 32)
        self.assertEqual(uuid.UUID(body["requestId"]).version, 4)
        self.assertIn("创建权限", feedback_relay_summary(result))

    def test_alerts_for_other_responses_and_never_reports_raw_body(self):
        for status, body in (
            (403, b'{"error":"upstream details"}'),
            (503, b'{"error":"disabled"}'),
            (502, b'{"error":"unavailable"}'),
            (302, b"not json"),
        ):
            with self.subTest(status=status):
                opener = Mock()
                opener.open.side_effect = http_error(status, body)
                result = probe_feedback_relay(opener)
                self.assertFalse(result["ok"])
                self.assertEqual(result["status"], status)
                summary = feedback_relay_summary(result)
                self.assertIn("异常", summary)
                self.assertNotIn("upstream details", summary)
                self.assertEqual(summary.encode("gbk").decode("gbk"), summary)
        opener = Mock()
        opener.open.side_effect = TimeoutError()
        self.assertEqual(probe_feedback_relay(opener),
                         {"ok": False, "status": None, "error": "network_or_invalid_response"})
        self.assertIsNone(NoProbeRedirect().redirect_request(None, None, 302, None, None, "https://example.invalid/"))
