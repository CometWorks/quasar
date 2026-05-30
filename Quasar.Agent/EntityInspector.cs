using System;
using System.Collections.Generic;
using System.Linq;
using Magnetar.Protocol.Model;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace Quasar.Agent
{
    /// <summary>
    /// Maps live <see cref="MyEntity"/> instances to transport-friendly
    /// <see cref="EntitySummary"/> DTOs and applies the admin entity filter.
    /// Every member must be called on the game thread.
    /// </summary>
    internal static class EntityInspector
    {
        private const int DefaultLimit = 500;
        private const int MaxLimit = 2000;

        public static EntityListResult Query(EntityListFilter filter)
        {
            filter = filter ?? new EntityListFilter();

            var entities = MyEntities.GetEntities();
            var totalEntityCount = entities?.Count ?? 0;

            var matches = new List<EntitySummary>();
            if (entities != null)
            {
                foreach (var entity in entities)
                {
                    EntitySummary summary;
                    try
                    {
                        summary = TryMap(entity);
                    }
                    catch
                    {
                        summary = null;
                    }

                    if (summary == null)
                        continue;

                    if (!MatchesType(summary, filter.TypeTag))
                        continue;

                    if (!MatchesSearch(summary, filter.Search))
                        continue;

                    matches.Add(summary);
                }
            }

            // Largest first — the entities an admin most likely cares about float to the top.
            matches.Sort((left, right) => right.SizeMeters.CompareTo(left.SizeMeters));

            var offset = Math.Max(0, filter.Offset);
            var limit = filter.Limit <= 0 ? DefaultLimit : Math.Min(filter.Limit, MaxLimit);
            var page = matches.Skip(offset).Take(limit).ToList();

            return new EntityListResult
            {
                Entities = page,
                TotalCount = matches.Count,
                TotalEntityCount = totalEntityCount,
                CapturedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        public static bool TryDelete(long entityId, out string message)
        {
            if (!MyEntities.TryGetEntityById(entityId, out var entity) || entity == null)
            {
                message = $"Entity {entityId} no longer exists.";
                return false;
            }

            var label = Describe(entity);
            entity.Close();
            message = $"Deleted {label}.";
            return true;
        }

        private static EntitySummary TryMap(MyEntity entity)
        {
            if (entity == null)
                return null;

            switch (entity)
            {
                case MyCubeGrid grid:
                    return MapGrid(grid);
                case MyCharacter character:
                    return MapCharacter(character);
                case MyFloatingObject floating:
                    return MapFloating(floating);
                case MyVoxelBase voxel:
                    return MapVoxel(voxel);
                default:
                    // Skip child/attached entities (subparts, seated occupants, …) so the
                    // catch-all bucket only ever lists standalone world roots.
                    return entity.Parent != null ? null : MapOther(entity);
            }
        }

        private static EntitySummary MapGrid(MyCubeGrid grid)
        {
            var summary = NewSummary(grid, "Grid");
            summary.DisplayName = FirstNonEmpty(grid.DisplayName, $"Grid {grid.EntityId}");
            summary.BlockCount = grid.BlocksCount;
            summary.Pcu = grid.BlocksPCU;

            var size = grid.GridSizeEnum == MyCubeSize.Large ? "Large" : "Small";
            summary.SubType = size + (grid.IsStatic ? "Static" : "Ship");

            var owners = grid.BigOwners;
            if (owners != null && owners.Count > 0)
                ResolveOwner(owners[0], summary);

            return summary;
        }

        private static EntitySummary MapCharacter(MyCharacter character)
        {
            var summary = NewSummary(character, "Character");
            summary.DisplayName = FirstNonEmpty(character.DisplayName, $"Character {character.EntityId}");

            string subType = "Character";
            try
            {
                if (character.IsDead)
                    subType = "Corpse";
                else if (character.IsBot)
                    subType = "Bot";
                else
                    subType = "Player";
            }
            catch
            {
            }

            summary.SubType = subType;

            try
            {
                var identityId = character.GetPlayerIdentityId();
                if (identityId != 0)
                    ResolveOwner(identityId, summary);
            }
            catch
            {
            }

            return summary;
        }

        private static EntitySummary MapFloating(MyFloatingObject floating)
        {
            var summary = NewSummary(floating, "Float");

            var itemName = "Item";
            try
            {
                var subtype = floating.Item.Content?.SubtypeName;
                if (!string.IsNullOrEmpty(subtype))
                    itemName = subtype;
            }
            catch
            {
            }

            summary.DisplayName = itemName;

            try
            {
                var amount = (float)floating.Amount.Value;
                summary.SubType = "x" + amount.ToString("0.##");
            }
            catch
            {
                summary.SubType = "Item";
            }

            return summary;
        }

        private static EntitySummary MapVoxel(MyVoxelBase voxel)
        {
            var summary = NewSummary(voxel, "Voxel");
            summary.DisplayName = FirstNonEmpty(voxel.StorageName, $"Voxel {voxel.EntityId}");
            summary.SubType = voxel.GetType().Name.IndexOf("Planet", StringComparison.OrdinalIgnoreCase) >= 0
                ? "Planet"
                : "Asteroid";
            return summary;
        }

        private static EntitySummary MapOther(MyEntity entity)
        {
            var summary = NewSummary(entity, "Other");
            var typeName = entity.GetType().Name;
            summary.DisplayName = FirstNonEmpty(entity.DisplayName, typeName);
            summary.SubType = typeName;
            return summary;
        }

        private static EntitySummary NewSummary(MyEntity entity, string typeTag)
        {
            var summary = new EntitySummary
            {
                EntityId = entity.EntityId,
                TypeTag = typeTag,
            };

            try
            {
                var position = entity.PositionComp.GetPosition();
                summary.PositionX = position.X;
                summary.PositionY = position.Y;
                summary.PositionZ = position.Z;

                var aabb = entity.PositionComp.WorldAABB;
                summary.AabbMinX = aabb.Min.X;
                summary.AabbMinY = aabb.Min.Y;
                summary.AabbMinZ = aabb.Min.Z;
                summary.AabbMaxX = aabb.Max.X;
                summary.AabbMaxY = aabb.Max.Y;
                summary.AabbMaxZ = aabb.Max.Z;

                var sizeVector = aabb.Max - aabb.Min;
                summary.SizeMeters = Math.Max(sizeVector.X, Math.Max(sizeVector.Y, sizeVector.Z));

                // WorldMatrixRef is the non-obsolete accessor; captured so a future
                // renderer can draw oriented bounding boxes, not just axis-aligned.
                MatrixD world = entity.PositionComp.WorldMatrixRef;
                summary.WorldMatrixM11 = world.M11;
                summary.WorldMatrixM12 = world.M12;
                summary.WorldMatrixM13 = world.M13;
                summary.WorldMatrixM14 = world.M14;
                summary.WorldMatrixM21 = world.M21;
                summary.WorldMatrixM22 = world.M22;
                summary.WorldMatrixM23 = world.M23;
                summary.WorldMatrixM24 = world.M24;
                summary.WorldMatrixM31 = world.M31;
                summary.WorldMatrixM32 = world.M32;
                summary.WorldMatrixM33 = world.M33;
                summary.WorldMatrixM34 = world.M34;
                summary.WorldMatrixM41 = world.M41;
                summary.WorldMatrixM42 = world.M42;
                summary.WorldMatrixM43 = world.M43;
                summary.WorldMatrixM44 = world.M44;
            }
            catch
            {
            }

            return summary;
        }

        private static void ResolveOwner(long identityId, EntitySummary summary)
        {
            var players = MySession.Static?.Players;
            if (players == null || identityId == 0)
                return;

            var identity = players.TryGetIdentity(identityId);
            if (identity != null && !string.IsNullOrEmpty(identity.DisplayName))
                summary.OwnerName = identity.DisplayName;

            if (players.TryGetPlayerId(identityId, out var playerId) && playerId.SteamId != 0)
                summary.OwnerSteamId = playerId.SteamId;
        }

        private static bool MatchesType(EntitySummary summary, string typeTag)
        {
            if (string.IsNullOrWhiteSpace(typeTag) ||
                string.Equals(typeTag, "All", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(summary.TypeTag, typeTag, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesSearch(EntitySummary summary, string search)
        {
            search = search?.Trim();
            if (string.IsNullOrEmpty(search))
                return true;

            return (summary.DisplayName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                   || (summary.OwnerName?.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                   || summary.EntityId.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string Describe(MyEntity entity)
        {
            var name = FirstNonEmpty(entity.DisplayName, entity.GetType().Name);
            return $"{name} (#{entity.EntityId})";
        }

        private static string FirstNonEmpty(string value, string fallback)
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }
    }
}
