using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Magnetar.Protocol.Model;
using Sandbox.Common.ObjectBuilders.Definitions;
using Sandbox.Definitions;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Cube;
using Sandbox.Game.EntityComponents;
using Sandbox.Game.World;
using SpaceEngineers.Game.EntityComponents.Blocks;
using VRage.Entities.Components;
using VRage.Game;
using VRage.Game.Entity;
using VRage.Game.GUI.TextPanel;
using VRage.Utils;
using VRage.Voxels;
using VRageMath;

namespace Quasar.Agent
{
    /// <summary>
    /// Builds metadata-only grid scene snapshots for Quasar's browser viewer.
    /// Every member must be called on the game thread.
    /// </summary>
    internal static class GridRenderSceneInspector
    {
        private const int ChunkSizeCells = 16;
        private const int DefaultVoxelChunkSize = 32;
        private const int MaxVoxelSceneChunks = 96;
        private const int MaxVoxelDataBytes = 8 * 1024 * 1024;
        private const int VoxelDataPadding = 1;
        private const int VoxelLod = 0;

        public static EntityRenderScene Build(long entityId, string gameVersion, string pluginVersion, bool includeVoxels = false)
        {
            if (!MyEntities.TryGetEntityById<MyCubeGrid>(entityId, out var grid) || grid == null || grid.MarkedForClose || grid.Closed)
                throw new InvalidOperationException("Grid not found or not loaded on this server.");

            var catalog = new MetadataAssetCatalog();
            var scene = new EntityRenderScene
            {
                GameVersion = gameVersion ?? string.Empty,
                PluginVersion = pluginVersion ?? string.Empty,
                Grid = ToGrid(grid),
                Environment = ToEnvironment(),
                CapturedAtUtc = DateTimeOffset.UtcNow,
            };

            var definitions = new Dictionary<string, ViewerBlockDefinition>(StringComparer.Ordinal);
            var chunks = new Dictionary<string, ChunkBuilder>(StringComparer.Ordinal);

            foreach (var block in grid.CubeBlocks)
            {
                if (block == null || block.BlockDefinition == null)
                    continue;

                var definitionId = DefinitionId(block.BlockDefinition);
                if (!definitions.ContainsKey(definitionId))
                    definitions[definitionId] = ToBlockDefinition(block.BlockDefinition, catalog, scene.Warnings);

                var chunkCoord = ChunkCoordinate(block.Position, ChunkSizeCells);
                var chunkId = ChunkId(chunkCoord);
                if (!chunks.TryGetValue(chunkId, out var chunk))
                {
                    chunk = new ChunkBuilder(chunkId);
                    chunks.Add(chunkId, chunk);
                }

                chunk.Include(block.Min, block.Max);
                var blockInstance = ToBlockInstance(grid, block, definitionId, chunkId, catalog, scene.Warnings);
                scene.BlockInstances.Add(blockInstance);
                AddLightSources(grid, block, scene.LightSources, scene.Warnings);
                AddSubpartLightSources(blockInstance, scene.LightSources);
            }

            scene.BlockDefinitions = definitions.Values.OrderBy(definition => definition.Id, StringComparer.Ordinal).ToList();
            scene.Chunks = chunks.Values.Select(chunk => chunk.ToDto(grid.GridSize)).OrderBy(chunk => chunk.Id, StringComparer.Ordinal).ToList();
            scene.ModelAssets = catalog.ModelAssetsSnapshot();
            scene.TextureAssets = catalog.TextureAssetsSnapshot();
            scene.Voxels = LoadedVoxels();
            if (includeVoxels)
            {
                scene.VoxelDeformations = BuildVoxelDeformations(grid, scene.Warnings);
                scene.VoxelMaterials = BuildVoxelMaterials(scene.VoxelDeformations, scene.Warnings);
            }
            else
                scene.Warnings.Add("Voxel data generation disabled by URL.");
            return scene;
        }

        private static ViewerSceneEnvironment ToEnvironment()
        {
            var direction = MySector.DirectionToSunNormalized;
            if (!direction.IsValid() || direction.LengthSquared() < 0.0001f)
                direction = new Vector3(0.33946735f, 0.70979536f, -0.61721337f);
            direction.Normalize();

            var intensity = MySector.SunProperties.SunIntensity;
            if (!float.IsNaN(intensity) && !float.IsInfinity(intensity) && intensity > 0)
                intensity = Math.Min(3f, intensity);
            else
                intensity = 1.9f;

            return new ViewerSceneEnvironment
            {
                SunDirection = ToDto(direction),
                SunIntensity = intensity,
            };
        }

        private static void AddLightSources(MyCubeGrid grid, MySlimBlock block, List<ViewerLightSource> lightSources, List<string> warnings)
        {
            var fatBlock = block.FatBlock;
            if (fatBlock == null || fatBlock.MarkedForClose || fatBlock.Closed || fatBlock.IsPreview || grid.IsPreview)
                return;

            if (block.BuildLevelRatio <= 0.01f)
                return;

            try
            {
                var lightingBlock = fatBlock as MyLightingBlock;
                if (lightingBlock != null)
                {
                    AddLightingBlockSource(grid, block, lightingBlock, lightSources);
                    return;
                }

                MyLightingComponent lightingComponent;
                if (fatBlock.Components != null && fatBlock.Components.TryGet(out lightingComponent))
                    AddLightingComponentSource(grid, block, lightingComponent, lightSources);
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to inspect light source for block " + block.Position + ": " + exception.Message);
            }
        }

        private static void AddLightingBlockSource(MyCubeGrid grid, MySlimBlock block, MyLightingBlock lightingBlock, List<ViewerLightSource> lightSources)
        {
            var definition = block.BlockDefinition as MyLightingBlockDefinition;
            var isReflector = lightingBlock is MyReflectorLight;
            var enabled = lightingBlock.IsWorking && lightingBlock.Intensity > 0f;
            var blockId = lightingBlock.EntityId.ToString();
            var radius = ValidLightValue(lightingBlock.Radius, 0f);
            var reflectorRadius = ValidLightValue(lightingBlock.ReflectorRadius, radius);
            var falloff = ValidLightValue(lightingBlock.Falloff, 1f);
            var coneDegrees = definition != null && definition.ReflectorConeDegrees > 0f ? definition.ReflectorConeDegrees : 52f;
            var offset = definition != null ? definition.LightOffset.Default : 0f;
            var position = LightPosition(block, offset);
            var direction = LightDirection(block);
            var up = LightUp(block);

            if (isReflector)
            {
                var spotIntensity = ViewerLightIntensity(lightingBlock.Intensity, 8f);
                if (reflectorRadius > 0f || spotIntensity > 0f)
                {
                    lightSources.Add(CreateLightSource(
                        blockId + ":spot", blockId, "spot", position, direction, up, lightingBlock.Color,
                        radius, reflectorRadius, spotIntensity, falloff, coneDegrees,
                        enabled && reflectorRadius > 0f && spotIntensity > 0f,
                        lightingBlock.BlinkIntervalSeconds, lightingBlock.BlinkLength, lightingBlock.BlinkOffset));
                }

                var companionIntensity = ViewerLightIntensity(lightingBlock.Intensity, 0.3f);
                if (radius > 0f && companionIntensity > 0f)
                {
                    lightSources.Add(CreateLightSource(
                        blockId + ":point", blockId, "point", position, direction, up, lightingBlock.Color,
                        radius, reflectorRadius, companionIntensity, falloff, coneDegrees,
                        enabled && radius > 0f && companionIntensity > 0f,
                        lightingBlock.BlinkIntervalSeconds, lightingBlock.BlinkLength, lightingBlock.BlinkOffset));
                }

                return;
            }

            var intensity = ViewerLightIntensity(lightingBlock.Intensity, 2f);
            if (radius <= 0f && intensity <= 0f)
                return;

            lightSources.Add(CreateLightSource(
                blockId + ":point", blockId, "point", position, direction, up, lightingBlock.Color,
                radius, reflectorRadius, intensity, falloff, coneDegrees,
                enabled && radius > 0f && intensity > 0f,
                lightingBlock.BlinkIntervalSeconds, lightingBlock.BlinkLength, lightingBlock.BlinkOffset));
        }

        private static void AddLightingComponentSource(MyCubeGrid grid, MySlimBlock block, MyLightingComponent lightingComponent, List<ViewerLightSource> lightSources)
        {
            var functionalBlock = block.FatBlock as MyFunctionalBlock;
            var enabledByBlock = functionalBlock == null || functionalBlock.IsWorking;
            var radius = ValidLightValue(lightingComponent.Radius, 0f);
            var intensity = ViewerLightIntensity(lightingComponent.Intensity, 2f);
            if (radius <= 0f && intensity <= 0f)
                return;

            var blockId = block.FatBlock.EntityId.ToString();
            var position = LightPosition(block, lightingComponent.Offset);
            var direction = LightDirection(block);
            var up = LightUp(block);
            lightSources.Add(CreateLightSource(
                blockId + ":component-light", blockId, "point", position, direction, up, lightingComponent.Color,
                radius, ValidLightValue(lightingComponent.ReflectorRadius, radius), intensity,
                ValidLightValue(lightingComponent.Falloff, 1f), 52f,
                enabledByBlock && radius > 0f && intensity > 0f,
                lightingComponent.BlinkIntervalSeconds, lightingComponent.BlinkLength, lightingComponent.BlinkOffset));
        }

        private static ViewerLightSource CreateLightSource(
            string id,
            string blockId,
            string kind,
            Vector3 position,
            Vector3 direction,
            Vector3 up,
            Color color,
            float radius,
            float reflectorRadius,
            float intensity,
            float falloff,
            float coneDegrees,
            bool enabled,
            float blinkIntervalSeconds,
            float blinkLength,
            float blinkOffset)
        {
            return new ViewerLightSource
            {
                Id = id,
                BlockId = blockId,
                Kind = kind,
                Position = ToDto(position),
                Direction = ToDto(direction),
                Up = ToDto(up),
                Color = ToDto(color),
                Radius = radius,
                ReflectorRadius = reflectorRadius,
                Intensity = intensity,
                Falloff = falloff,
                ConeDegrees = coneDegrees,
                Enabled = enabled,
                BlinkIntervalSeconds = ValidLightValue(blinkIntervalSeconds, 0f),
                BlinkLength = ValidLightValue(blinkLength, 0f),
                BlinkOffset = ValidLightValue(blinkOffset, 0f),
            };
        }

        private static void AddSubpartLightSources(ViewerBlockInstance blockInstance, List<ViewerLightSource> lightSources)
        {
            foreach (var subpart in blockInstance.Subparts)
            {
                foreach (var lightSource in subpart.LightSources)
                    lightSources.Add(lightSource);
            }
        }

        private static Vector3 LightPosition(MySlimBlock block, float offset)
        {
            block.GetLocalMatrix(out var localMatrix);
            if (offset.IsValid() && Math.Abs(offset) > 0.0001f)
                return localMatrix.Translation + localMatrix.Forward * offset;

            return localMatrix.Translation;
        }

        private static Vector3 LightDirection(MySlimBlock block)
        {
            block.GetLocalMatrix(out var localMatrix);
            var direction = localMatrix.Forward;
            if (!direction.IsValid() || direction.LengthSquared() < 0.0001f)
                direction = Vector3.Forward;
            direction.Normalize();
            return direction;
        }

        private static Vector3 LightUp(MySlimBlock block)
        {
            block.GetLocalMatrix(out var localMatrix);
            var up = localMatrix.Up;
            if (!up.IsValid() || up.LengthSquared() < 0.0001f)
                up = Vector3.Up;
            up.Normalize();
            return up;
        }

        private static float ViewerLightIntensity(float intensity, float gameScale)
        {
            if (!intensity.IsValid() || !gameScale.IsValid())
                return 0f;

            return Math.Min(80f, Math.Max(0f, intensity * gameScale));
        }

        private static float ValidLightValue(float value, float fallback)
        {
            return value.IsValid() ? Math.Max(0f, value) : fallback;
        }

        private static ViewerGrid ToGrid(MyCubeGrid grid)
        {
            return new ViewerGrid
            {
                Id = grid.EntityId.ToString(),
                DisplayName = FirstNonEmpty(grid.DisplayName, grid.Name, $"Grid {grid.EntityId}"),
                GridSize = grid.GridSize,
                GridSpace = grid.GridSizeEnum.ToString().ToLowerInvariant(),
                IsStatic = grid.IsStatic,
                WorldMatrix = ToDto(grid.WorldMatrix),
                BlockCount = grid.BlocksCount,
                Bounds = ToDto(grid.PositionComp.WorldAABB),
            };
        }

        private static List<ViewerVoxelBody> LoadedVoxels()
        {
            var session = MySession.Static;
            if (session?.VoxelMaps?.Instances == null)
                return new List<ViewerVoxelBody>();

            var voxels = new List<ViewerVoxelBody>();
            foreach (var voxel in session.VoxelMaps.Instances)
            {
                if (voxel == null || voxel.MarkedForClose || voxel.Closed)
                    continue;

                var kind = VoxelKind(voxel);
                if (kind == "voxelPhysics")
                    continue;

                try
                {
                    voxels.Add(ToVoxelBody(voxel, kind));
                }
                catch
                {
                    // Voxel metadata is optional for the grid viewer; skip bodies that are mid-close.
                }
            }

            return voxels.OrderBy(voxel => voxel.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static ViewerVoxelBody ToVoxelBody(MyVoxelBase voxel, string kind)
        {
            var storageSize = voxel.Storage != null ? voxel.Storage.Size : voxel.Size;
            var dto = new ViewerVoxelBody
            {
                Id = voxel.EntityId.ToString(),
                Kind = kind,
                DisplayName = FirstNonEmpty(voxel.DisplayName, voxel.Name, kind + " " + voxel.EntityId),
                WorldMatrix = ToDto(voxel.WorldMatrix),
                PositionLeftBottomCorner = ToDto(voxel.PositionLeftBottomCorner),
                StorageMin = ToDto(voxel.StorageMin),
                StorageMax = ToDto(voxel.StorageMax),
                StorageSize = ToDto(storageSize),
                SizeInMetres = ToDto(voxel.SizeInMetres),
                WorldAabb = ToDto(voxel.PositionComp.WorldAABB),
                ContentChanged = voxel.ContentChanged,
            };

            if (voxel is MyPlanet planet)
            {
                dto.Planet = new ViewerPlanetInfo
                {
                    MinimumRadius = planet.MinimumRadius,
                    AverageRadius = planet.AverageRadius,
                    MaximumRadius = planet.MaximumRadius,
                    AtmosphereRadius = planet.AtmosphereRadius,
                    HasAtmosphere = planet.HasAtmosphere,
                    SpherizeWithDistance = planet.SpherizeWithDistance,
                };
            }

            return dto;
        }

        private static List<ViewerVoxelMaterial> BuildVoxelMaterials(List<ViewerVoxelDataChunk> chunks, List<string> warnings)
        {
            var materialIndexes = new HashSet<byte>();
            foreach (var chunk in chunks)
            {
                if (chunk?.Materials == null)
                    continue;

                foreach (var material in chunk.Materials)
                    materialIndexes.Add(material);
            }

            var result = new List<ViewerVoxelMaterial>();
            foreach (var materialIndex in materialIndexes.OrderBy(index => index))
            {
                try
                {
                    var definition = MyDefinitionManager.Static.GetVoxelMaterialDefinition(materialIndex);
                    if (definition == null)
                        continue;

                    var textureSet = definition.RenderParams.TextureSets != null && definition.RenderParams.TextureSets.Length > 0
                        ? definition.RenderParams.TextureSets[0]
                        : default;
                    result.Add(new ViewerVoxelMaterial
                    {
                        Index = definition.Index,
                        SubtypeId = definition.Id.SubtypeName ?? string.Empty,
                        MaterialTypeName = definition.MaterialTypeName ?? string.Empty,
                        ColorMetalXZnY = textureSet.ColorMetalXZnY ?? string.Empty,
                        ColorMetalY = textureSet.ColorMetalY ?? string.Empty,
                        NormalGlossXZnY = textureSet.NormalGlossXZnY ?? string.Empty,
                        NormalGlossY = textureSet.NormalGlossY ?? string.Empty,
                        ExtXZnY = textureSet.ExtXZnY ?? string.Empty,
                        ExtY = textureSet.ExtY ?? string.Empty,
                        TilingScale = definition.RenderParams.StandardTilingSetup.TilingScale,
                    });
                }
                catch (Exception exception)
                {
                    warnings.Add("Failed to inspect voxel material " + materialIndex + ": " + exception.Message);
                }
            }

            return result;
        }

        private static List<ViewerVoxelDataChunk> BuildVoxelDeformations(MyCubeGrid grid, List<string> warnings)
        {
            var session = MySession.Static;
            var result = new List<ViewerVoxelDataChunk>();
            if (session?.VoxelMaps?.Instances == null)
                return result;

            var gridAabb = grid.PositionComp.WorldAABB;
            var totalBytes = 0;
            var chunkBudgetReached = false;

            foreach (var voxel in session.VoxelMaps.Instances)
            {
                if (voxel == null || voxel.MarkedForClose || voxel.Closed || VoxelKind(voxel) == "voxelPhysics")
                    continue;

                try
                {
                    var voxelAabb = voxel.PositionComp.WorldAABB;
                    if (!voxelAabb.Intersects(gridAabb))
                        continue;

                    var intersection = Intersect(voxelAabb, gridAabb);
                    if (!TryWorldAabbToStorageRange(voxel, intersection, out var storageMin, out var storageMax))
                        continue;

                    var bodyName = FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.EntityId.ToString());
                    var bodyContentState = ClassifyVoxelChunk(voxel, storageMin, storageMax);
                    if (bodyContentState == "empty")
                    {
                        warnings.Add("Skipped empty voxel data range for " + bodyName + ".");
                        continue;
                    }
                    if (bodyContentState == "full")
                    {
                        warnings.Add("Skipped full voxel data range for " + bodyName + ".");
                        continue;
                    }

                    var bodySampleCount = RangeSampleCount(storageMin, storageMax);
                    var bodyRequiredBytes = checked(bodySampleCount * 2);
                    if (bodyRequiredBytes <= MaxVoxelDataBytes - totalBytes)
                    {
                        var bodyChunk = TryBuildVoxelDataChunk(voxel, storageMin, storageMax, bodyContentState, warnings);
                        if (bodyChunk != null)
                        {
                            totalBytes += bodyChunk.Content.Length + bodyChunk.Materials.Length;
                            result.Add(bodyChunk);
                        }

                        continue;
                    }

                    var skippedEmpty = 0;
                    var skippedFull = 0;
                    foreach (var chunkRange in SplitStorageRange(storageMin, storageMax, DefaultVoxelChunkSize))
                    {
                        if (result.Count >= MaxVoxelSceneChunks)
                        {
                            chunkBudgetReached = true;
                            break;
                        }

                        var contentState = ClassifyVoxelChunk(voxel, chunkRange.Min, chunkRange.Max);
                        if (contentState == "empty")
                        {
                            skippedEmpty++;
                            continue;
                        }
                        if (contentState == "full")
                        {
                            skippedFull++;
                            continue;
                        }

                        var sampleCount = RangeSampleCount(chunkRange.Min, chunkRange.Max);
                        var requiredBytes = checked(sampleCount * 2);
                        if (totalBytes + requiredBytes > MaxVoxelDataBytes)
                        {
                            warnings.Add("Voxel data safety limit exceeded; remaining voxel chunks were skipped.");
                            return result;
                        }

                        var chunk = TryBuildVoxelDataChunk(voxel, chunkRange.Min, chunkRange.Max, contentState, warnings);
                        if (chunk == null)
                            continue;

                        totalBytes += chunk.Content.Length + chunk.Materials.Length;
                        result.Add(chunk);
                    }

                    if (skippedEmpty > 0 || skippedFull > 0)
                        warnings.Add("Skipped " + skippedEmpty + " empty and " + skippedFull + " full voxel data chunks for " + bodyName + ".");

                    if (chunkBudgetReached)
                        break;
                }
                catch (Exception exception)
                {
                    warnings.Add("Failed to sample voxel body " + FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.EntityId.ToString()) + ": " + exception.Message);
                }
            }

            if (chunkBudgetReached)
                warnings.Add("Voxel data chunk budget reached; remaining voxel chunks were skipped.");

            return result;
        }

        private static IEnumerable<StorageRange> SplitStorageRange(Vector3I min, Vector3I max, int chunkSize)
        {
            for (var z = min.Z; z <= max.Z; z += chunkSize)
            for (var y = min.Y; y <= max.Y; y += chunkSize)
            for (var x = min.X; x <= max.X; x += chunkSize)
            {
                yield return new StorageRange
                {
                    Min = new Vector3I(x, y, z),
                    Max = new Vector3I(
                        Math.Min(max.X, x + chunkSize),
                        Math.Min(max.Y, y + chunkSize),
                        Math.Min(max.Z, z + chunkSize)),
                };
            }
        }

        private static string ClassifyVoxelChunk(MyVoxelBase voxel, Vector3I min, Vector3I max)
        {
            var data = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
            data.Resize(min, max);
            var flags = MyVoxelRequestFlags.ConsiderContent;
            voxel.Storage.ReadRange(data, MyStorageDataTypeFlags.Content, VoxelLod, min, max, ref flags);

            var constitution = data.ComputeContentConstitution();
            if (constitution == MyVoxelContentConstitution.Empty) return "empty";
            if (constitution == MyVoxelContentConstitution.Full) return "full";
            return "mixed";
        }

        private static ViewerVoxelDataChunk TryBuildVoxelDataChunk(
            MyVoxelBase voxel,
            Vector3I min,
            Vector3I max,
            string contentState,
            List<string> warnings)
        {
            try
            {
                var data = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
                data.Resize(min, max);
                var flags = MyVoxelRequestFlags.ConsiderContent | MyVoxelRequestFlags.SurfaceMaterial;
                voxel.Storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, VoxelLod, min, max, ref flags);
                return CopyVoxelDataChunk(voxel, min, max, contentState, data);
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to sample voxel chunk " + ChunkId(min) + " for " + FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.EntityId.ToString()) + ": " + exception.Message);
                return null;
            }
        }

        private static ViewerVoxelDataChunk CopyVoxelDataChunk(MyVoxelBase voxel, Vector3I min, Vector3I max, string contentState, MyStorageData data)
        {
            var worldMatrix = VoxelWorldMatrix(voxel);
            var size = data.Size3D;
            return new ViewerVoxelDataChunk
            {
                VoxelBodyId = voxel.EntityId.ToString(),
                ChunkId = ChunkId(min),
                Lod = VoxelLod,
                StorageMin = ToDto(min),
                StorageMax = ToDto(max),
                WorldAabb = ToDto(StorageRangeWorldAabb(worldMatrix, min, max)),
                ContentState = contentState,
                Size = ToDto(size),
                Content = CopyStorageBytes(data, MyStorageDataTypeEnum.Content),
                Materials = CopyStorageBytes(data, MyStorageDataTypeEnum.Material),
            };
        }

        private static byte[] CopyStorageBytes(MyStorageData data, MyStorageDataTypeEnum type)
        {
            var source = data[type];
            var copy = new byte[data.Size3D.Size];
            Array.Copy(source, copy, copy.Length);
            return copy;
        }

        private static int RangeSampleCount(Vector3I min, Vector3I max)
        {
            return checked((max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1));
        }

        private static bool TryWorldAabbToStorageRange(MyVoxelBase voxel, BoundingBoxD worldAabb, out Vector3I storageMin, out Vector3I storageMax)
        {
            var inverse = MatrixD.Invert(VoxelWorldMatrix(voxel));
            var localMin = new Vector3D(double.PositiveInfinity);
            var localMax = new Vector3D(double.NegativeInfinity);
            foreach (var corner in WorldAabbCorners(worldAabb))
            {
                var local = Vector3D.Transform(corner, inverse);
                localMin = Vector3D.Min(localMin, local);
                localMax = Vector3D.Max(localMax, local);
            }

            storageMin = new Vector3I(
                (int)Math.Floor(localMin.X) - VoxelDataPadding,
                (int)Math.Floor(localMin.Y) - VoxelDataPadding,
                (int)Math.Floor(localMin.Z) - VoxelDataPadding);
            storageMax = new Vector3I(
                (int)Math.Ceiling(localMax.X) + VoxelDataPadding,
                (int)Math.Ceiling(localMax.Y) + VoxelDataPadding,
                (int)Math.Ceiling(localMax.Z) + VoxelDataPadding);
            storageMin = Clamp(storageMin, voxel.StorageMin, voxel.StorageMax);
            storageMax = Clamp(storageMax, voxel.StorageMin, voxel.StorageMax);
            return storageMin.X <= storageMax.X && storageMin.Y <= storageMax.Y && storageMin.Z <= storageMax.Z;
        }

        private static MatrixD VoxelWorldMatrix(MyVoxelBase voxel)
        {
            return MatrixD.CreateWorld(
                voxel.PositionLeftBottomCorner,
                voxel.Orientation.Forward,
                voxel.Orientation.Up);
        }

        private static BoundingBoxD StorageRangeWorldAabb(MatrixD worldMatrix, Vector3I min, Vector3I max)
        {
            var bounds = BoundingBoxD.CreateInvalid();
            var localMin = new Vector3D(min.X, min.Y, min.Z);
            var localMax = new Vector3D(max.X + 1, max.Y + 1, max.Z + 1);
            foreach (var corner in LocalAabbCorners(localMin, localMax))
                bounds.Include(Vector3D.Transform(corner, worldMatrix));
            return bounds;
        }

        private static BoundingBoxD Intersect(BoundingBoxD a, BoundingBoxD b)
        {
            return new BoundingBoxD(Vector3D.Max(a.Min, b.Min), Vector3D.Min(a.Max, b.Max));
        }

        private static IEnumerable<Vector3D> WorldAabbCorners(BoundingBoxD box)
        {
            return LocalAabbCorners(box.Min, box.Max);
        }

        private static IEnumerable<Vector3D> LocalAabbCorners(Vector3D min, Vector3D max)
        {
            yield return new Vector3D(min.X, min.Y, min.Z);
            yield return new Vector3D(max.X, min.Y, min.Z);
            yield return new Vector3D(min.X, max.Y, min.Z);
            yield return new Vector3D(max.X, max.Y, min.Z);
            yield return new Vector3D(min.X, min.Y, max.Z);
            yield return new Vector3D(max.X, min.Y, max.Z);
            yield return new Vector3D(min.X, max.Y, max.Z);
            yield return new Vector3D(max.X, max.Y, max.Z);
        }

        private static Vector3I Clamp(Vector3I value, Vector3I min, Vector3I max)
        {
            return new Vector3I(
                Math.Max(min.X, Math.Min(max.X, value.X)),
                Math.Max(min.Y, Math.Min(max.Y, value.Y)),
                Math.Max(min.Z, Math.Min(max.Z, value.Z)));
        }

        private static string VoxelKind(MyVoxelBase voxel)
        {
            var typeName = voxel.GetType().Name;
            if (typeName == "MyPlanet") return "planet";
            if (typeName == "MyVoxelMap") return "voxelMap";
            if (typeName == "MyVoxelPhysics") return "voxelPhysics";
            return "unknown";
        }

        private static ViewerBlockDefinition ToBlockDefinition(
            MyCubeBlockDefinition definition,
            MetadataAssetCatalog catalog,
            List<string> warnings)
        {
            var modelAssetId = catalog.RegisterModel(definition.Model);
            var dto = new ViewerBlockDefinition
            {
                Id = DefinitionId(definition),
                DisplayName = definition.DisplayNameText ?? string.Empty,
                GridSpace = definition.CubeSize.ToString().ToLowerInvariant(),
                Size = ToDto(definition.Size),
                ModelAssetId = modelAssetId ?? string.Empty,
                ModelOffset = ToDto(definition.ModelOffset),
                LocalAabbMin = ToDto(new Vector3(-definition.Size.X, -definition.Size.Y, -definition.Size.Z) * 0.5f),
                LocalAabbMax = ToDto(new Vector3(definition.Size.X, definition.Size.Y, definition.Size.Z) * 0.5f),
                VisibilityClass = VisibilityClass(definition),
                OpaqueFaceMask = OpaqueFaceMask(definition),
            };

            if (definition.BuildProgressModels != null)
            {
                foreach (var buildModel in definition.BuildProgressModels)
                {
                    var id = catalog.RegisterModel(buildModel.File);
                    if (!string.IsNullOrEmpty(id))
                        dto.BuildProgressModelAssetIds.Add(id);
                }
            }

            if (!string.IsNullOrEmpty(definition.Model) && string.IsNullOrEmpty(modelAssetId))
                warnings.Add("Block definition " + dto.Id + " has no model logical path.");

            return dto;
        }

        private static ViewerBlockInstance ToBlockInstance(
            MyCubeGrid grid,
            MySlimBlock block,
            string definitionId,
            string chunkId,
            MetadataAssetCatalog catalog,
            List<string> warnings)
        {
            block.GetLocalMatrix(out var localMatrix);
            var currentModelAssetId = string.Empty;
            try
            {
                var currentModel = block.CalculateCurrentModel(out var _);
                currentModelAssetId = catalog.RegisterModel(currentModel) ?? string.Empty;
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to resolve current model for block " + block.Position + ": " + exception.Message);
            }

            var dto = new ViewerBlockInstance
            {
                Id = block.FatBlock != null ? block.FatBlock.EntityId.ToString() : grid.EntityId + ":" + block.Min.X + "," + block.Min.Y + "," + block.Min.Z,
                GridId = grid.EntityId.ToString(),
                BlockTypeId = definitionId,
                ChunkId = chunkId,
                Cell = ToDto(block.Position),
                Min = ToDto(block.Min),
                Max = ToDto(block.Max),
                Translation = ToDto(localMatrix.Translation),
                Rotation = ToDto(localMatrix),
                Scale = new ViewerVector3 { X = 1f, Y = 1f, Z = 1f },
                OrientationForward = block.Orientation.Forward.ToString(),
                OrientationUp = block.Orientation.Up.ToString(),
                ColourMaskHsv = ToDto(block.ColorMaskHSV),
                SkinSubtypeId = block.SkinSubtypeId.String ?? string.Empty,
                BuildLevel = block.BuildLevelRatio,
                Integrity = block.Integrity,
                MaxIntegrity = block.MaxIntegrity,
                CurrentModelAssetId = currentModelAssetId,
            };

            AddGeneratedBlockModelParts(grid, block, dto, catalog, warnings);
            AddRuntimeSubparts(grid, block, dto, catalog, warnings);
            AddSkinTextureChanges(block, dto);
            AddLcdMaterialsToHideWhenOffline(block, dto);
            AddLcdSurfaces(block, dto, catalog, warnings);
            return dto;
        }

        private static void AddLcdMaterialsToHideWhenOffline(MySlimBlock block, ViewerBlockInstance dto)
        {
            var textPanelDefinition = block.BlockDefinition as MyTextPanelDefinition;
            if (textPanelDefinition?.MaterialNamesToHideWhenOffline == null)
                return;

            foreach (var materialName in textPanelDefinition.MaterialNamesToHideWhenOffline)
            {
                var name = materialName?.Trim();
                if (!string.IsNullOrEmpty(name) && !dto.LcdMaterialsToHideWhenOffline.Contains(name, StringComparer.OrdinalIgnoreCase))
                    dto.LcdMaterialsToHideWhenOffline.Add(name);
            }
        }

        private static void AddLcdSurfaces(
            MySlimBlock block,
            ViewerBlockInstance dto,
            MetadataAssetCatalog catalog,
            List<string> warnings)
        {
            var surfaces = TextSurfacesForBlock(block.FatBlock);
            if (surfaces.Count == 0)
                return;

            var screenAreas = ScreenAreasForBlock(block.BlockDefinition);
            var isWorking = (block.FatBlock as MyFunctionalBlock)?.IsWorking ?? true;
            for (var i = 0; i < surfaces.Count; i++)
            {
                try
                {
                    var surface = surfaces[i];
                    if (surface == null)
                        continue;

                    var surfaceDto = ToLcdSurface(i, surface, screenAreas, catalog, isWorking);
                    if (!string.IsNullOrEmpty(surfaceDto.MaterialName))
                        dto.LcdSurfaces.Add(surfaceDto);
                }
                catch (Exception exception)
                {
                    warnings.Add("Failed to inspect LCD surface " + i + " for block " + block.Position + ": " + exception.Message);
                }
            }
        }

        private static List<MyTextPanelComponent> TextSurfacesForBlock(MyCubeBlock fatBlock)
        {
            var result = new List<MyTextPanelComponent>();
            var textPanel = fatBlock as MyTextPanel;
            if (textPanel?.PanelComponent != null)
            {
                result.Add(textPanel.PanelComponent);
                return result;
            }

            var multiPanelOwner = fatBlock as IMyMultiTextPanelComponentOwner;
            if (multiPanelOwner?.MultiTextPanel?.Panels == null)
                return result;

            foreach (var panel in multiPanelOwner.MultiTextPanel.Panels)
            {
                if (panel != null)
                    result.Add(panel);
            }

            return result;
        }

        private static List<ScreenAreaInfo> ScreenAreasForBlock(MyCubeBlockDefinition definition)
        {
            var result = new List<ScreenAreaInfo>();
            var textPanelDefinition = definition as MyTextPanelDefinition;
            if (textPanelDefinition != null && textPanelDefinition.ScreenAreas != null && textPanelDefinition.ScreenAreas.Count > 0)
            {
                AddScreenAreas(result, textPanelDefinition.ScreenAreas);
                return result;
            }

            if (textPanelDefinition != null && !string.IsNullOrWhiteSpace(textPanelDefinition.PanelMaterialName))
            {
                result.Add(new ScreenAreaInfo
                {
                    MaterialName = textPanelDefinition.PanelMaterialName,
                    TextureResolution = textPanelDefinition.TextureResolution,
                    ScreenWidth = textPanelDefinition.ScreenWidth,
                    ScreenHeight = textPanelDefinition.ScreenHeight,
                });
                return result;
            }

            var functionalDefinition = definition as MyFunctionalBlockDefinition;
            if (functionalDefinition != null && functionalDefinition.ScreenAreas != null)
                AddScreenAreas(result, functionalDefinition.ScreenAreas);

            return result;
        }

        private static void AddScreenAreas(List<ScreenAreaInfo> result, List<ScreenArea> screenAreas)
        {
            foreach (var area in screenAreas)
            {
                if (area == null)
                    continue;

                result.Add(new ScreenAreaInfo
                {
                    MaterialName = area.Name ?? string.Empty,
                    DisplayName = area.DisplayName ?? string.Empty,
                    TextureResolution = area.TextureResolution,
                    ScreenWidth = area.ScreenWidth,
                    ScreenHeight = area.ScreenHeight,
                    Script = area.Script ?? string.Empty,
                });
            }
        }

        private static ViewerLcdSurface ToLcdSurface(
            int index,
            MyTextPanelComponent surface,
            List<ScreenAreaInfo> screenAreas,
            MetadataAssetCatalog catalog,
            bool isWorking)
        {
            var area = index < screenAreas.Count ? screenAreas[index] : null;
            var textureSize = surface.TextureSize;
            var surfaceSize = surface.SurfaceSize;
            var surfaceWidth = SafePositive(surfaceSize.X, area?.ScreenWidth ?? 1);
            var surfaceHeight = SafePositive(surfaceSize.Y, area?.ScreenHeight ?? 1);
            var dto = new ViewerLcdSurface
            {
                Index = index,
                MaterialName = FirstNonEmpty(surface.Name, area?.MaterialName),
                Name = surface.Name ?? string.Empty,
                DisplayName = FirstNonEmpty(surface.DisplayName, area?.DisplayName),
                ContentType = surface.ContentType.ToString(),
                IsWorking = isWorking,
                UsesOnlineTextureWhenEmpty = surface.UseOnlineTexture,
                TextureWidth = SafeDimension(textureSize.X, area?.TextureResolution ?? 512),
                TextureHeight = SafeDimension(textureSize.Y, area?.TextureResolution ?? 512),
                SurfaceWidth = surfaceWidth,
                SurfaceHeight = surfaceHeight,
                PreserveAspectRatio = surface.PreserveAspectRatio,
                TextPadding = surface.TextPadding,
                Font = surface.Font.SubtypeName ?? string.Empty,
                FontSize = surface.FontSize,
                Alignment = surface.Alignment.ToString(),
                Text = surface.Text ?? string.Empty,
                BackgroundColor = ToDto(surface.BackgroundColor),
                BackgroundAlpha = surface.BackgroundAlpha,
                FontColor = ToDto(surface.FontColor),
                ScriptBackgroundColor = ToDto(surface.ScriptBackgroundColor),
                ScriptForegroundColor = ToDto(surface.ScriptForegroundColor),
                CurrentlyShownImageId = CurrentlyShownImage(surface),
            };

            dto.EmptyOnlineImage = EmptyOnlineImage(surface.UseOnlineTexture, surfaceWidth, surfaceHeight, catalog);
            dto.EmptyOfflineImage = EmptyOfflineImage(surfaceWidth, surfaceHeight, catalog);

            for (var i = 0; i < surface.SelectedTexturesToDraw.Count; i++)
            {
                var image = ToLcdImage(surface.SelectedTexturesToDraw[i].Id.SubtypeName, catalog);
                dto.SelectedImages.Add(image);
                if (!string.IsNullOrEmpty(dto.CurrentlyShownImageId) && string.Equals(dto.CurrentlyShownImageId, image.Id, StringComparison.OrdinalIgnoreCase))
                    dto.CurrentImageIndex = i;
            }

            var component = surface as MyTextPanelComponent;
            if (component != null && component.ExternalSprites.Sprites != null)
            {
                foreach (var sprite in component.ExternalSprites.Sprites)
                {
                    if (sprite.Index >= component.ExternalSprites.Length)
                        continue;

                    dto.Sprites.Add(ToLcdSprite(sprite, catalog));
                }
            }

            return dto;
        }

        private static ViewerLcdImage EmptyOnlineImage(bool useOnlineTexture, float surfaceWidth, float surfaceHeight, MetadataAssetCatalog catalog)
        {
            return useOnlineTexture ? ToLcdImage(WideLcdPlaceholder(surfaceWidth, surfaceHeight) ? "Online_wide" : "Online", catalog) : null;
        }

        private static ViewerLcdImage EmptyOfflineImage(float surfaceWidth, float surfaceHeight, MetadataAssetCatalog catalog)
        {
            return ToLcdImage(WideLcdPlaceholder(surfaceWidth, surfaceHeight) ? "Offline_wide" : "Offline", catalog);
        }

        private static bool WideLcdPlaceholder(float surfaceWidth, float surfaceHeight)
        {
            return surfaceWidth > surfaceHeight * 4f;
        }

        private static string CurrentlyShownImage(MyTextPanelComponent surface)
        {
            if (surface.SelectedTexturesToDraw.Count == 0)
                return string.Empty;

            if (surface.CurrentSelectedTexture >= surface.SelectedTexturesToDraw.Count)
                return surface.SelectedTexturesToDraw[0].Id.SubtypeName;

            return surface.SelectedTexturesToDraw[surface.CurrentSelectedTexture].Id.SubtypeName;
        }

        private static ViewerLcdImage ToLcdImage(string id, MetadataAssetCatalog catalog)
        {
            var definition = MyDefinitionManager.Static.GetDefinition<MyLCDTextureDefinition>(id);
            var image = new ViewerLcdImage
            {
                Id = id ?? string.Empty,
                TexturePath = NormalizeAssetPath(definition?.TexturePath),
                SpritePath = NormalizeAssetPath(definition?.SpritePath),
            };
            catalog.RegisterTexture(FirstNonEmpty(image.SpritePath, image.TexturePath), "lcd");
            return image;
        }

        private static ViewerLcdSprite ToLcdSprite(MySerializableSprite sprite, MetadataAssetCatalog catalog)
        {
            var dto = new ViewerLcdSprite
            {
                Type = sprite.Type.ToString(),
                Data = sprite.Data ?? string.Empty,
                Position = sprite.Position.HasValue ? ToDto((Vector2)sprite.Position.Value) : null,
                Size = sprite.Size.HasValue ? ToDto((Vector2)sprite.Size.Value) : null,
                Color = sprite.Color.HasValue ? ToDto(new Color(sprite.Color.Value)) : null,
                FontId = sprite.FontId ?? string.Empty,
                Alignment = sprite.Alignment.ToString(),
                RotationOrScale = sprite.RotationOrScale,
                Index = sprite.Index,
            };

            if (sprite.Type == SpriteType.TEXTURE && !string.IsNullOrEmpty(sprite.Data))
            {
                var image = ToLcdImage(sprite.Data, catalog);
                dto.TexturePath = image.TexturePath;
                dto.SpritePath = image.SpritePath;
            }

            return dto;
        }

        private static int SafeDimension(float value, int fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0)
                return Math.Max(1, fallback);
            return Math.Max(1, Math.Min(4096, (int)Math.Round(value)));
        }

        private static float SafePositive(float value, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0)
                return Math.Max(1f, fallback);
            return value;
        }

        private static string NormalizeAssetPath(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/');
        }

        private static void AddGeneratedBlockModelParts(
            MyCubeGrid grid,
            MySlimBlock block,
            ViewerBlockInstance dto,
            MetadataAssetCatalog catalog,
            List<string> warnings)
        {
            if (block.BlockDefinition.CubeDefinition == null)
                return;

            var cubePartModels = new List<string>();
            var cubePartMatrices = new List<MatrixD>();
            var cubePartNormals = new List<Vector3>();
            var cubePartPatternOffsets = new List<Vector4UByte>();

            try
            {
                block.Orientation.GetMatrix(out Matrix rotation);
                MyCubeGrid.GetCubeParts(
                    block.BlockDefinition,
                    block.Position,
                    rotation,
                    grid.GridSize,
                    cubePartModels,
                    cubePartMatrices,
                    cubePartNormals,
                    cubePartPatternOffsets,
                    topologyCheck: true);

                for (var i = 0; i < cubePartModels.Count; i++)
                {
                    var modelAssetId = catalog.RegisterModel(cubePartModels[i]);
                    if (string.IsNullOrEmpty(modelAssetId))
                        continue;

                    dto.ModelParts.Add(new ViewerBlockModelPart
                    {
                        ModelAssetId = modelAssetId,
                        LocalMatrix = ToDto(cubePartMatrices[i]),
                        LocalNormal = ToDto(cubePartNormals[i]),
                        PatternOffset = ToDto(cubePartPatternOffsets[i]),
                    });
                }
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to resolve generated model parts for block " + block.Position + ": " + exception.Message);
            }
        }

        private static void AddRuntimeSubparts(
            MyCubeGrid grid,
            MySlimBlock block,
            ViewerBlockInstance dto,
            MetadataAssetCatalog catalog,
            List<string> warnings)
        {
            var fatBlock = block.FatBlock;
            if (fatBlock?.Subparts == null || fatBlock.Subparts.Count == 0)
                return;

            try
            {
                var gridWorldInverse = MatrixD.Invert(grid.WorldMatrix);
                AddRuntimeSubparts(block, fatBlock.Subparts, string.Empty, gridWorldInverse, dto, catalog, warnings);
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to resolve runtime subparts for block " + block.Position + ": " + exception.Message);
            }
        }

        private static void AddRuntimeSubparts(
            MySlimBlock block,
            Dictionary<string, MyEntitySubpart> subparts,
            string parentPath,
            MatrixD gridWorldInverse,
            ViewerBlockInstance dto,
            MetadataAssetCatalog catalog,
            List<string> warnings)
        {
            foreach (var pair in subparts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var subpart = pair.Value;
                if (subpart == null || subpart.Closed || subpart.MarkedForClose)
                    continue;

                var subpartName = string.IsNullOrEmpty(pair.Key) ? subpart.EntityId.ToString() : pair.Key;
                var subpartPath = string.IsNullOrEmpty(parentPath) ? subpartName : parentPath + "/" + subpartName;

                try
                {
                    var modelPath = subpart.Model?.AssetName;
                    if (string.IsNullOrEmpty(modelPath))
                    {
                        warnings.Add("Runtime subpart " + subpartPath + " for block " + block.Position + " has no model path.");
                    }
                    else if (subpart.PositionComp == null)
                    {
                        warnings.Add("Runtime subpart " + subpartPath + " for block " + block.Position + " has no position component.");
                    }
                    else
                    {
                        var modelAssetId = catalog.RegisterModel(modelPath);
                        if (string.IsNullOrEmpty(modelAssetId))
                        {
                            warnings.Add("Runtime subpart " + subpartPath + " for block " + block.Position + " has no registered model asset.");
                        }
                        else
                        {
                            var localMatrix = subpart.PositionComp.WorldMatrixRef * gridWorldInverse;
                            var subpartDto = new ViewerBlockSubpart
                            {
                                Name = subpartPath,
                                ModelAssetId = modelAssetId,
                                LocalMatrix = ToDto(localMatrix),
                            };
                            AddRuntimeSubpartLightSources(block, subpart, subpartPath, localMatrix, subpartDto.LightSources);
                            dto.Subparts.Add(subpartDto);
                        }
                    }
                }
                catch (Exception exception)
                {
                    warnings.Add("Failed to resolve runtime subpart " + subpartPath + " for block " + block.Position + ": " + exception.Message);
                }

                if (subpart.Subparts != null && subpart.Subparts.Count > 0)
                    AddRuntimeSubparts(block, subpart.Subparts, subpartPath, gridWorldInverse, dto, catalog, warnings);
            }
        }

        private static void AddRuntimeSubpartLightSources(
            MySlimBlock block,
            MyEntitySubpart subpart,
            string subpartPath,
            MatrixD subpartGridLocalMatrix,
            List<ViewerLightSource> lightSources)
        {
            var fatBlock = block.FatBlock;
            if (fatBlock == null)
                return;

            var logic = LightingLogic(fatBlock);
            if (logic != null)
            {
                AddRuntimeSubpartLightingLogicSources(block, subpart, subpartPath, subpartGridLocalMatrix, logic, lightSources);
                return;
            }

            MyLightingComponent lightingComponent;
            if (fatBlock.Components != null && fatBlock.Components.TryGet(out lightingComponent))
            {
                logic = LightingLogic(lightingComponent);
                if (logic != null)
                {
                    AddRuntimeSubpartLightingLogicSources(block, subpart, subpartPath, subpartGridLocalMatrix, logic, lightSources);
                    return;
                }
            }
        }

        private static void AddRuntimeSubpartLightingLogicSources(
            MySlimBlock block,
            MyEntitySubpart subpart,
            string subpartPath,
            MatrixD subpartGridLocalMatrix,
            MyLightingLogic logic,
            List<ViewerLightSource> lightSources)
        {
            var functionalBlock = block.FatBlock as MyFunctionalBlock;
            var enabled = (functionalBlock == null || functionalBlock.IsWorking) && logic.Intensity > 0f;
            var blockId = block.FatBlock.EntityId.ToString();
            var radius = ValidLightValue(logic.Radius, 0f);
            var reflectorRadius = ValidLightValue(logic.ReflectorRadius, radius);
            var falloff = ValidLightValue(logic.Falloff, 1f);
            var coneDegrees = logic.ReflectorConeDegrees > 0f ? logic.ReflectorConeDegrees : 52f;
            var matrices = RuntimeSubpartLightMatrices(logic, subpart, subpartGridLocalMatrix).ToList();
            foreach (var sourceMatrix in matrices)
            {
                var position = sourceMatrix.Translation;
                var direction = SafeNormalized(sourceMatrix.Forward, Vector3.Forward);
                var up = SafeNormalized(sourceMatrix.Up, Vector3.Up);
                var sourceId = blockId + ":subpart:" + subpartPath + ":" + lightSources.Count;

                if (logic.IsReflector)
                {
                    var spotIntensity = ViewerLightIntensity(logic.Intensity, 8f);
                    if (reflectorRadius > 0f || spotIntensity > 0f)
                    {
                        lightSources.Add(CreateLightSource(
                            sourceId + ":spot", blockId, "spot", (Vector3)position, direction, up, logic.Color,
                            radius, reflectorRadius, spotIntensity, falloff, coneDegrees,
                            enabled && reflectorRadius > 0f && spotIntensity > 0f,
                            logic.BlinkIntervalSeconds, logic.BlinkLength, logic.BlinkOffset));
                    }

                    var companionIntensity = ViewerLightIntensity(logic.Intensity, 0.3f);
                    if (radius > 0f && companionIntensity > 0f)
                    {
                        lightSources.Add(CreateLightSource(
                            sourceId + ":point", blockId, "point", (Vector3)position, direction, up, logic.Color,
                            radius, reflectorRadius, companionIntensity, falloff, coneDegrees,
                            enabled && radius > 0f && companionIntensity > 0f,
                            logic.BlinkIntervalSeconds, logic.BlinkLength, logic.BlinkOffset));
                    }

                    continue;
                }

                var intensity = ViewerLightIntensity(logic.Intensity, 2f);
                if (radius <= 0f && intensity <= 0f)
                    continue;

                lightSources.Add(CreateLightSource(
                    sourceId + ":point", blockId, "point", (Vector3)position, direction, up, logic.Color,
                    radius, reflectorRadius, intensity, falloff, coneDegrees,
                    enabled && radius > 0f && intensity > 0f,
                    logic.BlinkIntervalSeconds, logic.BlinkLength, logic.BlinkOffset));
            }
        }

        private static IEnumerable<MatrixD> RuntimeSubpartLightMatrices(MyLightingLogic logic, MyEntitySubpart subpart, MatrixD subpartGridLocalMatrix)
        {
            foreach (var localData in logic.LightLocalDatas)
            {
                if (localData == null)
                    continue;

                var localDataSubpart = localData.Subpart;
                if (!ReferenceEquals(localDataSubpart, subpart))
                    continue;

                yield return MatrixD.Normalize(localData.LocalMatrix) * subpartGridLocalMatrix;
            }
        }

        private static MyLightingLogic LightingLogic(object owner)
        {
            for (var type = owner.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField("m_lightingLogic", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(owner) is MyLightingLogic logic)
                    return logic;
            }

            return null;
        }

        private static Vector3 SafeNormalized(Vector3 value, Vector3 fallback)
        {
            if (!value.IsValid() || value.LengthSquared() < 0.0001f)
                value = fallback;
            value.Normalize();
            return value;
        }

        private static void AddSkinTextureChanges(MySlimBlock block, ViewerBlockInstance dto)
        {
            if (block.SkinSubtypeId == MyStringHash.NullOrEmpty)
                return;

            // Dedicated-server builds do not reference VRage.Render, where the
            // client-side texture-change payload types live. Keep SkinSubtypeId as
            // metadata and let the browser/local-content path decide fallbacks.
            dto.SkinTextureChanges.Clear();
        }

        private static string DefinitionId(MyCubeBlockDefinition definition)
        {
            return definition.Id.ToString();
        }

        private static string VisibilityClass(MyCubeBlockDefinition definition)
        {
            if (IsTransparentDefinition(definition))
                return "transparent";

            if (OpaqueFaceMask(definition) == 63)
                return "opaque-full-cell";

            return "opaque-partial";
        }

        private static int OpaqueFaceMask(MyCubeBlockDefinition definition)
        {
            if (IsTransparentDefinition(definition) ||
                !definition.BlockTopology.ToString().Equals("Cube", StringComparison.OrdinalIgnoreCase) ||
                definition.Size != Vector3I.One)
                return 0;

            if (definition.IsCubePressurized == null || !definition.IsCubePressurized.TryGetValue(Vector3I.Zero, out var faces))
                return 0;

            var mask = 0;
            AddOpaqueFaceBit(faces, new Vector3I(1, 0, 0), 0, ref mask);
            AddOpaqueFaceBit(faces, new Vector3I(-1, 0, 0), 1, ref mask);
            AddOpaqueFaceBit(faces, new Vector3I(0, 1, 0), 2, ref mask);
            AddOpaqueFaceBit(faces, new Vector3I(0, -1, 0), 3, ref mask);
            AddOpaqueFaceBit(faces, new Vector3I(0, 0, 1), 4, ref mask);
            AddOpaqueFaceBit(faces, new Vector3I(0, 0, -1), 5, ref mask);
            return mask;
        }

        private static void AddOpaqueFaceBit(
            Dictionary<Vector3I, MyCubeBlockDefinition.MyCubePressurizationMark> faces,
            Vector3I normal,
            int bit,
            ref int mask)
        {
            if (faces.TryGetValue(normal, out var mark) && mark == MyCubeBlockDefinition.MyCubePressurizationMark.PressurizedAlways)
                mask |= 1 << bit;
        }

        private static bool IsTransparentDefinition(MyCubeBlockDefinition definition)
        {
            var id = definition.Id.ToString();
            var model = definition.Model ?? string.Empty;
            return id.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   model.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static Vector3I ChunkCoordinate(Vector3I cell, int chunkSize)
        {
            return new Vector3I(FloorDiv(cell.X, chunkSize), FloorDiv(cell.Y, chunkSize), FloorDiv(cell.Z, chunkSize));
        }

        private static int FloorDiv(int value, int divisor)
        {
            var quotient = value / divisor;
            var remainder = value % divisor;
            return remainder != 0 && ((remainder < 0) != (divisor < 0)) ? quotient - 1 : quotient;
        }

        private static string ChunkId(Vector3I chunk)
        {
            return chunk.X + "," + chunk.Y + "," + chunk.Z;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return string.Empty;
        }

        private static ViewerVector3I ToDto(Vector3I value)
        {
            return new ViewerVector3I { X = value.X, Y = value.Y, Z = value.Z };
        }

        private static ViewerVector3 ToDto(Vector3 value)
        {
            return new ViewerVector3 { X = value.X, Y = value.Y, Z = value.Z };
        }

        private static ViewerVector2 ToDto(Vector2 value)
        {
            return new ViewerVector2 { X = value.X, Y = value.Y };
        }

        private static ViewerColor ToDto(Color value)
        {
            return new ViewerColor { R = value.R, G = value.G, B = value.B, A = value.A };
        }

        private static ViewerVector3D ToDto(Vector3D value)
        {
            return new ViewerVector3D { X = value.X, Y = value.Y, Z = value.Z };
        }

        private static ViewerVector4Byte ToDto(Vector4UByte value)
        {
            return new ViewerVector4Byte { X = value.X, Y = value.Y, Z = value.Z, W = value.W };
        }

        private static ViewerMatrix ToDto(Matrix value)
        {
            return new ViewerMatrix
            {
                M11 = value.M11, M12 = value.M12, M13 = value.M13, M14 = value.M14,
                M21 = value.M21, M22 = value.M22, M23 = value.M23, M24 = value.M24,
                M31 = value.M31, M32 = value.M32, M33 = value.M33, M34 = value.M34,
                M41 = value.M41, M42 = value.M42, M43 = value.M43, M44 = value.M44,
            };
        }

        private static ViewerMatrix ToDto(MatrixD value)
        {
            return new ViewerMatrix
            {
                M11 = value.M11, M12 = value.M12, M13 = value.M13, M14 = value.M14,
                M21 = value.M21, M22 = value.M22, M23 = value.M23, M24 = value.M24,
                M31 = value.M31, M32 = value.M32, M33 = value.M33, M34 = value.M34,
                M41 = value.M41, M42 = value.M42, M43 = value.M43, M44 = value.M44,
            };
        }

        private static ViewerBounds ToDto(BoundingBoxD value)
        {
            return new ViewerBounds { Min = ToDto(value.Min), Max = ToDto(value.Max) };
        }

        private sealed class ChunkBuilder
        {
            private readonly string _id;
            private Vector3I _min;
            private Vector3I _max;
            private bool _initialized;
            private int _count;

            public ChunkBuilder(string id)
            {
                _id = id;
            }

            public void Include(Vector3I blockMin, Vector3I blockMax)
            {
                if (!_initialized)
                {
                    _min = blockMin;
                    _max = blockMax;
                    _initialized = true;
                }
                else
                {
                    _min = Vector3I.Min(_min, blockMin);
                    _max = Vector3I.Max(_max, blockMax);
                }

                _count++;
            }

            public ViewerGridChunk ToDto(float gridSize)
            {
                var half = gridSize * 0.5f;
                return new ViewerGridChunk
                {
                    Id = _id,
                    MinCell = GridRenderSceneInspector.ToDto(_min),
                    MaxCell = GridRenderSceneInspector.ToDto(_max),
                    LocalAabbMin = GridRenderSceneInspector.ToDto(new Vector3(_min.X * gridSize - half, _min.Y * gridSize - half, _min.Z * gridSize - half)),
                    LocalAabbMax = GridRenderSceneInspector.ToDto(new Vector3(_max.X * gridSize + half, _max.Y * gridSize + half, _max.Z * gridSize + half)),
                    BlockCount = _count,
                };
            }
        }

        private sealed class ScreenAreaInfo
        {
            public string MaterialName { get; set; } = string.Empty;

            public string DisplayName { get; set; } = string.Empty;

            public int TextureResolution { get; set; } = 512;

            public int ScreenWidth { get; set; } = 1;

            public int ScreenHeight { get; set; } = 1;

            public string Script { get; set; } = string.Empty;
        }

        private sealed class StorageRange
        {
            public Vector3I Min { get; set; }

            public Vector3I Max { get; set; }
        }

        private sealed class MetadataAssetCatalog
        {
            private readonly Dictionary<string, ViewerModelAsset> _modelsById = new Dictionary<string, ViewerModelAsset>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ViewerTextureAsset> _texturesById = new Dictionary<string, ViewerTextureAsset>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _idsByLogicalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string RegisterModel(string logicalPath)
            {
                logicalPath = NormalizeLogicalPath(logicalPath);
                if (string.IsNullOrEmpty(logicalPath))
                    return null;

                var assetId = GetOrCreateId("model", logicalPath);
                if (!_modelsById.ContainsKey(assetId))
                {
                    _modelsById[assetId] = new ViewerModelAsset
                    {
                        AssetId = assetId,
                        LogicalPath = logicalPath,
                        SourceKind = SourceKind(logicalPath),
                    };
                }

                return assetId;
            }

            public string RegisterTexture(string logicalPath, string usage)
            {
                logicalPath = NormalizeLogicalPath(logicalPath);
                if (string.IsNullOrEmpty(logicalPath))
                    return null;

                var assetId = GetOrCreateId("texture", logicalPath);
                if (!_texturesById.ContainsKey(assetId))
                {
                    _texturesById[assetId] = new ViewerTextureAsset
                    {
                        AssetId = assetId,
                        LogicalPath = logicalPath,
                        SourceKind = SourceKind(logicalPath),
                        Usage = usage ?? "unknown",
                    };
                }

                return assetId;
            }

            public List<ViewerModelAsset> ModelAssetsSnapshot()
            {
                return _modelsById.Values.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).ToList();
            }

            public List<ViewerTextureAsset> TextureAssetsSnapshot()
            {
                return _texturesById.Values.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).ToList();
            }

            private string GetOrCreateId(string prefix, string logicalPath)
            {
                var key = prefix + ":" + logicalPath.ToLowerInvariant();
                if (_idsByLogicalPath.TryGetValue(key, out var existing))
                    return existing;

                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                    var builder = new StringBuilder(prefix).Append('_');
                    for (var i = 0; i < 8; i++)
                        builder.Append(hash[i].ToString("x2"));

                    var id = builder.ToString();
                    _idsByLogicalPath[key] = id;
                    return id;
                }
            }

            private static string NormalizeLogicalPath(string path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return null;

                path = path.Trim().Replace('\\', '/');
                var contentPath = VRage.FileSystem.MyFileSystem.ContentPath;
                if (!string.IsNullOrEmpty(contentPath) && Path.IsPathRooted(path))
                {
                    var fullPath = Path.GetFullPath(path);
                    var root = Path.GetFullPath(contentPath);
                    if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        path = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                }

                while (path.StartsWith("./", StringComparison.Ordinal))
                    path = path.Substring(2);

                return path;
            }

            private static string SourceKind(string logicalPath)
            {
                return logicalPath.IndexOf("Mods/", StringComparison.OrdinalIgnoreCase) >= 0 ? "mod" : "game";
            }
        }
    }
}
