namespace NovaSparx.Backend;

/// <summary>
/// Selects the strongest honest visual representation that NovaSparx can
/// produce for any Fortnite asset.
///
/// Layer order:
/// 1. the requested asset as a CUE4Parse mesh;
/// 2. verified texture/mesh references exposed by CUE4Parse;
/// 3. one level of referenced material inspection;
/// 4. metadata evidence for FNAA's deterministic PNG card.
///
/// The service never invents geometry, colors, or textures. Missing materials
/// are handled by MeshResolverService's explicit neutral-render fallback.
/// </summary>
public sealed class PreviewResolverService
{
    private const int MaxAttempts = 12;
    private const int MaxMaterialExpansions = 3;

    private readonly MeshResolverService _meshes;
    private readonly AssetInspectorService _inspector;
    private readonly TextureService _textures;
    private readonly ILogger<PreviewResolverService> _log;

    private sealed record Candidate(
        string Mode,
        string Kind,
        string Path,
        int Score);

    public PreviewResolverService(
        MeshResolverService meshes,
        AssetInspectorService inspector,
        TextureService textures,
        ILogger<PreviewResolverService> log)
    {
        _meshes = meshes;
        _inspector = inspector;
        _textures = textures;
        _log = log;
    }

    public async Task<UniversalPreviewPlan> ResolveAsync(
        string rawPath,
        CancellationToken cancellationToken)
    {
        var canonical =
            AssetPathResolver.Canonicalize(
                rawPath);

        if (canonical.Length == 0)
        {
            throw new ArgumentException(
                "Asset path is required.",
                nameof(rawPath));
        }

        ResolveEnvelope? directMesh =
            null;

        try
        {
            directMesh =
                await _meshes.ResolveAsync(
                    rawPath,
                    cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogDebug(
                ex,
                "Direct universal preview mesh failed for {Path}.",
                canonical);
        }

        if (directMesh is not null)
        {
            return MeshPlan(
                canonical,
                directMesh,
                inspection: null,
                attempted: [],
                source:
                    "cue4parse-direct-mesh",
                evidence:
                    "CUE4Parse converted the requested asset to verified preview geometry.");
        }

        var inspection =
            await _inspector.InspectAsync(
                rawPath,
                cancellationToken);

        if (inspection is null)
        {
            return MetadataPlan(
                canonical,
                assetType: "Unknown",
                inspection: null,
                attempted: [],
                evidence:
                    "The asset could not be loaded; only the normalized path is verified.");
        }

        var candidates =
            BuildCandidates(
                inspection);

        var queued =
            new HashSet<string>(
                candidates.Select(
                    CandidateKey),
                StringComparer.OrdinalIgnoreCase);

        var attempted =
            new List<AssetReference>();

        var materialExpansions = 0;

        for (var index = 0;
             index < candidates.Count &&
             attempted.Count < MaxAttempts;
             index++)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var candidate =
                candidates[index];

            attempted.Add(
                new AssetReference(
                    Kind:
                        $"preview-{candidate.Mode}:{candidate.Kind}",
                    Path:
                        candidate.Path));

            try
            {
                if (candidate.Mode == "texture")
                {
                    var texture =
                        await _textures.DecodePngAsync(
                            candidate.Path,
                            cancellationToken);

                    if (texture is not null)
                    {
                        return new UniversalPreviewPlan(
                            State: "ready",
                            Kind: "texture",
                            RequestedPath: canonical,
                            PreviewPath: texture.Path,
                            AssetType: inspection.AssetType,
                            Source:
                                "cue4parse-referenced-texture",
                            Evidence:
                                $"CUE4Parse verified a renderable texture reference ({candidate.Kind}).",
                            Mesh: null,
                            Inspection: inspection,
                            AttemptedReferences:
                                attempted.ToArray(),
                            TextureWidth: texture.Width,
                            TextureHeight: texture.Height);
                    }
                }
                else if (candidate.Mode == "mesh")
                {
                    var mesh =
                        await _meshes.ResolveAsync(
                            candidate.Path,
                            cancellationToken);

                    if (mesh is not null)
                    {
                        return MeshPlan(
                            canonical,
                            mesh,
                            inspection,
                            attempted,
                            source:
                                "cue4parse-referenced-mesh",
                            evidence:
                                $"CUE4Parse followed a verified mesh reference ({candidate.Kind}); unresolved materials use the neutral geometry fallback.");
                    }
                }
                else if (
                    candidate.Mode == "material" &&
                    materialExpansions++ <
                        MaxMaterialExpansions)
                {
                    var materialInspection =
                        await _inspector.InspectAsync(
                            candidate.Path,
                            cancellationToken);

                    if (materialInspection is null)
                        continue;

                    var nested =
                        BuildCandidates(
                                materialInspection)
                            .Where(
                                item =>
                                    item.Mode !=
                                    "material")
                            .Take(6)
                            .ToArray();

                    foreach (var item in nested)
                    {
                        var adjusted =
                            item with
                            {
                                Score =
                                    Math.Max(
                                        item.Score,
                                        candidate.Score + 40)
                            };

                        if (queued.Add(
                                CandidateKey(
                                    adjusted)))
                        {
                            candidates.Insert(
                                Math.Min(
                                    index + 1,
                                    candidates.Count),
                                adjusted);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Universal preview candidate failed: {Mode} {Path}",
                    candidate.Mode,
                    candidate.Path);
            }
        }

        return MetadataPlan(
            canonical,
            inspection.AssetType,
            inspection,
            attempted,
            evidence:
                "CUE4Parse found no decodable texture or renderable mesh reference; verified metadata remains available.");
    }

    private static UniversalPreviewPlan MeshPlan(
        string requestedPath,
        ResolveEnvelope mesh,
        AssetInspection? inspection,
        IEnumerable<AssetReference> attempted,
        string source,
        string evidence)
    {
        return new UniversalPreviewPlan(
            State: "ready",
            Kind: "mesh",
            RequestedPath: requestedPath,
            PreviewPath: mesh.ResolvedPath,
            AssetType:
                inspection?.AssetType ??
                mesh.AssetType,
            Source: source,
            Evidence: evidence,
            Mesh: mesh,
            Inspection: inspection,
            AttemptedReferences:
                attempted.ToArray());
    }

    private static UniversalPreviewPlan MetadataPlan(
        string requestedPath,
        string assetType,
        AssetInspection? inspection,
        IEnumerable<AssetReference> attempted,
        string evidence)
    {
        return new UniversalPreviewPlan(
            State: "ready",
            Kind: "metadata",
            RequestedPath: requestedPath,
            PreviewPath: string.Empty,
            AssetType: assetType,
            Source:
                "cue4parse-evidence-card",
            Evidence: evidence,
            Mesh: null,
            Inspection: inspection,
            AttemptedReferences:
                attempted.ToArray());
    }

    private static List<Candidate> BuildCandidates(
        AssetInspection inspection)
    {
        var output =
            new List<Candidate>();

        var seen =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        void Add(
            string mode,
            string kind,
            string? rawPath,
            int score)
        {
            var path =
                AssetPathResolver.Canonicalize(
                    rawPath ?? string.Empty);

            if (path.Length == 0 ||
                path.Equals(
                    inspection.Path,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var candidate =
                new Candidate(
                    Mode: mode,
                    Kind: kind,
                    Path: path,
                    Score: score);

            if (seen.Add(
                    CandidateKey(
                        candidate)))
            {
                output.Add(candidate);
            }
        }

        foreach (var material in
                 inspection.Materials)
        {
            Add(
                "texture",
                "material-base-color",
                material.BaseColorTexture,
                700);

            Add(
                "texture",
                "material-emissive",
                material.EmissiveTexture,
                660);

            Add(
                "texture",
                "material-opacity",
                material.OpacityTexture,
                610);

            Add(
                "texture",
                "material-packed",
                material.PackedTexture,
                560);

            Add(
                "texture",
                "material-normal",
                material.NormalTexture,
                420);
        }

        foreach (var reference in
                 inspection.References)
        {
            var path =
                AssetPathResolver.Canonicalize(
                    reference.Path);

            if (path.Length == 0)
                continue;

            var leaf =
                path[(path.LastIndexOf('/') + 1)..]
                    .Split('.')[0]
                    .ToLowerInvariant();

            var kind =
                reference.Kind
                    .ToLowerInvariant();

            var iconLike =
                ContainsAny(
                    $"{kind} {leaf}",
                    "icon",
                    "thumbnail",
                    "preview",
                    "display",
                    "gallery",
                    "portrait",
                    "keyart");

            var textureLike =
                iconLike ||
                ContainsAny(
                    kind,
                    "texture",
                    "diffuse",
                    "albedo",
                    "basecolor",
                    "emissive") ||
                leaf.StartsWith("t_") ||
                leaf.StartsWith("tex_") ||
                leaf.StartsWith("ui_");

            var meshLike =
                ContainsAny(
                    kind,
                    "staticmesh",
                    "skeletalmesh",
                    "mesh") ||
                leaf.StartsWith("sm_") ||
                leaf.StartsWith("sk_");

            var materialLike =
                ContainsAny(
                    kind,
                    "material") ||
                leaf.StartsWith("mi_") ||
                leaf.StartsWith("m_");

            if (textureLike)
            {
                Add(
                    "texture",
                    reference.Kind,
                    path,
                    iconLike
                        ? 640
                        : 500);
            }

            if (meshLike)
            {
                Add(
                    "mesh",
                    reference.Kind,
                    path,
                    520);
            }

            if (materialLike)
            {
                Add(
                    "material",
                    reference.Kind,
                    path,
                    300);
            }
        }

        return output
            .OrderByDescending(
                candidate =>
                    candidate.Score)
            .ThenBy(
                candidate =>
                    candidate.Path,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CandidateKey(
        Candidate candidate)
    {
        return
            $"{candidate.Mode}|{candidate.Path}";
    }

    private static bool ContainsAny(
        string value,
        params string[] terms)
    {
        return terms.Any(
            term =>
                value.Contains(
                    term,
                    StringComparison.OrdinalIgnoreCase));
    }
}

