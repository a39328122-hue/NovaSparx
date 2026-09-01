using System.Collections.Concurrent;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;

namespace NovaSparx.Backend;

/// <summary>
/// NovaSparx 1.0 live Fortnite provider.
///
/// Responsibilities:
/// - latest Fortnite core manifest
/// - Fortnite_Studio manifest
/// - streamed texture OnDemand TOC
/// - mappings + AES
/// - mount / virtual paths / post mount
/// - deterministic asset loading
/// - StaticMesh geometry
/// - real material evidence through MaterialResolver
/// - compact asset inspection
///
/// Heavy texture decoding is intentionally kept in TextureService.
/// </summary>
public sealed class LiveProviderService : IDisposable
{
    public const string BackendVersion = "1.0.0";

    private readonly PublicFortniteSources _sources;
    private readonly ILogger<LiveProviderService> _log;

    private readonly SemaphoreSlim _initGate = new(1, 1);

    private readonly SemaphoreSlim _parseGate =
        new(
            Math.Max(
                1,
                int.TryParse(
                    Environment.GetEnvironmentVariable(
                        "NOVASPARX_PARSE_CONCURRENCY"),
                    out var concurrency)
                    ? Math.Min(concurrency, 2)
                    : 1),
            2);

    private readonly ConcurrentDictionary<string, CacheEntry>
        _previewCache =
            new(StringComparer.OrdinalIgnoreCase);

    private NovaHybridFileProvider? _provider;

    private string? _manifestVersion;
    private string? _studioManifestVersion;
    private string? _lastError;
    private bool _textureStreamingTocRegistered;

    private sealed record CacheEntry(
        DateTimeOffset CreatedAt,
        ResolveEnvelope Value);

    private sealed record LoadedAsset(
        UObject Object,
        string ResolvedPath);

    private static readonly TimeSpan CacheTtl =
        TimeSpan.FromMinutes(
            int.TryParse(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_PREVIEW_CACHE_MINUTES"),
                out var minutes)
                ? Math.Clamp(minutes, 1, 120)
                : 20);

    public LiveProviderService(
        PublicFortniteSources sources,
        ILogger<LiveProviderService> log)
    {
        _sources = sources;
        _log = log;
    }

    public bool IsReady =>
        _provider is not null;

    public ProviderHealth Health()
    {
        var provider = _provider;

        return new ProviderHealth(
            Ok:
                provider is not null,

            Service:
                "NovaSparx.Backend",

            Version:
                BackendVersion,

            ProviderReady:
                provider is not null,

            Mode:
                "FortniteLive/NovaHybridFileProvider",

            ManifestVersion:
                BuildManifestHealthString(),

            RegisteredArchives:
                provider is null
                    ? 0
                    : provider.UnloadedVfs.Count +
                      provider.MountedVfs.Count,

            MountedArchives:
                provider?.MountedVfs.Count ?? 0,

            IndexedFiles:
                provider?.Files.Count ?? 0,

            RequiredKeys:
                provider?.RequiredKeys.Count ?? 0,

            LoadedKeys:
                provider?.Keys.Count ?? 0,

            LastError:
                _lastError,

            TextureStreamingReady:
                _textureStreamingTocRegistered,

            PreviewCacheEntries:
                _previewCache.Count);
    }

    private string? BuildManifestHealthString()
    {
        if (_manifestVersion is null)
            return null;

        var value =
            $"core={_manifestVersion}";

        if (!string.IsNullOrWhiteSpace(
                _studioManifestVersion))
        {
            value +=
                $";studio={_studioManifestVersion}";
        }

        if (_textureStreamingTocRegistered)
            value += ";texture-streaming=on";

        return value;
    }

    public async Task EnsureReadyAsync(
        CancellationToken cancellationToken)
    {
        if (_provider is not null)
            return;

        await _initGate.WaitAsync(
            cancellationToken);

        try
        {
            if (_provider is not null)
                return;

            _lastError = null;

            var started =
                DateTimeOffset.UtcNow;

            NovaHybridFileProvider? provider =
                null;

            try
            {
                var (
                    liveManifest,
                    liveVersion) =
                    await _sources.GetLiveManifestAsync(
                        cancellationToken);

                _manifestVersion =
                    liveVersion;

                var versions =
                    new VersionContainer(
                        EGame.GAME_UE6_0);

                provider =
                    new NovaHybridFileProvider(
                        new DirectoryInfo(
                            _sources.TocCache),
                        versions)
                    {
                        LoadOnDemandTocs =
                            true,

                        ReadNaniteData =
                            true,

                        ReadShaderMaps =
                            false,

                        ReadScriptData =
                            false,

                        UseLazyPackageSerialization =
                            true
                    };

                provider.OnDemandOptions =
                    new IoStoreOnDemandOptions
                    {
                        ChunkHostUri =
                            new Uri(
                                Environment.GetEnvironmentVariable(
                                    "NOVASPARX_ONDEMAND_HOST") ??
                                "https://egdownload.fastly-edge.com/",
                                UriKind.Absolute),

                        ChunkCacheDirectory =
                            new DirectoryInfo(
                                _sources.ChunkCache),

                        Timeout =
                            TimeSpan.FromSeconds(
                                int.TryParse(
                                    Environment.GetEnvironmentVariable(
                                        "NOVASPARX_ONDEMAND_TIMEOUT_SECONDS"),
                                    out var timeout)
                                    ? Math.Clamp(
                                        timeout,
                                        10,
                                        180)
                                    : 80)
                    };

                // Core Fortnite manifest.
                await provider.RegisterManifestAsync(
                    liveManifest,
                    "Fortnite",
                    cancellationToken);

                // Fortnite_Studio extends coverage for UEFN and GameFeatures.
                try
                {
                    var studio =
                        await _sources.GetStudioManifestAsync(
                            cancellationToken);

                    if (studio is not null)
                    {
                        var (
                            studioManifest,
                            studioVersion) =
                                studio.Value;

                        _studioManifestVersion =
                            studioVersion;

                        await provider.RegisterManifestAsync(
                            studioManifest,
                            "Fortnite_Studio",
                            cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Fortnite_Studio manifest registration failed. Core Fortnite will continue.");
                }

                // Streamed texture IoStore TOC.
                try
                {
                    var externalToc =
                        await _sources
                            .GetTextureStreamingTocAsync(
                                liveManifest,
                                cancellationToken);

                    if (externalToc is not null)
                    {
                        _textureStreamingTocRegistered =
                            await provider
                                .RegisterExternalOnDemandTocAsync(
                                    externalToc.Name,
                                    externalToc.Bytes,
                                    cancellationToken);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Texture-streaming TOC registration failed. Geometry can still work.");
                }

                provider.Initialize();

                // Current Fortnite USMAP mappings.
                try
                {
                    var mappings =
                        await _sources.GetMappingsAsync(
                            cancellationToken);

                    if (mappings is not null)
                    {
                        provider.MappingsContainer =
                            mappings;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Mappings source failed.");
                }

                // Current Fortnite AES keys.
                try
                {
                    var keys =
                        await _sources.GetAesKeysAsync(
                            cancellationToken);

                    foreach (var pair in keys)
                    {
                        cancellationToken
                            .ThrowIfCancellationRequested();

                        await provider.SubmitKeyAsync(
                            pair.Key,
                            pair.Value);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "AES source failed. Unencrypted archives can still mount.");
                }

                try
                {
                    await provider.MountAsync();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "One or more archives failed to mount.");
                }

                try
                {
                    provider.LoadVirtualPaths();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Virtual path loading failed.");
                }

                try
                {
                    provider.PostMount();
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "PostMount validation reported an error.");
                }

                _provider =
                    provider;

                provider =
                    null;

                var elapsed =
                    DateTimeOffset.UtcNow -
                    started;

                _log.LogInformation(
                    "NovaSparx 1.0 ready in {Seconds:F1}s | " +
                    "core={CoreVersion} studio={StudioVersion} textureToc={TextureToc} | " +
                    "archives={Archives} mounted={Mounted} files={Files} keys={Keys}/{RequiredKeys}",
                    elapsed.TotalSeconds,
                    _manifestVersion,
                    _studioManifestVersion ?? "none",
                    _textureStreamingTocRegistered,
                    _provider.UnloadedVfs.Count +
                    _provider.MountedVfs.Count,
                    _provider.MountedVfs.Count,
                    _provider.Files.Count,
                    _provider.Keys.Count,
                    _provider.RequiredKeys.Count);
            }
            catch (Exception ex)
            {
                _lastError =
                    ex.Message;

                provider?.Dispose();

                _log.LogError(
                    ex,
                    "NovaSparx live initialization failed.");

                throw;
            }
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>
    /// Loads an asset using the mount-aware candidate list.
    /// This method is also the common entry point used by TextureService.
    /// </summary>
    public async Task<(UObject Object, string ResolvedPath)?>
        LoadObjectAsync(
            string rawPath,
            CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(
            cancellationToken);

        await _parseGate.WaitAsync(
            cancellationToken);

        try
        {
            var loaded =
                LoadObjectNoLock(
                    rawPath,
                    cancellationToken);

            if (loaded is null)
                return null;

            return (
                loaded.Object,
                loaded.ResolvedPath);
        }
        finally
        {
            _parseGate.Release();
        }
    }

    public async Task<ResolveEnvelope?>
        ResolveAsync(
            string rawPath,
            CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(
            cancellationToken);

        var canonical =
            AssetPathResolver.Canonicalize(
                rawPath);

        if (canonical.Length == 0)
            return null;

        if (_previewCache.TryGetValue(
                canonical,
                out var cached) &&
            DateTimeOffset.UtcNow -
            cached.CreatedAt < CacheTtl)
        {
            return cached.Value;
        }

        await _parseGate.WaitAsync(
            cancellationToken);

        try
        {
            if (_previewCache.TryGetValue(
                    canonical,
                    out cached) &&
                DateTimeOffset.UtcNow -
                cached.CreatedAt < CacheTtl)
            {
                return cached.Value;
            }

            var loaded =
                LoadObjectNoLock(
                    rawPath,
                    cancellationToken);

            if (loaded?.Object is not UStaticMesh mesh)
                return null;

            var envelope =
                BuildStaticMeshEnvelope(
                    mesh,
                    canonical,
                    loaded.ResolvedPath);

            TrimPreviewCacheIfNeeded();

            _previewCache[canonical] =
                new CacheEntry(
                    DateTimeOffset.UtcNow,
                    envelope);

            return envelope;
        }
        finally
        {
            _parseGate.Release();
        }
    }

    /// <summary>
    /// Returns compact deterministic information about any loadable asset.
    /// No raw UObject graph is serialized to the public API.
    /// </summary>
    public async Task<AssetInspection?>
        InspectAsync(
            string rawPath,
            CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(
            cancellationToken);

        var canonical =
            AssetPathResolver.Canonicalize(
                rawPath);

        if (canonical.Length == 0)
            return null;

        await _parseGate.WaitAsync(
            cancellationToken);

        try
        {
            var loaded =
                LoadObjectNoLock(
                    rawPath,
                    cancellationToken);

            if (loaded is null)
                return null;

            var facts =
                new Dictionary<string, object?>(
                    StringComparer.OrdinalIgnoreCase);

            PreviewMaterial? material =
                null;

            PreviewMaterial[] materials =
                [];

            AssetReference[] references =
                [];

            string fidelity =
                "unknown";

            string assetType =
                FriendlyAssetType(
                    loaded.Object);

            if (loaded.Object is UUnrealMaterial unrealMaterial)
            {
                try
                {
                    material =
                        MaterialResolver.Resolve(
                            unrealMaterial);

                    materials =
                        [material];

                    references =
                        MaterialResolver.CollectReferences(
                            unrealMaterial);

                    fidelity =
                        material.Fidelity;

                    facts["opacityMode"] =
                        material.OpacityMode;

                    facts["twoSided"] =
                        material.TwoSided;

                    facts["roughness"] =
                        material.Roughness;

                    facts["metallic"] =
                        material.Metallic;

                    facts["specular"] =
                        material.Specular;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "Material inspection failed for {Path}.",
                        canonical);
                }
            }
            else if (loaded.Object is UStaticMesh mesh)
            {
                try
                {
                    var envelope =
                        BuildStaticMeshEnvelope(
                            mesh,
                            canonical,
                            loaded.ResolvedPath);

                    materials =
                        envelope.Manifest.Materials;

                    references =
                        envelope.Manifest.References ??
                        [];

                    fidelity =
                        envelope.Manifest.MaterialFidelity;

                    facts["lod"] =
                        envelope.Manifest.Lod;

                    facts["nanite"] =
                        envelope.Manifest.IsNanite;

                    facts["vertices"] =
                        envelope.Manifest.Geometry.Positions.Length /
                        3;

                    facts["triangles"] =
                        envelope.Manifest.Geometry.Indices.Length /
                        3;

                    facts["sections"] =
                        envelope.Manifest.Sections.Length;
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "StaticMesh inspection geometry/material pass failed for {Path}.",
                        canonical);
                }
            }

            facts["runtimeType"] =
                loaded.Object.GetType().Name;

            return new AssetInspection(
                State:
                    "ready",

                Path:
                    canonical,

                ResolvedPath:
                    loaded.ResolvedPath,

                AssetType:
                    assetType,

                Source:
                    _textureStreamingTocRegistered
                        ? "novasparx-hybrid-live+texture-streaming"
                        : "novasparx-hybrid-live",

                MaterialFidelity:
                    fidelity,

                Material:
                    material,

                Materials:
                    materials,

                References:
                    references,

                Facts:
                    facts);
        }
        finally
        {
            _parseGate.Release();
        }
    }

    private LoadedAsset? LoadObjectNoLock(
        string rawPath,
        CancellationToken cancellationToken)
    {
        var provider =
            _provider ??
            throw new InvalidOperationException(
                "NovaSparx provider is not ready.");

        foreach (var candidate in
                 AssetPathResolver.LoadCandidates(
                     rawPath))
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            try
            {
                var loaded =
                    provider.SafeLoadPackageObject(
                        candidate);

                if (loaded is not null)
                {
                    return new LoadedAsset(
                        Object:
                            loaded,

                        ResolvedPath:
                            candidate);
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Asset candidate failed: {Candidate}",
                    candidate);
            }
        }

        return null;
    }

    private ResolveEnvelope
        BuildStaticMeshEnvelope(
            UStaticMesh mesh,
            string canonical,
            string resolved)
    {
        // Keep the conversion API already verified against the repository's
        // pinned CUE4Parse-Conversion package:
        // 1.2.2.202608.
        if (!mesh.TryConvert(
                out CStaticMesh converted,
                ENaniteMeshFormat.AllLayersNaniteLast))
        {
            throw new InvalidOperationException(
                "CUE4Parse could not convert this StaticMesh.");
        }

        using (converted)
        {
            if (converted.LODs.Count == 0)
            {
                throw new InvalidOperationException(
                    "StaticMesh contains no renderable normal or Nanite LOD.");
            }

            var maxVertices =
                int.TryParse(
                    Environment.GetEnvironmentVariable(
                        "NOVASPARX_MAX_VERTICES"),
                    out var parsedVertices)
                    ? Math.Clamp(
                        parsedVertices,
                        10_000,
                        700_000)
                    : 320_000;

            var maxIndices =
                int.TryParse(
                    Environment.GetEnvironmentVariable(
                        "NOVASPARX_MAX_INDICES"),
                    out var parsedIndices)
                    ? Math.Clamp(
                        parsedIndices,
                        30_000,
                        2_100_000)
                    : 960_000;

            var candidates =
                new List<(
                    CStaticMeshLod Lod,
                    int Index,
                    int VertexCount,
                    int IndexCount)>();

            for (var index = 0;
                 index < converted.LODs.Count;
                 index++)
            {
                var lod =
                    converted.LODs[index];

                if (lod is null ||
                    lod.Verts is null ||
                    lod.Verts.Length == 0 ||
                    lod.Indices is null)
                {
                    continue;
                }

                try
                {
                    var indexCount =
                        lod.Indices.Value.Length;

                    if (indexCount < 3)
                        continue;

                    candidates.Add(
                        (
                            lod,
                            index,
                            lod.Verts.Length,
                            indexCount
                        ));
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "Failed to materialize StaticMesh LOD {LodIndex}.",
                        index);
                }
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    "StaticMesh conversion produced no usable geometry.");
            }

            var selected =
                candidates.FirstOrDefault(
                    candidate =>
                        candidate.VertexCount <= maxVertices &&
                        candidate.IndexCount <= maxIndices);

            if (selected.Lod is null)
            {
                selected =
                    candidates
                        .OrderBy(
                            candidate =>
                                candidate.VertexCount)
                        .ThenBy(
                            candidate =>
                                candidate.IndexCount)
                        .First();
            }

            if (selected.VertexCount > maxVertices ||
                selected.IndexCount > maxIndices)
            {
                throw new InvalidOperationException(
                    "The smallest available StaticMesh layer is still larger than " +
                    $"the NovaSparx preview budget " +
                    $"({selected.VertexCount:N0} vertices / " +
                    $"{selected.IndexCount:N0} indices).");
            }

            var chosen =
                selected.Lod;

            var vertices =
                chosen.Verts!;

            var indices =
                chosen.Indices!.Value;

            var rawSections =
                chosen.Sections?.Value ??
                Array.Empty<CMeshSection>();

            var vertexCount =
                vertices.Length;

            var positions =
                new float[vertexCount * 3];

            var normals =
                new float[vertexCount * 3];

            var tangents =
                new float[vertexCount * 4];

            var uv0 =
                new float[vertexCount * 2];

            for (var index = 0;
                 index < vertexCount;
                 index++)
            {
                var vertex =
                    vertices[index];

                positions[index * 3 + 0] =
                    (float)vertex.Position.X;

                positions[index * 3 + 1] =
                    (float)vertex.Position.Y;

                positions[index * 3 + 2] =
                    (float)vertex.Position.Z;

                normals[index * 3 + 0] =
                    (float)vertex.Normal.X;

                normals[index * 3 + 1] =
                    (float)vertex.Normal.Y;

                normals[index * 3 + 2] =
                    (float)vertex.Normal.Z;

                tangents[index * 4 + 0] =
                    (float)vertex.Tangent.X;

                tangents[index * 4 + 1] =
                    (float)vertex.Tangent.Y;

                tangents[index * 4 + 2] =
                    (float)vertex.Tangent.Z;

                tangents[index * 4 + 3] =
                    (float)vertex.Tangent.W;

                uv0[index * 2 + 0] =
                    vertex.UV.U;

                uv0[index * 2 + 1] =
                    vertex.UV.V;
            }

            float[]? colors =
                null;

            if (chosen.VertexColors is
                    { Length: > 0 } vertexColors &&
                vertexColors.Length ==
                    vertexCount)
            {
                colors =
                    new float[vertexCount * 4];

                for (var index = 0;
                     index < vertexCount;
                     index++)
                {
                    var color =
                        vertexColors[index];

                    colors[index * 4 + 0] =
                        color.R / 255f;

                    colors[index * 4 + 1] =
                        color.G / 255f;

                    colors[index * 4 + 2] =
                        color.B / 255f;

                    colors[index * 4 + 3] =
                        color.A / 255f;
                }
            }

            var validMaterialIndices =
                rawSections
                    .Where(
                        section =>
                            section.MaterialIndex >= 0)
                    .Select(
                        section =>
                            section.MaterialIndex)
                    .ToArray();

            var materialCount =
                validMaterialIndices.Length == 0
                    ? 1
                    : Math.Min(
                        validMaterialIndices.Max() + 1,
                        64);

            var materials =
                new PreviewMaterial[materialCount];

            var referenceList =
                new List<AssetReference>();

            var referenceSet =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            void AddReferences(
                IEnumerable<AssetReference> source)
            {
                foreach (var reference in source)
                {
                    var key =
                        $"{reference.Kind}|{reference.Path}";

                    if (referenceSet.Add(key))
                        referenceList.Add(reference);
                }
            }

            for (var materialIndex = 0;
                 materialIndex < materialCount;
                 materialIndex++)
            {
                var section =
                    rawSections.FirstOrDefault(
                        candidate =>
                            candidate.MaterialIndex ==
                            materialIndex);

                var name =
                    section?.MaterialName ??
                    $"Material_{materialIndex}";

                PreviewMaterial? previewMaterial =
                    null;

                try
                {
                    if (section?.Material?
                            .Load<UMaterialInterface>() is
                        { } loadedMaterial)
                    {
                        previewMaterial =
                            MaterialResolver.Resolve(
                                loadedMaterial,
                                name);

                        AddReferences(
                            MaterialResolver.CollectReferences(
                                loadedMaterial));
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "Could not fully resolve material slot {MaterialIndex}.",
                        materialIndex);
                }

                materials[materialIndex] =
                    previewMaterial ??
                    new PreviewMaterial(
                        Name:
                            name,

                        Path:
                            null,

                        BaseColor:
                            [1f, 1f, 1f, 1f],

                        Roughness:
                            0.62f,

                        Metallic:
                            0f,

                        TwoSided:
                            chosen.IsTwoSided,

                        Fidelity:
                            "unknown",

                        Evidence:
                            "material-slot-loaded-without-resolved-material-evidence");
            }

            var sections =
                rawSections
                    .Where(
                        section =>
                            section.FirstIndex >= 0 &&
                            section.NumFaces > 0)
                    .Select(
                        section =>
                        {
                            var materialIndex =
                                section.MaterialIndex < 0
                                    ? 0
                                    : Math.Clamp(
                                        section.MaterialIndex,
                                        0,
                                        materials.Length - 1);

                            return new PreviewSection(
                                FirstIndex:
                                    section.FirstIndex,

                                IndexCount:
                                    checked(
                                        section.NumFaces *
                                        3),

                                MaterialIndex:
                                    materialIndex,

                                Name:
                                    section.MaterialName ??
                                    materials[materialIndex].Name);
                        })
                    .ToArray();

            if (sections.Length == 0)
            {
                sections =
                [
                    new PreviewSection(
                        0,
                        indices.Length,
                        0,
                        materials[0].Name)
                ];
            }

            var geometry =
                new PreviewGeometry(
                    Positions:
                        positions,

                    Indices:
                        indices,

                    Normals:
                        normals,

                    Tangents:
                        tangents,

                    Uv0:
                        uv0,

                    Colors:
                        colors);

            var lodIndex =
                chosen.IsNanite
                    ? -1
                    : selected.Index;

            var materialFidelity =
                AggregateMaterialFidelity(
                    materials);

            var manifest =
                new PreviewManifest(
                    Path:
                        canonical,

                    Lod:
                        lodIndex,

                    IsNanite:
                        chosen.IsNanite,

                    Geometry:
                        geometry,

                    Sections:
                        sections,

                    Materials:
                        materials,

                    MaterialFidelity:
                        materialFidelity,

                    References:
                        referenceList.ToArray());

            return new ResolveEnvelope(
                State:
                    "ready",

                Source:
                    _textureStreamingTocRegistered
                        ? "novasparx-hybrid-live+texture-streaming"
                        : "novasparx-hybrid-live",

                ResolvedPath:
                    resolved,

                AssetType:
                    "StaticMesh",

                Schema:
                    "novasparx.preview.v1",

                Version:
                    BackendVersion,

                ManifestVersion:
                    BuildManifestHealthString() ??
                    "unknown",

                Manifest:
                    manifest);
        }
    }

    private static string AggregateMaterialFidelity(
        IEnumerable<PreviewMaterial> materials)
    {
        var best = 0;

        foreach (var material in materials)
        {
            var score =
                material.Fidelity.ToLowerInvariant() switch
                {
                    "high" => 4,
                    "medium" => 3,
                    "partial" => 2,
                    "low" => 1,
                    _ => 0
                };

            best =
                Math.Max(
                    best,
                    score);
        }

        return best switch
        {
            4 => "high",
            3 => "medium",
            2 => "partial",
            1 => "low",
            _ => "unknown"
        };
    }

    private static string FriendlyAssetType(
        UObject value)
    {
        return value switch
        {
            UStaticMesh => "StaticMesh",
            UMaterialInstanceConstant => "MaterialInstanceConstant",
            UMaterialInstance => "MaterialInstance",
            UMaterial => "Material",
            UMaterialInterface => "MaterialInterface",
            UUnrealMaterial => "Material",
            _ => value.GetType().Name.StartsWith(
                    'U')
                ? value.GetType().Name[1..]
                : value.GetType().Name
        };
    }

    private void TrimPreviewCacheIfNeeded()
    {
        if (_previewCache.Count <= 300)
            return;

        var oldest =
            _previewCache
                .OrderBy(
                    pair =>
                        pair.Value.CreatedAt)
                .Take(75)
                .Select(
                    pair =>
                        pair.Key)
                .ToArray();

        foreach (var key in oldest)
        {
            _previewCache.TryRemove(
                key,
                out _);
        }
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken)
    {
        await _initGate.WaitAsync(
            cancellationToken);

        try
        {
            _previewCache.Clear();

            _provider?.Dispose();
            _provider =
                null;

            _manifestVersion =
                null;

            _studioManifestVersion =
                null;

            _lastError =
                null;

            _textureStreamingTocRegistered =
                false;
        }
        finally
        {
            _initGate.Release();
        }

        await EnsureReadyAsync(
            cancellationToken);
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _initGate.Dispose();
        _parseGate.Dispose();
    }
}
