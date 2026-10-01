using System;
using System.Collections.Generic;
using System.Globalization;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Final-mesh sliver cleanup (PURE: no TaleWorlds types)
    // ==================================================================
    // The detail tessellation leaves long thin "needle" triangles in complex areas (a notched/jagged
    // outline over an open yard is the classic). They cost routing real time and they are ugly. The V3.35
    // attempt used Delaunay-style edge flips only and fell far short: on a real detail mesh a sliver is
    // usually bounded by OUTLINE edges or non-convex quads, so there is no valid flip for it. The cure is
    // to REMOVE the sliver's near-collinear vertex and retriangulate the tiny hole - which is also the
    // "combine" the user asked for (fewer vertices/faces, fatter triangles).
    //
    // Order: weld -> drop degenerates/duplicates -> DECIMATE (primary) -> FLIP (polish) -> gate.
    //
    // Hard invariants kept: no island merge/split (the component count is unchanged), manifoldness is
    // preserved, only vertices whose removal moves the surface by <= a small tolerance are removed, and
    // steep fans (steps / corners / connector rims) are skipped. Deterministic, flat arrays, chiefly
    // linear. The tolerance is justified by the mesh's OWN promise: detail max error is 0.25 m
    // (NavMeshMaxErrorMeters) and contour simplification allows 1.3 vox = 0.325 m at cs 0.25 m, so the
    // 0.05 m cleanup tolerance is 5-6x tighter than what the mesh already promises. Stacked levels are
    // protected by the planarity/step guard (a fan whose normals spread more than MaxFanNormalSpread is
    // never touched, so two levels over the same XZ are never joined).
    public static class NavMeshCleanup
    {
        public const float WeldEpsilonMeters = 1e-3f;
        // Surface-move tolerance for an interior vertex removal. 0.20 m is still inside the mesh's own
        // promise (detail max error 0.25 m, contour simplification 0.325 m); folds that would merge a step
        // are refused by the plane/outline bounds themselves.
        public const float SurfaceToleranceMeters = 0.20f;
        // Outline-move tolerance for a boundary vertex removal. Boundary needles are outline wobbles; a
        // sub-30 cm straightening is invisible to navigation (agent radius 0.25 m) and to the bot.
        public const float BoundaryToleranceMeters = 0.30f;
        // A fan whose triangle normals spread more than this (degrees) is a step/corner/rim: skip it.
        // Kept generous on purpose - the real bound on a fold is the move tolerance below, and a tight
        // spread cap was refusing ordinary bumpy ground.
        public const float MaxFanNormalSpreadDegrees = 120f;
        // Needle: shortest altitude < 0.05 m AND longest edge > 0.5 m. Thin: min angle < 5 deg.
        public const float NeedleAltitudeMeters = 0.05f;
        public const float NeedleLongestEdgeMeters = 0.5f;
        public const float ThinMinAngleDegrees = 5f;
        // Decimation candidates: only vertices of a needle or of a face thinner than this. Testing every
        // vertex's whole fan was burning the time budget on ordinary bumpy ground.
        public const float CandidateMinAngleDegrees = 3f;
        // Flip polish guards (from V3.35, with its bugs fixed).
        public const float FlipNormalAngleDegrees = 18f;
        public const float FlipSurfaceMoveMeters = 0.15f;
        public const float FlipMinAngleGainDegrees = 0.5f;

        public sealed class Stats
        {
            public int Welded;
            public int Removed;            // degenerate/duplicate faces dropped
            public int Decimated;          // vertices removed by decimation
            public int Flipped;            // interior edges flipped in the polish
            public int NeedlesBefore, NeedlesAfter;
            public int ThinBefore, ThinAfter;
            public int BoundaryMovedCount;
            public float BoundaryMaxMove;
            public int ComponentsBefore, ComponentsAfter;
            public int CandVerts, RejMove, RejFan, RejRing, RejTopo;
            public string EdgeHist;
            // Why each surviving needle's vertices were not removable (capped; goes to the run log).
            public List<string> Diag;
            // One-line decimation summary (budget/mask/sweeps); goes to the run log.
            public string DecimDiag;
        }

        // vertsXyz: flat x,y,z. tris: flat indices. maxMilliseconds bounds the decimation + flips.
        public static Stats Run(List<float> vertsXyz, List<int> tris, int maxMilliseconds)
        {
            var st = new Stats();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (vertsXyz == null || tris == null || tris.Count < 3) return st;

            float[] v = vertsXyz.ToArray();
            st.NeedlesBefore = CountNeedles(v, tris);
            st.ThinBefore = CountThin(v, tris);
            st.ComponentsBefore = CountComponents(tris);

            Weld(vertsXyz, tris, st);
            RemoveDegenerateAndDuplicate(vertsXyz, tris, st);

            Decimate(vertsXyz, tris, st, maxMilliseconds, watch);
            // Defensive: drop any sentinel slot an early-exiting sweep left behind, then drop the vertices
            // no face references (the ones decimation removed) and renumber. Indices are renumbered only
            // HERE, after decimation - decimation itself relies on stable vertex indices.
            CompactVertices(vertsXyz, tris);
            v = vertsXyz.ToArray();
            // The flip polish gets its own window: decimation is allowed to use the whole budget (it is
            // the primary lever), and squeezing the flips into the remainder left them doing nothing.
            FlipEdges(v, tris, st, maxMilliseconds + 2500, watch);

            st.NeedlesAfter = CountNeedles(vertsXyz.ToArray(), tris);
            st.ThinAfter = CountThin(vertsXyz.ToArray(), tris);
            st.ComponentsAfter = CountComponents(tris);
            if (st.NeedlesAfter > 0) st.Diag = NeedleDiag(vertsXyz, tris, 12);
            return st;
        }

        // ---- metrics -------------------------------------------------------------------------------
        private static bool IsNeedle(float[] v, int a, int b, int c)
        {
            double e0 = Len(v, a, b), e1 = Len(v, b, c), e2 = Len(v, c, a);
            double longest = Math.Max(e0, Math.Max(e1, e2));
            double area = Area(v, a, b, c);
            double alt = longest > 1e-9 ? 2.0 * area / longest : 0.0;
            return alt < NeedleAltitudeMeters && longest > NeedleLongestEdgeMeters;
        }

        private static int CountNeedles(float[] v, List<int> tris)
        {
            int n = 0;
            for (int t = 0; t + 2 < tris.Count; t += 3)
                if (IsNeedle(v, tris[t], tris[t + 1], tris[t + 2])) n++;
            return n;
        }

        private static int CountThin(float[] v, List<int> tris)
        {
            int n = 0;
            for (int t = 0; t + 2 < tris.Count; t += 3)
                if (MinAngle(v, tris[t], tris[t + 1], tris[t + 2]) < ThinMinAngleDegrees) n++;
            return n;
        }

        private static double Len(float[] v, int a, int b)
        {
            double dx = v[b * 3] - v[a * 3], dy = v[b * 3 + 1] - v[a * 3 + 1], dz = v[b * 3 + 2] - v[a * 3 + 2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private static double Area(float[] v, int a, int b, int c)
        {
            double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            double vx = v[c * 3] - v[a * 3], vy = v[c * 3 + 1] - v[a * 3 + 1], vz = v[c * 3 + 2] - v[a * 3 + 2];
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            return 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
        }

        private static double MinAngle(float[] v, int a, int b, int c)
        {
            double e0 = Len(v, a, b), e1 = Len(v, b, c), e2 = Len(v, c, a);
            double shortest = Math.Min(e0, Math.Min(e1, e2));
            if (shortest < 1e-9) return 0.0;
            double cosA;
            if (shortest == e0) cosA = (e2 * e2 + e1 * e1 - e0 * e0) / (2 * e2 * e1);
            else if (shortest == e1) cosA = (e0 * e0 + e2 * e2 - e1 * e1) / (2 * e0 * e2);
            else cosA = (e0 * e0 + e1 * e1 - e2 * e2) / (2 * e0 * e1);
            if (cosA > 1) cosA = 1; if (cosA < -1) cosA = -1;
            return Math.Acos(cosA) * 180.0 / Math.PI;
        }

        // ---- components (flat edge arrays, O(F log F), no dictionary) ------------------------------
        // Used only to ASSERT the component count is unchanged. Sorted 64-bit edge keys.
        private static int CountComponents(List<int> tris)
        {
            int nv = 0;
            for (int t = 0; t < tris.Count; t++) if (tris[t] + 1 > nv) nv = tris[t] + 1;
            var parent = new int[nv];
            for (int i = 0; i < nv; i++) parent[i] = i;
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                Union(parent, tris[t], tris[t + 1]);
                Union(parent, tris[t + 1], tris[t + 2]);
                Union(parent, tris[t + 2], tris[t]);
            }
            var roots = new HashSet<int>();
            for (int t = 0; t < tris.Count; t++) roots.Add(Find(parent, tris[t]));
            return roots.Count;
        }

        private static int Find(int[] p, int x) { while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; } return x; }
        private static void Union(int[] p, int a, int b)
        {
            int ra = Find(p, a), rb = Find(p, b);
            if (ra != rb) p[ra] = rb;
        }

        // ---- 1. weld ------------------------------------------------------------------------------
        private static void Weld(List<float> verts, List<int> tris, Stats st)
        {
            int nv = verts.Count / 3;
            var v = verts.ToArray();
            var map = new int[nv];
            var keep = new int[nv];
            var grid = new Dictionary<long, int>(LongHash.Instance);
            int outV = 0;
            for (int i = 0; i < nv; i++)
            {
                long key = VertKey(v, i);
                int at;
                if (grid.TryGetValue(key, out at)) { map[i] = at; st.Welded++; }
                else { grid[key] = outV; map[i] = outV; keep[outV] = i; outV++; }
            }
            if (st.Welded == 0) return;
            var nverts = new List<float>(outV * 3);
            for (int i = 0; i < outV; i++)
            {
                int src = keep[i];
                nverts.Add(v[src * 3]); nverts.Add(v[src * 3 + 1]); nverts.Add(v[src * 3 + 2]);
            }
            verts.Clear(); verts.AddRange(nverts);
            for (int t = 0; t < tris.Count; t++) tris[t] = map[tris[t]];
        }

        private static long VertKey(float[] v, int i)
        {
            long x = (long)Math.Round(v[i * 3] / WeldEpsilonMeters);
            long y = (long)Math.Round(v[i * 3 + 1] / WeldEpsilonMeters);
            long z = (long)Math.Round(v[i * 3 + 2] / WeldEpsilonMeters);
            return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L);
        }

        // ---- 2. remove degenerates / duplicates ---------------------------------------------------
        private static void RemoveDegenerateAndDuplicate(List<float> verts, List<int> tris, Stats st)
        {
            var outTris = new List<int>(tris.Count);
            var seen = new HashSet<long>(LongHash.Instance);
            int n = tris.Count / 3;
            for (int t = 0; t < n; t++)
            {
                int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                if (a == b || b == c || a == c) { st.Removed++; continue; }
                int x = Math.Min(a, Math.Min(b, c));
                int z = Math.Max(a, Math.Max(b, c));
                int y = a + b + c - x - z;
                long key = ((long)x * 1000003L + y) * 1000003L + z;
                if (!seen.Add(key)) { st.Removed++; continue; }
                outTris.Add(a); outTris.Add(b); outTris.Add(c);
            }
            tris.Clear(); tris.AddRange(outTris);
        }

        // ---- 3. DECIMATION (primary) --------------------------------------------------------------
        // Removes a vertex incident to a needle/thin triangle and retriangulates its ring, when the
        // surface move is within tolerance and the fan is planar enough. Boundary vertices ARE
        // candidates: a boundary needle's apex is usually a sub-5 cm bump; the OUTLINE MOVE is bounded,
        // not forbidden.
        //
        // Correctness notes (what the first attempt got wrong):
        //  - Vertex indices are STABLE for the whole run; only CompactVertices at the end renumbers.
        //  - Killing a vertex's fan blanks the leftover face slots with a -1 sentinel and NEVER compacts
        //    `tris` mid-sweep, so the CSR built at the sweep start keeps the same face numbering for the
        //    whole sweep (mid-sweep compaction used to invalidate every face index in it).
        //  - A removal rewrites face slots that also live in the incident lists of the ring vertices, so
        //    exactly those vertices are marked `dirty` and skipped until the next sweep (whose fresh CSR
        //    sees the new topology). This is what keeps the sweep correct without a per-removal rebuild.
        private static void Decimate(List<float> verts, List<int> tris, Stats st, int maxMs,
            System.Diagnostics.Stopwatch watch)
        {
            int nv = verts.Count / 3;
            int[] ringStart, ringFaces;
            BuildFaceAdjacency(tris, out ringStart, out ringFaces);
            MeshTopo topo = MeshTopo.Build(tris);
            // Boundary flags straight off the topology guard's edge counts (no second dictionary).
            var boundary = new bool[nv];
            foreach (KeyValuePair<long, int> kv in topo.EdgeCount)
            {
                if (kv.Value != 1) continue;
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                if (a >= 0 && a < nv) boundary[a] = true;
                if (b >= 0 && b < nv) boundary[b] = true;
            }
            var v = verts.ToArray();
            var alive = new bool[nv];
            for (int i = 0; i < nv; i++) alive[i] = true;
            var dirty = new bool[nv];

            for (int sweep = 0; sweep < 16; sweep++)
            {
                bool any = false;
                bool timeUp = false;
                int csrVerts = ringStart.Length - 1;
                // Candidate mask for this sweep: only vertices of a needle / very thin face. One O(F)
                // pass over the faces beats testing every vertex's fan with the full guard set (that was
                // spending the whole budget on ordinary bumpy ground).
                var cand = new bool[csrVerts];
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (a < 0) continue;
                    if (!IsNeedle(v, a, b, c) && MinAngle(v, a, b, c) >= CandidateMinAngleDegrees) continue;
                    if (a < cand.Length) cand[a] = true;
                    if (b < cand.Length) cand[b] = true;
                    if (c < cand.Length) cand[c] = true;
                }
                if (sweep == 0)
                {
                    int maskCount = 0;
                    for (int i = 0; i < cand.Length; i++) if (cand[i]) maskCount++;
                    st.DecimDiag = "decimate: start=" + watch.ElapsedMilliseconds + "ms budget=" + maxMs
                        + "ms faces=" + (tris.Count / 3) + " csrVerts=" + csrVerts + " mask=" + maskCount;
                }
                for (int vert = 0; vert < csrVerts; vert++)
                {
                    if ((vert & 255) == 0 && watch.ElapsedMilliseconds > maxMs) { timeUp = true; break; }
                    if (!cand[vert]) continue;
                    if (vert < alive.Length && (!alive[vert] || dirty[vert])) continue;
                    if (!IncidentSliver(v, tris, ringStart, ringFaces, vert)) continue;
                    st.CandVerts++;
                    if (!TryRemoveVertex(tris, v, ringStart, ringFaces, boundary, dirty, topo, vert, st))
                        continue;
                    alive[vert] = false;
                    st.Decimated++;
                    any = true;
                    // The slots that held this fan now hold the retriangulation: every vertex in them is a
                    // ring vertex, and those are the only vertices whose incident lists went stale.
                    for (int i = ringStart[vert]; i < ringStart[vert + 1]; i++)
                    {
                        int f = ringFaces[i];
                        if (f < 0 || f >= tris.Count / 3) continue;
                        for (int k = 0; k < 3; k++)
                        {
                            int w = tris[f * 3 + k];
                            if (w >= 0 && w < dirty.Length) dirty[w] = true;
                        }
                    }
                }
                if (any)
                {
                    CompactFaces(tris);
                    if (timeUp) break;
                    v = verts.ToArray();
                    BuildFaceAdjacency(tris, out ringStart, out ringFaces);
                    for (int i = 0; i < dirty.Length; i++) dirty[i] = false;
                }
                else break;
            }
        }

        private static bool IncidentSliver(float[] v, List<int> tris, int[] start, int[] faces, int vert)
        {
            int nf = tris.Count / 3;
            for (int i = start[vert]; i < start[vert + 1]; i++)
            {
                int f = faces[i];
                if (f < 0 || f >= nf) continue;                    // slot cannot exist any more
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                if (a < 0) continue;                               // blanked sentinel
                if (a != vert && b != vert && c != vert) continue; // stale list entry (defensive)
                if (IsNeedle(v, a, b, c) || MinAngle(v, a, b, c) < ThinMinAngleDegrees) return true;
            }
            return false;
        }

        // CSR: for each vertex, the faces that use it.
        private static void BuildFaceAdjacency(List<int> tris, out int[] start, out int[] faces)
        {
            int nv = 0;
            for (int t = 0; t < tris.Count; t++) if (tris[t] + 1 > nv) nv = tris[t] + 1;
            var count = new int[nv + 1];
            int nf = tris.Count / 3;
            for (int f = 0; f < nf; f++)
            {
                if (tris[f * 3] < 0) continue;                     // never build adjacency over sentinels
                count[tris[f * 3] + 1]++;
                count[tris[f * 3 + 1] + 1]++;
                count[tris[f * 3 + 2] + 1]++;
            }
            for (int i = 0; i < nv; i++) count[i + 1] += count[i];
            start = count;
            faces = new int[start[nv]];
            var cursor = new int[nv];
            for (int f = 0; f < nf; f++)
            {
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                if (a < 0) continue;
                faces[start[a] + cursor[a]++] = f;
                faces[start[b] + cursor[b]++] = f;
                faces[start[c] + cursor[c]++] = f;
            }
        }

        // Removes `vert`: its incident faces form a fan around it; the hole's ring (the other vertices, in
        // order) is retriangulated. Guards: the ring must be a simple fan (a closed cycle, or an OPEN
        // CHAIN for a boundary vertex); the fan normals must not spread too far; the removed vertex must
        // be within tolerance of the new surface - and, on the outline, of the chord its two outline
        // edges collapse to. Returns false, changing nothing, when a guard fails. Killing the fan blanks
        // the leftover slots with a -1 sentinel; the caller compacts them at the end of the sweep.
        private static bool TryRemoveVertex(List<int> tris, float[] v, int[] start, int[] faces,
            bool[] boundary, bool[] dirty, MeshTopo topo, int vert, Stats st)
        {
            int nf = tris.Count / 3;
            var incident = new List<int>();
            for (int i = start[vert]; i < start[vert + 1]; i++)
            {
                int f = faces[i];
                if (f < 0 || f >= nf) continue;                  // slot cannot exist any more
                if (tris[f * 3] < 0) continue;                   // blanked sentinel
                if (tris[f * 3] != vert && tris[f * 3 + 1] != vert && tris[f * 3 + 2] != vert) continue;
                incident.Add(f);
            }
            if (incident.Count == 0) return false;

            // Spike: the vertex hangs off a single face - the tip of a long thin flap, exactly the needles
            // in the user's screenshot. Delete the face when its base edge is interior (the spike is a
            // real protrusion; an isolated flap would change the component count and is left alone). The
            // collapse distance is the tip's distance to the base edge.
            if (incident.Count == 1)
            {
                int f = incident[0];
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                int p, q;
                if (a == vert) { p = b; q = c; }
                else if (b == vert) { p = c; q = a; }
                else { p = a; q = b; }
                if (p == q) return false;
                if (dirty[p] || dirty[q]) return false;           // stale lists: retry next sweep
                double d = PointSegDistance(v, vert, p, q);
                if (!(d <= SurfaceToleranceMeters)) { st.RejMove++; return false; }
                int shared = 0;
                for (int i = start[p]; i < start[p + 1]; i++)
                {
                    int g = faces[i];
                    if (g < 0 || g >= nf) continue;
                    int ga = tris[g * 3], gb = tris[g * 3 + 1], gc = tris[g * 3 + 2];
                    if (ga < 0) continue;
                    if ((ga == p || gb == p || gc == p) && (ga == q || gb == q || gc == q)) shared++;
                }
                if (shared != 2) return false;                    // not exactly one neighbour face
                var oldOne = new List<int>(1);
                oldOne.Add(f);
                var noNew = new List<int>();
                if (!topo.CanReplace(tris, oldOne, noNew)) { st.RejTopo++; return false; }
                topo.Commit(tris, oldOne, noNew);
                if (boundary[vert])
                {
                    if (d > st.BoundaryMaxMove) st.BoundaryMaxMove = (float)d;
                    st.BoundaryMovedCount++;
                }
                tris[f * 3] = -1; tris[f * 3 + 1] = -1; tris[f * 3 + 2] = -1;
                dirty[p] = true; dirty[q] = true;                 // their lists now hold a blanked slot
                return true;
            }

            // Fan normal spread guard.
            if (!FanPlanar(v, tris, incident)) { st.RejFan++; return false; }

            // Ring: vertices adjacent to `vert` along the fan boundary, in order.
            var ring = new List<int>();
            if (!BuildRing(tris, incident, vert, ring)) { st.RejRing++; return false; }
            if (ring.Count < 3) { st.RejRing++; return false; }

            float tol = boundary[vert] ? BoundaryToleranceMeters : SurfaceToleranceMeters;
            // Move: the removed vertex's distance to the ring's best-fit plane bounds how far the surface
            // moves at that spot.
            if (!SurfaceMoveOk(v, vert, ring, tol)) { st.RejMove++; return false; }
            if (boundary[vert])
            {
                // Outline move: the boundary must not swing more than the boundary tolerance.
                float moved = OutlineMove(v, vert, ring);
                if (moved > BoundaryToleranceMeters) { st.RejMove++; return false; }
                if (moved > st.BoundaryMaxMove) st.BoundaryMaxMove = moved;
                st.BoundaryMovedCount++;
            }

            // Retriangulate the ring (no `vert`) in one fixed plane, best-quality ear each step. Reject on
            // degenerate/self-intersecting output, and never trade a sliver for a new needle.
            var newTris = new List<int>();
            if (!TriangulateRing(v, ring, newTris)) { st.RejTopo++; return false; }
            for (int i = 0; i + 2 < newTris.Count; i += 3)
            {
                if (IsNeedle(v, newTris[i], newTris[i + 1], newTris[i + 2]))
                { st.RejTopo++; return false; }
            }

            // Topology guard: never produce a duplicate face or a three-face edge (the export gate).
            if (!topo.CanReplace(tris, incident, newTris)) { st.RejTopo++; return false; }
            topo.Commit(tris, incident, newTris);

            // Apply: rewrite the first incident slots with the new triangles, blank the rest.
            int write = 0;
            for (int i = 0; i < incident.Count && write < newTris.Count; i++)
            {
                int f = incident[i];
                tris[f * 3] = newTris[write]; tris[f * 3 + 1] = newTris[write + 1]; tris[f * 3 + 2] = newTris[write + 2];
                write += 3;
            }
            for (int i = write / 3; i < incident.Count; i++)
            {
                int f = incident[i];
                tris[f * 3] = -1; tris[f * 3 + 1] = -1; tris[f * 3 + 2] = -1;
            }
            return true;
        }

        private static void CompactFaces(List<int> tris)
        {
            var outT = new List<int>(tris.Count);
            for (int t = 0; t + 2 < tris.Count; t += 3)
                if (tris[t] >= 0) { outT.Add(tris[t]); outT.Add(tris[t + 1]); outT.Add(tris[t + 2]); }
            tris.Clear(); tris.AddRange(outT);
        }

        // Drops every vertex no face references (decimation leaves them behind) and renumbers the rest.
        // This is the ONLY place vertex indices change; decimation relies on stable indices.
        private static void CompactVertices(List<float> verts, List<int> tris)
        {
            CompactFaces(tris);
            int nv = verts.Count / 3;
            var used = new bool[nv];
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                used[tris[t]] = true; used[tris[t + 1]] = true; used[tris[t + 2]] = true;
            }
            var map = new int[nv];
            int outV = 0;
            for (int i = 0; i < nv; i++) map[i] = used[i] ? outV++ : -1;
            if (outV == nv) return;
            var v = verts.ToArray();
            var nverts = new List<float>(outV * 3);
            for (int i = 0; i < nv; i++)
            {
                if (map[i] < 0) continue;
                nverts.Add(v[i * 3]); nverts.Add(v[i * 3 + 1]); nverts.Add(v[i * 3 + 2]);
            }
            verts.Clear(); verts.AddRange(nverts);
            var outT = new List<int>(tris.Count);
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                outT.Add(map[tris[t]]); outT.Add(map[tris[t + 1]]); outT.Add(map[tris[t + 2]]);
            }
            tris.Clear(); tris.AddRange(outT);
        }

        private static bool FanPlanar(float[] v, List<int> tris, List<int> incident)
        {
            double[] refN = null;
            for (int i = 0; i < incident.Count; i++)
            {
                int f = incident[i];
                double[] n = Normal(v, tris[f * 3], tris[f * 3 + 1], tris[f * 3 + 2]);
                if (Mat(n) < 1e-12) continue;
                if (refN == null) { refN = n; continue; }
                double na = AngleDeg(refN, n);
                if (na > 90) na = 180 - na;
                if (na > MaxFanNormalSpreadDegrees) return false;
            }
            return refN != null;
        }

        // Walk the fan around `vert` to get the fence ring in order. WINDING-AGNOSTIC: the ring is the set
        // of edges opposite `vert` (one per incident face), walked as an undirected graph. The first
        // version walked the face winding (p->q), which breaks on the mixed-winding faces the winding
        // normalization legitimately leaves in the mesh - that was the dominant remaining blocker.
        // An interior vertex yields a closed cycle; a boundary vertex yields an OPEN CHAIN between its two
        // outline neighbours. Deterministic start: the chain end with the smaller index, else the smallest.
        private static bool BuildRing(List<int> tris, List<int> incident, int vert, List<int> ring)
        {
            var adj = new Dictionary<int, List<int>>();
            for (int i = 0; i < incident.Count; i++)
            {
                int f = incident[i];
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                int p, q;
                if (a == vert) { p = b; q = c; }
                else if (b == vert) { p = c; q = a; }
                else if (c == vert) { p = a; q = b; }
                else return false;
                if (p == q || p == vert || q == vert) return false;
                AddAdj(adj, p, q);
                AddAdj(adj, q, p);
            }
            if (adj.Count < 3) return false;

            // A simple fan: every ring vertex has degree 2, except - for a boundary fan - exactly two
            // chain ends of degree 1. A branch/multi-fan is refused here.
            int ends = 0;
            int start = int.MaxValue;
            foreach (KeyValuePair<int, List<int>> kv in adj)
            {
                int d = kv.Value.Count;
                if (d == 1) { ends++; if (kv.Key < start) start = kv.Key; }
                else if (d != 2) return false;
            }
            if (ends != 0 && ends != 2) return false;

            ring.Clear();
            var used = new HashSet<long>();
            int startV = ends == 2 ? start : MinKey(adj);
            int cur = startV;
            while (true)
            {
                ring.Add(cur);
                int nxt = -1;
                List<int> nb;
                if (adj.TryGetValue(cur, out nb))
                {
                    for (int i = 0; i < nb.Count; i++)
                    {
                        long k = EdgeKey(cur, nb[i]);
                        if (!used.Add(k)) continue;
                        nxt = nb[i];
                        break;
                    }
                }
                if (nxt < 0) break;
                if (nxt == startV) break;
                if (ring.Count > adj.Count) return false;
                cur = nxt;
            }
            return ring.Count == adj.Count;             // every ring vertex visited exactly once
        }

        private static void AddAdj(Dictionary<int, List<int>> adj, int a, int b)
        {
            List<int> l;
            if (!adj.TryGetValue(a, out l)) { l = new List<int>(4); adj[a] = l; }
            l.Add(b);
        }

        private static int MinKey(Dictionary<int, List<int>> adj)
        {
            int min = int.MaxValue;
            foreach (KeyValuePair<int, List<int>> kv in adj) if (kv.Key < min) min = kv.Key;
            return min;
        }

        private static bool SurfaceMoveOk(float[] v, int vert, List<int> ring, float tol)
        {
            return PlaneMove(v, vert, ring) <= tol;
        }

        // Distance from `vert` to the best-fit plane of its ring: how far the surface moves at that spot
        // when the vertex is folded away. double.MaxValue when the ring is degenerate.
        private static double PlaneMove(float[] v, int vert, List<int> ring)
        {
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < ring.Count; i++) { cx += v[ring[i] * 3]; cy += v[ring[i] * 3 + 1]; cz += v[ring[i] * 3 + 2]; }
            cx /= ring.Count; cy /= ring.Count; cz /= ring.Count;
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                int a = ring[i], b = ring[(i + 1) % ring.Count];
                double ux = v[a * 3] - cx, uy = v[a * 3 + 1] - cy, uz = v[a * 3 + 2] - cz;
                double vx = v[b * 3] - cx, vy = v[b * 3 + 1] - cy, vz = v[b * 3 + 2] - cz;
                nx += uy * vz - uz * vy; ny += uz * vx - ux * vz; nz += ux * vy - uy * vx;
            }
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) return double.MaxValue;
            return Math.Abs((v[vert * 3] - cx) * nx + (v[vert * 3 + 1] - cy) * ny + (v[vert * 3 + 2] - cz) * nz) / len;
        }

        private static float OutlineMove(float[] v, int vert, List<int> ring)
        {
            // Dropping a boundary vertex replaces its two outline edges (u,vert) and (vert,w) with the
            // chord (u,w); the outline swings by the distance from `vert` to that chord. (A boundary
            // vertex's ring is an open chain, so its two ends are exactly the outline neighbours.)
            if (ring.Count < 3) return 0f;
            int u = ring[0], w = ring[ring.Count - 1];
            double ax = v[u * 3], az = v[u * 3 + 2];
            double dx = v[w * 3] - ax, dz = v[w * 3 + 2] - az;
            double px = v[vert * 3] - ax, pz = v[vert * 3 + 2] - az;
            double len2 = dx * dx + dz * dz;
            double t = len2 > 1e-12 ? (px * dx + pz * dz) / len2 : 0.0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double ex = px - t * dx, ez = pz - t * dz;
            return (float)Math.Sqrt(ex * ex + ez * ez);
        }

        // Ear-clip the ring (a simple polygon, potentially an open chain closed by the new outline chord)
        // in ONE fixed best-fit plane, choosing the best-quality ear each step. The first attempt picked
        // a projection axis PER TRIANGLE, which is meaningless on a sliver (its normal is noise) and
        // caused both rejects and bad output. Emitted triangles get the surface's (+Y) winding.
        private static bool TriangulateRing(float[] v, List<int> ring, List<int> outTris)
        {
            outTris.Clear();
            int n = ring.Count;
            if (n < 3) return false;

            // Newell normal of the ring polygon (stable even on near-degenerate rings).
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < n; i++)
            {
                int a = ring[i], b = ring[(i + 1) % n];
                double ax = v[a * 3], ay = v[a * 3 + 1], az = v[a * 3 + 2];
                double bx = v[b * 3], by = v[b * 3 + 1], bz = v[b * 3 + 2];
                nx += (ay - by) * (az + bz);
                ny += (az - bz) * (ax + bx);
                nz += (ax - bx) * (ay + by);
            }
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-12) return false;
            nx /= nl; ny /= nl; nz /= nl;

            // Orthonormal basis spanning the plane.
            double ux, uy, uz;
            if (Math.Abs(nx) <= Math.Abs(ny) && Math.Abs(nx) <= Math.Abs(nz)) { ux = 0; uy = -nz; uz = ny; }
            else if (Math.Abs(ny) <= Math.Abs(nz)) { ux = -nz; uy = 0; uz = nx; }
            else { ux = -ny; uy = nx; uz = 0; }
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (ul < 1e-12) return false;
            ux /= ul; uy /= ul; uz /= ul;
            double wx = ny * uz - nz * uy, wy = nz * ux - nx * uz, wz = nx * uy - ny * ux;

            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++) { cx += v[ring[i] * 3]; cy += v[ring[i] * 3 + 1]; cz += v[ring[i] * 3 + 2]; }
            cx /= n; cy /= n; cz /= n;
            var px = new double[n];
            var py = new double[n];
            for (int i = 0; i < n; i++)
            {
                double dx = v[ring[i] * 3] - cx, dy = v[ring[i] * 3 + 1] - cy, dz = v[ring[i] * 3 + 2] - cz;
                px[i] = dx * ux + dy * uy + dz * uz;
                py[i] = dx * wx + dy * wy + dz * wz;
            }
            double area2 = 0;
            for (int i = 0; i < n; i++) { int j = (i + 1) % n; area2 += px[i] * py[j] - px[j] * py[i]; }
            if (Math.Abs(area2) < 1e-12) return false;
            if (area2 < 0) for (int i = 0; i < n; i++) px[i] = -px[i];   // make it CCW

            var idx = new List<int>(n);
            for (int i = 0; i < n; i++) idx.Add(i);
            int guard = 0;
            while (idx.Count > 3 && guard++ < 100000)
            {
                int bestI = -1; double bestQ = -1;
                for (int i = 0; i < idx.Count; i++)
                {
                    int a = idx[(i + idx.Count - 1) % idx.Count], b = idx[i], c = idx[(i + 1) % idx.Count];
                    double cross = (px[b] - px[a]) * (py[c] - py[a]) - (py[b] - py[a]) * (px[c] - px[a]);
                    if (cross <= 1e-12) continue;                    // reflex or degenerate corner
                    bool ok = true;
                    for (int k = 0; k < idx.Count && ok; k++)
                    {
                        int p = idx[k];
                        if (p == a || p == b || p == c) continue;
                        if (PointInTri2D(px, py, p, a, b, c)) ok = false;
                    }
                    if (!ok) continue;
                    double q = TriQuality2D(px, py, a, b, c);
                    if (q > bestQ) { bestQ = q; bestI = i; }
                }
                if (bestI < 0) return false;                         // no valid ear: leave the fan alone
                int bb = idx[bestI];
                int aa = idx[(bestI + idx.Count - 1) % idx.Count];
                int cc = idx[(bestI + 1) % idx.Count];
                EmitTri(v, ring, aa, bb, cc, outTris);
                idx.RemoveAt(bestI);
            }
            if (idx.Count == 3) EmitTri(v, ring, idx[0], idx[1], idx[2], outTris);
            return outTris.Count >= 3;
        }

        // Emits a ring-local triangle as world indices, with the surface's (+Y) winding.
        private static void EmitTri(float[] v, List<int> ring, int a, int b, int c, List<int> outTris)
        {
            int va = ring[a], vb = ring[b], vc = ring[c];
            if (NormalY(v, va, vb, vc) < 0) { int t = vb; vb = vc; vc = t; }
            outTris.Add(va); outTris.Add(vb); outTris.Add(vc);
        }

        // Strictly-inside test for a CCW triangle in the fixed 2D projection (points on an edge do not
        // block an ear - sliver rings are full of collinear vertices).
        private static bool PointInTri2D(double[] px, double[] py, int p, int a, int b, int c)
        {
            double d1 = (px[b] - px[a]) * (py[p] - py[a]) - (py[b] - py[a]) * (px[p] - px[a]);
            double d2 = (px[c] - px[b]) * (py[p] - py[b]) - (py[c] - py[b]) * (px[p] - px[b]);
            double d3 = (px[a] - px[c]) * (py[p] - py[c]) - (py[a] - py[c]) * (px[p] - px[c]);
            return d1 > 1e-12 && d2 > 1e-12 && d3 > 1e-12;
        }

        private static double TriQuality2D(double[] px, double[] py, int a, int b, int c)
        {
            double e0 = Hypot2D(px[b] - px[a], py[b] - py[a]);
            double e1 = Hypot2D(px[c] - px[b], py[c] - py[b]);
            double e2 = Hypot2D(px[a] - px[c], py[a] - py[c]);
            double den = e0 * e0 + e1 * e1 + e2 * e2;
            if (den < 1e-12) return 0.0;
            double area = Math.Abs((px[b] - px[a]) * (py[c] - py[a]) - (py[b] - py[a]) * (px[c] - px[a])) * 0.5;
            return 4.0 * Math.Sqrt(3.0) * area / den;
        }

        private static double Hypot2D(double x, double y) { return Math.Sqrt(x * x + y * y); }

        private static double TriQuality(float[] v, int a, int b, int c)
        {
            double area = Area(v, a, b, c);
            double e0 = Len(v, a, b), e1 = Len(v, b, c), e2 = Len(v, c, a);
            double den = e0 * e0 + e1 * e1 + e2 * e2;
            return den > 1e-12 ? 4.0 * Math.Sqrt(3.0) * area / den : 0.0;
        }

        // Boundary flag per vertex: a vertex is on the outline when any incident edge is used by a single
        // face. (Also used by the diag, which must recompute it on the FINAL arrays.)
            // Boundary flag per vertex: a vertex is on the outline when any incident edge is used by a single
            // face. (Diag-only here - decimation derives its own copy from its MeshTopo, so the heavy
            // passes never build two dictionaries over the same mesh.)
            private static bool[] BoundaryFlags(List<int> tris, int nv)
        {
            var boundary = new bool[nv];
            var counts = new Dictionary<long, int>(LongHash.Instance);
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a < 0) continue;
                Bump(counts, EdgeKey(a, b), 1);
                Bump(counts, EdgeKey(b, c), 1);
                Bump(counts, EdgeKey(c, a), 1);
            }
            foreach (KeyValuePair<long, int> kv in counts)
            {
                if (kv.Value != 1) continue;
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffff);
                if (a >= 0 && a < nv) boundary[a] = true;
                if (b >= 0 && b < nv) boundary[b] = true;
            }
            return boundary;
        }

        private static void Bump(Dictionary<long, int> d, long k, int v)
        {
            int n;
            d.TryGetValue(k, out n);
            d[k] = n + v;
        }

        private static long FaceKey(int a, int b, int c)
        {
            int x = Math.Min(a, Math.Min(b, c));
            int z = Math.Max(a, Math.Max(b, c));
            int y = a + b + c - x - z;
            return ((long)x * 1000003L + y) * 1000003L + z;
        }

        // Long-keyed maps. The raw 64-bit keys here are STRUCTURED (vertex index pairs/triples from a
        // raster mesh), and .NET's default Int64 hash (hi ^ lo) collapses them into a few buckets: the
        // dictionaries degenerated into quadratic chains (CC_20: ~80 s per map). This bijective splitmix
        // finalizer restores a uniform hash WITHOUT changing the keys, so equality is untouched.
        private sealed class LongHash : IEqualityComparer<long>
        {
            public static readonly LongHash Instance = new LongHash();
            public bool Equals(long x, long y) { return x == y; }
            public int GetHashCode(long v)
            {
                ulong z = (ulong)v;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z = z ^ (z >> 31);
                return (int)z;
            }
        }

        // Topology guard: the export's invariant gate refuses maxEdgeMult > 2 or duplicate faces, so the
        // cleanup must never produce them (the first 0.30 m tolerance run did, and the mesh stopped
        // shipping). Keeps the whole mesh's edge multiplicities and face keys, and validates each
        // removal's deltas BEFORE they are applied.
        private sealed class MeshTopo
        {
            public readonly Dictionary<long, int> EdgeCount = new Dictionary<long, int>(LongHash.Instance);
            public readonly HashSet<long> FaceKeys = new HashSet<long>(LongHash.Instance);

            public static MeshTopo Build(List<int> tris)
            {
                var t = new MeshTopo();
                for (int i = 0; i + 2 < tris.Count; i += 3)
                {
                    int a = tris[i], b = tris[i + 1], c = tris[i + 2];
                    if (a < 0) continue;
                    t.FaceKeys.Add(FaceKey(a, b, c));
                    t.AddEdge(a, b);
                    t.AddEdge(b, c);
                    t.AddEdge(c, a);
                }
                return t;
            }

            private void AddEdge(int a, int b)
            {
                long k = EdgeKey(a, b);
                int n;
                EdgeCount.TryGetValue(k, out n);
                EdgeCount[k] = n + 1;
            }

            private void RemoveEdge(int a, int b)
            {
                long k = EdgeKey(a, b);
                int n;
                if (!EdgeCount.TryGetValue(k, out n)) return;
                if (n <= 1) EdgeCount.Remove(k); else EdgeCount[k] = n - 1;
            }

            // Can `oldFaces` (still present in `tris`) be replaced by `newTris` without a duplicate face
            // or an edge with more than two faces?
            public bool CanReplace(List<int> tris, List<int> oldFaces, List<int> newTris)
            {
                for (int i = 0; i + 2 < newTris.Count; i += 3)
                {
                    int a = newTris[i], b = newTris[i + 1], c = newTris[i + 2];
                    if (a < 0 || a == b || b == c || a == c) return false;
                    if (FaceKeys.Contains(FaceKey(a, b, c))) return false;
                }
                var delta = new Dictionary<long, int>();
                for (int i = 0; i < oldFaces.Count; i++)
                {
                    int f = oldFaces[i];
                    int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                    if (a < 0) continue;
                    Bump(delta, EdgeKey(a, b), -1);
                    Bump(delta, EdgeKey(b, c), -1);
                    Bump(delta, EdgeKey(c, a), -1);
                }
                for (int i = 0; i + 2 < newTris.Count; i += 3)
                {
                    int a = newTris[i], b = newTris[i + 1], c = newTris[i + 2];
                    Bump(delta, EdgeKey(a, b), 1);
                    Bump(delta, EdgeKey(b, c), 1);
                    Bump(delta, EdgeKey(c, a), 1);
                }
                foreach (KeyValuePair<long, int> kv in delta)
                {
                    int n;
                    EdgeCount.TryGetValue(kv.Key, out n);
                    int after = n + kv.Value;
                    if (after < 0 || after > 2) return false;
                }
                return true;
            }

            // Commits a replacement. MUST run before the new triangles are written into the `tris` slots
            // (it reads the old faces from there).
            public void Commit(List<int> tris, List<int> oldFaces, List<int> newTris)
            {
                for (int i = 0; i < oldFaces.Count; i++)
                {
                    int f = oldFaces[i];
                    int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                    if (a < 0) continue;
                    FaceKeys.Remove(FaceKey(a, b, c));
                    RemoveEdge(a, b);
                    RemoveEdge(b, c);
                    RemoveEdge(c, a);
                }
                for (int i = 0; i + 2 < newTris.Count; i += 3)
                {
                    int a = newTris[i], b = newTris[i + 1], c = newTris[i + 2];
                    FaceKeys.Add(FaceKey(a, b, c));
                    AddEdge(a, b);
                    AddEdge(b, c);
                    AddEdge(c, a);
                }
            }
        }

        // Capped diagnostic for the run log: for each surviving needle, the altitude/length and, for each
        // of its vertices, why removal is refused (or "OK" if a later sweep should have taken it).
        private static List<string> NeedleDiag(List<float> verts, List<int> tris, int cap)
        {
            var lines = new List<string>();
            var v = verts.ToArray();
            int[] start, faces;
            BuildFaceAdjacency(tris, out start, out faces);
            var boundary = BoundaryFlags(tris, verts.Count / 3);
            for (int t = 0; t + 2 < tris.Count && lines.Count < cap; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a < 0 || !IsNeedle(v, a, b, c)) continue;
                double e0 = Len(v, a, b), e1 = Len(v, b, c), e2 = Len(v, c, a);
                double longest = Math.Max(e0, Math.Max(e1, e2));
                double alt = longest > 1e-9 ? 2.0 * Area(v, a, b, c) / longest : 0.0;
                string line = "needle alt=" + alt.ToString("0.###", CultureInfo.InvariantCulture)
                    + " longest=" + longest.ToString("0.##", CultureInfo.InvariantCulture);
                int[] vs = { a, b, c };
                for (int k = 0; k < 3; k++)
                {
                    line += " v" + vs[k] + (boundary[vs[k]] ? "(B)=" : "(I)=")
                        + RejectReason(tris, v, start, faces, boundary, vs[k]);
                }
                lines.Add(line);
            }
            return lines;
        }

        // Read-only twin of TryRemoveVertex's guard chain: which guard refuses `vert` (or "OK").
        private static string RejectReason(List<int> tris, float[] v, int[] start, int[] faces,
            bool[] boundary, int vert)
        {
            int nf = tris.Count / 3;
            var incident = new List<int>();
            for (int i = start[vert]; i < start[vert + 1]; i++)
            {
                int f = faces[i];
                if (f < 0 || f >= nf) continue;
                if (tris[f * 3] < 0) continue;
                if (tris[f * 3] != vert && tris[f * 3 + 1] != vert && tris[f * 3 + 2] != vert) continue;
                incident.Add(f);
            }
            if (incident.Count == 1)
            {
                int f = incident[0];
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                int p, q;
                if (a == vert) { p = b; q = c; }
                else if (b == vert) { p = c; q = a; }
                else { p = a; q = b; }
                double sd = PointSegDistance(v, vert, p, q);
                if (!(sd <= SurfaceToleranceMeters))
                    return "spike(d=" + sd.ToString("0.###", CultureInfo.InvariantCulture) + ")";
                int sh = 0;
                for (int i = start[p]; i < start[p + 1]; i++)
                {
                    int g = faces[i];
                    if (g < 0 || g >= nf) continue;
                    int ga = tris[g * 3], gb = tris[g * 3 + 1], gc = tris[g * 3 + 2];
                    if (ga < 0) continue;
                    if ((ga == p || gb == p || gc == p) && (ga == q || gb == q || gc == q)) sh++;
                }
                if (sh != 2) return "spike(shared=" + sh + ")";
                return "OK";
            }
            if (!FanPlanar(v, tris, incident)) return "fanSpread";
            var ring = new List<int>();
            if (!BuildRing(tris, incident, vert, ring)) return RingReason(tris, incident, vert);
            if (ring.Count < 3) return "ring<3";
            double pm = PlaneMove(v, vert, ring);
            float tol = boundary[vert] ? BoundaryToleranceMeters : SurfaceToleranceMeters;
            if (!(pm <= tol))
                return "plane=" + pm.ToString("0.###", CultureInfo.InvariantCulture) + ">";
            if (boundary[vert])
            {
                float om = OutlineMove(v, vert, ring);
                if (om > BoundaryToleranceMeters)
                    return "outline=" + om.ToString("0.###", CultureInfo.InvariantCulture);
            }
            var nt = new List<int>();
            if (!TriangulateRing(v, ring, nt)) return "tri";
            for (int i = 0; i + 2 < nt.Count; i += 3)
                if (IsNeedle(v, nt[i], nt[i + 1], nt[i + 2])) return "newNeedle";
            return "OK";
        }

        // 3D distance from vertex `vert` to the segment (p,q).
        private static double PointSegDistance(float[] v, int vert, int p, int q)
        {
            double ax = v[p * 3], ay = v[p * 3 + 1], az = v[p * 3 + 2];
            double dx = v[q * 3] - ax, dy = v[q * 3 + 1] - ay, dz = v[q * 3 + 2] - az;
            double px = v[vert * 3] - ax, py = v[vert * 3 + 1] - ay, pz = v[vert * 3 + 2] - az;
            double len2 = dx * dx + dy * dy + dz * dz;
            double t = len2 > 1e-12 ? (px * dx + py * dy + pz * dz) / len2 : 0.0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double ex = px - t * dx, ey = py - t * dy, ez = pz - t * dz;
            return Math.Sqrt(ex * ex + ey * ey + ez * ez);
        }

        // Explains a BuildRing refusal (diagnostic path only): the fan's degree profile.
        private static string RingReason(List<int> tris, List<int> incident, int vert)
        {
            var adj = new Dictionary<int, List<int>>();
            for (int i = 0; i < incident.Count; i++)
            {
                int f = incident[i];
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                int p, q;
                if (a == vert) { p = b; q = c; }
                else if (b == vert) { p = c; q = a; }
                else if (c == vert) { p = a; q = b; }
                else return "ring(vert-absent)";
                if (p == q || p == vert || q == vert) return "ring(degenerate-edge)";
                AddAdj(adj, p, q);
                AddAdj(adj, q, p);
            }
            if (adj.Count < 3) return "ring(small=" + adj.Count + ")";
            int ends = 0, maxd = 0;
            foreach (KeyValuePair<int, List<int>> kv in adj)
            {
                int d = kv.Value.Count;
                if (d > maxd) maxd = d;
                if (d == 1) ends++;
            }
            return "ring(e=" + ends + ",d=" + maxd + ",n=" + adj.Count + ",i=" + incident.Count + ")";
        }

        // ---- 4. flip polish -----------------------------------------------------------------------
        private static void FlipEdges(float[] v, List<int> tris, Stats st, int maxMs,
            System.Diagnostics.Stopwatch watch)
        {
            var edgeFaces = BuildEdgeFaces(tris);
            int nf = tris.Count / 3;
            var touched = new bool[nf];
            for (int iter = 0; iter < 6; iter++)
            {
                bool any = false;
                var keys = new List<long>(edgeFaces.Keys);
                keys.Sort();
                foreach (long key in keys)
                {
                    if (watch.ElapsedMilliseconds > maxMs) return;
                    List<int> fl;
                    if (!edgeFaces.TryGetValue(key, out fl) || fl.Count != 2) continue;
                    int f0 = fl[0], f1 = fl[1];
                    if (touched[f0] || touched[f1]) continue;
                    if (!TryFlip(v, tris, f0, f1, key, edgeFaces, st)) continue;
                    touched[f0] = true; touched[f1] = true; any = true;
                }
                if (!any) break;
                edgeFaces = BuildEdgeFaces(tris);
                for (int i = 0; i < nf; i++) touched[i] = false;
            }
        }

        private static long EdgeKey(int a, int b)
        {
            int lo = a < b ? a : b, hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        private static Dictionary<long, List<int>> BuildEdgeFaces(List<int> tris)
        {
            var map = new Dictionary<long, List<int>>(LongHash.Instance);
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a < 0) continue;
                AddFace(map, EdgeKey(a, b), t / 3);
                AddFace(map, EdgeKey(b, c), t / 3);
                AddFace(map, EdgeKey(c, a), t / 3);
            }
            return map;
        }

        private static void AddFace(Dictionary<long, List<int>> map, long key, int face)
        {
            List<int> l;
            if (!map.TryGetValue(key, out l)) { l = new List<int>(2); map[key] = l; }
            if (l.Count < 4) l.Add(face);
        }

        private static bool TryFlip(float[] v, List<int> tris, int f0, int f1, long sharedKey,
            Dictionary<long, List<int>> edges, Stats st)
        {
            int u = (int)(sharedKey >> 32), w = (int)(sharedKey & 0xffffffff);
            int x = OtherVertex(tris, f0, u, w), y = OtherVertex(tris, f1, u, w);
            if (x < 0 || y < 0 || x == y) return false;
            if (!QuadConvex(v, u, x, w, y)) return false;
            double coplanar = NormalAngleDeg(v, u, x, w, u, y, w);
            if (coplanar > 90) coplanar = 180 - coplanar;
            if (coplanar > FlipNormalAngleDegrees) return false;
            if (DistToPlane(v, u, x, w, y) > FlipSurfaceMoveMeters) { st.RejMove++; return false; }
            double before = Math.Min(MinAngle(v, u, x, w), MinAngle(v, u, y, w));
            double after = Math.Min(MinAngle(v, x, y, u), MinAngle(v, x, y, w));
            if (after < before + FlipMinAngleGainDegrees) return false;
            List<int> el;
            if (edges.TryGetValue(EdgeKey(x, y), out el))
                for (int i = 0; i < el.Count; i++) if (el[i] != f0 && el[i] != f1) return false;
            if (UseCount(edges, u, x) < 2 || UseCount(edges, x, w) < 2
                || UseCount(edges, w, y) < 2 || UseCount(edges, y, u) < 2) return false;
            int a0 = tris[f0 * 3], b0 = tris[f0 * 3 + 1], c0 = tris[f0 * 3 + 2];
            int a1 = tris[f1 * 3], b1 = tris[f1 * 3 + 1], c1 = tris[f1 * 3 + 2];
            double ny0 = NormalY(v, a0, b0, c0), ny1 = NormalY(v, a1, b1, c1);
            tris[f0 * 3] = x; tris[f0 * 3 + 1] = y; tris[f0 * 3 + 2] = u;
            if (NormalY(v, x, y, u) * ny0 < 0) { int tmp = tris[f0 * 3 + 1]; tris[f0 * 3 + 1] = tris[f0 * 3 + 2]; tris[f0 * 3 + 2] = tmp; }
            tris[f1 * 3] = x; tris[f1 * 3 + 1] = y; tris[f1 * 3 + 2] = w;
            if (NormalY(v, x, y, w) * ny1 < 0) { int tmp = tris[f1 * 3 + 1]; tris[f1 * 3 + 1] = tris[f1 * 3 + 2]; tris[f1 * 3 + 2] = tmp; }
            st.Flipped++;
            return true;
        }

        private static int OtherVertex(List<int> tris, int f, int u, int w)
        {
            int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
            if (a != u && a != w) return a;
            if (b != u && b != w) return b;
            if (c != u && c != w) return c;
            return -1;
        }

        private static int UseCount(Dictionary<long, List<int>> edges, int a, int b)
        {
            List<int> l;
            return edges.TryGetValue(EdgeKey(a, b), out l) ? l.Count : 0;
        }

        private static bool QuadConvex(float[] v, int u, int x, int w, int y)
        {
            if (SideXZ(v, u, w, x) * SideXZ(v, u, w, y) >= -1e-9) return false;
            if (SideXZ(v, x, y, u) * SideXZ(v, x, y, w) >= -1e-9) return false;
            return true;
        }

        private static double SideXZ(float[] v, int a, int b, int p)
        {
            return (v[b * 3] - v[a * 3]) * (v[p * 3 + 2] - v[a * 3 + 2])
                 - (v[b * 3 + 2] - v[a * 3 + 2]) * (v[p * 3] - v[a * 3]);
        }

        private static double DistToPlane(float[] v, int u, int x, int w, int y)
        {
            double[] n = Normal(v, u, x, w);
            double len = Mat(n);
            if (len < 1e-12) return 0.0;
            return Math.Abs((v[y * 3] - v[u * 3]) * n[0] + (v[y * 3 + 1] - v[u * 3 + 1]) * n[1]
                + (v[y * 3 + 2] - v[u * 3 + 2]) * n[2]) / len;
        }

        private static double NormalY(float[] v, int a, int b, int c)
        {
            double ux = v[b * 3] - v[a * 3], uz = v[b * 3 + 2] - v[a * 3 + 2];
            double vx = v[c * 3] - v[a * 3], vz = v[c * 3 + 2] - v[a * 3 + 2];
            return uz * vx - ux * vz;
        }

        private static double[] Normal(float[] v, int a, int b, int c)
        {
            double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            double vx = v[c * 3] - v[a * 3], vy = v[c * 3 + 1] - v[a * 3 + 1], vz = v[c * 3 + 2] - v[a * 3 + 2];
            return new double[] { uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx };
        }

        private static double Mat(double[] n) { return Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]); }

        private static double NormalAngleDeg(float[] v, int a, int b, int c, int d, int e, int f)
        {
            double[] n0 = Normal(v, a, b, c), n1 = Normal(v, d, e, f);
            double dot = n0[0] * n1[0] + n0[1] * n1[1] + n0[2] * n1[2];
            double l0 = Mat(n0), l1 = Mat(n1);
            if (l0 < 1e-12 || l1 < 1e-12) return 180.0;
            double cosA = dot / (l0 * l1);
            if (cosA > 1) cosA = 1; if (cosA < -1) cosA = -1;
            return Math.Acos(cosA) * 180.0 / Math.PI;
        }

        private static double AngleDeg(double[] n0, double[] n1)
        {
            double dot = n0[0] * n1[0] + n0[1] * n1[1] + n0[2] * n1[2];
            double l0 = Mat(n0), l1 = Mat(n1);
            if (l0 < 1e-12 || l1 < 1e-12) return 180.0;
            double cosA = dot / (l0 * l1);
            if (cosA > 1) cosA = 1; if (cosA < -1) cosA = -1;
            return Math.Acos(cosA) * 180.0 / Math.PI;
        }
    }
}
