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

        // Harness-only: cap on the seams ClosePlateSeams may emit. 0 disables the seam stage while leaving the
        // plate blend itself running, which is how the two halves of the pass are told apart on a real scene.
        public static int GatePlateSeamCap = GatePlateSeamMaxCount;

        // ==================================================================
        // Barrier support (V3.58)
        // ==================================================================
        // A Bannerlord "barrier" is an invisible one-way wall the mission AI refuses to cross. It is
        // NOT an engine entity type: the scene stores a `barrier_builder` script component on an
        // otherwise plain game_entity, and the engine's editor component (native
        // Rgl\ScriptComponents\barrier_builder.cpp) turns the referenced path into a `bo_barrier`
        // collision body at load time. That generated body is an ordinary physics body whose
        // BodyFlags carry the blocking mode, so it IS enumerated by the normal scene scan - which is
        // how we collect it (no XML re-parsing, no path re-derivation).
        //
        // BLOCKING MODE -> BodyFlags (decompiled from TaleWorlds.Native.dll: the component's build
        // function selects one of exactly four masks by its `Mask` string; the engine's own
        // XML<->flags decoder FUN_7ff8d040e6f0 confirms the bit values):
        //
        //   "Everything"             -> 0x00000200  Barrier
        //   "Only Agents"            -> 0x00200200  Barrier | AgentOnly
        //   "Only AI Agents"         -> 0x00200300  Barrier | AgentOnly | AILimiter
        //   "Only Missile and Melee" -> 0x00400200  MissileOnly | Barrier
        //
        // Which of those must the navmesh respect? Only the ones that block AGENTS. The engine's own
        // exclude-flag presets answer that outright (TaleWorlds.Engine.BodyFlags):
        //
        //   CommonCollisionExcludeFlagsForAgent   = 0x2051B189  includes MissileOnly, excludes AgentOnly
        //   CommonCollisionExcludeFlagsForMissile = 0x2031B789  includes Barrier|Barrier3D
        //
        // so a MissileOnly body is invisible to every agent raycast - it stops arrows, not people -
        // and carving the mesh there would make agents stop dead at a barrier the game itself lets
        // them walk through. The other three all carry `Barrier`, which is deliberately ABSENT from
        // every agent-facing exclude set (that is precisely what "invisible wall" means: the agent
        // raycasts straight into it). Hence the one rule below, and hence the unit test.
        //
        // ONE-WAY IS WAIVED. `Flip` negates the wall's physics normal (the component's build does
        // `iVar32 = *flip ? -1 : 1` and scales the quad by it) - it is a collision-response flag, not
        // graph data. A navmesh is an undirected polygon soup (the stitch's union-find is symmetric;
        // the engine's only "make this impassable" tool is a per-face boolean,
        // Scene.SetAbilityOfFacesWithId), so a directed constraint cannot be expressed in it at all.
        // We therefore carve a TWO-SIDED wall, which is strictly more restrictive than the runtime
        // rule: an agent the game would let through in one direction is stopped in both.
        // Documented in README.md.

        // BodyFlags bits, restated as plain ints: this file never touches TaleWorlds types, which is
        // what keeps the core standalone-harness testable (docs/plan-navmesh-v2.md section 2).
        public const int BodyFlagBarrier = 0x00000200;
        public const int BodyFlagBarrier3D = 0x00000400;
        public const int BodyFlagAiLimiter = 0x00000100;
        public const int BodyFlagAgentOnly = 0x00200000;
        public const int BodyFlagMissileOnly = 0x00400000;

        /// The whole honour rule, in one pure function so it can be unit-tested offline. True when a
        /// barrier body with these raw BodyFlags must be carved out of the navmesh.
        public static bool BarrierBlocksAgents(int flags)
        {
            if ((flags & (BodyFlagBarrier | BodyFlagBarrier3D)) == 0)
                return false;
            // A MissileOnly body is excluded from every agent raycast set, so the game lets agents walk
            // through it. Never carve. (An AgentOnly body has no such exclusion and IS carved.)
            return (flags & BodyFlagMissileOnly) == 0;
        }

        /// One barrier body's world-space collision geometry, ENGINE Z-up (x, y=height, z) - the same
        /// space SceneCollisionGeometry emits. Plain arrays, so it crosses the main-thread -> worker
        /// boundary like the rest of the asset soup.
        public sealed class BarrierWall
        {
            public string Name;
            public int Flags;              // raw BodyFlags of the source body
            public float[] VertsXyz;       // engine Z-up world space, 3 per vertex
            public int[] Tris;             // 0-based indices into VertsXyz
            public bool Flip;              // barrier_builder's `Flip` (recorded; two-sided cut, see above)
            public float ZMin, ZMax;       // engine-space vertical extent of the wall
        }

        /// All honour-able barriers of one scene, in engine Z-up world space.
        public sealed class BarrierWalls
        {
            public readonly List<BarrierWall> Walls = new List<BarrierWall>();

            /// Barriers that were seen but deliberately NOT carved, with the reason. Kept for the log
            /// so "the tool ignored this barrier" is always explainable.
            public readonly List<string> Skipped = new List<string>();

            public int TotalTris;
            public int Count { get { return Walls.Count; } }
            public bool IsEmpty { get { return Walls.Count == 0; } }

            public void Add(BarrierWall w)
            {
                if (w == null || w.Tris == null || w.Tris.Length < 3) return;
                Walls.Add(w);
                TotalTris += w.Tris.Length / 3;
            }

            public void AddSkipped(string name, int flags)
            {
                if (Skipped.Count < 64)
                    Skipped.Add(name + " flags=0x" + flags.ToString("X8") + " (missile-only: not carved)");
            }
        }

        // Why a cell is blocked. Provenance-tagged from the start: the tile-border work lands in the
        // SAME set next round, and the two must be intersectable, so this is a keyed map rather than
        // a bare HashSet<long>.
        public enum ObstructionKind
        {
            Barrier = 1,
            Border = 2,      // reserved for the tile-border pass
        }

        /// World-XZ cells that must not be bridged by any connectivity repair (gap fill, per-tile
        /// stitch, merge stitch). Keyed by CellKey(x, z, cs) - the same integer lattice the stepped-
        /// prop provenance uses, so the sets can be merged and compared.
        public sealed class ObstructionCells
        {
            private readonly Dictionary<long, ObstructionKind> _map =
                new Dictionary<long, ObstructionKind>(LongKeyComparer.Instance);
            private readonly float _cs;

            public ObstructionCells(float cellSizeMeters)
            {
                _cs = cellSizeMeters > 0f ? cellSizeMeters : 1f;
            }

            public int Count { get { return _map.Count; } }
            public float CellSize { get { return _cs; } }
            public IEnumerable<KeyValuePair<long, ObstructionKind>> All { get { return _map; } }

            public void Add(float x, float z, ObstructionKind kind)
            {
                long k = CellKey(x, z, _cs);
                ObstructionKind cur;
                if (_map.TryGetValue(k, out cur))
                {
                    if (cur == kind) return;
                    _map[k] = (ObstructionKind)((int)cur | (int)kind);
                }
                else _map[k] = kind;
            }

            /// Adds a cell by its already-computed key. Used to merge per-tile sets after the parallel
            /// pass without re-deriving the key (and without a float round-trip that could land the
            /// point in a different cell).
            public void AddRaw(long key, ObstructionKind kind)
            {
                ObstructionKind cur;
                if (_map.TryGetValue(key, out cur))
                {
                    if (cur == kind) return;
                    _map[key] = (ObstructionKind)((int)cur | (int)kind);
                }
                else _map[key] = kind;
            }

            public bool Contains(float x, float z)
            {
                return _map.ContainsKey(CellKey(x, z, _cs));
            }

            /// The obstruction kinds recorded for a cell, or 0 when the cell is clear. Lets a refusal
            /// be attributed to the wall that caused it (Barrier) or to the border (Border) instead of
            /// lumping the two together in one counter.
            public ObstructionKind KindAt(float x, float z)
            {
                ObstructionKind cur;
                if (_map.TryGetValue(CellKey(x, z, _cs), out cur)) return cur;
                return 0;
            }

            /// True when ANY cell on the straight XZ run from (ax,az) to (bx,bz) is obstructed. Used by
            /// every stitch, which all work on a straight segment between two boundary vertices.
            public bool SegmentCrosses(float ax, float az, float bx, float bz)
            {
                float dx = bx - ax, dz = bz - az;
                int steps = (int)Math.Ceiling(Math.Sqrt((double)(dx * dx + dz * dz)) / (_cs * 0.5f));
                if (steps < 1) steps = 1;
                if (steps > 4096) steps = 4096;
                for (int i = 0; i <= steps; i++)
                {
                    float t = (float)i / steps;
                    if (Contains(ax + dx * t, az + dz * t)) return true;
                }
                return false;
            }

            /// As SegmentCrosses, but only for cells carrying the given provenance. Lets a stitch refusal
            /// be charged to the border rather than to a barrier when both could have caused it.
            public bool SegmentCrossesKind(float ax, float az, float bx, float bz, ObstructionKind kind)
            {
                float dx = bx - ax, dz = bz - az;
                int steps = (int)Math.Ceiling(Math.Sqrt((double)(dx * dx + dz * dz)) / (_cs * 0.5f));
                if (steps < 1) steps = 1;
                if (steps > 4096) steps = 4096;
                for (int i = 0; i <= steps; i++)
                {
                    float t = (float)i / steps;
                    if ((KindAt(ax + dx * t, az + dz * t) & kind) != 0) return true;
                }
                return false;
            }
        }

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

            // BARRIER RESPEKT (NavMeshVoxelBarriers, default on). When Barriers is non-empty the
            // honour-able barrier bodies are rasterised into each tile's heightfield as SOLID
            // (RC_NULL_AREA) spans, so erosion / the clearance filter / the ledge trim all treat
            // them as real obstacles and no walkable face can sit on or across them. Null (or an
            // empty list) keeps the build byte-for-byte identical to the pre-barrier behaviour -
            // that is what the toggle's `false` restores.
            //
            // Two rules keep this from over-cutting:
            //  * HEIGHT RULE. A cut is applied only where the wall STRADDLES the navigable band:
            //    zMin <= floorTop + climb && zMax >= floorTop. A 2 m barrier whose base is 25 m below
            //    a walkway, or one sitting on a ledge above it, therefore cuts nothing on that
            //    walkway. CC_20's hand-placed _barrier_04x04m bodies sit at z~123.5 on high walls
            //    while the streets below run at z~95 - they must leave those streets untouched.
            //  * TWO-SIDED. `Flip` (the barrier's one-way flag) is recorded but not honoured; see the
            //    block comment at the top of this file.
            public BarrierWalls Barriers;

            // Barrier accounting, filled by Build (0 when there are none).
            public int BarrierBodies, BarrierSkipped, BarrierTrisCut, BarrierCellsCut,
                BarrierCellsReleased, BarrierCellsGated, BarrierStitchRefused, BarrierGapRefused;

            // Border accounting, filled by Build (all 0 when the scene declares no border probes, which
            // is the structural no-op path: no polygon, no lattice clamp, no soup cull, no span mask).
            public int BorderCellsMasked, BorderCellsOutside, BorderStitchRefused, BorderGapRefused;

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

            // Barrier accounting for this tile. BarrierCellsCut/Released are the height rule's verdict
            // (cut = the wall straddles the navigable band; released = it does not, so the wall is left
            // out of this tile entirely). BarrierCellsGated counts cells marked into the shared
            // obstruction map; BarrierGapRefused / BarrierStitchRefused count connectivity repairs this
            // tile refused because a barrier stood in the way.
            public int BarrierCellsCut, BarrierCellsReleased, BarrierCellsGated,
                BarrierGapRefused, BarrierStitchRefused;

            // Border accounting for this tile: BorderCellsOutside counts every column whose centre fell
            // outside the polygon (including the tile's padding), BorderCellsMasked only the walkable
            // spans that were actually nulled. The two refusal counters are the connectivity repairs this
            // tile declined because the border stood in the way.
            public int BorderCellsMasked, BorderCellsOutside, BorderGapRefused, BorderStitchRefused;
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
            // Border clamp effect (V3.59). BorderSource is BorderPolygon.SourceNone/Soft/Hard, 0 when the
            // scene declares no border probes.
            public int BorderSource, BorderVertices;
            public float BorderAreaM2, BorderMinX, BorderMinZ, BorderMaxX, BorderMaxZ;
            public int BorderCellsMasked, BorderCellsOutside, BorderGapRefused, BorderStitchRefused;
            public int CulledByBorderTris;
            public double SoupAreaM2BeforeClamp, SoupAreaM2AfterClamp;

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
            bool[] ledgeExemptTriangles = null, BorderPolygon borderPoly = null)
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

            // BORDER CLAMP (V3.59), part 1 of 3: bound the region we even consider to the playable
            // area. On a scene whose border covers a fifth of its terrain this is the single biggest win
            // in the build - CC_20_siege goes from a 100x100 tile grid over 2000x2000 m of heightmap to
            // roughly 37x30 over the 583x469 m the probes declare.
            //
            // The clamp is an INTERSECTION with the soup AABB, never a replacement: an absent, empty or
            // non-overlapping border therefore reproduces the unclamped numbers exactly. With border ==
            // null, or with a border that covers the soup, every term below collapses to its pre-V3.59
            // value and the tileOff terms are 0, which is what keeps the no-border path byte-identical.
            float clampMinX = minX, clampMaxX = maxX, clampMinZ = minZ, clampMaxZ = maxZ;
            bool borderClamped = false;
            if (borderPoly != null)
            {
                float bx0 = Math.Max(minX, borderPoly.MinX), bx1 = Math.Min(maxX, borderPoly.MaxX);
                float bz0 = Math.Max(minZ, borderPoly.MinZ), bz1 = Math.Min(maxZ, borderPoly.MaxZ);
                if (bx0 <= bx1 && bz0 <= bz1)
                {
                    clampMinX = bx0; clampMaxX = bx1; clampMinZ = bz0; clampMaxZ = bz1;
                    borderClamped = true;
                }
            }

            // Fixed snapped world origin on the tile lattice. Every tile boundary
            // and every emitted vertex then lies on the global lattice
            // origin + integer*cs (X/Z) / +integer*ch (Y), which is what makes
            // cross-tile welding exact. The snap is unchanged by the clamp - only the
            // input it is applied to moves - so the lattice is exactly as rigid as before.
            float originX = (float)(Math.Floor((double)clampMinX / tileSpan) * tileSpan);
            float originZ = (float)(Math.Floor((double)clampMinZ / tileSpan) * tileSpan);
            float margin = Math.Max(2f, s.AgentHeightMeters + 0.5f);
            float bminY = (float)(Math.Floor((double)(minY - margin) / ch) * ch);
            float bmaxY = (float)(Math.Ceiling((double)(maxY + margin) / ch) * ch);

            // Absolute tile index of the clamped region's first column/row, and the DENSE counts. The
            // tile array stays densely indexed from 0 (so every existing index computation is untouched)
            // while tileOffX/Z carry the lattice origin back into world space.
            int absTilesX = Math.Max(1, (int)Math.Ceiling((clampMaxX - originX) / tileSpan));
            int absTilesZ = Math.Max(1, (int)Math.Ceiling((clampMaxZ - originZ) / tileSpan));
            int tileOffX = Math.Max(0, (int)Math.Floor((clampMinX - originX) / tileSpan));
            int tileOffZ = Math.Max(0, (int)Math.Floor((clampMinZ - originZ) / tileSpan));
            if (tileOffX >= absTilesX) tileOffX = absTilesX - 1;
            if (tileOffZ >= absTilesZ) tileOffZ = absTilesZ - 1;
            int tilesX = absTilesX - tileOffX;
            int tilesZ = absTilesZ - tileOffZ;
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
                + (tileOffX != 0 || tileOffZ != 0 ? " tileOff=" + tileOffX + "," + tileOffZ : "")
                + " bminY=" + F(bminY) + " bmaxY=" + F(bmaxY));
            if (borderPoly != null)
            {
                Log(log, "v2: border: lattice clamped to the polygon AABB=" + borderClamped
                    + " cells=" + tilesX + "x" + tilesZ + " (" + tileCount + " tiles)"
                    + " area=" + F((float)((clampMaxX - clampMinX) * (clampMaxZ - clampMinZ) / 1000.0))
                    + "k m2 (soup " + F((float)((maxX - minX) * (maxZ - minZ) / 1000.0)) + "k m2)");
            }
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

            // --- barrier input: engine Z-up -> Recast Y-up, binned per tile ---
            // Built once, read-only in every worker. Null when the feature is off or the scene has no
            // honour-able barrier, which is what keeps the pre-barrier path byte-for-byte identical.
            float[] barrierVerts = null;
            int[] barrierTris = null;
            int barrierTriCount = 0;
            // SceneCollisionGeometry always hands us a (possibly empty) BarrierWalls, so the gate is
            // "has walls", not "is non-null". Only then is anything allocated: a scene with no
            // honour-able barrier must reach the builder with nothing barrier-shaped in it at all.
            bool haveBarriers = s.Barriers != null && !s.Barriers.IsEmpty;
            List<int>[] tileBarrierTris = haveBarriers ? new List<int>[tileCount] : null;
            if (haveBarriers)
            {
                s.BarrierBodies = s.Barriers.Count;
                s.BarrierSkipped = s.Barriers.Skipped.Count;
                barrierVerts = BarrierToRecastSpace(s.Barriers, out barrierTris);
                barrierTriCount = barrierTris != null ? barrierTris.Length / 3 : 0;

                for (int t = 0; t < barrierTriCount; t++)
                {
                    int i0 = barrierTris[t * 3], i1 = barrierTris[t * 3 + 1], i2 = barrierTris[t * 3 + 2];
                    float tx0 = barrierVerts[i0 * 3], tx1 = barrierVerts[i1 * 3], tx2 = barrierVerts[i2 * 3];
                    float tz0 = barrierVerts[i0 * 3 + 2], tz1 = barrierVerts[i1 * 3 + 2], tz2 = barrierVerts[i2 * 3 + 2];
                    float triMinX = Math.Min(tx0, Math.Min(tx1, tx2));
                    float triMaxX = Math.Max(tx0, Math.Max(tx1, tx2));
                    float triMinZ = Math.Min(tz0, Math.Min(tz1, tz2));
                    float triMaxZ = Math.Max(tz0, Math.Max(tz1, tz2));

                    int bTx0 = (int)Math.Floor((triMinX - originX - pad) / tileSpan) - tileOffX;
                    int bTx1 = (int)Math.Floor((triMaxX - originX + pad) / tileSpan) - tileOffX;
                    int bTz0 = (int)Math.Floor((triMinZ - originZ - pad) / tileSpan) - tileOffZ;
                    int bTz1 = (int)Math.Floor((triMaxZ - originZ + pad) / tileSpan) - tileOffZ;
                    if (bTx0 < 0) bTx0 = 0;
                    if (bTz0 < 0) bTz0 = 0;
                    if (bTx1 > tilesX - 1) bTx1 = tilesX - 1;
                    if (bTz1 > tilesZ - 1) bTz1 = tilesZ - 1;
                    if (bTx0 > bTx1 || bTz0 > bTz1) continue;

                    for (int tz = bTz0; tz <= bTz1; tz++)
                    {
                        for (int tx = bTx0; tx <= bTx1; tx++)
                        {
                            int idx = tz * tilesX + tx;
                            List<int> list = tileBarrierTris[idx];
                            if (list == null)
                            {
                                list = new List<int>();
                                tileBarrierTris[idx] = list;
                            }
                            list.Add(t);
                        }
                    }
                }
                Log(log, "v2: barriers: bodies=" + s.BarrierBodies + " skipped=" + s.BarrierSkipped
                    + " tris=" + barrierTriCount
                    + " (carved as solid; missile-only masks are not carved)");
                for (int i = 0; i < s.Barriers.Skipped.Count && i < 8; i++)
                    Log(log, "v2: barriers: skipped " + s.Barriers.Skipped[i]);
            }

            // --- bin input triangles into tiles (padded AABB overlap) ---
            var tileTris = new List<int>[tileCount];
            int culledByAabb = 0;
            int culledByBorder = 0;
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

                // BORDER CLAMP, part 2: a triangle whose AABB misses the border's AABB entirely can never
                // contribute an in-borders cell, so it is dropped before it is binned or rasterised. This
                // is a superset filter, not the clamp itself - a triangle straddling the border edge is
                // kept and judged cell by cell in part 3.
                if (borderPoly != null
                    && (triMaxX < borderPoly.MinX || triMinX > borderPoly.MaxX
                        || triMaxZ < borderPoly.MinZ || triMinZ > borderPoly.MaxZ))
                {
                    culledByBorder++;
                    culledByAabb++;
                    continue;
                }

                int xMin = (int)Math.Floor((triMinX - originX - pad) / tileSpan) - tileOffX;
                int xMax = (int)Math.Floor((triMaxX - originX + pad) / tileSpan) - tileOffX;
                int zMin = (int)Math.Floor((triMinZ - originZ - pad) / tileSpan) - tileOffZ;
                int zMax = (int)Math.Floor((triMaxZ - originZ + pad) / tileSpan) - tileOffZ;
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
                + (borderPoly != null ? " (of which outside the borderPoly AABB=" + culledByBorder + ")" : "")
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

            // One obstruction set PER TILE that actually has barrier geometry, never one per worker.
            //
            // The tempting `index % workers` partition is NOT thread-safe: Parallel.For makes no
            // promise about which indices run concurrently, and with workers = ProcessorCount two
            // live tiles can share a slot (tiles 5 and 17 both map to slot 5 on a 12-core box). Two
            // threads then mutate one Dictionary at once, which corrupts it and makes the whole build
            // non-deterministic - a data race reachable only when barriers are ON, i.e. by default.
            // Tiles WITHOUT barriers never write to a set at all, so allocating per barrier tile is
            // both correct and cheap: a wall spans a handful of tiles out of thousands.
            ObstructionCells[] tileObstructions = (barrierTriCount > 0 || borderPoly != null)
                ? new ObstructionCells[tileCount]
                : null;

            Parallel.For(0, tileCount, parallelOptions, delegate (int index)
            {
                int tx = index % tilesX + tileOffX;
                int ty = index / tilesX + tileOffZ;
                ObstructionCells mine = (tileBarrierTris != null && tileBarrierTris[index] != null) || borderPoly != null
                    ? new ObstructionCells(s.VoxelCellSizeMeters)
                    : null;
                if (tileObstructions != null) tileObstructions[index] = mine;
                results[index] = BuildTile(index, tx, ty, vertsXyz, tris, tileTris[index], s,
                    originX, originZ, tileSpan, cellsPerTile, bminY, bmaxY, width, height, border, pad,
                    walkableHeight, walkableClimb, walkableRadius, maxSimplificationError,
                    minRegionArea, mergeRegionArea, areaMod, forceWalkableTriangles, ledgeExemptTriangles,
                    barrierVerts, barrierTris,
                    tileBarrierTris != null ? tileBarrierTris[index] : null, mine, index, borderPoly);

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

            // --- merge the per-TILE obstruction sets into the one map the fillers consult ---
            ObstructionCells obstructions = (barrierTriCount > 0 || borderPoly != null)
                ? new ObstructionCells(s.VoxelCellSizeMeters)
                : null;
            if (obstructions != null)
            {
                int cutCells = 0, releasedCells = 0, gated = 0, gapRefused = 0, stitchRefused = 0;
                int borderCut = 0, borderOutside = 0, borderGapRefused = 0, borderStitchRefused = 0;
                for (int i = 0; i < results.Length; i++)
                {
                    VoxelTileResult r = results[i];
                    if (r == null || r.Failed) continue;
                    cutCells += r.BarrierCellsCut;
                    releasedCells += r.BarrierCellsReleased;
                    gated += r.BarrierCellsGated;
                    gapRefused += r.BarrierGapRefused;
                    stitchRefused += r.BarrierStitchRefused;
                    borderCut += r.BorderCellsMasked;
                    borderOutside += r.BorderCellsOutside;
                    borderGapRefused += r.BorderGapRefused;
                    borderStitchRefused += r.BorderStitchRefused;
                }
                for (int i = 0; i < tileObstructions.Length; i++)
                {
                    ObstructionCells t = tileObstructions[i];
                    if (t == null) continue;
                    foreach (KeyValuePair<long, ObstructionKind> kv in t.All)
                        obstructions.AddRaw(kv.Key, kv.Value);
                }
                s.BarrierCellsCut = cutCells;
                s.BarrierCellsReleased = releasedCells;
                s.BarrierCellsGated = gated;
                s.BarrierStitchRefused = stitchRefused;
                s.BarrierGapRefused = gapRefused;
                Log(log, "v2: barriers: cut cells=" + cutCells + " released=" + releasedCells
                    + " (height rule: wall outside the navigable band), obstruction cells="
                    + obstructions.Count + " (gap-fill refused=" + gapRefused + " stitch refused="
                    + stitchRefused + ")");
                if (borderPoly != null)
                {
                    s.BorderCellsMasked = borderCut;
                    s.BorderCellsOutside = borderOutside;
                    s.BorderGapRefused = borderGapRefused;
                    s.BorderStitchRefused = borderStitchRefused;
                    Log(log, "v2: border: masked spans=" + borderCut + " outside cells=" + borderOutside
                        + " (gap-fill refused=" + borderGapRefused + " stitch refused="
                        + borderStitchRefused + ")");
                }
            }

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

            // The coarse soup grid is built ONCE here and shared with the cross-tile stitch below and with the
            // gate-plate pass further down. The plate pass needs it too - the footprint stage asks the raw
            // collision what floor and what clearance a grown column would actually have - and the stitch
            // already needed it to decide whether a gap envelope is solid. One pass over the input soup.
            Dictionary<long, List<int>> soupGrid = null;
            List<int> soupBig = null;
            const float soupCell = 4f;
            if (s.GatePlateBlend || s.StitchMaxGapCells > 0)
            {
                BuildSoupGrid(vertsXyz, tris, inputTris, soupCell, out soupGrid, out soupBig);
            }

            // Cross-tile stitch (before pruning): reconnect near-miss pieces that the per-tile stitch
            // could not reach (different tiles, or beyond its cap). A stitch is refused when the input
            // soup has solid geometry spanning the gap envelope (wall, closed door, low ceiling).
            if (s.StitchMaxGapCells > 0)
            {
                int msPairs, msAdded, msRefused, msBarrierRefused, msBorderRefused;
                MergeStitch(merged, vertsXyz, tris, inputTris, soupGrid, soupBig, soupCell,
                    s.VoxelCellSizeMeters, s.CellHeightMeters,
                    s.HeightOffsetMeters, bminY, walkableHeight, walkableClimb, s.StitchMaxGapCells,
                    barrierTriCount > 0 ? obstructions : null,
                    out msPairs, out msAdded, out msRefused, out msBarrierRefused, out msBorderRefused);
                Log(log, "v2: merge-stitch: pairs=" + msPairs + " added=" + msAdded + " refused=" + msRefused
                    + (msBarrierRefused > 0 ? " (of which " + msBarrierRefused + " barrier-blocked)" : "")
                    + (msBorderRefused > 0 ? " (of which " + msBorderRefused + " border-blocked)" : "")
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
                PruneSmallIslands(merged, s.PruneMaxIslandFaces, steppedCells, qcs, pst,
                    barrierTriCount > 0 ? obstructions : null);
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
                if (pst.BarrierKeptIslands > 0)
                    Log(log, "v2: prune: kept " + pst.BarrierKeptIslands + " barrier-adjacent island(s), "
                        + pst.BarrierKeptFaces + " faces (largest=" + pst.BarrierLargestKept
                        + ") - the cut split them this pass, so the cap does not apply");
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

            // GATE/DOOR PLATE BLEND, and it is the LAST stage that touches geometry. Three stages on the
            // merged mesh: re-emit a prop's rigid flat floor slab on a plane fitted to the built mesh around
            // it, grow that plate's footprint back toward the prop's own source collision, then weld the
            // voids that are left edge to edge. See BlendGatePlates.
            //
            // It runs HERE, after the sliver cleanup and the face-quality repair, and that placement is load
            // bearing rather than cosmetic. Placed earlier - as the first two versions had it, right after
            // RepairNonManifold - the cleanup ate what it added: 266 grown + 100 seam triangles went in and
            // only 44 came out, the plate's own east tip face went with them, and the CC_20 gate went from
            // "covered to x=1132.75" back to "bare at x=1132.75". A synthesized strip is by definition a
            // fresh thin triangle, and the cleanup exists to delete exactly those. Nothing that can remove a
            // face may run after this point.
            //
            // Two consequences, both measured rather than assumed. (1) Nothing after it drops a strip, so a
            // strip that is wrong stays wrong: the guards below have to be right, and the strip tests are
            // correspondingly strict - a lattice of samples over each strip, not four points. (2) The pass
            // only ever APPENDS faces and vertices, so every index below the pre-pass face count is
            // untouched and the quadPairs remapped just above stay valid.
            //
            // The rotation itself is unaffected by moving: it caps the tilt at MaxSlopeDegrees (50 deg),
            // and RepairSteepFaces only ever dropped faces past 60 deg and taller than 1.4 m, so a rotated
            // plate was never one of its candidates.
            int gatePlateFaces = 0;
            if (s.GatePlateBlend && merged.Tris.Count >= 3)
            {
                gatePlateFaces = BlendGatePlates(merged, s.VoxelCellSizeMeters, s.CellHeightMeters,
                    s.AgentMaxClimbMeters, s.MaxSlopeDegrees, walkableHeight, s.HeightOffsetMeters,
                    forcedRampCells, vertsXyz, tris, inputTris, soupGrid, soupBig, soupCell, log);
            }

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
            result.CulledByBorderTris = culledByBorder;
            result.RasterizedTriangles = rasterizedTris;
            result.SoupAreaM2BeforeClamp = (double)(maxX - minX) * (maxZ - minZ);
            result.SoupAreaM2AfterClamp = (double)(clampMaxX - clampMinX) * (clampMaxZ - clampMinZ);
            result.BorderCellsMasked = borderPoly != null ? s.BorderCellsMasked : 0;
            result.BorderCellsOutside = borderPoly != null ? s.BorderCellsOutside : 0;
            result.BorderGapRefused = borderPoly != null ? s.BorderGapRefused : 0;
            result.BorderStitchRefused = borderPoly != null ? s.BorderStitchRefused : 0;
            result.BorderSource = borderPoly != null ? borderPoly.Source : BorderPolygon.SourceNone;
            result.BorderVertices = borderPoly != null ? borderPoly.VertexCount : 0;
            result.BorderAreaM2 = borderPoly != null ? borderPoly.AreaM2 : 0f;
            result.BorderMinX = borderPoly != null ? borderPoly.MinX : 0f;
            result.BorderMinZ = borderPoly != null ? borderPoly.MinZ : 0f;
            result.BorderMaxX = borderPoly != null ? borderPoly.MaxX : 0f;
            result.BorderMaxZ = borderPoly != null ? borderPoly.MaxZ : 0f;
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

        // ------------------------------------------------------------------
        // BORDER POLYGON (V3.59)
        //
        // The playable area of a battle scene is declared by the engine's border probes: point entities
        // tagged walk_area_vertex (soft) or walk_area_vertex_hard (hard). The engine convex-hulls them
        // natively (rglConvex_polygon, monotone chain, CCW) and exposes the result through
        // Scene.GetSoftBoundaryVertex* / GetHardBoundaryVertex*; MBSceneUtilities.GetSoftBoundaryPoints
        // and GetHardBoundaryPoints are the managed wrappers. Hard wins: MissionHardBorderPlacer turns
        // the hard polygon into a real invisible wall (the engine's FillEntityWithHardBorderPhysicsBarrier
        // builds the __scene_hard_border__ mesh), so it is the tighter, physical limit, while the soft
        // polygon only bounds spawn/deployment validity.
        //
        // This type is the pure, TaleWorlds-free half: the scene-facing read lives in the exporter, the
        // snapshot round-trips the same shape, and everything downstream (lattice clamp, soup cull, span
        // mask, obstruction cells) is a pure function of these vertices.
        //
        // Deliberately NOT implemented here: any hull of our own. The engine already hulls the probes, and
        // a second hull would be a second opinion on the same data. What we do add is a winding
        // normalisation, because the membership test below is a half-plane test that is only valid for a
        // CCW ring - if a snapshot were ever hand-edited clockwise the whole scene would invert.
        public sealed class BorderPolygon
        {
            public const int SourceNone = 0;
            public const int SourceSoft = 1;
            public const int SourceHard = 2;

            // CCW ring in engine XZ: _xy[2i] = x, _xy[2i+1] = z.
            private readonly float[] _xy;
            private readonly int _n;

            public readonly int Source;
            public readonly float MinX, MinZ, MaxX, MaxZ;
            public readonly float AreaM2;

            private BorderPolygon(float[] xy, int source)
            {
                _xy = xy;
                _n = xy.Length / 2;
                Source = source;

                float a = 0f;
                float mnx = float.MaxValue, mnz = float.MaxValue, mxx = float.MinValue, mxz = float.MinValue;
                for (int i = 0; i < _n; i++)
                {
                    float x = xy[i * 2], z = xy[i * 2 + 1];
                    if (x < mnx) mnx = x;
                    if (x > mxx) mxx = x;
                    if (z < mnz) mnz = z;
                    if (z > mxz) mxz = z;
                    float jx = xy[((i + 1) % _n) * 2], jz = xy[((i + 1) % _n) * 2 + 1];
                    a += x * jz - jx * z;
                }
                MinX = mnx; MinZ = mnz; MaxX = mxx; MaxZ = mxz;
                AreaM2 = Math.Abs(a) * 0.5f;
            }

            public int VertexCount { get { return _n; } }

            /// Flat CCW ring, x/z pairs - the snapshot's wire form.
            public float[] ToArray() { return (float[])_xy.Clone(); }

            /// Builds a polygon from a flat x/z ring. Returns null (no clamp, byte-identical build) for a
            /// ring with fewer than three vertices or one that encloses nothing, so a malformed snapshot
            /// can never invert or empty a scene.
            public static BorderPolygon FromPoints(float[] xy, int source)
            {
                if (xy == null || xy.Length < 6 || xy.Length % 2 != 0) return null;
                int n = xy.Length / 2;
                // Signed area; flip a clockwise ring so the half-plane test below is valid.
                float a = 0f;
                for (int i = 0; i < n; i++)
                {
                    float x = xy[i * 2], z = xy[i * 2 + 1];
                    float jx = xy[((i + 1) % n) * 2], jz = xy[((i + 1) % n) * 2 + 1];
                    a += x * jz - jx * z;
                }
                if (a == 0f || float.IsNaN(a)) return null;
                if (a < 0f)
                {
                    var flipped = new float[xy.Length];
                    for (int i = 0; i < n; i++)
                    {
                        flipped[i * 2] = xy[(n - 1 - i) * 2];
                        flipped[i * 2 + 1] = xy[(n - 1 - i) * 2 + 1];
                    }
                    xy = flipped;
                }
                // CONVEXITY GUARD. The engine always hands us its convex hull, and the half-plane test
                // below is only meaningful for a convex ring: on a concave one it silently rejects most
                // of the interior and the clamp erases the map (measured - a hand-edited ring ordered by
                // probe index rather than by hull produced 5754240 "outside" cells of 5753040 and an
                // empty mesh). Rather than ship that failure mode we refuse the ring, and a refused ring
                // is null, i.e. the no-border no-op: a bad ring degrades to today's behaviour instead of
                // to an empty mesh. A collinear vertex (cross == 0) is legal and common in probe data.
                for (int i = 0; i < n; i++)
                {
                    float ax = xy[i * 2], az = xy[i * 2 + 1];
                    int j = (i + 1 == n) ? 0 : i + 1;
                    int k = (j + 1 == n) ? 0 : j + 1;
                    float ex = xy[j * 2] - ax, ez = xy[j * 2 + 1] - az;
                    float fx = xy[k * 2] - xy[j * 2], fz = xy[k * 2 + 1] - xy[j * 2 + 1];
                    if (ex * fz - ez * fx < -1e-3f) return null;
                }
                var p = new BorderPolygon(xy, source);
                return p.AreaM2 > 0f ? p : null;
            }

            /// Point-in-polygon, CCW half-plane test. Points exactly ON an edge count as inside: the
            /// border line itself is the limit of the playable area, and the cell whose centre lands on it
            /// is as much inside as one a centimetre either side. No acceptance threshold is used - the
            /// game applies its own 0.05 m in MBSceneUtilities.IsPointInsideBoundaries, but that exists
            /// to forgive probe noise on a boolean question; here we simply never emit a cell outside.
            public bool Contains(float x, float z)
            {
                for (int i = 0; i < _n; i++)
                {
                    int j = (i + 1 == _n) ? 0 : i + 1;
                    float ex = _xy[j * 2] - _xy[i * 2];
                    float ez = _xy[j * 2 + 1] - _xy[i * 2 + 1];
                    float cx = x - _xy[i * 2];
                    float cz = z - _xy[i * 2 + 1];
                    if (ex * cz - ez * cx < 0f) return false;
                }
                return true;
            }

            public static string SourceName(int source)
            {
                if (source == SourceHard) return "hard";
                if (source == SourceSoft) return "soft";
                return "none";
            }
        }

        /// Which border polygon to honour. Mirrors BannerlordSceneChecker.CheckBorders, and exists as a
        /// pure function precisely so the policy can be tested without an editor scene.
        ///
        /// The hard side is gated on the PROBE COUNT, never on Scene.GetHardBoundaryVertexCount(): on a
        /// scene with fewer than three hard probes the engine's native getter does not report "no border",
        /// it returns a four-vertex default rectangle spanning roughly (-0.045,-100) to (50,50) and clears
        /// a private "hard border defined" flag that managed code cannot see (rglScene.cpp, the hard-polygon
        /// lazy builder). Trusting the count would clamp every borderless scene to that rectangle.
        public static int SelectBorderSource(int softProbeCount, int hardProbeCount)
        {
            if (hardProbeCount >= 3) return BorderPolygon.SourceHard;
            if (softProbeCount >= 3) return BorderPolygon.SourceSoft;
            return BorderPolygon.SourceNone;
        }

        /// Nulls every walkable span in a column whose centre is outside the border polygon, and marks the
        /// cell into the shared obstruction map so gap fill and both stitches cannot bridge the border.
        ///
        /// Runs after FilterLowHangingWalkableObstacles and NOT before it, which is the one place this
        /// pass differs from a literal "right after rasterisation": that filter re-marks any unmarked span
        /// that has a walkable floor within climb beneath it, so a mask applied before it would be
        /// promoted straight back. It is still ahead of every stage that could add or move a face -
        /// FilterLedgeSpans, FilterWalkableLowHeightSpans, BuildCompactHeightfield, ErodeWalkableArea,
        /// the region/contour/detail passes, the gap fill, the per-tile stitch and the merge stitch - so
        /// all of them work on cells that should exist.
        ///
        /// The membership predicate is a pure function of world XZ, which is what makes it safe to apply
        /// to a tile's PADDING cells as well as its own: the neighbour that owns that cell applies the
        /// identical verdict, so the two tiles agree everywhere and cross-tile welding is untouched.
        private static int ApplyBorderMask(RcHeightfield hf, BorderPolygon border,
            ObstructionCells obstructions, out int cellsOutside)
        {
            cellsOutside = 0;
            int cut = 0;
            int w = hf.width, h = hf.height;
            for (int ci = 0; ci < w * h; ci++)
            {
                int cx = ci % w, cz = ci / w;
                float wx = hf.bmin.X + (cx + 0.5f) * hf.cs;
                float wz = hf.bmin.Z + (cz + 0.5f) * hf.cs;
                if (border.Contains(wx, wz)) continue;
                cellsOutside++;
                // Mark the cell even when it carries no walkable span: an empty column outside the border
                // is exactly what the gap fill scans for, and it must refuse it.
                if (obstructions != null) obstructions.Add(wx, wz, ObstructionKind.Border);
                for (RcSpan sp = hf.spans[ci]; sp != null; sp = sp.next)
                {
                    if (!IsWalkableArea(sp.area)) continue;
                    sp.area = RcRecast.RC_NULL_AREA;
                    cut++;
                }
            }
            return cut;
        }

        // ------------------------------------------------------------------
        // Barrier cutting: barrier geometry -> solid spans in the tile heightfield
        //
        // The barriers arrive in ENGINE Z-up (x, y=height, z) and never touch the walkable soup. We
        // convert them to Recast Y-up once per build, bin them per tile exactly like the input
        // triangles, rasterise them into a SCRATCH heightfield, and then merge the resulting spans
        // into the tile's own heightfield by hand.
        //
        // The scratch heightfield is not an optimisation - it is what gives us provenance. A span in
        // the tile heightfield carries no "this came from a barrier" bit, so after rasterising
        // barriers straight into `hf` we could not tell which spans were ours and the height rule
        // would have nothing to release. Rasterising separately means every span we merge is known
        // to be barrier geometry, so a released one can simply be dropped.
        //
        // Engine (x, y, z) -> Recast (x, z, y): the same swap the soup capture uses.
        internal static float[] BarrierToRecastSpace(BarrierWalls walls, out int[] outTris)
        {
            outTris = null;
            if (walls == null || walls.IsEmpty) return null;
            int totalTris = walls.TotalTris;
            int totalVerts = 0;
            for (int i = 0; i < walls.Walls.Count; i++)
                totalVerts += walls.Walls[i].VertsXyz.Length / 3;
            var outV = new float[totalVerts * 3];
            var outT = new int[totalTris * 3];
            int vo = 0, to = 0;
            for (int i = 0; i < walls.Walls.Count; i++)
            {
                BarrierWall w = walls.Walls[i];
                int nv = w.VertsXyz.Length / 3;
                for (int v = 0; v < nv; v++)
                {
                    outV[(vo + v) * 3 + 0] = w.VertsXyz[v * 3 + 0];
                    outV[(vo + v) * 3 + 1] = w.VertsXyz[v * 3 + 2];   // engine z -> Recast y (height)
                    outV[(vo + v) * 3 + 2] = w.VertsXyz[v * 3 + 1];   // engine y -> Recast z
                }
                for (int k = 0; k < w.Tris.Length; k++) outT[to + k] = w.Tris[k] + vo;
                vo += nv;
                to += w.Tris.Length;
            }
            // Winding is irrelevant: every barrier triangle is rasterised with RC_NULL_AREA, so it can
            // only ever contribute solid volume, never a walkable surface that depends on facing.
            outTris = outT;
            return outV;
        }

        private static void CutBarriersIntoHeightfield(RcContext ctx, RcHeightfield hf,
            float[] barrierVerts, int[] barrierTris, int barrierTriCount,
            int walkableClimb, ObstructionCells obstructions,
            out int cellsCut, out int cellsReleased)
        {
            cellsCut = 0;
            cellsReleased = 0;
            if (barrierTris == null || barrierTriCount <= 0) return;

            // 1. Rasterise into a scratch heightfield with the same grid as the tile, so a column index
            //    means the same world cell in both.
            var scratch = new RcHeightfield(hf.width, hf.height, hf.bmin, hf.bmax, hf.cs, hf.ch,
                hf.borderSize);
            var nullAreas = new int[barrierTriCount];
            RcRasterizations.RasterizeTriangles(ctx, barrierVerts, barrierTris, nullAreas,
                barrierTriCount, scratch, walkableClimb);

            // 2. Height rule + merge. For each column that carries barrier geometry, keep the cut only
            //    when the wall STRADDLES that column's navigable band:
            //      zMin <= bandTop + climb && zMax >= bandBottom
            //    where [bandBottom, bandTop] spans ALL of the column's walkable surfaces, not just the
            //    lowest one. That distinction is not cosmetic: CC_20_siege's barrier_1 runs along a
            //    hillside where most columns carry two or more walkable levels (a terrace and the street
            //    below it). Judging against only the lowest one released 521 of 736 barrier columns on
            //    that scene and left walk-through holes in the wall - exactly what the carve exists to
            //    prevent. The band is computed from the tile's own heightfield, which already has the
            //    ordinary soup applied, so a barrier that misses the band misses it because the
            //    walkable surface really is elsewhere, not because we guessed wrong.
            float climbMeters = walkableClimb * hf.ch;
            for (int ci = 0; ci < hf.width * hf.height; ci++)
            {
                RcSpan bs = scratch.spans[ci];
                if (bs == null) continue;
                int bandBottom = int.MaxValue, bandTop = int.MinValue;
                for (RcSpan ws = hf.spans[ci]; ws != null; ws = ws.next)
                {
                    if (!IsWalkableArea(ws.area)) continue;
                    if (ws.smin < bandBottom) bandBottom = ws.smin;
                    if (ws.smax > bandTop) bandTop = ws.smax;
                }
                if (bandTop == int.MinValue)
                {
                    // No walkable surface in this column. A barrier here can only obstruct geometry
                    // that does not exist; releasing it keeps stray spans from seeding something the
                    // low-hanging-obstacle filter could later promote.
                    cellsReleased++;
                    continue;
                }
                float bandLoY = hf.bmin.Y + bandBottom * hf.ch;
                float bandHiY = hf.bmin.Y + bandTop * hf.ch;
                float zMin = hf.bmin.Y + bs.smin * hf.ch;
                float zMax = hf.bmin.Y + bs.smax * hf.ch;
                if (zMin > bandHiY + climbMeters || zMax < bandLoY)
                {
                    cellsReleased++;
                    continue;
                }
                for (RcSpan sp = bs; sp != null; sp = sp.next)
                {
                    InsertSolidSpan(hf, ci, sp.smin, sp.smax);
                    cellsCut++;
                }

                // 3. Record the obstruction so no connectivity repair can bridge the wall. Expanded by
                //    half a metre either way: the cells an agent standing next to the wall occupies are
                //    just as much a part of "no path through here" as the cells the wall itself owns.
                if (obstructions != null)
                {
                    int cx = ci % hf.width, cz = ci / hf.width;
                    float wx = hf.bmin.X + (cx + 0.5f) * hf.cs;
                    float wz = hf.bmin.Z + (cz + 0.5f) * hf.cs;
                    obstructions.Add(wx, wz, ObstructionKind.Barrier);
                    int r = Math.Max(1, (int)Math.Ceiling(0.5f / hf.cs));
                    for (int dz = -r; dz <= r; dz++)
                        for (int dx = -r; dx <= r; dx++)
                            obstructions.Add(wx + dx * hf.cs, wz + dz * hf.cs, ObstructionKind.Barrier);
                }
            }
        }

        /// Inserts a solid (RC_NULL_AREA) span of arbitrary height into a column, keeping the list
        /// sorted bottom-to-top as Recast expects. Never overlaps an existing span: an overlap means
        /// the barrier and ordinary geometry already agree there, and a merged span is exactly what
        /// Recast would have produced itself.
        private static void InsertSolidSpan(RcHeightfield hf, int columnIndex, int smin, int smax)
        {
            if (smax <= smin) return;
            for (RcSpan s = hf.spans[columnIndex]; s != null; s = s.next)
                if (s.smin < smax && s.smax > smin)
                {
                    // Overlapping existing span: grow it to cover the union instead of double-inserting.
                    if (smin < s.smin) s.smin = smin;
                    if (smax > s.smax) s.smax = smax;
                    return;
                }
            var ns = new RcSpan();
            ns.smin = smin;
            ns.smax = smax;
            ns.area = RcRecast.RC_NULL_AREA;
            RcSpan cur = hf.spans[columnIndex];
            if (cur == null || ns.smax <= cur.smin)
            {
                ns.next = cur;
                hf.spans[columnIndex] = ns;
                return;
            }
            while (cur.next != null && cur.next.smin < ns.smin) cur = cur.next;
            ns.next = cur.next;
            cur.next = ns;
        }

        private sealed class PruneStats
        {
            public int RemovedIslands, RemovedFaces, LargestRemoved, KeptIslands;
            public float LargestX, LargestY, LargestZ;
            public int ExemptIslands, ExemptFaces, ExemptLargest;
            public float ExemptX, ExemptY, ExemptZ;
            public double MaxKeptExemptFraction;
            public int MaxKeptFaces;
            // Islands kept only because they touch a barrier (see the guard in PruneSmallIslands).
            public int BarrierKeptIslands, BarrierKeptFaces, BarrierLargestKept;
        }

        private static void PruneSmallIslands(MergeOutput o, int maxFaces, HashSet<long> steppedCells,
            float cs, PruneStats st, ObstructionCells obstructions)
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
            st.BarrierKeptIslands = 0;
            st.BarrierKeptFaces = 0;
            st.BarrierLargestKept = 0;

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

            // BARRIER-ADJACENT ISLANDS ARE NEVER PRUNED. The cut can legitimately split one island in
            // two, and the side that happens to be small is then judged by the ordinary cap - which
            // would delete real navigable surface in the very pass that created it (the same failure
            // that lost CC_20 two islands when the gate-plate blend was first landed). So: an island
            // with a face next to a barrier is kept whatever its size. Keeping a small pocket is far
            // less harmful than deleting the area agents need, and the counters make the delta
            // auditable in the log.
            var barrierIsland = new Dictionary<int, bool>();
            if (obstructions != null && obstructions.Count > 0)
            {
                for (int f = 0; f < nf; f++)
                {
                    float cx = 0, cz = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        int v = o.Tris[f * 3 + k];
                        cx += o.X[v];
                        cz += o.Z[v];
                    }
                    if (!obstructions.Contains(cx / 3f, cz / 3f)) continue;
                    barrierIsland[FaceFind(fp, f)] = true;
                }
            }

            foreach (KeyValuePair<int, int> kv in count)
            {
                bool barrierTouched;
                if (barrierIsland.TryGetValue(kv.Key, out barrierTouched) && barrierTouched)
                {
                    st.BarrierKeptIslands++;
                    st.BarrierKeptFaces += kv.Value;
                    if (kv.Value > st.BarrierLargestKept) st.BarrierLargestKept = kv.Value;
                    st.KeptIslands++;
                    continue;
                }
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
            Dictionary<long, List<int>> grid, List<int> bigList, float soupCell,
            float cs, float ch, float offset, float bminY, int walkableHeight, int walkableClimb,
            int stitchMaxGapCells, ObstructionCells obstructions,
            out int pairs, out int added, out int refused, out int obstructionRefused,
            out int borderRefused)
        {
            obstructionRefused = 0;
            borderRefused = 0;
            pairs = 0;
            added = 0;
            refused = 0;
            int nf = o.Tris.Count / 3;
            if (nf < 2 || o.X.Count < 3)
                return;
            if (grid == null) BuildSoupGrid(soupVerts, soupTris, soupTriCount, soupCell, out grid, out bigList);
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

            // Coarse soup grid for the clearance probe (built by the caller and shared with the plate pass).
            const float soupCellUnused = 0f;
            _ = soupCellUnused;

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

                    // Barrier gate, same rule as the per-tile stitch: the connector's own segment must
                    // not touch an obstructed cell. Reported separately from `refused` so the log shows
                    // how much of the refusal is barrier-driven rather than soup-driven.
                    if (obstructions != null && obstructions.SegmentCrosses(mxA, mzA, mxB, mzB))
                    {
                        if (obstructions.SegmentCrossesKind(mxA, mzA, mxB, mzB, ObstructionKind.Border))
                            borderRefused++;
                        else obstructionRefused++;
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
            float maxSlopeDegrees, int walkableHeight, float heightOffsetMeters,
            HashSet<long> forcedRampCells,
            float[] soupVerts, int[] soupTris, int soupTriCount,
            Dictionary<long, List<int>> soupGrid, List<int> soupBig, float soupCell,
            Action<string> log)
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

            // --- 6. FOOTPRINT, then SEAM. The rotation fixes the plate's SLOPE; the growth puts the plate's
            //     own eroded margin back (see GatePlateGrowMaxMeters); the seam then welds whatever void is
            //     left to the nearest foreign boundary edge, edge to edge, so the engine gets a real EDGE
            //     portal rather than a corner touch. Order matters: the growth must land before the seam so
            //     the seam sees the grown plate's outer edge as a boundary edge it can weld to.
            //
            //     The growth is the one stage that reads the RAW SOUP, and the soup is NOT in mesh space:
            //     EmitVertex writes bminY + gy*ch + s.HeightOffsetMeters, so every mesh height is one
            //     HeightOffsetMeters above the soup height it came from (measured: plate h=86.300, soup floor
            //     85.800 - exactly the 0.5 m setting). The stage therefore works in soup space throughout.
            var emult = BuildEdgeMultiplicity(o);
            var grownFaces = new HashSet<int>();
            int growTris = GrowPlateFootprint(o, plans, isPlate, soupVerts, soupTris,
                soupGrid, soupBig, soupCell, grid, gcell, cs, ch, climb, maxSlopeDegrees,
                walkableHeight, heightOffsetMeters, emult, grownFaces, log,
                out int growStrips, out float growWidest);
            int seamTris = ClosePlateSeams(o, plans, isPlate, grownFaces, climb, maxSlopeDegrees,
                forcedRampCells, cs, ch, log, grid, gcell, emult,
                out int seamCount, out float seamWidest, out float seamWorstStep);
            if (seamCount > 0 && seamTris == 0) seamCount = 0;   // the self-check refused the whole batch
            if (growTris > 0)
            {
                Log(log, "v2: gate plate footprint: " + growStrips + " boundary edge(s) grown back toward the "
                    + "prop's own colliders, " + growTris + " triangles (widest " + F(growWidest) + "m, cap "
                    + F(GatePlateGrowMaxMeters) + "m, grown only where the raw soup still has an up-facing "
                    + "floor within one climb of the plate and walkableHeight of clearance above it)");
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
            if (seamCount > 0)
            {
                Log(log, "v2: gate plate seams: " + seamCount + " void(s) bridged, " + seamTris
                    + " triangles welded edge-to-edge (widest=" + F(seamWidest) + "m, worst step="
                    + F(seamWorstStep) + "m, cap " + F(GatePlateSeamMaxGapMeters)
                    + "m, only between already-connected faces so no island can move)");
            }
            return blendedFaces;
        }

        // Bridges a plate boundary edge to the nearest foreign boundary edge across a real void, as two
        // triangles welded edge-to-edge. Only pairs of faces that already share a component are bridged, so the
        // island count cannot change; the strip is shallow by construction and its cells go into
        // forcedRampCells so the steep-face repair and the cleanup leave it alone.
        private static int ClosePlateSeams(MergeOutput o, List<PlatePlan> plans, bool[] isPlate,
            HashSet<int> grownFaces, float climb,
            float maxSlopeDegrees, HashSet<long> forcedRampCells, float cs, float ch, Action<string> log,
            Dictionary<long, List<int>> grid, float gcell, Dictionary<long, int> emult,
            out int seamCount, out float widest, out float worstStep)
        {
            seamCount = 0; widest = 0f; worstStep = 0f;
            int faceCount = o.Tris.Count / 3;
            if (faceCount < 3 || plans.Count == 0) return 0;

            // grid the foreign BOUNDARY edges so the nearest-edge query is local. A face the growth stage
            // emitted counts as plate, so one plate cannot seam to its own freshly grown margin.
            float ecell = Math.Max(GatePlateSeamMaxGapMeters, Math.Max(4f * cs, 1f));
            var egrid = new Dictionary<long, List<int>>(LongKeyComparer.Instance);
            var edgeV = new List<int>();
            for (int f = 0; f < faceCount; f++)
            {
                if (f < isPlate.Length && isPlate[f]) continue;
                if (grownFaces.Contains(f)) continue;
                for (int k = 0; k < 3; k++)
                {
                    int i0 = o.Tris[f * 3 + k], i1 = o.Tris[f * 3 + ((k + 1) % 3)];
                    long key = StitchEdgeKey(i0, i1);
                    int n;
                    emult.TryGetValue(key, out n);
                    if (n != 1) continue;
                    int id = edgeV.Count / 2;
                    edgeV.Add(i0); edgeV.Add(i1);
                    float mx = (o.X[i0] + o.X[i1]) * 0.5f, mz = (o.Z[i0] + o.Z[i1]) * 0.5f;
                    long gk = unchecked(((long)(int)Math.Floor(mx / ecell) << 32)
                        | (uint)(int)Math.Floor(mz / ecell));
                    List<int> lst;
                    if (!egrid.TryGetValue(gk, out lst)) { lst = new List<int>(); egrid[gk] = lst; }
                    lst.Add(id);
                }
            }

            var usedEdge = new HashSet<long>();
            int added = 0;
            float maxSlopeTan = (float)Math.Tan(maxSlopeDegrees * Math.PI / 180.0);
            if (maxSlopeTan < 1e-3f) maxSlopeTan = 1e-3f;
            for (int pi = 0; pi < plans.Count && seamCount < GatePlateSeamCap; pi++)
            {
                PlatePlan p = plans[pi];
                for (int fi = 0; fi < p.Faces.Length; fi++)
                {
                    if (seamCount >= GatePlateSeamCap) break;
                    int f = p.Faces[fi];
                    if (f >= faceCount) continue;
                    for (int k = 0; k < 3 && seamCount < GatePlateSeamCap; k++)
                    {
                        int a = o.Tris[f * 3 + k], b = o.Tris[f * 3 + ((k + 1) % 3)];
                        long keyA = StitchEdgeKey(a, b);
                        int nA;
                        emult.TryGetValue(keyA, out nA);
                        if (nA != 1 || usedEdge.Contains(keyA)) continue;

                        // nearest foreign boundary edge, searched outward from this edge's own cells
                        float ex0 = o.X[a], ez0 = o.Z[a], ex1 = o.X[b], ez1 = o.Z[b];
                        float bestGap = float.MaxValue, bestStep = float.MaxValue;
                        int bestU = -1, bestV = -1;
                        float pminx = Math.Min(ex0, ex1) - GatePlateSeamMaxGapMeters;
                        float pmaxx = Math.Max(ex0, ex1) + GatePlateSeamMaxGapMeters;
                        float pminz = Math.Min(ez0, ez1) - GatePlateSeamMaxGapMeters;
                        float pmaxz = Math.Max(ez0, ez1) + GatePlateSeamMaxGapMeters;
                        int gx0 = (int)Math.Floor(pminx / ecell), gx1 = (int)Math.Floor(pmaxx / ecell);
                        int gz0 = (int)Math.Floor(pminz / ecell), gz1 = (int)Math.Floor(pmaxz / ecell);
                        for (int gxi = gx0; gxi <= gx1; gxi++)
                            for (int gzi = gz0; gzi <= gz1; gzi++)
                            {
                                long gk = unchecked(((long)gxi << 32) | (uint)gzi);
                                List<int> lst;
                                if (!egrid.TryGetValue(gk, out lst)) continue;
                                for (int li = 0; li < lst.Count; li++)
                                {
                                    int id = lst[li];
                                    int u = edgeV[id * 2], v = edgeV[id * 2 + 1];
                                    long keyB = StitchEdgeKey(u, v);
                                    if (usedEdge.Contains(keyB)) continue;
                                    float gap, step;
                                    if (!SeamCandidate(o, ex0, o.Y[a], ez0, ex1, o.Y[b], ez1,
                                        o.X[u], o.Y[u], o.Z[u], o.X[v], o.Y[v], o.Z[v],
                                        out gap, out step)) continue;
                                    if (gap > GatePlateSeamMaxGapMeters) continue;
                                    if (gap < GatePlateSeamMinGapMeters) continue;
                                    if (step > climb) continue;
                                    if (gap * maxSlopeTan < step) continue;         // would be a riser
                                    if (u == a || u == b || v == a || v == b) continue;
                                    // the strip's three NEW edges must not already be shared: the two triangles
                                    // contribute (a,u) and (b,v) once each and the diagonal (a,v) TWICE, so an
                                    // existing shared edge there would go to three or four ways and turn the
                                    // mesh non-manifold - which the invariants refuse to import. Measured: it
                                    // did, on Birtu (maxEdgeMult=4), before these two guards.
                                    if (EdgeMultOf(emult, StitchEdgeKey(a, v)) != 0) continue;
                                    if (EdgeMultOf(emult, StitchEdgeKey(a, u)) >= 2) continue;
                                    if (EdgeMultOf(emult, StitchEdgeKey(b, v)) >= 2) continue;
                                    // and the strip's own footprint must be EMPTY: the nearest boundary edge is
                                    // not always across a void, it can be round the side of a step, in which case
                                    // the strip lands on top of real geometry. That is what produced 23 of CC_20's
                                    // 24 self-overlaps, and the cleanup then deleted the face that was holding a
                                    // component together (CC_20 15 -> 16 islands, Basamu 1 -> 2). A seam is only
                                    // a seam if there is nothing under it.
                                    if (!StripIsClear(o, a, b, u, v, grid, gcell, faceCount, climb, ch)) continue;
                                    if (gap > bestGap) continue;
                                    bestGap = gap; bestStep = step; bestU = u; bestV = v;
                                }
                            }
                        if (bestU < 0) continue;

                        // a crossing pair would emit two overlapping triangles; the overlap invariants would
                        // (rightly) reject them, so a crossed candidate is simply not a seam
                        if (!StripIsSimple(o, a, b, bestU, bestV)) continue;

                        // emit the strip: (a,b) is the plate's boundary edge and (bestU,bestV) the foreign one,
                        // so both become interior and the engine sees an EDGE portal, not a corner touch.
                        // The strip REUSES the four existing vertices. A first version added exact copies and
                        // let the weld merge them back, and that broke Birtu (maxEdgeMult=4, invariant FAIL):
                        // the sliver cleanup welds BY POSITION and merged a copy onto a NEARBY vertex, so the
                        // strip's edges became edges the multiplicity guards above had never seen. Reusing the
                        // originals keeps every edge the strip adds in the index space the guards were written
                        // in, and adds no vertex at all.
                        if (StripArea(o, a, b, bestV, bestU) < GatePlateSeamMinAreaM2) continue;
                        AddTriUp(o, a, b, bestV);
                        AddTriUp(o, a, bestV, bestU);
                        usedEdge.Add(keyA);
                        usedEdge.Add(StitchEdgeKey(bestU, bestV));
                        if (forcedRampCells != null)
                        {
                            float mx = (ex0 + ex1 + o.X[bestU] + o.X[bestV]) * 0.25f;
                            float mz = (ez0 + ez1 + o.Z[bestU] + o.Z[bestV]) * 0.25f;
                            forcedRampCells.Add(CellKey(mx, mz, cs));
                        }
                        added += 2;
                        seamCount++;
                        if (bestGap > widest) widest = bestGap;
                        if (bestStep > worstStep) worstStep = bestStep;
                        if (GatePlateTrace && log != null)
                        {
                            log("    seam (" + F(ex0) + "," + F(o.Y[a]) + "," + F(ez0) + ")-("
                                + F(ex1) + "," + F(o.Y[b]) + "," + F(ez1) + ") -> (" + F(o.X[bestU]) + ","
                                + F(o.Y[bestU]) + "," + F(o.Z[bestU]) + ")-(" + F(o.X[bestV]) + ","
                                + F(o.Y[bestV]) + "," + F(o.Z[bestV]) + ") gap=" + F(bestGap) + "m step="
                                + F(bestStep) + "m");
                        }
                        // the strip's own edges are now on the mesh, so a later seam must not reuse them
                        emult[keyA] = 2;
                        emult[StitchEdgeKey(bestU, bestV)] = 2;
                        emult[StitchEdgeKey(a, bestU)] = 1;
                        emult[StitchEdgeKey(b, bestV)] = 1;
                        emult[StitchEdgeKey(a, bestV)] = 2;
                    }
                }
            }
            // Self-check: recount the whole mesh and refuse to have made it non-manifold. The guards above are
            // index-space arithmetic and a mistake in them would cost the whole import (the gate refuses a mesh
            // whose maxEdgeMult > 2), so the cost of proving it here - one linear pass - is worth paying.
            if (added > 0)
            {
                var recount = new Dictionary<long, int>(LongKeyComparer.Instance);
                int nf2 = o.Tris.Count / 3;
                for (int f = 0; f < nf2; f++)
                    for (int k = 0; k < 3; k++)
                    {
                        int i0 = o.Tris[f * 3 + k], i1 = o.Tris[f * 3 + ((k + 1) % 3)];
                        long key = StitchEdgeKey(i0, i1);
                        int n;
                        recount.TryGetValue(key, out n);
                        recount[key] = n + 1;
                    }
                int worst = 0;
                foreach (KeyValuePair<long, int> kv in recount)
                    if (kv.Value > worst) worst = kv.Value;
                if (worst > 2)
                {
                    Log(log, "v2: gate plate seams: REFUSED " + added + " triangle(s) - the recount found an edge "
                        + "shared by " + worst + " faces, so the mesh would not import");
                    return 0;
                }
            }
            return added;
        }

        // a candidate seam: the XZ gap between two segments and the worst height step over their contact
        private static bool SeamCandidate(MergeOutput o, float ax, float ay, float az, float bx, float by,
            float bz, float cx, float cy, float cz, float dx, float dy, float dz, out float gap, out float step)
        {
            gap = SegSegGap(ax, az, bx, bz, cx, cz, dx, dz);
            // the step an agent actually meets is the height difference across the whole contact, so it is
            // bounded by the difference at the closest pair PLUS each edge's own height variation; taking
            // only the closest pair's difference would let a steep foreign edge through.
            float g1 = SegSegGapY(ax, ay, az, bx, by, bz, cx, cy, cz, dx, dy, dz, out float dNear);
            step = dNear;
            float v1 = Math.Abs(ay - by), v2 = Math.Abs(cy - dy);
            if (v1 > step) step = v1;
            if (v2 > step) step = v2;
            return g1 >= 0f;
        }

        // Edge multiplicity over the whole mesh: 1 is a mesh boundary edge, 2 an ordinary interior one.
        // Built once after the plate rotation and then maintained by the footprint and seam stages, so both
        // reason about the same topology as it stands at that moment.
        private static Dictionary<long, int> BuildEdgeMultiplicity(MergeOutput o)
        {
            var emult = new Dictionary<long, int>(LongKeyComparer.Instance);
            int nf = o.Tris.Count / 3;
            for (int f = 0; f < nf; f++)
                for (int k = 0; k < 3; k++)
                {
                    int i0 = o.Tris[f * 3 + k], i1 = o.Tris[f * 3 + ((k + 1) % 3)];
                    long key = StitchEdgeKey(i0, i1);
                    int n;
                    emult.TryGetValue(key, out n);
                    emult[key] = n + 1;
                }
            return emult;
        }

        // GROWS a blended plate's footprint back toward its own source collision, outward across a plate
        // boundary edge, one voxel cell at a time, at the plate's own fitted height. See
        // GatePlateGrowMaxMeters for the measured CC_20 case and the three soup tests that decide each cell.
        private static int GrowPlateFootprint(MergeOutput o, List<PlatePlan> plans, bool[] isPlate,
            float[] soupVerts, int[] soupTris,
            Dictionary<long, List<int>> soupGrid, List<int> soupBig, float soupCell,
            Dictionary<long, List<int>> grid, float gcell, float cs, float ch, float climb,
            float maxSlopeDegrees, int walkableHeightCells, float heightOffsetMeters,
            Dictionary<long, int> emult,
            HashSet<int> grownFaces, Action<string> log, out int strips, out float widest)
        {
            strips = 0; widest = 0f;
            int faceCount = o.Tris.Count / 3;
            if (faceCount < 3 || plans.Count == 0 || soupGrid == null || soupVerts == null) return 0;
            int maxSteps = (int)Math.Floor(GatePlateGrowMaxMeters / cs + 1e-4f);
            if (maxSteps < 1) return 0;
            float maxSlopeTan = (float)Math.Tan(maxSlopeDegrees * Math.PI / 180.0);
            if (maxSlopeTan < 1e-3f) maxSlopeTan = 1e-3f;
            float clearBand = walkableHeightCells * ch;
            int added = 0;
            int growMissTrace = 0;
            var grownCells = new HashSet<long>();
            int voidCells = 0;
            if (GatePlateTrace && log != null)
            {
                for (int qi = 0; qi < plans.Count && qi < 3; qi++)
                {
                    PlatePlan q = plans[qi];
                    float qy = PlaneHeight(q, q.Cx, q.Cz) - heightOffsetMeters;
                    float fy;
                    bool got = SoupFloorUpTo(soupVerts, soupTris, soupGrid, soupBig, soupCell,
                        q.Cx, q.Cz, q.H - heightOffsetMeters + ch + climb, out fy);
                    log("    grow probe plate#" + qi + " c=(" + F(q.Cx) + "," + F(q.Cz) + ") plateH=" + F(q.H)
                        + " soupPlaneY=" + F(qy) + " soupFloorAtCentroid=" + (got ? F(fy) : "NONE")
                        + " offset=" + F(heightOffsetMeters));
                }
            }

            for (int pi = 0; pi < plans.Count && strips < GatePlateGrowMaxCount; pi++)
            {
                PlatePlan p = plans[pi];
                for (int fi = 0; fi < p.Faces.Length && strips < GatePlateGrowMaxCount; fi++)
                {
                    int f = p.Faces[fi];
                    if (f >= faceCount) continue;
                    for (int k = 0; k < 3 && strips < GatePlateGrowMaxCount; k++)
                    {
                        int a = o.Tris[f * 3 + k], b = o.Tris[f * 3 + ((k + 1) % 3)];
                        long keyA = StitchEdgeKey(a, b);
                        if (EdgeMultOf(emult, keyA) != 1) continue;   // not a plate boundary edge

                        // Outward XZ normal, straight from the face's own winding: an up-facing triangle lists
                        // its vertices counter-clockwise seen from above, so the edge a->b has the outside on
                        // its right, which is (-ez, ex)/len. A bbox guess was tried first and it is fragile -
                        // on a concave plate a point just outside the edge can still be inside the bbox, and
                        // the guess then sent the growth INTO the gatehouse. Verified against all three of the
                        // reported gate edges, where this formula and the measured outward agree exactly.
                        float ax = o.X[a], az = o.Z[a], bx = o.X[b], bz = o.Z[b];
                        float ay_ = o.Y[a], by_ = o.Y[b];
                        float ex = bx - ax, ez = bz - az;
                        float elen = (float)Math.Sqrt(ex * ex + ez * ez);
                        if (elen < 1e-4f) continue;
                        float nx = -ez / elen, nz = ex / elen;
                        float mx = (ax + bx) * 0.5f, mz = (az + bz) * 0.5f;

                        // ring 0 is the plate's own edge; rings 1..K are new vertices at the plane height
                        var rings = new List<int[]>();
                        rings.Add(new int[] { a, b });
                        int steps = 0;
                        for (int s = 1; s <= maxSteps; s++)
                        {
                            float d = s * cs;
                            float px = nx * d, pz = nz * d;
                            float y0 = PlaneHeight(p, ax + px, az + pz);
                            float y1 = PlaneHeight(p, bx + px, bz + pz);
                            float ym = PlaneHeight(p, mx + px, mz + pz);
                            // the ring must stay walkable: the STRIP's own slope, i.e. the plane's gradient
                            // ACROSS the strip, which is what an agent walks. Comparing the height difference
                            // ALONG the edge instead (measured: |y1-y0| = 0.36 m over a 1.58 m edge against a
                            // 0.30 m bound) refuses every long edge - the plate's own 13 deg tilt, which is
                            // already inside the cap and legal on the plate itself.
                            if (Math.Abs(ym - PlaneHeight(p, mx, mz)) > cs * maxSlopeTan + 1e-4f) break;
                            if (!GrowRingOk(o, ax, ay_, az, bx, by_, bz, px, pz, p, heightOffsetMeters,
                                    soupVerts, soupTris, soupGrid, soupBig, soupCell, grid, gcell,
                                    faceCount, climb, ch, clearBand, grownCells,
                                    GatePlateGrowVoidMaxSteps - voidCells,
                                    out int ringVoid)) break;
                            voidCells += ringVoid;
                            int q0 = o.X.Count;
                            o.X.Add(ax + px); o.Y.Add(y0); o.Z.Add(az + pz);
                            o.X.Add(bx + px); o.Y.Add(y1); o.Z.Add(bz + pz);
                            rings.Add(new int[] { q0, q0 + 1 });
                            grownCells.Add(CellKey(ax + px, az + pz, cs));
                            grownCells.Add(CellKey(bx + px, bz + pz, cs));
                            grownCells.Add(CellKey(mx + px, mz + pz, cs));
                            steps = s;
                        }
                        if (steps == 0)
                        {
                            if (GatePlateTrace && log != null && growMissTrace < 40)
                            {
                                growMissTrace++;
                                float px0 = nx * cs, pz0 = nz * cs;
                                log("    grow miss (" + F(ax) + "," + F(az) + ")-(" + F(bx) + "," + F(bz)
                                    + ") out (" + F(nx) + "," + F(nz) + ") h=" + F(p.H)
                                    + GrowWhy(o, ax + px0, az + pz0, PlaneHeight(p, ax + px0, az + pz0) - heightOffsetMeters,
                                        p.H - heightOffsetMeters,
                                        soupVerts, soupTris, soupGrid, soupBig, soupCell, grid, gcell,
                                        faceCount, climb, ch, clearBand)
                                    + GrowWhy(o, bx + px0, bz + pz0, PlaneHeight(p, bx + px0, bz + pz0) - heightOffsetMeters,
                                        p.H - heightOffsetMeters,
                                        soupVerts, soupTris, soupGrid, soupBig, soupCell, grid, gcell,
                                        faceCount, climb, ch, clearBand)
                                    + GrowWhy(o, mx + px0, mz + pz0, PlaneHeight(p, mx + px0, mz + pz0) - heightOffsetMeters,
                                        p.H - heightOffsetMeters,
                                        soupVerts, soupTris, soupGrid, soupBig, soupCell, grid, gcell,
                                        faceCount, climb, ch, clearBand));
                            }
                            continue;
                        }

                        // emit ring by ring; ring 0 reuses the plate's own edge so no plate edge is disturbed.
                        // (inner0,inner1) goes 1 -> 2 (it is now shared with the strip), the diagonal
                        // (inner0,outer1) is added by BOTH triangles so it must land on 2, and the two side
                        // edges plus the strip's new outer edge land on 1 - the last one is what the seam stage
                        // then welds to whatever the plate has to meet.
                        for (int s = 0; s < steps; s++)
                        {
                            int[] inner = rings[s], outer = rings[s + 1];
                            if (EdgeMultOf(emult, StitchEdgeKey(inner[0], inner[1])) != 1) break;
                            if (EdgeMultOf(emult, StitchEdgeKey(outer[0], outer[1])) != 0) break;
                            if (StripArea(o, inner[0], inner[1], outer[1], outer[0]) < GatePlateSeamMinAreaM2)
                                break;
                            AddTriUp(o, inner[0], inner[1], outer[1]);
                            AddTriUp(o, inner[0], outer[1], outer[0]);
                            grownFaces.Add(o.Tris.Count / 3 - 2);
                            grownFaces.Add(o.Tris.Count / 3 - 1);
                            added += 2;
                            emult[StitchEdgeKey(inner[0], inner[1])] = 2;
                            emult[StitchEdgeKey(inner[1], outer[1])] = 1;
                            emult[StitchEdgeKey(inner[0], outer[1])] = 2;
                            emult[StitchEdgeKey(outer[1], outer[0])] = 1;
                            emult[StitchEdgeKey(outer[0], inner[0])] = 1;
                        }
                        strips++;
                        float w = steps * cs;
                        if (w > widest) widest = w;
                        if (GatePlateTrace && log != null)
                        {
                            log("    grow edge (" + F(ax) + "," + F(o.Y[a]) + "," + F(az) + ")-(" + F(bx) + ","
                                + F(o.Y[b]) + "," + F(bz) + ") outward (" + F(nx) + "," + F(nz) + ") by "
                                + F(w) + "m (" + steps + " cells)");
                            // why it stopped there, at the three fixed sample points of the next ring
                            float qx = nx * cs * (steps + 1), qz = nz * cs * (steps + 1);
                            if (steps < maxSteps)
                            {
                                log("      stopped at cell " + (steps + 1)
                                    + GrowWhy(o, ax + qx, az + qz, PlaneHeight(p, ax + qx, az + qz),
                                        p.H - heightOffsetMeters, soupVerts, soupTris, soupGrid, soupBig,
                                        soupCell, grid, gcell, faceCount, climb, ch, clearBand)
                                    + GrowWhy(o, bx + qx, bz + qz, PlaneHeight(p, bx + qx, bz + qz),
                                        p.H - heightOffsetMeters, soupVerts, soupTris, soupGrid, soupBig,
                                        soupCell, grid, gcell, faceCount, climb, ch, clearBand)
                                    + GrowWhy(o, mx + qx, mz + qz, PlaneHeight(p, mx + qx, mz + qz),
                                        p.H - heightOffsetMeters, soupVerts, soupTris, soupGrid, soupBig,
                                        soupCell, grid, gcell, faceCount, climb, ch, clearBand));
                            }
                        }
                    }
                }
            }
            return added;
        }

        private static float PlaneHeight(PlatePlan p, float x, float z)
        {
            return (float)(p.A0 + p.Off + p.GradB * (x - p.Cx) + p.GradC * (z - p.Cz));
        }

        // One growth ring: every sample along the edge, pushed out by (px,pz), must pass GrowCellOk. Sampled
        // at the voxel cell size along the edge rather than at three fixed points, because a threshold lip
        // usually exists across only part of an edge - the first version's three points let a strip grow over
        // the part of an edge where the prop's floor had already ended.
        //
        // On success EVERY sample's cell is claimed, not just the three the first version recorded. Two plate
        // edges that grow outward from a shared corner meet in that corner, and recording only three cells let
        // both of them grow into it: measured 4 self-overlapping pairs on Basamu and 1 on Birtu, all of which
        // disappeared once the whole ring claimed its cells.
        private static bool GrowRingOk(MergeOutput o, float ax, float ay, float az, float bx, float by,
            float bz, float px, float pz, PlatePlan p, float heightOffsetMeters,
            float[] soupVerts, int[] soupTris,
            Dictionary<long, List<int>> soupGrid, List<int> soupBig, float soupCell,
            Dictionary<long, List<int>> grid, float gcell, int faceCount, float climb, float ch,
            float clearBand, HashSet<long> grownCells, int voidBudget, out int ringVoid)
        {
            ringVoid = 0;
            float ex = bx - ax, ez = bz - az;
            float elen = (float)Math.Sqrt(ex * ex + ez * ez);
            float step = ch > 0f ? ch : 0.25f;
            int n = (int)Math.Ceiling(elen / step);
            if (n < 1) n = 1;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                float sx = ax + ex * t + px, sz = az + ez * t + pz;
                float planeY = PlaneHeight(p, sx, sz);
                bool crossed;
                if (!GrowCellOk(o, sx, sz, planeY, p.H - heightOffsetMeters, soupVerts, soupTris,
                        soupGrid, soupBig, soupCell, grid, gcell, faceCount, climb, ch,
                        clearBand, grownCells, out crossed)) return false;
                if (crossed) ringVoid++;
            }
            if (ringVoid > voidBudget) return false;
            for (int i = 0; i <= n; i++)
            {
                float t = i / (float)n;
                grownCells.Add(CellKey(ax + ex * t + px, az + ez * t + pz, step));
            }
            _ = ay; _ = by;
            return true;
        }

        // One grown cell, accepted by EITHER of two routes.
        //
        // ROUTE A - the prop's own floor is still there: FLOOR (an up-facing soup surface at the cell, at or
        // below the plate's PRE-BLEND level plus ch + climb), CLEARANCE (nothing in the soup within
        // walkableHeight above that floor) and BARE (the built mesh does not already cover the cell within one
        // climb of the plane). Each of the three exists because its absence produced a wrong answer: without
        // the floor test the plate grows into thin air, without the clearance test it grows under the gate's own
        // wall, and without the bare test it paints over the neighbour it was meant to meet.
        //
        // ROUTE B - CROSSING THE VOID. Route A cannot fill the gap at the reported gate, and refused it
        // correctly: the threshold slab's own floor ENDS at x = 1133.37. `--soup 1133.0,990.8,1134.2,991.6,83,88`
        // returns 8 triangles and every one is VERTICAL - the colliders' end faces at 84.97-85.11 - so the
        // columns across the gap have no horizontal soup surface at all.
        //
        // An earlier round mis-measured that gap and reported a 1.1 m step, which is what stopped this pass
        // being finished. The number was a frame error: it subtracted a MESH height (the plate tip, 85.304) from
        // a SOUP height (the terrain under the lip, 84.19). Both sides of the crossing in ONE frame - the
        // final emitted mesh, which is what the engine imports - are:
        //
        //     near side, the growth's outer vertex  v144038 (1133.195, 85.270, 991.344)
        //     far  side, the boundary vertex      v64009  (1133.750, 85.100, 991.250)
        //
        // so the drop is **0.170 m**, comfortably inside the 0.6 m climb, and the only thing wrong is the
        // **0.555 m XZ void** between them. Recast links only TOUCHING footprints, so the engine sees two
        // surfaces 0.17 m apart that it refuses to join, and an agent cannot step over the hole.
        //
        // Route B fills that band, and only where all of these hold:
        //   - no soup up-facing floor at the cell within the plate's own level band (it is a void, not a floor
        //     being refused);
        //   - nothing in the soup within walkableHeight ABOVE the plane, so there is no wall or ceiling to grow
        //     into - the clearance test, asked of the raw soup because that is all there is;
        //   - walkable built mesh within GatePlateGrowVoidReachMeters of the cell AND within one climb of the
        //     plane. That is what makes it a CONNECTION rather than an invention: there has to be something to
        //     connect to, close by, at a step an agent can take.
        // At most GatePlateGrowVoidMaxSteps cells may be crossed per edge, so the growth closes a gap and does
        // not stroll across a missing prop. The strip is flat and one cell wide, so it stays inside the plate's
        // own boundary - it extends the plate's edge outward by at most one voxel cell per step, exactly as
        // Route A does.
        //
        // The floor is looked for at the plate's PRE-BLEND level p.H, not at the fitted plane. That is the whole
        // point: the blend deliberately lowers the plate by up to one climb plus its tilt, so at the gate the
        // plate sits at 85.5 while the slab's own top is still at 86.3. Anchoring the search to the plane
        // (planeY + climb) excluded the prop's own floor and refused every cell - measured: 0 strips.
        // Asking instead for the highest up-facing soup surface within p.H +- (ch + climb) finds the slab's own
        // top layer; the five overlapping collider copies are then irrelevant, because only the topmost one is
        // returned and the clearance band starts above it.
        private static bool GrowCellOk(MergeOutput o, float x, float z, float planeY, float plateH,
            float[] soupVerts, int[] soupTris,
            Dictionary<long, List<int>> soupGrid, List<int> soupBig, float soupCell,
            Dictionary<long, List<int>> grid, float gcell, int faceCount, float climb, float ch,
            float clearBand, HashSet<long> grownCells, out bool crossedVoid)
        {
            crossedVoid = false;
            if (grownCells.Contains(CellKey(x, z, ch))) return false;

            // BARE: the built mesh must not already be at THIS level there.
            //
            // The band is one cell height, NOT the agent climb, and that is the whole fix. With the climb band
            // the far-side face across the gate void - 0.18 m below the plate's plane - counted as "already
            // covered", so the growth refused every cell of the void and the plate never reached it: measured
            // 1 cell grown and a 0.65 m bare band still open at z = 991.25. A face a fifth of a metre BELOW the
            // plane is a separate step, not the same walking surface, and filling the space between them is the
            // entire point. One `ch` is "same surface"; the climb belongs to Route B's reach test, where the
            // question really is "can an agent step to that".
            if (MeshCovers(o, grid, gcell, faceCount, x, z, planeY, ch)) return false;

            // ROUTE A: FLOOR + CLEARANCE, straight from the raw soup.
            //
            // The floor is compared with the PLANE (in soup space), not with the plate's pre-blend level. That
            // distinction is the whole gate gap: the plate's pre-blend level is 86.3 in mesh space (85.8 in soup
            // space), the terrain under the threshold lip measures 84.2-84.6 in soup space, and the PLANE - what
            // the strip is actually placed at - is ~84.8 in soup space. So that ground is 0.2-0.6 m below the
            // plane, i.e. inside the climb, and comparing it against the pre-blend level instead (measured
            // `[floorLow@84.205]` in the trace) refused the lip as "not the plate's floor" - the same frame
            // mistake that produced the 1.1 m step, one level down. A floor within one climb of where the strip
            // is going IS a floor the strip may bridge to.
            float floorY;
            if (SoupFloorUpTo(soupVerts, soupTris, soupGrid, soupBig, soupCell,
                    x, z, planeY + climb, out floorY)
                && floorY >= planeY - climb)
            {
                if (SoupObstructs(soupVerts, soupTris, soupGrid, soupBig, soupCell,
                        x, z, floorY + ch * 0.5f, floorY + clearBand)) return false;
                return true;
            }
            _ = plateH;

            // ROUTE B: no usable floor at all - a true void, with open clearance above the plane and walkable
            // built mesh close enough and near enough in height to be the thing we are connecting to.
            if (SoupObstructs(soupVerts, soupTris, soupGrid, soupBig, soupCell,
                    x, z, planeY - ch * 0.5f, planeY + clearBand)) return false;
            if (!MeshSurfaceNear(o, grid, gcell, faceCount, x, z, planeY, climb,
                    GatePlateGrowVoidReachMeters)) return false;
            crossedVoid = true;
            return true;
        }

        // Is there walkable built mesh within `reach` of (x,z) in XZ whose height there is within `climb` of
        // planeY? "Height there" is that face's own surface height at the closest point of its footprint, i.e.
        // what an agent would stand on at the crossing.
        private static bool MeshSurfaceNear(MergeOutput o, Dictionary<long, List<int>> grid, float gcell,
            int faceCount, float x, float z, float planeY, float climb, float reach)
        {
            int cx = (int)Math.Floor(x / gcell), cz = (int)Math.Floor(z / gcell);
            for (int i = cx - 1; i <= cx + 1; i++)
                for (int j = cz - 1; j <= cz + 1; j++)
                {
                    List<int> lst;
                    if (!grid.TryGetValue(unchecked(((long)i << 32) | (uint)j), out lst)) continue;
                    for (int k = 0; k < lst.Count; k++)
                    {
                        int f = lst[k];
                        if (f >= faceCount) continue;
                        int p0 = o.Tris[f * 3], p1 = o.Tris[f * 3 + 1], p2 = o.Tris[f * 3 + 2];
                        float d2, hy;
                        MeshFaceDistToPoint(o, p0, p1, p2, x, z, out d2, out hy);
                        if (d2 > reach * reach) continue;
                        if (Math.Abs(hy - planeY) <= climb) return true;
                    }
                }
            return false;
        }

        // squared XZ distance from (x,z) to a face's footprint, and the face's surface height at the closest
        // point. Zero when the point is inside the footprint.
        private static void MeshFaceDistToPoint(MergeOutput o, int p0, int p1, int p2, float x, float z,
            out float dist2, out float hy)
        {
            double ax = o.X[p0], az = o.Z[p0], ay = o.Y[p0];
            double bx = o.X[p1], bz = o.Z[p1], by = o.Y[p1];
            double cx = o.X[p2], cz = o.Z[p2], cy = o.Y[p2];
            double den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            if (Math.Abs(den) > 1e-12)
            {
                double wa = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / den;
                double wb = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / den;
                double wc = 1.0 - wa - wb;
                if (wa >= 0.0 && wb >= 0.0 && wc >= 0.0)
                {
                    dist2 = 0f;
                    hy = (float)(wa * ay + wb * by + wc * cy);
                    return;
                }
            }
            float best = float.MaxValue, bestY = 0f;
            SegPtNearest(o, p0, p1, x, z, ref best, ref bestY);
            SegPtNearest(o, p1, p2, x, z, ref best, ref bestY);
            SegPtNearest(o, p2, p0, x, z, ref best, ref bestY);
            dist2 = best;
            hy = bestY;
        }

        private static void SegPtNearest(MergeOutput o, int ia, int ib, float x, float z,
            ref float best, ref float bestY)
        {
            float dx = o.X[ib] - o.X[ia], dz = o.Z[ib] - o.Z[ia];
            float len = dx * dx + dz * dz;
            float t = len < 1e-12f ? 0f : ((x - o.X[ia]) * dx + (z - o.Z[ia]) * dz) / len;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            float ex = o.X[ia] + t * dx - x, ez = o.Z[ia] + t * dz - z;
            float d2 = ex * ex + ez * ez;
            if (d2 >= best) return;
            best = d2;
            bestY = o.Y[ia] + t * (o.Y[ib] - o.Y[ia]);
        }

        // Harness-only: why one grown cell was refused, as a compact tag triple for the trace line.
        private static string GrowWhy(MergeOutput o, float x, float z, float planeY, float plateH,
            float[] soupVerts, int[] soupTris,
            Dictionary<long, List<int>> soupGrid, List<int> soupBig, float soupCell,
            Dictionary<long, List<int>> grid, float gcell, int faceCount, float climb, float ch,
            float clearBand)
        {
            if (MeshCovers(o, grid, gcell, faceCount, x, z, planeY, ch)) return " [covered]";
            float slack = ch + climb;
            float fy;
            bool has = SoupFloorUpTo(soupVerts, soupTris, soupGrid, soupBig, soupCell, x, z,
                plateH + slack, out fy);
            if (has && fy >= plateH - slack)
                return SoupObstructs(soupVerts, soupTris, soupGrid, soupBig, soupCell, x, z,
                    fy + ch * 0.5f, fy + clearBand) ? " [blocked@" + F(fy) + "]" : " [okA@" + F(fy) + "]";
            if (has) return " [floorLow@" + F(fy) + "]";
            // Route B
            if (SoupObstructs(soupVerts, soupTris, soupGrid, soupBig, soupCell, x, z,
                    planeY - ch * 0.5f, planeY + clearBand)) return " [blockedAbove]";
            float d2, hy;
            float best = float.MaxValue;
            int cx = (int)Math.Floor(x / gcell), cz = (int)Math.Floor(z / gcell);
            for (int i = cx - 1; i <= cx + 1; i++)
                for (int j = cz - 1; j <= cz + 1; j++)
                {
                    List<int> lst;
                    if (!grid.TryGetValue(unchecked(((long)i << 32) | (uint)j), out lst)) continue;
                    for (int k = 0; k < lst.Count; k++)
                    {
                        int f = lst[k];
                        if (f >= faceCount) continue;
                        MeshFaceDistToPoint(o, o.Tris[f * 3], o.Tris[f * 3 + 1], o.Tris[f * 3 + 2],
                            x, z, out d2, out hy);
                        if (d2 < best) best = d2;
                    }
                }
            if (best > GatePlateGrowVoidReachMeters * GatePlateGrowVoidReachMeters)
                return " [noMeshWithin" + F((float)Math.Sqrt(best)) + "m]";
            return " [okB@d" + F((float)Math.Sqrt(best)) + "]";
        }

        private static bool MeshCovers(MergeOutput o, Dictionary<long, List<int>> grid, float gcell,
            int faceCount, float x, float z, float planeY, float climb)
        {
            int cx = (int)Math.Floor(x / gcell), cz = (int)Math.Floor(z / gcell);
            for (int i = cx - 1; i <= cx + 1; i++)
                for (int j = cz - 1; j <= cz + 1; j++)
                {
                    List<int> lst;
                    if (!grid.TryGetValue(unchecked(((long)i << 32) | (uint)j), out lst)) continue;
                    for (int k = 0; k < lst.Count; k++)
                    {
                        int f = lst[k];
                        if (f >= faceCount) continue;
                        double wa, wb, wc;
                        if (!BaryInFace(o, o.Tris[f * 3], o.Tris[f * 3 + 1], o.Tris[f * 3 + 2], x, z,
                                out wa, out wb, out wc)) continue;
                        double hy = wa * o.Y[o.Tris[f * 3]] + wb * o.Y[o.Tris[f * 3 + 1]]
                            + wc * o.Y[o.Tris[f * 3 + 2]];
                        if (Math.Abs(hy - planeY) <= climb) return true;
                    }
                }
            return false;
        }

        private static bool BaryInFace(MergeOutput o, int p0, int p1, int p2, float x, float z,            out double wa, out double wb, out double wc)
        {
            double ax = o.X[p0], az = o.Z[p0], bx = o.X[p1], bz = o.Z[p1], cx = o.X[p2], cz = o.Z[p2];
            double den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            wa = 0.0; wb = 0.0; wc = 0.0;
            if (Math.Abs(den) < 1e-12) return false;
            wa = ((bz - cz) * (x - cx) + (cx - bx) * (z - cz)) / den;
            wb = ((cz - az) * (x - cx) + (ax - cx) * (z - cz)) / den;
            wc = 1.0 - wa - wb;
            // a small negative tolerance so a sample landing exactly on a shared edge still counts
            return wa >= -1e-3 && wb >= -1e-3 && wc >= -1e-3;
        }

        // highest up-facing soup surface at (x,z) at or below yMax. "Up-facing" with a 0.05 normal floor, and
        // only triangles that really contain the point - the coarse soup grid is a bbox index, not a query.
        private static bool SoupFloorUpTo(float[] v, int[] tris,
            Dictionary<long, List<int>> grid, List<int> bigList, float cellSize,
            float x, float z, float yMax, out float y)
        {
            y = float.MinValue;
            bool found = false;
            int cx = (int)Math.Floor(x / cellSize), cz = (int)Math.Floor(z / cellSize);
            for (int i = cx - 1; i <= cx + 1; i++)
                for (int j = cz - 1; j <= cz + 1; j++)
                {
                    List<int> lst;
                    if (!grid.TryGetValue(SoupCellKey(i, j), out lst)) continue;
                    for (int n = 0; n < lst.Count; n++)
                        if (SoupFloorIn(v, tris, lst[n], x, z, yMax, ref y)) found = true;
                }
            for (int n = 0; n < bigList.Count; n++)
                if (SoupFloorIn(v, tris, bigList[n], x, z, yMax, ref y)) found = true;
            return found;
        }

        private static bool SoupFloorIn(float[] v, int[] tris, int t, float x, float z, float yMax, ref float best)
        {
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            double minY = Math.Min(v[a * 3 + 1], Math.Min(v[b * 3 + 1], v[c * 3 + 1]));
            if (minY > yMax) return false;                        // the whole triangle is above the ceiling
            double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
            double wx = v[c * 3] - v[a * 3], wy = v[c * 3 + 1] - v[a * 3 + 1], wz = v[c * 3 + 2] - v[a * 3 + 2];
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len < 1e-12) return false;
            if (ny / len < 0.05) return false;                     // not up-facing: a floor, not a ceiling
            double den = (v[b * 3 + 2] - v[c * 3 + 2]) * (v[a * 3] - v[c * 3])
                + (v[c * 3] - v[b * 3]) * (v[a * 3 + 2] - v[c * 3 + 2]);
            if (Math.Abs(den) < 1e-12) return false;
            double wa = ((v[b * 3 + 2] - v[c * 3 + 2]) * (x - v[c * 3])
                + (v[c * 3] - v[b * 3]) * (z - v[c * 3 + 2])) / den;
            double wb = ((v[c * 3 + 2] - v[a * 3 + 2]) * (x - v[c * 3])
                + (v[a * 3] - v[c * 3]) * (z - v[c * 3 + 2])) / den;
            double wc = 1.0 - wa - wb;
            if (wa < -1e-3 || wb < -1e-3 || wc < -1e-3) return false;
            double hy = wa * v[a * 3 + 1] + wb * v[b * 3 + 1] + wc * v[c * 3 + 1];
            if (hy > yMax) return false;
            if (hy > best) best = (float)hy;
            return true;
        }

        // any soup triangle covering (x,z) that spans the vertical band [yLo, yHi] - Recast's own clearance
        // question, asked of the raw soup because the heightfield that decided it no longer exists here
        private static bool SoupObstructs(float[] v, int[] tris,
            Dictionary<long, List<int>> grid, List<int> bigList, float cellSize,
            float x, float z, float yLo, float yHi)
        {
            int cx = (int)Math.Floor(x / cellSize), cz = (int)Math.Floor(z / cellSize);
            for (int i = cx - 1; i <= cx + 1; i++)
                for (int j = cz - 1; j <= cz + 1; j++)
                {
                    List<int> lst;
                    if (!grid.TryGetValue(SoupCellKey(i, j), out lst)) continue;
                    for (int n = 0; n < lst.Count; n++)
                        if (SoupBlocks(v, tris, lst[n], x, z, yLo, yHi)) return true;
                }
            for (int n = 0; n < bigList.Count; n++)
                if (SoupBlocks(v, tris, bigList[n], x, z, yLo, yHi)) return true;
            return false;
        }

        private static bool SoupBlocks(float[] v, int[] tris, int t, float x, float z, float yLo, float yHi)
        {
            int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
            double minY = Math.Min(v[a * 3 + 1], Math.Min(v[b * 3 + 1], v[c * 3 + 1]));
            double maxY = Math.Max(v[a * 3 + 1], Math.Max(v[b * 3 + 1], v[c * 3 + 1]));
            if (maxY < yLo || minY > yHi) return false;
            double den = (v[b * 3 + 2] - v[c * 3 + 2]) * (v[a * 3] - v[c * 3])
                + (v[c * 3] - v[b * 3]) * (v[a * 3 + 2] - v[c * 3 + 2]);
            if (Math.Abs(den) < 1e-12) return false;
            double wa = ((v[b * 3 + 2] - v[c * 3 + 2]) * (x - v[c * 3])
                + (v[c * 3] - v[b * 3]) * (z - v[c * 3 + 2])) / den;
            double wb = ((v[c * 3 + 2] - v[a * 3 + 2]) * (x - v[c * 3])
                + (v[a * 3] - v[c * 3]) * (z - v[c * 3 + 2])) / den;
            double wc = 1.0 - wa - wb;
            return wa >= -1e-3 && wb >= -1e-3 && wc >= -1e-3;
        }

        private static int EdgeMultOf(Dictionary<long, int> emult, long key)
        {
            int n;
            return emult.TryGetValue(key, out n) ? n : 0;
        }

        // True when nothing in the mesh OVERLAPS the strip. Three tests, and together they are complete for a
        // simple quad against a triangle: a foreign triangle intersects the quad only if one of its edges
        // properly crosses one of the quad's four boundary edges, or one of its vertices lies strictly inside
        // the quad, or the quad lies wholly inside the triangle (caught by the two centroid samples).
        //
        // Two weaker versions came first, and both were measured wrong. Four fixed sample points cleared every
        // strip on Basamu and still left 11 self-overlapping pairs, because a 0.2 m face can sit between two
        // samples of a strip a metre wide. A full cs lattice over the footprint did find them, but it also
        // rejected 70 of CC_20's 71 legitimate seams - a face continuing right up to one of the strip's own
        // welded edges is exactly what a seam is for, and the lattice could not tell that from an intrusion.
        private static bool StripIsClear(MergeOutput o, int a, int b, int u, int v,
            Dictionary<long, List<int>> grid, float gcell, int faceCount, float climb, float ch)
        {
            float band = climb + ch;
            float ax = o.X[a], az = o.Z[a], ay = o.Y[a];
            float bx = o.X[b], bz = o.Z[b], by = o.Y[b];
            float ux = o.X[u], uz = o.Z[u], uy = o.Y[u];
            float vx = o.X[v], vz = o.Z[v], vy = o.Y[v];
            float minX = Math.Min(Math.Min(ax, bx), Math.Min(ux, vx));
            float maxX = Math.Max(Math.Max(ax, bx), Math.Max(ux, vx));
            float minZ = Math.Min(Math.Min(az, bz), Math.Min(uz, vz));
            float maxZ = Math.Max(Math.Max(az, bz), Math.Max(uz, vz));

            int cx0 = (int)Math.Floor(minX / gcell) - 1, cx1 = (int)Math.Floor(maxX / gcell) + 1;
            int cz0 = (int)Math.Floor(minZ / gcell) - 1, cz1 = (int)Math.Floor(maxZ / gcell) + 1;
            for (int i = cx0; i <= cx1; i++)
                for (int j = cz0; j <= cz1; j++)
                {
                    List<int> lst;
                    if (!grid.TryGetValue(unchecked(((long)i << 32) | (uint)j), out lst)) continue;
                    for (int k = 0; k < lst.Count; k++)
                    {
                        int f = lst[k];
                        if (f >= faceCount) continue;
                        int f0 = o.Tris[f * 3], f1 = o.Tris[f * 3 + 1], f2 = o.Tris[f * 3 + 2];
                        // 1. a foreign vertex strictly inside the quad
                        for (int t = 0; t < 3; t++)
                        {
                            int q = t == 0 ? f0 : (t == 1 ? f1 : f2);
                            if (q == a || q == b || q == u || q == v) continue;
                            if (PointInStrip(o.X[q], o.Z[q], ax, az, bx, bz, ux, uz, vx, vz)) return false;
                        }
                        // 2. a foreign edge properly crossing one of the quad's four boundary edges
                        for (int t = 0; t < 3; t++)
                        {
                            int q0 = t == 0 ? f0 : (t == 1 ? f1 : f2);
                            int q1 = t == 0 ? f1 : (t == 1 ? f2 : f0);
                            if (Cross2D(o.X[q0], o.Z[q0], o.X[q1], o.Z[q1], ax, az, bx, bz)) return false;
                            if (Cross2D(o.X[q0], o.Z[q0], o.X[q1], o.Z[q1], bx, bz, vx, vz)) return false;
                            if (Cross2D(o.X[q0], o.Z[q0], o.X[q1], o.Z[q1], vx, vz, ux, uz)) return false;
                            if (Cross2D(o.X[q0], o.Z[q0], o.X[q1], o.Z[q1], ux, uz, ax, az)) return false;
                        }
                    }
                }
            // 3. the quad wholly inside a bigger face
            if (!StripPointClear(o, a, b, v, grid, gcell, faceCount, band)) return false;
            if (!StripPointClear(o, a, v, u, grid, gcell, faceCount, band)) return false;
            return true;
        }

        // a proper interior crossing of two XZ segments; a shared endpoint or a mere touch does not count,
        // because the strip is SUPPOSED to share its ends with the two faces it welds to
        private static bool Cross2D(float ax, float az, float bx, float bz, float cx, float cz, float dx, float dz)
        {
            double d1 = Cross2(ax, az, bx, bz, cx, cz);
            double d2 = Cross2(ax, az, bx, bz, dx, dz);
            double d3 = Cross2(cx, cz, dx, dz, ax, az);
            double d4 = Cross2(cx, cz, dx, dz, bx, bz);
            const double e = 1e-6;
            return ((d1 > e && d2 < -e) || (d1 < -e && d2 > e))
                && ((d3 > e && d4 < -e) || (d3 < -e && d4 > e));
        }

        // inside the strip's XZ footprint, inset by a twentieth of its size so the lattice does not spend its
        // samples on the boundary, where the strip legitimately meets what it is welded to
        private static bool PointInStrip(float px, float pz,
            float ax, float az, float bx, float bz, float ux, float uz, float vx, float vz)
        {
            float cx = (ax + bx + ux + vx) * 0.25f, cz = (az + bz + uz + vz) * 0.25f;
            float hx = (Math.Max(Math.Max(ax, bx), Math.Max(ux, vx))
                - Math.Min(Math.Min(ax, bx), Math.Min(ux, vx))) * 0.5f;
            float hz = (Math.Max(Math.Max(az, bz), Math.Max(uz, vz))
                - Math.Min(Math.Min(az, bz), Math.Min(uz, vz))) * 0.5f;
            float inx = hx * 0.95f, inz = hz * 0.95f;
            if (inx < 1e-3f) inx = 1e-3f;
            if (inz < 1e-3f) inz = 1e-3f;
            if (px < cx - inx || px > cx + inx || pz < cz - inz || pz > cz + inz) return false;
            return InQuad(px, pz, ax, az, bx, bz, ux, uz, vx, vz);
        }

        private static bool InQuad(float px, float pz, float ax, float az, float bx, float bz,
            float ux, float uz, float vx, float vz)
        {
            // two triangles, a-b-v and a-v-u
            if (InTri(px, pz, ax, az, bx, bz, vx, vz)) return true;
            return InTri(px, pz, ax, az, vx, vz, ux, uz);
        }

        private static bool InTri(float px, float pz, float ax, float az, float bx, float bz, float cx, float cz)
        {
            double d1 = (bx - ax) * (pz - az) - (bz - az) * (px - ax);
            double d2 = (cx - bx) * (pz - bz) - (cz - bz) * (px - bx);
            double d3 = (ax - cx) * (pz - cz) - (az - cz) * (px - cx);
            bool neg = d1 < -1e-6 || d2 < -1e-6 || d3 < -1e-6;
            bool pos = d1 > 1e-6 || d2 > 1e-6 || d3 > 1e-6;
            return !(neg && pos);
        }

        private static float StripHeight(float px, float pz,
            float ax, float ay, float az, float bx, float by, float bz,
            float ux, float uy, float uz, float vx, float vy, float vz)
        {
            // split on the a-v diagonal, then barycentric inside whichever half the point is in
            double wa, wb, wc;
            if (InTriStrict(px, pz, ax, az, bx, bz, vx, vz))
            {
                if (!Bary2(px, pz, ax, az, bx, bz, vx, vz, out wa, out wb, out wc)) return ay;
                return (float)(wa * ay + wb * by + wc * vy);
            }
            if (!Bary2(px, pz, ax, az, vx, vz, ux, uz, out wa, out wb, out wc)) return ay;
            return (float)(wa * ay + wb * vy + wc * uy);
        }

        private static bool InTriStrict(float px, float pz, float ax, float az, float bx, float bz,
            float cx, float cz)
        {
            double d1 = (bx - ax) * (pz - az) - (bz - az) * (px - ax);
            double d2 = (cx - bx) * (pz - bz) - (cz - bz) * (px - bx);
            double d3 = (ax - cx) * (pz - cz) - (az - cz) * (px - cx);
            return (d1 >= 0.0 && d2 >= 0.0 && d3 >= 0.0) || (d1 <= 0.0 && d2 <= 0.0 && d3 <= 0.0);
        }

        private static bool Bary2(float px, float pz, float ax, float az, float bx, float bz,
            float cx, float cz, out double wa, out double wb, out double wc)
        {
            double den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
            wa = 0.0; wb = 0.0; wc = 0.0;
            if (Math.Abs(den) < 1e-12) return false;
            wa = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / den;
            wb = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / den;
            wc = 1.0 - wa - wb;
            return true;
        }

        private static bool StripPointClear(MergeOutput o, int a, int b, int v,
            Dictionary<long, List<int>> grid, float gcell, int faceCount, float band)
        {
            float x = (o.X[a] + o.X[b] + o.X[v]) / 3f;
            float y = (o.Y[a] + o.Y[b] + o.Y[v]) / 3f;
            float z = (o.Z[a] + o.Z[b] + o.Z[v]) / 3f;
            return StripSampleClear(o, x, y, z, grid, gcell, faceCount, band);
        }

        private static bool StripSampleClear(MergeOutput o, float px, float py, float pz,
            Dictionary<long, List<int>> grid, float gcell, int faceCount, float band)
        {
            int gx = (int)Math.Floor(px / gcell), gz = (int)Math.Floor(pz / gcell);
            for (int i = gx - 1; i <= gx + 1; i++)
                for (int j = gz - 1; j <= gz + 1; j++)
                {
                    List<int> lst;
                    if (!grid.TryGetValue(unchecked(((long)i << 32) | (uint)j), out lst)) continue;
                    for (int k = 0; k < lst.Count; k++)
                    {
                        int f = lst[k];
                        if (f >= faceCount) continue;
                        int p0 = o.Tris[f * 3], p1 = o.Tris[f * 3 + 1], p2 = o.Tris[f * 3 + 2];
                        double ax = o.X[p0], az = o.Z[p0];
                        double bx = o.X[p1], bz = o.Z[p1];
                        double cx = o.X[p2], cz = o.Z[p2];
                        double den = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
                        if (Math.Abs(den) < 1e-12) continue;
                        double wa = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / den;
                        double wb = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / den;
                        double wc = 1.0 - wa - wb;
                        if (wa < -1e-3 || wb < -1e-3 || wc < -1e-3) continue;
                        double hy = wa * o.Y[p0] + wb * o.Y[p1] + wc * o.Y[p2];
                        if (Math.Abs(hy - py) <= band) return false;
                    }
                }
            return true;
        }

        // closest XZ distance between two 2D segments
        private static float SegSegGap(float ax, float az, float bx, float bz, float cx, float cz,
            float dx, float dz)
        {
            SegSegClosest(ax, az, bx, bz, cx, cz, dx, dz, out float s, out float t);
            float px = ax + s * (bx - ax), pz = az + s * (bz - az);
            float qx = cx + t * (dx - cx), qz = cz + t * (dz - cz);
            float ex = px - qx, ez = pz - qz;
            return (float)Math.Sqrt(ex * ex + ez * ez);
        }

        // the same contact point, with the height difference between the two segments there
        private static float SegSegGapY(float ax, float ay, float az, float bx, float by, float bz,
            float cx, float cy, float cz, float dx, float dy, float dz, out float diff)
        {
            SegSegClosest(ax, az, bx, bz, cx, cz, dx, dz, out float s, out float t);
            diff = (cy + t * (dy - cy)) - (ay + s * (by - ay));
            return diff;
        }

        // standard 2D segment/segment closest-point parameters (Ericson, Real-Time Collision Detection)
        private static void SegSegClosest(float ax, float az, float bx, float bz, float cx, float cz,
            float dx, float dz, out float s, out float t)
        {
            float pax = ax, paz = az, pbx = bx, pbz = bz, pcx = cx, pcz = cz, pdx = dx, pdz = dz;
            float d1x = pbx - pax, d1z = pbz - paz;
            float d2x = pdx - pcx, d2z = pdz - pcz;
            float rx = pax - pcx, rz = paz - pcz;
            float a = d1x * d1x + d1z * d1z;
            float e = d2x * d2x + d2z * d2z;
            float f = d2x * rx + d2z * rz;
            const float eps = 1e-12f;
            if (a <= eps && e <= eps) { s = 0f; t = 0f; return; }
            if (a <= eps) { s = 0f; t = Clamp01(f / e); return; }
            float c = d1x * rx + d1z * rz;
            if (e <= eps) { t = 0f; s = Clamp01(-c / a); return; }
            float b = d1x * d2x + d1z * d2z;
            float denom = a * e - b * b;
            s = denom != 0f ? Clamp01((b * f - c * e) / denom) : 0f;
            t = (b * s + f) / e;
            if (t < 0f) { t = 0f; s = Clamp01(-c / a); }
            else if (t > 1f) { t = 1f; s = Clamp01((b - c) / a); }
        }

        private static float Clamp01(float v)
        {
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        private static bool StripIsSimple(MergeOutput o, int a, int b, int u, int v)
        {
            // the strip is simple when the two segments do not cross; a crossing would emit two triangles
            // that overlap, which the overlap invariants would (correctly) reject
            float rx = o.X[b] - o.X[a], rz = o.Z[b] - o.Z[a];
            float sx = o.X[v] - o.X[u], sz = o.Z[v] - o.Z[u];
            float den = rx * sz - rz * sx;
            if (Math.Abs(den) < 1e-9f) return true;                 // parallel
            float t = ((o.X[u] - o.X[a]) * sz - (o.Z[u] - o.Z[a]) * sx) / den;
            float w = ((o.X[u] - o.X[a]) * rz - (o.Z[u] - o.Z[a]) * rx) / den;
            return !(t > 0f && t < 1f && w > 0f && w < 1f);
        }

        private static float StripArea(MergeOutput o, int a, int b, int c, int d)
        {
            return 0.5f * Math.Abs((o.X[b] - o.X[a]) * (o.Z[c] - o.Z[a]) - (o.Z[b] - o.Z[a]) * (o.X[c] - o.X[a]))
                + 0.5f * Math.Abs((o.X[c] - o.X[a]) * (o.Z[d] - o.Z[a]) - (o.Z[c] - o.Z[a]) * (o.X[d] - o.X[a]));
        }

        // always up-facing: the seam is a floor, and its winding must not depend on which side it was found
        private static void AddTriUp(MergeOutput o, int i0, int i1, int i2)
        {
            double ux = o.X[i1] - o.X[i0], uz = o.Z[i1] - o.Z[i0];
            double wx = o.X[i2] - o.X[i0], wz = o.Z[i2] - o.Z[i0];
            double ny = uz * wx - ux * wz;
            if (ny < 0.0) { int tmp = i1; i1 = i2; i2 = tmp; }
            o.Tris.Add(i0); o.Tris.Add(i1); o.Tris.Add(i2);
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
            bool[] forceWalkableTriangles, bool[] ledgeExemptTriangles,
            float[] barrierVerts, int[] barrierTris, List<int> tileBarrierList,
            ObstructionCells obstructions, int tileIndex, BorderPolygon borderPoly)
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

                // BORDER CLAMP, part 3: the per-cell mask. See ApplyBorderMask for why this sits here
                // rather than immediately after the rasteriser.
                //
                // DELIBERATE agent-radius inset. Every span outside the polygon is nulled, and then
                // ErodeWalkableArea pulls the surviving boundary back a further walkableRadius cells
                // (0.25 m at the defaults, one cell at cs 0.25). That inset is wanted, not a rounding
                // artefact: the hard border is a physical wall in game (the engine's own
                // __scene_hard_border__ collider), and the soft one bounds where an agent may be
                // deployed, so the agent's CENTRE has to stay inside. Emitting a face whose centre sits
                // exactly on the border line would put a unit half outside its own walk area.
                if (borderPoly != null)
                {
                    int cellsOutside;
                    int masked = ApplyBorderMask(hf, borderPoly, obstructions, out cellsOutside);
                    r.BorderCellsMasked = masked;
                    r.BorderCellsOutside = cellsOutside;
                }

                // BARRIER CUT. Runs here, immediately after the ordinary soup has been rasterised and
                // the low-hanging filter applied, for two reasons:
                //  * the height rule needs the tile's own walkable floor, which only exists once the
                //    ordinary soup is in (WalkableFloor, inside CutBarriersIntoHeightfield);
                //  * it must precede gap fill, or the fill would see the barrier's own columns as an
                //    ordinary narrow gap and bridge them - which is the failure mode that made the
                //    first attempt useless (the _barrier_04x04m walls are ~1 m thick, i.e. 4 cells at
                //    cs 0.25, twice the default fill cap).
                // The wall becomes a solid span, so every downstream rule (ledge trim, clearance,
                // compaction, erosion) treats it as ordinary real geometry.
                if (tileBarrierList != null && tileBarrierList.Count > 0 && barrierTris != null)
                {
                    int[] bsubTris = new int[tileBarrierList.Count * 3];
                    for (int k = 0; k < tileBarrierList.Count; k++)
                    {
                        int src = tileBarrierList[k] * 3;
                        bsubTris[k * 3 + 0] = barrierTris[src + 0];
                        bsubTris[k * 3 + 1] = barrierTris[src + 1];
                        bsubTris[k * 3 + 2] = barrierTris[src + 2];
                    }
                    int cutCells, releasedCells;
                    CutBarriersIntoHeightfield(ctx, hf, barrierVerts, bsubTris, tileBarrierList.Count,
                        walkableClimb, obstructions, out cutCells, out releasedCells);
                    r.BarrierCellsCut = cutCells;
                    r.BarrierCellsReleased = releasedCells;
                    r.BarrierCellsGated = cutCells;
                }

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
                    int gapRefused, gapRefusedByBorder;
                    gapFilled = FillNarrowGaps(hf, s.GapFillCells, walkableClimb, border, cellsPerTile,
                        obstructions, s.VoxelCellSizeMeters, out gapEmpty, out gapRefused,
                        out gapRefusedByBorder);
                    r.GapFilled = gapFilled;
                    r.GapEmptyColumns = gapEmpty;
                    r.BarrierGapRefused = gapRefused;
                    r.BorderGapRefused = gapRefusedByBorder;
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
                {
                    int tileStitchRefused, tileBorderRefused;
                    r.Stitched = StitchTileComponents(r, hf, bminY, s.CellHeightMeters, s.HeightOffsetMeters,
                        walkableHeight, walkableClimb, s.StitchMaxGapCells, s.DetailWeldToleranceMeters,
                        obstructions, out tileStitchRefused, out tileBorderRefused);
                    r.BarrierStitchRefused = tileStitchRefused;
                    r.BorderStitchRefused = tileBorderRefused;
                }
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
            int border, int cellsPerTile, ObstructionCells obstructions, float cs,
            out int emptyColumns, out int obstructionRefused, out int borderRefused)
        {
            emptyColumns = 0;
            obstructionRefused = 0;
            borderRefused = 0;
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
                    // A barrier's own cells read as an empty column with walkable flanks on both sides
                    // - exactly the shape this pass exists to fill. Refuse it here, or the cut made a
                    // moment ago is undone.
                    if (obstructions != null && cs > 0f
                        && obstructions.Contains(hf.bmin.X + (x + 0.5f) * cs, hf.bmin.Z + (z + 0.5f) * cs))
                    {
                        if ((obstructions.KindAt(hf.bmin.X + (x + 0.5f) * cs, hf.bmin.Z + (z + 0.5f) * cs)
                                & ObstructionKind.Border) != 0)
                            borderRefused++;
                        else obstructionRefused++;
                        continue;
                    }
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

        /// A span counts as navigable exactly where WalkableFloor says it does - anything that is not
        /// RC_NULL_AREA. Kept as one predicate so the barrier height rule can scan a whole column's
        /// walkable band without duplicating (and eventually disagreeing with) that definition.
        private static bool IsWalkableArea(int area)
        {
            return area != RcRecast.RC_NULL_AREA;
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
            float offset, int walkableHeight, int walkableClimb, int stitchMaxGapCells, float weldTol,
            ObstructionCells obstructions, out int obstructionRefused, out int borderRefused)
        {
            obstructionRefused = 0;
            borderRefused = 0;
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
                    // Barrier gate. StitchGapClear above reads the tile heightfield, which now carries
                    // the barrier as a solid span, so it already refuses most crossings - but only where
                    // it happens to sample a barrier cell. This is the deterministic check on the
                    // connector's own segment, which is what actually gets added to the mesh.
                    if (obstructions != null && obstructions.SegmentCrosses(
                            r.VertsXyz[ua * 3], r.VertsXyz[ua * 3 + 2],
                            r.VertsXyz[va * 3], r.VertsXyz[va * 3 + 2]))
                    {
                        if (obstructions.SegmentCrossesKind(
                                r.VertsXyz[ua * 3], r.VertsXyz[ua * 3 + 2],
                                r.VertsXyz[va * 3], r.VertsXyz[va * 3 + 2], ObstructionKind.Border))
                            borderRefused++;
                        else obstructionRefused++;
                        continue;
                    }

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

        // SEAM WIDTH. Tilting a plate fixes its slope, but it cannot fix the columns BETWEEN the plate and
        // the ground it has to join: at the reported gate the plate's own collision reaches x=1133.465 and
        // the terrain mesh resumes at x=1133.75, yet the plate's built faces stop at x=1133.0 -
        // ErodeWalkableArea eats one cell and the contour simplifier another, so a 0.75 m void with a 0.50 m
        // step is left in the middle of the passage the user walks. A void that wide is not a gap an agent
        // can cross (Recast links adjacent columns, and 0.75 m is three columns at cs=0.25), and it is exactly
        // what reads on screen as "the navmesh stops here". FillNarrowGaps cannot fill it either: it runs
        // BEFORE FilterWalkableLowHeightSpans, so the columns are not empty yet - they still carry the
        // terrain span that the gate threshold beam's own underside then denies clearance to. Measured on
        // CC_20: the void is byte-identical with the blend on and off.
        //
        // So the seam is emitted here, as the missing strip between the plate's own boundary edge and the
        // nearest foreign boundary edge, and only where the bridge is genuinely climbable:
        //   - a real gap: wider than GatePlateSeamMinGapMeters (touching surfaces are already connected), and
        //     not wider than GatePlateSeamMaxGapMeters (past that it is a missing prop, not a seam);
        //   - every endpoint pair across the gap within the agent climb;
        //   - the resulting strip no steeper than MaxSlopeDegrees, or the agent meets a riser instead;
        //   - both edges unused so two plates cannot bridge the same void twice.
        // The island-safety lesson from the rejected connector experiment is applied directly: a seam is only
        // emitted BETWEEN ALREADY-CONNECTED faces (a single-component pair), so it cannot merge islands, and
        // it is registered in forcedRampCells so neither the steep-face repair nor the sliver cleanup can
        // remove the strip and leave the component split.
        public const float GatePlateSeamMinGapMeters = 0.1f;
        public const float GatePlateSeamMaxGapMeters = 1.5f;
        public const float GatePlateSeamMinAreaM2 = 0.08f;
        public const int GatePlateSeamMaxCount = 1024;

        // FOOTPRINT GROWTH. Tilting a plate fixes its slope and the seam fixes a void it can reach, but
        // neither puts the plate's own floor back where the voxel pipeline ate it. Measured on CC_20: the
        // gate threshold slab's colliders reach x = 1133.465 and its east end faces are at x = 1133.37
        // (--soup 1132.8,989.5,1136.5,993.5,80,95), while the plate's BUILT faces stop at x = 1132.75:
        // ErodeWalkableArea eats one cell and the contour simplifier another. The columns that leaves bare
        // (x = 1133.00..1133.50 at z = 991..992.5, inside the slab's own lip) are bare because the terrain
        // under the lip has 0.3-1.1 m of headroom and FilterWalkableLowHeightSpans correctly refuses it - the
        // walkable surface there IS the slab top, which the built mesh no longer reaches.
        //
        // So the plate polygon is grown back toward its own source collision, one cell at a time, outward
        // across a plate boundary edge, and only where the RAW SOUP says the floor is still there:
        //   FLOOR:    an up-facing soup triangle covers the cell at a height within one climb of the plate's
        //             own fitted plane there (this is the prop's own floor, not thin air and not a terrace);
        //   CLEARANCE:no soup triangle covers the cell between that floor and walkableHeight above it, which
        //             is Recast's own clearance rule, applied at emit time instead of trusting a filter that
        //             already ran before the plate was known;
        //   BARE:     the built mesh does not already cover the cell within one climb of the plane, so growth
        //             never paints over a neighbour;
        //   and the growth stops at the first cell that fails any of the three, at the first cell whose step
        //     from the plane exceeds MaxSlopeDegrees, and at GatePlateGrowMaxMeters.
        // Cells are tested at both edge endpoints and the edge midpoint, because a threshold lip commonly
        // exists only across the middle of an edge. Deterministic: plates in face-index order, edges in
        // face/edge order, integer cell steps outward.
        public const float GatePlateGrowMaxMeters = 1.0f;
        public const int GatePlateGrowMaxCount = 2048;

        // ROUTE B, the void crossing. See GrowCellOk. A cell with no soup floor of the plate's own may still be
        // grown into when it is a VOID - nothing in the soup within walkableHeight above the plane - and
        // walkable built mesh sits within GatePlateGrowVoidReachMeters of it at a step inside the climb. At the
        // reported gate that band is 0.555 m wide with a 0.170 m drop across it: inside the climb by a factor of
        // three, and therefore the exact case the engine's footprint-touch linking refuses and an agent walks.
        // The reach is a little over one agent radius (0.25 m), so Route B can close a seam-sized gap and not
        // reach across open ground; the cell budget caps it again at three cells, 0.75 m, per edge.
        public const float GatePlateGrowVoidReachMeters = 0.6f;
        public const int GatePlateGrowVoidMaxSteps = 3;

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
                    // vFaces is GLOBAL, so a candidate face's neighbours can belong to a DIFFERENT
                    // component whose fragment id is absent from this component's set. Such a fragment is
                    // its own root here - it must not be merged by this component's restore, and treating
                    // it as a singleton is what makes the walk terminate. (Looking it up blindly threw
                    // KeyNotFoundException; the barrier cut is what made a real scene reach this, by
                    // splitting a component at a shared boundary vertex.)
                    int r;
                    if (!fragRoot.TryGetValue(x, out r)) return x;
                    while (fragRoot[r] != r) r = fragRoot[r];
                    while (fragRoot[x] != r) { int nx = fragRoot[x]; fragRoot[x] = r; x = nx; }
                    return r;
                };
                // A fragment outside this component contributes nothing to "which fragments does this
                // candidate bridge" - counting it would let a face rejoin halves across a component
                // boundary this restore is not allowed to touch.
                Func<int, bool> inComponent = delegate (int fr) { return fragRoot.ContainsKey(fr); };
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
                                
                                if (!inComponent(fr)) continue;      // another component's fragment
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
                            int fr = fragOf[lst[q]];
                                if (!inComponent(fr)) continue;      // another component's fragment
                            int r = find(fr);
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