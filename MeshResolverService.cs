using System.Collections.Concurrent;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Nanite;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse_Conversion.Meshes;
using CUE4Parse_Conversion.Meshes.PSK;

namespace NovaSparx.Backend;

/// <summary>
/// Universal browser-preview mesh resolver.
///
/// StaticMesh and SkeletalMesh both become the same small NovaSparx preview
/// contract. Skeletal meshes are rendered in their imported/reference pose;
/// bone animation is deliberately not fabricated by the preview API.
/// </summary>
public sealed class MeshResolverService
{
    private readonly LiveProviderService _provider;
    private readonly ILogger<MeshResolverService> _log;

    private readonly SemaphoreSlim _convertGate;

    private readonly ConcurrentDictionary<string, CacheEntry>
        _cache =
            new(StringComparer.OrdinalIgnoreCase);

    private sealed record CacheEntry(
        DateTimeOffset CreatedAt,
        ResolveEnvelope Value);

    private static readonly TimeSpan CacheTtl =
        TimeSpan.FromMinutes(
            int.TryParse(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_MESH_CACHE_MINUTES"),
                out var minutes)
                ? Math.Clamp(minutes, 1, 120)
                : 20);

    private static readonly int MaxVertices =
        int.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_MAX_VERTICES"),
            out var parsedVertices)
            ? Math.Clamp(
                parsedVertices,
                10_000,
                700_000)
            : 320_000;

    private static readonly int MaxIndices =
        int.TryParse(
            Environment.GetEnvironmentVariable(
                "NOVASPARX_MAX_INDICES"),
            out var parsedIndices)
            ? Math.Clamp(
                parsedIndices,
                30_000,
                2_100_000)
            : 960_000;

    public MeshResolverService(
        LiveProviderService provider,
        ILogger<MeshResolverService> log)
    {
        _provider = provider;
        _log = log;

        var concurrency =
            int.TryParse(
                Environment.GetEnvironmentVariable(
                    "NOVASPARX_MESH_CONCURRENCY"),
                out var parsed)
                ? Math.Clamp(parsed, 1, 2)
                : 1;

        _convertGate =
            new SemaphoreSlim(
                concurrency,
                concurrency);
    }

    public int CacheEntries =>
        _cache.Count;

    public void ClearCache() =>
        _cache.Clear();

    public async Task<ResolveEnvelope?>
        ResolveAsync(
            string rawPath,
            CancellationToken cancellationToken)
    {
        var canonical =
            AssetPathResolver.Canonicalize(
                rawPath);

        if (canonical.Length == 0)
            return null;

        if (TryGetCached(
                canonical,
                out var cached))
        {
            return cached;
        }

        var loaded =
            await _provider.LoadObjectAsync(
                rawPath,
                cancellationToken);

        if (loaded is null)
            return null;

        return await ResolveLoadedAsync(
            loaded.Value.Object,
            canonical,
            loaded.Value.ResolvedPath,
            cancellationToken);
    }

    public async Task<ResolveEnvelope?>
        ResolveLoadedAsync(
            UObject value,
            string canonical,
            string resolvedPath,
            CancellationToken cancellationToken)
    {
        canonical =
            AssetPathResolver.Canonicalize(
                canonical);

        if (canonical.Length == 0)
            return null;

        if (TryGetCached(
                canonical,
                out var cached))
        {
            return cached;
        }

        if (value is not UStaticMesh &&
            value is not USkeletalMesh)
        {
            return null;
        }

        await _convertGate.WaitAsync(
            cancellationToken);

        try
        {
            if (TryGetCached(
                    canonical,
                    out cached))
            {
                return cached;
            }

            cancellationToken
                .ThrowIfCancellationRequested();

            var envelope =
                value switch
                {
                    UStaticMesh staticMesh =>
                        BuildStaticMeshEnvelope(
                            staticMesh,
                            canonical,
                            resolvedPath),

                    USkeletalMesh skeletalMesh =>
                        BuildSkeletalMeshEnvelope(
                            skeletalMesh,
                            canonical,
                            resolvedPath),

                    _ => null
                };

            if (envelope is null)
                return null;

            TrimCacheIfNeeded();

            _cache[canonical] =
                new CacheEntry(
                    DateTimeOffset.UtcNow,
                    envelope);

            return envelope;
        }
        finally
        {
            _convertGate.Release();
        }
    }

    private bool TryGetCached(
        string canonical,
        out ResolveEnvelope? value)
    {
        value = null;

        if (!_cache.TryGetValue(
                canonical,
                out var cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow -
            cached.CreatedAt >= CacheTtl)
        {
            _cache.TryRemove(
                canonical,
                out _);

            return false;
        }

        value = cached.Value;
        return true;
    }

    private ResolveEnvelope
        BuildStaticMeshEnvelope(
            UStaticMesh mesh,
            string canonical,
            string resolved)
    {
        if (!mesh.TryConvert(
                out CStaticMesh converted,
                ENaniteMeshFormat.AllLayersNaniteLast))
        {
            throw new InvalidOperationException(
                "CUE4Parse could not convert this StaticMesh.");
        }

        using (converted)
        {
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
                        "StaticMesh LOD {Lod} could not be materialized.",
                        index);
                }
            }

            if (candidates.Count == 0)
            {
                throw new InvalidOperationException(
                    "StaticMesh conversion produced no usable geometry.");
            }

            var selected =
                SelectLod(candidates);

            var chosen =
                selected.Lod;

            var geometry =
                BuildGeometry(
                    chosen);

            var rawSections =
                chosen.Sections?.Value ??
                Array.Empty<CMeshSection>();

            var materials =
                BuildMaterials(
                    rawSections,
                    chosen.IsTwoSided,
                    out var references);

            var sections =
                BuildSections(
                    rawSections,
                    materials,
                    geometry.Indices.Length);

            var fidelity =
                AggregateMaterialFidelity(
                    materials);

            var lodIndex =
                chosen.IsNanite
                    ? -1
                    : selected.Index;

            return Envelope(
                canonical,
                resolved,
                "StaticMesh",
                lodIndex,
                chosen.IsNanite,
                geometry,
                sections,
                materials,
                fidelity,
                references);
        }
    }

    private ResolveEnvelope
        BuildSkeletalMeshEnvelope(
            USkeletalMesh mesh,
            string canonical,
            string resolved)
    {
        if (!mesh.TryConvert(
                out CSkeletalMesh converted))
        {
            throw new InvalidOperationException(
                "CUE4Parse could not convert this SkeletalMesh.");
        }

        if (converted.LODs.Count == 0)
        {
            throw new InvalidOperationException(
                "SkeletalMesh contains no renderable LOD.");
        }

        var candidates =
            new List<(
                CSkelMeshLod Lod,
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
                    "SkeletalMesh LOD {Lod} could not be materialized.",
                    index);
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "SkeletalMesh conversion produced no usable geometry.");
        }

        var selected =
            SelectLod(candidates);

        var chosen =
            selected.Lod;

        var geometry =
            BuildGeometry(
                chosen);

        var rawSections =
            chosen.Sections?.Value ??
            Array.Empty<CMeshSection>();

        var materials =
            BuildMaterials(
                rawSections,
                fallbackTwoSided: false,
                out var references);

        var sections =
            BuildSections(
                rawSections,
                materials,
                geometry.Indices.Length);

        var fidelity =
            AggregateMaterialFidelity(
                materials);

        return Envelope(
            canonical,
            resolved,
            "SkeletalMesh",
            selected.Index,
            isNanite: false,
            geometry,
            sections,
            materials,
            fidelity,
            references);
    }

    private static (
        CStaticMeshLod Lod,
        int Index,
        int VertexCount,
        int IndexCount)
        SelectLod(
            List<(
                CStaticMeshLod Lod,
                int Index,
                int VertexCount,
                int IndexCount)> candidates)
    {
        var selected =
            candidates.FirstOrDefault(
                candidate =>
                    candidate.VertexCount <= MaxVertices &&
                    candidate.IndexCount <= MaxIndices);

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

        EnforceBudget(
            selected.VertexCount,
            selected.IndexCount,
            "StaticMesh");

        return selected;
    }

    private static (
        CSkelMeshLod Lod,
        int Index,
        int VertexCount,
        int IndexCount)
        SelectLod(
            List<(
                CSkelMeshLod Lod,
                int Index,
                int VertexCount,
                int IndexCount)> candidates)
    {
        var selected =
            candidates.FirstOrDefault(
                candidate =>
                    candidate.VertexCount <= MaxVertices &&
                    candidate.IndexCount <= MaxIndices);

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

        EnforceBudget(
            selected.VertexCount,
            selected.IndexCount,
            "SkeletalMesh");

        return selected;
    }

    private static void EnforceBudget(
        int vertices,
        int indices,
        string kind)
    {
        if (vertices <= MaxVertices &&
            indices <= MaxIndices)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The smallest available {kind} LOD is still larger than " +
            "the NovaSparx preview budget " +
            $"({vertices:N0} vertices / {indices:N0} indices).");
    }

    private static PreviewGeometry BuildGeometry(
        CStaticMeshLod chosen)
    {
        var vertices =
            chosen.Verts!;

        var indices =
            chosen.Indices!.Value;

        var vertexCount =
            vertices.Length;

        var positions =
            new float[
                vertexCount * 3];

        var normals =
            new float[
                vertexCount * 3];

        var tangents =
            new float[
                vertexCount * 4];

        var uv0 =
            new float[
                vertexCount * 2];

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

        // Keep the compile-time element type inferred from CUE4Parse. The
        // current conversion package exposes this buffer as Unreal FColor
        // data; naming the element type here would unnecessarily couple
        // NovaSparx to that implementation detail.
        var vertexColors =
            chosen.VertexColors;

        if (vertexColors is not null &&
            vertexColors.Length ==
                vertexCount)
        {
            colors =
                new float[
                    vertexCount * 4];

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

        return new PreviewGeometry(
            Positions: positions,
            Indices: indices,
            Normals: normals,
            Tangents: tangents,
            Uv0: uv0,
            Colors: colors);
    }

    private static PreviewGeometry BuildGeometry(
        CSkelMeshLod chosen)
    {
        var vertices =
            chosen.Verts!;

        var indices =
            chosen.Indices!.Value;

        var vertexCount =
            vertices.Length;

        var positions =
            new float[
                vertexCount * 3];

        var normals =
            new float[
                vertexCount * 3];

        var tangents =
            new float[
                vertexCount * 4];

        var uv0 =
            new float[
                vertexCount * 2];

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

        // Keep the compile-time element type inferred from CUE4Parse. The
        // current conversion package exposes this buffer as Unreal FColor
        // data; naming the element type here would unnecessarily couple
        // NovaSparx to that implementation detail.
        var vertexColors =
            chosen.VertexColors;

        if (vertexColors is not null &&
            vertexColors.Length ==
                vertexCount)
        {
            colors =
                new float[
                    vertexCount * 4];

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

        return new PreviewGeometry(
            Positions: positions,
            Indices: indices,
            Normals: normals,
            Tangents: tangents,
            Uv0: uv0,
            Colors: colors);
    }

    private PreviewMaterial[] BuildMaterials(
        CMeshSection[] rawSections,
        bool fallbackTwoSided,
        out AssetReference[] references)
    {
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
            new PreviewMaterial[
                materialCount];

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

            PreviewMaterial? resolved =
                null;

            try
            {
                if (section?.Material?
                        .Load<UMaterialInterface>() is
                    { } loadedMaterial)
                {
                    resolved =
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
                    "Material slot {MaterialIndex} could not be fully resolved.",
                    materialIndex);
            }

            materials[materialIndex] =
                resolved ??
                new PreviewMaterial(
                    Name: name,
                    Path: null,
                    BaseColor:
                        [1f, 1f, 1f, 1f],
                    Roughness: 0.62f,
                    Metallic: 0f,
                    TwoSided:
                        fallbackTwoSided,
                    Fidelity:
                        "geometry-only",
                    Evidence:
                        "neutral-render-fallback; no resolved material evidence");
        }

        references =
            referenceList.ToArray();

        return materials;
    }

    private static PreviewSection[] BuildSections(
        CMeshSection[] rawSections,
        PreviewMaterial[] materials,
        int totalIndexCount)
    {
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

        if (sections.Length > 0)
            return sections;

        return
        [
            new PreviewSection(
                FirstIndex: 0,
                IndexCount:
                    totalIndexCount,
                MaterialIndex: 0,
                Name:
                    materials[0].Name)
        ];
    }

    private ResolveEnvelope Envelope(
        string canonical,
        string resolved,
        string assetType,
        int lod,
        bool isNanite,
        PreviewGeometry geometry,
        PreviewSection[] sections,
        PreviewMaterial[] materials,
        string materialFidelity,
        AssetReference[] references)
    {
        var health =
            _provider.Health();

        var source =
            health.TextureStreamingReady
                ? "novasparx-hybrid-live+texture-streaming"
                : "novasparx-hybrid-live";

        var manifest =
            new PreviewManifest(
                Path: canonical,
                Lod: lod,
                IsNanite:
                    isNanite,
                Geometry:
                    geometry,
                Sections:
                    sections,
                Materials:
                    materials,
                MaterialFidelity:
                    materialFidelity,
                References:
                    references);

        return new ResolveEnvelope(
            State: "ready",
            Source: source,
            ResolvedPath:
                resolved,
            AssetType:
                assetType,
            Schema:
                "novasparx.preview.v1",
            Version:
                LiveProviderService.BackendVersion,
            ManifestVersion:
                health.ManifestVersion ??
                "unknown",
            Manifest:
                manifest);
    }

    private static string AggregateMaterialFidelity(
        IEnumerable<PreviewMaterial> materials)
    {
        var best = 0;

        foreach (var material in materials)
        {
            var score =
                material.Fidelity
                    .ToLowerInvariant() switch
                {
                    "full" => 6,
                    "high" => 5,
                    "medium" => 4,
                    "partial" => 3,
                    "low" => 2,
                    "geometry-only" => 1,
                    _ => 0
                };

            best =
                Math.Max(
                    best,
                    score);
        }

        return best switch
        {
            6 => "full",
            5 => "high",
            4 => "medium",
            3 => "partial",
            2 => "low",
            1 => "geometry-only",
            _ => "unknown"
        };
    }

    private void TrimCacheIfNeeded()
    {
        if (_cache.Count <= 240)
            return;

        var oldest =
            _cache
                .OrderBy(
                    pair =>
                        pair.Value.CreatedAt)
                .Take(60)
                .Select(
                    pair =>
                        pair.Key)
                .ToArray();

        foreach (var key in oldest)
        {
            _cache.TryRemove(
                key,
                out _);
        }
    }
}
