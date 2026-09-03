namespace NovaSparx.Backend;

/// <summary>
/// Shared API models for NovaSparx 1.0.
///
/// Compatibility rule:
/// the original 0.3.x fields stay first in each positional record so the
/// existing backend can keep compiling while the 1.0 services are uploaded
/// one file at a time.
/// </summary>

public sealed record Float4(
    float X,
    float Y,
    float Z,
    float W
);

public sealed record PreviewGeometry(
    float[] Positions,
    uint[] Indices,
    float[] Normals,
    float[] Tangents,
    float[] Uv0,
    float[]? Colors
);

public sealed record PreviewSection(
    int FirstIndex,
    int IndexCount,
    int MaterialIndex,
    string Name
);

/// <summary>
/// Browser-safe material description.
///
/// The first six fields are the original NovaSparx contract.
/// Everything after TwoSided is additive for 1.0.
/// </summary>
public sealed record PreviewMaterial(
    string Name,
    string? Path,
    float[] BaseColor,
    float Roughness,
    float Metallic,
    bool TwoSided,

    float[]? EmissiveColor = null,
    float Specular = 0.5f,
    float Opacity = 1.0f,
    string OpacityMode = "opaque",
    float OpacityCutoff = 0.333f,
    bool UseVertexColor = false,

    float[]? UvScale = null,
    float[]? UvOffset = null,

    string? BaseColorTexture = null,
    string? NormalTexture = null,
    string? EmissiveTexture = null,
    string? OpacityTexture = null,
    string? PackedTexture = null,

    PackedChannels? PackedChannels = null,

    string Fidelity = "unknown",
    string Evidence = ""
);

public sealed record PackedChannels(
    int Ao = -1,
    int Roughness = -1,
    int Metallic = -1
);

public sealed record AssetReference(
    string Kind,
    string Path
);

/// <summary>
/// Static/Skeletal mesh preview manifest.
///
/// The original 0.3.x fields remain in the same order.
/// MaterialFidelity and References are new additive fields.
/// </summary>
public sealed record PreviewManifest(
    string Path,
    int Lod,
    bool IsNanite,
    PreviewGeometry Geometry,
    PreviewSection[] Sections,
    PreviewMaterial[] Materials,

    string MaterialFidelity = "unknown",
    AssetReference[]? References = null
);

public sealed record ResolveEnvelope(
    string State,
    string Source,
    string ResolvedPath,
    string AssetType,
    string Schema,
    string Version,
    string ManifestVersion,
    PreviewManifest Manifest
);

public sealed record SourceStatus(
    bool Ready,
    string Name,
    string? Version,
    string? Error
);

/// <summary>
/// Health response.
///
/// The original fields remain untouched. New 1.0 fields are appended with
/// defaults so old construction sites still compile during the migration.
/// </summary>
public sealed record ProviderHealth(
    bool Ok,
    string Service,
    string Version,
    bool ProviderReady,
    string Mode,
    string? ManifestVersion,
    int RegisteredArchives,
    int MountedArchives,
    int IndexedFiles,
    int RequiredKeys,
    int LoadedKeys,
    string? LastError,

    bool TextureStreamingReady = false,
    int PreviewCacheEntries = 0
);

/// <summary>
/// Result returned by /nova/inspect.
/// It intentionally contains compact deterministic facts rather than a raw
/// UObject dump.
/// </summary>
public sealed record AssetInspection(
    string State,
    string Path,
    string ResolvedPath,
    string AssetType,
    string Source,
    string MaterialFidelity,
    PreviewMaterial? Material,
    PreviewMaterial[] Materials,
    AssetReference[] References,
    Dictionary<string, object?> Facts
);

/// <summary>
/// Decoded texture response produced by TextureService.
/// Bytes are sent directly as the declared ContentType and are not normally
/// serialized into JSON.
/// </summary>
public sealed record TexturePayload(
    string Path,
    string ResolvedPath,
    string ContentType,
    byte[] Bytes,
    int Width,
    int Height
);

/// <summary>
/// Universal visual handoff for FNAA's final preview layer.
///
/// A mesh plan contains CUE4Parse geometry for the browser PNG renderer. A
/// texture plan points at a texture already verified and cached by
/// TextureService. Metadata is still returned when the asset has no honest
/// visual representation, allowing FNAA to produce a clearly labelled
/// evidence card instead of an empty panel.
/// </summary>
public sealed record UniversalPreviewPlan(
    string State,
    string Kind,
    string RequestedPath,
    string PreviewPath,
    string AssetType,
    string Source,
    string Evidence,
    ResolveEnvelope? Mesh,
    AssetInspection? Inspection,
    AssetReference[] AttemptedReferences,
    int TextureWidth = 0,
    int TextureHeight = 0
);

/// <summary>
/// Internal transport response used by the direct HTTP API and by NovaLink.
/// Keeping one response model prevents the reverse tunnel and local endpoints
/// from drifting into different contracts.
/// </summary>
public sealed record DispatchResponse(
    int Status,
    string ContentType,
    byte[] Body
);

