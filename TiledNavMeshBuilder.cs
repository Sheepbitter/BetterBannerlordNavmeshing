using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using DotRecast.Core;
using DotRecast.Core.Numerics;
using DotRecast.Recast;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Tiled voxel core (navmesh v2, Phase 2) -- terrain only
    // ==================================================================
    // Pure data-in / data-out: it never touches TaleWorlds types, so it is
    // fully testable in a standalone net472 harness (see docs/plan-navmesh-v2.md
    // section 2 and docs/dotrecast-evaluation.md).
    //
    // Pipeline per tile (DotRecast Recast stages, terrain-only input):
    //   rasterize triangles -> 3 filters -> compact heightfield -> erode ->
    //   monotone regions -> contours -> convex polys -> fan to triangles.
    // Tiles are built independently (padded AABB + border cells) and merged in
    // sorted tile order, so the result is deterministic under Parallel.For.
    //
    // Coordinate convention: the input triangle soup and all world coordinates
    // here are in Recast's Y-up space (x, height, z). The v1 mesher and the
    // NMG8 prefab use Z-up (x, y, height); call ToEngineSpace(...) once before
    // serializing with Nmg8Writer.
    public static class TiledNavMeshBuilder
    {
        // Harness-only hook: when true the raw RcPolyMeshDetail of the first detail tile is kept in
        // DebugDetailMesh so a test can inspect the per-submesh index space. Never set in production.
        public static bool CaptureDetailDebug;
        public static RcPolyMeshDetail DebugDetailMesh;

        // Harness-only A/B switch for the steep-face repair's connectivity guard. Never set in production;
        // true (the default) restores only the minimum set of steep faces needed to keep the island count,
        // false reproduces the reverted V3.47 behaviour (unconditional drop) so the two can be measured
        // against each other on a real scene.
        public static bool SteepFaceConnectivityGuard = true;

        // Harness-only: log one explain line per gate/door plate candidate (why it was blended or why it
        // was rejected). Never set in production; off means the run log only carries the summary counters.
        public static bool GatePlateTrace;

        public sealed class TiledNavMeshSettings
        {
            // Tile world size (m). Comes from NavMeshCoarsestSpacing (16 m).
            public float TileWorldSizeMeters = 16f;

            // Voxel cell size (m). New NavMeshVoxelCellSize setting (default 0.5 m).
            // Regular voxel stage; tile span is rounded to a whole number of cells.
            public float VoxelCellSizeMeters = 0.5f;

            // Fixed cell height (m). Constant, not exposed.
            public float CellHeightMeters = 0.2f;

            // Walkable slope limit (deg) -> RcConfig.WalkableSlopeAngle.
            public float MaxSlopeDegrees = 65f;

            // NavMeshMaxErrorMeters (0.25 m). Used two ways:
            //  - contour simplification: MaxSimplificationError = MaxErrorMeters / cs
            //    (voxels; the plan's ~1.3 default is a derived-voxel heuristic and
            //    is NOT used here -- the metre budget is honoured instead);
            //  - detail mesh max error (metres) when UseDetailMesh is on.
            public float MaxErrorMeters = 0.25f;

            // Agent radius. Erosion = ceil(radius/cs) cells per side, and the tile border is
            // 3 + ceil(radius/cs). Default 0.25 m: at the default cs 0.5 this is still erosion 1
            // (identical to 0.35), but at the recommended cs 0.25 it stays erosion 1 instead of 2,
            // which is what lets 1.0-1.2 m doorways/walkways/stairs survive.
            public float AgentRadiusMeters = 0.25f;
            public float AgentHeightMeters = 1.8f;
            public float AgentMaxClimbMeters = 0.4f;

            // Recast's ledge filter (FilterLedgeSpans). It removes spans that sit within walkableClimb
            // of a drop, which is what keeps the mesh off cliff edges -- but it also eats the border
            // cells of narrow walkways/bridges that have drops on both sides. Toggle off to keep those
            // (at the cost of mesh reaching closer to real ledges). Default on = Recast behaviour.
            public bool UseLedgeFilter = true;

            // Gap fill (cells, before erosion). 0 = off. Fills a column with no walkable span when it
            // is flanked by walkable spans on opposite sides along one axis (N/S, E/W or the two
            // diagonal axes) with an empty run of at most this many cells and a floor difference
            // within walkableClimb. Fixes abutting-prefab / paved-terrace seams whose hairline
            // collision gap erosion would otherwise widen into a break. 2 by default.
            public int GapFillCells = 2;

            // Stitch (per tile, after the tile mesh is built). 0 = off. Reconnects two mesh
            // components of the same tile whose nearest boundary edges are within this many cells
            // (default 4 = 1.0 m at cs 0.25) and whose floors differ by at most walkableClimb, by
            // adding a small quad that reuses existing boundary vertices. Refused when solid geometry
            // sits inside the gap envelope (a wall / closed door / low ceiling).
            public int StitchMaxGapCells = 4;

            // Prune (after the global merge + non-manifold repair). Removes small isolated islands
            // (sealed-void lids and stray fragments) so they cannot sit where no agent can reach.
            public bool PruneIsolatedIslands = true;
            public int PruneMaxIslandFaces = 400;

            // Final-mesh sliver cleanup (after merge + stitch + prune, before the invariant gate).
            // Removes needle/thin vertices and retriangulates their fans (decimation), then flips to
            // polish. Conservative tolerances (0.05 m), island count and manifoldness preserved.
            public bool CleanupSlivers = true;
            public int CleanupMaxMs = 6000;

            // Final quad merge (part of the cleanup, after the flip polish). Thin faces are combined
            // with a near-coplanar neighbour into one emitted 4-vertex face (the writer writes the
            // engine's n-gon face record with an honest is_concave). Geometry and the triangle list are
            // unchanged - only the shared interior edge disappears - so this is purely a face-count /
            // routing-cost reduction. Default ON; switch OFF instantly if the importer dislikes n-gons.
            public bool QuadMerge = true;

            // Uniform lift applied to emitted vertices only (m).
            public float HeightOffsetMeters = 0.5f;

            // STEEP-FACE REPAIR (slope margin above MaxSlopeDegrees). Slope alone is not the discriminator
            // (see RepairSteepFaces); this is the margin and SteepFaceMinHeightMeters supplies the height
            // test.   < 0 = use SteepFaceMarginDegreesDefault.   10 = default (60 deg at the shipped 50).
            //   20+ = conservative: only the unmistakable walls (>= 70 deg) go.   100+ = disabled.
            public float SteepFaceMarginDegrees = SteepFaceMarginDegreesDefault;

            // GATE/DOOR PLATE BLEND (default on). A prop that carries a rigid, dead-flat floor slab
            // (a castle gate's floor colliders are the reported case) projects into the voxel grid as a
            // perfectly horizontal plate that ignores the ground it is dropped onto. On sloping terrain one
            // end of the plate then ends up more than an agent climb above its neighbour and the plate
            // stops stitching. When this is on, such a plate is RE-GENERATED: the neighbouring built mesh
            // around the plate's own footprint is least-squares fitted, the tilt is clamped to
            // MaxSlopeDegrees (and to the plate's own rise budget), and the plate is re-emitted on that
            // plane, so both ends meet the surrounding navmesh inside the climb. See BlendGatePlates.
            public bool GatePlateBlend = true;

            // Contour edge length cap (voxels).
            public int MaxEdgeLenVoxels = 12;

            // RecastDemo region parameters (cell counts); the voxel areas passed
            // to Recast are RegionMinSize^2 / RegionMergeSize^2.
            public int RegionMinSize = 8;
            public int RegionMergeSize = 20;

            public int VertsPerPoly = 6;

            // Optional detail mesh. Adds interior vertices so faces follow the surface instead of
            // chording under knolls; bounded by MaxErrorMeters. Default off here (the exporter
            // enables it); can be disabled for a leaner bin.
            public bool UseDetailMesh = false;

            // Cluster radius (m) for the detail-path seam weld. Cross-tile/cross-poly coincident
            // detail samples differ only by float lerp error (~1e-4 m), so a small radius welds
            // them; it must stay well below the smallest real stacked-surface separation (6.7 cm in
            // probe data) so distinct levels are never merged.
            public float DetailWeldToleranceMeters = 0.01f;

            // Detail-mesh sampling distance (m). >0 is used directly; 0 falls back to max(0.9, cs).
            // Default 2.0 m: the sweep shows it collapses the tight/sliver population along
            // walkable boundaries (hill 31 -> 1 slivers) while staying inside the fidelity budget.
            // Larger values tessellate edges into fewer samples but lose interior refinement; at a
            // value >= 2*minPolyExtent Recast skips interior sampling entirely (detail -> fan).
            public float DetailSampleDistMeters = 2.0f;

            // Contour simplification error (voxels). >0 is used directly; 0 falls back to
            // MaxErrorMeters / cs (0.5 vox at the defaults). Default 1.3 vox (the plan's value):
            // a simpler walkable boundary removes the tight/sliver faces that Recast's detail edge
            // tessellation makes at every boundary vertex on boundary-rich maps, with no fidelity
            // change (contour error is separate from the surface error budget).
            public float ContourMaxErrorVoxels = 1.3f;
        }

        public sealed class VoxelTileResult
        {
            public int Index;
            public int TileX;
            public int TileY;
            public bool Failed;
            public string Error;
            public long ElapsedMs;

            // Recast-space (Y-up) world vertices and triangles for this tile.
            public float[] VertsXyz;
            public int[] Tris;

            // Global weld key per vertex: fan path = integer voxel-lattice triple
            // (gx, gy, gz); null for the detail path (which is clustered by position).
            public int[] WeldLattice;

            public int PolyCount;
            public int VertexCount;
            public int FaceCount;
            public int RasterizedTris;
            public int GapFilled;      // columns filled by the gap-fill pre-pass (before erosion)
            public int GapEmptyColumns; // own-area columns with no walkable span that the fill scanned
            public int Stitched;       // connector faces added by the per-tile stitch pass
            public int SteppedMarkedCells;    // heightfield cells covered by forced (ramp) triangles
            public int SteppedRestoredSpans;  // ramp spans the ledge filter nulled and the exemption restored
            public int SteppedDroppedClearance; // remembered spans left nulled: no headroom above them
            public int SteppedDroppedOrphan;    // remembered spans left nulled: they would be orphans
            public int SteppedDroppedPlane;     // merge-plane spans suppressed above a restored span
            public int SteppedAboveNear;        // restored spans with a walkable span within climb above
            public int SteppedAboveFar;         // restored spans with a walkable span further above
            public int ConnectorForcedInTile;   // forced triangle entries in this tile that are ALSO ledge-exempt
                                                // (i.e. offset symbolic connectors; force && exempt)
            public int ConnectorMarkedCells;    // cells the connector marker covered in this tile
            public int ConnectorClearedCeilings; // real spans the clearance exemption unlinked above a connector
            public int ConnectorErosionExempt;  // connector spans erosion nulled that the erosion exemption restored
            public List<float> SteppedRestoredCellXz;  // world XZ of cells whose span the exemption restored
            public List<float> ForcedRampCellXz;    // world XZ of cells covered by a FORCED-walkable
                                                    // triangle (a synthesized ramp or offset connector).
                                                    // Per-face provenance for the steep-face repair: those
                                                    // cells are the only ones allowed to carry a face
                                                    // steeper than the walkable cap.
        }

        public sealed class VoxelMeshResult
        {
            // Recast-space (Y-up), welded, height-offset applied.
            public float[] VertsXyz;
            public int[] Tris;
            // Quad merge: two triangle indices per emitted 4-vertex face. Null when the cleanup is off
            // or no thin face could be merged. The triangles stay in Tris; the writer folds each pair
            // into one 4-vertex face (identical geometry).
            public int[] QuadPairs;
            public int VertexCount;
            public int FaceCount;
            public int EdgeCount;
            public int DegenerateFaces;
            public int WeldMergedVertices;

            public int TileCount;
            public int BuiltTiles;
            public int FailedTiles;
            public int Workers;

            public int InputTriangles;
            public int CulledByAabb;
            public int RasterizedTriangles;
            public int SteppedMarkedCells;     // cells covered by forced (ramp) triangles, all tiles
            public int SteppedRestoredSpans;   // ramp spans restored after the ledge trim, all tiles
            public int SteppedDroppedClearance; // remembered spans not restored: no headroom
            public int SteppedDroppedOrphan;    // remembered spans not restored: would be orphans
            public int SteppedDroppedPlane;     // merge-plane spans suppressed above a restored span
            public int SteppedAboveNear;        // restored spans with a walkable span within climb above
            public int SteppedAboveFar;         // restored spans with a walkable span further above
            public int ConnectorForcedInTile;   // forced connector triangles in tiles, all tiles
            public int ConnectorMarkedCells;    // cells covered by the connector marker, all tiles
            public int ConnectorClearedCeilings; // real spans unlinked above a restored connector, all tiles
            public int ConnectorErosionExempt;  // connector spans erosion nulled that the erosion exemption restored
            // Faces re-emitted on a fitted plane by the gate/door plate blend (NavMeshVoxelGatePlateBlend).
            public int GatePlateFaces;

            public long TilesMs;
            public long MergeMs;
            public long TotalMs;
            public long TileMsMin;
            public long TileMsAvg;
            public long TileMsMax;

            public string ObjText;  // null unless requested
            public float MinZ;
            public float MaxZ;

            // Topology invariants (computed after merge). When InvariantsPassed is false the mesh
            // must NOT be written or imported.
            public bool InvariantsPassed;
            public int UniqueEdges;
            public int BoundaryEdges;
            public int MaxEdgeMultiplicity;
            public int DuplicateFaces;
            public int SelfLoops;
            public int RequiredEdges;   // (3*faces + boundary) / 2
        // V3.46 face-quality invariants: none of these may be non-zero on a shipped mesh.
        public int InvertedFaces;       // a face whose ground footprint is negative (it looks down)
        public int VerticalFaces;       // a face whose ground footprint is zero (a wall, no footprint)
        public int SelfOverlapPairs;    // two vertex-disjoint faces of one level laid across each other
        public int OverlappingFacesAny; // the same count including edge-sharing pairs (measured, not gated)
        public int BadQuadWalks;        // an emitted quad that is not a simple walk with the right winding
        // Faces steeper than MaxSlopeDegrees + SteepFaceMarginDegrees that survived the steep-face repair.
        // Reported, not blocking: the repair needs the forced-ramp provenance (empty when no stepped prop
        // synthesized anything), a synthesized ladder chain is legitimately this steep, and a
        // connectivity-restored steep face is deliberately still there.
        public int SteepFaces;
        // ... of which the ones ALSO taller than SteepFaceMinHeightMeters: the vertical-jump class. This is
        // the actionable signal - non-zero means a jump survived the repair.
        public int SteepTallFaces;
        // Wall clock of the two V3.46 face-quality scans (overlap + quad walk), so the cost of the
        // guard is visible in the run log next to the build it protects.
        public int FaceQualityMs;
        }

        // ------------------------------------------------------------------
        // Terrain grid -> triangle soup
        // ------------------------------------------------------------------
        // Same sampling semantics as the v1 NavMeshSampleStride path
        // (BuildSampleAxis: sources = k*stride with the last row/column always
        // included), so face counts are comparable at equal settings.
        //
        // Input heights are indexed [x * height + y] with world
        // (terrainX, terrainY) = (x*spacing, y*spacing) and terrain Z-up.
        // Output is Recast Y-up: (terrainX, height, terrainY).
        public static void BuildTerrainSoup(float[] stitchedHeights, int width, int height,
            float terrainVertexSpacing, int sampleStride, out float[] vertsXyz, out int[] tris)
        {
            if (stitchedHeights == null)
                throw new ArgumentNullException("stitchedHeights");
            if (width < 2 || height < 2)
                throw new ArgumentException("width and height must be at least 2.");
            if (stitchedHeights.Length != width * height)
                throw new ArgumentException("stitchedHeights length must equal width * height.");
            if (sampleStride < 1)
                throw new ArgumentException("sampleStride must be at least 1.");

            int[] sampleX = BuildSampleAxis(width, sampleStride);
            int[] sampleY = BuildSampleAxis(height, sampleStride);
            int samplesX = sampleX.Length;
            int samplesY = sampleY.Length;

            vertsXyz = new float[samplesX * samplesY * 3];
            for (int sx = 0; sx < samplesX; sx++)
            {
                int sourceX = sampleX[sx];
                for (int sy = 0; sy < samplesY; sy++)
                {
                    int sourceY = sampleY[sy];
                    int s = (sx * samplesY + sy) * 3;
                    vertsXyz[s + 0] = sourceX * terrainVertexSpacing;                 // terrainX
                    vertsXyz[s + 1] = stitchedHeights[sourceX * height + sourceY];    // height (Recast up)
                    vertsXyz[s + 2] = sourceY * terrainVertexSpacing;                 // terrainY
                }
            }

            int cellsX = samplesX - 1;
            int cellsY = samplesY - 1;
            tris = cellsX > 0 && cellsY > 0 ? new int[cellsX * cellsY * 6] : new int[0];
            int t = 0;
            for (int sx = 0; sx < cellsX; sx++)
            {
                for (int sy = 0; sy < cellsY; sy++)
                {
                    int i00 = sx * samplesY + sy;
                    int i10 = (sx + 1) * samplesY + sy;
                    int i11 = (sx + 1) * samplesY + (sy + 1);
                    int i01 = sx * samplesY + (sy + 1);
                    // Winding gives +Y normals in Recast space (walkable).
                    tris[t++] = i00; tris[t++] = i11; tris[t++] = i10;
                    tris[t++] = i00; tris[t++] = i01; tris[t++] = i11;
                }
            }
        }

        // Terrain soup with a hole mask (indexed like stitchedHeights; true = terrain hole/void).
        // Any cell whose four corners touch a hole is skipped entirely, so no walkable lid is emitted
        // over a carve -- the real floor below (an asset) is then the only surface there.
        public static void BuildTerrainSoup(float[] stitchedHeights, int width, int height,
            float terrainVertexSpacing, int sampleStride, bool[] holeMask,
            out float[] vertsXyz, out int[] tris)
        {
            if (stitchedHeights == null)
                throw new ArgumentNullException("stitchedHeights");
            if (width < 2 || height < 2)
                throw new ArgumentException("width and height must be at least 2.");
            if (stitchedHeights.Length != width * height)
                throw new ArgumentException("stitchedHeights length must equal width * height.");
            if (sampleStride < 1)
                throw new ArgumentException("sampleStride must be at least 1.");
            if (holeMask == null)
                throw new ArgumentNullException("holeMask");

            int[] sampleX = BuildSampleAxis(width, sampleStride);
            int[] sampleY = BuildSampleAxis(height, sampleStride);
            int samplesX = sampleX.Length;
            int samplesY = sampleY.Length;

            vertsXyz = new float[samplesX * samplesY * 3];
            for (int sx = 0; sx < samplesX; sx++)
            {
                int sourceX = sampleX[sx];
                for (int sy = 0; sy < samplesY; sy++)
                {
                    int sourceY = sampleY[sy];
                    int s = (sx * samplesY + sy) * 3;
                    vertsXyz[s + 0] = sourceX * terrainVertexSpacing;
                    vertsXyz[s + 1] = stitchedHeights[sourceX * height + sourceY];
                    vertsXyz[s + 2] = sourceY * terrainVertexSpacing;
                }
            }

            int cellsX = samplesX - 1;
            int cellsY = samplesY - 1;
            var tri = new List<int>(cellsX > 0 && cellsY > 0 ? cellsX * cellsY * 6 : 0);
            for (int sx = 0; sx < cellsX; sx++)
            {
                for (int sy = 0; sy < cellsY; sy++)
                {
                    if (holeMask[sampleX[sx] * height + sampleY[sy]]
                        || holeMask[sampleX[sx + 1] * height + sampleY[sy]]
                        || holeMask[sampleX[sx + 1] * height + sampleY[sy + 1]]
                        || holeMask[sampleX[sx] * height + sampleY[sy + 1]])
                        continue;

                    int i00 = sx * samplesY + sy;
                    int i10 = (sx + 1) * samplesY + sy;
                    int i11 = (sx + 1) * samplesY + (sy + 1);
                    int i01 = sx * samplesY + (sy + 1);
                    tri.Add(i00); tri.Add(i11); tri.Add(i10);
                    tri.Add(i00); tri.Add(i01); tri.Add(i11);
                }
            }
            tris = tri.ToArray();
        }

        // Appends engine-space (Z-up) asset triangles to a Recast Y-up terrain soup. Asset
        // vertices are mapped engine (x,y,z) -> Recast (x, z, y) like BuildTerrainSoup, and each
        // asset triangle's winding is reversed because that axis swap is orientation-reversing
        // (without the reversal every engine-up floor would get a Recast-down normal and be
        // rejected by the slope filter). No height compensation is applied here: the merged soup is
        // lifted uniformly later by HeightOffsetMeters, so terrain AND structures share the same
        // anti-intersection offset (a navmesh that intersected the terrain/assets would make units
        // lose the mesh).
        //
        // Per-triangle winding normalisation: the blanket reversal above only fixes the axis swap.
        // Real collision data is frequently wound inconsistently per triangle, and a DOWNWARD-wound
        // floor (Recast normal.Y < 0) is rejected by MarkWalkableTriangles -> NULL area -> no navmesh
        // face at any cell size, with or without pruning (measured on Birtu: the corridor floors at
        // z 20.0-20.4 produced no navmesh at all). So each asset triangle whose Recast normal.Y < 0
        // has two indices swapped, i.e. such collision geometry is treated as DOUBLE-SIDED for
        // walkability. This changes only the input soup: the rasterizer merges the two orientations
        // into the same solid span, so the output mesh is unchanged apart from surfaces that were
        // previously dropped entirely.
        public static void BuildCombinedSoup(float[] terrainVertsXyz, int[] terrainTris,
            float[] assetVertsEngineXyz, int[] assetTris,
            out float[] vertsXyz, out int[] tris, out int flippedTriangles)
        {
            int tv = terrainVertsXyz != null ? terrainVertsXyz.Length / 3 : 0;
            int tt = terrainTris != null ? terrainTris.Length / 3 : 0;
            int av = assetVertsEngineXyz != null ? assetVertsEngineXyz.Length / 3 : 0;
            int at = assetTris != null ? assetTris.Length / 3 : 0;
            flippedTriangles = 0;

            vertsXyz = new float[(tv + av) * 3];
            if (tv > 0)
                Array.Copy(terrainVertsXyz, 0, vertsXyz, 0, tv * 3);
            for (int i = 0; i < av; i++)
            {
                int s = i * 3;
                int d = (tv + i) * 3;
                vertsXyz[d + 0] = assetVertsEngineXyz[s + 0];                 // engine X -> Recast X
                vertsXyz[d + 1] = assetVertsEngineXyz[s + 2];                 // engine Z -> Recast Y (height)
                vertsXyz[d + 2] = assetVertsEngineXyz[s + 1];                 // engine Y -> Recast Z
            }

            tris = new int[(tt + at) * 3];
            if (tt > 0)
                Array.Copy(terrainTris, 0, tris, 0, tt * 3);
            for (int i = 0; i < at; i++)
            {
                int s = i * 3;
                int d = (tt + i) * 3;
                // Reversed winding (see comment above): (b, b+2, b+1).
                int r0 = assetTris[s + 0] + tv;
                int r1 = assetTris[s + 2] + tv;
                int r2 = assetTris[s + 1] + tv;
                if (TriNormalY(vertsXyz, r0, r1, r2) < 0f)
                {
                    int tmp = r1;
                    r1 = r2;
                    r2 = tmp;
                    flippedTriangles++;
                }
                tris[d + 0] = r0;
                tris[d + 1] = r1;
                tris[d + 2] = r2;
            }
        }

        private static int[] BuildSampleAxis(int count, int stride)
        {
            int samples = (count - 1) / stride + 1;
            int[] sources = new int[samples];
            for (int k = 0; k < samples; k++)
                sources[k] = k * stride;
            if (samples > 1)
                sources[samples - 1] = count - 1;
            return sources;
        }

        // Converts Recast Y-up (x, height, z) to the engine/v1 Z-up convention
        // (x, y, height) and reverses triangle winding, so the emitted faces are
        // CCW seen from +Z -- the same convention the v1 mesher emits.
        public static void ToEngineSpace(float[] vertsXyz, int[] tris)
        {
            if (vertsXyz != null)
            {
                for (int i = 0; i + 2 < vertsXyz.Length; i += 3)
                {
                    float height = vertsXyz[i + 1];
                    vertsXyz[i + 1] = vertsXyz[i + 2];
                    vertsXyz[i + 2] = height;
                }
            }

            if (tris != null)
            {
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    int b = tris[i + 1];
                    tris[i + 1] = tris[i + 2];
                    tris[i + 2] = b;
                }
            }
        }

        // ------------------------------------------------------------------
        // Tiled build
        // ------------------------------------------------------------------
        // forceWalkableTriangles (optional, same length as the input triangle count): triangles that
        // must be marked walkable even when their slope exceeds MaxSlopeDegrees. SteppedPropRamps uses
        // it for synthesized stair/ladder ramps, which are meant to be traversed. Null (the default)
        // keeps the input byte-for-byte identical to the pre-ramp behaviour.
        public static VoxelMeshResult Build(float[] vertsXyz, int[] tris, TiledNavMeshSettings s,
            int maxWorkers, Action<string> log, bool[] forceWalkableTriangles = null,
            bool[] ledgeExemptTriangles = null)
        {
            if (vertsXyz == null)
                throw new ArgumentNullException("vertsXyz");
            if (tris == null)
                throw new ArgumentNullException("tris");
            if (s == null)
                throw new ArgumentNullException("s");
            if (vertsXyz.Length == 0 || vertsXyz.Length % 3 != 0)
                throw new ArgumentException("vertsXyz length must be a positive multiple of 3.");
            if (tris.Length % 3 != 0)
                throw new ArgumentException("tris length must be a multiple of 3.");

            var total = Stopwatch.StartNew();
            int inputTris = tris.Length / 3;
            if (forceWalkableTriangles != null && forceWalkableTriangles.Length != inputTris)
                throw new ArgumentException("forceWalkableTriangles length must match the triangle count.");
            if (ledgeExemptTriangles != null && ledgeExemptTriangles.Length != inputTris)
                throw new ArgumentException("ledgeExemptTriangles length must match the triangle count.");

            float cs = s.VoxelCellSizeMeters;
            float ch = s.CellHeightMeters;
            if (cs <= 0f) throw new ArgumentException("VoxelCellSizeMeters must be > 0.");
            if (ch <= 0f) throw new ArgumentException("CellHeightMeters must be > 0.");
            if (s.TileWorldSizeMeters <= 0f) throw new ArgumentException("TileWorldSizeMeters must be > 0.");
            if (s.VertsPerPoly < 3 || s.VertsPerPoly > 6) throw new ArgumentException("VertsPerPoly must be in [3, 6].");

            int cellsPerTile = (int)Math.Round(s.TileWorldSizeMeters / cs);
            if (cellsPerTile < 1) cellsPerTile = 1;
            float tileSpan = cellsPerTile * cs;

            // Global bounds of the input.
            float minX = vertsXyz[0], minY = vertsXyz[1], minZ = vertsXyz[2];
            float maxX = minX, maxY = minY, maxZ = minZ;
            for (int i = 0; i < vertsXyz.Length; i += 3)
            {
                float x = vertsXyz[i], y = vertsXyz[i + 1], z = vertsXyz[i + 2];
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (z < minZ) minZ = z;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
                if (z > maxZ) maxZ = z;
            }

            // Fixed snapped world origin on the tile lattice. Every tile boundary
            // and every emitted vertex then lies on the global lattice
            // origin + integer*cs (X/Z) / +integer*ch (Y), which is what makes
            // cross-tile welding exact.
            float originX = (float)(Math.Floor((double)minX / tileSpan) * tileSpan);
            float originZ = (float)(Math.Floor((double)minZ / tileSpan) * tileSpan);
            float margin = Math.Max(2f, s.AgentHeightMeters + 0.5f);
            float bminY = (float)(Math.Floor((double)(minY - margin) / ch) * ch);
            float bmaxY = (float)(Math.Ceiling((double)(maxY + margin) / ch) * ch);

            int tilesX = Math.Max(1, (int)Math.Ceiling((maxX - originX) / tileSpan));
            int tilesZ = Math.Max(1, (int)Math.Ceiling((maxZ - originZ) / tileSpan));
            int tileCount = tilesX * tilesZ;

            int border = RcConfig.CalcBorder(s.AgentRadiusMeters, cs);
            int width = cellsPerTile + 2 * border;
            int height = cellsPerTile + 2 * border;
            float pad = border * cs;

            int walkableHeight = (int)Math.Ceiling(s.AgentHeightMeters / ch);
            int walkableClimb = (int)Math.Floor(s.AgentMaxClimbMeters / ch + 1e-4f);
            int walkableRadius = (int)Math.Ceiling(s.AgentRadiusMeters / cs);
            float maxSimplificationError = EffectiveContourMaxError(s);
            int minRegionArea = s.RegionMinSize * s.RegionMinSize;
            int mergeRegionArea = s.RegionMergeSize * s.RegionMergeSize;
            var areaMod = new RcAreaModification(RcRecast.RC_WALKABLE_AREA);

            int requestedWorkers = maxWorkers;
            int workers = maxWorkers < 1 ? Environment.ProcessorCount : maxWorkers;
            if (workers > tileCount) workers = tileCount;
            if (workers < 1) workers = 1;

            Log(log, "v2: settings cs=" + F(cs) + "m ch=" + F(ch) + "m tile=" + F(tileSpan)
                + "m cellsPerTile=" + cellsPerTile + " tiles=" + tilesX + "x" + tilesZ
                + " border=" + border + " hf=" + width + "x" + height
                + " origin=(" + F(originX) + "," + F(originZ) + ")"
                + " bminY=" + F(bminY) + " bmaxY=" + F(bmaxY));
            Log(log, "v2: params slope=" + F(s.MaxSlopeDegrees) + "deg radius=" + F(s.AgentRadiusMeters)
                + "m height=" + F(s.AgentHeightMeters) + "m climb=" + F(s.AgentMaxClimbMeters)
                + "m walkable(r,h,c)=" + walkableRadius + "/" + walkableHeight + "/" + walkableClimb
                + " edgeLen=" + s.MaxEdgeLenVoxels + "vox vpp=" + s.VertsPerPoly
                + " regionMin=" + s.RegionMinSize + " regionMerge=" + s.RegionMergeSize
                + " gapFill=" + s.GapFillCells
                + " stitch=" + s.StitchMaxGapCells
                + " prune=" + (s.PruneIsolatedIslands ? s.PruneMaxIslandFaces.ToString(CultureInfo.InvariantCulture) : "off")
                + " ledgeFilter=" + s.UseLedgeFilter
                + " detail=" + s.UseDetailMesh
                + " detSampleDist=" + F(EffectiveDetailSampleDist(s)) + "m"
                + " contourSimpErr=" + F(EffectiveContourMaxError(s)) + "vox");
            Log(log, "v2: contour maxError=" + F(maxSimplificationError) + "vox (= " + F(maxSimplificationError * cs)
                + "m at cs=" + F(cs) + "m; NavMeshVoxelContourMaxError, 0=auto=MaxErrorMeters/cs)");
            Log(log, "v2: workers=" + workers + " (requested " + requestedWorkers + ", tileCount=" + tileCount + ")");

            // --- bin input triangles into tiles (padded AABB overlap) ---
            var tileTris = new List<int>[tileCount];
            int culledByAabb = 0;
            int rasterizedTris = 0;
            for (int t = 0; t < inputTris; t++)
            {
                int i0 = tris[t * 3 + 0], i1 = tris[t * 3 + 1], i2 = tris[t * 3 + 2];
                float tx0 = vertsXyz[i0 * 3], tx1 = vertsXyz[i1 * 3], tx2 = vertsXyz[i2 * 3];
                float tz0 = vertsXyz[i0 * 3 + 2], tz1 = vertsXyz[i1 * 3 + 2], tz2 = vertsXyz[i2 * 3 + 2];
                float triMinX = Math.Min(tx0, Math.Min(tx1, tx2));
                float triMaxX = Math.Max(tx0, Math.Max(tx1, tx2));
                float triMinZ = Math.Min(tz0, Math.Min(tz1, tz2));
                float triMaxZ = Math.Max(tz0, Math.Max(tz1, tz2));

                int xMin = (int)Math.Floor((triMinX - originX - pad) / tileSpan);
                int xMax = (int)Math.Floor((triMaxX - originX + pad) / tileSpan);
                int zMin = (int)Math.Floor((triMinZ - originZ - pad) / tileSpan);
                int zMax = (int)Math.Floor((triMaxZ - originZ + pad) / tileSpan);
                if (xMin < 0) xMin = 0;
                if (zMin < 0) zMin = 0;
                if (xMax > tilesX - 1) xMax = tilesX - 1;
                if (zMax > tilesZ - 1) zMax = tilesZ - 1;

                if (xMin > xMax || zMin > zMax)
                {
                    culledByAabb++;
                    continue;
                }

                for (int tz = zMin; tz <= zMax; tz++)
                {
                    for (int tx = xMin; tx <= xMax; tx++)
                    {
                        int idx = tz * tilesX + tx;
                        List<int> list = tileTris[idx];
                        if (list == null)
                        {
                            list = new List<int>();
                            tileTris[idx] = list;
                        }
                        list.Add(t);
                        rasterizedTris++;
                    }
                }
            }

            Log(log, "v2: tris input=" + inputTris + " culledByAabb=" + culledByAabb
                + " rasterized=" + rasterizedTris + " (rasterized counts per-tile border duplicates)");
            if (forceWalkableTriangles != null || ledgeExemptTriangles != null)
            {
                int forced = 0;
                if (forceWalkableTriangles != null)
                    for (int i = 0; i < forceWalkableTriangles.Length; i++)
                        if (forceWalkableTriangles[i]) forced++;
                int exempt = 0;
                if (ledgeExemptTriangles != null)
                    for (int i = 0; i < ledgeExemptTriangles.Length; i++)
                        if (ledgeExemptTriangles[i]) exempt++;
                Log(log, "v2: stepped tris: forced-walkable=" + forced + " ledge-exempt=" + exempt + "/"
                    + inputTris + " (ramps walkable regardless of slope; stepped-prop treads kept off the"
                    + " ledge trim)");
            }

            // --- parallel per-tile build ---
            var results = new VoxelTileResult[tileCount];
            var tilesWatch = Stopwatch.StartNew();
            var parallelOptions = new ParallelOptions();
            parallelOptions.MaxDegreeOfParallelism = workers;

            // Coarse progress for long builds: log every ~5% of tiles (and at the end),
            // throttled to at most one line per ~2 s. Purely observational -- the counter
            // and logs never influence the built output (determinism is unaffected).
            int tilesCompleted = 0;
            int progressStep = Math.Max(1, tileCount / 20);
            long lastProgressMs = 0;
            var progressLock = new object();
            var progressWatch = Stopwatch.StartNew();

            Parallel.For(0, tileCount, parallelOptions, delegate (int index)
            {
                int tx = index % tilesX;
                int ty = index / tilesX;
                results[index] = BuildTile(index, tx, ty, vertsXyz, tris, tileTris[index], s,
                    originX, originZ, tileSpan, cellsPerTile, bminY, bmaxY, width, height, border, pad,
                    walkableHeight, walkableClimb, walkableRadius, maxSimplificationError,
                    minRegionArea, mergeRegionArea, areaMod, forceWalkableTriangles, ledgeExemptTriangles);

                if (log != null)
                {
                    int done = Interlocked.Increment(ref tilesCompleted);
                    if (done == tileCount || done % progressStep == 0)
                    {
                        long elapsed = progressWatch.ElapsedMilliseconds;
                        bool emit;
                        lock (progressLock)
                        {
                            emit = done == tileCount || elapsed - lastProgressMs >= 2000;
                            if (emit)
                                lastProgressMs = elapsed;
                        }
                        if (emit)
                            log("v2: progress tiles=" + done + "/" + tileCount
                                + " (" + (done * 100 / tileCount) + "%) elapsedMs=" + elapsed);
                    }
                }
            });
            tilesWatch.Stop();

            // --- aggregate + log failures (never silently dropped) ---
            int builtTiles = 0;
            int failedTiles = 0;
            long tileMsSum = 0;
            long tileMsMin = long.MaxValue;
            long tileMsMax = 0;
            int polyTotal = 0;
            var failures = new List<string>();
            for (int i = 0; i < tileCount; i++)
            {
                VoxelTileResult r = results[i];
                if (r == null)
                {
                    failedTiles++;
                    failures.Add("tile#" + i + " (tx=" + (i % tilesX) + ",ty=" + (i / tilesX) + ") produced no result");
                    continue;
                }
                if (r.Failed)
                {
                    failedTiles++;
                    failures.Add("tile#" + i + " (tx=" + r.TileX + ",ty=" + r.TileY + ") failed: " + r.Error);
                    continue;
                }
                builtTiles++;
                polyTotal += r.PolyCount;
                tileMsSum += r.ElapsedMs;
                if (r.ElapsedMs < tileMsMin) tileMsMin = r.ElapsedMs;
                if (r.ElapsedMs > tileMsMax) tileMsMax = r.ElapsedMs;
            }
            for (int i = 0; i < failures.Count; i++)
                Log(log, "v2: FAILED " + failures[i]);
            if (tileMsMin == long.MaxValue) tileMsMin = 0;
            long tileMsAvg = builtTiles > 0 ? tileMsSum / builtTiles : 0;

            Log(log, "v2: tiles=" + tileCount + " built=" + builtTiles + " failed=" + failedTiles
                + " workers=" + workers + " polys=" + polyTotal);

            if (s.GapFillCells > 0)
            {
                int gapFilled = 0;
                int gapEmpty = 0;
                for (int i = 0; i < results.Length; i++)
                    if (results[i] != null && !results[i].Failed)
                    {
                        gapFilled += results[i].GapFilled;
                        gapEmpty += results[i].GapEmptyColumns;
                    }
                Log(log, "v2: gap fill: filled=" + gapFilled + " emptyColumns=" + gapEmpty
                    + " (cap=" + s.GapFillCells + " cells, before erosion)");
            }

            if (s.StitchMaxGapCells > 0)
            {
                int stitched = 0;
                for (int i = 0; i < results.Length; i++)
                    if (results[i] != null && !results[i].Failed)
                        stitched += results[i].Stitched;
                Log(log, "v2: stitch: added=" + stitched + " faces across tiles (cap=" + s.StitchMaxGapCells
                    + " cells" + (stitched > 0 ? "" : " - no near-miss cuts found") + ")");
            }

            int aggMarkedCells = 0, aggRestoredSpans = 0, aggDroppedClearance = 0, aggDroppedOrphan = 0;
            int aggDroppedPlane = 0, aggAboveNear = 0, aggAboveFar = 0;
            int aggConnectorForcedInTile = 0, aggConnectorMarkedCells = 0, aggConnectorClearedCeilings = 0;
            int aggConnectorErosionExempt = 0;
            if (forceWalkableTriangles != null || ledgeExemptTriangles != null)
            {
                for (int i = 0; i < results.Length; i++)
                    if (results[i] != null && !results[i].Failed)
                    {
                        aggMarkedCells += results[i].SteppedMarkedCells;
                        aggRestoredSpans += results[i].SteppedRestoredSpans;
                        aggDroppedClearance += results[i].SteppedDroppedClearance;
                        aggDroppedOrphan += results[i].SteppedDroppedOrphan;
                        aggDroppedPlane += results[i].SteppedDroppedPlane;
                        aggAboveNear += results[i].SteppedAboveNear;
                        aggAboveFar += results[i].SteppedAboveFar;
                        aggConnectorForcedInTile += results[i].ConnectorForcedInTile;
                        aggConnectorMarkedCells += results[i].ConnectorMarkedCells;
                        aggConnectorClearedCeilings += results[i].ConnectorClearedCeilings;
                        aggConnectorErosionExempt += results[i].ConnectorErosionExempt;
                    }
                Log(log, "v2: stepped: markedCells=" + aggMarkedCells + " restored=" + aggRestoredSpans
                    + " droppedClearance=" + aggDroppedClearance + " droppedOrphan=" + aggDroppedOrphan
                    + " droppedPlane=" + aggDroppedPlane + " aboveNear=" + aggAboveNear
                    + " aboveFar=" + aggAboveFar + " (ledge-trim exemption, gated per span)");
                // Connector diagnostics: connectorForced counts the forced triangle entries in tiles;
                // connectorMarked is how many cells the connector marker covered; clearedCeilings is how
                // many real spans the clearance exemption unlinked above a restored connector span;
                // erosionExempt is how many connector spans erosion nulled that were restored after it.
                Log(log, "v2: connector: forcedInTile=" + aggConnectorForcedInTile + " markedCells="
                    + aggConnectorMarkedCells + " clearedCeilings=" + aggConnectorClearedCeilings
                    + " erosionExempt=" + aggConnectorErosionExempt
                    + " (symbolic ladder links exempted from FilterWalkableLowHeightSpans and erosion)");
            }

            // --- merge in sorted order (deterministic) ---
            var mergeWatch = Stopwatch.StartNew();
            MergeOutput merged = s.UseDetailMesh
                ? MergeDetailTiles(results, tilesX, tilesZ, s.DetailWeldToleranceMeters)
                : MergeFanTiles(results, tilesX, tilesZ);
            mergeWatch.Stop();

            // Deterministic repair: Recast's per-tile detail tessellation can emit a few
            // non-manifold fans at tile corners (upstream; its delaunayHull even logs "Removing
            // dangling face"). Remove the surplus faces so every edge is shared by <= 2 faces.
            int nonManifoldRepaired = RepairNonManifold(merged);
            if (nonManifoldRepaired > 0)
                Log(log, "v2: repaired mesh: removed " + nonManifoldRepaired + " non-manifold faces");

            // Per-face provenance for the steep-face repair AND the gate-plate blend: the cells covered by a
            // FORCED-walkable triangle (a synthesized ramp or an offset symbolic connector). Those are the only
            // cells allowed to carry a face steeper than the walkable cap - a ladder chain legitimately runs
            // steeper than the cap and must not be cut - and they are NOT "surroundings" for the plate fit.
            // Collected BEFORE the per-tile structures are released below, and before the blend, the steep
            // repair and the prune that consume it.
            HashSet<long> forcedRampCells = null;
            {
                float fcs = s.VoxelCellSizeMeters;
                for (int i = 0; i < results.Length; i++)
                {
                    List<float> forcedXz = results[i] != null ? results[i].ForcedRampCellXz : null;
                    if (forcedXz == null) continue;
                    if (forcedRampCells == null) forcedRampCells = new HashSet<long>(LongKeyComparer.Instance);
                    for (int k = 0; k + 1 < forcedXz.Count; k += 2)
                        forcedRampCells.Add(CellKey(forcedXz[k], forcedXz[k + 1], fcs));
                }
            }

            // GATE/DOOR PLATE BLEND. Before the cross-tile stitch and the prune, on the merged mesh: a
            // prop's rigid flat floor slab is re-emitted on a plane fitted to the built mesh around it, so
            // it stops floating proud of sloping ground. See BlendGatePlates.
            int gatePlateFaces = 0;
            if (s.GatePlateBlend && merged.Tris.Count >= 3)
            {
                gatePlateFaces = BlendGatePlates(merged, s.VoxelCellSizeMeters, s.CellHeightMeters,
                    s.AgentMaxClimbMeters, s.MaxSlopeDegrees, forcedRampCells, log);
            }

            // Cross-tile stitch (before pruning): reconnect near-miss pieces that the per-tile stitch
            // could not reach (different tiles, or beyond its cap). A stitch is refused when the input
            // soup has solid geometry spanning the gap envelope (wall, closed door, low ceiling).
            if (s.StitchMaxGapCells > 0)
            {
                int msPairs, msAdded, msRefused;
                MergeStitch(merged, vertsXyz, tris, inputTris, s.VoxelCellSizeMeters, s.CellHeightMeters,
                    s.HeightOffsetMeters, bminY, walkableHeight, walkableClimb, s.StitchMaxGapCells,
                    out msPairs, out msAdded, out msRefused);
                Log(log, "v2: merge-stitch: pairs=" + msPairs + " added=" + msAdded + " refused=" + msRefused
                    + " (cap=" + s.StitchMaxGapCells + " cells)");
            }

            // STEEP-FACE REPAIR, and it must run BEFORE the prune so any fragment a component that cannot be
            // made whole leaves behind is judged by the island cap rather than shipped. Unlike the reverted
            // V3.47 the drops are minimally connectivity-restored (see RepairSteepFaces): the rule is
            // unchanged, but only the faces actually needed to hold the island count are put back - which is
            // what stops the pass from costing CC_20 two islands AND what lets it clear the staircase.
            RepairSteepFaces(merged, forcedRampCells, s.VoxelCellSizeMeters,
                s.MaxSlopeDegrees, s.SteepFaceMarginDegrees,
                SteepFaceMinHeightMeters(s.AgentMaxClimbMeters, s.CellHeightMeters), log);

            // Prune small isolated islands (sealed-void lids / stray fragments) before the gate, so an
            // unreachable fragment never reaches the engine. Never touches the big networks.
            if (s.PruneIsolatedIslands && s.PruneMaxIslandFaces > 0 && merged.Tris.Count >= 3)
            {
                // Per-face provenance for the exemption: the cells whose walkable span only exists because
                // the stepped-props exemption restored it. An island that is (almost) all such faces is a
                // merged stepped-prop leftover rather than a walkable network, and is pruned whatever its
                // size; an island that merely contains some restored faces (the castle network with its
                // flights) stays mixed provenance and is kept.
                HashSet<long> steppedCells = null;
                float qcs = s.VoxelCellSizeMeters;
                for (int i = 0; i < results.Length; i++)
                {
                    List<float> cellXz = results[i] != null ? results[i].SteppedRestoredCellXz : null;
                    if (cellXz == null) continue;
                    if (steppedCells == null) steppedCells = new HashSet<long>(LongKeyComparer.Instance);
                    for (int k = 0; k + 1 < cellXz.Count; k += 2)
                        steppedCells.Add(CellKey(cellXz[k], cellXz[k + 1], qcs));
                }
                var pst = new PruneStats();
                PruneSmallIslands(merged, s.PruneMaxIslandFaces, steppedCells, qcs, pst);
                Log(log, "v2: prune: removed " + pst.RemovedIslands + " islands, " + pst.RemovedFaces
                    + " faces (<=" + s.PruneMaxIslandFaces + " faces each); largest removed="
                    + pst.LargestRemoved + " at (" + F(pst.LargestX) + "," + F(pst.LargestY) + ","
                    + F(pst.LargestZ) + "); kept=" + pst.KeptIslands + " islands");
                if (pst.ExemptIslands > 0)
                    Log(log, "v2: prune: exempt-formed islands=" + pst.ExemptIslands + " faces="
                        + pst.ExemptFaces + " largest=" + pst.ExemptLargest + " at (" + F(pst.ExemptX)
                        + "," + F(pst.ExemptY) + "," + F(pst.ExemptZ)
                        + ") (restored stepped-prop leftovers, pruned regardless of the cap)");
                if (pst.MaxKeptFaces > 0)
                    Log(log, "v2: prune: kept islands worst exemption fraction="
                        + F((float)(pst.MaxKeptExemptFraction * 100.0)) + "% of " + pst.MaxKeptFaces
                        + " faces (mixed provenance, no kept island is exempt-formed)");
            }

            // Release the per-tile build structures before the cleanup. Keeping 15k+ tile results alive
            // (each with meshes, heightfields and index lists) turns every cleanup allocation into a gen2
            // collection over a multi-GB heap: on CC_20 the cleanup's topology maps took ~80 s each until
            // these were released. The weld totals the export reports are captured first.
            int tileVertexSum = SumTileVertices(results);
            results = null;
            tileTris = null;

            // Final-mesh sliver cleanup (thin/tiny triangles). Runs on the merged, welded, stitched,
            // pruned mesh; only retriangulates it (decimate needle/thin vertices + flip polish). The
            // island count is asserted unchanged and the outline moves by <= a small tolerance.
            int[] quadPairs = null;
            if (s.CleanupSlivers && merged.Tris.Count >= 3)
            {
                var cv = new List<float>(merged.X.Count * 3);
                for (int i = 0; i < merged.X.Count; i++)
                {
                    cv.Add(merged.X[i]); cv.Add(merged.Y[i]); cv.Add(merged.Z[i]);
                }
                var ct = new List<int>(merged.Tris);
                NavMeshCleanup.Stats cln = NavMeshCleanup.Run(cv, ct, s.CleanupMaxMs, s.QuadMerge);
                if (cln.Welded > 0 || cln.Removed > 0 || cln.Decimated > 0 || cln.Relaxed > 0
                    || cln.Refined > 0 || cln.Flipped > 0 || cln.Quads > 0)
                {
                    int nv = cv.Count / 3;
                    merged.X.Clear(); merged.Y.Clear(); merged.Z.Clear();
                    for (int i = 0; i < nv; i++)
                    {
                        merged.X.Add(cv[i * 3]); merged.Y.Add(cv[i * 3 + 1]); merged.Z.Add(cv[i * 3 + 2]);
                    }
                    merged.Tris.Clear(); merged.Tris.AddRange(ct);
                }
                if (cln.Quads > 0 && cln.QuadPairs != null)
                    quadPairs = cln.QuadPairs.ToArray();
                Log(log, "cleanup: welded=" + cln.Welded + " removed=" + cln.Removed + " decimated="
                    + cln.Decimated + " relaxed=" + cln.Relaxed + " refined=" + cln.Refined
                    + " flipped=" + cln.Flipped + " quads=" + cln.Quads
                    + " quadsXZworst=" + F((float)cln.QuadXzWorstAngle) + "deg (margin "
                    + NavMeshCleanup.QuadXzMaxInteriorAngleDegrees + "deg, nearStraight="
                    + cln.QuadXzNearStraight + ")"
                    + " needles before=" + cln.NeedlesBefore
                    + " after=" + cln.NeedlesAfter + " thin before=" + cln.ThinBefore + " after="
                    + cln.ThinAfter + " boundaryMoved=" + cln.BoundaryMovedCount + " boundaryMaxMove="
                    + F(cln.BoundaryMaxMove) + "m components " + cln.ComponentsBefore + "->"
                    + cln.ComponentsAfter + (cln.ComponentsBefore == cln.ComponentsAfter ? " (kept)"
                        : " (CHANGED!)") + " [cand=" + cln.CandVerts + " rejMove=" + cln.RejMove
                    + " rejFan=" + cln.RejFan + " rejRing=" + cln.RejRing + " rejTopo=" + cln.RejTopo + "]");
                if (cln.Diag != null)
                    for (int i = 0; i < cln.Diag.Count; i++) Log(log, "cleanup: " + cln.Diag[i]);
                if (cln.DecimDiag != null) Log(log, "cleanup: " + cln.DecimDiag);
            }

            // V3.46: the last pass that rewrites faces, so the face-quality repairs run HERE - after the
            // sliver cleanup has finished and BEFORE the result arrays are built, and the recorded quad
            // pairs are remapped through the same compaction (a pair whose face was dropped goes with
            // it, so the writer can never emit a quad over a hole).
            quadPairs = RepairFaceQuality(merged, quadPairs, log);

            int mergedVertices = merged.X.Count;
            var result = new VoxelMeshResult();
            result.VertexCount = mergedVertices;
            result.FaceCount = merged.Tris.Count / 3;
            result.DegenerateFaces = merged.Degenerate;
            result.WeldMergedVertices = tileVertexSum - mergedVertices;
            result.TileCount = tileCount;
            result.BuiltTiles = builtTiles;
            result.FailedTiles = failedTiles;
            result.Workers = workers;
            result.InputTriangles = inputTris;
            result.CulledByAabb = culledByAabb;
            result.RasterizedTriangles = rasterizedTris;
            result.SteppedMarkedCells = aggMarkedCells;
            result.SteppedRestoredSpans = aggRestoredSpans;
            result.SteppedDroppedClearance = aggDroppedClearance;
            result.SteppedDroppedOrphan = aggDroppedOrphan;
            result.SteppedDroppedPlane = aggDroppedPlane;
            result.SteppedAboveNear = aggAboveNear;
            result.SteppedAboveFar = aggAboveFar;
            result.ConnectorForcedInTile = aggConnectorForcedInTile;
            result.ConnectorMarkedCells = aggConnectorMarkedCells;
            result.ConnectorClearedCeilings = aggConnectorClearedCeilings;
            result.ConnectorErosionExempt = aggConnectorErosionExempt;
            result.GatePlateFaces = gatePlateFaces;
            result.TilesMs = tilesWatch.ElapsedMilliseconds;
            result.MergeMs = mergeWatch.ElapsedMilliseconds;
            result.TileMsMin = tileMsMin;
            result.TileMsAvg = tileMsAvg;
            result.TileMsMax = tileMsMax;
            result.MinZ = mergedVertices > 0 ? merged.MinY : 0f;
            result.MaxZ = mergedVertices > 0 ? merged.MaxY : 0f;

            result.VertsXyz = new float[mergedVertices * 3];
            for (int i = 0; i < mergedVertices; i++)
            {
                result.VertsXyz[i * 3 + 0] = merged.X[i];
                result.VertsXyz[i * 3 + 1] = merged.Y[i];
                result.VertsXyz[i * 3 + 2] = merged.Z[i];
            }
            result.Tris = merged.Tris.ToArray();
            result.QuadPairs = quadPairs;

            // --- topology + invariants (gate) ---
            ComputeInvariants(result, result.Tris, result.FaceCount, quadPairs,
                SteepFaceThresholdDegrees(s.MaxSlopeDegrees, s.SteepFaceMarginDegrees),
                SteepFaceMinHeightMeters(s.AgentMaxClimbMeters, s.CellHeightMeters));

            Log(log, "v2: weld verts=" + tileVertexSum + " -> " + mergedVertices
                + " faces=" + result.FaceCount + " edges=" + result.EdgeCount
                + " degenerate=" + merged.Degenerate + " (weldMerged=" + result.WeldMergedVertices + ")");
            if (gatePlateFaces > 0)
                Log(log, "v2: gate plate faces re-emitted: " + gatePlateFaces
                    + " (GatePlateBlend=" + s.GatePlateBlend + ", tilt capped at MaxSlopeDegrees="
                    + F(s.MaxSlopeDegrees) + " and at 2*climb/halfSpan=" + F(s.AgentMaxClimbMeters)
                    + "m, offset clamped to [-climb,0])");
            Log(log, "v2: invariants: faces=" + result.FaceCount + " edges=" + result.EdgeCount
                + " boundary=" + result.BoundaryEdges + " maxEdgeMult=" + result.MaxEdgeMultiplicity
                + " dupFaces=" + result.DuplicateFaces + " selfLoops=" + result.SelfLoops
                + " minEdges=" + result.RequiredEdges
                + " inverted=" + result.InvertedFaces + " vertical=" + result.VerticalFaces
                + " selfOverlap=" + result.SelfOverlapPairs + "/" + result.OverlappingFacesAny
                + " badQuadWalks=" + result.BadQuadWalks
                + " steep>" + F(SteepFaceThresholdDegrees(s.MaxSlopeDegrees, s.SteepFaceMarginDegrees)) + "deg="
                + result.SteepFaces + " tallSteep=" + result.SteepTallFaces
                + " (warn; tallSteep is the vertical-jump class - non-zero means a jump survived)"
                + " faceQualityMs=" + result.FaceQualityMs
                + " -> " + (result.InvariantsPassed ? "PASS" : "FAIL"));
            if (!result.InvariantsPassed)
                Log(log, "v2: ERROR mesh invariants failed: maxEdgeMult=" + result.MaxEdgeMultiplicity
                    + " inverted=" + result.InvertedFaces + " vertical=" + result.VerticalFaces
                    + " selfOverlap=" + result.SelfOverlapPairs
                    + " badQuadWalks=" + result.BadQuadWalks
                    + " dupFaces=" + result.DuplicateFaces + " selfLoops=" + result.SelfLoops
                    + " edges=" + result.EdgeCount + " required=" + result.RequiredEdges
                    + "; mesh is non-manifold/duplicated and must not be imported");

            LogIslands(result, log);

            total.Stop();
            result.TotalMs = total.ElapsedMilliseconds;
            Log(log, "v2 timings: tilesMs=" + result.TilesMs + " mergeMs=" + result.MergeMs
                + " totalMs=" + result.TotalMs);
            Log(log, "v2: tileMs min/avg/max=" + result.TileMsMin + "/" + result.TileMsAvg + "/" + result.TileMsMax);

            return result;
        }

        // Connected components by face adjacency (faces sharing an edge are one island). Always-on and
        // cheap; it is the data needed to choose a rule that prunes "sealed void" lids (small walkable
        // islands with no access, e.g. the tops of dungeon-corridor modules under a plank floor).
        private static void LogIslands(VoxelMeshResult r, System.Action<string> log)
        {
            if (log == null || r.FaceCount == 0)
                return;

            int nf = r.FaceCount;
            var fp = new int[nf];
            for (int i = 0; i < nf; i++) fp[i] = i;
            var edgeFirst = new Dictionary<long, int>(LongKeyComparer.Instance);
            for (int f = 0; f < nf; f++)
            {
                int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                FaceUnionEdge(edgeFirst, fp, f, a, b);
                FaceUnionEdge(edgeFirst, fp, f, b, c);
                FaceUnionEdge(edgeFirst, fp, f, c, a);
            }

            var faceCount = new Dictionary<int, int>();
            var areaSum = new Dictionary<int, double>();
            var cxSum = new Dictionary<int, double>();
            var cySum = new Dictionary<int, double>();
            var czSum = new Dictionary<int, double>();
            var zMin = new Dictionary<int, float>();
            var zMax = new Dictionary<int, float>();

            for (int f = 0; f < nf; f++)
            {
                int root = FaceFind(fp, f);
                int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                double ax = r.VertsXyz[a * 3], ay = r.VertsXyz[a * 3 + 1], az = r.VertsXyz[a * 3 + 2];
                double bx = r.VertsXyz[b * 3], by = r.VertsXyz[b * 3 + 1], bz = r.VertsXyz[b * 3 + 2];
                double cxx = r.VertsXyz[c * 3], cyy = r.VertsXyz[c * 3 + 1], czz = r.VertsXyz[c * 3 + 2];
                double area = 0.5 * Math.Abs((bx - ax) * (czz - az) - (bz - az) * (cxx - ax));
                if (area <= 0.0) area = 1e-9;

                int n;
                faceCount.TryGetValue(root, out n);
                faceCount[root] = n + 1;
                double s;
                areaSum.TryGetValue(root, out s);
                areaSum[root] = s + area;
                cxSum.TryGetValue(root, out s); cxSum[root] = s + (ax + bx + cxx) / 3.0 * area;
                cySum.TryGetValue(root, out s); cySum[root] = s + (ay + by + cyy) / 3.0 * area;
                czSum.TryGetValue(root, out s); czSum[root] = s + (az + bz + czz) / 3.0 * area;

                float fmin = (float)Math.Min(ay, Math.Min(by, cyy));
                float fmax = (float)Math.Max(ay, Math.Max(by, cyy));
                float zmn;
                if (zMin.TryGetValue(root, out zmn))
                {
                    if (fmin < zmn) zMin[root] = fmin;
                    if (fmax > zMax[root]) zMax[root] = fmax;
                }
                else
                {
                    zMin[root] = fmin;
                    zMax[root] = fmax;
                }
            }

            var roots = new List<int>(areaSum.Keys);
            roots.Sort(delegate (int x, int y)
            {
                int cmp = areaSum[y].CompareTo(areaSum[x]);
                if (cmp != 0) return cmp;
                cmp = faceCount[y].CompareTo(faceCount[x]);
                if (cmp != 0) return cmp;
                return x.CompareTo(y);
            });

            var ic = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            int listed = Math.Min(10, roots.Count);
            for (int i = 0; i < listed; i++)
            {
                int root = roots[i];
                double area = areaSum[root];
                double inv = area > 0.0 ? 1.0 / area : 0.0;
                if (i > 0) sb.Append(" | ");
                sb.Append(i)
                  .Append(":faces=").Append(faceCount[root].ToString(ic))
                  .Append(" area=").Append(area.ToString("F2", ic)).Append("m2")
                  .Append(" centroid=(")
                  .Append((cxSum[root] * inv).ToString("F2", ic)).Append(",")
                  .Append((cySum[root] * inv).ToString("F2", ic)).Append(",")
                  .Append((czSum[root] * inv).ToString("F2", ic)).Append(")")
                  .Append(" zr=[").Append(zMin[root].ToString("F2", ic)).Append("..")
                  .Append(zMax[root].ToString("F2", ic)).Append("]");
            }
            log("v2: islands=" + roots.Count + " totalFaces=" + nf + " largest=[" + sb.ToString() + "] top" + listed);
        }

        private static int FaceFind(int[] p, int x)
        {
            while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
            return x;
        }

        private static int FaceUnion(int[] p, int a, int b)
        {
            int ra = FaceFind(p, a);
            int rb = FaceFind(p, b);
            if (ra != rb) p[ra] = rb;
            return FaceFind(p, a);
        }

        private static void FaceUnionEdge(Dictionary<long, int> edgeFirstFace, int[] fparent, int face, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int other;
            if (edgeFirstFace.TryGetValue(key, out other))
                FaceUnion(fparent, other, face);
            else
                edgeFirstFace[key] = face;
        }

        // Removes every face-adjacency island with at most maxFaces faces and compacts the vertex array.
        // Reports the removed island/face counts, the largest removed island (faces + centroid) and the
        // islands kept. Vertex/face removal only, so the invariant gate is unaffected.
        private static void IslandCentroid(MergeOutput o, int[] fp, int nf, int root,
            ref float x, ref float y, ref float z)
        {
            double sx = 0, sy = 0, sz = 0;
            int n = 0;
            for (int f = 0; f < nf; f++)
            {
                if (FaceFind(fp, f) != root)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = o.Tris[f * 3 + k];
                    sx += o.X[v];
                    sy += o.Y[v];
                    sz += o.Z[v];
                    n++;
                }
            }
            if (n > 0)
            {
                x = (float)(sx / n);
                y = (float)(sy / n);
                z = (float)(sz / n);
            }
        }

        private static long CellKey(float x, float z, float cs)
        {
            int ix = (int)Math.Floor(x / cs);
            int iz = (int)Math.Floor(z / cs);
            return unchecked(((long)ix << 32) | (uint)iz);
        }

        private sealed class PruneStats
        {
            public int RemovedIslands, RemovedFaces, LargestRemoved, KeptIslands;
            public float LargestX, LargestY, LargestZ;
            public int ExemptIslands, ExemptFaces, ExemptLargest;
            public float ExemptX, ExemptY, ExemptZ;
            public double MaxKeptExemptFraction;
            public int MaxKeptFaces;
        }

        private static void PruneSmallIslands(MergeOutput o, int maxFaces, HashSet<long> steppedCells,
            float cs, PruneStats st)
        {
            st.RemovedIslands = 0;
            st.RemovedFaces = 0;
            st.LargestRemoved = 0;
            st.LargestX = 0f;
            st.LargestY = 0f;
            st.LargestZ = 0f;
            st.KeptIslands = 0;
            st.ExemptIslands = 0;
            st.ExemptFaces = 0;
            st.ExemptLargest = 0;
            st.ExemptX = 0f;
            st.ExemptY = 0f;
            st.ExemptZ = 0f;
            st.MaxKeptExemptFraction = 0.0;
            st.MaxKeptFaces = 0;

            int nf = o.Tris.Count / 3;
            if (nf == 0)
                return;

            var fp = new int[nf];
            var fpCore = new int[nf];
            for (int i = 0; i < nf; i++) { fp[i] = i; fpCore[i] = i; }
            var edgeFirst = new Dictionary<long, int>(LongKeyComparer.Instance);
            var edgeFirstCore = new Dictionary<long, int>(LongKeyComparer.Instance);
            var exemptFace = new bool[nf];
            for (int f = 0; f < nf; f++)
            {
                int a = o.Tris[f * 3], b = o.Tris[f * 3 + 1], c = o.Tris[f * 3 + 2];
                FaceUnionEdge(edgeFirst, fp, f, a, b);
                FaceUnionEdge(edgeFirst, fp, f, b, c);
                FaceUnionEdge(edgeFirst, fp, f, c, a);
                if (steppedCells != null)
                {
                    float cx = 0, cz = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = o.Tris[f * 3 + k];
                        cx += o.X[v];
                        cz += o.Z[v];
                    }
                    exemptFace[f] = steppedCells.Contains(CellKey(cx / 3f, cz / 3f, cs));
                }
                if (exemptFace[f]) continue;
                FaceUnionEdge(edgeFirstCore, fpCore, f, a, b);
                FaceUnionEdge(edgeFirstCore, fpCore, f, b, c);
                FaceUnionEdge(edgeFirstCore, fpCore, f, c, a);
            }

            var count = new Dictionary<int, int>();
            var coreCount = new Dictionary<int, int>();
            var exemptCount = new Dictionary<int, int>();
            for (int f = 0; f < nf; f++)
            {
                int root = FaceFind(fp, f);
                int c;
                count.TryGetValue(root, out c);
                count[root] = c + 1;
                if (exemptFace[f])
                {
                    int e;
                    exemptCount.TryGetValue(root, out e);
                    exemptCount[root] = e + 1;
                    continue;
                }
                int cr = FaceFind(fpCore, f);
                int cc;
                coreCount.TryGetValue(cr, out cc);
                coreCount[cr] = cc + 1;
            }
            var islandCoreMax = new Dictionary<int, int>();
            for (int f = 0; f < nf; f++)
            {
                if (exemptFace[f]) continue;
                int ir = FaceFind(fp, f);
                int cr = FaceFind(fpCore, f);
                int cs2;
                coreCount.TryGetValue(cr, out cs2);
                int cur;
                islandCoreMax.TryGetValue(ir, out cur);
                if (cs2 > cur) islandCoreMax[ir] = cs2;
            }

            var dropped = new Dictionary<int, bool>();
            int largestRoot = -1;
            int exemptRoot = -1;
            foreach (KeyValuePair<int, int> kv in count)
            {
                // Coalition test: with the exemption faces taken out, what is left of this island? If every
                // remaining core is at most the cap, then the island is a chain of fragments the unmodified
                // build prunes one by one and only the exemption holds it together - leftover, not a
                // network - so it goes whatever its size. Real networks keep a core far above the cap.
                int coreMax;
                islandCoreMax.TryGetValue(kv.Key, out coreMax);
                bool coalition = kv.Value >= 3 && coreMax <= maxFaces;
                if (kv.Value <= maxFaces || coalition)
                {
                    dropped[kv.Key] = true;
                    st.RemovedIslands++;
                    st.RemovedFaces += kv.Value;
                    if (coalition && kv.Value > maxFaces)
                    {
                        st.ExemptIslands++;
                        st.ExemptFaces += kv.Value;
                        if (kv.Value > st.ExemptLargest)
                        {
                            st.ExemptLargest = kv.Value;
                            exemptRoot = kv.Key;
                        }
                    }
                    if (kv.Value > st.LargestRemoved)
                    {
                        st.LargestRemoved = kv.Value;
                        largestRoot = kv.Key;
                    }
                }
                else
                {
                    st.KeptIslands++;
                    int e;
                    exemptCount.TryGetValue(kv.Key, out e);
                    double frac = kv.Value > 0 ? (double)e / kv.Value : 0.0;
                    if (frac > st.MaxKeptExemptFraction)
                    {
                        st.MaxKeptExemptFraction = frac;
                        st.MaxKeptFaces = kv.Value;
                    }
                }
            }
            if (st.RemovedIslands == 0)
                return;

            if (exemptRoot >= 0)
                IslandCentroid(o, fp, nf, exemptRoot, ref st.ExemptX, ref st.ExemptY, ref st.ExemptZ);
            if (largestRoot >= 0)
                IslandCentroid(o, fp, nf, largestRoot, ref st.LargestX, ref st.LargestY, ref st.LargestZ);

            var remap = new int[o.X.Count];
            for (int i = 0; i < remap.Length; i++) remap[i] = -1;
            var newX = new List<float>();
            var newY = new List<float>();
            var newZ = new List<float>();
            var newTris = new List<int>(o.Tris.Count);
            for (int f = 0; f < nf; f++)
            {
                bool drop;
                if (dropped.TryGetValue(FaceFind(fp, f), out drop) && drop)
                    continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = o.Tris[f * 3 + k];
                    if (remap[v] < 0)
                    {
                        remap[v] = newX.Count;
                        newX.Add(o.X[v]);
                        newY.Add(o.Y[v]);
                        newZ.Add(o.Z[v]);
                    }
                    newTris.Add(remap[v]);
                }
            }

            o.X.Clear(); o.X.AddRange(newX);
            o.Y.Clear(); o.Y.AddRange(newY);
            o.Z.Clear(); o.Z.AddRange(newZ);
            o.Tris.Clear(); o.Tris.AddRange(newTris);
            o.MinY = float.MaxValue;
            o.MaxY = float.MinValue;
            for (int i = 0; i < o.Y.Count; i++)
            {
                if (o.Y[i] < o.MinY) o.MinY = o.Y[i];
                if (o.Y[i] > o.MaxY) o.MaxY = o.Y[i];
            }
        }

        // ================================================================ merge-level stitch
        // Cross-tile stitch over the MERGED mesh, run before the prune so near-miss pieces join the big
        // networks (and therefore survive pruning). Same rule as the per-tile stitch; the clearance
        // check re-reads the input soup through a coarse XZ grid and refuses the stitch when solid
        // geometry spans the gap envelope.
        private static void MergeStitch(MergeOutput o, float[] soupVerts, int[] soupTris, int soupTriCount,
            float cs, float ch, float offset, float bminY, int walkableHeight, int walkableClimb,
            int stitchMaxGapCells, out int pairs, out int added, out int refused)
        {
            pairs = 0;
            added = 0;
            refused = 0;
            int nf = o.Tris.Count / 3;
            if (nf < 2 || o.X.Count < 3)
                return;
            float maxGap = stitchMaxGapCells * cs;
            float maxGap2 = maxGap * maxGap;

            int[] fp = new int[nf];
            for (int i = 0; i < nf; i++) fp[i] = i;
            var edgeMult = new Dictionary<long, int>(LongKeyComparer.Instance);
            for (int f = 0; f < nf; f++)
            {
                int a = o.Tris[f * 3], b = o.Tris[f * 3 + 1], c = o.Tris[f * 3 + 2];
                StitchAddEdge(edgeMult, fp, f, a, b);
                StitchAddEdge(edgeMult, fp, f, b, c);
                StitchAddEdge(edgeMult, fp, f, c, a);
            }

            var compIndex = new Dictionary<int, int>();
            var compRoots = new List<int>();
            for (int f = 0; f < nf; f++)
            {
                int root = StitchFind(fp, f);
                if (!compIndex.ContainsKey(root))
                {
                    compIndex[root] = compRoots.Count;
                    compRoots.Add(root);
                }
            }
            int nc = compRoots.Count;
            if (nc < 2 || nc > 4096)
                return;

            var bA = new List<int>[nc];
            var bB = new List<int>[nc];
            for (int i = 0; i < nc; i++) { bA[i] = new List<int>(); bB[i] = new List<int>(); }
            for (int f = 0; f < nf; f++)
            {
                int ci = compIndex[StitchFind(fp, f)];
                for (int k = 0; k < 3; k++)
                {
                    int i0 = o.Tris[f * 3 + k];
                    int i1 = o.Tris[f * 3 + ((k + 1) % 3)];
                    if (edgeMult[StitchEdgeKey(i0, i1)] != 1)
                        continue;                     // interior edge
                    bA[ci].Add(i0);
                    bB[ci].Add(i1);
                }
            }

            // Coarse soup grid for the clearance probe.
            const float soupCell = 4f;
            Dictionary<long, List<int>> grid;
            List<int> bigList;
            BuildSoupGrid(soupVerts, soupTris, soupTriCount, soupCell, out grid, out bigList);

            var compParent = new int[nc];
            for (int i = 0; i < nc; i++) compParent[i] = i;
            var edgeDistinct = new Dictionary<long, int>(LongKeyComparer.Instance);

            for (int i = 0; i < nc; i++)
            {
                int ei = bA[i].Count;
                if (ei == 0 || ei > 4096)
                    continue;
                for (int j = i + 1; j < nc; j++)
                {
                    int ej = bA[j].Count;
                    if (ej == 0 || ej > 4096)
                        continue;
                    if (StitchFind(compParent, i) == StitchFind(compParent, j))
                        continue;

                    // Coarse AABB pre-reject.
                    float aminX = float.MaxValue, amaxX = float.MinValue, aminZ = float.MaxValue, amaxZ = float.MinValue;
                    for (int k = 0; k < ei; k++)
                    {
                        float x0 = o.X[bA[i][k]], z0 = o.Z[bA[i][k]], x1 = o.X[bB[i][k]], z1 = o.Z[bB[i][k]];
                        if (x0 < aminX) aminX = x0;
                        if (x1 < aminX) aminX = x1;
                        if (x0 > amaxX) amaxX = x0;
                        if (x1 > amaxX) amaxX = x1;
                        if (z0 < aminZ) aminZ = z0;
                        if (z1 < aminZ) aminZ = z1;
                        if (z0 > amaxZ) amaxZ = z0;
                        if (z1 > amaxZ) amaxZ = z1;
                    }
                    float bminX = float.MaxValue, bmaxX = float.MinValue, bminZ = float.MaxValue, bmaxZ = float.MinValue;
                    for (int k = 0; k < ej; k++)
                    {
                        float x0 = o.X[bA[j][k]], z0 = o.Z[bA[j][k]], x1 = o.X[bB[j][k]], z1 = o.Z[bB[j][k]];
                        if (x0 < bminX) bminX = x0;
                        if (x1 < bminX) bminX = x1;
                        if (x0 > bmaxX) bmaxX = x0;
                        if (x1 > bmaxX) bmaxX = x1;
                        if (z0 < bminZ) bminZ = z0;
                        if (z1 < bminZ) bminZ = z1;
                        if (z0 > bmaxZ) bmaxZ = z0;
                        if (z1 > bmaxZ) bmaxZ = z1;
                    }
                    double ddx = Math.Max(0.0, Math.Max(aminX - bmaxX, bminX - amaxX));
                    double ddz = Math.Max(0.0, Math.Max(aminZ - bmaxZ, bminZ - amaxZ));
                    if (ddx * ddx + ddz * ddz > maxGap2)
                        continue;

                    // Nearest boundary-edge pair (3D distance).
                    int bestIA = -1, bestIB = -1;
                    double best = double.MaxValue;
                    for (int ka = 0; ka < ei; ka++)
                    {
                        int a0 = bA[i][ka], a1 = bB[i][ka];
                        for (int kb = 0; kb < ej; kb++)
                        {
                            int c0 = bA[j][kb], c1 = bB[j][kb];
                            double d = EdgePairDist2(o, a0, a1, c0, c1);
                            if (d < best) { best = d; bestIA = ka; bestIB = kb; }
                        }
                    }
                    if (bestIA < 0 || best > maxGap2)
                        continue;

                    int ua = bA[i][bestIA], ub = bB[i][bestIA];
                    int va = bA[j][bestIB], vb = bB[j][bestIB];
                    if (ua == va || ua == vb || ub == va || ub == vb)
                        continue;
                    float yLo = Math.Min(o.Y[ua], o.Y[va]);
                    float yHi = Math.Max(o.Y[ua], o.Y[va]);
                    if (yHi - yLo > walkableClimb * ch + 1e-4f)
                        continue;

                    pairs++;

                    // Clearance: solid geometry must not span the gap envelope. The floors themselves
                    // sit at the envelope's bottom (excluded by the 0.6*ch lift); a wall, a closed door
                    // or a ceiling within walkableHeight of the floor trips it.
                    float mxA = (o.X[ua] + o.X[ub]) * 0.5f, mzA = (o.Z[ua] + o.Z[ub]) * 0.5f;
                    float mxB = (o.X[va] + o.X[vb]) * 0.5f, mzB = (o.Z[va] + o.Z[vb]) * 0.5f;
                    float soupFloor = yLo - offset;
                    float envLo = soupFloor + 0.6f * ch;
                    float envHi = soupFloor + walkableHeight * ch;
                    if (GapEnvelopeSolid(soupVerts, soupTris, grid, bigList, soupCell,
                            mxA, mzA, mxB, mzB, cs, envLo, envHi))
                    {
                        refused++;
                        continue;
                    }

                    // Fresh-edge check, then add the quad (reusing existing vertices).
                    int mu, mv;
                    edgeMult.TryGetValue(StitchEdgeKey(ua, ub), out mu);
                    edgeMult.TryGetValue(StitchEdgeKey(va, vb), out mv);
                    if (mu != 1 || mv != 1)
                    {
                        refused++;                        // an earlier stitch already closed one side
                        continue;
                    }
                    if (edgeMult.ContainsKey(StitchEdgeKey(ua, va))
                        || edgeMult.ContainsKey(StitchEdgeKey(ub, vb))
                        || edgeMult.ContainsKey(StitchEdgeKey(ua, vb)))
                    {
                        refused++;
                        continue;
                    }
                    int t1a = ua, t1b = va, t1c = vb;
                    if (TriNormalYList(o, t1a, t1b, t1c) < 0f) { t1b = vb; t1c = va; }
                    int t2a = ua, t2b = vb, t2c = ub;
                    if (TriNormalYList(o, t2a, t2b, t2c) < 0f) { t2b = ub; t2c = vb; }
                    if (t1a == t1b || t1b == t1c || t1a == t1c || t2a == t2b || t2b == t2c || t2a == t2c)
                    {
                        refused++;
                        continue;
                    }
                    o.Tris.Add(t1a); o.Tris.Add(t1b); o.Tris.Add(t1c);
                    o.Tris.Add(t2a); o.Tris.Add(t2b); o.Tris.Add(t2c);
                    StitchRegister(edgeMult, ua, va);
                    StitchRegister(edgeMult, ub, vb);
                    StitchRegister(edgeMult, ua, vb);
                    StitchRegister(edgeMult, ua, ub);
                    StitchRegister(edgeMult, va, vb);
                    added += 2;
                    StitchUnion(compParent, i, j);
                }
            }
        }

        private static double EdgePairDist2(MergeOutput o, int a0, int a1, int b0, int b1)
        {
            double d = VDist2List(o, a0, b0);
            double t = VDist2List(o, a0, b1);
            if (t < d) d = t;
            t = VDist2List(o, a1, b0);
            if (t < d) d = t;
            t = VDist2List(o, a1, b1);
            if (t < d) d = t;
            return d;
        }

        private static double VDist2List(MergeOutput o, int i, int j)
        {
            double dx = o.X[i] - o.X[j];
            double dy = o.Y[i] - o.Y[j];
            double dz = o.Z[i] - o.Z[j];
            return dx * dx + dy * dy + dz * dz;
        }

        // ==================================================================
        // GATE / DOOR PLATE BLEND  (NavMeshVoxelGatePlateBlend, default on)
        // ==================================================================
        //
        // WHAT IT IS. A prop that carries a rigid, dead-flat FLOOR SLAB projects into the voxel grid as a
        // perfectly horizontal plate, because the grid's span tops are ch-quantised and the slab is level.
        // That is right in the abstract - the navmesh IS a projection of the collision - but it ignores the
        // ground the slab is dropped onto: drop a castle gate's floor colliders on sloping terrain and the
        // plate ends up a metre above the low side of the passage, more than one agent climb, so that end
        // stops stitching. The reported case (CC_20, european_castle_wall_gate_l1) measured exactly that:
        // 10 faces, every vertex at y = 86.30, slope 0.0 deg, over x 1127.75..1133.00 / z 989.00..993.00,
        // with the terrain falling 87.1 -> 82.8 m across the passage. The NW end met its neighbours inside
        // the climb; the SE end met faces 0.4-1.3 m below it (47.1 and 58.0 deg).
        //
        // THE RULE, generically - no prop names, only geometry. A "plate" is a maximal edge-connected group
        // of dead-flat up-facing faces that all sit on ONE ch level (>= 3 faces, 1..120 m^2, no side longer
        // than 8 m - bigger is a floor / a deck / a terrace and tilting it would move real geometry). It is
        // re-generated when ALL of:
        //
        //  1. PROUD: the plate's own height is more than one agent climb above - but at most
        //     GatePlateProudMaxMeters above - the 10th percentile of the surrounding mesh's heights in the
        //     ring around the plate's own footprint. This is the "the ground falls away from under me"
        //     signature, and requiring it to exceed the climb is what keeps already-stitching plates out.
        //  2. FITTABLE: at least GatePlateMinSamples neighbouring faces inside the ring, spread over at
        //     least GatePlateMinSpanMeters, with a well-conditioned normal matrix. Weights are the faces'
        //     own XZ areas (capped at 4 m^2 so one big floor cannot dominate), so this is a least-squares
        //     fit over the SURFACE, not over the raw soup - the soup carries the slab's own colliders and
        //     the wall above it, and a fit over it would tilt the plate towards the wall. Our OWN synthesized
        //     ramp / connector cells are excluded for the same reason: they answer a different question, and
        //     measured on the suite's narrow-doorway lab they made a real landing read 1.33 m "proud" and
        //     tilt it 11 deg off a level it must keep.
        //  3. BETTER: the clamped plane has to beat the flat plate by GatePlateMinImproveMeters on the worst
        //     neighbour mismatch, otherwise the plate is left byte-identical.
        //
        // THE FIT, and the clamps. y = a + b*(x-cx) + c*(z-cz) about the plate's own XZ centroid. The tilt is
        // clamped twice: to tan(MaxSlopeDegrees) (the agent's walk cap, the user's own bound) and to
        // GatePlateMaxRise / halfSpan, so a 5 m plate can never become a 6 m wall - a blend is a tilted
        // floor, not a ramp. The vertical offset is the MEDIAN of the per-sample residuals, clamped to
        // [-climb, 0]: the median is the L1 minimiser, so it balances the ends, and the clamp never raises
        // the plate (raising only makes the proud class worse) and never sinks it by more than one agent
        // climb (which is what the uniform HeightOffsetMeters lift already buys us).
        //
        // THE RE-EMIT is a rotation of the plate's OWN vertices - the user's own phrasing, "treated as a solid
        // plane, rotated by up to 50 degrees to better align with surrounding navmeshes". It is deliberately
        // not a detach-and-regenerate: keeping the shared vertices keeps every edge the plate had, so the
        // pass adds and removes no face and touches no edge. A shared VERTEX does move with the plate, so the
        // handful of faces that met the plate at a corner come along by at most the plate's own offset (one
        // agent climb, printed per plate in the trace). The alternative - clone the plate's vertices and
        // bridge each seam with a connector quad - was implemented and measured first: 172 connector quads on
        // CC_20, and because a 0.2-0.5 m connector sliver is something the steep repair or the cleanup can
        // remove, it turned Birtu from 5 islands into 8 and CC_20 from 15 into 16. The rotation is what ships.
        //
        // Deterministic: faces are visited in index order, groups are keyed by their lowest face index,
        // every accumulator is a double summed in that order, and the offsets come from a sorted copy of
        // the residual list. Cheap: one mask pass, one grid build, one local query per plate - 200-350 ms on
        // CC_20's 241k faces.

        private sealed class PlatePlan
        {
            public int[] Faces;
            public List<int> Verts;               // the plate's own vertices (rotated in place)
            public float MinX, MaxX, MinZ, MaxZ;
            public float Cx, Cz, H;
            public float A0, GradB, GradC, Off;   // y = A0 + Off + GradB*(x-Cx) + GradC*(z-Cz)
            public float TiltDeg, TiltCapDeg, Offset;
            public int Samples;
            public float Proud, Rms, WorstBefore, WorstAfter;
        }

        private static int BlendGatePlates(MergeOutput o, float cs, float ch, float climb,
            float maxSlopeDegrees, HashSet<long> forcedRampCells, Action<string> log)
        {
            int faceCount = o.Tris.Count / 3;
            if (faceCount == 0 || o.X.Count < 3) return 0;
            if (climb <= 0f) return 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // --- 1. dead-flat, up-facing face mask ----------------------------------------------------
            var flat = new bool[faceCount];
            double sinFlat = Math.Sin(GatePlateFlatDegrees * Math.PI / 180.0);
            float dyTol = ch * 0.5f + 1e-4f;
            for (int f = 0; f < faceCount; f++)
            {
                int a = o.Tris[f * 3], b = o.Tris[f * 3 + 1], c = o.Tris[f * 3 + 2];
                double ux = o.X[b] - o.X[a], uy = o.Y[b] - o.Y[a], uz = o.Z[b] - o.Z[a];
                double wx = o.X[c] - o.X[a], wy = o.Y[c] - o.Y[a], wz = o.Z[c] - o.Z[a];
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-12) continue;                                       // degenerate
                if (Math.Sqrt(nx * nx + nz * nz) / len > sinFlat) continue;       // not dead flat
                if (FootprintM2(o.X, o.Y, o.Z, a, b, c) <= 0.0) continue;       // must look up
                flat[f] = true;
            }

            // --- 2. union the flat faces into one plate per coplanar level ----------------------------
            var parent = new int[faceCount];
            for (int i = 0; i < faceCount; i++) parent[i] = i;
            var edgeOwner = new Dictionary<long, int>(LongKeyComparer.Instance);
            for (int f = 0; f < faceCount; f++)
            {
                if (!flat[f]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int i0 = o.Tris[f * 3 + k], i1 = o.Tris[f * 3 + ((k + 1) % 3)];
                    if (Math.Abs(o.Y[i0] - o.Y[i1]) > dyTol) continue;
                    long key = StitchEdgeKey(i0, i1);
                    int other;
                    if (edgeOwner.TryGetValue(key, out other))
                    {
                        if (other != f) FaceUnion(parent, other, f);
                    }
                    else edgeOwner[key] = f;
                }
            }
            edgeOwner = null;

            var groups = new Dictionary<int, List<int>>();
            var groupOrder = new List<int>();
            for (int f = 0; f < faceCount; f++)
            {
                if (!flat[f]) continue;
                int root = FaceFind(parent, f);
                List<int> g;
                if (!groups.TryGetValue(root, out g))
                {
                    g = new List<int>();
                    groups[root] = g;
                    groupOrder.Add(root);
                }
                g.Add(f);
            }
            groupOrder.Sort();                       // by lowest face index: deterministic
            if (groupOrder.Count == 0) return 0;

            // --- 3. one face grid for the local neighbour queries -------------------------------------
            // Keyed by the face's CENTROID cell, one entry per face (never by bbox: a face with a long
            // diagonal would otherwise be listed in every cell it touches and leak its height into rings
            // metres away - measured on CC_20, where a 13 m gatehouse face poisoned the gate plate's fit).
            float gcell = Math.Max(4f * cs, 1f);
            var grid = new Dictionary<long, List<int>>(LongKeyComparer.Instance);
            for (int f = 0; f < faceCount; f++)
            {
                int a = o.Tris[f * 3], b = o.Tris[f * 3 + 1], c = o.Tris[f * 3 + 2];
                float fx = (o.X[a] + o.X[b] + o.X[c]) / 3f;
                float fz = (o.Z[a] + o.Z[b] + o.Z[c]) / 3f;
                long key = unchecked(((long)(int)Math.Floor(fx / gcell) << 32) | (uint)(int)Math.Floor(fz / gcell));
                List<int> lst;
                if (!grid.TryGetValue(key, out lst)) { lst = new List<int>(); grid[key] = lst; }
                lst.Add(f);
            }

            // --- 4. fit every plate against the ORIGINAL mesh; nothing is mutated until all plans exist -
            float ring = Math.Max(8f * cs, 1.5f);
            var plans = new List<PlatePlan>();
            var isPlate = new bool[faceCount];
            int candidates = 0, skipped = 0;
            var sampleY = new List<float>();
            var sampleX = new List<float>();
            var sampleZ = new List<float>();
            var sampleW = new List<float>();
            int rejSize = 0, rejLevel = 0, rejSamples = 0, rejProud = 0, rejFit = 0, rejBetter = 0;
            for (int gi = 0; gi < groupOrder.Count; gi++)
            {
                List<int> g = groups[groupOrder[gi]];
                if (g.Count < GatePlateMinFaces) continue;

                // plate extents + its own vertex set
                float minX = float.MaxValue, maxX = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;
                float hSum = 0f, vSum = 0f, area = 0f;
                var verts = new List<int>();
                var seenV = new Dictionary<int, bool>();
                for (int fi = 0; fi < g.Count; fi++)
                {
                    int f = g[fi];
                    isPlate[f] = true;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = o.Tris[f * 3 + k];
                        if (seenV.ContainsKey(v)) continue;
                        seenV[v] = true;
                        verts.Add(v);
                        if (o.X[v] < minX) minX = o.X[v];
                        if (o.X[v] > maxX) maxX = o.X[v];
                        if (o.Z[v] < minZ) minZ = o.Z[v];
                        if (o.Z[v] > maxZ) maxZ = o.Z[v];
                        hSum += o.Y[v];
                        vSum += 1f;
                    }
                    double a2 = FootprintM2(o.X, o.Y, o.Z, o.Tris[f * 3], o.Tris[f * 3 + 1], o.Tris[f * 3 + 2]);
                    if (a2 > 0.0) area += (float)a2;
                }
                if (vSum < 3f || area < GatePlateMinAreaM2 || area > GatePlateMaxAreaM2) { rejSize++; ResetPlate(isPlate, g); continue; }
                float spanX = maxX - minX, spanZ = maxZ - minZ;
                if (spanX > GatePlateMaxSpanMeters || spanZ > GatePlateMaxSpanMeters)
                { rejSize++; ResetPlate(isPlate, g); continue; }
                float h = hSum / vSum;
                float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
                // one ch level, or it is not a plate
                bool levelOk = true;
                for (int vi = 0; vi < verts.Count; vi++)
                    if (Math.Abs(o.Y[verts[vi]] - h) > dyTol) { levelOk = false; break; }
                if (!levelOk) { rejLevel++; ResetPlate(isPlate, g); continue; }
                candidates++;

                // --- neighbour samples: non-plate faces in the ring, one per face, area weighted ----
                sampleX.Clear(); sampleY.Clear(); sampleZ.Clear(); sampleW.Clear();
                float bandLo = h - 3f * climb, bandHi = h + climb;
                int ix0 = (int)Math.Floor((minX - ring) / gcell), ix1 = (int)Math.Floor((maxX + ring) / gcell);
                int iz0 = (int)Math.Floor((minZ - ring) / gcell), iz1 = (int)Math.Floor((maxZ + ring) / gcell);
                for (int iz = iz0; iz <= iz1; iz++)
                    for (int ix = ix0; ix <= ix1; ix++)
                    {
                        List<int> lst;
                        if (!grid.TryGetValue(unchecked(((long)ix << 32) | (uint)iz), out lst)) continue;
                        for (int li = 0; li < lst.Count; li++)
                        {
                            int f = lst[li];
                            if (isPlate[f]) continue;
                            int a = o.Tris[f * 3], b = o.Tris[f * 3 + 1], c = o.Tris[f * 3 + 2];
                            float fx = (o.X[a] + o.X[b] + o.X[c]) / 3f;
                            float fz = (o.Z[a] + o.Z[b] + o.Z[c]) / 3f;
                            float fy = (o.Y[a] + o.Y[b] + o.Y[c]) / 3f;
                            if (fx < minX - ring || fx > maxX + ring || fz < minZ - ring || fz > maxZ + ring)
                                continue;
                            if (fy < bandLo || fy > bandHi) continue;
                            // Our own synthesized ramp / connector cells are NOT surroundings: they are the
                            // answer to a different question, and letting them into the fit tilts a real floor.
                            // Measured on the suite's narrow-doorway lab: the upper landing (h=3.4) sampled its
                            // own ladder connector at y=2.07, read itself 1.33 m "proud", and was tilted 11 deg
                            // off a level it must keep - the lab's crossing went from connected to broken.
                            if (forcedRampCells != null && forcedRampCells.Count > 0
                                && forcedRampCells.Contains(CellKey(fx, fz, cs)))
                                continue;
                            double a2 = FootprintM2(o.X, o.Y, o.Z, a, b, c);
                            if (a2 <= 0.0) continue;                       // a wall / an underside is not ground
                            sampleX.Add(fx); sampleY.Add(fy); sampleZ.Add(fz);
                            sampleW.Add((float)(a2 < 4.0 ? a2 : 4.0));
                        }
                    }
                int ns = sampleY.Count;
                if (ns < GatePlateMinSamples) { rejSamples++; TracePlate(log, "samples=" + ns, minX, minZ, maxX, maxZ, h, g.Count, area); ResetPlate(isPlate, g); continue; }

                // --- 1. proud ---------------------------------------------------------------------------
                var sortedY = new List<float>(sampleY);
                sortedY.Sort();
                float p10 = sortedY[(int)((sortedY.Count - 1) * 0.10)];
                float proud = h - p10;
                if (proud <= climb || proud > GatePlateProudMaxMeters)
                {
                    rejProud++;
                    TracePlate(log, "proud=" + F(proud) + "m p10=" + F(p10) + " n=" + ns, minX, minZ, maxX, maxZ, h, g.Count, area);
                    ResetPlate(isPlate, g); continue;
                }

                // --- 2. weighted least squares, taken about the SAMPLE mean -------------------------
                // The cross-moments must be centred on the sample cloud, not on the plate centroid: the
                // uncentred normal equations carry an a0*sum(w*dx) term, and solving them as if they did not
                // is what produced a 10.6 gradient (a 85 deg "plane") for the gate plate's 2 m sample band.
                // Two passes: the weighted means, then the centred moments.
                double sw = 0.0, sx = 0.0, sz = 0.0, sy = 0.0;
                float sminX = float.MaxValue, smaxX = float.MinValue;
                float sminZ = float.MaxValue, smaxZ = float.MinValue;
                for (int i = 0; i < ns; i++)
                {
                    double w = sampleW[i];
                    sw += w;
                    sx += w * (sampleX[i] - cx);
                    sz += w * (sampleZ[i] - cz);
                    sy += w * sampleY[i];
                    if (sampleX[i] < sminX) sminX = sampleX[i];
                    if (sampleX[i] > smaxX) smaxX = sampleX[i];
                    if (sampleZ[i] < sminZ) sminZ = sampleZ[i];
                    if (sampleZ[i] > smaxZ) smaxZ = sampleZ[i];
                }
                float spread = Math.Max(smaxX - sminX, smaxZ - sminZ);
                if (spread < GatePlateMinSpanMeters || sw <= 0.0)
                {
                    rejFit++;
                    TracePlate(log, "spread=" + F(spread) + "m proud=" + F(proud) + "m n=" + ns,
                        minX, minZ, maxX, maxZ, h, g.Count, area);
                    ResetPlate(isPlate, g); continue;
                }
                double mxd = sx / sw, mzd = sz / sw, myd = sy / sw;
                double cxx = 0.0, czz = 0.0, cxz = 0.0, cxy = 0.0, czy = 0.0;
                for (int i = 0; i < ns; i++)
                {
                    double w = sampleW[i];
                    double ux = (sampleX[i] - cx) - mxd;
                    double uz = (sampleZ[i] - cz) - mzd;
                    double uy = sampleY[i] - myd;
                    cxx += w * ux * ux; czz += w * uz * uz; cxz += w * ux * uz;
                    cxy += w * ux * uy; czy += w * uz * uy;
                }
                double det = cxx * czz - cxz * cxz;
                double cxxCzz = cxx * czz;
                // Conditioning, not just non-singularity: det must be a real fraction of cxx*czz, i.e. the
                // samples must not be nearly collinear.
                if (cxxCzz <= 0.0 || det <= GatePlateMinCorrelation1 * cxxCzz)
                {
                    rejFit++;
                    TracePlate(log, "spread=" + F(spread) + "m proud=" + F(proud) + "m n=" + ns
                        + " corr=" + F((float)(cxxCzz > 0.0 ? cxz / Math.Sqrt(cxxCzz) : 0.0)),
                        minX, minZ, maxX, maxZ, h, g.Count, area);
                    ResetPlate(isPlate, g); continue;
                }
                double gradX = (cxy * czz - czy * cxz) / det;
                double gradZ = (cxx * czy - cxy * cxz) / det;
                // The plane's height at the PLATE's centroid is where the tilt pivots (a rotation, not a
                // re-fit of the offset): y = a0 + gradX*(x-cx) + gradZ*(z-cz).
                double a0 = myd - gradX * mxd - gradZ * mzd;
                // Sanity: the plane must pass through the plate's own level band, or the sample cloud is
                // describing something else entirely and the plate is left exactly as it was.
                if (Math.Abs(a0 - h) > GatePlateProudMaxMeters * 2.0)
                {
                    rejFit++;
                    TracePlate(log, "a0=" + F((float)a0) + "m vs h=" + F(h) + "m n=" + ns
                        + " gradX=" + F((float)gradX) + " gradZ=" + F((float)gradZ),
                        minX, minZ, maxX, maxZ, h, g.Count, area);
                    ResetPlate(isPlate, g); continue;
                }

                // --- tilt clamp: the agent's walk cap AND the plate's own rise budget ----------------
                double tilt = Math.Sqrt(gradX * gradX + gradZ * gradZ);
                float halfSpan = Math.Max(spanX, spanZ) * 0.5f;
                if (halfSpan < 1e-3f) { skipped++; ResetPlate(isPlate, g); continue; }
                double tiltCap = Math.Tan(maxSlopeDegrees * Math.PI / 180.0);
                double riseCap = GatePlateMaxRiseMeters(climb) / halfSpan;
                if (riseCap < tiltCap) tiltCap = riseCap;
                if (tiltCap <= 0.0) { rejFit++; ResetPlate(isPlate, g); continue; }
                if (tilt > tiltCap)
                {
                    double sc = tiltCap / tilt;
                    gradX *= sc;
                    gradZ *= sc;
                    tilt = tiltCap;
                }

                // --- offset: median of the residuals, clamped so the plate never rises and never sinks
                //     by more than one agent climb (the uniform lift is what keeps it off the collision)
                var res = new List<float>(ns);
                for (int i = 0; i < ns; i++)
                    res.Add((float)(sampleY[i] - (a0 + gradX * (sampleX[i] - cx) + gradZ * (sampleZ[i] - cz))));
                var resSorted = new List<float>(res);
                resSorted.Sort();
                float off = resSorted[resSorted.Count / 2];
                if (off > 0f) off = 0f;
                if (off < -climb) off = -climb;

                // --- 3. is it actually better? ------------------------------------------------------
                float worstBefore = 0f, worstAfter = 0f, rmsAcc = 0f;
                for (int i = 0; i < ns; i++)
                {
                    float e0 = Math.Abs(sampleY[i] - h);
                    if (e0 > worstBefore) worstBefore = e0;
                    float e1 = (float)Math.Abs(res[i] + off);
                    if (e1 > worstAfter) worstAfter = e1;
                    rmsAcc += sampleW[i] * e1 * e1;
                }
                float rms = (float)Math.Sqrt(rmsAcc / sw);
                if (rms > GatePlateMaxFitRmsMeters || worstAfter + GatePlateMinImproveMeters > worstBefore)
                {
                    rejBetter++;
                    TracePlate(log, "tilt=" + F((float)(Math.Atan(tilt) * 180.0 / Math.PI)) + "deg"
                        + " proud=" + F(proud) + "m n=" + ns + " rms=" + F(rms) + "m"
                        + " worst=" + F(worstBefore) + "->" + F(worstAfter) + "m off=" + F(off) + "m",
                        minX, minZ, maxX, maxZ, h, g.Count, area);
                    ResetPlate(isPlate, g); continue;
                }

                var plan = new PlatePlan();
                plan.Faces = g.ToArray();
                plan.Verts = verts;
                plan.MinX = minX; plan.MaxX = maxX; plan.MinZ = minZ; plan.MaxZ = maxZ;
                plan.Cx = cx; plan.Cz = cz; plan.H = h;
                plan.A0 = (float)a0;
                plan.GradB = (float)gradX;
                plan.GradC = (float)gradZ;
                plan.Off = off;
                plan.TiltDeg = (float)(Math.Atan(tilt) * 180.0 / Math.PI);
                plan.TiltCapDeg = (float)(Math.Atan(tiltCap) * 180.0 / Math.PI);
                plan.Offset = off;
                plan.Samples = ns;
                plan.Proud = proud;
                plan.Rms = rms;
                plan.WorstBefore = worstBefore;
                plan.WorstAfter = worstAfter;
                plans.Add(plan);
                // isPlate stays set: a later group cannot sample this plate's faces as "neighbour"
            }

            if (plans.Count == 0)
            {
                Log(log, "v2: gate plates: examined=" + candidates + " blended=0"
                    + " rejected[tooBig=" + rejSize + " notOneLevel=" + rejLevel + " tooFewNeighbours="
                    + rejSamples + " notProud=" + rejProud + " notFittable=" + rejFit + " notBetter="
                    + rejBetter + "] (needs > " + F(climb) + "m proud, <= " + F(GatePlateProudMaxMeters)
                    + "m, and a plane that beats the flat plate)");
                return 0;
            }
            // --- 5. APPLY: rotate the plate ON ITS OWN VERTICES. This is deliberately a rigid rotation, not a
            // detach-and-regenerate: a plate that keeps the vertices it shares with its neighbours keeps every
            // edge it had, so the pass changes NO topology at all. That matters because the alternative was
            // measured and it cost islands - cloning the plate's vertices and bridging the seams with
            // connector quads (172 of them on CC_20) turned Birtu from 5 islands into 8 and CC_20 from 15
            // into 16, because a connector is a 0.2-0.5 m sliver that the steep repair or the cleanup can
            // remove, and a plate that was the only route between two parts of a component then splits.
            //
            // What does move is a shared VERTEX: a neighbour that met the plate at a corner comes along by
            // at most the plate's own offset (bounded by one agent climb, and measured per plate below).
            // Deterministic: faces in index order, groups keyed by lowest face index, double accumulators in
            // that order, offsets from a sorted residual list.
            plans.Sort(delegate (PlatePlan a, PlatePlan b)
            {
                return a.Faces[0].CompareTo(b.Faces[0]);
            });
            int verts0 = o.X.Count, blendedFaces = 0;
            var report = new System.Text.StringBuilder();
            int reported = 0;
            for (int pi = 0; pi < plans.Count; pi++)
            {
                PlatePlan p = plans[pi];
                float yLo = float.MaxValue, yHi = float.MinValue;
                for (int ki = 0; ki < p.Verts.Count; ki++)
                {
                    int v = p.Verts[ki];
                    float py = (float)(p.A0 + p.Off + p.GradB * (o.X[v] - p.Cx) + p.GradC * (o.Z[v] - p.Cz));
                    if (py < yLo) yLo = py;
                    if (py > yHi) yHi = py;
                    o.Y[v] = py;
                }
                blendedFaces += p.Faces.Length;
                if (reported < 4 || GatePlateTrace)
                {
                    reported++;
                    report.Append("    plate#" + reported + " bbox=(" + F(p.MinX) + "," + F(p.MinZ) + ")-("
                        + F(p.MaxX) + "," + F(p.MaxZ) + ") h=" + F(p.H) + " tilt=" + F(p.TiltDeg)
                        + "deg (cap " + F(p.TiltCapDeg) + "deg) off=" + F(p.Offset) + "m"
                        + " ring=" + p.Samples + " samples proud=" + F(p.Proud) + "m"
                        + " rms=" + F(p.Rms) + "m worst=" + F(p.WorstBefore) + "->" + F(p.WorstAfter)
                        + "m y=[" + F(yLo) + ".." + F(yHi) + "] faces=" + p.Faces.Length
                        + " verts=" + p.Verts.Count);
                    Log(log, report.ToString());
                    report.Length = 0;
                }
            }
            Log(log, "v2: gate plates: examined=" + candidates + " blended=" + plans.Count
                + " (" + blendedFaces + " faces rotated in place onto " + plans.Count
                + " fitted plane(s); " + (o.X.Count - verts0)
                + " vertices added, no face added or removed, no edge touched)"
                + " rejected[tooBig=" + rejSize + " notOneLevel=" + rejLevel + " tooFewNeighbours="
                + rejSamples + " notProud=" + rejProud + " notFittable=" + rejFit + " notBetter="
                + rejBetter + "] in " + watch.ElapsedMilliseconds + "ms (climb=" + F(climb)
                + "m proudMax=" + F(GatePlateProudMaxMeters) + "m flatDeg=" + F(GatePlateFlatDegrees)
                + " ring=" + F(ring) + "m)");
            return blendedFaces;
        }

        // Harness-only per-candidate explain line (GatePlateTrace): never set in production.
        private static void TracePlate(Action<string> log, string why, float minX, float minZ,
            float maxX, float maxZ, float h, int faces, float area)
        {
            if (!GatePlateTrace || log == null) return;
            log("    gateplate bbox=(" + F(minX) + "," + F(minZ) + ")-(" + F(maxX) + "," + F(maxZ) + ")"
                + " h=" + F(h) + " faces=" + faces + " area=" + F(area) + "m2 " + why);
        }

        // A rejected candidate must not keep its faces masked as "plate", or a later (smaller) group that
        // happens to sit inside it would sample itself as its own neighbour.
        private static void ResetPlate(bool[] isPlate, List<int> faces)
        {
            for (int i = 0; i < faces.Count; i++) isPlate[faces[i]] = false;
        }

        public static float GatePlateMaxRiseMeters(float agentClimbMeters)
        {
            return 2f * agentClimbMeters;
        }

        private static float TriNormalYList(MergeOutput o, int a, int b, int c)
        {
            float ux = o.X[b] - o.X[a], uy = o.Y[b] - o.Y[a], uz = o.Z[b] - o.Z[a];
            float wx = o.X[c] - o.X[a], wy = o.Y[c] - o.Y[a], wz = o.Z[c] - o.Z[a];
            return uz * wx - ux * wz;
        }

        private static long SoupCellKey(int x, int z)
        {
            return ((long)x << 32) | (uint)z;
        }

        // Coarse XZ grid over the input soup; triangles covering too many cells go to bigList.
        private static void BuildSoupGrid(float[] soupVerts, int[] soupTris, int triCount, float cellSize,
            out Dictionary<long, List<int>> grid, out List<int> bigList)
        {
            grid = new Dictionary<long, List<int>>(LongKeyComparer.Instance);
            bigList = new List<int>();
            for (int t = 0; t < triCount; t++)
            {
                int a = soupTris[t * 3], b = soupTris[t * 3 + 1], c = soupTris[t * 3 + 2];
                float minX = Math.Min(soupVerts[a * 3], Math.Min(soupVerts[b * 3], soupVerts[c * 3]));
                float maxX = Math.Max(soupVerts[a * 3], Math.Max(soupVerts[b * 3], soupVerts[c * 3]));
                float minZ = Math.Min(soupVerts[a * 3 + 2], Math.Min(soupVerts[b * 3 + 2], soupVerts[c * 3 + 2]));
                float maxZ = Math.Max(soupVerts[a * 3 + 2], Math.Max(soupVerts[b * 3 + 2], soupVerts[c * 3 + 2]));
                int cx0 = (int)Math.Floor(minX / cellSize), cx1 = (int)Math.Floor(maxX / cellSize);
                int cz0 = (int)Math.Floor(minZ / cellSize), cz1 = (int)Math.Floor(maxZ / cellSize);
                long cells = (long)(cx1 - cx0 + 1) * (cz1 - cz0 + 1);
                if (cells > 256)
                {
                    bigList.Add(t);
                    continue;
                }
                for (int cx = cx0; cx <= cx1; cx++)
                {
                    for (int cz = cz0; cz <= cz1; cz++)
                    {
                        long key = SoupCellKey(cx, cz);
                        List<int> list;
                        if (!grid.TryGetValue(key, out list))
                        {
                            list = new List<int>();
                            grid[key] = list;
                        }
                        list.Add(t);
                    }
                }
            }
        }

        // Walks the stitch line (plus parallel lines across the bridge's own footprint) and refuses when
        // solid geometry spans the envelope OVER it (a wall/door in the gap, or a ceiling within
        // walkableHeight). Testing thin lines rather than a box keeps walls beside the corridor from
        // refusing a valid stitch. The spacing is well under a cell and the offsets cover the width of
        // the stitch quad, so a thin obstacle cannot sit between samples or beside the centre line; each
        // sub-segment is additionally tested for *crossing* the triangle's XZ projection, because a
        // solid's interior is covered by no triangle at all (a box's vertical faces project to a
        // degenerate segment, so a point test inside the solid can never see them).
        private const float StitchProbeSpacingMeters = 0.1f;
        private static readonly float[] StitchProbeOffsets = { -0.2f, -0.1f, 0.1f, 0.2f };

        private static bool GapEnvelopeSolid(float[] soupVerts, int[] soupTris,
            Dictionary<long, List<int>> grid, List<int> bigList, float cellSize,
            float x0, float z0, float x1, float z1, float cs, float yLo, float yHi)
        {
            float dx = x1 - x0, dz = z1 - z0;
            float len = (float)Math.Sqrt((double)dx * dx + (double)dz * dz);
            if (len < 1e-6f)
                return false;
            int steps = Math.Max(2, (int)Math.Ceiling(len / StitchProbeSpacingMeters));
            // Perpendicular unit vector of the gap direction: offsetting along it moves across the
            // stitch quad's own width, which is where a thin obstacle under the bridge would hide.
            float nx = -dz / len, nz = dx / len;
            for (int band = -1; band < StitchProbeOffsets.Length; band++)
            {
                float off = band < 0 ? 0f : StitchProbeOffsets[band];
                float sx = x0 + nx * off, sz = z0 + nz * off;
                float ex = x1 + nx * off, ez = z1 + nz * off;
                for (int k = 0; k < steps; k++)
                {
                    float ax = sx + (ex - sx) * k / steps;
                    float az = sz + (ez - sz) * k / steps;
                    float bx = sx + (ex - sx) * (k + 1) / steps;
                    float bz = sz + (ez - sz) * (k + 1) / steps;
                    // Candidate triangles: the coarse cells this sub-segment (<= 0.1 m) touches.
                    for (int e = 0; e < 3; e++)
                    {
                        float qx = e == 0 ? ax : (e == 1 ? bx : (ax + bx) * 0.5f);
                        float qz = e == 0 ? az : (e == 1 ? bz : (az + bz) * 0.5f);
                        List<int> list;
                        if (!grid.TryGetValue(SoupCellKey((int)Math.Floor(qx / cellSize),
                                (int)Math.Floor(qz / cellSize)), out list))
                            continue;
                        for (int i = 0; i < list.Count; i++)
                            if (TriBlocksSpan(soupVerts, soupTris, list[i], ax, az, bx, bz, yLo, yHi))
                                return true;
                    }
                    for (int i = 0; i < bigList.Count; i++)
                        if (TriBlocksSpan(soupVerts, soupTris, bigList[i], ax, az, bx, bz, yLo, yHi))
                            return true;
                }
            }
            return false;
        }

        // The triangle spans the envelope vertically AND its XZ projection covers the sub-segment (a
        // sample point inside it, or one of its edges crossing the sub-segment).
        private static bool TriBlocksSpan(float[] v, int[] tris, int t,
            float ax, float az, float bx, float bz, float yLo, float yHi)
        {
            if (!TriSpansEnvelope(v, tris, t, yLo, yHi))
                return false;
            if (TriCoversPoint(v, tris, t, ax, az, yLo, yHi))
                return true;
            if (TriCoversPoint(v, tris, t, bx, bz, yLo, yHi))
                return true;
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            if (SegSegCross2(ax, az, bx, bz, v[a * 3], v[a * 3 + 2], v[b * 3], v[b * 3 + 2]))
                return true;
            if (SegSegCross2(ax, az, bx, bz, v[b * 3], v[b * 3 + 2], v[c * 3], v[c * 3 + 2]))
                return true;
            if (SegSegCross2(ax, az, bx, bz, v[c * 3], v[c * 3 + 2], v[a * 3], v[a * 3 + 2]))
                return true;
            return false;
        }

        private static bool TriSpansEnvelope(float[] v, int[] tris, int t, float yLo, float yHi)
        {
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            float minY = Math.Min(v[a * 3 + 1], Math.Min(v[b * 3 + 1], v[c * 3 + 1]));
            float maxY = Math.Max(v[a * 3 + 1], Math.Max(v[b * 3 + 1], v[c * 3 + 1]));
            return maxY >= yLo && minY <= yHi;
        }

        // 2D cross product of (b - a) x (p - a).
        private static double Cross2(float ax, float az, float bx, float bz, float px, float pz)
        {
            return (double)(bx - ax) * (pz - az) - (double)(bz - az) * (px - ax);
        }

        // Segment/segment intersection in XZ. Only a proper (interior) crossing counts; a mere touch or
        // a collinear overlap is left to the inclusive point tests above, which keeps the probe from
        // refusing a stitch over a triangle it only grazes.
        private static bool SegSegCross2(float ax, float az, float bx, float bz,
            float cx, float cz, float dx, float dz)
        {
            double d1 = Cross2(cx, cz, dx, dz, ax, az);
            double d2 = Cross2(cx, cz, dx, dz, bx, bz);
            if ((d1 >= 0.0 && d2 >= 0.0) || (d1 <= 0.0 && d2 <= 0.0))
                return false;
            double d3 = Cross2(ax, az, bx, bz, cx, cz);
            double d4 = Cross2(ax, az, bx, bz, dx, dz);
            if ((d3 >= 0.0 && d4 >= 0.0) || (d3 <= 0.0 && d4 <= 0.0))
                return false;
            return true;
        }

        // Conservative point test: the triangle's Y range spans the envelope and its XZ bbox covers the
        // sample point.
        private static bool TriCoversPoint(float[] v, int[] tris, int t, float px, float pz, float yLo, float yHi)
        {
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            float minY = Math.Min(v[a * 3 + 1], Math.Min(v[b * 3 + 1], v[c * 3 + 1]));
            float maxY = Math.Max(v[a * 3 + 1], Math.Max(v[b * 3 + 1], v[c * 3 + 1]));
            if (maxY < yLo || minY > yHi)
                return false;
            float minX = Math.Min(v[a * 3], Math.Min(v[b * 3], v[c * 3]));
            float maxX = Math.Max(v[a * 3], Math.Max(v[b * 3], v[c * 3]));
            if (px < minX || px > maxX)
                return false;
            float minZ = Math.Min(v[a * 3 + 2], Math.Min(v[b * 3 + 2], v[c * 3 + 2]));
            float maxZ = Math.Max(v[a * 3 + 2], Math.Max(v[b * 3 + 2], v[c * 3 + 2]));
            return pz >= minZ && pz <= maxZ;
        }

        private sealed class MergeOutput
        {
            public readonly List<float> X = new List<float>();
            public readonly List<float> Y = new List<float>();
            public readonly List<float> Z = new List<float>();
            public readonly List<int> Tris = new List<int>();
            public int Degenerate;
            public float MinY = float.MaxValue;
            public float MaxY = float.MinValue;
        }

        // Fan path: exact integer voxel-lattice weld (behaviour unchanged).
        private static MergeOutput MergeFanTiles(VoxelTileResult[] results, int tilesX, int tilesZ)
        {
            var o = new MergeOutput();
            var weld = new Dictionary<VertexKey, int>();
            int mergedVertices = 0;
            for (int ty = 0; ty < tilesZ; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    VoxelTileResult r = results[ty * tilesX + tx];
                    if (r == null || r.Failed || r.VertsXyz == null || r.Tris == null || r.Tris.Length == 0)
                        continue;

                    int vCount = r.VertexCount;
                    var remap = new int[vCount];
                    for (int k = 0; k < vCount; k++)
                    {
                        float x = r.VertsXyz[k * 3 + 0];
                        float y = r.VertsXyz[k * 3 + 1];
                        float z = r.VertsXyz[k * 3 + 2];
                        int qx = r.WeldLattice[k * 3 + 0];
                        int qy = r.WeldLattice[k * 3 + 1];
                        int qz = r.WeldLattice[k * 3 + 2];

                        var key = new VertexKey(qx, qy, qz);
                        int mapped;
                        if (!weld.TryGetValue(key, out mapped))
                        {
                            mapped = mergedVertices;
                            weld[key] = mapped;
                            o.X.Add(x);
                            o.Y.Add(y);
                            o.Z.Add(z);
                            mergedVertices++;
                            if (y < o.MinY) o.MinY = y;
                            if (y > o.MaxY) o.MaxY = y;
                        }
                        remap[k] = mapped;
                    }

                    AppendFaces(o, r, remap);
                }
            }
            return o;
        }

        // Detail path: robust deterministic weld. Vertices are gathered from every tile in sorted
        // tile order, then clustered by a lexicographic sweep: the sorted vertices are visited in
        // order and each is merged into the first existing cluster anchor within DetailWeldTolerance
        // (3D). The first vertex of a cluster (in lexicographic order) is the canonical
        // representative. This is stable regardless of which side of a rounding boundary a float
        // lerp lands on, and it only merges points that are genuinely co-located (so distinct
        // stacked surfaces stay distinct).
        private static MergeOutput MergeDetailTiles(VoxelTileResult[] results, int tilesX, int tilesZ, float tol)
        {
            var o = new MergeOutput();
            int tileCount = results.Length;
            var tileStart = new int[tileCount];
            var gx = new List<float>();
            var gy = new List<float>();
            var gz = new List<float>();

            for (int i = 0; i < tileCount; i++)
            {
                VoxelTileResult r = results[i];
                tileStart[i] = -1;
                if (r == null || r.Failed || r.VertsXyz == null || r.Tris == null || r.Tris.Length == 0)
                    continue;
                tileStart[i] = gx.Count;
                for (int k = 0; k < r.VertexCount; k++)
                {
                    gx.Add(r.VertsXyz[k * 3 + 0]);
                    gy.Add(r.VertsXyz[k * 3 + 1]);
                    gz.Add(r.VertsXyz[k * 3 + 2]);
                }
            }

            int n = gx.Count;
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, new LexicographicVertexComparer(gx, gy, gz));

            float tol2 = tol * tol;
            var clusterOf = new int[n];
            var anchors = new List<int>();
            int windowStart = 0;
            for (int s = 0; s < n; s++)
            {
                int p = order[s];
                float x = gx[p], y = gy[p], z = gz[p];

                // Anchors are created in ascending x order, so drop the ones that can no longer match.
                while (windowStart < anchors.Count && gx[anchors[windowStart]] < x - tol)
                    windowStart++;

                int found = -1;
                for (int a = windowStart; a < anchors.Count; a++)
                {
                    int q = anchors[a];
                    float dx = gx[q] - x, dy = gy[q] - y, dz = gz[q] - z;
                    if (dx * dx + dy * dy + dz * dz <= tol2)
                    {
                        found = a;
                        break;
                    }
                }

                if (found >= 0)
                {
                    clusterOf[p] = found;
                }
                else
                {
                    clusterOf[p] = anchors.Count;
                    anchors.Add(p);
                }
            }

            for (int c = 0; c < anchors.Count; c++)
            {
                int p = anchors[c];
                float y = gy[p];
                o.X.Add(gx[p]);
                o.Y.Add(y);
                o.Z.Add(gz[p]);
                if (y < o.MinY) o.MinY = y;
                if (y > o.MaxY) o.MaxY = y;
            }

            for (int ty = 0; ty < tilesZ; ty++)
            {
                for (int tx = 0; tx < tilesX; tx++)
                {
                    int ti = ty * tilesX + tx;
                    if (tileStart[ti] < 0)
                        continue;
                    VoxelTileResult r = results[ti];
                    int baseIndex = tileStart[ti];
                    int vCount = r.VertexCount;
                    var remap = new int[vCount];
                    for (int k = 0; k < vCount; k++)
                        remap[k] = clusterOf[baseIndex + k];
                    AppendFaces(o, r, remap);
                }
            }
            return o;
        }

        private static void AppendFaces(MergeOutput o, VoxelTileResult r, int[] remap)
        {
            int faceCount = r.FaceCount;
            for (int f = 0; f < faceCount; f++)
            {
                int a = remap[r.Tris[f * 3 + 0]];
                int b = remap[r.Tris[f * 3 + 1]];
                int c = remap[r.Tris[f * 3 + 2]];
                if (a == b || b == c || a == c || Math.Abs(Area2(o.X, o.Y, o.Z, a, b, c)) < 1e-6f)
                {
                    o.Degenerate++;
                    continue;
                }
                o.Tris.Add(a);
                o.Tris.Add(b);
                o.Tris.Add(c);
            }
        }

        // Total order on vertex indices by (x, y, z, index) -- deterministic under Array.Sort.
        private sealed class LexicographicVertexComparer : IComparer<int>
        {
            private readonly List<float> _x;
            private readonly List<float> _y;
            private readonly List<float> _z;

            public LexicographicVertexComparer(List<float> x, List<float> y, List<float> z)
            {
                _x = x;
                _y = y;
                _z = z;
            }

            public int Compare(int a, int b)
            {
                int c = _x[a].CompareTo(_x[b]);
                if (c != 0) return c;
                c = _y[a].CompareTo(_y[b]);
                if (c != 0) return c;
                c = _z[a].CompareTo(_z[b]);
                if (c != 0) return c;
                return a.CompareTo(b);
            }
        }

        private static int SumTileVertices(VoxelTileResult[] results)
        {
            int sum = 0;
            for (int i = 0; i < results.Length; i++)
            {
                VoxelTileResult r = results[i];
                if (r != null && !r.Failed && r.VertsXyz != null)
                    sum += r.VertexCount;
            }
            return sum;
        }

        // Marks the heightfield cells covered by the forced (synthesized ramp) triangles of one tile, in
        // XZ only. A cell is marked when its centre - or any of its four corners - lies inside a forced
        // triangle, which covers sub-cell slivers as well as multi-cell footprints without reimplementing
        // the rasterizer's edge walk. ~O(forced triangles x their cells), per tile.
        // connectorCells (out) flags the cells covered by a CONNECTOR triangle - one that is BOTH forced
        // walkable and ledge-exempt, the pair the collector sets for a ladder's offset symbolic link.
        // connectorTop (out) is the rasterised surface height (world Y) of the connector at each such
        // cell, so the clearance pass can find the connector's own span among a column's layers.
        private static bool[] MarkSteppedCells(RcHeightfield hf, float[] vertsXyz, int[] subTris,
            List<int> tileList, bool[] forceWalkableTriangles, bool[] ledgeExemptTriangles, float cellSize,
            out bool[] connectorCells, out float[] connectorTop, out int connectorMarked,
            out bool[] forcedCells)
        {
            int w = hf.width, h = hf.height;
            var marked = new bool[w * h];
            var conn = new bool[w * h];
            var fcell = new bool[w * h];
            var ctop = new float[w * h];
            for (int ci = 0; ci < ctop.Length; ci++) ctop[ci] = float.MinValue;
            connectorCells = conn;
            connectorTop = ctop;
            forcedCells = fcell;
            connectorMarked = 0;
            float minX = hf.bmin.X, minZ = hf.bmin.Z;
            float half = 0.5f * cellSize;
            for (int k = 0; k < tileList.Count; k++)
            {
                int src = tileList[k];
                bool forced = forceWalkableTriangles != null && src >= 0
                    && src < forceWalkableTriangles.Length && forceWalkableTriangles[src];
                bool exempt = ledgeExemptTriangles != null && src >= 0
                    && src < ledgeExemptTriangles.Length && ledgeExemptTriangles[src];
                if (!forced && !exempt) continue;
                bool isConnector = forced && exempt;
                int ia = subTris[k * 3 + 0], ib = subTris[k * 3 + 1], ic = subTris[k * 3 + 2];
                float ax = vertsXyz[ia * 3], ay = vertsXyz[ia * 3 + 1], az = vertsXyz[ia * 3 + 2];
                float bx = vertsXyz[ib * 3], by = vertsXyz[ib * 3 + 1], bz = vertsXyz[ib * 3 + 2];
                float cx = vertsXyz[ic * 3], cy = vertsXyz[ic * 3 + 1], cz = vertsXyz[ic * 3 + 2];
                float tMinX = Math.Min(ax, Math.Min(bx, cx)), tMaxX = Math.Max(ax, Math.Max(bx, cx));
                float tMinZ = Math.Min(az, Math.Min(bz, cz)), tMaxZ = Math.Max(az, Math.Max(bz, cz));
                int x0 = CellIndex(tMinX, minX, cellSize, w);
                int x1 = CellIndex(tMaxX, minX, cellSize, w);
                int z0 = CellIndex(tMinZ, minZ, cellSize, h);
                int z1 = CellIndex(tMaxZ, minZ, cellSize, h);
                for (int z = z0; z <= z1; z++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int ci = z * w + x;
                        if (marked[ci] && !isConnector) continue;
                        float px = minX + (x + 0.5f) * cellSize;
                        float pz = minZ + (z + 0.5f) * cellSize;
                        bool hit = PointInTriXZ(px, pz, ax, az, bx, bz, cx, cz)
                            || PointInTriXZ(px - half, pz - half, ax, az, bx, bz, cx, cz)
                            || PointInTriXZ(px + half, pz - half, ax, az, bx, bz, cx, cz)
                            || PointInTriXZ(px - half, pz + half, ax, az, bx, bz, cx, cz)
                            || PointInTriXZ(px + half, pz + half, ax, az, bx, bz, cx, cz);
                        if (!hit) continue;
                        // A FORCED-walkable triangle (a synthesized ramp or an offset connector) makes this
                        // cell one that is ALLOWED to carry a surface steeper than the walkable cap. Recorded
                        // separately from `marked` because that mask drives the ledge-trim exemption and is
                        // only consulted when that filter runs, while this provenance is needed by the
                        // final-mesh steep-face repair whatever the ledge filter does.
                        if (forced) fcell[ci] = true;
                        if (!marked[ci])
                        {
                            marked[ci] = true;
                            if (isConnector) conn[ci] = true;
                        }
                        else if (isConnector && !conn[ci])
                        {
                            conn[ci] = true;
                        }
                        if (isConnector)
                        {
                            // The connector surface height at the cell: the interpolated triangle height
                            // at whichever probe points land inside, taking the highest (the rasterised
                            // span's top). Keeps the clearance pass from mistaking the deck above for it.
                            float h0 = TriHeightAtXZ(px, pz, ax, ay, az, bx, by, bz, cx, cy, cz);
                            float h1 = TriHeightAtXZ(px - half, pz - half, ax, ay, az, bx, by, bz, cx, cy, cz);
                            float h2 = TriHeightAtXZ(px + half, pz - half, ax, ay, az, bx, by, bz, cx, cy, cz);
                            float h3 = TriHeightAtXZ(px - half, pz + half, ax, ay, az, bx, by, bz, cx, cy, cz);
                            float h4 = TriHeightAtXZ(px + half, pz + half, ax, ay, az, bx, by, bz, cx, cy, cz);
                            float hm = Math.Max(h0, Math.Max(h1, Math.Max(h2, Math.Max(h3, h4))));
                            if (!float.IsNaN(hm) && hm > ctop[ci]) ctop[ci] = hm;
                        }
                    }
                }
            }
            for (int ci = 0; ci < conn.Length; ci++)
                if (conn[ci]) connectorMarked++;
            return marked;
        }

        // Barycentric height of a triangle at an XZ point (NaN when the XZ projection is degenerate).
        // The caller only uses this for points it already knows are inside the triangle.
        private static float TriHeightAtXZ(float px, float pz, float ax, float ay, float az,
            float bx, float by, float bz, float cx, float cy, float cz)
        {
            float den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (den > -1e-12f && den < 1e-12f) return float.NaN;
            float inv = 1f / den;
            float wa = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) * inv;
            float wb = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) * inv;
            float wc = 1f - wa - wb;
            return wa * ay + wb * by + wc * cy;
        }

        // Restores only the remembered stepped-prop spans that the ledge trim nulled, and only where doing
        // so is sound - the exemption is gated PER SPAN, not per cell:
        //  - the span's own clearance above must survive walkableHeight (the same rule
        //    FilterWalkableLowHeightSpans applies one step later), so a tread is never restored into a slot
        //    that is already a ceiling;
        //  - the span must end up connected - through walkable neighbours within walkableClimb in adjacent
        //    columns (restored or not) - to a column that was walkable before this pass, or to the tile's
        //    border ring, whose neighbours belong to another tile. A patch that would only connect to
        //    itself is left nulled rather than becoming an orphan island.
        // Cost is one pass over the remembered spans plus a 4-neighbour flood over the tile's columns.
        private static void RestoreSteppedSpans(RcHeightfield hf, List<RcSpan>[] remembered,
            bool[] connectorCells, int walkableHeight, int walkableClimb, List<int> restoredCells,
            out int restored, out int droppedClearance, out int droppedOrphan, out int droppedPlane,
            out int aboveNear, out int aboveFar, out int clearedCeilings)
        {
            restored = 0;
            droppedClearance = 0;
            droppedOrphan = 0;
            droppedPlane = 0;
            aboveNear = 0;
            aboveFar = 0;
            clearedCeilings = 0;
            int w = hf.width, h = hf.height;

            // pass 1: clearance gate, and tentatively restore the survivors
            var restoredSpans = new HashSet<RcSpan>();
            for (int ci = 0; ci < remembered.Length; ci++)
            {
                List<RcSpan> list = remembered[ci];
                if (list == null) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    RcSpan sp = list[i];
                    if (sp.area != RcRecast.RC_NULL_AREA) continue;   // never nulled: nothing to do
                    bool isConnectorCell = connectorCells != null && ci < connectorCells.Length
                        && connectorCells[ci];
                    if (!isConnectorCell && sp.next != null && sp.next.smin - sp.smax < walkableHeight)
                    {
                        droppedClearance++;                          // no headroom: leave it nulled
                        continue;
                    }
                    sp.area = RcRecast.RC_WALKABLE_AREA;
                    restoredSpans.Add(sp);
                    restored++;
                    if (restoredCells != null) restoredCells.Add(ci);   // provenance for the prune pass

                    // Suppress the merge plane: the rasterizer merges a thin flight's boards into one
                    // coarse span per column, and where that artefact sits within walkableClimb above the
                    // span just restored it IS the same boards. Leaving it walkable puts two levels in the
                    // column; the region builder joins them and the contour interpolates between them, so
                    // the surface sags (measured ~0.2 m on two wooden flights). Nulling its area is not
                    // enough - FilterWalkableLowHeightSpans reads span.next.smin regardless of area and
                    // would null the restored span right back - so the artefact span is unlinked as well.
                    // Only spans of THIS stepped prop (i.e. remembered for this cell) are touched, so a
                    // neighbouring prop's platform is never removed.
                    //
                    // A CONNECTOR cell goes further: the symbolic link must survive even when a real span
                    // (the ladder's plank line, a deck slab, the hatch jamb) sits within walkableHeight
                    // above it - that is the whole point of the clearance exemption - so any remembered
                    // walkable span that is a low ceiling over it is unlinked too.
                    RcSpan prev = sp;
                    for (RcSpan up = sp.next; up != null; )
                    {
                        RcSpan next = up.next;
                        if (up.area != RcRecast.RC_NULL_AREA && up.smax - sp.smax <= walkableClimb)
                        {
                            aboveNear++;
                            if (Contains(list, up))
                            {
                                up.area = RcRecast.RC_NULL_AREA;
                                prev.next = next;            // unlink: it must not stay the ceiling
                                droppedPlane++;
                                up = next;
                                continue;
                            }
                        }
                        else if (up.area != RcRecast.RC_NULL_AREA)
                        {
                            aboveFar++;
                        }
                        if (isConnectorCell && up.area != RcRecast.RC_NULL_AREA
                            && up.smin - sp.smax < walkableHeight && Contains(list, up))
                        {
                            up.area = RcRecast.RC_NULL_AREA;
                            prev.next = next;                // unlink the low ceiling over the link
                            clearedCeilings++;
                            up = next;
                            continue;
                        }
                        prev = up;
                        up = next;
                    }
                }
            }
            if (restored == 0) return;

            // pass 2: flood the columns that were already walkable (plus the tile border, which is
            // another tile's business), stepping between columns whose walkable spans are within climb
            var connected = new bool[w * h];
            var queue = new Queue<int>();
            for (int ci = 0; ci < w * h; ci++)
            {
                for (RcSpan sp = hf.spans[ci]; sp != null; sp = sp.next)
                {
                    if (sp.area == RcRecast.RC_NULL_AREA) continue;
                    if (restoredSpans.Contains(sp)) continue;        // not a seed: restored this pass
                    connected[ci] = true;
                    queue.Enqueue(ci);
                    break;
                }
            }
            for (int x = 0; x < w; x++)
            {
                EnqueueColumn(connected, queue, x, 0, w, h);
                EnqueueColumn(connected, queue, x, h - 1, w, h);
            }
            for (int z = 0; z < h; z++)
            {
                EnqueueColumn(connected, queue, 0, z, w, h);
                EnqueueColumn(connected, queue, w - 1, z, w, h);
            }
            while (queue.Count > 0)
            {
                int ci = queue.Dequeue();
                int x = ci % w, z = ci / w;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (nx < 0 || nz < 0 || nx >= w || nz >= h) continue;
                    int nci = nz * w + nx;
                    if (connected[nci]) continue;
                    if (!ColumnsLink(hf, ci, nci, walkableClimb)) continue;
                    connected[nci] = true;
                    queue.Enqueue(nci);
                }
            }

            // pass 3: drop restored spans that ended up without a way back to the existing network
            for (int ci = 0; ci < remembered.Length; ci++)
            {
                List<RcSpan> list = remembered[ci];
                if (list == null || connected[ci]) continue;
                for (int i = 0; i < list.Count; i++)
                {
                    RcSpan sp = list[i];
                    if (!restoredSpans.Contains(sp)) continue;
                    sp.area = RcRecast.RC_NULL_AREA;
                    restored--;
                    droppedOrphan++;
                }
            }
        }

        private static bool Contains(List<RcSpan> list, RcSpan sp)
        {
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], sp)) return true;
            return false;
        }

        // The connector's own span in one column. Recast rasterises a SLOPED quad as per-column spans
        // whose smax is the cell's lower triangle edge, so the span can sit up to a slope-run BELOW the
        // triangle's interpolated surface height at the cell centre. Matching the nearest smax to that
        // surface height is therefore wrong: it picks the real ceiling above (e.g. the deck slab's
        // underside) instead of the connector's own span. The connector's span is instead the highest
        // walkable span that is still at or below the surface height (plus the climb tolerance), i.e. the
        // span the connector surface is standing on. Returns null when no such span exists.
        private static RcSpan ConnectorSpanAt(RcHeightfield hf, int ci, float top, int walkableClimb,
            float cellHeight)
        {
            if (top == float.MinValue) return null;
            // Span smin/smax are voxel indices RELATIVE to the heightfield's bmin.Y.
            int target = cellHeight > 0f ? (int)Math.Round((top - hf.bmin.Y) / cellHeight) : 0;
            int limit = target + walkableClimb;
            RcSpan conn = null;
            int best = int.MinValue;
            for (RcSpan sp = hf.spans[ci]; sp != null; sp = sp.next)
            {
                if (sp.area == RcRecast.RC_NULL_AREA) continue;
                if (sp.smax > limit) continue;              // this span is the ceiling, not the connector
                if (sp.smax > best) { best = sp.smax; conn = sp; }
            }
            return conn;
        }

        // Clears low ceilings above the connector's own span in each connector cell, so the symbol link
        // survives FilterWalkableLowHeightSpans. Only spans within walkableHeight above the connector are
        // unlinked; returns how many were.
        private static int ClearConnectorCeilings(RcHeightfield hf, bool[] connectorCells,
            float[] connectorTop, int walkableHeight, int walkableClimb, float cellHeight)
        {
            int cleared = 0;
            for (int ci = 0; ci < connectorCells.Length; ci++)
            {
                if (!connectorCells[ci]) continue;
                float top = connectorTop != null && ci < connectorTop.Length ? connectorTop[ci]
                    : float.MinValue;
                RcSpan conn = ConnectorSpanAt(hf, ci, top, walkableClimb, cellHeight);
                if (conn == null) continue;
                RcSpan prev = conn;
                for (RcSpan up = conn.next; up != null; )
                {
                    RcSpan next = up.next;
                    // Unlink a low ceiling EVEN IF ITS AREA IS ALREADY NULL (the ledge trim nulls it
                    // first): FilterWalkableLowHeightSpans reads span.next.smin regardless of area, so a
                    // linked-but-nulled span still kills the connector's span below it. This was the
                    // measured Test 26 (e) 1.0 m wall break.
                    if (up.smin - conn.smax < walkableHeight)
                    {
                        if (up.area != RcRecast.RC_NULL_AREA)
                        {
                            up.area = RcRecast.RC_NULL_AREA;
                            cleared++;
                        }
                        prev.next = next;         // unlink: it must not stay the ceiling
                        up = next;
                        continue;
                    }
                    prev = up;
                    up = next;
                }
            }
            return cleared;
        }

        // Restores the walkable area of the connector's OWN compact spans after ErodeWalkableArea nulled
        // them. A symbolic link is not a surface agents stand on, so the erosion inset that keeps ordinary
        // geometry off ledges must not eat it away (measured: the middle of a 62 deg connector eroded to
        // area=0 with ~2 neighbour connections while ordinary cells kept many). ErodeWalkableArea only
        // writes compactHeightfield.areas[spanIndex] - it never unlinks span neighbours or changes span
        // geometry (verified in RcAreas.cs) - so restoring the area alone is enough to bring the span
        // back. Scoped connector-only: stepped-prop treads and ordinary geometry keep erosion exactly as
        // before. Returns how many spans were restored.
        private static int RestoreConnectorErosion(RcCompactHeightfield chf, bool[] connectorCells,
            float[] connectorTop, int walkableClimb, float cellHeight)
        {
            int restored = 0;
            int w = chf.width, h = chf.height;
            for (int ci = 0; ci < connectorCells.Length && ci < w * h; ci++)
            {
                if (!connectorCells[ci]) continue;
                float top = connectorTop != null && ci < connectorTop.Length ? connectorTop[ci]
                    : float.MinValue;
                if (top == float.MinValue) continue;
                // A compact span's walkable surface is its BOTTOM (y); the connector surface funds the
                // span's own floor, so match the nearest y (not y+h). See ConnectorSpanAt for why the
                // surface-to-span mapping is not a simple smax match.
                int target = cellHeight > 0f ? (int)Math.Round((top - chf.bmin.Y) / cellHeight) : 0;
                RcCompactCell cell = chf.cells[ci];
                int bestSpan = -1;
                int best = int.MaxValue;
                for (int i = cell.index; i < cell.index + cell.count; i++)
                {
                    int y = chf.spans[i].y;
                    int d = Math.Abs(y - target);
                    if (d < best) { best = d; bestSpan = i; }
                }
                if (bestSpan < 0 || best > walkableClimb) continue;
                if (chf.areas[bestSpan] != RcRecast.RC_NULL_AREA) continue;   // erosion did not kill it
                chf.areas[bestSpan] = RcRecast.RC_WALKABLE_AREA;
                restored++;
            }
            return restored;
        }

        private static void EnqueueColumn(bool[] connected, Queue<int> queue, int x, int z, int w, int h)
        {
            if (x < 0 || z < 0 || x >= w || z >= h) return;
            int ci = z * w + x;
            if (connected[ci]) return;
            connected[ci] = true;
            queue.Enqueue(ci);
        }

        // Two columns are linked when they hold walkable spans whose walkable floors are within climb -
        // the same test BuildCompactHeightfield uses to join neighbours.
        private static bool ColumnsLink(RcHeightfield hf, int a, int b, int walkableClimb)
        {
            for (RcSpan sa = hf.spans[a]; sa != null; sa = sa.next)
            {
                if (sa.area == RcRecast.RC_NULL_AREA) continue;
                for (RcSpan sb = hf.spans[b]; sb != null; sb = sb.next)
                {
                    if (sb.area == RcRecast.RC_NULL_AREA) continue;
                    if (Math.Abs(sa.smax - sb.smax) <= walkableClimb) return true;
                }
            }
            return false;
        }

        private static int CellIndex(float p, float min, float cellSize, int count)
        {
            int i = cellSize > 0f ? (int)Math.Floor((p - min) / cellSize) : 0;
            if (i < 0) i = 0;
            if (i >= count) i = count - 1;
            return i;
        }

        // Inside-or-on-edge test for a point against a triangle projected to XZ.
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

        private static VoxelTileResult BuildTile(int index, int tx, int ty,
            float[] vertsXyz, int[] tris, List<int> tileList, TiledNavMeshSettings s,
            float originX, float originZ, float tileSpan, int cellsPerTile, float bminY, float bmaxY,
            int width, int height, int border, float pad,
            int walkableHeight, int walkableClimb, int walkableRadius, float maxSimplificationError,
            int minRegionArea, int mergeRegionArea, RcAreaModification areaMod,
            bool[] forceWalkableTriangles, bool[] ledgeExemptTriangles)
        {
            var r = new VoxelTileResult();
            r.Index = index;
            r.TileX = tx;
            r.TileY = ty;

            var tileWatch = Stopwatch.StartNew();
            try
            {
                float tileMinX = originX + tx * tileSpan;
                float tileMinZ = originZ + ty * tileSpan;
                float hfMinX = tileMinX - pad;
                float hfMinZ = tileMinZ - pad;
                float hfMaxX = hfMinX + width * s.VoxelCellSizeMeters;
                float hfMaxZ = hfMinZ + height * s.VoxelCellSizeMeters;

                int assigned = tileList != null ? tileList.Count : 0;
                r.RasterizedTris = assigned;
                if (assigned == 0)
                {
                    r.VertsXyz = new float[0];
                    r.Tris = new int[0];
                    r.WeldLattice = new int[0];
                    tileWatch.Stop();
                    r.ElapsedMs = tileWatch.ElapsedMilliseconds;
                    return r;
                }

                var ctx = new RcContext();
                var hf = new RcHeightfield(width, height,
                    new RcVec3f(hfMinX, bminY, hfMinZ),
                    new RcVec3f(hfMaxX, bmaxY, hfMaxZ),
                    s.VoxelCellSizeMeters, s.CellHeightMeters, border);

                // Sub-triangle index array for this tile.
                int[] subTris = new int[assigned * 3];
                for (int k = 0; k < assigned; k++)
                {
                    int src = tileList[k] * 3;
                    subTris[k * 3 + 0] = tris[src + 0];
                    subTris[k * 3 + 1] = tris[src + 1];
                    subTris[k * 3 + 2] = tris[src + 2];
                }

                int[] areaIds = RcRecast.MarkWalkableTriangles(ctx, s.MaxSlopeDegrees, vertsXyz, subTris,
                    assigned, areaMod);
                int forcedInTile = 0, exemptInTile = 0, connectorForTile = 0;
                if (forceWalkableTriangles != null)
                {
                    // Synthesized ramps are walkable by design even when steeper than MaxSlopeDegrees
                    // (a ladder or a stair is meant to be traversed). Everything else is untouched.
                    for (int k = 0; k < assigned; k++)
                    {
                        int src = tileList[k];
                        if (src >= 0 && src < forceWalkableTriangles.Length && forceWalkableTriangles[src])
                        {
                            areaIds[k] = RcRecast.RC_WALKABLE_AREA;
                            forcedInTile++;
                        }
                    }
                }
                if (ledgeExemptTriangles != null)
                {
                    // A stepped prop's own triangles keep their ordinary slope decision (so a vertical
                    // face is still not walkable) but are counted for the ledge-trim exemption below.
                    for (int k = 0; k < assigned; k++)
                    {
                        int src = tileList[k];
                        if (src >= 0 && src < ledgeExemptTriangles.Length && ledgeExemptTriangles[src])
                            exemptInTile++;
                    }
                }
                if (forceWalkableTriangles != null && ledgeExemptTriangles != null)
                {
                    // A ladder's offset symbolic connector is the one triangle class flagged in BOTH
                    // masks (forced walkable AND ledge-exempt); the pair singles it out for the clearance
                    // exemption below. Ordinary ramp / tread triangles are only ever in one mask.
                    for (int k = 0; k < assigned; k++)
                    {
                        int src = tileList[k];
                        if (src >= 0 && src < forceWalkableTriangles.Length && src < ledgeExemptTriangles.Length
                            && forceWalkableTriangles[src] && ledgeExemptTriangles[src])
                            connectorForTile++;
                    }
                }
                RcRasterizations.RasterizeTriangles(ctx, vertsXyz, subTris, areaIds, assigned, hf, walkableClimb);

                RcFilters.FilterLowHangingWalkableObstacles(ctx, walkableClimb, hf);

                // Stepped-prop spans are exempt from the ledge trim (NOT from the clearance rules below):
                // FilterLedgeSpans deletes any span steeper than atan(walkableClimb / (2*cs)) by nulling
                // its area, and it runs after the walkable-area marking, so the only way to keep a ramp -
                // or the tread of a 51-56 deg flight, which the trim also kills - is to remember which
                // spans those triangles' cells held as walkable just before that filter and restore
                // exactly those afterwards. Identifying them by area id is impossible: the filter
                // overwrites the id it was given.
                List<RcSpan>[] steppedSpans = null;
                bool[] connectorCells = null;
                float[] connectorTop = null;
                if (forcedInTile > 0 || exemptInTile > 0)
                {
                    bool[] steppedCells;
                    bool[] forcedCells;
                    int connectorMarked;
                    steppedCells = MarkSteppedCells(hf, vertsXyz, subTris, tileList,
                        forceWalkableTriangles, ledgeExemptTriangles, s.VoxelCellSizeMeters,
                        out connectorCells, out connectorTop, out connectorMarked, out forcedCells);
                    // Forced-cell provenance for the final-mesh steep-face repair (see ForcedRampCellXz).
                    // Recorded whatever the ledge filter does, so the repair never removes a synthesized
                    // ramp face just because the trim was switched off.
                    int forcedMarked = 0;
                    var forcedXz = new List<float>();
                    for (int ci = 0; ci < forcedCells.Length; ci++)
                    {
                        if (!forcedCells[ci]) continue;
                        forcedMarked++;
                        int cxi = ci % hf.width, czi = ci / hf.width;
                        forcedXz.Add(hf.bmin.X + (cxi + 0.5f) * s.VoxelCellSizeMeters);
                        forcedXz.Add(hf.bmin.Z + (czi + 0.5f) * s.VoxelCellSizeMeters);
                    }
                    if (forcedMarked > 0) r.ForcedRampCellXz = forcedXz;
                    if (s.UseLedgeFilter)
                    {
                        r.ConnectorForcedInTile = connectorForTile;
                        r.ConnectorMarkedCells = connectorMarked;
                        int marked = 0;
                        for (int ci = 0; ci < steppedCells.Length; ci++)
                            if (steppedCells[ci]) marked++;
                        if (marked > 0)
                        {
                            steppedSpans = new List<RcSpan>[steppedCells.Length];
                            for (int ci = 0; ci < steppedCells.Length; ci++)
                            {
                                if (!steppedCells[ci]) continue;
                                for (RcSpan sp = hf.spans[ci]; sp != null; sp = sp.next)
                                {
                                    if (sp.area == RcRecast.RC_NULL_AREA) continue;
                                    if (steppedSpans[ci] == null) steppedSpans[ci] = new List<RcSpan>();
                                    steppedSpans[ci].Add(sp);
                                }
                            }
                            r.SteppedMarkedCells = marked;
                        }
                    }
                }
                if (s.GapFillCells > 0)
                {
                    // Must run before the ledge filter: FilterLedgeSpans reads an in-bounds EMPTY
                    // column as a drop and deletes the floor cell beside a gap, which would widen a
                    // hairline seam by one cell per side and defeat the fill.
                    int gapFilled, gapEmpty;
                    gapFilled = FillNarrowGaps(hf, s.GapFillCells, walkableClimb, border, cellsPerTile,
                        out gapEmpty);
                    r.GapFilled = gapFilled;
                    r.GapEmptyColumns = gapEmpty;
                }
                if (s.UseLedgeFilter)
                {
                    // Vertical reach of "geometry ABOVE a surface". Audited end to end: every rule that
                    // lets a span influence the span under it is bounded by walkableHeight (9 voxels =
                    // 1.8 m at cs=0.25/ch=0.2) or tighter. Nothing reads the column above at unbounded
                    // distance, so a surface and geometry 21 m over it cannot interact. Per rule:
                    //   clearance (FilterWalkableLowHeightSpans, below) - reads span.next.smin only:
                    //       1.8 m.
                    //   this ledge trim - own column reads span.next.smin (1.8 m); a neighbour span is
                    //       admitted only when min(ceiling,nCeiling) - max(floor,nFloor) >= walkableHeight,
                    //       i.e. it must overlap our own clearance band (1.8 m). A span arbitrarily far
                    //       above can therefore only ever contribute a POSITIVE floor difference, and
                    //       lowestNeighborFloorDifference is a min() that is only ever driven negative by a
                    //       span BELOW us, so an overhang cannot null the floor under it.
                    //   stepped restore + connector clearance exemption - 1.8 m (see RestoreSteppedSpans /
                    //       ClearConnectorCeilings).
                    //   per-tile stitch clearance - minY-1 .. maxY+walkableHeight (1.8 m).
                    //   merge-stitch clearance - soupFloor+0.6*ch .. soupFloor+walkableHeight*ch (1.8 m).
                    //   gap fill - flanks within GapFillCells (2 cells) and |dy| <= walkableClimb (0.6 m).
                    //   low-hanging-obstacle filter - walkableClimb (0.6 m).
                    //   erosion - horizontal (walkableRadius) only.
                    RcFilters.FilterLedgeSpans(ctx, walkableHeight, walkableClimb, hf);
                }
                if (steppedSpans != null)
                {
                    // Restore per SPAN, not per cell: ordinary geometry in the same tile keeps the exact
                    // current cap, and a remembered span comes back only when doing so is sound.
                    int restored, droppedClearance, droppedOrphan, droppedPlane, aboveNear, aboveFar;
                    int clearedCeilings;
                    var restoredCells = new List<int>();
                    RestoreSteppedSpans(hf, steppedSpans, connectorCells, walkableHeight, walkableClimb,
                        restoredCells, out restored, out droppedClearance, out droppedOrphan,
                        out droppedPlane, out aboveNear, out aboveFar, out clearedCeilings);
                    r.SteppedRestoredSpans = restored;
                    r.SteppedDroppedClearance = droppedClearance;
                    r.SteppedDroppedOrphan = droppedOrphan;
                    r.SteppedDroppedPlane = droppedPlane;
                    r.SteppedAboveNear = aboveNear;
                    r.SteppedAboveFar = aboveFar;
                    r.ConnectorClearedCeilings = clearedCeilings;
                    if (restoredCells.Count > 0)
                    {
                        // World XZ of the restored cells: the prune pass uses them as per-face provenance
                        // for "this face only exists because of the exemption".
                        var cellXz = new List<float>(restoredCells.Count * 2);
                        for (int i = 0; i < restoredCells.Count; i++)
                        {
                            int cx = restoredCells[i] % width;
                            int cz = restoredCells[i] / width;
                            cellXz.Add(hf.bmin.X + (cx + 0.5f) * s.VoxelCellSizeMeters);
                            cellXz.Add(hf.bmin.Z + (cz + 0.5f) * s.VoxelCellSizeMeters);
                        }
                        r.SteppedRestoredCellXz = cellXz;
                    }
                }
                if (connectorCells != null)
                {
                    // A ladder's symbolic connector must not die under the ladder's own plank / a deck
                    // slab / the hatch jamb, and it must not turn the floor beneath it into a low ceiling:
                    // unlink any walkable span within walkableHeight above the connector's OWN span in
                    // its columns, BEFORE FilterWalkableLowHeightSpans runs. This is the clearance
                    // exemption; it is scoped to the connector's cells and to the connector's own span.
                    r.ConnectorClearedCeilings += ClearConnectorCeilings(hf, connectorCells, connectorTop,
                        walkableHeight, walkableClimb, s.CellHeightMeters);
                }
                // Clearance still applies to ramp surfaces: a ramp must not become a low ceiling.
                RcFilters.FilterWalkableLowHeightSpans(ctx, walkableHeight, hf);

                RcCompactHeightfield chf = RcCompacts.BuildCompactHeightfield(ctx, walkableHeight, walkableClimb, hf);
                RcAreas.ErodeWalkableArea(ctx, walkableRadius, chf);
                if (connectorCells != null)
                {
                    // Erosion must not eat the symbolic connector: a link is not a surface agents stand
                    // on, so the ledge keep-out inset does not apply to it. Area-only restore.
                    r.ConnectorErosionExempt = RestoreConnectorErosion(chf, connectorCells, connectorTop,
                        walkableClimb, s.CellHeightMeters);
                }
                RcRegions.BuildRegionsMonotone(ctx, chf, minRegionArea, mergeRegionArea);

                RcContourSet cset = RcContours.BuildContours(ctx, chf, maxSimplificationError,
                    s.MaxEdgeLenVoxels, RcBuildContoursFlags.RC_CONTOUR_TESS_WALL_EDGES);
                RcPolyMesh pmesh = RcMeshs.BuildPolyMesh(ctx, cset, s.VertsPerPoly);
                r.PolyCount = pmesh.npolys;

                if (r.PolyCount == 0)
                {
                    r.VertsXyz = new float[0];
                    r.Tris = new int[0];
                    r.WeldLattice = new int[0];
                    tileWatch.Stop();
                    r.ElapsedMs = tileWatch.ElapsedMilliseconds;
                    return r;
                }

                if (s.UseDetailMesh)
                    BuildDetailOutput(r, pmesh, chf, ctx, s);
                else
                    BuildFanOutput(r, pmesh, tx, ty, cellsPerTile, tileSpan, originX, originZ, bminY, s);

                if (s.StitchMaxGapCells > 0 && r.FaceCount > 1)
                    r.Stitched = StitchTileComponents(r, hf, bminY, s.CellHeightMeters, s.HeightOffsetMeters,
                        walkableHeight, walkableClimb, s.StitchMaxGapCells, s.DetailWeldToleranceMeters);
            }
            catch (Exception ex)
            {
                r.Failed = true;
                r.Error = ex.GetType().Name + ": " + ex.Message;
            }

            tileWatch.Stop();
            r.ElapsedMs = tileWatch.ElapsedMilliseconds;
            return r;
        }

        // Gap fill. Runs right after the low-obstacle filter and BEFORE the ledge filter: FilterLedgeSpans
        // treats an in-bounds EMPTY column as a drop and deletes the floor cell beside a gap, which would
        // widen a hairline seam by one cell per side and defeat this pass entirely.
        //
        // A column with no walkable span is filled when walkable spans flank it on OPPOSITE sides along
        // one axis (N/S, E/W or the two diagonal axes) with an empty run of at most gapFillCells cells
        // and a floor difference within walkableClimb; the filled span takes the LOWER neighbour floor.
        //
        // Tile consistency: only cells inside THIS tile's own area are filled and only columns inside
        // this tile's padded heightfield are probed, so exactly one tile decides each world cell and the
        // fill is identical on both sides of a tile boundary (no new cracks).
        private static int FillNarrowGaps(RcHeightfield hf, int gapFillCells, int walkableClimb,
            int border, int cellsPerTile, out int emptyColumns)
        {
            emptyColumns = 0;
            int w = hf.width;
            int h = hf.height;
            int ownX0 = border;
            int ownZ0 = border;
            int ownX1 = border + cellsPerTile;
            int ownZ1 = border + cellsPerTile;
            if (ownX1 > w) ownX1 = w;
            if (ownZ1 > h) ownZ1 = h;
            if (ownX0 >= ownX1 || ownZ0 >= ownZ1)
                return 0;

            // Floor (voxel) of the first walkable span per column, -1 when the column has none. Taken
            // once so a column's flanks are only ever ORIGINAL walkable columns (no cascade).
            var floor = new int[w * h];
            for (int ci = 0; ci < w * h; ci++)
                floor[ci] = WalkableFloor(hf.spans[ci]);

            int filled = 0;
            for (int z = ownZ0; z < ownZ1; z++)
            {
                for (int x = ownX0; x < ownX1; x++)
                {
                    int ci = x + z * w;
                    if (floor[ci] >= 0)
                        continue;                       // column already walkable
                    emptyColumns++;
                    int y;
                    if (!TryGapFloor(floor, w, h, x, z, gapFillCells, walkableClimb, out y))
                        continue;
                    if (!CanPlaceSpan(hf.spans[ci], y))
                        continue;                       // something solid already occupies that height
                    InsertSpanSorted(hf, ci, y);
                    filled++;
                }
            }
            return filled;
        }

        private static int WalkableFloor(RcSpan span)
        {
            for (RcSpan s = span; s != null; s = s.next)
                if (s.area != RcRecast.RC_NULL_AREA)
                    return s.smax;
            return -1;
        }

        // True when a one-voxel solid span [y-1, y] fits without overlapping existing geometry.
        private static bool CanPlaceSpan(RcSpan span, int y)
        {
            for (RcSpan s = span; s != null; s = s.next)
                if (!(s.smax <= y - 1 || s.smin >= y))
                    return false;
            return true;
        }

        // Keeps the column sorted bottom-to-top (Recast expects spans in ascending smin order).
        private static void InsertSpanSorted(RcHeightfield hf, int columnIndex, int y)
        {
            var s = new RcSpan();
            s.smin = y - 1;
            s.smax = y;
            s.area = RcRecast.RC_WALKABLE_AREA;
            RcSpan cur = hf.spans[columnIndex];
            if (cur == null || s.smax <= cur.smin)
            {
                s.next = cur;
                hf.spans[columnIndex] = s;
                return;
            }
            while (cur.next != null && cur.next.smin < s.smin)
                cur = cur.next;
            s.next = cur.next;
            cur.next = s;
        }

        // The four axis pairs: E/W, N/S, NE/SW, NW/SE. A diagonal pair is still one axis (its own
        // diagonal line), so a fill is always justified along a single axis.
        private static readonly int[] GapAxisX = { 1, 0, 1, 1 };
        private static readonly int[] GapAxisZ = { 0, 1, 1, -1 };

        private static bool TryGapFloor(int[] floor, int w, int h, int x, int z,
            int maxRun, int walkableClimb, out int floorY)
        {
            floorY = 0;
            int bestRun = int.MaxValue;
            bool found = false;
            for (int axis = 0; axis < 4; axis++)
            {
                int dx = GapAxisX[axis];
                int dz = GapAxisZ[axis];
                int y0, d0, y1, d1;
                if (!ScanFlank(floor, w, h, x, z, dx, dz, maxRun, out y0, out d0))
                    continue;
                if (!ScanFlank(floor, w, h, x, z, -dx, -dz, maxRun, out y1, out d1))
                    continue;
                int run = d0 + d1 - 1;                 // empty cells between the two flanks
                if (run > maxRun)
                    continue;
                if (Math.Abs(y0 - y1) > walkableClimb)
                    continue;
                if (run < bestRun)
                {
                    bestRun = run;
                    floorY = Math.Min(y0, y1);
                    found = true;
                }
            }
            return found;
        }

        private static bool ScanFlank(int[] floor, int w, int h, int x, int z,
            int dx, int dz, int maxRun, out int spanY, out int dist)
        {
            spanY = 0;
            dist = 0;
            for (int d = 1; d <= maxRun; d++)
            {
                int nx = x + dx * d;
                int nz = z + dz * d;
                if (nx < 0 || nz < 0 || nx >= w || nz >= h)
                    return false;
                int ci = nx + nz * w;
                if (floor[ci] >= 0)
                {
                    spanY = floor[ci];
                    dist = d;
                    return true;
                }
            }
            return false;
        }
        // Per-tile stitch. Reconnects components of the tile's own mesh whose nearest boundary edges are
        // within stitchMaxGapCells cells and within walkableClimb vertically, by adding a small quad that
        // REUSES existing boundary vertices (so both weld paths stay exact). It is refused when the
        // tile's solid spans put geometry inside the gap envelope (a wall or closed door) or leave too
        // little headroom (a ceiling). Deterministic, and per-tile only (cross-tile continuity is
        // already exact on the lattice).
        private static int StitchTileComponents(VoxelTileResult r, RcHeightfield hf, float bminY, float ch,
            float offset, int walkableHeight, int walkableClimb, int stitchMaxGapCells, float weldTol)
        {
            int nv = r.VertexCount;
            int nf = r.FaceCount;
            if (nv < 3 || nf < 2)
                return 0;
            float cs = hf.cs;
            float maxGap = stitchMaxGapCells * cs;
            float maxGap2 = maxGap * maxGap;

            int[] weld = BuildPositionWeld(r.VertsXyz, nv, weldTol);

            // Face adjacency + edge multiplicity over welded ids.
            int[] fp = new int[nf];
            for (int i = 0; i < nf; i++) fp[i] = i;
            var edgeMult = new Dictionary<long, int>(LongKeyComparer.Instance);
            for (int f = 0; f < nf; f++)
            {
                int a = weld[r.Tris[f * 3]], b = weld[r.Tris[f * 3 + 1]], c = weld[r.Tris[f * 3 + 2]];
                StitchAddEdge(edgeMult, fp, f, a, b);
                StitchAddEdge(edgeMult, fp, f, b, c);
                StitchAddEdge(edgeMult, fp, f, c, a);
            }
            // Early out: a single component (the common case) needs no stitching.
            bool multi = false;
            for (int i = 0; i < nf; i++) if (StitchFind(fp, i) != StitchFind(fp, 0)) { multi = true; break; }
            if (!multi)
                return 0;

            var compFaces = new Dictionary<int, int>();
            var compList = new List<int>();
            for (int i = 0; i < nf; i++)
            {
                int root = StitchFind(fp, i);
                if (!compFaces.ContainsKey(root)) { compFaces[root] = 0; compList.Add(root); }
                compFaces[root]++;
            }
            compList.Sort();
            int nc = compList.Count;
            if (nc < 2)
                return 0;

            // Boundary edges per component (welded edge multiplicity 1), with their original vertices.
            var compIndex = new Dictionary<int, int>();
            for (int i = 0; i < nc; i++) compIndex[compList[i]] = i;
            var bA = new List<int>[nc];
            var bB = new List<int>[nc];
            var bMin = new List<float>[nc];
            var bMax = new List<float>[nc];
            for (int i = 0; i < nc; i++)
            {
                bA[i] = new List<int>();
                bB[i] = new List<int>();
                bMin[i] = new List<float>();
                bMax[i] = new List<float>();
            }
            for (int f = 0; f < nf; f++)
            {
                int ci = compIndex[StitchFind(fp, f)];
                for (int k = 0; k < 3; k++)
                {
                    int i0 = r.Tris[f * 3 + k];
                    int i1 = r.Tris[f * 3 + ((k + 1) % 3)];
                    int w0 = weld[i0], w1 = weld[i1];
                    if (edgeMult[StitchEdgeKey(w0, w1)] != 1)
                        continue;                       // interior edge
                    // One entry per welded boundary edge (the face loop may see it twice for the two
                    // faces that share it; multiplicity 1 means it belongs to exactly one face).
                    bA[ci].Add(i0);
                    bB[ci].Add(i1);
                    float x0 = r.VertsXyz[i0 * 3], y0 = r.VertsXyz[i0 * 3 + 1], z0 = r.VertsXyz[i0 * 3 + 2];
                    float x1 = r.VertsXyz[i1 * 3], y1 = r.VertsXyz[i1 * 3 + 1], z1 = r.VertsXyz[i1 * 3 + 2];
                    bMin[ci].Add(Math.Min(x0, x1)); bMax[ci].Add(Math.Max(x0, x1));
                    bMin[ci].Add(Math.Min(z0, z1)); bMax[ci].Add(Math.Max(z0, z1));
                }
            }

            int[] compParent = new int[nc];
            for (int i = 0; i < nc; i++) compParent[i] = i;

            var tris = new List<int>(r.Tris);
            int added = 0;
            for (int i = 0; i < nc; i++)
            {
                int ei = bA[i].Count;
                if (ei == 0 || ei > 4096)
                    continue;
                for (int j = i + 1; j < nc; j++)
                {
                    int ej = bA[j].Count;
                    if (ej == 0 || ej > 4096)
                        continue;
                    if (StitchFind(compParent, i) == StitchFind(compParent, j))
                        continue;
                    // Coarse reject on the boundary AABB (XZ), in cell units.
                    float aminX = float.MaxValue, amaxX = float.MinValue, aminZ = float.MaxValue, amaxZ = float.MinValue;
                    for (int k = 0; k < ei; k++)
                    {
                        if (bMin[i][k * 2] < aminX) aminX = bMin[i][k * 2];
                        if (bMax[i][k * 2] > amaxX) amaxX = bMax[i][k * 2];
                        if (bMin[i][k * 2 + 1] < aminZ) aminZ = bMin[i][k * 2 + 1];
                        if (bMax[i][k * 2 + 1] > amaxZ) amaxZ = bMax[i][k * 2 + 1];
                    }
                    float bminX = float.MaxValue, bmaxX = float.MinValue, bminZ = float.MaxValue, bmaxZ = float.MinValue;
                    for (int k = 0; k < ej; k++)
                    {
                        if (bMin[j][k * 2] < bminX) bminX = bMin[j][k * 2];
                        if (bMax[j][k * 2] > bmaxX) bmaxX = bMax[j][k * 2];
                        if (bMin[j][k * 2 + 1] < bminZ) bminZ = bMin[j][k * 2 + 1];
                        if (bMax[j][k * 2 + 1] > bmaxZ) bmaxZ = bMax[j][k * 2 + 1];
                    }
                    double ddx = Math.Max(0.0, Math.Max(aminX - bmaxX, bminX - amaxX));
                    double ddz = Math.Max(0.0, Math.Max(aminZ - bmaxZ, bminZ - amaxZ));
                    if (ddx * ddx + ddz * ddz > maxGap2)
                        continue;

                    // Nearest boundary-edge pair.
                    int bestIA = -1, bestIB = -1;
                    double best = double.MaxValue;
                    for (int ka = 0; ka < ei; ka++)
                    {
                        int a0 = bA[i][ka], a1 = bB[i][ka];
                        for (int kb = 0; kb < ej; kb++)
                        {
                            int c0 = bA[j][kb], c1 = bB[j][kb];
                            double d = EdgePairDist2(r.VertsXyz, a0, a1, c0, c1);
                            if (d < best) { best = d; bestIA = ka; bestIB = kb; }
                        }
                    }
                    if (bestIA < 0 || best > maxGap2)
                        continue;

                    int ua = bA[i][bestIA], ub = bB[i][bestIA];
                    int va = bA[j][bestIB], vb = bB[j][bestIB];
                    if (ua == va || ua == vb || ub == va || ub == vb)
                        continue;                       // components share a vertex: skip
                    float dyAB = Math.Abs(r.VertsXyz[ua * 3 + 1] - r.VertsXyz[va * 3 + 1]);
                    if (dyAB > walkableClimb * ch + 1e-4f)
                        continue;

                    int yLo = (int)Math.Floor((Math.Min(r.VertsXyz[ua * 3 + 1], r.VertsXyz[va * 3 + 1]) - offset - bminY) / ch) - 1;
                    int yHi = (int)Math.Ceiling((Math.Max(r.VertsXyz[ua * 3 + 1], r.VertsXyz[va * 3 + 1]) - offset - bminY) / ch) + walkableHeight;
                    if (!StitchGapClear(r, hf, ua, va, yLo, yHi))
                        continue;

                    // Quad (two triangles) reusing the four boundary vertices. Keep the new edges fresh
                    // so no edge ends up shared by more than two faces, and wind with +Y normals.
                    int pmu, pmv;
                    edgeMult.TryGetValue(StitchEdgeKey(weld[ua], weld[ub]), out pmu);
                    edgeMult.TryGetValue(StitchEdgeKey(weld[va], weld[vb]), out pmv);
                    if (pmu != 1 || pmv != 1)
                        continue;   // a previous stitch already closed one side
                    if (edgeMult.ContainsKey(StitchEdgeKey(weld[ua], weld[va]))
                        || edgeMult.ContainsKey(StitchEdgeKey(weld[ub], weld[vb]))
                        || edgeMult.ContainsKey(StitchEdgeKey(weld[ua], weld[vb])))
                        continue;

                    int t1a = ua, t1b = va, t1c = vb;
                    if (TriNormalY(r.VertsXyz, t1a, t1b, t1c) < 0f) { t1b = vb; t1c = va; }
                    int t2a = ua, t2b = vb, t2c = ub;
                    if (TriNormalY(r.VertsXyz, t2a, t2b, t2c) < 0f) { t2b = ub; t2c = vb; }
                    if (t1a == t1b || t1b == t1c || t1a == t1c || t2a == t2b || t2b == t2c || t2a == t2c)
                        continue;

                    tris.Add(t1a); tris.Add(t1b); tris.Add(t1c);
                    tris.Add(t2a); tris.Add(t2b); tris.Add(t2c);
                    StitchRegister(edgeMult, weld[ua], weld[va]);
                    StitchRegister(edgeMult, weld[ub], weld[vb]);
                    StitchRegister(edgeMult, weld[ua], weld[vb]);
                    StitchRegister(edgeMult, weld[ua], weld[ub]);
                    StitchRegister(edgeMult, weld[va], weld[vb]);
                    added += 2;
                    StitchUnion(compParent, i, j);
                }
            }

            if (added == 0)
                return 0;
            r.Tris = tris.ToArray();
            r.FaceCount = r.Tris.Length / 3;
            return added;
        }

        private static void StitchAddEdge(Dictionary<long, int> mult, int[] fp, int face, int a, int b)
        {
            long key = StitchEdgeKey(a, b);
            int c;
            mult.TryGetValue(key, out c);
            mult[key] = c + 1;
            long tagged = key | FaceTagBit;
            if (c == 0)
                mult[tagged] = face;
            else
                StitchUnion(fp, mult[tagged], face);
        }

        private static long StitchEdgeKey(int a, int b)
        {
            return a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        }

        private const long FaceTagBit = long.MinValue;

        private static int StitchFind(int[] p, int x)
        {
            while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
            return x;
        }

        private static void StitchUnion(int[] p, int a, int b)
        {
            int ra = StitchFind(p, a), rb = StitchFind(p, b);
            if (ra != rb) p[ra] = rb;
        }

        private static double EdgePairDist2(float[] v, int a0, int a1, int b0, int b1)
        {
            double d = VDist2(v, a0, b0);
            double t = VDist2(v, a0, b1);
            if (t < d) d = t;
            t = VDist2(v, a1, b0);
            if (t < d) d = t;
            t = VDist2(v, a1, b1);
            if (t < d) d = t;
            return d;
        }

        private static double VDist2(float[] v, int i, int j)
        {
            double dx = v[i * 3] - v[j * 3];
            double dy = v[i * 3 + 1] - v[j * 3 + 1];
            double dz = v[i * 3 + 2] - v[j * 3 + 2];
            return dx * dx + dy * dy + dz * dz;
        }

        private static float TriNormalY(float[] v, int a, int b, int c)
        {
            float ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            float wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
            return uz * wx - ux * wz;
        }

        // A new edge may only be added when it does not already exist (any multiplicity).
        private static void StitchRegister(Dictionary<long, int> mult, int a, int b)
        {
            long key = StitchEdgeKey(a, b);
            int c;
            mult.TryGetValue(key, out c);
            mult[key] = c + 1;
        }

        // Clearance: sample the heightfield cells strictly between the two boundary vertices and refuse
        // when a solid span overlaps the gap envelope [yLo, yHi] (wall / closed door / low ceiling).
        private static bool StitchGapClear(VoxelTileResult r, RcHeightfield hf, int ia, int ib, int yLo, int yHi)
        {
            float cs = hf.cs;
            int cx0 = (int)Math.Floor((r.VertsXyz[ia * 3] - hf.bmin.X) / cs);
            int cz0 = (int)Math.Floor((r.VertsXyz[ia * 3 + 2] - hf.bmin.Z) / cs);
            int cx1 = (int)Math.Floor((r.VertsXyz[ib * 3] - hf.bmin.X) / cs);
            int cz1 = (int)Math.Floor((r.VertsXyz[ib * 3 + 2] - hf.bmin.Z) / cs);
            int steps = Math.Max(Math.Abs(cx1 - cx0), Math.Abs(cz1 - cz0));
            if (steps <= 1)
                return true;
            for (int i = 1; i < steps; i++)
            {
                int cx = cx0 + (int)Math.Round((double)(cx1 - cx0) * i / steps);
                int cz = cz0 + (int)Math.Round((double)(cz1 - cz0) * i / steps);
                if (cx < 0 || cz < 0 || cx >= hf.width || cz >= hf.height)
                    continue;
                for (RcSpan s = hf.spans[cx + cz * hf.width]; s != null; s = s.next)
                    if (s.smax > yLo && s.smin < yHi)
                        return false;
            }
            return true;
        }

        private static long Hash3(long x, long y, long z)
        {
            return (x * 73856093L) ^ (y * 19349663L) ^ (z * 83492791L);
        }

        // Position-based vertex weld (exact for lattice coordinates, tolerance-based for detail meshes).
        private static int[] BuildPositionWeld(float[] verts, int nv, float tol)
        {
            var ids = new int[nv];
            var grid = new Dictionary<long, List<int>>(LongKeyComparer.Instance);
            var cx = new List<float>();
            var cy = new List<float>();
            var cz = new List<float>();
            float inv = 1f / tol;
            float tol2 = tol * tol;
            int next = 0;
            for (int i = 0; i < nv; i++)
            {
                float x = verts[i * 3], y = verts[i * 3 + 1], z = verts[i * 3 + 2];
                long gx = (long)Math.Floor(x * inv);
                long gy = (long)Math.Floor(y * inv);
                long gz = (long)Math.Floor(z * inv);
                int found = -1;
                for (long dx = -1; dx <= 1 && found < 0; dx++)
                {
                    for (long dy = -1; dy <= 1 && found < 0; dy++)
                    {
                        for (long dz = -1; dz <= 1 && found < 0; dz++)
                        {
                            List<int> list;
                            if (!grid.TryGetValue(Hash3(gx + dx, gy + dy, gz + dz), out list))
                                continue;
                            for (int k = 0; k < list.Count; k++)
                            {
                                int id = list[k];
                                float ddx = x - cx[id], ddy = y - cy[id], ddz = z - cz[id];
                                if (ddx * ddx + ddy * ddy + ddz * ddz <= tol2) { found = id; break; }
                            }
                        }
                    }
                }
                if (found < 0)
                {
                    found = next++;
                    cx.Add(x); cy.Add(y); cz.Add(z);
                    long key = Hash3(gx, gy, gz);
                    List<int> list;
                    if (!grid.TryGetValue(key, out list)) { list = new List<int>(); grid[key] = list; }
                    list.Add(found);
                }
                ids[i] = found;
            }
            return ids;
        }

        // Fan-triangulate each convex Recast polygon. Vertices are placed on the
        // global integer voxel lattice so adjacent tiles emit bit-identical
        // coordinates at shared boundaries (exact welding, no cracks).
        private static void BuildFanOutput(VoxelTileResult r, RcPolyMesh pmesh,
            int tx, int ty, int cellsPerTile, float tileSpan, float originX, float originZ,
            float bminY, TiledNavMeshSettings s)
        {
            int nvp = pmesh.nvp;
            int maxVerts = pmesh.nverts;
            var xs = new List<float>();
            var ys = new List<float>();
            var zs = new List<float>();
            var lat = new List<int>();
            var t = new List<int>();
            var localToOutput = new int[maxVerts];
            for (int i = 0; i < maxVerts; i++) localToOutput[i] = -1;

            float cs = s.VoxelCellSizeMeters;
            float ch = s.CellHeightMeters;
            float offset = s.HeightOffsetMeters;

            for (int poly = 0; poly < pmesh.npolys; poly++)
            {
                int p = poly * nvp * 2;
                int nv = 0;
                while (nv < nvp && pmesh.polys[p + nv] != RcRecast.RC_MESH_NULL_IDX) nv++;
                if (nv < 3) continue;

                int fan0 = -1;
                for (int k = 1; k < nv - 1; k++)
                {
                    int lv0 = pmesh.polys[p + 0];
                    int lv1 = pmesh.polys[p + k];
                    int lv2 = pmesh.polys[p + k + 1];
                    fan0 = EmitVertex(pmesh, lv0, tx, ty, cellsPerTile, originX, originZ, bminY, cs, ch, offset,
                        localToOutput, xs, ys, zs, lat);
                    int o1 = EmitVertex(pmesh, lv1, tx, ty, cellsPerTile, originX, originZ, bminY, cs, ch, offset,
                        localToOutput, xs, ys, zs, lat);
                    int o2 = EmitVertex(pmesh, lv2, tx, ty, cellsPerTile, originX, originZ, bminY, cs, ch, offset,
                        localToOutput, xs, ys, zs, lat);
                    t.Add(fan0); t.Add(o1); t.Add(o2);
                }
            }

            r.VertexCount = xs.Count;
            r.FaceCount = t.Count / 3;
            r.VertsXyz = new float[xs.Count * 3];
            r.WeldLattice = new int[xs.Count * 3];
            for (int i = 0; i < xs.Count; i++)
            {
                r.VertsXyz[i * 3 + 0] = xs[i];
                r.VertsXyz[i * 3 + 1] = ys[i];
                r.VertsXyz[i * 3 + 2] = zs[i];
                r.WeldLattice[i * 3 + 0] = lat[i * 3 + 0];
                r.WeldLattice[i * 3 + 1] = lat[i * 3 + 1];
                r.WeldLattice[i * 3 + 2] = lat[i * 3 + 2];
            }
            r.Tris = t.ToArray();
        }

        private static int EmitVertex(RcPolyMesh pmesh, int localVertex, int tx, int ty, int cellsPerTile,
            float originX, float originZ, float bminY, float cs, float ch, float offset,
            int[] localToOutput, List<float> xs, List<float> ys, List<float> zs, List<int> lat)
        {
            int existing = localToOutput[localVertex];
            if (existing >= 0)
                return existing;

            int lx = pmesh.verts[localVertex * 3 + 0];
            int ly = pmesh.verts[localVertex * 3 + 1];
            int lz = pmesh.verts[localVertex * 3 + 2];

            int gx = tx * cellsPerTile + lx;   // global voxel-lattice indices (exact)
            int gy = ly;                        // bminY is global, so ly is already global
            int gz = ty * cellsPerTile + lz;

            float x = originX + gx * cs;
            float y = bminY + gy * ch + offset;
            float z = originZ + gz * cs;

            int outIndex = xs.Count;
            xs.Add(x); ys.Add(y); zs.Add(z);
            lat.Add(gx); lat.Add(gy); lat.Add(gz);
            localToOutput[localVertex] = outIndex;
            return outIndex;
        }

        // Optional detail mesh: Recast emits world-space (Y-up) detail triangles, including the
        // upstream +chf.ch nudge. WeldLattice is left null; MergeDetailTiles clusters by position.
        private static void BuildDetailOutput(VoxelTileResult r, RcPolyMesh pmesh, RcCompactHeightfield chf,
            RcContext ctx, TiledNavMeshSettings s)
        {
            float sampleDist = EffectiveDetailSampleDist(s);
            RcPolyMeshDetail dmesh = RcMeshDetails.BuildPolyMeshDetail(ctx, pmesh, chf, sampleDist, s.MaxErrorMeters);
            if (CaptureDetailDebug && DebugDetailMesh == null)
                DebugDetailMesh = dmesh;
            if (dmesh == null || dmesh.nverts == 0 || dmesh.ntris == 0)
            {
                r.VertsXyz = new float[0];
                r.Tris = new int[0];
                r.WeldLattice = new int[0];
                return;
            }

            r.VertsXyz = new float[dmesh.nverts * 3];
            for (int i = 0; i < dmesh.nverts; i++)
            {
                r.VertsXyz[i * 3 + 0] = dmesh.verts[i * 3 + 0];
                r.VertsXyz[i * 3 + 1] = dmesh.verts[i * 3 + 1] + s.HeightOffsetMeters;
                r.VertsXyz[i * 3 + 2] = dmesh.verts[i * 3 + 2];
            }
            r.VertexCount = dmesh.nverts;
            r.WeldLattice = null;

            // ROOT-CAUSE FIX: RcMeshDetails.BuildPolyMeshDetail stores each submesh's triangles with
            // indices LOCAL to that submesh's vertex range (dmesh.meshes[sm] = (vertBase, vertCount,
            // triBase, triCount)); dmesh.tris values are 0..#vertCount-1. They must be offset by
            // vertBase to index the global dmesh.verts array. Treating them as global (the previous
            // bug) made every submesh reference submesh 0's vertices -> mass duplicate / non-manifold
            // faces (E < F) and a broken mesh the engine could not import.
            var outTris = new List<int>(dmesh.ntris * 3);
            for (int sm = 0; sm < dmesh.nmeshes; sm++)
            {
                int vertBase = dmesh.meshes[sm * 4 + 0];
                int vertCount = dmesh.meshes[sm * 4 + 1];
                int triBase = dmesh.meshes[sm * 4 + 2];
                int triCount = dmesh.meshes[sm * 4 + 3];
                if (vertCount == 0 || triCount == 0)
                    continue;

                for (int k = 0; k < triCount; k++)
                {
                    int t = (triBase + k) * 4;
                    outTris.Add(dmesh.tris[t + 0] + vertBase);
                    outTris.Add(dmesh.tris[t + 1] + vertBase);
                    outTris.Add(dmesh.tris[t + 2] + vertBase);
                }
            }
            r.Tris = outTris.ToArray();
            r.FaceCount = r.Tris.Length / 3;
        }

        // Twice the signed area of the triangle projected on the XZ plane
        // (Recast up-axis is Y, so we test the horizontal footprint).
        private static float Area2(List<float> x, List<float> y, List<float> z, int a, int b, int c)
        {
            float abx = x[b] - x[a];
            float abz = z[b] - z[a];
            float acx = x[c] - x[a];
            float acz = z[c] - z[a];
            return abx * acz - abz * acx;
        }

        private struct VertexKey : IEquatable<VertexKey>
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;

            public VertexKey(int x, int y, int z)
            {
                X = x;
                Y = y;
                Z = z;
            }

            public bool Equals(VertexKey other)
            {
                return X == other.X && Y == other.Y && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is VertexKey && Equals((VertexKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + X;
                    h = h * 31 + Y;
                    h = h * 31 + Z;
                    return h;
                }
            }
        }

        // Deterministic non-manifold repair: while any edge is shared by > 2 faces, remove the
        // highest-index face on that edge (and re-evaluate). Removes as few faces as possible and is
        // order-independent (edges processed in sorted key order). Returns the number removed.
        private static int RepairNonManifold(MergeOutput o)
        {
            int faceCount = o.Tris.Count / 3;
            if (faceCount == 0)
                return 0;

            var count = new Dictionary<long, int>(LongKeyComparer.Instance);
            var perEdge = new Dictionary<long, List<int>>(LongKeyComparer.Instance);
            for (int i = 0; i < faceCount; i++)
            {
                int a = o.Tris[i * 3], b = o.Tris[i * 3 + 1], c = o.Tris[i * 3 + 2];
                AddFaceEdge(count, perEdge, i, a, b);
                AddFaceEdge(count, perEdge, i, b, c);
                AddFaceEdge(count, perEdge, i, c, a);
            }

            var removed = new bool[faceCount];
            int removedCount = 0;
            bool changed = true;
            while (changed)
            {
                changed = false;
                var bad = new List<long>();
                foreach (KeyValuePair<long, int> kv in count)
                    if (kv.Value > 2) bad.Add(kv.Key);
                if (bad.Count == 0) break;
                bad.Sort();

                for (int e = 0; e < bad.Count; e++)
                {
                    List<int> list = perEdge[bad[e]];
                    int active = 0;
                    for (int i = 0; i < list.Count; i++) if (!removed[list[i]]) active++;
                    if (active <= 2) continue;

                    list.Sort();
                    for (int i = list.Count - 1; i >= 0 && active > 2; i--)
                    {
                        int ff = list[i];
                        if (removed[ff]) continue;
                        removed[ff] = true;
                        removedCount++;
                        active--;
                        changed = true;
                        int a = o.Tris[ff * 3], b = o.Tris[ff * 3 + 1], c = o.Tris[ff * 3 + 2];
                        DecEdge(count, a, b);
                        DecEdge(count, b, c);
                        DecEdge(count, c, a);
                    }
                }
            }

            if (removedCount > 0)
            {
                var keep = new List<int>(o.Tris.Count);
                for (int i = 0; i < faceCount; i++)
                {
                    if (removed[i]) continue;
                    keep.Add(o.Tris[i * 3]);
                    keep.Add(o.Tris[i * 3 + 1]);
                    keep.Add(o.Tris[i * 3 + 2]);
                }
                o.Tris.Clear();
                o.Tris.AddRange(keep);
            }
            return removedCount;
        }

        // ------------------------------------------------------------------
        // V3.46 REPAIRS: the malformed-face classes the user reported, fixed where they are born.
        // ------------------------------------------------------------------
        // A navmesh face is a 2D polygon standing on the ground: it has a FOOTPRINT (its signed XZ area
        // in walk order) and that footprint must face up. Two things break that, and both were measured
        // on CC_20 at the coordinates the user reported:
        //   * a face with no footprint (exactly vertical) or a negative one (inside out). The writer
        //     always writes the per-face inverted flag 0, so such a face reaches the engine as a normal
        //     face and the editor draws it turned inside out - the inverted navmesh face the user saw
        //     next to european_castle_ladder at (1120.500, 117.367, 1077.000), where the prop cluster
        //     stairs_bo + bd_wood_heap_b + european_castle_ladder folds under itself in a 2 x 3 m spot.
        //   * two faces of the same walkable surface laid across each other - the navmesh overlapping
        //     and crisscrossing with itself, like an X.
        // Neither is born in the sliver cleanup: measured stage by stage, both classes are already in its
        // INPUT. They are dropped here, next to the non-manifold repair, so everything downstream (stitch,
        // prune, cleanup, writer, gate) sees faces that are all real walkable surfaces and the export gate
        // can ASSERT that instead of merely reporting it.
        // Runs BOTH face-quality repairs as one compaction and remaps the recorded quad pairs through it.
        // A pair whose face was dropped goes with it (the writer must never emit a quad over a hole), so the
        // quad count moves by a handful of faces at most - measured on CC_20: 1 dropped quad for 2 dropped
        // faces. quadPairs may be null (cleanup off, or nothing merged).
        private static int[] RepairFaceQuality(MergeOutput o, int[] quadPairs, Action<string> log)
        {
            var repairWatch = System.Diagnostics.Stopwatch.StartNew();
            int faceCount0 = o.Tris.Count / 3;
            var drop = new bool[faceCount0];
            var overlapDrop = new bool[faceCount0];
            string facingsLog;
            int badFacings = MarkBadFacings(o, drop, out facingsLog);
            string overlapsLog;
            int overlaps = MarkSelfOverlaps(o, drop, overlapDrop, out overlapsLog);
            int reverted = KeepConnectivitySafe(o, drop, overlapDrop);
            overlaps -= reverted;
            if (badFacings == 0 && overlaps == 0)
                return quadPairs;   // nothing dropped: the mesh and its quad pairs are untouched

            // Old face index -> new face index for the faces that survive (the compaction keeps their order).
            var map = new int[faceCount0];
            int kept = 0;
            for (int i = 0; i < faceCount0; i++) map[i] = drop[i] ? -1 : kept++;
            CompactFaces(o, drop);

            int keptQuads = 0, lostQuads = 0;
            if (quadPairs != null && quadPairs.Length >= 2)
            {
                for (int k = 0; k + 1 < quadPairs.Length; k += 2)
                {
                    int f0 = quadPairs[k], f1 = quadPairs[k + 1];
                    // A pair that cannot be carried over must be MARKED (-1), not merely skipped: the trim
                    // below recognises a dead pair by quadPairs[k] < 0. Leaving the original face index in
                    // place makes the trim copy it as if it were live and overrun the trimmed array.
                    // V3.46 shipped with that hole - it never fired because until now no face drop had ever
                    // cost a quad pair.
                    if (f0 < 0 || f1 < 0 || f0 >= faceCount0 || f1 >= faceCount0)
                    {
                        if (f0 >= 0 && f1 >= 0) lostQuads++;
                        quadPairs[k] = -1;
                        continue;
                    }
                    int n0 = map[f0], n1 = map[f1];
                    if (n0 < 0 || n1 < 0 || n0 == n1)
                    {
                        lostQuads++;
                        quadPairs[k] = -1;
                        continue;
                    }
                    quadPairs[k] = n0;
                    quadPairs[k + 1] = n1;
                    keptQuads++;
                }
                if (keptQuads * 2 < quadPairs.Length)
                {
                    var trimmed = new int[keptQuads * 2];
                    int w = 0;
                    for (int k = 0; k + 1 < quadPairs.Length; k += 2)
                    {
                        if (quadPairs[k] < 0) continue;
                        if (w + 1 >= trimmed.Length) break;   // belt and braces: never overrun
                        trimmed[w++] = quadPairs[k];
                        trimmed[w++] = quadPairs[k + 1];
                    }
                    quadPairs = trimmed;
                }
            }

            string msg = badFacings + " bad facing(s) (" + facingsLog + "), same-level overlaps dropped "
                + overlaps + " (" + overlapsLog
                + (reverted > 0 ? ", " + reverted + " reverted to keep the island count" : "")
                + "), quads kept " + keptQuads + "/" + (keptQuads + lostQuads) + " in " + repairWatch.ElapsedMilliseconds + "ms";
            Log(log, "v2: repaired mesh: " + msg);
            return quadPairs;
        }

        // Marks (does not yet remove) every face whose ground footprint is not a real walkable polygon:
        // negative = it looks DOWN (the inverted navmesh face the user saw), zero = it is a wall with no
        // footprint at all. The navmesh writer always writes the per-face inverted flag 0, so nothing
        // downstream would ever catch these.
        private static int MarkBadFacings(MergeOutput o, bool[] drop, out string log)
        {
            int faceCount = o.Tris.Count / 3;
            int removedCount = 0, inverted = 0, vertical = 0;
            float firstX = 0f, firstY = 0f, firstZ = 0f;
            for (int i = 0; i < faceCount; i++)
            {
                int a = o.Tris[i * 3], b = o.Tris[i * 3 + 1], c = o.Tris[i * 3 + 2];
                double ny = FootprintM2(o.X, o.Y, o.Z, a, b, c);
                if (ny > NavMeshCleanup.MinGroundFootprintM2) continue;
                drop[i] = true;
                removedCount++;
                if (ny < 0) inverted++; else vertical++;
                if (removedCount == 1)
                {
                    firstX = (o.X[a] + o.X[b] + o.X[c]) / 3f;
                    firstY = (o.Y[a] + o.Y[b] + o.Y[c]) / 3f;
                    firstZ = (o.Z[a] + o.Z[b] + o.Z[c]) / 3f;
                }
            }
            log = inverted + " inverted + " + vertical + " vertical, first at ("
                + F(firstX) + "," + F(firstY) + "," + F(firstZ) + ")";
            return removedCount;
        }

        // A face steeper than the walkable cap AND taller than an agent can step across is not a surface.
        // The engine draws it, the navmesh links it, and a bot handed it paths onto it and falls off - the
        // "vertical navmesh jump" the user reported on the CC_20 staircase ("The part of the navmesh on the
        // stairs that's causing a problem seems to all originate from sharing a point ... it is that one
        // single point that's causing the issue"), and the screenshot's "wall".
        //
        // MEASURED at that spot: a fan of EIGHT faces at 61.1-87.4 deg with footprints 0.14-2.19 m2, 3D areas
        // 0.66-6.86 m2 and OWN HEIGHT RANGES 3.2-8.4 m, ALL EIGHT sharing the single vertex
        // (1120.500, 115.700, 1078.500). That vertex is the contour vertex of the voxel column whose walkable
        // span top is the plate shared by `bd_wood_heap_b` (top face at engine H=114.724 -> ch-quantised
        // 114.7 -> +0.5 lift = 115.2, exactly the hub's pre-lift height) and `stairs_bo`'s lowest tread
        // (H=114.716). The flagged twin `stairs_bo` (0x800 = HasSteps) and the unflagged
        // `european_castle_ladder` (0x00000000) occupy the SAME column, so the region's contour wraps the
        // vertical stack and the fan leaps from the foot straight to treads 3-8 m up.
        //
        // It is a Recast CONTOUR SPIKE, born before any of our passes: it survives --detail off (so it is not
        // the detail mesh), --cleanup off (so it is not the sliver cleanup), and dropping
        // RC_CONTOUR_TESS_WALL_EDGES (so it is not wall-edge tessellation - that experiment also cost 5
        // islands and was rejected). V3.46's footprint gate cannot see it: a steep-but-not-exactly-vertical
        // face has a strictly POSITIVE footprint, so inverted=0 and vertical=0 while the jump ships.
        //
        // WHY BOTH TESTS, and not slope alone. Slope alone is NOT the discriminator - measured, on the
        // suite's own connectivity lab, the single face that connects "c) staircase 1.0 m wide, 6 x 0.4 m"
        // from its ground to its top platform is at 71.5 deg, and a pure 60 deg cut removes exactly that
        // face and breaks the scenario. Its OWN HEIGHT RANGE is 1.20 m, against 3.2-8.4 m for the fan.
        // That is the physical difference: an agent moves between navmesh faces only where they are
        // within walkableClimb of each other, so a face taller than about two climbs has no point from
        // which it can be entered or left, and the engine's agent movement cannot use it however the
        // navmesh links it. A short steep face (a kerb, a single riser, the lab's stair) is left alone.
        //
        // WHY THE RESTORE IS MINIMAL (this is the difference from the reverted V3.47). V3.47 used the same
        // two tests but dropped UNCONDITIONALLY, arguing "an unwalkable face is not a route". That fixes
        // the staircase (measured: steep >55 deg faces 11 -> 4, steep area 39.108 -> 3.450 m2, worst
        // 87.39 -> 69.35 deg, foot 113.90 and top 124.30 still one component) but it cost CC_20 two islands
        // (15 -> 13) and was reverted: one high-altitude island (centroid y 479-702 m) really is only linked
        // by a face over the cap. The island count is a hard acceptance criterion, so this pass restores
        // only the MINIMUM set of marked faces needed to keep the island count (RestoreSplitComponents) -
        // enough to keep that island, not so much as to keep the redundant spike. The coarse alternative
        // (KeepConnectivitySafe's all-or-nothing-per-component rule) was measured and is too blunt: it
        // reverted 1141 of 1205 marks and the "wall" survived untouched.
        //
        // The pass runs BEFORE the prune, so the fragments a component that cannot be made whole leaves
        // behind are judged by the island cap rather than shipped.
        //
        // The one exemption: a cell covered by a FORCED-walkable triangle. A synthesized ramp or offset
        // connector is deliberately steeper than the walkable cap (a ladder chain runs steeper than the cap)
        // and is the mechanism that makes a ladder traversable, so its cells are the ONLY ones allowed to
        // carry a face above it.
        public const float SteepFaceMarginDegreesDefault = 10f;

        // ------------------------------------------------------------------
        // GATE/DOOR PLATE BLEND - tolerances (see BlendGatePlates for the full rule)
        // ------------------------------------------------------------------
        // A face counts as part of a "dead-flat plate" at this slope or below. Recast's span tops are
        // ch-quantised, so a horizontal slab comes out at 0.0 deg; the detail mesh cannot add much.
        public const float GatePlateFlatDegrees = 2f;
        // Group size / XZ-area window. Below three faces or one square metre it is a sliver; above 120 m^2, or
        // longer than 8 m on a side, it is a floor, a deck or a terrace and blending it would move real
        // geometry. 8 m is the reported gate's own footprint plus room (5.25 x 3.0 m measured).
        public const int GatePlateMinFaces = 3;
        public const float GatePlateMinAreaM2 = 1f;
        public const float GatePlateMaxAreaM2 = 120f;
        public const float GatePlateMaxSpanMeters = 8f;
        // "Standing proud of the surroundings": the plate's height minus the 10th percentile of the
        // neighbouring mesh's heights in the ring around it. The FLOOR of the test is the agent climb itself -
        // a plate that already meets its surroundings inside the climb has nothing wrong with it, and leaving
        // it alone is worth an island - and 2.5 m caps the class (a roof / a bridge deck is not a floor, and
        // its surroundings are metres away).
        public const float GatePlateProudMaxMeters = 2.5f;
        // The fit needs enough, spread-out samples to know WHICH way the ground falls.
        public const int GatePlateMinSamples = 6;
        public const float GatePlateMinSpanMeters = 1.5f;
        public const float GatePlateMaxFitRmsMeters = 1.0f;
        // A blend must be an improvement: the worst neighbour mismatch after the fit has to beat the flat
        // plate's worst mismatch by this margin, or the plate is left exactly as it was.
        public const float GatePlateMinImproveMeters = 0.05f;
        // Conditioning floor on the fit's normal matrix: det(sxx*szz - sxz^2) must exceed this times
        // sxx*szz, i.e. |correlation| < 0.9995. A near-collinear sample cloud makes the gradient (and the
        // intercept solved around the plate centroid) explode while still "explaining" the band.
        public const double GatePlateMinCorrelation1 = 1e-3;

        // The face's own height range has to exceed this to count as a jump. Two agent climbs plus one
        // cell height (1.4 m at climb 0.6 / ch 0.2), which clears the lab stair's 1.20 m connector with
        // room to spare and still cuts the 3.2 m+ faces at the reported spot.
        public static float SteepFaceMinHeightMeters(float agentClimbMeters, float cellHeightMeters)
        {
            return 2f * agentClimbMeters + cellHeightMeters;
        }

        public static float SteepFaceThresholdDegrees(float maxSlopeDegrees, float marginDegrees)
        {
            if (marginDegrees < 0f) marginDegrees = SteepFaceMarginDegreesDefault;
            float t = maxSlopeDegrees + marginDegrees;
            if (t > 89.9f) t = 89.9f;
            return t;
        }

        // Its own pass, so it can run BEFORE the prune (see the call site in Build). The quad pairs do not
        // exist yet at that point, which is fine: a face this steep is never a quad (the quad merge only
        // combines near-coplanar, thin faces).
        private static int RepairSteepFaces(MergeOutput o, HashSet<long> forcedRampCells, float cellSize,
            float maxSlopeDegrees, float marginDegrees, float minHeightMeters, Action<string> log)
        {
            int faceCount = o.Tris.Count / 3;
            if (faceCount == 0) return 0;
            if (SteepFaceThresholdDegrees(maxSlopeDegrees, marginDegrees) >= 89.9f) return 0;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var drop = new bool[faceCount];
            var steepDrop = new bool[faceCount];
            var spike = new bool[faceCount];
            string detail;
            int marked = MarkSteepFaces(o, drop, spike, forcedRampCells, cellSize, maxSlopeDegrees,
                marginDegrees, minHeightMeters, out detail);
            if (marked == 0) return 0;
            // Every steep drop is RESTORABLE: unlike a bad FACING (a face with no ground footprint, which is
            // malformed whatever it costs), this class is a plausible surface that is merely too steep to
            // walk, so the island count gets a vote - but only for the faces it actually needs.
            for (int f = 0; f < faceCount; f++) steepDrop[f] = drop[f];
            int restored = SteepFaceConnectivityGuard
                ? RestoreSplitComponents(o, drop, steepDrop, spike)
                : 0;
            int removed = marked - restored;
            if (removed == 0)
            {
                Log(log, "v2: steep faces: marked " + marked + " face(s) " + detail
                    + " - all " + marked + " restored to keep the island count, none dropped, in "
                    + watch.ElapsedMilliseconds + "ms");
                return 0;
            }
            CompactFaces(o, drop);
            Log(log, "v2: steep faces: dropped " + removed + " of " + marked + " marked face(s) " + detail
                + (restored > 0 ? ", " + restored + " restored as the minimum needed to keep the island count"
                    : "")
                + " in " + watch.ElapsedMilliseconds + "ms (before the prune, so any fragment it leaves"
                + " is judged by the island cap)");
            return removed;
        }

        private static int MarkSteepFaces(MergeOutput o, bool[] drop, bool[] spike, HashSet<long> forcedRampCells,
            float cellSize, float maxSlopeDegrees, float marginDegrees, float minHeightMeters, out string log)
        {
            int faceCount = o.Tris.Count / 3;
            double thrRad = SteepFaceThresholdDegrees(maxSlopeDegrees, marginDegrees) * Math.PI / 180.0;
            double sinThr = Math.Sin(thrRad);
            int removed = 0, forcedKept = 0, shortKept = 0;
            float firstX = 0f, firstY = 0f, firstZ = 0f, worstDeg = 0f;
            bool haveFirst = false;
            for (int i = 0; i < faceCount; i++)
            {
                int a = o.Tris[i * 3], b = o.Tris[i * 3 + 1], c = o.Tris[i * 3 + 2];
                double ux = o.X[b] - o.X[a], uy = o.Y[b] - o.Y[a], uz = o.Z[b] - o.Z[a];
                double wx = o.X[c] - o.X[a], wy = o.Y[c] - o.Y[a], wz = o.Z[c] - o.Z[a];
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                if (len < 1e-12) continue;                 // degenerate: MarkBadFacings owns it
                // sin(slope) is the horizontal share of the normal: sin(slope) > sin(threshold) <=> the face
                // is steeper than the threshold, with no acos and no sign handling to get wrong.
                double horiz = Math.Sqrt(nx * nx + nz * nz) / len;
                if (horiz <= sinThr) continue;
                // ... AND tall: the face's own height range must exceed what an agent could step across. A
                // short steep face is a kerb or a single riser and is legitimate; see the header.
                float yLo = Math.Min(o.Y[a], Math.Min(o.Y[b], o.Y[c]));
                float yHi = Math.Max(o.Y[a], Math.Max(o.Y[b], o.Y[c]));
                if (yHi - yLo <= minHeightMeters) { shortKept++; continue; }
                // Forced-ramp cells are exempt (see header). A face belongs to the ramp if its CENTROID cell
                // or ANY of its three VERTEX cells is a forced cell: Recast merges a ramp's span with the
                // surrounding columns, and the face at that seam lies one span outside the ramp's own
                // footprint - measured on the suite's Test 23 (b) (ladder, floor to floor), where a
                // centroid-only test cut the single face that joins the ramp to the floor.
                if (forcedRampCells != null && forcedRampCells.Count > 0)
                {
                    float cx = (o.X[a] + o.X[b] + o.X[c]) / 3f;
                    float cz = (o.Z[a] + o.Z[b] + o.Z[c]) / 3f;
                    if (forcedRampCells.Contains(CellKey(cx, cz, cellSize))
                        || forcedRampCells.Contains(CellKey(o.X[a], o.Z[a], cellSize))
                        || forcedRampCells.Contains(CellKey(o.X[b], o.Z[b], cellSize))
                        || forcedRampCells.Contains(CellKey(o.X[c], o.Z[c], cellSize))) {
                        forcedKept++;
                        continue;
                    }
                }
                drop[i] = true;
                removed++;
                double deg = Math.Asin(horiz > 1.0 ? 1.0 : horiz) * 180.0 / Math.PI;
                if (deg > worstDeg) worstDeg = (float)deg;
                if (!haveFirst)
                {
                    haveFirst = true;
                    firstX = (o.X[a] + o.X[b] + o.X[c]) / 3f;
                    firstY = (o.Y[a] + o.Y[b] + o.Y[c]) / 3f;
                    firstZ = (o.Z[a] + o.Z[b] + o.Z[c]) / 3f;
                }
            }
            log = "> " + F(SteepFaceThresholdDegrees(maxSlopeDegrees, marginDegrees)) + "deg"
                + (maxSlopeDegrees > 0f ? " (maxSlope " + F(maxSlopeDegrees) + "deg + "
                    + F(marginDegrees) + "deg margin)" : "")
                + " AND taller than " + F(minHeightMeters) + "m"
                + ", worst=" + F(worstDeg) + "deg"
                + (forcedKept > 0 ? ", " + forcedKept + " kept on forced-ramp cells" : "")
                + (shortKept > 0 ? ", " + shortKept + " kept as short (<= " + F(minHeightMeters) + "m tall)" : "")
                + ", first at (" + F(firstX) + "," + F(firstY) + "," + F(firstZ) + ")";

            // CONTOUR-SPIKE SIGNATURE: a marked face that shares a vertex with at least two OTHER marked
            // faces is part of a fan at one hub, which is exactly the shape Recast's contour produces when a
            // boundary vertex takes its height from a span metres below its neighbours (the reported
            // staircase: eight marked faces on the single vertex (1120.500, 115.700, 1078.500)). A fan is not
            // a route - the levels it spans are joined by the flight itself - so the connectivity restore
            // below never picks one while any other candidate bridges the same fragments. A lone steep face
            // (the lab's stair riser, a real ledge) is not a fan and keeps its normal priority.
            var hubCount = new Dictionary<int, int>();
            for (int i = 0; i < faceCount; i++)
            {
                if (!drop[i]) continue;
                for (int k = 0; k < 3; k++)
                {
                    int v = o.Tris[i * 3 + k];
                    int n;
                    hubCount[v] = hubCount.TryGetValue(v, out n) ? n + 1 : 1;
                }
            }
            int fans = 0;
            for (int i = 0; i < faceCount; i++)
            {
                if (!drop[i]) continue;
                if (hubCount[o.Tris[i * 3]] >= 3 || hubCount[o.Tris[i * 3 + 1]] >= 3
                    || hubCount[o.Tris[i * 3 + 2]] >= 3) { spike[i] = true; fans++; }
            }
            log += ", " + fans + " of them in a fan at one hub";
            return removed;
        }

        // MINIMAL connectivity restoration for the steep-face repair - the reason this pass can fix the
        // reported staircase WITHOUT costing an island.
        //
        // KeepConnectivitySafe (the overlap repair's guard) is all-or-nothing PER COMPONENT: if dropping the
        // marked faces splits any component, EVERY marked face in it is put back. Applied to the steep class
        // that is far too coarse, and measurably so on CC_20: the class is 1205 faces spread over many
        // components, so one genuinely load-bearing steep connector anywhere puts every other component's
        // steep faces back too - including the eight-face contour-spike fan at the reported staircase, whose
        // levels are ALSO joined by a real 42-69 deg chain. Measured: with the coarse guard 1141 of 1205
        // marks were reverted and the "wall" survived; unguarded, the staircase was fixed but CC_20 lost 2
        // islands (15 -> 13).
        //
        // This restores the MINIMUM. For every original component the drop split, put back only the marked
        // faces needed to make it whole, greedily choosing the face that touches the most fragments (ties
        // broken by the total fragment size it would rejoin, then by face index, so the choice is
        // deterministic). A redundant spike is never picked, because another face in the same component
        // rejoins the fragments just as well; a sole-link connector IS picked, because it is the only one
        // touching both sides. A component that cannot be made whole (nothing left touches two fragments)
        // is left split and the prune judges the fragments - which is what the pass ordering is for.
        //
        // Cost: two union-find passes over the face graph (the same pair KeepConnectivitySafe already pays)
        // plus one vertex->face map and a per-component greedy.
        private static int RestoreSplitComponents(MergeOutput o, bool[] drop, bool[] restorable, bool[] spike)
        {
            int nf = o.Tris.Count / 3;
            int pending = 0;
            for (int f = 0; f < nf; f++) if (drop[f] && restorable[f]) pending++;
            if (pending == 0) return 0;

            int[] comp0 = FaceComponentsOf(o.Tris, nf);

            // Tentative drop -> the surviving face list, plus the old->new face map and its reverse.
            var kept = new List<int>(o.Tris.Count);
            var map = new int[nf];
            for (int f = 0; f < nf; f++)
            {
                if (drop[f] && restorable[f]) { map[f] = -1; continue; }
                map[f] = kept.Count / 3;
                kept.Add(o.Tris[f * 3]);
                kept.Add(o.Tris[f * 3 + 1]);
                kept.Add(o.Tris[f * 3 + 2]);
            }
            int nf2 = kept.Count / 3;
            if (nf2 == 0) return 0;
            int[] comp1 = FaceComponentsOf(kept, nf2);
            var origOf = new int[nf2];
            for (int f = 0; f < nf; f++) if (map[f] >= 0) origOf[map[f]] = f;

            // vertex -> surviving faces, so a dropped face can be scored by the fragments it would rejoin.
            var vFaces = new Dictionary<int, List<int>>();
            for (int f = 0; f < nf2; f++)
                for (int k = 0; k < 3; k++)
                {
                    int v = kept[f * 3 + k];
                    List<int> lst;
                    if (!vFaces.TryGetValue(v, out lst)) { lst = new List<int>(4); vFaces[v] = lst; }
                    lst.Add(f);
                }

            var fragOf = new int[nf2];
            var fragSize = new Dictionary<int, int>();
            for (int f = 0; f < nf2; f++)
            {
                int fr = comp1[f];
                fragOf[f] = fr;
                int sz;
                fragSize[fr] = fragSize.TryGetValue(fr, out sz) ? sz + 1 : 1;
            }

            // How many distinct fragments does each ORIGINAL component own? Counted with a hashed
            // (component, fragment) key: a per-component List.Contains would be O(fragments) per face,
            // i.e. O(F^2) on a component the size of the castle.
            var seenPair = new HashSet<long>();
            var fragCount = new Dictionary<int, int>();
            var splitFaces = new Dictionary<int, List<int>>();
            var candsOfComp = new Dictionary<int, List<int>>();
            for (int f = 0; f < nf2; f++)
            {
                int c = FaceFind(comp0, origOf[f]);
                if (!seenPair.Add(((long)c << 32) ^ (uint)fragOf[f]))
                {
                    // Second face of an already-counted fragment: remember it only once we know the
                    // component splits (checked below, on the first face's count).
                    continue;
                }
                int n;
                fragCount[c] = fragCount.TryGetValue(c, out n) ? n + 1 : 1;
            }
            // Marked faces per component, and the surviving faces of the components that split. One pass,
            // and only the components the drop actually fragmented are materialised.
            for (int f = 0; f < nf; f++)
            {
                if (!drop[f] || !restorable[f]) continue;
                int c = FaceFind(comp0, f);
                int n;
                if (!fragCount.TryGetValue(c, out n) || n <= 1) continue;
                List<int> lst;
                if (!candsOfComp.TryGetValue(c, out lst)) { lst = new List<int>(); candsOfComp[c] = lst; }
                lst.Add(f);
            }
            foreach (int c in candsOfComp.Keys)
                splitFaces[c] = new List<int>();
            for (int f = 0; f < nf2; f++)
            {
                int c = FaceFind(comp0, origOf[f]);
                List<int> lst;
                if (!splitFaces.TryGetValue(c, out lst)) continue;
                lst.Add(f);
            }

            int restored = 0;
            foreach (KeyValuePair<int, List<int>> kv in candsOfComp)
            {
                // The fragment list for this component, built only now and only for split components.
                var fragSeen = new HashSet<int>();
                var frags = new List<int>();
                List<int> mine = splitFaces[kv.Key];
                for (int i = 0; i < mine.Count; i++)
                    if (fragSeen.Add(fragOf[mine[i]])) frags.Add(fragOf[mine[i]]);
                if (frags.Count <= 1) continue;
                List<int> cands = kv.Value;

                // Union-find over this component's fragment ids.
                var fragRoot = new Dictionary<int, int>();
                for (int i = 0; i < frags.Count; i++) fragRoot[frags[i]] = frags[i];
                Func<int, int> find = delegate (int x)
                {
                    int r = x;
                    while (fragRoot[r] != r) r = fragRoot[r];
                    while (fragRoot[x] != r) { int nx = fragRoot[x]; fragRoot[x] = r; x = nx; }
                    return r;
                };
                int remaining = frags.Count;

                while (remaining > 1)
                {
                    int best = -1, bestTouch = 0, bestSize = 0;
                    bool bestSpike = true;      // a non-fan face always outranks a fan face
                    for (int i = 0; i < cands.Count; i++)
                    {
                        int f = cands[i];
                        if (!drop[f]) continue;                  // already restored
                        // Distinct fragment roots this face touches, and the total size it would rejoin.
                        int touched = 0, size = 0, rootSeen = -1;
                        int first = -1;
                        bool twoSides = false;
                        for (int k = 0; k < 3 && !twoSides; k++)
                        {
                            List<int> lst;
                            if (!vFaces.TryGetValue(o.Tris[f * 3 + k], out lst)) continue;
                            for (int q = 0; q < lst.Count; q++)
                            {
                                int fr = fragOf[lst[q]];
                                int r = find(fr);
                                if (r != rootSeen) { touched++; size += fragSize[fr]; rootSeen = r; }
                                if (first < 0) first = r; else if (r != first) { twoSides = true; }
                            }
                        }
                        if (!twoSides) continue;                 // touches one fragment only: cannot bridge
                        bool isSpike = spike != null && spike[f];
                        bool better = isSpike != bestSpike
                            ? !isSpike                              // never pick a fan while a plain face can
                            : (touched > bestTouch || (touched == bestTouch && size > bestSize));
                        if (best < 0 || better)
                        {
                            best = f; bestTouch = touched; bestSize = size; bestSpike = isSpike;
                        }
                    }
                    if (best < 0) break;                         // nothing left that bridges
                    int seen = -1;
                    for (int k = 0; k < 3; k++)
                    {
                        List<int> lst;
                        if (!vFaces.TryGetValue(o.Tris[best * 3 + k], out lst)) continue;
                        for (int q = 0; q < lst.Count; q++)
                        {
                            int r = find(fragOf[lst[q]]);
                            if (r == seen) continue;
                            if (seen >= 0) { fragRoot[seen] = r; remaining--; }
                            seen = r;
                        }
                    }
                    drop[best] = false;
                    restored++;
                }
            }
            return restored;
        }

        // Marks (does not remove) the redundant face of every same-level overlap - the face whose ground
        // centroid lies inside the other's, always the smaller of the two. This is the navmesh overlapping
        // and crisscrossing with itself: two surfaces of one walkable level laid across each other. For the
        // vertex-disjoint pairs (two sheets crossing - the X) dropping the smaller costs NO coverage at all,
        // because the bigger face already covers the same footprint; for the adjacent pairs it costs the
        // sliver's own area, which is exactly the visual garbage the sliver cleanup exists to remove.
        // Reverts the OVERLAP drops inside any component they would split apart. Dropping a face can disconnect the
        // surface when that face is the only link between two halves (a one-face neck), and the island count
        // is a hard acceptance criterion - the engine creates one island per component, so a repair that
        // fragments the mesh is worse than the defect it removes. Cost: two union-find passes over the face
        // graph (one before, one on the tentatively-dropped set) plus one triangle-list copy; measured on
        // CC_20 (241k faces) it is a few tens of milliseconds. Bad FACINGS are never reverted - a face with
        // no ground footprint is malformed whatever it costs.
        private static int KeepConnectivitySafe(MergeOutput o, bool[] drop, bool[] overlapDrop)
        {
            int nf = o.Tris.Count / 3;
            int touched = 0;
            for (int f = 0; f < nf; f++) if (drop[f] && overlapDrop[f]) touched++;
            if (touched == 0) return 0;

            int[] comp0 = FaceComponentsOf(o.Tris, nf);
            var map = new int[nf];
            var kept = new List<int>(o.Tris.Count);
            for (int f = 0; f < nf; f++)
            {
                if (drop[f] && overlapDrop[f]) { map[f] = -1; continue; }
                map[f] = kept.Count / 3;
                kept.Add(o.Tris[f * 3]);
                kept.Add(o.Tris[f * 3 + 1]);
                kept.Add(o.Tris[f * 3 + 2]);
            }
            int nf2 = kept.Count / 3;
            if (nf2 == 0) return 0;
            int[] comp1 = FaceComponentsOf(kept, nf2);

            // For every original component that had a drop: did its survivors end up in one component?
            var survivors = new Dictionary<int, int>();
            var split = new HashSet<int>();
            for (int f = 0; f < nf; f++)
            {
                if (map[f] < 0) continue;
                int c0 = FaceFind(comp0, f);
                int c1 = FaceFind(comp1, map[f]);
                int prev;
                if (survivors.TryGetValue(c0, out prev)) { if (prev != c1) split.Add(c0); }
                else survivors[c0] = c1;
            }
            if (split.Count == 0) return 0;

            int reverted = 0;
            for (int f = 0; f < nf; f++)
            {
                if (!drop[f] || !overlapDrop[f]) continue;
                if (!split.Contains(FaceFind(comp0, f))) continue;
                drop[f] = false;
                reverted++;
            }
            return reverted;
        }

        // Union-find over the face graph (faces joined by a shared edge), returning the raw parent array so
        // the caller can FaceFind() a root. Same edge rule as the prune's island count.
        private static int[] FaceComponentsOf(List<int> tris, int nf)
        {
            var fp = new int[nf];
            for (int i = 0; i < nf; i++) fp[i] = i;
            var edgeFirst = new Dictionary<long, int>(LongKeyComparer.Instance);
            for (int f = 0; f < nf; f++)
            {
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                FaceUnionEdge(edgeFirst, fp, f, a, b);
                FaceUnionEdge(edgeFirst, fp, f, b, c);
                FaceUnionEdge(edgeFirst, fp, f, c, a);
            }
            return fp;
        }

        private static int MarkSelfOverlaps(MergeOutput o, bool[] drop, bool[] overlapDrop, out string log)
        {
            int faceCount = o.Tris.Count / 3;
            if (faceCount == 0) { log = "no faces"; return 0; }
            var xyz = new float[o.X.Count * 3];
            for (int i = 0; i < o.X.Count; i++)
            {
                xyz[i * 3] = o.X[i];
                xyz[i * 3 + 1] = o.Y[i];
                xyz[i * 3 + 2] = o.Z[i];
            }
            var dropList = new List<int>();
            int total;
            int pairs = ScanSelfOverlaps(xyz, o.Tris, faceCount, SelfOverlapSameLevelMeters, dropList,
                true, out total);
            int n = 0;
            for (int i = 0; i < dropList.Count; i++)
            {
                int f = dropList[i];
                if (f < 0 || f >= faceCount || drop[f]) continue;
                drop[f] = true;
                overlapDrop[f] = true;
                n++;
            }
            log = pairs + " of " + total + " overlapping pair(s) are disjoint, dropped " + n + " face(s)";
            return n;
        }

        private static void CompactFaces(MergeOutput o, bool[] removed)
        {
            int faceCount = o.Tris.Count / 3;
            var keep = new List<int>(o.Tris.Count);
            for (int i = 0; i < faceCount; i++)
            {
                if (removed[i]) continue;
                keep.Add(o.Tris[i * 3]);
                keep.Add(o.Tris[i * 3 + 1]);
                keep.Add(o.Tris[i * 3 + 2]);
            }
            o.Tris.Clear();
            o.Tris.AddRange(keep);
        }

        // The signed ground footprint (m^2) of triangle (a,b,c) in walk order: the Y component of its
        // normal, positive when the face looks up. Identical to NavMeshCleanup's NormalY, so the
        // cleanup's guards, these repairs and the export gate all judge a face by one number.
        private static double FootprintM2(IList<float> x, IList<float> y, IList<float> z, int a, int b, int c)
        {
            double ux = x[b] - x[a], uz = z[b] - z[a];
            double vx = x[c] - x[a], vz = z[c] - z[a];
            return uz * vx - ux * vz;
        }

        private static double FootprintM2(float[] xyz, int a, int b, int c)
        {
            double ux = xyz[b * 3] - xyz[a * 3], uz = xyz[b * 3 + 2] - xyz[a * 3 + 2];
            double vx = xyz[c * 3] - xyz[a * 3], vz = xyz[c * 3 + 2] - xyz[a * 3 + 2];
            return uz * vx - ux * vz;
        }

        // Two faces are the same walkable surface when their centroid heights agree within this many
        // metres. It is the cleanup's own outline-move tolerance (NavMeshCleanup.BoundaryToleranceMeters):
        // two sheets of one floor never sit that far apart, while a genuine floor over a ceiling on a
        // multi-level map does. Measured on CC_20 the class saturates fast - 57 pairs at 0.15 m, 91 at
        // 0.35 m, 141 at 0.75 m - so this tolerance is generous rather than tight.
        public const float SelfOverlapSameLevelMeters = 0.35f;

        // Counts (and optionally collects) the faces that overlap a face of the same walkable surface: a
        // face overlaps when its ground centroid lies strictly inside the other's XZ triangle. This ONE
        // implementation backs both the repair above and the export gate, so the two can never disagree
        // about what clean means. Cost is O(F) expected: one uniform XZ grid over the face boxes, then one
        // point-in-triangle test per candidate pair, with early-outs on the height and the boxes.
        // drop receives the face indices to remove (the contained/smaller one); null = count only.
        private static int ScanSelfOverlaps(float[] xyz, List<int> tris, int faceCount, float levelMeters,
            List<int> drop, bool disjointOnly, out int totalPairs)
        {
            const float cell = 4f;
            var ccx = new float[faceCount]; var ccy = new float[faceCount]; var ccz = new float[faceCount];
            var lox = new float[faceCount]; var hix = new float[faceCount];
            var loz = new float[faceCount]; var hiz = new float[faceCount];
            var area = new float[faceCount];
            var grid = new Dictionary<long, List<int>>(LongKeyComparer.Instance);
            for (int f = 0; f < faceCount; f++)
            {
                int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                double ax = xyz[a * 3], ay = xyz[a * 3 + 1], az = xyz[a * 3 + 2];
                double bx = xyz[b * 3], by = xyz[b * 3 + 1], bz = xyz[b * 3 + 2];
                double ex = xyz[c * 3], ey = xyz[c * 3 + 1], ez = xyz[c * 3 + 2];
                ccx[f] = (float)((ax + bx + ex) / 3.0);
                ccy[f] = (float)((ay + by + ey) / 3.0);
                ccz[f] = (float)((az + bz + ez) / 3.0);
                lox[f] = (float)Math.Min(ax, Math.Min(bx, ex));
                hix[f] = (float)Math.Max(ax, Math.Max(bx, ex));
                loz[f] = (float)Math.Min(az, Math.Min(bz, ez));
                hiz[f] = (float)Math.Max(az, Math.Max(bz, ez));
                double nx = (by - ay) * (ez - az) - (bz - az) * (ey - ay);
                double ny = (bz - az) * (ex - ax) - (bx - ax) * (ez - az);
                double nz = (bx - ax) * (ey - ay) - (by - ay) * (ex - ax);
                area[f] = (float)(0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz));
                int x0 = (int)Math.Floor(lox[f] / cell), x1 = (int)Math.Floor(hix[f] / cell);
                int z0 = (int)Math.Floor(loz[f] / cell), z1 = (int)Math.Floor(hiz[f] / cell);
                for (int gx = x0; gx <= x1; gx++)
                    for (int gz = z0; gz <= z1; gz++)
                    {
                        long key = ((long)gx << 32) ^ (uint)gz;
                        List<int> list;
                        if (!grid.TryGetValue(key, out list)) { list = new List<int>(8); grid[key] = list; }
                        list.Add(f);
                    }
            }

            int pairs = 0;
            totalPairs = 0;
            var seen = drop != null ? new HashSet<int>() : null;
            foreach (KeyValuePair<long, List<int>> kv in grid)
            {
                List<int> cand = kv.Value;
                int n = cand.Count;
                for (int i = 0; i < n; i++)
                {
                    int f = cand[i];
                    for (int j = i + 1; j < n; j++)
                    {
                        int g = cand[j];
                        if (Math.Abs(ccy[f] - ccy[g]) > levelMeters) continue;
                        if (lox[f] > hix[g] || lox[g] > hix[f] || loz[f] > hiz[g] || loz[g] > hiz[f]) continue;
                        bool inF = CentroidInTri(xyz, tris, g, ccx[f], ccz[f]);
                        bool inG = CentroidInTri(xyz, tris, f, ccx[g], ccz[g]);
                        if (!inF && !inG) continue;
                        totalPairs++;
                        if (disjointOnly && SharesVert(tris, f, g)) continue;
                        pairs++;
                        if (drop == null) continue;
                        int loser = inF ? (inG ? (area[f] <= area[g] ? f : g) : f) : g;
                        if (seen.Add(loser)) drop.Add(loser);
                    }
                }
            }
            return pairs;
        }

        private static bool SharesVert(List<int> tris, int f, int g)
        {
            int v0 = tris[f * 3], v1 = tris[f * 3 + 1], v2 = tris[f * 3 + 2];
            return HasVertex(tris, g, v0) || HasVertex(tris, g, v1) || HasVertex(tris, g, v2);
        }

        private static bool HasVertex(List<int> tris, int f, int v)
        {
            return v == tris[f * 3] || v == tris[f * 3 + 1] || v == tris[f * 3 + 2];
        }

        private static bool CentroidInTri(float[] xyz, List<int> tris, int f, float px, float pz)
        {
            int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
            double ax = xyz[a * 3], az = xyz[a * 3 + 2];
            double bx = xyz[b * 3], bz = xyz[b * 3 + 2];
            double ex = xyz[c * 3], ez = xyz[c * 3 + 2];
            double d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
            double d2 = (px - ex) * (bz - ez) - (bx - ex) * (pz - ez);
            double d3 = (px - ax) * (ez - az) - (ex - ax) * (pz - az);
            const double eps = 1e-6;
            bool neg = d1 < -eps || d2 < -eps || d3 < -eps;
            bool pos = d1 > eps || d2 > eps || d3 > eps;
            return !(neg && pos);
        }

        private static void AddFaceEdge(Dictionary<long, int> count, Dictionary<long, List<int>> perEdge,
            int face, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int c;
            count.TryGetValue(key, out c);
            count[key] = c + 1;
            List<int> list;
            if (!perEdge.TryGetValue(key, out list))
            {
                list = new List<int>();
                perEdge[key] = list;
            }
            list.Add(face);
        }

        private static void DecEdge(Dictionary<long, int> count, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int c;
            if (count.TryGetValue(key, out c) && c > 0)
                count[key] = c - 1;
        }

        // Topology AND face-quality invariants for the merged mesh. A well-formed triangle soup over a
        // surface has every edge shared by at most 2 faces, no duplicate faces and no self-loops; then
        // E == (3F + boundary)/2 exactly. These gate the bin write + import.
        //
        // V3.46 adds the two malformed-face classes the user reported, so they can never ship silently
        // again (the writer always writes the per-face inverted flag 0, so nothing downstream would ever
        // catch them):
        //   InvertedFaces - a face whose ground footprint is negative (it looks DOWN). Never produced by
        //                   the cleanup any more (V3.46 guard A in NavMeshCleanup/TriangulateRing).
        //   VerticalFaces - a face whose ground footprint is zero (a wall: no footprint at all, which the
        //                   engine's n-gon triangulator turns into a degenerate polygon).
        //   SelfOverlapPairs - two vertex-disjoint faces of the same walkable surface (centroid heights
        //                   within SelfOverlapSameLevelMeters) whose footprints contain each other's
        //                   centroid: two surfaces laid across each other, the crisscrossing X.
        //   BadQuadWalks - an emitted 4-vertex face that is not a simple walk with the winding and the XZ
        //                   convexity of the two triangles it replaces (a bowtie would be the X).
        //   SteepFaces / SteepTallFaces - faces past the walkable cap, and the subset that is also taller
        //                   than two agent climbs (the "vertical jump" class). Reported, NOT blocking: the
        //                   steep repair needs the forced-ramp provenance (empty when no stepped prop
        //                   synthesized anything), a synthesized ladder chain is legitimately this steep,
        //                   and a connectivity-restored steep face is deliberately still there. The count
        //                   sits on the invariants line next to inverted/vertical, which do block, so a
        //                   regression in the repair shows up as a number instead of shipping silently.
        // The first three are removed by the repairs above; the fourth is guaranteed by MergeQuads. All
        // are re-measured here, so a regression anywhere between them fails the gate instead of
        // reaching the engine.
        private static void ComputeInvariants(VoxelMeshResult result, int[] tris, int faceCount, int[] quadPairs,
            float steepThresholdDegrees, float steepMinHeightMeters)
        {
            result.InvertedFaces = 0;
            result.VerticalFaces = 0;
            result.SelfOverlapPairs = 0;
            result.BadQuadWalks = 0;
            result.OverlappingFacesAny = 0;
            result.SteepFaces = 0;
            result.SteepTallFaces = 0;
            result.FaceQualityMs = 0;
            if (tris == null || faceCount == 0)
            {
                result.EdgeCount = 0;
                result.BoundaryEdges = 0;
                result.MaxEdgeMultiplicity = 0;
                result.DuplicateFaces = 0;
                result.SelfLoops = 0;
                result.RequiredEdges = 0;
                result.InvariantsPassed = faceCount == 0;
                return;
            }

            var edgeMult = new Dictionary<long, int>(LongKeyComparer.Instance);
            var faces = new Dictionary<FaceKey, int>();
            int selfLoops = 0;
            int dupFaces = 0;
            int inverted = 0;
            int vertical = 0;

            for (int f = 0; f < faceCount; f++)
            {
                int a = tris[f * 3 + 0];
                int b = tris[f * 3 + 1];
                int c = tris[f * 3 + 2];
                if (a == b || b == c || a == c)
                {
                    selfLoops++;
                    continue;
                }

                var fk = new FaceKey(a, b, c);
                int seen;
                if (faces.TryGetValue(fk, out seen))
                {
                    faces[fk] = seen + 1;
                    dupFaces++;
                }
                else
                {
                    faces[fk] = 1;
                }

                double fp = result.VertsXyz != null
                    ? FootprintM2(result.VertsXyz, a, b, c) : 1.0;
                if (fp <= 0) inverted++;
                if (fp <= NavMeshCleanup.MinGroundFootprintM2) vertical++;

                AddEdgeMult(edgeMult, a, b);
                AddEdgeMult(edgeMult, b, c);
                AddEdgeMult(edgeMult, c, a);
            }

            int boundary = 0;
            int maxMult = 0;
            foreach (KeyValuePair<long, int> kv in edgeMult)
            {
                if (kv.Value == 1) boundary++;
                if (kv.Value > maxMult) maxMult = kv.Value;
            }

            var qualityWatch = System.Diagnostics.Stopwatch.StartNew();
            int selfOverlaps = 0;
            if (result.VertsXyz != null)
            {
                var triList = new List<int>(tris);
                int totalOverlaps;
                selfOverlaps = ScanSelfOverlaps(result.VertsXyz, triList, faceCount,
                    SelfOverlapSameLevelMeters, null, true, out totalOverlaps);
                result.OverlappingFacesAny = totalOverlaps;
            }
            int badQuads = CheckQuadWalks(result, quadPairs);
            // The steep-face class (see the header). Reported, not part of the verdict.
            int steepFaces = 0, steepTallFaces = 0;
            if (result.VertsXyz != null && steepThresholdDegrees > 0f)
            {
                double sinThr = Math.Sin(steepThresholdDegrees * Math.PI / 180.0);
                for (int f = 0; f < faceCount; f++)
                {
                    int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
                    if (a == b || b == c || a == c) continue;
                    double ux = result.VertsXyz[b * 3] - result.VertsXyz[a * 3];
                    double uy = result.VertsXyz[b * 3 + 1] - result.VertsXyz[a * 3 + 1];
                    double uz = result.VertsXyz[b * 3 + 2] - result.VertsXyz[a * 3 + 2];
                    double wx = result.VertsXyz[c * 3] - result.VertsXyz[a * 3];
                    double wy = result.VertsXyz[c * 3 + 1] - result.VertsXyz[a * 3 + 1];
                    double wz = result.VertsXyz[c * 3 + 2] - result.VertsXyz[a * 3 + 2];
                    double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                    double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (len < 1e-12) continue;
                    double horiz = Math.Sqrt(nx * nx + nz * nz) / len;
                    if (horiz <= sinThr) continue;
                    steepFaces++;
                    float yLo = Math.Min(result.VertsXyz[a * 3 + 1],
                        Math.Min(result.VertsXyz[b * 3 + 1], result.VertsXyz[c * 3 + 1]));
                    float yHi = Math.Max(result.VertsXyz[a * 3 + 1],
                        Math.Max(result.VertsXyz[b * 3 + 1], result.VertsXyz[c * 3 + 1]));
                    if (yHi - yLo > steepMinHeightMeters) steepTallFaces++;
                }
            }
            result.SteepTallFaces = steepTallFaces;
            result.FaceQualityMs = (int)qualityWatch.ElapsedMilliseconds;

            result.EdgeCount = edgeMult.Count;
            result.BoundaryEdges = boundary;
            result.MaxEdgeMultiplicity = maxMult;
            result.DuplicateFaces = dupFaces;
            result.SelfLoops = selfLoops;
            result.RequiredEdges = (3 * faceCount + boundary) / 2;
            result.InvertedFaces = inverted;
            result.VerticalFaces = vertical;
            result.SelfOverlapPairs = selfOverlaps;
            result.BadQuadWalks = badQuads;
            result.SteepFaces = steepFaces;
            // SelfOverlapPairs is deliberately NOT part of the verdict, and that is a measured decision, not
            // an oversight: clearing it costs real coverage. The repair drops the redundant face of every
            // vertex-disjoint overlapping pair whose removal keeps the island count - and on CC_20 every
            // remaining pair sits on a ONE-FACE-WIDE neck, where dropping either face disconnects the
            // surface (measured: dropping the 64 edge-sharing slivers takes the map from 15 islands to
            // 51, which the engine would turn into 36 fragments). So the class is measured, reported and
            // repaired as far as connectivity allows, and the count goes on the invariants line so a
            // regression is visible instead of silent. The malformed-face classes below are hard.
            result.InvariantsPassed = maxMult <= 2 && dupFaces == 0 && selfLoops == 0
                && result.EdgeCount >= result.RequiredEdges
                && inverted == 0 && vertical == 0 && badQuads == 0;
        }

        // Re-derives every emitted quad's boundary walk exactly the way the writer and the merge stage do,
        // and refuses anything that is not a simple 4-cycle with the surface's winding and XZ convexity.
        // MergeQuads already enforces all of it; this is the independent second opinion the export gate
        // needs, because the writer trusts QuadPairs completely.
        private static int CheckQuadWalks(VoxelMeshResult result, int[] quadPairs)
        {
            if (quadPairs == null || quadPairs.Length < 2 || result.VertsXyz == null) return 0;
            int bad = 0;
            for (int k = 0; k + 1 < quadPairs.Length; k += 2)
            {
                int f0 = quadPairs[k], f1 = quadPairs[k + 1];
                if (f0 < 0 || f1 < 0 || f0 >= result.FaceCount || f1 >= result.FaceCount || f0 == f1)
                {
                    bad++;
                    continue;
                }
                int[] w = QuadWalkOf(result, f0, f1);
                // four distinct vertices, in a cycle that is not a bowtie (all four corners turn the same way)
                if (w[0] == w[1] || w[1] == w[2] || w[2] == w[3] || w[3] == w[0]
                    || w[0] == w[2] || w[1] == w[3])
                {
                    bad++;
                    continue;
                }
                double area2 = XzShoelace2(result.VertsXyz, w);
                double t0 = XzTriArea2(result.VertsXyz, result.Tris, f0);
                double t1 = XzTriArea2(result.VertsXyz, result.Tris, f1);
                if (Math.Abs(area2) < 1e-9 || Math.Abs(t0) < 1e-9 || Math.Abs(t1) < 1e-9) { bad++; continue; }
                if (area2 * t0 <= 0 || area2 * t1 <= 0) { bad++; continue; }   // winding flip
                bool reflex = false;
                for (int i = 0; i < 4 && !reflex; i++)
                {
                    int ia = w[(i + 3) % 4], ib = w[i], ic = w[(i + 1) % 4];
                    double cr = (result.VertsXyz[ib * 3] - result.VertsXyz[ia * 3])
                        * (double)(result.VertsXyz[ic * 3 + 2] - result.VertsXyz[ia * 3 + 2])
                        - (result.VertsXyz[ib * 3 + 2] - result.VertsXyz[ia * 3 + 2])
                        * (double)(result.VertsXyz[ic * 3] - result.VertsXyz[ia * 3]);
                    if (cr * area2 <= 0) reflex = true;                        // reflex or flat corner
                    if (SegsCrossXZ(result.VertsXyz, w[0], w[1], w[2], w[3])
                        || SegsCrossXZ(result.VertsXyz, w[1], w[2], w[3], w[0])) reflex = true;
                }
                if (reflex) bad++;
            }
            return bad;
        }

        // The 4-vertex boundary walk of the quad formed by two triangles sharing the edge (u,w): with the
        // shared edge directed along f0's winding, the walk is q -> x -> p -> y. Same derivation as
        // NavMeshCleanup.MergeQuads and Nmg8Writer.BuildQuad.
        private static int[] QuadWalkOf(VoxelMeshResult result, int f0, int f1)
        {
            int[] t = result.Tris;
            int a0 = t[f0 * 3], b0 = t[f0 * 3 + 1], c0 = t[f0 * 3 + 2];
            int a1 = t[f1 * 3], b1 = t[f1 * 3 + 1], c1 = t[f1 * 3 + 2];
            int p, q, x;
            if (Shares(a0, b0, a1, b1, c1)) { p = a0; q = b0; x = c0; }
            else if (Shares(b0, c0, a1, b1, c1)) { p = b0; q = c0; x = a0; }
            else { p = c0; q = a0; x = b0; }
            int y = a1 != p && a1 != q ? a1 : (b1 != p && b1 != q ? b1 : c1);
            return new int[] { q, x, p, y };
        }

        private static bool Shares(int u, int w, int a, int b, int c)
        {
            return (u == a || u == b || u == c) && (w == a || w == b || w == c);
        }

        private static double XzShoelace2(float[] xyz, int[] w)
        {
            double s = 0.0;
            for (int i = 0; i < 4; i++)
            {
                int a = w[i], b = w[(i + 1) % 4];
                s += (double)xyz[a * 3] * xyz[b * 3 + 2] - (double)xyz[b * 3] * xyz[a * 3 + 2];
            }
            return s;
        }

        private static double XzTriArea2(float[] xyz, int[] tris, int f)
        {
            int a = tris[f * 3], b = tris[f * 3 + 1], c = tris[f * 3 + 2];
            return (double)xyz[a * 3] * xyz[b * 3 + 2] - (double)xyz[b * 3] * xyz[a * 3 + 2]
                 + (double)xyz[b * 3] * xyz[c * 3 + 2] - (double)xyz[c * 3] * xyz[b * 3 + 2]
                 + (double)xyz[c * 3] * xyz[a * 3 + 2] - (double)xyz[a * 3] * xyz[c * 3 + 2];
        }

        private static bool SegsCrossXZ(float[] xyz, int a, int b, int c, int d)
        {
            double ax = xyz[a * 3], az = xyz[a * 3 + 2], bx = xyz[b * 3], bz = xyz[b * 3 + 2];
            double cx = xyz[c * 3], cz = xyz[c * 3 + 2], dx = xyz[d * 3], dz = xyz[d * 3 + 2];
            double d1 = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
            double d2 = (bx - ax) * (dz - az) - (bz - az) * (dx - ax);
            double d3 = (dx - cx) * (az - cz) - (dz - cz) * (ax - cx);
            double d4 = (dx - cx) * (bz - cz) - (dz - cz) * (bx - cx);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
                && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static void AddEdgeMult(Dictionary<long, int> map, int a, int b)
        {
            long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
            int c;
            map.TryGetValue(key, out c);
            map[key] = c + 1;
        }

        // Undirected face key (vertices sorted ascending), for duplicate detection.
        private struct FaceKey : IEquatable<FaceKey>
        {
            private readonly int _a;
            private readonly int _b;
            private readonly int _c;

            public FaceKey(int a, int b, int c)
            {
                int lo, mid, hi;
                if (a <= b && a <= c) { lo = a; if (b <= c) { mid = b; hi = c; } else { mid = c; hi = b; } }
                else if (b <= a && b <= c) { lo = b; if (a <= c) { mid = a; hi = c; } else { mid = c; hi = a; } }
                else { lo = c; if (a <= b) { mid = a; hi = b; } else { mid = b; hi = a; } }
                _a = lo; _b = mid; _c = hi;
            }

            public bool Equals(FaceKey other)
            {
                return _a == other._a && _b == other._b && _c == other._c;
            }

            public override bool Equals(object obj)
            {
                return obj is FaceKey && Equals((FaceKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + _a;
                    h = h * 31 + _b;
                    h = h * 31 + _c;
                    return h;
                }
            }
        }

        private sealed class LongKeyComparer : IEqualityComparer<long>
        {
            public static readonly LongKeyComparer Instance = new LongKeyComparer();

            public bool Equals(long a, long b)
            {
                return a == b;
            }

            public int GetHashCode(long value)
            {
                unchecked
                {
                    ulong x = (ulong)value;
                    x ^= x >> 33;
                    x *= 0xff51afd7ed558ccdUL;
                    x ^= x >> 33;
                    x *= 0xc4ceb9fe1a85ec53UL;
                    x ^= x >> 33;
                    return (int)x;
                }
            }
        }

        // ------------------------------------------------------------------
        // Agent climb derivation (pure; used by VoxelNavMeshGenerator and the harness)
        // ------------------------------------------------------------------
        // Recast rejects a span when the spread of its traversable neighbours' floors exceeds
        // walkableClimb. On a uniform slope the rise per cell is cs*tan(slope)/ch voxels and the
        // spread is twice that, so walkableClimb must be >= 2*cs*tan(slope)/ch for the slope to
        // survive the ledge filter. Derive the climb in whole ch steps, clamped to minClimb.
        // (A larger climb also connects single vertical steps up to that height; the trade-off is
        // documented in the README. Lowering VoxelCellSizeMeters also lowers the required climb.)
        public static float ResolveAgentClimbMeters(float cs, float ch, float slopeDegrees, float minClimb)
        {
            if (cs <= 0f || ch <= 0f)
                return minClimb;

            double slopeRad = slopeDegrees * Math.PI / 180.0;
            int climbCells = (int)Math.Ceiling(2.0 * cs * Math.Tan(slopeRad) / ch);
            if (climbCells < 1)
                climbCells = 1;

            float climb = climbCells * ch;
            return climb < minClimb ? minClimb : climb;
        }

        // walkableClimb in voxel steps for a resolved climb. Mirrors the build's derivation,
        // including the float-roundoff epsilon so e.g. 2.4/0.2 is 12, not 11.
        public static int WalkableClimbVoxels(float climbMeters, float ch)
        {
            if (ch <= 0f)
                return 0;
            return (int)Math.Floor(climbMeters / ch + 1e-4f);
        }

        // Effective detail-mesh sampling distance (m): explicit override, else max(0.9, cs).
        public static float EffectiveDetailSampleDist(TiledNavMeshSettings s)
        {
            return s.DetailSampleDistMeters > 0f ? s.DetailSampleDistMeters : Math.Max(0.9f, s.VoxelCellSizeMeters);
        }

        // Effective contour simplification error (voxels): explicit override, else MaxErrorMeters/cs.
        public static float EffectiveContourMaxError(TiledNavMeshSettings s)
        {
            return s.ContourMaxErrorVoxels > 0f ? s.ContourMaxErrorVoxels : s.MaxErrorMeters / s.VoxelCellSizeMeters;
        }

        private static void Log(Action<string> log, string message)
        {
            if (log != null)
                log(message);
        }

        private static string F(float value)
        {
            return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
