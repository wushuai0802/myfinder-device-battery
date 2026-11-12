"""Original INZONE H9 receiver COM battery reader; hardware unverified.

Protocol reference and limitations: docs/sony-inzone-h9.md. No pyserial is
required: the isolated reader uses Windows' System.IO.Ports implementation.
"""
import json
import os
import re
import subprocess
import sys

from .base import DeviceStatus, Provider

SIGNATURE = bytes.fromhex("04 FF 0B 00 96 C3 14 04")
VID = 0x054C
PIDS = {0x0E53: "Sony INZONE H9"}


def parse_reply(data):
    """Ignore partial/foreign frames and reject unknown charging/percent values."""
    for offset in range(max(0, len(data) - 13)):
        frame = data[offset:offset + 14]
        if (frame.startswith(SIGNATURE) and frame[11] in (0, 1)
                and frame[12] <= 100):
            return frame[12], frame[11] == 1
    return None


PS_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ports = @(Get-CimInstance Win32_SerialPort | Where-Object {
    $_.PNPDeviceID -match '^USB\\VID_054C&PID_0E53&MI_06\\' -and
    $_.DeviceID -match '^COM[0-9]+$'
})
# Ambiguity is not resolved by arbitrarily selecting a receiver.
if ($ports.Count -ne 1) { exit 0 }
foreach ($port in $ports) {
    $s = New-Object System.IO.Ports.SerialPort($port.DeviceID,115200,
        [System.IO.Ports.Parity]::None,8,[System.IO.Ports.StopBits]::One)
    try {
        $s.ReadTimeout = 100; $s.WriteTimeout = 300
        $s.DtrEnable = $true; $s.RtsEnable = $true
        $s.Open()
        Start-Sleep -Milliseconds 500
        $s.DiscardInBuffer(); $s.DiscardOutBuffer()
        $commands = @('0100FC0896C321010101007D',
                      '0100FC0896C341020101009E',
                      '0100FC0896C34104010100A0')
        $rx = New-Object 'System.Collections.Generic.List[byte]'
        foreach ($hex in $commands) {
            $rx.Clear()
            [byte[]]$cmd = for ($i=0; $i -lt $hex.Length; $i+=2) {
                [Convert]::ToByte($hex.Substring($i,2),16)
            }
            $s.Write($cmd,0,$cmd.Length)
            $clock = [Diagnostics.Stopwatch]::StartNew()
            while ($clock.ElapsedMilliseconds -lt 800 -and $rx.Count -lt 4096) {
                try { $rx.Add([byte]$s.ReadByte()) }
                catch [System.TimeoutException] { }
            }
        }
        [pscustomobject]@{ port=$port.DeviceID; data=[Convert]::ToBase64String($rx.ToArray()) } |
            ConvertTo-Json -Compress
    } catch {
        # Never publish exception strings, device paths or serial numbers.
    } finally { $s.Dispose() }
}
"""


class SonyInzoneProvider(Provider):
    name = "sony_inzone"

    def poll(self):
        if sys.platform != "win32":
            return []
        import base64
        executable = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"),
                                  "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
        try:
            result = subprocess.run(
                [executable, "-NoProfile", "-NonInteractive", "-Command", PS_SCRIPT],
                capture_output=True, timeout=8, creationflags=0x08000000)
            if result.returncode or len(result.stdout) > 65536:
                return []
            statuses = []
            for line in result.stdout.decode("utf-8-sig").splitlines():
                row = json.loads(line)
                if not isinstance(row, dict):
                    continue
                port = row.get("port", "")
                if not isinstance(port, str) or not re.fullmatch(r"COM[0-9]+", port):
                    continue
                parsed = parse_reply(base64.b64decode(row.get("data", ""), validate=True))
                if parsed is None:
                    continue
                level, charging = parsed
                statuses.append(DeviceStatus(
                    key="sony_inzone:" + port, name="Sony INZONE H9", level=level,
                    charging=charging, charging_known=True, source=self.name,
                    kind="headset", refresh_scope="provider", verification_level="C"))
            return statuses
        except (OSError, subprocess.SubprocessError, ValueError, TypeError):
            return []