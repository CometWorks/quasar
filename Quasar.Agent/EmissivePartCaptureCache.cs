using System;
using System.Collections.Generic;
using System.Linq;
using VRage.Game.Entity;
using VRageMath;

namespace Quasar.Agent
{
    internal sealed class CapturedEmissivePart
    {
        public long EntityId { get; set; }

        public string MaterialName { get; set; } = string.Empty;

        public Color Color { get; set; }

        public float Emissivity { get; set; }

        public DateTimeOffset UpdatedAtUtc { get; set; }

        public string Source { get; set; } = string.Empty;
    }

    internal static class EmissivePartCaptureCache
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CapturedEmissivePart> Parts = new Dictionary<string, CapturedEmissivePart>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<uint, long> RenderObjectEntityIds = new Dictionary<uint, long>();

        public static void Record(MyEntity entity, string materialName, Color color, float emissivity, string source)
        {
            if (entity == null)
                return;

            Record(entity.EntityId, materialName, color, emissivity, source);
        }

        public static void RecordByRenderObjectId(uint renderObjectId, string materialName, Color color, float emissivity, string source)
        {
            if (renderObjectId == uint.MaxValue)
                return;

            long entityId;
            lock (Gate)
            {
                if (!RenderObjectEntityIds.TryGetValue(renderObjectId, out entityId))
                    return;
            }

            Record(entityId, materialName, color, emissivity, source);
        }

        public static void RegisterRenderObjectIds(MyEntity entity)
        {
            var renderIds = entity?.Render?.RenderObjectIDs;
            if (entity == null || renderIds == null || renderIds.Length == 0)
                return;

            lock (Gate)
            {
                foreach (var renderId in renderIds)
                {
                    if (renderId != uint.MaxValue)
                        RenderObjectEntityIds[renderId] = entity.EntityId;
                }
            }
        }

        public static IReadOnlyList<CapturedEmissivePart> ForEntity(MyEntity entity)
        {
            if (entity == null)
                return Array.Empty<CapturedEmissivePart>();

            return ForEntityId(entity.EntityId);
        }

        public static IReadOnlyList<CapturedEmissivePart> ForEntityId(long entityId)
        {
            lock (Gate)
            {
                return Parts.Values
                    .Where(part => part.EntityId == entityId)
                    .OrderBy(part => part.MaterialName, StringComparer.OrdinalIgnoreCase)
                    .Select(Clone)
                    .ToList();
            }
        }

        public static void RemoveEntity(long entityId)
        {
            lock (Gate)
            {
                foreach (var key in Parts.Where(pair => pair.Value.EntityId == entityId).Select(pair => pair.Key).ToList())
                    Parts.Remove(key);

                foreach (var key in RenderObjectEntityIds.Where(pair => pair.Value == entityId).Select(pair => pair.Key).ToList())
                    RenderObjectEntityIds.Remove(key);
            }
        }

        private static void Record(long entityId, string materialName, Color color, float emissivity, string source)
        {
            materialName = materialName?.Trim() ?? string.Empty;
            if (entityId == 0 || !IsStatusMaterialName(materialName))
                return;

            if (float.IsNaN(emissivity) || float.IsInfinity(emissivity))
                emissivity = 0f;

            var key = entityId.ToString() + "\0" + materialName;
            lock (Gate)
            {
                Parts[key] = new CapturedEmissivePart
                {
                    EntityId = entityId,
                    MaterialName = materialName,
                    Color = color,
                    Emissivity = emissivity,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    Source = source ?? string.Empty,
                };
            }
        }

        private static bool IsStatusMaterialName(string materialName)
        {
            if (string.IsNullOrWhiteSpace(materialName))
                return false;

            return !string.Equals(materialName, "EmissiveColorable", StringComparison.OrdinalIgnoreCase);
        }

        private static CapturedEmissivePart Clone(CapturedEmissivePart part)
        {
            return new CapturedEmissivePart
            {
                EntityId = part.EntityId,
                MaterialName = part.MaterialName,
                Color = part.Color,
                Emissivity = part.Emissivity,
                UpdatedAtUtc = part.UpdatedAtUtc,
                Source = part.Source,
            };
        }
    }
}
