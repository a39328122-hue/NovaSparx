using System.Text.RegularExpressions;

namespace NovaSparx.Backend;

public static partial class AssetPathResolver
{
    [GeneratedRegex(@"^(?:StaticMesh|SkeletalMesh|Texture2D|Texture|Material|MaterialInstanceConstant|Object)?'(.+)'$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClassWrapper();

    public static string Canonicalize(string input)
    {
        var value = (input ?? string.Empty).Trim().Replace('\\', '/');
        if (value.Length == 0) return string.Empty;

        var match = ClassWrapper().Match(value);
        if (match.Success) value = match.Groups[1].Value;

        value = value.Trim('\'', '"');
        foreach (var ext in new[] { ".uasset", ".uexp", ".ubulk" })
        {
            if (value.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^ext.Length];
                break;
            }
        }

        var lastSlash = value.LastIndexOf('/');
        var lastDot = value.LastIndexOf('.');
        if (lastDot > lastSlash)
        {
            var package = value[..lastDot];
            var objectName = value[(lastDot + 1)..].TrimEnd();
            if (objectName.EndsWith("_C", StringComparison.OrdinalIgnoreCase))
                objectName = objectName[..^2];

            var packageName = package[(package.LastIndexOf('/') + 1)..];
            if (objectName.Equals(packageName, StringComparison.OrdinalIgnoreCase))
                value = package;
        }

        const string contentPrefix = "FortniteGame/Content/";
        if (value.StartsWith(contentPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = "/Game/" + value[contentPrefix.Length..];
        }

        if (!value.StartsWith('/') && value.Contains('/'))
            value = "/" + value;

        while (value.Contains("//", StringComparison.Ordinal))
            value = value.Replace("//", "/", StringComparison.Ordinal);

        return value;
    }

    public static string ObjectPath(string canonical)
    {
        canonical = Canonicalize(canonical);
        if (canonical.Length == 0) return string.Empty;

        var name = canonical[(canonical.LastIndexOf('/') + 1)..];
        return $"{canonical}.{name}";
    }

    public static IEnumerable<string> LoadCandidates(string raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(List<string> list, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Replace('\\', '/').Trim();
            if (seen.Add(value)) list.Add(value);
        }

        var list = new List<string>();
        var canonical = Canonicalize(raw);
        var objectPath = ObjectPath(canonical);

        Add(list, raw);
        Add(list, canonical);
        Add(list, objectPath);

        if (canonical.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            var relative = canonical[6..];
            var name = relative[(relative.LastIndexOf('/') + 1)..];
            Add(list, $"FortniteGame/Content/{relative}.{name}");
            Add(list, $"FortniteGame/Content/{relative}");
        }
        else if (canonical.StartsWith('/'))
        {
            // Plugin virtual path. CUE4Parse often resolves the virtual path directly,
            // but this fallback catches common GameFeatures layouts too.
            var parts = canonical[1..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var plugin = parts[0];
                var rest = string.Join('/', parts.Skip(1));
                var name = parts[^1];
                Add(list, $"FortniteGame/Plugins/GameFeatures/{plugin}/Content/{rest}.{name}");
                Add(list, $"FortniteGame/Plugins/GameFeatures/{plugin}/Content/{rest}");
            }
        }

        return list;
    }
}
