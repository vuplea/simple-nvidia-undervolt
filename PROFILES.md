# Ready-made NVIDIA overclock & undervolt profiles

Folders of profile shortcuts per card range are offered. They are based on instrumented reviews
and large owner discussion threads, cited inline below.

## Setup

1. Extract `simple-nvidia-undervolt-profiles.zip` from [releases](https://github.com/vuplea/simple-nvidia-undervolt/releases).
2. Run `~install-simple-nvidia-undervolt.exe` to install the app to Program Files, where the shortcuts are targeting.
3. Double-click shortcuts from the folder that lists your card to apply them.

## The matrix: three families × four risk tiers

Every profile is one of three goals at one of four aggressiveness levels:

| Family | Goal |
|---|---|
| **Perf boost** | A few percent more FPS at roughly stock power: a memory overclock plus, from tier 2 up, a core clock push. |
| **Power cut, same perf** | The classic undervolt: much less power/heat/noise at roughly the performance you have today. |
| **Deep power cut** | Maximum savings, giving up a few percent of performance (quiet/SFF/summer profile). |

| Tier | Meaning |
|---|---|
| **1 low risk** | Should just work on ~95% of cards. |
| **2 moderate risk** | Safe for a clear majority; a small minority steps down. |
| **3 high risk** | Tuned for the median card — roughly a 1-in-3 chance you need to step down. |
| **4 very high risk** | Silicon lottery: roughly half of all cards will NOT hold this. Good samples only. |

Three facts shape all the values:

- **Voltage behavior is uniform within an architecture.** Peak load voltage, the driver's voltage
  floor, and core overclock headroom don't vary between cards of the same generation, so the
  `--mv`/`--peak-mv` anchors and the core push are per-generation. Perf-boost profiles cap at
  1000 / 1030 / 1050 / 1050 / 1040 mV for Pascal / Turing / Ampere / Ada / Blackwell — each
  generation's real peak voltage under load, except on Pascal, which peaks at ~1050 mV; power and
  thermal limits rarely let sustained load sit that high anyway, so its lower cap costs about
  nothing while shaving some power.
- **Memory headroom is what splits a generation.** Safe memory-overclock percentages differ by
  memory type (GDDR5 / 5X / 6 / 6X / 7) and bus width, which is why most generations get more than
  one folder. Memory overclocks fail *soft*: GDDR5-and-newer error correction retries instead of
  crashing, so past the limit FPS silently drops. Validate perf-boost tiers with a benchmark, and
  if a tier is slower than the one below it, use the one below.
- **Deep power cut is pure caps** — each tier only requests a point the card's own factory curve
  already validated, so the risk is performance given up, not instability. The driver stops
  honoring caps below a per-generation floor (roughly 800 mV on GTX 10/16/RTX 20, 750 mV on
  RTX 30, 850–875 mV on RTX 40/50); a cap below the floor is silently clamped and simply saves no
  more than the tier above — harmless.

### Picking your folder

Folder names list their cards. The ambiguous cases:

- **3060 Ti**: two memory variants exist — the 2022 refresh is GDDR6X (check the box or GPU-Z's
  memory type); it belongs with the GDDR6X folder, the original with the GDDR6 one.
- **1650**: the GDDR6 revision belongs in the GTX 16 GDDR6 folder, the original GDDR5 card in the
  GDDR5 one.
- **5050**: the 8 GB desktop card is GDDR6; a 9 GB GDDR7 variant belongs with the GDDR7 folder.
- **Titan-class**: Titan X/Xp use the Pascal GDDR5X folder, Titan RTX the RTX 20 folder, and the
  RTX 4090 D the RTX 40 4070-and-up folder. Workstation and laptop GPUs are out of scope.

## GTX 10 series (Pascal)

Pascal's voltage/clock behavior is generation-wide: sustained load tops out near 1.05 V despite the
1.093 V firmware ceiling ([Tom's Hardware's 1080 Ti measurements](https://www.tomshardware.com/reviews/msi-geforce-gtx-1080-ti-gaming-x-11g,5036-4.html)),
max core OC converges to ~2050 MHz across samples, ~5–8% above a card's own peak boost
([TechPowerUp 1080 Ti](https://www.techpowerup.com/review/nvidia-geforce-gtx-1080-ti/33.html)),
875–900 mV holds ~1900 MHz on most cards for a 20–30% power cut
([Undervolting Pascal, Overclock.net](https://www.overclock.net/threads/undervolting-pascal.1665721/)),
and a full 1070 cap scan measured 159 W → 124 W at 950 mV for <1% performance
([SFF.life](https://sff.life/how-to-undervolt-gpu/)). The folders differ only on the memory
overclock, by memory type.

### GDDR5 — 1050, 1050 Ti, 1060, 1070, 1070 Ti

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +5% | core +3% · mem +8% | core +5% · mem +10% | core +8% · mem +12% |
| Power cut, same perf | 925 mV | 900 mV | 875 mV, −2% | 850 mV, −2% |
| Deep power cut | 900 mV | 875 mV | 850 mV | 825 mV |

GDDR5 takes a large percentage overclock: the everyday-stable range on the weakest 1070-class
members is ~+12% ([TechSpot MSI 1070 Ti: +12%](https://www.techspot.com/review/1515-msi-geforce-gtx-1070-ti/page2.html),
[TechPowerUp Gigabyte 1070](https://www.techpowerup.com/review/gigabyte-gtx-1070-xtreme-gaming/29.html)),
with record samples near +20%. The ladder is sized to the weakest member and stays below the
useful-throughput ceiling — GDDR5's error correction makes FPS peak around +12–15% and then
*decline* before anything crashes. These cards are strongly power-limit-bound (75 W hard cap on the
1050/1050 Ti), so the perf-boost design of a near-peak-voltage core hold plus memory OC is what
pays real FPS.

### GDDR5X — 1080, 1080 Ti

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +3% | core +3% · mem +5% | core +5% · mem +6% | core +8% · mem +8% |
| Power cut, same perf | 925 mV | 900 mV | 875 mV, −2% | 850 mV, −2% |
| Deep power cut | 900 mV | 875 mV | 850 mV | 825 mV |

GDDR5X tops out at a ~+8–10% useful overclock, measured on the 1080 Ti itself —
[its error correction fails silently past that](https://forums.tomshardware.com/threads/1080-ti-mem-oc-too-good-to-be-true.3367115/) —
so the memory ladder stays conservative. Both cards are power- and temperature-bound at stock (the
1080 Ti FE throttles to ~1650 MHz under load,
[TechPowerUp](https://www.techpowerup.com/review/nvidia-geforce-gtx-1080-ti/33.html)), so the
near-peak core hold converts freed wattage to FPS.

## GTX 16 / RTX 20 series (Turing)

Turing shares one voltage story across GTX 16 and RTX 20: load voltage sits at ~1.03–1.04 V, the
power limit — not voltage — bounds sustained clocks, so a core OC pays only ~5% real FPS
([TechPowerUp 2080 Ti FE](https://www.techpowerup.com/review/nvidia-geforce-rtx-2080-ti-founders-edition/36.html),
[TechSpot: +110 core/+700 mem = +5.6% average](https://www.techspot.com/article/1704-geforce-rtx-2080-overclocking/)).
The multi-user [ComputerBase Turing thread](https://www.computerbase.de/forum/threads/turing-rtx-2060-2070-2080-ti-overclocking-undervolting.1838762/)
pins the undervolt: a 75% power target held the same FPS at −45 W with load voltage falling to
~0.83 V. Caps below ~800 mV are ignored under load
([Overclockers.com](https://www.overclockers.com/forums/threads/undervolting-gpu-adjusting-a-voltage-frequency-curve-under-800-mv.787507/)).
All three folders share the voltage ladders; memory splits them.

### GTX 16, GDDR5 — 1630, 1650, 1660

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +4% | core +3% · mem +6% | core +5% · mem +7% | core +8% · mem +8% |
| Power cut, same perf | 925 mV | 900 mV | 875 mV, −5% | 850 mV, −5% |
| Deep power cut | 900 mV | 875 mV | 850 mV | 825 mV |

8 Gbps GDDR5 has modest headroom — a Zotac 1660 held +6.25% stable over a week of testing
([Wccftech](https://wccftech.com/overclocking-the-zotac-gaming-geforce-gtx-1660-for-impressive-gains/))
and a Gigabyte 1650 settled at ~+7.9%
([Modders-Inc](https://www.modders-inc.com/gigabyte-gtx-1650-gaming-oc-4g-review/7/)) — and its
error-correction plateau caps the useful window well below any crash point. The 1650 is a hard
75 W card ([SkatterBencher #42](https://skatterbencher.com/2022/06/09/skatterbencher-42-nvidia-geforce-gtx-1650-overclocked-to-2205-mhz/)).
The GTX 1630's tiny 64-bit bus puts it closer to these cards than to the 12 Gbps GDDR6 parts, so it
lives here.

### GTX 16, GDDR6 — 1650 GDDR6, 1650 Super, 1660 Super, 1660 Ti

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +8% | core +3% · mem +12% | core +5% · mem +16% | core +8% · mem +20% |
| Power cut, same perf | 925 mV | 900 mV | 875 mV, −5% | 850 mV, −5% |
| Deep power cut | 900 mV | 875 mV | 850 mV | 825 mV |

Most of this folder runs 12 Gbps GDDR6 that is physically 14 Gbps-rated silicon clocked down, so
+16.7% is essentially free and review samples reach +26–28% with net-positive FPS
([TechPowerUp MSI 1660 Ti: +27%](https://www.techpowerup.com/review/msi-geforce-gtx-1660-ti-gaming-x/33.html),
[Gigabyte 1650 GDDR6: +26%](https://www.techpowerup.com/review/gigabyte-geforce-gtx-1650-oc-gddr6/33.html),
[ASUS 1650 Super: +28%, +12.4% real FPS](https://www.techpowerup.com/review/asus-geforce-gtx-1650-super-strix-oc/33.html)).
The exception is the 1660 Super, whose memory ships at 14 Gbps already and tops out at ~+11–12%
([BabelTech 1660 Super vs 1660 Ti](https://babeltechreviews.com/46-game-overclocking-showdown-the-gtx-1660-super-vs-the-gtx-1660-ti/)) —
tier 1 (+8%) is sized so it still holds there, while tiers 2–4 pay off on the 12 Gbps parts and
merely plateau (soft, via error-correction retry) on the 1660 Super.

### RTX 20 — 2060, 2070, 2080, 2080 Ti

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +5% | core +3% · mem +6% | core +5% · mem +8% | core +8% · mem +10% |
| Power cut, same perf | 925 mV | 900 mV | 875 mV, −5% | 850 mV, −5% |
| Deep power cut | 900 mV | 875 mV | 850 mV | 825 mV |

A homogeneous block: every card (including the Super refreshes) runs 14 Gbps GDDR6, stable at +700
to +1000 MHz (~+11–14%), which brackets the +5–10% ladder with margin.

## RTX 30 series (Ampere)

Ampere's curve tops at ~1.05–1.08 V (a 3080 FE observed at 1.081 V under load) and the hot
Samsung 8 nm node undervolts famously well: 900 mV ≈ the stock 325 W performance on a 3080, and
each further 50 mV off the cap saves ~30 W
([Igor's Lab 3080](https://www.igorslab.de/en/geforce-rtx-3080-undervolting-when-reason-and-experiment-joy-on-ampere-meetings/3/),
[3090 follow-up](https://www.igorslab.de/en/nvidia-geforce-rtx-3090-undervolting-update-so-goes-a-little-reason-even/)).
The whole generation is power-limit-bound at stock, so the core push stays small everywhere; the
folders split on memory type and on where each card sits on its voltage/frequency curve.

### GDDR6 — 3050, 3060, 3060 Ti GDDR6, 3070

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +10% | core +3% · mem +12% | core +4% · mem +14% | core +5% · mem +16% |
| Power cut, same perf | 925 mV | 900 mV | 875 mV, −3% | 850 mV, −3% |
| Deep power cut | 900 mV | 875 mV | 850 mV | 825 mV |

Plain GDDR6 on these cards runs cool and overclocks far beyond GDDR6X: 3070 FE stable at +13%,
3060 Ti FE at +11%, 3050 at +18.6%
([thefpsreview 3070 FE](https://www.thefpsreview.com/2020/11/04/nvidia-geforce-rtx-3070-fe-overclocking/),
[3060 Ti FE](https://www.thefpsreview.com/2020/12/14/nvidia-geforce-rtx-3060-ti-fe-overclocking/),
[SkatterBencher #62](https://skatterbencher.com/2023/10/22/skatterbencher-62-nvidia-geforce-rtx-3050-overclocked-to-2220mhz/)).
Tier 1 sits just under the weakest confirmed sample; the upper tiers extrapolate above it, which is
what their risk labels say. The "power cut, same perf" ladder sits 25 mV above the GDDR6X folders'
because these lower-TDP cards boost to ~1.0–1.05 V at stock for their ~1900 MHz — unlike a
3080/3090, which self-settles near 900 mV to hold its power ceiling — so owner data puts "900 mV ≈
stock, 850 mV ≈ −5–6%" on a 3070; holding stock performance on ~95% of cards needs the higher caps.
The narrower-bus 3060 8GB and 3050 6GB variants share the folder; percentages and caps apply
unchanged.

### GDDR6X — 3060 Ti GDDR6X, 3070 Ti, 3080

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +5% | core +3% · mem +6% | core +4% · mem +7% | core +5% · mem +8% |
| Power cut, same perf | 900 mV | 875 mV | 850 mV, −3% | 825 mV, −3% |
| Deep power cut | 900 mV | 875 mV | 850 mV | 825 mV |

These cards are hard power-limited and already boost-throttled to ~900 mV at stock, so a 900 mV cap
is free, a core OC pays only ~1%, and hot GDDR6X limits the memory ladder to +5–8%
([TechPowerUp 3080 FE](https://www.techpowerup.com/review/nvidia-geforce-rtx-3080-founders-edition/39.html),
[thefpsreview 3070 Ti: mem OC discouraged beyond ~+5%](https://www.thefpsreview.com/2021/07/09/overclocking-nvidia-geforce-rtx-3070-ti-founders-edition/)).
The 1900 MHz @ 887 mV and 1890 MHz @ 850 mV sweet spots recur through the large
[Overclockers UK 3080 thread](https://forums.overclockers.co.uk/threads/3080-undervolting.18936805/).

### GDDR6X flagship — 3080 Ti, 3090, 3090 Ti

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +3% | core +3% · mem +4% | core +4% · mem +5% | core +5% · mem +6% |
| Power cut, same perf | 900 mV | 875 mV | 850 mV, −3% | 825 mV, −3% |
| Deep power cut | 875 mV | 850 mV | 825 mV | 800 mV |

The binding constraint is the double-sided 24 GB 3090, whose back-side GDDR6X modules run
100–110 °C junction ([Tom's Hardware](https://www.tomshardware.com/news/hwinfo64-adds-gddr6x-temp-monitoring-rtx30series)) —
over-overclocking hot GDDR6X is silently counter-productive, so the memory ladder stays
junction-safe on the worst card (a 3080 Ti or single-sided 3090 Ti has headroom to spare at these
tiers). The deep-cut ladder goes one step lower than the rest of Ampere because on 400–450 W dies
absolute watts dominate: ~30–40 W per 50 mV, a 3090 at 793 mV still ran 1785 MHz at 285 W
([Igor's Lab 3090](https://www.igorslab.de/en/nvidia-geforce-rtx-3090-undervolting-update-so-goes-a-little-reason-even/)),
and a 3090 Ti held to 300 W keeps ~90% of its 4K performance
([Tom's Hardware](https://www.tomshardware.com/news/rtx-3090-ti-gaming-beast-at-300w)).

## RTX 40 series (Ada)

Ada is voltage-limited: the curve tops at ~1.05 V (4090; 4080-class ~1.07 V) and clock tracks
voltage, so caps trade clock for power — 850 mV measured −5.3% performance for −33% power, and
near-half TDP loses only 8% at 4K
([QuasarZone via VideoCardz](https://videocardz.com/newz/nvidia-geforce-rtx-4090-power-limiting-and-undervolting-test-shows-only-8-performance-drop-at-half-the-tdp)).
Hours-long ray-traced sessions expose undervolts that pass short tests
([Overclock.net 4080S thread](https://www.overclock.net/threads/why-such-a-massive-difference-with-undervolt-rtx4080s.1818041/)).
The split is by die class: small 128-bit power-limited parts vs the wide-bus voltage-limited rest.

### GDDR6 128-bit — 4060, 4060 Ti

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +11% | core +3% · mem +13% | core +6% · mem +15% | core +8% · mem +17% |
| Power cut, same perf | 975 mV | 950 mV | 925 mV, −5% | 900 mV, −5% |
| Deep power cut | 925 mV | 900 mV | 885 mV | 875 mV |

These parts run plain 17/18 Gbps GDDR6 on a bandwidth-starved 128-bit bus, so memory is the
dominant lever: +1000 MHz (to 20 Gbps, ~+11%) is the everyday pick and +1250 was stable where
+1500 crashed ([Tom's Hardware 4060 Ti](https://www.tomshardware.com/reviews/nvidia-geforce-rtx-4060-ti-review/3),
[thefpsreview 4060 Ti FE: +1220](https://www.thefpsreview.com/2023/06/21/overclocking-nvidia-geforce-rtx-4060-ti-founders-edition/)).
Tier 1 maps to the 20 Gbps plateau, tier 4 to the ~21 Gbps crash edge. The power limit slider only
allows ~8–9% on the FE, so an undervolt frees TDP that becomes real boost
([HotHardware 4060 Ti](https://hothardware.com/reviews/nvidia-geforce-rtx-4060-ti-gpu-review?page=5)).
The deep-cut ladder bottoms at 875 mV because these small dies report a vBIOS voltage minimum near
~860 mV — a lower cap would clamp to nothing (single-thread sourcing; 875 is the conservative read).

### 4070, 4070 Ti, 4080, 4090

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +5% | core +3% · mem +6% | core +6% · mem +8% | core +8% · mem +10% |
| Power cut, same perf | 975 mV | 950 mV | 925 mV, −5% | 900 mV, −5% |
| Deep power cut | 925 mV | 900 mV | 875 mV | 850 mV |

The GDDR6X-class, voltage-limited regime (Supers included): caps trade clock for power at a
near-fixed ratio, GDDR6X memory headroom is ~+5–8%
([HotHardware 4090 undervolt](https://hothardware.com/news/undervolted-rtx-4090-benchmarked-impressive-results)),
and a non-golden FE holds +230 core (~+8%) and +750 mem (+6.5%)
([thefpsreview 4080 Super](https://www.thefpsreview.com/2024/03/30/overclocking-nvidia-geforce-rtx-4080-super-founders-edition/)).
The curve floor sits at ~875 mV on many cards; lower caps are silently clamped
([Overclockers UK: how far down can you downvolt a 4090](https://forums.overclockers.co.uk/threads/how-far-down-can-you-downvolt-the-rtx-4090.18960543/)).
The 2024 GDDR6 revision of the 4070 performs within 0–2% of the GDDR6X original
([VideoCardz](https://videocardz.com/newz/nvidia-geforce-rtx-4070-gddr6-vs-gddr6x-tested-99-performance-at-1440p-1080p-98-at-4k))
and takes the same values.

## RTX 50 series (Blackwell)

Blackwell's load voltage tops at ~1.04–1.06 V (forcing the 1.075 V slider max just throttles) and
core headroom is unusually large: +270 MHz ≈ +9% on the 5090 FE, +350 MHz ≈ +12.6% on the 5080 FE
([thefpsreview 5090](https://www.thefpsreview.com/2025/01/28/overclocking-nvidia-geforce-rtx-5090-founders-edition/),
[5080](https://www.thefpsreview.com/2025/02/07/overclocking-nvidia-geforce-rtx-5080-founders-edition/)).
The [ComputerBase Blackwell thread](https://www.computerbase.de/forum/threads/blackwell-5070-5080-5090-overclocking-undervolting-sammelthread.2228911/)
collects the undervolt spread — e.g. 870 mV/2572 MHz cut a 5090 from 579 W to 444 W for ~4–5% —
with ~875 mV about the lowest most cards genuinely hold. The lone GDDR6 card splits off; everything
else is uniform.

### GDDR7 — 5060, 5070, 5080, 5090

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +5% | core +3% · mem +6% | core +7% · mem +8% | core +10% · mem +10% |
| Power cut, same perf | 975 mV | 950 mV | 925 mV, −1% | 900 mV, −1% |
| Deep power cut | 925 mV | 900 mV | 875 mV | 850 mV |

GDDR7's on-die ECC turns memory over-OC into silent slowdown
([Guru3D](https://forums.guru3d.com/threads/rtx-5000-memory-oc-over-2000.455355/)), keeping the
memory ladder conservative across the stack (the 5060 Ti 8 GB and 16 GB run identical memory). One
known soft spot: the 5090 is pinned at its 575 W power limit, which eats part of a held core
overclock — perf-boost tier 4 realizes only ~+4–6% net on it, less than on the smaller cards. That
fails soft (the cap just throttles), so the 5090 shares the folder anyway.

### GDDR6 — 5050 8GB

| Family | 1 low | 2 moderate | 3 high | 4 very high |
|---|---|---|---|---|
| Perf boost | mem +9% | core +3% · mem +11% | core +7% · mem +13% | core +10% · mem +15% |
| Power cut, same perf | 975 mV | 950 mV | 925 mV, −1% | 900 mV, −1% |
| Deep power cut | 925 mV | 900 mV | 875 mV | 850 mV |

The desktop 5050 8 GB is the only Blackwell card on GDDR6 (20 Gbps, 128-bit), free of the GDDR7
ECC clamp: a review sample held ~+14.8% fully stable with crashes only near +16%, for ~+12% real
performance on this bandwidth-starved card
([TechPowerUp Gigabyte 5050](https://www.techpowerup.com/review/gigabyte-geforce-rtx-5050-gaming-oc/41.html)).
The upper-tier magnitudes rest on that single sample — the risk labels carry that uncertainty. The
card is power-limited at stock (130 W, +15% slider), so memory is the perf-boost lever
([HotHardware MSI 5050](https://hothardware.com/reviews/msi-geforce-rtx-5050-review?page=5)).

## What to expect

- **Perf boost**: roughly +1–2% FPS at tier 1 (memory-bound games gain most), up to +5–10% at the top
  tiers on cards that hold them. Power stays roughly flat. Wide-memory folders (GTX 16 GDDR6,
  Ampere GDDR6, Ada 128-bit, the 5050) gain the most, since their cards are bandwidth-starved.
- **Power cut, same perf**: about −10–25% board power at tiers 1–2 for 0 to −5% performance;
  −20–35% at tiers 3–4 for ~0–3% if the card holds the held clock.
- **Deep power cut**: about −15–35% board power and noticeably lower temperatures/noise, for a real
  −1% to −10% performance cost that grows with the tier and is steepest on RTX 40/50 (their clock
  follows voltage more tightly than older generations).

Apply a profile with the card cooled down (idle desktop, not right after a gaming session). The
tuning resolves against the live curve, which shifts slightly with temperature, so applying hot
bakes in a slightly different tuning than applying cold. Or run
`simple-nvidia-undervolt save-reference` once (idle and cool) and the temperature stops mattering:
tuning then resolves against the saved stock curve instead of the live one.

## If something goes wrong

Games crashing, artifacts, or a driver reset after applying a profile is the normal failure mode of
an unlucky sample, not damage — apply a lower tier (or Reset to stock) and you're done. Validate a
high/very-high tier with 20–30 minutes of a demanding (ray-traced) game, not just a benchmark: short
synthetic passes over-report stability. A memory overclock past a card's limit can also show up as
silently LOWER performance instead of crashes (the error correction on GDDR5 and newer memory
retries instead of crashing) — if a perf-boost tier benchmarks worse than the tier below, use the
tier below.

Because a profile re-applies at logon, an unstable one comes back after a crash reboot — that's
harmless at the desktop (instability shows under 3D load), but step down or Reset to stock before
returning to the game.
