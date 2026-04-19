using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;
using Vintagestory.API.Datastructures;

namespace AutomaticChiselling
{
    internal static class Patchs
    {
        private static Harmony harmony;

        // Cached reflection fields — resolved once, used every call
        private static FieldInfo poolsField;
        private static FieldInfo capiField;
        private static FieldInfo defaultIndexPoolSizeField;
        private static FieldInfo defaultVertexPoolSizeField;
        private static FieldInfo maxPartsPerPoolField;
        private static FieldInfo customFloatsField;
        private static FieldInfo customShortsField;
        private static FieldInfo customBytesField;
        private static FieldInfo customIntsField;
        private static FieldInfo masterPoolField;
        private static FieldInfo poolOriginField;
        private static FieldInfo dimensionIdField;

        private static bool patchApplied = false;
        private static bool reflectionCached = false;

        public static void PatchAll()
        {
            if (patchApplied) return;

            try
            {
                harmony = new Harmony("AutomaticChiselling");
                PatchAddModel();
                patchApplied = true;
            }
            catch (Exception)
            {
                // If patching fails (e.g., API changed), the mod still works — just without pool expansion
            }
        }

        public static void UnpatchAll()
        {
            harmony?.UnpatchAll("AutomaticChiselling");
            patchApplied = false;
        }

        private static void PatchAddModel()
        {
            var originalMethod = typeof(MeshDataPoolManager).GetMethod("AddModel", BindingFlags.Public | BindingFlags.Instance);
            if (originalMethod == null) return; // Method signature changed in new API

            var prefixMethod = typeof(Patchs).GetMethod("AddModelPrefix", BindingFlags.Public | BindingFlags.Static);
            harmony.Patch(originalMethod, new HarmonyMethod(prefixMethod));
        }

        private static void CacheReflection()
        {
            if (reflectionCached) return;

            var managerType = typeof(MeshDataPoolManager);
            var poolType = typeof(MeshDataPool);

            poolsField = managerType.GetField("pools", BindingFlags.NonPublic | BindingFlags.Instance);
            capiField = managerType.GetField("capi", BindingFlags.NonPublic | BindingFlags.Instance);
            defaultIndexPoolSizeField = managerType.GetField("defaultIndexPoolSize", BindingFlags.NonPublic | BindingFlags.Instance);
            defaultVertexPoolSizeField = managerType.GetField("defaultVertexPoolSize", BindingFlags.NonPublic | BindingFlags.Instance);
            maxPartsPerPoolField = managerType.GetField("maxPartsPerPool", BindingFlags.NonPublic | BindingFlags.Instance);
            customFloatsField = managerType.GetField("customFloats", BindingFlags.NonPublic | BindingFlags.Instance);
            customShortsField = managerType.GetField("customShorts", BindingFlags.NonPublic | BindingFlags.Instance);
            customBytesField = managerType.GetField("customBytes", BindingFlags.NonPublic | BindingFlags.Instance);
            customIntsField = managerType.GetField("customInts", BindingFlags.NonPublic | BindingFlags.Instance);
            masterPoolField = managerType.GetField("masterPool", BindingFlags.NonPublic | BindingFlags.Instance);

            poolOriginField = poolType.GetField("poolOrigin", BindingFlags.NonPublic | BindingFlags.Instance);
            dimensionIdField = poolType.GetField("dimensionId", BindingFlags.NonPublic | BindingFlags.Instance);

            reflectionCached = true;
        }

        public static bool AddModelPrefix(MeshDataPoolManager __instance, MeshData modeldata, Vec3i modelOrigin, int dimension, Sphere frustumCullSphere, ref ModelDataPoolLocation __result)
        {
            try
            {
                CacheReflection();

                // Validate required fields are available
                if (poolsField == null || capiField == null || defaultIndexPoolSizeField == null ||
                    defaultVertexPoolSizeField == null || masterPoolField == null)
                {
                    return true; // Fall back to original method
                }

                var pools = (List<MeshDataPool>)poolsField.GetValue(__instance);
                var clientApi = (ICoreClientAPI)capiField.GetValue(__instance);

                ModelDataPoolLocation location = null;
                for (int i = 0; i < pools.Count; i++)
                {
                    location = pools[i].TryAdd(clientApi, modeldata, modelOrigin, dimension, frustumCullSphere);
                    if (location != null) break;
                }

                if (location == null)
                {
                    int defaultVertexSize = (int)defaultVertexPoolSizeField.GetValue(__instance);
                    int defaultIndexSize = (int)defaultIndexPoolSizeField.GetValue(__instance);
                    int maxParts = (int)maxPartsPerPoolField.GetValue(__instance);

                    int vertexSize = Math.Max(modeldata.VerticesCount + 1, defaultVertexSize);
                    int indexSize = Math.Max(modeldata.IndicesCount + 1, defaultIndexSize);

                    if (vertexSize > defaultVertexSize)
                    {
                        clientApi.World.Logger.Warning(
                            "AutoChisel: Mesh at {0} exceeds default pool limits (#v={1}, #i={2}). Expanding pool.",
                            modelOrigin, modeldata.VerticesCount, modeldata.IndicesCount
                        );
                    }

                    var customFloats = (CustomMeshDataPartFloat)customFloatsField?.GetValue(__instance);
                    var customShorts = (CustomMeshDataPartShort)customShortsField?.GetValue(__instance);
                    var customBytes = (CustomMeshDataPartByte)customBytesField?.GetValue(__instance);
                    var customInts = (CustomMeshDataPartInt)customIntsField?.GetValue(__instance);

                    MeshDataPool pool = MeshDataPool.AllocateNewPool(
                        clientApi, vertexSize * 2, indexSize * 2, maxParts,
                        customFloats, customShorts, customBytes, customInts
                    );

                    poolOriginField?.SetValue(pool, modelOrigin);
                    dimensionIdField?.SetValue(pool, dimension);

                    var masterPool = (MeshDataPoolMasterManager)masterPoolField.GetValue(__instance);
                    masterPool.AddModelDataPool(pool);
                    pools.Add(pool);

                    location = pool.TryAdd(clientApi, modeldata, modelOrigin, dimension, frustumCullSphere);
                }

                if (location == null)
                {
                    clientApi.World.Logger.Error(
                        "AutoChisel: Cannot add mesh at {0} to pool (#v={1}, #i={2}). Chunk will be invisible.",
                        modelOrigin, modeldata.VerticesCount, modeldata.IndicesCount
                    );
                }

                __result = location;
                return false; // Skip original method
            }
            catch (Exception)
            {
                return true; // On any error, fall back to original method
            }
        }
    }
}
