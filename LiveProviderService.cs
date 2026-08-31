using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Dto;
using CUE4Parse_Conversion.Options;
using EpicManifestParser;
using EpicManifestParser.UE;

namespace NovaSparx.Backend;

public sealed partial class LiveProviderService : IDisposable
{
    public const string BackendVersion = "0.2.0-live-alpha";

    private readonly PublicFortniteSources _sources;
    private readonly ILogger<LiveProviderService> _log;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private readonly SemaphoreSlim _parseGate = new(
        Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("NOVASPARX_PARSE_CONCURRENCY"), out var c) ? Math.Min(c, 2) : 1),
        2);

    private readonly ConcurrentDictionary<string, CacheEntry> _previewCache =
        new(StringComparer.OrdinalIgnoreCase);

    private StreamedFileProvider? _provider;
    private string? _manifestVersion;
    private string? _lastError;
    private DateTimeOffset _lastInit = DateTimeOffset.MinValue;

    private sealed record CacheEntry(DateTimeOffset CreatedAt, ResolveEnvelope Value);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(
        int.TryParse(Environment.GetEnvironmentVariable("NOVASPARX_PREVIEW_CACHE_MINUTES"), out var mins)
            ? Math.Clamp(mins, 1, 120)
            : 20);

    public LiveProviderService(PublicFortniteSources sources, ILogger<LiveProviderService> log)
    {
        _sources = sources;
        _log = log;
    }

    public bool IsReady => _provider is not null;
    public string? ManifestVersion => _manifestVersion;
    public string? LastError => _lastError;

    public ProviderHealth Health()
    {
        var p = _provider;
        return new ProviderHealth(
            Ok: p is not null,
            Service: "NovaSparx.Backend",
            Version: BackendVersion,
            ProviderReady: p is not null,
            Mode: "FortniteLive/StreamedFileProvider",
            ManifestVersion: _manifestVersion,
            RegisteredArchives: p?.UnloadedVfs.Count + p?.MountedVfs.Count ?? 0,
            MountedArchives: p?.MountedVfs.Count ?? 0,
            IndexedFiles: p?.Files.Count ?? 0,
            RequiredKeys: p?.RequiredKeys.Count ?? 0,
            LoadedKeys: p?.Keys.Count ?? 0,
            LastError: _lastError
        );
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (_provider is not null) return;

        await _initGate.WaitAsync(cancellationToken);
        try
        {
            if (_provider is not null) return;

            _lastError = null;
            var started = DateTimeOffset.UtcNow;

            try
            {
                var (manifest, version) = await _sources.GetLiveManifestAsync(cancellationToken);
                _manifestVersion = version;

                var versions = new VersionContainer(EGame.GAME_UE5_LATEST);
                var provider = new StreamedFileProvider(
                    "FortniteLive",
                    versions,
                    StringComparer.OrdinalIgnoreCase);

                provider.ReadNaniteData = true;
                provider.ReadShaderMaps = false;
                provider.ReadScriptData = false;
                provider.UseLazyPackageSerialization = true;

                provider.OnDemandOptions = new IoStoreOnDemandOptions
                {
                    ChunkHostUri = new Uri(
                        Environment.GetEnvironmentVariable("NOVASPARX_ONDEMAND_HOST")
                        ?? "https://egdownload.fastly-edge.com/",
                        UriKind.Absolute),
                    ChunkCacheDirectory = new DirectoryInfo(_sources.ChunkCache),
                    DownloaderClient = new HttpClient()
                };

                var mappings = await _sources.GetMappingsAsync(cancellationToken);
                if (mappings is not null)
                    provider.MappingsContainer = mappings;

                await RegisterFortniteLiveArchives(provider, manifest, cancellationToken);

                provider.Initialize();

                IReadOnlyList<KeyValuePair<FGuid, FAesKey>> keys = [];
                try
                {
                    keys = await _sources.GetAesKeysAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "AES endpoint failed. Unencrypted archives can still mount.");
                }

                if (keys.Count > 0)
                    provider.SubmitKeys(keys);

                provider.PostMount();

                _provider = provider;
                _lastInit = DateTimeOffset.UtcNow;

                _log.LogInformation(
                    "NovaSparx Live ready in {Seconds:F1}s. Manifest={Manifest}; Archives={Archives}; Mounted={Mounted}; Files={Files}; Keys={Keys}/{Required}",
                    (_lastInit - started).TotalSeconds,
                    _manifestVersion,
                    provider.UnloadedVfs.Count + provider.MountedVfs.Count,
                    provider.MountedVfs.Count,
                    provider.Files.Count,
                    provider.Keys.Count,
                    provider.RequiredKeys.Count);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _provider?.Dispose();
                _provider = null;
                _log.LogError(ex, "NovaSparx Live initialization failed.");
                throw;
            }
        }
        finally
        {
            _initGate.Release();
        }
    }

    private async Task RegisterFortniteLiveArchives(
        StreamedFileProvider provider,
        FBuildPatchAppManifest manifest,
        CancellationToken cancellationToken)
    {
        var archiveFiles = manifest.Files
            .Where(x =>
                FnPakRegex().IsMatch(x.FileName) &&
                (x.FileName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                 x.FileName.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase) ||
                 x.FileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var normal = archiveFiles
            .Where(x => !x.FileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var maxParallel = int.TryParse(
            Environment.GetEnvironmentVariable("NOVASPARX_ARCHIVE_REGISTER_CONCURRENCY"),
            out var parsed)
            ? Math.Clamp(parsed, 1, 8)
            : 3;

        await Parallel.ForEachAsync(
            normal,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallel,
                CancellationToken = cancellationToken
            },
            (fileManifest, _) =>
            {
                provider.RegisterVfs(
                    fileManifest.FileName,
                    [fileManifest.GetStream()],
                    it => new FRandomAccessStreamArchive(
                        it,
                        manifest.FindFile(it)!.GetStream(),
                        provider.Versions));

                return ValueTask.CompletedTask;
            });

        // Match FModel's current approach for V2 on-demand TOCs:
        // materialize the relatively small TOC itself through EpicManifestParser's parallel
        // chunk path, then register IoChunkToc. The actual payload chunks remain on-demand.
        foreach (var fileManifest in archiveFiles.Where(
                     x => x.FileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var stream = fileManifest.GetStream();
            var degree = Math.Max(1, Math.Min(Environment.ProcessorCount, 4));
            var data = await stream.SaveBytesAsync(degree, cancellationToken);

            using var archive = new FByteArchive(fileManifest.FileName, data, provider.Versions);
            provider.RegisterVfs(new IoChunkToc(archive));
        }
    }

    public async Task<ResolveEnvelope?> ResolveAsync(
        string rawPath,
        CancellationToken cancellationToken)
    {
        await EnsureReadyAsync(cancellationToken);

        var canonical = AssetPathResolver.Canonicalize(rawPath);
        if (canonical.Length == 0) return null;

        if (_previewCache.TryGetValue(canonical, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAt < CacheTtl)
            return cached.Value;

        await _parseGate.WaitAsync(cancellationToken);
        try
        {
            if (_previewCache.TryGetValue(canonical, out cached) &&
                DateTimeOffset.UtcNow - cached.CreatedAt < CacheTtl)
                return cached.Value;

            var provider = _provider ?? throw new InvalidOperationException("Provider is not ready.");
            UObject? loaded = null;
            string? resolved = null;

            foreach (var candidate in AssetPathResolver.LoadCandidates(rawPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    loaded = provider.SafeLoadPackageObject(candidate);
                    if (loaded is not null)
                    {
                        resolved = candidate;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Load candidate failed: {Candidate}", candidate);
                }
            }

            if (loaded is not UStaticMesh mesh)
                return null;

            var envelope = BuildStaticMeshEnvelope(mesh, canonical, resolved ?? canonical);

            if (_previewCache.Count > 250)
            {
                var oldest = _previewCache
                    .OrderBy(x => x.Value.CreatedAt)
                    .Take(50)
                    .Select(x => x.Key)
                    .ToArray();

                foreach (var key in oldest)
                    _previewCache.TryRemove(key, out _);
            }

            _previewCache[canonical] = new CacheEntry(DateTimeOffset.UtcNow, envelope);
            return envelope;
        }
        finally
        {
            _parseGate.Release();
        }
    }

    private ResolveEnvelope BuildStaticMeshEnvelope(
        UStaticMesh mesh,
        string canonical,
        string resolved)
    {
        // Parse every normal LOD plus Nanite at the end. Then choose the highest-quality
        // LOD that stays inside the HTTP/browser geometry budget.
        using var dto = new StaticMeshDto(
            mesh,
            EMeshQuality.All,
            ENaniteMeshFormat.NaniteLast);

        if (dto.LODs.Count == 0)
            throw new InvalidOperationException("StaticMesh contains no renderable LODs.");

        var maxVertices = int.TryParse(Environment.GetEnvironmentVariable("NOVASPARX_MAX_VERTICES"), out var v)
            ? Math.Clamp(v, 10_000, 700_000)
            : 260_000;

        var maxIndices = int.TryParse(Environment.GetEnvironmentVariable("NOVASPARX_MAX_INDICES"), out var i)
            ? Math.Clamp(i, 30_000, 2_100_000)
            : 780_000;

        var chosen = dto.LODs
            .FirstOrDefault(x => x.Vertices.Length <= maxVertices && x.Indices.Length <= maxIndices)
            ?? dto.LODs
                .OrderBy(x => x.Vertices.Length)
                .ThenBy(x => x.Indices.Length)
                .First();

        if (chosen.Vertices.Length > maxVertices || chosen.Indices.Length > maxIndices)
        {
            throw new InvalidOperationException(
                $"Smallest available LOD is still too large for NovaSparx HTTP preview " +
                $"({chosen.Vertices.Length:N0} vertices / {chosen.Indices.Length:N0} indices).");
        }

        var positions = new float[chosen.Vertices.Length * 3];
        var normals = new float[chosen.Vertices.Length * 3];
        var tangents = new float[chosen.Vertices.Length * 4];
        var uv0 = new float[chosen.Vertices.Length * 2];

        for (var n = 0; n < chosen.Vertices.Length; n++)
        {
            var vertex = chosen.Vertices[n];

            positions[n * 3 + 0] = (float)vertex.Position.X;
            positions[n * 3 + 1] = (float)vertex.Position.Y;
            positions[n * 3 + 2] = (float)vertex.Position.Z;

            normals[n * 3 + 0] = (float)vertex.Normal.X;
            normals[n * 3 + 1] = (float)vertex.Normal.Y;
            normals[n * 3 + 2] = (float)vertex.Normal.Z;

            tangents[n * 4 + 0] = (float)vertex.Tangent.X;
            tangents[n * 4 + 1] = (float)vertex.Tangent.Y;
            tangents[n * 4 + 2] = (float)vertex.Tangent.Z;
            tangents[n * 4 + 3] = (float)vertex.Tangent.W;

            uv0[n * 2 + 0] = vertex.Uv.U;
            uv0[n * 2 + 1] = vertex.Uv.V;
        }

        float[]? colors = null;
        var colorSet = chosen.VertexColors?.FirstOrDefault();
        if (colorSet is not null && colorSet.Colors.Length == chosen.Vertices.Length)
        {
            colors = new float[chosen.Vertices.Length * 4];
            for (var n = 0; n < colorSet.Colors.Length; n++)
            {
                var c = colorSet.Colors[n];
                colors[n * 4 + 0] = c.R / 255f;
                colors[n * 4 + 1] = c.G / 255f;
                colors[n * 4 + 2] = c.B / 255f;
                colors[n * 4 + 3] = c.A / 255f;
            }
        }

        var sections = chosen.Sections
            .Select(section => new PreviewSection(
                FirstIndex: checked((int)section.FirstIndex),
                IndexCount: checked((int)section.NumFaces * 3),
                MaterialIndex: section.MaterialIndex,
                Name: dto.GetMaterial(section)?.SlotName ?? $"Material_{section.MaterialIndex}"))
            .ToArray();

        var materials = dto.Materials
            .Select(material =>
            {
                string? path = null;

                try
                {
                    if (material.Material?.TryLoad<UMaterialInterface>(out var loadedMaterial) == true)
                        path = loadedMaterial.GetPathName();
                }
                catch
                {
                    // Path enrichment is optional; geometry must still succeed.
                }

                return new PreviewMaterial(
                    Name: material.SlotName,
                    Path: path,
                    BaseColor: [1f, 1f, 1f, 1f],
                    Roughness: 0.62f,
                    Metallic: 0f,
                    TwoSided: chosen.IsTwoSided);
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

        var geometry = new PreviewGeometry(
            Positions: positions,
            Indices: chosen.Indices,
            Normals: normals,
            Tangents: tangents,
            Uv0: uv0,
            Colors: colors);

        var lodIndex = chosen.IsNanite
            ? -1
            : checked((int)chosen.SourceLodIndex);

        var manifest = new PreviewManifest(
            Path: canonical,
            Lod: lodIndex,
            IsNanite: chosen.IsNanite,
            Geometry: geometry,
            Sections: sections,
            Materials: materials);

        return new ResolveEnvelope(
            State: "ready",
            Source: "novasparx-live-cue4parse",
            ResolvedPath: resolved,
            AssetType: "StaticMesh",
            Schema: "novasparx.preview.v1",
            Version: BackendVersion,
            ManifestVersion: _manifestVersion ?? "unknown",
            Manifest: manifest);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _initGate.WaitAsync(cancellationToken);
        try
        {
            _previewCache.Clear();
            _provider?.Dispose();
            _provider = null;
            _manifestVersion = null;
            _lastError = null;
        }
        finally
        {
            _initGate.Release();
        }

        await EnsureReadyAsync(cancellationToken);
    }

    public void Dispose()
    {
        _provider?.Dispose();
        _initGate.Dispose();
        _parseGate.Dispose();
    }

    [GeneratedRegex(@"^FortniteGame[/\\]Content[/\\]Paks[/\\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FnPakRegex();
}
