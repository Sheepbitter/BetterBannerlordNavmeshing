using System;
using System.Collections.Generic;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Stepped / ladder prop ramp synthesis (PURE: no TaleWorlds types)
    // ==================================================================
    // Thin stepped collision props are the one class of geometry the voxel core cannot represent
    // faithfully. A wooden flight's boards or a ladder's rungs are thinner than a voxel in places, so
    // the rasterizer merges a whole flight into a coarse surface whose walkable top sits well above
    // the authored treads (measured on Birtu: the mesh rode 0.9-1.9 m above the treads of every
    // european_castle_ladder flight), and where no span survives the erosion / region-minimum / ledge
    // filters the flight carries no navmesh at all. The player sees a bare flight and the agent cannot
    // cross it.
    //
    // The fix is to give such a prop one honest walkable surface: a synthesized ramp from the flight's
    // bottom anchor to its top anchor, with a short apron past each end so the ramp merges with the
    // floor below and the landing above. The ramp is emitted as ordinary triangles into the same
    // collision soup, so winding normalisation, rasterisation, stitching and the detail mesh all treat
    // it like any other surface. One property is special: ramp triangles are FORCED walkable (area
    // RC_WALKABLE_AREA) even when their slope exceeds MaxSlopeDegrees, because a stair or a ladder is
    // meant to be traversed.
    //
    // Pure data in / data out on purpose: the editor collector and the offline harness/snapshot path
    // both call it, so a recorded snapshot validates the shipped behaviour without re-dumping.
    public static class SteppedPropRamps
    {
        // Mirrors TaleWorlds.Engine.BodyFlags (kept as ints so this file stays engine-free).
        public const int FlagLadder = 0x00000040;
        public const int FlagHasSteps = 0x00000800;

        // ---- tunables (documented in the README under "Stairs & ladders") --------------------------
        // Height layer used to find a flight's two ends and to split tall flights into chunks.
        public const float LayerHeightMeters = 0.5f;
        // A prop whose walkable height span is below this is not a flight: no ramp.
        public const float MinRiseMeters = 0.4f;
        // Above this implied slope the prop is a wall or a truly vertical ladder: no ramp.
        public const float MaxSlopeDegrees = 75f;
        // The floor is the width a ramp needs to survive Recast's ledge filter (which trims a span
        // next to a drop) plus erosion: walkableClimb + 2 x agent radius ~ 1.1 m, hence 1.2 m. A narrow
        // ramp measures 0% covered on Birtu, purely because nothing of it survives.
        // Ramp width = WidthFactor x the narrower end footprint, clamped to [MinWidth,MaxWidth].
        public const float MinWidthMeters = 1.2f;
        public const float MaxWidthMeters = 1.2f;
        public const float WidthFactor = 0.6f;
        // Horizontal overhang past each anchor: the ramp's ends reach the floor / landing levels a
        // little past the anchors instead of stopping on the first and last tread.
        public const float ApronMeters = 0.4f;
        // A tall flight (a tower stairwell, a long gatehouse stair) is split into chained sub-ramps that
        // each obey the slope rule. Needing more chunks than this is reported as capped and skipped.
        public const int MaxRampsPerProp = 4;
        // Rise a single ramp may span. A ramp is a plane, so it only stays faithful to the collision
        // Is one height layer (0.5 m) plus one more, so a chained ramp HUGS the flight: a longer
        // chord would hover over the lower treads (making them a low ceiling that FilterWalkableLowHeightSpans deletes) and would leave navmesh nothing to stand on where the collision is thin. Measured on Birtu: 1 m chunks keep the mesh within 4 faces of the no-ramp build, 4 m chords changed it by thousands.
        // plane (it would bridge over its own landings). Chaining and MaxRampsPerProp bound how far this
        // may go; past that the prop is skipped rather than misrepresented.
        public const float MaxRisePerRampMeters = 1.0f;
        // Horizontal run a single ramp may span. A flight that needs more than this for a <= 4 m rise is
        // a walkway or a curved ramp the base geometry already describes, and a prop whose collision
        // carries detached far-away triangles would otherwise produce an absurd bridge across the map.
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
            public int Ramps;          // ramp quads emitted
            public int Stairs;         // HasSteps props that produced at least one ramp
            public int Ladders;        // Ladder props that produced at least one ramp
            public int SkippedFlat;    // rise < MinRiseMeters
            public int SkippedSteep;   // implied slope > MaxSlopeDegrees at every chunk count
            public int SkippedLong;    // implied run > MaxRunPerRampMeters
            public int Capped;         // best chunk count hit MaxRampsPerProp
            public readonly List<RampInfo> Infos = new List<RampInfo>();
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
            return "layer=" + LayerHeightMeters + "m minRise=" + MinRiseMeters + "m maxSlope="
                + MaxSlopeDegrees + "deg width=" + MinWidthMeters + ".." + MaxWidthMeters + "m x"
                + WidthFactor + " apron=" + ApronMeters + "m maxRamps=" + MaxRampsPerProp
                + " maxRisePerRamp=" + MaxRisePerRampMeters + "m maxRunPerRamp=" + MaxRunPerRampMeters + "m";
        }

        // Deterministic: no dictionary iteration order reaches the output.
        public static Result Synthesize(IReadOnlyList<Prop> props)
        {
            var r = new Result();
            if (props == null) return r;
            for (int i = 0; i < props.Count; i++) AppendProp(props[i], r);
            return r;
        }

        private static void AppendProp(Prop p, Result r)
        {
            if (p == null || p.VertsXyz == null || p.Tris == null) return;
            if (!IsSteppedOrLadder(p.Flags)) return;
            int triCount = p.Tris.Length / 3;
            if (triCount <= 0) return;

            // -------- per-triangle centroids (all the anchors need) and the prop's height span
            var cx = new float[triCount];
            var cy = new float[triCount];   // engine Y (horizontal)
            var cz = new float[triCount];   // engine Z (up)
            int used = 0;
            float zMin = float.MaxValue, zMax = float.MinValue;
            int vertCount = p.VertsXyz.Length / 3;
            for (int t = 0; t < triCount; t++)
            {
                int i0 = p.Tris[t * 3 + 0], i1 = p.Tris[t * 3 + 1], i2 = p.Tris[t * 3 + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vertCount || i1 >= vertCount || i2 >= vertCount)
                    continue;
                float x = (p.VertsXyz[i0 * 3 + 0] + p.VertsXyz[i1 * 3 + 0] + p.VertsXyz[i2 * 3 + 0]) / 3f;
                float y = (p.VertsXyz[i0 * 3 + 1] + p.VertsXyz[i1 * 3 + 1] + p.VertsXyz[i2 * 3 + 1]) / 3f;
                float z = (p.VertsXyz[i0 * 3 + 2] + p.VertsXyz[i1 * 3 + 2] + p.VertsXyz[i2 * 3 + 2]) / 3f;
                cx[used] = x;
                cy[used] = y;
                cz[used] = z;
                if (z < zMin) zMin = z;
                if (z > zMax) zMax = z;
                used++;
            }
            if (used == 0) return;
            if (zMax - zMin < MinRiseMeters)
            {
                r.SkippedFlat++;
                return;
            }

            // -------- height layers; each layer keeps its triangle indices so the end footprints can be
            // measured exactly (perpendicular spread) once the ramp direction is known.
            int layerCount = (int)Math.Floor((zMax - zMin) / LayerHeightMeters) + 1;
            if (layerCount < 2) layerCount = 2;
            if (layerCount > 8192) layerCount = 8192;
            var layers = new List<int>[layerCount];
            var lx = new double[layerCount];
            var ly = new double[layerCount];
            var lz = new double[layerCount];
            for (int k = 0; k < used; k++)
            {
                int li = (int)((cz[k] - zMin) / LayerHeightMeters);
                if (li < 0) li = 0;
                if (li >= layerCount) li = layerCount - 1;
                if (layers[li] == null) layers[li] = new List<int>();
                layers[li].Add(k);
                lx[li] += cx[k];
                ly[li] += cy[k];
                lz[li] += cz[k];
            }

            var occ = new List<int>();
            for (int k = 0; k < layerCount; k++)
                if (layers[k] != null) occ.Add(k);
            if (occ.Count < 2)
            {
                r.SkippedFlat++;
                return;
            }

            float zBot = AnchorZ(lz, layers, occ[0]);
            float zTop = AnchorZ(lz, layers, occ[occ.Count - 1]);
            if (zTop - zBot < MinRiseMeters)
            {
                r.SkippedFlat++;
                return;
            }

            // -------- the fewest chained sub-ramps that each obey the slope rule
            int chunks = 0;
            for (int n = 1; n <= MaxRampsPerProp; n++)
            {
                if (AllChunksWithinSlope(occ, lx, ly, lz, layers, n)) { chunks = n; break; }
            }
            if (chunks == 0)
            {
                if (WholeRun(lx, ly, layers, occ) > MaxRunPerRampMeters + 1e-3f) r.SkippedLong++;
                else r.SkippedSteep++;
                return;
            }
            if (chunks == MaxRampsPerProp) r.Capped++;

            var keys = new List<string>();
            int emitted = 0;
            int prev = occ[0];
            for (int c = 1; c <= chunks; c++)
            {
                int idx = (c == chunks)
                    ? occ[occ.Count - 1]
                    : occ[(int)Math.Round((double)c * (occ.Count - 1) / chunks)];
                if (idx <= prev) { prev = idx; continue; }
                if (AppendRamp(r, p.Name, cx, cy, cz, lx, ly, lz, layers, prev, idx, keys)) emitted++;
                prev = idx;
            }

            if (emitted > 0)
            {
                if ((p.Flags & FlagHasSteps) != 0) r.Stairs++;
                if ((p.Flags & FlagLadder) != 0) r.Ladders++;
            }
        }

        private static float AnchorX(double[] lx, List<int>[] layers, int k)
        {
            return (float)(lx[k] / layers[k].Count);
        }

        private static float AnchorY(double[] ly, List<int>[] layers, int k)
        {
            return (float)(ly[k] / layers[k].Count);
        }

        private static float AnchorZ(double[] lz, List<int>[] layers, int k)
        {
            return (float)(lz[k] / layers[k].Count);
        }

        // Horizontal (engine X/Y) distance between two layer anchors.
        private static float RunBetween(double[] lx, double[] ly, List<int>[] layers, int ka, int kb)
        {
            float dx = AnchorX(lx, layers, ka) - AnchorX(lx, layers, kb);
            float dy = AnchorY(ly, layers, ka) - AnchorY(ly, layers, kb);
            return (float)Math.Sqrt((double)dx * dx + (double)dy * dy);
        }

        // Horizontal distance between the flight's two extreme layer anchors.
        private static float WholeRun(double[] lx, double[] ly, List<int>[] layers, List<int> occ)
        {
            return RunBetween(lx, ly, layers, occ[0], occ[occ.Count - 1]);
        }

        // Every chunk of an n-way split (by occupied-layer index, i.e. evenly across the flight's
        // layers) must have a real rise and stay within the slope rule.
        private static bool AllChunksWithinSlope(List<int> occ, double[] lx, double[] ly, double[] lz,
            List<int>[] layers, int n)
        {
            int prev = occ[0];
            for (int c = 1; c <= n; c++)
            {
                int idx = (c == n)
                    ? occ[occ.Count - 1]
                    : occ[(int)Math.Round((double)c * (occ.Count - 1) / n)];
                if (idx <= prev) { prev = idx; continue; }
                float rise = AnchorZ(lz, layers, idx) - AnchorZ(lz, layers, prev);
                float run = RunBetween(lx, ly, layers, prev, idx);
                if (rise < MinChunkRiseMeters || run < 1e-3f) return false;
                if (rise > MaxRisePerRampMeters + 1e-3f) return false;
                if (run > MaxRunPerRampMeters + 1e-3f) return false;
                if (Math.Atan2(rise, run) * 180.0 / Math.PI > MaxSlopeDegrees) return false;
                prev = idx;
            }
            return true;
        }

        // One planar ramp quad (2 triangles) from layer Kb up to layer Kt, with the per-prop dedupe.
        // Engine axes: X and Y are horizontal, Z is up.
        private static bool AppendRamp(Result r, string propName, float[] cx, float[] cy, float[] cz,
            double[] lx, double[] ly, double[] lz, List<int>[] layers, int ka, int kb, List<string> keys)
        {
            float bX = AnchorX(lx, layers, ka), bY = AnchorY(ly, layers, ka), bZ = AnchorZ(lz, layers, ka);
            float tX = AnchorX(lx, layers, kb), tY = AnchorY(ly, layers, kb), tZ = AnchorZ(lz, layers, kb);
            float rise = tZ - bZ;
            float dX = tX - bX, dY = tY - bY;
            float run = (float)Math.Sqrt((double)dX * dX + (double)dY * dY);
            if (rise < MinChunkRiseMeters || run < 1e-3f) return false;
            if (run > MaxRunPerRampMeters + 1e-3f) return false;
            if (Math.Atan2(rise, run) * 180.0 / Math.PI > MaxSlopeDegrees) return false;

            float dirX = dX / run, dirY = dY / run;
            float perpX = -dirY, perpY = dirX;

            // Ramp width = WidthFactor x the NARROWER end footprint (perpendicular extent of that
            // layer's triangle centroids), clamped. A single-triangle end has no spread of its own, so
            // the other end (then the clamped floor) is used.
            float wB = Spread(cx, cy, layers[ka], bX, bY, perpX, perpY);
            float wT = Spread(cx, cy, layers[kb], tX, tY, perpX, perpY);
            float w;
            if (wB > 0.02f && wT > 0.02f) w = Math.Min(wB, wT);
            else if (wB > 0.02f) w = wB;
            else if (wT > 0.02f) w = wT;
            else w = MinWidthMeters / WidthFactor;
            float width = Clamp(WidthFactor * w, MinWidthMeters, MaxWidthMeters);

            // Three quads: a flat apron at the bottom level, the sloped ramp exactly through the two
            // anchors, and a flat apron at the top level. The aprons are what let the ramp meet the
            // floor and the landing; keeping them FLAT (instead of extending the sloped plane) keeps the
            // ramp itself on the flight's chord, so it merges with the treads instead of hovering over
            // the lower ones and acting as a ceiling (which would delete their navmesh).
            float sX = bX - dirX * ApronMeters, sY = bY - dirY * ApronMeters;
            float eX = tX + dirX * ApronMeters, eY = tY + dirY * ApronMeters;
            float hw = width * 0.5f;
            float hx = perpX * hw, hy = perpY * hw;

            string key = Round(dX) + "," + Round(dY) + "," + Round(rise) + "," + Round(width)
                + "," + Round(bX) + "," + Round(bY);
            for (int i = 0; i < keys.Count; i++)
                if (keys[i] == key) return false;
            keys.Add(key);

            AppendQuad(r, sX, sY, bZ, bX, bY, bZ, hx, hy);    // bottom apron (flat)
            AppendQuad(r, bX, bY, bZ, tX, tY, tZ, hx, hy);    // the ramp itself
            AppendQuad(r, tX, tY, tZ, eX, eY, tZ, hx, hy);    // top apron (flat)
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

        // Perpendicular extent of one layer's triangle centroids about that layer's anchor.
        private static float Spread(float[] cx, float[] cy, List<int> tris, float ax, float ay,
            float perpX, float perpY)
        {
            if (tris == null || tris.Count < 2) return 0f;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < tris.Count; i++)
            {
                int t = tris[i];
                float p = (cx[t] - ax) * perpX + (cy[t] - ay) * perpY;
                if (p < lo) lo = p;
                if (p > hi) hi = p;
            }
            return hi - lo;
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
