using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Core.Math;

namespace NovaSparx.Backend;

/// <summary>
/// Converts CUE4Parse material data into NovaSparx's browser-safe material model.
///
/// Important:
/// - CUE4Parse does the actual parent-chain/material parameter resolution.
/// - NovaSparx never invents a texture or color that CUE4Parse did not expose.
/// - Packed channel layouts are only filled when the parameter name itself
///   gives us a known packing convention.
/// </summary>
public static class MaterialResolver
{
    public static PreviewMaterial Resolve(
        UUnrealMaterial material,
        string? slotName = null)
    {
        ArgumentNullException.ThrowIfNull(material);

        var semantic = new CMaterialParams();
        var rich = new CMaterialParams2();

        material.GetParams(semantic);
        material.GetParams(
            rich,
            EMaterialFormat.AllLayersNoRef);

        var baseMaterial = FindBaseMaterial(material);

        var materialPath = SafePath(material);
        var name =
            !string.IsNullOrWhiteSpace(slotName)
                ? slotName!
                : NameFromPath(materialPath);

        var diffuse = semantic.Diffuse;
        var normal = semantic.Normal;
        var emissive = semantic.Emissive;
        var opacity = semantic.Opacity;

        diffuse ??= FindTexture(
            rich,
            "basecolor",
            "base color",
            "diffuse",
            "albedo",
            "color texture",
            "texture_bc");

        normal ??= FindTexture(
            rich,
            "normal",
            "nrm",
            "_nm",
            "texture_n");

        emissive ??= FindTexture(
            rich,
            "emissive",
            "emiss");

        opacity ??= FindTexture(
            rich,
            "opacity",
            "opac",
            "alpha mask");

        var packedEntry = FindTextureEntry(
            rich,
            "packed",
            "specularmask",
            "specular masks",
            "orm",
            "mrao",
            "mra",
            "rmao",
            "occlusionroughnessmetallic",
            "metalroughocc");

        var packed =
            semantic.Mask ??
            semantic.Specular ??
            packedEntry.Texture;

        var baseColor =
            semantic.DiffuseColor is { } diffuseColor
                ? ColorArray(diffuseColor)
                : FindColor(
                    rich,
                    new[] {
                        "basecolor",
                        "base color",
                        "diffusecolor",
                        "albedo",
                        "color"
                    },
                    new[] { 1f, 1f, 1f, 1f });

        var emissiveColor =
            semantic.EmissiveColor is { } emission
                ? ColorArray(emission)
                : FindColor(
                    rich,
                    new[] {
                        "emissivecolor",
                        "emissive color",
                        "emissive"
                    },
                    new[] { 0f, 0f, 0f, 1f });

        var roughness =
            FindScalar(
                rich,
                new[] { "roughness", "roughnessvalue" },
                semantic.RoughnessValue);

        var metallic =
            FindScalar(
                rich,
                new[] { "metallic", "metalness" },
                semantic.MetallicValue);

        var specular =
            FindScalar(
                rich,
                new[] { "specular", "specularvalue" },
                semantic.SpecularValue <= 0f
                    ? 0.5f
                    : semantic.SpecularValue);

        var opacityValue =
            FindScalar(
                rich,
                new[] { "opacity", "opacityvalue" },
                1f);

        var opacityMode = "opaque";
        var opacityCutoff = 0.333f;
        var twoSided = false;

        if (baseMaterial is not null)
        {
            twoSided = baseMaterial.TwoSided;
            opacityCutoff = baseMaterial.OpacityMaskClipValue;

            if (baseMaterial.bIsMasked)
                opacityMode = "masked";
            else if (
                baseMaterial.BlendMode == EBlendMode.BLEND_Translucent ||
                semantic.IsTransparent ||
                rich.IsTranslucent)
                opacityMode = "translucent";
        }
        else if (semantic.IsTransparent || rich.IsTranslucent)
        {
            opacityMode = "translucent";
        }

        var packedChannels =
            InferPackedChannels(
                packedEntry.Name,
                packed is not null);

        var references = CollectReferences(material);

        var textureCount = new[]
        {
            diffuse,
            normal,
            emissive,
            opacity,
            packed
        }.Count(texture => texture is not null);

        var fidelity =
            textureCount >= 2 && diffuse is not null && normal is not null
                ? "high"
                : textureCount >= 1 && diffuse is not null
                    ? "medium"
                    : textureCount >= 1
                        ? "partial"
                        : (semantic.DiffuseColor is not null ||
                           rich.Colors.Count > 0 ||
                           rich.Scalars.Count > 0)
                            ? "low"
                            : "unknown";

        var evidenceParts = new List<string>
        {
            $"semantic-textures={textureCount}",
            $"rich-textures={rich.Textures.Count}",
            $"scalars={rich.Scalars.Count}",
            $"colors={rich.Colors.Count}"
        };

        if (baseMaterial is not null)
            evidenceParts.Add("base-material=resolved");

        if (packedChannels is { Ao: >= 0 } ||
            packedChannels is { Roughness: >= 0 } ||
            packedChannels is { Metallic: >= 0 })
        {
            evidenceParts.Add(
                $"packed-layout=inferred-from-parameter:{packedEntry.Name}");
        }

        return new PreviewMaterial(
            Name: name,
            Path: materialPath,
            BaseColor: baseColor,
            Roughness: Clamp01(roughness),
            Metallic: Clamp01(metallic),
            TwoSided: twoSided,

            EmissiveColor: emissiveColor,
            Specular: Clamp01(specular),
            Opacity: Clamp01(opacityValue),
            OpacityMode: opacityMode,
            OpacityCutoff: Clamp01(opacityCutoff),
            UseVertexColor: UsesVertexColor(rich),

            UvScale: new[] { 1f, 1f },
            UvOffset: new[] { 0f, 0f },

            BaseColorTexture: SafePath(diffuse),
            NormalTexture: SafePath(normal),
            EmissiveTexture: SafePath(emissive),
            OpacityTexture: SafePath(opacity),
            PackedTexture: SafePath(packed),

            PackedChannels: packedChannels,

            Fidelity: fidelity,
            Evidence: string.Join("; ", evidenceParts)
        );
    }

    public static AssetReference[] CollectReferences(
        UUnrealMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);

        var references = new List<AssetReference>();
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        void Add(string kind, UUnrealMaterial? value)
        {
            if (value is null)
                return;

            var path = SafePath(value);
            if (string.IsNullOrWhiteSpace(path))
                return;

            if (!seen.Add(path))
                return;

            references.Add(
                new AssetReference(
                    Kind: kind,
                    Path: path));
        }

        Add("material", material);

        var current = material;
        var depth = 0;

        while (
            current is UMaterialInstance instance &&
            instance.Parent is { } parent &&
            !ReferenceEquals(parent, current) &&
            depth++ < 16)
        {
            Add("material-parent", parent);
            current = parent;
        }

        var semantic = new CMaterialParams();
        material.GetParams(semantic);

        Add("texture-diffuse", semantic.Diffuse);
        Add("texture-normal", semantic.Normal);
        Add("texture-specular", semantic.Specular);
        Add("texture-specular-power", semantic.SpecPower);
        Add("texture-opacity", semantic.Opacity);
        Add("texture-emissive", semantic.Emissive);
        Add("texture-mask", semantic.Mask);
        Add("texture-misc", semantic.Misc);

        var rich = new CMaterialParams2();
        material.GetParams(
            rich,
            EMaterialFormat.AllLayersNoRef);

        foreach (var pair in rich.Textures)
            Add($"texture-param:{pair.Key}", pair.Value);

        return references.ToArray();
    }

    private static UMaterial? FindBaseMaterial(
        UUnrealMaterial material)
    {
        var current = material;
        var visited = new HashSet<UUnrealMaterial>();
        var depth = 0;

        while (
            current is not null &&
            visited.Add(current) &&
            depth++ < 16)
        {
            if (current is UMaterial unrealMaterial)
                return unrealMaterial;

            if (current is UMaterialInstance instance &&
                instance.Parent is { } parent &&
                !ReferenceEquals(parent, current))
            {
                current = parent;
                continue;
            }

            break;
        }

        return null;
    }

    private static UUnrealMaterial? FindTexture(
        CMaterialParams2 parameters,
        params string[] terms)
    {
        return FindTextureEntry(
            parameters,
            terms).Texture;
    }

    private static (
        string Name,
        UUnrealMaterial? Texture)
        FindTextureEntry(
            CMaterialParams2 parameters,
            params string[] terms)
    {
        foreach (var pair in parameters.Textures)
        {
            var key = pair.Key ?? string.Empty;

            if (terms.Any(
                    term => key.Contains(
                        term,
                        StringComparison.OrdinalIgnoreCase)))
            {
                return (key, pair.Value);
            }
        }

        return (string.Empty, null);
    }

    private static float[] FindColor(
        CMaterialParams2 parameters,
        IEnumerable<string> terms,
        float[] fallback)
    {
        foreach (var pair in parameters.Colors)
        {
            if (!terms.Any(
                    term => pair.Key.Contains(
                        term,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return ColorArray(pair.Value);
        }

        return fallback;
    }

    private static float FindScalar(
        CMaterialParams2 parameters,
        IEnumerable<string> terms,
        float fallback)
    {
        foreach (var pair in parameters.Scalars)
        {
            if (!terms.Any(
                    term => pair.Key.Contains(
                        term,
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return pair.Value;
        }

        return fallback;
    }

    private static bool UsesVertexColor(
        CMaterialParams2 parameters)
    {
        foreach (var pair in parameters.Switches)
        {
            if (!pair.Value)
                continue;

            if (
                pair.Key.Contains(
                    "vertexcolor",
                    StringComparison.OrdinalIgnoreCase) ||
                pair.Key.Contains(
                    "vertex color",
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static PackedChannels InferPackedChannels(
        string? parameterName,
        bool hasPackedTexture)
    {
        if (!hasPackedTexture ||
            string.IsNullOrWhiteSpace(parameterName))
        {
            return new PackedChannels();
        }

        var value = parameterName
            .Replace("_", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .ToUpperInvariant();

        if (
            value.Contains("ORM") ||
            value.Contains("OCCLUSIONROUGHNESSMETALLIC") ||
            value.Contains("AOROUGHNESSMETALLIC"))
        {
            return new PackedChannels(
                Ao: 0,
                Roughness: 1,
                Metallic: 2);
        }

        if (
            value.Contains("MRAO") ||
            value.Contains("MRA") ||
            value.Contains("METALROUGHNESSAO"))
        {
            return new PackedChannels(
                Ao: 2,
                Roughness: 1,
                Metallic: 0);
        }

        if (
            value.Contains("RMAO") ||
            value.Contains("ROUGHNESSMETALLICAO"))
        {
            return new PackedChannels(
                Ao: 2,
                Roughness: 0,
                Metallic: 1);
        }

        return new PackedChannels();
    }

    private static float[] ColorArray(
        FLinearColor color)
    {
        return new[]
        {
            color.R,
            color.G,
            color.B,
            color.A
        };
    }

    private static float Clamp01(float value)
    {
        if (float.IsNaN(value) ||
            float.IsInfinity(value))
        {
            return 0f;
        }

        return Math.Clamp(value, 0f, 1f);
    }

    private static string? SafePath(
        UUnrealMaterial? material)
    {
        if (material is null)
            return null;

        try
        {
            return material.GetPathName();
        }
        catch
        {
            return null;
        }
    }

    private static string NameFromPath(
        string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "Material";

        var value = path!;
        var colon = value.LastIndexOf(':');
        if (colon >= 0 && colon + 1 < value.Length)
            value = value[(colon + 1)..];

        var slash = value.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < value.Length)
            value = value[(slash + 1)..];

        var dot = value.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < value.Length)
            value = value[(dot + 1)..];

        return value;
    }
}
