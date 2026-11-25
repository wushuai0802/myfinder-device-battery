"""Reviewed C4 DPI stage-switching adapter for Dareu 2.4G JM mice (260D:1156).

Source: OpenMouse mouse-protocol 93c4cc601d448ea481eb59d7b785eec75ae16ec2,
src/dareu/index.ts and src/drivers/dareu/hid.ts (DAREU All in one WEB
jmdevice.js protocol facts). Report 0x08 (17 bytes, CompX sum & 0xFF == 0x55):
  * read buffer (0x08) at address 0x0002 (len 4): [stageCount, chk, activeStage, chk]
  * read buffer (0x08) at address 0x000C (len stageCount * 4, chunked <= 10 bytes):
    4-byte stage records [xLow, yLow, highFlags, chk] summing to 0x55 mod 256
  * write buffer (0x07) at address 0x0004 (len 2): [stageIndex, (0x55 - stageIndex) & 0xFF]
"""
from __future__ import annotations

REVISION = 1
MAX_WRITES = 1
MAX_QUERY_WRITES = 3

REPORT_ID = 0x08
PACKET_LEN = 17
CMD_WRITE_BUFFER = 0x07
CMD_READ_BUFFER = 0x08
CHECKSUM_TARGET = 0x55

ADDR_DPI_HEADER = 0x0002
ADDR_ACTIVE_STAGE = 0x0004
ADDR_LEGACY_STAGES = 0x000C
MAX_BUFFER_CHUNK = 10
MAX_STAGES = 5
MIN_DPI = 100
MAX_DPI = 26000
DPI_STEP = 50
READ_ATTEMPTS = 8
READ_TIMEOUT_MS = 250


def _checksum(prefix) -> int:
    return (CHECKSUM_TARGET - (sum(prefix) & 0xFF)) & 0xFF


def _frame(command: int, address: int, count: int, data=()) -> bytes:
    head = [
        REPORT_ID,
        command & 0xFF,
        0x00,
        (address >> 8) & 0xFF,
        address & 0xFF,
        count & 0xFF,
        *(int(b) & 0xFF for b in data),
    ]
    if len(head) > PACKET_LEN - 1:
        raise ValueError("dareu_frame_overflow")
    body = head + [0x00] * (PACKET_LEN - 1 - len(head))
    return bytes(body + [_checksum(body)])


def _dpi_from_id(raw_id: int) -> int:
    high = (raw_id >> 8) & 0xFF
    low = raw_id & 0xFF
    if high == 0x00:
        index = low
    elif high == 0x44:
        index = 256 + low
    elif high == 0x88:
        index = 512 + low
    else:
        raise ValueError("dareu_invalid_dpi_id")
    dpi = DPI_STEP * (index + 1)
    if not (MIN_DPI <= dpi <= MAX_DPI and dpi % DPI_STEP == 0):
        raise ValueError("dareu_dpi_out_of_range")
    return dpi


def _query(dev, address: int, count: int) -> list[int]:
    if not 1 <= count <= MAX_BUFFER_CHUNK:
        raise ValueError("dareu_invalid_chunk")
    req = _frame(CMD_READ_BUFFER, address, count)
    written = dev.write(req)
    if written not in (None, PACKET_LEN):
        raise ValueError("dareu_short_write")
    for _ in range(READ_ATTEMPTS):
        raw = dev.read(PACKET_LEN, READ_TIMEOUT_MS)
        if not raw:
            break
        r = list(raw)
        if (len(r) == PACKET_LEN
                and r[0] == REPORT_ID
                and r[1] == CMD_READ_BUFFER
                and (sum(r) & 0xFF) == CHECKSUM_TARGET
                and ((r[3] << 8) | r[4]) == address
                and r[5] == count):
            return r[6:6 + count]
    raise ValueError("dareu_read_timeout")


def read_stages(dev, info) -> dict:
    header = _query(dev, ADDR_DPI_HEADER, 4)
    if (((header[0] + header[1]) & 0xFF) != CHECKSUM_TARGET
            or ((header[2] + header[3]) & 0xFF) != CHECKSUM_TARGET):
        raise ValueError("dareu_header_checksum")
    count, active = header[0], header[2]
    if not (1 <= count <= MAX_STAGES and 0 <= active < count):
        raise ValueError("dareu_invalid_header")
    total = count * 4
    buf: list[int] = []
    offset = 0
    while offset < total:
        chunk = min(MAX_BUFFER_CHUNK, total - offset)
        buf.extend(_query(dev, ADDR_LEGACY_STAGES + offset, chunk))
        offset += chunk
    stages: list[int] = []
    for i in range(count):
        rec = buf[i * 4:(i + 1) * 4]
        if len(rec) != 4 or (sum(rec) & 0xFF) != CHECKSUM_TARGET:
            raise ValueError("dareu_stage_checksum")
        x = _dpi_from_id(rec[0] | (rec[2] << 8))
        y = _dpi_from_id(rec[1] | (rec[2] << 8))
        if x != y:
            raise ValueError("dareu_asymmetric_stage")
        stages.append(x)
    return {"current": stages[active], "stages": stages}


def select_stage(dev, info, index: int) -> None:
    if type(index) is not int or not 0 <= index < MAX_STAGES:
        raise ValueError("dareu_invalid_stage_index")
    pair = (index, (CHECKSUM_TARGET - index) & 0xFF)
    req = _frame(CMD_WRITE_BUFFER, ADDR_ACTIVE_STAGE, 2, pair)
    written = dev.write(req)
    if written not in (None, PACKET_LEN):
        raise ValueError("dareu_short_write")
    for _ in range(READ_ATTEMPTS):
        raw = dev.read(PACKET_LEN, READ_TIMEOUT_MS)
        if not raw:
            break
        r = list(raw)
        if (len(r) == PACKET_LEN
                and r[0] == REPORT_ID
                and r[1] == CMD_WRITE_BUFFER
                and (sum(r) & 0xFF) == CHECKSUM_TARGET
                and ((r[3] << 8) | r[4]) == ADDR_ACTIVE_STAGE):
            return
    raise ValueError("dareu_write_unconfirmed")
