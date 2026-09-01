using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;

namespace NovaSparx.Backend;

/// <summary>
/// Universal evidence inspector used by FNAA Description and References.
///
/// Dedicated material/mesh resolvers are used when available. Every other
/// UObject still gets compact real property metadata and explicit path
/// references from AssetReferenceScanner, so Blueprint/Niagara/etc. do not
/// fall back to name-only guessing merely because they are not visual meshes.
/// </summary>
public sealed class AssetInspectorService
{
    private readonly LiveProviderService _provider;
    private readonly MeshResolverService _meshes;
    private readonly ILogger<AssetInspectorService> _log;

    public AssetInspectorService(
        LiveProviderService provider,
        MeshResolverService meshes,
        ILogger<AssetInspectorService> log)
    {
        _provider = provider;
        _meshes = meshes;
        _log = log;
    }

    public async Task<AssetInspection?>
        InspectAsync(
            string rawPath,
            CancellationToken cancellationToken)
    {
        var canonical =
            AssetPathResolver.Canonicalize(
                rawPath);

        if (canonical.Length == 0)
            return null;

        var loaded =
            await _provider.LoadObjectAsync(
                rawPath,
                cancellationToken);

        if (loaded is null)
            return null;

        var value =
            loaded.Value.Object;

        var scan =
            AssetReferenceScanner.Scan(
                value);

        var facts =
            new Dictionary<string, object?>(
                scan.Facts,
                StringComparer.OrdinalIgnoreCase);

        var references =
            new List<AssetReference>();

        var referenceSet =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        void AddReferences(
            IEnumerable<AssetReference>? source)
        {
            if (source is null)
                return;

            foreach (var reference in source)
            {
                if (string.IsNullOrWhiteSpace(
                        reference.Path))
                {
                    continue;
                }

                // "self" is useful internally but is not a dependency.
                if (reference.Kind.Equals(
                        "self",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var key =
                    $"{reference.Kind}|{reference.Path}";

                if (referenceSet.Add(key))
                    references.Add(reference);
            }
        }

        AddReferences(
            scan.References);

        PreviewMaterial? material =
            null;

        PreviewMaterial[] materials =
            [];

        var materialFidelity =
            "unknown";

        var assetType =
            FriendlyAssetType(
                value);

        if (value is UUnrealMaterial unrealMaterial)
        {
            try
            {
                material =
                    MaterialResolver.Resolve(
                        unrealMaterial);

                materials =
                    [material];

                materialFidelity =
                    material.Fidelity;

                AddReferences(
                    MaterialResolver.CollectReferences(
                        unrealMaterial));

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

                facts["materialEvidence"] =
                    material.Evidence;
            }
            catch (Exception ex)
            {
                _log.LogDebug(
                    ex,
                    "Material inspection failed for {Path}.",
                    canonical);
            }
        }

        if (value is UStaticMesh or USkeletalMesh)
        {
            try
            {
                var envelope =
                    await _meshes.ResolveLoadedAsync(
                        value,
                        canonical,
                        loaded.Value.ResolvedPath,
                        cancellationToken);

                if (envelope is not null)
                {
                    materials =
                        envelope.Manifest.Materials;

                    materialFidelity =
                        envelope.Manifest.MaterialFidelity;

                    AddReferences(
                        envelope.Manifest.References);

                    facts["lod"] =
                        envelope.Manifest.Lod;

                    facts["nanite"] =
                        envelope.Manifest.IsNanite;

                    facts["vertices"] =
                        envelope.Manifest.Geometry
                            .Positions.Length / 3;

                    facts["triangles"] =
                        envelope.Manifest.Geometry
                            .Indices.Length / 3;

                    facts["sections"] =
                        envelope.Manifest.Sections.Length;

                    facts["renderablePreview"] =
                        true;

                    facts["previewSchema"] =
                        envelope.Schema;
                }
            }
            catch (Exception ex)
            {
                // Inspection should remain useful even if a huge or malformed
                // mesh cannot fit the browser preview budget.
                facts["renderablePreview"] =
                    false;

                facts["previewError"] =
                    ex.Message;

                _log.LogDebug(
                    ex,
                    "Mesh inspection preview pass failed for {Path}.",
                    canonical);
            }
        }

        if (value is USkeletalMesh skeletalMesh)
        {
            facts["bones"] =
                skeletalMesh
                    .ReferenceSkeleton
                    .FinalRefBoneInfo
                    .Length;

            facts["sourceLods"] =
                skeletalMesh
                    .LODModels
                    ?.Length ?? 0;

            facts["previewPose"] =
                "imported-reference-pose";
        }

        var runtime =
            value.GetType().Name;

        if (runtime.Contains(
                "Niagara",
                StringComparison.OrdinalIgnoreCase))
        {
            facts["metadataFamily"] =
                "Niagara";

            facts["visualPreviewPolicy"] =
                "metadata-only-until-deterministic-vfx-renderer";
        }
        else if (
            runtime.Contains(
                "Blueprint",
                StringComparison.OrdinalIgnoreCase) ||
            runtime.Contains(
                "GeneratedClass",
                StringComparison.OrdinalIgnoreCase))
        {
            facts["metadataFamily"] =
                "Blueprint";
        }
        else if (
            runtime.Contains(
                "Anim",
                StringComparison.OrdinalIgnoreCase))
        {
            facts["metadataFamily"] =
                "Animation";
        }
        else if (
            runtime.Contains(
                "Sound",
                StringComparison.OrdinalIgnoreCase) ||
            runtime.Contains(
                "Audio",
                StringComparison.OrdinalIgnoreCase))
        {
            facts["metadataFamily"] =
                "Audio";
        }

        var health =
            _provider.Health();

        var source =
            health.TextureStreamingReady
                ? "novasparx-hybrid-live+texture-streaming"
                : "novasparx-hybrid-live";

        return new AssetInspection(
            State:
                "ready",

            Path:
                canonical,

            ResolvedPath:
                loaded.Value.ResolvedPath,

            AssetType:
                assetType,

            Source:
                source,

            MaterialFidelity:
                materialFidelity,

            Material:
                material,

            Materials:
                materials,

            References:
                references
                    .Take(240)
                    .ToArray(),

            Facts:
                facts);
    }

    private static string FriendlyAssetType(
        UObject value)
    {
        return value switch
        {
            UStaticMesh =>
                "StaticMesh",

            USkeletalMesh =>
                "SkeletalMesh",

            UMaterialInstanceConstant =>
                "MaterialInstanceConstant",

            UMaterialInstance =>
                "MaterialInstance",

            UMaterial =>
                "Material",

            UMaterialInterface =>
                "MaterialInterface",

            UUnrealMaterial =>
                "Material",

            _ =>
                TrimLeadingU(
                    value.GetType().Name)
        };
    }

    private static string TrimLeadingU(
        string value)
    {
        return value.Length > 1 &&
               value[0] == 'U' &&
               char.IsUpper(value[1])
            ? value[1..]
            : value;
    }
}
