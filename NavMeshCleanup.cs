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
        // Surface-move tolerance for an interior vertex removal. This is the knob that decides how much of
        // the thin fan population can fold away: at 0.20 m the guard refused ~240k candidate folds on
        // Birtu alone. 0.30 m equals the contour simplification's own budget (1.3 vox at cs 0.25 m), i.e.
        // the surface may be re-shaped by no more than the mesher itself already promises.
        public const float SurfaceToleranceMeters = 0.50f;
        // Outline-move tolerance for a boundary vertex removal.
        public const float BoundaryToleranceMeters = 0.35f;
        // A fan whose triangle normals spread more than this (degrees) is a step/corner/rim: skip it.
        // Kept generous on purpose - the real bound on a fold is the move tolerance below, and a tight
        // spread cap was refusing ordinary bumpy ground.
        public const float MaxFanNormalSpreadDegrees = 120f;
        // Needle: shortest altitude < 0.05 m AND longest edge > 0.5 m. Thin: min angle < 5 deg.
        public const float NeedleAltitudeMeters = 0.05f;
        public const float NeedleLongestEdgeMeters = 0.5f;
        public const float ThinMinAngleDegrees = 5f;
        // Decimation candidates: vertices of a face thinner than this, or of an edge shorter than
        // ShortEdgeMeters (a redundant point). The user's screenshot: long thin fans over open ground -
        // 1-2 m wide on 10 m bases - are NOT "needles" by the altitude metric, so the candidate set has to
        // be the thin-edge/thin-face population, not just the extreme needles.
        public const float CandidateMinAngleDegrees = 15f;
        public const float ShortEdgeMeters = 0.25f;
        // A replacement face must clear this min angle. (The old rule refused anything it called a needle,
        // which blocked nearly every legitimate fold.)
        public const float NewFaceMinAngleDegrees = 1.5f;
        // Components at or below this face count are left completely alone (see the guard in Decimate): a
        // fold would otherwise be able to delete a small island outright.
        public const int SmallComponentFaces = 16;
        // Fans containing any face steeper than this are never touched: step risers, staircase treads, the
        // synthesised ramps and wall bases are load-bearing geometry, and folding them is what dropped the
        // mesh under the top of staircases.
        public const float MaxCleanupSlopeDegrees = 40f;
        // A fold or slide may not change the surface height by more than this at the affected point: this is
        // the direct form of the staircase defect (the mesh must stay ~0.5 m above the collision surface).
        public const float MaxHeightChangeMeters = 0.15f;
        // Flip polish guards (from V3.35, with its bugs fixed).
        public const float FlipNormalAngleDegrees = 18f;
        public const float FlipSurfaceMoveMeters = 0.15f;
        public const float FlipMinAngleGainDegrees = 0.5f;
        // Quad-merge (the final stage). A face is a candidate while it is still thin by either
        // predicate (min angle < 6 deg OR aspect longest^2/(2*area) > 8); it may then be combined
        // with a neighbour across one shared edge into a single 4-vertex face. The two faces must be
        // near-coplanar (fold <= this many degrees) so the quad is a sensible polygon and step rims /
        // stacked levels stay out, the four vertices must be distinct, the quad walk must be simple
        // with the surface's winding, and neither face may already be merged.
        public const float QuadMergeThinMinAngleDegrees = 6f;
        public const float QuadMergeThinAspect = 8f;
        public const float QuadMergeFoldMaxDegrees = 25f;

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
            public int Relaxed;            // thin vertices slid to a better in-plane position
            public float SlideMax;
            public int Collapsed;          // short-edge merges (redundant points)
            public int Refined;            // long thin triangles refined by an edge split
            // Quad merge (final stage): thin faces combined with a neighbour into one emitted
            // 4-vertex face. Geometry and the triangle list are unchanged - only the shared interior
            // edge is deleted from the EMITTED face perimeters. QuadPairs holds two face indices per
            // quad in the FINAL mesh's face numbering (no compaction happens after this stage).
            public int Quads;
            public List<int> QuadPairs;
            public string EdgeHist;
            // Why each surviving needle's vertices were not removable (capped; goes to the run log).
            public List<string> Diag;
            // One-line decimation summary (budget/mask/sweeps); goes to the run log.
            public string DecimDiag;
        }

        // vertsXyz: flat x,y,z. tris: flat indices. maxMilliseconds bounds the decimation + flips.
        public static Stats Run(List<float> vertsXyz, List<int> tris, int maxMilliseconds,
            bool quadMerge = true)
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

            // Long thin triangles get an extra vertex FIRST (see RefineLongThin): the user's taxonomy -
            // short needles are removed, medium ones are slid, long ones need an inserted vertex - and the
            // user's ordering instruction - the insert pass runs before removal/slide, so anything it
            // creates is remeasured and cleaned up by them. The split itself is surface-exact.
            //
            // Refine draws on the same wall clock, so the later passes' existing deadlines are shifted by
            // however long it actually took: running the insert pass first must not starve the primary
            // decimation/slide levers (V3.38's windows are preserved on top of refine).
            long runStart = watch.ElapsedMilliseconds;
            if (EnableRefine)
                RefineLongThin(vertsXyz, tris, st,
                    (int)(watch.ElapsedMilliseconds + maxMilliseconds), watch);
            long refineUsed = watch.ElapsedMilliseconds - runStart;
            // Defensive: drop any sentinel slot an early-exiting sweep left behind, then drop the vertices
            // no face references and renumber. Indices are renumbered only at this boundary; each pass that
            // relies on stable indices (refinement, decimation) finishes before its own compaction.
            CompactVertices(vertsXyz, tris);
            v = vertsXyz.ToArray();

            Decimate(vertsXyz, tris, st, (int)(maxMilliseconds + refineUsed), watch);
            CompactVertices(vertsXyz, tris);
            v = vertsXyz.ToArray();
            // Slide thin vertices to a better in-plane position (ratio needles cannot be folded away - see
            // RelaxThin), then polish with Delaunay-style flips.
            RelaxThin(vertsXyz, tris, st, (int)(maxMilliseconds + 1500 + refineUsed), watch);
            v = vertsXyz.ToArray();
            // The flip polish gets its own window: decimation is allowed to use the whole budget (it is
            // the primary lever), and squeezing the flips into the remainder left them doing nothing.
            FlipEdges(v, tris, st, (int)(maxMilliseconds + 2500 + refineUsed), watch);

            // The quad merge is the LAST stage and is purely additive: it does not move a vertex, does
            // not add/remove/reorder a face, and does not touch `tris` at all - it only records which
            // triangle pairs the writer should emit as a single 4-vertex face (identical geometry, the
            // shared interior edge simply disappears from the emitted perimeter). It runs after the flip
            // polish, the last pass that rewrites face contents; from here to the end of Run nothing
            // compacts `tris`, so the recorded face indices are exactly the final mesh's numbering.
            if (quadMerge) MergeQuads(vertsXyz, tris, st);

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
                // Never touch a tiny component: folding the only vertices of a 1-2 face island would delete
                // it outright (the island structure must not change).
                int nf0 = tris.Count / 3;
                var faceComp = new int[nf0];
                for (int i = 0; i < nf0; i++) faceComp[i] = -1;
                var compSize = new List<int>();
                var queue = new Queue<int>();
                for (int f = 0; f < nf0; f++)
                {
                    if (faceComp[f] >= 0) continue;
                    int id = compSize.Count;
                    compSize.Add(0);
                    faceComp[f] = id;
                    queue.Enqueue(f);
                    while (queue.Count > 0)
                    {
                        int g = queue.Dequeue();
                        compSize[id]++;
                        for (int k = 0; k < 3; k++)
                        {
                            int w = tris[g * 3 + k];
                            if (w < 0 || w >= csrVerts) continue;
                            for (int j = ringStart[w]; j < ringStart[w + 1]; j++)
                            {
                                int h = ringFaces[j];
                                if (h < 0 || h >= nf0 || faceComp[h] >= 0) continue;
                                faceComp[h] = id;
                                queue.Enqueue(h);
                            }
                        }
                    }
                }
                // Candidate mask for this sweep: only vertices of a needle / very thin face. One O(F)
                // pass over the faces beats testing every vertex's fan with the full guard set (that was
                // spending the whole budget on ordinary bumpy ground).
                var cand = new bool[csrVerts];
                for (int t = 0; t + 2 < tris.Count; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (a < 0) continue;
                    if (MinAngle(v, a, b, c) >= CandidateMinAngleDegrees
                        && Len(v, a, b) >= ShortEdgeMeters && Len(v, b, c) >= ShortEdgeMeters
                        && Len(v, c, a) >= ShortEdgeMeters) continue;
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
                    {
                        int f0 = -1;
                        for (int j = ringStart[vert]; j < ringStart[vert + 1] && f0 < 0; j++)
                        {
                            int f = ringFaces[j];
                            if (f >= 0 && f < nf0 && tris[f * 3] >= 0) f0 = f;
                        }
                        if (f0 < 0) continue;
                        if (compSize[faceComp[f0]] <= SmallComponentFaces) continue;
                    }
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
                if (MinAngle(v, a, b, c) < CandidateMinAngleDegrees) return true;
                if (Len(v, a, b) < ShortEdgeMeters || Len(v, b, c) < ShortEdgeMeters
                    || Len(v, c, a) < ShortEdgeMeters) return true;
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
                double spikeTol = boundary[vert] ? BoundaryToleranceMeters : SurfaceToleranceMeters;
                if (!(d <= spikeTol)) { st.RejMove++; return false; }
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
                if (MinAngle(v, newTris[i], newTris[i + 1], newTris[i + 2]) < NewFaceMinAngleDegrees)
                { st.RejTopo++; return false; }
            }

            // No fold-overs, no corner cuts: the replacement must not GROW the local surface (a bulge pushes
            // the mesh into the neighbouring geometry - this is what let the corridor corner get cut and let
            // the mesh double back on itself), and every new face must still face the way the removed fan did.
            double areaBefore = 0.0;
            double ax = 0, ay = 0, az = 0;
            for (int i = 0; i < incident.Count; i++)
            {
                int f = incident[i];
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                if (a < 0) continue;
                areaBefore += Area(v, a, b, c);
                double[] nn = Normal(v, a, b, c);
                ax += nn[0]; ay += nn[1]; az += nn[2];
            }
            double areaAfter = 0.0;
            double alen = Math.Sqrt(ax * ax + ay * ay + az * az);
            for (int i = 0; i + 2 < newTris.Count; i += 3)
            {
                int a = newTris[i], b = newTris[i + 1], c = newTris[i + 2];
                areaAfter += Area(v, a, b, c);
                double[] nn = Normal(v, a, b, c);
                double l1 = Mat(nn);
                if (alen < 1e-12 || l1 < 1e-12) { st.RejTopo++; return false; }
                double cosA = (nn[0] * ax + nn[1] * ay + nn[2] * az) / (alen * l1);
                if (cosA < 0.5) { st.RejTopo++; return false; }        // > 60 deg off the fan's facing
            }
            double growth = areaAfter - areaBefore;
            double growLimit = boundary[vert] ? 0.01 * areaBefore + 1e-6 : 0.05 * areaBefore + 1e-6;
            if (growth > growLimit) { st.RejTopo++; return false; }
            if (!boundary[vert] && growth < -0.05 * areaBefore) { st.RejTopo++; return false; }

            // The replacement must not drop (or lift) the surface at the removed vertex: this is the
            // staircase defect in its direct form. NaN means the spot is no longer covered (the outline
            // chord cut it off) - the boundary rules already bound that move.
            double newAlt = SurfaceAlt(v, newTris, v[vert * 3], v[vert * 3 + 2]);
            if (!double.IsNaN(newAlt) && Math.Abs(newAlt - v[vert * 3 + 1]) > MaxHeightChangeMeters)
            { st.RejMove++; return false; }

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

        // ---- 3b. RELAX: slide thin vertices -----------------------------------------------------------------
        // Ratio needles - the user's canonical case is a 10 m fan edge with a 1 m wide apex - cannot be
        // folded away: the apex sits a metre from the neighbouring geometry and removing it leaves a
        // star-shaped hole that retriangulates just as badly. But they are almost always badly PLACED:
        // sliding the apex within the local surface plane (so the height field is untouched) fattens the
        // whole fan. For each thin vertex this pass tries a small set of in-plane offsets and keeps the one
        // that maximises the worst incident angle, subject to the usual surface/outline bounds.
        public const float SlideRadiusMeters = 2.0f;
        public const float SlideMinGainDegrees = 1.0f;

        private static void RelaxThin(List<float> verts, List<int> tris, Stats st, int maxMs,
            System.Diagnostics.Stopwatch watch)
        {
            int nv = verts.Count / 3;
            var v = verts.ToArray();
            for (int sweep = 0; sweep < 3; sweep++)
            {
                int[] ringStart, ringFaces;
                BuildFaceAdjacency(tris, out ringStart, out ringFaces);
                var boundary = BoundaryFlags(tris, nv);
                bool any = false;
                int csrVerts = ringStart.Length - 1;
                for (int vert = 0; vert < csrVerts; vert++)
                {
                    if ((vert & 255) == 0 && watch.ElapsedMilliseconds > maxMs) goto done;
                    if (vert >= nv) continue;
                    if (!IncidentSliver(v, tris, ringStart, ringFaces, vert)) continue;
                    int nf = tris.Count / 3;
                    var incident = new List<int>();
                    for (int i = ringStart[vert]; i < ringStart[vert + 1]; i++)
                    {
                        int f = ringFaces[i];
                        if (f < 0 || f >= nf) continue;
                        if (tris[f * 3] < 0) continue;
                        if (tris[f * 3] != vert && tris[f * 3 + 1] != vert && tris[f * 3 + 2] != vert) continue;
                        incident.Add(f);
                    }
                    if (incident.Count < 3) continue;
                    if (!FanPlanar(v, tris, incident)) continue;
                    var ring = new List<int>();
                    if (!BuildRing(tris, incident, vert, ring)) continue;
                    if (ring.Count < 3) continue;
                    if (TryRelax(v, ring, vert, boundary[vert])) { st.Relaxed++; any = true; }
                }
                if (!any) break;
            }
            done:
            for (int i = 0; i < nv; i++)
            {
                verts[i * 3] = v[i * 3]; verts[i * 3 + 1] = v[i * 3 + 1]; verts[i * 3 + 2] = v[i * 3 + 2];
            }
        }

        // Worst min-angle over the fan (vert joined to each consecutive ring pair).
        private static double FanWorstAngle(float[] v, List<int> ring, int vert, bool boundary)
        {
            double worst = 180.0;
            int n = ring.Count;
            int last = boundary ? n - 1 : n;
            for (int i = 0; i < last; i++)
            {
                double ang = MinAngle(v, vert, ring[i], ring[(i + 1) % n]);
                if (ang < worst) worst = ang;
            }
            return worst;
        }

        // Try to slide `vert` inside its ring's plane so its worst incident angle improves. Returns false
        // (leaving it untouched) when no candidate helps or a bound would be crossed.
        private static bool TryRelax(float[] v, List<int> ring, int vert, bool boundary)
        {
            int k = ring.Count;
            // Ring plane (Newell) and in-plane basis.
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < k; i++) { cx += v[ring[i] * 3]; cy += v[ring[i] * 3 + 1]; cz += v[ring[i] * 3 + 2]; }
            cx /= k; cy /= k; cz /= k;
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < k; i++)
            {
                int a = ring[i], b = ring[(i + 1) % k];
                double ax = v[a * 3] - cx, ay = v[a * 3 + 1] - cy, az = v[a * 3 + 2] - cz;
                double bx = v[b * 3] - cx, by = v[b * 3 + 1] - cy, bz = v[b * 3 + 2] - cz;
                nx += ay * bz - az * by; ny += az * bx - ax * bz; nz += ax * by - ay * bx;
            }
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-12) return false;
            nx /= nl; ny /= nl; nz /= nl;
            double ux, uy, uz;
            if (Math.Abs(nx) <= Math.Abs(ny) && Math.Abs(nx) <= Math.Abs(nz)) { ux = 0; uy = -nz; uz = ny; }
            else if (Math.Abs(ny) <= Math.Abs(nz)) { ux = -nz; uy = 0; uz = nx; }
            else { ux = -ny; uy = nx; uz = 0; }
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (ul < 1e-12) return false;
            ux /= ul; uy /= ul; uz /= ul;
            double wx = ny * uz - nz * uy, wy = nz * ux - nx * uz, wz = nx * uy - ny * ux;
            // The original fan (vertex still at its position): used below to look up the surface height the
            // mesh had at any candidate spot. Keeping a slide within the height budget OF THAT is the exact
            // form of the staircase rule - it allows a slide along a smooth slope (where the original
            // surface at the new spot is the same height) and blocks one that would dip over a step.
            var fanTris = new List<int>();
            {
                int lastF = boundary ? k - 1 : k;
                for (int i = 0; i < lastF; i++)
                {
                    fanTris.Add(vert); fanTris.Add(ring[i]); fanTris.Add(ring[(i + 1) % k]);
                }
            }
            var px = new double[k];
            var py = new double[k];
            for (int i = 0; i < k; i++)
            {
                double dx = v[ring[i] * 3] - cx, dy = v[ring[i] * 3 + 1] - cy, dz = v[ring[i] * 3 + 2] - cz;
                px[i] = dx * ux + dy * uy + dz * uz;
                py[i] = dx * wx + dy * wy + dz * wz;
            }
            double fanArea0 = FanArea(v, ring, vert, boundary);

            double baseX = v[vert * 3], baseY = v[vert * 3 + 1], baseZ = v[vert * 3 + 2];
            double current = FanWorstAngle(v, ring, vert, boundary);
            double best = current + SlideMinGainDegrees;
            double bpx = baseX, bpy = baseY, bpz = baseZ;
            bool found = false;

            // Candidate offsets: the ring centroid projected into the plane, plus rings of directions.
            for (int c = 0; c < 25; c++)
            {
                double ox, oy, oz;
                if (c == 0)
                {
                    // Toward the centroid, scaled so the move is a fraction of the ring's radius.
                    double dx = cx - baseX, dy = cy - baseY, dz = cz - baseZ;
                    double dl = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dl < 1e-9) continue;
                    double take = Math.Min(dl, SlideRadiusMeters) / dl;
                    ox = dx * take; oy = dy * take; oz = dz * take;
                }
                else
                {
                    int dir = (c - 1) % 8;
                    int rad = (c - 1) / 8;                       // 0, 1, 2
                    double ang = dir * Math.PI / 4.0;
                    double r = SlideRadiusMeters * (rad == 0 ? 0.35 : rad == 1 ? 0.7 : 1.0);
                    double du = Math.Cos(ang) * r, dw = Math.Sin(ang) * r;
                    ox = ux * du + wx * dw; oy = uy * du + wy * dw; oz = uz * du + wz * dw;
                }
                double candX = baseX + ox, candY = baseY + oy, candZ = baseZ + oz;
                // The mesh's ORIGINAL height at the candidate spot (the fan still holds the vertex at its
                // old position): a slide may not dip or lift the surface by more than the height budget.
                double oldY = SurfaceAlt(v, fanTris, candX, candZ);
                if (!double.IsNaN(oldY) && Math.Abs(candY - oldY) > MaxHeightChangeMeters) continue;
                v[vert * 3] = (float)candX;
                v[vert * 3 + 1] = (float)candY;
                v[vert * 3 + 2] = (float)candZ;
                if (!FanOrientationsHold(v, ring, vert, baseX, baseY, baseZ)) continue;
                // The slide must stay inside its own fan (never push the apex past a ring edge, which is how
                // a face ends up folded back over its neighbour) and must not grow the local surface.
                double cdx = v[vert * 3] - cx, cdy = v[vert * 3 + 1] - cy, cdz = v[vert * 3 + 2] - cz;
                double c2x = cdx * ux + cdy * uy + cdz * uz;
                double c2y = cdx * wx + cdy * wy + cdz * wz;
                if (!InPoly2D(px, py, k, c2x, c2y)) continue;
                double fa = FanArea(v, ring, vert, boundary);
                if (fa > fanArea0 * 1.02 + 1e-6) continue;
                if (fa < fanArea0 * 0.5) continue;
                double ang2 = FanWorstAngle(v, ring, vert, boundary);
                if (ang2 <= best) continue;
                if (boundary)
                {
                    float om = OutlineMove(v, vert, ring);
                    if (om > BoundaryToleranceMeters) continue;
                }
                best = ang2;
                bpx = v[vert * 3]; bpy = v[vert * 3 + 1]; bpz = v[vert * 3 + 2];
                found = true;
            }
            v[vert * 3] = (float)bpx; v[vert * 3 + 1] = (float)bpy; v[vert * 3 + 2] = (float)bpz;
            return found;
        }

        // Every fan triangle must keep the orientation it had before the slide (an in-plane move that
        // inverts or collapses a triangle is rejected). Evaluated against the ORIGINAL position.
        private static bool FanOrientationsHold(float[] v, List<int> ring, int vert,
            double baseX, double baseY, double baseZ)
        {
            int k = ring.Count;
            for (int i = 0; i < k; i++)
            {
                int a = ring[i], b = ring[(i + 1) % k];
                double before = Orient3Y(v, baseX, baseY, baseZ, a, b);
                double after = Orient3Y(v, v[vert * 3], v[vert * 3 + 1], v[vert * 3 + 2], a, b);
                if (before > 0 && after <= 0) return false;
                if (before < 0 && after >= 0) return false;
            }
            return true;
        }

        private static double Orient3Y(float[] v, double px, double py, double pz, int a, int b)
        {
            double ux = v[a * 3] - px, uz = v[a * 3 + 2] - pz;
            double wx = v[b * 3] - px, wz = v[b * 3 + 2] - pz;
            return ux * wz - uz * wx;   // projected orientation about Y
        }

        // ---- 3c. REFINE: insert a vertex into long thin triangles -------------------------------------
        // The last class in the user's taxonomy: needles that span LONG distances cannot be fixed by moving
        // their ends and cannot be deleted (nothing else covers that span). Inserting one more point is the
        // answer - and it is the only surgery here that is EXACTLY surface-neutral: the new vertex is the
        // midpoint of an existing edge, so it lies on that edge, the split triangles stay in the same plane
        // and no tolerance is involved. Splitting the longest edge (a 2-4 split together with the face
        // across it, or a 1-2 split on an outline edge) halves the span, so repeated sweeps converge on the
        // target aspect ratio. The MeshTopo guard validates every split.
        // Aspect ratio = longest^2 / (2*area) = longest / altitude. The user's ratio taxonomy: a 10 m span
        // under 1 m wide is a needle, so a face is a long-thin candidate once it is 8x longer than wide.
        // The min-angle metric does NOT improve under a base split (the split halves the base, the apex
        // angle is unchanged); the aspect ratio is what converges - it roughly halves per split.
        public const float RefineAspectRatio = 8f;
        // The user measured the surviving long needles at 6-9 m (some 6-7 m); 4 m catches them with margin.
        public const float RefineMinEdgeMeters = 4f;
        // Only triangles with a real width get refined: an ultra-thin needle (a few centimetres wide) must
        // not be split - halving its base narrows the apex angle instead of widening the sliver - it belongs
        // to removal/slide. The long 10 m x 1 m class is what refinement is for.
        public const float RefineMinWidthMeters = 0.12f;
        public const int RefineMaxSplitsFactor = 20;   // at most faces/20 inserted vertices per mesh
        // Enabled by the exact edge-indexed neighbour lookup (BuildEdgeFaceIndex/SplitEdge): an interior
        // edge is ALWAYS split 2-4 with its true neighbour, so no T-junction is possible. (Deliberately not
        // a const: a const would make the body unreachable code and warn.)
        public static readonly bool EnableRefine = true;

        private static void RefineLongThin(List<float> verts, List<int> tris, Stats st, int maxMs,
            System.Diagnostics.Stopwatch watch)
        {
            int maxSplits = Math.Max(0, tris.Count / 3 / RefineMaxSplitsFactor);
            var v = verts.ToArray();
            int splits = 0;
            for (int sweep = 0; sweep < 8 && splits < maxSplits; sweep++)
            {
                // Exact edge -> faces index for this whole sweep, updated on every split (see SplitEdge).
                // Unlike a CSR snapshot it can always answer "who is across this edge?" mid-sweep, so an
                // interior split never leaves a T-junction. MeshTopo still validates each split's deltas.
                var edgeFaces = BuildEdgeFaceIndex(tris);
                MeshTopo topo = MeshTopo.Build(tris);
                int nv = verts.Count / 3;
                var boundary = BoundaryVertsFromIndex(edgeFaces, nv);
                var dirty = new bool[nv];
                bool any = false;
                for (int t = 0; t + 2 < tris.Count && splits < maxSplits; t += 3)
                {
                    if ((t & 1023) == 0 && watch.ElapsedMilliseconds > maxMs) goto done;
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    if (a < 0) continue;
                    if (IsDirty(dirty, a) || IsDirty(dirty, b) || IsDirty(dirty, c)) continue;
                    double e0 = Len(v, a, b), e1 = Len(v, b, c), e2 = Len(v, c, a);
                    int p, q, r;
                    double longest;
                    if (e0 >= e1 && e0 >= e2) { p = a; q = b; r = c; longest = e0; }
                    else if (e1 >= e2) { p = b; q = c; r = a; longest = e1; }
                    else { p = c; q = a; r = b; longest = e2; }
                    if (longest < RefineMinEdgeMeters) continue;
                    double area = Area(v, a, b, c);
                    if (area < 1e-12) continue;
                    // The user's ratio criterion: longest^2/(2*area) = longest/altitude. Halving the longest
                    // edge roughly halves this ratio, so repeated sweeps converge on RefineAspectRatio.
                    double aspect = longest * longest / (2.0 * area);
                    if (aspect <= RefineAspectRatio) continue;
                    double width = 2.0 * area / longest;
                    if (width < RefineMinWidthMeters) continue;   // ultra-thin: removal/slide's job
                    // Outline edges are split 1-2 (the spec's "outline edge, fine"). An INTERIOR edge is
                    // only split 2-4 when its whole face is interior: a 2-4 split sitting on the outline
                    // perturbs which boundary vertices the removal pass later folds away, which can shrink
                    // the mesh's coverage (harness Test 7 detailCoversFan dropped to 98.8% without this).
                    long pk = EdgeKey(p, q);
                    long pv;
                    if (!edgeFaces.TryGetValue(pk, out pv)) continue;
                    bool outline = (int)(pv >> 32) < 0;
                    if (!outline && (boundary[a] || boundary[b] || boundary[c])) continue;
                    if (!SplitEdge(verts, tris, ref v, edgeFaces, topo, t / 3, p, q, r, dirty, st))
                        continue;
                    splits++;
                    any = true;
                }
                if (!any) break;
            }
            done:
            st.Refined = splits;
            return;
        }

        // ---- exact edge -> faces index (per refine sweep) ----------------------------------------------
        // key = EdgeKey(a,b); value = two face indices packed into one long: face0 in the low 32 bits, face1
        // in the high 32 bits, -1 when absent (a third face is impossible - the input passes the invariant
        // gate with maxEdgeMult=2). Rebuilt at the start of every sweep and updated incrementally on every
        // split (remove the old faces' three edges, add the new faces' edges), exactly like MeshTopo.Commit
        // maintains the counts, so it is exact at all times - not just at sweep start.
        private static Dictionary<long, long> BuildEdgeFaceIndex(List<int> tris)
        {
            var map = new Dictionary<long, long>(LongHash.Instance);
            for (int t = 0; t + 2 < tris.Count; t += 3)
            {
                int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                if (a < 0) continue;
                int f = t / 3;
                IndexAddEdge(map, EdgeKey(a, b), f);
                IndexAddEdge(map, EdgeKey(b, c), f);
                IndexAddEdge(map, EdgeKey(c, a), f);
            }
            return map;
        }

        private static long PackFaces(int f0, int f1)
        {
            return ((long)f1 << 32) | (uint)f0;
        }

        // A boundary vertex is an endpoint of an edge used by a single face (the edge index's high slot
        // still holds -1). Refinement uses this to keep its interior 2-4 splits off the outline (an outline
        // edge may still be split 1-2, which adds only a vertex exactly on the boundary).
        private static bool[] BoundaryVertsFromIndex(Dictionary<long, long> map, int nv)
        {
            var boundary = new bool[nv];
            foreach (KeyValuePair<long, long> kv in map)
            {
                if ((int)(kv.Value >> 32) >= 0) continue;   // two faces: interior edge
                int a = (int)(kv.Key >> 32), b = (int)(kv.Key & 0xffffffffL);
                if (a >= 0 && a < nv) boundary[a] = true;
                if (b >= 0 && b < nv) boundary[b] = true;
            }
            return boundary;
        }

        private static void IndexAddEdge(Dictionary<long, long> map, long key, int face)
        {
            long packed;
            if (!map.TryGetValue(key, out packed)) { map[key] = PackFaces(face, -1); return; }
            int lo = (int)(packed & 0xffffffffL), hi = (int)(packed >> 32);
            if (lo < 0) lo = face; else if (hi < 0) hi = face;   // else a third face: cannot happen
            map[key] = PackFaces(lo, hi);
        }

        private static void IndexAddFace(Dictionary<long, long> map, int a, int b, int c, int f)
        {
            IndexAddEdge(map, EdgeKey(a, b), f);
            IndexAddEdge(map, EdgeKey(b, c), f);
            IndexAddEdge(map, EdgeKey(c, a), f);
        }

        private static void IndexRemoveFace(Dictionary<long, long> map, List<int> tris, int f)
        {
            int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
            if (a < 0) return;
            IndexRemoveEdge(map, EdgeKey(a, b), f);
            IndexRemoveEdge(map, EdgeKey(b, c), f);
            IndexRemoveEdge(map, EdgeKey(c, a), f);
        }

        private static void IndexRemoveEdge(Dictionary<long, long> map, long key, int face)
        {
            long packed;
            if (!map.TryGetValue(key, out packed)) return;
            int lo = (int)(packed & 0xffffffffL), hi = (int)(packed >> 32);
            if (lo == face) { lo = hi; hi = -1; }
            else if (hi == face) hi = -1;
            else return;
            if (lo < 0) map.Remove(key); else map[key] = PackFaces(lo, hi);
        }

        // Split the edge (p,q) of face `f0` at its midpoint m: (p,q,r) -> (p,m,r),(m,q,r); when the edge is
        // shared, the face `s` found in the exact edge index is split too (q,m,d),(m,p,d) so no T-junction
        // is left. This is the one surgery here that is EXACTLY surface-neutral: the inserted vertex IS the
        // midpoint of the split edge, so it lies on that edge, every new face stays in the plane of the face
        // it replaces, and no tolerance is involved. Children keep their parent's winding.
        private static bool SplitEdge(List<float> verts, List<int> tris, ref float[] v,
            Dictionary<long, long> edgeFaces, MeshTopo topo, int f0, int p, int q, int r,
            bool[] dirty, Stats st)
        {
            int nf = tris.Count / 3;
            if (f0 < 0 || f0 >= nf || p == q || p == r || q == r) return false;
            // Exact neighbour lookup: the edge exists (we are splitting it), so one slot must be f0.
            long key = EdgeKey(p, q);
            long packed;
            if (!edgeFaces.TryGetValue(key, out packed)) { st.RejTopo++; return false; }
            int lo = (int)(packed & 0xffffffffL), hi = (int)(packed >> 32);
            int s = lo == f0 ? hi : (hi == f0 ? lo : -1);
            if (s == -1) { st.RejTopo++; return false; }           // this face does not own the edge
            if (s >= nf) { st.RejTopo++; return false; }
            int d = -1;
            if (s >= 0)
            {
                d = OtherVertex(tris, s, p, q);
                if (d < 0 || d == r) { st.RejTopo++; return false; }
            }
            // Cross-check against MeshTopo's (kept) edge multiplicities: index and counts must agree, and an
            // interior edge whose neighbour cannot be found is refused (the V3.37 T-junction failure mode).
            int edgeMult; topo.EdgeCount.TryGetValue(key, out edgeMult);
            if (edgeMult != (s >= 0 ? 2 : 1)) { st.RejTopo++; return false; }

            int a0 = tris[f0 * 3], b0 = tris[f0 * 3 + 1], c0 = tris[f0 * 3 + 2];
            double ny0 = NormalY(v, a0, b0, c0);
            double ny1 = 0.0;
            if (s >= 0) ny1 = NormalY(v, tris[s * 3], tris[s * 3 + 1], tris[s * 3 + 2]);

            int m = verts.Count / 3;
            float mx = (v[p * 3] + v[q * 3]) * 0.5f;
            float my = (v[p * 3 + 1] + v[q * 3 + 1]) * 0.5f;
            float mz = (v[p * 3 + 2] + v[q * 3 + 2]) * 0.5f;

            var oldFaces = new List<int>(2);
            oldFaces.Add(f0);
            if (s >= 0) oldFaces.Add(s);
            var newTris = new List<int>(12);
            newTris.Add(p); newTris.Add(m); newTris.Add(r);
            newTris.Add(m); newTris.Add(q); newTris.Add(r);
            if (s >= 0)
            {
                newTris.Add(q); newTris.Add(m); newTris.Add(d);
                newTris.Add(m); newTris.Add(p); newTris.Add(d);
            }

            // The new vertex is added first so the children can be measured, then rolled back on refusal.
            verts.Add(mx); verts.Add(my); verts.Add(mz);
            if (v.Length < verts.Count) Array.Resize(ref v, verts.Count + 3);
            v[m * 3] = mx; v[m * 3 + 1] = my; v[m * 3 + 2] = mz;
            // Refuse a split that would itself manufacture a needle: an ultra-thin child belongs to the
            // removal/slide passes, not to insertion. (This is why the pass only splits faces with a real
            // width - and the footer of a long thin split can still narrow below the needle altitude.)
            for (int i = 0; i + 2 < newTris.Count; i += 3)
            {
                if (IsNeedle(v, newTris[i], newTris[i + 1], newTris[i + 2]))
                {
                    verts.RemoveRange(verts.Count - 3, 3);
                    st.RejTopo++;
                    return false;
                }
            }
            if (!topo.CanReplace(tris, oldFaces, newTris))
            {
                verts.RemoveRange(verts.Count - 3, 3);
                st.RejTopo++;
                return false;
            }
            topo.Commit(tris, oldFaces, newTris);

            OrientTri(newTris, 0, v, ny0);
            OrientTri(newTris, 3, v, ny0);
            if (s >= 0)
            {
                OrientTri(newTris, 6, v, ny1);
                OrientTri(newTris, 9, v, ny1);
            }

            // Update the index BEFORE `tris` is overwritten (removal reads the old faces).
            for (int i = 0; i < oldFaces.Count; i++) IndexRemoveFace(edgeFaces, tris, oldFaces[i]);
            int appendedBase = tris.Count / 3;
            for (int i = 0; i + 2 < newTris.Count; i += 3)
            {
                int k = i / 3;
                int f = k < oldFaces.Count ? oldFaces[k] : appendedBase + (k - oldFaces.Count);
                IndexAddFace(edgeFaces, newTris[i], newTris[i + 1], newTris[i + 2], f);
            }

            for (int i = 0; i < oldFaces.Count; i++)
            {
                int f = oldFaces[i];
                tris[f * 3] = newTris[i * 3]; tris[f * 3 + 1] = newTris[i * 3 + 1]; tris[f * 3 + 2] = newTris[i * 3 + 2];
            }
            for (int i = oldFaces.Count; i * 3 + 2 < newTris.Count; i++)
            {
                tris.Add(newTris[i * 3]); tris.Add(newTris[i * 3 + 1]); tris.Add(newTris[i * 3 + 2]);
            }
            // The touched vertices are deferred to the next sweep (their incident lists are stale mid-sweep).
            markDirty(dirty, p); markDirty(dirty, q); markDirty(dirty, r);
            if (d >= 0) markDirty(dirty, d);
            markDirty(dirty, m);
            return true;
        }

        // Orients the three vertices at `at` in `outTris` to match the parent face's winding (refY = its
        // NormalY; 0 leaves the order alone).
        private static void OrientTri(List<int> outTris, int at, float[] v, double refY)
        {
            int x = outTris[at], y = outTris[at + 1], z = outTris[at + 2];
            double ny = NormalY(v, x, y, z);
            if ((refY > 0 && ny < 0) || (refY < 0 && ny > 0))
            {
                outTris[at + 1] = z; outTris[at + 2] = y;
            }
        }

        private static void markDirty(bool[] dirty, int v)
        {
            if (v >= 0 && v < dirty.Length) dirty[v] = true;
        }

        // New vertices (created by a split) are past the end of the per-sweep dirty array; anything past it
        // is treated as clean, which is safe because a split's own faces always contain marked vertices.
        private static bool IsDirty(bool[] dirty, int v)
        {
            return v >= 0 && v < dirty.Length && dirty[v];
        }

        // Slope of a face relative to the up (Y) axis, in degrees.
        private static double FaceSlopeDeg(float[] v, int a, int b, int c)
        {
            double[] n = Normal(v, a, b, c);
            double len = Mat(n);
            if (len < 1e-12) return 0.0;
            double ny = Math.Abs(n[1]) / len;
            if (ny > 1.0) ny = 1.0;
            return Math.Acos(ny) * 180.0 / Math.PI;
        }

        // 2D point-in-polygon (ray crossing); the polygon is closed (i -> i+1, last -> first).
        private static bool InPoly2D(double[] px, double[] py, int n, double x, double y)
        {
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if ((py[i] > y) == (py[j] > y)) continue;
                double t = (y - py[i]) / (py[j] - py[i]);
                if (x < px[i] + t * (px[j] - px[i])) inside = !inside;
            }
            return inside;
        }

        // Sum of the fan's triangle areas (vert joined to each consecutive ring pair).
        private static double FanArea(float[] v, List<int> ring, int vert, bool boundary)
        {
            double area = 0.0;
            int n = ring.Count;
            int last = boundary ? n - 1 : n;
            for (int i = 0; i < last; i++) area += Area(v, vert, ring[i], ring[(i + 1) % n]);
            return area;
        }

        // Height of the replacement surface at the (x,z) of a point, interpolated over the new triangles, or
        // NaN when that spot is no longer covered (an outline chord).
        private static double SurfaceAlt(float[] v, List<int> newTris, double x, double z)
        {
            for (int i = 0; i + 2 < newTris.Count; i += 3)
            {
                int a = newTris[i], b = newTris[i + 1], c = newTris[i + 2];
                double x0 = v[a * 3], z0 = v[a * 3 + 2];
                double x1 = v[b * 3], z1 = v[b * 3 + 2];
                double x2 = v[c * 3], z2 = v[c * 3 + 2];
                double d = (z1 - z2) * (x0 - x2) + (x2 - x1) * (z0 - z2);
                if (Math.Abs(d) < 1e-12) continue;
                double l0 = ((z1 - z2) * (x - x2) + (x2 - x1) * (z - z2)) / d;
                double l1 = ((z2 - z0) * (x - x2) + (x0 - x2) * (z - z2)) / d;
                double l2 = 1.0 - l0 - l1;
                if (l0 < -1e-9 || l1 < -1e-9 || l2 < -1e-9) continue;
                return l0 * v[a * 3 + 1] + l1 * v[b * 3 + 1] + l2 * v[c * 3 + 1];
            }
            return double.NaN;
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

        // ---- 5. QUAD MERGE (final stage) ----------------------------------------------------------
        // The last quality lever the user asked for: "can any of the remaining needles be combined
        // with a nearby triangle to make a quad". This stage does NOT change geometry and does NOT
        // change the triangle list - it only records triangle pairs for the writer to emit as a single
        // 4-vertex face. The NMG8 format stores a per-face vertex count followed by that many vertex
        // indices and that many edge indices, plus an is_concave field, so an n-gon is a first-class
        // face (that is how the engine's own navmeshes are stored). "Merging" is therefore exactly
        // "delete the shared interior edge from the emitted perimeters": same vertices, same surface,
        // same coverage, zero geometric risk.
        //
        // Deterministic sweep: thin faces in ascending index order; each takes the neighbour across a
        // shared edge that yields the best quad (maximise the quad's worst interior angle, tie-break
        // smallest perimeter) subject to: (a) neither face already merged; (b) four distinct vertices;
        // (c) the quad walk is simple with the surface winding; (d) the two faces fold by <= 25 deg;
        // (e) geometry unchanged (above); (f) the writer derives the honest is_concave value from the
        // same walk. Face indices are stable here (no compaction after FlipEdges), so QuadPairs indexes
        // the final mesh directly.
        private static void MergeQuads(List<float> verts, List<int> tris, Stats st)
        {
            int nf = tris.Count / 3;
            if (nf < 2) return;
            var v = verts.ToArray();
            var merged = new bool[nf];
            // Exact edge -> faces index (the same leanness as the refine pass): the mesh is manifold
            // (maxEdgeMult <= 2), so one long per edge holds both face indices (-1 for a boundary edge).
            var edgeIndex = BuildEdgeFaceIndex(tris);
            var pairs = new List<int>();

            for (int f0 = 0; f0 < nf; f0++)
            {
                if (merged[f0]) continue;
                int a0 = tris[f0 * 3], b0 = tris[f0 * 3 + 1], c0 = tris[f0 * 3 + 2];
                if (a0 < 0) continue;
                if (!IsThinForQuad(v, a0, b0, c0)) continue;

                int bestF1 = -1;
                double bestWorst = 0.0, bestPerim = 0.0;
                for (int ei = 0; ei < 3; ei++)
                {
                    // The edge directed along f0's winding, so the quad walk below keeps f0's winding.
                    int u = ei == 0 ? a0 : (ei == 1 ? b0 : c0);
                    int w = ei == 0 ? b0 : (ei == 1 ? c0 : a0);
                    long packed;
                    if (!edgeIndex.TryGetValue(EdgeKey(u, w), out packed)) continue;
                    int lo = (int)(packed & 0xffffffffL), hi = (int)(packed >> 32);
                    int f1 = lo == f0 ? hi : (hi == f0 ? lo : -1);
                    if (f1 < 0 || f1 >= nf || merged[f1]) continue;
                    int a1 = tris[f1 * 3], b1 = tris[f1 * 3 + 1], c1 = tris[f1 * 3 + 2];
                    if (a1 < 0) continue;
                    int x = ThirdVertex(a0, b0, c0, u, w);          // f0's apex
                    int y = ThirdVertex(a1, b1, c1, u, w);          // neighbour's apex
                    if (x < 0 || y < 0 || x == y) continue;
                    if (x == u || x == w || y == u || y == w) continue;   // (b) four distinct vertices
                    double fold = NormalAngleDeg(v, a0, b0, c0, a1, b1, c1);
                    if (fold > QuadMergeFoldMaxDegrees) continue;   // (d) step rims / stacked levels out
                    double worst, perim;
                    if (!QuadWorstAndPerimeter(v, u, w, x, y, out worst, out perim)) continue; // (c)
                    bool better = bestF1 < 0
                        || worst > bestWorst + 1e-9
                        || (Math.Abs(worst - bestWorst) <= 1e-9 && perim < bestPerim - 1e-9);
                    if (better) { bestWorst = worst; bestPerim = perim; bestF1 = f1; }
                }

                if (bestF1 >= 0)
                {
                    merged[f0] = true; merged[bestF1] = true;
                    pairs.Add(f0); pairs.Add(bestF1);
                }
            }

            st.Quads = pairs.Count / 2;
            st.QuadPairs = pairs;
        }

        // A face is a quad-merge candidate while still thin by either predicate.
        private static bool IsThinForQuad(float[] v, int a, int b, int c)
        {
            if (MinAngle(v, a, b, c) < QuadMergeThinMinAngleDegrees) return true;
            double area = Area(v, a, b, c);
            if (area < 1e-12) return false;
            double longest = Math.Max(Len(v, a, b), Math.Max(Len(v, b, c), Len(v, c, a)));
            return longest * longest / (2.0 * area) > QuadMergeThinAspect;
        }

        // The one of (a,b,c) that is neither u nor w (0/1 for a malformed face).
        private static int ThirdVertex(int a, int b, int c, int u, int w)
        {
            if (a != u && a != w) return a;
            if (b != u && b != w) return b;
            if (c != u && c != w) return c;
            return -1;
        }

        // Builds the quad's boundary walk for the two triangles sharing (u,w), where (u,w) is directed
        // along f0's winding and x/y are the two apexes. The walk q->x->u->y (q=w, p=u) reproduces f0
        // and the neighbour triangle with their original winding. Returns the quad's worst interior
        // angle (degrees) and perimeter (m); false when the projection is degenerate or the walk is
        // self-intersecting (a bowtie - the two faces are inconsistently wound or overlap), i.e.
        // constraint (c).
        private static bool QuadWorstAndPerimeter(float[] v, int u, int w, int x, int y,
            out double worst, out double perim)
        {
            worst = 0.0; perim = 0.0;
            // walk = w -> x -> u -> y
            int[] walk = { w, x, u, y };

            // Newell normal of the quad (stable on a near-degenerate sliver).
            double nx = 0, ny = 0, nz = 0;
            for (int i = 0; i < 4; i++)
            {
                int a = walk[i], b = walk[(i + 1) % 4];
                double ax = v[a * 3], ay = v[a * 3 + 1], az = v[a * 3 + 2];
                double bx = v[b * 3], by = v[b * 3 + 1], bz = v[b * 3 + 2];
                nx += (ay - by) * (az + bz);
                ny += (az - bz) * (ax + bx);
                nz += (ax - bx) * (ay + by);
            }
            double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (nl < 1e-12) return false;
            nx /= nl; ny /= nl; nz /= nl;

            // Orthonormal basis spanning the quad plane; project the four corners.
            double ux, uy, uz;
            if (Math.Abs(nx) <= Math.Abs(ny) && Math.Abs(nx) <= Math.Abs(nz)) { ux = 0; uy = -nz; uz = ny; }
            else if (Math.Abs(ny) <= Math.Abs(nz)) { ux = -nz; uy = 0; uz = nx; }
            else { ux = -ny; uy = nx; uz = 0; }
            double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
            if (ul < 1e-12) return false;
            ux /= ul; uy /= ul; uz /= ul;
            double wx = ny * uz - nz * uy, wy = nz * ux - nx * uz, wz = nx * uy - ny * ux;

            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < 4; i++) { cx += v[walk[i] * 3]; cy += v[walk[i] * 3 + 1]; cz += v[walk[i] * 3 + 2]; }
            cx /= 4; cy /= 4; cz /= 4;
            var px = new double[4];
            var py = new double[4];
            for (int i = 0; i < 4; i++)
            {
                double dx = v[walk[i] * 3] - cx, dy = v[walk[i] * 3 + 1] - cy, dz = v[walk[i] * 3 + 2] - cz;
                px[i] = dx * ux + dy * uy + dz * uz;
                py[i] = dx * wx + dy * wy + dz * wz;
            }
            double area2 = 0;
            for (int i = 0; i < 4; i++) { int j = (i + 1) % 4; area2 += px[i] * py[j] - px[j] * py[i]; }
            if (Math.Abs(area2) < 1e-12) return false;

            worst = 180.0;
            int opposite = 0;   // vertices whose turn sign is opposite the polygon's: reflex / bowtie
            for (int i = 0; i < 4; i++)
            {
                int pa = (i + 3) % 4, pb = i, pc = (i + 1) % 4;
                double cr = (px[pb] - px[pa]) * (py[pc] - py[pa]) - (py[pb] - py[pa]) * (px[pc] - px[pa]);
                bool reflex = cr * area2 < -1e-12;
                if (reflex) opposite++;
                // Interior angle at this corner, [0,360): reflex corners see the far side.
                double e1x = px[pa] - px[pb], e1y = py[pa] - py[pb];
                double e2x = px[pc] - px[pb], e2y = py[pc] - py[pb];
                double l1 = Math.Sqrt(e1x * e1x + e1y * e1y), l2 = Math.Sqrt(e2x * e2x + e2y * e2y);
                if (l1 < 1e-12 || l2 < 1e-12) return false;
                double cosA = (e1x * e2x + e1y * e2y) / (l1 * l2);
                if (cosA > 1) cosA = 1; if (cosA < -1) cosA = -1;
                double ang = Math.Acos(cosA) * 180.0 / Math.PI;
                if (reflex) ang = 360.0 - ang;
                if (ang < worst) worst = ang;
            }
            // A simple quad has at most one reflex corner; two or more is a self-intersecting bowtie.
            if (opposite >= 2) return false;

            for (int i = 0; i < 4; i++)
            {
                int a = walk[i], b = walk[(i + 1) % 4];
                perim += Len(v, a, b);
            }
            return true;
        }
    }
}
