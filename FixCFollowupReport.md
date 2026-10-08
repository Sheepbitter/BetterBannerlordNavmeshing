# fixC follow-up — the user's staircase (european_castle_ladder @ 1166.69, 1122.40, 100.79)

Model/provider: **LongCat 2.5 Preview Free (OpenCode Go)**. Work root
`C:\Users\Sheepbitter\AppData\Local\Temp\opencode\fixC2\`; real repo read-only; no commits/deploys.

---

## 0. Disambiguation — which structure, and which hub

`--props european_castle_ladder` on CC_20 lists **6** ladders. The user's coordinate (1166.69, 1122.40,
100.79) sits inside the **#33/#34** pair:

> `[33] stairs_bo` (STEPPED/LADDER, 0x800) X=[1160.03..1170.16] H=[100.14..116.05] Z=[1120.93..1126.75]
> `[34] european_castle_ladder` X=[1158.32..1173.31] H=[100.19..116.34] Z=[1120.52..1126.22]
> centreXZ=(1165.82, 1123.37); `arrow_barrel` at (1162.99, 116.35..117.03, 1121.85) sits on its top.

The hub my fixC targeted — (1120.5, 115.7, 1078.5) — is ladder **#6**
(`centreXZ=(1120.28,1079.83)`). So the two are **different staircases**; the images' wooden platform
(with the barrel) is the **user's** #33/#34, not the #6 hub.

## 1. Reproduction at the user's staircase

The platform top is the flat bit at **y≈117.10** (the collision top of #33/#34 is 116.05/116.34). Stage
dumps (`ZZCEN=1150,1182,1105,1134,95,120`) + census `1166.69,1122.40` r=6:

| build | platform top | verdict |
|---|---|---|
| `67455a2` V3.48 | flat **0°** at **y=117.10** | kept |
| `386a1fb` V3.50 | flat **0°** at **y=117.10** | kept |
| `386a1fb` + steep OFF | flat **0°** at **y=117.10** | kept |
| `90acb62` V3.51 | flat **0°** at **y=117.10** | kept |
| `c6cb279` V3.52 | **62.6° face y=[116.22..117.30] + 5.7° at y≈116.2** | **DROPPED** |
| `c8e759d` V3.53 / `cc551b4` V3.55 / `d4c5d37` V3.56 / `355f0d6` V3.58 / `00b03f0` V3.59 | dropped | DROPPED |
| `9f7477a` V3.61 **pristine** | dropped (116.62) | DROPPED |
| `9f7477a` + fixB / + fixB + fixC | dropped | DROPPED |

**The platform drop is a V3.52 regression, present in pristine V3.61 — it is NOT fixB and NOT the V3.50
steep machinery; fixC neither caused nor fixed it.** The merge-stage meshes of V3.48 and V3.61+fixB are
byte-identical (617 faces, all common); the divergence appears at the prune stage in the dumps, but the
causal commit is V3.52.

## 2. The 'before' reference

The good image (platform meshed) matches **V3.50/V3.51** (the flat 0° surface at y=117.10 — "the flat bit
at the top of the stairs"). The bad image (bare) matches **V3.52+ / fixB** (the 117.10 lid rotated away,
mesh dropped to 116.2 with a 62.6° face). So the user's "before" is the pre-V3.52 build, not "pre-fixB".

## 3. Mechanism — a gate-plate false positive

V3.52 (`c6cb279`) added `BlendGatePlates` (TiledNavMeshBuilder.cs:2597). It:
1. masks **dead-flat, up-facing** faces (`GatePlateFlatDegrees=2°`);
2. unions them into "plates" per coplanar level;
3. accepts a group when `faces>=3, 1m²<=area<=120m², span<=8m`, all verts within one cell height, >=6
   neighbour samples in a 2 m ring, and it is "proud" of its surroundings by `> climb` and `<=2.5 m`;
4. fits a least-squares plane and **rotates the plate's own vertices in place** (capped at 50°/`2*climb`).

At the stairs the platform lid (dead-flat, ~15 m², span ~5 m, proud ~1 m over the flight) **matches every
one of those criteria**, so V3.52 rotates it off 117.10 onto the fitted plane — the 62.6° edge face and
the 116.2 surface are the result. V3.61's own log: `gate plates: examined=229 blended=53 (308 faces
rotated in place ...)`; V3.50/V3.51 have no such line (V3.51 is comments-only — `git show` confirms a
+21-line comment block, no behavioural change).

**Did fixC touch this region? No.** fixC re-runs the steep pass pre-prune for *tall contour-spike fans*;
the platform lid is a flat 0° plate removed by `BlendGatePlates`, which runs elsewhere in the pipeline.
fixB+fixC and fixB are byte-identical at this spot.

## 4. Revert question, answered

- **Does V3.48 have the problem here? No.** V3.48 and V3.50 and V3.51 all keep the flat 117.10 platform.
- **What does reverting `386a1fb`(+fixA/B/C) return/cost?** Reverting to V3.50 returns the platform and
  keeps the first hub's steep repair, but reinstates the V3.50 **roof bug** (8 under-roof faces).
  Reverting to V3.48 returns the platform but re-opens the first hub's fan (steep 11 / 39.1 m²).
- **Is the user's staircase issue caused by the chain, or merely not fixed by it?** Merely **not fixed**
  — it is a **V3.52** regression. So the user's "revert the 3.50 changes" instinct is **misdirected for
  this spot**; reverting the steep machinery would leave the platform drop in place.

## 5. Strategy comparison

The mission's options (a) extend fixC's fan filter, (b) revert the V3.50 machinery + a different fan
filter, (c) fix the fan upstream — all target the *fan/spike* class, which is **not** what breaks the
user's staircase. For **this location** the only effective move is to stop V3.52's plate blend
false-positiving on the stairs' lid:

- **(a) extend fixC's filter — irrelevant here.** The lid is not a spike; fixC's spike+height test can
  never see it. Would not help the user's staircase.
- **(b) revert the V3.50 machinery — does not fix this location** (the cause is V3.52), and costs the
  first hub unless the fan filter is replaced. Not recommended as the fix for the user's staircase.
- **(c) upstream contour fix — irrelevant here** (the lid is not a contour artifact).
- **(d) recommended: narrow `BlendGatePlates` so it cannot rotate a stepped-prop landing.** The gate
  plate is the raw voxel projection of the gate's own floor colliders at a gate/door; the stairs' lid is
  a `stairs_bo`/`european_castle_ladder` landing. A targeted criterion (e.g. require the plate's
  surrounding/neighbour set to touch a gate/door collider, or exclude a plate whose ring contains
  stepped-prop faces) restores the 117.10 lid while keeping the V3.52 gate gain. This is a one-function
  change in V3.52's area, independent of the steep machinery, the roof and fixC.

**Does this change my recommended plan? Yes.** The evidence says the user's staircase is a V3.52
gate-plate regression, not a fixB/steep regression. So the recommendation is: **keep fixC (it fixes the
first hub and is roof-safe) but fix the actual user-staircase cause by narrowing the gate-plate blend**,
rather than reverting the V3.50 machinery. The user's separate "kill the single-vertex wood-heap touch"
policy for the first hub remains a good, orthogonal direction (see §6).

## 6. The first hub's remaining fans (user's shelved note)

The user reports the first hub (1120.5, 115.7, 1078.5) still has one or two fans. In fixB+fixC the
staircase census there is `steep=6, 11.857 m², worst 70.53°` (V3.50 was 7/14.320/72.08); the restored
connector is the 81.6°-class face (`f133033` equivalent), i.e. the fan is reduced, not eliminated. The
user's intended policy — stop forcing the single-vertex touch between the stair structure and the
`bd_wood_heap_b`-like prop, let that piece island and be culled — is a **connectivity-policy** change
(region/contour/stitch), orthogonal to both fixC and the V3.52 plate blend, and is the right frame for
the hub (not a face filter). It is not prototyped here.

## 7. Acceptance matrix

Not re-run: no candidate was prototyped (the finding is diagnostic, and the fix belongs to V3.52's plate
blend, outside fixC). The standing fixC matrix from the prior report still holds
(`accept\fixC4_386a1fb`, `accept\fixC4_9f7477a`): roof 0 clearance<0 min +0.067; first-hub staircase
steep 6/11.857/70.53°; gate PASS badQuadWalks=0; islands 15/15/5/1; Birtu/Basamu held. The user's
staircase (this report) is **not** fixed by any of fixA/B/C — it needs the V3.52 plate-blend narrowing.

## 8. Artifacts

- `work\{67455a2,386a1fb,90acb62,c6cb279,c8e759d,cc551b4,d4c5d37,355f0d6,00b03f0,9f7477a}_new\` —
  bisect census runs at the user's spot.
- `work\{67455a2,386a1fb,9f7477a}_newinstr\`, `work\9f7477a_pristineinstr\` — stage dumps (ZZCEN box).
- `cls\platform.py` — the platform-top per-stage reader; `cls\stairdiff.py` (dead session's).
- `patches\instr\ZZStairDbg.cs` — box now overridable via `ZZCEN`.
- `runargs\census_new.txt`, `runargs\census_new_nosteep.txt`, `runargs\props_new.txt`,
  `runargs\propsbox_new.txt`.
- `REPORT_fixC.md` — the first-hub fixC report; this file for the follow-up.

## 9. Honest differences / limits

- The exact `BlendGatePlates` criterion that should exclude the stairs' lid is **not prototyped** (budget);
  §5 gives the two candidate criteria and where they go.
- The first-hub fan count ("one or two") was not reduced to an exact number; the census (steep=6) and the
  81.6°-class connector are the evidence.
- The user's y=100.79 is the ladder **base**; the platform top is y≈117.10 — both confirmed against the
  prop AABBs.
