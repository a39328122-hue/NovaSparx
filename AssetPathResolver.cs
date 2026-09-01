using System.Text.RegularExpressions;

namespace NovaSparx.Backend;

/// <summary>
/// Normalizes Fortnite / Unreal asset paths without assuming every plugin lives
/// under FortniteGame/Plugins/GameFeatures.
///
/// Supported examples:
///   FortniteGame/Content/Athena/Items/SM_Test.uasset
///   /Game/Athena/Items/SM_Test.SM_Test
///   StaticMesh'/Game/Athena/Items/SM_Test.SM_Test'
///   FortniteGame/Plugins/FloatingGeno/Content/Assets/SM_Test.uasset
///   /FloatingGeno/Assets/SM_Test.SM_Test
///   FortniteGame/Plugins/GameFeatures/Content/Effects/SM_Test.uasset
///   /GameFeatures/Effects/SM_Test.SM_Test
/// </summary>
public static partial class AssetPathResolver
{
    [GeneratedRegex(
        @"^(?:StaticMesh|SkeletalMesh|Texture2D|Texture|Material|MaterialInstanceConstant|MaterialInstance|Object|BlueprintGeneratedClass|Blueprint|WidgetBlueprint|AnimBlueprint|NiagaraSystem|NiagaraEmitter|SoundCue|SoundWave|World|LevelSequence)?'(.+)'$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClassWrapper();

    public static string Canonicalize(string input)
    {
        var value = (input ?? string.Empty)
            .Trim()
            .Replace('\\', '/');

        if (value.Length == 0)
            return string.Empty;

        var wrapper = ClassWrapper().Match(value);
        if (wrapper.Success)
            value = wrapper.Groups[1].Value;

        value = value.Trim('\'', '"').Trim();

        foreach (var extension in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            if (!value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                continue;

            value = value[..^extension.Length];
            break;
        }

        // Convert an Unreal object/class path back to its package path.
        //
        // /Game/Test/SM_Test.SM_Test
        // -> /Game/Test/SM_Test
        //
        // /Game/Test/BP_Test.BP_Test_C
        // -> /Game/Test/BP_Test
        var lastSlash = value.LastIndexOf('/');
        var lastDot = value.LastIndexOf('.');

        if (lastDot > lastSlash)
        {
            var package = value[..lastDot];

            var objectName = value[(lastDot + 1)..].Trim();
            if (objectName.EndsWith("_C", StringComparison.OrdinalIgnoreCase))
                objectName = objectName[..^2];

            var packageName = package[(package.LastIndexOf('/') + 1)..];

            if (objectName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                value = package;
        }

        const string fortniteContent = "FortniteGame/Content/";
        const string engineContent = "Engine/Content/";

        if (value.StartsWith(fortniteContent, StringComparison.OrdinalIgnoreCase))
        {
            value = "/Game/" + value[fortniteContent.Length..];
        }
        else if (value.StartsWith(engineContent, StringComparison.OrdinalIgnoreCase))
        {
            value = "/Engine/" + value[engineContent.Length..];
        }
        else if (
            value.StartsWith("FortniteGame/Plugins/", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("Plugins/", StringComparison.OrdinalIgnoreCase))
        {
            // Unreal's mounted virtual path uses the directory immediately before
            // Content as the mount name.
            //
            // FortniteGame/Plugins/FloatingGeno/Content/Assets/X
            // -> /FloatingGeno/Assets/X
            //
            // FortniteGame/Plugins/GameFeatures/Content/Effects/X
            // -> /GameFeatures/Effects/X
            //
            // FortniteGame/Plugins/GameFeatures/Creative/Content/X
            // -> /Creative/X
            var parts = value.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var contentIndex = Array.FindIndex(
                parts,
                part => part.Equals("Content", StringComparison.OrdinalIgnoreCase));

            if (contentIndex >= 1 && contentIndex + 1 < parts.Length)
            {
                var mount = parts[contentIndex - 1];
                var relative = string.Join('/', parts.Skip(contentIndex + 1));

                value = $"/{mount}/{relative}";
            }
        }

        if (!value.StartsWith('/') && value.Contains('/'))
            value = "/" + value;

        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);

        return value.TrimEnd('/');
    }

    public static string ObjectPath(string input)
    {
        var canonical = Canonicalize(input);

        if (canonical.Length == 0)
            return string.Empty;

        var name = canonical[(canonical.LastIndexOf('/') + 1)..];
        return $"{canonical}.{name}";
    }

    public static string ClassPath(string input)
    {
        var objectPath = ObjectPath(input);

        if (objectPath.Length == 0)
            return string.Empty;

        return objectPath.EndsWith("_C", StringComparison.OrdinalIgnoreCase)
            ? objectPath
            : objectPath + "_C";
    }

    /// <summary>
    /// Returns candidate strings in the order NovaSparx should try to load them.
    ///
    /// The exact user path is kept first because it can contain physical plugin
    /// information that cannot be reconstructed perfectly from a virtual mount.
    /// </summary>
    public static IEnumerable<string> LoadCandidates(string raw)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            candidate = candidate
                .Trim()
                .Trim('\'', '"')
                .Replace('\\', '/');

            if (seen.Add(candidate))
                result.Add(candidate);
        }

        void AddPackageAndObject(string package)
        {
            if (string.IsNullOrWhiteSpace(package))
                return;

            Add(package);

            var clean = package.TrimEnd('/');
            var name = clean[(clean.LastIndexOf('/') + 1)..];

            if (name.Length > 0)
                Add($"{clean}.{name}");
        }

        var canonical = Canonicalize(raw);
        var objectPath = ObjectPath(canonical);

        // Most accurate information first.
        Add(raw);
        Add(canonical);
        Add(objectPath);

        if (canonical.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = canonical[6..];

            AddPackageAndObject(
                $"FortniteGame/Content/{relative}");
        }
        else if (canonical.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = canonical[8..];

            AddPackageAndObject(
                $"Engine/Content/{relative}");
        }
        else if (canonical.StartsWith('/'))
        {
            var parts = canonical[1..].Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (parts.Length >= 2)
            {
                var mount = parts[0];
                var relative = string.Join('/', parts.Skip(1));

                // Direct plugin layout:
                // FortniteGame/Plugins/FloatingGeno/Content/...
                AddPackageAndObject(
                    $"FortniteGame/Plugins/{mount}/Content/{relative}");

                // Common Fortnite GameFeatures layout:
                // FortniteGame/Plugins/GameFeatures/Creative/Content/...
                AddPackageAndObject(
                    $"FortniteGame/Plugins/GameFeatures/{mount}/Content/{relative}");

                // Special case where GameFeatures itself is the mount:
                // FortniteGame/Plugins/GameFeatures/Content/...
                if (mount.Equals("GameFeatures", StringComparison.OrdinalIgnoreCase))
                {
                    AddPackageAndObject(
                        $"FortniteGame/Plugins/GameFeatures/Content/{relative}");
                }
            }
        }

        return result;
    }
}
