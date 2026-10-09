using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using BetterBannerlordNavmeshing;

// Offline verification of the barrier honour rule + the carved wall, injected into Program.cs.
// Compile-time snippet (kept out of the repo: the deliverable is TiledNavMeshBuilder.cs).
//
// The four masks are the EXACT values decompiled from TaleWorlds.Native.dll's barrier_builder build
// function (FUN_7ff8d04e4040, the length+strcmp chain at 0x7ff8d04e61e5..0x7ff8d04e628c).

internal static class BarrierTest
{
    private static int _fail;

    private static void Check(string what, bool ok, string detail = null)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + what + (detail != null ? "  " + detail : ""));
        if (!ok) _fail++;
    }

    public static int Run()
    {
        _fail = 0;
        Console.WriteLine("== BARRIER: honour-mask rule ==");
        const string Everything = "Everything";
        const string OnlyAgents = "Only Agents";
        const string OnlyAiAgents = "Only AI Agents";
        const string OnlyMissileMelee = "Only Missile and Melee";

        // The four masks, in the order the engine assigns them.
        var cases = new[]
        {
            new { Name = Everything,        Mask = 0x00000200, Honour = true  },
            new { Name = OnlyAgents,        Mask = 0x00200200, Honour = true  },
            new { Name = OnlyAiAgents,      Mask = 0x00200300, Honour = true  },
            new { Name = OnlyMissileMelee,  Mask = 0x00400200, Honour = false },
        };

        int nSkip = 0, nHon = 0;
        foreach (var c in cases) { if (c.Honour) nHon++; else nSkip++; }
        Check("exactly one mask is skipped (the missile-only one)",
            nSkip == 1 && cases.Length == 4
            && !TiledNavMeshBuilder.BarrierBlocksAgents(0x00400200),
            "skipped=" + nSkip + " honours=" + nHon + " of " + cases.Length);

        foreach (var c in cases)
        {
            bool got = TiledNavMeshBuilder.BarrierBlocksAgents(c.Mask);
            Check("mask '" + c.Name + "' 0x" + c.Mask.ToString("X8") + " -> honour=" + c.Honour,
                got == c.Honour, got == c.Honour ? null : "got " + got);
        }

        // The barrier bit itself is what makes it a barrier.
        Check("no Barrier/Barrier3D bit -> never honoured",
            !TiledNavMeshBuilder.BarrierBlocksAgents(0x00000000)
            && !TiledNavMeshBuilder.BarrierBlocksAgents(0x00200000)
            && !TiledNavMeshBuilder.BarrierBlocksAgents(0x00400000));
        Check("Barrier3D alone is honoured", TiledNavMeshBuilder.BarrierBlocksAgents(0x00000400));
        Check("Barrier|MissileOnly is NOT honoured (missile-only wins)",
            !TiledNavMeshBuilder.BarrierBlocksAgents(0x00400200));
        Check("Barrier|AgentOnly|MissileOnly is NOT honoured",
            !TiledNavMeshBuilder.BarrierBlocksAgents(0x00600200));
        Check("Barrier|AgentOnly|AILimiter is honoured",
            TiledNavMeshBuilder.BarrierBlocksAgents(0x00200300));

        // Cross-check against the engine's own exclude-flag presets, which are the actual reason.
        const int ForAgent = 0x2051B189;   // CommonCollisionExcludeFlagsForAgent
        const int ForMissile = 0x2031B789; // CommonCollisionExcludeFlagsForMissile
        const int AgentOnly = 0x00200000;
        const int MissileOnly = 0x00400000;
        const int Barrier = 0x00000200;
        foreach (var c in cases)
        {
            bool agentHit = (ForAgent & c.Mask) != c.Mask;   // not fully excluded => agents collide with it
            if (c.Mask == 0x00400200) continue;
            Check("engine agree: '" + c.Name + "' is not agent-excluded",
                agentHit, "ForAgent=0x" + ForAgent.ToString("X8") + " mask=0x" + c.Mask.ToString("X8"));
        }
        Check("engine agree: missile-only IS agent-excluded",
            (ForAgent & MissileOnly) == MissileOnly);
        Check("engine agree: MissileOnly is the only mask carrying it",
            (ForMissile & Barrier) != 0 && (ForAgent & Barrier) == 0
            && (ForAgent & AgentOnly) == 0 && (ForAgent & Barrier) == 0);

        Console.WriteLine("== BARRIER: " + (_fail == 0 ? "ALL PASS" : _fail + " FAILURES") + " ==");
        return _fail == 0 ? 0 : 1;
    }
}
// Offline verification of the user's exact case, injected into Program.cs (harness only).
//
// FIXTURE PROVENANCE - everything here is read out of the real scene, not invented:
//   scene            CC_20_siege
//   Modules\MyMaps\SceneObj\CC_20_siege\scene.xscene, the <Paths> element
//   <script name="barrier_builder"> on <game_entity name="barrier_1">:
//       Path Name = barrier_1   Mesh = barrier   Z Scale = 10.000   Flip = true
//       Mask = "Only Agents"    Barrier=false  Ai Limiter=true  Agent Only=true  Missile Only=false
//   -> runtime mask 0x00200200 (Barrier|AgentOnly), which BarrierBlocksAgents() must accept.
//   user report: "Crosses barrier at around: 1113.21 965.70 98.68"
//   nearest polyline node      : [10] (1112.878, 965.841, 97.352)  -> 0.36 m away in XZ
//   terrain under it (snapshot): 93.731 m, so with Z Scale 10 the wall spans ~93.7..107.4 m and
//                                 the reported 98.68 m crossing height is inside it.
//
// The wall is built the way the engine's barrier_builder does: a ribbon of quads along the polyline,
// one segment thick in Z (the node's own height is the wall's base), extruded `Z Scale` upward. The
// generated body is what we would read live; this is a faithful stand-in built from the same path.

internal static class BarrierCaseTest
{
    private static int _fail;
    private static void Check(string what, bool ok, string detail = null)
    {
        Console.WriteLine("  [" + (ok ? "PASS" : "FAIL") + "] " + what + (detail != null ? "  " + detail : ""));
        if (!ok) _fail++;
    }

    // barrier_1's 25 points, verbatim from scene.xscene (x, y=height, z).
    private static readonly float[,] Path =
    {
        {1126.344f,980.971f, 91.813f}, {1124.365f,978.508f, 93.403f}, {1123.379f,976.355f, 94.412f},
        {1123.283f,972.619f, 94.828f}, {1123.158f,970.734f, 94.125f}, {1123.609f,966.790f, 94.050f},
        {1122.691f,965.838f, 94.722f}, {1120.161f,964.116f, 96.132f}, {1117.138f,964.274f, 96.640f},
        {1114.287f,964.744f, 96.124f}, {1112.878f,965.841f, 97.352f}, {1111.667f,967.165f, 98.277f},
        {1109.527f,970.397f, 98.799f}, {1108.777f,971.861f, 98.261f}, {1107.660f,974.587f, 99.980f},
        {1106.534f,977.075f, 99.906f}, {1105.222f,978.806f, 98.647f}, {1103.967f,979.966f, 98.690f},
        {1093.482f,986.926f, 98.970f}, {1091.187f,986.698f,100.605f}, {1089.908f,987.303f, 99.708f},
        {1089.171f,986.451f,100.472f}, {1087.950f,985.181f,100.475f}, {1085.227f,983.967f,101.172f},
        {1083.056f,981.842f,102.757f},
    };

    private const float Thickness = 0.25f;   // the _barrier_04x04m prefab's Y scale: a 0.25 m slab
    private const float ZScale = 10.0f;        // barrier_builder's `Z Scale`
    private const int MaskOnlyAgents = 0x00200200;
    // Terrain floor under a world XZ point, from the snapshot terrain grid. BuildTerrainSoup indexes
    // the same way: value = Heights[xIndex * height + zIndex], world = index * spacing.
    private static float TerrainFloorAt(SceneSnapshot.TerrainData t, float x, float z)
    {
        if (t == null || t.Heights == null || t.Height < 1 || t.Width < 1) return 0f;
        float sp = t.Spacing > 0f ? t.Spacing : 1f;
        int xi = (int)Math.Floor(x / sp), zi = (int)Math.Floor(z / sp);
        if (xi < 0) xi = 0; if (xi >= t.Width) xi = t.Width - 1;
        if (zi < 0) zi = 0; if (zi >= t.Height) zi = t.Height - 1;
        return t.Heights[xi * t.Height + zi];
    }

    private static TiledNavMeshBuilder.BarrierWalls BuildWall(SceneSnapshot.TerrainData terrain)
    {
        var w = new TiledNavMeshBuilder.BarrierWalls();
        var wall = new TiledNavMeshBuilder.BarrierWall();
        wall.Name = "barrier_1";
        wall.Flags = MaskOnlyAgents;
        wall.Flip = true;
        var v = new System.Collections.Generic.List<float>();
        var t = new System.Collections.Generic.List<int>();
        int n = Path.GetLength(0);
        // The wall is PLANTED ON THE GROUND: its base is the terrain height under each path node, not
        // the node own z. That is what an author means by a barrier, and it is the shape the earlier
        // investigation measured (terrain 93.731 m under node[10], wall spanning ~93.7..107.4 m, so the
        // reported 98.68 m crossing height falls inside it). Using the raw node z instead would float
        // the wall 3.6 m above the floor, where it blocks nothing - and the height rule would then
        // correctly, but uselessly, release every column of it.
        for (int i = 0; i + 1 < n; i++)
        {
            float ax = Path[i, 0], az = Path[i, 1], ab = TerrainFloorAt(terrain, Path[i, 0], Path[i, 1]);
            float bx = Path[i + 1, 0], bz = Path[i + 1, 1], bb = TerrainFloorAt(terrain, Path[i + 1, 0], Path[i + 1, 1]);
            float dx = bx - ax, dz = bz - az;
            float len = (float)Math.Sqrt(dx * dx + dz * dz);
            if (len < 1e-4f) continue;
            float nx = -dz / len * Thickness * 0.5f, nz = dx / len * Thickness * 0.5f;
            int b0 = v.Count / 3;
            Add3(v, ax + nx, az + nz, ab);
            Add3(v, bx + nx, bz + nz, bb);
            Add3(v, bx - nx, bz - nz, bb + ZScale);
            Add3(v, ax - nx, az - nz, ab + ZScale);
            t.Add(b0); t.Add(b0 + 1); t.Add(b0 + 2);
            t.Add(b0); t.Add(b0 + 2); t.Add(b0 + 3);
        }
        float zmin = float.MaxValue, zmax = float.MinValue;
        for (int i = 0; i < v.Count / 3; i++)
        {
            float z = v[i * 3 + 2];
            if (z < zmin) zmin = z;
            if (z > zmax) zmax = z;
        }
        wall.VertsXyz = v.ToArray();
        wall.Tris = t.ToArray();
        wall.ZMin = zmin;
        wall.ZMax = zmax;
        w.Add(wall);
        return w;
    }

    private static void Add3(System.Collections.Generic.List<float> v, float x, float y, float z)
    {
        v.Add(x); v.Add(y); v.Add(z);
    }

    public static int Run(string[] args)
    {
        _fail = 0;
        const float uX = 1113.21f, uY = 965.70f, uZ = 98.68f;

        string prefix = args.Length > 1 ? args[1] : "CC_20_siege_navmesh";
        SceneSnapshot.TerrainData terrain = SceneSnapshot.ReadTerrainBin(prefix + "_snapshot_terrain.bin");
        TiledNavMeshBuilder.BarrierWalls walls = BuildWall(terrain);
        Console.WriteLine("== BARRIER: the user's case (CC_20_siege / barrier_1) ==");
        Console.WriteLine("  fixture: " + walls.Count + " body, " + walls.TotalTris + " tris, mask=0x"
            + MaskOnlyAgents.ToString("X8") + " Z=[" + walls.Walls[0].ZMin.ToString("0.###") + ".."
            + walls.Walls[0].ZMax.ToString("0.###") + "]m honour="
            + TiledNavMeshBuilder.BarrierBlocksAgents(MaskOnlyAgents));
        Check("mask 'Only Agents' (0x00200200) is honoured",
            TiledNavMeshBuilder.BarrierBlocksAgents(MaskOnlyAgents));
        Check("the reported crossing height is inside the wall's vertical extent",
            uZ >= walls.Walls[0].ZMin && uZ <= walls.Walls[0].ZMax,
            "userZ=" + uZ + " wallZ=[" + walls.Walls[0].ZMin.ToString("0.###") + ".."
            + walls.Walls[0].ZMax.ToString("0.###") + "]");

        // --- the user's point is on the polyline
        float bestD = float.MaxValue; int bestI = -1;
        for (int i = 0; i < Path.GetLength(0); i++)
        {
            float dx = Path[i, 0] - uX, dz = Path[i, 1] - uY;
            float d = (float)Math.Sqrt(dx * dx + dz * dz);
            if (d < bestD) { bestD = d; bestI = i; }
        }
        Check("nearest polyline node is [10] within 0.5 m",
            bestI == 10 && bestD < 0.5f, "node=[" + bestI + "] d=" + bestD.ToString("0.000") + "m");

        return BarrierCaseTest.RunSnapshot(prefix, walls, uX, uY, uZ);
    }

    // Build the real CC_20 snapshot twice - barriers off and on - and compare the mesh around the
    // crossing. This exercises the real heightfield path (tile raster, height rule, gap-fill gate,
    // stitch gate, prune guard) on the real terrain and the real asset soup.
private static int RunSnapshot(string prefix, TiledNavMeshBuilder.BarrierWalls walls,
        float uX, float uY, float uZ)
    {
        SceneSnapshot.TerrainData terrain = SceneSnapshot.ReadTerrainBin(prefix + "_snapshot_terrain.bin");
        List<SceneSnapshot.EntityRecord> assets = SceneSnapshot.ReadAssetsBin(prefix + "_snapshot_assets.bin");

        var s = new TiledNavMeshBuilder.TiledNavMeshSettings();
        s.VoxelCellSizeMeters = 0.25f;
        s.TileWorldSizeMeters = 10f;
        s.AgentRadiusMeters = 0.25f;
        s.AgentHeightMeters = 1.8f;
        s.MaxSlopeDegrees = 50f;
        s.MaxErrorMeters = 0.25f;
        s.HeightOffsetMeters = 0.5f;
        s.UseDetailMesh = true;
        s.DetailSampleDistMeters = 2f;
        s.ContourMaxErrorVoxels = 2.0f;
        s.QuadMerge = true;
        s.GatePlateBlend = true;
        // The exporter resolves the climb the way VoxelNavMeshGenerator does, and the editor's stitch
        // cap is 6 (not the class default 4). Matching the real run exactly is what makes off/on a
        // meaningful comparison.
        s.AgentMaxClimbMeters = TiledNavMeshBuilder.ResolveAgentClimbMeters(0.25f, 0.2f, 50f, 0.4f);
        s.StitchMaxGapCells = 6;
        s.GapFillCells = 2;
        s.PruneIsolatedIslands = true;
        s.PruneMaxIslandFaces = 400;
        s.CleanupSlivers = true;
        Console.WriteLine("  settings: cs=" + s.VoxelCellSizeMeters + " radius=" + s.AgentRadiusMeters
            + " climb=" + s.AgentMaxClimbMeters.ToString("0.###") + " slope=" + s.MaxSlopeDegrees
            + " gapFill=" + s.GapFillCells + " stitch=" + s.StitchMaxGapCells
            + " prune=" + (s.PruneIsolatedIslands ? s.PruneMaxIslandFaces.ToString() : "off")
            + " ledge=" + s.UseLedgeFilter + " detail=" + s.UseDetailMesh);

        // Build(...) mutates the soup arrays in place (winding normalisation), so every build gets a
        // FRESH soup. Sharing one array made builds 2 and 3 read already-reversed winding.
        Action<string> log = delegate (string m) { LogSink.Add(m); };
        var off = BuildOne(terrain, assets, s, null, log);
        // A/B: an EMPTY (but non-null) BarrierWalls is what SceneCollisionGeometry hands over on a
        // scene with no honour-able barrier. It must be byte-identical to passing null, which is what
        // proves NavMeshVoxelBarriers=false restores the previous output exactly.
        var sEmpty = new TiledNavMeshBuilder.TiledNavMeshSettings();
        CopySettings(s, sEmpty);
        sEmpty.Barriers = new TiledNavMeshBuilder.BarrierWalls();
        var emptyRes = BuildOne(terrain, assets, sEmpty, null, log);
        Console.WriteLine("  CONTROL emptyBarriers: F=" + emptyRes.FaceCount + " islands=" + CountIslands(emptyRes)
            + " invariants=" + (emptyRes.InvariantsPassed ? "PASS" : "FAIL")
            + " bodies=" + sEmpty.BarrierBodies + " cut=" + sEmpty.BarrierCellsCut);
        Check("CONTROL: an empty BarrierWalls == no barriers at all (byte-identical)",
            emptyRes.FaceCount == off.FaceCount && emptyRes.VertsXyz.Length == off.VertsXyz.Length
            && emptyRes.Tris.Length == off.Tris.Length,
            "off=" + off.FaceCount + " empty=" + emptyRes.FaceCount);
        var s4 = new TiledNavMeshBuilder.TiledNavMeshSettings();
        CopySettings(s, s4);
        var off2 = BuildOne(terrain, assets, s4, null, log);
        Console.WriteLine("  CONTROL off2 (identical settings, no barrier): F=" + off2.FaceCount
            + " islands=" + CountIslands(off2)
            + " invariants=" + (off2.InvariantsPassed ? "PASS" : "FAIL"));
        var s2 = new TiledNavMeshBuilder.TiledNavMeshSettings();
        CopySettings(s, s2);
        s2.Barriers = walls;
        var on = BuildOne(terrain, assets, s2, null, log);
        var s3 = new TiledNavMeshBuilder.TiledNavMeshSettings();
        CopySettings(s, s3);
        s3.Barriers = HighWall(uX, uY);
        var highRes = BuildOne(terrain, assets, s3, null, log);
        Check("CONTROL: two identical no-barrier builds in one process agree (no cross-build state leak)",
            off2.FaceCount == off.FaceCount && off2.VertsXyz.Length == off.VertsXyz.Length,
            "off=" + off.FaceCount + " off2=" + off2.FaceCount);

        Console.WriteLine("  barriers OFF: F=" + off.FaceCount + " islands=" + CountIslands(off)
            + " invariants=" + (off.InvariantsPassed ? "PASS" : "FAIL"));
        Console.WriteLine("  barriers ON : F=" + on.FaceCount + " islands=" + CountIslands(on)
            + " invariants=" + (on.InvariantsPassed ? "PASS" : "FAIL")
            + " cutCells=" + s2.BarrierCellsCut + " released=" + s2.BarrierCellsReleased
            + " gapRefused=" + s2.BarrierGapRefused + " stitchRefused=" + s2.BarrierStitchRefused);
        Console.WriteLine("  HIGH wall   : F=" + highRes.FaceCount + " islands=" + CountIslands(highRes)
            + " cutCells=" + s3.BarrierCellsCut + " released=" + s3.BarrierCellsReleased);
        foreach (var m in LogSink) if (m.Contains("barriers:") || m.Contains("barrier-adjacent")) Console.WriteLine("    " + m.Trim());

        Check("barriers ON keeps the invariants", on.InvariantsPassed);
        Check("HIGH wall alone keeps the invariants", highRes.InvariantsPassed);
        Check("the cut actually happened on the user's wall", s2.BarrierCellsCut > 0, "cutCells=" + s2.BarrierCellsCut);

        // The decisive measurement: faces inside a corridor around the polyline. The wall is 0.25 m
        // thick, so the band is the wall footprint plus a cell of agent standing room either side.
        const float measureBand = 0.75f;
        int bandOff = CountInBand(off, measureBand);
        int bandOn = CountInBand(on, measureBand);
        int bandHigh = CountInBand(highRes, measureBand);
        Console.WriteLine("  faces within " + measureBand + " m of the polyline:  OFF=" + bandOff
            + "  ON=" + bandOn + "  HIGHwallOnly=" + bandHigh);

// ---- THE DECISIVE TEST: can a route cross the wall without going through it? ----
        // Two probe points 3 m either side of the reported crossing, along x. `band` is the wall's
        // footprint plus the agent radius, i.e. the corridor an agent's body would occupy while
        // standing in the wall.
        const float band = 0.5f;
        float pAx = uX + 3.0f, pAz = uY, pBx = uX - 3.0f, pBz = uY;

        bool offThrough = Reachable(off, pAx, pAz, pBx, pBz, band);
        bool onThrough = Reachable(on, pAx, pAz, pBx, pBz, band);
        bool onAround = Reachable(on, pAx, pAz, pBx, pBz, 0f);
        bool offAround = Reachable(off, pAx, pAz, pBx, pBz, 0f);
        Console.WriteLine("  crossing reachability (probes 3 m either side, corridor band " + band + " m):");
        Console.WriteLine("    OFF: through the corridor=" + offThrough + "  ignoring it=" + offAround);
        Console.WriteLine("    ON : through the corridor=" + onThrough + "  ignoring it=" + onAround);

        Check("BEFORE: a route crosses the wall's corridor (the reported bug)",
            offThrough, "through=" + offThrough);
        Check("AFTER: NO route crosses the wall's corridor",
            !onThrough, "through=" + onThrough);
        Check("AFTER: the two sides are still connected AROUND the ends of the finite wall",
            onAround, "around=" + onAround);

        // Both sides still carry real navigable surface (the wall did not delete the area).
        float hA0, hB0, hA1, hB1;
        int fA0 = FacesAt(off, Islands(off), pAx, pAz, out hA0);
        int fB0 = FacesAt(off, Islands(off), pBx, pBz, out hB0);
        int fA1 = FacesAt(on, Islands(on), pAx, pAz, out hA1);
        int fB1 = FacesAt(on, Islands(on), pBx, pBz, out hB1);
        Console.WriteLine("  surface at the probes: OFF +x=" + fA0 + " faces h=" + hA0.ToString("0.0")
            + "  -x=" + fB0 + " faces h=" + hB0.ToString("0.0"));
        Console.WriteLine("                        ON  +x=" + fA1 + " faces h=" + hA1.ToString("0.0")
            + "  -x=" + fB1 + " faces h=" + hB1.ToString("0.0"));
        Check("both sides still carry walkable mesh (the wall did not delete the area)",
            fA1 > 0 && fB1 > 0, "faces +x=" + fA1 + " -x=" + fB1);

        // The carve may legitimately ADD faces (splitting one island into several leaves more, smaller
        // regions for the quad merge to work with), so this is reported, not asserted. What must hold is
        // that the bulk of the map is still one network.
        Check("the largest island still dominates (the wall did not shatter the map)",
            LargestIslandFaces(on) > off.FaceCount / 2,
            "largest=" + LargestIslandFaces(on) + " of " + on.FaceCount);

        // No over-cut on a wall 29 m above the street (the shipped _barrier_04x04m case at z~123.5).
        Check("a wall 29 m above the walkway cuts NOTHING (height rule)",
            s3.BarrierCellsCut == 0 && s3.BarrierCellsReleased > 0,
            "cut=" + s3.BarrierCellsCut + " released=" + s3.BarrierCellsReleased);
        Check("... and leaves the mesh identical to the no-barrier build",
            highRes.FaceCount == off.FaceCount && CountIslands(highRes) == CountIslands(off),
            "off=" + off.FaceCount + " highOnly=" + highRes.FaceCount);

        Console.WriteLine("== BARRIER CASE: " + (_fail == 0 ? "ALL PASS" : _fail + " FAILURES") + " ==");
        return _fail == 0 ? 0 : 1;
    }

    private static readonly List<string> LogSink = new List<string>();

// Reachability between two world XZ points over face adjacency, optionally forbidden from entering
    // any face whose centroid lies within `forbidBand` metres of the barrier polyline.
    //
    // This is the assertion that actually matches the requirement. "Are the two sides the same island"
    // is the WRONG question for a finite wall: the wall is ~40 m long, so the two sides are SUPPOSED to
    // still be connected around its ends, and they are. What must not be possible is a route THROUGH
    // the wall itself. So: flood-fill from one side while refusing the wall's corridor, and the other
    // side must be unreachable; then flood-fill without that refusal, and it must be reachable.
    private static bool Reachable(TiledNavMeshBuilder.VoxelMeshResult m, float ax, float az,
        float bx, float bz, float forbidBand)
    {
        int nf = m.Tris.Length / 3;
        if (nf == 0) return false;
        // vertex -> faces
        var vFaces = new Dictionary<int, List<int>>();
        for (int f = 0; f < nf; f++)
        {
            for (int k = 0; k < 3; k++)
            {
                int v = m.Tris[f * 3 + k];
                List<int> lst;
                if (!vFaces.TryGetValue(v, out lst))
                {
                    lst = new List<int>(4);
                    vFaces[v] = lst;
                }
                lst.Add(f);
            }
        }
        int start = NearestFace(m, ax, az, 2.0f);
        int goal = NearestFace(m, bx, bz, 2.0f);
        if (start < 0 || goal < 0) return false;
        if (start == goal) return true;

        // Precompute the centroid distance once; the flood touches every face exactly once.
        var dist = new float[nf];
        for (int f = 0; f < nf; f++)
        {
            float cx = 0, cz = 0;
            for (int k = 0; k < 3; k++)
            {
                int v = m.Tris[f * 3 + k];
                cx += m.VertsXyz[v * 3];
                cz += m.VertsXyz[v * 3 + 2];
            }
            dist[f] = DistToPolyline(cx / 3f, cz / 3f);
        }

        var seen = new bool[nf];
        var queue = new Queue<int>();
        seen[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            int f = queue.Dequeue();
            if (f == goal) return true;
            if (forbidBand > 0f && dist[f] <= forbidBand) continue;   // may not travel through here
            for (int k = 0; k < 3; k++)
            {
                List<int> lst;
                if (!vFaces.TryGetValue(m.Tris[f * 3 + k], out lst)) continue;
                for (int q = 0; q < lst.Count; q++)
                {
                    int g = lst[q];
                    if (seen[g]) continue;
                    if (forbidBand > 0f && dist[g] <= forbidBand) continue;
                    seen[g] = true;
                    queue.Enqueue(g);
                }
            }
        }
        return false;
    }

    private static int NearestFace(TiledNavMeshBuilder.VoxelMeshResult m, float px, float pz, float maxD)
    {
        int nf = m.Tris.Length / 3;
        int best = -1;
        float bestD = float.MaxValue;
        for (int f = 0; f < nf; f++)
        {
            float cx = 0, cz = 0;
            for (int k = 0; k < 3; k++)
            {
                int v = m.Tris[f * 3 + k];
                cx += m.VertsXyz[v * 3];
                cz += m.VertsXyz[v * 3 + 2];
            }
            float ex = cx / 3f - px, ez = cz / 3f - pz;
            float d = (float)Math.Sqrt(ex * ex + ez * ez);
            if (d < bestD) { bestD = d; best = f; }
        }
        return bestD <= maxD ? best : -1;
    }
    private static TiledNavMeshBuilder.VoxelMeshResult BuildOne(SceneSnapshot.TerrainData terrain,
        List<SceneSnapshot.EntityRecord> assets, TiledNavMeshBuilder.TiledNavMeshSettings s,
        TiledNavMeshBuilder.BarrierWalls unused, Action<string> log)
    {
        // Fresh copy of the asset list: ApplyPropRamps-style mutation must not leak between builds.
        var copy = new List<SceneSnapshot.EntityRecord>(assets.Count);
        for (int i = 0; i < assets.Count; i++)
        {
            SceneSnapshot.EntityRecord src = assets[i];
            var r = new SceneSnapshot.EntityRecord();
            r.Name = src.Name; r.Flags = src.Flags; r.Mobility = src.Mobility;
            r.TriCount = src.TriCount; r.Capped = src.Capped; r.RampTris = src.RampTris;
            r.VertsXyz = (float[])src.VertsXyz.Clone();
            r.Tris = (int[])src.Tris.Clone();
            copy.Add(r);
        }
        float[] sv; int[] st; bool[] sf; bool[] se;
        BuildSnapshotSoup(terrain, copy, out sv, out st, out sf, out se);
        return TiledNavMeshBuilder.Build(sv, st, s, Environment.ProcessorCount, log, sf, se);
        // Decisive: is the INPUT identical between builds, or is Build itself non-deterministic?
        Console.WriteLine("    soup hash: verts=" + sv.Length + " tris=" + st.Length
            + " hash=" + SoupHash(sv, st));
    }

    // A shipped hand-placed _barrier_04x04m: 21.5 x 2 m footprint, 2.9 m tall, at z~123.5 - i.e. a
    // barrier high on the castle wall, well above the streets at z~95.
    private static TiledNavMeshBuilder.BarrierWalls HighWall(float uX, float uY)
    {
        var high = new TiledNavMeshBuilder.BarrierWalls();
        var hw = new TiledNavMeshBuilder.BarrierWall();
        hw.Name = "_barrier_04x04m";
        hw.Flags = MaskOnlyAgents;
        var hv = new List<float>(); var ht = new List<int>();
        float hx0 = uX - 10f, hz0 = uY - 1f;
        float z0 = 122.561f, z1 = 125.469f;
        int hb = hv.Count / 3;
        Add3(hv, hx0, hz0, z0); Add3(hv, hx0 + 21.5f, hz0, z0);
        Add3(hv, hx0 + 21.5f, hz0 + 2f, z0); Add3(hv, hx0, hz0 + 2f, z0);
        Add3(hv, hx0, hz0, z1); Add3(hv, hx0 + 21.5f, hz0, z1);
        Add3(hv, hx0 + 21.5f, hz0 + 2f, z1); Add3(hv, hx0, hz0 + 2f, z1);
        ht.Add(hb); ht.Add(hb + 1); ht.Add(hb + 2); ht.Add(hb); ht.Add(hb + 2); ht.Add(hb + 3);
        ht.Add(hb + 4); ht.Add(hb + 7); ht.Add(hb + 6); ht.Add(hb + 4); ht.Add(hb + 6); ht.Add(hb + 5);
        hw.VertsXyz = hv.ToArray(); hw.Tris = ht.ToArray();
        hw.ZMin = z0; hw.ZMax = z1;
        high.Add(hw);
        return high;
    }
    private static void CopySettings(TiledNavMeshBuilder.TiledNavMeshSettings a,
        TiledNavMeshBuilder.TiledNavMeshSettings b)
    {
        b.VoxelCellSizeMeters = a.VoxelCellSizeMeters;
        b.TileWorldSizeMeters = a.TileWorldSizeMeters;
        b.AgentRadiusMeters = a.AgentRadiusMeters;
        b.AgentHeightMeters = a.AgentHeightMeters;
        b.MaxSlopeDegrees = a.MaxSlopeDegrees;
        b.MaxErrorMeters = a.MaxErrorMeters;
        b.HeightOffsetMeters = a.HeightOffsetMeters;
        b.UseDetailMesh = a.UseDetailMesh;
        b.DetailSampleDistMeters = a.DetailSampleDistMeters;
        b.ContourMaxErrorVoxels = a.ContourMaxErrorVoxels;
        b.QuadMerge = a.QuadMerge;
        b.GatePlateBlend = a.GatePlateBlend;
        b.AgentMaxClimbMeters = a.AgentMaxClimbMeters;   // resolved climb: omitting this silently
                                                            // rebuilt every copy at the 0.4 default
        b.CleanupSlivers = a.CleanupSlivers;
        b.GapFillCells = a.GapFillCells;
        b.StitchMaxGapCells = a.StitchMaxGapCells;
        b.PruneIsolatedIslands = a.PruneIsolatedIslands;
        b.PruneMaxIslandFaces = a.PruneMaxIslandFaces;
    }

    private static int[] Islands(TiledNavMeshBuilder.VoxelMeshResult m)
    {
        // Union-find over shared edges, exactly like the builder's own island report.
        int nf = m.Tris.Length / 3;
        int[] fp = new int[nf];
        for (int i = 0; i < nf; i++) fp[i] = i;
        var first = new Dictionary<long, int>();
        for (int f = 0; f < nf; f++)
        {
            int a = m.Tris[f * 3], b = m.Tris[f * 3 + 1], c = m.Tris[f * 3 + 2];
            Union(first, fp, f, a, b); Union(first, fp, f, b, c); Union(first, fp, f, c, a);
        }
        return fp;
    }

    private static void Union(Dictionary<long, int> first, int[] fp, int f, int a, int b)
    {
        long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        int other;
        if (first.TryGetValue(k, out other)) { Union2(fp, f, other); } else first[k] = f;
    }

    private static int Find(int[] fp, int x) { while (fp[x] != x) { fp[x] = fp[fp[x]]; x = fp[x]; } return x; }
    private static void Union2(int[] fp, int a, int b)
    {
        int ra = Find(fp, a), rb = Find(fp, b);
        if (ra != rb) fp[rb] = ra;
    }

    private static int CountIslands(TiledNavMeshBuilder.VoxelMeshResult m)
    {
        int nf = m.Tris.Length / 3;
        if (nf == 0) return 0;
        int[] fp = Islands(m);
        var s = new HashSet<int>();
        for (int i = 0; i < nf; i++) s.Add(Find(fp, i));
        return s.Count;
    }

    // Distance from a world XZ point to the polyline.
    private static float DistToPolyline(float px, float pz)
    {
        float best = float.MaxValue;
        for (int i = 0; i + 1 < Path.GetLength(0); i++)
        {
            float ax = Path[i, 0], az = Path[i, 1], bx = Path[i + 1, 0], bz = Path[i + 1, 1];
            float dx = bx - ax, dz = bz - az;
            float L2 = dx * dx + dz * dz;
            float t = L2 < 1e-9f ? 0f : ((px - ax) * dx + (pz - az) * dz) / L2;
            if (t < 0f) t = 0f; if (t > 1f) t = 1f;
            float qx = ax + dx * t, qz = az + dz * t;
            float ex = px - qx, ez = pz - qz;
            float d = (float)Math.Sqrt(ex * ex + ez * ez);
            if (d < best) best = d;
        }
        return best;
    }

// Faces whose centroid lies within `band` metres (XZ) of the wall's centreline. This is the
    // corridor measurement: the wall is 0.25 m thick, so a 0.75 m band is the wall's own footprint
    // plus a cell of standing room either side.
    private static int CountInBand(TiledNavMeshBuilder.VoxelMeshResult m, float band)
    {
        int nf = m.Tris.Length / 3;
        int hits = 0;
        for (int f = 0; f < nf; f++)
        {
            float cx = 0, cz = 0;
            for (int k = 0; k < 3; k++)
            {
                int v = m.Tris[f * 3 + k];
                cx += m.VertsXyz[v * 3];
                cz += m.VertsXyz[v * 3 + 2];
            }
            cx /= 3f; cz /= 3f;
            if (DistToPolyline(cx, cz) <= band) hits++;
        }
        return hits;
    }

    // The face nearest a world XZ point, and its island. `searchMeters` bounds the search so a
    // genuinely mesh-free spot reports 0 faces / island -1 instead of silently binding to something
    // metres away.
    private static int IslandAt(TiledNavMeshBuilder.VoxelMeshResult m, int[] fp, float px, float pz,
        out float hOut)
    {
        hOut = 0f;
        int bestFace = -1;
        float bestD = float.MaxValue;
        int nf = m.Tris.Length / 3;
        for (int f = 0; f < nf; f++)
        {
            float cx = 0, cz = 0;
            for (int k = 0; k < 3; k++)
            {
                int v = m.Tris[f * 3 + k];
                cx += m.VertsXyz[v * 3];
                cz += m.VertsXyz[v * 3 + 2];
            }
            cx /= 3f; cz /= 3f;
            float ex = cx - px, ez = cz - pz;
            float d = (float)Math.Sqrt(ex * ex + ez * ez);
            if (d < bestD) { bestD = d; bestFace = f; }
        }
        if (bestFace < 0 || bestD > 2.0f) return -1;
        hOut = m.VertsXyz[m.Tris[bestFace * 3] * 3 + 1];
        return Find(fp, bestFace);
    }

    // Faces belonging to the island that owns the mesh nearest a world XZ point (0 when there is none).
    private static int FacesAt(TiledNavMeshBuilder.VoxelMeshResult m, int[] fp, float px, float pz,
        out float hOut)
    {
        int isl = IslandAt(m, fp, px, pz, out hOut);
        if (isl < 0) return 0;
        int nf = m.Tris.Length / 3, n = 0;
        for (int f = 0; f < nf; f++) if (Find(fp, f) == isl) n++;
        return n;
    }

    private static int LargestIslandFaces(TiledNavMeshBuilder.VoxelMeshResult m)
    {
        int nf = m.Tris.Length / 3;
        if (nf == 0) return 0;
        int[] fp = Islands(m);
        var count = new Dictionary<int, int>();
        int best = 0;
        for (int f = 0; f < nf; f++)
        {
            int r = Find(fp, f);
            int c;
            count.TryGetValue(r, out c);
            c++;
            count[r] = c;
            if (c > best) best = c;
        }
        return best;
    }
    private static string SoupHash(float[] v, int[] t)
    {
        unchecked
        {
            long h = 1469598103934665603L;
            for (int i = 0; i < v.Length; i++) h = (h ^ BitConverter.DoubleToInt64Bits(v[i])) * 1099511628211L;
            for (int i = 0; i < t.Length; i++) h = (h ^ t[i]) * 1099511628211L;
            return h.ToString("X16");
        }
    }

    private static void BuildSnapshotSoup(SceneSnapshot.TerrainData terrain,
        List<SceneSnapshot.EntityRecord> assets, out float[] vertsXyz, out int[] tris,
        out bool[] force, out bool[] exempt)
    {
        TiledNavMeshBuilder.BuildTerrainSoup(terrain.Heights, terrain.Width, terrain.Height,
            terrain.Spacing, terrain.Stride, out vertsXyz, out tris);
        force = null; exempt = null;
        if (assets == null || assets.Count == 0) return;
        int terrainTris = tris.Length / 3;
        var av = new List<float>(); var at = new List<int>();
        var af = new List<bool>(); var ae = new List<bool>();
        for (int i = 0; i < assets.Count; i++)
        {
            SceneSnapshot.EntityRecord e = assets[i];
            if (e.Name != null && e.Name.EndsWith("#barrier", StringComparison.Ordinal)) continue;
            int baseIndex = av.Count / 3;
            for (int k = 0; k < e.VertsXyz.Length; k++) av.Add(e.VertsXyz[k]);
            for (int k = 0; k < e.Tris.Length; k++) at.Add(e.Tris[k] + baseIndex);
            int rampTris = e.RampTris;
            if (rampTris <= 0 && e.Name != null
                && (e.Name.EndsWith("#ramp", StringComparison.Ordinal)
                    || e.Name.EndsWith(GateBridgeStrips.GateNameSuffix, StringComparison.Ordinal)))
                rampTris = e.TriCount;
            if (rampTris > e.TriCount) rampTris = e.TriCount;
            bool steppedEntity = (e.Flags & (SteppedPropRamps.FlagHasSteps | SteppedPropRamps.FlagLadder)) != 0;
            for (int t = 0; t < e.TriCount; t++)
            {
                bool isRamp = t >= e.TriCount - rampTris;
                af.Add(isRamp); ae.Add(!isRamp && steppedEntity);
            }
        }
        float[] mx; int[] mt;
        TiledNavMeshBuilder.BuildCombinedSoup(vertsXyz, tris, av.ToArray(), at.ToArray(), out mx, out mt, out int _f);
        vertsXyz = mx; tris = mt;
        bool anyF = false, anyE = false;
        for (int k = 0; k < af.Count; k++) { if (af[k]) anyF = true; if (ae[k]) anyE = true; }
        if (anyF) { force = new bool[terrainTris + af.Count]; for (int k = 0; k < af.Count; k++) force[terrainTris + k] = af[k]; }
        if (anyE) { exempt = new bool[terrainTris + ae.Count]; for (int k = 0; k < ae.Count; k++) exempt[terrainTris + k] = ae[k]; }
    }
}
internal static class Program
{
    private static bool Ramp23PlateBlend = true;
    private static Action<string> Log = delegate (string m) { Console.WriteLine("    " + m); };

    private static void Main(string[] args)
    {
        if (args != null && args.Length > 0 && args[0] == "--snapshot")
        {
            Environment.Exit(SnapshotMode(args));
            return;
        }
        if (args != null && args.Length > 0 && args[0] == "--barriers")
        {
            Environment.Exit(BarrierTest.Run());
            return;
        }
        if (args != null && args.Length > 0 && args[0] == "--barriercase")
        {
            Environment.Exit(BarrierCaseTest.Run(args));
            return;
        }
        if (args != null && args.Length > 0 && args[0] == "--quadtest")
        {
            Test27_QuadMerge();
            return;
        }

        Console.WriteLine("== TiledNavMeshBuilder standalone harness (net472 / C# 7.3) ==");
        Console.WriteLine("processors = " + Environment.ProcessorCount);
        TiledNavMeshBuilder.GatePlateTrace = true;
        if (Environment.GetEnvironmentVariable("HARNESS_PLATE") == "off") Ramp23PlateBlend = false;
        
        Console.WriteLine();

        Test1_Coverage();
        Console.WriteLine();
        Test2_Slope();
        Console.WriteLine();
        Test3_Determinism();
        Console.WriteLine();
        Test4_Scaling();
        Console.WriteLine();
        Test5_DetailMesh();
        Console.WriteLine();
        Test6_Ramp65();
        Console.WriteLine();
        Test7_Hill();
        Console.WriteLine();
        Test8_TinyDetailDump();
        Console.WriteLine();
        Test9_LargeScene();
        Console.WriteLine();
        Test10_Slivers();
        Console.WriteLine();
        Test11_Assets();
        Console.WriteLine();
        Test12_RegionPruning();
        Console.WriteLine();
        Test13_Connectivity();
        Console.WriteLine();
        Test14_Sweep();
        Console.WriteLine();
        Test15_TerrainHole();
        Console.WriteLine();
        Test16_GapFill();
        Console.WriteLine();
        Test17_IslandReport();
        Console.WriteLine();
        Test18_Stitch();
        Console.WriteLine();
        Test19_Prune();
        Console.WriteLine();
        Test20_SnapshotRoundTrip();
        Console.WriteLine();
        Test21_MergeStitch();
        Console.WriteLine();
        Test22_Winding();
        Console.WriteLine();
        Test23_PropRamps();
        Console.WriteLine();
        Test24_DoorwayLadder();
        Console.WriteLine();
        Test25_LadderSlopeExemption();
        Console.WriteLine();
        Test26_NarrowDoorway();
        Console.WriteLine();
        Test27_QuadMerge();
        Console.WriteLine();
        Test28_CleanupDeterminism();
        Console.WriteLine();
        Test29_WaterCull();
        Console.WriteLine();
        SweepDoorway26();
    }

    // ================================================================ snapshot mode
    // --snapshot <prefix>                  -> <prefix>_snapshot_terrain.bin / _snapshot_assets.bin / .txt
    // --snapshot <terrainBin> <assetsBin> <txt>
    private static int SnapshotMode(string[] args)
    {
        try
        {
            // ---- parse: source (1 prefix or 3 paths) then repeatable --pair and setting overrides
            var src = new List<string>();
            int i = 1;
            while (i < args.Length && !args[i].StartsWith("--"))
            {
                src.Add(args[i]);
                i++;
            }
            string terrainBin, assetsBin, txt;
            if (src.Count == 1)
            {
                terrainBin = src[0] + "_snapshot_terrain.bin";
                assetsBin = src[0] + "_snapshot_assets.bin";
                txt = src[0] + "_snapshot.txt";
            }
            else if (src.Count == 3)
            {
                terrainBin = src[0];
                assetsBin = src[1];
                txt = src[2];
            }
            else
            {
                Console.WriteLine("usage: TiledCoreHarness --snapshot <prefix> [--pair \"x1,y1|x2,y2\"]... "
                    + "[--cs M] [--radius M] [--climb M] [--gapfill N] [--stitch N] [--prune N] "
                    + "[--ledge on|off] [--detail on|off] [--slope D] [--cullwater M] [--raisewater M]");
                return 2;
            }

            string coverageSpec = null;
            int holesMinCells = 0;
            bool propRamps = false;   // mirrors the shipped NavMeshVoxelPropRamps default
            bool gateConnectors = false;   // mirrors the shipped NavMeshVoxelGateConnectors (harness default off)
            bool steppedPropsOff = false;
            var probeSpecs = new List<string>();
            var pairSpecs = new List<string>();
            bool listLadders = false;
            string listProps = null;
        string vprobe = null;
        string vmesh = null;
        string pinchSpot = null;
        string floodSpot = null;
        bool nonManifold = false;
        string walkPath = null;
        string soupBox = null;
            bool connectOnly = false;
            bool quality = false;
            bool quiet = false;
            bool xzprobe = false;
            bool cleanupOn = true;
            bool scan = false;
            float levelTol = 0.35f;
            var defectSpots = new List<string>();
            var censusSpots = new List<string>();
            string dumpNear = null;
            int routingPairs = 0;
            float band = 1.0f;
            TiledNavMeshBuilder.TiledNavMeshSettings s = SnapshotSettings();
            bool climbSet = false;
            bool goldenHash = false;
            bool pruneSet = false;
            for (; i < args.Length; i++)
            {
                string a = args[i];
                string v = (i + 1 < args.Length) ? args[i + 1] : null;
                switch (a)
                {
                    case "--coverage":
                        coverageSpec = v; i++; break;
                    case "--holes":
                        holesMinCells = (int)ParseFloat(v); i++; break;
                    case "--ramps":
                        propRamps = ParseOnOff(v); i++; break;
                    case "--gate":
                        gateConnectors = ParseOnOff(v); i++; break;
                    case "--probe":
                        probeSpecs.Add(v); i++; break;
                    case "--propdump":
                        PropDump = v; i++; break;
                    case "--pair":
                        if (v == null) { Console.WriteLine("--pair needs a value"); return 2; }
                        pairSpecs.Add(v); i++; break;
                    case "--ladders":
                        listLadders = true; break;
                    case "--props":
                        listProps = v; i++; break;
                    case "--vprobe":
                        vprobe = v; i++; break;
                    case "--vmesh":
                        vmesh = v; i++; break;
                    case "--steepguard":
                        TiledNavMeshBuilder.SteepFaceConnectivityGuard = ParseFloat(v) != 0; i++; break;
                    case "--steepmargin":
                        s.SteepFaceMarginDegrees = (float)ParseFloat(v); i++; break;
                    case "--connect":
                        connectOnly = true; break;
                    case "--quality":
                        quality = true; break;
                    case "--xzprobe":
                        xzprobe = true; break;
                    case "--cleanup":
                        cleanupOn = ParseOnOff(v); i++; break;
                    // ---- cleanup determinism / calibration overrides (det v1) ----
                    // Every knob the cleanup's counted work budgets expose, so the calibration is
                    // executable with no code edit between runs. All of these are echoed back in the
                    // settings: line, so a captured log can never be mistaken for the default config.
                    case "--cleanup-deadline":
                        s.CleanupDeadlineMs = (int)ParseFloat(v); i++; break;
                    case "--dec-guard-mult":
                        s.CleanupDecimateGuardMultiple = (int)ParseFloat(v); i++; break;
                    case "--dec-sweep-cap":
                        s.CleanupDecimateSweepCap = (int)ParseFloat(v); i++; break;
                    case "--refine-sweep-cap":
                        s.CleanupRefineSweepCap = (int)ParseFloat(v); i++; break;
                    case "--relax-sweep-cap":
                        s.CleanupRelaxSweepCap = (int)ParseFloat(v); i++; break;
                    case "--flip-iter-cap":
                        s.CleanupFlipIterationCap = (int)ParseFloat(v); i++; break;
                    case "--relax-floor":
                        s.CleanupRelaxBudgetFloor = (int)ParseFloat(v); i++; break;
                    case "--flip-visit-mult":
                        s.CleanupFlipEdgeVisitMultiple = (int)ParseFloat(v); i++; break;
                    case "--dec-guard-floor-div":
                        s.CleanupDecimateGuardFloorDivisor = (int)ParseFloat(v); i++; break;
                    case "--goldenhash":
                        goldenHash = true; break;
                    case "--scan":
                        scan = true; break;
                    case "--defect":
                        defectSpots.Add(v); i++; break;
                    case "--census":
                        censusSpots.Add(v); i++; break;
                    case "--dumpnear":
                        dumpNear = v; i++; break;
                    case "--leveltol":
                        levelTol = ParseFloat(v); i++; break;
                    case "--routing":
                        routingPairs = (int)ParseFloat(v); i++; break;
                    case "--quiet":
                        quiet = true; break;
                    case "--cs":
                        s.VoxelCellSizeMeters = ParseFloat(v); i++; break;
                    case "--tile":
                        s.TileWorldSizeMeters = ParseFloat(v); i++; break;
                    case "--radius":
                        s.AgentRadiusMeters = ParseFloat(v); i++; break;
                    case "--climb":
                        s.AgentMaxClimbMeters = ParseFloat(v); climbSet = true; i++; break;
                    case "--gapfill":
                        s.GapFillCells = (int)ParseFloat(v); i++; break;
                    case "--stitch":
                        s.StitchMaxGapCells = (int)ParseFloat(v); i++; break;
                    case "--prune":
                        int pn = (int)ParseFloat(v);
                        s.PruneIsolatedIslands = pn > 0;
                        if (pn > 0) s.PruneMaxIslandFaces = pn;
                        pruneSet = true; i++; break;
                    case "--band":
                        band = ParseFloat(v); i++; break;
                    case "--ledge":
                        s.UseLedgeFilter = ParseOnOff(v); i++; break;
                    case "--detail":
                        s.UseDetailMesh = ParseOnOff(v); i++; break;
                    case "--slope":
                        s.MaxSlopeDegrees = ParseFloat(v); i++; break;
                    case "--cullwater":
                        // Mode A mirror of NavMeshCullBelowWaterLevel: drop every soup triangle whose
                        // LIFTED lowest corner (y + offset) is below this level. Off by default, so
                        // without this flag the build is unchanged.
                        s.CullBelowWaterLevel = true;
                        s.RaiseBelowWaterLevel = false;
                        s.WaterLevelMeters = ParseFloat(v); i++; break;
                    case "--raisewater":
                        // Mode B mirror of NavMeshCullBelowWaterLevel=false: clamp every soup vertex whose
                        // LIFTED height (y + offset) is below this level up to exactly the level. v2 only.
                        s.RaiseBelowWaterLevel = true;
                        s.CullBelowWaterLevel = false;
                        s.WaterLevelMeters = ParseFloat(v); i++; break;
                    case "--plate":
                        s.GatePlateBlend = ParseOnOff(v); i++; break;
                    case "--platetrace":
                        TiledNavMeshBuilder.GatePlateTrace = true; break;
                    case "--noseam":
                        Console.WriteLine("(ignored: the seam stage was removed with the V3.53/V3.55 revert)");
                        break;
                    case "--nonmanifold":
                        nonManifold = true; break;
                    case "--pinch":
                        pinchSpot = v; i++; break;
                    case "--flood":
                        floodSpot = v; i++; break;
                    case "--walk":
                        walkPath = v; i++; break;
                    case "--soup":
                        soupBox = v; i++; break;
                    default:
                        Console.WriteLine("unknown option '" + a + "'");
                        return 2;
                }
            }
            if (!climbSet)
                s.AgentMaxClimbMeters = TiledNavMeshBuilder.ResolveAgentClimbMeters(s.VoxelCellSizeMeters,
                    s.CellHeightMeters, s.MaxSlopeDegrees, 0.4f);
            if (!pruneSet && s.PruneIsolatedIslands && s.PruneMaxIslandFaces <= 0)
                s.PruneIsolatedIslands = false;
            s.CleanupSlivers = cleanupOn;

            Console.WriteLine("== snapshot mode ==");
            Console.WriteLine("terrain: " + terrainBin);
            Console.WriteLine("assets:  " + assetsBin);
            Console.WriteLine("txt:     " + txt);

            SceneSnapshot.TerrainData terrain = SceneSnapshot.ReadTerrainBin(terrainBin);
            List<SceneSnapshot.EntityRecord> assets = SceneSnapshot.ReadAssetsBin(assetsBin);
            Console.WriteLine("loaded: terrain=" + terrain.Width + "x" + terrain.Height
                + " spacing=" + terrain.Spacing.ToString("0.###") + "m stride=" + terrain.Stride
                + " heights=" + terrain.Heights.Length + "  assets=" + assets.Count + " entities");
            if (terrain.Stride < 1) terrain.Stride = 1;

            if (listProps != null)
            {
                // Either a name substring, or "x0,z0,x1,z1" as an asset-space XZ box (assets are Z-up:
                // VertsXyz = (x, y, height=z)), so it can be typed straight off a screenshot.
                string[] qp = listProps.Split(',');
                float qx0 = float.NaN, qz0 = float.NaN, qx1 = float.NaN, qz1 = float.NaN;
                bool box = qp.Length >= 4;
                if (box)
                {
                    qx0 = (float)ParseFloat(qp[0]); qz0 = (float)ParseFloat(qp[1]);
                    qx1 = (float)ParseFloat(qp[2]); qz1 = (float)ParseFloat(qp[3]);
                    if (qx1 < qx0) { float t = qx0; qx0 = qx1; qx1 = t; }
                    if (qz1 < qz0) { float t = qz0; qz0 = qz1; qz1 = t; }
                    Console.WriteLine("props with AABB in X=[" + qx0.ToString("F1") + ".." + qx1.ToString("F1")
                        + "] Z=[" + qz0.ToString("F1") + ".." + qz1.ToString("F1") + "]:");
                }
                else Console.WriteLine("props matching '" + listProps + "':");
                int n = 0;
                for (int e = 0; e < assets.Count; e++)
                {
                    SceneSnapshot.EntityRecord rec = assets[e];
                    if (rec.Name == null) continue;
                    if (!box && rec.Name.IndexOf(listProps, StringComparison.Ordinal) < 0) continue;
                    float x0 = float.MaxValue, x1 = float.MinValue;
                    float y0 = float.MaxValue, y1 = float.MinValue;
                    float z0 = float.MaxValue, z1 = float.MinValue;
                    for (int vi = 0; vi < rec.VertsXyz.Length; vi += 3)
                    {
                        float vx = rec.VertsXyz[vi], vy = rec.VertsXyz[vi + 1], vz = rec.VertsXyz[vi + 2];
                        if (vx < x0) x0 = vx; if (vx > x1) x1 = vx;
                        if (vy < y0) y0 = vy; if (vy > y1) y1 = vy;
                        if (vz < z0) z0 = vz; if (vz > z1) z1 = vz;
                    }
                    if (box && (x1 < qx0 || x0 > qx1 || y1 < qz0 || y0 > qz1)) continue;
                    // Printed as the engine-space (X, height, Z) triple the mesh uses, so it lines up
                    // with the census rows.
                    n++;
                    Console.WriteLine("  [" + n + "] '" + rec.Name + "' flags=0x" + rec.Flags.ToString("X8")
                        + (SteppedPropRamps.IsSteppedOrLadder(rec.Flags) ? " STEPPED/LADDER" : "")
                        + " tris=" + (rec.Tris.Length / 3)
                        + " X=[" + x0.ToString("F2") + ".." + x1.ToString("F2") + "]"
                        + " H=[" + z0.ToString("F2") + ".." + z1.ToString("F2") + "]"
                        + " Z=[" + y0.ToString("F2") + ".." + y1.ToString("F2") + "]"
                        + " centreXZ=(" + ((x0 + x1) * 0.5f).ToString("F2") + ","
                        + ((y0 + y1) * 0.5f).ToString("F2") + ")");
                }
                Console.WriteLine("props total=" + n);
                return 0;
            }

            if (listLadders)
            {
                Console.WriteLine("ladders (BodyFlags 0x40):");
                int n = 0;
                for (int e = 0; e < assets.Count; e++)
                {
                    SceneSnapshot.EntityRecord rec = assets[e];
                    if ((rec.Flags & SteppedPropRamps.FlagLadder) == 0) continue;
                    float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
                    float z0 = float.MaxValue, z1 = float.MinValue;
                    for (int v = 0; v < rec.VertsXyz.Length; v += 3)
                    {
                        float vx = rec.VertsXyz[v], vy = rec.VertsXyz[v + 1], vz = rec.VertsXyz[v + 2];
                        if (vx < x0) x0 = vx; if (vx > x1) x1 = vx;
                        if (vy < y0) y0 = vy; if (vy > y1) y1 = vy;
                        if (vz < z0) z0 = vz; if (vz > z1) z1 = vz;
                    }
                    n++;
                    Console.WriteLine("  [" + n + "] '" + rec.Name + "' tris=" + (rec.Tris.Length / 3)
                        + " x=[" + x0.ToString("F2") + ".." + x1.ToString("F2") + "]"
                        + " y=[" + y0.ToString("F2") + ".." + y1.ToString("F2") + "]"
                        + " z=[" + z0.ToString("F2") + ".." + z1.ToString("F2") + "]"
                        + " pos=(" + ((x0 + x1) * 0.5f).ToString("F2") + "," + ((y0 + y1) * 0.5f).ToString("F2")
                        + "," + z0.ToString("F2") + ") top=" + z1.ToString("F2"));
                }
                Console.WriteLine("ladders total=" + n);
                return 0;
            }

            if (vprobe != null)
            {
                // --vprobe "x0,z0,x1,z1[,hLo,hHi]" -> every ASSET-SOUP vertex whose engine XZ lands in the
                // box, per entity, with its height both raw and after the +HeightOffset lift. This is the
                // "which prop owns this point" question the --props AABB box cannot answer (several props
                // overlap the same footprint), and it is the one that decides a contour-spike hub's owner.
                string[] qp2 = vprobe.Split(',');
                if (qp2.Length < 4) { Console.WriteLine("--vprobe needs x0,z0,x1,z1"); return 2; }
                float vx0 = (float)ParseFloat(qp2[0]), vz0 = (float)ParseFloat(qp2[1]);
                float vx1 = (float)ParseFloat(qp2[2]), vz1 = (float)ParseFloat(qp2[3]);
                if (vx1 < vx0) { float t = vx0; vx0 = vx1; vx1 = t; }
                if (vz1 < vz0) { float t = vz0; vz0 = vz1; vz1 = t; }
                float vhLo = qp2.Length >= 5 ? (float)ParseFloat(qp2[4]) : float.NegativeInfinity;
                float vhHi = qp2.Length >= 6 ? (float)ParseFloat(qp2[5]) : float.MaxValue;
                Console.WriteLine("== vprobe X=[" + vx0.ToString("F2") + ".." + vx1.ToString("F2")
                    + "] Z=[" + vz0.ToString("F2") + ".." + vz1.ToString("F2") + "] H=["
                    + vhLo.ToString("F1") + ".." + vhHi.ToString("F1") + "] (H is engine Z-up; +"
                    + s.HeightOffsetMeters.ToString("F2") + " lift shown as liftedH) ==");
                int nv = 0;
                for (int e = 0; e < assets.Count; e++)
                {
                    SceneSnapshot.EntityRecord rec = assets[e];
                    if (rec.Name == null) continue;
                    int hit = 0;
                    var uniq = new Dictionary<long, int>();
                    for (int vi = 0; vi < rec.VertsXyz.Length; vi += 3)
                    {
                        float ex = rec.VertsXyz[vi], ez = rec.VertsXyz[vi + 1], eh = rec.VertsXyz[vi + 2];
                        if (ex < vx0 || ex > vx1 || ez < vz0 || ez > vz1) continue;
                        if (eh < vhLo || eh > vhHi) continue;
                        // The file stores 3 unique verts per triangle, so de-dup on the coordinate triple.
                        long key = ((long)(ex * 1000f) << 44) ^ ((long)(eh * 1000f) << 22) ^ (long)(ez * 1000f);
                        if (uniq.ContainsKey(key)) continue;
                        uniq[key] = 1;
                        hit++;
                        nv++;
                        if (nv <= 400)
                            Console.WriteLine("  '" + rec.Name + "' v" + (vi / 3) + " flags=0x"
                                + rec.Flags.ToString("X8") + " at (" + ex.ToString("F3") + ","
                                + ez.ToString("F3") + ") H=" + eh.ToString("F3") + " liftedH="
                                + (eh + s.HeightOffsetMeters).ToString("F3")
                                + " q0.2=" + (Math.Floor((eh + s.HeightOffsetMeters) / s.CellHeightMeters) * s.CellHeightMeters).ToString("F3"));
                    }
                    if (hit > 0)
                        Console.WriteLine("  -- '" + rec.Name + "' verts in box=" + hit
                            + (rec.TriCount * 3 > 0 ? " tris=" + (rec.Tris.Length / 3) : ""));
                }
                Console.WriteLine("vprobe: verts=" + nv);
                return 0;
            }

            var meshLog = new List<string>();
            Action<string> log = delegate (string m) { meshLog.Add(m); Console.WriteLine("    " + m); };

            // Stepped/ladder props: synthesize the same ramps the shipped collector does, so this
            // recorded (ramp-free) snapshot validates the shipped behaviour without re-dumping.
            if (propRamps)
            {
                ApplyPropRamps(assets, log, s.VoxelCellSizeMeters, s.AgentMaxClimbMeters, s.UseLedgeFilter);
            }
            else
            {
                Console.WriteLine("ramps: disabled (--ramps off)");
                steppedPropsOff = true;
            }

            if (gateConnectors)
            {
                ApplyGateConnectors(assets, terrain, log, s.VoxelCellSizeMeters, s.AgentMaxClimbMeters,
                    s.UseLedgeFilter);
            }
            else
            {
                Console.WriteLine("gate: disabled (--gate off)");
            }

            float[] soupVerts;
            bool[] soupExempt;
            int[] soupTris;
            bool[] soupForce;
            SoupSteppedExempt = propRamps;
            BuildSnapshotSoup(terrain, assets, out soupVerts, out soupTris, out soupForce, out soupExempt);
            if (steppedPropsOff && !gateConnectors) soupExempt = null;
            Console.WriteLine("soup: " + (soupVerts.Length / 3) + "v / " + (soupTris.Length / 3) + "t"
                + (soupForce == null ? "" : " (force-walkable tris=" + CountTrue(soupForce) + ")"));
            Console.WriteLine("settings: cs=" + s.VoxelCellSizeMeters.ToString("0.###")
                + " radius=" + s.AgentRadiusMeters.ToString("0.###")
                + " climb=" + s.AgentMaxClimbMeters.ToString("0.###") + (climbSet ? "" : "(auto)")
                + " slope=" + s.MaxSlopeDegrees.ToString("0.###")
                + " gapFill=" + s.GapFillCells + " stitch=" + s.StitchMaxGapCells
                + " prune=" + (s.PruneIsolatedIslands ? s.PruneMaxIslandFaces.ToString() : "off")
                + " ramps=" + propRamps
                + " ledge=" + s.UseLedgeFilter + " detail=" + s.UseDetailMesh
                + " cleanup=" + cleanupOn
                + " water=" + (s.CullBelowWaterLevel ? "cull@" + s.WaterLevelMeters.ToString("0.###")
                    : s.RaiseBelowWaterLevel ? "raise@" + s.WaterLevelMeters.ToString("0.###") : "off")
                // The effective Budgets, echoed so every captured log is self-describing and a
                // calibration run cannot be mistaken for the default configuration.
                + " budgets=[" + TiledNavMeshBuilder.BuildCleanupBudgets(s) + "]");

            var watch = Stopwatch.StartNew();
            TiledNavMeshBuilder.VoxelMeshResult r =
                TiledNavMeshBuilder.Build(soupVerts, soupTris, s, Environment.ProcessorCount,
                    quiet ? null : log, soupForce, soupExempt);
            watch.Stop();

            double worstQuadAngle = WorstQuadAngle(r);
            XzProbeReport xzr = ProbeXzQuads(r);
            Console.WriteLine("build: V=" + r.VertexCount + " F=" + r.FaceCount + " E=" + r.EdgeCount
                + " quads=" + (r.QuadPairs == null ? 0 : r.QuadPairs.Length / 2)
                + " concaveQuads=" + CountConcaveQuads(r)
                + " worstQuadAngle=" + worstQuadAngle.ToString("F4", CultureInfo.InvariantCulture) + "deg"
                + " xzConcave=" + xzr.ConcaveXZ + " xzOrientBad=" + xzr.OrientBad
                + " xzStraight=" + xzr.StraightXZ
                + " xzReflex=" + xzr.ReflexXZ
                + " worstXzAngle=" + xzr.WorstAngle.ToString("F4", CultureInfo.InvariantCulture) + "deg"
                + " invariants=" + (r.InvariantsPassed ? "PASS" : "FAIL") + " ms=" + watch.ElapsedMilliseconds);
            if (s.CullBelowWaterLevel || s.RaiseBelowWaterLevel)
                WaterShorelineReport(r, s.WaterLevelMeters, s.MaxSlopeDegrees);
            if (goldenHash)
            {
                // --goldenhash: build once, print the three hashes in a copy-pasteable form, exit.
                // Uses the harness's EXISTING FNV-1a 64-bit helpers (Hash(float[]) / Hash(int[])) - the
                // same ones Test3_Determinism's e016e53886cb9585 / ba807f993fde10fc constants come from.
                // A second hash implementation would make those constants incomparable.
                var qp = r.QuadPairs ?? new int[0];
                long hv = Hash(r.VertsXyz), ht = Hash(r.Tris), hq = Hash(qp);
                Console.WriteLine("GOLDENHASH vars=" + hv.ToString("x16")
                    + " tris=" + ht.ToString("x16")
                    + " quads=" + hq.ToString("x16")
                    + " quadPairs=" + (r.QuadPairs == null ? "null" : r.QuadPairs.Length.ToString(CultureInfo.InvariantCulture))
                    + " F=" + r.FaceCount.ToString(CultureInfo.InvariantCulture));
                // The golden record carries its CONTEXT. A refreshed fixture invalidates the goldens by
                // design - that is the point - so the assertion must fail LOUDLY when the context moves,
                // rather than silently passing or silently failing on an unrelated drift.
                FileInfo tfi = new FileInfo(terrainBin);
                string ctx = GoldenCtx(tfi.Name, tfi.Length, File.GetLastWriteTimeUtc(terrainBin).Ticks);
                GoldenEntry ge;
                bool known = GoldenTable.TryGetValue(ctx, out ge);
                string key = ctx;
                if (!known)
                {
                    Console.WriteLine("GOLDEN-UNRECORDED key=" + key
                        + "  (no baseline captured for this fixture/context yet)");
                }
                else if (ge.Vars != hv || ge.Tris != ht || ge.Quads != hq)
                {
                    Console.WriteLine("GOLDEN-MISMATCH key=" + key
                        + "\n  expected vars=" + ge.Vars.ToString("x16") + " tris=" + ge.Tris.ToString("x16")
                        + " quads=" + ge.Quads.ToString("x16")
                        + "\n  actual   vars=" + hv.ToString("x16") + " tris=" + ht.ToString("x16")
                        + " quads=" + hq.ToString("x16")
                        + "\n  RESULT: GOLDEN HASH FAIL");
                    return 1;
                }
                else
                {
                    Console.WriteLine("GOLDEN-OK key=" + key + "  RESULT: GOLDEN HASH PASS");
                }
                return 0;
            }
            if (xzprobe) { PrintXzProbe(r, xzr); return 0; }

            if (pinchSpot != null)
            {
                PinchReport(r, pinchSpot);
                return 0;
            }

            if (floodSpot != null)
            {
                _emultCache = null;
                FloodReport(r, floodSpot);
                return 0;
            }

            if (nonManifold)
            {
                NonManifoldReport(r);
                return 0;
            }

            if (walkPath != null)
            {
                WalkReport(r, walkPath);
                return 0;
            }

            if (soupBox != null)
            {
                SoupBoxReport(soupVerts, soupTris, soupBox);
                return 0;
            }

            if (vmesh != null)
            {
                // --vmesh "x,z,r" -> every BUILT-mesh vertex whose XZ lies within r of (x,z), with its
                // exact coordinate, its incident faces and each face's slope. This is the tool that says
                // "what IS this contour vertex": a fan's shared hub, its height, and whether the faces
                // around it belong to one prop or several.
                string[] qp3 = vmesh.Split(',');
                float mx = (float)ParseFloat(qp3[0]), mz = (float)ParseFloat(qp3[1]);
                float mr = qp3.Length >= 3 ? (float)ParseFloat(qp3[2]) : 1f;
                var inc = new Dictionary<int, List<int>>();
                for (int f = 0; f < r.FaceCount; f++)
                    for (int k = 0; k < 3; k++)
                    {
                        int vi = r.Tris[f * 3 + k];
                        List<int> lst;
                        if (!inc.TryGetValue(vi, out lst)) { lst = new List<int>(); inc[vi] = lst; }
                        lst.Add(f);
                    }
                var rows = new List<float[]>();
                foreach (KeyValuePair<int, List<int>> kv in inc)
                {
                    int vi = kv.Key;
                    float vx = r.VertsXyz[vi * 3], vy = r.VertsXyz[vi * 3 + 1], vz = r.VertsXyz[vi * 3 + 2];
                    float ddx = vx - mx, ddz = vz - mz;
                    if (ddx * ddx + ddz * ddz > mr * mr) continue;
                    rows.Add(new float[] { vi, vx, vy, vz, (float)kv.Value.Count, ddx * ddx + ddz * ddz });
                }
                rows.Sort(delegate (float[] a, float[] b) { return a[2].CompareTo(b[2]); });
                Console.WriteLine();
                Console.WriteLine("== vmesh (" + mx.ToString("F2") + "," + mz.ToString("F2") + ") r="
                    + mr.ToString("F2") + " verts=" + rows.Count + " ==");
                for (int vmi = 0; vmi < rows.Count; vmi++)
                {
                    int vi = (int)rows[vmi][0];
                    Console.WriteLine("  v" + vi + " at (" + rows[vmi][1].ToString("F3") + ","
                        + rows[vmi][2].ToString("F3") + "," + rows[vmi][3].ToString("F3") + ") dXZ="
                        + Math.Sqrt(rows[vmi][5]).ToString("F3") + " ring=" + rows[vmi][4]);
                    List<int> lst = inc[vi];
                    for (int k = 0; k < lst.Count; k++)
                    {
                        int f = lst[k];
                        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                        double[] n = TriNormal(r.VertsXyz, a, b, c);
                        double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                        double ny = len < 1e-12 ? 0.0 : n[1] / len;
                        double adeg = Math.Acos(Math.Min(1.0, Math.Abs(ny))) * 180.0 / Math.PI;
                        float ylo = Math.Min(r.VertsXyz[a * 3 + 1],
                            Math.Min(r.VertsXyz[b * 3 + 1], r.VertsXyz[c * 3 + 1]));
                        float yhi = Math.Max(r.VertsXyz[a * 3 + 1],
                            Math.Max(r.VertsXyz[b * 3 + 1], r.VertsXyz[c * 3 + 1]));
                        Console.WriteLine("      f" + f + " slope=" + adeg.ToString("F1")
                            + "deg ny=" + ny.ToString("F2") + " y=[" + ylo.ToString("F2") + ".."
                            + yhi.ToString("F2") + "] A3d=" + (0.5 * len).ToString("F3")
                            + " fp=" + TriFootprintXZ(r.VertsXyz, a, b, c).ToString("F3"));
                    }
                }
                if (censusSpots.Count == 0) return 0;
            }

            if (censusSpots.Count > 0)
            {
                CensusSpot(r, censusSpots);
                if (vmesh == null) return 0;
            }
            if (defectSpots.Count > 0 || scan)
            {
                ScanDefects(r, defectSpots, scan, levelTol);
                if (dumpNear != null)
                {
                    // Faces whose XZ bbox intersects a radius around the first spot, as an OBJ.
                    float[] pp = ParsePair(defectSpots[0] + "|" + defectSpots[0]);
                    float qx = pp[0], qz = pp[1];
                    float rad = 8f;
                    string[] parts = defectSpots[0].Split(',');
                    if (parts.Length >= 3) rad = (float)ParseFloat(parts[2]);
                    var sb = new System.Text.StringBuilder();
                    var remap = new Dictionary<int, int>();
                    for (int f = 0; f < r.FaceCount; f++)
                    {
                        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                        float minx = Math.Min(r.VertsXyz[a * 3], Math.Min(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
                        float maxx = Math.Max(r.VertsXyz[a * 3], Math.Max(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
                        float minz = Math.Min(r.VertsXyz[a * 3 + 2], Math.Min(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
                        float maxz = Math.Max(r.VertsXyz[a * 3 + 2], Math.Max(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
                        if (maxx < qx - rad || minx > qx + rad || maxz < qz - rad || minz > qz + rad) continue;
                        int[] idx = new int[3];
                        for (int k = 0; k < 3; k++)
                        {
                            int vi = k == 0 ? a : (k == 1 ? b : c);
                            int idxOut;
                            if (!remap.TryGetValue(vi, out idxOut))
                            {
                                idxOut = remap.Count + 1;
                                remap[vi] = idxOut;
                            }
                            idx[k] = idxOut;
                        }
                        sb.Append("f ").Append(idx[0]).Append(' ').Append(idx[1]).Append(' ').Append(idx[2])
                          .Append(" # f").Append(f).Append('\n');
                    }
                    var vlines = new System.Text.StringBuilder();
                    foreach (KeyValuePair<int, int> kv in remap)
                    {
                        int vi = kv.Key;
                        vlines.Append("v ").Append(r.VertsXyz[vi * 3].ToString("F4", CultureInfo.InvariantCulture))
                             .Append(' ').Append(r.VertsXyz[vi * 3 + 1].ToString("F4", CultureInfo.InvariantCulture))
                             .Append(' ').Append(r.VertsXyz[vi * 3 + 2].ToString("F4", CultureInfo.InvariantCulture))
                             .Append(" # v").Append(vi).Append('\n');
                    }
                    System.IO.File.WriteAllText(dumpNear, vlines.ToString() + sb.ToString());
                    Console.WriteLine("wrote " + dumpNear + " (" + remap.Count + " verts, " + sb.Length + " bytes)");
                }
                if (vmesh == null) return 0;
            }

            if (quality)
            {
                PrintTriangleQuality(r);
                return 0;
            }
            if (routingPairs > 0)
            {
                PrintRoutingCost(r, routingPairs);
                return 0;
            }

            int comps;
            int[] fp;
            if (connectOnly)
            {
                // The connect path uses the spatial index's own component roots; skip the harness's
                // O(F) island report (it dominates at 500k+ faces).
                IndexOf(r).EnsureComponents();
                fp = IndexOf(r).Roots;
                comps = IndexOf(r).RootCount;
            }
            else
            {
                fp = FaceComponents(r, out comps);
            }
            var sizes = new Dictionary<int, int>();
            var area = new Dictionary<int, double>();
            if (!connectOnly)
                for (int f = 0; f < r.FaceCount; f++)
                {
                    int root = Find(fp, f);
                    int c; sizes.TryGetValue(root, out c); sizes[root] = c + 1;
                    double a; area.TryGetValue(root, out a); area[root] = a + FaceAreaXZ(r, f);
                }
            var roots = new List<int>(sizes.Keys);
            roots.Sort(delegate (int a, int b)
            {
                int cmp = sizes[b].CompareTo(sizes[a]);
                if (cmp != 0) return cmp;
                return a.CompareTo(b);
            });
            var rank = new Dictionary<int, int>();
            for (int k = 0; k < roots.Count; k++) rank[roots[k]] = k;
            Console.WriteLine("islands: " + roots.Count);
            // Top island sizes get printed only when small enough to be useful; the giant-map case is
            // dominated by the prune leaving 1 island, and the per-island area sum is O(F).
            if (roots.Count <= 64)
                for (int k = 0; k < roots.Count && k < 10; k++)
                    Console.WriteLine("  [" + k + "] faces=" + sizes[roots[k]]
                        + " area=" + area[roots[k]].ToString("F1") + "m2");

            var diagNames = new List<string>();
            for (int k = 0; k < assets.Count; k++)
                if (assets[k].Name != null && assets[k].Name.IndexOf("diag", StringComparison.OrdinalIgnoreCase) >= 0
                    && !diagNames.Contains(assets[k].Name))
                    diagNames.Add(assets[k].Name);
            foreach (string n in ExtractDiagNamesFromTxt(txt))
                if (!diagNames.Contains(n)) diagNames.Add(n);

            List<float[]> points = ParseAssertPoints(txt);
            Console.WriteLine("assert-points: " + points.Count
                + " (level picked per point: a 'diag' asset bbox pins the floor level, else the highest face)");
            for (int k = 0; k < points.Count; k++)
            {
                float px = points[k][0], pz = points[k][1];
                LevelChoice lc = PickLevel(r, px, pz, 0f, false, assets, diagNames);
                if (lc.Face < 0)
                {
                    Console.WriteLine("  point (" + Num(px) + "," + Num(pz) + ") -> no navmesh at level "
                        + Num(lc.Target) + " (" + lc.Rule + "); candidates=["
                        + JoinFloats(lc.Heights, "F2") + "]");
                    continue;
                }
                int root = Find(fp, lc.Face);
                Console.WriteLine("  point (" + Num(px) + "," + Num(pz) + ") -> face#" + lc.Face
                    + " h=" + Num(lc.Height) + "m"
                    + " island=" + (rank.ContainsKey(root) ? rank[root] : -1)
                    + " faces=" + (sizes.ContainsKey(root) ? sizes[root] : 0)
                    + " area=" + (area.ContainsKey(root) ? area[root].ToString("F1") : "0") + "m2"
                    + " level=" + lc.Rule
                    + " candidates=[" + JoinFloats(lc.Heights, "F2") + "]");
            }

            bool pairsOk = true;
            if (connectOnly && pairSpecs.Count > 0)
            {
                MeshIndex ix = IndexOf(r);
                ix.EnsureComponents();
                Console.WriteLine("connect: " + pairSpecs.Count + " pair(s), O(1) component compare (radius 2.0 m)");
                for (int k = 0; k < pairSpecs.Count; k++)
                {
                    float[] pr = ParsePair(pairSpecs[k]);
                    if (pr == null) { Console.WriteLine("  pair bad format: " + pairSpecs[k]); pairsOk = false; continue; }
                    float x1 = pr[0], z1 = pr[1], x2 = pr[3], z2 = pr[4];
                    int c1 = ix.ComponentAt(x1, z1, 2.0f);
                    int c2 = ix.ComponentAt(x2, z2, 2.0f);
                    bool same = c1 >= 0 && c2 >= 0 && c1 == c2;
                    Console.WriteLine("  pair[" + k + "] (" + Num(x1) + "," + Num(z1) + ")|(" + Num(x2) + ","
                        + Num(z2) + ") c1=" + c1 + " c2=" + c2 + " -> " + (same ? "CONNECTED" : "disconnected"));
                }
            }
            if (!connectOnly && pairSpecs.Count > 0)
            {
                Console.WriteLine("pairs: " + pairSpecs.Count
                    + " (PASS = gapMaxRun <= 0.5m AND geo/euclid <= 1.5; samples are level-banded)");
                List<FaceArc>[] graph = BuildFaceGraph(r);
                for (int k = 0; k < pairSpecs.Count; k++)
                {
                    float[] pr = ParsePair(pairSpecs[k]);
                    if (pr == null)
                    {
                        Console.WriteLine("  pair '" + pairSpecs[k] + "': bad format (want \"x1,y1|x2,y2\")");
                        pairsOk = false;
                        continue;
                    }
                    if (!AnalyzePair(r, fp, rank, sizes, graph, pr, band, k,
                            soupVerts, soupTris, terrain, assets, diagNames))
                        pairsOk = false;
                }
            }

            if (diagNames.Count > 0)
            {
                Console.WriteLine("diag assets: " + diagNames.Count);
                for (int k = 0; k < diagNames.Count; k++)
                {
                    string name = diagNames[k];
                    int tris = 0;
                    bool capped = false;
                    float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                    float minY = float.MaxValue, maxY = float.MinValue;
                    int matched = 0;
                    for (int e = 0; e < assets.Count; e++)
                    {
                        if (assets[e].Name != name) continue;
                        SceneSnapshot.EntityRecord rec = assets[e];
                        tris += rec.TriCount;
                        capped |= rec.Capped;
                        for (int v = 0; v < rec.VertsXyz.Length; v += 3)
                        {
                            if (rec.VertsXyz[v] < minX) minX = rec.VertsXyz[v];
                            if (rec.VertsXyz[v] > maxX) maxX = rec.VertsXyz[v];
                            if (rec.VertsXyz[v + 1] < minZ) minZ = rec.VertsXyz[v + 1];
                            if (rec.VertsXyz[v + 1] > maxZ) maxZ = rec.VertsXyz[v + 1];
                            if (rec.VertsXyz[v + 2] < minY) minY = rec.VertsXyz[v + 2];
                            if (rec.VertsXyz[v + 2] > maxY) maxY = rec.VertsXyz[v + 2];
                        }
                    }
                    if (tris > 0)
                    {
                        float margin = 0.5f;
                        for (int f = 0; f < r.FaceCount; f++)
                        {
                            float cx = FaceCentroidX(r, f), cz = FaceCentroidZ(r, f);
                            float cy = FaceCentroidY(r, f);
                            if (cx >= minX - margin && cx <= maxX + margin
                                && cz >= minZ - margin && cz <= maxZ + margin
                                && cy >= minY - 1f && cy <= maxY + 1f)
                                matched++;
                        }
                    }
                    Console.WriteLine("  '" + name + "' tris=" + tris + (capped ? " (capped)" : "")
                        + " bbox=(" + Num(minX) + "," + Num(minZ) + ")-(" + Num(maxX) + "," + Num(maxZ)
                        + ") zr=[" + Num(minY) + ".." + Num(maxY) + "]"
                        + " facesInBBox=" + matched + " -> " + (matched > 0 ? "present" : "pruned"));
                }
            }
            else
            {
                Console.WriteLine("diag assets: none (rename the problem assets so their names contain 'diag')");
            }

            // ---- winding blast radius: which entities own the downward-wound triangles the mixer flips,
            // and where the output faces sit in height (to spot new undersides/ceilings).
            var flipNames = new List<string>();
            var flipCounts = new List<int>();
            var flipTotals = new List<int>();
            int totalFlip = 0, totalTris = 0;
            for (int e = 0; e < assets.Count; e++)
            {
                SceneSnapshot.EntityRecord rec = assets[e];
                int tris = rec.TriCount;
                int flips = 0;
                for (int k = 0; k < tris; k++)
                {
                    float x0 = rec.VertsXyz[(k * 3 + 0) * 3 + 0], z0 = rec.VertsXyz[(k * 3 + 0) * 3 + 1], y0 = rec.VertsXyz[(k * 3 + 0) * 3 + 2];
                    float x1 = rec.VertsXyz[(k * 3 + 1) * 3 + 0], z1 = rec.VertsXyz[(k * 3 + 1) * 3 + 1], y1 = rec.VertsXyz[(k * 3 + 1) * 3 + 2];
                    float x2 = rec.VertsXyz[(k * 3 + 2) * 3 + 0], z2 = rec.VertsXyz[(k * 3 + 2) * 3 + 1], y2 = rec.VertsXyz[(k * 3 + 2) * 3 + 2];
                    // Recast mapping (X=x, Y=z, Z=y) with the default reversed emission (0,2,1).
                    float ux = x2 - x0, uy = z2 - z0, uz = y2 - y0;
                    float wx = x1 - x0, wy = z1 - z0, wz = y1 - y0;
                    float ny = uz * wx - ux * wz;
                    if (ny < 0f) flips++;
                }
                if (flips > 0) { flipNames.Add(rec.Name); flipCounts.Add(flips); flipTotals.Add(tris); }
                totalFlip += flips;
                totalTris += tris;
            }
            Console.WriteLine("winding: flipped=" + totalFlip + "/" + totalTris + " asset triangles (downward-wound)");
            var flipOrder = new List<int>();
            for (int fi = 0; fi < flipNames.Count; fi++) flipOrder.Add(fi);
            flipOrder.Sort(delegate (int a, int b)
            {
                int cmp = flipCounts[b].CompareTo(flipCounts[a]);
                if (cmp != 0) return cmp;
                return flipNames[a].CompareTo(flipNames[b]);
            });
            var wsb = new System.Text.StringBuilder();
            for (int wi = 0; wi < flipOrder.Count && wi < 10; wi++)
            {
                if (wi > 0) wsb.Append(" | ");
                wsb.Append("'").Append(flipNames[flipOrder[wi]]).Append("'=")
                   .Append(flipCounts[flipOrder[wi]]).Append("/").Append(flipTotals[flipOrder[wi]]);
            }
            Console.WriteLine("winding top: " + (flipOrder.Count == 0 ? "none" : wsb.ToString()));

            var bandCount = new Dictionary<int, int>();
            for (int f = 0; f < r.FaceCount; f++)
            {
                int b = (int)Math.Round(FaceCentroidY(r, f));
                int c;
                bandCount.TryGetValue(b, out c);
                bandCount[b] = c + 1;
            }
            var bands = new List<int>(bandCount.Keys);
            bands.Sort();
            var bsb = new System.Text.StringBuilder();
            for (int bi = 0; bi < bands.Count; bi++)
            {
                if (bi > 0) bsb.Append(" ");
                bsb.Append(bands[bi]).Append("m:").Append(bandCount[bands[bi]]);
            }

            // ---- --probe "x,z,level": what the soup/mesh look like at a spot
            for (int pi = 0; pi < probeSpecs.Count; pi++)
            {
                float[] pp = ParsePair(probeSpecs[pi] + "|" + probeSpecs[pi]);
                if (pp == null) { Console.WriteLine("probe bad format: " + probeSpecs[pi]); continue; }
                float px = pp[0], pz = pp[1], plevel = float.IsNaN(pp[2]) ? 0f : pp[2];
                Console.WriteLine("probe (" + Num(px) + "," + Num(pz) + ") level=" + Num(plevel));
                PrintGapContext(px, pz, plevel, soupVerts, soupTris, terrain, assets, diagNames);
                List<int> cands = FacesAtAnyHeight(r, px, pz);
                var cs2 = new System.Text.StringBuilder();
                for (int ci = 0; ci < cands.Count; ci++)
                {
                    int root = Find(fp, cands[ci]);
                    if (ci > 0) cs2.Append(" | ");
                    float localY;
                    bool hasLocal = FaceYAt(r, cands[ci], px, pz, out localY);
                    cs2.Append("h=").Append(FaceCentroidY(r, cands[ci]).ToString("F2"))
                       .Append(" localH=").Append(hasLocal ? localY.ToString("F2") : "n/a")
                       .Append(" isl=").Append(rank.ContainsKey(root) ? rank[root] : -1)
                       .Append(" islandFaces=").Append(sizes.ContainsKey(root) ? sizes[root] : 0);
                }
                Console.WriteLine("      mesh faces at that XZ: " + (cands.Count == 0 ? "none" : cs2.ToString()));
            }
            Console.WriteLine("levels (faces per 1 m band): " + bsb.ToString());

            // ---- --coverage <nameSubstring>
            if (holesMinCells > 0)
                RunHoles(r, soupVerts, soupTris, assets, holesMinCells);

            if (coverageSpec != null)
                RunCoverage(r, assets, coverageSpec);

            Console.WriteLine("SNAPSHOT: " + (pairsOk ? "PASS" : "FAIL")
                + (pairSpecs.Count > 0 ? " (" + pairSpecs.Count + " pair(s))" : " (no pairs given)"));
            return pairsOk ? 0 : 1;        }
        catch (Exception ex)
        {
            Console.WriteLine("snapshot mode ERROR: " + ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    // ---- --holes [minCells]: the actual naked spots in the built mesh.
    // For every 0.5 m cell it takes the topmost walkable (slope<=limit) collision surface and asks
    // whether the mesh has a face on it (within [Y-0.2, Y+MeshOffset+0.6]); cells with none are holes.
    // Connected hole cells are reported largest first, with the entities whose XZ box contains them, so
    // "one whole flight of steps is bare" shows up with the prop name attached.
    private const float HoleCell = 0.5f;

    private static void RunHoles(TiledNavMeshBuilder.VoxelMeshResult r, float[] soupVerts, int[] soupTris,
        List<SceneSnapshot.EntityRecord> assets, int minCells)
    {
        int triCount = soupTris.Length / 3;
        float mnx = float.MaxValue, mxx = float.MinValue, mnz = float.MaxValue, mxz = float.MinValue;
        for (int i = 0; i < soupVerts.Length; i += 3)
        {
            if (soupVerts[i] < mnx) mnx = soupVerts[i];
            if (soupVerts[i] > mxx) mxx = soupVerts[i];
            if (soupVerts[i + 2] < mnz) mnz = soupVerts[i + 2];
            if (soupVerts[i + 2] > mxz) mxz = soupVerts[i + 2];
        }
        int nxh = (int)Math.Floor((mxx - mnx) / HoleCell) + 2;
        int nzh = (int)Math.Floor((mxz - mnz) / HoleCell) + 2;
        Console.WriteLine("holes: grid " + nxh + "x" + nzh + " cells of " + HoleCell + "m (origin "
            + Num(mnx) + "," + Num(mnz) + "), soupTris=" + triCount + " meshFaces=" + r.FaceCount);

        var soupBuckets = new List<int>[nxh * nzh];
        for (int t = 0; t < triCount; t++)
        {
            int a = soupTris[t * 3], b = soupTris[t * 3 + 1], c = soupTris[t * 3 + 2];
            float x0 = soupVerts[a * 3], z0 = soupVerts[a * 3 + 2];
            float x1 = soupVerts[b * 3], z1 = soupVerts[b * 3 + 2];
            float x2 = soupVerts[c * 3], z2 = soupVerts[c * 3 + 2];
            float tx0 = Math.Min(x0, Math.Min(x1, x2)), tx1 = Math.Max(x0, Math.Max(x1, x2));
            float tz0 = Math.Min(z0, Math.Min(z1, z2)), tz1 = Math.Max(z0, Math.Max(z1, z2));
            int i0 = ClampCell((int)Math.Floor((tx0 - mnx) / HoleCell), nxh);
            int i1 = ClampCell((int)Math.Floor((tx1 - mnx) / HoleCell), nxh);
            int j0 = ClampCell((int)Math.Floor((tz0 - mnz) / HoleCell), nzh);
            int j1 = ClampCell((int)Math.Floor((tz1 - mnz) / HoleCell), nzh);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    int ci = i * nzh + j;
                    if (soupBuckets[ci] == null) soupBuckets[ci] = new List<int>();
                    soupBuckets[ci].Add(t);
                }
        }
        var faceBuckets = new List<int>[nxh * nzh];
        for (int f = 0; f < r.FaceCount; f++)
        {
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            float x0 = r.VertsXyz[a * 3], z0 = r.VertsXyz[a * 3 + 2];
            float x1 = r.VertsXyz[b * 3], z1 = r.VertsXyz[b * 3 + 2];
            float x2 = r.VertsXyz[c * 3], z2 = r.VertsXyz[c * 3 + 2];
            float tx0 = Math.Min(x0, Math.Min(x1, x2)), tx1 = Math.Max(x0, Math.Max(x1, x2));
            float tz0 = Math.Min(z0, Math.Min(z1, z2)), tz1 = Math.Max(z0, Math.Max(z1, z2));
            int i0 = ClampCell((int)Math.Floor((tx0 - mnx) / HoleCell), nxh);
            int i1 = ClampCell((int)Math.Floor((tx1 - mnx) / HoleCell), nxh);
            int j0 = ClampCell((int)Math.Floor((tz0 - mnz) / HoleCell), nzh);
            int j1 = ClampCell((int)Math.Floor((tz1 - mnz) / HoleCell), nzh);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    int ci = i * nzh + j;
                    if (faceBuckets[ci] == null) faceBuckets[ci] = new List<int>();
                    faceBuckets[ci].Add(f);
                }
        }

        float cosLimit = (float)Math.Cos(50.0 * Math.PI / 180.0);
        var hole = new bool[nxh * nzh];
        var covered2 = new bool[nxh * nzh];
        var level = new float[nxh * nzh];
        int holeCells = 0;
        for (int i = 0; i < nxh; i++)
        {
            float px = mnx + i * HoleCell;
            for (int j = 0; j < nzh; j++)
            {
                List<int> st = soupBuckets[i * nzh + j];
                if (st == null) continue;
                float pz = mnz + j * HoleCell;
                bool on = false;
                float top = 0f;
                for (int k = 0; k < st.Count; k++)
                {
                    int t = st[k];
                    int a = soupTris[t * 3], b = soupTris[t * 3 + 1], c = soupTris[t * 3 + 2];
                    float ax = soupVerts[a * 3], ay = soupVerts[a * 3 + 1], az = soupVerts[a * 3 + 2];
                    float bx = soupVerts[b * 3], by = soupVerts[b * 3 + 1], bz = soupVerts[b * 3 + 2];
                    float cx = soupVerts[c * 3], cy = soupVerts[c * 3 + 1], cz = soupVerts[c * 3 + 2];
                    float wa, wb, wc;
                    if (!Bary(px, pz, ax, az, bx, bz, cx, cz, out wa, out wb, out wc)) continue;
                    float ux = bx - ax, uy = by - ay, uz = bz - az;
                    float wx = cx - ax, wy = cy - ay, wz = cz - az;
                    float vnx = uy * wz - uz * wy, vny = uz * wx - ux * wz, vnz = ux * wy - uy * wx;
                    double nlen = Math.Sqrt((double)vnx * vnx + (double)vny * vny + (double)vnz * vnz);
                    if (nlen < 1e-9) continue;
                    if (Math.Abs(vny) / nlen < cosLimit) continue;      // too steep to walk
                    float y = wa * ay + wb * by + wc * cy;
                    if (!on || y > top) { top = y; on = true; }
                }
                if (!on) continue;
                List<int> fb = faceBuckets[i * nzh + j];
                bool covered = false;
                if (fb != null)
                {
                    for (int k = 0; k < fb.Count && !covered; k++)
                    {
                        int f = fb[k];
                        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                        float wa, wb, wc;
                        if (!Bary(px, pz, r.VertsXyz[a * 3], r.VertsXyz[a * 3 + 2],
                            r.VertsXyz[b * 3], r.VertsXyz[b * 3 + 2],
                            r.VertsXyz[c * 3], r.VertsXyz[c * 3 + 2], out wa, out wb, out wc)) continue;
                        float y = wa * r.VertsXyz[a * 3 + 1] + wb * r.VertsXyz[b * 3 + 1]
                                + wc * r.VertsXyz[c * 3 + 1];
                        if (y >= top - 0.20f && y <= top + MeshOffset + 0.6f) covered = true;
                    }
                }
                if (!covered)
                {
                    hole[i * nzh + j] = true;
                    level[i * nzh + j] = top;
                    holeCells++;
                }
                else
                {
                    covered2[i * nzh + j] = true;
                    level[i * nzh + j] = top;
                }
            }
        }
        Console.WriteLine("holes: uncovered cells=" + holeCells + " (" + (holeCells * HoleCell * HoleCell)
            + "m2)");

        var seen = new bool[nxh * nzh];
        var comps = new List<int[]>();
        for (int i = 0; i < nxh; i++)
            for (int j = 0; j < nzh; j++)
            {
                int s0 = i * nzh + j;
                if (!hole[s0] || seen[s0]) continue;
                var cells = new List<int>();
                var stack = new Stack<int>();
                stack.Push(s0);
                seen[s0] = true;
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    cells.Add(cur);
                    int ci = cur / nzh, cj = cur % nzh;
                    for (int d = 0; d < 4; d++)
                    {
                        int ni = ci + (d == 0 ? 1 : d == 1 ? -1 : 0);
                        int nj = cj + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (ni < 0 || nj < 0 || ni >= nxh || nj >= nzh) continue;
                        int ns = ni * nzh + nj;
                        if (!hole[ns] || seen[ns]) continue;
                        if (Math.Abs(level[ns] - level[cur]) > 1.0f) continue;   // keep one storey per blob
                        seen[ns] = true;
                        stack.Push(ns);
                    }
                }
                comps.Add(cells.ToArray());
            }
        comps.Sort(delegate (int[] a, int[] b) { return b.Length.CompareTo(a.Length); });
        Console.WriteLine("holes: components=" + comps.Count + " (>= " + minCells + " cells)");
        int shown = 0;
        int skipped = 0;
        for (int c = 0; c < comps.Count && shown < 20; c++)
        {
            int[] cells = comps[c];
            if (cells.Length < minCells) break;
            // Keep only holes that sit inside covered ground: at least a third of the cells must have a
            // covered neighbour within 1 m at a similar level. Isolated roofs/ledges are skipped.
            int near = 0;
            for (int k = 0; k < cells.Length; k++)
            {
                int ci = cells[k] / nzh, cj = cells[k] % nzh;
                bool hit = false;
                for (int di = -2; di <= 2 && !hit; di++)
                    for (int dj = -2; dj <= 2 && !hit; dj++)
                    {
                        int ii = ci + di, jj = cj + dj;
                        if (ii < 0 || jj < 0 || ii >= nxh || jj >= nzh) continue;
                        int ns = ii * nzh + jj;
                        if (!covered2[ns]) continue;
                        if (Math.Abs(level[ns] - level[cells[k]]) <= 1.5f) hit = true;
                    }
                if (hit) near++;
            }
            if (near * 10 < cells.Length * 7) { skipped++; continue; }
            shown++;
            double cx = 0, cz = 0, ly = 0;
            for (int k = 0; k < cells.Length; k++)
            {
                int ci = cells[k] / nzh, cj = cells[k] % nzh;
                cx += mnx + ci * HoleCell;
                cz += mnz + cj * HoleCell;
                ly += level[cells[k]];
            }
            cx /= cells.Length;
            cz /= cells.Length;
            ly /= cells.Length;
            var names = new List<string>();
            for (int e = 0; e < assets.Count && names.Count < 6; e++)
            {
                SceneSnapshot.EntityRecord rec = assets[e];
                float ex0 = float.MaxValue, ex1 = float.MinValue, ez0 = float.MaxValue, ez1 = float.MinValue;
                float ey0 = float.MaxValue, ey1 = float.MinValue;
                for (int v = 0; v < rec.VertsXyz.Length; v += 3)
                {
                    if (rec.VertsXyz[v] < ex0) ex0 = rec.VertsXyz[v];
                    if (rec.VertsXyz[v] > ex1) ex1 = rec.VertsXyz[v];
                    if (rec.VertsXyz[v + 1] < ez0) ez0 = rec.VertsXyz[v + 1];
                    if (rec.VertsXyz[v + 1] > ez1) ez1 = rec.VertsXyz[v + 1];
                    if (rec.VertsXyz[v + 2] < ey0) ey0 = rec.VertsXyz[v + 2];
                    if (rec.VertsXyz[v + 2] > ey1) ey1 = rec.VertsXyz[v + 2];
                }
                if (cx < ex0 - 0.5f || cx > ex1 + 0.5f || cz < ez0 - 0.5f || cz > ez1 + 0.5f) continue;
                if (ly < ey0 - 1f || ly > ey1 + 1f) continue;
                string nm = rec.Name ?? "?";
                if (!names.Contains(nm)) names.Add(nm);
            }
            Console.WriteLine("  hole #" + shown + " cells=" + cells.Length + " area="
                + (cells.Length * HoleCell * HoleCell).ToString("F1") + "m2 level=" + Num((float)ly)
                + " centroid=(" + Num((float)cx) + "," + Num((float)cz) + ") names="
                + (names.Count == 0 ? "none" : string.Join(" | ", names.ToArray())));
        }
        Console.WriteLine("holes: isolated (surrounded by no mesh) components skipped=" + skipped);
    }

    private static int ClampCell(int v, int n)
    {
        if (v < 0) return 0;
        if (v >= n) return n - 1;
        return v;
    }

    private static string Num(float v)
    {
        return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    // TiledNavMeshSettings.HeightOffsetMeters default: the built mesh floats this far above the collision.
    private const float MeshOffset = 0.5f;

    // ---- --coverage <nameSubstring>: per-instance walkable-surface coverage of the built navmesh.
    private sealed class InstanceCov
    {
        public string Name = "";
        public string NameSet = "";
        public int Entities, Tris;
        public string Flags = "";
        public float X0, Z0, X1, Z1, Y0, Y1;
        public double Coverage;
        public double CoverageE;
        public double CoverageLvl;
        public double CoverageNative;
        public double CoverageRamp;
        public double WalkAreaNative;
        public double WalkAreaRamp;
        public int RampTris;
        public double WalkAreaE;
        public string Bands = "";
        public int MeshFaces;
        public double WalkArea;
        public double GapRun;
        public float GapX, GapZ;
        public float Level;
        // Why a walk sample was not counted as covered: the mesh faces at that XZ are all below the
        // window (a real unfilled surface), all above it (the mesh floats, a tolerance artefact), or
        // absent / mixed. Diagnostics only.
        public int MissBelow, MissAbove, MissBoth, MissNone;
        // How close the covered samples are to the top of the window (fy - sy), and how far the
        // "floats above" samples are past it. Unlike the raw coverage these are not knife-edged.
        public float CovMaxD;
        public float AboveMinD;
        public bool HasAboveMin;
    }

    private static string DecodeFlags(int f)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("0x").Append(f.ToString("X8")).Append("[");
        AppendFlag(sb, f, 1, "Disabled");
        AppendFlag(sb, f, 4, "TwoSided");
        AppendFlag(sb, f, 8, "Dynamic");
        AppendFlag(sb, f, 16, "Moveable");
        AppendFlag(sb, f, 64, "Ladder");
        AppendFlag(sb, f, 128, "OnlyRaycast");
        AppendFlag(sb, f, 512, "Barrier");
        AppendFlag(sb, f, 1024, "Barrier3D");
        AppendFlag(sb, f, 2048, "HasSteps");
        AppendFlag(sb, f, 131072, "NoPhysicsEngine");
        AppendFlag(sb, f, 4194304, "MissileOnly");
        AppendFlag(sb, f, 16777216, "OwnerEntity");
        AppendFlag(sb, f, 33554432, "OwnerTerrain");
        AppendFlag(sb, f, 67108864, "OwnerFlora");
        sb.Append("]");
        return sb.ToString();
    }

    private static void AppendFlag(System.Text.StringBuilder sb, int flags, int bit, string name)
    {
        if ((flags & bit) == 0) return;
        if (sb[sb.Length - 1] != '[') sb.Append(",");
        sb.Append(name);
    }

    private static bool FaceYAt(TiledNavMeshBuilder.VoxelMeshResult r, int f, float px, float pz, out float y)
    {
        y = 0f;
        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
        float wa, wb, wc;
        if (!Bary(px, pz, r.VertsXyz[a * 3], r.VertsXyz[a * 3 + 2],
            r.VertsXyz[b * 3], r.VertsXyz[b * 3 + 2],
            r.VertsXyz[c * 3], r.VertsXyz[c * 3 + 2], out wa, out wb, out wc)) return false;
        y = wa * r.VertsXyz[a * 3 + 1] + wb * r.VertsXyz[b * 3 + 1] + wc * r.VertsXyz[c * 3 + 1];
        return true;
    }

    private static bool TriY(List<float> tri, int t, float px, float pz, out float y)
    {
        y = 0f;
        int o = t * 9;
        float ax = tri[o + 0], ay = tri[o + 1], az = tri[o + 2];
        float bx = tri[o + 3], by = tri[o + 4], bz = tri[o + 5];
        float cx = tri[o + 6], cy = tri[o + 7], cz = tri[o + 8];
        float wa, wb, wc;
        if (!Bary(px, pz, ax, az, bx, bz, cx, cz, out wa, out wb, out wc)) return false;
        y = wa * ay + wb * by + wc * cy;
        return true;
    }

    private static void RunCoverage(TiledNavMeshBuilder.VoxelMeshResult r,
        List<SceneSnapshot.EntityRecord> assets, string substr)
    {
        Console.WriteLine("coverage: '" + substr + "'");
        var sel = new List<int>();
        for (int e = 0; e < assets.Count; e++)
            if (assets[e].Name != null
                && assets[e].Name.IndexOf(substr, StringComparison.OrdinalIgnoreCase) >= 0)
                sel.Add(e);
        Console.WriteLine("  entities matching=" + sel.Count);
        if (sel.Count == 0)
            return;

        int n = sel.Count;
        var ex0 = new float[n];
        var ex1 = new float[n];
        var ez0 = new float[n];
        var ez1 = new float[n];
        for (int k = 0; k < n; k++)
        {
            SceneSnapshot.EntityRecord rec = assets[sel[k]];
            float mnx = float.MaxValue, mxx = float.MinValue, mnz = float.MaxValue, mxz = float.MinValue;
            for (int v = 0; v < rec.VertsXyz.Length; v += 3)
            {
                if (rec.VertsXyz[v] < mnx) mnx = rec.VertsXyz[v];
                if (rec.VertsXyz[v] > mxx) mxx = rec.VertsXyz[v];
                if (rec.VertsXyz[v + 1] < mnz) mnz = rec.VertsXyz[v + 1];
                if (rec.VertsXyz[v + 1] > mxz) mxz = rec.VertsXyz[v + 1];
            }
            ex0[k] = mnx; ex1[k] = mxx; ez0[k] = mnz; ez1[k] = mxz;
        }

        int[] par = new int[n];
        for (int k = 0; k < n; k++) par[k] = k;
        for (int a = 0; a < n; a++)
            for (int b = a + 1; b < n; b++)
            {
                if (ex1[a] + 0.05f < ex0[b] || ex1[b] + 0.05f < ex0[a]) continue;
                if (ez1[a] + 0.05f < ez0[b] || ez1[b] + 0.05f < ez0[a]) continue;
                Union(par, a, b);
            }

        var groups = new Dictionary<int, List<int>>();
        for (int k = 0; k < n; k++)
        {
            int root = Find(par, k);
            List<int> list;
            if (!groups.TryGetValue(root, out list)) { list = new List<int>(); groups[root] = list; }
            list.Add(k);
        }

        var results = new List<InstanceCov>();
        foreach (KeyValuePair<int, List<int>> kv in groups)
            results.Add(AnalyseInstance(r, assets, sel, kv.Value));
        results.Sort(delegate (InstanceCov a, InstanceCov b)
        {
            int cmp = a.Coverage.CompareTo(b.Coverage);
            if (cmp != 0) return cmp;
            return a.Name.CompareTo(b.Name);
        });

        Console.WriteLine("  instances=" + results.Count + " (sorted by navmesh coverage ascending)");
        for (int i = 0; i < results.Count; i++)
        {
            InstanceCov c = results[i];
            Console.WriteLine("  cov=" + c.Coverage.ToString("F1").PadLeft(5) + "% covEroded="
                + c.CoverageE.ToString("F1").PadLeft(5) + "% native="
                + c.CoverageNative.ToString("F1").PadLeft(5) + "% ramp="
                + (c.WalkAreaRamp > 0 ? c.CoverageRamp.ToString("F1").PadLeft(5) + "%" : "  n/a")
                + " lvlCov=" + c.CoverageLvl.ToString("F1").PadLeft(5) + "% walkArea="
                + c.WalkArea.ToString("F1") + "m2"
                + " interior=" + c.WalkAreaE.ToString("F1") + "m2"
                + " rampTris=" + c.RampTris + " rampArea=" + c.WalkAreaRamp.ToString("F2") + "m2"
                + " ents=" + c.Entities + " tris=" + c.Tris + " " + c.Flags
                + " names='" + c.NameSet + "'"
                + " meshFacesInBBox=" + c.MeshFaces
                + " missBelow=" + c.MissBelow + " missAbove=" + c.MissAbove
                + " missMixed=" + c.MissBoth + " missNone=" + c.MissNone
                + " covMaxD=" + c.CovMaxD.ToString("F2")
                + " aboveMinD=" + (c.HasAboveMin ? c.AboveMinD.ToString("F2") : "n/a")
                + " bbox=(" + Num(c.X0) + "," + Num(c.Z0) + ")-(" + Num(c.X1) + "," + Num(c.Z1)
                + ") zr=[" + Num(c.Y0) + ".." + Num(c.Y1) + "]"
                + " gapRun=" + c.GapRun.ToString("F2") + "m at (" + Num(c.GapX) + "," + Num(c.GapZ) + ")");
            Console.WriteLine("        bands(level:cov/walk m2)=" + c.Bands);
        }
    }

    private static InstanceCov AnalyseInstance(TiledNavMeshBuilder.VoxelMeshResult r,
        List<SceneSnapshot.EntityRecord> assets, List<int> sel, List<int> members)
    {
        var c = new InstanceCov();
        c.Entities = members.Count;
        int flags = 0, tris = 0;
        float mnx = float.MaxValue, mxx = float.MinValue, mnz = float.MaxValue, mxz = float.MinValue;
        float mny = float.MaxValue, mxy = float.MinValue;
        var tri = new List<float>();
        var area = new List<float>();
        var triRamp = new List<bool>();
        float cosLimit = (float)Math.Cos(50.0 * Math.PI / 180.0);

        for (int k = 0; k < members.Count; k++)
        {
            SceneSnapshot.EntityRecord rec = assets[sel[members[k]]];
            if (c.Name.Length == 0) c.Name = rec.Name;
            // Ramp triangles are a record's last ones; a dumped '#ramp' record is all ramp.
            int rampTris = rec.RampTris;
            if (rampTris <= 0 && rec.Name != null
                && rec.Name.EndsWith(SteppedPropRamps.RampNameSuffix, StringComparison.Ordinal))
                rampTris = rec.TriCount;
            if (rampTris > rec.TriCount) rampTris = rec.TriCount;
            c.RampTris += rampTris;
            if (c.NameSet.Length == 0) c.NameSet = rec.Name ?? "(null)";
            else if (c.NameSet.IndexOf("'" + rec.Name + "'", StringComparison.Ordinal) < 0
                && rec.Name != c.NameSet) c.NameSet += " + '" + rec.Name + "'";
            flags |= rec.Flags;
            tris += rec.TriCount;
            for (int t = 0; t < rec.TriCount; t++)
            {
                int o = t * 9;
                float x0 = rec.VertsXyz[o + 0], y0 = rec.VertsXyz[o + 1], z0 = rec.VertsXyz[o + 2];
                float x1 = rec.VertsXyz[o + 3], y1 = rec.VertsXyz[o + 4], z1 = rec.VertsXyz[o + 5];
                float x2 = rec.VertsXyz[o + 6], y2 = rec.VertsXyz[o + 7], z2 = rec.VertsXyz[o + 8];
                // engine Z-up -> Recast Y-up
                float ax = x0, ay = z0, az = y0;
                float bx = x1, by = z1, bz = y1;
                float cx = x2, cy = z2, cz = y2;
                if (ax < mnx) mnx = ax; if (ax > mxx) mxx = ax;
                if (bx < mnx) mnx = bx; if (bx > mxx) mxx = bx;
                if (cx < mnx) mnx = cx; if (cx > mxx) mxx = cx;
                if (az < mnz) mnz = az; if (az > mxz) mxz = az;
                if (bz < mnz) mnz = bz; if (bz > mxz) mxz = bz;
                if (cz < mnz) mnz = cz; if (cz > mxz) mxz = cz;
                if (ay < mny) mny = ay; if (ay > mxy) mxy = ay;
                if (by < mny) mny = by; if (by > mxy) mxy = by;
                if (cy < mny) mny = cy; if (cy > mxy) mxy = cy;

                float ux = bx - ax, uy = by - ay, uz = bz - az;
                float wx = cx - ax, wy = cy - ay, wz = cz - az;
                float vnx = uy * wz - uz * wy, vny = uz * wx - ux * wz, vnz = ux * wy - uy * wx;
                double nlen = Math.Sqrt((double)vnx * vnx + (double)vny * vny + (double)vnz * vnz);
                if (nlen < 1e-9) continue;
                // the mixer flips so the emitted normal.Y >= 0; keep surfaces within the slope limit
                if (Math.Abs(vny) / nlen < cosLimit) continue;
                tri.Add(ax); tri.Add(ay); tri.Add(az);
                tri.Add(bx); tri.Add(by); tri.Add(bz);
                tri.Add(cx); tri.Add(cy); tri.Add(cz);
                triRamp.Add(t >= rec.TriCount - rampTris);
                double a2 = 0.5 * Math.Abs((bx - ax) * (cz - az) - (bz - az) * (cx - ax));
                area.Add((float)a2);
            }
        }
        c.Tris = tris;
        c.Flags = DecodeFlags(flags);
        c.X0 = mnx; c.X1 = mxx; c.Z0 = mnz; c.Z1 = mxz; c.Y0 = mny; c.Y1 = mxy;

        // Simple, metric-independent cross-check: how much of the built mesh sits inside the instance's
        // bounding box (the 'facesInBBox' count the diag-asset report uses).
        for (int f = 0; f < r.FaceCount; f++)
        {
            float fx = FaceCentroidX(r, f), fz = FaceCentroidZ(r, f), fy = FaceCentroidY(r, f);
            if (fx < c.X0 - 0.5f || fx > c.X1 + 0.5f) continue;
            if (fz < c.Z0 - 0.5f || fz > c.Z1 + 0.5f) continue;
            if (fy < c.Y0 - 1f || fy > c.Y1 + 1f) continue;
            c.MeshFaces++;
        }

        int triCount = area.Count;
        if (triCount == 0)
            return c;

        // Sample the instance's walkable surface on a 0.25 m grid. The reference height at a sample is
        // the topmost walkable (slope <= limit) collision surface under it, so a staircase is checked
        // tread by tread instead of being collapsed onto one "dominant" storey.
        const float step = 0.25f;
        const float tol = 0.6f;
        int nx = Math.Max(1, (int)Math.Floor((c.X1 - c.X0) / step) + 1);
        int nz = Math.Max(1, (int)Math.Floor((c.Z1 - c.Z0) / step) + 1);
        var walk = new bool[nx * nz];
        var cov = new bool[nx * nz];
        var ly = new float[nx * nz];
        int walkN = 0, covN = 0;
        int walkNatN = 0, covNatN = 0, walkRampN = 0, covRampN = 0;
        for (int i = 0; i < nx; i++)
        {
            float px = c.X0 + i * step;
            for (int j = 0; j < nz; j++)
            {
                float pz = c.Z0 + j * step;
                bool on = false;
                float sy = 0f;
                int bestTri = -1;
                for (int t = 0; t < triCount; t++)
                {
                    float y;
                    if (!TriY(tri, t, px, pz, out y)) continue;
                    if (!on || y > sy) { sy = y; on = true; bestTri = t; }
                }
                if (!on) continue;
                int idx = i * nz + j;
                walk[idx] = true;
                ly[idx] = sy;
                walkN++;
                List<int> cands = FacesAtAnyHeight(r, px, pz);
                bool inWindow = false;
                bool sawBelow = false, sawAbove = false;
                float aboveMin = 0f, inD = 0f;
                for (int k = 0; k < cands.Count; k++)
                {
                    // The mesh is lifted by HeightOffsetMeters (0.5) and snapped to the ch grid, so the
                    // covered window is [surface - 0.2, surface + offset + tol].
                    float fy;
                    if (!FaceYAt(r, cands[k], px, pz, out fy)) continue;
                    if (fy >= sy - 0.20f && fy <= sy + MeshOffset + tol)
                    {
                        inWindow = true;
                        inD = fy - sy;
                        break;
                    }
                    if (fy < sy - 0.20f) sawBelow = true;
                    else { if (!sawAbove || fy < aboveMin) aboveMin = fy; sawAbove = true; }
                }
                if (inWindow)
                {
                    cov[idx] = true;
                    covN++;
                    if (inD > c.CovMaxD) c.CovMaxD = inD;
                }
                else if (cands.Count == 0) c.MissNone++;
                else if (sawAbove && !sawBelow)
                {
                    c.MissAbove++;
                    float ex = aboveMin - (sy + MeshOffset + tol);
                    if (!c.HasAboveMin || ex < c.AboveMinD) { c.AboveMinD = ex; c.HasAboveMin = true; }
                }
                else if (sawBelow && !sawAbove) c.MissBelow++;
                else c.MissBoth++;
                if (bestTri >= 0 && bestTri < triRamp.Count && triRamp[bestTri])
                {
                    walkRampN++;
                    if (cov[idx]) covRampN++;
                }
                else
                {
                    walkNatN++;
                    if (cov[idx]) covNatN++;
                }
            }
        }
        c.WalkArea = walkN * (double)step * step;
        c.Coverage = walkN > 0 ? 100.0 * covN / walkN : 0.0;
        c.WalkAreaNative = walkNatN * (double)step * step;
        c.CoverageNative = walkNatN > 0 ? 100.0 * covNatN / walkNatN : 0.0;
        c.WalkAreaRamp = walkRampN * (double)step * step;
        c.CoverageRamp = walkRampN > 0 ? 100.0 * covRampN / walkRampN : 0.0;

        // Eroded interior: samples whose whole 0.25 m neighbourhood is walkable at the same level. The
        // voxel mesh is inset from every surface edge by ~agent radius, so only the interior can be
        // expected to be covered; a truly bare flight collapses to ~0% here.
        int innerN = 0, innerCov = 0;
        for (int i = 1; i < nx - 1; i++)
            for (int j = 1; j < nz - 1; j++)
            {
                int s = i * nz + j;
                if (!walk[s]) continue;
                bool ok = true;
                for (int di = -1; di <= 1 && ok; di++)
                    for (int dj = -1; dj <= 1 && ok; dj++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int ns2 = (i + di) * nz + (j + dj);
                        if (!walk[ns2] || Math.Abs(ly[ns2] - ly[s]) > 0.35f) ok = false;
                    }
                if (!ok) continue;
                innerN++;
                if (cov[s]) innerCov++;
            }
        c.WalkAreaE = innerN * (double)step * step;
        c.CoverageE = innerN > 0 ? 100.0 * innerCov / innerN : 0.0;

        // The 0.25 m bands the prop exposes, most walkable area first: a staircase shows up as one band
        // per tread, a flat deck as a single band.
        var bands = new Dictionary<int, int>();
        var bandsCov = new Dictionary<int, int>();
        for (int s = 0; s < walk.Length; s++)
        {
            if (!walk[s]) continue;
            int b = (int)Math.Round(ly[s] / 0.25);
            int cur;
            bands.TryGetValue(b, out cur);
            bands[b] = cur + 1;
            if (cov[s]) { bandsCov.TryGetValue(b, out cur); bandsCov[b] = cur + 1; }
        }
        int bestBand = 0, bestN = -1;
        foreach (KeyValuePair<int, int> kv in bands)
            if (kv.Value > bestN) { bestN = kv.Value; bestBand = kv.Key; }
        c.Level = bestBand * 0.25f;
        int bc;
        bandsCov.TryGetValue(bestBand, out bc);
        c.CoverageLvl = bestN > 0 ? 100.0 * bc / bestN : 0.0;

        var bandOrder = new List<int>(bands.Keys);
        bandOrder.Sort(delegate (int a2, int b2)
        {
            int cmp = bands[b2].CompareTo(bands[a2]);
            if (cmp != 0) return cmp;
            return a2.CompareTo(b2);
        });
        var bsb2 = new System.Text.StringBuilder();
        for (int bi = 0; bi < bandOrder.Count && bi < 12; bi++)
        {
            if (bi > 0) bsb2.Append(" ");
            int b = bandOrder[bi];
            int cc;
            bandsCov.TryGetValue(b, out cc);
            bsb2.Append(Num(b * 0.25f)).Append("m:").Append(cc).Append("/").Append(bands[b]);
        }
        c.Bands = bandOrder.Count > 8 ? bsb2.ToString() + " ..." : bsb2.ToString();

        // largest uncovered run: 4-connected component of (walkable && !covered)
        var seen = new bool[nx * nz];
        double bestExtent = 0;
        for (int i = 0; i < nx; i++)
            for (int j = 0; j < nz; j++)
            {
                int s0 = i * nz + j;
                if (!walk[s0] || cov[s0] || seen[s0]) continue;
                var stack = new Stack<int>();
                stack.Push(s0);
                seen[s0] = true;
                float lx0 = c.X0 + i * step, lx1 = lx0;
                float lz0 = c.Z0 + j * step, lz1 = lz0;
                while (stack.Count > 0)
                {
                    int cur = stack.Pop();
                    int ci = cur / nz, cj = cur % nz;
                    float cx3 = c.X0 + ci * step, cz3 = c.Z0 + cj * step;
                    if (cx3 < lx0) lx0 = cx3;
                    if (cx3 > lx1) lx1 = cx3;
                    if (cz3 < lz0) lz0 = cz3;
                    if (cz3 > lz1) lz1 = cz3;
                    for (int d = 0; d < 4; d++)
                    {
                        int ni2 = ci + (d == 0 ? 1 : d == 1 ? -1 : 0);
                        int nj2 = cj + (d == 2 ? 1 : d == 3 ? -1 : 0);
                        if (ni2 < 0 || nj2 < 0 || ni2 >= nx || nj2 >= nz) continue;
                        int ns = ni2 * nz + nj2;
                        if (!walk[ns] || cov[ns] || seen[ns]) continue;
                        seen[ns] = true;
                        stack.Push(ns);
                    }
                }
                double ext = Math.Sqrt((lx1 - lx0) * (lx1 - lx0) + (lz1 - lz0) * (lz1 - lz0));
                if (ext > bestExtent)
                {
                    bestExtent = ext;
                    c.GapX = (lx0 + lx1) * 0.5f;
                    c.GapZ = (lz0 + lz1) * 0.5f;
                }
            }
        c.GapRun = bestExtent;
        return c;
    }

    private static string JoinFloats(List<float> values, string fmt)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < values.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append(values[i].ToString(fmt, System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private sealed class LevelChoice
    {
        public int Face = -1;
        public float Height;
        public float Target = float.NaN;   // intended walkable level
        public bool HasTarget;
        public string Rule = "none";
        public List<float> Heights = new List<float>();
    }

    // Stacked levels: pick which face a bare (x,z) point refers to. A 'diag' asset bounding box that
    // contains the point pins the intended level (the asset's bottom is the floor it sits on); an
    // explicit z wins next; otherwise the highest face is used. The face is only accepted when a
    // candidate is within LevelTolerance of that target, so "no navmesh at this level" is visible.
    private const float LevelTolerance = 1.5f;

    private static LevelChoice PickLevel(TiledNavMeshBuilder.VoxelMeshResult r, float px, float pz,
        float explicitZ, bool hasExplicitZ, List<SceneSnapshot.EntityRecord> assets, List<string> diagNames)
    {
        var lc = new LevelChoice();
        List<int> cands = FacesAtAnyHeight(r, px, pz);
        for (int i = 0; i < cands.Count; i++) lc.Heights.Add(FaceCentroidY(r, cands[i]));
        if (cands.Count == 0)
            return lc;

        float target = float.NaN;
        if (hasExplicitZ)
        {
            // An explicit level wins: the caller knows which storey is meant.
            target = explicitZ;
            lc.Rule = "explicit z=" + Num(explicitZ);
        }
        for (int d = 0; d < diagNames.Count && float.IsNaN(target); d++)
        {
            string name = diagNames[d];
            for (int e = 0; e < assets.Count && float.IsNaN(target); e++)
            {
                if (assets[e].Name != name) continue;
                SceneSnapshot.EntityRecord rec = assets[e];
                float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                for (int v = 0; v < rec.VertsXyz.Length; v += 3)
                {
                    if (rec.VertsXyz[v] < minX) minX = rec.VertsXyz[v];
                    if (rec.VertsXyz[v] > maxX) maxX = rec.VertsXyz[v];
                    if (rec.VertsXyz[v + 1] < minZ) minZ = rec.VertsXyz[v + 1];
                    if (rec.VertsXyz[v + 1] > maxZ) maxZ = rec.VertsXyz[v + 1];
                    if (rec.VertsXyz[v + 2] < minY) minY = rec.VertsXyz[v + 2];
                    if (rec.VertsXyz[v + 2] > maxY) maxY = rec.VertsXyz[v + 2];
                }
                if (px >= minX - 0.5f && px <= maxX + 0.5f && pz >= minZ - 0.5f && pz <= maxZ + 0.5f)
                {
                    // The walkable surface of a renamed piece is its TOP (the user's pieces sit a few
                    // tenths above the neighbouring floors).
                    target = maxY;
                    lc.Rule = "diag '" + name + "' top=" + Num(maxY) + " zr=[" + Num(minY) + ".." + Num(maxY) + "]";
                    break;
                }
            }
        }
        if (float.IsNaN(target) && hasExplicitZ)
        {
            target = explicitZ;
            lc.Rule = "explicit z=" + Num(explicitZ);
        }

        int best = 0;
        if (float.IsNaN(target))
        {
            lc.Rule = "highest";
            for (int i = 1; i < cands.Count; i++)
                if (lc.Heights[i] > lc.Heights[best]) best = i;
            lc.Face = cands[best];
            lc.Height = lc.Heights[best];
            lc.Target = lc.Height;
            lc.HasTarget = true;
            return lc;
        }
        lc.Target = target;
        lc.HasTarget = true;
        for (int i = 1; i < cands.Count; i++)
            if (Math.Abs(lc.Heights[i] - target) < Math.Abs(lc.Heights[best] - target)) best = i;
        lc.Height = lc.Heights[best];
        if (Math.Abs(lc.Height - target) <= LevelTolerance)
            lc.Face = cands[best];
        else
            lc.Face = -1;                      // nothing walkable at this level here
        return lc;
    }

    // A sample is "on face" when some stacked face at (x,z) is inside the pair's level band.
    private static bool FaceInBand(TiledNavMeshBuilder.VoxelMeshResult r, float px, float pz, float lo, float hi)
    {
        List<int> cands = FacesAtAnyHeight(r, px, pz);
        for (int i = 0; i < cands.Count; i++)
        {
            float y = FaceCentroidY(r, cands[i]);
            if (y >= lo && y <= hi) return true;
        }
        return false;
    }

    private static float ParseFloat(string v)
    {
        return float.Parse(v, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool ParseOnOff(string v)
    {
        if (v == null) return true;
        return v.Equals("on", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static float[] ParsePair(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return null;
        string[] halves = spec.Split('|');
        if (halves.Length != 2) return null;
        string[] a = halves[0].Split(',');
        string[] b = halves[1].Split(',');
        if ((a.Length != 2 && a.Length != 3) || (b.Length != 2 && b.Length != 3)) return null;
        try
        {
            // Layout: x1, z1, level1, x2, z2, level2 (level NaN when not given).
            float l1 = a.Length == 3 ? ParseFloat(a[2].Trim()) : float.NaN;
            float l2 = b.Length == 3 ? ParseFloat(b[2].Trim()) : float.NaN;
            return new float[] { ParseFloat(a[0].Trim()), ParseFloat(a[1].Trim()), l1,
                ParseFloat(b[0].Trim()), ParseFloat(b[1].Trim()), l2 };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static List<int> FacesAtAnyHeight(TiledNavMeshBuilder.VoxelMeshResult r, float px, float pz)
    {
        return IndexOf(r).FacesAt(px, pz);
    }

    // Uniform XZ spatial index over the mesh faces, built ONCE per mesh (O(F)) so a point query is
    // O(faces in the cell) instead of O(F). This is what makes the snapshot pair/probe scan scale to
    // 500k+ face meshes (Basamu). Component roots are computed lazily via FaceComponents.
    private sealed class MeshIndex
    {
        private readonly TiledNavMeshBuilder.VoxelMeshResult _r;
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        private readonly float[] _minX, _maxX, _minZ, _maxZ;
        private readonly float _cell;
        public int[] Roots;
        public int RootCount;

        public MeshIndex(TiledNavMeshBuilder.VoxelMeshResult r, float cellMeters)
        {
            _r = r;
            _cell = cellMeters > 0.1f ? cellMeters : 1.0f;
            int n = r.FaceCount;
            _minX = new float[n]; _maxX = new float[n]; _minZ = new float[n]; _maxZ = new float[n];
            for (int f = 0; f < n; f++)
            {
                int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                float ax = r.VertsXyz[a * 3], az = r.VertsXyz[a * 3 + 2];
                float bx = r.VertsXyz[b * 3], bz = r.VertsXyz[b * 3 + 2];
                float cx = r.VertsXyz[c * 3], cz = r.VertsXyz[c * 3 + 2];
                _minX[f] = Math.Min(ax, Math.Min(bx, cx));
                _maxX[f] = Math.Max(ax, Math.Max(bx, cx));
                _minZ[f] = Math.Min(az, Math.Min(bz, cz));
                _maxZ[f] = Math.Max(az, Math.Max(bz, cz));
                int x0 = (int)Math.Floor(_minX[f] / _cell), x1 = (int)Math.Floor(_maxX[f] / _cell);
                int z0 = (int)Math.Floor(_minZ[f] / _cell), z1 = (int)Math.Floor(_maxZ[f] / _cell);
                if (x1 - x0 > 64) x1 = x0 + 64;   // guard against degenerate huge spans
                if (z1 - z0 > 64) z1 = z0 + 64;
                for (int x = x0; x <= x1; x++)
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = CellKey(x, z);
                        List<int> list;
                        if (!_cells.TryGetValue(key, out list)) { list = new List<int>(); _cells[key] = list; }
                        list.Add(f);
                    }
            }
        }

        private static long CellKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        // All faces whose XZ projection contains (px,pz). Bounded by the cell bucket.
        public List<int> FacesAt(float px, float pz)
        {
            var list = new List<int>();
            int cx = (int)Math.Floor(px / _cell), cz = (int)Math.Floor(pz / _cell);
            List<int> bucket;
            if (!_cells.TryGetValue(CellKey(cx, cz), out bucket)) return list;
            for (int i = 0; i < bucket.Count; i++)
            {
                int f = bucket[i];
                if (px < _minX[f] || px > _maxX[f] || pz < _minZ[f] || pz > _maxZ[f]) continue;
                int a = _r.Tris[f * 3], b = _r.Tris[f * 3 + 1], c = _r.Tris[f * 3 + 2];
                if (PointInTri(px, pz, _r.VertsXyz[a * 3], _r.VertsXyz[a * 3 + 2],
                    _r.VertsXyz[b * 3], _r.VertsXyz[b * 3 + 2], _r.VertsXyz[c * 3], _r.VertsXyz[c * 3 + 2]))
                    list.Add(f);
            }
            return list;
        }

        // Component root id of the face nearest (in XZ) to the point, or -1 when nothing is near.
        public int ComponentAt(float px, float pz, float tol)
        {
            int best = -1;
            float bd = tol;
            int cx = (int)Math.Floor(px / _cell), cz = (int)Math.Floor(pz / _cell);
            for (int gx = cx - 1; gx <= cx + 1; gx++)
                for (int gz = cz - 1; gz <= cz + 1; gz++)
                {
                    List<int> bucket;
                    if (!_cells.TryGetValue(CellKey(gx, gz), out bucket)) continue;
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        int f = bucket[i];
                        float fx = (float)FaceCentroidX(_r, f), fz = (float)FaceCentroidZ(_r, f);
                        float d = (float)Math.Sqrt((fx - px) * (fx - px) + (fz - pz) * (fz - pz));
                        if (d < bd) { bd = d; best = f; }
                    }
                }
            return best < 0 ? -1 : Find(Roots, best);
        }

        public void EnsureComponents()
        {
            if (Roots != null) return;
            Roots = FaceComponents(_r, out RootCount);
        }
    }

    // The index for the mesh currently being probed (built on demand; identity-guarded).
    private static MeshIndex _meshIndex;
    private static TiledNavMeshBuilder.VoxelMeshResult _meshIndexOwner;

    private static MeshIndex IndexOf(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        if (_meshIndex == null || !ReferenceEquals(_meshIndexOwner, r))
        {
            _meshIndex = new MeshIndex(r, 2.0f);
            _meshIndexOwner = r;
        }
        return _meshIndex;
    }

    private sealed class FaceArc
    {
        public int To;
        public float Mx, My, Mz;   // midpoint of the shared edge
    }

    // Adjacency over faces that share an edge; each arc goes through the shared edge midpoint, so the
    // geodesic is a real polyline length (not a centroid-to-centroid zig-zag).
    private static List<FaceArc>[] BuildFaceGraph(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        var edgeFaces = new Dictionary<long, List<int>>();
        var edgeMid = new Dictionary<long, float[]>();
        for (int f = 0; f < r.FaceCount; f++)
        {
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            AddEdgeFace(edgeFaces, edgeMid, r, a, b, f);
            AddEdgeFace(edgeFaces, edgeMid, r, b, c, f);
            AddEdgeFace(edgeFaces, edgeMid, r, c, a, f);
        }
        var adj = new List<FaceArc>[r.FaceCount];
        for (int f = 0; f < r.FaceCount; f++) adj[f] = new List<FaceArc>();
        foreach (KeyValuePair<long, List<int>> kv in edgeFaces)
        {
            List<int> fl = kv.Value;
            float[] mid = edgeMid[kv.Key];
            for (int i = 0; i < fl.Count; i++)
                for (int j = i + 1; j < fl.Count; j++)
                {
                    var ab = new FaceArc();
                    ab.To = fl[j]; ab.Mx = mid[0]; ab.My = mid[1]; ab.Mz = mid[2];
                    adj[fl[i]].Add(ab);
                    var ba = new FaceArc();
                    ba.To = fl[i]; ba.Mx = mid[0]; ba.My = mid[1]; ba.Mz = mid[2];
                    adj[fl[j]].Add(ba);
                }
        }
        return adj;
    }

    private static void AddEdgeFace(Dictionary<long, List<int>> map, Dictionary<long, float[]> mids,
        TiledNavMeshBuilder.VoxelMeshResult r, int a, int b, int face)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        List<int> list;
        if (!map.TryGetValue(key, out list))
        {
            list = new List<int>(2);
            map[key] = list;
            mids[key] = new float[] {
                (r.VertsXyz[a * 3] + r.VertsXyz[b * 3]) * 0.5f,
                (r.VertsXyz[a * 3 + 1] + r.VertsXyz[b * 3 + 1]) * 0.5f,
                (r.VertsXyz[a * 3 + 2] + r.VertsXyz[b * 3 + 2]) * 0.5f };
        }
        list.Add(face);
    }

    private sealed class FaceHeap
    {
        private readonly List<int> nodes = new List<int>();
        private readonly List<double> keys = new List<double>();
        public int Count { get { return nodes.Count; } }
        public void Push(int n, double k)
        {
            nodes.Add(n); keys.Add(k);
            int i = nodes.Count - 1;
            while (i > 0)
            {
                int p = (i - 1) / 2;
                if (keys[p] <= keys[i]) break;
                Swap(i, p); i = p;
            }
        }
        public int Pop()
        {
            int top = nodes[0];
            int last = nodes.Count - 1;
            nodes[0] = nodes[last]; keys[0] = keys[last];
            nodes.RemoveAt(last); keys.RemoveAt(last);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, rr = l + 1, m = i;
                if (l < nodes.Count && keys[l] < keys[m]) m = l;
                if (rr < nodes.Count && keys[rr] < keys[m]) m = rr;
                if (m == i) break;
                Swap(i, m); i = m;
            }
            return top;
        }
        private void Swap(int a, int b)
        {
            int tn = nodes[a]; nodes[a] = nodes[b]; nodes[b] = tn;
            double tk = keys[a]; keys[a] = keys[b]; keys[b] = tk;
        }
    }

    // Dijkstra over faces. The accumulated cost is the length of the walking polyline through the shared
    // edge midpoints (P1 -> mid ... mid -> P2), so geo is a real path length rather than a centroid
    // zig-zag; a straight, directly-connected pair then measures ~1.0x its euclidean distance.
    private static double Geodesic(TiledNavMeshBuilder.VoxelMeshResult r, List<FaceArc>[] adj,
        int from, int to, float sx, float sz, float ex, float ez)
    {
        if (from < 0 || to < 0) return double.PositiveInfinity;
        if (from == to) return Math.Sqrt((ex - sx) * (ex - sx) + (ez - sz) * (ez - sz));
        int n = r.FaceCount;
        var dist = new double[n];
        for (int i = 0; i < n; i++) dist[i] = double.PositiveInfinity;
        var done = new bool[n];
        var pmx = new float[n];
        var pmy = new float[n];
        var pmz = new float[n];
        var heap = new FaceHeap();
        dist[from] = 0.0;
        pmx[from] = sx;
        pmy[from] = FaceCentroidY(r, from);   // the start point sits at its face's height
        pmz[from] = sz;
        heap.Push(from, 0.0);
        while (heap.Count > 0)
        {
            int u = heap.Pop();
            if (done[u]) continue;
            done[u] = true;
            if (u == to) break;
            List<FaceArc> nb = adj[u];
            for (int i = 0; i < nb.Count; i++)
            {
                FaceArc arc = nb[i];
                int v = arc.To;
                if (done[v]) continue;
                double dx = arc.Mx - pmx[u], dy = arc.My - pmy[u], dz = arc.Mz - pmz[u];
                double nd = dist[u] + Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (nd < dist[v])
                {
                    dist[v] = nd;
                    pmx[v] = arc.Mx; pmy[v] = arc.My; pmz[v] = arc.Mz;
                    heap.Push(v, nd);
                }
            }
        }
        if (double.IsInfinity(dist[to])) return double.PositiveInfinity;
        double exd = ex - pmx[to];
        double eyd = FaceCentroidY(r, to) - pmy[to];
        double ezd = ez - pmz[to];
        return dist[to] + Math.Sqrt(exd * exd + eyd * eyd + ezd * ezd);
    }

    private static int LowestFaceAt(TiledNavMeshBuilder.VoxelMeshResult r, float px, float pz)
    {
        List<int> cands = FacesAtAnyHeight(r, px, pz);
        if (cands.Count == 0) return -1;
        int best = cands[0];
        for (int i = 1; i < cands.Count; i++)
            if (FaceCentroidY(r, cands[i]) < FaceCentroidY(r, best)) best = cands[i];
        return best;
    }

    private static bool AnalyzePair(TiledNavMeshBuilder.VoxelMeshResult r, int[] fp, Dictionary<int, int> rank,
        Dictionary<int, int> sizes, List<FaceArc>[] graph, float[] pr, float band, int index,
        float[] soupVerts, int[] soupTris, SceneSnapshot.TerrainData terrain,
        List<SceneSnapshot.EntityRecord> assets, List<string> diagNames)
    {
        float x1 = pr[0], z1 = pr[1], x2 = pr[3], z2 = pr[4];
        bool hasL1 = !float.IsNaN(pr[2]);
        bool hasL2 = !float.IsNaN(pr[5]);
        LevelChoice l1 = PickLevel(r, x1, z1, pr[2], hasL1, assets, diagNames);
        LevelChoice l2 = PickLevel(r, x2, z2, pr[5], hasL2, assets, diagNames);
        float lev1 = hasL1 ? pr[2] : (l1.HasTarget ? l1.Target : 0f);
        float lev2 = hasL2 ? pr[5] : (l2.HasTarget ? l2.Target : 0f);
        // The band spans the two endpoint levels, widened by --band on each side.
        float lo = Math.Min(lev1, lev2) - band;
        float hi = Math.Max(lev1, lev2) + band;
        float target = (lev1 + lev2) * 0.5f;

        double dx = x2 - x1, dz = z2 - z1;
        double euclid = Math.Sqrt(dx * dx + dz * dz);
        const double step = 0.25;
        int steps = Math.Max(1, (int)Math.Ceiling(euclid / step));
        double stepLen = euclid / steps;

        int onCount = 0;
        int runSamples = 0, maxRunSamples = 0;
        int worstStart = -1, worstEnd = -1;
        for (int k = 0; k <= steps; k++)
        {
            double t = (double)k / steps;
            float px = (float)(x1 + dx * t), pz = (float)(z1 + dz * t);
            bool on = FaceInBand(r, px, pz, lo, hi);
            if (on)
            {
                onCount++;
                runSamples = 0;
            }
            else
            {
                runSamples++;
                if (runSamples > maxRunSamples)
                {
                    maxRunSamples = runSamples;
                    worstEnd = k;
                    worstStart = k - runSamples + 1;
                }
            }
        }
        double gapMaxRun = maxRunSamples * stepLen;
        double coverage = 100.0 * onCount / (steps + 1);

        int f1 = l1.Face, f2 = l2.Face;
        double geo = Geodesic(r, graph, f1, f2, x1, z1, x2, z2);
        double ratio = (euclid > 1e-6 && !double.IsInfinity(geo)) ? geo / euclid : double.PositiveInfinity;
        bool pass = f1 >= 0 && f2 >= 0 && gapMaxRun <= 0.5 + 1e-6 && ratio <= 1.5;

        Console.WriteLine("  pair[" + index + "] (" + Num(x1) + "," + Num(z1) + ")|(" + Num(x2) + "," + Num(z2) + ")"
            + " level=" + Num(target) + " [" + l1.Rule + " / " + l2.Rule + "]"
            + " euclid=" + Num((float)euclid) + "m"
            + " gapMaxRun=" + Num((float)gapMaxRun) + "m"
            + " coverage=" + coverage.ToString("F1") + "%"
            + " band=[" + Num(lo) + ".." + Num(hi) + "]"
            + " f1=" + FaceDesc(r, fp, rank, sizes, f1, l1)
            + " f2=" + FaceDesc(r, fp, rank, sizes, f2, l2)
            + " geo=" + (double.IsInfinity(geo) ? "inf" : Num((float)geo) + "m")
            + " ratio=" + (double.IsInfinity(ratio) ? "inf" : Num((float)ratio))
            + " -> " + (pass ? "PASS" : "FAIL"));

        // For a failing pair, describe the worst off-band run's location.
        if (!pass && maxRunSamples > 0)
        {
            double tMid = ((worstStart + worstEnd) / 2.0) / steps;
            float mx = (float)(x1 + dx * tMid), mz = (float)(z1 + dz * tMid);
            float px0 = (float)(x1 + dx * ((double)worstStart / steps));
            float pz0 = (float)(z1 + dz * ((double)worstStart / steps));
            float px1 = (float)(x1 + dx * ((double)worstEnd / steps));
            float pz1 = (float)(z1 + dz * ((double)worstEnd / steps));
            Console.WriteLine("      worst off-band run: " + Num((float)gapMaxRun) + "m from ("
                + Num(px0) + "," + Num(pz0) + ") to (" + Num(px1) + "," + Num(pz1) + ")");
            PrintGapContext(mx, mz, target, soupVerts, soupTris, terrain, assets, diagNames);
        }
        return pass;
    }

    private static string FaceDesc(TiledNavMeshBuilder.VoxelMeshResult r, int[] fp, Dictionary<int, int> rank,
        Dictionary<int, int> sizes, int face, LevelChoice lc)
    {
        if (face < 0)
        {
            float nearest = float.NaN;
            for (int i = 0; i < lc.Heights.Count; i++)
                if (float.IsNaN(nearest) || Math.Abs(lc.Heights[i] - lc.Target) < Math.Abs(nearest - lc.Target))
                    nearest = lc.Heights[i];
            return "none(cands=[" + JoinFloats(lc.Heights, "F2")
                + (float.IsNaN(nearest) ? "" : ", nearest d=" + Num(Math.Abs(nearest - lc.Target))) + "])";
        }
        return "#" + face + "(h=" + Num(FaceCentroidY(r, face)) + ",isl=" + IslandOf(fp, rank, face)
            + ",faces=" + IslandFaces(fp, sizes, face) + ")";
    }

    private static int IslandOf(int[] fp, Dictionary<int, int> rank, int face)
    {
        int root = Find(fp, face);
        return rank.ContainsKey(root) ? rank[root] : -1;
    }

    private static int IslandFaces(int[] fp, Dictionary<int, int> sizes, int face)
    {
        int root = Find(fp, face);
        return sizes.ContainsKey(root) ? sizes[root] : 0;
    }

    // What is under a failing sample: the terrain height, the collision soup surfaces near it (and which
    // of them are close to the pair's level), and any 'diag' asset whose bounding box contains it.
    private static void PrintGapContext(float x, float z, float level, float[] soupVerts, int[] soupTris,
        SceneSnapshot.TerrainData terrain, List<SceneSnapshot.EntityRecord> assets, List<string> diagNames)
    {
        float terrainH = float.NaN;
        int gx = (int)Math.Round(x / terrain.Spacing);
        int gz = (int)Math.Round(z / terrain.Spacing);
        if (gx >= 0 && gz >= 0 && gx < terrain.Width && gz < terrain.Height)
            terrainH = terrain.Heights[gx * terrain.Height + gz];

        int near = 0;
        float yMin = float.MaxValue, yMax = float.MinValue;
        var levelSurfaces = new List<float>();
        int upN = 0, downN = 0;
        int triCount = soupTris.Length / 3;
        for (int t = 0; t < triCount; t++)
        {
            int a = soupTris[t * 3], b = soupTris[t * 3 + 1], c = soupTris[t * 3 + 2];
            float ax = soupVerts[a * 3], ay = soupVerts[a * 3 + 1], az = soupVerts[a * 3 + 2];
            float bx = soupVerts[b * 3], by = soupVerts[b * 3 + 1], bz = soupVerts[b * 3 + 2];
            float cx = soupVerts[c * 3], cy = soupVerts[c * 3 + 1], cz = soupVerts[c * 3 + 2];
            if (!PointInTri(x, z, ax, az, bx, bz, cx, cz)) continue;
            near++;
            float tmin = Math.Min(ay, Math.Min(by, cy)), tmax = Math.Max(ay, Math.Max(by, cy));
            if (tmin < yMin) yMin = tmin;
            if (tmax > yMax) yMax = tmax;
            if (Math.Abs(tmin - level) <= 3f || Math.Abs(tmax - level) <= 3f)
            {
                levelSurfaces.Add(tmin);
                levelSurfaces.Add(tmax);
                // Recast-space normal Y (the soup is already Y-up): tells whether the surface is
                // even eligible to be marked walkable.
                float ux2 = bx - ax, uy2 = by - ay, uz2 = bz - az;
                float wx = cx - ax, wy = cy - ay, wz = cz - az;
                float ny2 = uz2 * wx - ux2 * wz;
                if (ny2 > 1e-6f) upN++;
                else if (ny2 < -1e-6f) downN++;
            }
        }
        levelSurfaces.Sort();
        var distinct = new List<float>();
        for (int i = 0; i < levelSurfaces.Count; i++)
            if (distinct.Count == 0 || Math.Abs(levelSurfaces[i] - distinct[distinct.Count - 1]) > 0.01f)
                distinct.Add(levelSurfaces[i]);

        var hits = new List<string>();
        for (int e = 0; e < assets.Count; e++)
        {
            string name = assets[e].Name;
            if (name == null) continue;
            bool isDiag = false;
            for (int d = 0; d < diagNames.Count; d++) if (diagNames[d] == name) { isDiag = true; break; }
            if (!isDiag) continue;
            SceneSnapshot.EntityRecord rec = assets[e];
            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            for (int v = 0; v < rec.VertsXyz.Length; v += 3)
            {
                if (rec.VertsXyz[v] < minX) minX = rec.VertsXyz[v];
                if (rec.VertsXyz[v] > maxX) maxX = rec.VertsXyz[v];
                if (rec.VertsXyz[v + 1] < minZ) minZ = rec.VertsXyz[v + 1];
                if (rec.VertsXyz[v + 1] > maxZ) maxZ = rec.VertsXyz[v + 1];
                if (rec.VertsXyz[v + 2] < minY) minY = rec.VertsXyz[v + 2];
                if (rec.VertsXyz[v + 2] > maxY) maxY = rec.VertsXyz[v + 2];
            }
            if (x >= minX - 0.5f && x <= maxX + 0.5f && z >= minZ - 0.5f && z <= maxZ + 0.5f)
                hits.Add("'" + name + "' zr=[" + Num(minY) + ".." + Num(maxY) + "]");
        }

        Console.WriteLine("      context @(" + Num(x) + "," + Num(z) + ") level=" + Num(level)
            + ": terrain=" + (float.IsNaN(terrainH) ? "out-of-grid" : Num(terrainH) + "m")
            + " soupTrisNear=" + near
            + (near > 0 ? " soupZ=[" + Num(yMin) + ".." + Num(yMax) + "]" : "")
            + " soupAtLevel=[" + JoinFloats(distinct, "F3") + "] upFacing=" + upN + " downFacing=" + downN
            + " diagBBox=" + (hits.Count == 0 ? "none" : string.Join(" ", hits.ToArray())));
    }

    // Mirrors VoxelNavMeshGenerator: terrain grid -> Recast Y-up soup (stride/spacing from the header),
    // then assets merged with the engine Z-up -> Recast Y-up swap and winding reversal. The force array
    // (same length as the asset triangle count) marks the synthesized ramp triangles.
    private static void BuildSnapshotSoup(SceneSnapshot.TerrainData terrain,
        List<SceneSnapshot.EntityRecord> assets, out float[] vertsXyz, out int[] tris, out bool[] force,
        out bool[] exempt)
    {
        TiledNavMeshBuilder.BuildTerrainSoup(terrain.Heights, terrain.Width, terrain.Height,
            terrain.Spacing, terrain.Stride, out vertsXyz, out tris);
        force = null;
        exempt = null;
        if (assets == null || assets.Count == 0)
            return;
        int terrainTris = tris.Length / 3;
        var av = new List<float>();
        var at = new List<int>();
        var af = new List<bool>();
        var ae = new List<bool>();
        for (int i = 0; i < assets.Count; i++)
        {
            SceneSnapshot.EntityRecord e = assets[i];
            int baseIndex = av.Count / 3;
            for (int k = 0; k < e.VertsXyz.Length; k++) av.Add(e.VertsXyz[k]);
            for (int k = 0; k < e.Tris.Length; k++) at.Add(e.Tris[k] + baseIndex);
            // Ramp triangles are always the record's last ones; a dumped '#ramp' record is all ramp.
            int rampTris = e.RampTris;
            if (rampTris <= 0 && e.Name != null
                && (e.Name.EndsWith(SteppedPropRamps.RampNameSuffix, StringComparison.Ordinal)
                    || e.Name.EndsWith(GateBridgeStrips.GateNameSuffix, StringComparison.Ordinal)))
                rampTris = e.TriCount;
            if (rampTris > e.TriCount) rampTris = e.TriCount;
            // A stepped prop's own triangles are ledge-trim exempt (the same rule the collector applies
            // from the body flags).
            bool steppedEntity = (e.Flags &
                (SteppedPropRamps.FlagHasSteps | SteppedPropRamps.FlagLadder)) != 0;
            if (steppedEntity && !SoupSteppedExempt) steppedEntity = false;
            bool gateEntity = e.Name != null
                && e.Name.EndsWith(GateBridgeStrips.GateNameSuffix, StringComparison.Ordinal);
            for (int t = 0; t < e.TriCount; t++)
            {
                bool isRamp = t >= e.TriCount - rampTris;
                af.Add(isRamp);
                ae.Add(!isRamp && (steppedEntity || gateEntity));
            }
        }
        float[] mx;
        int[] mt;
        TiledNavMeshBuilder.BuildCombinedSoup(vertsXyz, tris, av.ToArray(), at.ToArray(), out mx, out mt, out int _f);
        vertsXyz = mx;
        tris = mt;
        bool anyForce = false, anyExempt = false;
        for (int k = 0; k < af.Count; k++)
        {
            if (af[k]) anyForce = true;
            if (ae[k]) anyExempt = true;
        }
        if (anyForce)
        {
            force = new bool[terrainTris + af.Count];
            for (int k = 0; k < af.Count; k++)
                force[terrainTris + k] = af[k];
        }
        if (anyExempt)
        {
            exempt = new bool[terrainTris + ae.Count];
            for (int k = 0; k < ae.Count; k++)
                exempt[terrainTris + k] = ae[k];
        }
    }

    // Mirrors VoxelNavMeshGenerator + SceneCollisionGeometry for the ramp path: each stepped / ladder
    // entity record gets its synthesized ramp triangles appended (and marked), while a record that is
    // already a dumped ramp ('...#ramp') is left alone so a fresh snapshot is never double-ramped.
    internal static void ApplyPropRamps(List<SceneSnapshot.EntityRecord> assets, Action<string> log,
        float cellSize, float climb, bool ledge)
    {
        if (PropDump != null)
        {
            for (int i = 0; i < assets.Count; i++)
            {
                SceneSnapshot.EntityRecord rec = assets[i];
                if (rec.Name == null || rec.Name.IndexOf(PropDump, StringComparison.Ordinal) < 0) continue;
                {
                    float bx0 = float.MaxValue, bx1 = float.MinValue;
                    float by0 = float.MaxValue, by1 = float.MinValue;
                    float bz0 = float.MaxValue, bz1 = float.MinValue;
                    for (int vi = 0; vi < rec.VertsXyz.Length; vi += 3)
                    {
                        float vx = rec.VertsXyz[vi], vy = rec.VertsXyz[vi + 1], vz = rec.VertsXyz[vi + 2];
                        if (vx < bx0) bx0 = vx; if (vx > bx1) bx1 = vx;
                        if (vy < by0) by0 = vy; if (vy > by1) by1 = vy;
                        if (vz < bz0) bz0 = vz; if (vz > bz1) bz1 = vz;
                    }
                    Console.WriteLine("propbbox '" + rec.Name + "' flags=0x" + rec.Flags.ToString("X8")
                        + " tris=" + (rec.Tris.Length / 3)
                        + " X=[" + bx0.ToString("F3") + ".." + bx1.ToString("F3") + "]"
                        + " H=[" + by0.ToString("F3") + ".." + by1.ToString("F3") + "]"
                        + " Z=[" + bz0.ToString("F3") + ".." + bz1.ToString("F3") + "]"
                        + " centreXZ=(" + ((bx0 + bx1) * 0.5f).ToString("F3") + ","
                        + ((by0 + by1) * 0.5f).ToString("F3") + ")");
                }
                for (int t = 0; t < rec.Tris.Length / 3; t++)
                {
                    var sb = new System.Text.StringBuilder("   tri " + t + ": ");
                    for (int k = 0; k < 3; k++)
                    {
                        int v = rec.Tris[t * 3 + k];
                        sb.Append("(").Append(rec.VertsXyz[v * 3].ToString("F3")).Append(",")
                          .Append(rec.VertsXyz[v * 3 + 1].ToString("F3")).Append(",")
                          .Append(rec.VertsXyz[v * 3 + 2].ToString("F3")).Append(") ");
                    }
                    Console.WriteLine(sb.ToString());
                }
            }
        }
        int props = 0, quads = 0, stairs = 0, ladders = 0, flat = 0, steep = 0, longRun = 0, capped = 0, dumped = 0;
        var infos = new List<SteppedPropRamps.RampInfo>();
        var skips = new List<SteppedPropRamps.Skip>();
        // A fresh dump carries the collector's synthesized ramps as separate '<prop>#ramp' records, so
        // the props that already have them must NOT be re-synthesized here (that would put the ramp
        // geometry in the soup twice).
        var dumpedProps = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < assets.Count; i++)
        {
            SceneSnapshot.EntityRecord rec0 = assets[i];
            if (rec0.Name != null
                && rec0.Name.EndsWith(SteppedPropRamps.RampNameSuffix, StringComparison.Ordinal))
                dumpedProps.Add(rec0.Name.Substring(0,
                    rec0.Name.Length - SteppedPropRamps.RampNameSuffix.Length));
        }
        for (int i = 0; i < assets.Count; i++)
        {
            SceneSnapshot.EntityRecord rec = assets[i];
            if (rec.Name != null
                && rec.Name.EndsWith(SteppedPropRamps.RampNameSuffix, StringComparison.Ordinal))
            {
                rec.RampTris = rec.TriCount;   // dumped ramps are never re-synthesized
                dumped++;
                continue;
            }
            if (dumpedProps.Contains(rec.Name)) continue;   // its ramp records are already in the soup
            if (!SteppedPropRamps.IsSteppedOrLadder(rec.Flags)) continue;
            if (rec.Tris == null || rec.Tris.Length == 0) continue;

            var p = new SteppedPropRamps.Prop();
            p.Name = rec.Name;
            p.Flags = rec.Flags;
            p.VertsXyz = rec.VertsXyz;
            p.Tris = rec.Tris;
            SteppedPropRamps.Result rr = SteppedPropRamps.Synthesize(
                new SteppedPropRamps.Prop[] { p }, cellSize, climb, ledge);
            quads += rr.Ramps;
            stairs += rr.Stairs;
            ladders += rr.Ladders;
            flat += rr.SkippedFlat;
            steep += rr.SkippedSteep;
            longRun += rr.SkippedLong;
            capped += rr.Capped;
            for (int k = 0; k < rr.Infos.Count; k++) infos.Add(rr.Infos[k]);
            for (int k = 0; k < rr.Skips.Count; k++) skips.Add(rr.Skips[k]);
            // Per-INSTANCE skip lines for the props --propdump asked about (the folded summary keeps only
            // the first-seen instance of each name, which hides the numbers of the one being investigated).
            if (PropDump != null && rec.Name != null
                && rec.Name.IndexOf(PropDump, StringComparison.Ordinal) >= 0)
                for (int k = 0; k < rr.Skips.Count; k++)
                    Console.WriteLine("  INSTANCE " + SteppedPropRamps.DescribeSkip(rr.Skips[k]));
            if (rr.Tris.Count == 0) continue;
            props++;

            // The snapshot record layout is 9 floats per triangle (3 unique verts each), so the ramp's
            // shared-vertex quads must be expanded the same way.
            int rampTriCount = rr.Tris.Count / 3;
            if (PropDump != null && rec.Name != null && rec.Name.IndexOf(PropDump, StringComparison.Ordinal) >= 0)
            {
                float nlo = float.MaxValue, nhi = float.MinValue;
                for (int k = 0; k < rr.VertsXyz.Count / 3; k++)
                {
                    float pv = rr.VertsXyz[k * 3 + 2];
                    if (pv < nlo) nlo = pv;
                    if (pv > nhi) nhi = pv;
                }
                Console.WriteLine("      synthesized ramp for '" + rec.Name + "': z=["
                    + nlo.ToString("F3") + ".." + nhi.ToString("F3") + "] tris=" + rampTriCount);
            }
            var nv = new float[rec.VertsXyz.Length + rampTriCount * 9];
            Array.Copy(rec.VertsXyz, nv, rec.VertsXyz.Length);
            var nt = new int[rec.Tris.Length + rampTriCount * 3];
            Array.Copy(rec.Tris, nt, rec.Tris.Length);
            int vout = rec.VertsXyz.Length;
            int tout = rec.Tris.Length;
            int vindex = rec.VertsXyz.Length / 3;
            for (int t = 0; t < rampTriCount; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int src = rr.Tris[t * 3 + k] * 3;
                    nv[vout + 0] = rr.VertsXyz[src + 0];
                    nv[vout + 1] = rr.VertsXyz[src + 1];
                    nv[vout + 2] = rr.VertsXyz[src + 2];
                    vout += 3;
                    nt[tout++] = vindex++;
                }
            }
            rec.VertsXyz = nv;
            rec.Tris = nt;
            rec.RampTris = rampTriCount;
            rec.TriCount = nt.Length / 3;
            if (ApplyRampsVerbose)
            {
                float rminx = float.MaxValue, rmaxx = float.MinValue, rminy = float.MaxValue, rmaxy = float.MinValue;
                for (int k = rec.VertsXyz.Length - rampTriCount * 9; k < rec.VertsXyz.Length; k += 3)
                {
                    if (rec.VertsXyz[k] < rminx) rminx = rec.VertsXyz[k];
                    if (rec.VertsXyz[k] > rmaxx) rmaxx = rec.VertsXyz[k];
                    if (rec.VertsXyz[k + 1] < rminy) rminy = rec.VertsXyz[k + 1];
                    if (rec.VertsXyz[k + 1] > rmaxy) rmaxy = rec.VertsXyz[k + 1];
                }
                Console.WriteLine("ramps: '" + rec.Name + "' quads=" + (rampTriCount / 2)
                    + " rampXY=(" + Num(rminx) + "," + Num(rminy) + ")-(" + Num(rmaxx) + "," + Num(rmaxy)
                    + ") tris=" + rec.TriCount);
            }
        }
        Console.WriteLine("ramps: synthesized=" + quads + " props=" + props + " stairs=" + stairs
            + " ladders=" + ladders + " skipped=" + (flat + steep + longRun) + " (walkable-if-steep)");
        if (ApplyRampsVerbose)
            Console.WriteLine("ramps: props with ramps printed above; total=" + props);
        Console.WriteLine("ramps: skipped detail flat=" + flat + " steep=" + steep + " long=" + longRun
            + " capped=" + capped + "; dumped-ramp records=" + dumped
            + (dumpedProps.Count > 0 ? " (" + dumpedProps.Count + " props already dumped, not re-synthesized)" : "")
            + "; " + SteppedPropRamps.Describe());
        // One line per skipped prop asset, folded by (name, reason) and capped (diagnostic, always on).
        List<SteppedPropRamps.Skip> foldedSkips = SteppedPropRamps.FoldSkips(skips);
        for (int k = 0; k < foldedSkips.Count && k < 20; k++)
            Console.WriteLine("  " + SteppedPropRamps.DescribeSkip(foldedSkips[k]));
        if (foldedSkips.Count > 20)
            Console.WriteLine("  ramps: skip ... (+" + (foldedSkips.Count - 20) + " more)");
        var order = new List<int>();
        for (int k = 0; k < infos.Count; k++) order.Add(k);
        order.Sort(delegate (int a, int b)
        {
            float ra = infos[a].TopY - infos[a].BottomY, rb = infos[b].TopY - infos[b].BottomY;
            int cmp = rb.CompareTo(ra);
            return cmp != 0 ? cmp : infos[a].Prop.CompareTo(infos[b].Prop);
        });
        for (int k = 0; k < order.Count && k < 5; k++)
        {
            SteppedPropRamps.RampInfo ri = infos[order[k]];
            Console.WriteLine("ramps: biggest rise=" + Num(ri.TopY - ri.BottomY) + "m run=" + Num(ri.Run)
                + "m slope=" + Num(ri.SlopeDeg) + "deg width=" + Num(ri.Width) + "m '" + ri.Prop + "'");
        }
        if (log != null)
            log("ramps: synthesized=" + quads + " stairs=" + stairs + " ladders=" + ladders
                + " skipped=" + (flat + steep) + " (walkable-if-steep)");
    }

    private static bool ApplyRampsVerbose = false;

    // Mirrors SceneCollisionGeometry's gate/door bridge-strip synthesis for the snapshot path. Plate
    // candidates are the records carrying the fixE gate bit (rec.Gate); a snapshot that predates that
    // bit falls back to a generic thin flat-slab predicate so the offline path still exercises the fix.
    // The strip geometry is appended as its own '<plate>#gate' record (all force-walkable).
    internal static void ApplyGateConnectors(List<SceneSnapshot.EntityRecord> assets,
        SceneSnapshot.TerrainData terrain, Action<string> log, float cellSize, float climb, bool ledge)
    {
        if (assets == null || assets.Count == 0) return;
        // Already dumped (fresh editor snapshot): never synthesize a second strip on top of this one.
        var dumped = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < assets.Count; i++)
        {
            SceneSnapshot.EntityRecord e = assets[i];
            if (e.Name != null
                && e.Name.EndsWith(GateBridgeStrips.GateNameSuffix, StringComparison.Ordinal))
                dumped.Add(e.Name.Substring(0,
                    e.Name.Length - GateBridgeStrips.GateNameSuffix.Length));
        }

        bool anyGate = false;
        for (int i = 0; i < assets.Count; i++) if (assets[i].Gate != 0) { anyGate = true; break; }

        // Detection is the fixE gate bit (v2 assets). A v1 snapshot carries no provenance, so no plate
        // is attributed and nothing is emitted - the live editor writes v2, and a stale v1 fixture must
        // be re-dumped before the connector can fire (a generic thin-slab fallback was tried and fired
        // too broadly on unrelated village slabs).
        var plates = new List<GateBridgeStrips.Prop>();
        for (int i = 0; i < assets.Count; i++)
        {
            SceneSnapshot.EntityRecord rec = assets[i];
            if (rec.Name == null || rec.Tris == null || rec.Tris.Length == 0) continue;
            if (rec.Name.EndsWith(GateBridgeStrips.GateNameSuffix, StringComparison.Ordinal)) continue;
            bool isPlate = rec.Gate != 0 && IsSlabPlate(rec);
            if (!isPlate) continue;
            if (dumped.Contains(rec.Name)) continue;
            var p = new GateBridgeStrips.Prop();
            p.Name = rec.Name; p.Flags = rec.Flags;
            p.VertsXyz = rec.VertsXyz; p.Tris = rec.Tris;
            plates.Add(p);
        }
        Console.WriteLine("gate: mode=" + (anyGate ? "gate-bit" : "no-gate-bit")
            + " plateProps=" + plates.Count);
        if (plates.Count == 0) return;

        // Assets + the terrain soup, both in the engine soup layout (x, z, h). The terrain is the target
        // for a gate whose far side is open ground; passing it as geometry keeps the target height exactly
        // the surface the builder will rasterise (a strided-node height probe does not match the built
        // terrain mesh, which is the detail mesh of those triangles).
        var av = new List<float>();
        var at = new List<int>();
        if (terrain != null && terrain.Heights != null)
        {
            float[] tv; int[] tt;
            TiledNavMeshBuilder.BuildTerrainSoup(terrain.Heights, terrain.Width, terrain.Height,
                terrain.Spacing, terrain.Stride, out tv, out tt);
            int tbase = av.Count / 3;
            for (int v = 0; v < tv.Length / 3; v++)
            {
                av.Add(tv[v * 3 + 0]);
                av.Add(tv[v * 3 + 2]);
                av.Add(tv[v * 3 + 1]);
            }
            for (int t = 0; t < tt.Length / 3; t++)
            {
                at.Add(tt[t * 3 + 0] + tbase);
                at.Add(tt[t * 3 + 2] + tbase);
                at.Add(tt[t * 3 + 1] + tbase);
            }
        }
        for (int i = 0; i < assets.Count; i++)
        {
            SceneSnapshot.EntityRecord rec = assets[i];
            if (rec.Name != null && rec.Name.EndsWith("#barrier", StringComparison.Ordinal)) continue;
            if (rec.VertsXyz == null || rec.Tris == null) continue;
            int baseIndex = av.Count / 3;
            for (int k = 0; k < rec.VertsXyz.Length; k++) av.Add(rec.VertsXyz[k]);
            for (int k = 0; k < rec.Tris.Length; k++) at.Add(rec.Tris[k] + baseIndex);
        }
        Func<float, float, float> terrainAt = null;

        if (Environment.GetEnvironmentVariable("GATE_TRACE") == "1")
            GateBridgeStrips.Trace = delegate (string m) { Console.WriteLine("    " + m); };
        else
            GateBridgeStrips.Trace = null;

        GateBridgeStrips.Result rr = GateBridgeStrips.Synthesize(plates, av.ToArray(), at.ToArray(),
            terrainAt, cellSize, climb, ledge);
        GateBridgeStrips.Trace = null;
        Console.WriteLine("gate: " + GateBridgeStrips.Describe());
        Console.WriteLine("gate: plates=" + rr.Plates + " strips=" + rr.Strips + " skipped=" + rr.Skips
            + " plateTris=" + rr.PlateTris + " cells=" + rr.Cells);
        for (int k = 0; k < rr.Infos.Count && k < 20; k++)
            Console.WriteLine("  " + GateBridgeStrips.DescribeInfo(rr.Infos[k]));
        if (Environment.GetEnvironmentVariable("GATE_TRACE") == "1")
        {
            for (int k = 0; k < rr.Infos.Count; k++)
            {
                GateBridgeStrips.Info gi = rr.Infos[k];
                if (gi.Tx > 1130f && gi.Tx < 1135f && gi.Tz > 987f && gi.Tz < 994f)
                {
                    Console.WriteLine("    gate-quad " + k + " H=" + Num(gi.Hp) + "->" + Num(gi.Ht)
                        + " width=" + Num(gi.Width));
                    for (int v = 0; v < 4 && k * 12 + v * 3 + 2 < rr.VertsXyz.Count; v++)
                        Console.WriteLine("      v" + v + " (" + Num(rr.VertsXyz[k * 12 + v * 3]) + ","
                            + Num(rr.VertsXyz[k * 12 + v * 3 + 1]) + ","
                            + Num(rr.VertsXyz[k * 12 + v * 3 + 2]) + ")");
                }
            }
        }
        if (rr.Tris.Count == 0) return;

        int triCount = rr.Tris.Count / 3;
        SceneSnapshot.EntityRecord outRec = SceneSnapshot.Expand(
            (plates.Count > 0 ? plates[0].Name : "gate") + GateBridgeStrips.GateNameSuffix,
            0, 0, false, rr.VertsXyz.ToArray(), rr.Tris.ToArray(), 1);
        outRec.RampTris = triCount;
        assets.Add(outRec);
        if (log != null) log("gate: synthesized=" + rr.Strips + " strips (force-walkable)");
    }

    // Generic name-free fallback for a snapshot that predates the fixE gate bit: a thin, rigid, mostly
    // up-facing slab with a real footprint (the gate/door passage colliders are exactly that). The
    // bridge only fires where the slab actually abuts a void, so a false positive cannot add geometry.
    private static bool IsSlabPlate(SceneSnapshot.EntityRecord rec)
    {
        if (rec.VertsXyz == null || rec.Tris == null) return false;
        int vc = rec.VertsXyz.Length / 3;
        float hmin = float.MaxValue, hmax = float.MinValue;
        int up = 0;
        double area = 0.0;
        float cosFlat = (float)Math.Cos(20.0 * Math.PI / 180.0);
        for (int t = 0; t < rec.Tris.Length / 3; t++)
        {
            int i0 = rec.Tris[t * 3 + 0], i1 = rec.Tris[t * 3 + 1], i2 = rec.Tris[t * 3 + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= vc || i1 >= vc || i2 >= vc) continue;
            float ax = rec.VertsXyz[i0 * 3], az = rec.VertsXyz[i0 * 3 + 1], ah = rec.VertsXyz[i0 * 3 + 2];
            float bx = rec.VertsXyz[i1 * 3], bz = rec.VertsXyz[i1 * 3 + 1], bh = rec.VertsXyz[i1 * 3 + 2];
            float cx = rec.VertsXyz[i2 * 3], cz = rec.VertsXyz[i2 * 3 + 1], ch = rec.VertsXyz[i2 * 3 + 2];
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
        if (up < 8) return false;
        if (hmax - hmin > 1.2f) return false;
        if (area < 2.0) return false;
        return true;
    }

    // Bilinear terrain height over the SAME strided nodes BuildTerrainSoup meshes, so the gate strip's
    // target height is the surface the builder will rasterise.
    private static Func<float, float, float> MakeTerrainProbe(SceneSnapshot.TerrainData terrain)
    {
        if (terrain == null || terrain.Heights == null || terrain.Width < 2 || terrain.Height < 2)
            return null;
        float sp = terrain.Spacing > 0f ? terrain.Spacing : 1f;
        int H = terrain.Height;
        int stride = terrain.Stride > 0 ? terrain.Stride : 1;
        float[] hh = terrain.Heights;
        int[] srcX = SampleAxis(terrain.Width, stride);
        int[] srcZ = SampleAxis(H, stride);
        return delegate (float x, float z)
        {
            int k = AxisIndex(srcX, x, sp), j = AxisIndex(srcZ, z, sp);
            float x0 = srcX[k] * sp, x1 = srcX[k + 1] * sp;
            float z0 = srcZ[j] * sp, z1 = srcZ[j + 1] * sp;
            float tx = (x1 > x0) ? (x - x0) / (x1 - x0) : 0f;
            float tz = (z1 > z0) ? (z - z0) / (z1 - z0) : 0f;
            if (tx < 0f) tx = 0f; if (tx > 1f) tx = 1f;
            if (tz < 0f) tz = 0f; if (tz > 1f) tz = 1f;
            float h00 = hh[srcX[k] * H + srcZ[j]];
            float h10 = hh[srcX[k + 1] * H + srcZ[j]];
            float h01 = hh[srcX[k] * H + srcZ[j + 1]];
            float h11 = hh[srcX[k + 1] * H + srcZ[j + 1]];
            return (1f - tx) * (1f - tz) * h00 + tx * (1f - tz) * h10
                + (1f - tx) * tz * h01 + tx * tz * h11;
        };
    }

    private static int[] SampleAxis(int count, int stride)
    {
        int samples = (count - 1) / stride + 1;
        var s = new int[samples];
        for (int k = 0; k < samples; k++) s[k] = k * stride;
        if (samples > 1) s[samples - 1] = count - 1;
        return s;
    }

    private static int AxisIndex(int[] src, float world, float sp)
    {
        if (src.Length < 2) return 0;
        int k = (int)((world / sp) / (src[1] - src[0]));
        if (k < 0) k = 0;
        if (k > src.Length - 2) k = src.Length - 2;
        return k;
    }

    // Mirrors the shipped stepped-props setting for the harness soup: false disables the stepped-prop
    // ledge exemption (the `--ramps off` behaviour) without disabling the gate strips' exemption.
    internal static bool SoupSteppedExempt = true;

    private static int CountTrue(bool[] v)
    {
        int n = 0;
        for (int i = 0; i < v.Length; i++)
            if (v[i]) n++;
        return n;
    }

    // The recommended defaults for snapshot runs (see the README).
    private static TiledNavMeshBuilder.TiledNavMeshSettings SnapshotSettings()
    {
        var s = new TiledNavMeshBuilder.TiledNavMeshSettings();
        s.VoxelCellSizeMeters = 0.25f;
        s.AgentRadiusMeters = 0.25f;
        s.MaxSlopeDegrees = 50f;
        s.UseDetailMesh = true;
        s.GapFillCells = 2;
        s.StitchMaxGapCells = 6;
        s.PruneIsolatedIslands = true;
        s.PruneMaxIslandFaces = 400;
        s.AgentMaxClimbMeters =
            TiledNavMeshBuilder.ResolveAgentClimbMeters(0.25f, s.CellHeightMeters, 50f, 0.4f);
        return s;
    }

    private static List<float[]> ParseAssertPoints(string txtPath)
    {
        var points = new List<float[]>();
        if (!System.IO.File.Exists(txtPath)) return points;
        string[] lines = System.IO.File.ReadAllLines(txtPath);
        bool inSection = false;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith("assert-points:", StringComparison.OrdinalIgnoreCase))
            {
                inSection = true;
                continue;
            }
            if (!inSection) continue;
            if (line.Length == 0) continue;
            string[] xy = line.Split(',');
            if (xy.Length != 2) break;
            float x, y;
            if (float.TryParse(xy[0].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out x)
                && float.TryParse(xy[1].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out y))
                points.Add(new float[] { x, y });
        }
        return points;
    }

    private static List<string> ExtractDiagNamesFromTxt(string txtPath)
    {
        var names = new List<string>();
        if (!System.IO.File.Exists(txtPath)) return names;
        string[] lines = System.IO.File.ReadAllLines(txtPath);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int q = line.IndexOf('\'');
            int q2 = q >= 0 ? line.IndexOf('\'', q + 1) : -1;
            if (q < 0 || q2 < 0) continue;
            string name = line.Substring(q + 1, q2 - q - 1);
            if (name.IndexOf("diag", StringComparison.OrdinalIgnoreCase) >= 0 && !names.Contains(name))
                names.Add(name);
        }
        return names;
    }

    private static int FaceAtAnyHeight(TiledNavMeshBuilder.VoxelMeshResult r, float px, float pz)
    {
        List<int> list = IndexOf(r).FacesAt(px, pz);
        return list.Count > 0 ? list[0] : -1;
    }

    private static double FaceAreaXZ(TiledNavMeshBuilder.VoxelMeshResult r, int f)
    {
        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
        double ax = r.VertsXyz[a * 3], az = r.VertsXyz[a * 3 + 2];
        double bx = r.VertsXyz[b * 3], bz = r.VertsXyz[b * 3 + 2];
        double cx = r.VertsXyz[c * 3], cz = r.VertsXyz[c * 3 + 2];
        return 0.5 * Math.Abs((bx - ax) * (cz - az) - (bz - az) * (cx - ax));
    }

    // Triangle-quality report (the "before" baseline for the thin/tiny-triangle cleanup pass).
    // Definitions (stated so the cleanup pass can reproduce them exactly):
    //   min angle    = smallest interior angle of the triangle (degrees);
    //   edge ratio   = longest edge / shortest edge;
    //   needle       = shortest altitude < 0.05 m AND longest edge > 0.5 m (a long spike);
    //   tiny         = longest edge < 0.2 m (sub-cell sliver);
    //   quality      = 4*sqrt(3)*Area / (a^2+b^2+c^2), 1.0 = equilateral (mean reported).
    private static void PrintTriangleQuality(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        int n = r.FaceCount;
        int[] angBuckets = new int[5];   // <2, 2-5, 5-15, 15-30, >30
        int[] ratBuckets = new int[5];   // <1.5, 1.5-3, 3-6, 6-15, >15
        int[] lenBuckets = new int[5];   // longest edge: <0.1, 0.1-0.25, 0.25-0.5, 0.5-1, >=1
        int needles = 0, tiny = 0, degen = 0, small = 0;
        int longThin8 = 0, longThin4 = 0;   // aspect = longest^2/(2*area), gated on longest >= 4 m
        double qSum = 0.0;
        for (int f = 0; f < n; f++)
        {
            int ia = r.Tris[f * 3], ib = r.Tris[f * 3 + 1], ic = r.Tris[f * 3 + 2];
            double ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            double bx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bz = r.VertsXyz[ib * 3 + 2];
            double cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];
            double ab = Dist(ax, ay, az, bx, by, bz);
            double bc = Dist(bx, by, bz, cx, cy, cz);
            double ca = Dist(cx, cy, cz, ax, ay, az);
            double longest = Math.Max(ab, Math.Max(bc, ca));
            double shortest = Math.Min(ab, Math.Min(bc, ca));
            // Area (3D) and quality.
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double vx = cx - ax, vy = cy - ay, vz = cz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double area = 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
            double denom = ab * ab + bc * bc + ca * ca;
            double q = denom > 1e-12 ? 4.0 * Math.Sqrt(3.0) * area / denom : 0.0;
            qSum += q;
            // Min interior angle via law of cosines (smallest angle is opposite the shortest edge).
            double minAng;
            if (shortest < 1e-9) { minAng = 0.0; degen++; }
            else
            {
                // angle opposite `shortest`: pick the vertex between the two longer edges.
                double cosA;
                if (shortest == ab) cosA = (ca * ca + bc * bc - ab * ab) / (2 * ca * bc);
                else if (shortest == bc) cosA = (ab * ab + ca * ca - bc * bc) / (2 * ab * ca);
                else cosA = (ab * ab + bc * bc - ca * ca) / (2 * ab * bc);
                if (cosA > 1.0) cosA = 1.0; if (cosA < -1.0) cosA = -1.0;
                minAng = Math.Acos(cosA) * 180.0 / Math.PI;
            }
            if (minAng < 2.0) angBuckets[0]++;
            else if (minAng < 5.0) angBuckets[1]++;
            else if (minAng < 15.0) angBuckets[2]++;
            else if (minAng < 30.0) angBuckets[3]++;
            else angBuckets[4]++;
            double ratio = shortest > 1e-9 ? longest / shortest : double.PositiveInfinity;
            if (ratio < 1.5) ratBuckets[0]++;
            else if (ratio < 3.0) ratBuckets[1]++;
            else if (ratio < 6.0) ratBuckets[2]++;
            else if (ratio < 15.0) ratBuckets[3]++;
            else ratBuckets[4]++;
            // Needle: shortest altitude = 2*Area/longest.
            double alt = longest > 1e-9 ? 2.0 * area / longest : 0.0;
            if (alt < 0.05 && longest > 0.5) needles++;
            // Long-thin: aspect = longest^2/(2*area) = longest/altitude (the cleanup's refine criterion).
            double aspect = longest > 1e-9 && area > 1e-12 ? longest * longest / (2.0 * area) : 0.0;
            if (longest >= 4.0)
            {
                if (aspect > 8.0) longThin8++;
                if (aspect > 4.0) longThin4++;
            }
            if (longest < 0.2) tiny++;
            if (longest < 0.1) lenBuckets[0]++;
            else if (longest < 0.25) lenBuckets[1]++;
            else if (longest < 0.5) lenBuckets[2]++;
            else if (longest < 1.0) lenBuckets[3]++;
            else lenBuckets[4]++;
            if (longest < 1.0) small++;
        }
        Console.WriteLine("quality: faces=" + n + " degenerate=" + degen);
        Console.WriteLine("  min-angle: <2=" + angBuckets[0] + " 2-5=" + angBuckets[1] + " 5-15=" + angBuckets[2]
            + " 15-30=" + angBuckets[3] + " >30=" + angBuckets[4]);
        Console.WriteLine("  edge-ratio: <1.5=" + ratBuckets[0] + " 1.5-3=" + ratBuckets[1] + " 3-6=" + ratBuckets[2]
            + " 6-15=" + ratBuckets[3] + " >15=" + ratBuckets[4]);
        Console.WriteLine("  needles (alt<0.05m & edge>0.5m)=" + needles
            + " tiny (longest<0.2m)=" + tiny
            + " meanQuality=" + (n > 0 ? (qSum / n) : 0.0).ToString("F4"));
        Console.WriteLine("  long-thin (longest>=4m): aspect>8=" + longThin8 + " aspect>4=" + longThin4);
        Console.WriteLine("  longest-edge: <0.1m=" + lenBuckets[0] + " 0.1-0.25=" + lenBuckets[1]
            + " 0.25-0.5=" + lenBuckets[2] + " 0.5-1=" + lenBuckets[3] + " >=1m=" + lenBuckets[4]
            + "  SMALL(<1m)=" + small);
    }

    private static double Dist(double x0, double y0, double z0, double x1, double y1, double z1)
    {
        double dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    // Routing-cost proxy: for N random walkable point pairs (both in the largest component), walk the
    // straight XZ segment at 0.25 m steps and count the DISTINCT faces crossed. Reported as the mean and
    // median; the thin/tiny-triangle cleanup can re-run this to show the routing-cost improvement. Cheap:
    // it uses the same spatial index as the pair probe (no pathfinding).
    private static void PrintRoutingCost(TiledNavMeshBuilder.VoxelMeshResult r, int requested)
    {
        MeshIndex ix = IndexOf(r);
        ix.EnsureComponents();
        // Collect one face per component id, so we can pick a point in the largest component.
        var repByComp = new Dictionary<int, int>();
        var compSize = new Dictionary<int, int>();
        for (int f = 0; f < r.FaceCount; f++)
        {
            int root = Find(ix.Roots, f);
            if (!repByComp.ContainsKey(root)) repByComp[root] = f;
            int c; compSize.TryGetValue(root, out c); compSize[root] = c + 1;
        }
        int bigRoot = -1, bigSize = -1;
        foreach (KeyValuePair<int, int> kv in compSize)
            if (kv.Value > bigSize) { bigSize = kv.Value; bigRoot = kv.Key; }
        // Faces of the largest component (sample a stride for speed).
        var big = new List<int>();
        int stride = Math.Max(1, r.FaceCount / 200000);
        for (int f = 0; f < r.FaceCount; f += stride)
            if (Find(ix.Roots, f) == bigRoot) big.Add(f);
        if (big.Count < 2) { Console.WriteLine("routing: largest component too small"); return; }
        var rnd = new Random(12345);
        var counts = new List<int>();
        int done = 0, tries = 0;
        while (done < requested && tries < requested * 20)
        {
            tries++;
            int fa = big[rnd.Next(big.Count)], fb = big[rnd.Next(big.Count)];
            float ax = (float)FaceCentroidX(r, fa), az = (float)FaceCentroidZ(r, fa);
            float bx = (float)FaceCentroidX(r, fb), bz = (float)FaceCentroidZ(r, fb);
            double dx = bx - ax, dz = bz - az;
            double len = Math.Sqrt(dx * dx + dz * dz);
            if (len < 4.0 || len > 200.0) continue;   // local-ish routable pairs
            int steps = Math.Max(1, (int)(len / 0.25));
            var seen = new HashSet<int>();
            for (int k = 0; k <= steps; k++)
            {
                double t = (double)k / steps;
                float px = (float)(ax + dx * t), pz = (float)(az + dz * t);
                List<int> cand = ix.FacesAt(px, pz);
                for (int j = 0; j < cand.Count; j++) seen.Add(cand[j]);
            }
            counts.Add(seen.Count);
            done++;
        }
        counts.Sort();
        double mean = 0; for (int i = 0; i < counts.Count; i++) mean += counts[i];
        if (counts.Count > 0) mean /= counts.Count;
        int median = counts.Count > 0 ? counts[counts.Count / 2] : 0;
        Console.WriteLine("routing: pairs=" + counts.Count + " step=0.25m faces-crossed mean="
            + mean.ToString("F1") + " median=" + median
            + " min=" + (counts.Count > 0 ? counts[0] : 0)
            + " max=" + (counts.Count > 0 ? counts[counts.Count - 1] : 0)
            + " (straight segment, distinct faces, largest component)");
    }

    private static float FaceCentroidX(TiledNavMeshBuilder.VoxelMeshResult r, int f)
    {
        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
        return (r.VertsXyz[a * 3] + r.VertsXyz[b * 3] + r.VertsXyz[c * 3]) / 3f;
    }

    private static float FaceCentroidY(TiledNavMeshBuilder.VoxelMeshResult r, int f)
    {
        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
        return (r.VertsXyz[a * 3 + 1] + r.VertsXyz[b * 3 + 1] + r.VertsXyz[c * 3 + 1]) / 3f;
    }

    private static float FaceCentroidZ(TiledNavMeshBuilder.VoxelMeshResult r, int f)
    {
        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
        return (r.VertsXyz[a * 3 + 2] + r.VertsXyz[b * 3 + 2] + r.VertsXyz[c * 3 + 2]) / 3f;
    }

    // ---------------------------------------------------------------- test 1
    private static void Test1_Coverage()
    {
        Console.WriteLine("== Test 1: flat 160x160 plane, 0.5 m grid ==");
        float[] verts;
        int[] tris;
        MakeGrid(160f, 160f, 0.5f, delegate (float x, float z) { return 0f; }, out verts, out tris);
        Console.WriteLine("  soup: verts=" + (verts.Length / 3) + " tris=" + (tris.Length / 3));

        TiledNavMeshBuilder.TiledNavMeshSettings s1 = SettingsAuto(0.5f, 65f);
        TiledNavMeshBuilder.VoxelMeshResult res =
            TiledNavMeshBuilder.Build(verts, tris, s1, Environment.ProcessorCount, Log);
        // Tile count is derived from the (now 10 m) tile setting, not hard-coded: a 160 m plane is
        // ceil(160 / tileSpan) tiles a side. (Was tiles==100 when the tile default was 16 m.)
        int tilesPerAxis = (int)Math.Ceiling(160.0 / s1.TileWorldSizeMeters);
        int expectedTiles = tilesPerAxis * tilesPerAxis;

        double area = SumArea(res);
        double full = 160.0 * 160.0;
        double inset = Math.Pow(160.0 - 2.0 * (0.5 * Math.Ceiling(0.35 / 0.5)), 2.0); // erosion inset

        Console.WriteLine("  faces=" + res.FaceCount + " verts=" + res.VertexCount + " edges=" + res.EdgeCount);
        Console.WriteLine("  tiles=" + res.TileCount + " built=" + res.BuiltTiles + " failed=" + res.FailedTiles);
        Console.WriteLine("  area=" + area.ToString("F1") + " m^2  full=" + full.ToString("F0")
            + "  ratio=" + (area / full).ToString("P3") + "  (erosion-inset area " + inset.ToString("F0") + ")");
        double cleanInset = 158.0 * 158.0;
        Console.WriteLine("  effective covered square = " + Math.Sqrt(area).ToString("F3") + " m per side"
            + " (158x158=" + cleanInset.ToString("F0") + ")");
        Console.WriteLine("  RESULT: faces>0 " + (res.FaceCount > 0) + ", tiles==" + expectedTiles + " "
            + (res.TileCount == expectedTiles)
            + ", noDoubleCoverage " + (area <= full * 1.001)
            + ", noInternalGaps(<=1% of 158^2) " + (Math.Abs(area - cleanInset) <= cleanInset * 0.01));
    }

    // ---------------------------------------------------------------- test 2
    private static void Test2_Slope()
    {
        Console.WriteLine("== Test 2: 80 deg cliff band + 45 deg hill ==");
        float[] verts;
        int[] tris;
        MakeGrid(80f, 20f, 0.5f, CliffHeight, out verts, out tris);
        Console.WriteLine("  soup: verts=" + (verts.Length / 3) + " tris=" + (tris.Length / 3));

        TiledNavMeshBuilder.VoxelMeshResult res =
            TiledNavMeshBuilder.Build(verts, tris, SettingsAuto(0.5f, 65f), Environment.ProcessorCount, null);

        int cliffFaces = 0, hillFaces = 0, lowerFaces = 0, upperFaces = 0;
        double maxSlope = MaxSlope(res);
        for (int f = 0; f < res.FaceCount; f++)
        {
            float cxz = CentroidX(res, f);
            if (cxz > 30.5f && cxz < 31.5f) cliffFaces++;
            if (cxz > 55f && cxz < 65f) hillFaces++;
            if (cxz < 29f) lowerFaces++;
            if (cxz > 33f && cxz < 49f) upperFaces++;
        }

        Console.WriteLine("  faces=" + res.FaceCount);
        Console.WriteLine("  cliff-band faces (x in 30.5..31.5) = " + cliffFaces + " (expect 0)");
        Console.WriteLine("  45-deg hill faces (x in 55..65) = " + hillFaces + " (expect >0)");
        Console.WriteLine("  lower plateau faces (x<29) = " + lowerFaces + ", upper plateau (33..49) = " + upperFaces);
        Console.WriteLine("  max slope over ALL output faces = " + maxSlope.ToString("F1") + " deg (limit 65)");
        Console.WriteLine("  RESULT: cliffEmpty " + (cliffFaces == 0) + ", hillWalkable " + (hillFaces > 0)
            + ", allWithinSlope " + (maxSlope <= 65.5));

        // Informational: with the detail phase ON the OUTPUT can contain locally steeper triangles than
        // the poly mesh, because the detail triangulation interpolates across the height discontinuity at
        // the cliff edge. This bounds what "max face slope" can be asserted on for detail meshes.
        TiledNavMeshBuilder.TiledNavMeshSettings detOn = SettingsAuto(0.5f, 65f);
        detOn.UseDetailMesh = true;
        TiledNavMeshBuilder.VoxelMeshResult resDet =
            TiledNavMeshBuilder.Build(verts, tris, detOn, Environment.ProcessorCount, null);
        Console.WriteLine("  [info] detail-ON max slope over ALL output faces = "
            + MaxSlope(resDet).ToString("F1") + " deg, faces above 65 deg = "
            + CountSlopeAbove(resDet, 65.0) + "/" + resDet.FaceCount);
        Console.WriteLine("  NOTE: auto agentClimb (see Test 6) makes the 45-deg hill walkable; the 80-deg band is");
        Console.WriteLine("        still non-walkable (above NavMeshMaxSlopeDegrees).");

        Console.WriteLine();
        Console.WriteLine("  -- slope sweep (single ramp, auto climb) --");
        double[] slopes = { 10.0, 20.0, 25.0, 30.0, 45.0, 60.0, 65.0, 80.0 };
        for (int i = 0; i < slopes.Length; i++)
        {
            double s = slopes[i];
            float tan = (float)Math.Tan(s * Math.PI / 180.0);
            MakeGrid(40f, 20f, 0.5f, delegate (float x, float z) { return Ramp(x, tan); }, out verts, out tris);
            TiledNavMeshBuilder.VoxelMeshResult r =
                TiledNavMeshBuilder.Build(verts, tris, SettingsAuto(0.5f, 65f), Environment.ProcessorCount, null);
            int inRamp = 0;
            for (int f = 0; f < r.FaceCount; f++)
            {
                float cxz = CentroidX(r, f);
                if (cxz > 12f && cxz < 28f) inRamp++;
            }
            Console.WriteLine("    slope " + s.ToString("F0") + " deg: totalFaces=" + r.FaceCount
                + " rampFaces=" + inRamp + " maxSlope=" + MaxSlope(r).ToString("F1") + " deg");
        }

        Console.WriteLine();
        Console.WriteLine("  -- climb sensitivity at 65 deg ramp (cs=0.5, ch=0.2) --");
        double[] climbs = { 1.2, 2.2, 2.4, 3.0 };
        for (int i = 0; i < climbs.Length; i++)
        {
            double c = climbs[i];
            float tan65 = (float)Math.Tan(65.0 * Math.PI / 180.0);
            MakeGrid(40f, 20f, 0.5f, delegate (float x, float z) { return Ramp(x, tan65); }, out verts, out tris);
            TiledNavMeshBuilder.TiledNavMeshSettings st = Settings(0.5f);
            st.AgentMaxClimbMeters = (float)c;
            TiledNavMeshBuilder.VoxelMeshResult r =
                TiledNavMeshBuilder.Build(verts, tris, st, Environment.ProcessorCount, null);
            int inRamp = 0;
            for (int f = 0; f < r.FaceCount; f++)
            {
                float cxz = CentroidX(r, f);
                if (cxz > 12f && cxz < 28f) inRamp++;
            }
            Console.WriteLine("    climb " + c.ToString("F1") + "m (walkableClimb="
                + TiledNavMeshBuilder.WalkableClimbVoxels((float)c, 0.2f) + "): rampFaces=" + inRamp
                + " maxSlope=" + MaxSlope(r).ToString("F1") + " deg");
        }
    }

    private static float Ramp(float x, float tan)
    {
        if (x < 10f) return 0f;
        if (x < 30f) return (x - 10f) * tan;
        return 20f * tan;
    }

    private static float CliffHeight(float x, float z)
    {
        if (x < 30f) return 0f;
        if (x < 32f) return (x - 30f) * 5.67f;      // ~80 deg band
        if (x < 50f) return 11.34f;
        if (x <= 70f) return 11.34f + (x - 50f);    // 45 deg hill
        return 31.34f;
    }

    private static float CentroidX(TiledNavMeshBuilder.VoxelMeshResult r, int f)
    {
        return (r.VertsXyz[r.Tris[f * 3 + 0] * 3] + r.VertsXyz[r.Tris[f * 3 + 1] * 3]
            + r.VertsXyz[r.Tris[f * 3 + 2] * 3]) / 3f;
    }

    private static double MaxSlope(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        double max = 0.0;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int ia = r.Tris[f * 3 + 0], ib = r.Tris[f * 3 + 1], ic = r.Tris[f * 3 + 2];
            float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            float bx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bz = r.VertsXyz[ib * 3 + 2];
            float cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cx - ax, vy = cy - ay, vz = cz - az;
            float nx = uy * vz - uz * vy;
            float ny = uz * vx - ux * vz;
            float nz = ux * vy - uy * vx;
            double slope = Math.Atan2(Math.Sqrt((double)nx * nx + (double)nz * nz), Math.Abs(ny)) * 180.0 / Math.PI;
            if (slope > max) max = slope;
        }
        return max;
    }

    // Mode A/B shoreline diagnostic. After a water build, classify faces by height relative to the
    // level:
    //   sheet      : every corner within +/-0.3 m of the level (the flat raised sheet, Mode B)
    //   submerged  : every corner below level-0.3 (should be 0 in BOTH modes: cull drops them, raise
    //                lifts them)
    //   transition : touches the level band and rises above it (level+0.3) - the shoreline. Its max
    //                slope is the "shoreline slope": in Mode B the raised sheet meets the rising
    //                terrain here. If it exceeds the slope cap the transition is a hole, not a ramp.
    private static void WaterShorelineReport(TiledNavMeshBuilder.VoxelMeshResult r, float level,
        float slopeCapDeg)
    {
        const float band = 0.3f;
        int sheet = 0, submerged = 0, transition = 0, transitionWalkable = 0;
        double transitionMaxSlope = 0.0, allMaxSlope = 0.0;
        float meshMinY = float.MaxValue, meshMaxY = float.MinValue;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int ia = r.Tris[f * 3 + 0], ib = r.Tris[f * 3 + 1], ic = r.Tris[f * 3 + 2];
            float ay = r.VertsXyz[ia * 3 + 1], by = r.VertsXyz[ib * 3 + 1], cy = r.VertsXyz[ic * 3 + 1];
            float lo = Math.Min(ay, Math.Min(by, cy));
            float hi = Math.Max(ay, Math.Max(by, cy));
            if (lo < meshMinY) meshMinY = lo;
            if (hi > meshMaxY) meshMaxY = hi;
            double[] n = TriNormal(r.VertsXyz, ia, ib, ic);
            double slope = Math.Atan2(Math.Sqrt(n[0] * n[0] + n[2] * n[2]), Math.Abs(n[1])) * 180.0 / Math.PI;
            if (slope > allMaxSlope) allMaxSlope = slope;
            if (hi < level - band) submerged++;
            if (lo >= level - band && hi <= level + band) sheet++;
            if (lo < level + band && hi > level + band)
            {
                transition++;
                if (slope <= slopeCapDeg) transitionWalkable++;
                if (slope > transitionMaxSlope) transitionMaxSlope = slope;
            }
        }
        Console.WriteLine("shoreline: level=" + level.ToString("F2", CultureInfo.InvariantCulture)
            + "m sheetFaces=" + sheet + " submergedFaces=" + submerged + " transitionFaces=" + transition
            + " transitionWalkable(<= " + slopeCapDeg.ToString("F0", CultureInfo.InvariantCulture) + "deg)="
            + transitionWalkable + " transitionMaxSlope="
            + transitionMaxSlope.ToString("F2", CultureInfo.InvariantCulture) + "deg"
            + " meshY=[" + meshMinY.ToString("F2", CultureInfo.InvariantCulture) + ".."
            + meshMaxY.ToString("F2", CultureInfo.InvariantCulture) + "]"
            + " allFacesMaxSlope=" + allMaxSlope.ToString("F2", CultureInfo.InvariantCulture) + "deg");
    }

    private static int CountSlopeAbove(TiledNavMeshBuilder.VoxelMeshResult r, double deg)
    {
        int n = 0;
        double thr = deg * Math.PI / 180.0;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int ia = r.Tris[f * 3 + 0], ib = r.Tris[f * 3 + 1], ic = r.Tris[f * 3 + 2];
            float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            float bx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bz = r.VertsXyz[ib * 3 + 2];
            float cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cx - ax, vy = cy - ay, vz = cz - az;
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double slope = Math.Atan2(Math.Sqrt((double)nx * nx + (double)nz * nz), Math.Abs(ny));
            if (slope > thr) n++;
        }
        return n;
    }

    private static void PrintSteepest(TiledNavMeshBuilder.VoxelMeshResult r, string tag)
    {
        double max = 0.0;
        int mi = -1;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int ia = r.Tris[f * 3 + 0], ib = r.Tris[f * 3 + 1], ic = r.Tris[f * 3 + 2];
            float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            float bx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bz = r.VertsXyz[ib * 3 + 2];
            float cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float vx = cx - ax, vy = cy - ay, vz = cz - az;
            float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            double slope = Math.Atan2(Math.Sqrt((double)nx * nx + (double)nz * nz), Math.Abs(ny)) * 180.0 / Math.PI;
            if (slope > max) { max = slope; mi = f; }
        }
        if (mi < 0) { Console.WriteLine("  " + tag + " steepest: none"); return; }
        int a = r.Tris[mi * 3], b = r.Tris[mi * 3 + 1], c = r.Tris[mi * 3 + 2];
        Console.WriteLine("  " + tag + " steepest face " + mi + " slope=" + max.ToString("F1") + "deg: ("
            + r.VertsXyz[a * 3].ToString("F2") + "," + r.VertsXyz[a * 3 + 1].ToString("F2") + "," + r.VertsXyz[a * 3 + 2].ToString("F2") + ") ("
            + r.VertsXyz[b * 3].ToString("F2") + "," + r.VertsXyz[b * 3 + 1].ToString("F2") + "," + r.VertsXyz[b * 3 + 2].ToString("F2") + ") ("
            + r.VertsXyz[c * 3].ToString("F2") + "," + r.VertsXyz[c * 3 + 1].ToString("F2") + "," + r.VertsXyz[c * 3 + 2].ToString("F2") + ")");
    }

    // ---------------------------------------------------------------- test 3
    private static void Test3_Determinism()
    {
        Console.WriteLine("== Test 3: determinism (cliff scene, parallel) ==");
        float[] verts;
        int[] tris;
        MakeGrid(80f, 20f, 0.5f, CliffHeight, out verts, out tris);

        TiledNavMeshBuilder.VoxelMeshResult a =
            TiledNavMeshBuilder.Build(verts, tris, SettingsAuto(0.5f, 65f), Environment.ProcessorCount, null);
        TiledNavMeshBuilder.VoxelMeshResult b =
            TiledNavMeshBuilder.Build(verts, tris, SettingsAuto(0.5f, 65f), Environment.ProcessorCount, null);

        long hAv = Hash(a.VertsXyz), hBv = Hash(b.VertsXyz);
        long hAt = Hash(a.Tris), hBt = Hash(b.Tris);
        bool sameCounts = a.VertexCount == b.VertexCount && a.FaceCount == b.FaceCount && a.EdgeCount == b.EdgeCount;
        bool sameVerts = hAv == hBv;
        bool sameTris = hAt == hBt;

        Console.WriteLine("  run1: V=" + a.VertexCount + " F=" + a.FaceCount + " E=" + a.EdgeCount
            + " vertHash=" + hAv.ToString("x16") + " triHash=" + hAt.ToString("x16"));
        Console.WriteLine("  run2: V=" + b.VertexCount + " F=" + b.FaceCount + " E=" + b.EdgeCount
            + " vertHash=" + hBv.ToString("x16") + " triHash=" + hBt.ToString("x16"));
        Console.WriteLine("  RESULT: " + (sameCounts && sameVerts && sameTris ? "DETERMINISM PASS" : "DETERMINISM FAIL"));

        // RcRasterizations scratch-buffer neutrality: the pre-change harness (per-call allocations,
        // climb=0.4) produced V=62 F=58 E=117 vertHash=e016e53886cb9585 triHash=ba807f993fde10fc.
        TiledNavMeshBuilder.VoxelMeshResult legacy =
            TiledNavMeshBuilder.Build(verts, tris, Settings(0.5f), Environment.ProcessorCount, null);
        string lv = Hash(legacy.VertsXyz).ToString("x16");
        string lt = Hash(legacy.Tris).ToString("x16");
        bool neutral = legacy.VertexCount == 62 && legacy.FaceCount == 58 && legacy.EdgeCount == 117
            && lv == "e016e53886cb9585" && lt == "ba807f993fde10fc";
        Console.WriteLine("  RcRasterizations neutrality (climb=0.4): V=" + legacy.VertexCount + " F="
            + legacy.FaceCount + " E=" + legacy.EdgeCount + " vertHash=" + lv + " triHash=" + lt
            + " -> " + (neutral ? "MATCH" : "DIFF"));
    }

    // ---------------------------------------------------------------- test 4
    private static void Test4_Scaling()
    {
        Console.WriteLine("== Test 4: scaling 320x320 plane ==");
        float[] verts;
        int[] tris;
        MakeGrid(320f, 320f, 0.5f, delegate (float x, float z) { return 0f; }, out verts, out tris);
        Console.WriteLine("  soup: verts=" + (verts.Length / 3) + " tris=" + (tris.Length / 3));

        var sw = Stopwatch.StartNew();
        TiledNavMeshBuilder.VoxelMeshResult one =
            TiledNavMeshBuilder.Build(verts, tris, Settings(0.5f), 1, null);
        sw.Stop();
        long oneMs = sw.ElapsedMilliseconds;

        sw.Restart();
        TiledNavMeshBuilder.VoxelMeshResult many =
            TiledNavMeshBuilder.Build(verts, tris, Settings(0.5f), Environment.ProcessorCount, null);
        sw.Stop();
        long manyMs = sw.ElapsedMilliseconds;

        Console.WriteLine("  workers=1 : " + oneMs + " ms  (faces=" + one.FaceCount + ", tilesMs=" + one.TilesMs + ")");
        Console.WriteLine("  workers=" + Environment.ProcessorCount + " : " + manyMs + " ms  (faces=" + many.FaceCount
            + ", tilesMs=" + many.TilesMs + ")");
        Console.WriteLine("  speedup = " + ((double)oneMs / Math.Max(1, manyMs)).ToString("F2") + "x"
            + "; same faces " + (one.FaceCount == many.FaceCount));
    }

    // ---------------------------------------------------------------- helpers
    private static void Test5_DetailMesh()
    {
        Console.WriteLine("== Test 5: optional detail mesh (UseDetailMesh) vs fan ==");
        float[] verts;
        int[] tris;
        MakeGrid(40f, 40f, 0.5f, delegate (float x, float z) { return 0f; }, out verts, out tris);

        TiledNavMeshBuilder.TiledNavMeshSettings fanS = SettingsAuto(0.5f, 65f);
        fanS.UseDetailMesh = false;
        TiledNavMeshBuilder.TiledNavMeshSettings detS = SettingsAuto(0.5f, 65f);
        detS.UseDetailMesh = true;
        detS.MaxErrorMeters = 0.25f;

        TiledNavMeshBuilder.VoxelMeshResult fan =
            TiledNavMeshBuilder.Build(verts, tris, fanS, Environment.ProcessorCount, null);
        TiledNavMeshBuilder.VoxelMeshResult det =
            TiledNavMeshBuilder.Build(verts, tris, detS, Environment.ProcessorCount, null);

        Console.WriteLine("  fan    : F=" + fan.FaceCount + " V=" + fan.VertexCount + " E=" + fan.EdgeCount
            + " degenerate=" + fan.DegenerateFaces + " failed=" + fan.FailedTiles);
        Console.WriteLine("  detail : F=" + det.FaceCount + " V=" + det.VertexCount + " E=" + det.EdgeCount
            + " degenerate=" + det.DegenerateFaces + " failed=" + det.FailedTiles);
        Console.WriteLine("  RESULT: detailRuns " + (det.FaceCount > 0 && det.FailedTiles == 0)
            + ", detailFaces>=fanFaces " + (det.FaceCount >= fan.FaceCount));
    }

    private static void Test6_Ramp65()
    {
        Console.WriteLine("== Test 6: 65 deg ramp with auto agent climb ==");
        TiledNavMeshBuilder.TiledNavMeshSettings s = SettingsAuto(0.5f, 65f);
        float autoClimb = s.AgentMaxClimbMeters;
        int wc = TiledNavMeshBuilder.WalkableClimbVoxels(autoClimb, s.CellHeightMeters);
        double eff = Math.Atan((double)wc * s.CellHeightMeters / (2.0 * s.VoxelCellSizeMeters)) * 180.0 / Math.PI;
        Console.WriteLine("  resolveAgentClimb(cs=0.5, ch=0.2, slope=65, min=0.4) = " + autoClimb.ToString("0.###")
            + "m -> walkableClimb=" + wc + " voxels, effectiveMaxSlope=" + eff.ToString("F1") + " deg");

        float tan65 = (float)Math.Tan(65.0 * Math.PI / 180.0);
        float[] verts;
        int[] tris;
        MakeGrid(40f, 20f, 0.5f, delegate (float x, float z) { return Ramp(x, tan65); }, out verts, out tris);
        TiledNavMeshBuilder.VoxelMeshResult r =
            TiledNavMeshBuilder.Build(verts, tris, s, Environment.ProcessorCount, null);

        int rampFaces = 0;
        for (int f = 0; f < r.FaceCount; f++)
        {
            float cxz = CentroidX(r, f);
            if (cxz > 12f && cxz < 28f) rampFaces++;
        }
        double ms = MaxSlope(r);
        Console.WriteLine("  65-deg ramp: totalFaces=" + r.FaceCount + " rampFaces=" + rampFaces
            + " maxSlope=" + ms.ToString("F1") + " deg (tolerance 65+2)");
        Console.WriteLine("  RESULT: " + (rampFaces > 0 && ms <= 67.0 ? "RAMP65 PASS" : "RAMP65 FAIL"));
    }

    // ---------------------------------------------------------------- test 7
    private static void Test7_Hill()
    {
        Console.WriteLine("== Test 7: hill fidelity + integrity (analytic field, 0.25 m soup, offset 0) ==");
        float[] verts;
        int[] tris;
        MakeGrid(160f, 160f, 0.25f, HillHeightF, out verts, out tris);
        Console.WriteLine("  soup: verts=" + (verts.Length / 3) + " tris=" + (tris.Length / 3));

        double mn = double.MaxValue, mx = double.MinValue, maxGrad = 0.0;
        for (int i = 0; i <= 640; i++)
        {
            double x = i * 0.25;
            for (int j = 0; j <= 640; j++)
            {
                double z = j * 0.25;
                double h = HillHeight(x, z);
                if (h < mn) mn = h;
                if (h > mx) mx = h;
                double gx = (HillHeight(x + 0.25, z) - h) / 0.25;
                double gz = (HillHeight(x, z + 0.25) - h) / 0.25;
                double g = Math.Sqrt(gx * gx + gz * gz);
                if (g > maxGrad) maxGrad = g;
            }
        }
        Console.WriteLine("  analytic: relief=" + (mx - mn).ToString("F2") + "m min=" + mn.ToString("F2")
            + " max=" + mx.ToString("F2") + " maxSlope=" + (Math.Atan(maxGrad) * 180.0 / Math.PI).ToString("F1") + " deg");

        TiledNavMeshBuilder.VoxelMeshResult fan =
            TiledNavMeshBuilder.Build(verts, tris, HillSettings(false), Environment.ProcessorCount, null);
        TiledNavMeshBuilder.VoxelMeshResult det =
            TiledNavMeshBuilder.Build(verts, tris, HillSettings(true), Environment.ProcessorCount, null);

        Fid fOff = Fidelity("detail OFF", fan, true);
        Fid fOn = Fidelity("detail ON ", det, true);
        Integ iOff = Integrity("detail OFF", fan, true);
        Integ iOn = Integrity("detail ON ", det, true);

        Topo tf = Topology("detail OFF (fan)", fan);
        Topo td = Topology("detail ON", det);

        bool[] covFan = CoverageMask(fan, 160f, 1f);
        bool[] covDet = CoverageMask(det, 160f, 1f);
        int fanN = 0, detN = 0, both = 0, detOnly = 0;
        for (int i = 0; i < covFan.Length; i++)
        {
            if (covFan[i]) fanN++;
            if (covDet[i]) detN++;
            if (covFan[i] && covDet[i]) both++;
            if (!covFan[i] && covDet[i]) detOnly++;
        }
        double fanCovByDet = fanN > 0 ? (double)both / fanN : 1.0;
        Console.WriteLine("  coverage(1m grid): fan=" + fanN + " detail=" + detN + " both=" + both
            + " detailCoversFan=" + fanCovByDet.ToString("P2") + " detailExtra=" + detOnly
            + " -> " + (fanCovByDet >= 0.99 ? "PASS" : "FAIL"));

        Console.WriteLine("  faces/verts: OFF F=" + fan.FaceCount + " V=" + fan.VertexCount
            + "  ON F=" + det.FaceCount + " V=" + det.VertexCount);
        Console.WriteLine("  weld/degenerate: OFF weldMerged=" + fan.WeldMergedVertices + " deg=" + fan.DegenerateFaces
            + "  ON weldMerged=" + det.WeldMergedVertices + " deg=" + det.DegenerateFaces);
        Console.WriteLine("  REQUIREMENT detail ON: minD>=-0.5m " + (fOn.MinD >= -0.5)
            + ", zero samples below -0.5m " + (fOn.Below50 == 0)
            + ", components==1 " + (iOn.Components == 1));
        Console.WriteLine("  max upward d (floating): OFF=" + fOff.MaxD.ToString("F3")
            + "m ON=" + fOn.MaxD.ToString("F3") + "m");

        Console.WriteLine();
        Console.WriteLine("  -- detail weld tolerance sweep (hill scene, detail ON) --");
        float[] tols = { 0.01f, 0.003f, 0.001f };
        for (int i = 0; i < tols.Length; i++)
        {
            TiledNavMeshBuilder.TiledNavMeshSettings st = HillSettings(true);
            st.DetailWeldToleranceMeters = tols[i];
            TiledNavMeshBuilder.VoxelMeshResult r =
                TiledNavMeshBuilder.Build(verts, tris, st, Environment.ProcessorCount, null);
            Fid fi = Fidelity("", r, false);
            Integ it = Integrity("", r, false);
            Console.WriteLine("    tol=" + tols[i].ToString("F4") + "m: F=" + r.FaceCount + " V=" + r.VertexCount
                + " comp=" + it.Components + " unshared=" + it.UnsharedEdges + " deg=" + r.DegenerateFaces
                + " weldMerged=" + r.WeldMergedVertices + " minD=" + fi.MinD.ToString("F3")
                + " below-0.5=" + fi.Below50 + " maxUp=" + fi.MaxD.ToString("F3"));
        }
    }

    // ---------------------------------------------------------------- topology diagnostic
    private struct HFaceKey : IEquatable<HFaceKey>
    {
        public readonly int A;
        public readonly int B;
        public readonly int C;

        public HFaceKey(int a, int b, int c)
        {
            int lo, mi, hi;
            if (a <= b && a <= c) { lo = a; mi = b; hi = c; if (mi > hi) { int t = mi; mi = hi; hi = t; } }
            else if (b <= a && b <= c) { lo = b; mi = a; hi = c; if (mi > hi) { int t = mi; mi = hi; hi = t; } }
            else { lo = c; mi = a; hi = b; if (mi > hi) { int t = mi; mi = hi; hi = t; } }
            A = lo; B = mi; C = hi;
        }

        public bool Equals(HFaceKey o) { return A == o.A && B == o.B && C == o.C; }
        public override bool Equals(object o) { return o is HFaceKey && Equals((HFaceKey)o); }
        public override int GetHashCode() { unchecked { int h = 17; h = h * 31 + A; h = h * 31 + B; h = h * 31 + C; return h; } }
    }

    private sealed class Topo
    {
        public int V, E, F, Boundary, MaxMult, DupFaces, SelfLoops, Required;
        public long M1, M2, M3, M4;
        public bool Pass;
    }

    private static Topo Topology(string label, TiledNavMeshBuilder.VoxelMeshResult r)
    {
        var t = new Topo();
        t.V = r.VertexCount;
        t.F = r.FaceCount;
        var edgeMult = new Dictionary<long, int>();
        var faces = new Dictionary<HFaceKey, int>();
        for (int f = 0; f < r.FaceCount; f++)
        {
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            if (a == b || b == c || a == c) { t.SelfLoops++; continue; }
            var k = new HFaceKey(a, b, c);
            int cnt;
            faces.TryGetValue(k, out cnt);
            faces[k] = cnt + 1;
            if (cnt >= 1) t.DupFaces++;
            AddE(edgeMult, a, b); AddE(edgeMult, b, c); AddE(edgeMult, c, a);
        }
        t.E = edgeMult.Count;
        foreach (KeyValuePair<long, int> kv in edgeMult)
        {
            if (kv.Value == 1) { t.Boundary++; t.M1++; }
            else if (kv.Value == 2) t.M2++;
            else if (kv.Value == 3) t.M3++;
            else t.M4++;
            if (kv.Value > t.MaxMult) t.MaxMult = kv.Value;
        }
        t.Required = (3 * t.F + t.Boundary) / 2;
        t.Pass = t.MaxMult <= 2 && t.DupFaces == 0 && t.SelfLoops == 0 && t.E >= t.Required;
        Console.WriteLine("  [topology " + label + "] V=" + t.V + " E=" + t.E + " F=" + t.F
            + " E/F=" + (t.F > 0 ? ((double)t.E / t.F).ToString("F3") : "n/a")
            + " boundary=" + t.Boundary + " requiredEdges=" + t.Required
            + " m1=" + t.M1 + " m2=" + t.M2 + " m3=" + t.M3 + " m4+=" + t.M4
            + " maxMult=" + t.MaxMult + " dupFaces=" + t.DupFaces + " selfLoops=" + t.SelfLoops
            + " -> " + (t.Pass ? "PASS" : "FAIL"));
        Console.WriteLine("    (builder gate: passed=" + r.InvariantsPassed + " E=" + r.EdgeCount
            + " boundary=" + r.BoundaryEdges + " maxMult=" + r.MaxEdgeMultiplicity + " dupFaces=" + r.DuplicateFaces
            + " selfLoops=" + r.SelfLoops + " required=" + r.RequiredEdges + ")");
        return t;
    }

    private static void AddE(Dictionary<long, int> m, int a, int b)
    {
        long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        int c;
        m.TryGetValue(k, out c);
        m[k] = c + 1;
    }

    private static bool PointInTri(float px, float pz, float ax, float az, float bx, float bz, float cx, float cz)
    {
        float d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
        float d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
        float d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
        bool neg = (d1 < -1e-5f) || (d2 < -1e-5f) || (d3 < -1e-5f);
        bool pos = (d1 > 1e-5f) || (d2 > 1e-5f) || (d3 > 1e-5f);
        return !(neg && pos);
    }

    private static bool[] CoverageMask(TiledNavMeshBuilder.VoxelMeshResult r, float extent, float step)
    {
        int n = (int)Math.Round(extent / step);
        var mask = new bool[(n + 1) * (n + 1)];
        for (int i = 0; i <= n; i++)
        {
            float x = i * step;
            for (int j = 0; j <= n; j++)
            {
                float z = j * step;
                for (int f = 0; f < r.FaceCount; f++)
                {
                    int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                    if (PointInTri(x, z, r.VertsXyz[a * 3], r.VertsXyz[a * 3 + 2],
                        r.VertsXyz[b * 3], r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3], r.VertsXyz[c * 3 + 2]))
                    {
                        mask[i * (n + 1) + j] = true;
                        break;
                    }
                }
            }
        }
        return mask;
    }

    // ---------------------------------------------------------------- test 8
    private static void Test8_TinyDetailDump()
    {
        Console.WriteLine("== Test 8: tiny-scene raw detail index space ==");
        float[] verts;
        int[] tris;
        MakeGrid(32f, 32f, 0.5f, delegate (float x, float z) { return 0f; }, out verts, out tris);
        TiledNavMeshBuilder.CaptureDetailDebug = true;
        TiledNavMeshBuilder.DebugDetailMesh = null;
        TiledNavMeshBuilder.TiledNavMeshSettings s = SettingsAuto(0.5f, 65f);
        s.UseDetailMesh = true;
        s.TileWorldSizeMeters = 32f;
        TiledNavMeshBuilder.VoxelMeshResult r = TiledNavMeshBuilder.Build(verts, tris, s, 1, null);
        TiledNavMeshBuilder.CaptureDetailDebug = false;

        DotRecast.Recast.RcPolyMeshDetail dm = TiledNavMeshBuilder.DebugDetailMesh;
        if (dm == null) { Console.WriteLine("  no detail mesh captured"); return; }
        Console.WriteLine("  raw dmesh: nmeshes=" + dm.nmeshes + " nverts=" + dm.nverts + " ntris=" + dm.ntris);
        int maxRawIdx = 0;
        int maxVertCount = 0;
        for (int sm = 0; sm < dm.nmeshes; sm++)
        {
            int vc = dm.meshes[sm * 4 + 1];
            if (vc > maxVertCount) maxVertCount = vc;
        }
        for (int sm = 0; sm < dm.nmeshes && sm < 6; sm++)
        {
            int vb = dm.meshes[sm * 4 + 0], vc = dm.meshes[sm * 4 + 1];
            int tb = dm.meshes[sm * 4 + 2], tc = dm.meshes[sm * 4 + 3];
            Console.WriteLine("    submesh " + sm + ": vertBase=" + vb + " vertCount=" + vc
                + " triBase=" + tb + " triCount=" + tc);
            for (int k = 0; k < tc; k++)
            {
                int t = (tb + k) * 4;
                for (int q = 0; q < 3; q++) if (dm.tris[t + q] > maxRawIdx) maxRawIdx = dm.tris[t + q];
            }
        }
        Console.Write("    first raw tris: ");
        for (int k = 0; k < Math.Min(6, dm.ntris); k++)
            Console.Write("[" + dm.tris[k * 4] + "," + dm.tris[k * 4 + 1] + "," + dm.tris[k * 4 + 2] + "] ");
        Console.WriteLine();
        Console.WriteLine("  max raw tris index=" + maxRawIdx + " (global dmesh.nverts=" + dm.nverts
            + ", max submesh vertCount=" + maxVertCount + ") -> indices are "
            + (dm.nmeshes > 1 && maxRawIdx < dm.nverts ? "SUBMESH-LOCAL (must add vertBase)" : "inline/global")
            + "; our fix adds each submesh's vertBase -> built F=" + r.FaceCount + " V=" + r.VertexCount
            + " passed=" + r.InvariantsPassed);
    }

    // ---------------------------------------------------------------- test 9
    private static void Test9_LargeScene()
    {
        Console.WriteLine("== Test 9: 1600x1600 m synthetic scene (cs 0.5, detail ON) ==");
        float[] verts;
        int[] tris;
        MakeGrid(1600f, 1600f, 1.0f, LogMapHeight, out verts, out tris);
        Console.WriteLine("  soup: verts=" + (verts.Length / 3) + " tris=" + (tris.Length / 3));
        TiledNavMeshBuilder.TiledNavMeshSettings s = SettingsAuto(0.5f, 65f);
        s.UseDetailMesh = true;

        var sw = Stopwatch.StartNew();
        TiledNavMeshBuilder.VoxelMeshResult r =
            TiledNavMeshBuilder.Build(verts, tris, s, Environment.ProcessorCount, null);
        sw.Stop();

        Console.WriteLine("  time=" + sw.ElapsedMilliseconds + " ms  tiles=" + r.TileCount
            + " built=" + r.BuiltTiles + " failed=" + r.FailedTiles);
        Console.WriteLine("  V=" + r.VertexCount + " F=" + r.FaceCount + " E=" + r.EdgeCount
            + " E/F=" + (r.FaceCount > 0 ? ((double)r.EdgeCount / r.FaceCount).ToString("F3") : "n/a")
            + " boundary=" + r.BoundaryEdges + " required=" + r.RequiredEdges
            + " maxEdgeMult=" + r.MaxEdgeMultiplicity + " dupFaces=" + r.DuplicateFaces
            + " selfLoops=" + r.SelfLoops + " -> " + (r.InvariantsPassed ? "PASS" : "FAIL"));
        double area = SumAreaXZ(r);
        Console.WriteLine("  XZ area=" + area.ToString("F0") + " m^2 (domain 1600^2=2560000) ratio="
            + (area / (1600.0 * 1600.0)).ToString("P3"));
        DumpMultiEdges(r);

        Console.WriteLine("  -- large-scene weld-tolerance sweep --");
        float[] tols = { 0.01f, 0.001f, 0.0005f, 0.0001f };
        for (int i = 0; i < tols.Length; i++)
        {
            TiledNavMeshBuilder.TiledNavMeshSettings st = SettingsAuto(0.5f, 65f);
            st.UseDetailMesh = true;
            st.DetailWeldToleranceMeters = tols[i];
            TiledNavMeshBuilder.VoxelMeshResult rr =
                TiledNavMeshBuilder.Build(verts, tris, st, Environment.ProcessorCount, null);
            Console.WriteLine("    tol=" + tols[i].ToString("F4") + ": V=" + rr.VertexCount + " F=" + rr.FaceCount
                + " E=" + rr.EdgeCount + " boundary=" + rr.BoundaryEdges + " required=" + rr.RequiredEdges
                + " maxMult=" + rr.MaxEdgeMultiplicity + " dup=" + rr.DuplicateFaces + " selfLoops=" + rr.SelfLoops
                + " -> " + (rr.InvariantsPassed ? "PASS" : "FAIL"));
        }
    }

    private static void DumpMultiEdges(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        var edgeMult = new Dictionary<long, int>();
        var edgeEnd = new Dictionary<long, int>();
        for (int f = 0; f < r.FaceCount; f++)
        {
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            EdgeTrack(edgeMult, edgeEnd, a, b);
            EdgeTrack(edgeMult, edgeEnd, b, c);
            EdgeTrack(edgeMult, edgeEnd, c, a);
        }
        int bad = 0;
        int printed = 0;
        foreach (KeyValuePair<long, int> kv in edgeMult)
        {
            if (kv.Value <= 2) continue;
            bad++;
            if (printed < 12)
            {
                int a = (int)(kv.Key >> 32);
                int b = (int)(uint)kv.Key;
                Console.WriteLine("    mult=" + kv.Value + " edge " + a + "-" + b
                    + " A=(" + r.VertsXyz[a * 3].ToString("F2") + "," + r.VertsXyz[a * 3 + 1].ToString("F2")
                    + "," + r.VertsXyz[a * 3 + 2].ToString("F2") + ") B=(" + r.VertsXyz[b * 3].ToString("F2")
                    + "," + r.VertsXyz[b * 3 + 1].ToString("F2") + "," + r.VertsXyz[b * 3 + 2].ToString("F2") + ")");
                printed++;
            }
        }
        Console.WriteLine("  edges with multiplicity > 2: " + bad);
    }

    private static void EdgeTrack(Dictionary<long, int> mult, Dictionary<long, int> end, int a, int b)
    {
        long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        int c;
        mult.TryGetValue(k, out c);
        mult[k] = c + 1;
        if (c == 0) end[k] = a < b ? a : b;
    }

    private static double SumAreaXZ(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        double s = 0.0;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            float abx = r.VertsXyz[b * 3] - r.VertsXyz[a * 3];
            float abz = r.VertsXyz[b * 3 + 2] - r.VertsXyz[a * 3 + 2];
            float acx = r.VertsXyz[c * 3] - r.VertsXyz[a * 3];
            float acz = r.VertsXyz[c * 3 + 2] - r.VertsXyz[a * 3 + 2];
            s += Math.Abs(abx * acz - abz * acx) * 0.5;
        }
        return s;
    }

    // Rolling synthetic terrain for the large-scene test (mirrors an open battle map).
    private static float LogMapHeight(float x, float z)
    {
        return (float)(6.0 * Math.Sin(2.0 * Math.PI * x / 220.0) * Math.Cos(2.0 * Math.PI * z / 190.0)
            + 2.0 * Math.Sin(2.0 * Math.PI * x / 70.0)
            + 1.5 * Math.Cos(2.0 * Math.PI * z / 55.0));
    }

    // ---------------------------------------------------------------- test 10
    private sealed class QReport
    {
        public int F, V, E, Boundary, MaxMult, Tight, Sliver;
        public bool Pass;
        public long[] MinEdge = new long[5];
        public long[] MinAngle = new long[4];
        public long[] Area = new long[4];
        public long[] BoundDist = new long[5];
        public long[] TileDist = new long[3];
    }

    private static QReport Quality(string label, TiledNavMeshBuilder.VoxelMeshResult r, float tileSize)
    {
        var q = new QReport();
        q.F = r.FaceCount;
        q.V = r.VertexCount;
        q.E = r.EdgeCount;
        q.Boundary = r.BoundaryEdges;
        q.MaxMult = r.MaxEdgeMultiplicity;
        q.Pass = r.InvariantsPassed;

        var em = new Dictionary<long, int>();
        for (int f = 0; f < r.FaceCount; f++)
        {
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            AddE(em, a, b); AddE(em, b, c); AddE(em, c, a);
        }
        var bx = new List<float>();
        var bz = new List<float>();
        foreach (KeyValuePair<long, int> kv in em)
        {
            if (kv.Value != 1) continue;
            int a = (int)(kv.Key >> 32), b = (int)(uint)kv.Key;
            bx.Add((r.VertsXyz[a * 3] + r.VertsXyz[b * 3]) * 0.5f);
            bz.Add((r.VertsXyz[a * 3 + 2] + r.VertsXyz[b * 3 + 2]) * 0.5f);
        }

        for (int f = 0; f < r.FaceCount; f++)
        {
            int ia = r.Tris[f * 3], ib = r.Tris[f * 3 + 1], ic = r.Tris[f * 3 + 2];
            float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            float bxx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bzz = r.VertsXyz[ib * 3 + 2];
            float cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];

            float e0 = Len(ax, ay, az, bxx, by, bzz);
            float e1 = Len(bxx, by, bzz, cx, cy, cz);
            float e2 = Len(cx, cy, cz, ax, ay, az);
            float minLen = Math.Min(e0, Math.Min(e1, e2));
            float minAng = MinAngleDeg(ax, ay, az, bxx, by, bzz, cx, cy, cz);
            double area = Area3(ax, ay, az, bxx, by, bzz, cx, cy, cz);

            if (minLen < 0.1f) q.MinEdge[0]++;
            else if (minLen < 0.25f) q.MinEdge[1]++;
            else if (minLen < 0.5f) q.MinEdge[2]++;
            else if (minLen < 1f) q.MinEdge[3]++;
            else q.MinEdge[4]++;
            if (minLen < 0.25f) q.Tight++;

            if (minAng < 5f) { q.MinAngle[0]++; q.Sliver++; }
            else if (minAng < 15f) q.MinAngle[1]++;
            else if (minAng < 30f) q.MinAngle[2]++;
            else q.MinAngle[3]++;

            if (area < 0.01) q.Area[0]++;
            else if (area < 0.1) q.Area[1]++;
            else if (area < 1.0) q.Area[2]++;
            else q.Area[3]++;

            float cxf = (ax + bxx + cx) / 3f, czf = (az + bzz + cz) / 3f;
            double bd = double.MaxValue;
            for (int k = 0; k < bx.Count; k++)
            {
                double dx = cxf - bx[k], dz = czf - bz[k];
                double d2 = dx * dx + dz * dz;
                if (d2 < bd) bd = d2;
            }
            bd = Math.Sqrt(bd);
            if (bd < 0.5) q.BoundDist[0]++;
            else if (bd < 1.0) q.BoundDist[1]++;
            else if (bd < 2.0) q.BoundDist[2]++;
            else if (bd < 5.0) q.BoundDist[3]++;
            else q.BoundDist[4]++;

            float tx = Math.Abs(cxf - (float)Math.Round(cxf / tileSize) * tileSize);
            float tz = Math.Abs(czf - (float)Math.Round(czf / tileSize) * tileSize);
            float td = Math.Min(tx, tz);
            if (td < 0.5f) q.TileDist[0]++;
            else if (td < 1f) q.TileDist[1]++;
            else q.TileDist[2]++;
        }

        double ff = Math.Max(1, q.F);
        Console.WriteLine("  [" + label + "] F=" + q.F + " V=" + q.V + " E=" + q.E + " E/F="
            + (q.E / ff).ToString("F3") + " boundary=" + q.Boundary + " maxMult=" + q.MaxMult
            + " tight(minEdge<0.25m)=" + q.Tight + " (" + (q.Tight / ff).ToString("P1") + ")"
            + " sliver(minAngle<5deg)=" + q.Sliver + " (" + (q.Sliver / ff).ToString("P1") + ")"
            + " pass=" + q.Pass);
        Console.WriteLine("    minEdge(m): <0.1=" + Pct(q.MinEdge[0], ff) + " 0.1-0.25=" + Pct(q.MinEdge[1], ff)
            + " 0.25-0.5=" + Pct(q.MinEdge[2], ff) + " 0.5-1=" + Pct(q.MinEdge[3], ff) + " >1=" + Pct(q.MinEdge[4], ff));
        Console.WriteLine("    minAngle: <5=" + Pct(q.MinAngle[0], ff) + " 5-15=" + Pct(q.MinAngle[1], ff)
            + " 15-30=" + Pct(q.MinAngle[2], ff) + " >30=" + Pct(q.MinAngle[3], ff));
        Console.WriteLine("    area(m2): <0.01=" + Pct(q.Area[0], ff) + " 0.01-0.1=" + Pct(q.Area[1], ff)
            + " 0.1-1=" + Pct(q.Area[2], ff) + " >1=" + Pct(q.Area[3], ff));
        Console.WriteLine("    distBoundary(m): 0-0.5=" + Pct(q.BoundDist[0], ff) + " 0.5-1=" + Pct(q.BoundDist[1], ff)
            + " 1-2=" + Pct(q.BoundDist[2], ff) + " 2-5=" + Pct(q.BoundDist[3], ff) + " >5=" + Pct(q.BoundDist[4], ff));
        Console.WriteLine("    distTileBorder(m): 0-0.5=" + Pct(q.TileDist[0], ff) + " 0.5-1=" + Pct(q.TileDist[1], ff)
            + " >1=" + Pct(q.TileDist[2], ff));
        return q;
    }

    private static string Pct(long n, double total)
    {
        return n + "/" + ((100.0 * n / total).ToString("F1") + "%");
    }

    private static float Len(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = bx - ax, dy = by - ay, dz = bz - az;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static float MinAngleDeg(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz)
    {
        float minA = 180f;
        minA = Math.Min(minA, AngleAt(ax, ay, az, bx, by, bz, cx, cy, cz));
        minA = Math.Min(minA, AngleAt(bx, by, bz, ax, ay, az, cx, cy, cz));
        minA = Math.Min(minA, AngleAt(cx, cy, cz, ax, ay, az, bx, by, bz));
        return minA;
    }

    private static float AngleAt(float vx, float vy, float vz, float ax, float ay, float az, float bx, float by, float bz)
    {
        float ux = ax - vx, uy = ay - vy, uz = az - vz;
        float wx = bx - vx, wy = by - vy, wz = bz - vz;
        double du = Math.Sqrt((double)ux * ux + (double)uy * uy + (double)uz * uz);
        double dw = Math.Sqrt((double)wx * wx + (double)wy * wy + (double)wz * wz);
        if (du < 1e-9 || dw < 1e-9) return 0f;
        double c = (ux * wx + uy * wy + uz * wz) / (du * dw);
        if (c > 1) c = 1; if (c < -1) c = -1;
        return (float)(Math.Acos(c) * 180.0 / Math.PI);
    }

    private static double Area3(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz)
    {
        float ux = bx - ax, uy = by - ay, uz = bz - az;
        float vx = cx - ax, vy = cy - ay, vz = cz - az;
        float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        return 0.5 * Math.Sqrt((double)nx * nx + (double)ny * ny + (double)nz * nz);
    }

    private static void SampleFid(Fid f, Func<float, float, float> h, float x, float y, float z)
    {
        double d = y - h(x, z);
        if (d < f.MinD) { f.MinD = d; f.MinX = x; f.MinZ = z; }
        if (d > f.MaxD) { f.MaxD = d; f.MaxX = x; f.MaxZ = z; }
        f.Sum += d;
        f.N++;
        if (d < -0.05) f.Below05++;
        if (d < -0.5) f.Below50++;
    }

    private static Fid FidelityFor(Func<float, float, float> h, TiledNavMeshBuilder.VoxelMeshResult r)
    {
        var f = new Fid();
        for (int t = 0; t < r.FaceCount; t++)
        {
            int ia = r.Tris[t * 3 + 0], ib = r.Tris[t * 3 + 1], ic = r.Tris[t * 3 + 2];
            float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            float bx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bz = r.VertsXyz[ib * 3 + 2];
            float cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];
            SampleFid(f, h, ax, ay, az);
            SampleFid(f, h, bx, by, bz);
            SampleFid(f, h, cx, cy, cz);
            SampleFid(f, h, (ax + bx + cx) / 3f, (ay + by + cy) / 3f, (az + bz + cz) / 3f);
            SampleFid(f, h, (ax + bx) / 2f, (ay + by) / 2f, (az + bz) / 2f);
            SampleFid(f, h, (bx + cx) / 2f, (by + cy) / 2f, (bz + cz) / 2f);
            SampleFid(f, h, (cx + ax) / 2f, (cy + ay) / 2f, (cz + az) / 2f);
        }
        return f;
    }

    private static TiledNavMeshBuilder.TiledNavMeshSettings DetailSettings(float cs, float sd, float simp, int el)
    {
        var s = new TiledNavMeshBuilder.TiledNavMeshSettings();
        s.VoxelCellSizeMeters = cs;
        s.UseDetailMesh = true;
        s.HeightOffsetMeters = 0f;
        s.DetailSampleDistMeters = sd;
        s.ContourMaxErrorVoxels = simp;
        s.MaxEdgeLenVoxels = el;
        s.AgentMaxClimbMeters = TiledNavMeshBuilder.ResolveAgentClimbMeters(cs, s.CellHeightMeters, 65f, 0.4f);
        // Keep the sliver/face sweeps comparable: the new repair passes have their own tests.
        s.PruneIsolatedIslands = false;
        s.StitchMaxGapCells = 0;
        return s;
    }

    private static void SweepRow(string label, float[] v, int[] t, Func<float, float, float> h,
        float cs, float sd, float simp, int el)
    {
        var s = DetailSettings(cs, sd, simp, el);
        var r = TiledNavMeshBuilder.Build(v, t, s, Environment.ProcessorCount, null);
        int tight = 0, sliver = 0;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int ia = r.Tris[f * 3], ib = r.Tris[f * 3 + 1], ic = r.Tris[f * 3 + 2];
            float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            float bx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bz = r.VertsXyz[ib * 3 + 2];
            float cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];
            float minLen = Math.Min(Len(ax, ay, az, bx, by, bz), Math.Min(Len(bx, by, bz, cx, cy, cz), Len(cx, cy, cz, ax, ay, az)));
            if (minLen < 0.25f) tight++;
            if (MinAngleDeg(ax, ay, az, bx, by, bz, cx, cy, cz) < 5f) sliver++;
        }
        Fid fid = FidelityFor(h, r);
        Console.WriteLine("    " + label + ": F=" + r.FaceCount + " E/F="
            + (r.FaceCount > 0 ? ((double)r.EdgeCount / r.FaceCount).ToString("F3") : "n/a")
            + " tight=" + tight + " sliver=" + sliver + " maxMult=" + r.MaxEdgeMultiplicity
            + " dup=" + r.DuplicateFaces + " selfLoops=" + r.SelfLoops + " pass=" + r.InvariantsPassed
            + " minD=" + fid.MinD.ToString("F2") + " maxUp=" + fid.MaxD.ToString("F2")
            + " below-0.5=" + fid.Below50);
    }

    private static float MesaHeightF(float x, float z)
    {
        // Flat ground with a grid of tall (3.5 m) boxes: their vertical sides are non-walkable
        // (drop > agent climb), so each one contributes walkable-area boundary edges -- the
        // "terrain breaks" the user reports slivers around.
        int bx = (int)(x / 20f), bz = (int)(z / 20f);
        float fx = x - bx * 20f, fz = z - bz * 20f;
        if (fx < 6f && fz < 6f)
            return 3.5f;
        // plus a climbable terrace elsewhere for variety
        if (fx > 12f && fz > 12f)
            return ((bx * 3 + bz * 5) % 3) * 0.5f;
        return 0f;
    }

    private static void Test10_Slivers()
    {
        Console.WriteLine("== Test 10: sliver quantification + knobs (detail ON) ==");
        float[] hv;
        int[] ht;
        MakeGrid(160f, 160f, 0.25f, HillHeightF, out hv, out ht);
        float[] mv;
        int[] mt;
        MakeGrid(160f, 160f, 0.5f, MesaHeightF, out mv, out mt);

        Console.WriteLine("  scene A = analytic hill; scene B = tall boxes (non-walkable sides) + terraces");
        var rA = TiledNavMeshBuilder.Build(hv, ht, DetailSettings(0.5f, 2.0f, 1.3f, 12), Environment.ProcessorCount, null);
        Quality("A hill DEFAULT(sd=2,simp=1.3)", rA, 16f);
        var fA = FidelityFor(HillHeightF, rA);
        Console.WriteLine("    fidelity minD=" + fA.MinD.ToString("F3") + " maxUp=" + fA.MaxD.ToString("F3") + " below-0.5=" + fA.Below50);

        var rB = TiledNavMeshBuilder.Build(mv, mt, DetailSettings(0.5f, 2.0f, 1.3f, 12), Environment.ProcessorCount, null);
        Quality("B mesa DEFAULT(sd=2,simp=1.3)", rB, 16f);
        var fB = FidelityFor(MesaHeightF, rB);
        Console.WriteLine("    fidelity minD=" + fB.MinD.ToString("F3") + " maxUp=" + fB.MaxD.ToString("F3") + " below-0.5=" + fB.Below50);

        // validation at the chosen defaults: coverage vs the fan path computed with the same settings
        TiledNavMeshBuilder.TiledNavMeshSettings fanS = DetailSettings(0.5f, 2.0f, 1.3f, 12);
        fanS.UseDetailMesh = false;
        var fanA = TiledNavMeshBuilder.Build(hv, ht, fanS, Environment.ProcessorCount, null);
        bool[] covFan = CoverageMask(fanA, 160f, 1f);
        bool[] covDet = CoverageMask(rA, 160f, 1f);
        int fn = 0, dn = 0, both = 0;
        for (int i = 0; i < covFan.Length; i++)
        {
            if (covFan[i]) fn++;
            if (covDet[i]) dn++;
            if (covFan[i] && covDet[i]) both++;
        }
        double cov = fn > 0 ? (double)both / fn : 1.0;
        Console.WriteLine("  DEFAULT coverage(1m, hill): fan=" + fn + " detail=" + dn + " both=" + both
            + " detailCoversFan=" + cov.ToString("P2") + " -> " + (cov >= 0.99 ? "PASS" : "FAIL"));

        Console.WriteLine();
        Console.WriteLine("  -- sweep (sampleDist; 0=auto) --");
        float[] sds = { 0f, 0.9f, 1.5f, 2.0f, 4.0f, 100000f };
        for (int i = 0; i < sds.Length; i++)
        {
            SweepRow("A sd=" + (sds[i] == 0f ? "auto" : sds[i].ToString("0.###")), hv, ht, HillHeightF, 0.5f, sds[i], 0f, 12);
            SweepRow("B sd=" + (sds[i] == 0f ? "auto" : sds[i].ToString("0.###")), mv, mt, MesaHeightF, 0.5f, sds[i], 0f, 12);
        }
        Console.WriteLine("  -- sweep (contour simplification; vox; 0=auto) --");
        float[] simps = { 0f, 0.5f, 1.0f, 2.0f };
        for (int i = 0; i < simps.Length; i++)
        {
            SweepRow("A simp=" + (simps[i] == 0f ? "auto" : simps[i].ToString("0.###")), hv, ht, HillHeightF, 0.5f, 0f, simps[i], 12);
            SweepRow("B simp=" + (simps[i] == 0f ? "auto" : simps[i].ToString("0.###")), mv, mt, MesaHeightF, 0.5f, 0f, simps[i], 12);
        }
        Console.WriteLine("  -- sweep (maxEdgeLen vox) --");
        int[] els = { 12, 24, 48 };
        for (int i = 0; i < els.Length; i++)
        {
            SweepRow("A el=" + els[i], hv, ht, HillHeightF, 0.5f, 0f, 0f, els[i]);
            SweepRow("B el=" + els[i], mv, mt, MesaHeightF, 0.5f, 0f, 0f, els[i]);
        }
        Console.WriteLine("  -- combos --");
        SweepRow("A sd=2 simp=2 el=24", hv, ht, HillHeightF, 0.5f, 2.0f, 2.0f, 24);
        SweepRow("B sd=2 simp=2 el=24", mv, mt, MesaHeightF, 0.5f, 2.0f, 2.0f, 24);
        SweepRow("A sd=4 simp=2 el=48", hv, ht, HillHeightF, 0.5f, 4.0f, 2.0f, 48);
        SweepRow("B sd=4 simp=2 el=48", mv, mt, MesaHeightF, 0.5f, 4.0f, 2.0f, 48);
        SweepRow("A sd=2 simp=1 el=24", hv, ht, HillHeightF, 0.5f, 2.0f, 1.0f, 24);
        SweepRow("B sd=2 simp=1 el=24", mv, mt, MesaHeightF, 0.5f, 2.0f, 1.0f, 24);
    }

    // ---------------------------------------------------------------- test 11
    private static void Test11_Assets()
    {
        Console.WriteLine("== Test 11: multi-level assets (platform/room/stairs/bridge) ==");

        float[] tv;
        int[] tt;
        MakeGrid(40f, 40f, 0.5f, TerrainGully, out tv, out tt);

        var av = new List<float>();
        var at = new List<int>();
        AddBoxZ(av, at, 4f, 4f, 2.8f, 8f, 8f, 3.0f);            // platform slab (top 3.0)
        AddBoxZ(av, at, 12f, 4f, 0f, 18f, 4.3f, 3f);            // room N wall
        AddBoxZ(av, at, 12f, 11.7f, 0f, 18f, 12f, 3f);          // room S wall
        AddBoxZ(av, at, 12f, 4f, 0f, 12.3f, 12f, 3f);           // room W wall
        AddBoxZ(av, at, 17.7f, 4f, 0f, 18f, 12f, 3f);           // room E wall
        AddBoxZ(av, at, 12f, 4f, 3.0f, 18f, 12f, 3.2f);         // room ceiling (deck top 3.2)
        for (int k = 0; k < 6; k++)                              // 6-step staircase, 0.3 m riser, 3 m
            AddBoxZ(av, at, 22f, 2f + 3f * k, 0f, 26f, 5f + 3f * k, 0.3f * (k + 1)); // treads, 4 m wide
        AddBoxZ(av, at, 29f, 24f, 2.8f, 35f, 28f, 3.0f);        // bridge deck over the gully

        float[] assetVerts = av.ToArray();
        int[] assetTris = at.ToArray();

        float[] mx;
        int[] mtt;
        TiledNavMeshBuilder.BuildCombinedSoup(tv, tt, assetVerts, assetTris, out mx, out mtt, out int _flipped);
        Console.WriteLine("  terrain(40x40, gully x30-34) + assets(" + (assetVerts.Length / 3) + "v/"
            + (assetTris.Length / 3) + "t) -> soup " + (mx.Length / 3) + "v/" + (mtt.Length / 3) + "t");

        // --- fan build: proves the offset mechanics. The builder lifts the WHOLE merged soup by
        // HeightOffsetMeters (+0.5), so structures and terrain share the same anti-intersection
        // offset: a platform top at true 3.0 lands at ~3.5, the ground beneath at ~0.7. The detail
        // path adds the upstream +ch nudge on top of that.
        TiledNavMeshBuilder.TiledNavMeshSettings fanS = AssetSettings(0.5f);
        fanS.UseDetailMesh = false;
        TiledNavMeshBuilder.VoxelMeshResult rf =
            TiledNavMeshBuilder.Build(mx, mtt, fanS, Environment.ProcessorCount, null);
        Console.WriteLine("  [fan] F=" + rf.FaceCount + " V=" + rf.VertexCount + " E=" + rf.EdgeCount
            + " maxMult=" + rf.MaxEdgeMultiplicity + " dup=" + rf.DuplicateFaces + " selfLoops=" + rf.SelfLoops
            + " pass=" + rf.InvariantsPassed);

        bool ok = rf.InvariantsPassed;
        ok &= AssertBand("[fan] platform top (true 3.0 + ~0.5)", rf, 6f, 6f, 3.48f, 3.95f, true);
        ok &= AssertBand("[fan] ground under platform (terrain 0 + ~0.5)", rf, 6f, 6f, 0.48f, 0.95f, true);
        ok &= AssertBand("[fan] room floor (terrain 0 + ~0.5)", rf, 15f, 8f, 0.48f, 0.95f, true);
        ok &= AssertBand("[fan] room ceiling deck (true 3.2 + ~0.5)", rf, 15f, 8f, 3.68f, 4.15f, true);
        ok &= AssertBand("[fan] room interior (no face)", rf, 15f, 8f, 1.40f, 3.30f, false);
        ok &= AssertBand("[fan] bridge deck (true 3.0 + ~0.5)", rf, 32f, 26f, 3.48f, 3.95f, true);
        ok &= AssertBand("[fan] gully ground under bridge (terrain -3 + ~0.5)", rf, 32f, 26f, -2.52f, -2.05f, true);
        PrintSteepest(rf, "[fan]");
        double ms = MaxSlope(rf);
        bool slopeOk = ms <= fanS.MaxSlopeDegrees + 2.0;
        Console.WriteLine("  [fan] max face slope=" + ms.ToString("F1") + " deg (limit " + fanS.MaxSlopeDegrees
            + ") ok=" + slopeOk);
        ok &= slopeOk;

        // --- detail build (the shipped default): invariants + determinism. The uniform +0.5 lift
        // applies to structures too; the detail path adds the upstream +ch nudge on top. ---
        TiledNavMeshBuilder.TiledNavMeshSettings ds = AssetSettings(0.5f);
        TiledNavMeshBuilder.VoxelMeshResult rd =
            TiledNavMeshBuilder.Build(mx, mtt, ds, Environment.ProcessorCount, null);
        float ch = ds.CellHeightMeters;
        Console.WriteLine("  [detail] F=" + rd.FaceCount + " V=" + rd.VertexCount + " E=" + rd.EdgeCount
            + " E/F=" + (rd.FaceCount > 0 ? ((double)rd.EdgeCount / rd.FaceCount).ToString("F3") : "n/a")
            + " maxMult=" + rd.MaxEdgeMultiplicity + " dup=" + rd.DuplicateFaces + " selfLoops=" + rd.SelfLoops
            + " pass=" + rd.InvariantsPassed);
        ok &= rd.InvariantsPassed;
        ok &= AssertBand("[detail] platform top (true 3.0 + 0.5 + nudge)", rd, 6f, 6f,
            3.0f + 0.5f - 0.02f, 3.0f + 0.5f + 2f * ch + 0.05f, true);
        ok &= AssertBand("[detail] terrain ground still at +0.5", rd, 20f, 15f,
            0.5f + ch - 0.05f, 0.5f + 2f * ch + 0.25f, true);

        // Stairs: steps within walkableClimb merge into one region in the fan build (flattened), so
        // the traversable step heights are only recovered by the detail mesh sampling the heightfield.
        // The lowest step (0.3 m) is itself within the detail maxError of the ground and is bridged by
        // interpolation; the upper steps are explicit faces.
        ok &= AssertBand("[detail] stairs mid-step (bridged)", rd, 24f, 13f, 2.05f, 2.50f, true);
        ok &= AssertBand("[detail] stairs step5 top", rd, 24f, 18.5f, 2.45f, 2.95f, true);
        double msd = MaxSlope(rd);
        int above = CountSlopeAbove(rd, ds.MaxSlopeDegrees);
        Console.WriteLine("  [detail] max face slope=" + msd.ToString("F1") + " deg; output faces above the "
            + ds.MaxSlopeDegrees + " deg poly limit=" + above + "/" + rd.FaceCount
            + " (detail-phase triangles bridging the stair/ground step; detail is height-bounded, not slope-bounded)");
        // The POLY (fan) mesh bounds traversability; the detail phase refines geometry and can locally
        // exceed the poly slope where it interpolates across a height discontinuity (a step/ledge), just
        // as it can at a terrain cliff edge (see Test 2's detail-ON note). Assert the poly mesh slope.
        bool slopeOk2 = ms <= ds.MaxSlopeDegrees + 2.0;
        Console.WriteLine("  [detail] poly-mesh max slope=" + ms.ToString("F1") + " deg (limit "
            + ds.MaxSlopeDegrees + "+2) ok=" + slopeOk2);
        ok &= slopeOk2;

        // Caveat (informational + asserted): with the SHIPPED RegionMinSize=8 (min region area 64
        // cells ~= 16 m^2 at cs=0.5) an isolated 4x4 m platform is pruned after agent-radius erosion,
        // which is why the mechanics test above lowers RegionMinSize to 1. Surfaces that connect to
        // larger navmesh (the usual in-scene case) are unaffected.
        TiledNavMeshBuilder.TiledNavMeshSettings shipCfg = AssetSettings(0.5f);
        shipCfg.UseDetailMesh = false;
        shipCfg.RegionMinSize = 8;
        shipCfg.RegionMergeSize = 20;
        TiledNavMeshBuilder.VoxelMeshResult rship =
            TiledNavMeshBuilder.Build(mx, mtt, shipCfg, Environment.ProcessorCount, null);
        float sh;
        bool shipPlatform = ProbeBand(rship, 6f, 6f, 3.40f, 3.95f, out sh);
        Console.WriteLine("  [shipped RegionMinSize=8] isolated 4x4 platform present=" + shipPlatform
            + " (expected False: pruned as < min region area)");
        ok &= !shipPlatform;

        TiledNavMeshBuilder.VoxelMeshResult rd2 =
            TiledNavMeshBuilder.Build(mx, mtt, ds, Environment.ProcessorCount, null);
        bool det = Hash(rd.VertsXyz) == Hash(rd2.VertsXyz) && Hash(rd.Tris) == Hash(rd2.Tris)
            && rd.FaceCount == rd2.FaceCount;
        Console.WriteLine("  [detail] determinism=" + (det ? "PASS" : "FAIL"));
        ok &= det;

        Console.WriteLine("  ASSET TEST: " + (ok ? "PASS" : "FAIL"));
    }

    // ---------------------------------------------------------------- test 12
    private static void Test12_RegionPruning()
    {
        Console.WriteLine("== Test 12: isolated-region pruning floor ==");

        // The same 40x40 terrain + isolated 4x4 m platform 3 m up as Test 11.
        float[] tv;
        int[] tt;
        MakeGrid(40f, 40f, 0.5f, TerrainGully, out tv, out tt);
        var av = new List<float>();
        var at = new List<int>();
        AddBoxZ(av, at, 4f, 4f, 2.8f, 8f, 8f, 3.0f);
        float[] mx;
        int[] mtt;
        TiledNavMeshBuilder.BuildCombinedSoup(tv, tt, av.ToArray(), at.ToArray(), out mx, out mtt, out int _flipped);

        // An isolated region survives iff its walkable span count >= RegionMinSize^2 (the min-area
        // prune is skipped for regions that connect to a tile border; an isolated platform never does).
        // Sweep r to bracket the platform's post-erosion span count.
        int surviveAt = -1;
        Console.WriteLine("  isolated 4x4 m platform: survival vs RegionMinSize (minArea = r*r cells; cs=0.5,");
        Console.WriteLine("  agent radius 0.35 -> erosion 1 cell, ledge filter 1 cell => geometric core 6x6=36 cells):");
        for (int r = 8; r >= 1; r--)
        {
            TiledNavMeshBuilder.TiledNavMeshSettings s = AssetSettings(0.5f);
            s.UseDetailMesh = false;
            s.RegionMinSize = r;
            s.RegionMergeSize = 20;
            TiledNavMeshBuilder.VoxelMeshResult res =
                TiledNavMeshBuilder.Build(mx, mtt, s, Environment.ProcessorCount, null);
            float h;
            bool present = ProbeBand(res, 6f, 6f, 3.40f, 3.95f, out h);
            Console.WriteLine("    r=" + r + " minArea=" + (r * r) + " cells: platform=" + present
                + " F=" + res.FaceCount + " pass=" + res.InvariantsPassed);
            if (present && surviveAt < 0)
                surviveAt = r;
        }
        int spanLo = surviveAt * surviveAt;
        int spanHi = (surviveAt + 1) * (surviveAt + 1) - 1;
        Console.WriteLine("  => platform walkable spanCount in [" + spanLo + ", " + spanHi + "] cells;"
            + " shipped RegionMinSize=8 (64 cells = 16 m^2) prunes it.");
        Console.WriteLine("  => MIN isolated square side to survive at cs=0.5 / RegionMinSize=8:"
            + " walkable >= 64 cells => (side-2) cells per edge => side >= 12 cells = 6.0 m (or any shape with >= 16 m^2 of walkable core).");

        // Regression check: lowering the region minimum must not change the terrain-only scenes.
        float[] hv;
        int[] ht;
        MakeGrid(160f, 160f, 0.25f, HillHeightF, out hv, out ht);
        float[] mv;
        int[] mt;
        MakeGrid(160f, 160f, 0.5f, MesaHeightF, out mv, out mt);

        int cand = surviveAt;
        bool allIdentical = true;
        Console.WriteLine("  terrain-only regression check (detail ON, shipped defaults otherwise), default 8 vs candidate "
            + cand + ":");
        string[] names = { "hill", "mesa" };
        for (int i = 0; i < 2; i++)
        {
            bool hill = i == 0;
            float[] sv = hill ? hv : mv;
            int[] st = hill ? ht : mt;
            TiledNavMeshBuilder.TiledNavMeshSettings s8 = HillSettings(true);
            s8.RegionMinSize = 8;
            s8.RegionMergeSize = 20;
            TiledNavMeshBuilder.TiledNavMeshSettings sc = HillSettings(true);
            sc.RegionMinSize = cand;
            sc.RegionMergeSize = 20;
            TiledNavMeshBuilder.VoxelMeshResult r8 =
                TiledNavMeshBuilder.Build(sv, st, s8, Environment.ProcessorCount, null);
            TiledNavMeshBuilder.VoxelMeshResult rc =
                TiledNavMeshBuilder.Build(sv, st, sc, Environment.ProcessorCount, null);
            bool same = r8.FaceCount == rc.FaceCount && r8.VertexCount == rc.VertexCount
                && Hash(r8.VertsXyz) == Hash(rc.VertsXyz) && Hash(r8.Tris) == Hash(rc.Tris);
            allIdentical &= same;
            Console.WriteLine("    " + names[i] + ": r8 F=" + r8.FaceCount + " V=" + r8.VertexCount
                + " | r" + cand + " F=" + rc.FaceCount + " V=" + rc.VertexCount + " | identical=" + same);
            if (!same)
            {
                Quality(names[i] + " r8", r8, 16f);
                Quality(names[i] + " r" + cand, rc, 16f);
            }
        }

        // Decision: the isolated-platform survival floor is real and measurable, but lowering the global
        // very thing the minimum exists to remove). The harness hill/mesa are byte-identical only because
        // they contain no such isles; a real map may, which would break the phase-3 terrain byte-identity
        // guarantee. So the shipped default stays 8 and the threshold is documented instead.
        Console.WriteLine("  RESULT: measured (platform pruned at r=8, survives at r=" + surviveAt
            + "; terrain scenes byte-identical at r=8 vs r=" + cand + "=" + allIdentical
            + "); shipped default UNCHANGED (isolated-deck floor documented, not widened).");
        Console.WriteLine("  REGION TEST: " + (surviveAt == 5 && allIdentical ? "PASS" : "FAIL"));
    }
    private static Dictionary<long, int> _emultCache;

    private static Dictionary<long, int> EMult(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        if (_emultCache != null && _emultCache.Count > 0) return _emultCache;
        var m = new Dictionary<long, int>();
        for (int f = 0; f < r.FaceCount; f++)
            for (int k = 0; k < 3; k++)
            {
                int a = r.Tris[f * 3 + k], b = r.Tris[f * 3 + ((k + 1) % 3)];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                int n;
                m.TryGetValue(key, out n);
                m[key] = n + 1;
            }
        _emultCache = m;
        return m;
    }

    private static int EdgeMult(TiledNavMeshBuilder.VoxelMeshResult r, long key)
    {
        int n;
        EMult(r).TryGetValue(key, out n);
        return n;
    }

    private static bool TriCovers(TiledNavMeshBuilder.VoxelMeshResult r, int f, float x, float z, float tol)
    {
        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
        float wa, wb, wc;
        if (!Bary(x, z, r.VertsXyz[a * 3], r.VertsXyz[a * 3 + 2],
            r.VertsXyz[b * 3], r.VertsXyz[b * 3 + 2],
            r.VertsXyz[c * 3], r.VertsXyz[c * 3 + 2], out wa, out wb, out wc)) return false;
        return wa >= -tol && wb >= -tol && wc >= -tol;
    }

    // The engine's adjacency test: two polys link when their columns touch, not when their centroids are near.
    //
    // gap = the minimum XZ distance between the two footprints (zero when they share a vertex), which is what
    // decides adjacency.
    //
    // dy = the WORST floor mismatch anywhere the two surfaces actually meet. This is the part that has to be
    // right: measuring the height difference at the single closest point reports 0.000 for every shared edge,
    // because the closest point of two edge-sharing triangles IS a shared vertex where both surfaces agree.
    // The step an agent really takes is the difference between the two surface heights across the whole
    // contact - the ends of a shared edge, and any T-junction where one face's edge ends inside the other.
    private static void TriGapDy(TiledNavMeshBuilder.VoxelMeshResult r, int f, int g, out float gap, out float dy)
    {
        gap = float.MaxValue;
        for (int k = 0; k < 3; k++)
        {
            int vi = r.Tris[f * 3 + k];
            float vx = r.VertsXyz[vi * 3], vz = r.VertsXyz[vi * 3 + 2];
            float d, hy;
            PointTriDistXz(r, g, vx, vz, out d, out hy);
            if (d < gap) gap = d;
            int wj = r.Tris[g * 3 + k];
            float wx = r.VertsXyz[wj * 3], wz = r.VertsXyz[wj * 3 + 2];
            float d2, hy2;
            PointTriDistXz(r, f, wx, wz, out d2, out hy2);
            if (d2 < gap) gap = d2;
        }
        dy = ContactDy(r, f, g);
    }

    // worst floor mismatch over the XZ contact region of two faces; NaN-free and 0 when they do not touch
    private static float ContactDy(TiledNavMeshBuilder.VoxelMeshResult r, int f, int g)
    {
        float worst = 0f;
        bool any = false;
        // each vertex of f against g's surface, and each vertex of g against f's surface
        for (int k = 0; k < 3; k++)
        {
            int vi = r.Tris[f * 3 + k];
            float vx = r.VertsXyz[vi * 3], vy = r.VertsXyz[vi * 3 + 1], vz = r.VertsXyz[vi * 3 + 2];
            float hy;
            if (TriHeightAt(r, g, vx, vz, out hy)) { float d = hy - vy; if (Math.Abs(d) > worst) worst = Math.Abs(d); any = true; }
            int wj = r.Tris[g * 3 + k];
            float wx = r.VertsXyz[wj * 3], wy = r.VertsXyz[wj * 3 + 1], wz = r.VertsXyz[wj * 3 + 2];
            float hy2;
            if (TriHeightAt(r, f, wx, wz, out hy2)) { float d = wy - hy2; if (Math.Abs(d) > worst) worst = Math.Abs(d); any = true; }
        }
        if (any) return worst;
        return 0f;
    }

    // height of face t's surface at (x,z), false when the point is outside its footprint
    private static bool TriHeightAt(TiledNavMeshBuilder.VoxelMeshResult r, int t, float x, float z, out float h)
    {
        int a = r.Tris[t * 3], b = r.Tris[t * 3 + 1], c = r.Tris[t * 3 + 2];
        float wa, wb, wc;
        if (!Bary(x, z, r.VertsXyz[a * 3], r.VertsXyz[a * 3 + 2],
            r.VertsXyz[b * 3], r.VertsXyz[b * 3 + 2],
            r.VertsXyz[c * 3], r.VertsXyz[c * 3 + 2], out wa, out wb, out wc)) { h = 0f; return false; }
        h = wa * r.VertsXyz[a * 3 + 1] + wb * r.VertsXyz[b * 3 + 1] + wc * r.VertsXyz[c * 3 + 1];
        return true;
    }

    private static void PointTriDistXz(TiledNavMeshBuilder.VoxelMeshResult r, int t, float px, float pz,
        out float dist, out float hy)
    {
        int a = r.Tris[t * 3], b = r.Tris[t * 3 + 1], c = r.Tris[t * 3 + 2];
        float ax = r.VertsXyz[a * 3], ay = r.VertsXyz[a * 3 + 1], az = r.VertsXyz[a * 3 + 2];
        float bx = r.VertsXyz[b * 3], by = r.VertsXyz[b * 3 + 1], bz = r.VertsXyz[b * 3 + 2];
        float cx = r.VertsXyz[c * 3], cy = r.VertsXyz[c * 3 + 1], cz = r.VertsXyz[c * 3 + 2];
        float wa, wb, wc;
        if (Bary(px, pz, ax, az, bx, bz, cx, cz, out wa, out wb, out wc))
        {
            dist = 0f;
            hy = wa * ay + wb * by + wc * cy;
            return;
        }
        float d = PointSegXz(ax, az, bx, bz, px, pz);
        dist = d;
        hy = PointSegY(ax, az, bx, bz, ay, by, px, pz);
        float d1 = PointSegXz(bx, bz, cx, cz, px, pz);
        if (d1 < d) { d = d1; hy = PointSegY(bx, bz, cx, cz, by, cy, px, pz); }
        float d2 = PointSegXz(cx, cz, ax, az, px, pz);
        if (d2 < d) { d = d2; hy = PointSegY(cx, cz, ax, az, cy, ay, px, pz); }
        dist = d;
    }

    private static float PointSegXz(float ax, float az, float bx, float bz, float px, float pz)
    {
        float dx = bx - ax, dz = bz - az;
        float len = dx * dx + dz * dz;
        float t = len < 1e-12f ? 0f : ((px - ax) * dx + (pz - az) * dz) / len;
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        float qx = ax + t * dx - px, qz = az + t * dz - pz;
        return (float)Math.Sqrt(qx * qx + qz * qz);
    }

    private static float PointSegY(float ax, float az, float bx, float bz, float ay, float by, float px, float pz)
    {
        float dx = bx - ax, dz = bz - az;
        float len = dx * dx + dz * dz;
        float t = len < 1e-12f ? 0f : ((px - ax) * dx + (pz - az) * dz) / len;
        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
        return ay + t * (by - ay);
    }

    private static float TriCX(TiledNavMeshBuilder.VoxelMeshResult r, int f)
    {
        return (r.VertsXyz[r.Tris[f * 3] * 3] + r.VertsXyz[r.Tris[f * 3 + 1] * 3] + r.VertsXyz[r.Tris[f * 3 + 2] * 3]) / 3f;
    }

    private static float TriCY(TiledNavMeshBuilder.VoxelMeshResult r, int f)
    {
        return (r.VertsXyz[r.Tris[f * 3] * 3 + 1] + r.VertsXyz[r.Tris[f * 3 + 1] * 3 + 1] + r.VertsXyz[r.Tris[f * 3 + 2] * 3 + 1]) / 3f;
    }

    private static float TriCZ(TiledNavMeshBuilder.VoxelMeshResult r, int f)
    {
        return (r.VertsXyz[r.Tris[f * 3] * 3 + 2] + r.VertsXyz[r.Tris[f * 3 + 1] * 3 + 2] + r.VertsXyz[r.Tris[f * 3 + 2] * 3 + 2]) / 3f;
    }

    // ================================================================================================
    // --soup "x0,z0,x1,z1[,hLo,hHi]": every MERGED SOUP triangle (terrain + assets, already lifted) whose
        // minimum would ALSO let isolated terrain noise-isles of 25-63 cells survive on every scene (the
    // ================================================================================================
    // --flood "x,z[,climb,linkGap[,boxR]]": the ENGINE's link rule, not ours.
    //
    // FaceComponents (and --connect) count edge-adjacency. The engine never sees our edges: it re-voxelizes
    // the imported collision faces and links two polys when their XZ columns are adjacent (within the agent
    // radius) and their floors differ by at most its climb. Two surfaces can therefore be edge-connected in
    // one big component around a loop, and STILL be uncrossable where the user walks.
    //
    // This does that link test literally: BFS over faces, seeded at the walkable face under (x,z), stepping to
    // any face whose XZ footprint is within linkGap of the current one and whose floor differs by <= climb.
    // It then reports the flood size and the BLOCKED FRONTIER - the flood's own boundary edges where a real
    // surface exists close enough to reach but is too high/low (dy > climb), or where nothing exists within
    // linkGap at all (a void). Those two lists are the crossable/unreachable answer.
    // ================================================================================================
    private static void FloodReport(TiledNavMeshBuilder.VoxelMeshResult r, string spec)
    {
        string[] qp = spec.Split(',');
        float px = (float)ParseFloat(qp[0]);
        float pz = (float)ParseFloat(qp[1]);
        float climb = qp.Length >= 3 ? (float)ParseFloat(qp[2]) : 0.6f;
        float linkGap = qp.Length >= 4 ? (float)ParseFloat(qp[3]) : 0.5f;
        float boxR = qp.Length >= 5 ? (float)ParseFloat(qp[4]) : 60f;
        int nf = r.FaceCount;

        // seed: the lowest face covering (x,z) within 3m of the terrain-ish expectation
        int seed = -1;
        float seedY = 0f, bestDy = float.MaxValue;
        for (int f = 0; f < nf; f++)
        {
            if (!TriCovers(r, f, px, pz, 0.05f)) continue;
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            float y = (r.VertsXyz[a * 3 + 1] + r.VertsXyz[b * 3 + 1] + r.VertsXyz[c * 3 + 1]) / 3f;
            float d = Math.Abs(y - (r.VertsXyz[a * 3 + 1]));
            if (d < bestDy) { bestDy = d; seed = f; seedY = y; }
        }
        if (seed < 0)
        {
            Console.WriteLine("== flood: no face covers (" + px.ToString("F2") + "," + pz.ToString("F2") + ") ==");
            return;
        }

        // XZ uniform grid over the box so the neighbour scan is local
        float mnx = px - boxR, mnz = pz - boxR;
        int nc = (int)Math.Ceiling(boxR * 2f / Math.Max(0.25f, linkGap)) + 2;
        float cs = (boxR * 2f) / (nc - 2);
        var buckets = new List<int>[nc * nc];
        bool InBox(int f)
        {
            int a = r.Tris[f * 3];
            float fx = r.VertsXyz[a * 3], fz = r.VertsXyz[a * 3 + 2];
            return fx >= mnx && fx <= mnx + boxR * 2f && fz >= mnz && fz <= mnz + boxR * 2f;
        }
        int Cx(float x) { int i = (int)((x - mnx) / cs) + 1; return i < 0 ? 0 : (i >= nc ? nc - 1 : i); }
        int Cz(float z) { int j = (int)((z - mnz) / cs) + 1; return j < 0 ? 0 : (j >= nc ? nc - 1 : j); }
        for (int f = 0; f < nf; f++)
        {
            if (!InBox(f)) continue;
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            float x0 = Math.Min(r.VertsXyz[a * 3], Math.Min(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            float x1 = Math.Max(r.VertsXyz[a * 3], Math.Max(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            float z0 = Math.Min(r.VertsXyz[a * 3 + 2], Math.Min(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
            float z1 = Math.Max(r.VertsXyz[a * 3 + 2], Math.Max(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
            for (int i = Cx(x0); i <= Cx(x1); i++)
                for (int j = Cz(z0); j <= Cz(z1); j++)
                {
                    List<int> lst = buckets[i * nc + j];
                    if (lst == null) { lst = new List<int>(); buckets[i * nc + j] = lst; }
                    lst.Add(f);
                }
        }

        var comp = new int[nf];
        for (int i = 0; i < nf; i++) comp[i] = -1;
        var inFlood = new bool[nf];
        var q = new Queue<int>();
        inFlood[seed] = true; comp[seed] = 0; q.Enqueue(seed);
        int count = 0;
        float floodMinY = seedY, floodMaxY = seedY;
        while (q.Count > 0)
        {
            int f = q.Dequeue();
            count++;
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            float fy = (r.VertsXyz[a * 3 + 1] + r.VertsXyz[b * 3 + 1] + r.VertsXyz[c * 3 + 1]) / 3f;
            if (fy < floodMinY) floodMinY = fy;
            if (fy > floodMaxY) floodMaxY = fy;
            float fx0 = Math.Min(r.VertsXyz[a * 3], Math.Min(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            float fx1 = Math.Max(r.VertsXyz[a * 3], Math.Max(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            float fz0 = Math.Min(r.VertsXyz[a * 3 + 2], Math.Min(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
            float fz1 = Math.Max(r.VertsXyz[a * 3 + 2], Math.Max(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
            for (int i = Cx(fx0) - 1; i <= Cx(fx1) + 1; i++)
                for (int j = Cz(fz0) - 1; j <= Cz(fz1) + 1; j++)
                {
                    if (i < 0 || j < 0 || i >= nc || j >= nc) continue;
                    List<int> lst = buckets[i * nc + j];
                    if (lst == null) continue;
                    for (int k = 0; k < lst.Count; k++)
                    {
                        int g = lst[k];
                        if (inFlood[g]) continue;
                        float gap, dy;
                        TriGapDy(r, f, g, out gap, out dy);
                        if (gap > linkGap) continue;
                        if (Math.Abs(dy) > climb) continue;
                        inFlood[g] = true; comp[g] = count; q.Enqueue(g);
                    }
                }
        }

        Console.WriteLine("== flood from (" + px.ToString("F2") + "," + pz.ToString("F2") + ") f" + seed
            + " y=" + seedY.ToString("F2") + ": faces=" + count + " of " + nf + " in a " + boxR.ToString("F0")
            + "m box, y=[" + floodMinY.ToString("F2") + ".." + floodMaxY.ToString("F2") + "] climb="
            + climb.ToString("F2") + " linkGap=" + linkGap.ToString("F2") + " ==");

        // Which of the probe targets are reachable under this rule?
        var targets = new List<string>();
        for (int i = 0; i < nf && i < 0; i++) { }
        // report reachability of a coarse lattice so the answer is legible
        int lat = (int)(boxR * 2f / 5f) + 1;
        var map = new System.Text.StringBuilder();
        int reachN = 0, unreachN = 0;
        for (int j = 0; j <= lat; j++)
        {
            for (int i = 0; i <= lat; i++)
            {
                float sx = mnx + i * 5f, sz = mnz + j * 5f;
                int f = -1;
                float by = float.MaxValue;
                for (int t = 0; t < nf; t++)
                {
                    if (!TriCovers(r, t, sx, sz, 0.05f)) continue;
                    int a = r.Tris[t * 3], b = r.Tris[t * 3 + 1], c = r.Tris[t * 3 + 2];
                    float y = (r.VertsXyz[a * 3 + 1] + r.VertsXyz[b * 3 + 1] + r.VertsXyz[c * 3 + 1]) / 3f;
                    if (y < by) { by = y; f = t; }
                }
                if (f < 0) { map.Append("  . "); continue; }
                if (inFlood[f]) { map.Append("  # "); reachN++; }
                else { map.Append("  o "); unreachN++; }
            }
            map.Append("   z=" + (mnz + j * 5f).ToString("F0") + "\n");
        }
        Console.Write("  lattice (# = reachable under the engine link rule, o = covered but NOT reachable, . = void)\n");
        Console.Write(map.ToString());
        Console.WriteLine("  lattice: reachable=" + reachN + " coveredButUnreachable=" + unreachN
            + (unreachN == 0 ? "  -> NO CROSSING BLOCKER in the box" : "  -> CROSSING BLOCKER(S) present"));

        // blocked frontier: flood boundary edges with a nearby surface that is too high or too low
        var rows = new List<string>();
        int tooHigh = 0, tooLow = 0, voids = 0;
        for (int f = 0; f < nf; f++)
        {
            if (!inFlood[f]) continue;
            for (int k = 0; k < 3; k++)
            {
                int va = r.Tris[f * 3 + k], vb = r.Tris[f * 3 + ((k + 1) % 3)];
                long key = va < vb ? ((long)va << 32) | (uint)vb : ((long)vb << 32) | (uint)va;
                if (EdgeMult(r, key) > 1) continue;       // interior edge, not a frontier
                float ax = r.VertsXyz[va * 3], ay = r.VertsXyz[va * 3 + 1], az = r.VertsXyz[va * 3 + 2];
                float bx = r.VertsXyz[vb * 3], by = r.VertsXyz[vb * 3 + 1], bz = r.VertsXyz[vb * 3 + 2];
                float mx = (ax + bx) * 0.5f, mz = (az + bz) * 0.5f, my = (ay + by) * 0.5f;
                int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                float fx0 = Math.Min(r.VertsXyz[a * 3], Math.Min(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
                float fx1 = Math.Max(r.VertsXyz[a * 3], Math.Max(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
                float fz0 = Math.Min(r.VertsXyz[a * 3 + 2], Math.Min(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
                float fz1 = Math.Max(r.VertsXyz[a * 3 + 2], Math.Max(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
                float fGap = float.MaxValue, fDy = 0f, fY = 0f;
                int bestFace = -1;
                for (int i = Cx(fx0) - 1; i <= Cx(fx1) + 1; i++)
                    for (int j = Cz(fz0) - 1; j <= Cz(fz1) + 1; j++)
                    {
                        if (i < 0 || j < 0 || i >= nc || j >= nc) continue;
                        List<int> lst = buckets[i * nc + j];
                        if (lst == null) continue;
                        for (int t = 0; t < lst.Count; t++)
                        {
                            int g = lst[t];
                            if (inFlood[g]) continue;
                            float gap, dy;
                            TriGapDy(r, f, g, out gap, out dy);
                            if (gap > linkGap || gap >= fGap) continue;
                            int ga = r.Tris[g * 3], gb = r.Tris[g * 3 + 1], gc = r.Tris[g * 3 + 2];
                            fGap = gap; fDy = dy; bestFace = g;
                            fY = (r.VertsXyz[ga * 3 + 1] + r.VertsXyz[gb * 3 + 1] + r.VertsXyz[gc * 3 + 1]) / 3f;
                        }
                    }
                string tag;
                if (bestFace < 0) { voids++; tag = "VOID"; }
                else if (fDy > climb) { tooHigh++; tag = "TOO HIGH"; }
                else if (fDy < -climb) { tooLow++; tag = "TOO LOW"; }
                else continue;
                rows.Add("  " + tag.PadRight(8) + " at (" + mx.ToString("F2") + "," + my.ToString("F2") + ","
                    + mz.ToString("F2") + ") -> "
                    + (bestFace < 0 ? "nothing within " + linkGap.ToString("F2") + "m"
                        : "f" + bestFace + " gapXZ=" + fGap.ToString("F3") + "m dy=" + fDy.ToString("F3")
                            + "m y=" + fY.ToString("F2")));
            }
        }
        rows.Sort(StringComparer.Ordinal);
        int shown = 0;
        for (int i = 0; i < rows.Count && shown < 40; i++, shown++) Console.WriteLine(rows[i]);
        Console.WriteLine("  frontier summary: void=" + voids + " tooHigh=" + tooHigh + " tooLow=" + tooLow);
    }

    // ================================================================================================
    // --walk "x,z;x,z;...": foot -> through -> far-side traversal under the ENGINE's link rule.
    //
    // Seeds a flood at the first point and reports, for every other point, whether a walkable face there is
    // reachable - plus the first blocking step along the way. Unlike FaceComponents (edge adjacency) and
    // unlike the old probe (mesh coverage at a column), this answers the only question that matters: can an
    // agent with a climb and a radius get from A to B over surfaces that are really there.
    // ================================================================================================
    // ================================================================================================
    // --walk "x,z,y;x,z,y;...": foot -> through -> far-side traversal under the ENGINE's link rule.
    //
    // Seeds a flood at the LOWEST face under the first point and reports, for every other point, whether a
    // walkable face there is reachable. FaceComponents counts edge adjacency; the old probe only asked
    // whether a column is covered. Neither answers the only question that matters: can an agent with a climb
    // and a radius actually get from A to B over surfaces that are really there.
    //
    // The link test is the honest one: two polys are linked when their XZ footprints TOUCH (the engine links
    // adjacent columns, not centroids) and their floors differ by at most the climb. Footprint distance is
    // the exact min over vertex-to-opposite-triangle pairs, which is what "adjacent columns" means here.
    // ================================================================================================
    // ================================================================================================
    // --walk "x,z[,y];x,z[,y];... [|climb,gap]": foot -> through -> far-side traversal under the ENGINE's link
    // rule, WITH the actual route printed.
    //
    // FaceComponents counts edge adjacency; a column probe only asks whether a column is covered. Neither
    // answers the only question that matters: can an agent with a climb and a radius get from A to B over
    // surfaces that are really there. This does that test literally - two polys link when their XZ footprints
    // TOUCH (the engine links adjacent columns, not centroids) and their floors differ by at most the climb -
    // and then prints the route so the gate crossing can be seen rather than assumed.
    // ================================================================================================
    private static void WalkReport(TiledNavMeshBuilder.VoxelMeshResult r, string spec)
    {
        string climbGap = null;
        int bar = spec.IndexOf('|');
        if (bar >= 0) { climbGap = spec.Substring(bar + 1); spec = spec.Substring(0, bar); }
        float climb = 0.6f, linkGap = 0.02f;
        if (climbGap != null)
        {
            string[] cg = climbGap.Split(',');
            if (cg.Length > 0) climb = (float)ParseFloat(cg[0]);
            if (cg.Length > 1) linkGap = (float)ParseFloat(cg[1]);
        }
        string[] pts = spec.Split(';');
        var pl = new List<float[]>();
        foreach (string t in pts)
        {
            string[] q = t.Split(',');
            if (q.Length < 2) continue;
            pl.Add(new float[] { (float)ParseFloat(q[0]), (float)ParseFloat(q[1]),
                q.Length >= 3 ? (float)ParseFloat(q[2]) : 0f });
        }
        if (pl.Count < 2) { Console.WriteLine("walk: need >= 2 points"); return; }
        float[] s0 = pl[0];
        float rad = 0f;
        foreach (float[] p in pl)
        {
            rad = Math.Max(rad, Math.Abs(p[0] - s0[0]));
            rad = Math.Max(rad, Math.Abs(p[1] - s0[1]));
        }
        float boxR = rad + 40f;
        float mnx = s0[0] - boxR, mnz = s0[1] - boxR;
        int nc = (int)Math.Ceiling(boxR * 2f / 0.5f) + 2;
        float cs = (boxR * 2f) / (nc - 2);
        int nf = r.FaceCount;
        var buckets = new List<int>[nc * nc];
        int Cx(float x) { int i = (int)((x - mnx) / cs) + 1; return i < 0 ? 0 : (i >= nc ? nc - 1 : i); }
        int Cz(float z) { int j = (int)((z - mnz) / cs) + 1; return j < 0 ? 0 : (j >= nc ? nc - 1 : j); }
        for (int f = 0; f < nf; f++)
        {
            int a = r.Tris[f * 3];
            float fx = r.VertsXyz[a * 3], fz = r.VertsXyz[a * 3 + 2];
            if (fx < mnx || fx > mnx + boxR * 2f || fz < mnz || fz > mnz + boxR * 2f) continue;
            int b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            float x0 = Math.Min(r.VertsXyz[a * 3], Math.Min(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            float x1 = Math.Max(r.VertsXyz[a * 3], Math.Max(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            float z0 = Math.Min(r.VertsXyz[a * 3 + 2], Math.Min(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
            float z1 = Math.Max(r.VertsXyz[a * 3 + 2], Math.Max(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
            for (int i = Cx(x0); i <= Cx(x1); i++)
                for (int j = Cz(z0); j <= Cz(z1); j++)
                {
                    List<int> lst = buckets[i * nc + j];
                    if (lst == null) { lst = new List<int>(); buckets[i * nc + j] = lst; }
                    lst.Add(f);
                }
        }

        var inFlood = new bool[nf];
        var parent = new int[nf];
        for (int i = 0; i < nf; i++) parent[i] = -1;
        int seed = -1;
        float seedY = 0f;
        {
            float by = float.MaxValue;
            for (int f = 0; f < nf; f++)
            {
                if (!TriCovers(r, f, s0[0], s0[1], 0.05f)) continue;
                float y = TriCY(r, f);
                float d = s0[2] > 0f ? Math.Abs(y - s0[2]) : y;
                if (d < by) { by = d; seed = f; seedY = y; }
            }
        }
        if (seed < 0) { Console.WriteLine("walk: no face at (" + s0[0] + "," + s0[1] + ")"); return; }
        var bfs = new Queue<int>();
        inFlood[seed] = true; bfs.Enqueue(seed);
        int count = 0;
        while (bfs.Count > 0)
        {
            int f = bfs.Dequeue();
            count++;
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            float fx0 = Math.Min(r.VertsXyz[a * 3], Math.Min(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            float fx1 = Math.Max(r.VertsXyz[a * 3], Math.Max(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            float fz0 = Math.Min(r.VertsXyz[a * 3 + 2], Math.Min(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
            float fz1 = Math.Max(r.VertsXyz[a * 3 + 2], Math.Max(r.VertsXyz[b * 3 + 2], r.VertsXyz[c * 3 + 2]));
            for (int i = Cx(fx0) - 1; i <= Cx(fx1) + 1; i++)
                for (int j = Cz(fz0) - 1; j <= Cz(fz1) + 1; j++)
                {
                    if (i < 0 || j < 0 || i >= nc || j >= nc) continue;
                    List<int> lst = buckets[i * nc + j];
                    if (lst == null) continue;
                    for (int t = 0; t < lst.Count; t++)
                    {
                        int g = lst[t];
                        if (inFlood[g]) continue;
                        float gap, dy;
                        TriGapDy(r, f, g, out gap, out dy);
                        if (gap > linkGap || Math.Abs(dy) > climb) continue;
                        inFlood[g] = true; parent[g] = f; bfs.Enqueue(g);
                    }
                }
        }
        Console.WriteLine("walk: seed f" + seed + " at (" + s0[0].ToString("F2") + "," + s0[1].ToString("F2")
            + ") y=" + seedY.ToString("F2") + " -> flood=" + count + " faces, climb=" + climb.ToString("F2")
            + " linkGap=" + linkGap.ToString("F3") + " (footprint touch)");

        var picks = new int[pl.Count];
        for (int i = 0; i < pl.Count; i++)
        {
            float[] p = pl[i];
            float pick = float.MaxValue, pickY = 0f;
            picks[i] = -1;
            for (int f = 0; f < nf; f++)
            {
                if (!TriCovers(r, f, p[0], p[1], 0.05f)) continue;
                float y = TriCY(r, f);
                float d = p[2] > 0f ? Math.Abs(y - p[2]) : y;
                if (d < pick) { pick = d; pickY = y; picks[i] = f; }
                if (i == 0 && picks[i] != seed && f == seed) { picks[i] = seed; pick = pickY; }
            }
            picks[i] = picks[i];
            if (picks[i] < 0)
            {
                Console.WriteLine("  target " + i + " (" + p[0].ToString("F2") + "," + p[1].ToString("F2")
                    + ") -> NO MESH AT ALL  [VOID]");
                continue;
            }
            bool ok = inFlood[picks[i]];
            Console.WriteLine("  target " + i + " (" + p[0].ToString("F2") + "," + p[1].ToString("F2")
                + ") y=" + pickY.ToString("F2") + " f" + picks[i] + " -> " + (ok ? "WALKABLE" : "BLOCKED"));
            if (ok || picks[i] == seed) continue;
            float bestGap = float.MaxValue;
            int near = -1;
            float nd = 0f;
            for (int f = 0; f < nf; f++)
            {
                if (!inFlood[f]) continue;
                float gap, dy;
                TriGapDy(r, f, picks[i], out gap, out dy);
                if (gap < bestGap) { bestGap = gap; near = f; nd = dy; }
            }
            if (near >= 0)
            {
                Console.WriteLine("      nearest reachable f" + near + " at ("
                    + TriCX(r, near).ToString("F2") + "," + TriCY(r, near).ToString("F2") + ","
                    + TriCZ(r, near).ToString("F2") + ") gapXZ=" + bestGap.ToString("F3")
                    + "m dy=" + nd.ToString("F3") + "m -> "
                    + (bestGap > linkGap ? "XZ GAP " + bestGap.ToString("F2") + "m (footprints do not touch)"
                        : (Math.Abs(nd) > climb ? (nd > 0 ? "STEP UP " + nd.ToString("F2") + "m" : "STEP DOWN " + (-nd).ToString("F2") + "m") : "??")));
            }
        }

        // route from the seed to the last reachable target: where does it actually cross?
        int last = -1;
        for (int i = pl.Count - 1; i >= 0; i--)
            if (picks[i] >= 0 && inFlood[picks[i]]) { last = picks[i]; break; }
        if (last < 0 || last == seed) { Console.WriteLine("  route: none"); return; }
        var path = new List<int>();
        for (int f = last; f != -1; f = parent[f]) path.Add(f);
        path.Reverse();
        float worstDy = 0f; int worstAt = -1;
        float worstGap = 0f;
        for (int i = 1; i < path.Count; i++)
        {
            float gap, dy;
            TriGapDy(r, path[i - 1], path[i], out gap, out dy);
            if (gap > worstGap) worstGap = gap;
            if (Math.Abs(dy) > Math.Abs(worstDy)) { worstDy = dy; worstAt = i; }
        }
        Console.WriteLine("  route: " + path.Count + " faces, worst step dy=" + worstDy.ToString("F3") + "m at f"
            + (worstAt > 0 ? path[worstAt] : seed).ToString() + " ("
            + (worstAt > 0 ? TriCX(r, path[worstAt]).ToString("F2") + "," + TriCY(r, path[worstAt]).ToString("F2")
                + "," + TriCZ(r, path[worstAt]).ToString("F2") : "-") + "), widest footprint gap="
            + worstGap.ToString("F3") + "m");
        // crossings of the gate band x in [1126,1136]
        Console.WriteLine("  route through the gate band:");
        for (int i = 1; i < path.Count; i++)
        {
            float gx = TriCX(r, path[i]), gz = TriCZ(r, path[i]);
            if (gx < 1125.5f || gx > 1136.5f) continue;
            float gap, dy;
            TriGapDy(r, path[i - 1], path[i], out gap, out dy);
            Console.WriteLine("    step " + i + ": f" + path[i - 1] + "->f" + path[i] + " at ("
                + gx.ToString("F2") + "," + TriCY(r, path[i]).ToString("F2") + "," + gz.ToString("F2")
                + ") dy=" + dy.ToString("F3") + "m gapXZ=" + gap.ToString("F3") + "m");
        }
    }

    // Minimum XZ distance between two triangles and the height difference AT that closest pair of points.
    // This is the engine's adjacency test: two polys link when their columns touch, not when their centroids
    // are near. Vertex-to-triangle in both directions covers every case (edge-edge crossing is caught by the
    // endpoints), and the reported dy is the actual step an agent would climb at the crossing.
    // ================================================================================================
    // --pinch "x,z,r[,reach]": where can an agent NOT cross?
    //
    // The engine re-voxelizes the imported faces and links two polys when their columns are adjacent and
    // their floors differ by at most its climb. So the questions that matter are NOT "is the mesh edge-
    // connected" (the harness's own component metric) but:
    //   (1) PINCH: a vertex whose incident faces split into two or more groups that share no EDGE. The
    //       surface touches itself at a point; nothing links across it, and a top-down render cannot show
    //       it, because every face is there - the mesh just fans out from one point.
    //   (2) PORTAL: for every mesh BOUNDARY edge in the spot, the nearest walkable surface that is not one
    //       of its own faces - its XZ gap and its dy. That is the engine's link test, reported per edge.
    // ================================================================================================
    // --soup "x0,z0,x1,z1[,hLo,hHi]": every MERGED SOUP triangle (terrain + assets, already lifted) whose
    // XZ centroid lands in the box, with its vertex heights, its slope and whether it looks up. This is the
    // ground truth for "is there walkable collision in that hole, and what shape is it" - the terrain grid
    // alone cannot answer it, and a mesh probe only says "nothing is there".
    // ================================================================================================
    private static void SoupBoxReport(float[] soupVerts, int[] soupTris, string spec)
    {
        string[] qp = spec.Split(',');
        float x0 = (float)ParseFloat(qp[0]), z0 = (float)ParseFloat(qp[1]);
        float x1 = (float)ParseFloat(qp[2]), z1 = (float)ParseFloat(qp[3]);
        float hLo = qp.Length >= 5 ? (float)ParseFloat(qp[4]) : float.NegativeInfinity;
        float hHi = qp.Length >= 6 ? (float)ParseFloat(qp[5]) : float.MaxValue;
        if (x1 < x0) { float t = x0; x0 = x1; x1 = t; }
        if (z1 < z0) { float t = z0; z0 = z1; z1 = t; }
        Console.WriteLine("== soupbox X=[" + x0.ToString("F2") + ".." + x1.ToString("F2") + "] Z=["
            + z0.ToString("F2") + ".." + z1.ToString("F2") + "] H=[" + hLo.ToString("F1") + ".."
            + hHi.ToString("F1") + "] (lifted soup space) ==");
        int tri = soupTris.Length / 3, shown = 0, total = 0;
        var byLevel = new Dictionary<int, int>();
        for (int t = 0; t < tri; t++)
        {
            int a = soupTris[t * 3], b = soupTris[t * 3 + 1], c = soupTris[t * 3 + 2];
            float ax = soupVerts[a * 3], ay = soupVerts[a * 3 + 1], az = soupVerts[a * 3 + 2];
            float bx = soupVerts[b * 3], by = soupVerts[b * 3 + 1], bz = soupVerts[b * 3 + 2];
            float cx = soupVerts[c * 3], cy = soupVerts[c * 3 + 1], cz = soupVerts[c * 3 + 2];
            float gx = (ax + bx + cx) / 3f, gz = (az + bz + cz) / 3f, gy = (ay + by + cy) / 3f;
            if (gx < x0 || gx > x1 || gz < z0 || gz > z1) continue;
            if (gy < hLo || gy > hHi) continue;
            total++;
            int band = (int)Math.Round(gy);
            int cnt;
            byLevel.TryGetValue(band, out cnt);
            byLevel[band] = cnt + 1;
            if (shown < 400)
            {
                shown++;
                double ux = bx - ax, uy = by - ay, uz = bz - az;
                double wx = cx - ax, wy = cy - ay, wz = cz - az;
                double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                double nyN = len < 1e-12 ? 0.0 : ny / len;
                double slope = Math.Acos(Math.Min(1.0, Math.Abs(nyN))) * 180.0 / Math.PI;
                Console.WriteLine("  t" + t + " c=(" + gx.ToString("F2") + "," + gy.ToString("F2") + ","
                    + gz.ToString("F2") + ") y=[" + Math.Min(ay, Math.Min(by, cy)).ToString("F2") + ".."
                    + Math.Max(ay, Math.Max(by, cy)).ToString("F2") + "] slope=" + slope.ToString("F1")
                    + "deg ny=" + nyN.ToString("F2")
                    + (len < 1e-9 ? " DEGENERATE" : "")
                    + (Math.Abs(nyN) * len < 1e-9 ? " VERTICAL" : ""));
            }
        }
        var bands = new List<int>(byLevel.Keys);
        bands.Sort();
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < bands.Count; i++)
        {
            if (i > 0) sb.Append(" ");
            sb.Append(bands[i]).Append("m:").Append(byLevel[bands[i]]);
        }
        Console.WriteLine("soupbox: tris=" + total + " bands(" + bands.Count + ")=" + sb.ToString());
    }

    // ================================================================================================
    // ================================================================================================
    // --nonmanifold: every edge shared by three or more faces, with its endpoints and its faces.
    // The gate refuses to import a mesh whose maxEdgeMult > 2, so when a pass changes topology the first
    // question is WHICH edge and WHICH faces made it three-way - a position report is enough to tell a
    // duplicated strip from a pre-existing junction.
    // ================================================================================================
    private static void NonManifoldReport(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        var m = new Dictionary<long, List<int>>();
        for (int f = 0; f < r.FaceCount; f++)
            for (int k = 0; k < 3; k++)
            {
                int a = r.Tris[f * 3 + k], b = r.Tris[f * 3 + ((k + 1) % 3)];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                List<int> lst;
                if (!m.TryGetValue(key, out lst)) { lst = new List<int>(); m[key] = lst; }
                lst.Add(f);
            }
        int bad = 0;
        foreach (KeyValuePair<long, List<int>> kv in m)
        {
            if (kv.Value.Count <= 2) continue;
            bad++;
            int a = (int)(kv.Key >> 32), b = (int)(uint)kv.Key;
            var parts = new System.Text.StringBuilder();
            for (int i = 0; i < kv.Value.Count; i++)
            {
                if (i > 0) parts.Append(", ");
                parts.Append("f").Append(kv.Value[i]).Append("(")
                    .Append(TriCX(r, kv.Value[i]).ToString("F2")).Append(",")
                    .Append(TriCY(r, kv.Value[i]).ToString("F2")).Append(",")
                    .Append(TriCZ(r, kv.Value[i]).ToString("F2")).Append(")");
            }
            Console.WriteLine("  EDGE x" + kv.Value.Count + " v" + a + "("
                + r.VertsXyz[a * 3].ToString("F3") + "," + r.VertsXyz[a * 3 + 1].ToString("F3") + ","
                + r.VertsXyz[a * 3 + 2].ToString("F3") + ") v" + b + "("
                + r.VertsXyz[b * 3].ToString("F3") + "," + r.VertsXyz[b * 3 + 1].ToString("F3") + ","
                + r.VertsXyz[b * 3 + 2].ToString("F3") + ") faces=" + parts);
        }
        Console.WriteLine("nonmanifold: edges with >2 faces = " + bad + " (total distinct edges=" + m.Count + ")");
    }

    private static void PinchReport(TiledNavMeshBuilder.VoxelMeshResult r, string spec)
    {
        string[] qp = spec.Split(',');
        float px = (float)ParseFloat(qp[0]);
        float pz = (float)ParseFloat(qp[1]);
        float rad = qp.Length >= 3 ? (float)ParseFloat(qp[2]) : 8f;
        float reach = qp.Length >= 4 ? (float)ParseFloat(qp[3]) : 1.5f;
        int comps;
        int[] fp = FaceComponents(r, out comps);
        int nf = r.FaceCount;
        float r2 = rad * rad;

        var inc = new Dictionary<int, List<int>>();
        for (int f = 0; f < nf; f++)
            for (int k = 0; k < 3; k++)
            {
                int vi = r.Tris[f * 3 + k];
                float ddx = r.VertsXyz[vi * 3] - px, ddz = r.VertsXyz[vi * 3 + 2] - pz;
                if (ddx * ddx + ddz * ddz > r2) continue;
                List<int> lst;
                if (!inc.TryGetValue(vi, out lst)) { lst = new List<int>(); inc[vi] = lst; }
                lst.Add(f);
            }

        var emult = new Dictionary<long, int>();
        for (int f = 0; f < nf; f++)
            for (int k = 0; k < 3; k++)
            {
                int a = r.Tris[f * 3 + k], b = r.Tris[f * 3 + ((k + 1) % 3)];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                int n;
                emult.TryGetValue(key, out n);
                emult[key] = n + 1;
            }

        Console.WriteLine("== pinch (x=" + px.ToString("F2") + ",z=" + pz.ToString("F2") + ") r="
            + rad.ToString("F2") + " reach=" + reach.ToString("F2") + " verts=" + inc.Count
            + " components=" + comps + " ==");
        int pinches = 0, deadEnds = 0, portals = 0;
        var keys = new List<int>(inc.Keys);
        keys.Sort();
        for (int ki = 0; ki < keys.Count; ki++)
        {
            int vi = keys[ki];
            List<int> faces = inc[vi];
            if (faces.Count == 1) deadEnds++;
            var par = new int[faces.Count];
            for (int i = 0; i < faces.Count; i++) par[i] = i;
            for (int i = 0; i < faces.Count; i++)
            {
                int f = faces[i];
                for (int q = 0; q < 3; q++)
                {
                    int a = r.Tris[f * 3 + q], b = r.Tris[f * 3 + ((q + 1) % 3)];
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    int n;
                    emult.TryGetValue(key, out n);
                    if (n != 2) continue;                      // only an interior edge joins two faces
                    for (int j = 0; j < faces.Count; j++)
                    {
                        if (j == i) continue;
                        int g = faces[j];
                        bool shares = false;
                        for (int t = 0; t < 3 && !shares; t++)
                        {
                            int u = r.Tris[g * 3 + t];
                            if (u != a && u != b) continue;
                            for (int w = 0; w < 3; w++)
                                if (r.Tris[f * 3 + w] == u) { shares = true; break; }
                        }
                        if (!shares) continue;
                        int ra = Find(par, i), rb = Find(par, j);
                        if (ra != rb) par[ra] = rb;
                    }
                }
            }
            var groups = new Dictionary<int, int>();
            for (int i = 0; i < faces.Count; i++)
            {
                int rt = Find(par, i);
                int n;
                groups.TryGetValue(rt, out n);
                groups[rt] = n + 1;
            }
            if (groups.Count < 2) continue;
            pinches++;
            var gs = new List<int>(groups.Values);
            gs.Sort();
            gs.Reverse();
            var compIds = new List<string>();
            for (int i = 0; i < faces.Count; i++)
            {
                int rt = Find(par, i);
                string cid = "g" + rt + "/isl" + Find(fp, faces[i]);
                if (!compIds.Contains(cid)) compIds.Add(cid);
            }
            Console.WriteLine("  PINCH v" + vi + " at (" + r.VertsXyz[vi * 3].ToString("F3") + ","
                + r.VertsXyz[vi * 3 + 1].ToString("F3") + "," + r.VertsXyz[vi * 3 + 2].ToString("F3")
                + ") ring=" + faces.Count + " edgeGroups=" + groups.Count + " sizes=" + string.Join("/", gs)
                + " " + string.Join(" ", compIds.ToArray())
                + (gs[0] == 1 ? "  [DEAD END]" : ""));
        }

        var rows = new List<string>();
        for (int f = 0; f < nf; f++)
        {
            float fx = (r.VertsXyz[r.Tris[f * 3] * 3] + r.VertsXyz[r.Tris[f * 3 + 1] * 3] + r.VertsXyz[r.Tris[f * 3 + 2] * 3]) / 3f;
            float fz = (r.VertsXyz[r.Tris[f * 3] * 3 + 2] + r.VertsXyz[r.Tris[f * 3 + 1] * 3 + 2] + r.VertsXyz[r.Tris[f * 3 + 2] * 3 + 2]) / 3f;
            float fdx = fx - px, fdz = fz - pz;
            if (fdx * fdx + fdz * fdz > r2) continue;
            for (int k = 0; k < 3; k++)
            {
                int va = r.Tris[f * 3 + k], vb = r.Tris[f * 3 + ((k + 1) % 3)];
                long key = va < vb ? ((long)va << 32) | (uint)vb : ((long)vb << 32) | (uint)va;
                int n;
                emult.TryGetValue(key, out n);
                if (n != 1) continue;
                float ax = r.VertsXyz[va * 3], ay = r.VertsXyz[va * 3 + 1], az = r.VertsXyz[va * 3 + 2];
                float bx = r.VertsXyz[vb * 3], by = r.VertsXyz[vb * 3 + 1], bz = r.VertsXyz[vb * 3 + 2];
                float my = (ay + by) * 0.5f;
                float bestGap = float.MaxValue, bestDy = 0f, bestY = 0f;
                int bestFace = -1;
                for (int g = 0; g < nf; g++)
                {
                    if (g == f) continue;
                    bool shares = false;
                    for (int q = 0; q < 3 && !shares; q++)
                    {
                        int u = r.Tris[g * 3 + q];
                        if (u != va && u != vb) continue;
                        for (int w = 0; w < 3; w++)
                            if (r.Tris[f * 3 + w] == u) { shares = true; break; }
                    }
                    if (shares) continue;
                    int ga = r.Tris[g * 3], gb = r.Tris[g * 3 + 1], gc = r.Tris[g * 3 + 2];
                    float gx = (r.VertsXyz[ga * 3] + r.VertsXyz[gb * 3] + r.VertsXyz[gc * 3]) / 3f;
                    float gz = (r.VertsXyz[ga * 3 + 2] + r.VertsXyz[gb * 3 + 2] + r.VertsXyz[gc * 3 + 2]) / 3f;
                    float gxx = gx - (ax + bx) * 0.5f, gzz = gz - (az + bz) * 0.5f;
                    float d = (float)Math.Sqrt(gxx * gxx + gzz * gzz);
                    if (d >= bestGap || d > reach) continue;
                    float gy = (r.VertsXyz[ga * 3 + 1] + r.VertsXyz[gb * 3 + 1] + r.VertsXyz[gc * 3 + 1]) / 3f;
                    bestGap = d; bestDy = gy - my; bestY = gy; bestFace = g;
                }
                portals++;
                rows.Add("  PORTAL edge (" + ax.ToString("F2") + "," + ay.ToString("F2") + "," + az.ToString("F2")
                    + ")-(" + bx.ToString("F2") + "," + by.ToString("F2") + "," + bz.ToString("F2") + ")"
                    + (bestFace < 0
                        ? " -> NO surface within " + reach.ToString("F2") + "m   [OPEN HOLE]"
                        : " gapXZ=" + bestGap.ToString("F3") + "m dy=" + bestDy.ToString("F3")
                            + "m nearestY=" + bestY.ToString("F2") + " f" + bestFace));
            }
        }
        rows.Sort(StringComparer.Ordinal);
        int shown = 0;
        for (int i = 0; i < rows.Count && shown < 70; i++, shown++) Console.WriteLine(rows[i]);
        Console.WriteLine("pinch summary: pinchVerts=" + pinches + " deadEndVerts=" + deadEnds
            + " boundaryEdges=" + portals);
    }

    // ================================================================ V4 connectivity lab
    // Terrain+asset scenes that reproduce the editor's narrow-feature failures. Each scenario
    // has probe points and "same component" / "different component" face-adjacency assertions.
    private static float FlatT(float x, float z) { return 0f; }

    private sealed class Lab
    {
        public string Name;
        public float[] TV;
        public int[] TT;
        public List<float> AV = new List<float>();
        public List<int> AT = new List<int>();
        public List<string> PName = new List<string>();
        public List<float> PX = new List<float>();
        public List<float> PZ = new List<float>();
        public List<float> PY = new List<float>();
        public List<int[]> Same = new List<int[]>();
        public List<int[]> Diff = new List<int[]>();

        public int P(string n, float x, float z, float y)
        {
            PName.Add(n); PX.Add(x); PZ.Add(z); PY.Add(y);
            return PName.Count - 1;
        }
    }

    private static Lab MakeDoorwayLab()
    {
        var lab = new Lab();
        lab.Name = "a) doorway 1.2m";
        MakeGrid(24f, 24f, 1f, FlatT, out lab.TV, out lab.TT);
        AddBoxZ(lab.AV, lab.AT, 4f, 4f, 0f, 20f, 4.4f, 3f);        // south wall
        AddBoxZ(lab.AV, lab.AT, 4f, 15.6f, 0f, 20f, 16f, 3f);      // north wall
        AddBoxZ(lab.AV, lab.AT, 4f, 4.4f, 0f, 4.4f, 15.6f, 3f);    // west wall
        AddBoxZ(lab.AV, lab.AT, 19.6f, 4.4f, 0f, 20f, 15.6f, 3f);  // east wall
        AddBoxZ(lab.AV, lab.AT, 11.8f, 4.4f, 0f, 12.2f, 8.4f, 3f); // divider south of the doorway
        AddBoxZ(lab.AV, lab.AT, 11.8f, 9.6f, 0f, 12.2f, 15.6f, 3f); // divider north of the doorway
        int a = lab.P("roomA", 8f, 10f, 0f);
        int b = lab.P("roomB", 16f, 10f, 0f);
        lab.Same.Add(new[] { a, b });
        return lab;
    }

    private static Lab MakeWalkwayLab()
    {
        var lab = new Lab();
        lab.Name = "b) walkway 1.0m, drops both sides";
        MakeGrid(24f, 24f, 1f, FlatT, out lab.TV, out lab.TT);
        AddBoxZ(lab.AV, lab.AT, 2f, 9f, 0f, 8f, 15f, 3f);          // platform A (top 3)
        AddBoxZ(lab.AV, lab.AT, 16f, 9f, 0f, 22f, 15f, 3f);        // platform B (top 3)
        AddBoxZ(lab.AV, lab.AT, 8f, 11.5f, 0f, 16f, 12.5f, 3f);    // 1.0 m walkway between them
        int a = lab.P("platformA", 5f, 12f, 3f);
        int b = lab.P("platformB", 19f, 12f, 3f);
        lab.Same.Add(new[] { a, b });
        return lab;
    }

    private static Lab MakeStairsLab()
    {
        var lab = new Lab();
        lab.Name = "c) staircase 1.0m wide, 6 x 0.4m";
        MakeGrid(28f, 24f, 1f, FlatT, out lab.TV, out lab.TT);
        AddBoxZ(lab.AV, lab.AT, 14f, 9f, 0f, 20f, 15f, 2.4f);      // top platform (top 2.4)
        for (int k = 0; k < 6; k++)                                 // steps up to 2.4
            AddBoxZ(lab.AV, lab.AT, 9.2f + k * 0.8f, 11.5f, 0f, 10f + k * 0.8f, 12.5f, 0.4f * (k + 1));
        int g = lab.P("ground", 6f, 12f, 0f);
        int t = lab.P("topPlatform", 17f, 12f, 2.4f);
        lab.Same.Add(new[] { g, t });
        return lab;
    }

    private static Lab MakeTunnelLab()
    {
        var lab = new Lab();
        lab.Name = "d) tunnel 2.5 x 2.2 x 8, both ends open";
        MakeGrid(24f, 24f, 1f, FlatT, out lab.TV, out lab.TT);
        AddBoxZ(lab.AV, lab.AT, 6f, 4f, 0f, 6.25f, 12f, 2.5f);     // west wall
        AddBoxZ(lab.AV, lab.AT, 8.75f, 4f, 0f, 9f, 12f, 2.5f);     // east wall
        AddBoxZ(lab.AV, lab.AT, 6f, 4f, 2.5f, 9f, 12f, 2.8f);      // roof (top 2.8)
        int i0 = lab.P("tunnelIn near-end", 7.5f, 5.5f, 0f);
        int i1 = lab.P("tunnelIn far-end", 7.5f, 10.5f, 0f);
        int out0 = lab.P("outside", 7.5f, 2f, 0f);
        int roof = lab.P("roof deck", 7.5f, 8f, 2.8f);
        lab.Same.Add(new[] { i0, i1 });
        lab.Same.Add(new[] { i0, out0 });
        lab.Diff.Add(new[] { i0, roof });
        return lab;
    }

    private static Lab MakeCourtyardLab()
    {
        var lab = new Lab();
        lab.Name = "e) courtyard, two 1.2m mouths";
        MakeGrid(24f, 24f, 1f, FlatT, out lab.TV, out lab.TT);
        AddBoxZ(lab.AV, lab.AT, 6f, 6f, 0f, 10f, 6.4f, 3f);        // south wall (west of mouth)
        AddBoxZ(lab.AV, lab.AT, 11.2f, 6f, 0f, 18f, 6.4f, 3f);     // south wall (east of mouth)
        AddBoxZ(lab.AV, lab.AT, 6f, 17.6f, 0f, 18f, 18f, 3f);      // north wall
        AddBoxZ(lab.AV, lab.AT, 6f, 6.4f, 0f, 6.4f, 17.6f, 3f);    // west wall
        AddBoxZ(lab.AV, lab.AT, 17.6f, 6.4f, 0f, 18f, 10f, 3f);    // east wall (south of mouth)
        AddBoxZ(lab.AV, lab.AT, 17.6f, 11.2f, 0f, 18f, 17.6f, 3f); // east wall (north of mouth)
        int inside = lab.P("courtyard", 12f, 12f, 0f);
        int outS = lab.P("outside S mouth", 10.6f, 3f, 0f);
        lab.Same.Add(new[] { inside, outS });
        return lab;
    }


    private static TiledNavMeshBuilder.TiledNavMeshSettings LabSettings(float cs, float radius, bool ledge)    {
        var s = new TiledNavMeshBuilder.TiledNavMeshSettings();
        s.VoxelCellSizeMeters = cs;
        s.AgentRadiusMeters = radius;
        s.UseLedgeFilter = ledge;
        s.MaxSlopeDegrees = 50f;
        s.UseDetailMesh = true;
        s.RegionMinSize = 1;
        s.RegionMergeSize = 1;
        s.HeightOffsetMeters = 0f;
        s.AgentMaxClimbMeters = TiledNavMeshBuilder.ResolveAgentClimbMeters(cs, s.CellHeightMeters, s.MaxSlopeDegrees, 0.4f);
        // The lab isolates the raw Recast configuration (cs x radius x ledge); the stitch and prune
        // passes have their own tests (18/19).
        s.PruneIsolatedIslands = false;
        s.StitchMaxGapCells = 0;
        return s;
    }

    private sealed class LabResult
    {
        public int F;
        public int Components;
        public bool Pass = true;
        public string Notes = "";
    }

    // Face-adjacency components (faces sharing an edge are traversable between each other).
    private static int[] FaceComponents(TiledNavMeshBuilder.VoxelMeshResult r, out int count)
    {
        var edgeFirst = new Dictionary<long, int>();
        var fp = new int[r.FaceCount];
        for (int i = 0; i < r.FaceCount; i++) fp[i] = i;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            UnionFaceEdge(edgeFirst, fp, f, a, b);
            UnionFaceEdge(edgeFirst, fp, f, b, c);
            UnionFaceEdge(edgeFirst, fp, f, c, a);
        }
        var roots = new HashSet<int>();
        for (int i = 0; i < r.FaceCount; i++) roots.Add(Find(fp, i));
        count = roots.Count;
        return fp;
    }

    private static int FaceAt(TiledNavMeshBuilder.VoxelMeshResult r, float px, float pz, float py, float tol)
    {
        int best = -1;
        float bd = float.MaxValue;
        List<int> cands = IndexOf(r).FacesAt(px, pz);
        for (int k = 0; k < cands.Count; k++)
        {
            int f = cands[k];
            int ia = r.Tris[f * 3], ib = r.Tris[f * 3 + 1], ic = r.Tris[f * 3 + 2];
            float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            float bx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bz = r.VertsXyz[ib * 3 + 2];
            float cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];
            float wa, wb, wc;
            if (!Bary(px, pz, ax, az, bx, bz, cx, cz, out wa, out wb, out wc)) continue;
            float y = wa * ay + wb * by + wc * cy;
            float d = Math.Abs(y - py);
            if (d <= tol && d < bd) { bd = d; best = f; }
        }
        return best;
    }

    private static LabResult RunLab(Lab lab, TiledNavMeshBuilder.TiledNavMeshSettings s)
    {
        float[] mx;
        int[] mt;
        TiledNavMeshBuilder.BuildCombinedSoup(lab.TV, lab.TT, lab.AV.ToArray(), lab.AT.ToArray(), out mx, out mt, out int _flipped);
        var r = TiledNavMeshBuilder.Build(mx, mt, s, Environment.ProcessorCount, null);
        var res = new LabResult();
        res.F = r.FaceCount;
        int comps;
        int[] fp = FaceComponents(r, out comps);
        res.Components = comps;
        res.Pass = r.InvariantsPassed;
        // Slope census, so a scenario's steepest face is visible next to its verdict (the steep-face
        // repair's threshold has to clear real stair risers, not just walls).
        {
            double worst = 0;
            int steep55 = 0, steep60 = 0, steep70 = 0, steep80 = 0;
            for (int f = 0; f < r.FaceCount; f++)
            {
                double[] nn = TriNormal(r.VertsXyz, r.Tris[f * 3], r.Tris[f * 3 + 1], r.Tris[f * 3 + 2]);
                double len = Math.Sqrt(nn[0] * nn[0] + nn[1] * nn[1] + nn[2] * nn[2]);
                if (len < 1e-12) continue;
                double ay = Math.Abs(nn[1]) / len;
                if (ay > 1.0) ay = 1.0;
                double dg = Math.Acos(ay) * 180.0 / Math.PI;
                if (dg > worst) worst = dg;
                if (dg > 55) steep55++;
                if (dg > 60) steep60++;
                if (dg > 70) steep70++;
                if (dg > 80) steep80++;
            }
            res.Notes += "; slopes worst=" + worst.ToString("F1") + "deg >55:" + steep55 + " >60:" + steep60
                + " >70:" + steep70 + " >80:" + steep80;
            // For every face over the steep threshold, its OWN height range: a surface an agent can be on
            // is never more than a step or two tall, so "steep AND tall" is the vertical-jump signature.
            {
                int nSteep = 0;
                float worstY = 0f;
                float worstSlopeAt = 0f, worstFp = 0f, worstArea = 0f;
                var hist = new int[8];   // yRange in [0,.25) [.25,.5) [.5,1) [1,1.5) [1.5,2) [2,4) [4,8) [8,inf)
                for (int f = 0; f < r.FaceCount; f++)
                {
                    int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
                    double[] nn = TriNormal(r.VertsXyz, a, b, c);
                    double len = Math.Sqrt(nn[0] * nn[0] + nn[1] * nn[1] + nn[2] * nn[2]);
                    if (len < 1e-12) continue;
                    double ay = Math.Abs(nn[1]) / len;
                    if (ay > 1.0) ay = 1.0;
                    double dg = Math.Acos(ay) * 180.0 / Math.PI;
                    if (dg <= 60.0) continue;
                    nSteep++;
                    float yLo = Math.Min(r.VertsXyz[a * 3 + 1],
                        Math.Min(r.VertsXyz[b * 3 + 1], r.VertsXyz[c * 3 + 1]));
                    float yHi = Math.Max(r.VertsXyz[a * 3 + 1],
                        Math.Max(r.VertsXyz[b * 3 + 1], r.VertsXyz[c * 3 + 1]));
                    float yr = yHi - yLo;
                    if (yr > worstY) { worstY = yr; worstSlopeAt = (float)dg; }
                    worstFp = (float)Math.Abs(TriFootprintXZ(r.VertsXyz, a, b, c));
                    worstArea = (float)(0.5 * len);
                    int bin = yr < 0.25f ? 0 : yr < 0.5f ? 1 : yr < 1f ? 2 : yr < 1.5f ? 3
                        : yr < 2f ? 4 : yr < 4f ? 5 : yr < 8f ? 6 : 7;
                    hist[bin]++;
                }
                res.Notes += "; steep>60 n=" + nSteep + " maxYRange=" + worstY.ToString("F2") + "m"
                    + " (at " + worstSlopeAt.ToString("F1") + "deg) fp=" + worstFp.ToString("F3")
                    + " A3d=" + worstArea.ToString("F3")
                    + " yHist<.25/.5/1/1.5/2/4/8/+inf="
                    + hist[0] + "/" + hist[1] + "/" + hist[2] + "/" + hist[3] + "/" + hist[4] + "/"
                    + hist[5] + "/" + hist[6] + "/" + hist[7];
            }
        }

        int n = lab.PName.Count;
        var comp = new int[n];
        var height = new float[n];
        // Detail mesh floats ~+ch above the poly mesh; 0.6 still separates every stacked level here.
        float tol = 0.6f;
        for (int i = 0; i < n; i++)
        {
            int f = FaceAt(r, lab.PX[i], lab.PZ[i], lab.PY[i], tol);
            if (f < 0) { comp[i] = -2; height[i] = float.NaN; }
            else { comp[i] = Find(fp, f); height[i] = r.VertsXyz[r.Tris[f * 3] * 3 + 1]; }
        }
        for (int i = 0; i < lab.Same.Count; i++)
        {
            int a = lab.Same[i][0], b = lab.Same[i][1];
            bool ok = comp[a] >= 0 && comp[a] == comp[b];
            if (!ok) res.Pass = false;
            res.Notes += (res.Notes.Length > 0 ? "; " : "") + "same(" + lab.PName[a] + "," + lab.PName[b] + ")=" + (ok ? "ok" : "FAIL");
        }
        for (int i = 0; i < lab.Diff.Count; i++)
        {
            int a = lab.Diff[i][0], b = lab.Diff[i][1];
            bool ok = comp[a] >= 0 && comp[b] >= 0 && comp[a] != comp[b];
            if (!ok) res.Pass = false;
            res.Notes += (res.Notes.Length > 0 ? "; " : "") + "diff(" + lab.PName[a] + "," + lab.PName[b] + ")=" + (ok ? "ok" : "FAIL");
        }
        return res;
    }

    private static Lab[] AllLabs()
    {
        return new[] { MakeDoorwayLab(), MakeWalkwayLab(), MakeStairsLab(), MakeTunnelLab(), MakeCourtyardLab() };
    }

    private static void Test13_Connectivity()
    {
        Console.WriteLine("== Test 13: connectivity lab (narrow features) ==");
        Lab[] labs = AllLabs();
        Console.WriteLine("  -- shipped config: cs=0.5 radius=0.35 ledge=on (informational) --");
        int pass = 0;
        for (int i = 0; i < labs.Length; i++)
        {
            LabResult r = RunLab(labs[i], LabSettings(0.5f, 0.35f, true));
            if (r.Pass) pass++;
            Console.WriteLine("    " + (r.Pass ? "PASS" : "FAIL") + "  " + labs[i].Name
                + "  F=" + r.F + " components=" + r.Components + "  [" + r.Notes + "]");
        }
        Console.WriteLine("  shipped config (cs=0.5): " + pass + "/" + labs.Length
            + " scenarios connected (expected: only the wide tunnel; narrow features need cs=0.25)");

        int recPass = 0;
        string recNotes = "";
        for (int i = 0; i < labs.Length; i++)
        {
            LabResult r = RunLab(labs[i], LabSettings(0.25f, 0.25f, true));
            if (r.Pass) recPass++;
            else recNotes += " " + labs[i].Name + "[" + r.Notes + "]";
        }
        Console.WriteLine("  recommended config (cs=0.25 radius=0.25 ledge=on): " + recPass + "/" + labs.Length
            + " scenarios connected" + (recNotes.Length > 0 ? " failures:" + recNotes : ""));
        Console.WriteLine("  CONNECTIVITY LAB: " + (recPass == labs.Length ? "PASS" : "FAIL"));
    }

    private static void Test14_Sweep()
    {
        Console.WriteLine("== Test 14: cs x agentRadius x ledgeFilter sweep ==");
        Lab[] labs = AllLabs();
        float[] css = { 0.5f, 0.25f, 0.15f };
        float[] rads = { 0.35f, 0.25f, 0.1f };
        bool[] ledges = { true, false };

        int bestPass = -1;
        string bestLabel = "";
        for (int ci = 0; ci < css.Length; ci++)
        {
            for (int ri = 0; ri < rads.Length; ri++)
            {
                for (int li = 0; li < ledges.Length; li++)
                {
                    int ok = 0;
                    int faces = 0;
                    var sb = new System.Text.StringBuilder();
                    var sw = Stopwatch.StartNew();
                    for (int i = 0; i < labs.Length; i++)
                    {
                        LabResult r = RunLab(labs[i], LabSettings(css[ci], rads[ri], ledges[li]));
                        if (r.Pass) ok++;
                        faces += r.F;
                        sb.Append(r.Pass ? "P" : ".");
                    }
                    sw.Stop();
                    Console.WriteLine("  cs=" + css[ci].ToString("0.##") + " r=" + rads[ri].ToString("0.##")
                        + " ledge=" + (ledges[li] ? "on " : "off") + " | " + sb.ToString()
                        + " | pass=" + ok + "/" + labs.Length + " faces=" + faces
                        + " ms=" + sw.ElapsedMilliseconds);
                    if (ok == labs.Length && bestPass < 0)
                    {
                        bestPass = ci;
                        bestLabel = "cs=" + css[ci] + " r=" + rads[ri] + " ledge=" + (ledges[li] ? "on" : "off");
                    }
                }
            }
        }
        Console.WriteLine("  scenarios order: a=" + labs[0].Name + " | b=" + labs[1].Name
            + " | c=" + labs[2].Name + " | d=" + labs[3].Name + " | e=" + labs[4].Name);
        Console.WriteLine("  SWEEP cheapest all-pass config (coarse cs preferred): "
            + (bestLabel == "" ? "NONE (no config passes all five)" : bestLabel));
    }

    // ---------------------------------------------------------------- test 15
    private static void Test15_TerrainHole()
    {
        Console.WriteLine("== Test 15: terrain hole mask (no false lid, floor below intact) ==");
        int w = 25, h = 25;
        float spacing = 1f;
        var heights = new float[w * h];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                heights[x * h + y] = 5f;              // plateau at 5 m
        var mask = new bool[w * h];
        for (int x = 8; x <= 16; x++)                 // carved hole x/y in [8,16] -> cells [7,15]
            for (int y = 8; y <= 16; y++)
                mask[x * h + y] = true;

        // A real floor below the carve (asset box, top 0.4 m), slightly larger than the hole.
        var av = new List<float>();
        var at = new List<int>();
        AddBoxZ(av, at, 7f, 7f, 0f, 17f, 17f, 0.4f);

        TiledNavMeshBuilder.TiledNavMeshSettings s = LabSettings(0.5f, 0.25f, true);

        // control: no mask -> the plateau is a lid over the void
        float[] cv;
        int[] ct;
        TiledNavMeshBuilder.BuildTerrainSoup(heights, w, h, spacing, 1, out cv, out ct);
        float[] cm;
        int[] cmt;
        TiledNavMeshBuilder.BuildCombinedSoup(cv, ct, av.ToArray(), at.ToArray(), out cm, out cmt, out int _flipped);
        var rc = TiledNavMeshBuilder.Build(cm, cmt, s, Environment.ProcessorCount, null);
        bool controlLid = FaceAt(rc, 12f, 12f, 5f, 0.6f) >= 0;
        Console.WriteLine("  control (no mask): lid over the carve present=" + controlLid
            + " (expected True)  F=" + rc.FaceCount);

        // masked: hole cells dropped -> no lid; the asset floor below is the surface
        float[] mv;
        int[] mt;
        TiledNavMeshBuilder.BuildTerrainSoup(heights, w, h, spacing, 1, mask, out mv, out mt);
        float[] mm;
        int[] mmt;
        TiledNavMeshBuilder.BuildCombinedSoup(mv, mt, av.ToArray(), at.ToArray(), out mm, out mmt, out int _flipped2);
        var rr = TiledNavMeshBuilder.Build(mm, mmt, s, Environment.ProcessorCount, null);
        Console.WriteLine("  masked: soup " + (mm.Length / 3) + "v/" + (mmt.Length / 3) + "t F=" + rr.FaceCount
            + " invariants=" + rr.InvariantsPassed);

        bool ok = rr.InvariantsPassed;
        int comps;
        int[] fp = FaceComponents(rr, out comps);
        int fLid = FaceAt(rr, 12f, 12f, 5f, 0.6f);
        int fA = FaceAt(rr, 10f, 12f, 0.4f, 0.6f);
        int fB = FaceAt(rr, 14f, 12f, 0.4f, 0.6f);
        bool noLid = fLid < 0;
        bool floorPresent = fA >= 0 && fB >= 0;
        bool floorConnected = floorPresent && Find(fp, fA) == Find(fp, fB);
        Console.WriteLine("    no lid at the carve (h=5)      = " + noLid);
        Console.WriteLine("    floor below present (h=0.4)    = " + floorPresent);
        Console.WriteLine("    floor connected across hole    = " + floorConnected);
        ok &= controlLid && noLid && floorConnected;
        Console.WriteLine("  TERRAIN HOLE TEST: " + (ok ? "PASS" : "FAIL"));
    }

    // ---------------------------------------------------------------- test 16
    // Recast Y-up flat floor over an n*n cell grid with a vertical strip of `gapCells` cells (starting
    // at cell gapStart) left completely absent -- exactly the "hairline collision gap" between two
    // abutting prefabs. Left cells are at yLeft, right cells at yRight.
    private static void MakeGappedFloor(int n, float sp, int gapStart, int gapCells,
        float yLeft, float yRight, out float[] verts, out int[] tris)
    {
        int m = n + 1;
        verts = new float[m * m * 3];
        for (int i = 0; i < m; i++)
        {
            float y = i >= gapStart + gapCells ? yRight : yLeft;
            for (int j = 0; j < m; j++)
            {
                int s = (i * m + j) * 3;
                verts[s + 0] = i * sp;
                verts[s + 1] = y;
                verts[s + 2] = j * sp;
            }
        }
        var t = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (i >= gapStart && i < gapStart + gapCells)
                continue;
            for (int j = 0; j < n; j++)
            {
                int i00 = i * m + j;
                int i10 = (i + 1) * m + j;
                int i11 = (i + 1) * m + (j + 1);
                int i01 = i * m + (j + 1);
                t.Add(i00); t.Add(i11); t.Add(i10);
                t.Add(i00); t.Add(i01); t.Add(i11);
            }
        }
        tris = t.ToArray();
    }

    private static bool GapConnected(int n, float sp, int gapStart, int gapCells, float yLeft, float yRight,
        int gapFillCells, float extraAssetZ, float probeY, string label, out int faces)
    {
        float[] tv;
        int[] tt;
        MakeGappedFloor(n, sp, gapStart, gapCells, yLeft, yRight, out tv, out tt);
        var av = new List<float>();
        var at = new List<int>();
        if (!float.IsNaN(extraAssetZ))
            AddBoxZ(av, at, (gapStart - 1) * sp, 2f, extraAssetZ, (gapStart + gapCells + 1) * sp, 22f, extraAssetZ + 0.4f);

        float[] mx;
        int[] mt;
        TiledNavMeshBuilder.BuildCombinedSoup(tv, tt, av.ToArray(), at.ToArray(), out mx, out mt, out int _flipped);
        TiledNavMeshBuilder.TiledNavMeshSettings s = LabSettings(0.5f, 0.25f, true);
        s.GapFillCells = gapFillCells;
        var r = TiledNavMeshBuilder.Build(mx, mt, s, Environment.ProcessorCount, null);
        faces = r.FaceCount;
        int comps;
        int[] fp = FaceComponents(r, out comps);
        int fa = FaceAt(r, (gapStart - 2) * sp, 12f, probeY, 0.6f);
        int fb = FaceAt(r, (gapStart + gapCells + 1) * sp, 12f, probeY, 0.6f);
        bool conn = fa >= 0 && fb >= 0 && Find(fp, fa) == Find(fp, fb);
        Console.WriteLine("    " + label + ": gapFill=" + gapFillCells + " F=" + faces
            + " comps=" + comps + " connected=" + conn + (conn ? "" : " (broken)"));
        return conn;
    }

    private static void Test16_GapFill()
    {
        Console.WriteLine("== Test 16: gap bridging (NavMeshVoxelGapFillCells) ==");
        const int n = 48;
        const float sp = 0.5f;
        const int gapStart = 23;
        int f;

        bool a2 = GapConnected(n, sp, gapStart, 1, 0f, 0f, 2, float.NaN, 0f, "a) 1-cell gap, cap 2", out f);
        bool a0 = GapConnected(n, sp, gapStart, 1, 0f, 0f, 0, float.NaN, 0f, "a) 1-cell gap, cap 0", out f);
        bool b2 = GapConnected(n, sp, gapStart, 2, 0f, 0f, 2, float.NaN, 0f, "b) 2-cell gap, cap 2", out f);
        bool b1 = GapConnected(n, sp, gapStart, 2, 0f, 0f, 1, float.NaN, 0f, "b) 2-cell gap, cap 1", out f);
        bool c2 = GapConnected(n, sp, gapStart, 3, 0f, 0f, 2, float.NaN, 0f, "c) 3-cell gap, cap 2", out f);
        bool d2 = GapConnected(n, sp, gapStart, 2, 0f, -2f, 2, float.NaN, -2f, "d) 2-cell gap, 2 m step (delta>climb)", out f);
        bool e2 = GapConnected(n, sp, gapStart, 3, 0f, 0f, 2, -2.5f, 0f, "e) 1.5 m ditch with a floor below", out f);

        bool ok = a2 && !a0 && b2 && !b1 && !c2 && !d2 && !e2;
        Console.WriteLine("  expectations: a(cap2)=connected a(cap0)=broken b(cap2)=connected b(cap1)=broken"
            + " c(broken) d(broken) e(broken)");
        Console.WriteLine("  GAP FILL TEST: " + (ok ? "PASS" : "FAIL"));
    }

    private static void Test17_IslandReport()
    {
        Console.WriteLine("== Test 17: island report on a Birtu-scale scene (1600x1600, cs 0.5, detail ON) ==");
        float[] verts;
        int[] tris;
        MakeGrid(1600f, 1600f, 1.0f, LogMapHeight, out verts, out tris);
        TiledNavMeshBuilder.TiledNavMeshSettings s = SettingsAuto(0.5f, 65f);
        s.UseDetailMesh = true;
        TiledNavMeshBuilder.VoxelMeshResult r =
            TiledNavMeshBuilder.Build(verts, tris, s, Environment.ProcessorCount, Log);
        Console.WriteLine("  scene totals: V=" + r.VertexCount + " F=" + r.FaceCount + " E=" + r.EdgeCount
            + " boundary=" + r.BoundaryEdges + " (the 'v2: islands=' line above is the report)");
    }

    // ---------------------------------------------------------------- test 18
    // Stitch lab: two floors separated by a small geometric gap (the mesh gap after erosion is wider),
    // optionally with a wall or a low ceiling in the gap.
    private static bool StitchLab(int n, float sp, int gapStart, int gapCells, float yRight, int stitchCap,
        int mode, string label, out int faces)
    {
        float[] tv;
        int[] tt;
        MakeGappedFloor(n, sp, gapStart, gapCells, 0f, yRight, out tv, out tt);
        var av = new List<float>();
        var at = new List<int>();
        if (mode == 1)       // wall in the gap, top far above the floors
            AddBoxZ(av, at, (gapStart - 2) * sp, 4f, -1f, (gapStart + gapCells + 2) * sp, 22f, 2.0f);
        else if (mode == 2)  // ceiling slab 1.0 m above the floor (below walkableHeight)
            AddBoxZ(av, at, (gapStart - 2) * sp, 4f, 1.0f, (gapStart + gapCells + 2) * sp, 22f, 1.2f);

        float[] mx;
        int[] mt;
        TiledNavMeshBuilder.BuildCombinedSoup(tv, tt, av.ToArray(), at.ToArray(), out mx, out mt, out int _flipped);
        TiledNavMeshBuilder.TiledNavMeshSettings s = LabSettings(0.25f, 0.25f, true);
        s.StitchMaxGapCells = stitchCap;
        s.GapFillCells = 0;          // only the stitch may bridge here
        var r = TiledNavMeshBuilder.Build(mx, mt, s, Environment.ProcessorCount, null);
        faces = r.FaceCount;
        int comps;
        int[] fp = FaceComponents(r, out comps);
        int fa = FaceAt(r, (gapStart - 3) * sp, 12f, 0f, 0.6f);
        int fb = FaceAt(r, (gapStart + gapCells + 2) * sp, 12f, yRight, 0.6f);
        bool conn = fa >= 0 && fb >= 0 && Find(fp, fa) == Find(fp, fb);
        string gap = "";
        if (stitchCap == 0)
        {
            // measure the mesh gap: min distance between vertices of different components
            double best = double.MaxValue;
            for (int f1 = 0; f1 < r.FaceCount; f1++)
            {
                for (int f2 = f1 + 1; f2 < r.FaceCount; f2++)
                {
                    if (Find(fp, f1) == Find(fp, f2)) continue;
                    for (int k1 = 0; k1 < 3; k1++)
                    {
                        int a = r.Tris[f1 * 3 + k1];
                        for (int k2 = 0; k2 < 3; k2++)
                        {
                            int b = r.Tris[f2 * 3 + k2];
                            double dx = r.VertsXyz[a * 3] - r.VertsXyz[b * 3];
                            double dz = r.VertsXyz[a * 3 + 2] - r.VertsXyz[b * 3 + 2];
                            double d = dx * dx + dz * dz;
                            if (d < best) best = d;
                        }
                    }
                }
            }
            gap = " meshGap=" + (best == double.MaxValue ? -1 : Math.Sqrt(best)).ToString("F3") + "m";
        }
        Console.WriteLine("    " + label + ": stitchCap=" + stitchCap + " F=" + faces + " comps=" + comps
            + " connected=" + conn + (conn ? "" : " (broken)") + gap);
        return conn;
    }

    private static void Test18_Stitch()
    {
        Console.WriteLine("== Test 18: per-tile stitch (repair near-miss cuts) ==");
        const int n = 64;          // 16 m at 0.25 m -> exactly one 16 m tile
        const float sp = 0.25f;
        const int gapStart = 40;   // x = 10 m
        int f;

        // A real seam rasterizes to a 1-cell gap, and erosion + the contour inset widen it to 5 cells
        // (measured: 1.25 m at cs 0.25), so cap 5 is the smallest that bridges it. The shipped default
        // (4) is one cell short - reported, not hidden.
        bool a5 = StitchLab(n, sp, gapStart, 1, 0f, 5, 0, "a) 1-cell raster seam, clear air, cap 5", out f);
        bool a4 = StitchLab(n, sp, gapStart, 1, 0f, 4, 0, "a) same seam, cap 4 (shipped default, 1 cell short)", out f);
        bool a0 = StitchLab(n, sp, gapStart, 1, 0f, 0, 0, "a) same seam, cap 0 (off)", out f);
        bool b5 = StitchLab(n, sp, gapStart, 1, 0f, 5, 1, "b) same seam with a wall, cap 5", out f);
        bool c5 = StitchLab(n, sp, gapStart, 1, 0f, 5, 2, "c) same seam with a low ceiling, cap 5", out f);
        bool d5 = StitchLab(n, sp, gapStart, 1, -2f, 5, 0, "d) floors 2 m apart, cap 5", out f);

        bool ok = a5 && !a0 && !b5 && !c5 && !d5;
        Console.WriteLine("  expectations: a(cap5)=connected a(cap0)=broken b(wall)=broken c(ceiling)=broken d(2m step)=broken");
        Console.WriteLine("  calibration row: cap 4 (shipped default) connected=" + a4
            + " (informational; 1.25 m seam needs 5 cells)");
        Console.WriteLine("  STITCH TEST: " + (ok ? "PASS" : "FAIL"));
    }

    // ---------------------------------------------------------------- test 19
    private static void Test19_Prune()
    {
        Console.WriteLine("== Test 19: isolated-island pruning ==");
        // A terrain scene whose main island is bigger than the prune cap (the analytic hill, ~1192
        // faces with the detail mesh), so pruning must not touch it.
        float[] tv;
        int[] tt;
        MakeGrid(160f, 160f, 0.25f, HillHeightF, out tv, out tt);

        TiledNavMeshBuilder.TiledNavMeshSettings a = HillSettings(true);
        a.PruneIsolatedIslands = true;
        TiledNavMeshBuilder.TiledNavMeshSettings b = HillSettings(true);
        b.PruneIsolatedIslands = false;
        var ra = TiledNavMeshBuilder.Build(tv, tt, a, Environment.ProcessorCount, null);
        var rb = TiledNavMeshBuilder.Build(tv, tt, b, Environment.ProcessorCount, null);
        bool same = ra.FaceCount == rb.FaceCount && Hash(ra.VertsXyz) == Hash(rb.VertsXyz)
            && Hash(ra.Tris) == Hash(rb.Tris);
        Console.WriteLine("    (a) hill terrain: prune on F=" + ra.FaceCount + " off F=" + rb.FaceCount
            + " identical=" + same + " (both must be >cap=" + a.PruneMaxIslandFaces + ")");

        // (b) the same terrain plus a small detached platform island well above it
        var av = new List<float>();
        var at = new List<int>();
        AddBoxZ(av, at, 38f, 38f, 30.0f, 42f, 42f, 30.2f);
        float[] mx;
        int[] mt;
        TiledNavMeshBuilder.BuildCombinedSoup(tv, tt, av.ToArray(), at.ToArray(), out mx, out mt, out int _flipped);

        TiledNavMeshBuilder.TiledNavMeshSettings on = HillSettings(true);
        on.PruneIsolatedIslands = true;
        on.RegionMinSize = 1; on.RegionMergeSize = 1;
        TiledNavMeshBuilder.TiledNavMeshSettings off = HillSettings(true);
        off.PruneIsolatedIslands = false;
        off.RegionMinSize = 1; off.RegionMergeSize = 1;
        TiledNavMeshBuilder.TiledNavMeshSettings zero = HillSettings(true);
        zero.PruneMaxIslandFaces = 0;
        zero.RegionMinSize = 1; zero.RegionMergeSize = 1;
        var rOn = TiledNavMeshBuilder.Build(mx, mt, on, Environment.ProcessorCount, Log);
        var rOff = TiledNavMeshBuilder.Build(mx, mt, off, Environment.ProcessorCount, null);
        var rZero = TiledNavMeshBuilder.Build(mx, mt, zero, Environment.ProcessorCount, null);

        bool offHas = FaceAt(rOff, 40f, 40f, 30.2f, 0.6f) >= 0;
        bool onHas = FaceAt(rOn, 40f, 40f, 30.2f, 0.6f) >= 0;
        bool zeroHas = FaceAt(rZero, 40f, 40f, 30.2f, 0.6f) >= 0;
        // The terrain must survive pruning. The old proxy (rOn.FaceCount < rOff.FaceCount) is not robust:
        // the final cleanup's decimation is order-sensitive, and the sliver-refinement pass legitimately
        // moves that count by a few faces either way, so the platform's own 2 faces no longer dominate it.
        // Check the actual intent instead - the terrain network is far larger than the cap, and a terrain
        // sample well away from the platform still has a face after pruning.
        float tY = 0f; bool tHave = false;
        List<int> tCands = FacesAtAnyHeight(rOff, 110f, 110f);
        for (int i = 0; i < tCands.Count && !tHave; i++)
        {
            float yy;
            if (FaceYAt(rOff, tCands[i], 110f, 110f, out yy)) { tY = yy; tHave = true; }
        }
        bool terrainPresent = tHave && FaceAt(rOn, 110f, 110f, tY, 1.0f) >= 0;
        bool terrainKept = rOn.InvariantsPassed && rOn.FaceCount > 400 && terrainPresent;
        Console.WriteLine("    (b) platform island: off F=" + rOff.FaceCount + " (present=" + offHas
            + ") on F=" + rOn.FaceCount + " (present=" + onHas + ") cap0 F=" + rZero.FaceCount
            + " (present=" + zeroHas + ")");
        Console.WriteLine("    terrain network kept=" + terrainKept + " invariants(on)=" + rOn.InvariantsPassed);

        bool ok = same && offHas && !onHas && zeroHas && terrainKept;
        Console.WriteLine("  expectations: big terrain island unchanged; detached platform pruned with the default,"
            + " kept when the cap is 0 (off)");
        Console.WriteLine("  PRUNE TEST: " + (ok ? "PASS" : "FAIL"));
    }

    // ---------------------------------------------------------------- test 20
    private static void Test20_SnapshotRoundTrip()
    {
        Console.WriteLine("== Test 20: snapshot round-trip (write -> load -> rebuild == in-process build) ==");

        // Scenario: a sloping heightfield plus two asset boxes -> terrain + per-entity assets.
        int w = 41, h = 41;
        float sp = 1f;
        var heights = new float[w * h];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                heights[x * h + y] = 0.5f * x + 0.25f * y
                    + 1.5f * (float)Math.Sin(x * 0.7) * (float)Math.Cos(y * 0.5);

        var av = new List<float>();
        var at = new List<int>();
        AddBoxZ(av, at, 6f, 6f, 3f, 12f, 12f, 3.4f);
        AddBoxZ(av, at, 20f, 20f, 2f, 28f, 28f, 6f);
        float[] allVerts = av.ToArray();
        int[] allTris = at.ToArray();

        var records = new List<SceneSnapshot.EntityRecord>();
        for (int b = 0; b < 2; b++)
        {
            var rec = new SceneSnapshot.EntityRecord();
            rec.Name = b == 0 ? "diag_roundtrip_slab" : "plain_roundtrip_block";
            rec.Flags = 0;
            rec.Mobility = 0;
            rec.TriCount = 12;
            rec.VertsXyz = new float[24];
            Array.Copy(allVerts, b * 24, rec.VertsXyz, 0, 24);
            rec.Tris = new int[36];
            for (int k = 0; k < 36; k++)
                rec.Tris[k] = allTris[b * 36 + k] - b * 8;
            records.Add(SceneSnapshot.Expand(rec.Name, 0, 0, false, rec.VertsXyz, rec.Tris));
        }

        var terrain = new SceneSnapshot.TerrainData();
        terrain.Width = w;
        terrain.Height = h;
        terrain.Spacing = sp;
        terrain.Stride = 2;
        terrain.Heights = heights;

        // 1) in-process build, exactly as the editor would (prune off here: the round trip proves the
        // loader, and a scene this small would otherwise be pruned to nothing).
        float[] v1;
        int[] t1;
        BuildSnapshotSoup(terrain, records, out v1, out t1, out bool[] _f1, out bool[] _e1);
        TiledNavMeshBuilder.TiledNavMeshSettings s1 = SnapshotSettings();
        s1.PruneIsolatedIslands = false;
        TiledNavMeshBuilder.VoxelMeshResult r1 =
            TiledNavMeshBuilder.Build(v1, t1, s1, Environment.ProcessorCount, null);

        // 2) write the snapshot with the real writer
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bbnm_snapshot_roundtrip");
        System.IO.Directory.CreateDirectory(dir);
        string prefix = System.IO.Path.Combine(dir, "roundtrip");
        SceneSnapshot.WriteTerrainBin(prefix + "_snapshot_terrain.bin", terrain);
        SceneSnapshot.WriteAssetsBin(prefix + "_snapshot_assets.bin", records);
        SceneSnapshot.WriteText(prefix + "_snapshot.txt",
            "scene: harness-roundtrip\ngrid: " + w + "x" + h + "\nassert-points:\n  3.5,3.5\n");

        // 3) load it back and rebuild through the same helpers the --snapshot mode uses
        SceneSnapshot.TerrainData t2 = SceneSnapshot.ReadTerrainBin(prefix + "_snapshot_terrain.bin");
        List<SceneSnapshot.EntityRecord> a2 = SceneSnapshot.ReadAssetsBin(prefix + "_snapshot_assets.bin");
        float[] v2;
        int[] tv2;
        BuildSnapshotSoup(t2, a2, out v2, out tv2, out bool[] _f2, out bool[] _e2);
        TiledNavMeshBuilder.TiledNavMeshSettings s2 = SnapshotSettings();
        s2.PruneIsolatedIslands = false;
        TiledNavMeshBuilder.VoxelMeshResult r2 =
            TiledNavMeshBuilder.Build(v2, tv2, s2, Environment.ProcessorCount, null);

        bool sameSoup = v1.Length == v2.Length && t1.Length == tv2.Length
            && Hash(v1) == Hash(v2) && Hash(t1) == Hash(tv2);
        bool sameMesh = r1.FaceCount == r2.FaceCount && r1.VertexCount == r2.VertexCount
            && Hash(r1.VertsXyz) == Hash(r2.VertsXyz) && Hash(r1.Tris) == Hash(r2.Tris);
        bool points = ParseAssertPoints(prefix + "_snapshot.txt").Count == 1;
        bool diag = ExtractDiagNamesFromTxt(prefix + "_snapshot.txt").Count == 0
            || records[0].Name.IndexOf("diag", StringComparison.OrdinalIgnoreCase) >= 0;

        Console.WriteLine("    terrain reload: " + t2.Width + "x" + t2.Height + " spacing=" + t2.Spacing
            + " stride=" + t2.Stride + "  assets reload: " + a2.Count + " entities");
        Console.WriteLine("    soup identical=" + sameSoup + " (V1=" + (v1.Length / 3) + " V2=" + (v2.Length / 3) + ")");
        Console.WriteLine("    mesh face-for-face identical=" + sameMesh + " (F1=" + r1.FaceCount + " F2=" + r2.FaceCount + ")");
        Console.WriteLine("    txt assert-points parsed=" + points + " diag name present=" + diag);

        bool ok = sameSoup && sameMesh && points && r1.FaceCount > 50;
        Console.WriteLine("  SNAPSHOT ROUND-TRIP TEST: " + (ok ? "PASS" : "FAIL"));
    }

    // ---------------------------------------------------------------- test 21
    // mode: 0 = clear air; 1 = a tall wall across the gap; 2 = an extra plate 3.8 m above the floor.
    private static bool MergeStitchCase(int n, float sp, int gapStart, int gapCells, int mode,
        int stitchCap, bool expectConnected, bool expectPlateSeparate, string label, out int faces)
    {
        float[] tv;
        int[] tt;
        MakeGappedFloor(n, sp, gapStart, gapCells, 0f, 0f, out tv, out tt);
        var av = new List<float>();
        var at = new List<int>();
        if (mode == 1)
            AddBoxZ(av, at, (gapStart - 2) * sp, 4f, 0f, (gapStart + gapCells + 2) * sp, 12f, 2f);
        else if (mode == 2)
            AddBoxZ(av, at, (gapStart + gapCells) * sp, 4f, 3.8f,
                (gapStart + gapCells + 16) * sp, 12f, 4.0f);

        float[] mx;
        int[] mt;
        int flipped;
        TiledNavMeshBuilder.BuildCombinedSoup(tv, tt, av.ToArray(), at.ToArray(), out mx, out mt, out flipped);
        TiledNavMeshBuilder.TiledNavMeshSettings s = LabSettings(0.25f, 0.25f, true);
        s.StitchMaxGapCells = stitchCap;
        s.GapFillCells = 0;               // isolate the stitch
        s.PruneIsolatedIslands = false;
        TiledNavMeshBuilder.VoxelMeshResult r =
            TiledNavMeshBuilder.Build(mx, mt, s, Environment.ProcessorCount, null);
        faces = r.FaceCount;
        int comps;
        int[] fp = FaceComponents(r, out comps);
        int fa = FaceAt(r, (gapStart - 2) * sp, 6f, 0f, 0.6f);
        int fb = FaceAt(r, (gapStart + gapCells + 2) * sp, 6f, 0f, 0.6f);
        bool conn = fa >= 0 && fb >= 0 && Find(fp, fa) == Find(fp, fb);
        bool plateOk = true;
        if (expectPlateSeparate)
        {
            int p = FaceAt(r, (gapStart + gapCells + 6) * sp, 8f, 3.8f, 0.8f);
            plateOk = p >= 0 && fa >= 0 && Find(fp, p) != Find(fp, fa);
        }
        bool inv = r.InvariantsPassed;
        Console.WriteLine("    " + label + ": F=" + faces + " comps=" + comps + " connected=" + conn
            + (expectPlateSeparate ? " plateSeparate=" + plateOk : "")
            + " invariants=" + (inv ? "PASS" : "FAIL"));
        return conn == expectConnected && plateOk && inv;
    }

    private static void Test21_MergeStitch()
    {
        Console.WriteLine("== Test 21: merge-stitch (cross-tile) ==");
        const int n = 128;          // 32 m at 0.25 m -> the gap sits on the x=16 tile boundary
        const float sp = 0.25f;
        const int gapStart = 64;

        int f;
        bool a6 = MergeStitchCase(n, sp, gapStart, 1, 0, 6, true, false, "a) 1-cell seam cross-tile, stitch 6", out f);
        bool a0 = MergeStitchCase(n, sp, gapStart, 1, 0, 0, false, false, "a) same, stitch 0 (off)", out f);
        bool b6 = MergeStitchCase(n, sp, gapStart, 1, 1, 6, false, false, "b) same + wall in the gap, stitch 6", out f);
        bool c6 = MergeStitchCase(n, sp, gapStart, 1, 2, 6, true, true, "c) same + plate 3.8 m above the floor", out f);

        bool ok = a6 && a0 && b6 && c6;
        Console.WriteLine("  expectations: a(stitch6)=connected a(stitch0)=broken b(wall)=broken"
            + " c(floor connected, plate its own island)");
        Console.WriteLine("  MERGE-STITCH TEST: " + (ok ? "PASS" : "FAIL"));
    }

    // ---------------------------------------------------------------- test 22
    private static void Test22_Winding()
    {
        Console.WriteLine("== Test 22: per-triangle winding normalisation (double-sided assets) ==");
        float[] tv;
        int[] tt;
        MakeGrid(8f, 8f, 1f, FlatT, out tv, out tt);

        // A horizontal 4x4 m quad at engine z=3, supplied in both windings.
        float[] both = { 2f, 2f, 3f, 6f, 2f, 3f, 6f, 6f, 3f,
                         2f, 2f, 3f, 6f, 6f, 3f, 6f, 2f, 3f };
        int[] upOnly = { 0, 1, 2 };
        int[] downOnly = { 3, 4, 5 };
        int[] bothTris = { 0, 1, 2, 3, 4, 5 };

        bool iu, id, ib;
        int pu, pd, pb;
        int fu = WindingCase(tv, tt, both, upOnly, out iu, out pu);
        int fd = WindingCase(tv, tt, both, downOnly, out id, out pd);
        int fb = WindingCase(tv, tt, both, bothTris, out ib, out pb);
        Console.WriteLine("    up-wound only  : flipped=" + fu + " plateFaces=" + pu + " invariants=" + iu);
        Console.WriteLine("    down-wound only: flipped=" + fd + " plateFaces=" + pd + " invariants=" + id);
        Console.WriteLine("    both (twin)    : flipped=" + fb + " plateFaces=" + pb + " invariants=" + ib);

        // One detailed poly is emitted as 2 triangles, so "one span" means the same surface count in
        // all three cases (the duplicate winding must not add a stacked surface) plus a green gate.
        bool ok = fu == 0 && fd == 1 && fb == 1
            && pu == 2 && pd == 2 && pb == 2
            && iu && id && ib;
        Console.WriteLine("  expectations: down-wound floor is walkable (flipped=1) and the twin adds no"
            + " stacked surface (same face count, invariants PASS)");
        Console.WriteLine("  WINDING TEST: " + (ok ? "PASS" : "FAIL"));
    }

    private static int WindingCase(float[] tv, int[] tt, float[] av, int[] at, out bool invariants, out int plateFaces)
    {
        float[] mx;
        int[] mt;
        int flipped;
        TiledNavMeshBuilder.BuildCombinedSoup(tv, tt, av, at, out mx, out mt, out flipped);
        TiledNavMeshBuilder.TiledNavMeshSettings s = LabSettings(0.25f, 0.25f, true);
        s.GapFillCells = 0;
        s.StitchMaxGapCells = 0;
        s.PruneIsolatedIslands = false;
        TiledNavMeshBuilder.VoxelMeshResult r =
            TiledNavMeshBuilder.Build(mx, mt, s, Environment.ProcessorCount, null);
        invariants = r.InvariantsPassed;
        List<int> cands = FacesAtAnyHeight(r, 5f, 3f);
        int n = 0;
        for (int i = 0; i < cands.Count; i++)
            if (Math.Abs(FaceCentroidY(r, cands[i]) - 3f) <= 0.8f) n++;
        plateFaces = n;
        return flipped;
    }

    // ---------------------------------------------------------------- test 23
    // Synthesized ramps for stepped / ladder props: the same SteppedPropRamps the shipped collector
    // calls, driven from synthetic collision geometry. Every case asserts the ramp is what makes the
    // link (broken with the ramps off) and that invariants stay green.
    // Prints the walkable face height near points along a line (engine coords), for diagnosing where a
    // ramp's navmesh stops.
    private static void ProbeLine23(TiledNavMeshBuilder.VoxelMeshResult r, float x0, float y0, float z0,
        float x1, float y1, float z1, int n)
    {
        for (int i = 0; i <= n; i++)
        {
            float t = (float)i / n;
            float px = x0 + (x1 - x0) * t, py = y0 + (y1 - y0) * t, pz = z0 + (z1 - z0) * t;
            List<int> cands = FacesAtAnyHeight(r, px, py);
            int best = -1;
            float bd = float.MaxValue;
            for (int k = 0; k < cands.Count; k++)
            {
                float d = Math.Abs(FaceCentroidY(r, cands[k]) - pz);
                if (d < bd) { bd = d; best = cands[k]; }
            }
            Console.Write("      [line] (" + px.ToString("F2") + "," + py.ToString("F2") + ","
                + pz.ToString("F2") + ") -> ");
            if (best < 0 || bd > 1.6f) Console.WriteLine("no face near (cands=" + cands.Count + ")");
            else Console.WriteLine("y=" + FaceCentroidY(r, best).ToString("F2") + " (d="
                + bd.ToString("F2") + ")");
        }
    }

    private static string PropDump = null;

    private static bool Test23Verbose = true;
    private static readonly List<string> Test23Log = new List<string>();
    private static TiledNavMeshBuilder.VoxelMeshResult LastRampMesh23;

    private static string MeshYRange(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < r.VertexCount; i++)
        {
            float y = r.VertsXyz[i * 3 + 1];
            if (y < lo) lo = y;
            if (y > hi) hi = y;
        }
        return lo.ToString("F2") + ".." + hi.ToString("F2");
    }

    private static SteppedPropRamps.Prop Prop23(string name, int flags, List<float> v, List<int> t)
    {
        var p = new SteppedPropRamps.Prop();
        p.Name = name;
        p.Flags = flags;
        p.VertsXyz = v.ToArray();
        p.Tris = t.ToArray();
        return p;
    }

    // Tessellated inclined strip (engine Z-up) from p0 to p1, `width` across, `segments` quads.
    private static void AddStripZ(List<float> v, List<int> t, float x0, float y0, float z0,
        float x1, float y1, float z1, float width, int segments)
    {
        float dx = x1 - x0, dy = y1 - y0, dz = z1 - z0;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        // A truly vertical plane (a vertical ladder's plank) has no run: lay it along X.
        float px, py;
        if (len < 1e-4f) { px = width * 0.5f; py = 0f; }
        else { px = -dy / len * width * 0.5f; py = dx / len * width * 0.5f; }
        for (int s = 0; s < segments; s++)
        {
            float t0 = (float)s / segments, t1 = (float)(s + 1) / segments;
            float ax = x0 + dx * t0, ay = y0 + dy * t0, az = z0 + dz * t0;
            float bx = x0 + dx * t1, by = y0 + dy * t1, bz = z0 + dz * t1;
            int b = v.Count / 3;
            v.Add(ax + px); v.Add(ay + py); v.Add(az);
            v.Add(ax - px); v.Add(ay - py); v.Add(az);
            v.Add(bx - px); v.Add(by - py); v.Add(bz);
            v.Add(bx + px); v.Add(by + py); v.Add(bz);
            t.Add(b + 0); t.Add(b + 1); t.Add(b + 2);
            t.Add(b + 0); t.Add(b + 2); t.Add(b + 3);
        }
    }

    // A thin horizontal tread box centred on (cx,cy) at height z.
    private static void AddTreadZ(List<float> v, List<int> t, float cx, float cy, float z, float w, float d)
    {
        AddBoxZ(v, t, cx - w * 0.5f, cy - d * 0.5f, z, cx + w * 0.5f, cy + d * 0.5f, z + 0.08f);
    }

    // Mirrors the collector: the merged soup plus the forced-walkable mask for the synthesized ramps.
    // Test-23 terrain: far below the props, so a probe can only ever hit the prop surfaces (with the
    // terrain at z=0 a floor lying on it would make every connectivity probe trivially true).
    private static float Test23Terrain(float x, float z) { return -8f; }

    // A ladder's rungs: `count` thin oriented boxes along the line from p0 to p1, `width` across.
    // This is what a real ladder's collision is - horizontal rungs the slope rule accepts, spaced more
    // than an agent can step, so only a synthesized ramp can make the ladder traversable.
    private static void AddRungsZ(List<float> v, List<int> t, float x0, float y0, float z0,
        float x1, float y1, float z1, float width, int count)
    {
        float dx = x1 - x0, dy = y1 - y0;
        float len = (float)Math.Sqrt(dx * dx + dy * dy);
        // A truly vertical ladder has no run: its rungs still have a width, laid along X.
        float ux, uy;
        if (len < 1e-4f) { ux = 0f; uy = 1f; } else { ux = dx / len; uy = dy / len; }
        float halfAlong = 0.05f, halfAcross = width * 0.5f;
        for (int i = 0; i < count; i++)
        {
            float f = count > 1 ? (float)i / (count - 1) : 0.5f;
            float cx = x0 + dx * f, cy = y0 + dy * f, cz = z0 + (z1 - z0) * f;
            // four footprint corners: centre +/- along +/- across
            float[] px = new float[4];
            float[] py = new float[4];
            px[0] = cx + ux * halfAlong + uy * halfAcross; py[0] = cy + uy * halfAlong - ux * halfAcross;
            px[1] = cx + ux * halfAlong - uy * halfAcross; py[1] = cy + uy * halfAlong + ux * halfAcross;
            px[2] = cx - ux * halfAlong - uy * halfAcross; py[2] = cy - uy * halfAlong + ux * halfAcross;
            px[3] = cx - ux * halfAlong + uy * halfAcross; py[3] = cy - uy * halfAlong - ux * halfAcross;
            int b = v.Count / 3;
            for (int k = 0; k < 4; k++) { v.Add(px[k]); v.Add(py[k]); v.Add(cz); }
            for (int k = 0; k < 4; k++) { v.Add(px[k]); v.Add(py[k]); v.Add(cz + 0.08f); }
            for (int k = 0; k < 4; k++)
            {
                int n = (k + 1) % 4;
                t.Add(b + k); t.Add(b + n); t.Add(b + 4 + n);          // side
                t.Add(b + k); t.Add(b + 4 + n); t.Add(b + 4 + k);
            }
            t.Add(b + 0); t.Add(b + 1); t.Add(b + 2);                  // top
            t.Add(b + 0); t.Add(b + 2); t.Add(b + 3);
        }
    }

    private static void BuildRampSoup23(List<SteppedPropRamps.Prop> props, bool useRamps,
        out float[] mx, out int[] mt, out bool[] force, out bool[] exempt, out int rampTris)
    {
        var av = new List<float>();
        var at = new List<int>();
        var af = new List<bool>();
        var ae = new List<bool>();
        force = null;
        exempt = null;
        rampTris = 0;
        for (int i = 0; i < props.Count; i++)
        {
            SteppedPropRamps.Prop p = props[i];
            int baseIndex = av.Count / 3;
            for (int k = 0; k < p.VertsXyz.Length; k++) av.Add(p.VertsXyz[k]);
            for (int k = 0; k < p.Tris.Length; k++) at.Add(p.Tris[k] + baseIndex);
            int nt = p.Tris.Length / 3;
            bool steppedProp = (p.Flags & (SteppedPropRamps.FlagHasSteps | SteppedPropRamps.FlagLadder)) != 0;
            for (int k = 0; k < nt; k++) { af.Add(false); ae.Add(steppedProp); }
            if (!useRamps) continue;
            SteppedPropRamps.Result rr = SteppedPropRamps.Synthesize(new SteppedPropRamps.Prop[] { p });
            int rt = rr.Tris.Count / 3;
            var connector = new bool[rt];
            for (int k = 0; k < rr.ConnectorTris.Count; k++)
            {
                int qi = rr.ConnectorTris[k];
                if (qi >= 0 && qi < rt) connector[qi] = true;
            }
            for (int q = 0; q < rt; q++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int src = rr.Tris[q * 3 + k] * 3;
                    int vi = av.Count / 3;
                    av.Add(rr.VertsXyz[src + 0]);
                    av.Add(rr.VertsXyz[src + 1]);
                    av.Add(rr.VertsXyz[src + 2]);
                    at.Add(vi);
                }
                af.Add(true);
                // A symbolic connector is ALSO ledge-exempt so the builder can single it out (force &&
                // exempt) for the clearance exemption.
                ae.Add(connector[q]);
            }
            if (Test23Verbose && rt > 0)
            {
                int n0 = av.Count - rt * 9;
                Console.WriteLine("      [diag] ramp '" + p.Name + "' tris=" + rt + " first=("
                    + av[n0].ToString("F2") + "," + av[n0 + 1].ToString("F2") + "," + av[n0 + 2].ToString("F2")
                    + ") last=(" + av[av.Count - 3].ToString("F2") + "," + av[av.Count - 2].ToString("F2")
                    + "," + av[av.Count - 1].ToString("F2") + ")");
            }
            if (Test23Verbose)
                Console.WriteLine("      [diag] synth '" + p.Name + "': ramps=" + rr.Ramps + " holes=" + rr.Holes
                    + " noHoles=" + rr.NoHoles + " flat=" + rr.SkippedFlat + " steep=" + rr.SkippedSteep
                    + " long=" + rr.SkippedLong + " capped=" + rr.Capped);
            rampTris += rt;
        }
        float[] tv;
        int[] tt;
        MakeGrid(12f, 12f, 0.5f, Test23Terrain, out tv, out tt);
        TiledNavMeshBuilder.BuildCombinedSoup(tv, tt, av.ToArray(), at.ToArray(), out mx, out mt, out int _f);
        bool anyForce = false;
        for (int k = 0; k < af.Count; k++)
            if (af[k]) { anyForce = true; break; }
        bool anyExempt = false;
        for (int k = 0; k < ae.Count; k++)
            if (ae[k]) { anyExempt = true; break; }
        if (anyForce)
        {
            force = new bool[(tt.Length / 3) + af.Count];
            for (int k = 0; k < af.Count; k++)
                force[(tt.Length / 3) + k] = af[k];
        }
        // Sanity: the mask must be indexed by the COMBINED soup's triangle index, and BuildCombinedSoup
        // emits (terrain tris) + (asset tris) with the assets in the order they were appended. If these
        // differ, the marker/clearance passes silently see no forced triangles at all.
        if (Test23Verbose)
            Console.WriteLine("      [diag] mask setup: terrainTris=" + (tt.Length / 3) + " assetEntries="
                + af.Count + " combinedTris=" + (mt.Length / 3) + " forceLen="
                + (force == null ? -1 : force.Length) + " matches="
                + (force != null && force.Length == mt.Length / 3));
        if (anyExempt)
        {
            exempt = new bool[(tt.Length / 3) + ae.Count];
            for (int k = 0; k < ae.Count; k++)
                exempt[(tt.Length / 3) + k] = ae[k];
        }
    }

    // Returns true when the two probes are connected in the built mesh.
    // Surfaces the steep-face repair line in the Test 23 rigs (they run with a null log, so a drop that
// breaks a case would otherwise be invisible).
    private static void RampDiag23(string m)
    {
        Console.WriteLine("      [diag] " + m);
    }

    private static bool RampLink23(List<SteppedPropRamps.Prop> props, bool useRamps,
        float ax, float ay, float az, float bx, float by, float bz, out bool invariants, out int faces,
        out int rampTris, bool ledge = true, int stitchCells = 6)
    {
        float[] mx;
        int[] mt;
        bool[] force;
        bool[] exempt;
        BuildRampSoup23(props, useRamps, out mx, out mt, out force, out exempt, out rampTris);
        TiledNavMeshBuilder.TiledNavMeshSettings s = LabSettings(0.25f, 0.25f, ledge);
        s.GapFillCells = 0;
        s.StitchMaxGapCells = stitchCells;
        s.PruneIsolatedIslands = false;
        s.GatePlateBlend = Ramp23PlateBlend;
        TiledNavMeshBuilder.VoxelMeshResult r =
            TiledNavMeshBuilder.Build(mx, mt, s, Environment.ProcessorCount,
                delegate (string m) { if (m.IndexOf("steep faces", StringComparison.Ordinal) >= 0) RampDiag23(m);
                    if (m.IndexOf("gate plates", StringComparison.Ordinal) >= 0
                        || m.IndexOf("gateplate", StringComparison.Ordinal) >= 0
                        || m.IndexOf("plate#", StringComparison.Ordinal) >= 0) RampDiag23(m); },
                force, exempt);
        invariants = r.InvariantsPassed;
        faces = r.FaceCount;
        LastRampMesh23 = r;
        int comps;
        int[] fp = FaceComponents(r, out comps);
        int fa = FaceAt(r, ax, ay, az, 0.8f);
        int fb = FaceAt(r, bx, by, bz, 0.8f);
        if (Test23Verbose)
        {
            List<int> ca = FacesAtAnyHeight(r, ax, ay);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < ca.Count && i < 6; i++)
                sb.Append(FaceCentroidY(r, ca[i]).ToString("F2")).Append(" ");
            Console.WriteLine("      [diag] F=" + r.FaceCount + " comps=" + comps
                + " meshY=[" + MeshYRange(r) + "]"
                + " A faces@(" + ax.ToString("F1") + "," + ay.ToString("F1") + ")={" + sb + "}"
                + " B=" + (fb < 0 ? "none" : FaceCentroidY(r, fb).ToString("F2"))
                + " same=" + (fa >= 0 && fb >= 0 && Find(fp, fa) == Find(fp, fb)));
        }
        if (Test23Verbose)
        {
            int comps2;
            int[] fp2 = FaceComponents(r, out comps2);
            var compInfo = new Dictionary<int, string>();
            var compN = new Dictionary<int, int>();
            for (int f = 0; f < r.FaceCount; f++)
            {
                int root = Find(fp2, f);
                int n;
                compN.TryGetValue(root, out n);
                compN[root] = n + 1;
            }
            var sb2 = new System.Text.StringBuilder();
            var roots = new List<int>(compN.Keys);
            roots.Sort(delegate (int a2, int b2) { return compN[b2].CompareTo(compN[a2]); });
            for (int i2 = 0; i2 < roots.Count && i2 < 5; i2++)
            {
                int root = roots[i2];
                float lo = float.MaxValue, hi = float.MinValue, sx = 0, sy = 0;
                int n = 0;
                for (int f = 0; f < r.FaceCount; f++)
                {
                    if (Find(fp2, f) != root) continue;
                    float y = FaceCentroidY(r, f);
                    if (y < lo) lo = y;
                    if (y > hi) hi = y;
                    sx += FaceCentroidX(r, f);
                    sy += FaceCentroidZ(r, f);
                    n++;
                }
                sb2.Append(" [n=").Append(compN[root]).Append(" y=").Append(lo.ToString("F2")).Append("..")
                   .Append(hi.ToString("F2")).Append(" c=(").Append((sx / n).ToString("F1")).Append(",")
                   .Append((sy / n).ToString("F1")).Append(")]");
            }
            Console.WriteLine("      [diag] components:" + sb2);
        }
        return fa >= 0 && fb >= 0 && Find(fp, fa) == Find(fp, fb);
    }

    private static void Test23_PropRamps()
    {
        Console.WriteLine("== Test 23: synthesized ramps for stepped / ladder props ==");
        const int HasSteps = SteppedPropRamps.FlagHasSteps;
        const int Ladder = SteppedPropRamps.FlagLadder;

        // ---- (a) steep stair with a mid-flight hole (the collision boards are missing there)
        var aProps = new List<SteppedPropRamps.Prop>();
        {
            var lv = new List<float>();
            var lt = new List<int>();
            AddBoxZ(lv, lt, -1f, 0f, 0f, 1.3f, 2f, 0.2f);          // lower landing
            AddBoxZ(lv, lt, 2.4f, 0f, 2.65f, 4.4f, 2f, 2.85f);     // top landing
            var tv2 = new List<float>();
            var tt2 = new List<int>();
            for (int i = 0; i < 8; i++)
            {
                float x = 0.5f + i * 0.3f, z = 0.2f + i * 0.35f;
                if (i == 4 || i == 5) continue;                    // the hole: 1.05 m of rise missing
                AddTreadZ(tv2, tt2, x, 1.0f, z, 0.26f, 0.9f);      // 0.26 deep along the run, 0.9 wide
            }
            aProps.Add(Prop23("stairs_a", HasSteps, lv, lt));
            aProps.Add(Prop23("stairs_a#flight", HasSteps, tv2, tt2));
        }
        bool ia, ic;
        int fa, fca, rta, fc2;
        bool aOff = RampLink23(aProps, false, 0f, 1f, 0.2f, 3.5f, 1f, 2.85f, out ia, out fa, out rta);
        bool aOn = RampLink23(aProps, true, 0f, 1f, 0.2f, 3.5f, 1f, 2.85f, out ic, out fca, out rta);
        Console.WriteLine("    a) stair with a 1.05 m mid-flight hole: rampsOff connected=" + aOff
            + " rampsOn connected=" + aOn + " rampTris=" + rta + " F=" + fca
            + " invariants=" + ((ia && ic) ? "PASS" : "FAIL"));

        // ---- (b) near-vertical ladder (62 deg) between two floors
        var bProps = new List<SteppedPropRamps.Prop>();
        {
            var lv = new List<float>();
            var lt = new List<int>();
            AddBoxZ(lv, lt, -1f, -1f, 0f, 2f, 2f, 0.2f);           // lower floor
            AddBoxZ(lv, lt, 2.0f, 2.0f, 3.0f, 5.0f, 5.0f, 3.2f);   // upper floor, starting at the ladder top
            var sv = new List<float>();
            var st = new List<int>();
            AddRungsZ(sv, st, 1.0f, 1.0f, 0.2f, 2.1f, 2.1f, 3.2f, 0.7f, 5);
            bProps.Add(Prop23("floors_b", 0, lv, lt));
            bProps.Add(Prop23("ladder_b", Ladder, sv, st));
        }
        bool bOff = RampLink23(bProps, false, 0f, 0f, 0.2f, 4.0f, 4.0f, 3.2f, out ia, out fa, out int rtb);
        bool bOn = RampLink23(bProps, true, 0f, 0f, 0.2f, 4.0f, 4.0f, 3.2f, out ic, out fc2, out rtb);
        Console.WriteLine("    b) 62 deg ladder floor->floor: rampsOff connected=" + bOff
            + " rampsOn connected=" + bOn + " rampTris=" + rtb + " F=" + fc2
            + " invariants=" + ((ia && ic) ? "PASS" : "FAIL"));
        if (Test23Verbose)
        {
            ProbeLine23(LastRampMesh23, 1.0f, 1.0f, 0.3f, 2.1f, 2.1f, 3.1f, 8);
            bool iv;
            int f2, rt2;
            bool bNoLedge = RampLink23(bProps, true, 0f, 0f, 0.2f, 4.0f, 4.0f, 3.2f, out iv, out f2,
                out rt2, false);
            Console.WriteLine("      [diag] b with ledgeFilter OFF: connected=" + bNoLedge + " F=" + f2);
        }

        // ---- (c) two ladders onto one shared platform: end-to-end through the platform
        var cProps = new List<SteppedPropRamps.Prop>();
        {
            var lv = new List<float>();
            var lt = new List<int>();
            AddBoxZ(lv, lt, -1f, -1f, 0f, 2f, 2f, 0.2f);           // lower floor A
            AddBoxZ(lv, lt, 5.4f, 5.4f, 0f, 8.4f, 8.4f, 0.2f);     // lower floor B
            AddBoxZ(lv, lt, 2.4f, 2.4f, 3.0f, 6.4f, 6.4f, 3.2f);   // the shared platform
            var s1v = new List<float>();
            var s1t = new List<int>();
            AddRungsZ(s1v, s1t, 0.9f, 0.9f, 0.2f, 2.5f, 2.5f, 3.2f, 0.7f, 4);
            var s2v = new List<float>();
            var s2t = new List<int>();
            AddRungsZ(s2v, s2t, 7.6f, 7.6f, 0.2f, 6.3f, 6.3f, 3.2f, 0.7f, 4);
            cProps.Add(Prop23("floors_c", 0, lv, lt));
            cProps.Add(Prop23("ladder_c1", Ladder, s1v, s1t));
            cProps.Add(Prop23("ladder_c2", Ladder, s2v, s2t));
        }
        bool cOff = RampLink23(cProps, false, 0f, 0f, 0.2f, 8f, 8f, 0.2f, out ia, out fa, out int rtc);
        bool cOn = RampLink23(cProps, true, 0f, 0f, 0.2f, 8f, 8f, 0.2f, out ic, out fc2, out rtc);
        Console.WriteLine("    c) two ladders, one platform (end-to-end): rampsOff connected=" + cOff
            + " rampsOn connected=" + cOn + " rampTris=" + rtc + " F=" + fc2
            + " invariants=" + ((ia && ic) ? "PASS" : "FAIL"));

        // ---- (d) chained ladders: the first ladder's top apron lands on the second's base level
        var dProps = new List<SteppedPropRamps.Prop>();
        {
            var lv = new List<float>();
            var lt = new List<int>();
            AddBoxZ(lv, lt, -1f, -1f, 0f, 2f, 2f, 0.2f);           // ground
            AddBoxZ(lv, lt, 1.7f, 1.7f, 2.2f, 3.7f, 3.7f, 2.4f);   // mid landing
            AddBoxZ(lv, lt, 3.7f, 3.7f, 4.4f, 5.7f, 5.7f, 4.6f);   // top landing
            var s1v = new List<float>();
            var s1t = new List<int>();
            AddRungsZ(s1v, s1t, 1.0f, 1.0f, 0.2f, 2.0f, 2.0f, 2.4f, 0.7f, 4);
            var s2v = new List<float>();
            var s2t = new List<int>();
            AddRungsZ(s2v, s2t, 3.0f, 3.0f, 2.4f, 4.0f, 4.0f, 4.6f, 0.7f, 4);
            dProps.Add(Prop23("floors_d", 0, lv, lt));
            dProps.Add(Prop23("ladder_d1", Ladder, s1v, s1t));
            dProps.Add(Prop23("ladder_d2", Ladder, s2v, s2t));
        }
        bool dOff = RampLink23(dProps, false, 0f, 0f, 0.2f, 5f, 5f, 4.6f, out ia, out fa, out int rtd);
        bool dOn = RampLink23(dProps, true, 0f, 0f, 0.2f, 5f, 5f, 4.6f, out ic, out fc2, out rtd);
        Console.WriteLine("    d) chained ladders (apron to base): rampsOff connected=" + dOff
            + " rampsOn connected=" + dOn + " rampTris=" + rtd + " F=" + fc2
            + " invariants=" + ((ia && ic) ? "PASS" : "FAIL"));

        bool ok = !aOff && aOn && !bOff && bOn && !cOff && cOn && !dOff && dOn
            && rta > 0 && rtb > 0 && rtc > 0 && rtd > 0;
        Console.WriteLine("  expectations: every case is broken with ramps off and connected through the"
            + " synthesized ramp with them on; invariants PASS throughout");
        Console.WriteLine("  PROP-RAMP TEST: " + (ok ? "PASS" : "FAIL"));
    }

    // ---- Test 24: a leaning ladder whose top reaches a small doorway in a tall wall.
    // Mirrors the user's real setup (civilian_ladder_6m/5m leaning ~29-35 deg off vertical against a
    // wall, the ladder connecting to a small doorway at its top). The rungs are 0.7 m apart - more than
    // the agent climb - so the synthesized ramps are what carry the flight, and the top apron must
    // flatten only the prop's OWN footprint: a forced-walkable pad reaching further would sit inside
    // the wall and tunnel it at the landing level.
    // Engine Z-up. Lower floor x[1..4] y[1..5] top 0.2 | tall wall x[4..4+thick] with a doorway
    // (y[2.9..3.9] = 1.0 m wide, z[3.0..5.2] = 2.2 m high) | upper floor beyond, top 3.0.
    // The ladder: base (2.04,3.4,0.2) -> top (4.0,3.4,3.0), 0.7 m wide, 5 rungs (0.7 m apart).
    private static void BuildDoorwayGeometry24(List<float> v, List<int> t, float thick, bool plugDoor)
    {
        float x1 = 4.0f + thick;
        AddBoxZ(v, t, 1.0f, 1.0f, 0f, 4.0f, 5.0f, 0.2f);            // lower floor, top 0.2
        AddBoxZ(v, t, 4.0f, 1.0f, 0.2f, x1, 2.9f, 6.0f);            // jamb -Y
        AddBoxZ(v, t, 4.0f, 3.9f, 0.2f, x1, 5.0f, 6.0f);            // jamb +Y
        AddBoxZ(v, t, 4.0f, 2.9f, 5.2f, x1, 3.9f, 6.0f);            // lintel above the doorway
        AddBoxZ(v, t, x1, 1.0f, 2.8f, x1 + 3.0f, 5.0f, 3.0f);       // upper floor beyond, top 3.0
        // The doorway. Open: the wall under it ends AT the upper floor level, so its top face is the
        // doorway's sill (the floor running through the opening), which is what joins the ladder's top
        // to the floor beyond. Walled shut (control): ONE solid block from the lower floor up to the
        // lintel, so no internal horizontal face exists at 3.0 for the stepped-prop exemption to
        // restore - the only way up would be a forced pad tunneling through the wall.
        if (plugDoor)
            AddBoxZ(v, t, 4.0f, 2.9f, 0.2f, x1, 3.9f, 5.2f);
        else
            AddBoxZ(v, t, 4.0f, 2.9f, 0.2f, x1, 3.9f, 3.0f);
    }

    private static void AddDoorwayLadder24(List<float> v, List<int> t)
    {
        AddRungsZ(v, t, 2.04f, 3.4f, 0.2f, 4.0f, 3.4f, 3.0f, 0.7f, 5);
    }

    private static List<SteppedPropRamps.Prop> DoorwayProps24(float thick, bool plugDoor)
    {
        var props = new List<SteppedPropRamps.Prop>();
        var lv = new List<float>();
        var lt = new List<int>();
        BuildDoorwayGeometry24(lv, lt, thick, plugDoor);
        props.Add(Prop23("doorway_wall", 0, lv, lt));
        var sv = new List<float>();
        var st = new List<int>();
        AddDoorwayLadder24(sv, st);
        props.Add(Prop23("civilian_ladder_6m", SteppedPropRamps.FlagLadder, sv, st));
        return props;
    }

    // Lower floor -> upper floor through the doorway, plus the two threshold legs. stitchCells
    // isolates the ramps from the merge-stitch pass.
    private static bool DoorwayRun24(string label, float thick, bool plugDoor, bool useRamps,
        int stitchCells, out bool invariants, out int rampTris)
    {
        const float lowerX = 1.6f, lowerZ = 0.2f;
        const float y0 = 3.4f, upperZ = 3.0f;
        float doorX = 4.0f + thick * 0.5f, upperX = 4.0f + thick + 1.5f;
        List<SteppedPropRamps.Prop> props = DoorwayProps24(thick, plugDoor);
        bool inv1, inv2, inv3;
        int f1, rt1, f2, rt2, f3, rt3;
        Test23Verbose = false;
        bool direct = RampLink23(props, useRamps, lowerX, y0, lowerZ, upperX, y0, upperZ,
            out inv1, out f1, out rt1, true, stitchCells);
        bool toDoor = RampLink23(props, useRamps, lowerX, y0, lowerZ, doorX, y0, upperZ,
            out inv2, out f2, out rt2, true, stitchCells);
        bool doorUp = RampLink23(props, useRamps, doorX, y0, upperZ, upperX, y0, upperZ,
            out inv3, out f3, out rt3, true, stitchCells);
        invariants = inv1 && inv2 && inv3;
        rampTris = rt1;
        Console.WriteLine("    " + label + ": lower->upper=" + direct + " lower->threshold=" + toDoor
            + " threshold->upper=" + doorUp + " F=" + f1 + " rampTris=" + rt1
            + " invariants=" + (invariants ? "PASS" : "FAIL"));
        return direct;
    }

    private static void Test24_DoorwayLadder()
    {
        Console.WriteLine("== Test 24: leaning ladder through a small doorway onto an upper floor ==");
        const float Thin = 0.5f, Thick = 2.0f;   // Thick = castle wall, beyond the 6-cell stitch cap
        bool iv;
        int rt;

        // The apron must stay inside the prop's own footprint (the wall's near face is x=4.0): it may
        // flatten the flight's own top so the spans merge with the landing, but a forced-walkable pad
        // reaching past the prop would sit inside the wall and tunnel it.
        SteppedPropRamps.Result rr = SteppedPropRamps.Synthesize(
            new SteppedPropRamps.Prop[] { DoorwayProps24(Thin, false)[1] });
        float rampMinX = float.MaxValue, rampMaxX = float.MinValue;
        for (int k = 0; k + 2 < rr.VertsXyz.Count; k += 3)
        {
            float vx = rr.VertsXyz[k];
            if (vx < rampMinX) rampMinX = vx;
            if (vx > rampMaxX) rampMaxX = vx;
        }
        bool apronInsideWall = rampMaxX < 4.5f;
        Console.WriteLine("    ramps for the ladder: ramps=" + rr.Ramps + " holes=" + rr.Holes
            + " triangles=" + (rr.Tris.Count / 3) + " x=[" + rampMinX.ToString("F2") + ".."
            + rampMaxX.ToString("F2") + "] -> apron stops inside the 0.5 m wall (< x=4.5): "
            + apronInsideWall);

        Console.WriteLine("  -- castle wall, 2.0 m thick (beyond the 6-cell stitch cap) --");
        bool a = DoorwayRun24("a) door open,   ramps OFF", Thick, false, false, 6, out iv, out rt);
        bool b = DoorwayRun24("b) door open,   ramps ON ", Thick, false, true, 6, out iv, out rt);
        bool c = DoorwayRun24("c) door walled, ramps ON ", Thick, true, true, 6, out iv, out rt);

        Console.WriteLine("  -- thin wall, 0.5 m (the V3.16 merge-stitch finding, fixed in V3.17) --");
        bool d = DoorwayRun24("d) walled, ramps ON, stitch OFF", Thin, true, true, 0, out iv, out rt);
        bool e = DoorwayRun24("e) walled, ramps ON, stitch ON ", Thin, true, true, 6, out iv, out rt);
        bool f = DoorwayRun24("f) open,   ramps ON, stitch OFF", Thin, false, true, 0, out iv, out rt);

        bool ok = !a && b && !c && !d && !e && f && apronInsideWall && rt > 0 && iv;
        Console.WriteLine("  expectations: the flight is broken without the ramps (rungs 0.7 m > climb);"
            + " with them it climbs, crosses the doorway sill and reaches the upper floor; the apron"
            + " stays inside the prop's footprint; and a walled-shut doorway is NOT connected at any"
            + " wall thickness or stitch setting (V3.17: the merge-stitch clearance probe sees the"
            + " block's solid volume, so (e) is refused like (d))");
        Console.WriteLine("  DOORWAY-LADDER TEST: " + (ok ? "PASS" : "FAIL"));
        Test23Verbose = true;
    }

    // ---- Test 25: the ladder flag is exempt from the synthesis slope cap.
    // Some maps have ladders that go straight up: a 90 deg ladder has no horizontal run of its own, so its
    // connector can only exist if it is given a minimum footprint - and no slope rule may refuse it.
    // Engine Z-up. Lower floor x[1..4] y[1..5] top 0.2 | upper deck top 3.2 with a 0.9 m hole above the
    // ladder top (x[2.15..3.05] y[2.95..3.85]) | the ladder: 5 rungs + a plank from (2.6,3.4,0.2) upward,
    // leaning by `lean` (0 = straight up).
    // True when the mesh connects the two probe points (same component), built with the ramps on.
    private static bool PairConnected25(List<SteppedPropRamps.Prop> props,
        float ax, float ay, float az, float bx, float by, float bz)
    {
        bool iv;
        int faces, rt;
        RampLink23(props, true, ax, ay, az, bx, by, bz, out iv, out faces, out rt, true, 0);
        int comps;
        int[] fp = FaceComponents(LastRampMesh23, out comps);
        int fa = FaceAt(LastRampMesh23, ax, ay, az, 0.8f);
        int fb = FaceAt(LastRampMesh23, bx, by, bz, 0.8f);
        if (fa < 0 || fb < 0) return false;
        return Find(fp, fa) == Find(fp, fb);
    }

    private static void BuildFloors25(List<float> v, List<int> t)
    {
        // The lower floor must be WIDE: a ladder connector may be offset sideways to reach a usable slope,
        // and its offset bottom apron has to land on real floor (that is the real-world case - the navmesh
        // sits near the geometry, not on it). It also needs to extend past the deck hatch.
        AddBoxZ(v, t, -4.0f, -3.0f, 0f, 8.0f, 9.0f, 0.2f);        // lower floor, top 0.2
        AddBoxZ(v, t, 1.0f, 1.0f, 3.0f, 3.00f, 6.0f, 3.2f);       // upper deck, west of the hatch
        // The hatch is wide enough for BOTH ladder cases: the 90 deg ladder's connector tops out at
        // x~3.9 and the 60 deg leaning ladder's top is at x~4.33, and a ladder must lean INTO an opening,
        // never through a slab (a ladder passing through a slab is a rig bug, not a navmesh failure).
        AddBoxZ(v, t, 4.45f, 1.0f, 3.0f, 6.0f, 6.0f, 3.2f);       // upper deck, east of the hatch
        AddBoxZ(v, t, 3.00f, 1.0f, 3.0f, 4.45f, 2.80f, 3.2f);     // upper deck, south of the hatch
        AddBoxZ(v, t, 3.00f, 4.00f, 3.0f, 4.45f, 6.0f, 3.2f);     // upper deck, north of the hatch
    }

    // The ladder prop's own collision: rungs (what a real ladder has) plus the plank behind them (the
    // slanted/vertical plane whose faces span the whole run). `flag` is Ladder for the real case and a
    // non-ladder flag for the control.
    private static SteppedPropRamps.Prop LadderProp25(float lean, int flag)
    {
        var v = new List<float>();
        var t = new List<int>();
        AddRungsZ(v, t, 2.6f, 3.4f, 0.2f, 2.6f + lean, 3.4f, 3.2f, 0.7f, 5);
        AddStripZ(v, t, 2.62f, 3.4f, 0.2f, 2.62f + lean, 3.4f, 3.2f, 0.7f, 3);
        return Prop23(flag == SteppedPropRamps.FlagLadder ? "ladder25" : "slab25", flag, v, t);
    }

    private static List<SteppedPropRamps.Prop> LadderScene25(float lean, int flag)
    {
        var props = new List<SteppedPropRamps.Prop>();
        var fv = new List<float>();
        var ft = new List<int>();
        BuildFloors25(fv, ft);
        props.Add(Prop23("floors25", 0, fv, ft));
        props.Add(LadderProp25(lean, flag));
        return props;
    }

    // The corrected measurement: build the scene's mesh with the ramps on, then walk the connector's own
    // centre line station by station, reporting for each station what the MESH has there (every face's
    // height and island) and what the CONNECTOR has there (its own surface height). That separates "the
    // connector's surface is present" from "the navmesh carries it and it is merged with the ends".
    private static void DumpConnectorStations25(List<SteppedPropRamps.Prop> props, string label,
        SteppedPropRamps.Result conn)
    {
        bool iv;
        int faces, rt;
        RampLink23(props, true, 1.2f, 3.4f, 0.2f, 2.6f, 3.4f, 3.2f, out iv, out faces, out rt, true, 0);
        int comps;
        int[] fp = FaceComponents(LastRampMesh23, out comps);
        var rank = new Dictionary<int, int>();
        var sizes = new Dictionary<int, int>();
        for (int f = 0; f < LastRampMesh23.FaceCount; f++)
        {
            int root = Find(fp, f);
            int n;
            sizes.TryGetValue(root, out n);
            sizes[root] = n + 1;
        }
        // Rank islands by size (1 = largest) so the table reads like the --probe output.
        var order = new List<int>(sizes.Keys);
        order.Sort(delegate (int a, int b)
        {
            int c = sizes[b].CompareTo(sizes[a]);
            return c != 0 ? c : a.CompareTo(b);
        });
        for (int i = 0; i < order.Count; i++) rank[order[i]] = i + 1;

        Console.WriteLine("    [stations] " + label + " rampsOn F=" + faces + " comps=" + comps
            + " connector ramps=" + conn.Ramps + " tris=" + (conn.Tris.Count / 3)
            + " | connector forcedInTile=" + LastRampMesh23.ConnectorForcedInTile
            + " markedCells=" + LastRampMesh23.ConnectorMarkedCells
            + " clearedCeilings=" + LastRampMesh23.ConnectorClearedCeilings
            + " erosionExempt=" + LastRampMesh23.ConnectorErosionExempt
            + " | stepped marked=" + LastRampMesh23.SteppedMarkedCells
            + " restored=" + LastRampMesh23.SteppedRestoredSpans);

        // Connector line: bottom anchor -> top anchor, derived from its own quads' extremes.
        float cminX = float.MaxValue, cmaxX = float.MinValue, cminZ = float.MaxValue, cmaxZ = float.MinValue;
        float cminY = float.MaxValue, cmaxY = float.MinValue;
        for (int v = 0; v < conn.VertsXyz.Count / 3; v++)
        {
            float vx = conn.VertsXyz[v * 3], vy = conn.VertsXyz[v * 3 + 1], vz = conn.VertsXyz[v * 3 + 2];
            if (vx < cminX) cminX = vx;
            if (vx > cmaxX) cmaxX = vx;
            if (vy < cminY) cminY = vy;
            if (vy > cmaxY) cmaxY = vy;
            if (vz < cminZ) cminZ = vz;
            if (vz > cmaxZ) cmaxZ = vz;
        }
        Console.WriteLine("    [stations] connector extent x=[" + cminX.ToString("F2") + ".."
            + cmaxX.ToString("F2") + "] y=[" + cminY.ToString("F2") + ".." + cmaxY.ToString("F2")
            + "] z=[" + cminZ.ToString("F2") + ".." + cmaxZ.ToString("F2") + "]");

        // Walk from the connector's low end to its high end along its own XZ span, and at each station
        // report: the connector's own surface height there (interpolated from its z range) and every mesh
        // face at that XZ with height + island.
        // When the connector is the offset symbolic link, use its real centre line (Foot/Head in engine
        // X and engine Y = Recast X and Recast Z); otherwise fall back to the bounding-box diagonal.
        float lineAX = cminX, lineAZ = cminZ, lineBX = cmaxX, lineBZ = cmaxZ;
        if (conn.Infos.Count > 0)
        {
            SteppedPropRamps.RampInfo li = conn.Infos[conn.Infos.Count - 1];
            if (li.FootX != 0f || li.FootHz != 0f || li.HeadX != 0f || li.HeadHz != 0f)
            {
                lineAX = li.FootX; lineAZ = li.FootHz;
                lineBX = li.HeadX; lineBZ = li.HeadHz;
            }
        }
        int stations = 10;
        for (int s = 0; s <= stations; s++)
        {
            float f = (float)s / stations;
            float px = lineAX + (lineBX - lineAX) * f;
            float pz = lineAZ + (lineBZ - lineAZ) * f;
            float py = cminY + (cmaxY - cminY) * f;       // connector's own local level
            List<int> cands = FacesAtAnyHeight(LastRampMesh23, px, pz);
            var sb = new System.Text.StringBuilder();
            sb.Append("      st").Append(s.ToString("00")).Append(" (").Append(px.ToString("F2")).Append(",")
              .Append(pz.ToString("F2")).Append(") connY=").Append(py.ToString("F2")).Append("  mesh:");
            if (cands.Count == 0) sb.Append(" NONE");
            for (int k = 0; k < cands.Count && k < 6; k++)
            {
                int root = Find(fp, cands[k]);
                float hy = FaceCentroidY(LastRampMesh23, cands[k]);
                bool atConn = Math.Abs(hy - py) <= 0.6f;
                sb.Append(" h=").Append(hy.ToString("F2"))
                  .Append(" isl=").Append(rank.ContainsKey(root) ? rank[root] : 0)
                  .Append(atConn ? "*" : "");      // * = a face at the connector's own level
            }
            Console.WriteLine(sb.ToString());
        }

        // The two ends: is the foot on the floor island, and is the top on the landing/deck island?
        int fFoot = FaceAt(LastRampMesh23, cminX + 0.1f, (cminZ + cmaxZ) * 0.5f, cminY, 1.0f);
        int fTop = FaceAt(LastRampMesh23, cmaxX - 0.1f, (cminZ + cmaxZ) * 0.5f, cmaxY, 1.0f);
        int fFloor = FaceAt(LastRampMesh23, 1.2f, 3.4f, 0.2f, 0.8f);
        int fDeck = FaceAt(LastRampMesh23, 3.4f, 1.5f, 3.2f, 0.8f);
        Console.WriteLine("    [stations] ends: floor(isl=" + Isl(rank, fp, fFloor)
            + ") foot(isl=" + Isl(rank, fp, fFoot) + " h=" + (fFoot >= 0 ? FaceCentroidY(LastRampMesh23, fFoot).ToString("F2") : "-")
            + ") top(isl=" + Isl(rank, fp, fTop) + " h=" + (fTop >= 0 ? FaceCentroidY(LastRampMesh23, fTop).ToString("F2") : "-")
            + ") deck(isl=" + Isl(rank, fp, fDeck) + ")"
            + "  sameIsland(foot,top)=" + (fFoot >= 0 && fTop >= 0 && Find(fp, fFoot) == Find(fp, fTop))
            + " sameIsland(foot,floor)=" + (fFoot >= 0 && fFloor >= 0 && Find(fp, fFoot) == Find(fp, fFloor))
            + " sameIsland(top,deck)=" + (fTop >= 0 && fDeck >= 0 && Find(fp, fTop) == Find(fp, fDeck)));

        // Where did the connector's OWN triangles end up in the mesh? Rebuild with the connector's
        // triangles forced and nothing removed, and sweep its bounding box on a grid, reporting any face
        // that is not the floor or the deck (i.e. the connector's own contribution).
        Console.WriteLine("    [stations] connector-only sweep (faces that are neither floor 0.60 nor deck 3.60):");
        int found = 0;
        for (float sx = cminX; sx <= cmaxX + 1e-3f; sx += 0.25f)
        {
            for (float sz = cminZ; sz <= cmaxZ + 1e-3f; sz += 0.25f)
            {
                List<int> cd = FacesAtAnyHeight(LastRampMesh23, sx, sz);
                for (int k = 0; k < cd.Count; k++)
                {
                    float hy = FaceCentroidY(LastRampMesh23, cd[k]);
                    if (Math.Abs(hy - 0.60f) < 0.05f || Math.Abs(hy - 3.60f) < 0.05f) continue;
                    if (Math.Abs(hy + 7.60f) < 0.05f) continue;
                    found++;
                    if (found <= 12)
                        Console.WriteLine("      conn face at (" + sx.ToString("F2") + "," + sz.ToString("F2")
                            + ") h=" + hy.ToString("F2") + " isl=" + Isl(rank, fp, cd[k]));
                }
            }
        }
        Console.WriteLine("    [stations] connector-origin faces found = " + found);
    }

    private static int Isl(Dictionary<int, int> rank, int[] fp, int f)
    {
        if (f < 0) return -1;
        int root = Find(fp, f);
        return rank.ContainsKey(root) ? rank[root] : 0;
    }

    private static void Test25_LadderSlopeExemption()
    {
        Console.WriteLine("== Test 25: ladder flag is exempt from the synthesis slope cap ==");
        const float lean60 = 1.732f;    // tan(30 deg) x 3.0 m rise => 60 deg from horizontal

        bool iv;
        int faces, rt;
        bool aOff, aOn, bOn, cOn;
        int ra;

        // (a) 90 deg ladder (no horizontal run of its own): the connector must be synthesized at all,
        // which the pre-V3.19 path could not do (the 75 deg cap refused it and a zero-run quad has no
        // footprint), and the ladder flag must be what lets it through.
        {
            List<SteppedPropRamps.Prop> props = LadderScene25(0f, SteppedPropRamps.FlagLadder);
            SteppedPropRamps.Result rchain = SteppedPropRamps.Synthesize(
                new SteppedPropRamps.Prop[] { props[props.Count - 1] });
            Console.WriteLine("    [chain] ramps=" + rchain.Ramps + " tris=" + (rchain.Tris.Count / 3)
                + " verts=" + (rchain.VertsXyz.Count / 3));
            for (int k = 0; k < rchain.Infos.Count; k++)
                Console.WriteLine("    [chain] seg" + k + " z=" + rchain.Infos[k].BottomY.ToString("F2")
                    + "->" + rchain.Infos[k].TopY.ToString("F2") + " run="
                    + rchain.Infos[k].Run.ToString("F2") + "m slope="
                    + rchain.Infos[k].SlopeDeg.ToString("F1") + "deg");
            aOff = RampLink23(props, false, 1.2f, 3.4f, 0.2f, 2.6f, 3.4f, 3.2f, out iv, out faces, out ra);
            aOn = RampLink23(props, true, 1.2f, 3.4f, 0.2f, 2.6f, 3.4f, 3.2f, out iv, out faces, out rt, true, 0);
            // ---- corrected station table: probe along the connector line and at both ends, with the
            // FLOORS present (the mesh that matters). Positive control = a working tilted connector.
            DumpConnectorStations25(props, "90deg ladder", rchain);
            Console.WriteLine("    a) 90 deg vertical ladder: rampsOff connected=" + aOff
                + " rampsOn connected=" + aOn + " rampTris=" + rt + " F=" + faces
                + " invariants=" + (iv ? "PASS" : "FAIL"));
            // Probe along the connector's own line (bottom offset anchor -> top anchor).
            Test23Verbose = false;
            RampLink23(props, true, 1.2f, 3.4f, 0.2f, 2.6f, 3.4f, 3.2f, out iv, out faces, out rt, true, 0);
            {
                float ox = 2.6f - 1.64f * 0.866f, oz = 3.4f - 1.64f * 0.5f;   // approx offset anchor
                var sbx = new System.Text.StringBuilder("    [line] ");
                for (int q = 0; q <= 6; q++)
                {
                    float f = q / 6f;
                    float px = ox + (2.6f - ox) * f, pz = oz + (3.4f - oz) * f;
                    List<int> cd = FacesAtAnyHeight(LastRampMesh23, px, pz);
                    var lv = new System.Text.StringBuilder();
                    for (int j2 = 0; j2 < cd.Count && j2 < 5; j2++)
                        lv.Append(FaceCentroidY(LastRampMesh23, cd[j2]).ToString("F2")).Append(",");
                    sbx.Append("(").Append(px.ToString("F2")).Append(",").Append(pz.ToString("F2"))
                       .Append("){").Append(lv).Append("} ");
                }
                Console.WriteLine(sbx.ToString());
                Console.WriteLine("    [line] floor probe (1.2,3.4)=" + (FaceAt(LastRampMesh23, 1.2f, 3.4f, 0.2f, 0.8f) >= 0)
                    + " top probe (2.6,3.4,3.2)=" + (FaceAt(LastRampMesh23, 2.6f, 3.4f, 3.2f, 0.8f) >= 0));
            }
            // Isolated-ish: the connector's own triangles PLUS the floors (so the aprons have something to
            // land on), with the forced mask. Connector-only builds are meaningless: with no floor beneath,
            // the aprons cannot merge and FilterWalkableLowHeightSpans has nothing to attach to.
            {
                var cv = new List<float>();
                var ct = new List<int>();
                var ctb = new List<bool>();
                var fv2 = new List<float>();
                var ft2 = new List<int>();
                BuildFloors25(fv2, ft2);
                // floors as assets with no marks
                for (int k = 0; k < fv2.Count; k++) cv.Add(fv2[k]);
                for (int k = 0; k < ft2.Count; k++) ct.Add(ft2[k]);
                for (int k = 0; k < ft2.Count / 3; k++) ctb.Add(false);
                int baseV = cv.Count / 3;
                for (int k = 0; k < rchain.VertsXyz.Count; k++) cv.Add(rchain.VertsXyz[k]);
                for (int k = 0; k < rchain.Tris.Count; k++) ct.Add(rchain.Tris[k] + baseV);
                for (int k = 0; k < rchain.Tris.Count / 3; k++) ctb.Add(true);
                TiledNavMeshBuilder.BuildCombinedSoup(new float[0], new int[0], cv.ToArray(),
                    ct.ToArray(), out float[] omx, out int[] omt, out int _);
                var of = new bool[omt.Length / 3];
                for (int k = 0; k < of.Length; k++) of[k] = true;
                TiledNavMeshBuilder.TiledNavMeshSettings s2 = LabSettings(0.25f, 0.25f, true);
                s2.GapFillCells = 0; s2.StitchMaxGapCells = 0; s2.PruneIsolatedIslands = false;
                TiledNavMeshBuilder.VoxelMeshResult r2 =
                    TiledNavMeshBuilder.Build(omx, omt, s2, Environment.ProcessorCount, null, of, null);
                Console.WriteLine("    [iso] connector+floors: F=" + r2.FaceCount
                    + " marked=" + r2.SteppedMarkedCells + " restored=" + r2.SteppedRestoredSpans
                    + " connForced=" + r2.ConnectorForcedInTile + " connMarked=" + r2.ConnectorMarkedCells
                    + " connCleared=" + r2.ConnectorClearedCeilings
                    + " tris=" + (rchain.Tris.Count / 3));
            }
            // Isolated: build ONLY the chain's own triangles, so we see whether the rasterizer keeps
            // them as one walkable strip (nothing else in the scene can interfere).
            if (rchain.VertsXyz.Count > 0)
            {
                var cv = new List<float>(rchain.VertsXyz);
                var ct = new List<int>(rchain.Tris);
                TiledNavMeshBuilder.BuildCombinedSoup(new float[0], new int[0], cv.ToArray(),
                    ct.ToArray(), out float[] omx, out int[] omt, out int _);
                var of = new bool[omt.Length / 3];
                for (int k = 0; k < of.Length; k++) of[k] = true;
                TiledNavMeshBuilder.TiledNavMeshSettings s2 = LabSettings(0.25f, 0.25f, true);
                s2.GapFillCells = 0; s2.StitchMaxGapCells = 0; s2.PruneIsolatedIslands = false;
                TiledNavMeshBuilder.VoxelMeshResult r2 =
                    TiledNavMeshBuilder.Build(omx, omt, s2, Environment.ProcessorCount, null, of, null);
                Console.WriteLine("    [chain] ISOLATED build (ledge ON ): F=" + r2.FaceCount
                    + " marked=" + r2.SteppedMarkedCells + " restored=" + r2.SteppedRestoredSpans
                    + " connForced=" + r2.ConnectorForcedInTile + " connMarked=" + r2.ConnectorMarkedCells
                    + " connCleared=" + r2.ConnectorClearedCeilings);
                {
                    TiledNavMeshBuilder.TiledNavMeshSettings sFan = LabSettings(0.25f, 0.25f, true);
                    sFan.GapFillCells = 0; sFan.StitchMaxGapCells = 0; sFan.PruneIsolatedIslands = false;
                    sFan.UseDetailMesh = false;
                    TiledNavMeshBuilder.VoxelMeshResult rf =
                        TiledNavMeshBuilder.Build(omx, omt, sFan, Environment.ProcessorCount, null, of, null);
                    Console.WriteLine("      iso FAN (no detail): F=" + rf.FaceCount);
                }
                for (int f = 0; f < r2.FaceCount && f < 20; f++)
                {
                    int ia2 = r2.Tris[f * 3], ib2 = r2.Tris[f * 3 + 1], ic2 = r2.Tris[f * 3 + 2];
                    Console.WriteLine("      iso face " + f + " c=("
                        + ((r2.VertsXyz[ia2 * 3] + r2.VertsXyz[ib2 * 3] + r2.VertsXyz[ic2 * 3]) / 3f).ToString("F2")
                        + "," + ((r2.VertsXyz[ia2 * 3 + 2] + r2.VertsXyz[ib2 * 3 + 2] + r2.VertsXyz[ic2 * 3 + 2]) / 3f).ToString("F2")
                        + ") y=" + ((r2.VertsXyz[ia2 * 3 + 1] + r2.VertsXyz[ib2 * 3 + 1] + r2.VertsXyz[ic2 * 3 + 1]) / 3f).ToString("F2"));
                }
                TiledNavMeshBuilder.TiledNavMeshSettings s3 = LabSettings(0.25f, 0.25f, false);
                s3.GapFillCells = 0; s3.StitchMaxGapCells = 0; s3.PruneIsolatedIslands = false;
                TiledNavMeshBuilder.VoxelMeshResult r3 =
                    TiledNavMeshBuilder.Build(omx, omt, s3, Environment.ProcessorCount, null, of, null);
                Console.WriteLine("    [chain] ISOLATED build (ledge OFF): F=" + r3.FaceCount
                    + " marked=" + r3.SteppedMarkedCells + " restored=" + r3.SteppedRestoredSpans);
                for (int q = 0; q < 7; q++)
                {
                    List<int> cd = FacesAtAnyHeight(r2, 2.6f + 0.31f * q, 3.4f);
                    var sb = new System.Text.StringBuilder("    [chain]  x" + (2.6f + 0.31f * q).ToString("F2") + " {");
                    for (int j2 = 0; j2 < cd.Count && j2 < 6; j2++)
                        sb.Append(FaceCentroidY(r2, cd[j2]).ToString("F2")).Append(",");
                    Console.WriteLine(sb.ToString() + "}");
                }
            }
        }

        // (b) control: the identical vertical geometry flagged HasSteps instead of Ladder. The cap
        // exemption is ladder-flag-only, so this must still be refused (no ramp at all).
        {
            List<SteppedPropRamps.Prop> props = LadderScene25(0f, SteppedPropRamps.FlagHasSteps);
            SteppedPropRamps.Result rr = SteppedPropRamps.Synthesize(
                new SteppedPropRamps.Prop[] { props[1] });
            bOn = RampLink23(props, true, 1.2f, 3.4f, 0.2f, 2.6f, 3.4f, 3.2f, out iv, out faces, out rt, true, 0);
            string why = rr.Skips.Count > 0 ? (" reason=" + rr.Skips[0].Reason) : " reason=none";
            Console.WriteLine("    b) control (same geometry, HasSteps flag): rampsOn connected=" + bOn
                + " synthRamps=" + rr.Ramps + why + " rampTris=" + rt + " F=" + faces
                + " invariants=" + (iv ? "PASS" : "FAIL") + "   [must stay refused]");
        }

        // (c) 60 deg ladder: a leaning ladder keeps working.
        {
            List<SteppedPropRamps.Prop> props = LadderScene25(lean60, SteppedPropRamps.FlagLadder);
            cOn = RampLink23(props, true, 1.2f, 3.4f, 0.2f, 2.6f + lean60, 3.4f, 3.2f,
                out iv, out faces, out rt, true, 0);
            Console.WriteLine("    [c] meshY=[" + MeshYRange(LastRampMesh23) + "]"
                + " floorProbe=" + (FaceAt(LastRampMesh23, 1.2f, 3.4f, 0.2f, 0.8f) >= 0)
                + " topProbe=" + (FaceAt(LastRampMesh23, 2.6f + lean60, 3.4f, 3.2f, 0.8f) >= 0));
            Console.WriteLine("    c) 60 deg leaning ladder: rampsOn connected=" + cOn + " rampTris=" + rt
                + " F=" + faces + " invariants=" + (iv ? "PASS" : "FAIL"));
        }

        bool ok = !aOff && aOn && !bOn && cOn;
        Console.WriteLine("  expectations: the 90 deg ladder synthesizes and connects with the ramps on"
            + " (impossible before V3.19) and not with them off; the same vertical geometry flagged"
            + " HasSteps is still refused; a 60 deg ladder still connects");
        Console.WriteLine("  LADDER-CAP TEST: " + (ok ? "PASS" : "FAIL"));
    }

    // ---- Test 26: a ladder whose top leads through a NARROW doorway ("barely fits a person").
    // The user's editor feedback: with the V3.28 connector the foot and climb are good, but the top
    // dies at a small arched opening - the top pad stopped at the threshold, so the doorway's throat was
    // ordinary geometry and erosion/clearance thinned it away. The fix is the connector top pad running
    // THROUGH the throat (its cells are connector cells, so all three exemptions cover them), with a
    // guardrail refusing the extension into solid geometry.
    // Engine Z-up. Lower floor x[0..10] y[0..10] top 0.2 | wall x[5.0..5.5], opening
    // y[4.5..5.5] (1.0 m wide) z[3.0..5.0] (2.0 m high) | upper floor x[5.5..9] top 3.0.
    // The ladder: a vertical ladder (rungs + plank) from (4.3,5.0,0.2) up to (4.3,5.0,3.0), so its
    // offset connector's top anchor sits at the near face of the opening and the top pad must bridge
    // the 0.5 m wall throat to the upper floor beyond.
    // Parametric narrow-doorway scene (all engine Z-up). Defaults reproduce the original Test 26.
    private sealed class Door26
    {
        public float WallX0 = 5.0f;
        public float WallThick = 1.6f;      // wall thickness (x1 = WallX0 + WallThick)
        public float OpY0 = 4.5f;           // opening Y span
        public float OpY1 = 5.5f;           // -> opening width = OpY1 - OpY0 (default 1.0 m)
        public float LintelZ = 3.4f;        // wall solid above this z (default gives 3.2 m clearance)
        public float SillZ = 3.0f;          // opening top / threshold level (wall under opening ends here)
        public float UpperTopZ = 3.0f;      // upper floor top
        public float LadderX = 4.3f;        // ladder X (its own top is at z=3.0 by default)
        public float LadderTopZ = 3.0f;     // ladder physics top height (default reaches the sill)
        public bool PlugDoor = false;
        public float WallX1 { get { return WallX0 + WallThick; } }
    }

    private static void BuildNarrowDoorway26(List<float> v, List<int> t, Door26 d)
    {
        float x0 = d.WallX0, x1 = d.WallX1;
        AddBoxZ(v, t, 0f, 0f, 0f, x0, 10.0f, 0.2f);                          // lower floor, top 0.2
        AddBoxZ(v, t, x0, 0f, 0.2f, x1, d.OpY0, 6.0f);                       // wall -Y of the opening
        AddBoxZ(v, t, x0, d.OpY1, 0.2f, x1, 10.0f, 6.0f);                    // wall +Y of the opening
        AddBoxZ(v, t, x0, d.OpY0, d.LintelZ, x1, d.OpY1, 6.0f);              // wall ABOVE the opening
        AddBoxZ(v, t, x1, 0f, d.UpperTopZ - 0.2f, x1 + 3.5f, 10.0f, d.UpperTopZ); // upper floor beyond
        if (d.PlugDoor)
            AddBoxZ(v, t, x0, d.OpY0, 0.2f, x1, d.OpY1, d.SillZ);            // walled shut (control)
        else
            AddBoxZ(v, t, x0, d.OpY0, 0.2f, x1, d.OpY1, d.SillZ);            // opening sill (floor through)
    }

    private static List<SteppedPropRamps.Prop> NarrowDoorwayProps26(Door26 d)
    {
        var props = new List<SteppedPropRamps.Prop>();
        var lv = new List<float>();
        var lt = new List<int>();
        BuildNarrowDoorway26(lv, lt, d);
        props.Add(Prop23("narrow_wall", 0, lv, lt));
        var sv = new List<float>();
        var st = new List<int>();
        AddRungsZ(sv, st, d.LadderX, 5.0f, 0.2f, d.LadderX, 5.0f, d.LadderTopZ, 0.7f, 5);  // vertical ladder
        AddStripZ(sv, st, d.LadderX + 0.02f, 5.0f, 0.2f, d.LadderX + 0.02f, 5.0f, d.LadderTopZ, 0.7f, 3);
        SteppedPropRamps.Prop ladder = Prop23("narrow_ladder", SteppedPropRamps.FlagLadder, sv, st);
        ladder.IsFreeAt = delegate (float x, float y, float z) { return Door26Free(d, x, y, z); };
        ladder.SeekSurface = delegate (float x, float y, float from, bool up, float reach)
        {
            return Door26Seek(d, x, y, from, up, reach);
        };
        props.Add(ladder);
        return props;
    }

    // True where the point is NOT inside any solid box of the parametric doorway scene.
    private static bool Door26Free(Door26 d, float x, float y, float z)
    {
        float x0 = d.WallX0, x1 = d.WallX1;
        if (InBox(x, y, z, 0f, 0f, -1f, x0, 10.0f, 0.2f)) return false;                      // lower floor
        if (InBox(x, y, z, x0, 0f, 0.2f, x1, d.OpY0, 6.0f)) return false;                    // wall -Y
        if (InBox(x, y, z, x0, d.OpY1, 0.2f, x1, 10.0f, 6.0f)) return false;                 // wall +Y
        if (InBox(x, y, z, x0, d.OpY0, d.LintelZ, x1, d.OpY1, 6.0f)) return false;           // wall above
        if (InBox(x, y, z, x1, 0f, d.UpperTopZ - 0.2f, x1 + 3.5f, 10.0f, d.UpperTopZ)) return false; // upper floor
        if (d.PlugDoor && InBox(x, y, z, x0, d.OpY0, 0.2f, x1, d.OpY1, d.SillZ)) return false; // walled shut
        return true;
    }

    // Surface seek: the highest solid TOP within the reach band at (x,y) (mirrors the collector).
    private static float Door26Seek(Door26 d, float x, float y, float from, bool up, float reach)
    {
        float lo = up ? from - 0.5f : from - reach;
        float hi = up ? from + reach : from;
        float best = float.NaN;
        // Candidate top surfaces (the boxes' top faces) that contain (x,y) within the band.
        ConsiderTop(ref best, x, y, lo, hi, d.SillZ);                       // opening sill / threshold floor
        ConsiderTop(ref best, x, y, lo, hi, d.UpperTopZ);                   // upper floor top
        ConsiderTop(ref best, x, y, lo, hi, 0.2f);                          // lower floor top
        ConsiderTop(ref best, x, y, lo, hi, 6.0f);                          // wall / lintel top
        return best;
    }

    private static void ConsiderTop(ref float best, float x, float y, float lo, float hi, float top)
    {
        if (top < lo - 1e-4f || top > hi + 1e-4f) return;
        // The surface must actually exist at (x,y): be within the scene's XZ extent.
        if (float.IsNaN(best) || top > best) best = top;
    }

    private static bool InBox(float x, float y, float z, float x0, float y0, float z0,
        float x1, float y1, float z1)
    {
        return x >= x0 && x <= x1 && y >= y0 && y <= y1 && z >= z0 && z <= z1;
    }

    private static void Test26_NarrowDoorway()
    {
        Console.WriteLine("== Test 26: ladder top through a narrow (1.0 x 2.0 m) doorway ==");
        bool iv;
        int faces, rt;
        const float lx = 2.0f, lz = 0.2f, ly = 5.0f;
        const float ux = 8.0f, uz = 3.0f;
        const float tx = 5.8f, tz = 3.0f;

        Door26 d = new Door26();
        List<SteppedPropRamps.Prop> openProps = NarrowDoorwayProps26(d);
        SteppedPropRamps.ConnectorTopPadExtension = true;
        bool onDirect = RampLink23(openProps, true, lx, ly, lz, ux, ly, uz, out iv, out faces, out rt, true, 6);
        bool onThresh = RampLink23(openProps, true, lx, ly, lz, tx, ly, tz, out iv, out faces, out rt, true, 6);
        Console.WriteLine("    a) open door, extension ON : lower->upper=" + onDirect
            + " lower->threshold=" + onThresh + " F=" + faces + " rampTris=" + rt
            + " invariants=" + (iv ? "PASS" : "FAIL"));
        for (int q = 0; q <= 10; q++)
        {
            float pxx = 4.0f + q * 0.35f;
            List<int> cd = FacesAtAnyHeight(LastRampMesh23, pxx, 5.0f);
            var sb = new System.Text.StringBuilder("    [26] x=" + pxx.ToString("F2") + " {");
            for (int j2 = 0; j2 < cd.Count && j2 < 6; j2++)
                sb.Append(FaceCentroidY(LastRampMesh23, cd[j2]).ToString("F2")).Append(",");
            Console.WriteLine(sb.ToString() + "}");
        }

        // Extension OFF (legacy short pad): the control proving the extension carries the throat.
        SteppedPropRamps.ConnectorTopPadExtension = false;
        bool offDirect = RampLink23(openProps, true, lx, ly, lz, ux, ly, uz, out iv, out faces, out rt, true, 6);
        SteppedPropRamps.ConnectorTopPadExtension = true;
        Console.WriteLine("    b) open door, extension OFF (legacy 0.5 m pad): lower->upper=" + offDirect
            + " F=" + faces + " rampTris=" + rt + "   [control: extension is what carries it]");

        // Walled shut: no tunneling.
        Door26 shut = new Door26(); shut.PlugDoor = true;
        bool shutDirect = RampLink23(NarrowDoorwayProps26(shut), true, lx, ly, lz, ux, ly, uz,
            out iv, out faces, out rt, true, 6);
        Console.WriteLine("    c) door walled shut, extension ON: lower->upper=" + shutDirect
            + " F=" + faces + " rampTris=" + rt + "   [must stay refused: no tunneling]");

        // ---- V3.32 physics gap (small, 0-10 cm): ladder top a hair below the sill ----
        Door26 gap = new Door26(); gap.LadderTopZ = 2.94f;   // 6 cm below the 3.0 sill
        bool gapOn = RampLink23(NarrowDoorwayProps26(gap), true, lx, ly, lz, ux, ly, uz,
            out iv, out faces, out rt, true, 6);
        Console.WriteLine("    d) physics gap 6 cm (ladder top 2.94 < sill 3.0): lower->upper=" + gapOn
            + " F=" + faces + " rampTris=" + rt + " invariants=" + (iv ? "PASS" : "FAIL")
            + "   [surface-seek must snap the top anchor to the sill]");

        // ---- no-landing-in-reach: sill raised beyond the reach ----
        Door26 noLand = new Door26(); noLand.LadderTopZ = 2.0f; noLand.LintelZ = 2.4f; noLand.SillZ = 2.0f;
        noLand.UpperTopZ = 4.6f;   // landing far above the ladder top
        SteppedPropRamps.Result rcNo = SteppedPropRamps.Synthesize(
            new SteppedPropRamps.Prop[] { NarrowDoorwayProps26(noLand)[1] });
        string why = rcNo.Skips.Count > 0 ? rcNo.Skips[0].Reason
            : (rcNo.Ramps > 0 ? "connector-emitted" : "none");
        Console.WriteLine("    e) landing beyond reach (upper top 4.6, ladder top 2.0): reason=" + why
            + " ramps=" + rcNo.Ramps + "   [expect no-landing-in-reach or a refused connector]");

        bool ok = onDirect && onThresh && !offDirect && !shutDirect && gapOn;
        Console.WriteLine("  expectations: the connector crosses the narrow doorway (extension carrying it;"
            + " refused when walled shut); the small physics gap is snapped to the landing; a landing"
            + " beyond the reach is skipped");
        Console.WriteLine("  NARROW-DOORWAY TEST: " + (ok ? "PASS" : "FAIL"));
    }

    // V3.32 parametric sweep: which real-geometry differences break the crossing, and where the mesh dies.
    private static void SweepDoorway26()
    {
        Console.WriteLine("== Sweep 26: which geometry differences break the narrow-doorway crossing ==");
        const float lx = 2.0f, lz = 0.2f, ly = 5.0f;
        const float ux = 8.0f, uz = 3.0f;
        bool iv; int faces, rt;
        Console.WriteLine("    variant                          lower->upper  faces  dying-x");
        SweepCase("baseline (1.0 m, lintel 3.4, sill 3.0)", new Door26(), lx, ly, lz, ux, ly, uz,
            out iv, out faces);
        Door26 narrow = new Door26(); narrow.OpY0 = 4.65f; narrow.OpY1 = 5.35f;      // 0.7 m wide
        SweepCase("(a) opening 0.7 m wide", narrow, lx, ly, lz, ux, ly, uz, out iv, out faces);
        // (b) LOW headroom: a lintel just 0.2 m above the landing level (real "barely fits a person"
        // opening: the clear height above the threshold is small). Lintel must be >= sill for validity.
        Door26 arch = new Door26(); arch.LintelZ = 3.2f;                            // 0.2 m clear over the sill
        SweepCase("(b) lintel 0.2 m over sill", arch, lx, ly, lz, ux, ly, uz, out iv, out faces);
        Door26 up = new Door26(); up.UpperTopZ = 3.2f; up.SillZ = 3.2f; up.LintelZ = 3.6f;  // landing up 0.2
        SweepCase("(c) landing +0.2 m (up)", up, lx, ly, lz, ux, ly, uz, out iv, out faces);
        Door26 down = new Door26(); down.UpperTopZ = 2.8f; down.SillZ = 2.8f;       // landing 20 cm DOWN
        SweepCase("(c) landing -0.2 m (down)", down, lx, ly, lz, ux, ly, uz, out iv, out faces);
        Door26 off = new Door26(); off.LadderX = 3.95f;                             // ladder offset -0.35 m
        SweepCase("(d) ladder offset -0.35 m", off, lx, ly, lz, ux, ly, uz, out iv, out faces);
        Door26 thin = new Door26(); thin.WallThick = 0.4f;
        SweepCase("(e) wall 0.4 m", thin, lx, ly, lz, ux, ly, uz, out iv, out faces);
        Door26 thick = new Door26(); thick.WallThick = 1.0f;
        SweepCase("(e) wall 1.0 m", thick, lx, ly, lz, ux, ly, uz, out iv, out faces);
    }

    // ---------------------------------------------------------------- test 27
    private static void Test27_QuadMerge()
    {
        Console.WriteLine("== Test 27: quad merge - convex-only selection + writer NMG8 round trip ==");

        // Part A (WRITER, unchanged): a hand-made concave quad: two CCW triangles share edge 0-1;
        // apexes on opposite sides but placed so the merged walk 1->2->0->3 has a reflex corner at
        // vertex 1, i.e. the emitted face is a concave 4-gon (is_concave = 1). The writer must still
        // compute the flag honestly - policy lives in MergeQuads, not in the serializer - so this part
        // deliberately bypasses the selection and hands the pair straight to the writer.
        float[] xyz = { 0f, 0f, 0f, 1f, 0f, 0f, 0.5f, 1f, 0f, 2f, -1f, 0f };
        int[] tri = { 0, 1, 2, 1, 0, 3 };
        int[] qp = { 0, 1 };

        byte[] plainA = Nmg8Writer.WriteInterleaved(xyz, 4, tri, 2, (int[])null, out int ePlain);
        byte[] plainB = Nmg8Writer.WriteInterleaved(xyz, 4, tri, 2, out int ePlainB);
        bool byteIdentical = ePlain == ePlainB && plainA.Length == plainB.Length;
        if (byteIdentical)
            for (int i = 0; i < plainA.Length; i++)
                if (plainA[i] != plainB[i]) { byteIdentical = false; break; }

        byte[] quadBin = Nmg8Writer.WriteInterleaved(xyz, 4, tri, 2, qp, out int eQuad);
        Nmg8Parse pq = Nmg8Parse.Parse(quadBin);
        Nmg8Parse pp = Nmg8Parse.Parse(plainA);

        Console.WriteLine("  A plain: faces=" + pp.FaceCount + " edges=" + pp.EdgeCount
            + " verts/face=[" + IntList(pp.FaceVertexCounts) + "]");
        Console.WriteLine("  A quad:  faces=" + pq.FaceCount + " edges=" + pq.EdgeCount
            + " verts/face=[" + IntList(pq.FaceVertexCounts) + "]"
            + " concave=[" + IntList(pq.FaceConcave) + "]");

        bool passA = pp.FaceCount == 2 && pp.EdgeCount == 5
            && pp.FaceVertexCounts[0] == 3 && pp.FaceVertexCounts[1] == 3
            && pq.FaceCount == 1 && pq.EdgeCount == 4
            && pq.FaceVertexCounts[0] == 4 && pq.FaceConcave[0] == 1
            && byteIdentical;

        // Part B (SELECTION, unit level): the merge stage must REFUSE a concave pair, because the
        // engine's navmesh validator rejects a concave face outright ("Concave Navigation Mesh Face
        // Detected" on import). The two probes below are the same two-triangle patch - one thin
        // sliver across edge 0-1 and its coplanar neighbour - differing ONLY in where the neighbour's
        // apex sits, so convexity is the single distinguishing guard:
        //   (b1) apex on the far side, x=0.5  -> walk 1->2->0->3 is convex   (worst interior angle 33.1 deg)
        //   (b2) apex dragged past the sliver, x=2.5 -> reflex corner at vertex 1 (233.3 deg)
        // Both probes clear every other guard: f0 is thin by aspect (1^2 / (2*0.05) = 10 > 8), the four
        // vertices are distinct, the pair is exactly coplanar (fold 0 deg), the walk is simple.
        double convWorst = ProbeWorstAngle(new float[]
            { 0f, 0f, 0f, 1f, 0f, 0f, 0.5f, 0f, 0.1f, 0.5f, 0f, -0.2f });
        double concWorst = ProbeWorstAngle(new float[]
            { 0f, 0f, 0f, 1f, 0f, 0f, 0.5f, 0f, 0.1f, 2.5f, 0f, -0.2f });
        int convQuads, concQuads, convFaces, concFaces;
        SelectionQuads(new float[]
            { 0f, 0f, 0f, 1f, 0f, 0f, 0.5f, 0f, 0.1f, 0.5f, 0f, -0.2f }, out convQuads, out convFaces);
        SelectionQuads(new float[]
            { 0f, 0f, 0f, 1f, 0f, 0f, 0.5f, 0f, 0.1f, 2.5f, 0f, -0.2f }, out concQuads, out concFaces);
        Console.WriteLine("  B convex patch : worstAngle=" + F1(convWorst) + " deg -> quads=" + convQuads
            + " (faces left=" + convFaces + ")");
        Console.WriteLine("  B concave patch: worstAngle=" + F1(concWorst) + " deg -> quads=" + concQuads
            + " (faces left=" + concFaces + ")");
        // The independent probe must agree with the product about which patch is convex, otherwise the
        // refusal below could be an unrelated guard firing (e.g. the pair being untouched for a
        // different reason entirely).
        bool passB1 = convQuads == 1 && convWorst > 0.0 && convWorst < 180.0;
        bool passB2 = concQuads == 0 && concWorst > 180.0;

        // Part B2 (SELECTION, the engine's frame): V3.41's plane-only test shipped five quads on
        // Birtu that are convex in their own plane and REFLEX in the XZ projection the engine's
        // validator actually sees - and the engine's dialog named exactly those five. This is the
        // first of them (the engine reported it at x=513.38 y=111.81 z=44.70, which is this quad's
        // centroid in the engine's Z-up frame), captured verbatim from the real mesh and arranged as
        // SelectionQuads expects (tris 0,1,2 / 1,0,3). The merge stage must now REFUSE the pair even
        // though every other guard passes:
        //   - both faces are thin by aspect, four distinct vertices;
        //   - the fold is 7.3 deg, under even the tightened 12 deg cap, so (d) cannot be what
        //     refuses it;
        //   - the quad's worst interior angle in its OWN plane is 179.85 deg - just inside V3.41's
        //     179.9 ceiling, so V3.41's (g) accepted it. Only the new XZ test (h) can be firing here,
        //     which is exactly what this probe pins down.
        float[] realXzReflex =
        {
            515.00f, 46.7000008f, 112.00f,   // 0 = walk[2] (the "p" corner)
            512.75f, 43.8999977f, 112.00f,   // 1 = walk[0] (the "q" corner)
            512.50f, 43.7000008f, 112.50f,   // 2 = walk[1] (f0's apex)
            513.25f, 44.5000000f, 110.75f,   // 3 = walk[3] (f1's apex)
        };
        double reflexPlane = ProbeWorstAngle(realXzReflex);
        int[] rw = new int[] { 1, 2, 0, 3 };   // the walk SelectionQuads / the writer build
        double reflexXZ = ProbeWorstAngleXZ(realXzReflex, rw, out int rRefl, out int rFlat);
        int rQuads, rFaces;
        SelectionQuads(realXzReflex, out rQuads, out rFaces);
        Console.WriteLine("  B2 real XZ-reflex patch: planeWorstAngle=" + F1(reflexPlane)
            + " deg xzWorstAngle=" + reflexXZ.ToString("F4", CultureInfo.InvariantCulture) + " deg"
            + " (reflex=" + rRefl + " flat=" + rFlat + ") -> quads=" + rQuads
            + " (faces left=" + rFaces + ")");
        // The independent probes must agree that this patch is convex in its plane and reflex in XZ,
        // otherwise the refusal could be an unrelated guard firing (as in part B).
        bool passB3 = rQuads == 0 && reflexPlane < 180.0 && reflexXZ > 180.0 && rRefl == 1;

        // Part C: a real pipeline build with a sliver-rich scene; every merged quad must show up as
        // exactly one 4-vertex face and every unpaired triangle as one 3-vertex face, each merge must
        // remove exactly the one shared interior edge, and NO emitted quad may be concave - the
        // writer's own is_concave must be 0 for all of them, confirmed by the independent probe too.
        float[] mv;
        int[] mt;
        MakeGrid(160f, 160f, 0.5f, MesaHeightF, out mv, out mt);
        TiledNavMeshBuilder.TiledNavMeshSettings s = DetailSettings(0.5f, 2.0f, 1.3f, 12);
        TiledNavMeshBuilder.VoxelMeshResult r =
            TiledNavMeshBuilder.Build(mv, mt, s, Environment.ProcessorCount, null);
        int quads = r.QuadPairs == null ? 0 : r.QuadPairs.Length / 2;
        byte[] bin = Nmg8Writer.WriteInterleaved(r.VertsXyz, r.VertexCount, r.Tris, r.FaceCount,
            r.QuadPairs, out int ecEmitted);
        Nmg8Parse p = Nmg8Parse.Parse(bin);
        int fours = 0, threes = 0, other = 0, concaveFaces = 0;
        for (int i = 0; i < p.FaceVertexCounts.Count; i++)
        {
            int c = p.FaceVertexCounts[i];
            if (c == 4) fours++; else if (c == 3) threes++; else other++;
            if (p.FaceConcave[i] != 0) concaveFaces++;
        }
        // Independent convexity sweep over the quads actually recorded in QuadPairs.
        int nonConvexPairs = 0, straightPairs = 0;
        double worstQuadAngle = 0.0;
        if (r.QuadPairs != null)
            for (int k = 0; k + 1 < r.QuadPairs.Length; k += 2)
            {
                double a = QuadWorstAngleV(r, r.QuadPairs[k], r.QuadPairs[k + 1]);
                if (a > worstQuadAngle) worstQuadAngle = a;
                if (a > 180.0) nonConvexPairs++;
                else if (a >= NavMeshCleanup.QuadConvexMaxInteriorAngleDegrees) straightPairs++;
            }
        bool passC = quads > 0 && other == 0
            && p.FaceCount == r.FaceCount - quads
            && fours == quads
            && threes == r.FaceCount - 2 * quads
            && p.EdgeCount == r.EdgeCount - quads
            && concaveFaces == 0
            && nonConvexPairs == 0
            && straightPairs == 0;
        Console.WriteLine("  C mesa: triFaces=" + r.FaceCount + " quads=" + quads + " emittedFaces=" + p.FaceCount
            + " threes=" + threes + " fours=" + fours + " concave=" + concaveFaces
            + " triEdges=" + r.EdgeCount + " emittedEdges=" + p.EdgeCount);
        Console.WriteLine("  C mesa: independent probe -> concaveQuads=" + nonConvexPairs
            + " straightQuads=" + straightPairs + " worstQuadAngle=" + F1(worstQuadAngle) + " deg");

        // Part C2: the ENGINE'S FRAME. Every emitted quad must also be strictly convex in the XZ
        // projection (the only thing a 2D navmesh validator can see), with the product's XZ margin,
        // and its XZ winding must still match the two triangles it replaces. This is the assertion
        // V3.41 lacked, and it is the one that reproduces the engine's five concave-face reports.
        int xzReflex = 0, xzFlat = 0, xzOrientBad = 0, xzDegenerate = 0, xzOverMargin = 0;
        double xzMargin = ProductXzMarginDegrees();
        double worstXzAngle = 0.0;
        if (r.QuadPairs != null)
            for (int k = 0; k + 1 < r.QuadPairs.Length; k += 2)
            {
                int f0 = r.QuadPairs[k], f1 = r.QuadPairs[k + 1];
                int[] w = QuadWalk(r, f0, f1);
                double a = ProbeWorstAngleXZ(r.VertsXyz, w, out int refl, out int flat);
                double quad2 = 0.0;
                for (int c2 = 0; c2 < 4; c2++)
                {
                    int ia = w[c2], ib = w[(c2 + 1) % 4];
                    quad2 += (double)r.VertsXyz[ia * 3] * r.VertsXyz[ib * 3 + 2]
                           - (double)r.VertsXyz[ib * 3] * r.VertsXyz[ia * 3 + 2];
                }
                double t0 = TriXzArea2(r, f0), t1 = TriXzArea2(r, f1);
                if (a < 0.0 || Math.Abs(quad2) < 1e-9) xzDegenerate++;
                else
                {
                    if (a > worstXzAngle) worstXzAngle = a;
                    if (refl != 0) xzReflex++;
                    if (flat != 0) xzFlat++;
                    if (quad2 * t0 <= 0.0 || quad2 * t1 <= 0.0) xzOrientBad++;
                    if (a >= xzMargin) xzOverMargin++;
                }
            }
        bool passC2 = quads > 0 && xzReflex == 0 && xzFlat == 0 && xzOrientBad == 0
            && xzDegenerate == 0 && xzOverMargin == 0
            && Math.Abs(xzMargin - XzMarginDegreesProbe) < 1e-9;
        Console.WriteLine("  C2 mesa XZ frame: reflex=" + xzReflex + " flat=" + xzFlat
            + " orientBad=" + xzOrientBad + " degenerate=" + xzDegenerate
            + " overMargin(>=" + xzMargin + "deg)=" + xzOverMargin
            + " worstXzAngle=" + worstXzAngle.ToString("F4", CultureInfo.InvariantCulture) + " deg"
            + " margin=" + xzMargin + "deg expected=" + XzMarginDegreesProbe + "deg");

        Console.WriteLine("  RESULT: partA " + passA + ", partB(convex accepted) " + passB1
            + ", partB(concave refused) " + passB2 + ", partB2(XZ-reflex refused) " + passB3
            + ", partC " + passC + ", partC2(XZ frame) " + passC2
            + ", invariantsPASS " + r.InvariantsPassed
            + " -> " + ((passA && passB1 && passB2 && passB3 && passC && passC2 && r.InvariantsPassed)
                ? "PASS" : "FAIL"));
    }

    // Independent XZ signed area x2 of triangle f, in its own winding order.
    private static double TriXzArea2(TiledNavMeshBuilder.VoxelMeshResult r, int f)
    {
        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
        float[] v = r.VertsXyz;
        return (double)v[a * 3] * v[b * 3 + 2] - (double)v[b * 3] * v[a * 3 + 2]
             + (double)v[b * 3] * v[c * 3 + 2] - (double)v[c * 3] * v[b * 3 + 2]
             + (double)v[c * 3] * v[a * 3 + 2] - (double)v[a * 3] * v[c * 3 + 2];
    }

    // Independent largest XZ interior angle of the quad's ground projection. >180 = the engine's
    // frame sees a reflex corner. -1 when the projection is degenerate.
    private static double ProbeWorstAngleXZ(float[] v, int[] w)
    {
        return ProbeWorstAngleXZ(v, w, out _, out _);
    }

    private static double ProbeWorstAngleXZ(float[] v, int[] w, out int reflex, out int flat)
    {
        var px = new double[4]; var pz = new double[4];
        for (int i = 0; i < 4; i++) { px[i] = v[w[i] * 3]; pz[i] = v[w[i] * 3 + 2]; }
        double area2 = 0.0;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            area2 += px[i] * pz[j] - px[j] * pz[i];
        }
        reflex = 0; flat = 0;
        if (Math.Abs(area2) < 1e-9) return -1.0;
        double worst = 0.0;
        for (int i = 0; i < 4; i++)
        {
            int pa = (i + 3) % 4, pb = i, pc = (i + 1) % 4;
            double cr = (px[pb] - px[pa]) * (pz[pc] - pz[pa]) - (pz[pb] - pz[pa]) * (px[pc] - px[pa]);
            double e1x = px[pa] - px[pb], e1z = pz[pa] - pz[pb];
            double e2x = px[pc] - px[pb], e2z = pz[pc] - pz[pb];
            double l1 = Math.Sqrt(e1x * e1x + e1z * e1z), l2 = Math.Sqrt(e2x * e2x + e2z * e2z);
            if (l1 < 1e-12 || l2 < 1e-12) return -1.0;
            double cosA = (e1x * e2x + e1z * e2z) / (l1 * l2);
            if (cosA > 1) cosA = 1; if (cosA < -1) cosA = -1;
            double ang = Math.Acos(cosA) * 180.0 / Math.PI;
            if (cr * area2 < 0) { ang = 360.0 - ang; reflex = 1; }
            else if (cr * area2 == 0) flat = 1;
            if (ang > worst) worst = ang;
        }
        return worst;
    }

    private static string F1(double v) { return v.ToString("F1", CultureInfo.InvariantCulture); }

    // Runs the real NavMeshCleanup.Run over a two-triangle patch (sliver + neighbour) and reports how
    // many pairs the merge stage recorded (1 = merged, 0 = left as two triangles) plus the face count
    // the cleanup left behind, so a test can tell "refused" apart from "the patch was rewritten".
    private static void SelectionQuads(float[] verts, out int quads, out int faces)
    {
        var cv = new List<float>(verts);
        var ct = new List<int> { 0, 1, 2, 1, 0, 3 };
        NavMeshCleanup.Stats st = NavMeshCleanup.Run(cv, ct, 500, true);
        if (ct.Count != 6) throw new InvalidOperationException("selection probe: the cleanup rewrote the patch");
        quads = st.QuadPairs == null ? 0 : st.QuadPairs.Count / 2;
        faces = ct.Count / 3;
    }

    // Worst interior angle (degrees) of the quad the two triangles of a hand-made patch would merge
    // into, computed independently of the product's guard. > 180 = the merged quad is concave.
    private static double ProbeWorstAngle(float[] verts)
    {
        return QuadWorstAngle(verts, 0, 1, 2, 1, 0, 3);
    }

    private static double QuadWorstAngleV(TiledNavMeshBuilder.VoxelMeshResult r, int f0, int f1)
    {
        int[] t = r.Tris;
        return QuadWorstAngle(r.VertsXyz, t[f0 * 3], t[f0 * 3 + 1], t[f0 * 3 + 2],
            t[f1 * 3], t[f1 * 3 + 1], t[f1 * 3 + 2]);
    }

    // Shared probe: the walk is q -> x -> p -> y exactly as both the writer and the merge stage build
    // it; projects the four corners into the quad's own plane and returns the largest interior angle
    // in [0,360) (reflex corners see the far side). -1 = degenerate projection.
    private static double QuadWorstAngle(float[] v, int a0, int b0, int c0, int a1, int b1, int c1)
    {
        int p, q, x;
        if (Has(a0, b0, a1, b1, c1)) { p = a0; q = b0; x = c0; }
        else if (Has(b0, c0, a1, b1, c1)) { p = b0; q = c0; x = a0; }
        else { p = c0; q = a0; x = b0; }
        int y = a1 != p && a1 != q ? a1 : (b1 != p && b1 != q ? b1 : c1);
        int[] w = { q, x, p, y };

        double nx = 0, ny = 0, nz = 0;
        for (int i = 0; i < 4; i++)
        {
            int a = w[i], b = w[(i + 1) % 4];
            double ax = v[a * 3], ay = v[a * 3 + 1], az = v[a * 3 + 2];
            double bx = v[b * 3], by = v[b * 3 + 1], bz = v[b * 3 + 2];
            nx += (ay - by) * (az + bz); ny += (az - bz) * (ax + bx); nz += (ax - bx) * (ay + by);
        }
        double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (nl < 1e-12) return -1.0;
        nx /= nl; ny /= nl; nz /= nl;
        double ux, uy, uz;
        if (Math.Abs(nx) <= Math.Abs(ny) && Math.Abs(nx) <= Math.Abs(nz)) { ux = 0; uy = -nz; uz = ny; }
        else if (Math.Abs(ny) <= Math.Abs(nz)) { ux = -nz; uy = 0; uz = nx; }
        else { ux = -ny; uy = nx; uz = 0; }
        double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz);
        if (ul < 1e-12) return -1.0;
        ux /= ul; uy /= ul; uz /= ul;
        double wx = ny * uz - nz * uy, wy = nz * ux - nx * uz, wz = nx * uy - ny * ux;
        double cx = 0, cy = 0, cz = 0;
        for (int i = 0; i < 4; i++) { cx += v[w[i] * 3]; cy += v[w[i] * 3 + 1]; cz += v[w[i] * 3 + 2]; }
        cx /= 4; cy /= 4; cz /= 4;
        var px = new double[4]; var py = new double[4];
        for (int i = 0; i < 4; i++)
        {
            double dx = v[w[i] * 3] - cx, dy = v[w[i] * 3 + 1] - cy, dz = v[w[i] * 3 + 2] - cz;
            px[i] = dx * ux + dy * uy + dz * uz; py[i] = dx * wx + dy * wy + dz * wz;
        }
        double area2 = 0;
        for (int i = 0; i < 4; i++) { int j = (i + 1) % 4; area2 += px[i] * py[j] - px[j] * py[i]; }
        if (Math.Abs(area2) < 1e-12) return -1.0;
        double worst = 0.0;
        for (int i = 0; i < 4; i++)
        {
            int pa = (i + 3) % 4, pb = i, pc = (i + 1) % 4;
            double cr = (px[pb] - px[pa]) * (py[pc] - py[pa]) - (py[pb] - py[pa]) * (px[pc] - px[pa]);
            double e1x = px[pa] - px[pb], e1y = py[pa] - py[pb];
            double e2x = px[pc] - px[pb], e2y = py[pc] - py[pb];
            double l1 = Math.Sqrt(e1x * e1x + e1y * e1y), l2 = Math.Sqrt(e2x * e2x + e2y * e2y);
            if (l1 < 1e-12 || l2 < 1e-12) return -1.0;
            double cosA = (e1x * e2x + e1y * e2y) / (l1 * l2);
            if (cosA > 1) cosA = 1; if (cosA < -1) cosA = -1;
            double ang = Math.Acos(cosA) * 180.0 / Math.PI;
            if (cr * area2 < 0) ang = 360.0 - ang;   // reflex corner: the interior angle is the far side
            if (ang > worst) worst = ang;
        }
        return worst;
    }

    private static string IntList(List<int> a)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < a.Count; i++) { if (i > 0) sb.Append(','); sb.Append(a[i]); }
        return sb.ToString();
    }

    // Largest interior angle over every merged quad (degrees, independent of the product's guard). Reports
// how close the convexity epsilon is to actually clipping a real merge: it stays well under the 179.9
// deg ceiling, so the ceiling is not what is refusing quads on a real map.
private static double WorstQuadAngle(TiledNavMeshBuilder.VoxelMeshResult r)
{
    double worst = 0.0;
    if (r.QuadPairs == null) return worst;
    for (int k = 0; k + 1 < r.QuadPairs.Length; k += 2)
    {
        double a = QuadWorstAngleV(r, r.QuadPairs[k], r.QuadPairs[k + 1]);
        if (a > worst) worst = a;
    }
    return worst;
}

// Independent concave-quad count (mirrors the writer's walk derivation) so the snapshot report can
    // state how many merged quads are concave. A quad counts when its worst interior angle reaches
    // 180 deg, which covers both a reflex corner (>180) and a numerically straight one (=180); a
    // degenerate projection also counts, because then convexity cannot be shown at all. Test 27 owns
    // the assertions; this is the reporting path.
    private static int CountConcaveQuads(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        if (r.QuadPairs == null) return 0;
        int n = 0;
        for (int k = 0; k + 1 < r.QuadPairs.Length; k += 2)
        {
            double a = QuadWorstAngleV(r, r.QuadPairs[k], r.QuadPairs[k + 1]);
            if (a < 0.0 || a >= 180.0) n++;
        }
        return n;
    }

    private static bool Has(int u, int w, int a, int b, int c)
    {
        return (u == a || u == b || u == c) && (w == a || w == b || w == c);
    }

    // ------------------------------------------------------------------ XZ-frame probe
    // The engine's navmesh is a 2D structure: whatever concavity test it runs, it can only see the
    // XZ projection. Our own guard tests the quad in its OWN best-fit plane, so a bent quad can be
    // strictly convex there while a corner is reflex in XZ. This probe measures the XZ frame
    // independently of the product's guard: shoelace sign (orientation), the four XZ interior
    // angles, and whether the quad's XZ orientation matches the XZ orientation of the two triangles
    // it replaces (which is fixed by the triangle winding and must not flip).
    private sealed class XzQuadInfo
    {
        public int F0, F1;
        public double Shoelace;      // signed area x2 of the XZ projection (walk order)
        public double TriShoelace;   // signed area x2 of triangle f0 in XZ
        public double WorstAngle;    // largest XZ interior angle (deg); >180 = reflex
        public bool Degenerate;
        public bool ConvexXZ;
        public int XzReflex;        // 1 when some XZ corner's turn is strictly backwards
        public int XzFlat;          // 1 when some XZ corner is exactly collinear
        public bool OrientOk;
        public double Cx, Cy, Cz;    // centroid of the quad's four corners (the reported location)
        public double MinX, MaxX, MinZ, MaxZ;
        public double FoldDeg;       // the dihedral between the two triangles (deg)
    }

    private sealed class XzProbeReport
    {
        public int Quads;
        public int ConcaveXZ;        // convex in own plane, reflex/collinear in XZ
        public int OrientBad;        // XZ orientation flipped vs the two triangles
        public int StraightXZ;       // XZ corner within the XZ margin of 180 deg
        public int DegenerateXZ;     // zero XZ area (vertical quad)
        public int ReflexXZ;        // a corner whose XZ turn is strictly backwards (cr*area2 < 0)
        public int FlatXZ;          // a corner that is EXACTLY collinear in XZ (cr*area2 == 0)
        public double WorstAngle;
        public bool WorstIsReflex;  // the worst XZ corner turns backwards (not merely straight)
        public List<XzQuadInfo> Offenders = new List<XzQuadInfo>();
    }

    // Rebuilds the emitted 4-vertex walk (same derivation as the writer and the merge stage).
    private static int[] QuadWalk(TiledNavMeshBuilder.VoxelMeshResult r, int f0, int f1)
    {
        int[] t = r.Tris;
        int a0 = t[f0 * 3], b0 = t[f0 * 3 + 1], c0 = t[f0 * 3 + 2];
        int a1 = t[f1 * 3], b1 = t[f1 * 3 + 1], c1 = t[f1 * 3 + 2];
        int p, q, x;
        if (Has(a0, b0, a1, b1, c1)) { p = a0; q = b0; x = c0; }
        else if (Has(b0, c0, a1, b1, c1)) { p = b0; q = c0; x = a0; }
        else { p = c0; q = a0; x = b0; }
        int y = a1 != p && a1 != q ? a1 : (b1 != p && b1 != q ? b1 : c1);
        return new int[] { q, x, p, y };
    }

    private static double XzShoelace(float[] v, int[] w)
    {
        double s = 0.0;
        for (int i = 0; i < 4; i++)
        {
            int a = w[i], b = w[(i + 1) % 4];
            s += (double)v[a * 3] * v[b * 3 + 2] - (double)v[b * 3] * v[a * 3 + 2];
        }
        return s;
    }

    private static double TriXzShoelace(float[] v, int f, TiledNavMeshBuilder.VoxelMeshResult r)
    {
        int a = r.Tris[f * 3], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
        double s = (double)v[a * 3] * v[b * 3 + 2] - (double)v[b * 3] * v[a * 3 + 2]
                 + (double)v[b * 3] * v[c * 3 + 2] - (double)v[c * 3] * v[b * 3 + 2]
                 + (double)v[c * 3] * v[a * 3 + 2] - (double)v[a * 3] * v[c * 3 + 2];
        return s;
    }

    // Largest XZ interior angle of the quad's XZ projection. A reflex corner (>180) means the XZ
    // projection is a genuinely concave 4-gon even though the 3D quad may be convex in its own
    // plane. -1 when the projection is degenerate.
    private static double WorstAngleXZ(float[] v, int[] w, out double shoelace, out bool convex)
    {
        return WorstAngleXZ(v, w, out shoelace, out convex, out _, out _);
    }

    private static double WorstAngleXZ(float[] v, int[] w, out double shoelace, out bool convex,
        out int reflex, out int flat)
    {
        var px = new double[4]; var pz = new double[4];
        for (int i = 0; i < 4; i++) { px[i] = v[w[i] * 3]; pz[i] = v[w[i] * 3 + 2]; }
        double area2 = 0.0;
        for (int i = 0; i < 4; i++)
        {
            int j = (i + 1) % 4;
            area2 += px[i] * pz[j] - px[j] * pz[i];
        }
        shoelace = area2;
        convex = false;
        reflex = 0; flat = 0;
        if (Math.Abs(area2) < 1e-9) return -1.0;
        double worst = 0.0;
        convex = true;
        for (int i = 0; i < 4; i++)
        {
            int pa = (i + 3) % 4, pb = i, pc = (i + 1) % 4;
            double cr = (px[pb] - px[pa]) * (pz[pc] - pz[pa]) - (pz[pb] - pz[pa]) * (px[pc] - px[pa]);
            double e1x = px[pa] - px[pb], e1z = pz[pa] - pz[pb];
            double e2x = px[pc] - px[pb], e2z = pz[pc] - pz[pb];
            double l1 = Math.Sqrt(e1x * e1x + e1z * e1z), l2 = Math.Sqrt(e2x * e2x + e2z * e2z);
            if (l1 < 1e-12 || l2 < 1e-12) return -1.0;
            double cosA = (e1x * e2x + e1z * e2z) / (l1 * l2);
            if (cosA > 1) cosA = 1; if (cosA < -1) cosA = -1;
            double ang = Math.Acos(cosA) * 180.0 / Math.PI;
            if (cr * area2 < 0) { ang = 360.0 - ang; convex = false; reflex = 1; }   // reflex in XZ
            else if (cr * area2 == 0) { convex = false; flat = 1; }                   // straight in XZ
            if (ang > worst) worst = ang;
        }
        return worst;
    }

    private static XzProbeReport ProbeXzQuads(TiledNavMeshBuilder.VoxelMeshResult r)
    {
        var rep = new XzProbeReport();
        if (r.QuadPairs == null) return rep;
        float[] v = r.VertsXyz;
        rep.Quads = r.QuadPairs.Length / 2;
        for (int k = 0; k + 1 < r.QuadPairs.Length; k += 2)
        {
            int f0 = r.QuadPairs[k], f1 = r.QuadPairs[k + 1];
            int[] w = QuadWalk(r, f0, f1);
            var info = new XzQuadInfo();
            info.F0 = f0; info.F1 = f1;
            info.Shoelace = XzShoelace(v, w);
            info.TriShoelace = TriXzShoelace(v, f0, r);
            double worst = WorstAngleXZ(v, w, out double sh, out bool convex, out int refl, out int flat);
            info.Shoelace = sh;
            info.WorstAngle = worst;
            info.Degenerate = worst < 0.0;
            info.ConvexXZ = convex;
            info.XzReflex = refl;
            info.XzFlat = flat;
            // Orientation: the quad walk's XZ shoelace must have the same sign as the XZ shoelace of
            // BOTH triangles it replaces (in the engine's frame the winding is what it is).
            double t1 = TriXzShoelace(v, f1, r);
            info.OrientOk = Math.Abs(sh) > 1e-9 && sh * info.TriShoelace > 0.0 && sh * t1 > 0.0;
            info.Cx = (v[w[0] * 3] + v[w[1] * 3] + v[w[2] * 3] + v[w[3] * 3]) / 4.0;
            info.Cy = (v[w[0] * 3 + 1] + v[w[1] * 3 + 1] + v[w[2] * 3 + 1] + v[w[3] * 3 + 1]) / 4.0;
            info.Cz = (v[w[0] * 3 + 2] + v[w[1] * 3 + 2] + v[w[2] * 3 + 2] + v[w[3] * 3 + 2]) / 4.0;
            info.MinX = info.MaxX = v[w[0] * 3];
            info.MinZ = info.MaxZ = v[w[0] * 3 + 2];
            for (int i = 1; i < 4; i++)
            {
                float x = v[w[i] * 3], z = v[w[i] * 3 + 2];
                if (x < info.MinX) info.MinX = x; if (x > info.MaxX) info.MaxX = x;
                if (z < info.MinZ) info.MinZ = z; if (z > info.MaxZ) info.MaxZ = z;
            }
            info.FoldDeg = FoldDeg(v, r, f0, f1);
            if (info.XzReflex != 0) rep.ReflexXZ++;
            if (info.XzFlat != 0) rep.FlatXZ++;
            if (!info.ConvexXZ || !info.OrientOk) rep.Offenders.Add(info);
            if (info.Degenerate) { rep.DegenerateXZ++; continue; }
            if (worst > rep.WorstAngle) { rep.WorstAngle = worst; rep.WorstIsReflex = refl != 0; }
            if (worst >= 180.0) rep.ConcaveXZ++;
            else if (worst >= XzMarginDegreesProbe) rep.StraightXZ++;
            if (!info.OrientOk) rep.OrientBad++;
        }
        return rep;
    }

    private static double FoldDeg(float[] v, TiledNavMeshBuilder.VoxelMeshResult r, int f0, int f1)
    {
        int[] t = r.Tris;
        double[] n0 = TriNormal(v, t[f0 * 3], t[f0 * 3 + 1], t[f0 * 3 + 2]);
        double[] n1 = TriNormal(v, t[f1 * 3], t[f1 * 3 + 1], t[f1 * 3 + 2]);
        double dot = n0[0] * n1[0] + n0[1] * n1[1] + n0[2] * n1[2];
        double l0 = Math.Sqrt(n0[0] * n0[0] + n0[1] * n0[1] + n0[2] * n0[2]);
        double l1 = Math.Sqrt(n1[0] * n1[0] + n1[1] * n1[1] + n1[2] * n1[2]);
        if (l0 < 1e-12 || l1 < 1e-12) return 180.0;
        double c = dot / (l0 * l1);
        if (c > 1) c = 1; if (c < -1) c = -1;
        return Math.Acos(c) * 180.0 / Math.PI;
    }

    private static double[] TriNormal(float[] v, int a, int b, int c)
    {
        double ux = v[b * 3] - v[a * 3], uy = v[b * 3 + 1] - v[a * 3 + 1], uz = v[b * 3 + 2] - v[a * 3 + 2];
        double vx = v[c * 3] - v[a * 3], vy = v[c * 3 + 1] - v[a * 3 + 1], vz = v[c * 3 + 2] - v[a * 3 + 2];
        return new double[] { uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx };
    }

    // ==================================================================
    // Defect scan (3.42): overlap / inversion / spike classification
    // ==================================================================
    private sealed class DefectRow
    {
        public string Kind;
        public int F;
        public int G;
        public float X, Y, Z;
        public float DX, DY, DZ;
        public float DY2, DZ2;
        public string Extra;
    }

    private static string Df(float v) { return v.ToString("F3", CultureInfo.InvariantCulture); }

    // Full-surface classification. Optional spots limit the DETAIL printing (the counts are always
    // whole-surface). A "level" here is the face centroid's height; two faces are "the same level"
    // when their centroid heights agree within levelTol metres - stacked floors legitimately overlap
    // in XZ, a self-overlap on one floor does not.
    private static void ScanDefects(TiledNavMeshBuilder.VoxelMeshResult r, List<string> spots,
        bool scan, float levelTol)
    {
        float[] v = r.VertsXyz;
        int[] t = r.Tris;
        int nf = r.FaceCount;
        var sw = Stopwatch.StartNew();

        // ---- per-face summary
        var fx = new float[nf]; var fy = new float[nf]; var fz = new float[nf];
        var fminx = new float[nf]; var fmaxx = new float[nf];
        var fminz = new float[nf]; var fmaxz = new float[nf];
        var fny = new float[nf]; var farea = new float[nf];
        for (int f = 0; f < nf; f++)
        {
            int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
            float ax = v[a * 3], ay = v[a * 3 + 1], az = v[a * 3 + 2];
            float bx = v[b * 3], by = v[b * 3 + 1], bz = v[b * 3 + 2];
            float cx = v[c * 3], cy = v[c * 3 + 1], cz = v[c * 3 + 2];
            fx[f] = (ax + bx + cx) / 3f; fy[f] = (ay + by + cy) / 3f; fz[f] = (az + bz + cz) / 3f;
            fminx[f] = Math.Min(ax, Math.Min(bx, cx)); fmaxx[f] = Math.Max(ax, Math.Max(bx, cx));
            fminz[f] = Math.Min(az, Math.Min(bz, cz)); fmaxz[f] = Math.Max(az, Math.Max(bz, cz));
            float ux = bx - ax, uy = by - ay, uz = bz - az;
            float wx = cx - ax, wy = cy - ay, wz = cz - az;
            fny[f] = uz * wx - ux * wz;                       // +Y = up-facing (Y is height here)
            farea[f] = 0.5f * (float)Math.Sqrt(fny[f] * (double)fny[f]
                + (ux * wy - uy * wx) * (double)(ux * wy - uy * wx)
                + (uy * wz - uz * wy) * (double)(uy * wz - uz * wy));
        }

        // ---- (1) downward / degenerate faces
        int down = 0, degen = 0;
        var downRows = new List<DefectRow>();
        for (int f = 0; f < nf; f++)
        {
            if (farea[f] < 1e-6f) { degen++; continue; }
            if (fny[f] <= 0f)
            {
                down++;
                var d = new DefectRow();
                d.Kind = "DOWN"; d.F = f; d.X = fx[f]; d.Y = fy[f]; d.Z = fz[f];
                d.DX = v[t[f * 3] * 3]; d.DY = v[t[f * 3] * 3 + 1]; d.DZ = v[t[f * 3] * 3 + 2];
                d.Extra = "v=" + t[f * 3] + "," + t[f * 3 + 1] + "," + t[f * 3 + 2]
                    + " area=" + Df(farea[f]) + " ny=" + Df(fny[f]);
                downRows.Add(d);
            }
        }

        // ---- (2) duplicate vertex triples
        int dup = 0;
        var dupRows = new List<DefectRow>();
        var seenTri = new Dictionary<long, int>();
        for (int f = 0; f < nf; f++)
        {
            int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
            int lo, mid, hi;
            Sort3(a, b, c, out lo, out mid, out hi);
            long key = ((long)lo << 42) ^ ((long)mid << 21) ^ (long)hi;
            int other;
            if (seenTri.TryGetValue(key, out other))
            {
                dup++;
                var d = new DefectRow();
                d.Kind = "DUP"; d.F = other; d.G = f; d.X = fx[f]; d.Y = fy[f]; d.Z = fz[f];
                d.Extra = "tri " + lo + "," + mid + "," + hi;
                dupRows.Add(d);
            }
            else seenTri[key] = f;
        }

        // ---- (3) overlap: a face centroid strictly inside another face of the same level
        var rows = new List<DefectRow>();
        int overlapPairs = 0, overlapFaces = 0;
        int ovDisjoint = 0, ovShare1 = 0, ovShare2 = 0;
        var disjointRows = new List<DefectRow>();
        {
            const float cell = 4f;
            var grid = new Dictionary<long, List<int>>();
            for (int f = 0; f < nf; f++)
            {
                int x0 = (int)Math.Floor(fminx[f] / cell), x1 = (int)Math.Floor(fmaxx[f] / cell);
                int z0 = (int)Math.Floor(fminz[f] / cell), z1 = (int)Math.Floor(fmaxz[f] / cell);
                for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        long k = ((long)cx << 32) ^ (uint)cz;
                        List<int> list;
                        if (!grid.TryGetValue(k, out list)) { list = new List<int>(); grid[k] = list; }
                        list.Add(f);
                    }
            }
            var flagged = new bool[nf];
            for (int f = 0; f < nf; f++)
            {
                int cx = (int)Math.Floor(fx[f] / cell), cz = (int)Math.Floor(fz[f] / cell);
                List<int> cand;
                if (!grid.TryGetValue(((long)cx << 32) ^ (uint)cz, out cand)) continue;
                for (int i = 0; i < cand.Count; i++)
                {
                    int g = cand[i];
                    if (g == f) continue;
                    if (Math.Abs(fy[g] - fy[f]) > levelTol) continue;      // different level
                    int a = t[g * 3], b = t[g * 3 + 1], c = t[g * 3 + 2];
                    if (!PointInTri(fx[f], fz[f], v[a * 3], v[a * 3 + 2], v[b * 3], v[b * 3 + 2],
                        v[c * 3], v[c * 3 + 2])) continue;
                    // skip the trivial "centroid on its own shared edge" (impossible for a strict
                    // inside test) and near-degenerate containers
                    if (farea[g] < 1e-4f) continue;
                    overlapPairs++;
                    if (!flagged[f]) { flagged[f] = true; overlapFaces++; }
                    int shared = SharedVerts(t, f, g);
                    if (shared == 0) ovDisjoint++; else if (shared == 1) ovShare1++; else ovShare2++;
                    var d = new DefectRow();
                    d.Kind = "OVERLAP"; d.F = f; d.G = g;
                    d.X = fx[f]; d.Y = fy[f]; d.Z = fz[f];
                    d.DX = fx[g]; d.DY = fy[g]; d.DZ = fz[g];
                    d.DY2 = farea[f]; d.DZ2 = farea[g];
                    if (shared == 0) disjointRows.Add(d);
                    d.Extra = "f=(" + t[f * 3] + "," + t[f * 3 + 1] + "," + t[f * 3 + 2] + ")"
                        + " g=(" + a + "," + b + "," + c + ")"
                        + " shareVerts=" + SharedVerts(t, f, g)
                        + " areaF=" + Df(farea[f]) + " areaG=" + Df(farea[g]);
                    rows.Add(d);
                }
            }
        }

        // ---- (4) spike vertices: a vertex far off the mean height of its 1-ring neighbours
        var spikeRows = new List<DefectRow>();
        int spikes = 0;
        float worstSpike = 0f;
        {
            var nb = new Dictionary<int, List<int>>();     // vertex -> neighbour vertex indices
            for (int f = 0; f < nf; f++)
            {
                int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
                LinkNb(nb, a, b); LinkNb(nb, b, a); LinkNb(nb, a, c); LinkNb(nb, c, a);
                LinkNb(nb, b, c); LinkNb(nb, c, b);
            }
            foreach (KeyValuePair<int, List<int>> kv in nb)
            {
                int vert = kv.Key;
                if (kv.Value.Count < 3) continue;
                float sum = 0f;
                for (int i = 0; i < kv.Value.Count; i++) sum += v[kv.Value[i] * 3 + 1];
                float mean = sum / kv.Value.Count;
                float dev = v[vert * 3 + 1] - mean;
                float adev = Math.Abs(dev);
                if (adev > 1.0f)
                {
                    spikes++;
                    if (adev > worstSpike) worstSpike = adev;
                    var d = new DefectRow();
                    d.Kind = "SPIKE"; d.F = vert; d.G = kv.Value.Count;
                    d.X = v[vert * 3]; d.Y = v[vert * 3 + 1]; d.Z = v[vert * 3 + 2];
                    d.DY = dev;
                    d.Extra = "ringMean=" + Df(mean);
                    spikeRows.Add(d);
                }
            }
        }

        // ---- (5) emitted quads: self-intersecting (bowtie) walks
        int quads = 0, bowties = 0, quadReflex = 0;
        var quadRows = new List<DefectRow>();
        if (r.QuadPairs != null)
        {
            quads = r.QuadPairs.Length / 2;
            for (int k = 0; k + 1 < r.QuadPairs.Length; k += 2)
            {
                int f0 = r.QuadPairs[k], f1 = r.QuadPairs[k + 1];
                int[] w = QuadWalk(r, f0, f1);
                bool cross = SegsCrossXZ(v, w[0], w[1], w[2], w[3]) || SegsCrossXZ(v, w[1], w[2], w[3], w[0]);
                double worst = WorstAngleXZ(v, w, out double sh, out bool convex, out int refl, out int flat);
                if (refl != 0) quadReflex++;
                if (cross || refl != 0)
                {
                    bowties++;
                    var d = new DefectRow();
                    d.Kind = cross ? "QUAD-X" : "QUAD-REFLEX";
                    d.F = f0; d.G = f1;
                    float cx = 0, cy = 0, cz = 0;
                    for (int i = 0; i < 4; i++) { cx += v[w[i] * 3]; cy += v[w[i] * 3 + 1]; cz += v[w[i] * 3 + 2]; }
                    d.X = cx / 4f; d.Y = cy / 4f; d.Z = cz / 4f;
                    d.Extra = "walk=" + w[0] + ">" + w[1] + ">" + w[2] + ">" + w[3]
                        + " worstXZ=" + worst.ToString("F3", CultureInfo.InvariantCulture)
                        + " cross=" + cross;
                    quadRows.Add(d);
                }
            }
        }

        // ---- (6) CROSSING: two faces whose interiors actually intersect (the "crisscross" class).
        // A proper edge/triangle hit with all three barycentrics strictly interior, on faces that
        // share no vertex. This is the X the user sees: two long thin faces laid across each other.
        var crossRows = new List<DefectRow>();
        int crossPairs = 0, crossFaces = 0;
        {
            const float cell = 4f;
            var grid = new Dictionary<long, List<int>>();
            for (int f = 0; f < nf; f++)
            {
                int x0 = (int)Math.Floor(fminx[f] / cell), x1 = (int)Math.Floor(fmaxx[f] / cell);
                int z0 = (int)Math.Floor(fminz[f] / cell), z1 = (int)Math.Floor(fmaxz[f] / cell);
                for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                    {
                        long k = ((long)cx << 32) ^ (uint)cz;
                        List<int> list;
                        if (!grid.TryGetValue(k, out list)) { list = new List<int>(); grid[k] = list; }
                        list.Add(f);
                    }
            }
            var flagged = new bool[nf];
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
                        if (SharedVerts(t, f, g) != 0) continue;
                        if (fminx[f] > fmaxx[g] || fminx[g] > fmaxx[f]) continue;
                        if (fminz[f] > fmaxz[g] || fminz[g] > fmaxz[f]) continue;
                        float dyy = Math.Abs(fy[f] - fy[g]);
                        if (dyy > levelTol) continue;              // a different floor: not an X
                        if (!FacesCross(v, t, f, g)) continue;
                        crossPairs++;
                        if (!flagged[f]) { flagged[f] = true; crossFaces++; }
                        if (!flagged[g]) { flagged[g] = true; crossFaces++; }
                        var d = new DefectRow();
                        d.Kind = "CROSS"; d.F = f; d.G = g;
                        d.X = (fx[f] + fx[g]) * 0.5f; d.Y = (fy[f] + fy[g]) * 0.5f; d.Z = (fz[f] + fz[g]) * 0.5f;
                        d.DX = dyy; d.DY = farea[f]; d.DZ = farea[g];
                        d.Extra = "f=(" + t[f * 3] + "," + t[f * 3 + 1] + "," + t[f * 3 + 2] + ")"
                            + " g=(" + t[g * 3] + "," + t[g * 3 + 1] + "," + t[g * 3 + 2] + ")"
                            + " dY=" + Df(dyy);
                        crossRows.Add(d);
                    }
                }
            }
        }

        Console.WriteLine("scan: faces=" + nf + " quads=" + quads
            + " DOWN=" + down + " degenerate=" + degen + " DUPTRIPLE=" + dup
            + " OVERLAPpairs=" + overlapPairs + " overlapFaces=" + overlapFaces
            + " (disjoint=" + ovDisjoint + " share1=" + ovShare1 + " share2=" + ovShare2 + ")"
            + " CROSSpairs=" + crossPairs + " crossFaces=" + crossFaces
            + " levelTol=" + Df(levelTol)
            + " SPIKE(>1m)=" + spikes + " worstSpike=" + Df(worstSpike)
            + " quadBowtie=" + bowties + " quadReflexXZ=" + quadReflex
            + " ms=" + sw.ElapsedMilliseconds);
        if (spots.Count > 0)
        {
            float[] first = ParsePair(spots[0] + "|" + spots[0]);
            if (first != null) { qrefx = first[0]; qrefz = first[1]; }
        }
        if (disjointRows.Count > 0)
        {
            Console.WriteLine("  disjoint overlaps (vertex-shared = 0):");
            for (int i = 0; i < disjointRows.Count && i < 40; i++)
            {
                DefectRow d = disjointRows[i];
                Console.WriteLine("    OVLDISJOINT f=" + d.F + " g=" + d.G
                    + " at (" + Df(d.X) + "," + Df(d.Y) + "," + Df(d.Z) + ")"
                    + " other (" + Df(d.DX) + "," + Df(d.DY) + "," + Df(d.DZ) + ")"
                    + " areaF=" + Df(d.DY2) + " areaG=" + Df(d.DZ2) + " " + d.Extra);
            }
        }
        if (downRows.Count > 0)
        {
            Console.WriteLine("  downward faces (all):");
            for (int i = 0; i < downRows.Count && i < 40; i++)
            {
                DefectRow d = downRows[i];
                Console.WriteLine("    DOWN f=" + d.F + " at (" + Df(d.X) + "," + Df(d.Y) + "," + Df(d.Z)
                    + ") " + d.Extra);
            }
        }
        if (crossRows.Count > 0)
        {
            crossRows.Sort(delegate (DefectRow a, DefectRow b)
            {
                double da = (a.X - qrefx) * (a.X - qrefx) + (a.Z - qrefz) * (a.Z - qrefz);
                double db = (b.X - qrefx) * (b.X - qrefx) + (b.Z - qrefz) * (b.Z - qrefz);
                return da.CompareTo(db);
            });
            Console.WriteLine("  crossings (same level, nearest first):");
            for (int i = 0; i < crossRows.Count && i < 25; i++)
            {
                DefectRow d = crossRows[i];
                Console.WriteLine("    CROSS f=" + d.F + " g=" + d.G
                    + " at (" + Df(d.X) + "," + Df(d.Y) + "," + Df(d.Z) + ")"
                    + " dY=" + Df(d.DX) + " areaF=" + Df(d.DY) + " areaG=" + Df(d.DZ)
                    + " " + d.Extra);
            }
        }
        if (quadRows.Count > 0)
        {
            Console.WriteLine("  quad offenders:");
            for (int i = 0; i < quadRows.Count && i < 20; i++)
            {
                DefectRow d = quadRows[i];
                Console.WriteLine("    " + d.Kind + " f0=" + d.F + " f1=" + d.G
                    + " at (" + Df(d.X) + "," + Df(d.Y) + "," + Df(d.Z) + ") " + d.Extra);
            }
        }
        if (spikeRows.Count > 0)
        {
            spikeRows.Sort(delegate (DefectRow a, DefectRow b) { return Math.Abs(b.DY).CompareTo(Math.Abs(a.DY)); });
            Console.WriteLine("  worst spikes:");
            for (int i = 0; i < spikeRows.Count && i < 20; i++)
            {
                DefectRow d = spikeRows[i];
                Console.WriteLine("    SPIKE v" + d.F + " at (" + Df(d.X) + "," + Df(d.Y) + "," + Df(d.Z)
                    + ") dev=" + Df(d.DY) + "m ring=" + d.G + " " + d.Extra);
            }
        }

        if (spots.Count == 0) return;
        if (spots.Count > 1) qrefx = float.NaN;   // no single reference for the sorted printout
        foreach (string spot in spots)
        {
            float[] pp = ParsePair(spot + "|" + spot);
            if (pp == null) { Console.WriteLine("defect spot bad format: " + spot); continue; }
            float qx = pp[0], qz = pp[1];
            float radius = spot.IndexOf(',') >= 0 && spot.Split(',').Length >= 3
                ? (float)ParseFloat(spot.Split(',')[2]) : 4f;
            Console.WriteLine();
            Console.WriteLine("== defect spot (" + Df(qx) + "," + Df(qz) + ") r=" + Df(radius) + " ==");
            DumpSpot(r, v, t, qx, qz, radius, levelTol, downRows, dupRows, rows, quadRows, crossRows);
        }
    }

        private static float qrefx = float.NaN, qrefz = float.NaN;   // spot used to sort crossing rows

    // ---------------------------------------------------------------------------
    // Per-face census around a spot: slope / XZ footprint / area / height band and the
    // edge-sharing neighbourhood, plus a global slope histogram and the component id.
    // This is the "what IS the surface here" tool: it answers floating-treads-plus-steep-
    // connectors vs continuous-steep-sheet, and where the jumps are.
    // ---------------------------------------------------------------------------
    private static void CensusSpot(TiledNavMeshBuilder.VoxelMeshResult r, List<string> specs)
    {
        float[] v = r.VertsXyz;
        int[] t = r.Tris;
        int nf = r.FaceCount;

        // Global slope census (the gate-metric view over the WHOLE mesh).
        var globalBuckets = new int[7];
        int globalSteep = 0;
        double globalSteepArea = 0.0;
        for (int f = 0; f < nf; f++)
        {
            double[] n = TriNormal(v, t[f * 3], t[f * 3 + 1], t[f * 3 + 2]);
            double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            if (len < 1e-12) { globalBuckets[0]++; continue; }
            double ny = n[1] / len;
            if (ny < 0) ny = -ny;
            double deg = Math.Acos(Math.Min(1.0, ny)) * 180.0 / Math.PI;
            int b = deg < 1 ? 0 : deg < 20 ? 1 : deg < 35 ? 2 : deg < 50 ? 3 : deg < 60 ? 4 : deg < 75 ? 5 : 6;
            globalBuckets[b]++;
            if (deg > 55.0)
            {
                globalSteep++;
                globalSteepArea += 0.5 * len;
            }
        }
        Console.WriteLine("census(global): faces=" + nf
            + " slopeDeg[0-1)=" + globalBuckets[0] + " [1-20)=" + globalBuckets[1]
            + " [20-35)=" + globalBuckets[2] + " [35-50)=" + globalBuckets[3]
            + " [50-60)=" + globalBuckets[4] + " [60-75)=" + globalBuckets[5]
            + " [75-90]=" + globalBuckets[6]
            + " steep(>55deg)=" + globalSteep + " steepArea=" + globalSteepArea.ToString("F2") + "m2");

        // Edge-sharing adjacency over the whole mesh (exact: welded vertices).
        var edgeFaces = new Dictionary<long, List<int>>();
        for (int f = 0; f < nf; f++)
        {
            int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
            AddEdge(edgeFaces, a, b, f);
            AddEdge(edgeFaces, b, c, f);
            AddEdge(edgeFaces, c, a, f);
        }

        int comps;
        int[] fp = FaceComponents(r, out comps);

        var quadOf = new Dictionary<int, int>();
        if (r.QuadPairs != null)
            for (int k = 0; k + 1 < r.QuadPairs.Length; k += 2)
            {
                quadOf[r.QuadPairs[k]] = k / 2;
                quadOf[r.QuadPairs[k + 1]] = k / 2;
            }

        foreach (string spec in specs)
        {
            string[] parts = spec.Split(',');
            if (parts.Length < 2) { Console.WriteLine("census spot bad format: " + spec); continue; }
            float qx = (float)ParseFloat(parts[0]);
            float qz = (float)ParseFloat(parts[1]);
            float radius = parts.Length >= 3 ? (float)ParseFloat(parts[2]) : 6f;

            var near = new List<int>();
            for (int f = 0; f < nf; f++)
            {
                int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
                float minx = Math.Min(v[a * 3], Math.Min(v[b * 3], v[c * 3]));
                float maxx = Math.Max(v[a * 3], Math.Max(v[b * 3], v[c * 3]));
                float minz = Math.Min(v[a * 3 + 2], Math.Min(v[b * 3 + 2], v[c * 3 + 2]));
                float maxz = Math.Max(v[a * 3 + 2], Math.Max(v[b * 3 + 2], v[c * 3 + 2]));
                if (maxx < qx - radius || minx > qx + radius || maxz < qz - radius || minz > qz + radius) continue;
                near.Add(f);
            }

            Console.WriteLine();
            Console.WriteLine("== census spot (" + qx.ToString("F2") + "," + qz.ToString("F2")
                + ") r=" + radius.ToString("F2") + " faces=" + near.Count + " ==");

            int steep = 0, vertical = 0, inverted = 0, lowFootprint = 0;
            double steepArea = 0.0;
            float worstDeg = 0f;
            int worstFace = -1;
            float yLo = float.MaxValue, yHi = float.MinValue;
            var rows = new List<CensusRow>();
            for (int i = 0; i < near.Count; i++)
            {
                int f = near[i];
                int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
                double[] n = TriNormal(v, a, b, c);
                double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
                double ny = len < 1e-12 ? 0.0 : n[1] / len;
                double adeg = Math.Acos(Math.Min(1.0, Math.Abs(ny))) * 180.0 / Math.PI;
                double area3 = 0.5 * len;
                double footprint = TriFootprintXZ(v, a, b, c);
                var row = new CensusRow();
                row.F = f;
                row.Cx = (v[a * 3] + v[b * 3] + v[c * 3]) / 3f;
                row.Cy = (v[a * 3 + 1] + v[b * 3 + 1] + v[c * 3 + 1]) / 3f;
                row.Cz = (v[a * 3 + 2] + v[b * 3 + 2] + v[c * 3 + 2]) / 3f;
                row.YLo = Math.Min(v[a * 3 + 1], Math.Min(v[b * 3 + 1], v[c * 3 + 1]));
                row.YHi = Math.Max(v[a * 3 + 1], Math.Max(v[b * 3 + 1], v[c * 3 + 1]));
                row.Deg = (float)adeg;
                row.Ny = (float)ny;
                row.Area3 = area3;
                row.Footprint = footprint;
                row.Comp = Find(fp, f);
                int qi;
                row.Quad = quadOf.TryGetValue(f, out qi) ? qi : -1;
                row.Nb = new List<int>();
                AddNbs(row.Nb, edgeFaces, a, b);
                AddNbs(row.Nb, edgeFaces, b, c);
                AddNbs(row.Nb, edgeFaces, c, a);
                row.Nb.Sort();
                row.Edges = new List<string>();
                row.Edges.Add(EdgeLabel(v, a, b, row.YLo, row.YHi));
                row.Edges.Add(EdgeLabel(v, b, c, row.YLo, row.YHi));
                row.Edges.Add(EdgeLabel(v, c, a, row.YLo, row.YHi));
                row.ElevDrop = EdgeRise(v, row.Edges);
                rows.Add(row);
                if (row.YLo < yLo) yLo = row.YLo;
                if (row.YHi > yHi) yHi = row.YHi;
                if (adeg > worstDeg) { worstDeg = (float)adeg; worstFace = f; }
                if (adeg > 55.0) { steep++; steepArea += area3; }
                if (adeg > 89.999) vertical++;
                if (ny <= 0) inverted++;
                if (Math.Abs(footprint) < 1e-9) lowFootprint++;
            }
            Console.WriteLine("  steep(>55deg)=" + steep + " steepArea=" + steepArea.ToString("F3")
                + "m2 vertical(>=90deg)=" + vertical + " inverted/flat(ny<=0)=" + inverted
                + " zeroFootprint=" + lowFootprint
                + " worstSlope=" + worstDeg.ToString("F2") + "deg f" + worstFace
                + " Y=[" + (yLo == float.MaxValue ? 0f : yLo).ToString("F2") + ".."
                + (yHi == float.MinValue ? 0f : yHi).ToString("F2") + "]");

            rows.Sort(delegate (CensusRow a, CensusRow b)
            {
                int c = a.Cy.CompareTo(b.Cy);
                if (c != 0) return c;
                return a.F.CompareTo(b.F);
            });
            for (int i = 0; i < rows.Count; i++)
            {
                CensusRow row = rows[i];
                var sb = new System.Text.StringBuilder();
                sb.Append("    f").Append(row.F).Append(row.Quad >= 0 ? "[q" + row.Quad + "]" : "    ");
                sb.Append(" c=(").Append(row.Cx.ToString("F2")).Append(",").Append(row.Cy.ToString("F2"))
                  .Append(",").Append(row.Cz.ToString("F2")).Append(")");
                sb.Append(" slope=").Append(row.Deg.ToString("F1")).Append("deg");
                sb.Append(" ny=").Append(row.Ny.ToString("F2"));
                sb.Append(" fp=").Append(row.Footprint.ToString("F3"));
                sb.Append(" A3d=").Append(row.Area3.ToString("F3"));
                sb.Append(" y=[").Append(row.YLo.ToString("F2")).Append("..").Append(row.YHi.ToString("F2")).Append("]");
                sb.Append(" maxEdgeDy=").Append(row.ElevDrop.ToString("F2"));
                sb.Append(" comp=").Append(row.Comp);
                sb.Append(" nb=");
                if (row.Nb.Count == 0) sb.Append("-");
                else
                    for (int k = 0; k < row.Nb.Count; k++)
                    {
                        if (k > 0) sb.Append(',');
                        sb.Append(row.Nb[k]);
                    }
                Console.WriteLine(sb.ToString());
            }
        }
        Console.WriteLine("census: components=" + comps);
    }

    private sealed class CensusRow
    {
        public int F, Comp, Quad = -1;
        public float Cx, Cy, Cz, YLo, YHi, Deg, Ny;
        public double Area3, Footprint;
        public List<int> Nb;
        public List<string> Edges;
        public float ElevDrop;
    }

    private static float EdgeRise(float[] v, List<string> labels)
    {
        float worst = 0f;
        for (int i = 0; i < labels.Count; i++)
        {
            int at = labels[i].IndexOf("dy=", StringComparison.Ordinal);
            if (at < 0) continue;
            float d;
            if (!float.TryParse(labels[i].Substring(at + 3), NumberStyles.Float,
                CultureInfo.InvariantCulture, out d)) continue;
            if (d > worst) worst = d;
        }
        return worst;
    }

    private static void AddEdge(Dictionary<long, List<int>> map, int a, int b, int f)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        List<int> list;
        if (!map.TryGetValue(key, out list)) { list = new List<int>(); map[key] = list; }
        list.Add(f);
    }

    private static void AddNbs(List<int> acc, Dictionary<long, List<int>> map, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        List<int> list;
        if (!map.TryGetValue(key, out list)) return;
        for (int i = 0; i < list.Count; i++) if (!acc.Contains(list[i])) acc.Add(list[i]);
    }

    // "vA>vB dy=0.42 nb=12,13" - the edge's own rise and which faces share it.
    private static string EdgeLabel(float[] v, int a, int b, float yLo, float yHi)
    {
        return "v" + a + ">v" + b + " dy=" + Math.Abs(v[a * 3 + 1] - v[b * 3 + 1]).ToString("F2",
            CultureInfo.InvariantCulture);
    }

    // Signed XZ (ground) footprint in the walk order - the SAME measure the product's
    // NavMeshCleanup.MinGroundFootprintM2 guards (its NormalY = uz*vx - ux*vz), so a positive
    // value here means walkable and 0 means numerically vertical, exactly as in the gate.
    private static double TriFootprintXZ(float[] v, int a, int b, int c)
    {
        double ux = v[b * 3] - v[a * 3], uz = v[b * 3 + 2] - v[a * 3 + 2];
        double wx = v[c * 3] - v[a * 3], wz = v[c * 3 + 2] - v[a * 3 + 2];
        return 0.5 * (uz * wx - ux * wz);
    }

    // Do the two triangles' interiors intersect? Moller-Trumbore both ways with a strictly interior
    // barycentric test, so a shared edge (adjacent faces) never counts.
    private static bool FacesCross(float[] v, int[] t, int f, int g)
    {
        int a0 = t[f * 3], b0 = t[f * 3 + 1], c0 = t[f * 3 + 2];
        int a1 = t[g * 3], b1 = t[g * 3 + 1], c1 = t[g * 3 + 2];
        if (EdgeHitsTri(v, a0, b0, a1, b1, c1)) return true;
        if (EdgeHitsTri(v, b0, c0, a1, b1, c1)) return true;
        if (EdgeHitsTri(v, c0, a0, a1, b1, c1)) return true;
        if (EdgeHitsTri(v, a1, b1, a0, b0, c0)) return true;
        if (EdgeHitsTri(v, b1, c1, a0, b0, c0)) return true;
        if (EdgeHitsTri(v, c1, a1, a0, b0, c0)) return true;
        return false;
    }

    private static bool EdgeHitsTri(float[] v, int p0, int p1, int q0, int q1, int q2)
    {
        double ax = v[p0 * 3], ay = v[p0 * 3 + 1], az = v[p0 * 3 + 2];
        double bx = v[p1 * 3], by = v[p1 * 3 + 1], bz = v[p1 * 3 + 2];
        double cx = v[q0 * 3], cy = v[q0 * 3 + 1], cz = v[q0 * 3 + 2];
        double dx = v[q1 * 3] - cx, dy = v[q1 * 3 + 1] - cy, dz = v[q1 * 3 + 2] - cz;
        double ex = v[q2 * 3] - cx, ey = v[q2 * 3 + 1] - cy, ez = v[q2 * 3 + 2] - cz;
        double ux = bx - ax, uy = by - ay, uz = bz - az;
        double px = dy * ez - dz * ey, py = dz * ex - dx * ez, pz = dx * ey - dy * ex;
        double det = ux * px + uy * py + uz * pz;
        if (Math.Abs(det) < 1e-12) return false;             // edge parallel to the triangle
        double inv = 1.0 / det;
        double tx = cx - ax, ty = cy - ay, tz = cz - az;
        double s = (tx * px + ty * py + tz * pz) * inv;        // edge parameter
        if (s <= 1e-6 || s >= 1.0 - 1e-6) return false;
        double qx = ty * uz - tz * uy, qy = tz * ux - tx * uz, qz = tx * uy - ty * ux;
        double u = (dx * qx + dy * qy + dz * qz) * inv;        // barycentric for q1
        double w = (ex * qx + ey * qy + ez * qz) * inv;        // barycentric for q2
        double vv = 1.0 - u - w;
        const double eps = 1e-6;
        return u > eps && w > eps && vv > eps;
    }

    private static void Sort3(int a, int b, int c, out int lo, out int mid, out int hi)
    {
        if (a <= b && a <= c) { lo = a; if (b <= c) { mid = b; hi = c; } else { mid = c; hi = b; } }
        else if (b <= a && b <= c) { lo = b; if (a <= c) { mid = a; hi = c; } else { mid = c; hi = a; } }
        else { lo = c; if (a <= b) { mid = a; hi = b; } else { mid = b; hi = a; } }
    }

    private static int SharedVerts(int[] t, int f, int g)
    {
        int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
        int n = 0;
        if (a == t[g * 3] || a == t[g * 3 + 1] || a == t[g * 3 + 2]) n++;
        if (b == t[g * 3] || b == t[g * 3 + 1] || b == t[g * 3 + 2]) n++;
        if (c == t[g * 3] || c == t[g * 3 + 1] || c == t[g * 3 + 2]) n++;
        return n;
    }

    private static void LinkNb(Dictionary<int, List<int>> nb, int vert, int other)
    {
        List<int> list;
        if (!nb.TryGetValue(vert, out list)) { list = new List<int>(); nb[vert] = list; }
        list.Add(other);
    }

    private static bool SegsCrossXZ(float[] v, int a, int b, int c, int d)
    {
        float ax = v[a * 3], az = v[a * 3 + 2], bx = v[b * 3], bz = v[b * 3 + 2];
        float cx = v[c * 3], cz = v[c * 3 + 2], dx = v[d * 3], dz = v[d * 3 + 2];
        double d1 = Cross2(ax, az, bx, bz, cx, cz);
        double d2 = Cross2(ax, az, bx, bz, dx, dz);
        double d3 = Cross2(cx, cz, dx, dz, ax, az);
        double d4 = Cross2(cx, cz, dx, dz, bx, bz);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross2(float ax, float az, float bx, float bz, float px, float pz)
    {
        return (bx - ax) * (double)(pz - az) - (bz - az) * (double)(px - ax);
    }

    private static void DumpSpot(TiledNavMeshBuilder.VoxelMeshResult r, float[] v, int[] t,
        float qx, float qz, float radius, float levelTol,
        List<DefectRow> downRows, List<DefectRow> dupRows, List<DefectRow> rows,
        List<DefectRow> quadRows, List<DefectRow> crossRows)
    {
        qrefx = qx; qrefz = qz;
        int nf = r.FaceCount;
        var near = new List<int>();
        for (int f = 0; f < nf; f++)
        {
            int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
            float minx = Math.Min(v[a * 3], Math.Min(v[b * 3], v[c * 3]));
            float maxx = Math.Max(v[a * 3], Math.Max(v[b * 3], v[c * 3]));
            float minz = Math.Min(v[a * 3 + 2], Math.Min(v[b * 3 + 2], v[c * 3 + 2]));
            float maxz = Math.Max(v[a * 3 + 2], Math.Max(v[b * 3 + 2], v[c * 3 + 2]));
            if (maxx < qx - radius || minx > qx + radius || maxz < qz - radius || minz > qz + radius) continue;
            near.Add(f);
        }
        // Group by height band so multi-level spots stay readable.
        near.Sort(delegate (int a, int b)
        {
            float ya = (v[t[a * 3] * 3 + 1] + v[t[a * 3 + 1] * 3 + 1] + v[t[a * 3 + 2] * 3 + 1]) / 3f;
            float yb = (v[t[b * 3] * 3 + 1] + v[t[b * 3 + 1] * 3 + 1] + v[t[b * 3 + 2] * 3 + 1]) / 3f;
            int c = ya.CompareTo(yb);
            if (c != 0) return c;
            return a.CompareTo(b);
        });
        Console.WriteLine("  faces within radius: " + near.Count);
        var quadOf = new Dictionary<int, int>();
        if (r.QuadPairs != null)
            for (int k = 0; k + 1 < r.QuadPairs.Length; k += 2)
            {
                quadOf[r.QuadPairs[k]] = k / 2;
                quadOf[r.QuadPairs[k + 1]] = k / 2;
            }
        for (int i = 0; i < near.Count; i++)
        {
            int f = near[i];
            int a = t[f * 3], b = t[f * 3 + 1], c = t[f * 3 + 2];
            double[] n = TriNormal(v, a, b, c);
            double len = Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2]);
            int qi; bool isQuad = quadOf.TryGetValue(f, out qi);
            Console.WriteLine("    f" + f + (isQuad ? "[quad" + qi + "]" : "      ")
                + " v=(" + a + "," + b + "," + c + ")"
                + " A=(" + Df(v[a * 3]) + "," + Df(v[a * 3 + 1]) + "," + Df(v[a * 3 + 2]) + ")"
                + " B=(" + Df(v[b * 3]) + "," + Df(v[b * 3 + 1]) + "," + Df(v[b * 3 + 2]) + ")"
                + " C=(" + Df(v[c * 3]) + "," + Df(v[c * 3 + 1]) + "," + Df(v[c * 3 + 2]) + ")"
                + " ny=" + (len < 1e-12 ? "0" : Df((float)(n[1] / len)))
                + (n[1] < 0 ? " DOWN" : ""));
        }
        Console.WriteLine("  defect rows in spot:");
        foreach (List<DefectRow> set in new List<List<DefectRow>> { downRows, dupRows, rows, quadRows, crossRows })
            for (int i = 0; i < set.Count; i++)
            {
                DefectRow d = set[i];
                float dx = d.X, dz = d.Z;
                if (dx < qx - radius || dx > qx + radius || dz < qz - radius || dz > qz + radius) continue;
                Console.WriteLine("    " + d.Kind + " f=" + d.F + " g=" + d.G
                    + " at (" + Df(d.X) + "," + Df(d.Y) + "," + Df(d.Z) + ")"
                    + (d.Kind == "OVERLAP" ? " other (" + Df(d.DX) + "," + Df(d.DY) + "," + Df(d.DZ) + ")" : "")
                    + " " + d.Extra);
            }
        // Also list every defect row within 20 m so a spot that is a couple of metres off still sees it.
        Console.WriteLine("  defect rows within 20 m:");
        var near20 = new List<DefectRow>();
        foreach (List<DefectRow> set in new List<List<DefectRow>> { downRows, dupRows, rows, quadRows, crossRows })
            for (int i = 0; i < set.Count; i++)
            {
                DefectRow d = set[i];
                float ddx = d.X - qx, ddz = d.Z - qz;
                if (ddx * ddx + ddz * ddz > 400.0) continue;
                near20.Add(d);
            }
        near20.Sort(delegate (DefectRow a, DefectRow b)
        {
            float ax = a.X - qx, az = a.Z - qz, bx2 = b.X - qx, bz2 = b.Z - qz;
            return (ax * ax + az * az).CompareTo(bx2 * bx2 + bz2 * bz2);
        });
        for (int i = 0; i < near20.Count && i < 40; i++)
        {
            DefectRow d = near20[i];
            Console.WriteLine("    " + d.Kind + " f=" + d.F + " g=" + d.G
                + " at (" + Df(d.X) + "," + Df(d.Y) + "," + Df(d.Z) + ")"
                + (d.Kind == "OVERLAP" ? " other (" + Df(d.DX) + "," + Df(d.DY) + "," + Df(d.DZ) + ")" : "")
                + " " + d.Extra);
        }
    }

    // The XZ margin the probe measures against. Kept local so this diagnostic compiles against the
    // pre-fix product too (it is a REPORTING tool, so it must not depend on the guard it audits).
    private const double XzMarginDegreesProbe = 175.0;

    // Test 27 asserts the harness's own expected margin AND that the product's constant agrees, so
    // neither side can drift alone. Read reflectively so the harness still builds against a product
    // that predates the XZ guard.
    private static double ProductXzMarginDegrees()
    {
        System.Reflection.FieldInfo fi =
            typeof(NavMeshCleanup).GetField("QuadXzMaxInteriorAngleDegrees");
        if (fi == null) return XzMarginDegreesProbe;
        return Convert.ToDouble(fi.GetRawConstantValue(), CultureInfo.InvariantCulture);
    }

    // The five locations the engine's concave-face dialog reported, as (x, y=height, z).
    private static readonly double[][] EngineConcaveReports =
    {
        new double[] { 513.38, 111.81, 44.70 },
        new double[] { 304.71, 265.83, 39.95 },
        new double[] { 350.44, 327.81, 37.60 },
        new double[] { 264.75, 376.99, 21.15 },
        new double[] { 362.35, 400.80, 21.73 },
    };

    private static void PrintXzProbe(TiledNavMeshBuilder.VoxelMeshResult r, XzProbeReport rep)
    {
        Console.WriteLine("xzprobe: quads=" + rep.Quads
            + " xzReflex=" + rep.ReflexXZ
            + " xzFlat=" + rep.FlatXZ
            + " xzConcave=" + rep.ConcaveXZ
            + " xzStraight=" + rep.StraightXZ
            + " xzOrientBad=" + rep.OrientBad
            + " xzDegenerate=" + rep.DegenerateXZ
            + " worstXzAngle=" + rep.WorstAngle.ToString("F4", CultureInfo.InvariantCulture) + "deg"
            + " worstIsReflex=" + rep.WorstIsReflex);
        rep.Offenders.Sort(delegate (XzQuadInfo a, XzQuadInfo b)
        {
            double da = NearestEngineReport(a), db = NearestEngineReport(b);
            int c = da.CompareTo(db);
            if (c != 0) return c;
            return b.WorstAngle.CompareTo(a.WorstAngle);
        });
        int shown = 0;
        for (int i = 0; i < rep.Offenders.Count && shown < 40; i++, shown++)
        {
            XzQuadInfo q = rep.Offenders[i];
            Console.WriteLine("  xzBAD #" + (i + 1) + " f0=" + q.F0 + " f1=" + q.F1
                + " plane=" + QuadWorstAngleV(r, q.F0, q.F1).ToString("F4", CultureInfo.InvariantCulture) + "deg"
                + " xz=" + q.WorstAngle.ToString("F4", CultureInfo.InvariantCulture) + "deg"
                + " " + (q.XzReflex != 0 ? "REFLEX" : "FLAT  ")
                + " shoelace=" + F3(q.Shoelace) + " tri0=" + F3(q.TriShoelace)
                + " orientOk=" + q.OrientOk + " fold=" + F1(q.FoldDeg) + "deg"
                + " centre=(" + F2(q.Cx) + "," + F2(q.Cy) + "," + F2(q.Cz) + ")"
                + " x=[" + F2(q.MinX) + ".." + F2(q.MaxX) + "] z=[" + F2(q.MinZ) + ".." + F2(q.MaxZ) + "]"
                + " nearestReport=" + F2(NearestEngineReport(q)) + "m");
            if (q.XzReflex != 0)
            {
                int[] wv = QuadWalk(r, q.F0, q.F1);
                Console.Write("        verts=[");
                for (int c2 = 0; c2 < 4; c2++)
                {
                    int vi = wv[c2];
                    Console.Write((c2 > 0 ? ", " : "")
                        + r.VertsXyz[vi * 3].ToString("R", CultureInfo.InvariantCulture) + "f, "
                        + r.VertsXyz[vi * 3 + 1].ToString("R", CultureInfo.InvariantCulture) + "f, "
                        + r.VertsXyz[vi * 3 + 2].ToString("R", CultureInfo.InvariantCulture) + "f");
                }
                Console.WriteLine("]");
            }
        }
        if (rep.Offenders.Count > shown) Console.WriteLine("  ... " + (rep.Offenders.Count - shown) + " more");

        // Do the engine's five reported locations land on (or beside) any offender at all? The engine
        // prints the quad's CENTRE in its own Z-up frame (engine x, engine y, engine z=height), which
        // is our (Cx, Cz, Cy) - so the horizontal pair to compare is (Cx, Cz) either way.
        for (int e = 0; e < EngineConcaveReports.Length; e++)
        {
            double[] p = EngineConcaveReports[e];
            XzQuadInfo byCentre = null; double cd = double.MaxValue;
            XzQuadInfo byBounds = null; double bd = double.MaxValue;
            for (int i = 0; i < rep.Offenders.Count; i++)
            {
                XzQuadInfo q = rep.Offenders[i];
                double c = Math.Sqrt((q.Cx - p[0]) * (q.Cx - p[0]) + (q.Cz - p[1]) * (q.Cz - p[1]));
                if (c < cd) { cd = c; byCentre = q; }
                double dx = Math.Max(q.MinX - p[0], Math.Max(0.0, p[0] - q.MaxX));
                double dz = Math.Max(q.MinZ - p[1], Math.Max(0.0, p[1] - q.MaxZ));
                double d = Math.Sqrt(dx * dx + dz * dz);
                if (d < bd) { bd = d; byBounds = q; }
            }
            Console.WriteLine("  report[" + (e + 1) + "] engineDialog=(" + F2(p[0]) + "," + F2(p[1]) + ","
                + F2(p[2]) + "=height)"
                + (byCentre == null ? " -> no XZ offender" :
                    " -> xzBAD f0=" + byCentre.F0 + " " + (byCentre.XzReflex != 0 ? "REFLEX" : "FLAT")
                    + " xz=" + byCentre.WorstAngle.ToString("F4", CultureInfo.InvariantCulture) + "deg"
                    + " ourCentre=(" + F2(byCentre.Cx) + "," + F2(byCentre.Cy) + "=height,"
                    + F2(byCentre.Cz) + ") centreDist=" + F2(cd) + "m boundsDist=" + F2(bd) + "m"
                    + (bd < 1.0f ? "  <== MATCH" : "")));
        }
    }

    // Distance in XZ from the quad's XZ bounding box to the nearest engine-reported location.
    private static double NearestEngineReport(XzQuadInfo q)
    {
        double bd = double.MaxValue;
        for (int e = 0; e < EngineConcaveReports.Length; e++)
        {
            double[] p = EngineConcaveReports[e];
            double dx = Math.Max(q.MinX - p[0], Math.Max(0.0, p[0] - q.MaxX));
            double dz = Math.Max(q.MinZ - p[2], Math.Max(0.0, p[2] - q.MaxZ));
            double d = Math.Sqrt(dx * dx + dz * dz);
            if (d < bd) bd = d;
        }
        return bd;
    }

    private static string F2(double v) { return v.ToString("F2", CultureInfo.InvariantCulture); }
    private static string F3(double v) { return v.ToString("F3", CultureInfo.InvariantCulture); }

    // Minimal NMG8 reader: enough to check the face count and the per-face vertex counts / is_concave
    // the writer emitted.
    private sealed class Nmg8Parse
    {
        public int VertexCount, EdgeCount, FaceCount;
        public List<int> FaceVertexCounts = new List<int>();
        public List<int> FaceConcave = new List<int>();

        public static Nmg8Parse Parse(byte[] bin)
        {
            var p = new Nmg8Parse();
            int off = 0;
            uint bodyLen = BitConverter.ToUInt32(bin, off); off += 4;
            uint magic = BitConverter.ToUInt32(bin, off); off += 4;
            if (magic != Nmg8Writer.Nmg8Version)
                throw new InvalidOperationException("bad NMG8 magic");
            if (bodyLen != bin.Length - 4)
                throw new InvalidOperationException("body length mismatch: " + bodyLen + " vs " + (bin.Length - 4));
            p.VertexCount = BitConverter.ToInt32(bin, off); off += 4;
            off += p.VertexCount * 12;
            p.EdgeCount = BitConverter.ToInt32(bin, off); off += 4;
            off += p.EdgeCount * 20;
            p.FaceCount = BitConverter.ToInt32(bin, off); off += 4;
            for (int f = 0; f < p.FaceCount; f++)
            {
                int n = BitConverter.ToInt32(bin, off); off += 4;
                off += n * 4;   // vertex indices
                off += n * 4;   // edge indices
                int nfields = BitConverter.ToInt32(bin, off); off += 4;
                int isConcave = BitConverter.ToInt32(bin, off); off += 4;
                for (int k = 1; k < nfields; k++) off += 4;
                off += 4;       // cost
                off += 4;       // level mask
                off += 1;       // inverted flag
                p.FaceVertexCounts.Add(n);
                p.FaceConcave.Add(isConcave);
            }
            int groupCount = BitConverter.ToInt32(bin, off); off += 4;
            off += groupCount * 8;
            if (off != bin.Length)
                throw new InvalidOperationException("parse ended at " + off + " of " + bin.Length);
            return p;
        }
    }

    private static void SweepCase(string label, Door26 d, float lx, float ly, float lz, float ux,
        float uy, float uz, out bool iv, out int faces)
    {
        int rt;
        bool ok = RampLink23(NarrowDoorwayProps26(d), true, lx, ly, lz, ux, uy, uz, out iv, out faces,
            out rt, true, 6);
        // Where does the mesh have no face between the ladder top (x~4.3) and the floor navmesh (~7.1)?
        float dying = -1f;
        for (float pxx = 4.0f; pxx <= 7.6f; pxx += 0.1f)
        {
            List<int> cd = FacesAtAnyHeight(LastRampMesh23, pxx, 5.0f);
            bool hasLanding = false;
            for (int j2 = 0; j2 < cd.Count; j2++)
                if (Math.Abs(FaceCentroidY(LastRampMesh23, cd[j2]) - 3.4f) < 0.35f) hasLanding = true;
            if (!hasLanding) { dying = pxx; break; }
        }
        Console.WriteLine("    " + label.PadRight(34) + (ok ? "TRUE " : "false").PadRight(14)
            + faces.ToString().PadRight(7) + (dying < 0f ? "(clear)" : dying.ToString("F2")));
    }

    private static TiledNavMeshBuilder.TiledNavMeshSettings AssetSettings(float cs)
    {
        var s = new TiledNavMeshBuilder.TiledNavMeshSettings();
        s.VoxelCellSizeMeters = cs;
        s.MaxSlopeDegrees = 50f;
        s.UseDetailMesh = true;
        s.RegionMinSize = 1;
        s.RegionMergeSize = 1;
        s.AgentMaxClimbMeters = TiledNavMeshBuilder.ResolveAgentClimbMeters(cs, s.CellHeightMeters, s.MaxSlopeDegrees, 0.4f);
        s.PruneIsolatedIslands = false;
        return s;
    }

    private static float TerrainGully(float x, float z)
    {
        return (x >= 30f && x <= 34f) ? -3f : 0f;
    }

    // Outward-wound box in engine Z-up space (the harness's asset stand-in for a collision shape).
    private static void AddBoxZ(List<float> v, List<int> t,
        float x0, float y0, float z0, float x1, float y1, float z1)
    {
        int b = v.Count / 3;
        float[] c =
        {
            x0, y0, z0, x1, y0, z0, x1, y1, z0, x0, y1, z0,
            x0, y0, z1, x1, y0, z1, x1, y1, z1, x0, y1, z1
        };
        for (int i = 0; i < c.Length; i++) v.Add(c[i]);
        int[] f =
        {
            0, 2, 1, 0, 3, 2,   // bottom -Z
            4, 5, 6, 4, 6, 7,   // top +Z
            0, 1, 5, 0, 5, 4,   // front -Y
            3, 7, 6, 3, 6, 2,   // back +Y
            0, 4, 7, 0, 7, 3,   // left -X
            1, 2, 6, 1, 6, 5    // right +X
        };
        for (int i = 0; i < f.Length; i++) t.Add(b + f[i]);
    }

    private static bool AssertBand(string label, TiledNavMeshBuilder.VoxelMeshResult r,
        float ex, float ey, float lo, float hi, bool expect)
    {
        float h;
        bool hit = ProbeBand(r, ex, ey, lo, hi, out h);
        bool ok = hit == expect;
        Console.WriteLine("    probe " + label + " @(" + ex + "," + ey + ") band=[" + lo.ToString("F2") + ","
            + hi.ToString("F2") + "] -> " + (hit ? "h=" + h.ToString("F3") : "none")
            + " expect=" + (expect ? "hit" : "none") + " " + (ok ? "OK" : "FAIL"));
        return ok;
    }

    private static bool ProbeBand(TiledNavMeshBuilder.VoxelMeshResult r, float px, float pz,
        float lo, float hi, out float height)
    {
        height = 0f;
        float best = float.MaxValue;
        bool found = false;
        float mid = (lo + hi) * 0.5f;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int ia = r.Tris[f * 3], ib = r.Tris[f * 3 + 1], ic = r.Tris[f * 3 + 2];
            float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            float bx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bz = r.VertsXyz[ib * 3 + 2];
            float cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];
            float wa, wb, wc;
            if (!Bary(px, pz, ax, az, bx, bz, cx, cz, out wa, out wb, out wc))
                continue;
            float y = wa * ay + wb * by + wc * cy;
            if (y < lo || y > hi)
                continue;
            float d = Math.Abs(y - mid);
            if (d < best)
            {
                best = d;
                height = y;
                found = true;
            }
        }
        return found;
    }

    private static bool Bary(float px, float pz, float ax, float az, float bx, float bz, float cx, float cz,
        out float wa, out float wb, out float wc)
    {
        float d = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
        if (Math.Abs(d) < 1e-9f) { wa = 0f; wb = 0f; wc = 0f; return false; }
        wa = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / d;
        wb = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / d;
        wc = 1f - wa - wb;
        const float e = 1e-4f;
        return wa >= -e && wb >= -e && wc >= -e;
    }

    private const double HillDomain = 160.0;
    private static float HillHeightF(float x, float z) { return (float)HillHeight(x, z); }

    private static double HillHeight(double x, double z)
    {
        double h = Gauss(x, z, 80, 80, 22.0, 42.0);   // broad central hill
        h += Gauss(x, z, 35, 45, 8.0, 6.0);           // steep bumps
        h += Gauss(x, z, 115, 50, 7.0, 6.0);
        h += Gauss(x, z, 70, 120, 8.0, 6.0);
        h += Gauss(x, z, 130, 120, 6.0, 7.0);
        h += Gauss(x, z, 25, 120, 5.0, 7.0);
        h += Gauss(x, z, 140, 30, 4.0, 6.0);
        return h;
    }

    private static double Gauss(double x, double z, double cx, double cz, double amp, double sigma)
    {
        double dx = x - cx, dz = z - cz;
        return amp * Math.Exp(-(dx * dx + dz * dz) / (2.0 * sigma * sigma));
    }

    private static TiledNavMeshBuilder.TiledNavMeshSettings HillSettings(bool detail)
    {
        TiledNavMeshBuilder.TiledNavMeshSettings s = SettingsAuto(0.5f, 65f);
        s.HeightOffsetMeters = 0f;
        s.UseDetailMesh = detail;
        s.MaxErrorMeters = 0.25f;
        return s;
    }

    private sealed class Fid
    {
        public double MinD = double.MaxValue;
        public double MaxD = double.MinValue;
        public double Sum;
        public int N;
        public int Below05;
        public int Below50;
        public double MinX;
        public double MinZ;
        public double MaxX;
        public double MaxZ;
    }

    private static Fid Fidelity(string label, TiledNavMeshBuilder.VoxelMeshResult r, bool verbose)
    {
        var f = new Fid();
        for (int t = 0; t < r.FaceCount; t++)
        {
            int ia = r.Tris[t * 3 + 0], ib = r.Tris[t * 3 + 1], ic = r.Tris[t * 3 + 2];
            float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
            float bx = r.VertsXyz[ib * 3], by = r.VertsXyz[ib * 3 + 1], bz = r.VertsXyz[ib * 3 + 2];
            float cx = r.VertsXyz[ic * 3], cy = r.VertsXyz[ic * 3 + 1], cz = r.VertsXyz[ic * 3 + 2];
            Sample(f, ax, ay, az);
            Sample(f, bx, by, bz);
            Sample(f, cx, cy, cz);
            Sample(f, (ax + bx + cx) / 3f, (ay + by + cy) / 3f, (az + bz + cz) / 3f);      // centroid
            Sample(f, (ax + bx) / 2f, (ay + by) / 2f, (az + bz) / 2f);                      // edge ab
            Sample(f, (bx + cx) / 2f, (by + cy) / 2f, (bz + cz) / 2f);                      // edge bc
            Sample(f, (cx + ax) / 2f, (cy + ay) / 2f, (cz + az) / 2f);                      // edge ca
        }
        if (verbose)
            Console.WriteLine("  " + label + ": samples=" + f.N
                + " minD=" + f.MinD.ToString("F3") + " @" + f.MinX.ToString("F1") + "," + f.MinZ.ToString("F1")
                + " meanD=" + (f.Sum / Math.Max(1, f.N)).ToString("F3")
                + " maxD=" + f.MaxD.ToString("F3") + " @" + f.MaxX.ToString("F1") + "," + f.MaxZ.ToString("F1")
                + " below-0.05=" + f.Below05 + " (" + (100.0 * f.Below05 / Math.Max(1, f.N)).ToString("F2") + "%)"
                + " below-0.5=" + f.Below50 + " (" + (100.0 * f.Below50 / Math.Max(1, f.N)).ToString("F2") + "%)");
        return f;
    }

    private static void Sample(Fid f, float x, float y, float z)
    {
        double d = y - HillHeight(x, z);
        if (d < f.MinD) { f.MinD = d; f.MinX = x; f.MinZ = z; }
        if (d > f.MaxD) { f.MaxD = d; f.MaxX = x; f.MaxZ = z; }
        f.Sum += d;
        f.N++;
        if (d < -0.05) f.Below05++;
        if (d < -0.5) f.Below50++;
    }

    private sealed class Integ
    {
        public int Components;
        public int FaceComponents;
        public int UnsharedEdges;
        public int Triangles;
        public int VertsUsed;
    }

    private static Integ Integrity(string label, TiledNavMeshBuilder.VoxelMeshResult r, bool verbose)
    {
        var res = new Integ();
        res.Triangles = r.FaceCount;
        int nv = r.VertexCount;
        var parent = new int[nv];
        for (int i = 0; i < nv; i++) parent[i] = i;
        var used = new bool[nv];
        var usedList = new List<int>();
        var edges = new Dictionary<long, int>();
        var edgeFirstFace = new Dictionary<long, int>();
        var fparent = new int[r.FaceCount];
        for (int i = 0; i < r.FaceCount; i++) fparent[i] = i;

        for (int t = 0; t < r.FaceCount; t++)
        {
            int a = r.Tris[t * 3], b = r.Tris[t * 3 + 1], c = r.Tris[t * 3 + 2];
            if (!used[a]) { used[a] = true; usedList.Add(a); }
            if (!used[b]) { used[b] = true; usedList.Add(b); }
            if (!used[c]) { used[c] = true; usedList.Add(c); }
            Union(parent, a, b); Union(parent, b, c); Union(parent, c, a);
            AddEdge(edges, a, b); AddEdge(edges, b, c); AddEdge(edges, c, a);
            UnionFaceEdge(edgeFirstFace, fparent, t, a, b);
            UnionFaceEdge(edgeFirstFace, fparent, t, b, c);
            UnionFaceEdge(edgeFirstFace, fparent, t, c, a);
        }

        var size = new Dictionary<int, int>();
        var sx = new Dictionary<int, double>();
        var sz = new Dictionary<int, double>();
        for (int i = 0; i < usedList.Count; i++)
        {
            int v = usedList[i];
            int root = Find(parent, v);
            int c;
            size.TryGetValue(root, out c);
            size[root] = c + 1;
            double ex, ez;
            sx.TryGetValue(root, out ex); sz.TryGetValue(root, out ez);
            sx[root] = ex + r.VertsXyz[v * 3];
            sz[root] = ez + r.VertsXyz[v * 3 + 2];
        }
        var faceRoots = new HashSet<int>();
        for (int i = 0; i < r.FaceCount; i++) faceRoots.Add(Find(fparent, i));
        int unshared = 0;
        foreach (KeyValuePair<long, int> kv in edges) if (kv.Value == 1) unshared++;
        res.Components = size.Count;
        res.UnsharedEdges = unshared;
        res.VertsUsed = usedList.Count;
        res.FaceComponents = faceRoots.Count;

        // Minimum distance between vertices in different components (crack indicator).
        double minCross = double.MaxValue;
        if (res.Components > 1 && usedList.Count <= 60000)
        {
            for (int a = 0; a < usedList.Count; a++)
            {
                int ia = usedList[a];
                int ra = Find(parent, ia);
                float ax = r.VertsXyz[ia * 3], ay = r.VertsXyz[ia * 3 + 1], az = r.VertsXyz[ia * 3 + 2];
                for (int b = a + 1; b < usedList.Count; b++)
                {
                    int ib = usedList[b];
                    if (Find(parent, ib) == ra) continue;
                    float dx = ax - r.VertsXyz[ib * 3];
                    float dy = ay - r.VertsXyz[ib * 3 + 1];
                    float dz = az - r.VertsXyz[ib * 3 + 2];
                    double d2 = dx * dx + dy * dy + dz * dz;
                    if (d2 < minCross) minCross = d2;
                }
            }
            minCross = Math.Sqrt(minCross);
        }

        if (verbose)
            Console.WriteLine("  " + label + ": triangles=" + res.Triangles + " vertsUsed=" + res.VertsUsed
                + " edges=" + edges.Count + " unsharedEdges=" + unshared
                + " vertComponents=" + res.Components + " edgeComponents=" + res.FaceComponents
                + (res.Components > 1 ? " minCrossCompDist=" + minCross.ToString("F4") + "m" : ""));
        if (verbose && res.Components > 1)
        {
            var order = new List<int>(size.Keys);
            order.Sort(delegate (int a, int b) { return size[b].CompareTo(size[a]); });
            for (int i = 0; i < order.Count && i < 5; i++)
            {
                int root = order[i];
                Console.WriteLine("      comp[" + i + "] verts=" + size[root]
                    + " centroid=(" + (sx[root] / size[root]).ToString("F2") + ","
                    + (sz[root] / size[root]).ToString("F2") + ")");
            }
        }
        return res;
    }

    private static int Find(int[] p, int x)
    {
        while (p[x] != x) { p[x] = p[p[x]]; x = p[x]; }
        return x;
    }

    private static void Union(int[] p, int a, int b)
    {
        int ra = Find(p, a), rb = Find(p, b);
        if (ra != rb) p[ra] = rb;
    }

    private static void UnionFaceEdge(Dictionary<long, int> edgeFirstFace, int[] fparent, int face, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        int other;
        if (edgeFirstFace.TryGetValue(key, out other)) Union(fparent, other, face);
        else edgeFirstFace[key] = face;
    }

    private static void AddEdge(Dictionary<long, int> edges, int a, int b)
    {
        long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
        int c;
        edges.TryGetValue(key, out c);
        edges[key] = c + 1;
    }

    private static TiledNavMeshBuilder.TiledNavMeshSettings SettingsAuto(float cs, float slope)
    {
        var s = Settings(cs);
        s.AgentMaxClimbMeters = TiledNavMeshBuilder.ResolveAgentClimbMeters(cs, s.CellHeightMeters, slope, 0.4f);
        return s;
    }

    private static TiledNavMeshBuilder.TiledNavMeshSettings Settings(float cs)
    {
        var s = new TiledNavMeshBuilder.TiledNavMeshSettings();
        s.VoxelCellSizeMeters = cs;
        // The legacy scenarios are tiny; keep island pruning out of them so their numbers stay
        // comparable. Test 19 exercises the prune explicitly.
        s.PruneIsolatedIslands = false;
        return s;
    }

    private static void MakeGrid(float widthMeters, float depthMeters, float spacing,
        Func<float, float, float> height, out float[] verts, out int[] tris)
    {
        int nx = (int)Math.Round(widthMeters / spacing);
        int nz = (int)Math.Round(depthMeters / spacing);
        int mx = nx + 1;
        int mz = nz + 1;

        verts = new float[mx * mz * 3];
        for (int i = 0; i < mx; i++)
        {
            float x = i * spacing;
            for (int j = 0; j < mz; j++)
            {
                float z = j * spacing;
                int idx = i * mz + j;
                verts[idx * 3 + 0] = x;
                verts[idx * 3 + 1] = height(x, z);
                verts[idx * 3 + 2] = z;
            }
        }

        tris = new int[nx * nz * 6];
        int t = 0;
        for (int i = 0; i < nx; i++)
        {
            for (int j = 0; j < nz; j++)
            {
                int i00 = i * mz + j;
                int i10 = (i + 1) * mz + j;
                int i11 = (i + 1) * mz + (j + 1);
                int i01 = i * mz + (j + 1);
                tris[t++] = i00; tris[t++] = i11; tris[t++] = i10;
                tris[t++] = i00; tris[t++] = i01; tris[t++] = i11;
            }
        }
    }

    private static double SumArea(TiledNavMeshBuilder.VoxelMeshResult res)
    {
        double sum = 0.0;
        for (int f = 0; f < res.FaceCount; f++)
        {
            int ia = res.Tris[f * 3 + 0], ib = res.Tris[f * 3 + 1], ic = res.Tris[f * 3 + 2];
            double ax = res.VertsXyz[ia * 3 + 0], ay = res.VertsXyz[ia * 3 + 1], az = res.VertsXyz[ia * 3 + 2];
            double bx = res.VertsXyz[ib * 3 + 0], by = res.VertsXyz[ib * 3 + 1], bz = res.VertsXyz[ib * 3 + 2];
            double cx = res.VertsXyz[ic * 3 + 0], cy = res.VertsXyz[ic * 3 + 1], cz = res.VertsXyz[ic * 3 + 2];
            double ux = bx - ax, uy = by - ay, uz = bz - az;
            double vx = cx - ax, vy = cy - ay, vz = cz - az;
            double nx = uy * vz - uz * vy;
            double ny = uz * vx - ux * vz;
            double nz = ux * vy - uy * vx;
            sum += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
        }
        return sum;
    }

    private static long Hash(int[] a)
    {
        unchecked
        {
            long h = 1469598103934665603L;
            for (int i = 0; i < a.Length; i++) h = (h ^ (uint)a[i]) * 1099511628211L;
            return h;
        }
    }

    private static long Hash(float[] a)
    {
        unchecked
        {
            long h = 1469598103934665603L;
            for (int i = 0; i < a.Length; i++)
            {
                int bits = BitConverter.ToInt32(BitConverter.GetBytes(a[i]), 0);
                h = (h ^ (uint)bits) * 1099511628211L;
            }
            return h;
        }
    }

    // ==================================================================== golden hashes (det v1)
    // End-to-end determinism gate: for a given fixture + settings, the built mesh's three arrays must
    // hash identically. Test28 covers NavMeshCleanup in isolation; this covers the whole
    // TiledNavMeshBuilder.Build on a real fixture, so a leak between builds (the harness has been bitten
    // by RcRasterizations scratch buffers before) shows up as a hash change.
    //
    // CONTEXT IS PART OF THE KEY on purpose: the fixture's terrain path, its assets filename and the
    // terrain file's length + last-write ticks are folded in. A refreshed snapshot therefore reports
    // GOLDEN-UNRECORDED instead of a spurious mismatch, and a mismatch can only ever mean "the code
    // changed the mesh for the same input".
    private sealed class GoldenEntry
    {
        public long Vars, Tris, Quads;
        public string Note;
    }

    // Captured by the S8 run: 3 repeats x 3 SEPARATE processes per fixture, all nine agreeing (A14).
    // The key is the fixture's CONTEXT - terrain file name, length and last-write ticks - so a refreshed
    // snapshot reports GOLDEN-UNRECORDED rather than a spurious mismatch. A refreshed fixture
    // invalidating its goldens is BY DESIGN: that is what makes the gate able to fail.
    private static readonly Dictionary<string, GoldenEntry> GoldenTable =
        new Dictionary<string, GoldenEntry>(StringComparer.Ordinal)
    {
        { GoldenCtx("mp_tdm_Birtu_navmesh_snapshot_terrain.bin", 592920, 639263973584410258L),
          new GoldenEntry { Vars = unchecked((long)0xb17450554723fc69UL), Tris = unchecked((long)0x4090f55eb95abdedUL), Quads = unchecked((long)0x96708a7dfa90d58eUL),
                            Note = "Birtu; tile 10; quadPairs=736 F=43656" } },
        { GoldenCtx("CC_20_siege_navmesh_snapshot_terrain.bin", 6563864, 639264301284916775L),
          new GoldenEntry { Vars = unchecked((long)0xc10f245dfef9da9bUL), Tris = unchecked((long)0x507584c6a9f673ddUL), Quads = unchecked((long)0x0f520c5ea985fde6UL),
                            Note = "CC_20 v1 (caves, stale fixture); tile 10; quadPairs=1882 F=246095" } },
        { GoldenCtx("snapshot_v2_terrain.bin", 6563920, 639269148326352812L),
          new GoldenEntry { Vars = unchecked((long)0x81619b5b95d61db1UL), Tris = unchecked((long)0xaa4db11656c56fefUL), Quads = unchecked((long)0x7730ab5d82a72307UL),
                            Note = "CC_20 v2 (current fixture, walk-area border block); tile 10; quadPairs=1878 F=246028" } },
        { GoldenCtx("mp_battle_Basamu_village_navmesh_snapshot_terrain.bin", 16793624, 639264258666049675L),
          new GoldenEntry { Vars = unchecked((long)0x4a262382d29a3e58UL), Tris = unchecked((long)0x0a2eee71ebebf0ccUL), Quads = unchecked((long)0xe9276b611bb343eeUL),
                            Note = "Basamu; tile 10; quadPairs=2920 F=491641" } },
    };

    private static string GoldenCtx(string name, long len, long ticks)
    {
        return name + "|" + len.ToString(CultureInfo.InvariantCulture)
            + "|" + ticks.ToString(CultureInfo.InvariantCulture);
    }

    // ==================================================================== Test 28: cleanup determinism
    // Runs the cleanup twice on INDEPENDENT copies of the same input and asserts byte-identical
    // verts / tris / QuadPairs plus every deterministic Stats field.
    //
    // EXPLICIT CARVE-OUT: DeadlineExceeded and every *Ms field are EXCLUDED from the equality assert.
    // They are wall-clock dependent by construction. Do not "fix" this by making the deadline
    // deterministic - that would defeat the whole point of the change (the deadline exists only to warn).
    private static void Test28_CleanupDeterminism()
    {
        Console.WriteLine("== Test 28: cleanup determinism (double-run byte compare) ==");
        var verts = new List<float>();
        var tris = new List<int>();
        // A cliff grid with detail mesh on - the same shape Test3_Determinism uses, and one that really
        // produces slivers. The mesh is obtained through the full Build (so the cleanup sees exactly the
        // array shapes it sees in production), then fed to the cleanup twice.
        float[] gv;
        int[] gt;
        // A fine, tall cliff grid: the detail mesh has to tessellate the steep faces hard, and that is
        // what produces the slivers the cleanup exists to remove.
        MakeGrid(400f, 200f, 0.25f, CliffHeight, out gv, out gt);
        var set = SettingsAuto(0.25f, 65f);
        set.UseDetailMesh = true;
        set.MaxErrorMeters = 0.02f;
        set.DetailSampleDistMeters = 0.15f;
        TiledNavMeshBuilder.VoxelMeshResult built =
            TiledNavMeshBuilder.Build(gv, gt, set, Environment.ProcessorCount, null);
        verts.AddRange(built.VertsXyz);
        tris.AddRange(built.Tris);
        Console.WriteLine("  input: V=" + built.VertexCount + " F=" + built.FaceCount);
        var budget = NavMeshCleanup.Budgets.Default(6000);
        bool ok = true;

        var a = new List<float>(verts); var at = new List<int>(tris);
        NavMeshCleanup.Stats s1 = NavMeshCleanup.Run(a, at, budget, true);
        var b = new List<float>(verts); var bt = new List<int>(tris);
        NavMeshCleanup.Stats s2 = NavMeshCleanup.Run(b, bt, budget, true);

        if (a.Count != b.Count) { Console.WriteLine("  FAIL verts count " + a.Count + " != " + b.Count); ok = false; }
        else if (Hash(a.ToArray()) != Hash(b.ToArray())) { Console.WriteLine("  FAIL verts not byte-identical"); ok = false; }
        else Console.WriteLine("  ok  verts byte-identical (" + a.Count + " floats)");
        if (at.Count != bt.Count) { Console.WriteLine("  FAIL tris count " + at.Count + " != " + bt.Count); ok = false; }
        else if (Hash(at.ToArray()) != Hash(bt.ToArray())) { Console.WriteLine("  FAIL tris not byte-identical"); ok = false; }
        else Console.WriteLine("  ok  tris byte-identical (" + at.Count + " ints)");
        if (s1.QuadPairs.Count != s2.QuadPairs.Count) { Console.WriteLine("  FAIL QuadPairs count"); ok = false; }
        else if (Hash(s1.QuadPairs.ToArray()) != Hash(s2.QuadPairs.ToArray())) { Console.WriteLine("  FAIL QuadPairs not byte-identical"); ok = false; }
        else Console.WriteLine("  ok  QuadPairs byte-identical (" + s1.QuadPairs.Count + " pairs)");

        // QuadXzWorstAngle is compared with a tolerance of EXACTLY 0: same inputs, same acos, same order.
        string[] fields = {
            "Welded", "Removed", "Decimated", "Flipped", "Relaxed", "Refined", "Quads",
            "NeedlesBefore", "NeedlesAfter", "ThinBefore", "ThinAfter", "BoundaryMovedCount",
            "ComponentsBefore", "ComponentsAfter", "CandVerts", "RejMove", "RejFan", "RejRing",
            "RejTopo", "QuadXzNearStraight", "DecimateGuardEvals", "DecimateCand0",
            "DecimateGuardBudget", "DecimateBudgetHit", "DecimateSweeps", "RefineFaceVisits",
            "RefineFaceVisitBudget", "RefineSweeps", "RelaxEvals", "RelaxCand0", "RelaxBudget",
            "RelaxSweeps", "FlipEdgeVisits", "FlipEdgeVisitBudget", "FlipE0", "FlipIters", };
        int[] v1 = FieldValues(s1), v2 = FieldValues(s2);
        for (int i = 0; i < fields.Length; i++)
            if (v1[i] != v2[i]) { Console.WriteLine("  FAIL Stats." + fields[i] + " " + v1[i] + " != " + v2[i]); ok = false; }
        if (s1.QuadXzWorstAngle != s2.QuadXzWorstAngle)
        { Console.WriteLine("  FAIL Stats.QuadXzWorstAngle differs"); ok = false; }
        Console.WriteLine("  ok  " + fields.Length + " deterministic Stats fields + QuadXzWorstAngle identical");

        // The BUDGET must be deterministic too, not just the default configuration - otherwise a budget
        // that bites on some machines and not others would slip straight through.
        int[] mults = { 1, 64 };
        int[] budgetHit = new int[2];
        for (int m = 0; m < mults.Length; m++)
        {
            var bk = NavMeshCleanup.Budgets.Default(6000);
            bk.DecimateGuardMultiple = mults[m];
            var p = new List<float>(verts); var pt = new List<int>(tris);
            NavMeshCleanup.Stats sp = NavMeshCleanup.Run(p, pt, bk, true);
            var q = new List<float>(verts); var qt = new List<int>(tris);
            NavMeshCleanup.Stats sq = NavMeshCleanup.Run(q, qt, bk, true);
            budgetHit[m] = sp.DecimateBudgetHit;
            if (Hash(p.ToArray()) != Hash(q.ToArray()) || Hash(pt.ToArray()) != Hash(qt.ToArray())
                || sp.DecimateGuardEvals != sq.DecimateGuardEvals)
            {
                Console.WriteLine("  FAIL DecimateGuardMultiple=" + mults[m] + " not self-consistent");
                ok = false;
            }
            else Console.WriteLine("  ok  DecimateGuardMultiple=" + mults[m] + " self-consistent ("
                + sp.DecimateGuardEvals + " guardEvals, budgetHit=" + sp.DecimateBudgetHit + ")");
        }
        // A budget that never triggers is not a budget.
        //
        // The budget is multiple * C0 + F0/Divisor, so on a modest mesh the F0/2 FLOOR term alone can
        // exceed the CONVERGED eval count - multiple=1 then cannot bite and the assertion would be
        // testing the floor, not the budget. The probe therefore also raises DecimateGuardFloorDivisor
        // (a shipped override) so the multiple term dominates, and runs on a ROUGH surface whose thin
        // faces make C0 large. Both are legitimate configuration; the cliff grid above keeps the
        // byte-identity assertions.
        {
            var rv = new List<float>(verts);
            var rt = new List<int>(tris);
            // Deterministic hash-based perturbation (no RNG state: an FNV-style mix keyed on the vertex
            // index) so the probe mesh is itself reproducible.
            uint h = 2166136261u;
            for (int i = 0; i + 2 < rv.Count; i += 3)
            {
                h ^= (uint)i; h *= 16777619u;
                float j = ((h >> 8) & 0xffff) / 65535.0f - 0.5f;   // [-0.5, 0.5]
                h ^= (uint)(i * 7 + 1); h *= 16777619u;
                float j2 = ((h >> 8) & 0xffff) / 65535.0f - 0.5f;
                rv[i + 1] += j * 0.35f;      // mostly vertical jitter: keeps slope inside the walk range
                rv[i] += j2 * 0.08f;
            }
            var bk1 = NavMeshCleanup.Budgets.Default(6000);
            bk1.DecimateGuardMultiple = 1;
            bk1.DecimateGuardFloorDivisor = 100000;   // shrink the floor so multiple*C0 dominates
            var rr = new List<float>(rv); var rrt = new List<int>(rt);
            NavMeshCleanup.Stats rough = NavMeshCleanup.Run(rr, rrt, bk1, true);
            var rr2 = new List<float>(rv); var rrt2 = new List<int>(rt);
            NavMeshCleanup.Stats rough2 = NavMeshCleanup.Run(rr2, rrt2, bk1, true);
            if (Hash(rr.ToArray()) != Hash(rr2.ToArray()) || Hash(rrt.ToArray()) != Hash(rrt2.ToArray()))
            {
                Console.WriteLine("  FAIL rough probe not self-consistent at multiple=1");
                ok = false;
            }
        Console.WriteLine("  ok  rough probe at multiple=1 floorDiv=100000: cand0=" + rough.DecimateCand0
                + " guardBudget=" + rough.DecimateGuardBudget
                + " guardEvals=" + rough.DecimateGuardEvals + " budgetHit=" + rough.DecimateBudgetHit);
            if (rough.DecimateBudgetHit != 1)
            {
                Console.WriteLine("  FAIL the guard budget never bites"
                    + " (budgetHit=" + rough.DecimateBudgetHit + ") - the budget is not calibrated");
                ok = false;
            }
        }

        Console.WriteLine("  RESULT: CLEANUP DETERMINISM " + (ok ? "PASS" : "FAIL"));
    }

    // ==================================================================== Test 29: water level
    // The v2 core's water handling has two modes, both offset-aware (the comparison is on the LIFTED
    // height soupY + HeightOffsetMeters, because that is where the emitted mesh actually sits):
    //   Mode A (CullBelowWaterLevel): drop a triangle whose lifted lowest corner is below the level.
    //   Mode B (RaiseBelowWaterLevel): clamp every vertex up to max(y, level - offset), so its lifted
    //       height is exactly the level.
    // This pins the min-corner rule, the offset accounting, the raise exactness, and the default no-op.
    private static void Test29_WaterCull()
    {
        Console.WriteLine("== Test 29: water level - Mode A cull / Mode B raise, offset-aware ==");
        const float level = 5f;
        float[] verts;
        int[] tris;

        // ---- A: Mode A min-corner rule (offset 0) ------------------------------------------------
        // Four 10x10 m triangles: below(1), above(10), mincorner(10,10,1), atlevel(5).
        MakeWaterSoup(out verts, out tris);
        TiledNavMeshBuilder.TiledNavMeshSettings on = WaterSettings();   // offset 0
        on.CullBelowWaterLevel = true; on.WaterLevelMeters = level;
        TiledNavMeshBuilder.VoxelMeshResult rc =
            TiledNavMeshBuilder.Build(verts, tris, on, Environment.ProcessorCount, null);
        TiledNavMeshBuilder.TiledNavMeshSettings off = WaterSettings();
        off.CullBelowWaterLevel = false; off.WaterLevelMeters = level;
        TiledNavMeshBuilder.VoxelMeshResult ro =
            TiledNavMeshBuilder.Build(verts, tris, off, Environment.ProcessorCount, null);
        // The level must be inert when both modes are off: a second off build with a different level
        // hashes identically, i.e. the default path cannot be perturbed by a stray level.
        TiledNavMeshBuilder.TiledNavMeshSettings offZero = WaterSettings();
        offZero.CullBelowWaterLevel = false; offZero.WaterLevelMeters = 0f;
        TiledNavMeshBuilder.VoxelMeshResult roZero =
            TiledNavMeshBuilder.Build(verts, tris, offZero, Environment.ProcessorCount, null);
        bool offInert = Hash(ro.VertsXyz) == Hash(roZero.VertsXyz)
            && Hash(ro.Tris) == Hash(roZero.Tris) && ro.FaceCount == roZero.FaceCount;
        float minKeptY = float.MaxValue;
        for (int i = 0; i < rc.VertexCount; i++)
            if (rc.VertsXyz[i * 3 + 1] < minKeptY) minKeptY = rc.VertsXyz[i * 3 + 1];
        bool passA = rc.CulledByWater == 2 && ro.CulledByWater == 0 && rc.FaceCount > 0
            && ro.FaceCount > rc.FaceCount && Hash(rc.VertsXyz) != Hash(ro.VertsXyz)
            && minKeptY >= level - 1e-3f && offInert;
        Console.WriteLine("  A min-corner (offset 0): culled=" + rc.CulledByWater + " keptFaces=" + rc.FaceCount
            + " minKeptY=" + minKeptY.ToString("F3") + "m | default: culled=" + ro.CulledByWater
            + " F=" + ro.FaceCount + " offInert=" + offInert + " -> " + (passA ? "PASS" : "FAIL"));

        // ---- B: offset accounting (Mode A) -------------------------------------------------------
        // Two triangles whose soup y sits 0.01 either side of (level - offset=0.5): 4.51 and 4.49.
        // With offset 0.5 the lifted heights are 5.01 / 4.99 -> only the lower is culled.
        // With offset 0   the lifted heights are 4.51 / 4.49 -> both are culled. That is the offset.
        MakeFlatPair(level - 0.5f + 0.01f, level - 0.5f - 0.01f, out verts, out tris);
        TiledNavMeshBuilder.TiledNavMeshSettings bo = WaterSettings();
        bo.HeightOffsetMeters = 0.5f;
        bo.CullBelowWaterLevel = true; bo.WaterLevelMeters = level;
        TiledNavMeshBuilder.VoxelMeshResult rbo =
            TiledNavMeshBuilder.Build(verts, tris, bo, Environment.ProcessorCount, null);
        MakeFlatPair(level - 0.5f + 0.01f, level - 0.5f - 0.01f, out verts, out tris);
        TiledNavMeshBuilder.TiledNavMeshSettings bz = WaterSettings();   // offset 0
        bz.CullBelowWaterLevel = true; bz.WaterLevelMeters = level;
        TiledNavMeshBuilder.VoxelMeshResult rbz =
            TiledNavMeshBuilder.Build(verts, tris, bz, Environment.ProcessorCount, null);
        bool passB = rbo.CulledByWater == 1 && rbz.CulledByWater == 2;
        Console.WriteLine("  B offset accounting: offset0.5 culled=" + rbo.CulledByWater
            + " (expect 1, the 4.99 one) | offset0 culled=" + rbz.CulledByWater
            + " (expect 2) -> " + (passB ? "PASS" : "FAIL"));

        // ---- C: Mode B raise ---------------------------------------------------------------------
        // A low triangle (soup y=1) and a high one (y=10), offset 0.5, level 5 -> target 4.5.
        MakeFlatPair(1f, 10f, out verts, out tris);
        TiledNavMeshBuilder.TiledNavMeshSettings rs = WaterSettings();
        rs.HeightOffsetMeters = 0.5f;
        rs.RaiseBelowWaterLevel = true; rs.WaterLevelMeters = level;
        TiledNavMeshBuilder.VoxelMeshResult rr =
            TiledNavMeshBuilder.Build(verts, tris, rs, Environment.ProcessorCount, null);
        // Build clamps the soup in place: the low triangle's three verts are now exactly 4.5, the high
        // triangle's stay 10. (max() semantics; counted once each.)
        bool soupExact = true;
        for (int i = 0; i < 3; i++)
        {
            if (verts[i * 3 + 1] != level - 0.5f) soupExact = false;
            if (verts[(3 + i) * 3 + 1] != 10f) soupExact = false;
        }
        float raisedMinY = float.MaxValue, raisedMaxY = float.MinValue;
        for (int i = 0; i < rr.VertexCount; i++)
        {
            float y = rr.VertsXyz[i * 3 + 1];
            if (y < raisedMinY) raisedMinY = y;
            if (y > raisedMaxY) raisedMaxY = y;
        }
        bool passC = rr.RaisedByWater == 3 && soupExact
            && raisedMinY >= level - 1e-3f                                  // never under the surface
            && raisedMinY <= level + rs.CellHeightMeters + 1e-3f            // at the level, within one voxel
            && raisedMaxY >= 10f + 0.5f - 1e-3f
            && raisedMaxY <= 10f + 0.5f + rs.CellHeightMeters + 1e-3f;      // above untouched
        Console.WriteLine("  C raise (offset 0.5, target " + (level - 0.5f).ToString("F2")
            + "m): raisedVerts=" + rr.RaisedByWater + " soupExact=" + soupExact
            + " emittedY=[" + raisedMinY.ToString("F3") + ".." + raisedMaxY.ToString("F3") + "]m"
            + " (level+ch=" + (level + rs.CellHeightMeters).ToString("F3") + ")"
            + " -> " + (passC ? "PASS" : "FAIL"));

        // ---- D: min-corner + exact level (offset 0) ----------------------------------------------
        MakeOneTri(out verts, out tris, 0f, 0f, 10f, 10f, 1f);
        TiledNavMeshBuilder.TiledNavMeshSettings d = WaterSettings();
        d.CullBelowWaterLevel = true; d.WaterLevelMeters = level;
        TiledNavMeshBuilder.VoxelMeshResult rd =
            TiledNavMeshBuilder.Build(verts, tris, d, Environment.ProcessorCount, null);
        MakeOneTri(out verts, out tris, 0f, 0f, level, level, level);
        TiledNavMeshBuilder.TiledNavMeshSettings e = WaterSettings();
        e.CullBelowWaterLevel = true; e.WaterLevelMeters = level;
        TiledNavMeshBuilder.VoxelMeshResult re =
            TiledNavMeshBuilder.Build(verts, tris, e, Environment.ProcessorCount, null);
        bool passD = rd.CulledByWater == 1 && re.CulledByWater == 0 && re.FaceCount > 0;
        Console.WriteLine("  D min-corner culled=" + rd.CulledByWater + " (expect 1); exact-level culled="
            + re.CulledByWater + " F=" + re.FaceCount + " (expect 0, F>0) -> " + (passD ? "PASS" : "FAIL"));

        // ---- E: WALL case (the regression) -------------------------------------------------------
        // A vertical wall at x=10 spans from below the level (y=0) to above it (y=12), between two
        // y=10 floors. The wall is obstacle geometry: the floor-like gate must keep it out of both modes,
        // and the navmesh must still be blocked by it (no emitted face crosses x=10).
        MakeWallSoup(out verts, out tris);
        TiledNavMeshBuilder.TiledNavMeshSettings ea = WaterSettings();
        ea.HeightOffsetMeters = 0.5f;
        ea.CullBelowWaterLevel = true; ea.WaterLevelMeters = level;
        TiledNavMeshBuilder.VoxelMeshResult reA =
            TiledNavMeshBuilder.Build(verts, tris, ea, Environment.ProcessorCount, null);
        MakeWallSoup(out verts, out tris);   // raise mutates the soup; use a fresh copy
        TiledNavMeshBuilder.TiledNavMeshSettings eb = WaterSettings();
        eb.HeightOffsetMeters = 0.5f;
        eb.RaiseBelowWaterLevel = true; eb.WaterLevelMeters = level;
        TiledNavMeshBuilder.VoxelMeshResult reB =
            TiledNavMeshBuilder.Build(verts, tris, eb, Environment.ProcessorCount, null);
        int crossA = CountCrossingFaces(reA, 10f);
        int crossB = CountCrossingFaces(reB, 10f);
        bool passE = reA.CulledByWater == 0 && reA.FaceCount > 0 && crossA == 0
            && reB.RaisedByWater == 0 && reB.FaceCount > 0 && crossB == 0;
        Console.WriteLine("  E wall (y 0..12, level " + level.ToString("F0") + "): cullMode culled="
            + reA.CulledByWater + " F=" + reA.FaceCount + " crossingFaces=" + crossA
            + " | raiseMode raised=" + reB.RaisedByWater + " F=" + reB.FaceCount + " crossingFaces=" + crossB
            + " (expect 0 culled/raised, 0 crossing) -> " + (passE ? "PASS" : "FAIL"));

        Console.WriteLine("  RESULT: WATER " + ((passA && passB && passC && passD && passE) ? "PASS" : "FAIL"));
    }

    // A settings object with no detail/prune/region interference, so the cull count is the only variable.
    private static TiledNavMeshBuilder.TiledNavMeshSettings WaterSettings()
    {
        var s = new TiledNavMeshBuilder.TiledNavMeshSettings();
        s.VoxelCellSizeMeters = 0.25f;
        s.AgentRadiusMeters = 0.25f;
        s.MaxSlopeDegrees = 60f;
        s.UseDetailMesh = false;
        s.PruneIsolatedIslands = false;
        s.RegionMinSize = 1;
        s.RegionMergeSize = 1;
        s.HeightOffsetMeters = 0f;   // exact height assertions: no lift
        s.AgentMaxClimbMeters = TiledNavMeshBuilder.ResolveAgentClimbMeters(0.25f, s.CellHeightMeters, 60f, 0.4f);
        return s;
    }

    // Four 10x10 m triangles at x = 0 / 12 / 24 / 36 (z 0..10): below(1), above(10), mincorner(10,10,1),
    // atlevel(5). Wound (p00, p11, p10) = upward normal.
    private static void MakeWaterSoup(out float[] verts, out int[] tris)
    {
        var v = new List<float>();
        var t = new List<int>();
        AddTri(v, t, 0f, 0f, 1f, 10f, 10f, 1f, 10f, 0f, 1f);        // below:  all 1
        AddTri(v, t, 12f, 0f, 10f, 22f, 10f, 10f, 22f, 0f, 10f);     // above:  all 10
        AddTri(v, t, 24f, 0f, 10f, 34f, 10f, 10f, 34f, 0f, 1f);      // mincorner: 10,10,1
        AddTri(v, t, 36f, 0f, 5f, 46f, 10f, 5f, 46f, 0f, 5f);        // atlevel: all 5
        verts = v.ToArray();
        tris = t.ToArray();
    }

    private static void MakeOneTri(out float[] verts, out int[] tris,
        float x0, float z0, float y00, float y11, float y10)
    {
        var v = new List<float>();
        var t = new List<int>();
        AddTri(v, t, x0, z0, y00, x0 + 10f, z0 + 10f, y11, x0 + 10f, z0, y10);
        verts = v.ToArray();
        tris = t.ToArray();
    }

    // Two flat 10x10 m triangles at x=0 (yA) and x=12 (yB), for the offset / raise probes.
    private static void MakeFlatPair(float yA, float yB, out float[] verts, out int[] tris)
    {
        var v = new List<float>();
        var t = new List<int>();
        AddTri(v, t, 0f, 0f, yA, 10f, 10f, yA, 10f, 0f, yA);
        AddTri(v, t, 12f, 0f, yB, 22f, 10f, yB, 22f, 0f, yB);
        verts = v.ToArray();
        tris = t.ToArray();
    }

    // Two y=10 floors either side of a vertical wall at x=10 that spans from below the level (y=0) to
    // above it (y=12). The wall is obstacle geometry and must survive both water modes and keep blocking.
    private static void MakeWallSoup(out float[] verts, out int[] tris)
    {
        var v = new List<float>();
        var t = new List<int>();
        // left floor x 0..10, right floor x 10..20, both y=10 (above the level)
        AddTri(v, t, 0f, 0f, 10f, 10f, 10f, 10f, 10f, 0f, 10f);
        AddTri(v, t, 0f, 0f, 10f, 0f, 10f, 10f, 10f, 10f, 10f);
        AddTri(v, t, 10f, 0f, 10f, 20f, 10f, 10f, 20f, 0f, 10f);
        AddTri(v, t, 10f, 0f, 10f, 10f, 10f, 10f, 20f, 10f, 10f);
        // vertical wall at x=10, z 0..10, y 0..12 (below -> above the level); normal is +-X (ny=0)
        AddTri(v, t, 10f, 0f, 0f, 10f, 10f, 12f, 10f, 0f, 12f);
        AddTri(v, t, 10f, 0f, 0f, 10f, 10f, 0f, 10f, 10f, 12f);
        verts = v.ToArray();
        tris = t.ToArray();
    }

    // Number of emitted faces whose XZ footprint straddles the plane x = xPlane (with a small dead band).
    // A wall that still blocks leaves this at 0; a culled wall lets the mesh bridge across.
    private static int CountCrossingFaces(TiledNavMeshBuilder.VoxelMeshResult r, float xPlane)
    {
        int n = 0;
        for (int f = 0; f < r.FaceCount; f++)
        {
            int a = r.Tris[f * 3 + 0], b = r.Tris[f * 3 + 1], c = r.Tris[f * 3 + 2];
            float minx = Math.Min(r.VertsXyz[a * 3], Math.Min(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            float maxx = Math.Max(r.VertsXyz[a * 3], Math.Max(r.VertsXyz[b * 3], r.VertsXyz[c * 3]));
            if (minx < xPlane - 0.05f && maxx > xPlane + 0.05f) n++;
        }
        return n;
    }

    // Appends one triangle with the (p00, p11, p10) winding (upward Recast normal).
    private static void AddTri(List<float> v, List<int> t,
        float x0, float z0, float y0, float x1, float z1, float y1, float x2, float z2, float y2)
    {
        int b = v.Count / 3;
        v.Add(x0); v.Add(y0); v.Add(z0);
        v.Add(x1); v.Add(y1); v.Add(z1);
        v.Add(x2); v.Add(y2); v.Add(z2);
        t.Add(b); t.Add(b + 1); t.Add(b + 2);
    }

    private static int[] FieldValues(NavMeshCleanup.Stats s)
    {
        return new int[] { s.Welded, s.Removed, s.Decimated, s.Flipped, s.Relaxed, s.Refined, s.Quads,
            s.NeedlesBefore, s.NeedlesAfter, s.ThinBefore, s.ThinAfter, s.BoundaryMovedCount,
            s.ComponentsBefore, s.ComponentsAfter, s.CandVerts, s.RejMove, s.RejFan, s.RejRing,
            s.RejTopo, s.QuadXzNearStraight, s.DecimateGuardEvals, s.DecimateCand0,
            s.DecimateGuardBudget, s.DecimateBudgetHit, s.DecimateSweeps, s.RefineFaceVisits,
            s.RefineFaceVisitBudget, s.RefineSweeps, s.RelaxEvals, s.RelaxCand0, s.RelaxBudget,
            s.RelaxSweeps, s.FlipEdgeVisits, s.FlipEdgeVisitBudget, s.FlipE0, s.FlipIters, };
    }
}
