'''Razer Viper V4 battery provider for the verified MI_03 collection.'''
import time

import hid

from providers.base import DeviceStatus

PIDS = frozenset({0x00E5, 0x00E6})
NAME = 'Razer Viper V4 Pro'


def matches_collection(info):
    return (
        isinstance(info, dict)
        and type(info.get('vendor_id')) is int and info['vendor_id'] == 0x1532
        and type(info.get('product_id')) is int and info['product_id'] in PIDS
        and type(info.get('interface_number')) is int and info['interface_number'] == 3
        and type(info.get('usage_page')) is int and info['usage_page'] == 0x000C
        and type(info.get('usage')) is int and info['usage'] == 1
    )


def physical_id(info, pid):
    value = info.get('physical_id')
    if not isinstance(value, str) or value != value.strip():
        return None
    value = value.lower()
    sep = '\\'
    lead = f'pnp:usb{sep}vid_1532&pid_{pid:04x}{sep}'
    instance = value[len(lead):] if value.startswith(lead) else ''
    if not instance or sep in instance or '&rev_' in value:
        return None
    return value


def device_path(info, pid):
    path = info.get('path')
    if type(path) is not bytes:
        return None
    marker = b'hid#vid_1532&pid_' + f'{pid:04x}'.encode() + b'&mi_03#'
    return path if path.lower().startswith(b'\\\\?\\' + marker) else None


def usable(info):
    sizes = info.get('feature_report_sizes')
    length = info.get('feature_report_length')
    return type(length) is int and length == 91 and isinstance(sizes, dict) and sizes == {'0': 91}


def report_value(reply):
    try:
        data = bytes(reply)
    except (TypeError, ValueError):
        return None
    fields = (data[0], data[1], data[2], data[6], data[7], data[8]) if len(data) == 91 else ()
    if fields != (0, 2, 0x1F, 2, 7, 0x80):
        return None
    return data[10]


def battery_level(device, builder):
    try:
        request = bytes(builder(0x1F, 7, 0x80))
    except (OSError, ValueError):
        return None
    if len(request) != 90 or device.send_feature_report(b"\x00" + request) != 91:
        return None
    for attempt in range(3):
        time.sleep(0.065 if attempt == 0 else 0.01)
        raw = report_value(device.get_feature_report(0, 91))
        if raw is not None and raw != 0:
            return (raw * 100 + 127) // 255
    return None


def poll_devices(infos, builder):
    groups = {}
    for info in infos:
        if matches_collection(info):
            pid = info['product_id']
            physical = physical_id(info, pid)
            if physical is not None:
                groups.setdefault((physical, pid), []).append(info)

    results = []
    for (physical, pid), candidates in groups.items():
        if len(candidates) != 1:
            continue
        info = candidates[0]
        path = device_path(info, pid)
        if path is None or not usable(info):
            continue
        device = None
        try:
            device = hid.device()
            device.open_path(path)
            level = battery_level(device, builder)
        except (OSError, ValueError):
            level = None
        finally:
            if device is not None:
                try:
                    device.close()
                except (OSError, ValueError):
                    pass
        if level is not None:
            results.append(DeviceStatus(
                key=f'razer:{pid:04x}:{physical}', name=NAME, level=level,
                online=True, source='razer', kind='mouse', physical_id=physical,
                charging_known=False, verification_level='C'))
    return results
