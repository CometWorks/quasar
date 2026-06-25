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

    public ViewerSceneEnvironment Environment { get; set; } = new();

    public List<ViewerBlockDefinition> BlockDefinitions { get; set; } = new();

    public List<ViewerBlockInstance> BlockInstances { get; set; } = new();

    public List<ViewerModelAsset> ModelAssets { get; set; } = new();

    public List<ViewerTextureAsset> TextureAssets { get; set; } = new();

    public List<ViewerGridChunk> Chunks { get; set; } = new();

    public List<ViewerVoxelBody> Voxels { get; set; } = new();

    public List<ViewerLightSource> LightSources { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public class ViewerSceneEnvironment
{
    public ViewerVector3 SunDirection { get; set; } = new() { X = 0.33946735f, Y = 0.70979536f, Z = -0.61721337f };

    public float SunIntensity { get; set; } = 1.9f;
}

public class ViewerLightSource
{
    public string Id { get; set; } = string.Empty;

    public string BlockId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public ViewerVector3 Position { get; set; } = new();

    public ViewerVector3 Direction { get; set; } = new();

    public ViewerVector3 Up { get; set; } = new();

    public ViewerColor Color { get; set; } = new() { R = 255, G = 255, B = 255, A = 255 };

    public float Radius { get; set; }

    public float ReflectorRadius { get; set; }

    public float Intensity { get; set; }

    public float Falloff { get; set; }

    public float ConeDegrees { get; set; }

    public bool Enabled { get; set; }

    public float BlinkIntervalSeconds { get; set; }

    public float BlinkLength { get; set; }

    public float BlinkOffset { get; set; }
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

    public List<ViewerLcdSurface> LcdSurfaces { get; set; } = new();

    public List<string> LcdMaterialsToHideWhenOffline { get; set; } = new();
}

public class ViewerLcdSurface
{
    public int Index { get; set; }

    public string MaterialName { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;

    public bool IsWorking { get; set; } = true;

    public bool UsesOnlineTextureWhenEmpty { get; set; }

    public int TextureWidth { get; set; }

    public int TextureHeight { get; set; }

    public float SurfaceWidth { get; set; }

    public float SurfaceHeight { get; set; }

    public bool PreserveAspectRatio { get; set; }

    public float TextPadding { get; set; }

    public string Font { get; set; } = string.Empty;

    public float FontSize { get; set; }

    public string Alignment { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public ViewerColor BackgroundColor { get; set; } = new();

    public byte BackgroundAlpha { get; set; }

    public ViewerColor FontColor { get; set; } = new() { R = 255, G = 255, B = 255, A = 255 };

    public ViewerColor ScriptBackgroundColor { get; set; } = new();

    public ViewerColor ScriptForegroundColor { get; set; } = new() { R = 255, G = 255, B = 255, A = 255 };

    public List<ViewerLcdImage> SelectedImages { get; set; } = new();

    public int CurrentImageIndex { get; set; }

    public string CurrentlyShownImageId { get; set; } = string.Empty;

    public ViewerLcdImage? EmptyOnlineImage { get; set; }

    public ViewerLcdImage? EmptyOfflineImage { get; set; }

    public List<ViewerLcdSprite> Sprites { get; set; } = new();
}

public class ViewerLcdImage
{
    public string Id { get; set; } = string.Empty;

    public string TexturePath { get; set; } = string.Empty;

    public string SpritePath { get; set; } = string.Empty;
}

public class ViewerLcdSprite
{
    public string Type { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;

    public string TexturePath { get; set; } = string.Empty;

    public string SpritePath { get; set; } = string.Empty;

    public ViewerVector2? Position { get; set; }

    public ViewerVector2? Size { get; set; }

    public ViewerColor? Color { get; set; }

    public string FontId { get; set; } = string.Empty;

    public string Alignment { get; set; } = string.Empty;

    public float RotationOrScale { get; set; }

    public int Index { get; set; }
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

    public List<ViewerLightSource> LightSources { get; set; } = new();
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

public class ViewerVoxelBody
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public ViewerMatrix WorldMatrix { get; set; } = ViewerMatrix.Identity();

    public ViewerVector3D PositionLeftBottomCorner { get; set; } = new();

    public ViewerVector3I StorageMin { get; set; } = new();

    public ViewerVector3I StorageMax { get; set; } = new();

    public ViewerVector3I StorageSize { get; set; } = new();

    public ViewerVector3 SizeInMetres { get; set; } = new();

    public ViewerBounds WorldAabb { get; set; } = new();

    public bool ContentChanged { get; set; }

    public ViewerPlanetInfo? Planet { get; set; }
}

public class ViewerPlanetInfo
{
    public float MinimumRadius { get; set; }

    public float AverageRadius { get; set; }

    public float MaximumRadius { get; set; }

    public float AtmosphereRadius { get; set; }

    public bool HasAtmosphere { get; set; }

    public bool SpherizeWithDistance { get; set; }
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

public class ViewerVector2
{
    public float X { get; set; }

    public float Y { get; set; }
}

public class ViewerColor
{
    public byte R { get; set; }

    public byte G { get; set; }

    public byte B { get; set; }

    public byte A { get; set; }
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
