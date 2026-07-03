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
using Sandbox.Game.GameSystems;
using Sandbox.Game.GameSystems.Conveyors;
using Sandbox.Game.World;
using SpaceEngineers.Game.Entities.Blocks;
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
    /// Keep capture read-oriented; viewer commands run on a background worker.
    /// </summary>
    internal static class GridRenderSceneInspector
    {
        private const int ChunkSizeCells = 16;
        private const int DefaultVoxelChunkSize = 32;
        private const int DamageVoxelChunkSize = 16;
        private const int MaxVoxelSceneChunks = 96;
        private const int MaxVoxelDataBytes = 8 * 1024 * 1024;
        private const int MaxVoxelDamageSceneChunks = 160;
        private const int MaxVoxelDamageDataBytes = 4 * 1024 * 1024;
        private const int MaxVoxelBodySceneChunks = 160;
        private const int MaxVoxelBodyDataBytes = 12 * 1024 * 1024;
        private const int MaxVoxelBodyLod = 5;
        private const int VoxelDataPadding = 1;
        private const int VoxelLod = 0;
        private const double SmallGridCubeSize = 0.5;
        private const double LargeGridCubeSize = 2.5;
        private const int FloorGridPaddingSupersquares = 2;
        private const double ContextHalfExtentScale = 2.0;
        private const int MaxContextGrids = 64;
        private const int MaxContextBlocks = 20000;
        private const int MaxContextGridBlocks = 8000;
        private static readonly FieldInfo CubeGridSkeletonField = typeof(MyCubeGrid).GetField("Skeleton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly MethodInfo GridSkeletonTryGetBoneMethod = CubeGridSkeletonField?.FieldType.GetMethod("TryGetBone", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Vector3I).MakeByRefType(), typeof(Vector3).MakeByRefType() }, null);

        public static EntityRenderScene Build(long entityId, string gameVersion, string pluginVersion, bool includeVoxels = false, bool includeContext = false)
        {
            if (!MyEntities.TryGetEntityById<MyCubeGrid>(entityId, out var grid) || grid == null || grid.MarkedForClose || grid.Closed)
                throw new InvalidOperationException("Grid not found or not loaded on this server.");

            return Build(grid, gameVersion, pluginVersion, includeVoxels, includeContext);
        }

        public static EntityRenderScene Build(MyCubeGrid grid, string gameVersion, string pluginVersion, bool includeVoxels = false, bool includeContext = false)
        {
            if (grid == null || grid.MarkedForClose || grid.Closed)
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

            AddSceneMods(catalog);

            var definitions = new Dictionary<string, ViewerBlockDefinition>(StringComparer.Ordinal);
            var chunks = new Dictionary<string, ChunkBuilder>(StringComparer.Ordinal);
            var contextLocalAabb = includeContext ? ContextLocalAabb(grid) : (BoundingBoxD?)null;
            var contextRelativeAabb = contextLocalAabb.HasValue ? ContextRelativeAabb(grid, contextLocalAabb.Value) : (BoundingBoxD?)null;
            var contextClip = contextRelativeAabb.HasValue ? BuildContextClipVolume(grid, contextRelativeAabb.Value) : null;
            var contextAabb = contextLocalAabb.HasValue ? LocalAabbToWorldAabb(contextLocalAabb.Value.Min, contextLocalAabb.Value.Max, grid.WorldMatrix) : (BoundingBoxD?)null;
            var contextClipAabb = contextClip != null ? ContextClipWorldAabb(contextClip) : (BoundingBoxD?)null;
            var contextVoxelAabb = contextClipAabb ?? contextAabb;
            var contextBlocks = 0;
            var clippedGridCount = 0;
            var logisticsGrids = new List<MyCubeGrid>();

            foreach (var sceneGridCandidate in ContextGrids(grid, contextClip, contextClipAabb, scene.Warnings))
            {
                var sceneGrid = sceneGridCandidate.Grid;
                var isPrimary = sceneGrid.EntityId == grid.EntityId;
                if (!isPrimary && includeContext && !sceneGridCandidate.ForceFullGrid)
                {
                    if (scene.Grids.Count >= MaxContextGrids)
                    {
                        scene.Warnings.Add("Context grid limit reached; remaining nearby grids were skipped.");
                        break;
                    }

                    if (contextBlocks >= MaxContextBlocks)
                    {
                        scene.Warnings.Add("Context block limit reached; remaining nearby grids were skipped.");
                        break;
                    }
                }

                var result = AddGridToScene(scene, sceneGrid, isPrimary, includeContext, sceneGridCandidate.ForceFullGrid, contextAabb, contextClip, definitions, chunks, catalog, ref contextBlocks);
                if (result.Grid.IsClippedToContext)
                    clippedGridCount++;
                scene.Grids.Add(result.Grid);
                if (isPrimary || result.IncludedBlockCount > 0)
                    logisticsGrids.Add(sceneGrid);
            }

            if (scene.Grids.Count == 0)
                scene.Grids.Add(scene.Grid);
            scene.Grid = scene.Grids.FirstOrDefault(candidate => candidate.IsPrimary) ?? scene.Grid;
            scene.BlockDefinitions = definitions.Values.OrderBy(definition => definition.Id, StringComparer.Ordinal).ToList();
            scene.Chunks = chunks.Values.Select(chunk => chunk.ToDto()).OrderBy(chunk => chunk.Id, StringComparer.Ordinal).ToList();
            scene.Logistics = BuildLogistics(logisticsGrids, scene.BlockInstances, scene.Warnings);
            scene.ModelAssets = catalog.ModelAssetsSnapshot();
            scene.TextureAssets = catalog.TextureAssetsSnapshot();
            scene.Mods = catalog.ModsSnapshot();
            scene.Voxels = LoadedVoxels(contextVoxelAabb, contextClip);
            if (includeVoxels)
            {
                scene.VoxelDeformations = BuildVoxelDeformations(grid, scene.Warnings, contextVoxelAabb);
                scene.VoxelDamageDeformations = BuildVoxelDamageDeformations(scene.VoxelDeformations, scene.Warnings);
                scene.VoxelMaterials = BuildVoxelMaterials(scene.VoxelDeformations, scene.Warnings);
            }
            else
                scene.Warnings.Add("Voxel data generation disabled by URL.");

            if (includeContext && contextAabb.HasValue)
            {
                scene.Context = new ViewerSceneContext
                {
                    Enabled = true,
                    PrimaryGridId = grid.EntityId.ToString(),
                    WorldAabb = ToDto(contextAabb.Value),
                    RelativeAabb = ToDto(contextRelativeAabb.Value),
                    GridCount = scene.Grids.Count,
                    ClippedGridCount = clippedGridCount,
                    VoxelBodyCount = scene.Voxels.Count,
                    VoxelMeshChunkCount = scene.VoxelDeformations.Count,
                };
            }
            return scene;
        }

        public static EntityRenderScene BuildVoxel(long entityId, string gameVersion, string pluginVersion, bool includeVoxels = true)
        {
            if (!MyEntities.TryGetEntityById<MyVoxelBase>(entityId, out var voxel) || voxel == null || voxel.MarkedForClose || voxel.Closed)
                throw new InvalidOperationException("Voxel entity not found or not loaded on this server.");

            return BuildVoxel(voxel, gameVersion, pluginVersion, includeVoxels);
        }

        public static EntityRenderScene BuildVoxel(MyVoxelBase voxel, string gameVersion, string pluginVersion, bool includeVoxels = true)
        {
            if (voxel == null || voxel.MarkedForClose || voxel.Closed)
                throw new InvalidOperationException("Voxel entity not found or not loaded on this server.");

            var kind = VoxelKind(voxel);
            if (kind == "voxelPhysics")
            {
                voxel = voxel.RootVoxel;
                if (voxel == null || voxel.MarkedForClose || voxel.Closed)
                    throw new InvalidOperationException("Voxel physics root entity is not loaded on this server.");

                kind = VoxelKind(voxel);
            }

            if (kind == "planet")
                throw new InvalidOperationException("Planet viewer support is not available yet.");
            if (voxel.Storage == null)
                throw new InvalidOperationException("Voxel entity storage is not loaded on this server.");

            var scene = new EntityRenderScene
            {
                GameVersion = gameVersion ?? string.Empty,
                PluginVersion = pluginVersion ?? string.Empty,
                Grid = ToSyntheticGrid(voxel),
                Environment = ToEnvironment(),
                CapturedAtUtc = DateTimeOffset.UtcNow,
            };
            scene.Grids.Add(scene.Grid);

            scene.Voxels.Add(ToVoxelBody(voxel, kind));
            if (includeVoxels)
            {
                scene.VoxelDeformations = BuildVoxelBodyDeformations(voxel, scene.Warnings);
                scene.VoxelDamageDeformations = BuildVoxelDamageDeformations(scene.VoxelDeformations, scene.Warnings);
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

        private static void AddSceneMods(MetadataAssetCatalog catalog)
        {
            var mods = MySession.Static?.Mods;
            if (mods == null)
                return;

            foreach (var mod in mods)
                catalog.RegisterMod(mod);
        }

        private static GridSceneAddResult AddGridToScene(
            EntityRenderScene scene,
            MyCubeGrid grid,
            bool isPrimary,
            bool includeContext,
            bool forceFullGrid,
            BoundingBoxD? contextAabb,
            ContextClipVolume contextClip,
            Dictionary<string, ViewerBlockDefinition> definitions,
            Dictionary<string, ChunkBuilder> chunks,
            MetadataAssetCatalog catalog,
            ref int contextBlocks)
        {
            var gridDto = ToGrid(grid, isPrimary, includeContext && !isPrimary);
            var occupancy = BuildBlockOccupancy(grid);
            var included = 0;
            var skipped = 0;
            var includedBounds = BoundingBoxD.CreateInvalid();
            foreach (var block in grid.CubeBlocks)
            {
                if (block == null || block.BlockDefinition == null)
                    continue;

                var includeBlock = isPrimary || (!includeContext && forceFullGrid) || contextClip == null || BlockIntersectsContext(grid, block, contextClip);
                if (!includeBlock)
                {
                    skipped++;
                    continue;
                }

                if (!isPrimary && includeContext)
                {
                    if (included >= MaxContextGridBlocks)
                    {
                        skipped++;
                        continue;
                    }

                    if (contextBlocks >= MaxContextBlocks)
                    {
                        skipped++;
                        continue;
                    }

                    contextBlocks++;
                }

                var definitionId = DefinitionId(block.BlockDefinition);
                var chunkCoord = ChunkCoordinate(block.Position, ChunkSizeCells);
                var chunkId = grid.EntityId + ":" + ChunkId(chunkCoord);
                var blockWorldAabb = BlockWorldAabb(grid, block);
                var blockInstance = ToBlockInstance(grid, block, definitionId, chunkId, occupancy, catalog, scene.Warnings, out var fullyCulledGeneratedBlock);
                if (fullyCulledGeneratedBlock)
                    continue;

                if (!definitions.ContainsKey(definitionId))
                    definitions[definitionId] = ToBlockDefinition(block.BlockDefinition, catalog, scene.Warnings);

                if (!chunks.TryGetValue(chunkId, out var chunk))
                {
                    chunk = new ChunkBuilder(chunkId, grid.EntityId.ToString(), grid.GridSize);
                    chunks.Add(chunkId, chunk);
                }

                chunk.Include(block.Min, block.Max);
                includedBounds.Include(blockWorldAabb.Min);
                includedBounds.Include(blockWorldAabb.Max);
                scene.BlockInstances.Add(blockInstance);
                AddLightSources(grid, block, scene.LightSources, scene.Warnings);
                AddSubpartLightSources(blockInstance, scene.LightSources);
                included++;
            }

            gridDto.BlockCount = included;
            if (!isPrimary && includeContext && !forceFullGrid && contextAabb.HasValue && contextClip != null)
            {
                gridDto.IsClippedToContext = skipped > 0 || !GridInsideContext(grid, contextClip);
                if (gridDto.IsClippedToContext)
                {
                    if (included == 0)
                        includedBounds = Intersect(grid.PositionComp.WorldAABB, contextAabb.Value);
                    gridDto.ContextClippedWorldAabb = ToDto(includedBounds);
                    scene.Warnings.Add("Context grid " + gridDto.DisplayName + " was clipped to the context volume.");
                }
            }

            return new GridSceneAddResult { Grid = gridDto, IncludedBlockCount = included, SkippedBlockCount = skipped };
        }

        private static BoundingBoxD ContextLocalAabb(MyCubeGrid grid)
        {
            var selected = new BoundingBoxD(grid.PositionComp.LocalAABB.Min, grid.PositionComp.LocalAABB.Max);
            var center = selected.Center;
            var halfExtents = (selected.Max - selected.Min) * 0.5;
            halfExtents.X = Math.Max(halfExtents.X, LargeGridCubeSize);
            halfExtents.Y = Math.Max(halfExtents.Y, LargeGridCubeSize);
            halfExtents.Z = Math.Max(halfExtents.Z, LargeGridCubeSize);
            var contextHalfExtents = halfExtents * ContextHalfExtentScale;
            return new BoundingBoxD(center - contextHalfExtents, center + contextHalfExtents);
        }

        private static ContextClipVolume BuildContextClipVolume(MyCubeGrid grid, BoundingBoxD contextRelativeAabb)
        {
            Vector3I minCell;
            Vector3I maxCell;
            BoundingBoxD selected;
            var hasBlocks = TryGridLocalBlockBounds(grid, out selected, out minCell, out maxCell);

            var padding = LargeGridCubeSize * FloorGridPaddingSupersquares;
            var offsetX = hasBlocks ? FloorAxisOffset(maxCell.X - minCell.X + 1, LargeGridCubeSize) : 0;
            var offsetZ = hasBlocks ? FloorAxisOffset(maxCell.Z - minCell.Z + 1, LargeGridCubeSize) : 0;
            var startXCell = (int)Math.Floor((contextRelativeAabb.Min.X - padding - offsetX) / SmallGridCubeSize);
            var endXCell = (int)Math.Ceiling((contextRelativeAabb.Max.X + padding - offsetX) / SmallGridCubeSize);
            var startZCell = (int)Math.Floor((contextRelativeAabb.Min.Z - padding - offsetZ) / SmallGridCubeSize);
            var endZCell = (int)Math.Ceiling((contextRelativeAabb.Max.Z + padding - offsetZ) / SmallGridCubeSize);

            return new ContextClipVolume
            {
                Primary = grid,
                CenterLocal = new BoundingBoxD(grid.PositionComp.LocalAABB.Min, grid.PositionComp.LocalAABB.Max).Center,
                MinX = offsetX + startXCell * SmallGridCubeSize,
                MaxX = offsetX + endXCell * SmallGridCubeSize,
                MinY = contextRelativeAabb.Min.Y,
                MaxY = contextRelativeAabb.Max.Y,
                MinZ = offsetZ + startZCell * SmallGridCubeSize,
                MaxZ = offsetZ + endZCell * SmallGridCubeSize,
            };
        }

        private static BoundingBoxD ContextRelativeAabb(MyCubeGrid grid, BoundingBoxD contextLocalAabb)
        {
            var selected = new BoundingBoxD(grid.PositionComp.LocalAABB.Min, grid.PositionComp.LocalAABB.Max);
            var center = selected.Center;
            return new BoundingBoxD(contextLocalAabb.Min - center, contextLocalAabb.Max - center);
        }

        private static BoundingBoxD ContextClipWorldAabb(ContextClipVolume contextClip)
        {
            var localMin = contextClip.CenterLocal + new Vector3D(contextClip.MinX, contextClip.MinY, contextClip.MinZ);
            var localMax = contextClip.CenterLocal + new Vector3D(contextClip.MaxX, contextClip.MaxY, contextClip.MaxZ);
            return LocalAabbToWorldAabb(localMin, localMax, contextClip.Primary.WorldMatrix);
        }

        private static IEnumerable<SceneGridCandidate> ContextGrids(MyCubeGrid primary, ContextClipVolume contextClip, BoundingBoxD? contextClipAabb, List<string> warnings)
        {
            var visible = new Dictionary<long, SceneGridCandidate>();
            AddSceneGridCandidate(visible, primary, forceFullGrid: true);

            if (contextClip != null && contextClipAabb.HasValue)
            {
                IEnumerable<MyCubeGrid> candidates;
                try
                {
                    candidates = MyEntities.GetEntities().OfType<MyCubeGrid>().ToList();
                }
                catch (Exception exception)
                {
                    warnings.Add("Failed to enumerate context grids: " + exception.Message);
                    candidates = Enumerable.Empty<MyCubeGrid>();
                }

                foreach (var grid in candidates.Where(candidate => IsUsableContextGrid(candidate) && candidate.EntityId != primary.EntityId))
                {
                    if (GridIntersectsContext(grid, contextClip, contextClipAabb.Value))
                        AddSceneGridCandidate(visible, grid, forceFullGrid: false);
                }
            }

            if (contextClip == null)
            {
                foreach (var connectedGrid in MechanicalConnectedGrids(primary, warnings))
                    AddSceneGridCandidate(visible, connectedGrid, forceFullGrid: true);
            }

            return visible.Values
                .OrderBy(candidate => candidate.Grid.EntityId == primary.EntityId ? 0 : 1)
                .ThenBy(candidate => FirstNonEmpty(candidate.Grid.DisplayName, candidate.Grid.Name, "Grid " + candidate.Grid.EntityId), StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Grid.EntityId);
        }

        private static void AddSceneGridCandidate(Dictionary<long, SceneGridCandidate> visible, MyCubeGrid grid, bool forceFullGrid)
        {
            if (!IsUsableContextGrid(grid))
                return;

            if (visible.TryGetValue(grid.EntityId, out var existing))
                existing.ForceFullGrid |= forceFullGrid;
            else
                visible.Add(grid.EntityId, new SceneGridCandidate { Grid = grid, ForceFullGrid = forceFullGrid });
        }

        private static IEnumerable<MyCubeGrid> MechanicalConnectedGrids(MyCubeGrid grid, List<string> warnings)
        {
            try
            {
                return grid.GetConnectedGrids(VRage.Game.ModAPI.GridLinkTypeEnum.Mechanical)
                    .Where(IsUsableContextGrid)
                    .ToList();
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to enumerate mechanical subgrids for " + FirstNonEmpty(grid.DisplayName, grid.Name, "Grid " + grid.EntityId) + ": " + exception.Message);
                return Enumerable.Empty<MyCubeGrid>();
            }
        }

        private static bool IsUsableContextGrid(MyCubeGrid grid)
        {
            return grid != null && !grid.MarkedForClose && !grid.Closed;
        }

        private static bool GridIntersectsContext(MyCubeGrid grid, ContextClipVolume contextClip, BoundingBoxD contextWorldAabb)
        {
            if (grid.PositionComp == null)
                return false;

            if (!grid.PositionComp.WorldAABB.Intersects(contextWorldAabb))
                return false;

            foreach (var block in grid.CubeBlocks)
            {
                if (block != null && BlockIntersectsContext(grid, block, contextClip))
                    return true;
            }

            return false;
        }

        private static bool GridInsideContext(MyCubeGrid grid, ContextClipVolume contextClip)
        {
            return grid.PositionComp != null && ProjectedWorldAabbInsideContext(grid.PositionComp.WorldAABB, contextClip);
        }

        private static bool BlockIntersectsContext(MyCubeGrid grid, MySlimBlock block, ContextClipVolume contextClip)
        {
            return ProjectedLocalAabbIntersectsContext(grid, BlockLocalAabb(grid, block), contextClip);
        }

        private static BoundingBoxD BlockLocalAabb(MyCubeGrid grid, MySlimBlock block)
        {
            var gridSize = GridCubeSize(grid);
            var half = gridSize * 0.5;
            var localMin = new Vector3D(block.Min.X * gridSize - half, block.Min.Y * gridSize - half, block.Min.Z * gridSize - half);
            var localMax = new Vector3D(block.Max.X * gridSize + half, block.Max.Y * gridSize + half, block.Max.Z * gridSize + half);
            return new BoundingBoxD(localMin, localMax);
        }

        private static bool ProjectedLocalAabbIntersectsContext(MyCubeGrid grid, BoundingBoxD localAabb, ContextClipVolume contextClip)
        {
            var projected = ProjectLocalAabbToContext(grid, localAabb, contextClip);
            return projected.HasValue && AabbIntersectsContext(projected.Value, contextClip);
        }

        private static bool ProjectedWorldAabbIntersectsContext(BoundingBoxD worldAabb, ContextClipVolume contextClip)
        {
            var projected = ProjectWorldAabbToContext(worldAabb, contextClip);
            return projected.HasValue && AabbIntersectsContext(projected.Value, contextClip);
        }

        private static bool ProjectedWorldAabbInsideContext(BoundingBoxD worldAabb, ContextClipVolume contextClip)
        {
            var projected = ProjectWorldAabbToContext(worldAabb, contextClip);
            return projected.HasValue && AabbInsideContext(projected.Value, contextClip);
        }

        private static BoundingBoxD? ProjectLocalAabbToContext(MyCubeGrid grid, BoundingBoxD localAabb, ContextClipVolume contextClip)
        {
            if (grid == null || contextClip == null || contextClip.Primary == null)
                return null;

            var projected = BoundingBoxD.CreateInvalid();
            var inversePrimaryWorld = MatrixD.Invert(contextClip.Primary.WorldMatrix);
            foreach (var corner in LocalAabbCorners(localAabb.Min, localAabb.Max))
            {
                var world = Vector3D.Transform(corner, grid.WorldMatrix);
                var primaryLocal = Vector3D.Transform(world, inversePrimaryWorld) - contextClip.CenterLocal;
                projected.Include(primaryLocal);
            }

            return projected;
        }

        private static BoundingBoxD? ProjectWorldAabbToContext(BoundingBoxD worldAabb, ContextClipVolume contextClip)
        {
            if (contextClip == null || contextClip.Primary == null)
                return null;

            var projected = BoundingBoxD.CreateInvalid();
            var inversePrimaryWorld = MatrixD.Invert(contextClip.Primary.WorldMatrix);
            foreach (var corner in LocalAabbCorners(worldAabb.Min, worldAabb.Max))
            {
                var primaryLocal = Vector3D.Transform(corner, inversePrimaryWorld) - contextClip.CenterLocal;
                projected.Include(primaryLocal);
            }

            return projected;
        }

        private static bool AabbIntersectsContext(BoundingBoxD projected, ContextClipVolume contextClip)
        {
            return projected.Max.X >= contextClip.MinX
                   && projected.Min.X <= contextClip.MaxX
                   && projected.Max.Y >= contextClip.MinY
                   && projected.Min.Y <= contextClip.MaxY
                   && projected.Max.Z >= contextClip.MinZ
                   && projected.Min.Z <= contextClip.MaxZ;
        }

        private static bool AabbInsideContext(BoundingBoxD projected, ContextClipVolume contextClip)
        {
            return projected.Min.X >= contextClip.MinX
                   && projected.Max.X <= contextClip.MaxX
                   && projected.Min.Y >= contextClip.MinY
                   && projected.Max.Y <= contextClip.MaxY
                   && projected.Min.Z >= contextClip.MinZ
                   && projected.Max.Z <= contextClip.MaxZ;
        }

        private static BoundingBoxD BlockWorldAabb(MyCubeGrid grid, MySlimBlock block)
        {
            var gridSize = GridCubeSize(grid);
            var half = gridSize * 0.5;
            var localMin = new Vector3D(block.Min.X * gridSize - half, block.Min.Y * gridSize - half, block.Min.Z * gridSize - half);
            var localMax = new Vector3D(block.Max.X * gridSize + half, block.Max.Y * gridSize + half, block.Max.Z * gridSize + half);
            return LocalAabbToWorldAabb(localMin, localMax, grid.WorldMatrix);
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
                var start = lightSources.Count;
                var lightingBlock = fatBlock as MyLightingBlock;
                if (lightingBlock != null)
                {
                    AddLightingBlockSource(grid, block, lightingBlock, lightSources);
                    AssignGridId(lightSources, start, grid.EntityId.ToString());
                    return;
                }

                MyLightingComponent lightingComponent;
                if (fatBlock.Components != null && fatBlock.Components.TryGet(out lightingComponent))
                {
                    AddLightingComponentSource(grid, block, lightingComponent, lightSources);
                    AssignGridId(lightSources, start, grid.EntityId.ToString());
                }
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to inspect light source for block " + block.Position + ": " + exception.Message);
            }
        }

        private static void AssignGridId(List<ViewerLightSource> lightSources, int start, string gridId)
        {
            for (var i = start; i < lightSources.Count; i++)
                lightSources[i].GridId = gridId;
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
                {
                    lightSource.GridId = blockInstance.GridId;
                    lightSources.Add(lightSource);
                }
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
            return ToGrid(grid, false, false);
        }

        private static ViewerGrid ToGrid(MyCubeGrid grid, bool isPrimary, bool isContext)
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
                IsPrimary = isPrimary,
                IsContext = isContext,
                Bounds = ToDto(grid.PositionComp.WorldAABB),
            };
        }

        private static ViewerGrid ToSyntheticGrid(MyVoxelBase voxel)
        {
            return new ViewerGrid
            {
                Id = voxel.EntityId.ToString(),
                DisplayName = FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.StorageName, "Voxel " + voxel.EntityId),
                GridSize = (float)LargeGridCubeSize,
                GridSpace = "voxel",
                IsStatic = true,
                BlockCount = 0,
                IsPrimary = true,
                WorldMatrix = ToDto(MatrixD.Identity),
                Bounds = ToDto(voxel.PositionComp.WorldAABB),
            };
        }

        private static List<ViewerVoxelBody> LoadedVoxels(BoundingBoxD? worldAabb = null, ContextClipVolume contextClip = null)
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
                if (worldAabb.HasValue && (voxel.PositionComp == null || !voxel.PositionComp.WorldAABB.Intersects(worldAabb.Value)))
                    continue;
                if (contextClip != null && (voxel.PositionComp == null || !ProjectedWorldAabbIntersectsContext(voxel.PositionComp.WorldAABB, contextClip)))
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

        private static List<ViewerVoxelDataChunk> BuildVoxelDeformations(MyCubeGrid grid, List<string> warnings, BoundingBoxD? samplingOverride = null)
        {
            var session = MySession.Static;
            var result = new List<ViewerVoxelDataChunk>();
            if (session?.VoxelMaps?.Instances == null)
                return result;

            var samplingAabb = samplingOverride ?? VoxelSamplingWorldAabb(grid);
            var samples = new List<VoxelSceneSample>();
            var totalBytes = 0;
            var chunkBudgetReached = false;

            foreach (var voxel in session.VoxelMaps.Instances)
            {
                if (voxel == null || voxel.MarkedForClose || voxel.Closed || VoxelKind(voxel) == "voxelPhysics")
                    continue;

                try
                {
                    var voxelAabb = voxel.PositionComp.WorldAABB;
                    if (!voxelAabb.Intersects(samplingAabb))
                        continue;

                    var intersection = Intersect(voxelAabb, samplingAabb);
                    if (!TryWorldAabbToStorageRange(voxel, intersection, out var storageMin, out var storageMax))
                        continue;

                    var bodyName = FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.EntityId.ToString());
                    samples.Add(new VoxelSceneSample
                    {
                        Voxel = voxel,
                        BodyName = bodyName,
                        StorageMin = storageMin,
                        StorageMax = storageMax,
                    });
                }
                catch (Exception exception)
                {
                    warnings.Add("Failed to prepare voxel body " + FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.EntityId.ToString()) + ": " + exception.Message);
                }
            }

            var lod = ChooseVoxelSceneLod(samples);
            if (lod > 0)
                warnings.Add("Sampling grid voxel data at LOD " + lod + " to fit viewer payload limits.");

            foreach (var sample in samples)
            {
                try
                {
                    var voxel = sample.Voxel;
                    var bodyName = sample.BodyName;
                    var storageMin = sample.StorageMin >> lod;
                    var storageMax = sample.StorageMax >> lod;

                    var bodyRequiredBytes = RangeSampleCountLong(storageMin, storageMax) * 2L;
                    if (bodyRequiredBytes <= MaxVoxelDataBytes - totalBytes)
                    {
                        var bodyContentState = ClassifyVoxelChunk(voxel, storageMin, storageMax, lod);
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

                        var bodyChunk = TryBuildVoxelDataChunk(voxel, storageMin, storageMax, bodyContentState, warnings, lod);
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

                        var contentState = ClassifyVoxelChunk(voxel, chunkRange.Min, chunkRange.Max, lod);
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

                        var requiredBytes = RangeSampleCountLong(chunkRange.Min, chunkRange.Max) * 3L;
                        if (totalBytes + requiredBytes > MaxVoxelDataBytes)
                        {
                            warnings.Add("Voxel data safety limit exceeded; remaining voxel chunks were skipped.");
                            return result;
                        }

                        var chunk = TryBuildVoxelDataChunk(voxel, chunkRange.Min, chunkRange.Max, contentState, warnings, lod);
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
                    warnings.Add("Failed to sample voxel body " + sample.BodyName + ": " + exception.Message);
                }
            }

            if (chunkBudgetReached)
                warnings.Add("Voxel data chunk budget reached; remaining voxel chunks were skipped.");

            return result;
        }

        private static int ChooseVoxelSceneLod(List<VoxelSceneSample> samples)
        {
            var lod = 0;
            while (lod < MaxVoxelBodyLod)
            {
                var chunks = 0L;
                var bytes = 0L;
                foreach (var sample in samples)
                {
                    var min = sample.StorageMin >> lod;
                    var max = sample.StorageMax >> lod;
                    chunks += RangeChunkCount(min, max, DefaultVoxelChunkSize);
                    bytes += RangeSampleCountLong(min, max) * 2L;
                    if (chunks > MaxVoxelSceneChunks || bytes > MaxVoxelDataBytes)
                        break;
                }

                if (chunks <= MaxVoxelSceneChunks && bytes <= MaxVoxelDataBytes)
                    break;

                lod++;
            }

            return lod;
        }

        private static List<ViewerVoxelDataChunk> BuildVoxelBodyDeformations(MyVoxelBase voxel, List<string> warnings)
        {
            var result = new List<ViewerVoxelDataChunk>();
            var totalBytes = 0;
            var skippedEmpty = 0;
            var skippedFull = 0;
            var bodyName = FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.StorageName, voxel.EntityId.ToString());
            var lod = ChooseVoxelBodyLod(voxel);
            var lodStorageMin = voxel.StorageMin >> lod;
            var lodStorageMax = voxel.StorageMax >> lod;
            if (lod > 0)
                warnings.Add("Sampling voxel body " + bodyName + " at LOD " + lod + " to fit viewer payload limits.");

            foreach (var chunkRange in SplitStorageRange(lodStorageMin, lodStorageMax, DefaultVoxelChunkSize))
            {
                try
                {
                    if (result.Count >= MaxVoxelBodySceneChunks)
                    {
                        warnings.Add("Voxel data chunk budget reached for " + bodyName + "; remaining chunks were skipped.");
                        break;
                    }

                    var contentState = ClassifyVoxelChunk(voxel, chunkRange.Min, chunkRange.Max, lod);
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
                    if (totalBytes + requiredBytes > MaxVoxelBodyDataBytes)
                    {
                        warnings.Add("Voxel data safety limit reached for " + bodyName + "; remaining chunks were skipped.");
                        break;
                    }

                    var chunk = TryBuildVoxelDataChunk(voxel, chunkRange.Min, chunkRange.Max, contentState, warnings, lod);
                    if (chunk == null)
                        continue;

                    totalBytes += chunk.Content.Length + chunk.Materials.Length;
                    result.Add(chunk);
                }
                catch (Exception exception)
                {
                    warnings.Add("Failed to sample voxel chunk " + ChunkId(chunkRange.Min) + " for " + bodyName + ": " + exception.Message);
                }
            }

            if (skippedEmpty > 0 || skippedFull > 0)
                warnings.Add("Skipped " + skippedEmpty + " empty and " + skippedFull + " full voxel data chunks for " + bodyName + ".");

            return result;
        }

        private static List<ViewerVoxelDataChunk> BuildVoxelDamageDeformations(List<ViewerVoxelDataChunk> sourceChunks, List<string> warnings)
        {
            var result = new List<ViewerVoxelDataChunk>();
            var totalBytes = 0;
            var chunkBudgetReached = false;
            var sourceVoxelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourceChunk in sourceChunks)
            {
                if (sourceChunk == null || string.IsNullOrEmpty(sourceChunk.VoxelBodyId))
                    continue;
                sourceVoxelIds.Add(sourceChunk.VoxelBodyId);
            }

            if (sourceVoxelIds.Count == 0)
                return result;

            var voxelsById = new Dictionary<string, MyVoxelBase>(StringComparer.Ordinal);
            var session = MySession.Static;
            if (session?.VoxelMaps?.Instances != null)
            {
                foreach (var voxel in session.VoxelMaps.Instances)
                {
                    if (voxel == null || voxel.MarkedForClose || voxel.Closed || VoxelKind(voxel) == "voxelPhysics")
                        continue;
                    var id = voxel.EntityId.ToString();
                    if (sourceVoxelIds.Contains(id))
                        voxelsById[id] = voxel;
                }
            }

            foreach (var sourceChunk in sourceChunks)
            {
                if (sourceChunk == null || !voxelsById.TryGetValue(sourceChunk.VoxelBodyId, out var voxel))
                    continue;

                var lod = Math.Max(0, sourceChunk.Lod);
                var sourceMin = ToVector3I(sourceChunk.StorageMin);
                var sourceMax = ToVector3I(sourceChunk.StorageMax);
                foreach (var chunkRange in SplitStorageRange(sourceMin, sourceMax, DamageVoxelChunkSize))
                {
                    try
                    {
                        if (result.Count >= MaxVoxelDamageSceneChunks)
                        {
                            chunkBudgetReached = true;
                            break;
                        }

                        var contentState = ClassifyVoxelChunk(voxel, chunkRange.Min, chunkRange.Max, lod);
                        if (contentState == "empty" || contentState == "full")
                            continue;

                        var requiredBytes = RangeSampleCountLong(chunkRange.Min, chunkRange.Max) * 2L;
                        if (totalBytes + requiredBytes > MaxVoxelDamageDataBytes)
                        {
                            warnings.Add("Voxel damage data safety limit exceeded; remaining voxel damage chunks were skipped.");
                            return result;
                        }

                        var chunk = TryBuildVoxelDamageDataChunk(voxel, chunkRange.Min, chunkRange.Max, contentState, warnings, lod);
                        if (chunk == null)
                            continue;

                        chunk.IsModified = true;
                        totalBytes += chunk.Content.Length + chunk.Materials.Length;
                        result.Add(chunk);
                    }
                    catch (Exception exception)
                    {
                        warnings.Add("Failed to sample voxel damage chunk " + ChunkId(chunkRange.Min) + " for " + FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.EntityId.ToString()) + ": " + exception.Message);
                    }
                }

                if (chunkBudgetReached)
                    break;
            }

            if (chunkBudgetReached)
                warnings.Add("Voxel damage chunk budget reached; remaining voxel damage chunks were skipped.");

            return result;
        }

        private static int ChooseVoxelBodyLod(MyVoxelBase voxel)
        {
            var lod = 0;
            while (lod < MaxVoxelBodyLod)
            {
                var min = voxel.StorageMin >> lod;
                var max = voxel.StorageMax >> lod;
                var chunks = RangeChunkCount(min, max, DefaultVoxelChunkSize);
                var bytes = RangeSampleCountLong(min, max) * 2L;
                if (chunks <= MaxVoxelBodySceneChunks && bytes <= MaxVoxelBodyDataBytes)
                    break;

                lod++;
            }

            return lod;
        }

        private static long RangeChunkCount(Vector3I min, Vector3I max, int chunkSize)
        {
            return AxisChunkCount(min.X, max.X, chunkSize)
                   * AxisChunkCount(min.Y, max.Y, chunkSize)
                   * AxisChunkCount(min.Z, max.Z, chunkSize);
        }

        private static long AxisChunkCount(int min, int max, int chunkSize)
        {
            return max >= min ? ((long)max - min) / chunkSize + 1L : 0L;
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
            return ClassifyVoxelChunk(voxel, min, max, VoxelLod);
        }

        private static string ClassifyVoxelChunk(MyVoxelBase voxel, Vector3I min, Vector3I max, int lod)
        {
            var data = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
            data.Resize(min, max);
            var flags = MyVoxelRequestFlags.ConsiderContent;
            voxel.Storage.ReadRange(data, MyStorageDataTypeFlags.Content, lod, min, max, ref flags);

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
            return TryBuildVoxelDataChunk(voxel, min, max, contentState, warnings, VoxelLod);
        }

        private static ViewerVoxelDataChunk TryBuildVoxelDataChunk(
            MyVoxelBase voxel,
            Vector3I min,
            Vector3I max,
            string contentState,
            List<string> warnings,
            int lod)
        {
            try
            {
                var data = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
                data.Resize(min, max);
                var flags = MyVoxelRequestFlags.ConsiderContent | MyVoxelRequestFlags.SurfaceMaterial;
                voxel.Storage.ReadRange(data, MyStorageDataTypeFlags.ContentAndMaterial, lod, min, max, ref flags);
                return CopyVoxelDataChunk(voxel, min, max, contentState, data, lod);
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to sample voxel chunk " + ChunkId(min) + " at LOD " + lod + " for " + FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.EntityId.ToString()) + ": " + exception.Message);
                return null;
            }
        }

        private static ViewerVoxelDataChunk TryBuildVoxelDamageDataChunk(
            MyVoxelBase voxel,
            Vector3I min,
            Vector3I max,
            string contentState,
            List<string> warnings,
            int lod)
        {
            try
            {
                var provider = voxel?.Storage?.DataProvider;
                if (provider == null)
                    return null;

                var current = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
                current.Resize(min, max);
                var currentFlags = MyVoxelRequestFlags.ConsiderContent | MyVoxelRequestFlags.SurfaceMaterial;
                voxel.Storage.ReadRange(current, MyStorageDataTypeFlags.ContentAndMaterial, lod, min, max, ref currentFlags);

                var original = new MyStorageData(MyStorageDataTypeFlags.ContentAndMaterial);
                original.Resize(min, max);
                var offset = Vector3I.Zero;
                var providerMin = min;
                var providerMax = max;
                provider.ReadRange(original, MyStorageDataTypeFlags.ContentAndMaterial, ref offset, lod, ref providerMin, ref providerMax);

                var modified = BuildVoxelModifiedMask(current, original);
                if (modified == null)
                    return null;

                var chunk = CopyVoxelDataChunk(voxel, min, max, contentState, current, lod);
                chunk.IsModified = true;
                chunk.Modified = modified;
                return chunk;
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to sample voxel damage chunk " + ChunkId(min) + " at LOD " + lod + " for " + FirstNonEmpty(voxel.DisplayName, voxel.Name, voxel.EntityId.ToString()) + ": " + exception.Message);
                return null;
            }
        }

        private static byte[] BuildVoxelModifiedMask(MyStorageData current, MyStorageData original)
        {
            var currentContent = current[MyStorageDataTypeEnum.Content];
            var originalContent = original[MyStorageDataTypeEnum.Content];
            var currentMaterials = current[MyStorageDataTypeEnum.Material];
            var originalMaterials = original[MyStorageDataTypeEnum.Material];
            var length = current.Size3D.Size;
            var modified = new byte[length];
            var hasModified = false;
            for (var index = 0; index < length; index++)
            {
                if (currentContent[index] == originalContent[index] && currentMaterials[index] == originalMaterials[index])
                    continue;

                modified[index] = byte.MaxValue;
                hasModified = true;
            }

            return hasModified ? modified : null;
        }

        private static ViewerVoxelDataChunk CopyVoxelDataChunk(MyVoxelBase voxel, Vector3I min, Vector3I max, string contentState, MyStorageData data)
        {
            return CopyVoxelDataChunk(voxel, min, max, contentState, data, VoxelLod);
        }

        private static ViewerVoxelDataChunk CopyVoxelDataChunk(MyVoxelBase voxel, Vector3I min, Vector3I max, string contentState, MyStorageData data, int lod)
        {
            var worldMatrix = VoxelWorldMatrix(voxel);
            var size = data.Size3D;
            return new ViewerVoxelDataChunk
            {
                VoxelBodyId = voxel.EntityId.ToString(),
                ChunkId = lod == 0 ? ChunkId(min) : "lod" + lod + ":" + ChunkId(min),
                Lod = lod,
                StorageMin = ToDto(min),
                StorageMax = ToDto(max),
                WorldAabb = ToDto(StorageRangeWorldAabb(worldMatrix, min, max, lod)),
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

        private static long RangeSampleCountLong(Vector3I min, Vector3I max)
        {
            return max.X >= min.X && max.Y >= min.Y && max.Z >= min.Z
                ? ((long)max.X - min.X + 1L) * ((long)max.Y - min.Y + 1L) * ((long)max.Z - min.Z + 1L)
                : 0L;
        }

        private static BoundingBoxD VoxelSamplingWorldAabb(MyCubeGrid grid)
        {
            if (!TryGridLocalBlockBounds(grid, out var bounds, out var minCell, out var maxCell))
                return grid.PositionComp.WorldAABB;

            var gridSize = GridCubeSize(grid);
            var padding = gridSize * FloorGridPaddingSupersquares;
            var offsetX = FloorAxisOffset(maxCell.X - minCell.X + 1, gridSize);
            var offsetZ = FloorAxisOffset(maxCell.Z - minCell.Z + 1, gridSize);
            var startXCell = (int)Math.Floor((bounds.Min.X - padding - offsetX) / SmallGridCubeSize);
            var endXCell = (int)Math.Ceiling((bounds.Max.X + padding - offsetX) / SmallGridCubeSize);
            var startZCell = (int)Math.Floor((bounds.Min.Z - padding - offsetZ) / SmallGridCubeSize);
            var endZCell = (int)Math.Ceiling((bounds.Max.Z + padding - offsetZ) / SmallGridCubeSize);

            var localMin = new Vector3D(
                offsetX + startXCell * SmallGridCubeSize,
                bounds.Min.Y - padding,
                offsetZ + startZCell * SmallGridCubeSize);
            var localMax = new Vector3D(
                offsetX + endXCell * SmallGridCubeSize,
                bounds.Max.Y + padding,
                offsetZ + endZCell * SmallGridCubeSize);
            return LocalAabbToWorldAabb(localMin, localMax, grid.WorldMatrix);
        }

        private static bool TryGridLocalBlockBounds(MyCubeGrid grid, out BoundingBoxD bounds, out Vector3I minCell, out Vector3I maxCell)
        {
            bounds = BoundingBoxD.CreateInvalid();
            minCell = new Vector3I(int.MaxValue);
            maxCell = new Vector3I(int.MinValue);
            var hasBlocks = false;
            var gridSize = GridCubeSize(grid);
            var half = gridSize * 0.5;

            foreach (var block in grid.CubeBlocks)
            {
                if (block == null)
                    continue;

                hasBlocks = true;
                minCell = Vector3I.Min(minCell, block.Min);
                maxCell = Vector3I.Max(maxCell, block.Max);
                bounds.Include(new Vector3D(block.Min.X * gridSize - half, block.Min.Y * gridSize - half, block.Min.Z * gridSize - half));
                bounds.Include(new Vector3D(block.Max.X * gridSize + half, block.Max.Y * gridSize + half, block.Max.Z * gridSize + half));
            }

            return hasBlocks;
        }

        private static double GridCubeSize(MyCubeGrid grid)
        {
            var size = grid.GridSize;
            if (size > 0f && size.IsValid())
                return size;

            return grid.GridSizeEnum == MyCubeSize.Small ? SmallGridCubeSize : LargeGridCubeSize;
        }

        private static double FloorAxisOffset(int cellCount, double gridSize)
        {
            return Math.Abs(cellCount % 2) == 1 ? gridSize * 0.5 : 0;
        }

        private static BoundingBoxD LocalAabbToWorldAabb(Vector3D localMin, Vector3D localMax, MatrixD worldMatrix)
        {
            var bounds = BoundingBoxD.CreateInvalid();
            foreach (var corner in LocalAabbCorners(localMin, localMax))
                bounds.Include(Vector3D.Transform(corner, worldMatrix));
            return bounds;
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
            return StorageRangeWorldAabb(worldMatrix, min, max, VoxelLod);
        }

        private static BoundingBoxD StorageRangeWorldAabb(MatrixD worldMatrix, Vector3I min, Vector3I max, int lod)
        {
            var bounds = BoundingBoxD.CreateInvalid();
            var scale = 1 << lod;
            var localMin = new Vector3D(min.X * scale, min.Y * scale, min.Z * scale);
            var localMax = new Vector3D((max.X + 1) * scale, (max.Y + 1) * scale, (max.Z + 1) * scale);
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
            var modelAssetId = catalog.RegisterModel(definition.Model, definition.Context);
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
                OpaqueFaceMask = OpaqueGeneratedCubeFaceMask(definition),
            };

            if (definition.BuildProgressModels != null)
            {
                foreach (var buildModel in definition.BuildProgressModels)
                {
                    var id = catalog.RegisterModel(buildModel.File, definition.Context);
                    if (!string.IsNullOrEmpty(id))
                        dto.BuildProgressModelAssetIds.Add(id);
                }
            }

            if (!string.IsNullOrEmpty(definition.Model) && string.IsNullOrEmpty(modelAssetId))
                warnings.Add("Block definition " + dto.Id + " has no model logical path.");

            return dto;
        }

        private static ViewerGridLogistics BuildLogistics(IEnumerable<MyCubeGrid> grids, List<ViewerBlockInstance> instances, List<string> warnings)
        {
            var logistics = new ViewerGridLogistics();
            var usableGrids = grids
                .Where(candidate => candidate != null && !candidate.MarkedForClose && !candidate.Closed)
                .GroupBy(candidate => candidate.EntityId)
                .Select(group => group.First())
                .ToList();
            var nodesByEndpoint = new Dictionary<IMyConveyorEndpoint, ViewerLogisticsNode>();
            foreach (var grid in usableGrids)
                AddGridLogisticsNodes(logistics, grid, instances, nodesByEndpoint, warnings);

            var seenLines = new HashSet<MyConveyorLine>();
            foreach (var grid in usableGrids)
                AddGridLogisticsEdges(logistics, grid, nodesByEndpoint, seenLines, warnings);

            AssignLogisticsSystems(logistics, ReachableLogisticsLinks(nodesByEndpoint, warnings));
            return logistics;
        }

        private static void AddGridLogisticsNodes(
            ViewerGridLogistics logistics,
            MyCubeGrid grid,
            List<ViewerBlockInstance> instances,
            Dictionary<IMyConveyorEndpoint, ViewerLogisticsNode> nodesByEndpoint,
            List<string> warnings)
        {
            var gridId = grid.EntityId.ToString();
            var instancesByBlockId = instances
                .Where(instance => string.Equals(instance.GridId, gridId, StringComparison.Ordinal))
                .Where(instance => !string.IsNullOrEmpty(instance.Id))
                .ToDictionary(instance => instance.Id, instance => instance, StringComparer.Ordinal);
            var instancesByCell = new Dictionary<Vector3I, ViewerBlockInstance>();
            foreach (var instance in instances.Where(instance => string.Equals(instance.GridId, gridId, StringComparison.Ordinal)))
                instancesByCell[new Vector3I(instance.Cell.X, instance.Cell.Y, instance.Cell.Z)] = instance;

            var endpointBlocks = grid.GridSystems?.ConveyorSystem?.ConveyorEndpointBlocks;
            if (endpointBlocks == null)
                return;

            try
            {
                foreach (var endpointBlock in endpointBlocks.Value)
                {
                    var endpoint = endpointBlock?.ConveyorEndpoint;
                    var fatBlock = endpoint?.CubeBlock ?? endpointBlock as MyCubeBlock;
                    var slimBlock = fatBlock?.SlimBlock;
                    if (endpoint == null || fatBlock == null || slimBlock == null || fatBlock.CubeGrid != grid)
                        continue;

                    var blockId = fatBlock.EntityId.ToString();
                    if (!instancesByBlockId.TryGetValue(blockId, out var instance) && !instancesByCell.TryGetValue(slimBlock.Position, out instance))
                        continue;

                    var node = new ViewerLogisticsNode
                    {
                        Id = instance.Id,
                        GridId = gridId,
                        BlockId = instance.Id,
                        BlockTypeId = instance.BlockTypeId,
                        Role = LogisticsRole(fatBlock, slimBlock.BlockDefinition),
                        Cell = instance.Cell,
                        Min = instance.Min,
                        Max = instance.Max,
                        IsWorking = (fatBlock as MyFunctionalBlock)?.IsWorking ?? true,
                        HasInventory = fatBlock.HasInventory,
                        InventoryCount = fatBlock.InventoryCount,
                        ConveyorPortCount = endpoint.GetLineCount(),
                    };

                    logistics.Nodes.Add(node);
                    nodesByEndpoint[endpoint] = node;
                }
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to inspect grid logistics nodes: " + exception.Message);
            }
        }

        private static void AddGridLogisticsEdges(
            ViewerGridLogistics logistics,
            MyCubeGrid grid,
            Dictionary<IMyConveyorEndpoint, ViewerLogisticsNode> nodesByEndpoint,
            HashSet<MyConveyorLine> seenLines,
            List<string> warnings)
        {
            var gridId = grid.EntityId.ToString();
            var endpointBlocks = grid.GridSystems?.ConveyorSystem?.ConveyorEndpointBlocks;
            if (endpointBlocks == null)
                return;

            try
            {
                foreach (var endpointBlock in endpointBlocks.Value)
                {
                    var endpoint = endpointBlock?.ConveyorEndpoint;
                    if (endpoint == null || !nodesByEndpoint.ContainsKey(endpoint))
                        continue;

                    for (var i = 0; i < endpoint.GetLineCount(); i++)
                    {
                        var line = endpoint.GetConveyorLine(i);
                        if (line == null || !seenLines.Add(line))
                            continue;

                        var endpointA = line.GetEndpoint(0);
                        var endpointB = line.GetEndpoint(1);
                        ViewerLogisticsNode fromNode = null;
                        ViewerLogisticsNode toNode = null;
                        var hasEndpointA = endpointA != null && nodesByEndpoint.TryGetValue(endpointA, out fromNode);
                        var hasEndpointB = endpointB != null && nodesByEndpoint.TryGetValue(endpointB, out toNode);
                        if (!hasEndpointA && !hasEndpointB)
                            continue;

                        var fromIndex = hasEndpointA ? 0 : 1;
                        var toIndex = hasEndpointA ? 1 : 0;
                        var sourceNode = hasEndpointA ? fromNode : toNode;
                        var targetEndpoint = hasEndpointA ? endpointB : endpointA;
                        var targetNode = hasEndpointA && hasEndpointB ? toNode : null;
                        var from = LogisticsLinePosition(grid, line, fromIndex, sourceNode);
                        var to = LogisticsLineEndpointPosition(grid, line, toIndex, from);
                        var path = LogisticsLinePath(grid, line, from, to, fromIndex);
                        var isSmall = line.Type == MyObjectBuilder_ConveyorLine.LineType.SMALL_LINE;
                        logistics.Edges.Add(new ViewerLogisticsEdge
                        {
                            Id = gridId + ":line:" + logistics.Edges.Count,
                            GridId = gridId,
                            FromNodeId = sourceNode.Id,
                            ToNodeId = targetNode?.Id ?? string.Empty,
                            LineType = isSmall ? "small" : line.Type == MyObjectBuilder_ConveyorLine.LineType.LARGE_LINE ? "large" : "unknown",
                            IsSmallRestricted = isSmall,
                            IsDangling = targetEndpoint == null,
                            IsWorking = line.IsWorking,
                            From = ToDto(from),
                            To = ToDto(to),
                            Path = path.Select(ToDto).ToList(),
                        });
                    }
                }
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to inspect grid logistics edges: " + exception.Message);
            }
        }

        private static List<KeyValuePair<string, string>> ReachableLogisticsLinks(Dictionary<IMyConveyorEndpoint, ViewerLogisticsNode> nodesByEndpoint, List<string> warnings)
        {
            var links = new List<KeyValuePair<string, string>>();
            var endpoints = nodesByEndpoint.Keys.ToList();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reachable = new List<IMyConveyorEndpoint>();
            foreach (var endpoint in endpoints)
            {
                if (endpoint == null || !nodesByEndpoint.TryGetValue(endpoint, out var sourceNode))
                    continue;

                reachable.Clear();
                try
                {
                    MyGridConveyorSystem.FindReachable(endpoint, reachable, candidate => candidate != null && nodesByEndpoint.ContainsKey(candidate));
                }
                catch (Exception exception)
                {
                    warnings.Add("Failed to inspect logistics conveyor reachability: " + exception.Message);
                    continue;
                }

                foreach (var target in reachable)
                {
                    if (target == null || !nodesByEndpoint.TryGetValue(target, out var targetNode) || string.Equals(sourceNode.Id, targetNode.Id, StringComparison.Ordinal))
                        continue;

                    var first = string.CompareOrdinal(sourceNode.Id, targetNode.Id) <= 0 ? sourceNode.Id : targetNode.Id;
                    var second = string.Equals(first, sourceNode.Id, StringComparison.Ordinal) ? targetNode.Id : sourceNode.Id;
                    var key = first + "\n" + second;
                    if (seen.Add(key))
                        links.Add(new KeyValuePair<string, string>(first, second));
                }
            }

            return links;
        }

        private static string LogisticsRole(MyCubeBlock block, MyCubeBlockDefinition definition)
        {
            if (block is MyConveyorSorter)
                return "sorter";
            if (block is MyShipConnector)
                return "connector";
            if (block is MyCargoContainer)
                return "storage";
            if (block is MyProductionBlock)
                return "production";
            if (block is MyReactor || block is MyFueledPowerProducer)
                return "power";
            if (block is MyGasTank || block is MyGasGenerator || block is MyAirVent || block is MyOxygenFarm)
                return "gas";
            if (block is MyConveyor)
                return "conveyor";

            var text = ((definition?.Id.ToString() ?? string.Empty) + " " + block.GetType().Name).ToLowerInvariant();
            if (text.Contains("sorter")) return "sorter";
            if (text.Contains("connector")) return "connector";
            if (text.Contains("cargo") || text.Contains("container")) return "storage";
            if (text.Contains("assembler") || text.Contains("refinery") || text.Contains("survival")) return "production";
            if (text.Contains("reactor") || text.Contains("hydrogenengine") || text.Contains("powerproducer")) return "power";
            if (text.Contains("gastank") || text.Contains("oxygengenerator") || text.Contains("airvent") || text.Contains("oxygenfarm")) return "gas";
            if (text.Contains("turret") || text.Contains("gatling") || text.Contains("missile") || text.Contains("weapon")) return "weapon";
            if (text.Contains("drill") || text.Contains("welder") || text.Contains("grinder")) return "tool";
            if (text.Contains("conveyor")) return "conveyor";
            return "other";
        }

        private static Vector3 LogisticsLinePosition(MyCubeGrid grid, MyConveyorLine line, int endpointIndex, ViewerLogisticsNode fallbackNode)
        {
            try
            {
                return LogisticsLineEndpointPosition(grid, line, endpointIndex, LogisticsNodeCenter(fallbackNode, grid.GridSize));
            }
            catch
            {
                return LogisticsNodeCenter(fallbackNode, grid.GridSize);
            }
        }

        private static Vector3 LogisticsLineEndpointPosition(MyCubeGrid grid, MyConveyorLine line, int endpointIndex, Vector3 fallback)
        {
            try
            {
                var position = line.GetEndpointPosition(endpointIndex);
                var direction = position.VectorDirection;
                return new Vector3(
                    (position.LocalGridPosition.X + direction.X * 0.5f) * grid.GridSize,
                    (position.LocalGridPosition.Y + direction.Y * 0.5f) * grid.GridSize,
                    (position.LocalGridPosition.Z + direction.Z * 0.5f) * grid.GridSize);
            }
            catch
            {
                return fallback;
            }
        }

        private static List<Vector3> LogisticsLinePath(MyCubeGrid grid, MyConveyorLine line, Vector3 from, Vector3 to, int fromIndex)
        {
            var path = new List<Vector3>();
            AddLogisticsPathPoint(path, fromIndex == 0 ? from : to);
            try
            {
                foreach (var cell in line)
                    AddLogisticsPathPoint(path, LogisticsCellCenter(grid, cell));
            }
            catch
            {
                path.Clear();
                AddLogisticsPathPoint(path, fromIndex == 0 ? from : to);
            }

            AddLogisticsPathPoint(path, fromIndex == 0 ? to : from);
            if (fromIndex == 1)
                path.Reverse();
            return SimplifyLogisticsPath(path);
        }

        private static Vector3 LogisticsCellCenter(MyCubeGrid grid, Vector3I cell)
        {
            return new Vector3(cell.X * grid.GridSize, cell.Y * grid.GridSize, cell.Z * grid.GridSize);
        }

        private static void AddLogisticsPathPoint(List<Vector3> path, Vector3 point)
        {
            if (path.Count > 0 && Vector3.DistanceSquared(path[path.Count - 1], point) < 0.0001f)
                return;
            path.Add(point);
        }

        private static List<Vector3> SimplifyLogisticsPath(List<Vector3> path)
        {
            if (path.Count <= 2)
                return path;

            var simplified = new List<Vector3> { path[0] };
            for (var i = 1; i < path.Count - 1; i++)
            {
                var previous = simplified[simplified.Count - 1];
                var current = path[i];
                var next = path[i + 1];
                var a = current - previous;
                var b = next - current;
                if (a.LengthSquared() > 0.0001f && b.LengthSquared() > 0.0001f)
                {
                    a.Normalize();
                    b.Normalize();
                    if (Vector3.DistanceSquared(a, b) < 0.0001f)
                        continue;
                }

                simplified.Add(current);
            }

            AddLogisticsPathPoint(simplified, path[path.Count - 1]);
            return simplified;
        }

        private static Vector3 LogisticsNodeCenter(ViewerLogisticsNode node, float gridSize)
        {
            return new Vector3(
                (node.Min.X + node.Max.X) * 0.5f * gridSize,
                (node.Min.Y + node.Max.Y) * 0.5f * gridSize,
                (node.Min.Z + node.Max.Z) * 0.5f * gridSize);
        }

        private static void AssignLogisticsSystems(ViewerGridLogistics logistics, IEnumerable<KeyValuePair<string, string>> extraLinks)
        {
            var nodes = logistics.Nodes.ToDictionary(node => node.Id, node => node, StringComparer.Ordinal);
            var adjacency = nodes.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var edge in logistics.Edges)
            {
                if (!edge.IsWorking)
                    continue;

                if (!adjacency.ContainsKey(edge.FromNodeId) || !adjacency.ContainsKey(edge.ToNodeId))
                    continue;

                adjacency[edge.FromNodeId].Add(edge.ToNodeId);
                adjacency[edge.ToNodeId].Add(edge.FromNodeId);
            }

            if (extraLinks != null)
            {
                foreach (var link in extraLinks)
                {
                    if (!adjacency.ContainsKey(link.Key) || !adjacency.ContainsKey(link.Value))
                        continue;

                    adjacency[link.Key].Add(link.Value);
                    adjacency[link.Value].Add(link.Key);
                }
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var systemId = 0;
            foreach (var node in logistics.Nodes)
            {
                if (!visited.Add(node.Id))
                    continue;

                var queue = new Queue<string>();
                queue.Enqueue(node.Id);
                node.SystemId = systemId;
                var nodeCount = 0;
                while (queue.Count > 0)
                {
                    var id = queue.Dequeue();
                    nodeCount++;
                    foreach (var next in adjacency[id])
                    {
                        if (!visited.Add(next))
                            continue;
                        nodes[next].SystemId = systemId;
                        queue.Enqueue(next);
                    }
                }

                var edgeCount = 0;
                var hasSmall = false;
                foreach (var edge in logistics.Edges)
                {
                    if (!nodes.TryGetValue(edge.FromNodeId, out var fromNode) || fromNode.SystemId != systemId)
                        continue;
                    edge.SystemId = systemId;
                    edgeCount++;
                    hasSmall |= edge.IsSmallRestricted;
                }

                logistics.Systems.Add(new ViewerLogisticsSystem
                {
                    Id = systemId,
                    NodeCount = nodeCount,
                    EdgeCount = edgeCount,
                    HasSmallRestrictedEdges = hasSmall,
                });
                systemId++;
            }
        }

        private static ViewerBlockInstance ToBlockInstance(
            MyCubeGrid grid,
            MySlimBlock block,
            string definitionId,
            string chunkId,
            Dictionary<Vector3I, MySlimBlock> occupancy,
            MetadataAssetCatalog catalog,
            List<string> warnings,
            out bool fullyCulledGeneratedBlock)
        {
            fullyCulledGeneratedBlock = false;
            block.GetLocalMatrix(out var localMatrix);
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
                AccumulatedDamage = block.AccumulatedDamage,
                MaxIntegrity = block.MaxIntegrity,
                CurrentModelLocalMatrix = ToDto(localMatrix),
            };

            var generatedPartStats = AddGeneratedBlockModelParts(grid, block, dto, occupancy, catalog, warnings);
            fullyCulledGeneratedBlock = generatedPartStats.Total > 0 && generatedPartStats.Culled == generatedPartStats.Total;
            if (fullyCulledGeneratedBlock)
                return dto;

            try
            {
                var currentModel = block.CalculateCurrentModel(out var currentModelOrientation);
                currentModelOrientation.Translation = localMatrix.Translation;
                dto.CurrentModelLocalMatrix = ToDto(currentModelOrientation);
                dto.CurrentModelAssetId = catalog.RegisterModel(currentModel, block.BlockDefinition.Context) ?? string.Empty;
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to resolve current model for block " + block.Position + ": " + exception.Message);
            }

            AddBlockDeformations(grid, block, dto, warnings);
            AddRuntimeSubparts(grid, block, dto, catalog, warnings);
            RefreshEmissiveParts(block, warnings);
            AddEmissiveParts(block, dto);
            AddSkinTextureChanges(block, dto);
            AddLcdMaterialsToHideWhenOffline(block, dto);
            AddLcdSurfaces(block, dto, catalog, warnings);
            return dto;
        }

        private static void RefreshEmissiveParts(MySlimBlock block, List<string> warnings)
        {
            var fatBlock = block.FatBlock;
            if (fatBlock == null)
                return;

            RegisterEmissiveRenderObjectIds(fatBlock);
        }

        private static void RegisterEmissiveRenderObjectIds(MyEntity entity)
        {
            EmissivePartCaptureCache.RegisterRenderObjectIds(entity);
            if (entity?.Subparts == null)
                return;

            foreach (var subpart in entity.Subparts.Values)
            {
                if (subpart != null)
                    RegisterEmissiveRenderObjectIds(subpart);
            }
        }

        private static void AddEmissiveParts(MySlimBlock block, ViewerBlockInstance dto)
        {
            var fatBlock = block.FatBlock;
            if (fatBlock == null)
                return;

            AddEmissivePartsForEntity(fatBlock, dto);
            foreach (var subpart in dto.Subparts)
            {
                if (long.TryParse(subpart.EntityId, out var entityId))
                    AddEmissivePartsForEntity(entityId, dto);
            }
        }

        private static void AddEmissivePartsForEntity(MyEntity entity, ViewerBlockInstance dto)
        {
            foreach (var part in EmissivePartCaptureCache.ForEntity(entity))
                dto.EmissiveParts.Add(ToDto(part));
        }

        private static void AddEmissivePartsForEntity(long entityId, ViewerBlockInstance dto)
        {
            foreach (var part in EmissivePartCaptureCache.ForEntityId(entityId))
                dto.EmissiveParts.Add(ToDto(part));
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

        private struct GeneratedModelPartStats
        {
            public int Total;
            public int Culled;
        }

        private static Dictionary<Vector3I, MySlimBlock> BuildBlockOccupancy(MyCubeGrid grid)
        {
            var occupancy = new Dictionary<Vector3I, MySlimBlock>(Vector3I.Comparer);
            foreach (var block in grid.CubeBlocks)
            {
                if (block == null || block.BlockDefinition == null)
                    continue;

                for (var x = block.Min.X; x <= block.Max.X; x++)
                for (var y = block.Min.Y; y <= block.Max.Y; y++)
                for (var z = block.Min.Z; z <= block.Max.Z; z++)
                    occupancy[new Vector3I(x, y, z)] = block;
            }

            return occupancy;
        }

        private static bool ShouldCullGeneratedModelPart(
            MySlimBlock block,
            Vector3 normal,
            Dictionary<Vector3I, MySlimBlock> occupancy)
        {
            if (block.BlockDefinition == null || block.BlockDefinition.Size != Vector3I.One || block.Min != block.Max)
                return false;

            var faceBit = FaceBitFromNormal(normal);
            if (!faceBit.HasValue || BlockHasDeformation(block))
                return false;

            var adjacentCell = block.Position + FaceNormal(faceBit.Value);
            if (!occupancy.TryGetValue(adjacentCell, out var neighbour) || neighbour == null || neighbour == block)
                return false;
            if (BlockHasDeformation(neighbour))
                return false;

            if (!neighbour.ShowParts)
                return false;

            var neighbourMask = OpaqueGeneratedCubeFaceMaskAtCell(neighbour, adjacentCell);
            return (neighbourMask & (1 << OppositeFaceBit(faceBit.Value))) != 0;
        }

        private static GeneratedModelPartStats AddGeneratedBlockModelParts(
            MyCubeGrid grid,
            MySlimBlock block,
            ViewerBlockInstance dto,
            Dictionary<Vector3I, MySlimBlock> occupancy,
            MetadataAssetCatalog catalog,
            List<string> warnings)
        {
            var stats = new GeneratedModelPartStats();
            if (block.BlockDefinition.CubeDefinition == null)
                return stats;
            if (!block.ShowParts)
                return stats;

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
                    stats.Total++;
                    if (ShouldCullGeneratedModelPart(block, cubePartNormals[i], occupancy))
                    {
                        stats.Culled++;
                        continue;
                    }

                    var modelAssetId = catalog.RegisterModel(cubePartModels[i], block.BlockDefinition.Context);
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

            return stats;
        }

        private static void AddBlockDeformations(
            MyCubeGrid grid,
            MySlimBlock block,
            ViewerBlockInstance dto,
            List<string> warnings)
        {
            if (block.CurrentDamage <= 0f && block.AccumulatedDamage <= 0f)
                return;
            if (CubeGridSkeletonField == null || GridSkeletonTryGetBoneMethod == null)
                return;

            try
            {
                var skeleton = CubeGridSkeletonField.GetValue(grid);
                if (skeleton == null)
                    return;

                for (var x = block.Min.X; x <= block.Max.X; x++)
                for (var y = block.Min.Y; y <= block.Max.Y; y++)
                for (var z = block.Min.Z; z <= block.Max.Z; z++)
                {
                    var cube = new Vector3I(x, y, z);
                    for (var bx = 0; bx <= 2; bx++)
                    for (var by = 0; by <= 2; by++)
                    for (var bz = 0; bz <= 2; bz++)
                    {
                        var bonePosition = cube * 2 + new Vector3I(bx, by, bz);
                        var args = new object[] { bonePosition, default(Vector3) };
                        if (!(bool)GridSkeletonTryGetBoneMethod.Invoke(skeleton, args))
                            continue;

                        var offset = (Vector3)args[1];
                        if (offset.LengthSquared() <= 0.000001f)
                            continue;

                        dto.Deformations.Add(new ViewerBlockDeformationPoint
                        {
                            BonePosition = ToDto(bonePosition),
                            Offset = ToDto(offset),
                        });
                    }
                }
            }
            catch (Exception exception)
            {
                warnings.Add("Failed to inspect skeleton deformation for block " + block.Position + ": " + exception.Message);
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
                        var modelAssetId = catalog.RegisterModel(modelPath, block.BlockDefinition.Context);
                        if (string.IsNullOrEmpty(modelAssetId))
                        {
                            warnings.Add("Runtime subpart " + subpartPath + " for block " + block.Position + " has no registered model asset.");
                        }
                        else
                        {
                            var localMatrix = subpart.PositionComp.WorldMatrixRef * gridWorldInverse;
                            var subpartDto = new ViewerBlockSubpart
                            {
                                EntityId = subpart.EntityId.ToString(),
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

            if (OpaqueGeneratedCubeFaceMask(definition) == 63)
                return "opaque-full-cell";

            return "opaque-partial";
        }

        private static int OpaqueGeneratedCubeFaceMask(MyCubeBlockDefinition definition)
        {
            if (IsTransparentDefinition(definition) ||
                !definition.BlockTopology.ToString().Equals("Cube", StringComparison.OrdinalIgnoreCase) ||
                definition.Size != Vector3I.One ||
                definition.CubeDefinition == null)
                return 0;

            var mask = 0;
            foreach (var tile in MyCubeGridDefinitions.GetCubeTiles(definition))
            {
                if (!tile.FullQuad || tile.IsEmpty)
                    continue;

                var faceBit = FaceBitFromNormal(tile.Normal);
                if (faceBit.HasValue)
                    mask |= 1 << faceBit.Value;
            }

            return mask;
        }

        private static int OpaqueGeneratedCubeFaceMaskAtCell(MySlimBlock block, Vector3I gridCell)
        {
            var definition = block.BlockDefinition;
            if (definition == null)
                return 0;

            if (definition.Size != Vector3I.One ||
                block.Position != gridCell ||
                !definition.BlockTopology.ToString().Equals("Cube", StringComparison.OrdinalIgnoreCase))
                return 0;

            block.Orientation.GetMatrix(out Matrix orientation);
            var mask = 0;
            foreach (var tile in MyCubeGridDefinitions.GetCubeTiles(definition))
            {
                if (!tile.FullQuad || tile.IsEmpty)
                    continue;

                var normal = Vector3.TransformNormal(tile.Normal, orientation);
                var faceBit = FaceBitFromNormal(normal);
                if (faceBit.HasValue)
                    mask |= 1 << faceBit.Value;
            }

            return mask;
        }

        private static bool BlockHasDeformation(MySlimBlock block)
        {
            return block.CurrentDamage > 0f || block.AccumulatedDamage > 0f;
        }

        private static Vector3I FaceNormal(int bit)
        {
            switch (bit)
            {
                case 0: return new Vector3I(1, 0, 0);
                case 1: return new Vector3I(-1, 0, 0);
                case 2: return new Vector3I(0, 1, 0);
                case 3: return new Vector3I(0, -1, 0);
                case 4: return new Vector3I(0, 0, 1);
                case 5: return new Vector3I(0, 0, -1);
                default: return Vector3I.Zero;
            }
        }

        private static int OppositeFaceBit(int bit)
        {
            switch (bit)
            {
                case 0: return 1;
                case 1: return 0;
                case 2: return 3;
                case 3: return 2;
                case 4: return 5;
                case 5: return 4;
                default: return -1;
            }
        }

        private static int? FaceBitFromNormal(Vector3 normal)
        {
            var rounded = Vector3I.Round(normal);
            if (rounded == new Vector3I(1, 0, 0)) return 0;
            if (rounded == new Vector3I(-1, 0, 0)) return 1;
            if (rounded == new Vector3I(0, 1, 0)) return 2;
            if (rounded == new Vector3I(0, -1, 0)) return 3;
            if (rounded == new Vector3I(0, 0, 1)) return 4;
            if (rounded == new Vector3I(0, 0, -1)) return 5;
            return null;
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

        private static Vector3I ToVector3I(ViewerVector3I value)
        {
            return value == null ? Vector3I.Zero : new Vector3I(value.X, value.Y, value.Z);
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

        private static ViewerEmissivePart ToDto(CapturedEmissivePart value)
        {
            return new ViewerEmissivePart
            {
                EntityId = value.EntityId.ToString(),
                MaterialName = value.MaterialName ?? string.Empty,
                Color = ToDto(value.Color),
                Emissivity = value.Emissivity,
                Source = value.Source ?? string.Empty,
            };
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
            private readonly string _gridId;
            private readonly float _gridSize;
            private Vector3I _min;
            private Vector3I _max;
            private bool _initialized;
            private int _count;

            public ChunkBuilder(string id, string gridId, float gridSize)
            {
                _id = id;
                _gridId = gridId;
                _gridSize = gridSize;
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

            public ViewerGridChunk ToDto()
            {
                var half = _gridSize * 0.5f;
                return new ViewerGridChunk
                {
                    Id = _id,
                    GridId = _gridId,
                    MinCell = GridRenderSceneInspector.ToDto(_min),
                    MaxCell = GridRenderSceneInspector.ToDto(_max),
                    LocalAabbMin = GridRenderSceneInspector.ToDto(new Vector3(_min.X * _gridSize - half, _min.Y * _gridSize - half, _min.Z * _gridSize - half)),
                    LocalAabbMax = GridRenderSceneInspector.ToDto(new Vector3(_max.X * _gridSize + half, _max.Y * _gridSize + half, _max.Z * _gridSize + half)),
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

        private sealed class VoxelSceneSample
        {
            public MyVoxelBase Voxel { get; set; }

            public string BodyName { get; set; } = string.Empty;

            public Vector3I StorageMin { get; set; }

            public Vector3I StorageMax { get; set; }
        }

        private sealed class GridSceneAddResult
        {
            public ViewerGrid Grid { get; set; }

            public int IncludedBlockCount { get; set; }

            public int SkippedBlockCount { get; set; }
        }

        private sealed class SceneGridCandidate
        {
            public MyCubeGrid Grid { get; set; }

            public bool ForceFullGrid { get; set; }
        }

        private sealed class ContextClipVolume
        {
            public MyCubeGrid Primary { get; set; }

            public Vector3D CenterLocal { get; set; }

            public double MinX { get; set; }

            public double MaxX { get; set; }

            public double MinY { get; set; }

            public double MaxY { get; set; }

            public double MinZ { get; set; }

            public double MaxZ { get; set; }
        }

        private sealed class MetadataAssetCatalog
        {
            private readonly Dictionary<string, ViewerModelAsset> _modelsById = new Dictionary<string, ViewerModelAsset>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ViewerTextureAsset> _texturesById = new Dictionary<string, ViewerTextureAsset>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ViewerModAssetRoot> _modsById = new Dictionary<string, ViewerModAssetRoot>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _modIdsByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _idsByLogicalPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public string RegisterModel(string logicalPath)
            {
                return RegisterModel(logicalPath, null);
            }

            public string RegisterModel(string logicalPath, MyModContext context)
            {
                var normalized = NormalizeLogicalPath(logicalPath, context);
                if (string.IsNullOrEmpty(normalized.LogicalPath))
                    return null;

                var assetId = GetOrCreateId("model", normalized.LogicalPath, normalized.RootId);
                if (!_modelsById.ContainsKey(assetId))
                {
                    _modelsById[assetId] = new ViewerModelAsset
                    {
                        AssetId = assetId,
                        LogicalPath = normalized.LogicalPath,
                        SourceKind = SourceKind(normalized.LogicalPath, normalized.RootId),
                        RootId = normalized.RootId,
                    };
                }

                return assetId;
            }

            public string RegisterTexture(string logicalPath, string usage)
            {
                var normalized = NormalizeLogicalPath(logicalPath, null);
                if (string.IsNullOrEmpty(normalized.LogicalPath))
                    return null;

                var assetId = GetOrCreateId("texture", normalized.LogicalPath, normalized.RootId);
                if (!_texturesById.ContainsKey(assetId))
                {
                    _texturesById[assetId] = new ViewerTextureAsset
                    {
                        AssetId = assetId,
                        LogicalPath = normalized.LogicalPath,
                        SourceKind = SourceKind(normalized.LogicalPath, normalized.RootId),
                        RootId = normalized.RootId,
                        Usage = usage ?? "unknown",
                    };
                }

                return assetId;
            }

            public string RegisterMod(MyObjectBuilder_Checkpoint.ModItem? maybeModItem)
            {
                if (!maybeModItem.HasValue)
                    return string.Empty;

                var modItem = maybeModItem.Value;

                var name = modItem.Name ?? string.Empty;
                var service = modItem.PublishedServiceName ?? string.Empty;
                var key = (service + ":" + modItem.PublishedFileId + ":" + name).ToLowerInvariant();
                if (_modIdsByKey.TryGetValue(key, out var existing))
                    return existing;

                var rootId = "mod_" + ShortHash(key);
                _modIdsByKey[key] = rootId;
                _modsById[rootId] = new ViewerModAssetRoot
                {
                    RootId = rootId,
                    Name = name,
                    PublishedFileId = modItem.PublishedFileId,
                    PublishedServiceName = service,
                    FriendlyName = modItem.FriendlyName ?? string.Empty,
                    IsDependency = modItem.IsDependency,
                };
                return rootId;
            }

            public List<ViewerModelAsset> ModelAssetsSnapshot()
            {
                return _modelsById.Values.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).ToList();
            }

            public List<ViewerTextureAsset> TextureAssetsSnapshot()
            {
                return _texturesById.Values.OrderBy(asset => asset.AssetId, StringComparer.Ordinal).ToList();
            }

            public List<ViewerModAssetRoot> ModsSnapshot()
            {
                return _modsById.Values.OrderBy(mod => mod.RootId, StringComparer.Ordinal).ToList();
            }

            private string GetOrCreateId(string prefix, string logicalPath, string rootId)
            {
                var key = prefix + ":" + (rootId ?? string.Empty).ToLowerInvariant() + ":" + logicalPath.ToLowerInvariant();
                if (_idsByLogicalPath.TryGetValue(key, out var existing))
                    return existing;

                var id = prefix + "_" + ShortHash(key);
                _idsByLogicalPath[key] = id;
                return id;
            }

            private NormalizedAssetPath NormalizeLogicalPath(string path, MyModContext context)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return new NormalizedAssetPath();

                path = path.Trim().Replace('\\', '/');
                var rootId = string.Empty;
                var contentPath = VRage.FileSystem.MyFileSystem.ContentPath;
                if (!string.IsNullOrEmpty(contentPath) && Path.IsPathRooted(path))
                {
                    var fullPath = Path.GetFullPath(path);
                    var root = Path.GetFullPath(contentPath);
                    if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        path = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                }

                var modsPath = VRage.FileSystem.MyFileSystem.ModsPath;
                if (!string.IsNullOrEmpty(modsPath) && Path.IsPathRooted(path))
                {
                    var fullPath = Path.GetFullPath(path);
                    var root = Path.GetFullPath(modsPath);
                    if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        var relative = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                        var slash = relative.IndexOf('/');
                        var modName = slash >= 0 ? relative.Substring(0, slash) : relative;
                        path = slash >= 0 ? relative.Substring(slash + 1) : string.Empty;
                        rootId = RegisterModByName(modName);
                    }
                }

                while (path.StartsWith("./", StringComparison.Ordinal))
                    path = path.Substring(2);

                if (path.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
                    path = path.Substring("Content/".Length);

                if (path.StartsWith("Mods/", StringComparison.OrdinalIgnoreCase))
                {
                    var relative = path.Substring("Mods/".Length);
                    var slash = relative.IndexOf('/');
                    if (slash >= 0)
                    {
                        rootId = RegisterModByName(relative.Substring(0, slash));
                        path = relative.Substring(slash + 1);
                    }
                }

                if (string.IsNullOrEmpty(rootId))
                {
                    var modItem = context?.ModItem;
                    if (modItem.HasValue)
                        rootId = RegisterMod(modItem);
                }

                return new NormalizedAssetPath { LogicalPath = path, RootId = rootId ?? string.Empty };
            }

            private string RegisterModByName(string name)
            {
                if (string.IsNullOrWhiteSpace(name))
                    return string.Empty;

                var service = string.Empty;
                var key = service + ":0:" + name;
                if (_modIdsByKey.TryGetValue(key, out var existing))
                    return existing;

                var rootId = "mod_" + ShortHash(key.ToLowerInvariant());
                _modIdsByKey[key] = rootId;
                _modsById[rootId] = new ViewerModAssetRoot
                {
                    RootId = rootId,
                    Name = name,
                    FriendlyName = name,
                };
                return rootId;
            }

            private static string SourceKind(string logicalPath, string rootId)
            {
                return !string.IsNullOrEmpty(rootId) || logicalPath.IndexOf("Mods/", StringComparison.OrdinalIgnoreCase) >= 0 ? "mod" : "game";
            }

            private static string ShortHash(string key)
            {
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                    var builder = new StringBuilder();
                    for (var i = 0; i < 8; i++)
                        builder.Append(hash[i].ToString("x2"));

                    return builder.ToString();
                }
            }

            private sealed class NormalizedAssetPath
            {
                public string LogicalPath { get; set; } = string.Empty;

                public string RootId { get; set; } = string.Empty;
            }
        }
    }
}
