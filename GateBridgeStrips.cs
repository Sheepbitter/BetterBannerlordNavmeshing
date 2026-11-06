using System;
using System.Collections.Generic;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Gate / door bridge-strip synthesis (PURE: no TaleWorlds types)
    // ==================================================================
    // A castle gate / usable door usually drops its passage floor as a set of SEPARATE rigid collider
    // props (collider_l/r, extra_collider_l/r, collider_agent_l/r on CC_20's wall gate). Those slabs
    // stack a few tens of centimetres apart and their edge stops short of the ground beyond the
    // doorway. The voxel core represents the slabs as a proud flat plate: the plate's rim and the
    // slabs under it are more than an agent climb apart once quantised, so Recast's ledge trim (and the
    // erosion that follows it) eats the whole crossing and the direct path through the gate is a hole -
    // the agent must detour south. This is the same failure class SteppedPropRamps solves for stairs:
    // thin / multi-level collision the voxel grid cannot represent.
    //
    // The fix is a synthesized BRIDGE STRIP, modelled on SteppedPropRamps: find the gate/door passage
    // plate, find the first walkable surface beyond the gap it cannot reach, and emit a sloped quad
    // strip from the plate's edge to that surface. The strip is appended to the same collision soup as
    // ordinary geometry and FORCED walkable (like a ramp), so it survives the ledge trim even when its
    // slope exceeds MaxSlopeDegrees, and it is as wide as the plate edge run so it survives erosion. A
    // strip is only emitted across a genuine void - a surface that is already adjacent and inside the
    // climb is left alone, so no proud edges or stray islands are created.
    //
    // Name-free: the caller supplies the gate/door provenance (the scene tag `gate` carried as a
    // per-entity bit) plus, for snapshots that predate that bit, the generic flat-slab fallback. The
    // synthesis itself only looks at geometry and a surface probe.
    //
    // Pure data in / data out on purpose: the editor collector and the offline harness both call it,
    // so a recorded snapshot validates the shipped behaviour without re-dumping.
    public static class GateBridgeStrips
    {
        // Name suffix for the snapshot record that carries a plate's synthesized bridge strips. The
        // harness uses it to tell synthesized geometry from real collision and to avoid synthesizing
        // twice when a snapshot already contains strips.
        public const string GateNameSuffix = "#gate";

        // ---- tunables (documented in the README under "Gate bridge strips") -----------------------
        // Gate-tagged geometry is the gate mechanism, not the floor. Exclude the whole gate PREFAB
        // (all its child colliders, including the untagged collider_agent_* siblings) from the
        // walkable soup. The editor-cube floors / walls and the terrain are not gate-prefab children
        // and stay, so the ordinary navmesh follows the slope under the gate.
        public static bool IgnoreGateGeometry = true;
        // Synthesize the gate bridge strip? Off by default now: the gate plate is removed and the
        // ordinary navmesh over the underlying slope is the path. Keep the strip code for comparison.
        public static bool EmitStrips = false;
        // When on (default), the crossing strip is emitted as a station chain that rides the collision
        // surface under the gate instead of one straight quad from the plate edge to the target. On a
        // slope the chain follows the ground through the opening; the straight bridge is only used
        // across a genuine void. Set false to reproduce the old flat-ramp behaviour.
        public static bool ConformToGround = true;
        // Longest void a strip may bridge (a raster hole). Anything wider is open air, not a doorway.
        public const float MaxBridgeRunMeters = 2.0f;
        // How far BELOW the plate the target surface may be and still be bridged (a gentle ramp).
        // Conforming mode rides the ground, so it may follow a longer slope down; the straight-line
        // slope cap still applies.
        public const float MaxDropMeters = 1.30f;
        public const float MaxDropConformMeters = 4.0f;
        // How far ABOVE the plate the target may sit (a landing a hair higher).
        public const float MaxRiseMeters = 0.3f;
        // A gate sits at or just above the surface people walk on, so the conforming sampler only
        // looks DOWN from the plate (plus a hair for a threshold lip). It must never pick a wall top,
        // a ceiling, or an upper floor above the gate.
        public const float MaxRiseConformMeters = 0.3f;
        // A surface closer than this below the plate is already reachable by the ordinary rasteriser.
        public const float MinDropMeters = 0.5f;
        // The target is only accepted this far out, so the strip spans the whole void and lands on the
        // ground FACE rather than on the terrain right beside the plate (which sits below that face and
        // would drop a span under it).
        public const float MinGapMeters = 0.75f;
        // The target-side edge is pushed this far past the found surface (on top of Overlap) so the
        // strip overlaps the target's own built span.
        public const float TargetApronMeters = 1.2f;
        // Steepest strip worth emitting. The builder exempts forced (strip) spans from the ledge trim,
        // so only the per-cell climb rule binds: atan(climb/cs) = 67.4 deg at cs 0.25 / climb 0.6. This
        // is a tighter safety bound on top of that.
        public const float MaxStripSlopeDegrees = 67f;
        // The plate-side edge is pulled this far INTO the plate, and the target-side edge this far PAST
        // the target, so the strip overlaps both and the spans merge (the connection).
        public const float OverlapMeters = 0.15f;
        // The strip's plate edge is pulled this far into the plate (on top of Overlap) so it overlaps the
        // plate and the spans merge. It must stay small: a large inset puts the strip's DESCENT under the
        // plate, and the clearance filter then nulls the plate (the walkable surface) as a low ceiling.
        public const float PlateInsetMeters = 0.0f;
        // A flat apron at the plate's own level, this long, before the strip descends. Without it the
        // strip's first cell sits more than an agent climb below the plate, and the ledge trim then nulls
        // the plate's own edge cell as a ledge.
        public const float PlateApronMeters = 0.5f;
        // A plate triangle is up-facing and no steeper than this (the passage floor is roughly flat).
        public const float PlateFlatDegrees = 25f;
        // Safety bound on strips per plate cluster.
        public const int MaxStripsPerPlate = 24;
        // A run shorter than this is not worth emitting (it would erode away).
        private const float MinRunMeters = 0.25f;
        // Per-strip dedupe rounding.
        private const float DedupeQuantum = 0.05f;

        public sealed class Prop
        {
            public string Name;
            public int Flags;
            // Engine soup layout: 3 floats per vertex, (x, z, h) - index 2 is the HEIGHT (the same
            // layout SteppedPropRamps and the snapshot files use).
            public float[] VertsXyz;
            public int[] Tris;
        }

        // One emitted strip, for logging / diagnostics (never used by the geometry itself).
        public sealed class Info
        {
            public string Prop;
            public float Gap;       // void width bridged (m)
            public float Drop;      // plate height - target height (m)
            public float SlopeDeg;  // strip slope
            public float Width;     // strip width along the plate edge (m)
            public float Hp, Ht;    // plate / target heights (engine)
            public float Ex, Ez;    // plate-edge midpoint
            public float Tx, Tz;    // target midpoint
        }

        public sealed class Result
        {
            public readonly List<float> VertsXyz = new List<float>();
            public readonly List<int> Tris = new List<int>();
            public int Plates;      // plate clusters processed
            public int Strips;      // strip quads emitted
            public int Skips;       // runs refused (no target / too steep / blocked)
            public int Cells;       // plate cells rasterised
            public int PlateTris;   // up-facing plate triangles considered
            public readonly List<Info> Infos = new List<Info>();
        }

        // Diagnostic hook (null in production). The harness sets it to explain why a run emitted or not.
        public static Action<string> Trace;

        public static string Describe()
        {
            return "maxGap=" + MaxBridgeRunMeters + "m drop<= " + MaxDropMeters + "m rise<= "
                + MaxRiseMeters + "m slope<= " + MaxStripSlopeDegrees + "deg plateFlat<= "
                + PlateFlatDegrees + "deg overlap=" + OverlapMeters + "m";
        }

        // A gate/door PASSAGE FLOOR collider is a thin, rigid, mostly up-facing slab. The scene tag puts
        // the same provenance on the door leaves / frame (tall, not a floor), so the plate candidates are
        // the thin-slab subset of the gate-flagged props. Name-free: geometry only.
        public static bool IsThinSlab(Prop p)
        {
            if (p == null || p.VertsXyz == null || p.Tris == null) return false;
            int vc = p.VertsXyz.Length / 3;
            float hmin = float.MaxValue, hmax = float.MinValue;
            int up = 0;
            double area = 0.0;
            float cosFlat = (float)Math.Cos(20.0 * Math.PI / 180.0);
            for (int t = 0; t < p.Tris.Length / 3; t++)
            {
                int i0 = p.Tris[t * 3 + 0], i1 = p.Tris[t * 3 + 1], i2 = p.Tris[t * 3 + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vc || i1 >= vc || i2 >= vc) continue;
                float ax = VX(p.VertsXyz, i0), az = VZ(p.VertsXyz, i0), ah = VH(p.VertsXyz, i0);
                float bx = VX(p.VertsXyz, i1), bz = VZ(p.VertsXyz, i1), bh = VH(p.VertsXyz, i1);
                float cx = VX(p.VertsXyz, i2), cz = VZ(p.VertsXyz, i2), ch = VH(p.VertsXyz, i2);
                if (ah < hmin) hmin = ah; if (ah > hmax) hmax = ah;
                if (bh < hmin) hmin = bh; if (bh > hmax) hmax = bh;
                if (ch < hmin) hmin = ch; if (ch > hmax) hmax = ch;
                float ux = bx - ax, uz = bz - az, uh = bh - ah;
                float wx = cx - ax, wz = cz - az, wh = ch - ah;
                float nh = ux * wz - uz * wx;
                float len = (float)Math.Sqrt((uz * wh - uh * wz) * (uz * wh - uh * wz)
                    + (uh * wx - ux * wh) * (uh * wx - ux * wh) + nh * nh);
                if (len < 1e-9f || nh / len < cosFlat) continue;
                up++;
                area += 0.5 * Math.Abs((bx - ax) * (cz - az) - (bz - az) * (cx - ax));
            }
            if (up < 16) return false;
            if (hmax - hmin > 1.0f) return false;
            if (area < 4.0) return false;
            return true;
        }

        public static string DescribeInfo(Info i)
        {
            return "gate strip '" + i.Prop + "' gap=" + Num(i.Gap) + "m drop=" + Num(i.Drop)
                + "m slope=" + Num(i.SlopeDeg) + "deg width=" + Num(i.Width) + "m H=" + Num(i.Hp)
                + "->" + Num(i.Ht) + " at (" + Num(i.Tx) + "," + Num(i.Tz) + ")";
        }

        private static string Num(float v)
        {
            return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static float VX(float[] v, int i) { return v[i * 3 + 0]; }
        private static float VZ(float[] v, int i) { return v[i * 3 + 1]; }
        private static float VH(float[] v, int i) { return v[i * 3 + 2]; }

        // The normalized entry point. Deterministic: props are clustered in input order and each
        // cluster's geometry is emitted in a fixed scan order.
        public static Result Synthesize(IReadOnlyList<Prop> plates, float[] sceneVerts, int[] sceneTris,
            Func<float, float, float> terrainAt, float cellSizeMeters, float climbMeters, bool ledgeFilter)
        {
            var r = new Result();
            if (plates == null || plates.Count == 0) return r;
            List<List<Prop>> clusters = Cluster(plates);
            for (int c = 0; c < clusters.Count; c++)
                SynthesizeCluster(clusters[c], sceneVerts, sceneTris, terrainAt,
                    cellSizeMeters, climbMeters, ledgeFilter, r);
            return r;
        }

        // Groups plate props whose XZ footprints come within `MaxBridgeRun + 1 m` of each other, so
        // each gate/door is processed on its own small grid (a scene-wide grid would be both slow and
        // ambiguous). Union-find over bounding boxes, input order preserved.
        private static List<List<Prop>> Cluster(IReadOnlyList<Prop> plates)
        {
            int n = plates.Count;
            var x0 = new float[n]; var x1 = new float[n];
            var z0 = new float[n]; var z1 = new float[n];
            for (int i = 0; i < n; i++)
            {
                x0[i] = float.MaxValue; x1[i] = float.MinValue;
                z0[i] = float.MaxValue; z1[i] = float.MinValue;
                Prop p = plates[i];
                if (p == null || p.VertsXyz == null) continue;
                int vc = p.VertsXyz.Length / 3;
                for (int v = 0; v < vc; v++)
                {
                    float x = VX(p.VertsXyz, v), z = VZ(p.VertsXyz, v);
                    if (x < x0[i]) x0[i] = x; if (x > x1[i]) x1[i] = x;
                    if (z < z0[i]) z0[i] = z; if (z > z1[i]) z1[i] = z;
                }
            }
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            float margin = MaxBridgeRunMeters + 1f;
            for (int i = 0; i < n; i++)
            {
                if (x1[i] < x0[i]) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (x1[j] < x0[j]) continue;
                    if (x1[i] < x0[j] - margin || x0[i] > x1[j] + margin) continue;
                    if (z1[i] < z0[j] - margin || z0[i] > z1[j] + margin) continue;
                    Union(parent, i, j);
                }
            }
            var order = new List<int>();
            var index = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                int at;
                if (!index.TryGetValue(root, out at)) { index[root] = order.Count; order.Add(root); }
            }
            var clusters = new List<List<Prop>>();
            for (int k = 0; k < order.Count; k++) clusters.Add(new List<Prop>());
            for (int i = 0; i < n; i++)
                clusters[index[Find(parent, i)]].Add(plates[i]);
            return clusters;
        }

        private static int Find(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }
        private static void Union(int[] p, int a, int b)
        {
            int ra = Find(p, a), rb = Find(p, b);
            if (ra != rb) p[rb] = ra;
        }

        // One plate cluster: rasterise the plate footprint, find each boundary run's void, emit strips.
        private static void SynthesizeCluster(IReadOnlyList<Prop> plates, float[] sceneVerts, int[] sceneTris,
            Func<float, float, float> terrainAt, float cellSizeMeters, float climbMeters, bool ledgeFilter,
            Result r)
        {
            float g = cellSizeMeters > 0.05f ? cellSizeMeters : 0.25f;
            float slopeCap = SteppedPropRamps.NavigableSlopeDegrees(cellSizeMeters, climbMeters, ledgeFilter);
            if (slopeCap > MaxStripSlopeDegrees) slopeCap = MaxStripSlopeDegrees;
            if (slopeCap < 20f) slopeCap = 20f;

            // -------- 1. plate triangles (the props' whole footprint) and their bbox
            var ptri = new List<long>();   // (propIndex << 32) | triIndex
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int pi = 0; pi < plates.Count; pi++)
            {
                Prop p = plates[pi];
                if (p == null || p.VertsXyz == null || p.Tris == null) continue;
                int vc = p.VertsXyz.Length / 3;
                int tc = p.Tris.Length / 3;
                for (int t = 0; t < tc; t++)
                {
                    int i0 = p.Tris[t * 3 + 0], i1 = p.Tris[t * 3 + 1], i2 = p.Tris[t * 3 + 2];
                    if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vc || i1 >= vc || i2 >= vc) continue;
                    float ax = VX(p.VertsXyz, i0), az = VZ(p.VertsXyz, i0), ah = VH(p.VertsXyz, i0);
                    float bx = VX(p.VertsXyz, i1), bz = VZ(p.VertsXyz, i1), bh = VH(p.VertsXyz, i1);
                    float cx = VX(p.VertsXyz, i2), cz = VZ(p.VertsXyz, i2), ch = VH(p.VertsXyz, i2);
                    // The plate is the prop's whole XZ footprint (a thin collider slab projects to its
                    // own footprint; its vertical sides project to zero area). The per-cell top is the
                    // slab's top height, so the strip anchors at the walkable plate level.
                    ptri.Add(((long)pi << 32) | (uint)t);
                    if (ax < minX) minX = ax; if (ax > maxX) maxX = ax;
                    if (bx < minX) minX = bx; if (bx > maxX) maxX = bx;
                    if (cx < minX) minX = cx; if (cx > maxX) maxX = cx;
                    if (az < minZ) minZ = az; if (az > maxZ) maxZ = az;
                    if (bz < minZ) minZ = bz; if (bz > maxZ) maxZ = bz;
                    if (cz < minZ) minZ = cz; if (cz > maxZ) maxZ = cz;
                }
            }
            r.PlateTris += ptri.Count;
            if (ptri.Count < 3) return;

            // -------- 2. local grid over the plate footprint, padded by the bridge reach
            float pad = MaxBridgeRunMeters + 2f * g;
            float ox = (float)Math.Floor((minX - pad) / g) * g;
            float oz = (float)Math.Floor((minZ - pad) / g) * g;
            int nx = (int)Math.Floor((maxX + pad - ox) / g) + 2;
            int nz = (int)Math.Floor((maxZ + pad - oz) / g) + 2;
            if (nx < 3 || nz < 3 || (long)nx * nz > 4000000) return;
            var plateCell = new bool[nx * nz];
            var plateTop = new float[nx * nz];
            for (int i = 0; i < plateTop.Length; i++) plateTop[i] = float.NaN;

            for (int k = 0; k < ptri.Count; k++)
            {
                Prop p = plates[(int)(ptri[k] >> 32)];
                int t = (int)(ptri[k] & 0xffffffff);
                int i0 = p.Tris[t * 3 + 0], i1 = p.Tris[t * 3 + 1], i2 = p.Tris[t * 3 + 2];
                float ax = VX(p.VertsXyz, i0), az = VZ(p.VertsXyz, i0), ah = VH(p.VertsXyz, i0);
                float bx = VX(p.VertsXyz, i1), bz = VZ(p.VertsXyz, i1), bh = VH(p.VertsXyz, i1);
                float cx = VX(p.VertsXyz, i2), cz = VZ(p.VertsXyz, i2), ch = VH(p.VertsXyz, i2);
                float top = Math.Max(ah, Math.Max(bh, ch));
                int ix0 = (int)Math.Floor((Math.Min(ax, Math.Min(bx, cx)) - ox) / g);
                int ix1 = (int)Math.Floor((Math.Max(ax, Math.Max(bx, cx)) - ox) / g);
                int iz0 = (int)Math.Floor((Math.Min(az, Math.Min(bz, cz)) - oz) / g);
                int iz1 = (int)Math.Floor((Math.Max(az, Math.Max(bz, cz)) - oz) / g);
                if (ix0 < 0) ix0 = 0; if (iz0 < 0) iz0 = 0;
                if (ix1 >= nx) ix1 = nx - 1; if (iz1 >= nz) iz1 = nz - 1;
                for (int iz = iz0; iz <= iz1; iz++)
                    for (int ix = ix0; ix <= ix1; ix++)
                    {
                        float cellX = ox + ix * g, cellZ = oz + iz * g;
                        if (!TriCoversCell(cellX, cellZ, g, ax, az, bx, bz, cx, cz)) continue;
                        int ci = ix + iz * nx;
                        plateCell[ci] = true;
                        if (float.IsNaN(plateTop[ci]) || top > plateTop[ci]) plateTop[ci] = top;
                    }
            }

            // -------- 3. local asset grids from the caller's scene soup (terrain + assets for the
            // harness; assets only for the editor, where terrain is probed separately). `assetTop` is
            // every triangle top (for the solid guardrail); `assetUp` is up-facing triangles only (the
            // walkable target surface - a vertical door-frame quad must never be a target).
            // assetUp is band-limited to a plausible FLOOR band around the plate's top, so the gate's own
            // overhead structure (up-facing decks many metres above) cannot mask the ground below it.
            float clusterPlateTopMax = float.MinValue;
            for (int i = 0; i < plateTop.Length; i++)
                if (!float.IsNaN(plateTop[i]) && plateTop[i] > clusterPlateTopMax)
                    clusterPlateTopMax = plateTop[i];
            float floorLo = clusterPlateTopMax
                - ((ConformToGround ? MaxDropConformMeters : MaxDropMeters) + 0.5f);
            float floorHi = clusterPlateTopMax + (ConformToGround ? 0.5f : 1.0f);
            var assetTop = new float[nx * nz];
            var assetUp = new float[nx * nz];
            // Highest up-facing surface at least 0.5 m BELOW the gate plate stack. This is the floor /
            // threshold the gate is placed over. A gate's own horizontal bar can be a thin "plate" too,
            // but it is not the floor; the crossing must start on the floor under it.
            var assetFloor = new float[nx * nz];
            for (int i = 0; i < assetTop.Length; i++)
            {
                assetTop[i] = float.NaN; assetUp[i] = float.NaN; assetFloor[i] = float.NaN;
            }
            if (sceneVerts != null && sceneTris != null)
            {
                int svc = sceneVerts.Length / 3;
                for (int t = 0; t < sceneTris.Length / 3; t++)
                {
                    int i0 = sceneTris[t * 3 + 0], i1 = sceneTris[t * 3 + 1], i2 = sceneTris[t * 3 + 2];
                    if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= svc || i1 >= svc || i2 >= svc) continue;
                    float ax = VX(sceneVerts, i0), az = VZ(sceneVerts, i0), ah = VH(sceneVerts, i0);
                    float bx = VX(sceneVerts, i1), bz = VZ(sceneVerts, i1), bh = VH(sceneVerts, i1);
                    float cx = VX(sceneVerts, i2), cz = VZ(sceneVerts, i2), ch = VH(sceneVerts, i2);
                    float top = Math.Max(ah, Math.Max(bh, ch));
                    float ux = bx - ax, uz = bz - az, uh = bh - ah;
                    float wx = cx - ax, wz = cz - az, wh = ch - ah;
                    float nh = ux * wz - uz * wx;
                    float len = (float)Math.Sqrt((uz * wh - uh * wz) * (uz * wh - uh * wz)
                        + (uh * wx - ux * wh) * (uh * wx - ux * wh) + nh * nh);
                    bool up = len > 1e-9f && nh / len > 0.3f;
                    if (up && (top < floorLo || top > floorHi)) up = false;
                    int ix0 = (int)Math.Floor((Math.Min(ax, Math.Min(bx, cx)) - ox) / g);
                    int ix1 = (int)Math.Floor((Math.Max(ax, Math.Max(bx, cx)) - ox) / g);
                    int iz0 = (int)Math.Floor((Math.Min(az, Math.Min(bz, cz)) - oz) / g);
                    int iz1 = (int)Math.Floor((Math.Max(az, Math.Max(bz, cz)) - oz) / g);
                    if (ix0 < 0) ix0 = 0; if (iz0 < 0) iz0 = 0;
                    if (ix1 >= nx) ix1 = nx - 1; if (iz1 >= nz) iz1 = nz - 1;
                    if (ix1 < ix0 || iz1 < iz0) continue;
                    for (int iz = iz0; iz <= iz1; iz++)
                        for (int ix = ix0; ix <= ix1; ix++)
                        {
                            float cellX = ox + ix * g, cellZ = oz + iz * g;
                            if (!TriCoversCell(cellX, cellZ, g, ax, az, bx, bz, cx, cz)) continue;
                            int ci = ix + iz * nx;
                            if (float.IsNaN(assetTop[ci]) || top > assetTop[ci]) assetTop[ci] = top;
                            if (up && (float.IsNaN(assetUp[ci]) || top > assetUp[ci])) assetUp[ci] = top;
                            if (up && top <= clusterPlateTopMax - 0.1f
                                && (float.IsNaN(assetFloor[ci]) || top > assetFloor[ci]))
                                assetFloor[ci] = top;
                        }
                }
            }

            if (Trace != null)
            {
                int upN = 0, allN = 0, plateN = 0;
                for (int i = 0; i < assetUp.Length; i++)
                {
                    if (!float.IsNaN(assetUp[i])) upN++;
                    if (!float.IsNaN(assetTop[i])) allN++;
                    if (plateCell[i]) plateN++;
                }
                Trace("grid nx=" + nx + " nz=" + nz + " plate=" + plateN + " assetTop=" + allN
                    + " assetUp=" + upN);
            }

            // -------- 4. connected plate clusters (4-connected), processed in scan order
            var comp = new int[nx * nz];
            for (int i = 0; i < comp.Length; i++) comp[i] = -1;
            int ncomp = 0;
            var queue = new Queue<int>();
            for (int start = 0; start < plateCell.Length; start++)
            {
                if (!plateCell[start] || comp[start] >= 0) continue;
                comp[start] = ncomp;
                queue.Clear();
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int ci = queue.Dequeue();
                    int ix = ci % nx, iz = ci / nx;
                    for (int d = 0; d < 4; d++)
                    {
                        int jx = ix + (d == 0 ? 1 : d == 1 ? -1 : 0);
                        int jz = iz + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (jx < 0 || jx >= nx || jz < 0 || jz >= nz) continue;
                        int cj = jx + jz * nx;
                        if (!plateCell[cj] || comp[cj] >= 0) continue;
                        comp[cj] = ncomp;
                        queue.Enqueue(cj);
                    }
                }
                ncomp++;
            }
            for (int i = 0; i < plateCell.Length; i++) if (plateCell[i]) r.Cells++;

            // probe helpers (deterministic, fixed iteration)
            Func<float, float, float, float, float> topInBand = delegate (float px, float pz, float lo, float hi)
            {
                float best = float.NaN;
                int ix = (int)Math.Floor((px - ox) / g), iz = (int)Math.Floor((pz - oz) / g);
                bool inGrid = ix >= 0 && ix < nx && iz >= 0 && iz < nz;
                bool isPlate = inGrid && plateCell[ix + iz * nx];
                // A plate cell (including the ground directly UNDER the plate) is never a target: the
                // strip must reach a surface BEYOND the plate, not tunnel down into the floor below it.
                if (inGrid && !isPlate)
                {
                    float a = assetUp[ix + iz * nx];
                    if (!float.IsNaN(a) && a >= lo - 1e-4f && a <= hi + 1e-4f) best = a;
                }
                if (!isPlate && terrainAt != null)
                {
                    float t = terrainAt(px, pz);
                    if (!float.IsNaN(t) && t >= lo - 1e-4f && t <= hi + 1e-4f
                        && (float.IsNaN(best) || t > best)) best = t;
                }
                return best;
            };
            Func<float, float, float, bool> isFreeAt = delegate (float px, float pz, float h)
            {
                int ix = (int)Math.Floor((px - ox) / g), iz = (int)Math.Floor((pz - oz) / g);
                if (ix >= 0 && ix < nx && iz >= 0 && iz < nz)
                {
                    int ci = ix + iz * nx;
                    if (plateCell[ci]) return true;   // the plate's own geometry is never an obstacle
                    float a = assetTop[ci];
                    // Block only solid WELL above the ramp line (a wall / ceiling the strip would tunnel
                    // through). A thin door-frame post whose top is a few centimetres above the ramp is
                    // not an obstacle to an agent, so a tight threshold would false-reject every gate.
                    if (!float.IsNaN(a) && a >= h + 0.4f) return false;
                }
                return true;
            };

            var keys = new List<string>();
            var clusterCells = new List<int>();
            for (int c = 0; c < ncomp; c++)
            {
                clusterCells.Clear();
                float plateTopMax = float.MinValue;
                for (int ci = 0; ci < comp.Length; ci++)
                {
                    if (comp[ci] != c) continue;
                    clusterCells.Add(ci);
                    if (plateTop[ci] > plateTopMax) plateTopMax = plateTop[ci];
                }
                if (clusterCells.Count == 0 || plateTopMax == float.MinValue) continue;
                // A gate sits on the floor below it. Always use the highest up-facing surface at or
                // below the gate member as the strip's level; the plate's own top is only a fallback
                // when there is no floor at all. The 1.5 m bar must never become the walkable level.
                float hp = plateTopMax;
                float floorH = float.MinValue;
                for (int q = 0; q < clusterCells.Count; q++)
                {
                    int ci = clusterCells[q];
                    float f = assetFloor[ci];
                    if (!float.IsNaN(f) && f > floorH) floorH = f;
                    if (terrainAt != null)
                    {
                        float tx = ox + (ci % nx) * g + 0.5f * g;
                        float tz = oz + (ci / nx) * g + 0.5f * g;
                        float t = terrainAt(tx, tz);
                        if (!float.IsNaN(t) && t <= clusterPlateTopMax - 0.1f && t > floorH) floorH = t;
                    }
                }
                if (floorH > float.MinValue) hp = floorH;
                if (clusterCells.Count == 0) continue;
                int bx0 = nx, bx1 = -1, bz0 = nz, bz1 = -1;
                for (int q = 0; q < clusterCells.Count; q++)
                {
                    int ix = clusterCells[q] % nx, iz = clusterCells[q] / nx;
                    if (ix < bx0) bx0 = ix; if (ix > bx1) bx1 = ix;
                    if (iz < bz0) bz0 = iz; if (iz > bz1) bz1 = iz;
                }
                // The plate is no longer in the soup, so the strip must cover its footprint itself:
                // a flat central section at the floor level, joining the two crossing runs.
                if (ConformToGround && bx1 >= bx0 && bz1 >= bz0)
                {
                    float qx0 = ox + bx0 * g, qx1 = ox + (bx1 + 1) * g;
                    float qz0 = oz + bz0 * g, qz1 = oz + (bz1 + 1) * g;
                    AppendQuad(r, qx0, qz0, hp, qx1, qz0, hp, qx1, qz1, hp, qx0, qz1, hp);
                }
                r.Plates++;
                if (Trace != null)
                {
                    Trace("cluster cells=" + clusterCells.Count + " hp=" + Num(hp) + " plateTop="
                        + Num(plateTopMax) + " floorH=" + Num(floorH) + " ncomp=" + ncomp
                        + " x=[" + Num(ox + bx0 * g) + ".." + Num(ox + (bx1 + 1) * g) + "] z=["
                        + Num(oz + bz0 * g) + ".." + Num(oz + (bz1 + 1) * g) + "]");
                    if (ox + bx0 * g < 1135 && ox + (bx1 + 1) * g > 1126
                        && oz + bz0 * g < 994 && oz + (bz1 + 1) * g > 987)
                    {
                        for (int iz = bz0; iz <= bz1; iz++)
                        {
                            var sb2 = new System.Text.StringBuilder("map z=" + Num(oz + iz * g) + " ");
                            for (int ix = bx0; ix <= bx1; ix++)
                                sb2.Append(plateCell[ix + iz * nx] ? '#' : '.');
                            Trace(sb2.ToString());
                        }
                    }
                }
                int stripsBefore = r.Strips;
                // A gate threshold slab is a rectangle: the LONG footprint axis is the opening (the
                // gate's width), the SHORT axis is the passage through the wall. Only the two boundary
                // runs on the long edges are crossings; the short-edge runs face the jambs and, if
                // emitted, punch a strip sideways through the gatehouse wall into whatever room/island
                // happens to be behind it.
                float extentX = maxX - minX;
                float extentZ = maxZ - minZ;
                bool crossingAlongZ = extentX >= extentZ;
                if (Trace != null)
                    Trace("  crossing axis=" + (crossingAlongZ ? "Z" : "X") + " extents=("
                        + Num(extentX) + "," + Num(extentZ) + ")");
                for (int dir = 0; dir < 4; dir++)
                {
                    if (crossingAlongZ && dir < 2) continue;    // skip the jamb runs (±X)
                    if (!crossingAlongZ && dir >= 2) continue;  // skip the jamb runs (±Z)
                    int dx = dir == 0 ? 1 : dir == 1 ? -1 : 0;
                    int dz = dir == 2 ? 1 : dir == 3 ? -1 : 0;
                    // Boundary cells in this direction, as (perp, along) pairs. Runs are grouped by
                    // CONTIGUOUS perp so a jagged edge still yields one wide strip (a per-cell strip is
                    // only 0.25 m and would erode away). The run's plate edge uses the most inward
                    // `along`, so the strip overlaps the whole jagged edge.
                    var cells = new List<int[]>();   // {perp, along, cellIndex}
                    for (int q = 0; q < clusterCells.Count; q++)
                    {
                        int ci = clusterCells[q];
                        int ix = ci % nx, iz = ci / nx;
                        int jx = ix + dx, jz = iz + dz;
                        bool bound = jx < 0 || jx >= nx || jz < 0 || jz >= nz
                            || !plateCell[jx + jz * nx];
                        if (!bound) continue;
                        int along = (dx != 0) ? ix : iz;
                        int perp = (dx != 0) ? iz : ix;
                        cells.Add(new int[] { perp, along, ci });
                    }
                    cells.Sort(delegate (int[] a, int[] b)
                    {
                        int cmp = a[0].CompareTo(b[0]);
                        return cmp != 0 ? cmp : a[1].CompareTo(b[1]);
                    });
                    int s = 0;
                    while (s < cells.Count)
                    {
                        int e = s;
                        while (e + 1 < cells.Count && cells[e + 1][0] == cells[e][0] + 1) e++;
                        int perpS = cells[s][0], perpE = cells[e][0];
                        int alongMax = int.MinValue;
                        float hpRun = float.MinValue;
                        for (int q = s; q <= e; q++)
                        {
                            if (cells[q][1] > alongMax) alongMax = cells[q][1];
                            float pt = plateTop[cells[q][2]];
                            if (!float.IsNaN(pt) && pt > hpRun) hpRun = pt;
                        }
                        if (hpRun < hp) hpRun = hp;   // anchor at the walkable plate level
                        float width = (perpE - perpS + 1) * g;
                        if (width + 1e-4f >= MinRunMeters && r.Strips - stripsBefore < MaxStripsPerPlate)
                            EmitRun(r, plates, ox, oz, g, dx, dz, alongMax, perpS, perpE, hpRun,
                                topInBand, isFreeAt, slopeCap, keys);
                        s = e + 1;
                    }
                }
            }
        }

        // Emit the strip for one boundary run: the plate edge segment (at the plate's own level) to the
        // first surface beyond the void that a gentle ramp can reach.
        private static void EmitRun(Result r, IReadOnlyList<Prop> plates, float ox, float oz, float g,
            int dx, int dz, int along, int perpS, int perpE, float hp,
            Func<float, float, float, float, float> topInBand,
            Func<float, float, float, bool> isFreeAt, float slopeCap, List<string> keys)
        {
            // plate edge world segment (the boundary between the plate cell and the void cell)
            float ex0, ez0, ex1, ez1;
            if (dx != 0)
            {
                float ex = ox + (along + (dx > 0 ? 1 : 0)) * g;
                ex0 = ex; ez0 = oz + perpS * g;
                ex1 = ex; ez1 = oz + (perpE + 1) * g;
            }
            else
            {
                float ez = oz + (along + (dz > 0 ? 1 : 0)) * g;
                ex0 = ox + perpS * g; ez0 = ez;
                ex1 = ox + (perpE + 1) * g; ez1 = ez;
            }
            float dirX = dx, dirZ = dz;
            float mx = 0.5f * (ex0 + ex1), mz = 0.5f * (ez0 + ez1);
            if (Trace != null)
                Trace("  run dir=(" + dx + "," + dz + ") edge=(" + Num(ex0) + "," + Num(ez0) + ")-("
                    + Num(ex1) + "," + Num(ez1) + ") hp=" + Num(hp));

            float step = 0.5f * g;
            float bandLo = hp - (ConformToGround ? MaxDropConformMeters : MaxDropMeters);
            float bandHi = hp + (ConformToGround ? MaxRiseConformMeters : MaxRiseMeters);
            for (float t = step; t <= MaxBridgeRunMeters + 1e-4f; t += step)
            {
                float px = mx + dirX * t, pz = mz + dirZ * t;
                float top = topInBand(px, pz, bandLo, bandHi);
                if (float.IsNaN(top)) { if (Trace != null) Trace("    t=" + Num(t) + " void"); continue; }
                if (t < MinGapMeters && !ConformToGround) { if (Trace != null) Trace("    t=" + Num(t) + " top=" + Num(top) + " (too close)"); continue; }
                float drop = hp - top;
                if (drop < MinDropMeters) { if (Trace != null) Trace("    t=" + Num(t) + " top=" + Num(top) + " drop=" + Num(drop) + " (too near)"); continue; }
                float run = t + PlateInsetMeters;
                float slope = (float)(Math.Atan2(drop, run) * 180.0 / Math.PI);
                if (slope > slopeCap) { if (Trace != null) Trace("    t=" + Num(t) + " top=" + Num(top) + " slope=" + Num(slope) + " (steep)"); continue; }
                // Guardrail: disabled for now. The cell-level max-top grid cannot distinguish a wall
                // crossing the ramp from the gate's own overhead structure in the same cell, so it
                // false-rejected every gate run. The target criterion (a walkable surface BEYOND the
                // plate, within the climb band) is what scopes the strip; a walled-shut doorway yields
                // no such target within the bridge reach.
                bool blocked = false;
                if (blocked) { r.Skips++; return; }

                // corners: plate edge exactly at the boundary (no inset - a strip that descends under the
                // plate would drop a span below it and let the clearance filter null the plate). The
                // ramp reaches the target at `t`; a flat apron at the target level then runs on to
                // `t+Overlap+TargetApron` so it overlaps the target's own built span.
                float inset = PlateInsetMeters;
                float aX = ex0 - dirX * inset, aZ = ez0 - dirZ * inset;
                float bX = ex1 - dirX * inset, bZ = ez1 - dirZ * inset;
                float reach = t + OverlapMeters + TargetApronMeters;
                float cX = ex0 + dirX * reach, cZ = ez0 + dirZ * reach;
                float dX = ex1 + dirX * reach, dZ = ez1 + dirZ * reach;
                float hc = top, hd = top;
                float tc = topInBand(cX, cZ, bandLo, bandHi);
                if (!float.IsNaN(tc)) hc = tc;
                float td = topInBand(dX, dZ, bandLo, bandHi);
                if (!float.IsNaN(td)) hd = td;

                string key = Round(aX) + "," + Round(aZ) + "," + Round(cX) + "," + Round(cZ);
                bool dup = false;
                for (int i = 0; i < keys.Count; i++) if (keys[i] == key) { dup = true; break; }
                if (dup) return;
                keys.Add(key);

                // Conforming mode: ride the collision surface under the gate instead of one straight
                // quad. The chain starts inside the plate (inset), follows the sampled ground through
                // the opening, and continues onto the approach at the far end.
                if (ConformToGround)
                {
                    EmitConformingRun(r, plates, ex0, ez0, ex1, ez1, dirX, dirZ, hp, top, t,
                        inset, OverlapMeters + TargetApronMeters, g, topInBand);
                    return;
                }

                // Flat plate-level apron, the ramp (ending AT the found target), then a flat target-level
                // apron. Ending the ramp at the target keeps the target's own level, so a walk point on
                // the ground stays on the ground.
                float apron = PlateApronMeters;
                if (apron > t * 0.5f) apron = t * 0.5f;
                if (apron < 0.25f) apron = 0.25f;
                float mX = ex0 + dirX * apron, mZ = ez0 + dirZ * apron;
                float nX = ex1 + dirX * apron, nZ = ez1 + dirZ * apron;
                float pX = ex0 + dirX * t, pZ = ez0 + dirZ * t;
                float qX = ex1 + dirX * t, qZ = ez1 + dirZ * t;
                if (apron > 1e-4f)
                    AppendQuad(r, aX, aZ, hp, bX, bZ, hp, nX, nZ, hp, mX, mZ, hp);
                // ramp: mid edge (at hp) -> ramp end (at the target heights)
                AppendQuad(r, mX, mZ, hp, nX, nZ, hp, qX, qZ, hd, pX, pZ, hc);
                // target apron: ramp end -> reach, flat at the target heights
                if (reach > t + 1e-4f)
                    AppendQuad(r, pX, pZ, hc, qX, qZ, hd, dX, dZ, hd, cX, cZ, hc);
                var info = new Info();
                info.Prop = plates.Count > 0 ? plates[0].Name : "";
                info.Gap = run; info.Drop = drop; info.SlopeDeg = slope;
                info.Width = (ex1 - ex0) + (ez1 - ez0);
                info.Hp = hp; info.Ht = top;
                info.Ex = mx; info.Ez = mz; info.Tx = px; info.Tz = pz;
                r.Infos.Add(info);
                r.Strips++;
                if (Trace != null) Trace("    EMIT gap=" + Num(run) + " drop=" + Num(drop) + " slope="
                    + Num(slope) + " width=" + Num((ex1 - ex0) + (ez1 - ez0)) + " H=" + Num(hp) + "->" + Num(top));
                return;
            }
            r.Skips++;
        }

        // A station chain from the plate edge through the gate to the approach beyond it. Each station
        // samples the walkable surface in the floor band (asset + terrain) at both edges of the run, so
        // the ribbon follows the ground under the gate - including a slope - instead of cutting one
        // straight quad. Where the surface is genuinely absent (a void), the height interpolates
        // linearly from the plate level to the found target (the old bridge), so an open gate over a
        // hole still gets its straight crossing.
        private static void EmitConformingRun(Result r, IReadOnlyList<Prop> plates,
            float ex0, float ez0, float ex1, float ez1, float dirX, float dirZ,
            float hp, float top, float t, float inset, float tail, float g,
            Func<float, float, float, float, float> topInBand)
        {
            float start = -inset;
            float end = t + tail;
            if (end - start < 1e-3f) return;
            int n = (int)Math.Ceiling((end - start) / Math.Max(0.15f, g));
            if (n < 2) n = 2;
            if (n > 64) n = 64;
            var hL = new float[n + 1];
            var hR = new float[n + 1];
            float lo = hp - MaxDropConformMeters;
            float hi = hp + MaxRiseConformMeters;
            for (int i = 0; i <= n; i++)
            {
                float d = start + (end - start) * i / n;
                float px0 = ex0 + dirX * d, pz0 = ez0 + dirZ * d;
                float px1 = ex1 + dirX * d, pz1 = ez1 + dirZ * d;
                if (d <= 1e-4f)
                {
                    // Inside the plate: hold the gate's own walkable level.
                    hL[i] = hp; hR[i] = hp;
                    continue;
                }
                float gl = topInBand(px0, pz0, lo, hi);
                float gr = topInBand(px1, pz1, lo, hi);
                if (d >= t - 1e-3f)
                {
                    if (float.IsNaN(gl)) gl = top;
                    if (float.IsNaN(gr)) gr = top;
                }
                float fallback = d <= t ? hp + (top - hp) * (d / Math.Max(1e-4f, t)) : top;
                if (float.IsNaN(gl)) gl = fallback;
                if (float.IsNaN(gr)) gr = fallback;
                if (gl > hi) gl = hi; if (gl < lo) gl = lo;
                if (gr > hi) gr = hi; if (gr < lo) gr = lo;
                hL[i] = gl; hR[i] = gr;
            }
            hL[0] = hp; hR[0] = hp;
            for (int i = 0; i < n; i++)
            {
                float d0 = start + (end - start) * i / n;
                float d1 = start + (end - start) * (i + 1) / n;
                float a0x = ex0 + dirX * d0, a0z = ez0 + dirZ * d0;
                float b0x = ex1 + dirX * d0, b0z = ez1 + dirZ * d0;
                float a1x = ex0 + dirX * d1, a1z = ez0 + dirZ * d1;
                float b1x = ex1 + dirX * d1, b1z = ez1 + dirZ * d1;
                AppendQuad(r, a0x, a0z, hL[i], b0x, b0z, hR[i], b1x, b1z, hR[i + 1], a1x, a1z, hL[i + 1]);
            }
            var info = new Info();
            info.Prop = plates.Count > 0 ? plates[0].Name : "";
            info.Gap = t + inset;
            info.Drop = hp - top;
            info.SlopeDeg = (float)(Math.Atan2(hp - top, Math.Max(1e-4f, t + inset)) * 180.0 / Math.PI);
            info.Width = Math.Max(0f, (ex1 - ex0) + (ez1 - ez0));
            info.Hp = hp; info.Ht = top;
            info.Ex = 0.5f * (ex0 + ex1); info.Ez = 0.5f * (ez0 + ez1);
            info.Tx = ex0 + dirX * t; info.Tz = ez0 + dirZ * t;
            r.Infos.Add(info);
            r.Strips++;
            if (Trace != null)
                Trace("    EMIT-CONFORM gap=" + Num(t + inset) + " drop=" + Num(hp - top)
                    + " stations=" + n + " width=" + Num(info.Width));
        }

        private static void AppendQuad(Result r, float x0, float z0, float h0, float x1, float z1, float h1,
            float x2, float z2, float h2, float x3, float z3, float h3)
        {
            int b = r.VertsXyz.Count / 3;
            r.VertsXyz.Add(x0); r.VertsXyz.Add(z0); r.VertsXyz.Add(h0);
            r.VertsXyz.Add(x1); r.VertsXyz.Add(z1); r.VertsXyz.Add(h1);
            r.VertsXyz.Add(x2); r.VertsXyz.Add(z2); r.VertsXyz.Add(h2);
            r.VertsXyz.Add(x3); r.VertsXyz.Add(z3); r.VertsXyz.Add(h3);
            r.Tris.Add(b + 0); r.Tris.Add(b + 1); r.Tris.Add(b + 2);
            r.Tris.Add(b + 0); r.Tris.Add(b + 2); r.Tris.Add(b + 3);
        }

        // True when a grid cell (its centre or any of its four corners) is covered by the triangle.
        // Small collision triangles are common on a gate's collider stack, so a centre-only test would
        // leave holes in the plate footprint.
        private static bool TriCoversCell(float cellX, float cellZ, float g,
            float ax, float az, float bx, float bz, float cx, float cz)
        {
            float h = 0.5f * g;
            if (PointInTriXZ(cellX + h, cellZ + h, ax, az, bx, bz, cx, cz)) return true;
            if (PointInTriXZ(cellX, cellZ, ax, az, bx, bz, cx, cz)) return true;
            if (PointInTriXZ(cellX + g, cellZ, ax, az, bx, bz, cx, cz)) return true;
            if (PointInTriXZ(cellX, cellZ + g, ax, az, bx, bz, cx, cz)) return true;
            if (PointInTriXZ(cellX + g, cellZ + g, ax, az, bx, bz, cx, cz)) return true;
            return false;
        }

        private static bool PointInTriXZ(float px, float pz, float ax, float az, float bx, float bz,
            float cx, float cz)
        {
            float d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
            float d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
            float d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
            bool neg = d1 < 0f || d2 < 0f || d3 < 0f;
            bool pos = d1 > 0f || d2 > 0f || d3 > 0f;
            return !(neg && pos);
        }

        private static string Round(float v)
        {
            return ((float)Math.Round(v / DedupeQuantum) * DedupeQuantum).ToString("0.###",
                System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
