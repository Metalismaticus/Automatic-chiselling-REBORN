using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace AutomaticChiselling
{
    /// <summary>
    /// Renders a translucent 3D hologram of a .vox model in the world.
    /// Uses CubeMeshUtil.GetCube() per voxel — same mesh format as clay forming.
    /// </summary>
    public class HologramRenderer : IRenderer, IDisposable
    {
        private ICoreClientAPI capi;
        private MeshRef meshRef;
        private Matrixf modelMat = new Matrixf();
        private bool visible = false;
        private int textureId;

        // Model origin in world-block coords (double precision for no jitter)
        private double originX, originY, originZ;

        // Model extent in world-block units (for shader gradient)
        private float modelHeightBlocks = 1f;

        // Custom shader (animated vertical gradient). Set via SetShader. If null, fall
        // back to the standard shader path.
        private IShaderProgram hologramShader;
        private float elapsedSec = 0f;

        public double RenderOrder => 0.5;
        public int RenderRange => 200;

        public HologramRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
            capi.Event.RegisterRenderer(this, EnumRenderStage.Opaque);
            textureId = 0;
        }

        /// <summary>Assigns (or clears) the custom hologram shader program.</summary>
        public void SetShader(IShaderProgram program)
        {
            hologramShader = program;
        }

        private void EnsureTextureId()
        {
            if (textureId != 0) return;
            try
            {
                var atlas = capi?.BlockTextureAtlas;
                var tex = atlas?.AtlasTextures;
                if (tex != null && tex.Count > 0 && tex[0] != null)
                    textureId = tex[0].TextureId;
            }
            catch { /* atlas still not ready — try again next frame */ }
        }

        public void SetVisible(bool v) => visible = v;

        public void SetModel(VoxelsStorage storage)
        {
            meshRef?.Dispose();
            meshRef = null;

            try
            {
                var voxelBlocks = storage.GetVoxelBlocks();
                if (voxelBlocks == null || voxelBlocks.Count == 0) return;

                var palette = storage.Palette;

                // Step 1: Find bounding box in block coords
                int minBX = int.MaxValue, minBY = int.MaxValue, minBZ = int.MaxValue;
                int maxBX = int.MinValue, maxBY = int.MinValue, maxBZ = int.MinValue;
                foreach (var bp in voxelBlocks.Keys)
                {
                    if (bp.X < minBX) minBX = bp.X; if (bp.X > maxBX) maxBX = bp.X;
                    if (bp.Y < minBY) minBY = bp.Y; if (bp.Y > maxBY) maxBY = bp.Y;
                    if (bp.Z < minBZ) minBZ = bp.Z; if (bp.Z > maxBZ) maxBZ = bp.Z;
                }
                originX = minBX; originY = minBY; originZ = minBZ;

                // Step 2: Build a flat 3D bool array covering ALL voxels of the model.
                // +2 padding so border voxels naturally see "air" in all 6 directions.
                int sizeX = (maxBX - minBX + 1) * 16 + 2;
                int sizeY = (maxBY - minBY + 1) * 16 + 2;
                int sizeZ = (maxBZ - minBZ + 1) * 16 + 2;

                // Model height in world-block units (1 block = 16 voxels = 1 unit in mesh coords)
                modelHeightBlocks = Math.Max(1f, maxBY - minBY + 1);

                // Safety: skip mesh for enormous models (> 20M voxels grid)
                long totalCells = (long)sizeX * sizeY * sizeZ;
                if (totalCells > 20_000_000) return;

                bool[,,] solid = new bool[sizeX, sizeY, sizeZ];
                byte[,,] colors = new byte[sizeX, sizeY, sizeZ];

                // Step 3: Fill the array from voxel blocks (local coords with +1 padding)
                foreach (var kvp in voxelBlocks)
                {
                    var bp = kvp.Key;
                    var bd = kvp.Value;
                    int bxOff = (bp.X - minBX) * 16 + 1; // +1 for padding
                    int byOff = (bp.Y - minBY) * 16 + 1;
                    int bzOff = (bp.Z - minBZ) * 16 + 1;
                    for (int x = 0; x < 16; x++)
                        for (int y = 0; y < 16; y++)
                            for (int z = 0; z < 16; z++)
                                if (bd.Voxels[x, y, z])
                                {
                                    solid[bxOff + x, byOff + y, bzOff + z] = true;
                                    colors[bxOff + x, byOff + y, bzOff + z] = bd.MaterialIndex[x, y, z];
                                }
                }

                // Step 4: For every solid voxel, check its 6 neighbors in the array.
                // If any neighbor is empty → surface voxel → render.
                var renderVoxels = new List<(int lx, int ly, int lz, byte ci)>();
                for (int x = 1; x < sizeX - 1; x++)
                    for (int y = 1; y < sizeY - 1; y++)
                        for (int z = 1; z < sizeZ - 1; z++)
                        {
                            if (!solid[x, y, z]) continue;
                            // Check 6 neighbors directly — padding ensures border voxels see "air"
                            bool exposed = !solid[x + 1, y, z] || !solid[x - 1, y, z]
                                        || !solid[x, y + 1, z] || !solid[x, y - 1, z]
                                        || !solid[x, y, z + 1] || !solid[x, y, z - 1];
                            if (exposed)
                            {
                                // Convert back to local coords (without padding) for mesh rendering
                                renderVoxels.Add((x - 1, y - 1, z - 1, colors[x, y, z]));
                            }
                        }

                // If too many, downsample by keeping whole Y layers uniformly.
                // This gives clean horizontal stripes instead of random gaps.
                const int MAX_RENDER = 90000;
                if (renderVoxels.Count > MAX_RENDER)
                {
                    // Find Y range
                    int minY = int.MaxValue, maxY = int.MinValue;
                    foreach (var v in renderVoxels)
                    {
                        if (v.ly < minY) minY = v.ly;
                        if (v.ly > maxY) maxY = v.ly;
                    }
                    int yCount = maxY - minY + 1;

                    // How much do we need to reduce? e.g. 120K → 90K = keep 75%
                    float keepRatio = (float)MAX_RENDER / renderVoxels.Count;
                    // Keep every Nth Y layer
                    int yStep = (int)Math.Ceiling(1f / keepRatio);

                    var keptY = new HashSet<int>();
                    for (int y = minY; y <= maxY; y += yStep) keptY.Add(y);

                    var trimmed = new List<(int, int, int, byte)>();
                    foreach (var v in renderVoxels)
                        if (keptY.Contains(v.ly)) trimmed.Add(v);
                    renderVoxels = trimmed;
                }

                // Build mesh with LOCAL coordinates (small numbers → no float jitter)
                float voxelHalf = 1f / 32f;
                MeshData combined = new MeshData(24, 36).WithColorMaps().WithRenderpasses().WithXyzFaces();

                foreach (var (lx, ly, lz, ci) in renderVoxels)
                {
                    // Local position: voxel coords / 16 (relative to model origin)
                    float px = lx / 16f + voxelHalf;
                    float py = ly / 16f + voxelHalf;
                    float pz = lz / 16f + voxelHalf;

                    MeshData cube = CubeMeshUtil.GetCube(voxelHalf, voxelHalf, new Vec3f(px, py, pz));

                    // Color from palette
                    byte r = 80, g = 180, b = 255, a = 120;
                    if (palette != null && ci < palette.Length && palette[ci] != null)
                    { r = palette[ci][0]; g = palette[ci][1]; b = palette[ci][2]; }

                    if (cube.Rgba != null)
                    {
                        for (int i = 0; i < cube.Rgba.Length; i += 4)
                        {
                            cube.Rgba[i + 0] = r;
                            cube.Rgba[i + 1] = g;
                            cube.Rgba[i + 2] = b;
                            cube.Rgba[i + 3] = a;
                        }
                    }

                    combined.AddMeshData(cube);
                }

                if (combined.VerticesCount > 0)
                {
                    meshRef = capi.Render.UploadMesh(combined);
                    // Mesh uploaded
                }
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[Hologram] SetModel error: " + e);
            }
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (!visible || meshRef == null) return;

            EnsureTextureId();
            if (textureId == 0) return;

            elapsedSec += deltaTime;

            IRenderAPI rpi = capi.Render;
            Vec3d camPos = capi.World.Player.Entity.CameraPos;

            rpi.GlToggleBlend(true, EnumBlendMode.Standard);
            rpi.GLEnableDepthTest();
            rpi.GLDepthMask(false);

            // Camera-relative model matrix (double precision first, then float → no jitter)
            var modelMatrix = modelMat
                .Identity()
                .Translate(
                    (float)(originX - camPos.X),
                    (float)(originY - camPos.Y),
                    (float)(originZ - camPos.Z))
                .Values;

            if (hologramShader != null)
            {
                // === Custom animated-gradient shader path ===
                hologramShader.Use();
                hologramShader.UniformMatrix("projectionMatrix", rpi.CurrentProjectionMatrix);
                hologramShader.UniformMatrix("viewMatrix", rpi.CameraMatrixOriginf);
                hologramShader.UniformMatrix("modelMatrix", modelMatrix);

                // World position of the model's local-(0,0,0) — used to reconstruct world-space
                // Y inside the fragment shader for the gradient calculation.
                hologramShader.Uniform("uChunkWorldPos",
                    new Vec3f((float)originX, (float)originY, (float)originZ));

                // Gradient parameters
                hologramShader.Uniform("uMinY", (float)originY);
                hologramShader.Uniform("uHeight", modelHeightBlocks);
                hologramShader.Uniform("uTime", elapsedSec);
                hologramShader.Uniform("uColorA", new Vec3f(0.30f, 0.85f, 1.00f));    // cyan
                hologramShader.Uniform("uColorB", new Vec3f(0.75f, 0.30f, 1.00f));    // violet
                hologramShader.Uniform("uSpeedBlocksPerSec", 2.0f);

                rpi.BindTexture2d(textureId);
                rpi.RenderMesh(meshRef);
                hologramShader.Stop();
            }
            else
            {
                // === Fallback: standard VS shader (used if custom shader failed to compile) ===
                IStandardShaderProgram prog = rpi.PreparedStandardShader(
                    (int)camPos.X, (int)camPos.Y, (int)camPos.Z);
                prog.ExtraGlow = 50;
                prog.ModelMatrix = modelMatrix;
                prog.ViewMatrix = rpi.CameraMatrixOriginf;
                prog.ProjectionMatrix = rpi.CurrentProjectionMatrix;
                rpi.BindTexture2d(textureId);
                rpi.RenderMesh(meshRef);
                prog.Stop();
            }
            rpi.GLDepthMask(true);
        }

        public void Dispose()
        {
            meshRef?.Dispose();
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Opaque);
        }
    }
}
