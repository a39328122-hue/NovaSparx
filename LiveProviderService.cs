using System.Collections.Concurrent;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;

namespace NovaSparx.Backend;

/// <summary>
/// NovaSparx 0.3 Live service.
///
/// Design goals:
/// - main Fortnite live manifest
/// - optional Fortnite_Studio manifest
/// - optional texture-streaming IoStore TOC
/// - current mappings + AES
/// - normal LOD fallback + Nanite fallback
/// - low memory footprint for a free backend
/// </summary>
public sealed class LiveProviderService : IDisposable
{
    public const string BackendVersion =
        "0.3.0-hybrid-alpha";

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

    private readonly ConcurrentDictionary<
        string,
        CacheEntry> _previewCache =
        new(StringComparer.OrdinalIgnoreCase);

    private NovaHybridFileProvider? _provider;

    private string? _manifestVersion;
    private string? _studioManifestVersion;
    private string? _lastError;

    private ManifestRegistrationResult? _coreRegistration;
    private ManifestRegistrationResult? _studioRegistration;
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

    public bool IsReady => _provider is not null;

    public ProviderHealth Health()
    {
        var provider = _provider;

        return new ProviderHealth(
            Ok: provider is not null,
            Service: "NovaSparx.Backend",
            Version: BackendVersion,
            ProviderReady: provider is not null,
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

        var text =
            $"core={_manifestVersion}";

        if (!string.IsNullOrWhiteSpace(
            _studioManifestVersion))
        {
            text +=
                $";studio={_studioManifestVersion}";
        }

        if (_textureStreamingTocRegistered)
            text += ";texture-streaming=on";

        return text;
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

            try
            {
                var (liveManifest, liveVersion) =
                    await _sources.GetLiveManifestAsync(
                        cancellationToken);

                _manifestVersion =
                    liveVersion;

                // FortnitePorting's current Live path has moved to UE6 game versioning.
                // This was one of the important differences from NovaSparx 0.2.
                var versions =
                    new VersionContainer(
                        EGame.GAME_UE6_0);

                var provider =
                    new NovaHybridFileProvider(
                        new DirectoryInfo(
                            _sources.TocCache),
                        versions)
                    {
                        LoadOnDemandTocs = true,
                        ReadNaniteData = true,
                        ReadShaderMaps = false,
                        ReadScriptData = false,
                        UseLazyPackageSerialization = true
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
                                    ? Math.Clamp(timeout, 10, 180)
                                    : 80)
                    };

                // 1) Main live Fortnite archives.
                _coreRegistration =
                    await provider.RegisterManifestAsync(
                        liveManifest,
                        "Fortnite",
                        cancellationToken);

                // 2) Fortnite_Studio / UEFN manifest.
                // This increases plugin/GameFeature coverage when Dilly exposes it.
                try
                {
                    var studio =
                        await _sources.GetStudioManifestAsync(
                            cancellationToken);

                    if (studio is not null)
                    {
                        var (
                            studioManifest,
                            studioVersion) = studio.Value;

                        _studioManifestVersion =
                            studioVersion;

                        _studioRegistration =
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
                        "Fortnite_Studio manifest registration failed. " +
                        "Core Fortnite will continue.");
                }

                // 3) Texture streaming TOC from Cloud/IoStoreOnDemand.ini.
                // This is especially important for high-quality material/texture previews.
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
                        "Texture-streaming TOC registration failed. " +
                        "Mesh geometry can still work.");
                }

                provider.Initialize();

                // Mappings before object parsing.
                try
                {
                    var mappings =
                        await _sources.GetMappingsAsync(
                            cancellationToken);

                    if (mappings is not null)
                        provider.MappingsContainer =
                            mappings;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(
                        ex,
                        "Mappings source failed.");
                }

                // Submit public/current keys.
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
                        "AES source failed. " +
                        "Unencrypted archives can still mount.");
                }

                // Mount remaining unencrypted/now-unlocked archives.
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

                // Resolve plugin virtual paths after the archive set is mounted.
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
                    provider.UnloadedVfs.Count +
                    provider.MountedVfs.Count,
                    provider.MountedVfs.Count,
                    provider.Files.Count,
                    provider.Keys.Count,
                    provider.RequiredKeys.Count);
            }
            catch (Exception ex)
            {
                _lastError =
                    ex.Message;

                _provider?.Dispose();
                _provider = null;

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

            UObject? loaded = null;
            string? resolved = null;

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
        // All normal LODs first, Nanite last.
        // The HTTP preview picks the highest quality layer that fits the mobile budget.
        using var dto =
            new StaticMeshDto(
                mesh,
                EMeshQuality.All,
                ENaniteMeshFormat.NaniteLast);

        if (dto.LODs.Count == 0)
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

        // StaticMeshDto preserves quality order.
        // Prefer the first (highest-quality) LOD that fits.
        var chosen =
            dto.LODs.FirstOrDefault(
                lod =>
                    lod.Vertices.Length <=
                    maxVertices &&
                    lod.Indices.Length <=
                    maxIndices);

        // If all layers are large, choose the smallest layer only if it still fits.
        chosen ??=
            dto.LODs
                .OrderBy(lod =>
                    lod.Vertices.Length)
                .ThenBy(lod =>
                    lod.Indices.Length)
                .First();

        if (chosen.Vertices.Length >
                maxVertices ||
            chosen.Indices.Length >
                maxIndices)
        {
            throw new InvalidOperationException(
                "The smallest available StaticMesh layer is still larger than " +
                $"the NovaSparx HTTP preview budget " +
                $"({chosen.Vertices.Length:N0} vertices / " +
                $"{chosen.Indices.Length:N0} indices).");
        }

        var vertexCount =
            chosen.Vertices.Length;

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
                chosen.Vertices[index];

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
                vertex.Uv.U;

            uv0[index * 2 + 1] =
                vertex.Uv.V;
        }

        float[]? colors = null;

        var colorSet =
            chosen.VertexColors?
                .FirstOrDefault();

        if (colorSet is not null &&
            colorSet.Value.Colors.Length ==
            vertexCount)
        {
            colors =
                new float[vertexCount * 4];

            for (var index = 0;
                 index < vertexCount;
                 index++)
            {
                var color =
                    colorSet.Value.Colors[index];

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

        var sections =
            chosen.Sections
                .Select(section =>
                {
                    var material =
                        dto.GetMaterial(section);

                    return new PreviewSection(
                        FirstIndex:
                            section.FirstIndex,

                        IndexCount:
                            checked(
                                section.NumFaces *
                                3),

                        MaterialIndex:
                            Math.Max(
                                0,
                                section.MaterialIndex),

                        Name:
                            material?.SlotName ??
                            $"Material_{section.MaterialIndex}");
                })
                .ToArray();

        var materials =
            dto.Materials
                .Select(material =>
                {
                    string? path = null;

                    try
                    {
                        if (material.Material?
                                .TryLoad<UMaterialInterface>(
                                    out var loadedMaterial) ==
                            true)
                        {
                            path =
                                loadedMaterial
                                    .GetPathName();
                        }
                    }
                    catch
                    {
                        // Material path enrichment is optional.
                    }

                    return new PreviewMaterial(
                        Name:
                            material.SlotName,

                        Path:
                            path,

                        BaseColor:
                            [1f, 1f, 1f, 1f],

                        Roughness:
                            0.62f,

                        Metallic:
                            0f,

                        TwoSided:
                            chosen.IsTwoSided);
                })
                .ToArray();

        if (materials.Length == 0)
        {
            materials =
            [
                new PreviewMaterial(
                    "FallbackMaterial",
                    null,
                    [0.72f, 0.75f, 0.80f, 1f],
                    0.68f,
                    0f,
                    chosen.IsTwoSided)
            ];
        }

        if (sections.Length == 0)
        {
            sections =
            [
                new PreviewSection(
                    0,
                    chosen.Indices.Length,
                    0,
                    materials[0].Name)
            ];
        }

        // Clamp corrupt/invalid material slot indices.
        sections =
            sections
                .Select(section =>
                    section with
                    {
                        MaterialIndex =
                            Math.Clamp(
                                section.MaterialIndex,
                                0,
                                materials.Length - 1)
                    })
                .ToArray();

        var geometry =
            new PreviewGeometry(
                Positions:
                    positions,

                Indices:
                    chosen.Indices,

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
                : checked(
                    (int)chosen.SourceLodIndex);

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

    private void TrimPreviewCacheIfNeeded()
    {
        if (_previewCache.Count <= 300)
            return;

        var oldest =
            _previewCache
                .OrderBy(pair =>
                    pair.Value.CreatedAt)
                .Take(75)
                .Select(pair =>
                    pair.Key)
                .ToArray();

        foreach (var key in oldest)
            _previewCache.TryRemove(
                key,
                out _);
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
            _provider = null;

            _manifestVersion = null;
            _studioManifestVersion = null;
            _lastError = null;

            _coreRegistration = null;
            _studioRegistration = null;

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
