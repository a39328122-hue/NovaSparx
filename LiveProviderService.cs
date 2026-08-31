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
/// NovaSparx 0.3.1 Live service.
/// Compile hotfix aligned to CUE4Parse/CUE4Parse-Conversion 1.2.2.202608.
/// </summary>
public sealed class LiveProviderService : IDisposable
{
    public const string BackendVersion =
        "0.3.1-compile-hotfix";

    private readonly PublicFortniteSources _sources;
    private readonly ILogger<LiveProviderService> _log;

    private readonly SemaphoreSlim _initGate =
        new(1, 1);

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
        var provider =
            _provider;

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
                _lastError);
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
            value +=
                ";texture-streaming=on";

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

            _lastError =
                null;

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

                // Core Fortnite BuildPatch archives.
                await provider.RegisterManifestAsync(
                    liveManifest,
                    "Fortnite",
                    cancellationToken);

                // Optional Fortnite_Studio manifest for UEFN/GameFeature coverage.
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

                // Optional streamed-texture IoStore TOC.
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
                    "NovaSparx Hybrid ready in {Seconds:F1}s | " +
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
                    "NovaSparx Hybrid initialization failed.");

                throw;
            }
        }
        finally
        {
            _initGate.Release();
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

            var provider =
                _provider ??
                throw new InvalidOperationException(
                    "NovaSparx provider is not ready.");

            UObject? loaded =
                null;

            string? resolved =
                null;

            foreach (var candidate in
                     AssetPathResolver.LoadCandidates(
                         rawPath))
            {
                cancellationToken
                    .ThrowIfCancellationRequested();

                try
                {
                    loaded =
                        provider.SafeLoadPackageObject(
                            candidate);

                    if (loaded is not null)
                    {
                        resolved =
                            candidate;
                        break;
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

            if (loaded is not UStaticMesh mesh)
                return null;

            var envelope =
                BuildStaticMeshEnvelope(
                    mesh,
                    canonical,
                    resolved ?? canonical);

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

    private ResolveEnvelope
        BuildStaticMeshEnvelope(
            UStaticMesh mesh,
            string canonical,
            string resolved)
    {
        // Use the exact mesh-conversion API shipped by CUE4Parse-Conversion 1.2.2.202608.
        // The matching release API is MeshConverter.TryConvert + CStaticMesh/CStaticMeshLod.
        //
        // AllLayersNaniteLast = normal LODs first, Nanite fallback last.
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
                    $"the NovaSparx HTTP preview budget " +
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
                        32);

            var materials =
                new PreviewMaterial[materialCount];

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

                string? materialPath =
                    null;

                try
                {
                    if (section?.Material?
                            .Load<UMaterialInterface>() is
                        { } loadedMaterial)
                    {
                        materialPath =
                            loadedMaterial.GetPathName();
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(
                        ex,
                        "Could not resolve material slot {MaterialIndex}.",
                        materialIndex);
                }

                materials[materialIndex] =
                    new PreviewMaterial(
                        Name:
                            name,

                        Path:
                            materialPath,

                        BaseColor:
                            [1f, 1f, 1f, 1f],

                        Roughness:
                            0.62f,

                        Metallic:
                            0f,

                        TwoSided:
                            chosen.IsTwoSided);
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
                        materials);

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
