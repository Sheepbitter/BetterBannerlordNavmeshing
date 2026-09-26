using System;
using System.Diagnostics;
using System.Globalization;

namespace BetterBannerlordNavmeshing
{
    // ==================================================================
    // Tiled voxel core -> NMG8 mesh (editor-facing adapter)
    // ==================================================================
    // Bridges the pure TiledNavMeshBuilder into the v1 export pipeline:
    //  - terrain grid -> Recast Y-up triangle soup (same stride semantics as v1),
    //  - voxel build (parallel tiles),
    //  - Recast Y-up -> engine Z-up conversion,
    //  - NMG8 serialization via the shared Nmg8Writer.
    // The resulting TerrainNavMeshBuilder.Result is fed to the same
    // TerrainNavMeshGenerator.WriteAndImport used by the v1 mesher.
    public static class VoxelNavMeshGenerator
    {
        public static TerrainNavMeshBuilder.Result BuildExportMesh(
            float[] stitchedHeights, int width, int height, float terrainVertexSpacing, int sampleStride,
            TiledNavMeshBuilder.TiledNavMeshSettings voxelSettings, bool writeObj,
            float agentClimbOverrideMeters, int maxWorkers,
            System.Action<string> log, out long buildMs)
        {
            if (log != null)
                log("v2: core engaged (tiled voxel). cellSize=" + F(voxelSettings.VoxelCellSizeMeters)
                    + "m tile=" + F(voxelSettings.TileWorldSizeMeters) + "m");

            // Resolve the agent climb that actually honours MaxSlopeDegrees (0 = auto). Recast's ledge
            // filter needs walkableClimb >= 2*cs*tan(slope)/ch, so the naive cs*tan(slope) is too small.
            float cs = voxelSettings.VoxelCellSizeMeters;
            float ch = voxelSettings.CellHeightMeters;
            float slopeDeg = voxelSettings.MaxSlopeDegrees;
            bool autoClimb = agentClimbOverrideMeters <= 0f;
            float agentClimb = autoClimb
                ? TiledNavMeshBuilder.ResolveAgentClimbMeters(cs, ch, slopeDeg, 0.4f)
                : agentClimbOverrideMeters;
            voxelSettings.AgentMaxClimbMeters = agentClimb;

            int walkableClimb = TiledNavMeshBuilder.WalkableClimbVoxels(agentClimb, ch);
            double effectiveMaxSlope = Math.Atan((double)walkableClimb * ch / (2.0 * cs)) * 180.0 / Math.PI;

            if (log != null)
            {
                log("v2: agentClimb=" + F(agentClimb) + "m (" + (autoClimb ? "auto" : "override") + ")"
                    + " walkableClimb=" + walkableClimb + " voxels"
                    + " effectiveMaxSlope=atan(walkableClimb*ch/(2*cs))=" + F((float)effectiveMaxSlope) + "deg"
                    + " (setting=" + F(slopeDeg) + "deg)");
                if (effectiveMaxSlope < slopeDeg - 1.0)
                    log("v2: WARNING effectiveMaxSlope=" + F((float)effectiveMaxSlope) + "deg < setting="
                        + F(slopeDeg) + "deg-1; increase NavMeshVoxelAgentClimbMeters or reduce NavMeshVoxelCellSize");
            }

            var watch = Stopwatch.StartNew();

            float[] vertsXyz;
            int[] tris;
            TiledNavMeshBuilder.BuildTerrainSoup(stitchedHeights, width, height, terrainVertexSpacing,
                sampleStride, out vertsXyz, out tris);
            if (log != null)
                log("v2: soup verts=" + (vertsXyz.Length / 3) + " tris=" + (tris.Length / 3)
                    + " stride=" + sampleStride + " spacing=" + F(terrainVertexSpacing) + "m");

            TiledNavMeshBuilder.VoxelMeshResult vres =
                TiledNavMeshBuilder.Build(vertsXyz, tris, voxelSettings, maxWorkers, log);

            watch.Stop();
            buildMs = watch.ElapsedMilliseconds;

            var result = new TerrainNavMeshBuilder.Result();
            result.VertexCount = vres.VertexCount;
            result.FaceCount = vres.FaceCount;
            result.EdgeCount = vres.EdgeCount;
            result.CulledBySlope = 0;
            result.CulledByHeight = 0;

            if (vres.VertexCount == 0 || vres.FaceCount == 0)
            {
                result.Bin = new byte[0];
                result.MinZ = 0f;
                result.MaxZ = 0f;
                return result;
            }

            // Recast Y-up -> engine Z-up (matches the v1 face convention).
            TiledNavMeshBuilder.ToEngineSpace(vres.VertsXyz, vres.Tris);

            int edgeCount;
            result.Bin = Nmg8Writer.WriteInterleaved(vres.VertsXyz, vres.VertexCount, vres.Tris,
                vres.FaceCount, out edgeCount);
            result.EdgeCount = edgeCount;

            if (writeObj)
                result.ObjText = BuildObjText(vres.VertsXyz, vres.VertexCount, vres.Tris, vres.FaceCount);

            float minZ = vres.VertsXyz[2];
            float maxZ = minZ;
            for (int i = 0; i < vres.VertexCount; i++)
            {
                float z = vres.VertsXyz[i * 3 + 2];
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
            result.MinZ = minZ;
            result.MaxZ = maxZ;

            if (log != null)
                log("v2: mesh V=" + result.VertexCount + " E=" + result.EdgeCount + " F=" + result.FaceCount
                    + " degenerate=" + vres.DegenerateFaces + " binBytes=" + result.Bin.Length
                    + " buildMs=" + buildMs);

            return result;
        }

        // Same OBJ layout as the v1 mesher (debug view; engine Z-up).
        private static string BuildObjText(float[] vertsXyz, int vertexCount, int[] tris, int faceCount)
        {
            var ic = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append("# terrain navmesh (v2 voxel core)");
            sb.AppendLine();
            sb.Append("# vertices ").Append(vertexCount.ToString(ic))
              .Append(" faces ").Append(faceCount.ToString(ic));
            sb.AppendLine();

            for (int i = 0; i < vertexCount; i++)
            {
                sb.Append("v ")
                  .Append(vertsXyz[i * 3 + 0].ToString("R", ic)).Append(' ')
                  .Append(vertsXyz[i * 3 + 1].ToString("R", ic)).Append(' ')
                  .Append(vertsXyz[i * 3 + 2].ToString("R", ic)).AppendLine();
            }

            for (int f = 0; f < faceCount; f++)
            {
                sb.Append("f ")
                  .Append((tris[f * 3 + 0] + 1).ToString(ic)).Append(' ')
                  .Append((tris[f * 3 + 1] + 1).ToString(ic)).Append(' ')
                  .Append((tris[f * 3 + 2] + 1).ToString(ic)).AppendLine();
            }

            return sb.ToString();
        }

        private static string F(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
