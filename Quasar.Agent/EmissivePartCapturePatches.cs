using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using VRage.Game.Entity;
using VRage.Game.Graphics;
using VRage.Utils;
using VRageMath;

namespace Quasar.Agent
{
    internal static class EmissivePartCapturePatches
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> DefaultEmissivePartsMethods = new ConcurrentDictionary<Type, MethodInfo>();
        private static Harmony _harmony;
        private static bool _applied;

        public static void Apply()
        {
            if (_applied)
                return;

            _applied = true;
            try
            {
                _harmony = new Harmony("quasar.agent.emissiveParts");
                var count = 0;
                count += Patch(AccessTools.Method(typeof(MyEntity), "SetEmissiveParts"), nameof(MyEntitySetEmissivePartsPrefix)) ? 1 : 0;
                count += Patch(AccessTools.Method(typeof(MyCubeBlock), "SetEmissiveState"), nameof(MyCubeBlockSetEmissiveStatePrefix)) ? 1 : 0;
                count += Patch(AccessTools.Method(typeof(MyCubeBlock), "UpdateEmissiveParts"), nameof(MyCubeBlockUpdateEmissivePartsPrefix)) ? 1 : 0;
                count += Patch(AccessTools.Method(typeof(MyEntity), "UpdateNamedEmissiveParts"), nameof(MyEntityUpdateNamedEmissivePartsPrefix)) ? 1 : 0;
                Console.WriteLine($"Quasar emissive part capture patches applied: {count}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Quasar emissive part capture patches failed: {exception.Message}");
            }
        }

        public static void Dispose()
        {
            try
            {
                _harmony?.UnpatchAll("quasar.agent.emissiveParts");
            }
            catch
            {
            }
            finally
            {
                _harmony = null;
                _applied = false;
                DefaultEmissivePartsMethods.Clear();
            }
        }

        private static bool Patch(MethodBase method, string prefixMethodName)
        {
            if (method == null || _harmony == null)
                return false;

            try
            {
                var prefix = new HarmonyMethod(typeof(EmissivePartCapturePatches).GetMethod(prefixMethodName, BindingFlags.Static | BindingFlags.NonPublic));
                _harmony.Patch(method, prefix);
                return true;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Quasar emissive part capture patch skipped: {method.DeclaringType?.FullName}.{method.Name}: {exception.Message}");
                return false;
            }
        }

        private static void MyEntitySetEmissivePartsPrefix(MyEntity __instance, string emissiveName, Color emissivePartColor, float emissivity)
        {
            if (__instance is MyCubeBlock)
            {
                EmissivePartCaptureCache.RegisterRenderObjectIds(__instance);
                EmissivePartCaptureCache.Record(__instance, emissiveName, emissivePartColor, emissivity, "entity");
            }
        }

        private static void MyEntityUpdateNamedEmissivePartsPrefix(uint renderObjectId, string emissiveName, Color emissivePartColor, float emissivity)
        {
            EmissivePartCaptureCache.RecordByRenderObjectId(renderObjectId, emissiveName, emissivePartColor, emissivity, "renderObject");
        }

        private static void MyCubeBlockSetEmissiveStatePrefix(MyCubeBlock __instance, MyStringHash state, uint renderObjectId, string namedPart)
        {
            if (__instance == null || __instance.BlockDefinition == null || !RenderIdBelongsTo(__instance, renderObjectId))
                return;

            EmissivePartCaptureCache.RegisterRenderObjectIds(__instance);
            if (!TryResolveState(__instance, state, out var result))
                return;

            if (!string.IsNullOrEmpty(namedPart))
            {
                EmissivePartCaptureCache.Record(__instance, namedPart, result.EmissiveColor, result.Emissivity, "cubeBlockState");
                return;
            }

            for (byte i = 0; i < 32; i++)
            {
                var materialName = DefaultEmissivePart(__instance, i);
                if (string.IsNullOrEmpty(materialName))
                    break;

                EmissivePartCaptureCache.Record(__instance, materialName, result.EmissiveColor, result.Emissivity, "cubeBlockState");
            }
        }

        private static void MyCubeBlockUpdateEmissivePartsPrefix(MyCubeBlock __instance, uint renderObjectId, float emissivity, Color emissivePartColor, Color displayPartColor)
        {
            if (__instance == null || !RenderIdBelongsTo(__instance, renderObjectId))
                return;

            EmissivePartCaptureCache.RegisterRenderObjectIds(__instance);
            EmissivePartCaptureCache.Record(__instance, "Emissive", emissivePartColor, emissivity, "cubeBlockParts");
            EmissivePartCaptureCache.Record(__instance, "Display", displayPartColor, emissivity, "cubeBlockParts");
        }

        private static bool TryResolveState(MyCubeBlock block, MyStringHash state, out MyEmissiveColorStateResult result)
        {
            if (!block.HandleEmissiveStateChange)
            {
                result = default(MyEmissiveColorStateResult);
                return false;
            }

            return MyEmissiveColorPresets.LoadPresetState(block.BlockDefinition.EmissiveColorPreset, state, out result);
        }

        private static bool RenderIdBelongsTo(MyEntity entity, uint renderObjectId)
        {
            if (entity == null || renderObjectId == uint.MaxValue)
                return false;

            var renderIds = entity.Render?.RenderObjectIDs;
            return renderIds != null && renderIds.Contains(renderObjectId);
        }

        private static string DefaultEmissivePart(MyCubeBlock block, byte index)
        {
            try
            {
                var method = DefaultEmissivePartsMethods.GetOrAdd(block.GetType(), type =>
                    type.GetMethod("GetDefaultEmissiveParts", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) ??
                    typeof(MyCubeBlock).GetMethod("GetDefaultEmissiveParts", BindingFlags.Instance | BindingFlags.NonPublic));
                return method?.Invoke(block, new object[] { index }) as string;
            }
            catch
            {
                return index == 0 ? "Emissive" : index == 1 ? "Display" : null;
            }
        }
    }
}
