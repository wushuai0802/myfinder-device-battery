"""Original INZONE H9 receiver COM battery reader; hardware unverified.

Protocol reference and limitations: docs/sony-inzone-h9.md. No pyserial is
required: the isolated reader uses Windows' System.IO.Ports implementation.
"""
import base64
import json
import os
import re
import subprocess
import sys

from .base import DeviceStatus, Provider

SIGNATURE = bytes.fromhex("04 FF 0B 00 96 C3 14 04")
VID = 0x054C
PIDS = {0x0E53: "Sony INZONE H9"}

# Fixed, protocol-owned reasons only. They never carry a COM number, device
# path, serial, product string or exception text.
NO_PORT = "receiver serial port not found"
AMBIGUOUS_PORT = "multiple receiver serial ports; not opened"
PORT_FAILED = "serial port open or write failed; no measurement"
NO_FRAME = "no valid battery frame; no measurement"
READER_FAILED = "serial reader failed; no measurement"


def parse_reply(data):
    """Ignore partial/foreign frames and reject unknown charging/percent values."""
    for offset in range(max(0, len(data) - 13)):
        frame = data[offset:offset + 14]
        if (frame.startswith(SIGNATURE) and frame[11] in (0, 1)
                and frame[12] <= 100):
            return frame[12], frame[11] == 1
    return None


# Win32_SerialPort (WMI) took over five seconds on a machine with several
# ports, which outlived the bounded reader and got the child killed mid-read.
# usbser writes the interface's PortName below the USB enum key, and the serial
# stack's present-port list rejects phantom instances left by earlier USB
# ports, so no slow WMI enumeration is needed at all.
PS_SCRIPT = r"""
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$present = @([System.IO.Ports.SerialPort]::GetPortNames())
$ports = New-Object 'System.Collections.Generic.List[string]'
foreach ($key in @('HKLM:\SYSTEM\CurrentControlSet\Enum\USB\VID_054C&PID_0E53&MI_06',
                   'HKLM:\SYSTEM\CurrentControlSet\Enum\USB\VID_054C&PID_0E53')) {
    if (-not (Test-Path -LiteralPath $key)) { continue }
    foreach ($instance in @(Get-ChildItem -LiteralPath $key -ErrorAction SilentlyContinue)) {
        $name = $null
        try {
            $name = (Get-ItemProperty -LiteralPath (Join-Path $instance.PSPath 'Device Parameters') `
                -Name PortName -ErrorAction Stop).PortName
        } catch { }
        if ($name -match '^COM[0-9]+$' -and $present -contains $name -and -not $ports.Contains($name)) {
            $ports.Add($name)
        }
    }
}
# Ambiguity is not resolved by arbitrarily selecting a receiver.
if ($ports.Count -ne 1) {
    [pscustomobject]@{ ports = $ports.Count } | ConvertTo-Json -Compress
    exit 0
}
$port = $ports[0]
$commands = @('0100FC0896C321010101007D',
              '0100FC0896C341020101009E',
              '0100FC0896C34104010100A0')
$data = ''
$failed = $false
# A reader killed mid-transfer can leave the port busy or its write pipe
# stalled until it is reopened; one bounded reopen covers that without
# touching line settings or escalating to a device reset.
for ($attempt = 1; $attempt -le 2; $attempt++) {
    $failed = $false
    $s = New-Object System.IO.Ports.SerialPort($port,115200,
        [System.IO.Ports.Parity]::None,8,[System.IO.Ports.StopBits]::One)
    try {
        $s.ReadTimeout = 100; $s.WriteTimeout = 1000
        $s.DtrEnable = $true; $s.RtsEnable = $true
        $s.Open()
        Start-Sleep -Milliseconds 400
        $s.DiscardInBuffer(); $s.DiscardOutBuffer()
        $rx = New-Object 'System.Collections.Generic.List[byte]'
        foreach ($hex in $commands) {
            $rx.Clear()
            [byte[]]$cmd = for ($i=0; $i -lt $hex.Length; $i+=2) {
                [Convert]::ToByte($hex.Substring($i,2),16)
            }
            try { $s.Write($cmd,0,$cmd.Length) }
            catch { $failed = $true; break }
            $clock = [Diagnostics.Stopwatch]::StartNew()
            while ($clock.ElapsedMilliseconds -lt 800 -and $rx.Count -lt 4096) {
                try { $rx.Add([byte]$s.ReadByte()) }
                catch [System.TimeoutException] { }
            }
        }
        if ($rx.Count) { $data = [Convert]::ToBase64String($rx.ToArray()) }
    } catch {
        # Never publish exception strings, device paths or serial numbers.
        $failed = $true
    } finally { $s.Dispose() }
    if ($data -or -not $failed) { break }
    Start-Sleep -Milliseconds 300
}
[pscustomobject]@{ ports = 1; port = $port; data = $data; writeFailed = $failed } |
    ConvertTo-Json -Compress
"""


class SonyInzoneProvider(Provider):
    name = "sony_inzone"

    def __init__(self):
        self._diag = []

    def diagnostics(self):
        return list(self._diag)

    def _note(self, message):
        if message not in self._diag:
            self._diag.append(message)

    def poll(self):
        self._diag = []
        if sys.platform != "win32":
            return []
        executable = os.path.join(os.environ.get("SystemRoot", r"C:\Windows"),
                                  "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
        try:
            result = subprocess.run(
                [executable, "-NoProfile", "-NonInteractive", "-Command", PS_SCRIPT],
                capture_output=True, timeout=8, creationflags=0x08000000)
        except (OSError, subprocess.SubprocessError):
            self._note(READER_FAILED)
            return []
        if result.returncode or len(result.stdout) > 65536:
            self._note(READER_FAILED)
            return []
        statuses = []
        for line in result.stdout.decode("utf-8-sig").splitlines():
            try:
                row = json.loads(line)
            except ValueError:
                continue
            if not isinstance(row, dict):
                continue
            if "port" not in row:
                # The script reports only how many receiver ports it saw.
                count = row.get("ports")
                if count == 0:
                    self._note(NO_PORT)
                elif isinstance(count, int) and count > 1:
                    self._note(AMBIGUOUS_PORT)
                continue
            port = row.get("port", "")
            if not isinstance(port, str) or not re.fullmatch(r"COM[0-9]+", port):
                continue
            write_failed = row.get("writeFailed") is True
            if write_failed:
                self._note(PORT_FAILED)
            try:
                reply = base64.b64decode(row.get("data", ""), validate=True)
            except (ValueError, TypeError):
                continue
            parsed = parse_reply(reply)
            if parsed is None:
                # A failed transfer never delivered a reply to judge.
                if not write_failed:
                    self._note(NO_FRAME)
                continue
            level, charging = parsed
            statuses.append(DeviceStatus(
                key="sony_inzone:" + port, name="Sony INZONE H9", level=level,
                charging=charging, charging_known=True, source=self.name,
                kind="headset", refresh_scope="provider", verification_level="C"))
        return statuses

