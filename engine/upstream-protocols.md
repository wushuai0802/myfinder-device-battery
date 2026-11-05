# Protocols

How each device's battery level is read - one section per device, linked from the support table in
the [README](../README.md#supported-devices). These are the implementation notes: what is sent,
what comes back, and where each protocol was taken from.

## Headsets

### MCHOSE V9 PRO (291D:385D)

**Project verification: C, pending hardware validation.** This project implements
only `FF90:0001` with known input and output lengths of exactly 64 bytes (including
report ID `55`). Unknown/mismatched lengths cause no request. Physical receivers
remain distinct; `FF22` and `FF82` are not battery targets. The separate Turbo+
identity now reuses this reader under the evidence and limits below.

One output request `55 65 01` plus 61 zero bytes is followed by input reads on the
same handle, within a one-second deadline and at most 32 frames. Non-battery
frames such as `55 11` are skipped. A full `55 65` response uses byte 2 for the
percentage and byte 3 for state: `02` accepts only 0-100; `00`/`03` signal external
power with `level=None` (charging-compatible flag true, unable to distinguish
charging from full while plugged in); `1A` explicitly signals offline with no
percentage. An explicit unknown percentage clears the old value in the engine.
Other states and invalid lengths/percentages are diagnosed and rejected. Silence
does not prove power-off. No feature, EQ, firmware or alternative protocol commands
are sent, and no speculative checksum is added.

Sources: [rafagfran hardware/caps report](https://github.com/rafagfran/mchose-v9-pro-battery-tray#protocolo)
([MIT](https://github.com/rafagfran/mchose-v9-pro-battery-tray/blob/main/LICENSE));
[Device-Battery-Info issue #24](https://github.com/PyFlat/Device-Battery-Info/issues/24)
and [parser](https://github.com/PyFlat/Device-Battery-Info/blob/main/src/DeviceBatteryInfo/Sources/Mchose/MchoseProtocol.cs)
([MIT](https://github.com/PyFlat/Device-Battery-Info/blob/main/LICENSE)). The issue's
user captures have no separately identified licence. The
[JoaoKSS Linux source](https://github.com/JoaoKSS/MCHOSE_v9_PRO_Controller/blob/main/mchose_qt.py)
corroborates only the battery frame; no licence was found, and no implementation
or extra commands were copied. See [research section 4 and S8-S10/S14](research/p2-protocols.md#4-mchose-v9-pro-291d385d可限定实现)
for evidence limits. Upstream hardware evidence does not count as this project's
acceptance. Independent reads with the vendor app closed, charging/full/unplug
transitions, power cycling, receiver replugging and final UI still need hardware
validation. The current native UI requires a fresh numeric level to display its
charging indicator, so the engine's external-power flag with an unknown percentage
is not yet displayed there; native UI changes are outside this provider task.

### MCHOSE V9 Turbo+ (3837:600A)

**Project verification: C. Source: 用户实机抓包，#61 (2026-11-04).** The user's
capture identifies the 2.4 GHz headset receiver as `3837:600A`, product
`MCHOSE V9 Turbo+`, manufacturer `C-Media Electronics Inc`. Interface 0 has
`FF21:0001` (input/output 2/2), `FF22:0001` (0/0), `000C:0001` (3/0),
`000B:0001` (3/3), `FF90:0001` (64/64), and `FF82:0001` (64/64).
Only `FF90:0001` with exact 64/64 lengths may receive the V9 PRO battery query
`55 65 01` plus 61 zero bytes. Unknown or mismatched lengths cause no I/O.

Two queries returned `55 65 50 02` followed by zeros: **80%, discharging**,
matching the actual headset. Charging/external power (`00`/`03`) and power-off
(`1A`) were **not hardware-tested on Turbo+**; their handling is inherited from
V9 PRO above. Silence still does not prove power-off. This capture establishes
discharge only, not percentage resolution, state transitions or final UI behavior.

The simultaneously connected `3837:6008`, product `V9 Turbo`, manufacturer
`wch.cn`, is a **lighting device**, not another battery target. It is not added
to any reader. Both `3837:600A` and `3837:6008` are excluded from the mouse
provider's broad `3837` scan before any I/O, regardless of their collections or
report lengths, with the reason recorded in diagnostics. No mouse feature,
lighting, EQ or firmware commands are sent. The implementation reuses the
existing local reader from protocol facts; no code is copied from the issue.

### Astro A50 Gen 5 (Logitech 046D:0B1C)

**Connection:** Base station

The station's own vendor collection (usage page 0xFF32 / usage 0x0074): 64-byte reports, report id 0x02, `02 0c <len> 00 <cmd> <handle>`. Battery command 0x06 answers with the level in byte 6 and byte 8 set while the headset sits on the dock (charging). The protocol is HeadsetControl's, reverse-engineered from G HUB captures and verified on this USB id; it is neither HID++ nor the A50 X's Centurion protocol. **Unverified** - no A50 was on hand, so a level out of 0..100 is refused rather than shown

### Audeze Maxwell

**Connection:** 2.4 GHz dongle (3329:4B19) and USB-C cable (3329:4B1A)

The vendor collection (usage page 0xFF13): the sequence HeadsetControl uses, whose answer carries the battery as attribute 0x0CD6 (`05 5D <len> 00 D6 0C <percent>`). One packet is enough — the query for that attribute is answered with the marker on its own (0.19 s against 1.48 s for the whole sequence) — with the full sequence as the fallback. Dongle and cable are one headset and share one icon; charging is inferred from the cable answering, so the icon breathes while it sits on USB-C. With the headset switched off the dongle keeps answering with the last value it held, so the provider goes by the dongle's own product string — `"Audeze Maxwell Dongle"` with no headset linked, `"Audeze Maxwell HID"` with one — and reports nothing: the icon leaves the tray the way any switched-off device does

### Corsair Void v2 Wireless, Virtuoso Max Wireless, HS80 Max Wireless

**Connection:** Wireless receiver (1B1C:2A08, 1B1C:2A02, 1B1C:0A97)

The receiver's vendor collection on interface 4: 65-byte writes `00 02 <endpoint> 02 <cmd>`, endpoint 0x08 the receiver and 0x09 the headset, and 64-byte replies. After a minimal wake handshake (the one HeadsetControl uses, which avoids the pop of switching the headset into software mode), battery command 0x0F answers with a 16-bit value in hundredths of a percent at bytes 4-5. **Unverified** - no Corsair headset was on hand; the receiver sometimes answers with something other than a level, so that is retried and then refused rather than shown

### Corsair Virtuoso RGB Wireless SE / XT

**Connection:** Slipstream receiver or USB cable - SE `1B1C:0A3E` receiver / `1B1C:0A3D` cable / `1B1C:0A40` the newer Slipstream Multi-Device receiver (#204); XT `1B1C:0A64` receiver / `1B1C:0A62` cable

The routed exchange the Dark Core row describes, framed on report `0x02`: the request is a 64-byte report - `02 <target> <command> <property>`, target `0x09` the headset behind a receiver, `0x08` the device itself, command `0x02` "get property" - answered on report `0x01` with `01 01 <command echo> <status> <value:4 LE>` (`00` ok, `05` no such property, `09` refused). Property `0x0F` is the level in tenths of a percent and `0x10` the charge state (1 = charging). The wired ids read with target `0x08` first and the receivers with `0x09`, each with the other as the fallback; reads need neither a session nor software mode (HeadsetControl notes only writes need software mode), and the headset's unsolicited volume reports (report id `0x0E`) are not replies and are skipped. On Windows the frame goes out bare - the 64 bytes with `0x02` first; every report-id-prefixed form is refused with `0x57` (#204, measured on the `0A40`). One icon covers a product's receiver and cable ids together, and a reading that reports charging wins the merge. Source: HeadsetControl's reworked Virtuoso XT/SE device ([#570](https://github.com/Sapd/HeadsetControl/pull/570)), with its four XT/SE ids run on its author's hardware per the #568/#570 discussion, plus the `0A40`'s end-to-end measurement with @jeffpeng3's probe runs (#204: 96 % in both states, charge `0x10` = 1 on the cable). **Confirmed in the app on the `0A40`** - @jeffpeng3's run read 97 % over the receiver (the level frame `01 01 02 00 ca 03` twice, identical) together with the charge state; the four XT/SE ids have not answered a HaloBattery build yet and stay marked unverified

### HyperX Cloud Alpha 2

**Connection:** 2.4 GHz station (03F0:08BE - the station's other function, 03F0:0ABE, is the audio-only "Chat" half and has nothing to read)

The battery collection is picked by usage page, 0xFF13:0xFF00. A 64-byte output report `50 02 00 00 ...` is answered by the input report starting `51 02`: the level is index 2 as a plain percentage, and bit 7 of index 6 was set after a recharge and clear before it, so it is shown as charging. Both were taken from two USBPcap captures of NGENUITY attached to issue #26, at 51 % and 67 %, and matched to the percentage NGENUITY displayed at the time; the `61 02` frame is byte-identical across the two states, so it is not the battery, and a level above 100 is refused rather than shown. **Confirmed on real hardware** by @azizen12 - the percentage was right on the station's first test. With NGENUITY running the station also emits thousands of `ff 01` / `44`-`45` housekeeping frames (its diagnostics showed the app reading only those); stale reports are therefore drained before the request and the reply is read until it arrives, so the two applications can run side by side - both states confirmed by @azizen12 on the final build. Credit for the captures: @azizen12.

### HyperX Cloud II Core Wireless

**Connection:** 2.4 GHz dongle (03F0:0995) or its second mode (03F0:0795)

The same `66 <cmd>` 62-byte exchange as the Cloud III Wireless row, from @dantifrice's USBPcap capture of NGENUITY in [#155](https://github.com/HeyOkay/HaloBattery/issues/155): NGENUITY wrote `66 89` to interface 3 as an output report and the dongle answered `66 89 0e d7 30 ...` (48 % - the level at byte 4, bytes 2-3 non-zero, as the reference parses it), and `66 8a` was answered `66 8a 00 00` (off the cable). **Verified on hardware** in the reporter's run: the icon read the level, and while charging both ids answer at once with the same reading (0995 and 0795) - they share one icon, with the charging reading winning

### HyperX Cloud II Wireless

**Connection:** 2.4 GHz dongle (03F0:0696, and 03F0:018B on the newer dongle revision)

The vendor collection, picked by usage page rather than position (0xFF90:0x0303): the dongle carries four collections on the one interface, so the first one is not the right one. A 52-byte output report `06 ff bb <command> 00` is answered by 20 bytes echoing the command: command 0x02 carries the level in byte 7 (voltage in bytes 5-6), command 0x03 reports charging in byte 4 - the same exchange HeadsetControl uses for these two product ids. **Unverified** - no Cloud II Wireless was on hand, so a reply that does not echo the command is ignored and a level above 100 refused rather than shown

### HyperX Cloud III S Wireless

**Connection:** 2.4 GHz dongle (03F0:02CC, and 03F0:06BE which the reference also lists)

Another protocol again (LennardKittner/HyperHeadset's `cloud_iii_s_wireless`, and NGENUITY's USB traffic from HyperHeadset #36): a 64-byte *output* report `0c 02 03 01 00 <cmd>` (byte 3 = 01 only reads; the dongle ignores the same bytes as a feature report), command 0x06 for the battery and 0x48 for charging; the answer is input report 0x0C with the command in byte 5 and the value in byte 6 (0xFF = no value). The request goes to the collection that takes output report 0x0C, and all the dongle's collections are read for the answer. **Verified on hardware** in #106 (02CC): 89 %, the same level as NGENUITY; and in #156 (06BE), the level and the charging state

### HyperX Cloud III Wireless

**Connection:** 2.4 GHz dongle (03F0:05B7, and 03F0:0C9D which the reference also lists)

A different protocol from the Cloud II: the vendor collection is `0xFF13:0x0001`, a 62-byte `66 <command> 00 ...` packet goes out (0x66 is the report id) and 62 bytes come back whose second byte is either the command echo or the matching response id - `0x89`/`0x0D` carries the level in byte 4 (only when byte 2 or 3 is non-zero), `0x8A`/`0x0C` carries charging in byte 2 (0 not charging, 1 charging, 2 fully charged). Taken from LennardKittner/HyperHeadset, which lists this product id; that project also documents that some HyperX dongles accept these packets as **feature reports** only on Windows (`write()` fails with "Incorrect function" and the packet is retried with `send_feature_report`, which the diagnostics say either way). **Unverified on hardware** - no Cloud III Wireless was available here

### JBL Quantum 910 Wireless

**Connection:** 2.4 GHz dongle (0ECB:2088)

The receiver exposes one vendor collection, `ff13:0001` (interface 5 on a real unit), and the headset pushes its own reports there; there is no request to send. The battery arrives as report 0x08 with the level in byte 1 - the pattern plugato/JBL_Baterry_Monitor confirmed on this USB id - and report 0x2f is the microphone mute state. The headset can stay quiet for long stretches, so the last level heard is kept and shown greyed out until something arrives. **Confirmed on a real unit**: the provider finds the receiver, matches its collection and shows the level as soon as the headset reports. That report is an event - on the unit tested it arrives when the headset is plugged into its charger (a capture read 95% at the moment JBL's own app said 95%), and pressing the buttons or the volume rocker does not produce it - so the last level heard is kept and shown greyed out until the next one. The receiver also pushes a power report (0x09 - byte 1 is `0x00` when the headset is switched off and `0x01` when it is on, confirmed on a real unit - plus a 0x02 frame); none of them is a level, and a headset last seen switched off says so in the diagnostics rather than only "nothing heard"

### Logitech G PRO X 2 LIGHTSPEED

**Connection:** 2.4 GHz receiver (046D:0AF7)

Not HID++ on ff43: Logitech's "Centurion" transport on the vendor collection `ffa0:0001`, report 0x51. The app lists the receiver's features, reaches the headset through the receiver's bridge feature (0x0003), lists the headset's features and reads its battery feature 0x0104 (percent, charging state), all with read-only functions. From Solaar (tested on this headset) and HeadsetControl; firmware without 0x0104 gets HeadsetControl's fixed request. Confirmed on hardware in #103 (75 %, level and charging equal to G HUB)

### Razer Barracuda Pro (2.4 GHz)

**Connection:** 2.4 GHz dongle (1532:053a)

Its receiver publishes two collections and neither answers the standard Razer mouse request this app sends. The headset speaks the "PA" protocol instead: 64-byte vendor frames, `P`,`A` out and `P`,`I` back, battery command `0x21` with the level in the reply's data byte, charging `0x2A`. Decoded from a USBPcap capture of Razer Synapse on a real unit (it read 34%), then confirmed on hardware with @phl23's own Barracuda Pro: the level tracks (27% at the test), charging follows the charger, and a switched-off headset reports "no link" rather than a stale value. Over Bluetooth Windows reports the level itself.

### Razer BlackShark V2 Pro (2020)

**Connection:** 2.4 GHz receiver (1532:0528)

Uses a 64-byte feature report `0xFF` on usage page `0xFF00`, with request
`FF 0A 00 FD 04 12 F1 02 05`. Reads the device-reported level and power state.
Observed states are on battery (`0x01`), low battery (`0x02`), then very low battery (`0x03`),
charging (`0x09`) and charge complete (`0x06`). An unknown state keeps a fresh
battery reading visible and marks the charging state as unknown. Stale replies
are rejected. The charging cable (1532:052E) is skipped to keep one icon;
the receiver must remain plugged in.

**The device-reported battery percentage is imprecise and should be treated as
a rough indication of charge, not an exact measurement.** Observed discharge
levels were 100 -> 80 -> 50 -> 30 -> 10, with no intermediate values.
The full set of levels and their thresholds have not been established.
Connecting the charger produced an immediate jump from 10 to 80, so the reported
percentage during charging is unreliable. At charge completion the receiver
reported 100, which remained 100 after unplugging. The application currently
hides the unreliable level during active charging and shows the charging state.
On battery and after charge completion, it displays the reported level unchanged.

### Razer BlackShark V2 Pro (2023)

**Connection:** 2.4 GHz receiver (1532:0555)

The headset's own "PA" protocol: output reports 0x02 on the vendor interface 0xFF00, remote mode 0xE1, commands 0x21 (battery) and 0x2A (charging)

### SteelSeries Arctis and GameBuds (other models)

**Connection:** wireless base station or dongle

The same `b0` exchanges as the Nova 7 above, applied to the other Arctis Nova 7 and Nova 5 family models; and the older Arctis 1, 7, 9, Pro Wireless, the Arctis 7+ (nine-step level) and the GameBuds (lower earbud level) exactly as HeadsetControl documents their requests. The Nova Pro Wireless stations (`1038:12E0`, `1038:12E5`) are handled too. The models in the support table above are the confirmed ones.

### SteelSeries Arctis Nova 7

**Connection:** 2.4 GHz dongle (1038:22A1)

Output report `00 b0` on interface 3 (usage page 0xFFC0); the reply carries the level and the status (off / charging / on battery), as documented by HeadsetControl. Works alongside SteelSeries GG. The other Nova 7 variants and the Nova 5 / 5X use the same request and are included, but not tested

### SteelSeries Arctis Nova Elite

**Connection:** Wireless base station, interface 3 (`1038:2244`)

A read-only status request `01 b0` (a 64-byte output report, report id `01`) goes to the vendor collection of interface 3 that takes it (`0xFFC0` first; the other one refuses the report id); the station answers with a direct `01 b0` reply (headset level in byte 6, power state in byte 14, charging in byte 15, the layout loteran/Arctis-Sound-Manager takes from SteelSeries GG's own description of the station) or with `07` frames, which can arrive on the other collection, so both are read. `07 b7` carries the headset level in byte 2 and charging in byte 4 (`02` charging, `08` on battery); `07 b5` carries the power state in byte 4 (`01` off, `02` cable charging, `04` standby, `08` online). A headset reported as off (power code `01`) shows nothing, and the spare battery level in byte 3 is not shown. A `07 b7` that the station sends by itself is read as well. Exchange from elegos/Linux-Arctis-Manager (made from a USB capture of SteelSeries GG on Windows), the same in loteran/Arctis-Sound-Manager. **Level and charging verified on hardware** in #138: the level matches SteelSeries GG (31 % at the first test, from the direct reply) and the charging animation follows the charger (build 1.12.0.7). A switched-off headset shows 0 %, as SteelSeries GG also does in its tray: on this station the off state does not arrive as power code `01`, so this is parity with the vendor rather than a gap. A level above 100 is refused rather than shown. The other ids of this station (`2246`, `2249`, `2270`) are not included yet

### SteelSeries Arctis Nova Pro Wireless (`1038:12E0`, `1038:12E5` X)

**Connection:** Wireless base station, interface 3 or 4

The same `b0` exchange as the other Nova headsets, asked for with report id `06` instead of `00`, as HeadsetControl asks for these base stations: the level is a nine-step code in byte 6 (shown as "about NN%") and the state in byte 15 (`01` off / out of range, `02` cable charging, `08` on battery). A reply with any other state byte, a level code above 8 or fewer than 16 bytes is refused rather than shown. The request goes only to a vendor collection of these two ids; the diagnostics in #41 show `1038:12e5` exposing `ffc0:0001` on interface 4 (plus a second vendor collection `ff00:0001` there), so both are tried and the answering one is remembered. The reply layout is HeadsetControl's - not yet seen on hardware here

## Mice

### CompX onboard DPI selection (2026-11-05)

**Project verification: C, fake devices only; hardware acceptance pending.**
This extension to `providers/pulsar.py` reads existing onboard stages and switches
the active stage. It never edits DPI values/counts, polling rate, colours, lights,
LOD, profiles or other settings. Battery protocol-family matching does not grant
DPI capability. `stageSource` is always `onboard`.

Source, retrieved using `gh api repos/OpenMouse-Project/mouse-protocol/contents/<path>?ref=<commit>`:
OpenMouse `mouse-protocol`, commit **`30d6ad37d0ecc9fff53f86d5058d67a88e07150d`**,
licensed **AGPL-3.0-or-later** in its `package.json`. The implementation adapts:

- [`src/drivers/atk/hid.ts`](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/drivers/atk/hid.ts):
  `REGISTER`, `readDpiStage`, `dpiAddress`, `setActiveDpiStage`, `read`, `write`,
  `identify` and the R1 transport predicates.
- [`src/atk/index.ts`](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/atk/index.ts):
  `atkDpiStageLength`, `atkUnpackDpiStageForSensor`, PAW3395/SE and PAW3955 codecs.
- [`src/endgame-gear/wireless.ts`](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/endgame-gear/wireless.ts):
  `weBuildCmdPayload`, `weReportChecksum`, `wePackScalarPair`, `weUnpackScalarPair`.
- [`products.ts`](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/drivers/atk/products.ts)
  and [`device-catalog.generated.ts`](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/drivers/atk/device-catalog.generated.ts):
  exact USB IDs, live CID/MID identities, sensor and transport selection.

The complete allow-list is deliberately small. All three IDs already reach this
project's battery reader. DPI additionally requires a proven physical instance,
the exact `FF02:0002` collection, output length 17, a successful battery reply in
this poll, and the live CID/MID below. Unknown lengths, other IDs/identities and
model-pack definitions grant no DPI capability. NearLink names are excluded;
R1 wired IDs also reject product strings identifying a receiver/dongle.

| USB ID | Required CID,MID | Evidence and DPI map |
|---|---|---|
| `373B:12A7` | `1,62` | Catalog explicitly identifies **ATK F1 V3 ULTIMATE +**, PAW3955Master; ATK `isSupported` accepts its FF02 collection. `dpiAddress` selects **0x1B00 + index * 6**, at most 6 stages. Each row is X low/high, Y low/high, flags, checksum; decode each little-endian axis + 1, doubled by flag 0/4 respectively. Existing battery support uses the strict 373B family gate; this is the only such family ID granted DPI here. |
| `3554:F58C` | `2,27` | `ATK_COMPX_PRODUCT_IDS` plus `ATK_PRODUCTS["2,27"]` and `usesVerifiedR1WiredTransport` explicitly allow wired R1 Pro Max. **0x000C + index * 4**, up to 8 stages, PAW3395 50-DPI codec with high count bits and the doubling bit. Other F58C identities, including R1 PRO `2,7`, remain excluded. |
| `3554:F58F` | `2,32` | `ATK_COMPX_PRODUCT_IDS` plus `ATK_PRODUCTS["2,32"]` and `usesVerifiedR1WiredTransport` explicitly allow wired R1 SE+. **0x000C + index * 4**, up to 8 stages, PAW3395SE skipped-code table and high-range flag, copied from the codec. This PID is shared: all other CID/MID replies remain excluded, even if the catalog names them R1/SE/SE+. |

Excluded branches: `3554:F58A` + `2,27` requires OpenMouse's **complete 10-byte
system-row write** to switch a stage. That would write other EEPROM addresses,
so this task does not enable it. `373B:1085` / `3554:F58E` use R1 live-settings
transport (`0x0070`); upstream explicitly refuses active-stage selection on
those connections. No live-settings write is sent. Native `373B:1278` remains
owned by the C# A9 reader; generic NearLink, unknown 373B IDs and other PAW3955
IDs are not added. `3554:F505`, `3554:FB56`, `3554:F517`, the Pulsar and Hitscan
IDs have battery evidence but are not in this pinned ATK client's exact CompX
transport list; their battery support alone is insufficient for this DPI map.

Wire format (HIDAPI includes the report ID, unlike WebHID's 16-byte payload):
`08 cmd 00 addrHi addrLo length data... zero-padding checksum`, **17 bytes**.
The sum of all bytes, including report ID `08`, is `0x55` modulo 256. `cmd=10`
reads CID/MID with an otherwise empty payload; `cmd=08` reads EEPROM. Reads
accept command `08` or `07` replies, matching upstream's firmware compatibility
branch, but require report ID, success/status byte, address, sufficient bounded
length and valid frame checksum. `system` is read at `0000`, length 6: polling
rate, count and zero-based active index are three value/checksum pairs. Every
pair and every stage row must sum to `55`. Unlike upstream's presentation clamp,
invalid counts/indices are rejected, not repaired or silently clamped.

The only mutation is `cmd=07`, address **0004**, length **2**, data
`[index, (55-index) & FF]`. For example, selecting stage index 1 sends exactly:

```text
08 07 00 00 04 02 01 54 00 00 00 00 00 00 00 00 EB
```

There is no generic EEPROM writer exposed, no full-row rewrite and no rollback
or retry. After the upstream 10 ms settle, the implementation rereads the full
6-byte system block, checks the unchanged rate/count pairs and requested active
index, then rereads that stage and CID/MID. A mismatch, timeout, short write or
exception after dispatch is `outcomeUnknown`: current DPI, token and `canSet`
are cleared. Only a new successful read can grant another write lease. A failed
DPI read leaves independently confirmed battery evidence intact.

Before dispatch the manager checks online/fresh DPI capability, provider,
onboard source, non-pack origin, token, expected current DPI and offered value.
The child restricts inventory to the selected physical receiver and provider,
rejects pack definitions, and rechecks path, USB ID, physical instance, optional
serial, live CID/MID and the complete stage table. The opaque SHA-256 token binds
those fields; raw identity never leaves the private worker. System is reread
after the stage scan and immediately before the single mutation. Duplicate X
values and asymmetric X/Y stages are display-only because the existing UI sends
a DPI value, not a stage index or XY pair. Logitech's provider/codec and original
tests are unchanged; manager dispatch and C# leases now recognize pulsar onboard
stages and keep the issuing provider bound through write confirmation.

Verification: `engine/tests/test_compx_dpi.py` exercises exact frames, sensor
layouts, 8-stage R1, checksums, identity/caps/pack exclusions, state races and
unknown outcomes. `tests/test_compx_dpi_engine.py` exercises real parent/child
routing against fake HID, stale token/expected-value rejection without I/O,
disconnects, pack blocks and read reconciliation. `GenericDpiTests.cs` and
`WirelessDpiUiTests.cs` cover parsing, onboard leases, provider-bound IPC
confirmation and existing panel controls with fake workers. No real hardware
was opened during development or verification.

Maintainer acceptance still needed: an allow-listed F1 V3 ULTIMATE + (or one of
the exact wired R1 identities) must confirm the displayed stage order/current
value, a single switch, unchanged other settings, sleep/wake and replug behavior.
CID/MID identifies a model, not a unique radio unit; this implementation does
not claim to detect replacement by an indistinguishable same-model unit behind
the same receiver. The maintainer's **native ATK A9 Air+** should retain native
DPI behavior. Their **VGN F1 Pro via 3554:F505** should retain battery reads and
offer no generic DPI controls; it cannot validate the enabled DPI transports.

### HECATE G3M PRO (320F:706E)

**C — pending this project's hardware validation.** `providers/g3m.py` is an
independent battery-only implementation of [batch-3 research §4.1](research/batch3-protocols.md#41-g3m-pro-320f706e).
Only `320F:706E / FF1C:0092` qualifies. Descriptor byte-array caps must prove
input/output report `04` are 64 bytes including the ID; unknown caps or more
than one eligible collection per physical receiver send nothing. `320F:5088`
is excluded from G3M; the separate VGN keyboard reader below now has official
and maintainer evidence for that ID, with a different request. Rainy75 remains
unverified despite sharing the ID and collection.

The only query is `04 20 00 1A 06` plus 59 zeroes; `20 00` is the little-endian
sum of request bytes 3..63. Replies must be exactly 64 bytes, report `04`, command
`1A`, without error `FE/FF` at byte 7, and byte 8 must be 0..100. An exact request
echo is rejected, including an indistinguishable all-zero battery payload.
Response checksum rules are unconfirmed and are not inferred from the request.
Charging remains unknown (`charging_known=False`). No configuration session,
commit, DPI, lighting, online-probe or keyboard-collection fallback is used.

Sources G1/G2: BoBppy/g3mpro-macos, commit
`6271ac2bfc482168d88e95480a8bcc5c05a16ff5`,
[CHIDBridge.c](https://github.com/BoBppy/g3mpro-macos/blob/6271ac2bfc482168d88e95480a8bcc5c05a16ff5/Sources/CHIDBridge/CHIDBridge.c)
(`build_packet`, `transact`, battery part of `read_snapshot_open`) and
[README](https://github.com/BoBppy/g3mpro-macos/blob/6271ac2bfc482168d88e95480a8bcc5c05a16ff5/README.md),
[MIT](https://github.com/BoBppy/g3mpro-macos/blob/6271ac2bfc482168d88e95480a8bcc5c05a16ff5/LICENSE),
copyright 2026 BoBppy. Source G3: BlueEve04/G3M-PRO-Controller,
commit `38a11b1bfcb32506f1c2951a33a9e6aaa1723033`,
[Windows README](https://github.com/BlueEve04/G3M-PRO-Controller/blob/38a11b1bfcb32506f1c2951a33a9e6aaa1723033/README.md)
declares MIT and independent battery polling; used only as corroborating
documentation, not copied implementation. Sources checked 2026-11-04.
Both licensing choices are compatible with this project's AGPL-3.0-only.

Still needs hardware comparison at multiple levels, including a genuine 0%,
sleep/wake, receiver removal, cold-start polling with the official driver closed,
actual Windows caps and firmware variants. Charging requires separate evidence.

### EWEADN S9 (089D:062F)

**C — pending this project's hardware validation.** `providers/eweadn.py` follows
[batch-3 research §4.2](research/batch3-protocols.md#42-前行者-s9-089d062f33).
Only `089D:062F / FF01:0010` qualifies; no `FF00:0000`, wired `088D:062E`, M302
or product-name fallback. A physical receiver must have one eligible collection.
Descriptor byte-array caps must identify unnumbered input/output reports.
Output is exactly 33 bytes to hidapi: `00 30` plus 31 zeroes (32-byte payload).
The request checksum at byte 32 sums bytes 5..31, so is zero.

**Input length is learned, not guessed.** The upstream `read(33)` specifies a
buffer limit, not a descriptor. This reader requires actual input caps of
16..34 bytes including Windows' ID slot, and reads exactly caps minus one
(15..33 unnumbered bytes). Unknown caps, contradictory per-ID sizes, numbered
input or insufficient capacity cause no query. These bounds describe the
parser's supported capacity, not a claim that every such length exists in S9
hardware. Exact hardware input caps remain to be supplied at acceptance.

A reply must fill that descriptor-backed read, begin `30 00`, declare enough
payload for bytes 4..14 without exceeding the frame, contain literal ASCII
`M156` at 4..7, percentage 0..100 at 13 and the observed connected value `01`
at 14. `30 02`, other model identities and invalid frames produce no reading,
never 0%. Charging stays unknown; passive `D1` reports and unverified response
checksums are not used. Existing engine history handles silence as unconfirmed.

Sources E1/E2: [HaloBattery PR #352](https://github.com/HeyOkay/HaloBattery/pull/352),
open when checked 2026-11-04; Rum-right/HaloBattery commit
`c9c75371e00e42d5714416718b40992e4a6ac7e7`,
[eweadn.py](https://github.com/Rum-right/HaloBattery/blob/c9c75371e00e42d5714416718b40992e4a6ac7e7/providers/eweadn.py),
[MIT](https://github.com/Rum-right/HaloBattery/blob/c9c75371e00e42d5714416718b40992e4a6ac7e7/LICENSE),
copyright 2026 Halo Battery contributors (AGPL-compatible).
This is an independent implementation of the packet facts, with stricter
identity, descriptor and response checks than the PR; the PR's broad matching,
brand-wide key and charging inference are not imported.

Still needs actual input caps/full frames, 70%/69% or other multi-level comparison,
sleep/wake, removal/reconnect, independent polling and a charging/unplugged
comparison. Other connection-state values are intentionally unaccepted.

### EWEADN S9 PRO 8K (372E:1026)

**C — implemented, pending this project's hardware validation.** The independent
`providers/eweadn_s9pro.py` reader sends only the basics/battery request below.
The fourth-pass brief supplies seven consistent diagnostic inventories with
vendor collections `FF00:0001` and `FF06:0001`. Within each physical receiver,
only the unique collection whose runtime descriptor proves Feature report `03`
has 64 bytes (ID + 63-byte payload) is eligible. Zero or multiple proven matches
cause no protocol handle to open and no request to be sent. Descriptor discovery
uses access-zero handles and preparsed caps only, without feature transfers.

The third-pass snapshot of
[EWEADN Hub](https://new-hub.eweadn.cn/), supplied as retrieved on 2026-11-06,
contains all 67 supplied chunks, including `vendor-bytech` and its module 74590
(71 JavaScript files in total). The previous missing-controller finding is
superseded: battery request bytes, charging parsing and a read-only SDK call
path are established. Collection ownership and length are now required at
runtime rather than guessed from a static usage-page preference. Complete real
replies, cold-start and sleep/offline behavior still need hardware validation.

The earlier analysis used only local text and bounded Python string-table decoding in
`.ai/evidence/eweadnhub/`; no vendor JavaScript, hardware or network was used.
REA's static-analysis call again timed out after 300 seconds without evidence;
none of the conclusions below relies on a successful REA result. The HTML
declares build `3.3.5-20261104025216275`, version `3.3.5`.

Snapshot identity (SHA-256 of the original bytes):

| Local file | Bytes | SHA-256 |
| --- | ---: | --- |
| `index.html` | 1411 | `844fd2d92ed9713d8100ec1e666f46bdf529d8522a7b0fb70b4808c088977e81` |
| `main.0d125cb779cf4c8408a8.js` (runtime) | 7072 | `dc38f4b97d5de7bfd73c21f053bbbc62229471ac7e6ed130e265f801b6c855b2` |
| `main.59124dce33c280445fd9.js` (shared) | 1178843 | `38600cd494f8e505b100379db9d3b3217bfe834fc88804d15c566cf9972a5235` |
| `main.6a3b1fe4700c6a5bd57e.js` (app) | 1404935 | `02ae29b557b435d9d502289aa71f72becd9933fe9aee03ebc02e38be5539cc2f` |
| `main.f8e5777ff051023749db.js` | 333032 | `0263501996a75358d7d7491b321a388baae1d2f30d5913f400b16eb55cfbbbe0` |
| `chunks/main.sdk-mouse-beiying.b4ffbeadbe8c318936bb.js` (mouse wrapper) | 83146 | `0487720e9d1c54c6106588b6d1b460ecff33cb991437d558e4c5bc417274d843` |
| `chunks/main.sdk-api.b41e96c5c2fd148df1b7.js` (includes provider loader) | 471566 | `e25bbe23d82e884f99e9fde54b60628e2095c182092ce3f39ae80d95011c4b7c` |
| `chunks/main.vendor-bytech.803cedc7e7fc4dcad3e3.js` (module 74590) | 103775 | `252fbd62a7cc964e38f6ac84d80cf88967c3de31c17f389da01d58b253913797` |

All offsets below are **zero-based UTF-8 byte offsets in those original files**,
not character positions in a prettified or decoded copy.

**Identity and runtime collection selection.** The app's mouse catalog at
1375074 records decimal VID/PID `14126:4134` (`372E:1026`), product `EWEADN S9`,
display name `S9 Pro`, `deviceId: s9pro`, `is4G: true`, UUID `9895604649998`,
`solutionRovider: beiying` (the vendor's spelling), and `hidReportId: 3`.
At 1374397 the same receiver is listed for `S9 Max`, UUID `9895604650026`.
Other rows share that VID/PID: S7 Max (1372432), S7 Pro (1373092), S7 Ultra
(1373964), S9 Ultra (1375735), and S9 Ultra+ (1376404). Consequently VID/PID
alone does not distinguish these mouse models. `base.getUUID()` is traced
to a read request, described below, but is not sent by this reader. The display
name is `EWEADN S9 PRO 8K` as required by the fourth-pass brief; it is not a
runtime identification of the paired model or a claim of S7/S9-family coverage.

The task's diagnostic reports identify `FF00:0001` and `FF06:0001` collections;
neither the catalog nor the mouse wrapper chooses one. The wrapper's filter `L`
at 4993 derives exact VID/PID pairs from the `beiying` mouse catalog, without
usage-page/usage constraints. Module 74590 now proves **feature** traffic on
report `03`, but contains no collection selector or feature descriptor. WebHID
uses the opened device and report ID; that does not identify which of the two
Windows collection paths owns the report. In particular, the shared file's
`Wo` filter at 248141 is
`vendorId:14126, usage:97, usagePage:65376, productId:0` (`FF60:0061`) in the
keyboard module `68572`, which starts at 48272. It is not evidence for choosing
either S9 diagnostic collection.

`topology.py` enables `battery_hid.byte_report_sizes(..., report_type=2)` only
for this exact VID/PID and these two vendor collections. The reader requires
the proven per-report size `3:64`, consistent feature collection capacity and
a resolved physical parent/path. A collection maximum of 64 alone is not
proof of report 03; another report may own that capacity. Other report IDs may
coexist, including a larger feature report, but only report 03 is transferred.
Selection counts proven matches across both pages before opening either; it
does not prefer FF00, fall back to FF06 after a failed read, or probe both.

**Resolved loader and controller.** `sdk-api` defines module 5122 at 32786 and module
83143 at 159951. Its Webpack header registers chunk IDs including 8886 and 3143,
so a separate `main.3143.*.js` file is not needed to inspect this loader.
Likewise, `sdk-mouse-beiying` registers chunks 8056, 1274 and 2773 together.

The recovered dependency chain is:

| Evidence anchor | Static observation |
| --- | --- |
| `sdk-api`, 160235 | The `beiying` loader waits for **both** chunks 2542 and 8056, then imports module 4694. |
| Runtime, 1347 and 2296 | Chunk 2542 maps to the now-present `main.vendor-bytech.803cedc7e7fc4dcad3e3.js`. |
| `sdk-mouse-beiying`, 30552 | Module 4694 combines the mouse basic/settings wrappers. |
| `sdk-mouse-beiying`, 406 | Basic module 51274 imports module 74590 as `o`; other wrapper modules import it too. |
| `sdk-mouse-beiying`, 5305, 5471, 5505 | `T(e,t=3)` opens an authorized HID device and constructs `new o.uv(e,{reportId:t, protocolVersion:o.Gf.V2_1})`. |
| `vendor-bytech`, 117, 102531 | Module 74590 exports `uv = me`; `me` constructs the basic, OTA and display adapters and service objects. |
| `vendor-bytech`, 29685, 34567 | Basic plugin `F` creates transport `y`, protocol `M` and feature adapter `_`; `tn` forwards requests through that adapter. |
| `vendor-bytech`, 91111, 91554 | `V2_1` selects the same basic service `sn` as the other listed protocol versions. |

**Reproducible bounded decoding.** Parse each literal string array as data;
try only its finite left rotations. Evaluate the bootstrap checksum using
integer-prefix `parseInt` semantics and arithmetic AST nodes for constants,
unary signs, addition, subtraction, multiplication and rational division.
Reject nonnumeric prefixes, calls and all other AST nodes. Each table below
has exactly one rotation matching its literal target. For index `k`, use
`original[(k - base + rotation) % count]`. Substitute only the aliases bound
to that decoder in the inspected method; short aliases are reused in other
scopes. No bootstrap, function, module, `eval` or JavaScript runtime is executed.

| Decoder / array | Base | Count | Left rotation | Checksum target | Relevant resolved names |
| --- | ---: | ---: | ---: | ---: | --- |
| `d` / `w` (`I`) | 191 | 100 | 64 | 158359 | `224=request`, `248=transport`, `204=protocol`, `216=receive`, `278=onReport` |
| `x` / `E` (`m`) | 246 | 25 | 11 | 389721 | `249=reportId`, `247=sendFeature`, `266=receiveFeatureReport`, `269=connect` |
| `T` / `C` (`N`) | 283 | 55 | 40 | 225694 | `325=requestIndexes`, `284=responseIndexes`, `286=isValidResponse`, `321=decode` |
| `Z` / `V` | 121 | 12 | 10 | 930915 | `130=reportId` |
| `nn` / `J` (`Q`) | 305 | 76 | 26 | 710356 | `352=request`, `329=adapter`, `318=interceptors` |
| `rn` / `un` (`en`) | 482 | 90 | 6 | 309423 | `552=request`, `498=getUint8`, `500=buffer`, `522=bull` |
| `ee` / `te` (`ne`) | 451 | 34 | 2 | 892207 | `463=V2_1`, `460=createServices` |
| `_e` / `we` (`de`) | 456 | 38 | 21 | 120466 | `460=bull`, `472=base`, `476=getUUID`, `464=createServices` |

**Requests and response parsing.** All offsets in this and the next two
paragraphs refer to `vendor-bytech`. `sn.getBasics` (41370) calls
`bull.request(79,129)` with no data: main command `4F`, offset/subcommand `81`.
`M` (17471) defines a 63-byte request payload, command type `50`, a default
response-data limit of 10, and `readCmds = {4F}`. Its encoder (18677) emits one
packet for this call. The payload fields are checksum at 0, command type at 1,
data length at 2, encapsulation index at 3, main command at 4, offset at 5,
and data at 6. The additive checksum (18090) sums the zero-initialized payload
modulo 256, then replaces byte 0; it does not include the HID report ID.

| Read operation | SDK request | 63-byte WebHID feature payload (hex) | hidapi representation if the descriptor permits it |
| --- | --- | --- | --- |
| Basics / battery | `4F,81` | `20 50 00 00 4F 81` + 57 zero bytes | `03` + payload, 64 bytes |
| UUID / version | `4F,80` (40889) | `1F 50 00 00 4F 80` + 57 zero bytes | `03` + payload, 64 bytes |
| Wireless online | `47,00` (41252) | `97 50 00 00 47 00` + 57 zero bytes | `03` + payload, 64 bytes |

Adapter `_` (8372; `request` at 12152) encodes, calls
`transport.sendFeature`, waits and retrieves `transport.receiveFeature`, then
decodes. Transport `y` (14039) maps those operations to
`sendFeatureReport(reportId,payload)` and `receiveFeatureReport(reportId)`.
This is **not** an output-report `write`/input-report `read` exchange. The
controller sets a 100 ms receive delay; the SDK's generic retries/timeouts
are not evidence of an offline frame or permission to copy its retry policy.
The reader performs one SET_FEATURE of the 64-byte battery request, waits
100 ms, then performs one GET_FEATURE of report 03 with a 64-byte buffer.
It stops on a short write or any I/O failure; no retry or other command follows.

`M.responseIndexes` names report ID at 0, command type at 1, ACK at 2, data
length at 3, encapsulation index at 4, offset at 5 and data at 6. Its validator
(18486) checks only ACK `00` and matching encapsulation index (here `00`).
Its decoder (19328) slices `length` bytes starting at 6. It neither validates
a response checksum nor proves an echoed battery command/offset or response
command-type value. Thus the known layout is `ID TYPE 00 LEN 00 OFFSET DATA...`,
not a fully established constant response header. Do not invent additional
constant bytes from the request header.

`getBasics` consumes at least seven decoded bytes. Data byte 0 (raw response
index 6) gives `level = byte & 0x7F` and `charging = (byte & 0x80) != 0`.
The next six bytes contain profile/report/sensor/DPI/macro metadata. Examples
of the observed bit arithmetic: `00 -> 0%, false`, `64 -> 100%, false`,
`80 -> 0%, true`, `E4 -> 100%, true`; values with low seven bits above 100
must not become battery readings. This does not prove that an all-zero reply
is a live 0% report. The SDK's default 10-byte data limit is a request
pagination setting, **not** a descriptor length or proof of a fixed 10-byte
battery reply. A 63-byte outgoing buffer likewise does not prove the incoming
feature report's complete Windows length.

The implemented parser requires exactly the runtime-proven 64-byte report,
ID `03`, ACK `00`, encapsulation index `00`, and declared data length 7..58
so all decoded data fits after index 6. It follows the SDK signature checks;
TYPE and OFFSET are not assumed to echo request bytes, and no reply checksum
is invented. Empty, short/oversized, wrong-ID/ACK/index, invalid-length,
all-zero, request-echo and out-of-range-level replies produce no reading.
A structurally valid reply may report 0%; an all-zero buffer is not such a
reply. Synthetic fixtures test these rules, not actual receiver captures.

**Initialization and freshness.** `me`, `tn`, `_`, `y` and basic-service `sn`
constructors allocate objects, bind handlers and install an `inputreport`
listener; no HID send/receive occurs on their basic creation path. The wrapper's
`init` (7769) calls UUID, then basics (8426), then installs listeners. UUID is
the read frame above; the controller does not cache a token, change protocol
mode or consume its result to enable basics. No state-changing handshake is
required by this static SDK path. Constructing the OTA/display services does
not invoke their write methods, and they are unnecessary for an independent
battery reader. Actual cold-start device behavior remains untested.

The refresh path (`sdk-mouse-beiying`, 9480, 10294) subsequently calls
`checkWirelessOnline()`, which returns true only if its first decoded data
byte equals `01`. The wrapper treats a failed online query as unknown and can
still display basics. Neither the basic parser nor its generic ACK validator
establishes whether an asleep receiver returns an error, zeros, a cached level
or silence. This reader sends no separate online query: missing or invalid
basics yields no new reading and no engine error, leaving history to the
existing engine retention policy without renewing its timestamp or claiming
a live connection. It cannot distinguish a structurally valid cached device
reply from a fresh sample; that remains a hardware acceptance boundary. Passive bubble
events (`M.decodeBubble`, `ce` at 93002) use main `FA`, subcommand `05` for a
battery event; the handler returns a byte, and the wrapper at 8832 defaults
charging to false. That event path is not a substitute for the charged-bit
basics reply or proof of a fresh polled sample.

**Previous false lead remains excluded.** Shared-file keyboard module `68572`
has `getBatteryStatus()` at 150853 using command `87`. Its `xn/_n` table has
72 strings, base 206, rotation 39 and checksum target 446656 (`Sn(271)=encode`,
`Sn(208)=send`, `Sn(241)=decode`). Neither that keyboard command/collection nor
the existing `089D:062F` reader's `00 30 ...` / `M156` format applies here.

**State-changing operations to exclude.** The table identifies concrete
controller requests where established; these are command arguments, not a
denylist sufficient to make arbitrary requests safe. Every operation outside
the independently reviewed read sequence must be unreachable. No operation in
this table is sent by this task. Offsets are in `vendor-bytech` unless noted.

| Operation | Evidence anchor | Command arguments / boundary |
| --- | --- | --- |
| Factory, button and DPI reset | `It`, 57774 | `06,FF`; `06,01`; `06,02` |
| Mouse key binding / macro key binding | `ht`, 56031 | `30,<key index>` with configuration payload |
| Report rate, LOD, debounce, performance, sleep | `Et`, 59248 | `35` with subcommands `01`, `03`, `04`, `05`, `08` |
| Sensor angle, key rate, Bluetooth sleep | `Ct` (V2.1), 62781 | `35,43`; `35,44`; `35,0A` |
| DPI stages / separate X-Y DPI | `Lt` (V2.1), 67567 | `3A,00`; `3B,00` |
| DPI lighting | `sdk-mouse-beiying`, 32379 | `35,0B`, enabled/mode/brightness/speed data |
| Full board configuration | `sdk-mouse-beiying`, `writeFullBoardConfig` at 16046, implementation at 19569 | Composes macro, DPI, performance, lighting and key writes; never call |
| Macro transfer / OTA-format transport | `Gt`, 78740; `setFormatSpace` 84574; batch data 84708; end 84946 | Uses OTA adapter `S`: authorization/start `44`/`57`, status `52`, format `55`, data `50`, checksum `51`, end `5E`; exclude the entire transfer service |
| Display/image/time writes | Display service `$t`, main commands visible in its methods | `A0`..`A3`, `B1`, `B2`, through report `09`; unrelated to battery |
| Receiver pairing | App `pairing.clickToStart`, 974197 | No target-specific pairing method/frame established in the traced Bytech mouse API; other SDK pairing frames must not be borrowed |
| Firmware upgrade | App `otaAuthFailed` / `otaTransferError` near 973987; separate OTA adapter `S` | Full target firmware authorization/erase/transfer flow not established; never call OTA helpers or send guessed upgrade frames |

Hazard-table decoder parameters `(array, base, count, rotation, target)` are
`pt=(ft,117,29,27,686379)`, `dt=(wt,187,14,7,950879)`,
`mt=(xt,284,42,18,817285)`, `Nt=(gt,461,18,11,258563)`,
`Kt=(Xt,326,43,1,653677)`, `Bt=(jt,193,119,100,549013)`,
and `zt=(Jt,141,74,5,687846)`. Their method-local aliases are respectively
`ct`, `lt`, `_t`, `At`, `kt`, `Wt`, `Qt`. Macro methods use OTA terminology;
this does not prove that their macro-transfer framing is a complete firmware
update procedure for this receiver.

**Other candidates, also deferred.** The same mouse catalog records wireless
`372E:103D` (decimal PID 4157; S9 Pro at 1375285, S9 Max at 1374608, plus other
S7/S9 variants) and `372E:106E` (PID 4206; S7 Max at 1372218 and S7 Ultra at
1373744), with `beiying` and report ID 3. They are absent from the current
provider registrations. They route through the same controller/basic service,
so the SDK request/bit arithmetic above applies to those catalog routes too;
their collection descriptors and device response/freshness contracts remain
unproved. The wrapper's decimal `14126` occurrences are
report-rate limits for `372E:103D` and wired `372E:1029` (first at 14171), not
battery protocol definitions for `372E:1026`. None is added while these same
gaps remain. Wired catalog entries are not receiver candidates.

**Remaining evidence and acceptance boundary.** The reader, server registration,
offline ID index, exact-identity feature descriptor discovery and grade-C
catalog are now connected. Synthetic tests cover byte-exact requests, response
bit parsing/validation, unique descriptor-based selection on either page,
ambiguous/missing proof with zero protocol opens/sends, scope isolation,
descriptor caching without feature traffic, silence/history and forbidden I/O.
No hardware or network was accessed for implementation or verification.

The retained SDK path shows no state-changing handshake prerequisite; actual
cold-start device behavior remains untested. Real descriptor shapes may fail
the conservative byte-array proof, in which case the device stays unopened.
Complete real replies, TYPE/OFFSET semantics, cached responses during sleep,
percentage/charging accuracy, wake/reconnect, driver coexistence and final UI
behavior are unverified. Only `372E:1026` is added; no UUID, online, reset,
configuration, pairing, macro, display or firmware commands are reachable.

### AM Infinity 8K (Angry Miao)

**Connection:** 2.4 GHz receiver (3151:5007)

The AJAZZ Control Center project's AJ-series exchange: a zero-payload `0xF7` status poll brings the receiver's 2.4G telemetry up, then the charge reads from status report `0x05` (`05 00 00 64 01 01 01 02`, charge at byte 3 on Windows). A zero charge or junk bytes are shown as no level rather than a wrong value, and no charging state is reported. **Unverified** - no AM Infinity was on hand; the exchange is confirmed on the reference project's own unit, so the layout stands until the reporter of #72 confirms it

### ASUS ROG Gladius III Aimpoint and other ROG / TUF wireless mice

**Connection:** 2.4 GHz receiver or USB cable (for example 0B05:1A72)

The battery command G-Helper uses: output report 0 `12 07` (65 bytes) on the vendor collection of interface 0, answered by a report echoing `12 07` with the battery in byte 5 (a percentage, or a level 0-4 on older models such as the Chakram and Keris Wireless) and charging in byte 10. 0 without charging is standby, not empty, so it shows nothing; `ff aa` (command not known) and an all-zero reply are not read as a level. Receiver and cable share one icon. The OMNI receiver and models with other layouts are not included. **Unverified** - no ASUS mouse was on hand

### Corsair Dark Core RGB Pro SE

**Connection:** 2.4 GHz dongle (1B1C:1B7F)

The newer routed exchange that ckb-next calls "bragi" and OpenLinkHub "slipstream": 64-byte frames behind report id 0, route `0x09` asks the mouse behind the receiver (the receiver itself is `0x08`), command `0x02` "get property", property `0x0F` battery. The answer `01 02 00 <value:2 LE> ...` carries the level in tenths of a percent (`26 02` = 550 = 55 %). Frames that are not exactly that shape - the receiver's device records, its notices - are not levels. The request and the answer were captured from the reporter's dongle in #56 and the exchange is confirmed on hardware: KKiruano's test build run showed the same level SignalRGB does. Because the answer echoes the command and not the property, a second app on the same dongle (iCUE, SignalRGB) can put an answer for another property on the channel; the provider asks twice and takes only two equal answers, waits in a time window (traffic frames end a read early) and holds a per-dongle budget. No charging state is reported. The wired id 1B1C:1B7E is left out.

### G-Wolves HSK Pro ACE and the other models with a receiver of their own

**Connection:** the model's own receiver (e.g. 33E4:5803 for the HSK Pro ACE, #105) or USB cable

HSK Pro / Plus / Lite, HSK Plus ACE, HTX, HTX ACE, HTX Mini, HTS Plus, HTS Plus ACE, HTS Ultra, HTR, HTR Pro, HT-S2, HT-S2 Pro, Fenrir, Fenrir Max, VUK; HTM Plus, HSK Pro 2.0, HTXU, Fenrir Pro. The ids and the exchange per model come from G-Wolves' web driver list (mouse.xyz, `Config/env-models.json`). Models with "IsNewProtocol" 0 use the web driver's getOldBattery: feature report `00 02 8f 01` (00 on the cable), reply `a1 02 8f .. <charging> <battery %>`; the others use the exchange of the 8K receiver below. Same 64-byte feature report collection; receiver and cable of one model share an icon. **Unverified** - no such mouse was on hand

### G-Wolves WARG, HTS Plus (Pro), HTXU, Lycan, Fenrir Pro / Asym, HTX Mini

**Connection:** 8K receiver or USB cable

The same feature report exchange as the WLmouse mice (`00 00 02 02 00 83` out, `a1 ... 83 <charging> <battery%>` back), which G-Wolves' web driver mouse.xyz uses for every mouse on this receiver. The request goes only to the collection that Windows reports with a 64-byte feature report (HidP_GetCaps), as the web driver chooses it. A sleeping mouse keeps its last value on a greyed icon. **Unverified** - no G-Wolves mouse was on hand

### Hitscan Hyperlight

**Connection:** 2.4 GHz receiver (3770:0200) or USB cable (3770:0100)

The same 17-byte frames as the Pulsar / ATK / VXE row, on the vendor collection `ff02:0002`: [sopparus/hitscan-battery](https://github.com/sopparus/hitscan-battery) mapped them from USB captures of Hitscan Utility 1.0.2 (command 0x04, level in byte 6, charging in byte 7, millivolts in bytes 8-9) and reads them on Linux. Note the vendor application's own battery indicator is broken - it showed 100 % while the device answered 75 - so the raw byte is the truth. **Unverified** here: no Hyperlight was on hand, [so #105's reporter confirming the level](https://github.com/HeyOkay/HaloBattery/issues/105) would settle it

### LAMZU Maya X

**Connection:** 8K dongle (373E:001E) or USB cable (373E:001C)

The same feature report exchange as the WLmouse and G-Wolves mice (`00 00 02 02 00 83` out, `a1 00 02 02 00 83 <charging> <battery %>` back), on the vendor collection `ffff:0000` of interface 2 only; protocol from Sheroune/lamzu-battery-monitory (MIT). Confirmed on a Maya X on its 8K dongle

### MCHOSE A5 Pro Max

**Connection:** 1K receiver (2023:F013), 4K receiver (2023:F015) or USB cable (2023:F019)

A third MCHOSE exchange, on a third vendor id: 64-byte feature reports on the vendor collection (usage page `0xFFFF`, report id 0), from the community web driver [z750sasr/mchose-a5-pro-max-web-driver](https://github.com/z750sasr/mchose-a5-pro-max-web-driver) - the same route/length/page/command framing for every command, battery on page `0x00` command `0x83`, whose reply carries the `0xA1` marker, echoes page and command, and holds the charging state in byte 6 and the level in byte 7 (clamped to 100, as the reference clamps it). The transport is corroborated by [Klegus/mchose-macos](https://github.com/Klegus/mchose-macos), an independent reverse-engineering of the same vendor id's driver. **Confirmed on hardware** (@Pekish992, [#149](https://github.com/HeyOkay/HaloBattery/issues/149)): his 1K receiver answered 22 % and then 25 % while charging, matching the MCHOSE software and its charging animation; the 4K receiver and cable ids are still untried

### Feedback batch 1: Scyrox and deferred MCHOSE / CompX keyboards (2026-11-04)

The original issue bodies #19, #22, #24 and #25 are preserved under
`engine/tests/fixtures/feedback/`. They supply USB IDs, product strings and
usage-page/usage pairs, **not** manufacturer strings or report lengths. Test
paths, interface placeholders, descriptor lengths and replies are synthetic;
passing these tests does not establish a reading on the reporter's hardware.

**Scyrox Turbo 8k dongle, `3554:F517`: added, 待实机验证 (level C).** #19 has
`FF02:0002`, `FF03:0000` and `FF04:0002`. OpenMouse's
[Scyrox catalog](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/scyrox/index.ts)
and [V8S hardware notes](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/docs/scyrox-v8s-testing.md)
explicitly identify this PID and product string and record command `0x04`
returning 60% / 3931 mV discharging on the receiver. Its
[Scyrox client](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/drivers/scyrox/hid.ts)
inherits the Atlantis report-8 transport. The
[shared constants and checksum](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/pulsar/index.ts)
use report ID 8 plus 16 payload bytes, the existing provider's 17-byte frame;
the [battery decoder](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/lamzu/atlantis.ts)
agrees on percentage, charging and big-endian millivolt offsets.

Only this PID is added to `pulsar.PIDS`. Before opening it for IO, the reader
requires `FF02:0002` and an actual `OutputReportByteLength` of exactly 17.
Missing/other lengths or wrong collections produce no query; silence never
falls through to another collection or protocol. Only the existing battery
query is sent, with no DPI/settings writes. The name comes from the receiver's
product string (including a sibling collection if necessary), falling back to
`Scyrox 8K 接收器`. The reader's existing C verification level is retained.
Still needed: the #19 device's descriptor and a live reading/charging comparison;
the upstream notes warn that the official UI smooths a voltage-derived value,
which can differ from the device's raw percentage. No other Scyrox IDs are added.

**MCHOSE A7 V3 Pro, `3837:1018`: deferred in the initial feedback batch;
now implemented by the [separate V3 battery reader](#mchose-a7-v3-pro-38371018).** Matching `FF01:0001` does not
establish the V2 protocol, and #24 has neither RealTek strings nor feature report
lengths. More decisively, OpenMouse's
[V3 codec](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/mchose/v3.ts)
assigns `1018` to the V3 8K receiver: 64-byte **output** frames with report ID
`0x4D`, plain payload with an XOR checksum, and command `0x0900` for identity
and power. This cannot reuse V2's inverted `0x11`/`0x12` feature reports or
command `0x06`. The initial batch excluded this ID from the vendor-wide V2
matcher and offline index. The separate implementation below replaces that
exclusion with a bounded V3 read and adds the ID to the offline index. Descriptors
and battery / charging captures from the exact Pro model are still needed;
the upstream hardware evidence describes an Ultra+ sibling. Existing V2 behavior
is preserved.

**CompX keyboard receiver `3554:FA09`: unified identification/battery path
added on 2026-11-05, C / unverified; see the [M HUB/VGN HUB evidence below](#mchose-by-keyboard-receiver-3554fa09).
`3554:FA07` remains unimplemented.** `FF02:0002` is transport shape, not evidence
that a keyboard answers a mouse battery command. The reviewed OpenMouse mouse package `0.49.0` and
[keyboard package `1.6.1`](https://www.npmjs.com/package/@openmouse/keyboard-protocol/v/1.6.1)
contain no matching keyboard IDs / CompX keyboard battery implementation. The
repository's existing `pulsar.py`, `gwolves.py` and G-Wolves Compx notes describe
mouse framing and mouse battery reads only. They do not justify a keyboard
command or reply offsets. Neither ID receives a mouse query. The FA09 reader
uses the official BY keyboard protocol on `FF04:0002`, not those mouse readers;
unknown identities use a neutral generic battery read, without claiming a
particular brand/model. Hardware captures of the exact
descriptor, responses, charging transitions and sleep/wake behavior are still needed.

Sources above were read on **2026-11-04**. OpenMouse mouse-protocol commit
`30d6ad37d0ecc9fff53f86d5058d67a88e07150d` and the two published packages use
**AGPL-3.0-or-later**, compatible with this project's AGPL-3.0 license. No new
vendor driver code or protocol is imported in this batch; Scyrox reuses the
existing battery exchange.

### CompX receiver batch: FB56, F58E, F505 and deferred K99 V3

Reviewed on **2026-11-04**. The original reports #55, #47, #49 and #43 are
preserved in `engine/tests/fixtures/feedback/issue*-body.txt`. They establish
USB IDs, product strings and collections only; they contain no report lengths
or battery responses. FB56/F58E test caps and replies, and all test paths and
interface placeholders, are synthetic. F505 was subsequently checked on the
project maintainer's VGN F1 Pro on **2026-11-05**; its battery fixture replays
the captured response described below.

| Device | Decision and exact-ID evidence |
| --- | --- |
| VGN Dragonfly F2 Master+ `3554:FB56` (#55) | Added, **C / 待实机验证**. [Batch 3 §5.6](research/batch3-protocols.md#56-dragonfly-3-se-391d1a01) explicitly records that OpenMouse's VGN transport accepts `3554:FB56/FB57`, `FF02`, report `08` with 16 payload bytes, and its VGN codec defines battery command `04` and `sum=55`. Only the requested `FB56` is added. |
| VXE R1 SE 1K receiver `3554:F58E` (#47) | Added, **C / 待实机验证**. The supplied [OpenMouse ATK client](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/drivers/atk/hid.ts) explicitly defines `VXE_R1_COMPX_RECEIVER_PID = 0xf58e`; its CompX transport uses `FF02:0002`, report `08` plus 16 payload bytes. `readBattery()` uses `04`, with percentage/charging at payload offsets 5/6 (raw offsets 6/7), followed by big-endian millivolts. |
| VGN Dragonfly 4K Receiver `3554:F505` (#49) | **Added; 本项目维护者实机确认, model-specific A / historical hardware evidence.** The maintainer's VGN F1 Pro has `FF02:0002`, input/output 17 bytes. The existing `08 04 … 49` query returned `08 04 00 00 00 02 23 00 0e b6 … 60`: valid checksum, **35%, not charging, 3766 mV**, while VGN HUB showed **36%**. This single comparison does not verify charging transitions or sleep/wake. It does not establish the reporter's differently named mouse or upgrade other Pulsar models. |
| MCHOSE K99 V3 `3837:3033`, product `MCHOSE K99 V3 2.4G` (#43) | **Not added; D / observed, unsupported.** [Batch 2 §1](research/batch2-protocols.md#1-g87-v2可实现使用-glw-键盘协议) records the shared PID but warns that the community K99 trace uses `55 03 ...` and reply byte 8, whereas the official G87 GLW path uses `05` and configuration `D[32]`. That does not establish that K99 can reuse the G87 battery field. G87's exact product-name match remains unchanged. |

The three added IDs use the existing `pulsar` battery exchange, sharing Scyrox's
strict selection: exact VID/PID, `FF02:0002`, and a known output report length
of **exactly 17 bytes** before opening for IO. Unknown or other lengths and all
sibling collections receive no query. Only `08 04` plus zero arguments and the
existing checksum is sent. Existing reply header/command/checksum/percentage
validation remains in force; silence or invalid replies never trigger another
collection or protocol. Other pre-existing IDs retain their matching rules.

For FB56/F58E, still needed: each receiver's actual descriptor, successful
standalone reads compared with the vendor display, charging transitions and
sleep/wake behavior. For F505, the maintainer's descriptor and one successful
comparison are recorded in `.ai/evidence/f505-vgn-f1pro-2026-11-05.md`;
charging transitions and repeatable sleep/wake behavior still need confirmation.
For K99, obtain exact product/firmware evidence
for the GLW configuration offset and charging bit (or a separately documented
protocol), plus report IDs/lengths and correlated battery/charging captures.
No speculative `03` query or alternate K99 reader is introduced in this batch.

Source boundary: F58E's local OpenMouse ATK source is supplied at commit
`30d6ad37d0ecc9fff53f86d5058d67a88e07150d`, **AGPL-3.0-or-later**. FB56 relies
on the existing research's V2/V3 findings and AGPL-3.0 attribution; those
[VGN transport](https://github.com/OpenMouse-Project/mouse-protocol/blob/main/src/drivers/vgn/hid.ts)
and [VGN codec](https://github.com/OpenMouse-Project/mouse-protocol/blob/main/src/vgn/index.ts)
links are mutable `main` references, not the supplied pinned ATK snapshot.
The K99 community source has no confirmed reuse licence in the research and
is not copied. No vendor driver code, settings writes or DPI commands are added.

### Windows XInput gamepad battery

Independent read-only use of Microsoft's public
[XInputGetState](https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputgetstate),
[XInputGetBatteryInformation](https://learn.microsoft.com/en-us/windows/win32/api/xinput/nf-xinput-xinputgetbatteryinformation)
and [XINPUT_BATTERY_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/xinput/ns-xinput-xinput_battery_information).
Only `xinput1_4.dll` from System32 is loaded; missing DLL/functions disable the
reader without an error. There is no fallback DLL, HID exchange or vibration.
Slots 0–3 require a successful state read before requesting gamepad battery data.
Disconnected slots/types are omitted. WIRED and UNKNOWN have no percentage;
ALKALINE/NIMH EMPTY/LOW/MEDIUM/FULL map to **about 5/30/65/100%**, with charging
unknown. Data quality is `system_property`, not a device measurement timestamp.
The maintainer observed an Xbox One controller through the Xbox wireless adapter
(`045E:02E6` / `045E:0B00`), slot 0, NIMH/FULL; the integrated reader remains C
pending end-to-end hardware verification.

Windows Bluetooth gamepad battery properties are also accepted. For every
online Bluetooth gamepad whose name contains `Xbox` (case-insensitive) and
which has a percentage, presentation suppresses one XInput wireless battery
tier, in ascending slot order. XInput cannot reliably map a slot to a Bluetooth
identity: this is a count-based heuristic that favors the system percentage,
and can choose the wrong slot in a mixed Bluetooth/adapter setup. Wired and
unknown battery types remain visible. Filtering runs after history is merged,
so suppressed slots do not return as cached duplicates. Real mixed-controller
Bluetooth deduplication and charging behavior still need hardware checks.

### MCHOSE A7 V3 Pro (3837:1018)

**C / 待本项目实机验证.** Implemented on 2026-11-04 in `providers/mchose.py`,
using the task's read-only OpenMouse source snapshot pinned to
`30d6ad37d0ecc9fff53f86d5058d67a88e07150d`. Reviewed sources:
[codec](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/mchose/v3.ts),
[HID driver](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/drivers/mchose/v3-hid.ts),
[driver fixtures](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/drivers/mchose/v3-hid.test.ts),
[protocol notes](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/docs/mchose-protocol.md),
[registry](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/drivers/registry.ts)
and [LICENSE](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/LICENSE).
The supplied LICENSE is GNU AGPL version 3, compatible with this project's AGPL-3.0
license. Only the battery framing/decoding subset was adapted; no upstream file
was copied into the tracked tree or executed, and no network fetch was needed.

The match requires **exactly `3837:1018 / FF01:0001`**, with input and output
caps both **64 bytes including report ID `4D`** (63-byte body). Caps come from
the existing topology inventory; unknown or different lengths prevent opening
for I/O and sending. Other collections and legacy protocols are never tried for
this ID, including after an invalid reply or timeout. Other V3 receiver/wired
IDs in OpenMouse's broader table are not added by this change.

Only `readDeviceInfo`, command **`0x0900`**, is sent: full frame
`4d 01 01 00 00 09 00 00 08`, then zeros to 64 bytes. Full-frame offsets are
report ID 0, version 1, flags 2, payload length 3, little-endian command 4–5,
business code 6, sequence 7, payload from 8. With flags 1, XOR of offsets
`2..7+length` must match offset `8+length`; flags 0 has no checksum, as in the
upstream codec. Unknown flags, wrong ID/version/command, bad lengths or XOR
are refused. The declared data must contain at least 14 bytes: vendor at 0–1
must be `3837`, link at 10 must be nonzero, charge at 11 is a nonzero boolean,
and percentage at 12 must be 0–100. Extra declared data is accepted and covered
by the checksum. The reply PID is not used to identify the model: the upstream
Ultra+ returned `4026` (an A5 ID). Use the USB product string, including a sibling
collection, or the neutral `MCHOSE V3 8K 接收器` fallback.

The local reader sends one battery query per poll, then waits at most 1500 ms
and 32 input reads, ignoring unrelated or malformed frames. This clears the
upstream's measured ~1001 ms deferred response. It does not reproduce the full
driver's multi-command status read or its immediate busy/time-out retries.
Busy (`ff` payload), refusal (`0000` command), disconnected link and silence
produce no fresh percentage; any existing reading remains offline within the
legacy cache lifetime. Loss of the required collection/caps drops the V3 cache.
No settings, DPI, lighting, firmware or feature-report commands are sent.

Offline tests reconstruct framing around the upstream 87% discharging fixture
and captured Ultra+ 85% charging payload, and cover invalid replies, input noise,
I/O failures, time/read limits, caps refusal, V2 isolation, cache freshness and
public C-level metadata. They are **not Pro hardware captures**. Still needed:
the exact Pro descriptor/report layout, firmware, live percentage/charging
comparison, sleep/wake behavior and coexistence with M HUB. Shared receiver PID
and Ultra+ evidence do not establish successful reads on every Pro variant.

### MCHOSE A7 V2 Ultra

**Connection:** 2.4 GHz receiver (3837:100B, RealTek strings)

The same protocol as the M7 Ultra on MCHOSE's newer vendor id, which the reference driver treats identically: the diagnostics in [#4](https://github.com/HeyOkay/HaloBattery/issues/4) show the same interface shape (collections 0xFF0B:0x104 and 0xFF01:0x01 on interface 2). The status read is documented on the shorter 0x11 report, so both report ids are tried, and the mouse is named from the receiver's own product string. Icon of its own, so it and an M7 Ultra stay two devices. **Unverified** — no A7 V2 Ultra was on hand, so a level out of range is refused rather than shown

### MCHOSE G7

**Connection:** USB (A8A5:2255, chip 'YJX-CHIP')

A different chip from the M7 Ultra, and a different protocol: a 65-byte output report `00 55 30 A5 0B 2E 01 01 01`, answered by an input report starting `AA 30` whose byte 8 is the level and byte 9 the charging flag. Written from @kek353's own monitor and the device dump in [#8](https://github.com/HeyOkay/HaloBattery/issues/8); only its 0xFF01 vendor collection is written to. Confirmed on @kek353's G7, which answers `aa 30 a5 0b 0a 01 01 01 2e 00 00 00` — 46%, not charging, the same level their own tool shows — and again on its cable (`aa 30 a5 3c 0a 01 01 01 2e 01 00 00`, byte 9 = 1 while charging) with the PID unchanged, so a G7 keeps one icon on the dongle or on the cable. Only the `AA 30` header is relied on: byte 3 differs between the two (`0x0b` against `0x3c`). A level out of range is refused rather than shown

### MCHOSE M7 Ultra

**Connection:** 2.4 GHz receiver (5253:1020)

The vendor collection (usage page 0xFF01; the sibling 0xFF0B never answers): feature report 0x11 (the shorter report, tried first) or 0x12 with command 0x06, every payload byte inverted, which returns `53 52 31 00 02 05 07 00 09 64 00 64` — vid 0x5253, model 0x31, firmware, flags, then the level and the charging byte (1 while charging). The request has to be repeated for each read, and the receiver only relays a real value while the mouse is awake: asleep it answers with zeros, so a silent mouse keeps its last level on a greyed icon. On the cable the mouse answers on its own PID (5253:0031, the number it reports as its model id) and the receiver goes quiet: both connections share one icon, and whichever one reports charging wins

### Pulsar X2 V2 Mini, Pulsar X2 CrazyLight Mini, ATK VXE R1 SE+, VXE R1 Pro Max

**Connection:** 2.4 GHz dongle (3554:F508, 373B:1085, 3554:F58A, 3710:5406) and USB cable (3554:F507, 3554:F58F, 3554:F58C, 3710:3414)

17-byte big-endian frames, report id 0x08: command 0x04 asks for the power details and answers with the level in byte 6, the on-cable flag in byte 7 and millivolts in bytes 8-9, with a checksum (0x55 minus the sum of the first 16 bytes) in byte 16. From andrewrabert/python-pulsar-mouse-tool, which also backs the "HID: pulsar" driver in review for Linux; the Kysona M600 and VXE Dragonfly R1 Pro use the same protocol but their ids are not claimed here. The ATK/VXE control panel of the OpenMouse project (@openmouse/protocol, drivers/atk) lists the R1 Pro Max receiver as 3554:f58a and reads it with the same command 0x04 frame, opening the collection with usage page 0xFF02 and usage 0x0002 - the R1 Pro Max dongle has five other interface-1 collections and the first of them is not the one that answers, so that collection is now preferred; a collection whose output report cannot carry the 17-byte frame is skipped, because Windows refuses that write and the refusal looks exactly like a device that is switched off. The framing is also in G-Wolves' own web driver (mouse.xyz), whose Compx class sends the same report-0x08 frame with a checksum of 0x55 minus the sum of the payload. The R1 Pro Max is confirmed on hardware on both of its transports, by the reporter of #87: this provider read the mouse on its stock 1 kHz receiver, on the `ff02:0002` collection, and on its cable the level agreed with ATK's own panel (hub.atk.pro) with the charging flag following the cable in both directions, so the cable id (3554:F58C) is read on hardware as well rather than only claimed from their report. A frame whose checksum does not match is refused rather than shown. The X2 CrazyLight Mini (#164) was tested by its reporter on both of its transports with this same frame - the dongle and the cable both answered command 0x04 with valid checksums, the cable reporting charging - so both of its ids (3710:5406, 3710:3414) are claimed from that report; the app-side reading was confirmed on their run of this branch in #164 - the dongle read 20 %, the same raw level as their own test, and the cable read while charging.

### Razer Basilisk V3 Pro, Razer Basilisk Ultimate

**Connection:** 2.4 GHz receiver

The standard Razer 90-byte feature report, as used by Synapse and OpenRazer: power class 0x07, commands 0x80 (battery) and 0x84 (charging)

### Razer DeathAdder V4 Pro

**Connection:** 2.4 GHz receiver (1532:00BF)

Same mice protocol: transaction id 0x1F, command class 0x07, command 0x80 answers `02 1f 00 00 00 02 07 80 00 ab`, and raw 0xAB = 171/255 = 67%, stable across polls and unchanged while Synapse runs. Idle, the mouse answers status 04 and keeps its last level on a greyed icon

### Razer Viper V3 Pro SE

**Connection:** USB cable (1532:00DE), 2.4 GHz receiver (1532:00DF)

**Project verification: C, pending hardware validation.** Both IDs use the same
existing battery/charging feature-report reader as Viper V3 Pro (00C0/00C1),
with preferred transaction ID `0x1F`. No other model's handling changes.
The diagnostic report in #78 identifies `1532:00DF "Razer Viper V3 Pro SE"`;
it does not verify battery or charging readings on this project.

Sources: OpenRazer's [mouse IDs](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.h)
define `USB_DEVICE_ID_RAZER_VIPER_V3_PRO_SE_WIRED` / `..._WIRELESS` as `0x00DE` /
`0x00DF`. Its [mouse driver](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.c)
groups them with `VIPER_V3_PRO_WIRELESS` in the charge-level cases and selects
`transaction_id.id = 0x1f`. The [daemon mouse model](https://github.com/openrazer/openrazer/blob/master/daemon/openrazer_daemon/hardware/mouse.py)
with `USB_PID = 0x00DF` exposes `get_battery` and `is_charging`.
OpenRazer is GPL-2.0; only device IDs, transaction values and capability facts
are cited here, with no driver implementation copied.

### Razer wireless mice (other OpenRazer models)

**Connection:** 2.4 GHz receiver or USB cable

The same exchange as the Razer mice above: the 90-byte feature report, command class `0x07`, command `0x80`, transaction ids from OpenRazer's device entries. Pro Click, Pro Click V2 and V2 Vertical, Naga, Viper, DeathAdder, Basilisk, Mamba, Lancehead and others - `KNOWN` in `providers/razer.py` holds the full list. Battery is only read for the models OpenRazer itself reads.

### SteelSeries Aerox 3 Wireless

**Connection:** 2.4 GHz dongle (1038:1838)

The receiver's battery query: it flags its configuration opcodes with `0x40` over their wired values, so the wired `92` stays silent and a 64-byte `00 d2 ...` is answered by a report echoing `d2`. The level byte is the charging flag in bit 7 plus a 1..21 step value (or a direct percentage above 21), as yurtemre7/steel-mouse decodes it; the interface and the product id come from alloyctl's reverse engineering of this exact id, cross-checked against the capture notes at gort818/aerox3-wireless. A level byte of 0 means off or asleep rather than empty, and the 2.4 GHz link sleeps when the mouse is idle - it wakes on the next movement, so a poll that lands on a sleeping mouse simply shows nothing. Works alongside SteelSeries GG; the CS2 Dragon Lore edition (1038:1878) shares the protocol and is included untested The Aerox 5 Wireless (1038:1852, 185C, 1860) and Aerox 9 Wireless (1038:1858, 1874) in 2.4 GHz mode use the same battery exchange in rivalcfg (`0x92` with the wireless flag `0x40`, level `(value - 1) * 5`); the level is confirmed on an Aerox 9 Wireless by @AJD00m in #79 (`d2 04 ...` = 15 %, the same as SteelSeries GG), the charging bit is not tested yet

### SteelSeries Rival 3 Wireless

**Connection:** 2.4 GHz dongle (1038:1830)

The mouse exchange on the same interface and the same `0xFFC0` configuration collection as the headsets, but not the `b0` request: a 64-byte `00 aa 01 ...` out, answered by a report carrying the level. The two references disagree about the reply and neither has been on hardware here, so both shapes are read: a reply that echoes `aa` (level in byte 1, charging in byte 3, as yurtemre7/steel-mouse reads it) and a 3-byte reply read the way flozz/rivalcfg does (level in byte 0, charging in byte 2, the same request and the same hidapi read). A leading report id byte of `0x00` is skipped, a level above 100 is refused in both shapes, and in the 3-byte shape the charging byte has to be 0 or 1 so a stray report cannot pass as a level - the diagnostics print the raw reply and say which shape was used. The collection is picked by usage page `0xFFC0` rather than by interface number, because the Rival 650 has it on interface 0 (rivalcfg issue #202). Works alongside SteelSeries GG; the Gen 2 revision (1038:1872) is included untested

### WLmouse Beast X and Beast X Mini Pro

**Connection:** 8K or 1K receiver, or USB cable

The same feature-report exchange as the Beast X Max (`00 00 02 02 00 83` out, `a1 00 02 02 00 83 <charging> <battery %>` back), on the vendor collection `ffff:0000`. The provider's receiver table lists `0xA887` (Beast X) and `0xA868` (Beast X Mini Pro); the Max is confirmed, these two are not.

### WLmouse Beast X Max

**Connection:** 8K receiver (36A7:A880) and USB cable

Feature request `02 02 00 83`; if there is no reply, the mouse heartbeat is used. Receiver and cable share one icon. A receiver whose mouse is switched off is walked only every fifth poll once nothing is shown (each walk refuses on every vendor collection, ~0.75 s apiece, then listens ~2 s), and at once when the receiver's device set changes (#62 item 5)

### NZXT Lift Elite Wireless (1e71:2101)

**Connection:** 2.4 GHz receiver (enumerates as "NZXT Lift Elite Dongle"); the
same mouse on its USB cable enumerates as `1e71:2129` and speaks the same
protocol, so both ids are read.

No open-source project reads this mouse's battery (OpenRGB covers the wired
Lift's LEDs only), so the protocol comes from a USBPcap capture of NZXT CAM
talking to the dongle ([#148](https://github.com/HeyOkay/HaloBattery/issues/148),
2026-09-30). CAM speaks in 64-byte reports that start `4e`: a request on
interrupt OUT, `4e 02 81 00 b0 ..` (02 = the mouse behind the dongle, `81` =
read a property, `b0` = the telemetry one), answered on interrupt IN with
`4e 02 97 ..` about 25 ms later, after a `4e e5` acknowledgement. In the
reply, bytes 17-18 little-endian are the battery percent (the captures hold
77 then 78 across the reporter's charging session, and 84 while wired) and
bytes 8-9 are the cell voltage in millivolts (4093 wireless, 4348 while
charging); byte 22's low bit reads 1 while the mouse is on its charging cable.
The wired id (`1e71:2129`) carries the identical `ffca:0001` collection and
was read by CAM with the very same request. The flags byte 7 and the
rest of the frame are not decoded and nothing is guessed from them. The
conversation rides the `ffca:0001` collection (the same page OpenRGB uses for
the wired Lift), picked by usage page. **Confirmed on hardware** by @MrBeat93:
both connections read (2.4 GHz and USB cable); the level tracks CAM (one step
above its smoothed panel) and charging follows the cable. While NZXT CAM
itself is open the dongle stops answering this request - the app keeps the
last value on a greyed icon, so live readings want CAM closed.

## Mice and keyboards

### VGN 2.4G keyboard receiver (320F:5088)

**A — one historical project-maintainer hardware reading (2026-11-05).**
The [VGN HUB official driver](https://drive.vgnlab.com.cn/), bundle
[`assets/index-ZngoA_qG.js`](https://drive.vgnlab.com.cn/assets/index-ZngoA_qG.js)
(fetched 2026-11-05 in the supplied evidence), lists `320F:5088 / FF1C:0092`,
report `4`, receiver, type `weisheng`, for VGN S99 / V98 PRO / VXE75 V2 2.4G
Dongle. Its `getElectricQuantity()` sends `sendReport(4, [0,0,0x1A,6,0,0,0])`;
the input callback takes `data[7]` when `data[2]==0x1A` (data excludes the ID).
The vendor bundle has no identified open-source licence; only protocol facts
are used in the independent `providers/vgn_keyboard.py` implementation.

Only this VID/PID and collection qualify. The same byte-array descriptor checks
as G3M must prove input/output report `4` are each exactly 64 bytes including
the ID. Unknown lengths or non-unique eligible collections send nothing. The
only query is `04 00 00 1A 06` plus 59 zeroes: byte 1 is `00`, not G3M's `20`.
A reply must be exactly 64 bytes with `r[0]==04`, `r[3]==1A`, `r[4]==06` and
`r[8]` in 0..100. An exact request echo or a malformed reply is ignored; an
otherwise identical zero-percent frame cannot be distinguished from an echo.

Maintainer caps: interface 1 `FF1C:0092`, input/output 64 bytes, report `4`.
While asleep the receiver returned `04 00 00 FF 06 00...` 15 times; after key
input it returned `04 00 00 1A 06 00 00 00 5D...`, or 93%, while VGN HUB showed
94%. `r[3]==FF` means asleep/offline and produces no new reading or error;
the engine retains an unconfirmed grey historical reading as for silence.
Charging is unknown (`charging_known=False`); no response checksum is guessed.
The device product string is the display name, or `VGN 2.4G 键盘` if empty.

This revises the earlier blanket prohibition on querying `5088` in
[batch-3 research](research/batch3-protocols.md#45-vgn-24g-键盘接收器-320f5088-2026-11-05-补充):
official evidence now names the ID, rather than borrowing G3M's protocol.
Rainy75 (#23) shares `320F:5088` and the product string `Wireless Gaming Keyboard`
but is absent from the VGN device table and is **unverified, not claimed
compatible**. ID, collection, descriptor and product cannot distinguish it, so
it may receive this read-only query; replies outside the validated format never
produce a level. Wired `320F:5055` and other IDs are excluded. A single 93%/94%
comparison and partial sleep/wake observation do not validate every listed VGN
model or firmware, charging transitions, independent cold starts or final UI.

### MCHOSE G98 V3 (3837:1015, QHW)

**Project verification: C, pending hardware validation.** Only the exact product
string `MCHOSE G98 V3 2.4G` and collection `FF80:0001` are eligible. Both input
and output must declare only report ID `4D`, with descriptor capacities of at
least 10 and 8 payload bytes respectively. Windows caps lengths include the ID
slot. The request is zero-padded to that output capacity; input must have its
exact descriptor length and ID. Unknown lengths/IDs and ambiguous collections
are refused before command I/O. No probing on `FF70`, `FF81` or `FF31:0074`.

The only output payload is `01 03 00 04 09 00 ss cc`, where the sequence starts
at zero, wraps at 256, and `cc = 0E XOR ss`. A reply must echo version, flags,
command `0904`, bizCode and sequence, and declare exactly two data bytes:
`charge level`. Percentage is 0..100; charge 0 is discharging, 1 charging,
2 full (charging=false); other values are rejected. The trailing reply checkCode
algorithm is unconfirmed and is **not** validated using a guessed XOR. Padding,
fixed hardware report lengths, full-state behavior, independent cold-start reads
and stale receiver replies while asleep/off still need real captures.

### MCHOSE G87 V2 (3837:3033, GLW)

**Project verification: C, pending hardware validation.** The PID is shared with
other keyboards, so the exact product `MCHOSE G87 V2 2.4G` and collection
`0001:0000` are mandatory. `FF31:0074`, standard keyboard/mouse/consumer
collections and other product names never receive these requests. Output must
be unnumbered, 64 payload bytes (65 Windows/hidapi output bytes). Input must
declare exactly one ID and at least 64 payload bytes. That descriptor ID and
capacity determine the exact accepted input layout: hidapi strips the zero ID
slot on unnumbered input, but retains a nonzero ID. Multiple IDs are rejected,
because collection-wide maximum caps do not prove per-ID capacity.

Only three output requests are used: `04` reads base offset 0/length 56; then
`05` reads the active profile at offset `64*profile`/length 56 and
`64*profile+56`/length 8. The `55 cmd 00 sum len offsetLE 00` header is padded
to 64 payload bytes; sum is the low byte of payload[4..63]. Base data[0] must
be in 0..3; a 0..3 permutation in data[2..5] maps it to the active slot,
otherwise the documented legacy direct index is used and diagnosed. Invalid
indices stop the read; no `setBase`, repair, handshake, heartbeat or settings
commands are sent. Replies must start `AA`, echo command and offset, and declare
the full requested length; a short final segment is not a shorter HID report.
Both segments must be complete before using config[32]: low seven bits are a
0..100 percentage and bit 7 is charging. A failed read never becomes zero.
Reply checksum/other header flags are unconfirmed and are not guessed. Active
profile changes during a read, charging/full transitions, padding, cold-start
independence and sleep/off caching require hardware acceptance.

For both keyboards, descriptor IDs come from Windows `HidP_GetValueCaps` and
`HidP_GetButtonCaps` using the existing zero-access discovery handle, without
reading device input or feature reports. Reads have a one-second/32-frame bound
per request. The mouse provider explicitly skips both keyboard PIDs even when
keyboard matching fails. Provider diagnostics record refusals and unconfirmed
protocol boundaries, also exposed in the engine JSON health diagnostics. Native
UI text is unchanged and does not consume that additional diagnostics array.
An unresolved Windows USB parent is refused: path-scoped readers cannot rule out
another collection belonging to the same receiver. Tests use synthetic HID/caps
only, not hardware evidence.

Sources: [batch2 research, sections 1/3 and source table M1/M2](research/batch2-protocols.md),
the official [M HUB application](https://www.mchose.com.cn/assets/main-ChPQDflP.js)
and [CZ SDK](https://www.mchose.com.cn/cizhou/CZ_SHARED_DATA/main.b1d3fb6f68d495cc5961.js)
(SHA256 recorded in the research). No open-source reuse licence was found for
the vendor code. These readers independently implement documented protocol
facts; no vendor source or automatic repair branch was copied. Community
G87 `3837:201B`, G87 `41E4:2001`, K99 and G98 V2 protocols differ from these
QHW/GLW readers and are not reused here. `41E4:2001` is now separately gated by
the [BY reader](#mchose-41e42001-and-other-41e4-identities). The research table records their MIT, missing or
unverified licence status; none establishes acceptance for these two identities.

### Keychron Ultra-Link 8K, Keychron M5

**Connection:** 2.4 GHz receiver (3434:D028) and USB cable (3434:D048)

Keychron's vendor protocol on interface 4: a 64-byte feature report `b3 06` (status), answered by a 64-byte input report `b4 06` whose byte 20 is the level, retried up to three times. From csutcliff/keychron-battery-dkms, which implements it for these two ids. **Unverified** - no Keychron device was on hand, so a level above 100 is refused rather than shown

### Lofree Hyzen

**Connection:** 2.4 GHz dongle (388D:0025)

The transaction Lofree's own web driver (hyzen.lofree.tech) uses, on report 0x04 of the vendor collection 0xFF1C:0x92: start (`00 00 01`), the command, end (`00 00 02`), each acknowledged in byte 2. Command `AA` asks if the keyboard is online, command `1A` returns the level in byte 7. As in the web driver, only the dongle is read, and not while the same keyboard is on its cable; no charging state. **Unverified** - no Lofree keyboard was on hand

### Logitech G502 LIGHTSPEED, G502 X PLUS

**Connection:** Lightspeed receiver (046D:C539, 046D:C547)

HID++ 2.0 on the receiver's vendor interface: the device name (feature 0x0005) and the first battery feature the device supports (0x1004 unified battery, 0x1000 battery status or 0x1001 battery voltage; the G502 LIGHTSPEED reports voltage, converted to % with the Li-ion curve used by Solaar, the G502 X PLUS the unified battery percentage). The icon follows the device's unit id (feature 0x0003). Works alongside G HUB

### Razer BlackWidow V3 Pro

**Connection:** 2.4 GHz receiver (1532:025C), cable (1532:025A)

The standard Razer commands: class `0x07` id `0x80` for the level (0-255, shown as a percentage) and id `0x84` for charging. The keyboard answers on its own control collection, not a `ff00` vendor page - the probe order's ranking is a preference, not a filter, so it reaches any collection that answers. From OpenRazer's keyboard driver: `razer_attr_read_charge_level()` reads the wireless id with transaction id `0x9F` and the wired id with `0x3F`, and `razer_get_report_params()` puts both on USB interface 2. **Unverified** - no Razer keyboard was on hand; the diagnostics name every interface/usage they try, so a dump from the reporter of #56 settles it

### Razer DeathStalker V2 Pro TKL

**Connection:** HyperSpeed receiver (1532:0296) or USB cable (1532:0298)

The standard Razer commands (class `0x07` id `0x80` for the level, 0-255 shown as a percentage, and id `0x84` for charging) with OpenRazer's keyboard-driver values: transaction id `0x9F` on the receiver on USB interface 2, `0x1F` on the cable on interface 3 (`razer_attr_read_charge_level()` and `razer_get_report_params()`). That interface is asked first, the others stay a fallback. The receiver's product string is " DSV2Pro TKL", without a wireless word, so the PID is listed in `KNOWN` and always polled. The icon shows the keyboard pictogram. Confirmed on hardware over the receiver (#106)

### Razer DeathStalker V2 Pro, BlackWidow V3 Mini, V4 Mini and V4 Tenkeyless HyperSpeed

**Connection:** HyperSpeed receiver or USB cable

The same exchange as the DeathStalker V2 Pro TKL above, with the ids and interfaces from OpenRazer's keyboard driver: DeathStalker V2 Pro 1532:0290 (receiver, `0x9F`, interface 2) and 1532:0292 (cable, `0x1F`, interface 3); BlackWidow V3 Mini HyperSpeed 1532:0271 / 0258 and BlackWidow V4 Mini HyperSpeed 1532:02BA / 02B9 (`0x9F` / `0x1F`, both on interface 3); BlackWidow V4 Tenkeyless HyperSpeed 1532:02D5 (receiver, `0x9F`, interface 2) and 1532:02D7 (cable, `0x1F`, interface 3). **Unverified** - only the DeathStalker V2 Pro TKL has been tested on hardware so far

## Controllers

### Flydigi APEX5 (37D7:2501)

**C — pending this project's hardware validation.** `providers/flydigi.py` follows
[batch-3 research §4.3](research/batch3-protocols.md#43-apex5-37d72501).
Only `37D7:2501 / FFA0:0001` qualifies, with one eligible collection per physical
device. Input report `04` and output report `03` must each be proven 32-byte
byte arrays by Windows caps, including the report ID. APEX5's additional output
`08` (13 bytes including ID) is allowed but never written; its capacity cannot
substitute for report `03`. `FFEE:0001`, charging docks and other PIDs are excluded.

The only request is GET_INFO: `03 5A A5 01 02 03` plus 26 zeroes, with the request
sum `03` at byte 5. No IMU/third-party-control enable, rumble, trigger, lighting
or mode-setting commands are sent. Replies must be exactly 32 bytes, start
`04 5A A5 01 01 00 80` (observed single-fragment APEX5 model) and identify wired
`01` or dongle `02` at byte 7. Other models/fragments remain unknown.

Byte 12's low nibble is a tier 0..5, mapped to 0/20/40/60/80/100 with a nonempty
`approx` label and `kind="gamepad"`. It is never advertised as 1% precision.
High nibble `01` is the vendor charging sentinel: report charging and explicitly
clear the percentage, including any historical value. High nibble `00` is the
non-charging case and requires tier <=5; other states are rejected, without
clamping bad tiers to charging. No response checksum algorithm is assumed.

Sources F1/F2/F3: mkaliaha/openflydigi commit
`11653507bbe4dc77f3c479c2a7aaebf25bbda995`,
[PROTOCOL.md §2/§7](https://github.com/mkaliaha/openflydigi/blob/11653507bbe4dc77f3c479c2a7aaebf25bbda995/PROTOCOL.md)
(CC0-1.0),
[device.py](https://github.com/mkaliaha/openflydigi/blob/11653507bbe4dc77f3c479c2a7aaebf25bbda995/flydigi/device.py)
and [motion.py](https://github.com/mkaliaha/openflydigi/blob/11653507bbe4dc77f3c479c2a7aaebf25bbda995/flydigi/motion.py)
(MIT, copyright 2026 Mikalai Kaliaha), checked 2026-11-04.
The [per-file licence index](https://github.com/mkaliaha/openflydigi/blob/11653507bbe4dc77f3c479c2a7aaebf25bbda995/LICENSE)
distinguishes MIT protocol code from the GPL GUI. Only protocol facts were
independently implemented; MIT/CC0 are compatible with this project's AGPL.

Still needs Windows wired/dongle caps, all six tiers, charging/unplugging,
sleep/wake, removal/reconnect, official-software coexistence and cross-firmware
comparison. Source-reported wired/dongle success is not this project's acceptance.

### 8BitDo Pro 2, Pro 3, SN30 Pro, SF30 Pro in D-input mode

**Connection:** Bluetooth (2DC8:6006 and the other ids in `providers/eightbitdo.py`) or USB

Listened to, never written: byte 14 of the controller's enhanced input report (report 0x01 over Bluetooth, 0x04 over USB; bits 0-6 the level in %, bit 7 charging), as SDL reads it. The controller sends that report only after Steam or a game has switched it on, see the note below the table. The Pro 3 (2DC8:6009) is the exception: its descriptor declares one input report, 0x04, on both transports, and it carries the level in byte 14 at all times, so the Pro 3 shows its level without Steam - **confirmed on a real Pro 3 over Bluetooth**, with Steam closed and the controller freshly turned on. In XInput mode these controllers are read as Xbox controllers. The other models are **unverified**, so a level of 0 or above 100 is refused rather than shown

### GameSir G7 Pro; FlyDigi Vader Pro

**Connection:** 2.4 GHz receiver (shows up as an Xbox controller)

Windows.Gaming.Input battery report: exact percentage and charging state. XInput is the fallback (four levels only)

### Nintendo Switch Pro Controller, Joy-Con (L) / (R)

**Connection:** Bluetooth (057E:2009, 2006, 2007)

Byte 2 of the controller's own input report: level 0-8 in steps of 2 (full, medium, low, critical, empty) and the charging bit, shown as SDL shows it (level / 8, so 100 / 75 / 50 / 25 / 0 %) with the level name in the tooltip. When Steam has put the controller in the full mode, the 0x30 reports carry the byte and nothing is written; otherwise one read-only subcommand (0x02, request device info) is sent and its 0x21 reply carries the byte. The controller mode is never changed. USB is not read (the controller charges there). **Unverified** - no Switch controller was on hand, so a level above 8 is refused rather than shown

### Sony DualSense (PS5)

**Connection:** USB or Bluetooth

Read straight from the HID input report (USB byte 53, Bluetooth full report byte 54). Over Bluetooth, see the note below the table

### Sony DualShock 4 (PS4)

**Connection:** USB cable and Bluetooth (054C:09CC)

Read straight from the HID input report: exact percentage and charging state (USB byte 30, Bluetooth full report byte 32). Over Bluetooth, see the note below the table

### Steam Controller (2025) with its puck

**Connection:** 2.4 GHz puck (28DE:1304 Proteus, 28DE:1305 Nereid)

Listened to, never written: the puck relays each controller on its own vendor collection (usage page 0xFF00 / usage 0x0001) on interfaces 2 to 5, one interface per controller slot, as SDL's driver reads it (src/joystick/hidapi/SDL_hidapi_steam_triton.c). Input report 0x43 carries the charge state in byte 0 (1 discharging, 2 charging, 4 charging done) and the level in % in byte 1; reports 0x79 and 0x46 report a controller connecting or disconnecting, and 0x42, 0x45 and 0x47 are the controller's state reports. The puck sends 0x43 about every 2.5 s while the controller is awake - ~0.4 Hz, measured over ~9k frames by [CouchTurtle/sc2-research](https://github.com/CouchTurtle/sc2-research) and on live hardware by [eva-val/steambattery](https://github.com/eva-val/steambattery), and #58's first tester run is what showed the original 0.4 s listen window missing it. A slot with a live controller is therefore listened to for up to 5 s per poll (twice the cadence) and stops early on a disconnect; a quiet slot costs only the short window. The last level is kept between reports the way SDL keeps it; a slot that goes quiet keeps its last value greyed, and a disconnect removes the icon at once. SDL sends rumble, IMU and a lizard mode command over the same report and disables lizard mode every 3 seconds - none of that is sent here, so the controller keeps behaving the way the user's games expect. The older 2015 Steam Controller and its dongle speak a different protocol and are not covered. **Confirmed on hardware** (@Kosminaut13, #58): his run caught the 0x43 report inside the 5 s window - `43 01 5f` at 95 % while awake and `43 04 64` at 100 % charging on the cable, with the tray following both states and an asleep slot keeping its last value greyed. A level above 100 is refused rather than shown

### Xbox-compatible controllers (other models)

**Connection:** USB or the Xbox wireless adapter

Any Xbox-compatible controller is read the same way as the GameSir G7 Pro: the battery level from `Windows.Gaming.Input.RawGameControllers`, with XInput's coarse level (low / medium / full) as the fallback. Controllers other than the GameSir have not been individually tested.

## Bluetooth

### Bluetooth devices, tested on the 1MORE SonoFlow headset

**Connection:** Bluetooth (on by default, can be turned off in the menu)

The level Windows itself knows (`DEVPKEY_Bluetooth_Battery`). Only devices connected right now are shown: the link state comes from WinRT (`BluetoothDevice.ConnectionStatus`, the same source as Windows Settings). A device that is also read over HID keeps one icon: the HID reading wins and the Bluetooth copy is dropped

## Others

### VGN Dragonfly3 SE 391D:1A01

**C — protocol implemented, no hardware validation.** Independent battery-only
implementation in `providers/vgn_dragonfly3.py`; only `391D:1A01 / FF01:0010`.
The other diagnosed collection `FF05:0002`, wired `1001`, Pro `1A03` and all
other PIDs are excluded. Unknown report layouts or more than one eligible
collection on a physical receiver cause no open/query.

Source: the official [VGN HUB](https://drive.vgnlab.com.cn/) bundle
`assets/index-ZngoA_qG.js`, supplied at `.ai/evidence/vgnhub/index-ZngoA_qG.js`,
retrieved 2026-11-05, SHA256
`53adfab22f478249bdb9117bb26cedf8a7d3c6c561f2f2eb735e7d86e943be63`.
No source commit or redistribution licence was provided for the proprietary
bundle. Only protocol facts were extracted; vendor implementation code is not
copied or bundled by this change. Stable locators within this exact artifact
(zero-based UTF-16 character offsets, not byte offsets):

- `1718131`: device table, VID 14621 / PID 6657, usage page 65281 / usage 16,
  `customDeviceType:"yongjiaxin"`, receiver, report ID 0.
- `2755846`: `Yd.GetBattery=48`; `2758803`: `class oc`, report ID 0.
- `2776232`: `oc.getBattery`; `2784545`: `oc.sendCmd` and adjacent
  `sendHidBuffer`; `2781311`: `oc.readHidBuffer` reply handler.
- `2884917`: `yongjiaxin` dispatch to `oc`.

REA static inventory Evidence
`ev_4a033baf1ccf93b5e4f41d744777c7f6c6e2de3aaf6b1f738874aac6e03bb5d4`
parsed the supplied JS without execution. Its semantic graph hit a node limit;
the protocol facts above were checked directly at the listed source locations.
Neither the static analysis nor synthetic tests establish hardware behavior.

| Item | Implemented boundary |
| --- | --- |
| Request | WebHID Output ID 0, 64-byte payload: `55 30` + 62 zeros. Hidapi supplies an additional leading ID-0 slot, 65 bytes total. No checksum is added by `oc.sendCmd`. |
| Descriptor gate | Windows input/output lengths exactly 65, each direction proving only report ID 0 using the existing byte-array capability checks (including a lone selector array spanning 0..255). Collection maxima alone are insufficient. |
| Input | Unnumbered hidapi input is exactly 64 bytes, without an ID slot. HUB correlates payload `[1]==30`, takes `[8]` as battery and treats `[9]==1` as wired with no battery reading. This implementation requires 0..100, rejects the exact request echo and emits no reading when wired. |
| Unknown fields | The upstream handler does not check an input report ID, byte-0 signature, or response checksum. This reader accepts only descriptor-proven unnumbered input; it does not invent a signature/checksum or interpret the wired byte as charging. |

One battery request only; no online probe, DPI, lighting, configuration, save,
mode change or firmware command. Silence uses `battery_hid`'s bounded no-reading
behavior and does not become an engine error. Charging remains unknown.
`engine/tests/test_vgn_dragonfly3.py` replies are **synthetic**, derived from the
handler, not captures. A zero-percent response identical to the request echo
is conservatively discarded. Actual Windows caps, firmware response padding,
stale receiver responses during sleep, wired/unwired transitions, physical
battery accuracy and official-driver coexistence remain unverified.

### Ninjutso Sora V2 legacy receiver 1915:AE1C

**C — protocol implemented, no hardware validation.** Independent read-only
implementation in `providers/ninjutso_legacy.py`. Only `1915:AE1C / FFA0:0001`
with product name exactly `Ninjutso Sora V2`; collection evidence is the task's
user diagnosis #112. AE8C/AE8A are not added: upstream lists their PIDs but does
not prove their collection. The compact `Ninjutso Sora V2 8K` format is excluded.

Source: [OpenMouse-Project/mouse-protocol](https://github.com/OpenMouse-Project/mouse-protocol),
commit `30d6ad37d0ecc9fff53f86d5058d67a88e07150d` (current head checked 2026-11-05),
[`src/ninjutso/index.ts`](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/ninjutso/index.ts)
lines 2, 8, 25–28, 36–37 and 135–139 (identity, legacy report and builder), and
[`src/drivers/ninjutso/hid.ts`](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/src/drivers/ninjutso/hid.ts)
lines 63–70, 441–442, 631–655 and 699–702 (selection, fields and read path).
[`package.json`](https://github.com/OpenMouse-Project/mouse-protocol/blob/30d6ad37d0ecc9fff53f86d5058d67a88e07150d/package.json)
declares **AGPL-3.0-or-later**, compatible with this project's AGPL-3.0-only.
Sources were read through the GitHub contents API at that fixed commit.

All offsets below include the feature report ID. WebHID send payloads exclude
that byte; `receiveFeatureReport`/hidapi feature replies include it.

| Step | Exact read-only request and response |
| --- | --- |
| Active profile | Feature ID 5: `05 0D 00 00 01 00 00 16` + 24 zeros (32 total, payload 31). Wait 15 ms, get report 5/32 bytes; profile is reply `[9]`. Only a well-sized ID-5 zero-profile reply gets one retry, as in upstream. Silence/malformed replies stop; a second zero stops. |
| Configuration block read | Feature ID 4: `04 26 00 00 01 00 <profile>` + 697 zeros (704 total, payload 703). Wait 90 ms, get report 4/704 bytes. No setting is modified by this read request. |
| Integrity | Require exactly 704 bytes and ID 4; `sum(reply[0:701]) == reply[702] + (reply[703] << 8)`. Byte 701 is deliberately excluded, with no modulo, matching upstream. |
| Battery | Upstream slices at offset 9 and reads data `[7]`/`[8]`, hence full reply `[16]` is percent and `[17]==1` is charging. Accept percent 0..100 and charging 0/1; reject other values. |

Before opening, require a unique physical collection and descriptor-proven
feature sizes `4:704` and `5:32`, with collection maximum 704. The shared size
proof now supports feature caps as well as input/output; topology invokes it
only for this exact new identity/collection. No size is inferred from a maximum.
The active profile must be nonzero (1..255); no unproven upper limit or command
echo/header signature is invented. A settings-request echo fails the checksum.

`readLegacySettings` requires no entering/exiting configuration mode, control
command, write/save, profile switching, firmware read or paired-product query.
The new report-6 battery commands 18/17 are **not** used. The normal engine reader
process provides the existing outer isolation/deadline for synchronous feature
I/O. Empty/invalid replies yield no reading and no engine error; previous readings
retain the existing stale-history behavior.

`engine/tests/test_ninjutso_legacy.py` uses **synthetic** replies. Upstream's own
`src/drivers/ninjutso/hid.test.ts:188–244` supports field/checksum offsets but has
inconsistent fake descriptor/report metadata (report-4 count 704 vs driver
payload 703, short ID-5 fake response, zeroed report/header bytes); those mocks
are not descriptor evidence. The driver itself allocates payload 703 and reads
through full reply offset 703. Runtime descriptor proof is mandatory; a real
704-byte payload/705-byte full report, array/split layout that cannot prove
both sizes, or compact 74-byte full report is rejected rather than guessed.
Actual Windows feature transfer lengths, wake/sleep behavior, cached-response
freshness, charge transitions, firmware variants and official-driver coexistence
remain unverified. No hardware was accessed in this task.

### Deferred VGN N75 V2 391D:2A01

**Not implemented.** The same official VGN HUB bundle above lists
`vendorId:14621,productId:10753` at UTF-16 offset `1720691`, with
`FF02:0002`, receiver, report ID 19 (`13` hex), and `beiying` dispatch. Task
diagnosis #97 for `VGN N75 V2 2.4G Dongle` shows only vendor `FF00:0001`, alongside
standard collections. This contradicts the exact official collection; the
`FF00` path must not be substituted or inferred from the shared VID/PID/name.
No provider or compatible-device claim is added. Required evidence: the correct
receiver/firmware's matching collection, descriptor-proven input/output or
feature IDs and lengths, and a battery request/reply tied to that precise
variant. Further N75 implementation remains deferred under the revised brief.

### Rapoo VT nrf54L passive input 24AE:1411 and listed wireless IDs

**C / 被动接收，待本项目实机验证.** The 69 exact wireless PIDs below are
implemented, including VT3S `24AE:1411` (**用户实机验证，#120**).
`24AE:1601` remains **D / not implemented**. Each PID was checked against the official
[Rapoo A HUB](https://hub.rapoo.cn/) bundle supplied at
`.ai/evidence/rapoohub/index-cgGQw9wn.js`, retrieved 2026-11-05 from
`https://hid.rapoo.cn/AHUB_WEB/dist/assets/js/index-cgGQw9wn.js`. SHA256:
`09a7906e669d75275a77ab394207bb6ab7264efa6eb6998fea3c792a31a36ecf`.
Only protocol facts were extracted; no vendor implementation was copied.
No redistribution licence or source commit was supplied for this artifact.

| Target / task diagnosis | Exact official identity and dispatch | Decision |
| --- | --- | --- |
| `24AE:1411`, #120 VT3S (not MAX) | `model:"VT3S"`, `mainControl:"VT3S"`, product IDs `[17937,5137]` (`4611,1411`); `dongleModel:"Dongle_NordicL5"`, `driver:"VT_nrf54L"`, MCU `N54L15` | C / 用户实机验证，#120: passive Input 07, 19 bytes, 67–68%; project hardware verification pending. |
| `24AE:1412`, #120 VT3 dual 8K | `model:"VT3"`, `mainControl:"VT3"`, product IDs `[17938,5138]` (`4612,1412`); `dongleModel:"Dongle_NordicL5"`, `driver:"VT_nrf54L"`, MCU `N54L15` | C / passive input only, pending hardware verification. |
| `24AE:1421`, #86 VT3S MAX | `model:"VT3S MAX"`, `mainControl:"VT3S MAX"`, product IDs `[17953,5153]` (`4621,1421`); same dongle/driver/MCU | C / passive input only, independently matched to this driver. |
| `24AE:14A1`, #54 | `model:"VT0 Air MAX"`, `mainControl:"VT0Air MAX"`, product IDs `[18081,5281]` (`46A1,14A1`); same dongle/driver/MCU | C / passive input only. The exact official name corrects the older batch4 note's shorthand "VT0". |
| `24AE:1601`, #36 user-reported VT9 PRO | No matching device manifest in this supplied bundle; official class, `mainControl`, `dongleModel` and filters unknown | Deferred: identity-to-protocol mapping and the entire battery exchange remain unproven. |

These four implemented identities each occur in exactly one product-ID declaration.
Their official filters all designate **`FF00:0002` as the default
"Configuration Interface (Secondary)"**. `FF00:000E` is "Only update Firmware";
`FF00:0001` is "Only update WCH Firmware". Neither update collection is a battery
fallback. Factory dispatch uses the manifest's `driver`, with
`VT_nrf54L` registered to `Ik` (`VTNrf54LMouseDevice`, extending `Os`/`Dl`).
`mainControl` and `dongleModel` identify firmware models rather than selecting
a different battery class. Each target is the second product ID and selects
wireless command routing byte `A5`; the paired wired IDs are outside this task.

The input channel is the same default channel, not a firmware-update channel:
`Ul.scan` matches `usagePage` and `usage`, `Ul.open` opens that exact path,
and `Ul.onInputReport` splits the first byte as the ID before delivering its
remaining bytes. `Dl` subscribes `this.channel.onInputReport(this.handleReport.bind(this))`
on the primary channel; `Ik.handleReport` consumes ID `7` there. The WebHID
equivalent (`Po`) forwards `event.reportId` and `event.data` from the selected
device. This source route supports `FF00:0002`; it is not a hardware capture
proving the contents of a receiver's actual descriptor.

#### Additional wireless IDs checked against the same driver

All 77 `driver:"VT_nrf54L"` manifests in this supplied bundle were inspected.
Exactly 69 list a wired/wireless PID pair: each has vendor `9390` (`24AE`),
one default `FF00:0002` filter, and no `input-report` capability selecting
the `cybercafe` wrapper. `Ik.detectConnectionType` explicitly treats the second
PID as wireless, so these 69 use the same retail `Ok` -> `dy` battery parser.
The four IDs above plus the following 65 are allow-listed at **C**; the table
records each exact identity rather than enabling a VID-wide or PID-range match.
The wired partner is recorded for evidence only and is **not** enabled.

| Wireless PID | Official model | Excluded wired PID | Manifest symbol / UTF-16 offset |
| --- | --- | --- | --- |
| `24AE:1410` | VT0 | `4610` | `NN` / `1950602` |
| `24AE:1413` | VT7 | `4613` | `BW` / `2038310` |
| `24AE:1414` | VT9 | `4614` | `pK` / `2066908` |
| `24AE:1415` | VT1 | `4615` | `YN` / `1957081` |
| `24AE:1416` | VT7 MAX | `4616` | `kW` / `2031112` |
| `24AE:1417` | VT3 MAX | `4617` | `BH` / `1990407` |
| `24AE:1418` | VT9 MAX | `4618` | `aK` / `2061407` |
| `24AE:1420` | VT0 MAX | `4620` | `RN` / `1948941` |
| `24AE:1422` | VT2 | `4622` | `gH` / `1975019` |
| `24AE:1423` | VT2 MAX | `4623` | `oH` / `1965492` |
| `24AE:1444` | VT1 MAX | `4644` | `GN` / `1953849` |
| `24AE:144A` | VT0 MAX MASTER | `464A` | `UN` / `1947358` |
| `24AE:144B` | VT3S MAX MASTER | `464B` | `GH` / `2001124` |
| `24AE:1451` | VT6 | `4651` | `yW` / `2023087` |
| `24AE:1452` | VT6 MAX | `4652` | `gW` / `2021310` |
| `24AE:1453` | VT9S V2 | `4653` | `xK` / `2075549` |
| `24AE:1459` | VT7S V2 | `4659` | `ZW` / `2048037` |
| `24AE:145B` | VT4 V2 | `465B` | `uW` / `2018027` |
| `24AE:145D` | VT2S V2 | `465D` | `kH` / `1983513` |
| `24AE:1460` | VT7 V2 | `4660` | `PW` / `2036393` |
| `24AE:1462` | VT3 V2 | `4662` | `UH` / `1994085` |
| `24AE:1464` | VT3S V2 | `4664` | `QH` / `2007938` |
| `24AE:1466` | VT9 V2 | `4666` | `dK` / `2064987` |
| `24AE:1468` | VT2 V2 | `4668` | `fH` / `1973304` |
| `24AE:146A` | VT1S V2 | `466A` | `QN` / `1960503` |
| `24AE:146C` | VT8 V2 | `466C` | `SW` / `2054684` |
| `24AE:146D` | VT3 MAX MASTER V2 | `466D` | `AH` / `1986803` |
| `24AE:146E` | VT3S MAX MASTER V2 | `466E` | `WH` / `1999525` |
| `24AE:1470` | VT7 MAX MASTER V2 | `4670` | `wW` / `2027518` |
| `24AE:1471` | VT7S MAX MASTER V2 | `4671` | `IW` / `2043081` |
| `24AE:1473` | VT9 MAX MASTER V2 | `4673` | `nK` / `2057897` |
| `24AE:1474` | VT9S MAX MASTER V2 | `4674` | `mK` / `2070496` |
| `24AE:1475` | VT2 MAX MASTER V2 | `4675` | `eH` / `1962148` |
| `24AE:1476` | VT2S MAX MASTER V2 | `4676` | `wH` / `1980247` |
| `24AE:1477` | VT4 MAX MASTER V2 | `4677` | `iW` / `2014679` |
| `24AE:1478` | VT8 MAX MASTER V2 | `4678` | `XW` / `2051410` |
| `24AE:14A2` | VT3S Air Max | `46A2` | `oW` / `2013119` |
| `24AE:14A4` | VT2K MAX | `46A4` | `hH` / `1978590` |
| `24AE:14A5` | VT3 PRO V2 | `46A5` | `$H` / `1992281` |
| `24AE:14A6` | VT3S PRO V2 | `46A6` | `qH` / `2006248` |
| `24AE:14A7` | VT7 PRO V2 | `46A7` | `MW` / `2033151` |
| `24AE:14A8` | VT7S PRO V2 | `46A8` | `KW` / `2046357` |
| `24AE:14A9` | VT9 PRO V2 | `46A9` | `cK` / `2063189` |
| `24AE:14AA` | VT9S PRO V2 | `46AA` | `_K` / `2073865` |
| `24AE:1B01` | VT9S MAX V2 | `4B01` | `vK` / `2072095` |
| `24AE:1B02` | VT7S MAX V2 | `4B02` | `HW` / `2044680` |
| `24AE:1B03` | VT4 MAX V2 | `4B03` | `lW` / `2016274` |
| `24AE:1B04` | VT2S MAX V2 | `4B04` | `bH` / `1981846` |
| `24AE:1B05` | VT7 MAX V2 | `4B05` | `bW` / `2029233` |
| `24AE:1B06` | VT3 MAX V2 | `4B06` | `PH` / `1988518` |
| `24AE:1B07` | VT3S MAX V2 | `4B07` | `zH` / `2002711` |
| `24AE:1B08` | VT9 MAX V2 | `4B08` | `rK` / `2059612` |
| `24AE:1B09` | VT2 MAX V2 | `4B09` | `sH` / `1963743` |
| `24AE:1B0A` | VT1S MAX V2 | `4B0A` | `qN` / `1958828` |
| `24AE:1B0B` | VT8 MAX V2 | `4B0B` | `JW` / `2053005` |
| `24AE:1B0C` | VT3 Air MAX V2 | `4B0C` | `NH` / `1997923` |
| `24AE:1B0D` | VT3S Air MAX V2 | `4B0D` | `sW` / `2011551` |
| `24AE:1B0E` | VT7 Air MAX V2 | `4B0E` | `OW` / `2041397` |
| `24AE:1B0F` | VT7S Air MAX V2 | `4B0F` | `jW` / `2049842` |
| `24AE:1B10` | VT9 Air MAX V2 | `4B10` | `CK` / `2068812` |
| `24AE:1B11` | VT9S Air MAX V2 | `4B11` | `LK` / `2077358` |
| `24AE:1B12` | VT2 Air MAX V2 | `4B12` | `yH` / `1977026` |
| `24AE:1B13` | VT2S Air MAX V2 | `4B13` | `MH` / `1985235` |
| `24AE:1B14` | VT4 Air MAX V2 | `4B14` | `fW` / `2019746` |
| `24AE:1B15` | VT8 Air MAX V2 | `4B15` | `tK` / `2056333` |

The other eight same-driver manifests have only a single PID and no declared
dongle: `ESM612ACE:464D`, `ESM612MAX:4681`, `ESM612PRO:4631`,
`ESM622MAX:4637`, `ESM622PRO:4632`, `ESM652PRO:4633`,
`ESM672MAX:4640`, `ESM672PRO:4635`. Their receiver identities and wireless
routing remain unproven, so none is enabled. Other driver families and
`24AE:1601` are not inferred from a nearby model name.

#### #120: Windows report-length proof, not missed notifications

The user's local 1.3.8 adaptation is retained in
`.ai/evidence/rapoo-vt3s/issue-120-plaudern.md` and `VT3S.patch`.
This is **用户实机验证，#120**, not a project-maintainer hardware test:
VT3S `24AE:1411`, `FF00:0002`, Input `07`, 19 bytes including the ID.
The reconstructed descriptor was:

```text
06 00 ff 09 02 a1 01 85 07 09 02 15 00 26 ff 00
75 08 95 12 81 00 09 02 15 00 26 ff 00 75 08 95
12 91 00 c0
```

Windows exposes its input as one non-aliased, non-range button cap:
Data,Array, report `07`, fixed usage `2`, `report_count=18`,
no input value caps, collection input maximum `19`. The previous
`byte_report_sizes()` accepted numeric byte arrays or selector ranges
`0..255`, returned `None` for this shape, and left `input_report_sizes`
unproven. `RapooProvider._pick()` therefore refused to open the device.
Adding the PID alone cannot fix this. The earlier attribution to missed
reports, including the provider comment's claimed 30–60 s cadence, was wrong
for this documented failure.

Reuse `fixed_usage_report_size()`, already used for VGN #190, via
`byte_report_sizes(..., report_type=0, allow_fixed_usage=True)` only for the
Rapoo allow-list on `FF00:0002`. It proves `{"7": 19}` for these caps by
checking the lone fixed-usage Data,Array item and `report_count == maximum - 1`.
Aliases, mixed fields, ranges, constant/variable items and count mismatches
still fail closed. No byte-for-byte descriptor fallback, extra device open,
output proof, feature query or new transport operation is introduced.

The user received 18 valid notifications in about 55 s, averaging **3.04 s**
between reports, all 68% / state `1`; the patched application later showed
67–68% with zero read errors. Example full report:

```text
07 10 01 40 06 40 06 01 44 00 03 81 00 11 46 01 00 05 b1
```

Full-report `[7]=01` and `[8]=44` use the existing status/percentage parser
without a battery request. The cadence is one user's VT3S observation, not a
promise for the other 68 models or firmware versions. No official-driver
percentage calibration, complete charging transition, long sleep/wake or
cross-firmware validation was supplied; all added identities remain **C**.

#### Confirmed Nordic facts and passive-reader boundaries

| Item | Observation in the supplied artifact / unresolved boundary |
| --- | --- |
| Battery delivery | `Ik.handleReport` handles **Input report ID `07`**, passes the body (excluding report ID) to `Ok`/`dy`, and publishes battery changes. All 69 enabled manifests use the default retail parser, not the cybercafe wrapper. |
| Fields | Body `[0]` low nibble is connection protocol, high nibble sensor; `[6]` is `batStatus`, `[7]` is `batPercent`. Thus hidapi's numbered input would place the battery fields at full-report `[7]` and `[8]`. These are status notifications, not proven replies to a battery query. |
| Charging UI | `le` passes the stored `batStatus` to `batteryStatus`; `Hr` accepts `1/2/3` as battery states, and `Ii` shows charging for `2/3` (also for wired transport, excluded here). Thus `1` maps to not charging and `2/3` to charging, following the official wireless UI. The inspected parser/UI does not distinguish full from charging for `2` versus `3`. `Jm` treats `0` as unavailable; other values have no established mapping. The reader keeps an in-range reported percentage but sets `charging_known=False` for `0`/unknown values; it never invents a full-state flag. |
| Length / integrity | `dy` uses defaults for missing bytes and conditionally parses fields through body `[17]`. This proves field offsets, **not** a universal fixed payload length. #120 supplies a 19-byte full-report VT3S descriptor; every actual collection still needs its own per-report length proof. No checksum validation is present in this parser; that does not prove the firmware has no integrity fields. |
| Request | No standalone battery query or battery EEPROM address is established for this class. `Ik` installs config/DPI/keys/performance/firmware/pairing features, but no battery feature. Its connection callback reads configuration and DPI; those operations are not documented as battery requests. No request bytes, response correlation, request length or response deadline can therefore be specified. |
| Host session | WebHID/NodeHID channel open/close only open/close host handles and register/remove input listeners. The base connection session calls the class callback, which reads configuration/DPI. This passive implementation does not reproduce those operations or report backfill. No battery-specific enter/exit command was found. This does not establish whether/when an otherwise idle receiver will emit a status report without setup. |
| Excluded command families | The AP/Telink3228C `getBatteryLevel:173` / `readBatteryStatus()` and the CH315 `batteryPercent:t[9]` parser belong to other classes. Telink8273's `BB` status path also has no dispatch link to these targets. They cannot supply the missing Nordic exchange. |

In particular, Nordic `en.init_default_flash` is **`AD`**, and
`zx.initAllConfigs` sends Output ID `06` with body `A5 AD 52 3D` for a wireless
factory reset. It is **not** the AP class's `AD` battery query. The Nordic
general command transport uses Output `06` and Feature `08` (busy `02`, OK `01`),
which are separate from passive Input `07`. No reset, configuration write,
pairing, firmware operation, guessed unlock frame, report-length trial or
cross-protocol fallback is implemented or sent.

For `1601`, bounded searches found no standalone decimal `5633`, hexadecimal
`0x1601`, product-ID declaration containing that value, or exact
`model:"VT9 PRO"`. The nearby `VT9 PRO V2` uses `46A9/14A9`, not `1601`.
The older OpenMouse `1205/4405` protocol evidence in
`research/batch4-protocols.md` still does not establish this target's class.

#### Reproducible source locations

All offsets are zero-based **UTF-16 character offsets**, not UTF-8 byte offsets.
The following strings are unique within this exact supplied bundle:

| Offset | Searchable anchor | Fact |
| --- | --- | --- |
| `1996012` | `RH={channel:["china_game","global"],model:"VT3"` | `1412` manifest and filters |
| `2009753` | `eW={channel:["china_game","global"],model:"VT3S"` | `1411` manifest and filters |
| `2004523` | `model:"VT3S MAX",mainControl:"VT3S MAX"` | `1421` manifest and filters |
| `1952330` | `model:"VT0 Air MAX",mainControl:"VT0Air MAX"` | `14A1` manifest and filters |
| `120182` | `static create(t,e){const s=this.registry.get(t.driver)` | Factory dispatch key |
| `815800` | `is.register("VT_nrf54L",Ik)` | Official Nordic class registration |
| `229394` | `class Ik extends Os` | Wireless PID selection, installed features, connection callback |
| `231310` | `async handleReport(e,s){if(e===7){const o=Ok` | Nordic input-report selection |
| `228733` | `batStatus:n[6]??0,batPercent:n[7]??0` | Default retail battery body offsets |
| `141295` | `const Dl=class Dl` | Base host connection lifecycle |
| `123291` | `class Po{` | WebHID host handles and input listeners |
| `126440` | `class Ul{` | NodeHID host handles and numbered input splitting |
| `1788098` | `const Hr=(n,t)=>` | UI valid battery states and adjacent `Ii` charging mapping / `Jm` validation |
| `1795166` | `function J(K){const te=K.features.battery,me=g.get(K.id);if(!te||!me||!S(K))return` | UI battery query requires a battery feature, absent from `Ik` |
| `150137` | `yn={outputReportId:6` | Nordic general command IDs and adjacent command table |
| `162287` | `async initAllConfigs(){const t=en.init_default_flash` | `AD` is factory reset in this class |
| `620900` | `getBatteryLevel:173` | Different AP command family, not target evidence |

REA static inventory Evidence
`ev_94a2e7e23216b9728fd6bd2dcea9f3cdffb8eda7420faeb33ebd74d2ba8ee99c`
parsed the single supplied JS file without execution or parse errors. Its
semantic graph reached its node ceiling and is partial; the identity and
protocol facts above were checked directly in the source. The current static
recheck produced application Evidence
`ev_5b8d9d5481d3711d31b7da30f58085831b5dd6a402e162c3ab73f7e1f96282af`
(summary view `ev_5476d9430c618f9a20dc4de11219c21d8b7546d6073902686e64c7dcc8fe0079`):
one JS file, zero parse failures, semantic graph partial at its node ceiling.
The 77 manifest declarations and input-report capabilities were inspected
directly as inert source. Static analysis does not establish firmware behavior
or battery accuracy.

`providers/rapoo.py` **only receives and never sends**: open, bounded input
reads, close. There is no `write`, `send_feature_report` or `get_feature_report`
(Windows GET_FEATURE also sends a request). Nordic `AD`, Output `06`, Feature
`08`, connection/configuration/DPI reads and guessed setup are never used.
The receiver must have one uniquely selected `FF00:0002` collection under a
proven USB parent. `topology` enables input-only `battery_hid.byte_report_sizes`
proof for exactly the 69 listed wireless IDs and this collection: numeric byte
arrays, a lone byte-selector range, or the opt-in lone fixed-usage Data,Array.
Collection maxima alone do not suffice;
without a proven `"7"` input length, the provider does not open the device.
The descriptor supplies the length (9..4096 full-report bytes); no fixed
payload length is inferred from the optional parser fields. Only exact-length
ID `07` input with percentage `0..100` is accepted; other reports are ignored.

The first poll after opening listens for at most **1500 ms**, bounded to 32
reads. Handles stay open between polls, which drain up to 256 queued reports
with 1 ms read timeouts and keep the newest valid battery report. This handle
lifetime and drain behavior is unchanged by the descriptor-proof fix.
**Notification frequency beyond the #120 VT3S sample is unverified**; a fresh
launch may initially have no battery reading. Silence is normal
`no_measurement`, including after a previous reading, and the UI can show
“休眠中，动一下即可刷新”. It never fabricates 0% or renews a historical timestamp.
The provider instance retains each receiver's last reading/receive time as
`cached`, with unknown current connection/charging, until that receiver leaves
the USB inventory. No configuration, output or feature descriptor is required
for this passive path. No device-pack extension is added.

Synthetic fake-device tests raise on every attempted write or feature request,
cover parsing, other report IDs, silence/history, deadlines, collection and
descriptor rejection, exact PID scope and descriptor-only discovery. No
hardware was accessed by this implementation task. The user supplied one VT3S
descriptor and passive-reading result; actual descriptors for other models,
notification frequency/freshness across firmware, standalone operation,
sleep/wake, charging transitions and coexistence with the official driver
remain unverified by this project. For `1601`, an exact official
device/class/filter mapping is still needed before any implementation.

### MCHOSE BY keyboard receiver 3554:FA09

**C / 待本项目实机验证; `kind="keyboard"`.** Independently implemented in
`providers/mchose_by_keyboard.py` on 2026-11-05. Source: the official
[M HUB bundle](https://www.mchose.com.cn/assets/main-DCRwBIvm.js), supplied at
`.ai/evidence/mhub/main-DCRwBIvm.js` (9,375,649 bytes, SHA-256
`405102805e2adeb93073ee2186f6d0193ca1317a6b4657d636e4e611bd25a446`).
No vendor implementation was copied. These are static protocol observations,
not USB captures. REA parsed the artifact without execution (Evidence
`ev_98c981e0bb862fabffdef01c9b1f5871776c8f3eca1485589034d68c1d2206b8`);
its semantic graph hit its node ceiling, so the findings below were checked
directly against the named source anchors rather than assuming full graph coverage.

#### Routing and model gate

The filter `{vendorId:13652,productId:64009,usagePage:65284}` and the matching
`getDeviceMap` predicate route FA09's **FF04** endpoint to the BY keyboard path.
The supplied #22/#25 diagnostics record `FF04:0002` alongside `FF02:0002`; only
the former is eligible. `registerHidDevice` / `instantiateDevice` create
`DeviceManager` / **`HidDeviceHandler`**. Wireless names select
`defaultWirelessRID=19` (`0x13`); other names select wired feature ID 6 or 9.

M HUB does **not** read battery on every FA09 receiver. For the exact generic
names `2.4G Wireless Receiver` and `MCHOSE 2.4G Wireless`, its identification
path (`Va`, `getPassWord`, `keyboardPwds`, `Ba` in `getDeviceMap`) reads a
six-byte model code and searches `keyboardPwds` for an exact match. Only a match
creates `realName = productName + "-" + model`; the unmatched generic entry is
removed. The home battery path (`Nd`, `isValidPrefix`, `getBattery`) then selects
that recognized receiver. `getDevName` also recognizes named K99_V2/G98/X75/
X75_V2/GX87/GX87_V2/K87/G75_Pro variants, but that alone does not prove they use
the wireless rather than wired transport.

This implementation supports **only those two exact generic wireless names**.
It identifies each receiver once per observed plug-in. A complete unknown
identity is cached: FA09 continues with a neutral battery read as described
below; `41E4:2001` still sends nothing further until removal. Silent
identification is retried on the next poll, then backs off from 60 seconds to
at most 30 minutes; silence never enables the generic battery path.
It never trusts an injected/cached `realName`,
a substring such as `X75`, the manufacturer string, or the USB ID alone. The
following source-table codes are protocol facts, not passwords or credentials:

| Six bytes (hex) | Official model key | Six bytes (hex) | Official model key |
| --- | --- | --- | --- |
| `03 00 00 00 02 1C` | G87 | `03 00 00 00 00 77` | K99 |
| `03 00 00 00 01 DF` | K99_V2 | `03 00 00 00 01 32` | G98 |
| `03 00 00 00 00 D6` | X75 | `0A 00 00 00 00 03` | G98_Pro |
| `03 00 00 00 01 C6` | X75_V2 | `03 00 00 00 01 15` | G75_Pro |
| `0A 00 00 00 00 18` | G98_V2 | `0A 00 00 00 00 2B` | UT98 |
| `0A 00 00 00 00 01` | Z75 | `0A 00 00 00 00 09` | Z75S |
| `03 00 00 00 01 C7` | KX75 | `03 00 00 00 01 74` | GX87 |
| `03 00 00 00 01 ED` | GX87_V2 | `03 00 00 00 01 35` | K87 |
| `03 00 00 00 03 35` | G75_V2 | `0A 00 00 00 00 34` | G87_V2 |

The generic-receiver branch uses the whole table, without an additional
model/PID cross-product filter. These are eligible identification results, **not
18 hardware-verified FA09 models**. Named AULA/狼蛛, NEWMEN/新盟, XUNSHE/迅蛇,
SCC100, bare MCHOSE G75, unknown names and all suffixed product strings receive
no commands here. A generic name shared by another brand receives the same
read-only identity query M HUB issues. For FA09 only, an unknown code enables
the generic `4A` battery read below. Sharing FA09 is not a hardware compatibility claim for
#22/#25/#53/#99/#108/#110/#115/#128/#129/#133 or another brand. If another brand
reuses a recognized code, these published fields cannot distinguish it; this
is an unverified firmware-identity boundary, not proof of its compatibility.

#### Shared FA09: one owner, identification before the generic battery read

`3554:FA09` belongs exclusively to `mchose_by_keyboard`, including its offline
device index and worker routing. `vgn_beiying` excludes the pair entirely, so
the receiver's FF02 collection cannot start a second query or create a VXE
card alongside the FF04 card. The existing generic-name, unique FF04:0002,
single report-ID `13` / 20-byte input/output descriptor gates remain in place.
An FF02-only or otherwise ineligible FA09 is not queried by either reader.

The first query is always the fixed read-only `05` identification frame.
A complete accepted `05` reply matching `keyboardPwds` retains the existing
`MCHOSE <model>` name and M HUB battery/charging interpretation. A complete
accepted identity reply that cannot match that table, including an unknown
six-byte code, instead sends the same fixed `4A` battery frame and uses
**`2.4G 无线键盘（3554:FA09）`**. It never infers VXE, AULA, NEWMEN, XUNSHE or
another brand from the shared VID/PID. Both identity outcomes are cached until
observed removal; subsequent polls send only `4A`. Silent identification keeps
the existing retry/back-off state and sends no `4A`; malformed frames, request
echoes and unrelated commands do not count as an identity response.

Basis for this deliberate extension beyond M HUB's model gate: the recorded
[VGN HUB beiying evidence](#vgn-beiying-keyboard-receivers) uses bundle
`index-ZngoA_qG.js` / SHA-256
`53adfab22f478249bdb9117bb26cedf8a7d3c6c561f2f2eb735e7d86e943be63`.
The `3554:FA09` receiver row is in the table starting at UTF-16 offset
`1715479`; `isBeiyingDongle` (`2873695`) explicitly includes it. The branch at
`2878686` selects `ds` for V87pro and calls `getDeviceInfo` ->
`getBatteryInternal` without a product-name or model query/gate. `ds`
(`2168848`) sends report `13`, command `4A`, using the same 20-byte BY frame.
That is evidence for a receiver-level percentage read, not evidence that
every FA09 keyboard is a VXE V87 Pro. We retain M HUB's FF04 identification
endpoint and apply this ungated battery-read policy only after an unknown
identity response, rather than probing its second endpoint.

For an unknown model, the generic parser follows VGN HUB's percentage-only
interpretation: full-frame byte 5, 1..100 accepted here, zero suppressed as
for the existing FA09 reader. It does not apply MCHOSE's flag-nibble meanings
to another brand, and exposes charging as unknown. Both HUB input handlers
discard the reply trailer; this FA09 path retains the existing no-checksum
validation policy, rather than extending #190's 258A:024F checksum evidence
to unknown FA09 firmware. Complete single-packet framing and echo rejection
remain required. A zero or invalid battery reply creates no new reading and
does not refresh a previous reading.

`05` is identification only, never authentication, pairing, unlock or a
settings write. This provider sends **only `05` and `4A`**, with no other
commands or feature operations. The recorded M HUB/VGN HUB read paths contain
no evidence of `4A` state-changing side effects on non-MCHOSE/non-VGN devices;
that is a bounded static finding, not a hardware safety result. Synthetic
tests cover both provider orders, both FA09 collections, one open/one card,
known/unknown names, silent back-off, zero suppression and stale history.
Other-brand firmware, FF04/FF02 equivalence in practice, cold starts, delayed
or cached replies, charging and coexistence with either HUB remain unverified.

#### Complete exchanges and session requirements

`configManagerMap`, `HidDeviceHandler.readCommand`, `crcHandler16` and
`genCommandHandler` establish these complete **20-byte hidapi frames**, including
the report-ID slot (WebHID supplies ID `0x13` separately and sends 19 data bytes):

```text
identify: 13 05 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 19
battery:  13 4A 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5E
```

The last byte is the sum of the preceding bytes for these fixed requests.
There is no nonce, incrementing transaction ID, challenge response, unlock,
pairing, wake, reset, or session-opening write in this read path. The sequence
state is host-local packet reassembly (`expectedSequence=0`). Identification
returns data; it is not an authentication handshake. Its `processQueue` branch
also invalidates the browser's `sessions` WeakMap used for lighting snapshots;
`invalidateByLocalLight` only deletes that host entry and sends no device command.
The constructor/register/read path contains no configuration/firmware writes.
This is the source-level evidence for treating `05` as read-only; actual
firmware behavior and cold-start independence remain hardware-unverified.

`DeviceManager.handleInputReport` passes WebHID input **data without the ID**
to `handleCommandResponse`, which checks command, packet total and packet index,
then appends data `[4:-1]`. `getPassWordParserWireless` takes its first six bytes;
`getBatteryParser(false)` takes percentage and two four-bit flags. In a numbered
20-byte HID reply used here:

| Offset including ID | Meaning and implemented acceptance |
| --- | --- |
| 0 | Input report ID `13`, required by this descriptor-gated implementation |
| 1 | Echoed command `05` for identification, `4A` for battery |
| 2 / 3 | Total packets = 1 / current packet = 0; multipart packets refused |
| 4 | Packet metadata; ignored by upstream, no invented length validation |
| 5..10 | Six-byte identity in the `05` reply |
| 5 | Battery percentage 0..100 in the `4A` reply; 101..255 refused |
| 6 | LOW nibble `fullStatus`, HIGH nibble `chargeStatus` |
| 19 | Trailer discarded by upstream; response checksum not established |

For identified MCHOSE models, the nibble order follows the bundled `Parser.generateBit` little-endian branch,
not the textual field order. `resolveBatteryIcon` and the home battery status
use `chargeStatus === 1 && battery < 100`. Accept only 0/1 for each flag; other
nibble values have no established semantics and produce no reading. Like M HUB,
do not replace percentage with 100 based on `fullStatus`. Reject literal request
echoes, wrong commands/IDs, malformed lengths and out-of-order packets.

The source input handler itself does **not** inspect `event.reportId` or enforce
input length. Consequently this reader deliberately accepts only the descriptor
variant with **one input and one output report, ID 13, 20 bytes each**, each
proven as a raw byte array by `battery_caps` (including the single byte-selector
array case). This is a conservative local acceptance boundary, not a claim that
the script proves all FA09 input IDs/lengths. Unknown/multiple IDs, split/aliased
fields, unproven lengths, wrong collection or duplicate eligible collections
cause zero opens/sends. Topology uses preparsed descriptor data only; no feature
request or output query is used to discover capabilities.

Each exchange sends once and reads at most 32 packets/2200 ms (M HUB's
`retryTime:2` budget). A silent/asleep receiver or invalid battery reply creates
no new reading and no engine error; unknown FA09 identities use the generic
path above, while unknown `41E4:2001` identities remain blocked. There is no invented sleep byte or
offline 0%. The protocol has no nonce; cached receiver replies, delayed packets,
sleep freshness, charge transitions and concurrent M HUB ownership are untested.
All new reply fixtures are **synthetic**, including their trailer values.

#### Commands excluded from this reader

The complete `configManagerMap` used by this class has these state-changing
entries. The wireless opcode is at byte 1 of the report; wired opcodes belong
to feature payloads and must not be confused with wireless opcodes.

| Entry | Wireless opcode | Wired opcode | Effect |
| --- | --- | --- | --- |
| setKeySetting | `01` | `03` | Key mapping/configuration |
| setDiyLight | `02` | `06` | Per-key RGB tables |
| setMacro | `03` | `05` | Macro storage |
| setPerformance | `04` | `04` | Performance/configuration |
| setReset | `06` | `04` | Factory reset (confirmed by reset UI call site) |
| setLightColor | `09` | `10` | Lighting configuration |

No pairing or firmware command is present in that class's command map. This is
a bounded finding about the BY class, not a claim that the whole M HUB bundle
has no updater or pairing logic. Those other paths, wired feature operations,
streaming RGB commands, and every read except identity `05` and battery `4A`
are absent from this provider. Fake handles provide no feature API and tests
assert the entire allowed send trace, including failure and unknown-model paths.

`258A:010C / FF00` is selected by the same upstream predicate; `05AC:024F`
appears with FF00/FF04 in the filter table and K99/K87 naming logic. The evidence
does not supply their exact target usage/report descriptor variants or prove
which named devices take feature vs output transport. They are **recorded only,
not enabled**. FA07 and other keyboard/mouse identities are also excluded.

#### MCHOSE 41E4:2001 and other 41E4 identities

**Implemented, C / synthetic tests only, 2026-11-05.** The same source artifact
and SHA-256 above establish that `41E4:2001` uses **the identical BY wireless
identity/battery path** when its product name is exactly `MCHOSE 2.4G Wireless`
or `2.4G Wireless Receiver`. The task's #169 diagnostics identify the receiver
as `MCHOSE 2.4G Wireless`, with `FF04:0002` and `FF02:0002`; they do not supply
raw reports or prove their sizes. Only `FF04:0002` is eligible here. At runtime
the same preparsed-descriptor proof must establish one input and one output
byte-array report, each ID `0x13` and 20 bytes including the report-ID slot.
Unknown lengths, extra IDs, an ambiguous collection or a different product
name fail closed before any battery/identity handle is opened.

Evidence chain in `main-DCRwBIvm.js` (named anchors, not copied implementation):

| Anchor | Static observation and consequence |
| --- | --- |
| `HidIndexDeviceFilters`, `getDeviceMapInner` predicates `la`, `ca`, `pa` | `16868:8193` / page `65284` is selected by both `la` and `ca`; FA09 is `13652:64009` / page `65284` in `pa`. Both feed the same device map. |
| `getDeviceMapInner` constants `va`, `Ca`, functions `Va`, `Ba`, `keyboardPwds` | Exact generic names enter the same `getPassWord` query and exact six-byte match against all 18 model codes above. Only a match produces `realName` with the model suffix; the unresolved generic entry is removed. There is no additional PID/model cross-product restriction. |
| `registerHidDevice`, `instantiateDevice`, `defaultWirelessRID`, `diffRID` | Both generic names create `HidDeviceHandler` with `isWired=false`, report ID 19. Every model's wireless RID is 19; the 6/9 distinction is for wired feature reports only. |
| `configManagerMap`, `readCommand`, `crcHandler16`, `genCommandHandler` | Both IDs use the exact 20-byte `13 05 ... 19` and `13 4A ... 5E` requests above, additive request checksum included. WebHID sends 19 data bytes with report ID 19 separately. No VID/PID-dependent frame or checksum branch is present in this read path. |
| `DeviceManager.handleInputReport`, `handleCommandResponse`, `getPassWordParserWireless`, `getBatteryParser(false)`, `Parser.generateBit` | Same reply assembly, six-byte model code at HID offsets 5..10, percentage at 5, full flag in the low nibble and charge flag in the high nibble at 6. The trailer is discarded, not checksum-validated; input report ID/length are not checked upstream, hence our conservative descriptor gate. |
| `isValidPrefix`, home battery function `Nd`, `resolveBatteryIcon` | The identified generic receiver enters the BY `getBattery` call. Charging is charge flag 1 below 100%; the full flag does not override percentage. The PID-based `getDevName` defaults (`G87` / `G75_V2`) apply to named devices, not an unidentified generic receiver. |

REA parsed this supplied tree without executing it (Evidence
`ev_162220dc08fe53e87d23d4e5d7c36c3d45ce64d42db5ccb9952aa6c29a630c6c`;
summary `ev_45a313db6506e640effb350cfd228af8557a462b8de73fe34a989eaafef4b1f4`).
Its semantic graph reached the node ceiling; the table's specific branches and
shared method bodies were inspected directly. This proves static reuse for
the selected names/IDs, not complete bundle coverage or device execution.

The provider adds only the exact pair `41E4:2001`, retaining `3554:FA09`;
`3554:2001` and `41E4:FA09` are not accepted. It shares the existing codec,
M HUB model gate, bounded reads and zero-percent rejection. The later generic
unknown-model fallback applies only to FA09, not `41E4:2001`. Model/unknown/silent
back-off caches remain per physical receiver and path across both vendor
inventories, and are pruned only after both have been polled. No model name is
guessed from PID 2001. The **state-changing commands excluded** are the same
complete BY map listed above: wireless `01` key settings, `02` per-key RGB,
`03` macros, `04` performance, `06` reset and `09` lighting (wired `03`, `06`,
`05`, `04`, `04`, `10`, respectively). This reader can only send identity `05`
and battery `4A`; it never invokes any setter, feature operation, firmware or
pairing path. No authentication/unlock or wake command is added.

Other literal product IDs for VID 16868 in this bundle were checked separately:

| PID (decimal / hex) | Upstream route / limit | Decision |
| --- | --- | --- |
| 8705 / `2201` | `la`, page `FF00`; `getDevName` selects G87 for named devices. A non-generic name uses wired feature ID 6, a different transport/frame. | Record only; no exact usage/report descriptor evidence for a wireless variant. |
| 8196 / `2004`, 8707 / `2203` | `ua`, page `FF00`; named UT98 or G98_V2 in `getDevName`, wired feature ID 9 via `diffRID`. | Record only; same class family does not prove the 20-byte wireless descriptor/transport. |
| 4353 / `1101`, 4354 / `1102` | `ryMouseFilters`, `getMouseMapInner` and the mouse branch of `Nd`; pages `FF01` / `FF0B`. | Mouse paths, not BY receiver evidence; never queried by this reader. |
| 4097 / `1001`, 4099 / `1003`, 4612 / `1204` | `getMouseMapInner` and the mouse branch of `Nd`, page `FF00`. | Record only; no BY receiver claim. |
| Other `21xx` literals in `HidIndexDeviceFilters` / `getDeviceMapInner` | Standard page `0001:0000` or `FF00:0001` entries, without the `la`/`ca`/`ua` BY identity evidence above. | Not enabled; protocol, generic-name eligibility and exact descriptor variant are not established here. |

Validation uses synthetic descriptor/reply fixtures, including the complete
FA09 behavioral suite applied to `41E4:2001`, G87/G75_V2 identity results, exact
request bytes, forbidden-send traces, independent two-vendor caches, scoped
reader-child transport and stale-reading preservation. **No hardware was
opened or queried.** Actual #169 report descriptors, firmware behavior, reply
checksum, cold-start independence, sleeping/cached replies, charge transitions
and coexistence with M HUB remain unverified. These are the same acceptance
limits as FA09, not evidence of a different wire protocol. Dynamic remote M HUB
configuration and other firmware versions are outside this static snapshot.

### VGN beiying keyboard receivers

**C / 258A:024F 用户实机验证，#190；本读取器其余四条仅官方静态协议证据，待本项目实机验证。FA09 已统一到 MCHOSE BY 读取器。**
Thanks to **shijiu599** for issue **#190**, the V98 Pro V3 receiver readout,
the 99% plugged/unplugged comparison, and the fixed-usage descriptor proof
proposal. The supplied issue is `.ai/evidence/issue-190.md`; its saved Chinese
text has encoding damage, but the code, IDs and numeric frame evidence are
legible. This implementation shares BY framing with MCHOSE rather than copying
the proposed provider or vendor code.

Source: the supplied [VGN HUB bundle](https://drive.vgnlab.com.cn/assets/index-ZngoA_qG.js),
`.ai/evidence/vgnhub/index-ZngoA_qG.js`, 2,960,070 bytes, SHA-256
`53adfab22f478249bdb9117bb26cedf8a7d3c6c561f2f2eb735e7d86e943be63`.
REA parsed this file without execution (Evidence
`ev_8b7ab4ceabb53af4ff5db48e9d6a240b92c7f4b397bd4055665c804a651413ae`;
summary `ev_2125bf75a686ed414ee5d9794386480c969df554682a41136f95715cec7914b7`).
Its semantic graph reached its node ceiling; the following findings were
checked directly against source anchors, not inferred from complete graph coverage.
Offsets below are zero-based UTF-16 character offsets in this exact bundle.

#### Complete beiying device table and routing

All 14 `customDeviceType:"beiying"` rows use `deviceKeyboard`. IDs, collections
and report IDs below are hexadecimal. The table starts at offset 1715479;
the last row starts at 1721391. Five `receiver:true` rows use this reader;
shared FA09 uses the single-owner flow documented above.

| VID:PID | Official product | Collection | Report | Decision |
| --- | --- | --- | --- | --- |
| `258A:024E` | VGN V98pro V3 | `FF00:0001` | `09` | Wired; excluded |
| `258A:024F` | VGN V98pro V3 | `FF02:0002` | `13` | Enabled; user hardware evidence #190 |
| `258A:027E` | VGN V98pro V3 | `FF00:0001` | `09` | Wired; excluded |
| `258A:027F` | VGN V98pro V3 | `FF02:0002` | `13` | Enabled; static evidence only |
| `258A:010C` | VXE V87pro | `FF00:0001` | `06` | Wired; excluded |
| `3554:FA09` | VXE V87pro (upstream label only) | `FF02:0002` | `13` | Excluded here; owned solely by `mchose_by_keyboard` on FF04, neutral name for unknown models |
| `258A:026A` | VGN V87 V2 | `FF00:0001` | `09` | Wired; excluded |
| `258A:027B` | VGN V87 V2 | `FF02:0002` | `13` | Enabled; static evidence only |
| `391D:2003` | VGN N75 V2 | `FF00:0001` | `09` | Wired; excluded |
| `391D:2A01` | VGN N75 V2 | `FF02:0002` | `13` | Enabled; static evidence only |
| `391D:2004` | VGN N75 V2 | `FF00:0001` | `09` | Wired; excluded |
| `391D:2005` | VGN V108 | `FF00:0001` | `09` | Wired; excluded |
| `391D:2006` | VGN V108 | `FF00:0001` | `09` | Wired; excluded |
| `391D:2A02` | VGN V108 | `FF02:0002` | `13` | Enabled; static evidence only |

The N75 receiver row also contains `path:"mi_01&col01"`. The inspected WebHID
read path uses `getDevice`'s exact VID/PID match and collection page/usage,
not that path string or a product-name/model whitelist. `isBeiyingDongle`
(2873695) explicitly lists all six pairs. The branch at 2878686 selects `ns`
for N75, `ds` for V87pro, otherwise `As`, installs `readBuffer`, then calls
`getDeviceInfo` -> `getBatteryInternal`. Constructors only initialize host
state; this battery path does not call a model query, `getDeviceAllData`,
configuration setters, unlock, pairing or wake commands.

`As` (2064857), `ns` (2120381) and `ds` (2168848) all use report 19 and the
same battery command. `getCrc` seeds the report ID and adds the WebHID data
bytes; `sendCmd` pads to 19 data bytes and calls output `sendReport`, not a
feature operation. Complete hidapi request, including the report-ID slot:

```text
13 4A 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 5E
```

`readBuffer` accepts command `4A` and reads WebHID data `[4]` (full-frame
`[5]`) as percentage. It reads/discards data `[5]`; it does not establish
charging semantics or verify the response checksum. Issue #190 supplies the
additional evidence: full-frame `[6]` bit `0x10` changed with external power
at the same 99%, with trailers `D3` plugged and `C3` unplugged. The sum of the
preceding 19 bytes modulo 256 matches both. A request without the checksum
was reported silently discarded. This is user-reported hardware evidence,
not a capture made by this task.

`by_keyboard.py` now holds the existing fixed BY read frames and single-packet
payload parser. The unified FA09 reader retains its FF04 collection, generic-name
gate, identity caching/back-off and zero-percent refusal; recognized MCHOSE
models retain their nibble semantics. Unknown FA09 models use the neutral
percentage-only path above. `providers/vgn_beiying.py` reads its five pairs directly,
requires the reply checksum, rejects echo/bad header/length/out-of-range
percentage, and accepts 0..100 without inventing MCHOSE's zero-asleep policy.
Only `258A:024F` reports charging as known; other IDs leave it unknown. At
100%, bit 4 is not forced off: full-on-external-power semantics remain untested.
`3554:FA09 / FF02:0002` is no longer queried by this provider; only the unified
FF04 path may produce its card. Neither the upstream label nor the PID alone
proves a brand/model or hardware compatibility with other brands.

#### Descriptor gate and excluded state changes

Every enabled pair requires a unique FF02:0002 collection under a resolved
physical parent and **one input and one output report, ID 13, exactly 20 bytes
including ID**, proven at runtime. Unknown or mismatched lengths, extra report
IDs and ambiguous endpoints cause no reader open or send. Existing byte-value
and 0..255-selector proofs remain supported. The additional
`battery_hid.fixed_usage_report_size` proof requires one non-aliased button cap,
Data/Array flags, non-range repeated fixed usage, no value caps, and
`report_count == maximum - 1`. Topology opts into this fallback only for the
five remaining exact pairs and FF02:0002, never for FA09, wired devices or
MCHOSE's FF04 path.
It uses cached preparsed descriptors only; no input/output/feature transfers.
Failed proof is not cached as success and can be retried at later discovery.

The state-changing commands found in the beiying controller classes are:

| Transport / command | Effect and source anchors | Reader policy |
| --- | --- | --- |
| Receiver `01` | Key/layer remapping, disable/clear keys; `setKey`, `clearAllKey`, `sendKeyMatrixPackets` | Never sent |
| Receiver `02` | Per-key custom RGB, including clearing custom lighting; `sendCustomLightingPackets` | Never sent |
| Receiver `03` | Macro storage; `setMacro` / `setMacroInternal` | Never sent |
| Receiver `04` | Profile/performance, OS mode, Win/all-key locks, debounce/tap delay, direction keys, dormancy and lighting settings; `sendProfilePackets` | Never sent |
| Receiver `09` | Lighting color tables; `sendKeyboardLightPackets` | Never sent |
| Receiver factory reset | `factoryDataReset` / `factoryDataResetInternal` compose profile, custom-color, color-table and key writes above; no standalone reset needed | Never invoked |
| Wired feature `03`, `04`, `05`, `06`, `0A` | Key matrix, profile, macros, custom RGB, color table respectively; `_o`, `is`, `rs` setters / `sendCmdWithBuffer` / `sendCmdProfile` | All wired endpoints and feature APIs excluded |
| Wired N75 feature `08` | `is.sendRhythm` writes a 378-byte rhythm-light buffer | Never sent |

The provider exposes only the fixed `4A` battery request, once per poll per
eligible receiver, with no write retry and bounded input reads. It does not
send even the read-only `05` model query or other configuration reads; no
firmware, pairing or reset entry point is implemented. Synthetic write traces
assert the entire packet, so any command other than this battery request fails.

Tests cover both #190 charging frames, checksum corruption, percentage limits,
echo rejection, all five remaining pairs, FA09 exclusion, crossed/wired IDs, descriptor positive/negative
cases, isolation/JSON preservation, history on silence and existing MCHOSE BY
behavior. **This task did not access hardware or the network.** Remaining
unknowns: sibling descriptors/reply checksums and firmware behavior in practice,
full-charge external-power meaning, zero-percent/sleep/cached replies, repeated
polling, cold starts and coexistence with VGN HUB. No current hardware acceptance
or new verification level is inferred from synthetic tests.

### Pulsar Link 8K receiver 3710:5501

**2026-11-05: integrated, C / 待实机验证.** The follow-up task supplies #134's
program diagnostic: `3710:5501 "Pulsar Link 8K Dongle" 0001:0006 0001:0080
000C:0001 FF02:0002 FF03:0000 FF04:0002 FF05:0000`. This establishes the target's
`FF02:0002` collection; it does not establish report lengths. The web snapshot
selects by report shape/ID, not usage page, and specifies no conflicting usage.
Only this exact VID:PID and a unique `FF02:0002` collection are enabled.
`topology.py` now requests per-report proof through `battery_hid.battery_caps`:
both input and output must contain only report ID `08`, each exactly 17 bytes
including the report-ID slot. Unknown, wrong or ambiguous descriptors never
open an I/O handle and never fall back to a sibling collection. Descriptor
discovery itself uses access-zero metadata handles and sends no device commands.
This replaces the earlier D / observed-only conclusion; actual hardware replies
are still unverified. No identification handshake or DPI support is enabled.

#### Local evidence and reproducible anchors

Only the supplied `.ai/evidence/pulsarhub/` snapshot was inspected; no website,
device or updater was run. The brief attributes it to
`https://bbb.pulsar.gg/cMouse/`; `cfg.json` reports version `1.3.13`.
Protocol facts below are independently described, not copied implementation code.

| Artifact | SHA-256 |
| --- | --- |
| `app.2262e49f.js` | `45ee1dca4bd3196dbf5cfad73b984c5e592d03ad15a501cfad76b751e2b5cec5` |
| `cfg.json` | `85e02f7f77e71236b6acfe30c6a04ed91593e4aeeb8b947796711214a6d94d52` |
| `devicename.json` | `b0470ebf70f0ffbb2eed597f92cc381fc5eb5c2d7924560c2969d0fc6debe8e9` |
| `sensor.json` | `72c4b019ad255c2ca0f5b66599e43df1798b9299d380dc108eff81245185cdb6` |
| `chunk-vendors.adfd4048.js` | `770bd14f84ba13a767099b5bb91e394afa784c6fa87c16d3298c1b3390b598d8` |

Anchors below are function/symbol names and zero-based UTF-16 character offsets
in the hashed application file (not byte offsets). REA static inventory Evidence
`ev_3d249fb29eb54de8c5ad8a623089f929fe02f6d37350c4d1c5b78b77e304fe40`
parsed both JS files without parse errors; its semantic graph is partial. The
following conclusions use direct inspection of the named functions and JSON,
not a claim of complete automatic data-flow recovery or observed execution.

| Question | Observed static evidence / boundary |
| --- | --- |
| USB selection | `cfg.json` `/vid` and `/pid/mouse/wireless`; filter construction near `94319` forms the Cartesian product of the VID list and all configured PIDs. This includes `3710:5501`, but does not prove that every generated pair is a real product. |
| Collection | `Ut` at `40090`, repeated in `ds` at `48225`: one input report, one output report, output report ID equal to `mt=8` (`37676`). Neither predicate checks usage page, usage, report size/count or input report ID. `os` separately accepts incoming report ID 8. |
| Descriptor boundary | The snapshot contains no target usage/descriptor fields. The follow-up #134 diagnostic supplies the collection list above. Actual report IDs/lengths must be proven at runtime; the web transmit buffer alone does not establish them. |
| Request | `ut.BatteryLevel=4` (`36122`), `hs` (`48118`), checksum `cs` (`47852`), sender `ls` (`47519`): 16 payload bytes sent with report ID 8. Including that ID, the full request is `08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49`; the sum modulo 256 is `55`. This matches `make_request()` byte for byte. Buffer length is not descriptor evidence. |
| Reply | `os` (`43145`) reads the 16-byte payload excluding report ID: command at 0, success status at 1, percentage at 5, charging iff byte 6 equals 1, voltage at 7–8 big-endian. Including report ID, these are raw bytes 1, 2, 6, 7, 8–9. The snapshot does not establish that every actual reply has exactly this payload length. |
| Model identity | `gs` (`48918`) sends command `01` with an eight-byte payload (four random bytes, then four zeros). `os` takes CID, MID, transport type and dongle type from payload 9–12 (raw 10–13). `deviceConnect` near `95000` matches CID/MID against `cfg.json` mouse entries and selects `sensor.json`; the name handler near `117062` uses matching CID and `name[MID-1]` in `devicename.json`. PID `5501` alone does not identify the paired mouse. No live CID/MID is available. |

The raw battery field layout agrees with the existing reader for normal 0/1
charging flags, but the complete behavior is **not identical**. The web handler
updates battery only for status zero and treats charging as exactly 1; the
existing parser does not check status and treats any nonzero charging byte as
true. Conversely, the existing parser validates checksum and percentage range;
the web battery handler has no equivalent checks. The web UI also smooths and
persists the displayed percentage, whereas the provider returns the raw level.
The 5501-specific `parse_link8k_power` accepts only exactly 17 bytes, status zero,
report `08`, command `04`, a valid checksum and percentage 0..100. It takes the
raw percentage from byte 6 and sets charging only when byte 7 equals 1 (2/255
are false, not a bitmask). Voltage remains big-endian bytes 8..9. Checksum and
range validation are retained defensive guards, not claims about checks made
by the web handler. Existing models keep `parse_power` unchanged, including its
nonzero charging interpretation and lack of a status check. Rejected 5501 replies
are skipped within the existing bounded receive loop, with no extra command.

`Cs` (`49818`) wraps the battery request in `ys(1)` / `ys(0)` device-online
commands (command `03`, payload byte 5 toggled), and the driver has a separate
PC-driver-status command `02`. It is unknown whether this receiver answers a
standalone `04` query without that session setup. The web retry timing, UI
smoothing and initialization are not permission to reproduce its state changes.
The existing provider's bounded timeouts and sleeping/history behavior are
unchanged; none has been verified on this receiver.

#### State-changing paths — never send for this investigation

The `ut` command map at `36122` declares the following operations. The distinction
between a declaration and a directly traced sender matters: declarations alone
do not prove execution on `3710:5501`.

| Operation | Commands / anchors |
| --- | --- |
| Pair / factory reset | `05 DongleEnterPair` (`ws`) and `09 ClearSetting` (`xs`), both directly constructed and sent. `06 GetPairState` only polls the result; do not start pairing to identify the mouse. |
| Write configuration / active profile | `07 WriteFlashData`, directly sent by `Qs` (`54288`) and `ei` (`54693`) for settings, DPI, keys and macros; `0F SetCurrentConfig` in `$s`. |
| Driver/session state | `02 PCDriverStatus` in `fs`; `03 DeviceOnLine` with the control byte changed in `ys`; command `01` is the random identity handshake, whose side effects are not established. None is required or sent by this investigation. |
| Other settings | Declared `14 Set4KDongleRGB`, `16 SetLongRangeMode`, `18 SetPulsarDongleLightParam`, `23 SetPulsarDongleKeyFunction`, `25 SetPulsarDongleDPILightParam`, `27 SetPulsarDongleOButtonCurrentMode`, `29 SetPulsarDongleOButtonFunction`; these have setter paths using `us`. Also declared: `0B SetDeviceVidPid`, `0C SetDeviceDescriptorString`, `0D EnterUsbUpdateMode`, `11 EnterMTKMode`, `B0 MusicColorful`, `B1 MusicSingleColor`, `F0 WriteKBCIdMID`; do not treat the declaration as a verified target-specific frame. |
| Firmware / bootloader | Separate updater map `oe` (`25977`) defines `B0 Cmd_PrepareDownLoad` and `B1 Cmd_DownLoadFile` (distinct from the music-command namespace), and packet markers `C0`/`C1`. `Ge` (`31473`) loads reset/prepare/data commands, report IDs and endpoints from a firmware file; `qe` (`33686`) sends reset-to-update, and `ze`/`it` send preparation and firmware data with output or feature reports. Firmware files are absent, so the exact target boot/reset frames cannot be established. Never fetch firmware or invoke this updater. |

No web application code was executed and no hardware commands were sent during
implementation. Synthetic tests check the exact battery request, target-specific
status/charging fields, invalid frames, report-ID/length and collection rejection,
and that only command `04` is sent even after silence or invalid replies. No
identity, online/session setup, pairing, configuration, reset or firmware commands
are sent by the 5501 path. Sleeping/history behavior is exercised synthetically;
it is not a captured hardware exchange.

#### Other configured receiver candidates — not newly enabled

The wireless PID list is `5406, 5502, 5501, 5504, 5407, F517, 5601, 5603`.
Existing `3710:5406` and `3554:F517` keep their separate prior evidence and
behavior. The remaining Cartesian-product candidates are recorded only:

| VID | PIDs not currently claimed by this provider |
| --- | --- |
| `3710` | `5502`, `5504`, `5407`, `F517`, `5601`, `5603` |
| `3554` | `5406`, `5502`, `5501`, `5504`, `5407`, `5601`, `5603` |

All lack the same exact-collection/descriptor proof in this snapshot; no
additional real products or mouse model names are inferred. The configured
keyboard and wired PID lists are not expanded by this receiver task.

Hardware acceptance still needs the actual runtime descriptor proof and a
standalone read-only `04` request/reply trace with status/checksum. Whether a
particular firmware answers without the web driver's session setup, charging
transitions, sleep/wake, cached readings and paired-model identity remain
unverified. The displayed name identifies only the receiver. `server.py` already
routes VID `3710` to Pulsar and counts its explicit `PIDS`; `known_ids.py` derives
its static IDs from that same table. Adding `5501` there registers it through
both existing paths without duplicate registrations or changes to either file.
The compatibility catalog now includes this receiver under the C-level Pulsar
reader and removes its D-level observed-only entry.

### Dareu EM901 260D:1074

**2026-11-06: integrated, C / 待实机验证; 260D:1090 remains disabled.** The
third-round brief supplies #81's `FF02:0002` control collection and authorizes
the official driver's **17-byte Feature report `08`, command `04`, followed
by input report `09`** as a separate CompX transport. `providers/dareu.py`
reuses only Pulsar's fixed power-frame/checksum constructor, not its output
transport, input-08 parser, identification or DPI paths. The displayed name
is `Dareu EM901`, kind `mouse`. This is not the BY `13 / 4A / 20-byte` path.

Only exact `260D:1074` and a unique `FF02:0002` collection per physical group
are eligible. Before opening a query handle, runtime descriptor evidence must
prove Feature `08` is 17 bytes (also the collection's feature maximum) and
Input `09` exists with a known length of at least 8 bytes. Input lengths are
bounded at 4096 bytes; the collection maximum must agree with the per-ID map.
`topology.py` enables `battery_hid.byte_report_sizes` only for this exact
identity/collection, for feature and input directions. Its descriptor handles
have zero access and send nothing. Missing/unsupported descriptors, wrong IDs,
wrong lengths or ambiguous collections never open a battery query handle.
The feature and input proofs must belong to the same collection; no sibling
fallback or active collection-association probe is used. `260D:1090` appears
in the configuration below but lacks the supplied #81 collection evidence,
so it is not registered or enabled for descriptor proof.

#### Source identity and analysis boundary

The supplied snapshot came from the official
[EM901X AIM-WL Driver page](https://www.dareu.com/pages/mice-driver), obtained
2026-11-06 and extracted with innoextract by the supervisor. This investigation
did not fetch the page or run the installer. Evidence is under
`.ai/evidence/dareu-em901x/unpacked/app/`:

| File | SHA-256 |
| --- | --- |
| `OemDrv.exe` (2,172,928 bytes, PE32 x86) | `afac433fbd34b67994886a079a917c832c7f5e12cd22189e5fd84b303f56052e` |
| `InitSetup.dll` (56,320 bytes, PE32 x86) | `72f5cfe575253eaff31e27ce8f70b4caaa079d2c42a4130515eecf7f0967115d` |
| `Cfg.ini` (GBK) | `34e23f5067f77620c58803ecc243bf7a8fa1a52fdf9d49d63a371f269024c02c` |

Analysis used local Ghidra 12.1.4 disassembly/decompilation and independent PE
file-byte checks. All addresses below are static preferred VAs, not observed
runtime addresses; offsets are zero-based original file offsets. For the cited
EXE `.text` anchors, `file = VA - 0x400C00`; for the cited `.rdata` anchors,
`file = VA - 0x401400`. Optimized calling conventions confuse some decompiled
signatures; the byte positions and arguments below were checked against x86
instructions. No evidence EXE/DLL was executed or loaded as executable code;
no HID enumeration, device commands, network access, or firmware operation was
performed. The local analysis project and text extracts remain in ignored
`tmp/`; this section contains the durable findings.

`Cfg.ini` contains `Title=EM901 Dual Mode Wireless Mouse` at file `0x2F`,
`VID=0x260d` at `0x207`, `PID=0x1090` at `0x213`, `PID2=0x1074` at `0x21F`,
and `ptPower=949,110` at `0x26C`. Its `[DEV_1]` also specifies `CID=0x10`,
`MID=0x04`. The configuration loader at VA `0x443D30` / file `0x43130` reads
these VID/PID/PID2 keys into the globals used by discovery. This proves both
IDs are configured for this driver, but does not prove the #81 receiver's
descriptor, firmware, or connection mode matches this snapshot.

`InitSetup.dll` exports only `SetupInit`, VA `0x10001080` / file `0x480`.
It checks `PathFileExistsW` and calls recursive filesystem cleanup at
`0x100010F0` / `0x4F0`: `FindFirstFileW`, `SetFileAttributesW`, `DeleteFileW`,
`FindNextFileW`, `FindClose`, and nested `RemoveDirectoryW`. Its imports are
SHLWAPI/KERNEL32, with no HID/SetupAPI imports. The strings at file `0xA9A4`
(`InitSetup: pszAppdata = NULL.`), `0xAA64` (`Remove Folder OK.`) and `0xAAB0`
(`DeleteFile %s, err=%d`) corroborate this installation role. It supplies no
evidence of a battery handshake. The English/Chinese XML has `tc_msg10` for
the mouse being offline and `tc_msg8` for firmware update; labels alone are
not protocol evidence.

#### Transport, battery frame and reply

| Question | Static evidence / conclusion |
| --- | --- |
| HID transport | `HidD_SetFeature` IAT VA `0x5771F0` / file `0x175DF0`, thunk `0x56365A`. The battery sender reaches it at `0x449D4A` / `0x4914A`. This path does not use `WriteFile` or `HidD_GetFeature` for the request. |
| Discovery and collections | Enumeration at `0x410900` / `0xFD00` matches the configured VID with either PID, calls `HidD_GetAttributes`, `HidD_GetPreparsedData`, `HidP_GetCaps`, and records usage/page and report lengths. `FindDev` at `0x40C430` / `0xB830` selects input handles with `InputReportByteLength == 17` (`0x40C533` / `0xB933`) and command handles with `FeatureReportByteLength == 17` (`0x40C5D0` / `0xB9D0`). These selectors do **not** test usagePage/usage. Input and command handles need not be the same collection. No `FF04:0002` or `FF02:0002` mapping is established. |
| Battery constructor | `GetPower` at `0x449BD0` / `0x48FD0`, reached from `0x433F50` / `0x33350`, zeroes a 15-byte command payload and sets its first byte to `04` at `0x449C5B` / `0x4905B`. There is no extra device command within this getter. |
| Framing/checksum | `SendCommand` at `0x449CE0` / `0x490E0` prepends report ID `08` and copies the 15-byte payload. For the zero-initialized battery buffer it sets raw byte 16 so the complete 17-byte sum is `0x55` modulo 256. The helper also subtracts the previous value of byte 16; the battery caller explicitly initializes that slot to zero. It calls `HidD_SetFeature(..., 17)` with up to three attempts, 50 ms apart on failure. |
| Input handling | `ServiceThread` at `0x448320` / `0x47720` initializes the input report-ID slot to `09` and calls `ReadFile(..., 17)` at `0x4483DD` / `0x477DD`. It passes the buffer **after the report ID** to `0x448090` / `0x47490`, which queues non-`0A` command replies. `WaitNotify` at `0x448220` / `0x47620` selects command `04`; it sleeps/polls a queue with roughly a one-second empty-queue budget. |
| Battery fields | `0x449C8D` / `0x4908D` requires status zero; `0x449C93` and `0x449C98` load percentage and the raw charging byte. Including the report ID these are **raw bytes 2, 6, 7**, respectively; raw byte 1 is command `04`. The UI at `0x433F8F` / `0x3338F` accepts percentage `0..100` and treats any nonzero raw charging byte as charging (`0x433F9C`). It does not apply the BY nibble/bit interpretation. |
| Reply limits | The inspected getter/queue path does not validate a reply checksum, actual returned report ID, or exact received byte count. `09` is the driver's expected input ID, not a captured descriptor/report. No reply checksum rule, charging value enumeration, voltage field, sleep sentinel, or freshness guarantee is established here. |

The statically constructed battery request, including report ID and checksum,
is exactly:

```text
08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49
```

This differs from BY in transport, IDs, length, command, field offsets and
checksum rule. Reusing `by_keyboard.make_request()` or its payload parser would
send/interpret the wrong protocol even though #81 lists similar vendor pages.

#### Initialization and commands excluded from any battery implementation

Discovery sends `08 03` followed by **15 zero bytes**, via `HidD_SetFeature`
at `0x40C66E` / `0xBA6E`. It does not use the regular checksum helper for this
probe (the final byte is zero, not `4A`). Temporary read threads at `0x40C3C0`
/ `0xB7C0`, using `GetData` at `0x448A40` / `0x47E40`, associate a responding
input handle with the command handle. This is not proof of physical-parent
uniqueness or a safe exact-collection selector for this project.

`Psd_Thread` at `0x40BF70` / `0xB370` then calls `GetConnectStatus` at
`0x4486E0` / `0x47AE0` (regular checksummed command `03`). It normally follows
with `CheckPsd` at `0x448820` / `0x47C20`: command `01`, length marker `08`,
four random bytes, and CID/MID processing. The configured model is `10:04`.
There is a global bypass branch, but no instruction to enable it is inferred.
The code does not establish that the random exchange has no device side effects,
or that an uninitialized device answers standalone `04`. The UI's first getter
occurs after this discovery sequence; later timer calls reuse its handles.
No additional explicit wake command was identified in the inspected battery
chain. That bounded finding does not prove one is unnecessary on hardware.

| Operation | Confirmed command / anchor / boundary |
| --- | --- |
| Driver/session status | Command `02` with a one-byte value; `TellDrvStatus` at `0x448DC0` / `0x481C0` sets the generic sender's command to `02` at `0x448DDB`. Show/minimize/exit callers send 1/0. This changes driver/session status and is excluded. |
| Write DPI, keys/macros, lighting and other settings | `WriteEEPROM` at `0x449100` / `0x48500` sends command `07`, address and payload. The apply worker `0x40FB60` / `0xEF60` has explicit `Set Key`, `Set LED`, `Set DPI`, `Set DPI Color` paths into it. DPI uses `0x4496F0` / `0x48AF0` (addresses starting at `0C`), key mapping uses `0x449930` / `0x48D30` (starting at `60`), and lighting writes include addresses `A0`, `4C`, `54`, `58`. These are settings writes, never battery initialization. |
| Restore / apply completion | The restore-confirmation text `tc_msg5` is loaded into `0x603270` at `0x4314AA`; the restore UI at `0x436A20` / `0x35E20` uses it, then queues an all-settings apply (`EAX=FFFF` at `0x436D00`, `0x409220` / `0x8620`). The apply worker's flag-`0100` branch sets command `09` at `0x410765` / `0xFB65` and calls the generic sender at `0x41076C` / `0xFB6C`, after the enabled `07` writes. Exclude this entire restore/apply path. A standalone hardware factory-reset meaning for `09` is not established. |
| Active profile | `SetCurProfile` caller `0x412B00` / `0x11F00` reaches `0x449020` / `0x48420`, which sends command `10` with the selected profile byte. Excluded. |
| Identification / status / EEPROM read | Commands `01` (random `CheckPsd`), `03` (connection status), `08` (EEPROM read at `0x449460` / `0x48860`), `0E` (current-profile read at `0x448E80` / `0x48280`) and `0F` (getter at `0x449AD0` / `0x48ED0`) are distinct from battery `04`. Their presence does not authorize copying the initialization sequence. |
| Pairing and firmware | No exact pairing, bootloader or firmware-write frame was recovered in the inspected HID senders/callers. `tc_msg8=Firmware Update` and the `ShowFWUpdate` configuration key are UI evidence only; the latter defaults to zero and is absent from this Cfg.ini. Do not borrow presumed `05`/`0D`/`B0`/`B1` meanings from other drivers, run the updater, or infer these operations are absent from every version. |

#### Battery-only implementation, validation and remaining boundaries

Each eligible poll sends exactly one `send_feature_report` (`HidD_SetFeature`)
with the 17 bytes above, then reads input reports for at most one second and
32 reads. It never uses output `write` or `get_feature_report`; a failed/short
send is not retried. The receive buffer uses the descriptor's input maximum,
and an accepted reply must have Input `09`'s exact proven length, report ID
`09`, command `04`, status byte 2 equal to zero and byte 6 in `0..100`.
Byte 7 is charging whenever nonzero. Command correlation follows the driver's
`WaitNotify`; report ID and length checks are additional local safeguards.
The inspected getter/queue path does **not** validate a reply checksum, so
this reader does not impose Pulsar's unproven reply-checksum rule. No voltage
or other fields are inferred. Silence/sleep or invalid replies produce no new
reading and no error; there is no setup, retry, wake, pairing or firmware
fallback. In particular, commands `02`, `07`, `09` and `10` are never sent.

The server, offline ID index and C-level compatibility catalog register only
`260D:1074`; `device_pack.py` also registers the reader in its brand-disable
allow-list, preserving the existing provider/disable registry invariant.
The marked generated documentation reflects that same catalog.
Synthetic provider tests cover the exact request bytes, feature-only send,
input-09 parser, status/range/ID/length rejection, nonzero charging, deliberately
unchecked checksum bytes, descriptor/collection rejection before opening,
bounded silence, handle cleanup and absence of all non-battery commands.
Fake Windows descriptor tests cover feature/input proof, caching and recovery
from incomplete proof without device I/O. The required offline suites also
retain the existing Pulsar tests; synthetic success is not hardware acceptance.

The prior static driver findings above are reused; no evidence EXE/DLL is run
and no hardware or network is accessed in this implementation. Whether the
real receiver exposes both reports on the required collection, answers a
standalone `04` without the vendor's initialization, reports fresh percentages,
and behaves correctly through charging and sleep/wake remains unverified.
Actual report captures and authorized hardware acceptance are still needed;
the implementation fails closed if runtime descriptor proof is unavailable.

### MOONDROP GM01 Pro (35D8:0133)

**Evidence and identity.** Static inspection of the supplied official MOONDROP
Hub snapshot `.ai/evidence/moondrophub/index-BJe6cqeQ.js`, SHA256
`5385b8f8a65643aa5cb65f54e865aa87cef91474db1669743a86904406698f13`.
`oj=13784`, `ij/pR`, `iy=Qp` map VID `35D8`, PID `307` (`0133`) to `Qp.config`:
`GM01 Pro`, `HEADPHONE`, report ID `75` (`4B`), feature `zP()`. This is a
headset, not a CompX mouse. Provider `moondrop` exposes only this exact pair,
name `MOONDROP GM01 Pro`, kind `headset`, level **C**. No vendor code is copied
or executed; the local implementation uses protocol facts from static text.
REA analyzed 15 JavaScript files without parse failures; its semantic graph
was partial at its node ceiling, so the following anchors were also inspected
directly. Analysis evidence:
`ev_26296df6304414d418feebf35f8902c5e7976c091a19f2a0832228a5378bb7f9`.

**Request and reply.** `et.OP_READ=128`, `zP.command=39`, `UG` and
`em.buildCommand/queryCommand` establish WebHID output report `4B` with exactly
three payload bytes `80 27 00` (OP, CMD, zero request payload length).
`Xj` calls `sendReport`, then accepts an input event with the same report ID;
this is not a feature-report protocol. The hidapi frame is
`4B 80 27 00` plus zeros to the runtime-proven output length. No checksum is
present in this request builder. No fixed 17/64/65-byte report size is inferred.
`Sc.PAYLOAD=Lt=3`, `UG` calls the decoder with `Lt`, and `zP.decode` reads
`Yt(reply, Lt+4) !== 0 ? Yt(reply, Lt+5) : 0`. Thus WebHID indices 7 and 8
are the validity gate and raw percentage; hidapi indices are **8 and 9**.
`Yt` reads an unsigned byte (0..255); this reader accepts only **0..100** and
does not clamp or invent a scale. Gate zero or a truncated response yields
no reading, rather than the Hub helper's fallback zero. A valid gate and a
genuine zero percentage remain representable. The ancillary UI snapshot
`chunks/index-B-KWtA3L.js`, `A9/DeviceBattery`, displays this value as percent
and does not pass charging state. No charging field was found in this path;
`charging_known=False` keeps the public value unknown.

The reader additionally requires exact runtime-proven input length, report
ID `4B`, CMD `27` and a declared payload length of at least 6, no larger than
the available report. CMD/LEN use the shared three-byte framing (`Sc`, `Lt`,
`tb.readFirmwareVersion`, `iR`); **battery CMD echo and LEN values are a local
conservative correlation guard, not an observed GM01 reply capture**. The
Hub transport itself only correlates report ID, excluding OTA `iR` frames.
No response OP, checksum, charging, voltage or percentage resolution is
assumed. Command `4F` OTA/control replies, other commands and request echoes
cannot become readings under the local guard. The actual response header
must still be checked on hardware; a different header fails closed.

**Preflight and descriptor boundary.** `jp/F8/N8` wrap async device methods;
`Qp.beforeHook/pingDevice` query `OP_READ/DONGLE_STATUS` (`80 31 00`) and delay
50 ms before checking the returned flag. This is a read-only reachability
guard, with no flash/EQ/initialization write. `UG`'s battery getter directly
calls `queryCommand`; its settle object only waits. The implementation sends
only the battery query and omits even that status read. Static analysis does
not prove a cold receiver answers the standalone battery query; silence is
accepted as no measurement, with no fallback initialization or retry.

The task's #126 diagnostic identifies `35D8:0133 "MOONDROP GM01 Pro"` and
includes `FF02:0002`, but does **not** establish its report lengths or prove
that this collection owns `4B`. The Hub's `tf/vg` selects by output report ID
and input capability, not a hard-coded usage. Locally, only a **unique
FF02:0002** collection per resolved physical receiver is a candidate. Passive
Windows preparsed-data inspection must prove input and output `4B` byte-array
sizes before any query handle opens. Input must hold at least 10 bytes and
output at least 4, both at most 4096 including ID; `4B` must equal the
collection maximum in each direction. Unknown, inconsistent, non-byte,
ambiguous, or shorter-than-maximum `4B` descriptors are rejected. Other
collections are never tried. This is a runtime condition, not a claim that
#126 or the web snapshot supplies a full descriptor. Unsupported descriptor
shapes remain disabled rather than guessed.

**Commands never sent.** `et.OP_WRITE=01`: EQ flash save (`01`), offset flash
save (`04`), flash clear (`05`, target ALL=`04`), active EQ (`0F`), PEQ update
(`09`), EQ coefficients (`0A`), DAC offset/global gain (`03`), pre-gain (`23`),
and control-panel writes (ANC `25/33`, gain `26`, LED `28`, prompt tone `2B`,
dual-mic ENC `34`). No generic command API is exposed. OTA command `4F`
(`oR`, `zd/cW/bW/EW`) and its subcommands, including START_OTA `02`,
ENTER_OTA_MODE `03`, BLOCK_RESP `05`, FINISH `08`, REBOOT `09`, CANCEL `0B`
and LINK_MODE `0C`, are never sent. Nor are status, identity, firmware-version,
pairing or wake requests. One battery write is followed by at most 32 reads
within one second; short writes, silence, invalid data and ordinary HID I/O
failure produce no new reading or provider error. Existing engine history
can remain unconfirmed; it is not refreshed as a new observation.

Registration includes server dispatch, the offline ID index, descriptor
discovery, the compatibility catalog and its generated documentation. The
brand-disable registry in `device_pack.py` adds only `moondrop`, preserving
the existing required equality with registered brand readers. Synthetic
tests cover wire bytes, offsets/ranges, guards before opening, no other
commands, deadlines, cleanup, child isolation, history, and fake Windows
descriptor discovery/caching. No hardware, network or vendor script is used.
Actual collection/lengths, response headers, standalone cold-start queries,
freshness while off, charging and sleep/wake remain unverified.

### Logitech (more HID++ 2.0 devices and G-series headsets)

**Connection:** Lightspeed, Unifying or Bolt receiver

The provider reads HID++ 2.0 generically from the receiver's `ff00` vendor collections (slots 1-6), so most other HID++ 2.0 mice and keyboards on a Lightspeed, Unifying or Bolt receiver should answer the same `0x1004` unified battery request. The G-series headsets - G533, G535, G633, G635, G733, G933, G935, G PRO, G PRO X - are in the headset pid table and use feature `0x1F20` (the G535 on its consumer collection). Only the models listed in the support table above are confirmed so far.

Devices with no feature table at all (the older HID++ 1.0 generation) are read through the old battery registers instead: a short report `10 <slot> 81 <register>` on the short collection reads register `0x0D` (a percentage with a charging status - `30` discharging, `50` recharging, `90` full) or register `0x07` (a discrete level - 7 full, 5 good, 3 low, 1 critical - beside a charging byte), the same registers and order Solaar uses. This was added for the M710 keyboard of the MK710 wireless desktop ([#159](https://github.com/HeyOkay/HaloBattery/issues/159)), built from Solaar's implementation; **confirmed on hardware** in the reporter's test-build run - the icon shows the keyboard's coarse level.
