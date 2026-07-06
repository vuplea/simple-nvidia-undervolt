# Ready-made tuning profiles

Folders of profile shortcuts per GPU generation are offered.
They are based on reviews and discussion threads (see below).

## Setup

1. Extract `simple-nvidia-undervolt-profiles.zip` from [releases](https://github.com/vuplea/simple-nvidia-undervolt/releases).
2. Run `~install-simple-nvidia-undervolt.exe` to install the app to Program Files, where the shortcuts are targeting.
3. Double-click shortcuts from the folder matching your GPU generation to apply them.

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

### The values

Perf boost (all tiers cap at 1000 / 1030 / 1050 / 1050 / 1040 mV for Pascal / Turing / Ampere /
Ada / Blackwell. That is each generation's real peak voltage under load except on Pascal, which
peaks at ~1050 mV — power and thermal limits rarely let sustained load sit that high anyway, so
the lower cap costs about nothing while shaving some power. The core push is measured from the
peak clock; the gain comes from it plus the memory offset):

| Tier | Pascal | Turing | Ampere | Ada | Blackwell |
|---|---|---|---|---|---|
| 1 low | mem +3% | mem +5% | mem +5% | mem +5% | mem +5% |
| 2 moderate | core +3% · mem +5% | core +3% · mem +6% | core +3% · mem +6% | core +3% · mem +6% | core +3% · mem +6% |
| 3 high | core +5% · mem +6% | core +5% · mem +8% | core +4% · mem +7% | core +6% · mem +8% | core +7% · mem +8% |
| 4 very high | core +8% · mem +8% | core +8% · mem +10% | core +5% · mem +8% | core +8% · mem +10% | core +10% · mem +10% |

Power cut, same perf (tiers 1–2 are pure voltage caps — the clock stays whatever the card's own
factory curve says at that voltage, which is stable by construction; tiers 3–4 lower the cap further
AND hold a near-peak clock there — shown as the % off the generation's peak clock — which is where
savings deepen and the lottery begins):

| Tier | Pascal | Turing | Ampere | Ada | Blackwell |
|---|---|---|---|---|---|
| 1 low | 925 mV | 925 mV | 900 mV | 975 mV | 975 mV |
| 2 moderate | 900 mV | 900 mV | 875 mV | 950 mV | 950 mV |
| 3 high | 875 mV, hold −2% | 875 mV, hold −5% | 850 mV, hold −3% | 925 mV, hold −5% | 925 mV, hold −1% |
| 4 very high | 850 mV, hold −2% | 850 mV, hold −5% | 825 mV, hold −3% | 900 mV, hold −5% | 900 mV, hold −1% |

Deep power cut (pure caps — each only requests a point the factory curve already validated, so the
risk here is performance given up, not instability. The driver stops honoring caps below a
per-generation floor — roughly 800 mV on GTX 10/RTX 20, 750 mV on RTX 30, 850–875 mV on
RTX 40/50 — so each ladder bottoms out just above its floor):

| Tier | Pascal / Turing / Ampere | Ada / Blackwell |
|---|---|---|
| 1 low | 900 mV | 925 mV |
| 2 moderate | 875 mV | 900 mV |
| 3 high | 850 mV | 875 mV |
| 4 very high | 825 mV | 850 mV |

On RTX 40/50 cards whose floor sits at ~875 mV, tier 4 settles at the floor and saves no more than
tier 3 — harmless, just no extra gain.

### What to expect

- **Perf boost**: roughly +1–2% FPS at tier 1 (memory-bound games gain most), up to +5–10% at the top
  tiers on cards that hold them. Power stays roughly flat.
- **Power cut, same perf**: about −10–25% board power at tiers 1–2 for 0 to −5% performance;
  −20–35% at tiers 3–4 for ~0–3% if the card holds the held clock.
- **Deep power cut**: about −15–35% board power and noticeably lower temperatures/noise, for a real
  −1% to −10% performance cost that grows with the tier and is steepest on RTX 40/50 (their clock
  follows voltage more tightly than older generations).

Apply a profile with the card cooled down (idle desktop, not right after a gaming session). The
tuning resolves against the live curve, which shifts slightly with temperature, so applying hot
bakes in a slightly different tuning than applying cold.

## Where the values come from

Instrumented reviews (TechPowerUp, Tom's Hardware, Igor's Lab, TechSpot, thefpsreview, QuasarZone)
pin each generation's peak load voltage, overclocking headroom and measured savings; large owner
threads (ComputerBase, Overclock.net, Overclockers UK) show how those numbers spread across many
cards, which is what the risk tiers encode. The load-bearing evidence per generation:

- **GTX 10 (Pascal)** — under sustained load Pascal tops out near 1.05 V despite its 1.093 V
  firmware ceiling ([Tom's Hardware's 1080 Ti measurements](https://www.tomshardware.com/reviews/msi-geforce-gtx-1080-ti-gaming-x-11g,5036-4.html)).
  Max core OC converges to ~2050 MHz across samples, ~5–8% above a card's own peak boost
  ([TechPowerUp 1080 Ti](https://www.techpowerup.com/review/nvidia-geforce-gtx-1080-ti/33.html)),
  and 1080 Ti GDDR5X tops out at ~+8–10% ([its error correction fails silently past that](https://forums.tomshardware.com/threads/1080-ti-mem-oc-too-good-to-be-true.3367115/)).
  875–900 mV holds ~1900 MHz on most cards for a 20–30% power cut
  ([Undervolting Pascal, Overclock.net](https://www.overclock.net/threads/undervolting-pascal.1665721/));
  a full 1070 cap scan measured 159 W → 124 W at 950 mV for <1% performance
  ([SFF.life](https://sff.life/how-to-undervolt-gpu/)).
- **RTX 20 (Turing)** — load voltage sits at ~1.03–1.04 V and the power limit, not voltage, bounds
  sustained clocks, so core OC pays only ~5% real FPS
  ([TechPowerUp 2080 Ti FE](https://www.techpowerup.com/review/nvidia-geforce-rtx-2080-ti-founders-edition/36.html),
  [TechSpot: +110 core/+700 mem = +5.6% average](https://www.techspot.com/article/1704-geforce-rtx-2080-overclocking/)).
  The multi-user [ComputerBase Turing thread](https://www.computerbase.de/forum/threads/turing-rtx-2060-2070-2080-ti-overclocking-undervolting.1838762/)
  pins the undervolt: a 75% power target held the same FPS at −45 W with load voltage falling to
  ~0.83 V. Caps below ~800 mV are ignored under load
  ([Overclockers.com](https://www.overclockers.com/forums/threads/undervolting-gpu-adjusting-a-voltage-frequency-curve-under-800-mv.787507/)).
- **RTX 30 (Ampere)** — the curve tops at ~1.05–1.08 V (a 3080 FE observed at 1.081 V under load)
  and the hot Samsung 8 nm node undervolts famously well: 900 mV ≈ the stock 325 W, and each
  further 50 mV off the cap saves ~30 W on a 3080
  ([Igor's Lab 3080](https://www.igorslab.de/en/geforce-rtx-3080-undervolting-when-reason-and-experiment-joy-on-ampere-meetings/3/),
  [3090 follow-up](https://www.igorslab.de/en/nvidia-geforce-rtx-3090-undervolting-update-so-goes-a-little-reason-even/)).
  Core OC gains only ~1% (power-limited) while GDDR6X takes ~+11%
  ([TechPowerUp 3080 FE](https://www.techpowerup.com/review/nvidia-geforce-rtx-3080-founders-edition/39.html));
  the 1900 MHz @ 887 mV and 1890 MHz @ 850 mV sweet spots recur through the large
  [Overclockers UK 3080 thread](https://forums.overclockers.co.uk/threads/3080-undervolting.18936805/).
- **RTX 40 (Ada)** — voltage-limited: the curve tops at ~1.05 V (4090; 4080-class ~1.07 V) and clock
  tracks voltage, so caps trade clock for power — 850 mV measured −5.3% performance for −33% power,
  and near-half TDP loses only 8% at 4K
  ([QuasarZone via VideoCardz](https://videocardz.com/newz/nvidia-geforce-rtx-4090-power-limiting-and-undervolting-test-shows-only-8-performance-drop-at-half-the-tdp)).
  The curve floor sits at ~875 mV on many cards; lower caps are silently clamped
  ([Overclockers UK: how far down can you downvolt a 4090](https://forums.overclockers.co.uk/threads/how-far-down-can-you-downvolt-the-rtx-4090.18960543/)).
  A non-golden FE holds +230 core (~+8%) and +750 mem (+6.5%)
  ([thefpsreview 4080 Super](https://www.thefpsreview.com/2024/03/30/overclocking-nvidia-geforce-rtx-4080-super-founders-edition/)),
  and hours-long ray-traced sessions expose undervolts that pass short tests
  ([Overclock.net 4080S thread](https://www.overclock.net/threads/why-such-a-massive-difference-with-undervolt-rtx4080s.1818041/)).
- **RTX 50 (Blackwell)** — load voltage tops at ~1.04–1.06 V (forcing the 1.075 V slider max just
  throttles) and core headroom is unusually large: +270 MHz ≈ +9% on the 5090 FE, +350 MHz ≈ +12.6%
  on the 5080 FE ([thefpsreview 5090](https://www.thefpsreview.com/2025/01/28/overclocking-nvidia-geforce-rtx-5090-founders-edition/),
  [5080](https://www.thefpsreview.com/2025/02/07/overclocking-nvidia-geforce-rtx-5080-founders-edition/)).
  The [ComputerBase Blackwell thread](https://www.computerbase.de/forum/threads/blackwell-5070-5080-5090-overclocking-undervolting-sammelthread.2228911/)
  collects the undervolt spread — e.g. 870 mV/2572 MHz cut a 5090 from 579 W to 444 W for ~4–5% —
  with ~875 mV about the lowest most cards genuinely hold. GDDR7's on-die ECC turns memory over-OC
  into silent slowdown ([Guru3D](https://forums.guru3d.com/threads/rtx-5000-memory-oc-over-2000.455355/)).

## If something goes wrong

Games crashing, artifacts, or a driver reset after applying a profile is the normal failure mode of
an unlucky sample, not damage — apply a lower tier (or Reset to stock) and you're done. Validate a
high/very-high tier with 20–30 minutes of a demanding (ray-traced) game, not just a benchmark: short
synthetic passes over-report stability. A memory overclock past a card's limit can also show up as
silently LOWER performance instead of crashes (the error correction on GDDR5X and newer memory
retries instead of crashing) — if a perf-boost tier benchmarks worse than the tier below, use the
tier below.

Because a profile re-applies at logon, an unstable one comes back after a crash reboot — that's
harmless at the desktop (instability shows under 3D load), but step down or Reset to stock before
returning to the game.
