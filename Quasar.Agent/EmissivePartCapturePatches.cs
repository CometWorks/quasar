using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using VRage.Game.Entity;
using VRage.Game.Graphics;
using VRage.Utils;
using VRageMath;
using VRageRender;

namespace Quasar.Agent
{
    internal static class EmissivePartCapturePatches
    {
        private static readonly ConcurrentDictionary<Type, MethodInfo> DefaultEmissivePartsMethods = new ConcurrentDictionary<Type, MethodInfo>();
        private static readonly ConcurrentDictionary<Type, string[]> EmissiveTextureNames = new ConcurrentDictionary<Type, string[]>();
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
                count += Patch(AccessTools.Method(typeof(MyRenderProxy), "UpdateModelProperties", new[] { typeof(uint), typeof(string), typeof(RenderFlags), typeof(RenderFlags), typeof(Color?), typeof(float?) }), nameof(MyRenderProxyUpdateModelPropertiesPrefix)) ? 1 : 0;
                count += PatchSegmentedSetEmissive("Sandbox.Game.Entities.MyBatteryBlock", nameof(BatterySetEmissivePrefix)) ? 1 : 0;
                count += Patch(AccessTools.Method(AccessTools.TypeByName("Sandbox.Game.Entities.MyBatteryBlock"), "UpdateEmissivity"), nameof(BatteryUpdateEmissivityPostfix), postfix: true) ? 1 : 0;
                count += PatchSegmentedSetEmissive("Sandbox.Game.Entities.Blocks.MyGasTank", nameof(GasTankSetEmissivePrefix)) ? 1 : 0;
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
                EmissiveTextureNames.Clear();
            }
        }

        private static bool Patch(MethodBase method, string patchMethodName, bool postfix = false)
        {
            if (method == null || _harmony == null)
                return false;

            try
            {
                var patch = new HarmonyMethod(typeof(EmissivePartCapturePatches).GetMethod(patchMethodName, BindingFlags.Static | BindingFlags.NonPublic));
                if (postfix)
                    _harmony.Patch(method, postfix: patch);
                else
                    _harmony.Patch(method, patch);
                return true;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Quasar emissive part capture patch skipped: {method.DeclaringType?.FullName}.{method.Name}: {exception.Message}");
                return false;
            }
        }

        private static bool PatchSegmentedSetEmissive(string typeName, string prefixMethodName)
        {
            var type = AccessTools.TypeByName(typeName);
            return Patch(AccessTools.Method(type, "SetEmissive", new[] { typeof(Color), typeof(float) }), prefixMethodName);
        }

        private static void MyEntitySetEmissivePartsPrefix(MyEntity __instance, string emissiveName, Color emissivePartColor, float emissivity)
        {
            if (__instance is MyCubeBlock block && !IsLightingBlock(block))
            {
                EmissivePartCaptureCache.RegisterRenderObjectIds(__instance);
                EmissivePartCaptureCache.Record(__instance, emissiveName, emissivePartColor, emissivity, "entity");
            }
        }

        private static void MyEntityUpdateNamedEmissivePartsPrefix(uint renderObjectId, string emissiveName, Color emissivePartColor, float emissivity)
        {
            EmissivePartCaptureCache.RecordByRenderObjectId(renderObjectId, emissiveName, emissivePartColor, emissivity, "renderObject");
        }

        private static void MyRenderProxyUpdateModelPropertiesPrefix(uint id, string materialName, Color? diffuseColor, float? emissivity)
        {
            if (!diffuseColor.HasValue || !emissivity.HasValue)
                return;

            EmissivePartCaptureCache.RecordByRenderObjectId(id, materialName, diffuseColor.Value, emissivity.Value, "renderModelProperties");
        }

        private static void MyCubeBlockSetEmissiveStatePrefix(MyCubeBlock __instance, MyStringHash state, uint renderObjectId, string namedPart)
        {
            if (__instance == null || IsLightingBlock(__instance) || __instance.BlockDefinition == null || !RenderIdBelongsTo(__instance, renderObjectId))
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
            if (__instance == null || IsLightingBlock(__instance) || !RenderIdBelongsTo(__instance, renderObjectId))
                return;

            EmissivePartCaptureCache.RegisterRenderObjectIds(__instance);
            EmissivePartCaptureCache.Record(__instance, "Emissive", emissivePartColor, emissivity, "cubeBlockParts");
            EmissivePartCaptureCache.Record(__instance, "Display", displayPartColor, emissivity, "cubeBlockParts");
        }

        private static void BatterySetEmissivePrefix(MyEntity __instance, Color color, float fill)
        {
            CaptureSegmentedEmissive(__instance, color, fill, offEmissivity: 0f, source: "batterySegments");
        }

        private static void GasTankSetEmissivePrefix(MyEntity __instance, Color color, float fill)
        {
            CaptureSegmentedEmissive(__instance, color, fill, offEmissivity: 1f, source: "gasTankSegments");
        }

        private static void BatteryUpdateEmissivityPostfix(MyCubeBlock __instance)
        {
            if (__instance?.BlockDefinition == null || __instance.BlockDefinition.Id.SubtypeName != "SmallBlockSmallBatteryBlock")
                return;

            var materialName = EmissiveTextureNamesFor(__instance.GetType()).FirstOrDefault();
            if (string.IsNullOrEmpty(materialName))
                return;

            EmissivePartCaptureCache.RegisterRenderObjectIds(__instance);
            EmissivePartCaptureCache.Record(__instance, materialName, BatteryStatusColor(__instance), 1f, "batterySmall");
        }

        private static void CaptureSegmentedEmissive(MyEntity entity, Color color, float fill, float offEmissivity, string source)
        {
            if (entity == null)
                return;

            EmissivePartCaptureCache.RegisterRenderObjectIds(entity);
            var names = EmissiveTextureNamesFor(entity.GetType());
            var fillCount = (int)(fill * names.Length);
            for (var i = 0; i < names.Length; i++)
                EmissivePartCaptureCache.Record(entity, names[i], i < fillCount ? color : Color.Black, i < fillCount ? 1f : offEmissivity, source);
        }

        private static Color BatteryStatusColor(MyCubeBlock block)
        {
            var state = MyCubeBlock.m_emissiveNames.Damaged;
            var fallback = Color.Red;
            var functional = block as MyFunctionalBlock;
            if (block.IsFunctional && (functional?.Enabled ?? true))
            {
                if (block.IsWorking)
                {
                    var chargeMode = block.GetType().GetProperty("ChargeMode", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(block, null)?.ToString() ?? string.Empty;
                    if (string.Equals(chargeMode, "Discharge", StringComparison.OrdinalIgnoreCase))
                    {
                        state = MyCubeBlock.m_emissiveNames.Alternative;
                        fallback = Color.SteelBlue;
                    }
                    else if (string.Equals(chargeMode, "Recharge", StringComparison.OrdinalIgnoreCase))
                    {
                        state = MyCubeBlock.m_emissiveNames.Warning;
                        fallback = Color.Yellow;
                    }
                    else
                    {
                        state = MyCubeBlock.m_emissiveNames.Working;
                        fallback = Color.Green;
                    }
                }
                else
                {
                    state = MyCubeBlock.m_emissiveNames.Disabled;
                }
            }
            else if (block.IsFunctional)
            {
                state = MyCubeBlock.m_emissiveNames.Disabled;
            }

            return MyEmissiveColorPresets.LoadPresetState(block.BlockDefinition.EmissiveColorPreset, state, out var result)
                ? result.EmissiveColor
                : fallback;
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

        private static bool IsLightingBlock(MyCubeBlock block)
        {
            return block is MyLightingBlock;
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

        private static string[] EmissiveTextureNamesFor(Type type)
        {
            return EmissiveTextureNames.GetOrAdd(type, current =>
            {
                var field = current.GetField("m_emissiveTextureNames", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(null) as string[] ?? new[] { "Emissive0", "Emissive1", "Emissive2", "Emissive3" };
            });
        }
    }
}
