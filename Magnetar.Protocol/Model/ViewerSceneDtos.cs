using System;
using System.Collections.Generic;

namespace Magnetar.Protocol.Model;

/// <summary>
/// Request body for <see cref="Transport.ServerCommandType.GetEntityRenderScene"/>.
/// Serialized as JSON into <see cref="Transport.ServerCommandEnvelope.Payload"/>.
/// </summary>
public class EntityRenderSceneRequest
{
    public long EntityId { get; set; }
}

/// <summary>
/// Metadata-only scene snapshot for the browser grid viewer. This contract must not
/// contain model bytes, texture bytes, or extracted mesh geometry.
/// </summary>
public class EntityRenderScene
{
    public string SchemaVersion { get; set; } = "quasar-grid-scene.v1";

    public string GameVersion { get; set; } = string.Empty;

    public string PluginVersion { get; set; } = string.Empty;

    public ViewerGrid Grid { get; set; } = new();

    public List<ViewerBlockDefinition> BlockDefinitions { get; set; } = new();

    public List<ViewerBlockInstance> BlockInstances { get; set; } = new();

    public List<ViewerModelAsset> ModelAssets { get; set; } = new();

    public List<ViewerTextureAsset> TextureAssets { get; set; } = new();

    public List<ViewerGridChunk> Chunks { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class ViewerGrid
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public float GridSize { get; set; }

    public string GridSpace { get; set; } = string.Empty;

    public bool IsStatic { get; set; }

    public int BlockCount { get; set; }

    public ViewerMatrix WorldMatrix { get; set; } = ViewerMatrix.Identity();

    public ViewerBounds Bounds { get; set; } = new();
}

public class ViewerBlockDefinition
{
    public string Id { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string GridSpace { get; set; } = string.Empty;

    public ViewerVector3I Size { get; set; } = new();

    public string ModelAssetId { get; set; } = string.Empty;

    public ViewerVector3 ModelOffset { get; set; } = new();

    public ViewerVector3 LocalAabbMin { get; set; } = new();

    public ViewerVector3 LocalAabbMax { get; set; } = new();

    public string VisibilityClass { get; set; } = string.Empty;

    public int OpaqueFaceMask { get; set; }

    public List<string> BuildProgressModelAssetIds { get; set; } = new();
}

public class ViewerBlockInstance
{
    public string Id { get; set; } = string.Empty;

    public string GridId { get; set; } = string.Empty;

    public string BlockTypeId { get; set; } = string.Empty;

    public string ChunkId { get; set; } = string.Empty;

    public ViewerVector3I Cell { get; set; } = new();

    public ViewerVector3I Min { get; set; } = new();

    public ViewerVector3I Max { get; set; } = new();

    public ViewerVector3 Translation { get; set; } = new();

    public ViewerMatrix Rotation { get; set; } = ViewerMatrix.Identity();

    public ViewerVector3 Scale { get; set; } = new() { X = 1f, Y = 1f, Z = 1f };

    public string OrientationForward { get; set; } = string.Empty;

    public string OrientationUp { get; set; } = string.Empty;

    public ViewerVector3 ColourMaskHsv { get; set; } = new();

    public string SkinSubtypeId { get; set; } = string.Empty;

    public float BuildLevel { get; set; }

    public float Integrity { get; set; }

    public float MaxIntegrity { get; set; }

    public long OwnerIdentityId { get; set; }

    public long BuiltByIdentityId { get; set; }

    public string CurrentModelAssetId { get; set; } = string.Empty;

    public List<ViewerBlockModelPart> ModelParts { get; set; } = new();

    public List<ViewerBlockSubpart> Subparts { get; set; } = new();

    public List<ViewerMaterialTextureChange> SkinTextureChanges { get; set; } = new();
}

public class ViewerBlockModelPart
{
    public string ModelAssetId { get; set; } = string.Empty;

    public ViewerMatrix LocalMatrix { get; set; } = ViewerMatrix.Identity();

    public ViewerVector3 LocalNormal { get; set; } = new();

    public ViewerVector4Byte PatternOffset { get; set; } = new();
}

public class ViewerBlockSubpart
{
    public string Name { get; set; } = string.Empty;

    public string ModelAssetId { get; set; } = string.Empty;

    public ViewerMatrix LocalMatrix { get; set; } = ViewerMatrix.Identity();
}

public class ViewerMaterialTextureChange
{
    public string MaterialName { get; set; } = string.Empty;

    public Dictionary<string, string> Textures { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ViewerModelAsset
{
    public string AssetId { get; set; } = string.Empty;

    public string LogicalPath { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;
}

public class ViewerTextureAsset
{
    public string AssetId { get; set; } = string.Empty;

    public string LogicalPath { get; set; } = string.Empty;

    public string SourceKind { get; set; } = string.Empty;

    public string Usage { get; set; } = string.Empty;
}

public class ViewerGridChunk
{
    public string Id { get; set; } = string.Empty;

    public ViewerVector3I MinCell { get; set; } = new();

    public ViewerVector3I MaxCell { get; set; } = new();

    public ViewerVector3 LocalAabbMin { get; set; } = new();

    public ViewerVector3 LocalAabbMax { get; set; } = new();

    public int BlockCount { get; set; }
}

public class ViewerBounds
{
    public ViewerVector3D Min { get; set; } = new();

    public ViewerVector3D Max { get; set; } = new();
}

public class ViewerVector3I
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Z { get; set; }
}

public class ViewerVector3
{
    public float X { get; set; }

    public float Y { get; set; }

    public float Z { get; set; }
}

public class ViewerVector3D
{
    public double X { get; set; }

    public double Y { get; set; }

    public double Z { get; set; }
}

public class ViewerVector4Byte
{
    public byte X { get; set; }

    public byte Y { get; set; }

    public byte Z { get; set; }

    public byte W { get; set; }
}

public class ViewerMatrix
{
    public double M11 { get; set; }
    public double M12 { get; set; }
    public double M13 { get; set; }
    public double M14 { get; set; }
    public double M21 { get; set; }
    public double M22 { get; set; }
    public double M23 { get; set; }
    public double M24 { get; set; }
    public double M31 { get; set; }
    public double M32 { get; set; }
    public double M33 { get; set; }
    public double M34 { get; set; }
    public double M41 { get; set; }
    public double M42 { get; set; }
    public double M43 { get; set; }
    public double M44 { get; set; }

    public static ViewerMatrix Identity() => new()
    {
        M11 = 1,
        M22 = 1,
        M33 = 1,
        M44 = 1,
    };
}
