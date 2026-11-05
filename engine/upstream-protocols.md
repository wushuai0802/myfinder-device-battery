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

**CompX keyboard receivers `3554:FA09` (#22, #25) and `3554:FA07` (#25): not
added.** `FF02:0002` is transport shape, not evidence that a keyboard answers a
mouse battery command. The reviewed OpenMouse mouse package `0.49.0` and
[keyboard package `1.6.1`](https://www.npmjs.com/package/@openmouse/keyboard-protocol/v/1.6.1)
contain no matching keyboard IDs / CompX keyboard battery implementation. The
repository's existing `pulsar.py`, `gwolves.py` and G-Wolves Compx notes describe
mouse framing and mouse battery reads only. They do not justify a keyboard
command or reply offsets. Both IDs remain unclaimed and receive no mouse query.
Needed: an official-driver USB capture with the exact receiver ID, collection,
report IDs/lengths, battery command/reply, and corresponding level/charging state,
or an independently documented compatible implementation. Any future entries
must use `kind=keyboard` and remain pending hardware verification.

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
G87 `3837:201B`, G87 `41E4:2001`, K99 and G98 V2 protocols are different and
are not reused. The research table separately records their MIT, missing or
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

### Logitech (more HID++ 2.0 devices and G-series headsets)

**Connection:** Lightspeed, Unifying or Bolt receiver

The provider reads HID++ 2.0 generically from the receiver's `ff00` vendor collections (slots 1-6), so most other HID++ 2.0 mice and keyboards on a Lightspeed, Unifying or Bolt receiver should answer the same `0x1004` unified battery request. The G-series headsets - G533, G535, G633, G635, G733, G933, G935, G PRO, G PRO X - are in the headset pid table and use feature `0x1F20` (the G535 on its consumer collection). Only the models listed in the support table above are confirmed so far.

Devices with no feature table at all (the older HID++ 1.0 generation) are read through the old battery registers instead: a short report `10 <slot> 81 <register>` on the short collection reads register `0x0D` (a percentage with a charging status - `30` discharging, `50` recharging, `90` full) or register `0x07` (a discrete level - 7 full, 5 good, 3 low, 1 critical - beside a charging byte), the same registers and order Solaar uses. This was added for the M710 keyboard of the MK710 wireless desktop ([#159](https://github.com/HeyOkay/HaloBattery/issues/159)), built from Solaar's implementation; **confirmed on hardware** in the reporter's test-build run - the icon shows the keyboard's coarse level.
