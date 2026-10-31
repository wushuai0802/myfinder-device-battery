"""Local physical-instance evidence, without opening device input collections.

A USB parent's PnP instance is reliable grouping evidence for its HID
collections, not a statement that all receiver slots are the same device.
When Windows cannot prove that relationship we retain the exact collection
path. Names, model IDs and empty/reused serials are never grouping evidence.
"""
from __future__ import annotations

import ctypes as c
import os
import re


class _Guid(c.Structure):
    _fields_ = [("a", c.c_uint32), ("b", c.c_uint16), ("c", c.c_uint16),
                ("d", c.c_ubyte * 8)]


class _Interface(c.Structure):
    _fields_ = [("size", c.c_uint32), ("guid", _Guid), ("flags", c.c_uint32),
                ("reserved", c.c_void_p)]


class _DevInfo(c.Structure):
    _fields_ = [("size", c.c_uint32), ("guid", _Guid), ("devinst", c.c_uint32),
                ("reserved", c.c_void_p)]


def usb_parent(path):
    """Exact Windows USB-parent PnP instance, or None; no handles to the HID."""
    if os.name != "nt":
        return None
    setup = c.WinDLL("setupapi", use_last_error=True)
    cfg = c.WinDLL("cfgmgr32", use_last_error=True)
    setup.SetupDiCreateDeviceInfoList.argtypes = [c.c_void_p, c.c_void_p]
    setup.SetupDiCreateDeviceInfoList.restype = c.c_void_p
    setup.SetupDiOpenDeviceInterfaceW.argtypes = [c.c_void_p, c.c_wchar_p, c.c_uint32, c.POINTER(_Interface)]
    setup.SetupDiGetDeviceInterfaceDetailW.argtypes = [c.c_void_p, c.POINTER(_Interface), c.c_void_p,
                                                     c.c_uint32, c.POINTER(c.c_uint32), c.POINTER(_DevInfo)]
    setup.SetupDiDestroyDeviceInfoList.argtypes = [c.c_void_p]
    cfg.CM_Get_Parent.argtypes = [c.POINTER(c.c_uint32), c.c_uint32, c.c_uint32]
    cfg.CM_Get_Device_IDW.argtypes = [c.c_uint32, c.c_wchar_p, c.c_uint32, c.c_uint32]
    handle = setup.SetupDiCreateDeviceInfoList(None, None)
    if handle in (None, c.c_void_p(-1).value):
        return None
    try:
        interface = _Interface()
        interface.size = c.sizeof(interface)
        if not setup.SetupDiOpenDeviceInterfaceW(handle, path, 0, c.byref(interface)):
            return None
        size = c.c_uint32()
        setup.SetupDiGetDeviceInterfaceDetailW(handle, c.byref(interface), None, 0, c.byref(size), None)
        if not 8 <= size.value <= 65536:
            return None
        detail = c.create_string_buffer(size.value)
        c.cast(detail, c.POINTER(c.c_uint32))[0] = 8 if c.sizeof(c.c_void_p) == 8 else 6
        dev = _DevInfo()
        dev.size = c.sizeof(dev)
        if not setup.SetupDiGetDeviceInterfaceDetailW(handle, c.byref(interface), detail,
                                                       size, None, c.byref(dev)):
            return None
        node = dev.devinst
        for _ in range(12):
            text = c.create_unicode_buffer(4096)
            if cfg.CM_Get_Device_IDW(node, text, len(text), 0) != 0:
                return None
            value = text.value.lower()
            if value.startswith("usb\\vid_") and "&mi_" not in value.split("\\")[1]:
                return "pnp:" + value
            parent = c.c_uint32()
            if cfg.CM_Get_Parent(c.byref(parent), node, 0) != 0:
                break
            node = parent.value
    finally:
        setup.SetupDiDestroyDeviceInfoList(handle)
    return None


def physical_instance(info):
    # Explicit metadata is supplied only by our topology builder/tests, never
    # inferred from a product string or VID/PID pair.
    if info.get("physical_id"):
        return str(info["physical_id"])
    path = info.get("path", b"")
    if isinstance(path, bytes):
        path = path.decode("utf-8", "replace")
    return usb_parent(path) or "collection:" + path.lower()


def enrich(inventory, excluded=()):
    out = []
    for info in inventory:
        if (info.get("vendor_id"), info.get("product_id")) in excluded:
            continue
        value = dict(info)
        value["physical_id"] = physical_instance(info)
        out.append(value)
    return out


def physical_groups(inventory):
    groups = {}
    for info in inventory:
        groups.setdefault(physical_instance(info), []).append(info)
    return groups


class _Attributes(c.Structure):
    _fields_ = [("size", c.c_uint32), ("vendor", c.c_uint16),
                ("product", c.c_uint16), ("release", c.c_uint16)]


_descriptor_cache = {}
_descriptor_paths = None


def discover(paths, excluded=(), *, full=False):
    """Serialized descriptor discovery with native paths excluded BEFORE open.

    hidapi's global enumeration opens every HID collection, even when its
    results are subsequently filtered. Here cfgmgr32 already supplied present
    paths; read only attributes/caps/strings for remaining paths, with access 0.
    No keyboard input reports, feature queries or feature writes are read here.
    Cache complete descriptors while the full path set is unchanged: product
    strings can encode connection state. Partial discoveries always refresh
    their selected paths. enrich recomputes physical identity on every call.
    """
    global _descriptor_paths
    if os.name != "nt":
        from providers import hidlist
        return enrich(hidlist.enumerate(), excluded)
    if paths is None:
        # Do not fall back to global hidapi enumeration on Windows: native
        # collections must remain excluded before any descriptor handle opens.
        raise RuntimeError("topology_unavailable")
    paths = frozenset(paths)
    selected_paths = sorted(paths)[:512]
    if full:
        if paths != _descriptor_paths:
            _descriptor_cache.clear()
            _descriptor_paths = paths
        present = set(selected_paths)
        for path in list(_descriptor_cache):
            if path not in present:
                _descriptor_cache.pop(path)
    from standardhid import Caps
    kernel = c.WinDLL("kernel32", use_last_error=True)
    hid = c.WinDLL("hid", use_last_error=True)
    kernel.CreateFileW.argtypes = [c.c_wchar_p, c.c_uint32, c.c_uint32, c.c_void_p, c.c_uint32, c.c_uint32, c.c_void_p]
    kernel.CreateFileW.restype = c.c_void_p
    kernel.CloseHandle.argtypes = [c.c_void_p]
    hid.HidD_GetAttributes.argtypes = [c.c_void_p, c.POINTER(_Attributes)]
    hid.HidD_GetAttributes.restype = c.c_ubyte
    hid.HidD_GetPreparsedData.argtypes = [c.c_void_p, c.POINTER(c.c_void_p)]
    hid.HidD_GetPreparsedData.restype = c.c_ubyte
    hid.HidD_FreePreparsedData.argtypes = [c.c_void_p]
    hid.HidP_GetCaps.argtypes = [c.c_void_p, c.POINTER(Caps)]
    for getter in ("HidD_GetProductString", "HidD_GetManufacturerString", "HidD_GetSerialNumberString"):
        getattr(hid, getter).argtypes = [c.c_void_p, c.c_void_p, c.c_uint32]
        getattr(hid, getter).restype = c.c_ubyte
    inventory = []
    for path in selected_paths:
        match = re.search(r"vid[_&]([0-9a-f]{4,8}).*?pid[_&]([0-9a-f]{4})", path, re.I)
        if match and (int(match[1][-4:], 16), int(match[2], 16)) in excluded:
            _descriptor_cache.pop(path, None)
            continue
        if not full:
            _descriptor_cache.pop(path, None)
        cached = _descriptor_cache.get(path)
        if cached is not None:
            if (cached["vendor_id"], cached["product_id"]) in excluded:
                _descriptor_cache.pop(path)
                continue
            inventory.append(dict(cached))
            continue
        handle = kernel.CreateFileW(path, 0, 3, None, 3, 0, None)
        if handle in (None, c.c_void_p(-1).value):
            continue
        data = c.c_void_p()
        try:
            attrs = _Attributes()
            attrs.size = c.sizeof(attrs)
            if not hid.HidD_GetAttributes(handle, c.byref(attrs)) or (attrs.vendor, attrs.product) in excluded:
                continue
            iface = re.search(r"&mi_([0-9a-f]{2})", path, re.I)
            info = {"path": path.encode("utf-8"), "vendor_id": attrs.vendor,
                    "product_id": attrs.product, "release_number": attrs.release,
                    "interface_number": int(iface[1], 16) if iface else -1,
                    "usage_page": 0, "usage": 0}
            caps_complete = False
            if hid.HidD_GetPreparsedData(handle, c.byref(data)):
                caps = Caps()
                if hid.HidP_GetCaps(data, c.byref(caps)) == 0x00110000:
                    info.update(usage_page=caps.page, usage=caps.usage)
                    caps_complete = True
            for field, getter in (("product_string", "HidD_GetProductString"),
                                  ("manufacturer_string", "HidD_GetManufacturerString"),
                                  ("serial_number", "HidD_GetSerialNumberString")):
                value = c.create_unicode_buffer(256)
                info[field] = value.value if getattr(hid, getter)(handle, value, c.sizeof(value)) else ""
            if caps_complete and info["product_string"] and len(_descriptor_cache) < 512:
                _descriptor_cache[path] = dict(info)
            inventory.append(info)
        finally:
            if data.value:
                hid.HidD_FreePreparsedData(data)
            kernel.CloseHandle(handle)
    return enrich(inventory, excluded)
