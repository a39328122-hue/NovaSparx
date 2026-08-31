namespace NovaSparx.Backend;

public sealed record Float4(float X, float Y, float Z, float W);

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

public sealed record PreviewMaterial(
    string Name,
    string? Path,
    float[] BaseColor,
    float Roughness,
    float Metallic,
    bool TwoSided
);

public sealed record PreviewManifest(
    string Path,
    int Lod,
    bool IsNanite,
    PreviewGeometry Geometry,
    PreviewSection[] Sections,
    PreviewMaterial[] Materials
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
    string? LastError
);
