using System;
using System.Collections.Generic;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Stepped / ladder prop ramp synthesis (PURE: no TaleWorlds types)
    // ==================================================================
    // Thin stepped collision props are the one class of geometry the voxel core cannot represent
    // faithfully. A wooden flight's boards or a ladder's rungs are thinner than a voxel in places, so the
    // rasterizer loses their spans (erosion plus the region minimum eat narrow treads) and a whole flight
    // can end up with no navmesh at all - the player sees a bare flight and the agent cannot cross it.
    //
    // The fix is one continuous walkable surface that RIDES the flight's own tread-top envelope: the run
    // is sampled along the flight's direction, each station's height is the highest top surface there, and
    // a chain of quads follows those stations (cut so the chord never deviates from the sampled envelope
    // by more than the merge reach). Because the ramp sits ON the treads instead of hovering above them, a
    // ramp span and the tread span under it merge into ONE span: the treads keep their navmesh, the ramp
    // adds a surface exactly where the collision is thin or missing, and there is no floating plane for
    // FilterWalkableLowHeightSpans to turn into a ceiling. A horizontal apron at each end reaches past the
    // flight's own footprint so it overlaps the floor below and the landing above (a shared span is the
    // connection). A stretch of air wider than MaxBridgeRunMeters is never bridged, so a landing platform
    // that merely carries HasSteps does not get a magic ramp across its void.
    //
    // Ramp triangles are emitted as ordinary geometry into the same collision soup, so winding
    // normalisation, rasterisation, stitching and the detail mesh all treat them like any other surface -
    // with one special property: they are FORCED walkable (area RC_WALKABLE_AREA) even when their slope
    // exceeds MaxSlopeDegrees, because a stair or a ladder is meant to be traversed.
    //
    // Pure data in / data out on purpose: the editor collector and the offline harness/snapshot path both
    // call it, so a recorded snapshot validates the shipped behaviour without re-dumping.
    public static class SteppedPropRamps
    {
        // Mirrors TaleWorlds.Engine.BodyFlags (kept as ints so this file stays engine-free).
        public const int FlagLadder = 0x00000040;
        public const int FlagHasSteps = 0x00000800;

        // ---- tunables (documented in the README under "Stairs & ladders") --------------------------
        // Height band used only to estimate the flight's direction (bottom / top band envelope centres).
        public const float LayerHeightMeters = 0.5f;
        // Top-surface heights within this distance of a station's highest top count as its envelope.
        public const float EnvelopeTolerance = 0.12f;
        // Spacing of the run stations the envelope is sampled at.
        public const float StationStepMeters = 0.25f;
        private const int MaxStations = 1024;
        // A prop whose walkable height span is below this is not a flight: no ramp.
        public const float MinRiseMeters = 0.4f;
        // Above this implied slope the prop is a wall or a truly vertical ladder: no ramp.
        public const float MaxSlopeDegrees = 75f;
        // Ramp width = WidthFactor x the narrower end footprint, clamped. 1.2 m is the width a ramp needs
        // to survive Recast's ledge filter (walkableClimb trimmed per side) plus erosion.
        public const float MinWidthMeters = 1.2f;
        public const float MaxWidthMeters = 2.0f;
        public const float WidthFactor = 0.6f;
        // Horizontal reach of an end apron PAST the flight's own footprint edge at that level, so the
        // apron overlaps the floor/landing collision and shares a span with it (that is the connection).
        public const float ApronMeters = 0.55f;
        // Longest stretch of the run with no collision under the ramp that a chunk may bridge (a raster
        // hole). Anything wider is left alone: that is air, not a flight section.
        public const float MaxBridgeRunMeters = 1.5f;
        // A chunk may span about this much rise (a single flight). The deviation rule below is the real
        // shaper; this is only a safety bound on how far one quad may reach.
        public const float MaxRisePerRampMeters = 2.0f;
        // How far the chord may sit from the sampled envelope before the chunk is cut shorter. Must stay
        // inside Recast's span merge reach (walkableClimb), or the ramp stops merging with the treads.
        // (unused by the holes-only emitter; kept for the record)
        // Chained sub-ramps per prop. A flight needing more is reported as capped and skipped.
        public const int MaxRampsPerProp = 4;
        // Horizontal run a single ramp may span (also rejects walkways the base geometry already
        // describes, and props whose collision carries detached far-away triangles).
        public const float MaxRunPerRampMeters = 12.0f;
        // Name suffix for the snapshot record that carries a prop's synthesized ramp triangles. The
        // harness uses it to tell synthesized geometry from real collision, and to avoid synthesizing
        // twice when a snapshot already contains ramps.
        public const string RampNameSuffix = "#ramp";
        // A sub-ramp shorter than this is not worth emitting.
        private const float MinChunkRiseMeters = 0.05f;
        // Per-prop dedupe rounding.
        private const float DedupeQuantum = 0.05f;

        public sealed class Prop
        {
            public string Name;
            public int Flags;          // TaleWorlds BodyFlags as int
            public float[] VertsXyz;   // engine Z-up, 3 floats per vertex
            public int[] Tris;         // 0-based indices into VertsXyz
        }

        public sealed class Result
        {
            public readonly List<float> VertsXyz = new List<float>();
            public readonly List<int> Tris = new List<int>();
            public int Ramps;          // ramp quads emitted (the sloped part; aprons are extra quads)
            public int Stairs;         // HasSteps props that produced at least one ramp
            public int Ladders;        // Ladder props that produced at least one ramp
            public int Holes;          // run stretches with no collision, considered for bridging
            public int NoHoles;        // props with nothing to bridge (their treads are the path)
            public int SkippedFlat;    // rise < MinRiseMeters (or nothing walkable to follow)
            public int SkippedSteep;   // no chunk could obey the slope rule
            public int SkippedLong;    // no chunk could obey the run rule
            public int Capped;         // the flight needed more than MaxRampsPerProp chunks
            public float SlopeCapDegrees;   // the steepest ramp the Recast build can keep walkable
            public readonly List<RampInfo> Infos = new List<RampInfo>();
            // One entry per prop that produced no ramp, with the reason and the key numbers. Diagnostic
            // only (never used by the geometry); the caller logs them, capped.
            public readonly List<Skip> Skips = new List<Skip>();
        }

        // Why a prop produced no ramp. Reason is one of: z-range, no-direction, vertical, too-short,
        // no-support, no-rise, no-holes, within-climb, hole-steep, hole-long, hole-rise, hole-short,
        // capped, chunk-refused.
        public sealed class Skip
        {
            public string Prop;
            public string Reason;
            public int Tris;            // prop triangles used
            public float ZMin, ZMax;    // the prop's vertical extent
            public float RunSpan;       // horizontal extent along the flight's own direction
            public int Stations;        // run stations the flight was sampled at
            public int Supported;       // stations where the prop supplies collision
            public int Holes;           // unsupported stretches considered
            public float HoleRun;       // the offending hole's run (m), when relevant
            public float HoleRise;      // ... its rise (m)
            public float HoleSlopeDeg;  // ... its implied slope
            public int HoleCount;       // how many holes hit that reason
            public int Counter;         // which Result counter this skip bumped (1 flat, 2 steep, 3 long,
                                        // 4 capped, 5 no-holes) so it can be undone if a fallback runs
            public int Instances = 1;   // how many props folded into this line
        }

        // Folds identical (prop, reason) rows into one line with a count, keeping first-seen order
        // (deterministic). Birtu has dozens of entities of the same asset, so a raw per-prop list is
        // dominated by one name and hides the interesting rows.
        public static List<Skip> FoldSkips(List<Skip> src)
        {
            var folded = new List<Skip>();
            var index = new Dictionary<string, int>();
            for (int i = 0; i < src.Count; i++)
            {
                Skip s = src[i];
                string key = s.Prop + "\n" + s.Reason;
                int at;
                if (index.TryGetValue(key, out at)) { folded[at].Instances++; continue; }
                index[key] = folded.Count;
                folded.Add(s);
            }
            return folded;
        }

        // Undoes the bookkeeping of a skip (used when a ladder falls back to a synthesized chain).
        private static void UndoSkip(Result r, Skip s)
        {
            r.Skips.Remove(s);
            if (s.Counter == 1) r.SkippedFlat--;
            else if (s.Counter == 2) r.SkippedSteep--;
            else if (s.Counter == 3) r.SkippedLong--;
            else if (s.Counter == 4) r.Capped--;
            else if (s.Counter == 5) r.NoHoles--;
        }

        // A Ladder prop's collision is a *climbable plane* (a plank), not a tread pattern: its triangles
        // span the whole run, so the per-station envelope cannot see the flight rise, there are no gaps
        // between treads to bridge - and its own surface is too steep (and, at a ladder's width, too
        // narrow to survive erosion) to be the path itself. When the hole-based synthesis found nothing
        // to use, lay ONE straight ramp from the prop's own bottom to its top instead: that is exactly
        // the chain the hole logic would produce if every station were a hole. It goes through EmitChunk,
        // so the width clamp (>= MinWidthMeters, which is what beats erosion), the slope cap, the dedupe
        // and the two end aprons are identical to a hole bridge's.
        private static bool TryLadderChain(Result r, Prop p, float[] perp, float[] winLo, float[] winHi,
            float[] cx, float[] cy, float[] cz, float[] topZ, int used, int stations,
            float runMin, float runSpan, float zMin, float zMax, float slopeCapDegrees,
            float cellSizeMeters, Skip skip)
        {
            if ((p.Flags & FlagLadder) == 0) return false;
            if (stations < 2 || used <= 0) return false;
            if (zMax - zMin < MinRiseMeters) return false;

            // The flight's own bottom and top (the band envelope centres), NOT the per-station envelope
            // centres: a plank's every triangle spans the whole run, so each station's centre is the
            // prop's global centroid and the chain would have no run at all.
            float bx, by, tx, ty;
            if (!BandCentres(cx, cy, cz, topZ, used, zMin, zMax, out bx, out by, out tx, out ty))
                return false;

            var px = new float[2];
            var py = new float[2];
            var pz = new float[2];
            px[0] = bx;
            py[0] = by;
            pz[0] = zMin;
            px[1] = tx;
            py[1] = ty;
            pz[1] = zMax;

            float halfCell = 0.5f * cellSizeMeters;
            var keys = new List<string>();
            if (!EmitChunk(r, p.Name, perp, winLo, winHi, px, py, pz, 0, 1, runMin, runSpan,
                zMin, zMax, halfCell, halfCell, true, true, keys, slopeCapDegrees))
                return false;

            r.Ladders++;
            if (skip != null) UndoSkip(r, skip);
            return true;
        }

        public static string DescribeSkip(Skip s)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("ramps: skip '").Append(s.Prop).Append("' ").Append(s.Reason);
            if (s.HoleCount > 0)
                sb.Append(" run=").Append(Num(s.HoleRun)).Append("m rise=").Append(Num(s.HoleRise))
                  .Append("m -> ").Append(Num(s.HoleSlopeDeg)).Append("deg (holes=").Append(s.HoleCount)
                  .Append("/").Append(s.Holes).Append(")");
            sb.Append(" tris=").Append(s.Tris)
              .Append(" z=[").Append(Num(s.ZMin)).Append("..").Append(Num(s.ZMax)).Append("]")
              .Append(" runSpan=").Append(Num(s.RunSpan)).Append("m stations=").Append(s.Supported)
              .Append("/").Append(s.Stations);
            if (s.Instances > 1) sb.Append(" (x").Append(s.Instances).Append(" props)");
            return sb.ToString();
        }

        private static string Num(float v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // One emitted ramp, for logging / diagnostics (never used by the geometry itself).
        public sealed class RampInfo
        {
            public string Prop;
            public float BottomY, TopY, Run, Width, SlopeDeg;
        }

        public static bool IsSteppedOrLadder(int flags)
        {
            return (flags & (FlagHasSteps | FlagLadder)) != 0;
        }

        public static string Describe()
        {
            return "band=" + LayerHeightMeters + "m envTol=" + EnvelopeTolerance + "m station="
                + StationStepMeters + "m minRise=" + MinRiseMeters + "m maxSlope=" + MaxSlopeDegrees
                + "deg width=" + MinWidthMeters + ".." + MaxWidthMeters + "m x" + WidthFactor + " apron="
                + ApronMeters + "m (capped to the prop's own footprint) maxBridge=" + MaxBridgeRunMeters
                + "m maxRamps=" + MaxRampsPerProp
                + "m maxRunPerRamp=" + MaxRunPerRampMeters + "m (navigable cap applied per build)";
        }

        // Deterministic: no dictionary iteration order reaches the output.
        // cellSize/climb/ledgeFilter describe the Recast build the ramp will be rasterized by: they set
        // the steepest surface that can SURVIVE the ledge filter (see NavigableSlopeDegrees).
        public static Result Synthesize(IReadOnlyList<Prop> props, float cellSizeMeters = 0.25f,
            float climbMeters = 0.6f, bool ledgeFilter = true)
        {
            var r = new Result();
            if (props == null) return r;
            float slopeCap = Math.Min(MaxSlopeDegrees,
                NavigableSlopeDegrees(cellSizeMeters, climbMeters, ledgeFilter));
            r.SlopeCapDegrees = slopeCap;
            for (int i = 0; i < props.Count; i++) AppendProp(props[i], r, slopeCap, climbMeters, cellSizeMeters);
            return r;
        }

        // The steepest ramp that is worth synthesizing. Recast's ledge filter would delete a steep
        // surface - but the builder exempts FORCED (ramp) spans from that trim (see BuildTile's marker
        // pass), so a ramp only has to obey the per-cell climb rule, tan(slope) <= climb/cs (67.4 deg at
        // cs 0.25 / climb 0.6). Ordinary geometry is untouched by that exemption and keeps the ledge cap
        // atan(climb / (2*cs)) = 50.19 deg.
        public static float NavigableSlopeDegrees(float cellSizeMeters, float climbMeters, bool ledgeFilter)
        {
            if (cellSizeMeters <= 1e-4f || climbMeters <= 1e-4f) return MaxSlopeDegrees;
            float t = climbMeters / cellSizeMeters;
            return (float)(Math.Atan(t) * 180.0 / Math.PI);
        }

        private static void AppendProp(Prop p, Result r, float slopeCapDegrees, float climbMeters,
            float cellSizeMeters)
        {
            if (p == null || p.VertsXyz == null || p.Tris == null) return;
            if (!IsSteppedOrLadder(p.Flags)) return;
            int triCount = p.Tris.Length / 3;
            if (triCount <= 0) return;
            int vertCount = p.VertsXyz.Length / 3;

            // Every early exit below fills this and appends it to r.Skips, so a prop that produced no
            // ramp is always named with the reason and the numbers that made it so.
            var skip = new Skip();
            skip.Prop = p.Name;

            // -------- pass 1: compact per-triangle data (centroid XZ, centroid Z, top surface height)
            var cx = new float[triCount];
            var cy = new float[triCount];
            var cz = new float[triCount];
            var topZ = new float[triCount];
            var compactOf = new int[triCount];
            int used = 0;
            float zMin = float.MaxValue, zMax = float.MinValue;
            for (int t = 0; t < triCount; t++)
            {
                compactOf[t] = -1;
                int i0 = p.Tris[t * 3 + 0], i1 = p.Tris[t * 3 + 1], i2 = p.Tris[t * 3 + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vertCount || i1 >= vertCount || i2 >= vertCount)
                    continue;
                float x = (p.VertsXyz[i0 * 3 + 0] + p.VertsXyz[i1 * 3 + 0] + p.VertsXyz[i2 * 3 + 0]) / 3f;
                float y = (p.VertsXyz[i0 * 3 + 1] + p.VertsXyz[i1 * 3 + 1] + p.VertsXyz[i2 * 3 + 1]) / 3f;
                float z = (p.VertsXyz[i0 * 3 + 2] + p.VertsXyz[i1 * 3 + 2] + p.VertsXyz[i2 * 3 + 2]) / 3f;
                float za = p.VertsXyz[i0 * 3 + 2], zb = p.VertsXyz[i1 * 3 + 2], zc = p.VertsXyz[i2 * 3 + 2];
                float tz = Math.Max(za, Math.Max(zb, zc));
                float bz = Math.Min(za, Math.Min(zb, zc));
                cx[used] = x;
                cy[used] = y;
                cz[used] = z;
                topZ[used] = tz;
                compactOf[t] = used;
                if (bz < zMin) zMin = bz;
                if (tz > zMax) zMax = tz;
                used++;
            }
            if (used == 0) return;
            skip.Tris = used;
            skip.ZMin = zMin;
            skip.ZMax = zMax;
            if (zMax - zMin < MinRiseMeters)
            {
                r.SkippedFlat++;
                skip.Reason = "z-range"; skip.Counter = 1;
                r.Skips.Add(skip);
                return;
            }

            // -------- the flight's horizontal direction (bottom band envelope centre -> top band's)
            float dirX, dirY;
            if (!FlightDirection(cx, cy, cz, topZ, used, zMin, zMax, out dirX, out dirY))
            {
                r.SkippedFlat++;
                skip.Reason = "no-direction"; skip.Counter = 1;
                r.Skips.Add(skip);
                return;
            }
            float perpDirX = -dirY, perpDirY = dirX;

            // -------- pass 2: per-triangle run projection window + perpendicular coordinate
            var winLo = new float[used];
            var winHi = new float[used];
            var perp = new float[used];
            float runMin = float.MaxValue, runMax = float.MinValue;
            for (int k = 0; k < used; k++) { winLo[k] = float.MaxValue; winHi[k] = float.MinValue; }
            for (int t = 0; t < triCount; t++)
            {
                int k = compactOf[t];
                if (k < 0) continue;
                int i0 = p.Tris[t * 3 + 0], i1 = p.Tris[t * 3 + 1], i2 = p.Tris[t * 3 + 2];
                float a = p.VertsXyz[i0 * 3 + 0] * dirX + p.VertsXyz[i0 * 3 + 1] * dirY;
                float b = p.VertsXyz[i1 * 3 + 0] * dirX + p.VertsXyz[i1 * 3 + 1] * dirY;
                float c = p.VertsXyz[i2 * 3 + 0] * dirX + p.VertsXyz[i2 * 3 + 1] * dirY;
                winLo[k] = Math.Min(a, Math.Min(b, c));
                winHi[k] = Math.Max(a, Math.Max(b, c));
                perp[k] = cx[k] * perpDirX + cy[k] * perpDirY;
                if (winLo[k] < runMin) runMin = winLo[k];
                if (winHi[k] > runMax) runMax = winHi[k];
            }
            float runSpan = runMax - runMin;
            skip.RunSpan = runSpan;
            if (runSpan < 1e-3f)
            {
                r.SkippedSteep++;   // perfectly vertical (a true wall): nothing to walk on
                skip.Reason = "vertical"; skip.Counter = 2;
                r.Skips.Add(skip);
                return;
            }

            // -------- run stations: is there collision at this station, and what is its top envelope?
            // The step adapts to the flight's length: a short ladder has rungs on a ~0.3 m pitch, and a
            // fixed 0.25 m step would miss the holes between them entirely.
            float step = Math.Min(StationStepMeters, Math.Max(0.1f, runSpan / 32f));
            int stationCount = (int)Math.Floor(runSpan / step) + 1;
            skip.Stations = stationCount;
            if (stationCount < 2)
            {
                r.SkippedFlat++;
                skip.Reason = "too-short"; skip.Counter = 1;
                r.Skips.Add(skip);
                return;
            }
            if (stationCount > MaxStations)
            {
                stationCount = MaxStations;
                step = runSpan / (stationCount - 1);
            }
            var stOk = new bool[stationCount];
            var stZ = new float[stationCount];
            for (int i = 0; i < stationCount; i++) stZ[i] = float.MinValue;
            for (int k = 0; k < used; k++)
            {
                if (winLo[k] > winHi[k]) continue;
                int a = StationIndex(winLo[k], runMin, step, stationCount);
                int b = StationIndex(winHi[k], runMin, step, stationCount);
                for (int i = a; i <= b; i++)
                {
                    stOk[i] = true;
                    if (topZ[k] > stZ[i]) stZ[i] = topZ[k];
                }
            }

            // -------- envelope centre (XZ) per station, so the ramp follows the flight's own centre line
            var stSX = new double[stationCount];
            var stSY = new double[stationCount];
            var stN = new int[stationCount];
            for (int k = 0; k < used; k++)
            {
                if (winLo[k] > winHi[k]) continue;
                int a = StationIndex(winLo[k], runMin, step, stationCount);
                int b = StationIndex(winHi[k], runMin, step, stationCount);
                for (int i = a; i <= b; i++)
                {
                    if (!stOk[i]) continue;
                    if (topZ[k] < stZ[i] - EnvelopeTolerance) continue;
                    stSX[i] += cx[k];
                    stSY[i] += cy[k];
                    stN[i]++;
                }
            }
            float cX = 0, cY = 0;
            for (int k = 0; k < used; k++) { cX += cx[k]; cY += cy[k]; }
            cX /= used;
            cY /= used;
            float cProj = cX * dirX + cY * dirY;
            var stX = new float[stationCount];
            var stY = new float[stationCount];
            for (int i = 0; i < stationCount; i++)
            {
                if (stN[i] > 0)
                {
                    stX[i] = (float)(stSX[i] / stN[i]);
                    stY[i] = (float)(stSY[i] / stN[i]);
                }
                else
                {
                    // Fall back to the run line through the prop's centroid (keeps the chain walkable).
                    float proj = runMin + i * step;
                    stX[i] = cX + dirX * (proj - cProj);
                    stY[i] = cY + dirY * (proj - cProj);
                }
            }

            // -------- supported stations, bottom to top
            var sup = new List<int>();
            for (int i = 0; i < stationCount; i++)
                if (stOk[i]) sup.Add(i);
            if (sup.Count < 2)
            {
                r.SkippedFlat++;
                skip.Supported = sup.Count;
                skip.Reason = "no-support"; skip.Counter = 1;
                r.Skips.Add(skip);
                return;
            }
            if (stZ[sup[sup.Count - 1]] - stZ[sup[0]] < MinRiseMeters)
            {
                r.SkippedFlat++;
                skip.Supported = sup.Count;
                skip.Reason = "no-rise"; skip.Counter = 1;
                r.Skips.Add(skip);
                // A ladder's plank spans the whole run, so every station carries the plank's global top
                // and the envelope cannot rise. It is still a straight climbable plane: fall back to one
                // chain over its own footprint (UndoSkip removes this skip when that succeeds).
                if (TryLadderChain(r, p, perp, winLo, winHi, cx, cy, cz, topZ, used, stationCount,
                        runMin, runSpan, zMin, zMax, slopeCapDegrees, cellSizeMeters, skip))
                    return;
                return;
            }

            // -------- holes: maximal stretches of the run where the prop supplies NO collision. A ramp
            // chunk bridges exactly one hole - from the last supported station before it to the first
            // supported station after it - so a ramp can never hover over an existing tread. Where the
            // prop does have geometry the treads themselves are the walkable path (the builder exempts a
            // stepped prop's own triangles from the ledge trim).
            var starts = new List<int>();
            var ends = new List<int>();
            int lastSup = -1;
            int holes = 0;
            // Hole refusals, per reason, with the worst (steepest / longest / tallest) example, so a prop
            // that ends up with no bridge can say exactly why.
            int holeSteep = 0, holeLong = 0, holeRise = 0, holeShort = 0, holeWithinClimb = 0;
            float stRun = 0f, stRise = 0f, stDeg = 0f;
            float loRun = 0f, loRise = 0f;
            float riRun = 0f, riRise = 0f;
            for (int i = 0; i < stationCount; i++)
            {
                if (stOk[i]) { lastSup = i; continue; }
                while (i < stationCount && !stOk[i]) i++;
                if (lastSup < 0 || i >= stationCount) continue;   // a hole reaching the run's own end
                holes++;
                // Bridge one station PAST each side so the ramp overlaps the geometry it lands on (that
                // overlap is what merges the spans); the station grid quantises the gap, so the extra
                // station also keeps the slope test honest.
                int bStart = lastSup > 0 ? lastSup - 1 : lastSup;
                int bEnd = i < stationCount - 1 ? i + 1 : i;
                float gapRun = (bEnd - bStart) * step;
                float gapRise = stZ[bEnd] - stZ[bStart];
                if (gapRise <= climbMeters + 1e-3f)
                {
                    // Within a step: ordinary rasterisation already walks over it, no ramp needed.
                    holeWithinClimb++;
                    continue;
                }
                if (gapRun > MaxBridgeRunMeters + 1e-3f)
                {
                    r.SkippedLong++;
                    holeLong++;
                    if (gapRun > loRun) { loRun = gapRun; loRise = gapRise; }
                    continue;
                }
                if (gapRise < MinChunkRiseMeters || gapRun < 1e-3f
                    || gapRise > MaxRisePerRampMeters + 1e-3f)
                {
                    r.SkippedSteep++;
                    if (gapRise > MaxRisePerRampMeters + 1e-3f)
                    {
                        holeRise++;
                        if (gapRise > riRise) { riRise = gapRise; riRun = gapRun; }
                    }
                    else
                    {
                        holeShort++;
                    }
                    continue;
                }
                float gapDeg = (float)(Math.Atan2(gapRise, gapRun) * 180.0 / Math.PI);
                if (gapDeg > slopeCapDegrees)
                {
                    r.SkippedSteep++;
                    holeSteep++;
                    if (gapDeg > stDeg) { stDeg = gapDeg; stRun = gapRun; stRise = gapRise; }
                    continue;
                }
                starts.Add(bStart);
                ends.Add(bEnd);
            }
            r.Holes += holes;
            skip.Holes = holes;
            if (starts.Count == 0)
            {
                // No hole to bridge (or every hole was refused): the prop's own, now ledge-exempt treads
                // are the path. This is NOT a skip - reporting it as one hid whole flights in V3.11.
                r.NoHoles++;
                skip.Counter = 5;
                if (holeSteep > 0)
                {
                    skip.Reason = "hole-steep"; skip.HoleRun = stRun; skip.HoleRise = stRise;
                    skip.HoleSlopeDeg = stDeg; skip.HoleCount = holeSteep;
                }
                else if (holeRise > 0)
                {
                    skip.Reason = "hole-rise"; skip.HoleRun = riRun; skip.HoleRise = riRise;
                    skip.HoleSlopeDeg = (float)(Math.Atan2(riRise, riRun) * 180.0 / Math.PI);
                    skip.HoleCount = holeRise;
                }
                else if (holeLong > 0)
                {
                    skip.Reason = "hole-long"; skip.HoleRun = loRun; skip.HoleRise = loRise;
                    skip.HoleSlopeDeg = (float)(Math.Atan2(loRise, loRun) * 180.0 / Math.PI);
                    skip.HoleCount = holeLong;
                }
                else if (holeShort > 0) { skip.Reason = "hole-short"; skip.HoleCount = holeShort; }
                else if (holes == 0) skip.Reason = "no-holes";
                else skip.Reason = "within-climb";   // every hole is steppable: treads are the path
                r.Skips.Add(skip);
                // Same ladder fallback: a ladder whose rungs/holes are all steppable still has no
                // widened, erosion-proof path of its own.
                if (TryLadderChain(r, p, perp, winLo, winHi, cx, cy, cz, topZ, used, stationCount,
                        runMin, runSpan, zMin, zMax, slopeCapDegrees, cellSizeMeters, skip))
                    return;
                return;
            }
            if (starts.Count > MaxRampsPerProp)
            {
                r.Capped++;
                skip.Reason = "capped";
                skip.Counter = 4;
                r.Skips.Add(skip);
                return;
            }

            // -------- apron reach: the pads flatten the flight to its floor / landing level so their
            // spans merge with the geometry the prop stands on, but they are forced walkable, so they
            // must not reach past the prop's OWN footprint. A pad poking further would sit inside
            // whatever the prop leans on and, because forced spans survive the ledge trim, it would
            // tunnel through a wall (or a thin door) at the flight's landing level. The prop's own
            // footprint at the bottom / top bands is what bounds it; half a cell of overshoot is kept
            // so a cell straddling the footprint edge still receives a span (the merge is by cell
            // adjacency within walkableClimb, so no bigger overlap is needed).
            float botFootprint = float.MaxValue, topFootprint = float.MinValue;
            for (int k = 0; k < used; k++)
            {
                if (topZ[k] <= zMin + EnvelopeTolerance && winLo[k] < botFootprint) botFootprint = winLo[k];
                if (topZ[k] >= zMax - EnvelopeTolerance && winHi[k] > topFootprint) topFootprint = winHi[k];
            }
            if (botFootprint == float.MaxValue) botFootprint = runMin;
            if (topFootprint == float.MinValue) topFootprint = runMax;
            float halfCell = 0.5f * cellSizeMeters;

            float botReach = starts[0] * step + ApronMeters;
            float botAllowed = Math.Max(0f, runMin - botFootprint) + halfCell;
            if (botReach > starts[0] * step + botAllowed) botReach = starts[0] * step + botAllowed;

            float runEnd = runMin + (stationCount - 1) * step;
            float topGap = (stationCount - 1 - ends[ends.Count - 1]) * step;
            float topReach = topGap + ApronMeters;
            float topAllowed = Math.Max(0f, topFootprint - runEnd) + halfCell;
            if (topReach > topGap + topAllowed) topReach = topGap + topAllowed;

            var keys = new List<string>();
            int emitted = 0;
            int steep0 = r.SkippedSteep, long0 = r.SkippedLong;
            for (int c = 0; c < starts.Count; c++)
            {
                bool first = c == 0;
                bool last = c == starts.Count - 1;
                if (EmitChunk(r, p.Name, perp, winLo, winHi, stX, stY, stZ, starts[c], ends[c],
                    runMin, step, zMin, zMax, botReach, topReach, first, last, keys, slopeCapDegrees))
                    emitted++;
            }
            if (emitted > 0)
            {
                if ((p.Flags & FlagHasSteps) != 0) r.Stairs++;
                if ((p.Flags & FlagLadder) != 0) r.Ladders++;
            }
            else if (r.SkippedSteep == steep0 && r.SkippedLong == long0)
            {
                r.SkippedFlat++;   // no chunk even reached the slope / run guards
            }
            if (emitted == 0)
            {
                skip.Reason = "chunk-refused";
                skip.HoleCount = starts.Count;
                r.Skips.Add(skip);
            }
        }

        // The flight's horizontal direction, from the band envelope centres of its lowest and highest
        // occupied 0.5 m bands. False when the prop has no usable rise. Also returns those two centres
        // (the flight's own bottom and top), which a ladder chain needs as its endpoints.
        private static bool FlightDirection(float[] cx, float[] cy, float[] cz, float[] topZ, int used,
            float zMin, float zMax, out float dirX, out float dirY)
        {
            dirX = 0f;
            dirY = 0f;
            int bandCount = (int)Math.Floor((zMax - zMin) / LayerHeightMeters) + 1;
            if (bandCount < 2) bandCount = 2;
            if (bandCount > 8192) bandCount = 8192;
            var bandZ = new float[bandCount];
            for (int b = 0; b < bandCount; b++) bandZ[b] = float.MinValue;
            for (int k = 0; k < used; k++)
            {
                int b = BandOf(cz[k], zMin, bandCount);
                if (topZ[k] > bandZ[b]) bandZ[b] = topZ[k];
            }
            int bandLo = -1, bandHi = -1;
            for (int b = 0; b < bandCount; b++)
            {
                if (bandZ[b] == float.MinValue) continue;
                if (bandLo < 0) bandLo = b;
                bandHi = b;
            }
            if (bandLo < 0 || bandHi <= bandLo) return false;
            if (bandZ[bandHi] - bandZ[bandLo] < MinRiseMeters) return false;

            float lx = 0, ly = 0, hx = 0, hy = 0;
            int ln = 0, hn = 0;
            for (int k = 0; k < used; k++)
            {
                int b = BandOf(cz[k], zMin, bandCount);
                if (b == bandLo && topZ[k] >= bandZ[bandLo] - EnvelopeTolerance) { lx += cx[k]; ly += cy[k]; ln++; }
                if (b == bandHi && topZ[k] >= bandZ[bandHi] - EnvelopeTolerance) { hx += cx[k]; hy += cy[k]; hn++; }
            }
            if (ln == 0 || hn == 0) return false;
            float dX = hx / hn - lx / ln;
            float dY = hy / hn - ly / ln;
            float len = (float)Math.Sqrt((double)dX * dX + (double)dY * dY);
            if (len < 1e-3f) return false;
            dirX = dX / len;
            dirY = dY / len;
            return true;
        }

        // The envelope centres of the prop's lowest and highest occupied 0.5 m bands: the flight's own
        // bottom and top points. Same bands FlightDirection uses for the run direction.
        private static bool BandCentres(float[] cx, float[] cy, float[] cz, float[] topZ, int used,
            float zMin, float zMax, out float bx, out float by, out float tx, out float ty)
        {
            bx = 0f; by = 0f; tx = 0f; ty = 0f;
            int bandCount = (int)Math.Floor((zMax - zMin) / LayerHeightMeters) + 1;
            if (bandCount < 2) bandCount = 2;
            if (bandCount > 8192) bandCount = 8192;
            var bandZ = new float[bandCount];
            for (int b = 0; b < bandCount; b++) bandZ[b] = float.MinValue;
            for (int k = 0; k < used; k++)
            {
                int b = BandOf(cz[k], zMin, bandCount);
                if (topZ[k] > bandZ[b]) bandZ[b] = topZ[k];
            }
            int bandLo = -1, bandHi = -1;
            for (int b = 0; b < bandCount; b++)
            {
                if (bandZ[b] == float.MinValue) continue;
                if (bandLo < 0) bandLo = b;
                bandHi = b;
            }
            if (bandLo < 0 || bandHi <= bandLo) return false;
            float lx = 0, ly = 0, hx = 0, hy = 0;
            int ln = 0, hn = 0;
            for (int k = 0; k < used; k++)
            {
                int b = BandOf(cz[k], zMin, bandCount);
                if (b == bandLo && topZ[k] >= bandZ[bandLo] - EnvelopeTolerance) { lx += cx[k]; ly += cy[k]; ln++; }
                if (b == bandHi && topZ[k] >= bandZ[bandHi] - EnvelopeTolerance) { hx += cx[k]; hy += cy[k]; hn++; }
            }
            if (ln == 0 || hn == 0) return false;
            bx = lx / ln; by = ly / ln;
            tx = hx / hn; ty = hy / hn;
            return true;
        }
        private static int BandOf(float z, float zMin, int bandCount)
        {
            int b = (int)((z - zMin) / LayerHeightMeters);
            if (b < 0) b = 0;
            if (b >= bandCount) b = bandCount - 1;
            return b;
        }

        private static int StationIndex(float proj, float runMin, float step, int stationCount)
        {
            int i = step > 0f ? (int)((proj - runMin) / step) : 0;
            if (i < 0) i = 0;
            if (i >= stationCount) i = stationCount - 1;
            return i;
        }

        // Emits one chunk (plus an end apron on the first / last chunk). Returns true when the sloped
        // ramp quad itself was emitted.
        private static bool EmitChunk(Result r, string propName, float[] perp, float[] winLo, float[] winHi,
            float[] stX, float[] stY, float[] stZ, int sb, int eb, float runMin, float step,
            float floorZ, float ceilZ, float botReach, float topReach, bool first, bool last,
            List<string> keys, float slopeCapDegrees)
        {
            float bX = stX[sb], bY = stY[sb], bZ = stZ[sb];
            float tX = stX[eb], tY = stY[eb], tZ = stZ[eb];
            float rise = tZ - bZ;
            float dX = tX - bX, dY = tY - bY;
            float run = (float)Math.Sqrt((double)dX * dX + (double)dY * dY);
            if (rise < MinChunkRiseMeters || run < 1e-3f)
                return false;                       // a degenerate chunk (a single station): nothing to emit
            if (run > MaxRunPerRampMeters + 1e-3f)
            {
                r.SkippedLong++;
                return false;
            }
            if (Math.Atan2(rise, run) * 180.0 / Math.PI > slopeCapDegrees)
            {
                r.SkippedSteep++;
                return false;
            }

            float perpX = -dY / run, perpY = dX / run;
            float wB = EndSpread(perp, winLo, winHi, runMin + sb * step);
            float wT = EndSpread(perp, winLo, winHi, runMin + eb * step);
            float w;
            if (wB > 0.02f && wT > 0.02f) w = Math.Min(wB, wT);
            else if (wB > 0.02f) w = wB;
            else if (wT > 0.02f) w = wT;
            else w = MinWidthMeters / WidthFactor;
            float width = Clamp(WidthFactor * w, MinWidthMeters, MaxWidthMeters);
            float hw = width * 0.5f;
            float hx = perpX * hw, hy = perpY * hw;

            string key = Round(dX) + "," + Round(dY) + "," + Round(rise) + "," + Round(width)
                + "," + Round(bX) + "," + Round(bY);
            for (int i = 0; i < keys.Count; i++)
                if (keys[i] == key) return false;
            keys.Add(key);

            float rampDX = dX / run, rampDY = dY / run;
            if (first && botReach > 1e-3f)
            {
                // Apron at the flight's own bottom level, reaching ApronMeters past the flight's
                // footprint edge. Landing at zMin (not at the first station's envelope) is what makes the
                // apron share a span with the FLOOR: the ramp meets solid ground where the agent stands.
                AppendQuad(r, bX - rampDX * botReach, bY - rampDY * botReach, floorZ, bX, bY, bZ, hx, hy);
            }
            AppendQuad(r, bX, bY, bZ, tX, tY, tZ, hx, hy);
            if (last && topReach > 1e-3f)
            {
                // Likewise the top apron sits on the landing level (the flight's own top).
                AppendQuad(r, tX, tY, tZ, tX + rampDX * topReach, tY + rampDY * topReach, ceilZ, hx, hy);
            }

            var info = new RampInfo();
            info.Prop = propName;
            info.BottomY = bZ;
            info.TopY = tZ;
            info.Run = run;
            info.Width = width;
            info.SlopeDeg = (float)(Math.Atan2(rise, run) * 180.0 / Math.PI);
            r.Infos.Add(info);
            r.Ramps++;
            return true;
        }

        // Perpendicular extent of the triangles that cover one run position.
        private static float EndSpread(float[] perp, float[] winLo, float[] winHi, float proj)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int k = 0; k < winLo.Length; k++)
            {
                if (winLo[k] > proj || winHi[k] < proj) continue;
                float p = perp[k];
                if (p < lo) lo = p;
                if (p > hi) hi = p;
            }
            if (lo > hi) return 0f;
            return hi - lo;
        }

        // One flat-or-sloped quad (2 triangles) between two centre-line points, of the given half-width
        // perpendicular offset. Engine Z-up; the Recast mixer normalises the winding per triangle.
        private static void AppendQuad(Result r, float x0, float y0, float z0, float x1, float y1,
            float z1, float hx, float hy)
        {
            int b = r.VertsXyz.Count / 3;
            r.VertsXyz.Add(x0 + hx); r.VertsXyz.Add(y0 + hy); r.VertsXyz.Add(z0);
            r.VertsXyz.Add(x0 - hx); r.VertsXyz.Add(y0 - hy); r.VertsXyz.Add(z0);
            r.VertsXyz.Add(x1 - hx); r.VertsXyz.Add(y1 - hy); r.VertsXyz.Add(z1);
            r.VertsXyz.Add(x1 + hx); r.VertsXyz.Add(y1 + hy); r.VertsXyz.Add(z1);
            r.Tris.Add(b + 0); r.Tris.Add(b + 1); r.Tris.Add(b + 2);
            r.Tris.Add(b + 0); r.Tris.Add(b + 2); r.Tris.Add(b + 3);
        }

        private static float Clamp(float v, float lo, float hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static string Round(float v)
        {
            return ((float)Math.Round(v / DedupeQuantum) * DedupeQuantum).ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
