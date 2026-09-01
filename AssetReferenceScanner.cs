using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports;

namespace NovaSparx.Backend;

/// <summary>
/// Extracts compact, deterministic metadata and explicit path references from
/// any CUE4Parse UObject without serializing the raw Unreal object graph.
///
/// This is intentionally generic so Blueprint, Niagara, sound, animation and
/// other asset families can expose useful evidence even before they gain a
/// dedicated resolver.
/// </summary>
public static partial class AssetReferenceScanner
{
    public sealed record ScanResult(
        Dictionary<string, object?> Facts,
        AssetReference[] References);

    private const int MaxProperties = 96;
    private const int MaxCollectionItems = 24;
    private const int MaxTextLength = 1200;

    [GeneratedRegex(
        @"(?<path>(?:/(?:Game|Engine|[A-Za-z0-9_]+)/|(?:FortniteGame|Engine)/)[A-Za-z0-9_./\-]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AssetPathRegex();

    public static ScanResult Scan(UObject value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var facts =
            new Dictionary<string, object?>(
                StringComparer.OrdinalIgnoreCase);

        var references =
            new List<AssetReference>();

        var seenReferences =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        var propertyCount = 0;

        foreach (var property in value.Properties)
        {
            if (propertyCount++ >= MaxProperties)
                break;

            var name =
                string.IsNullOrWhiteSpace(property.Name.Text)
                    ? $"Property_{propertyCount}"
                    : property.Name.Text;

            var type =
                property.PropertyType.Text;

            object? raw = null;

            try
            {
                var tag = property.Tag;

                raw =
                    tag?
                        .GetType()
                        .GetProperty(
                            "Value",
                            BindingFlags.Instance |
                            BindingFlags.Public)
                        ?.GetValue(tag);
            }
            catch
            {
                // Keep the property type even when a custom tag cannot be
                // reflected safely.
            }

            var safeValue =
                ToSafeValue(
                    raw,
                    depth: 0);

            facts[name] =
                new Dictionary<string, object?>
                {
                    ["type"] = type,
                    ["value"] = safeValue
                };

            CollectReferences(
                raw,
                $"property:{name}",
                references,
                seenReferences,
                depth: 0);
        }

        facts["propertyCount"] =
            value.Properties.Count;

        facts["runtimeType"] =
            value.GetType().Name;

        TryAddObjectPath(
            value,
            "self",
            references,
            seenReferences);

        return new ScanResult(
            facts,
            references.ToArray());
    }

    private static object? ToSafeValue(
        object? value,
        int depth)
    {
        if (value is null)
            return null;

        if (depth >= 3)
            return SafeText(value);

        switch (value)
        {
            case string text:
                return Trim(text);

            case bool or byte or sbyte or
                 short or ushort or
                 int or uint or
                 long or ulong or
                 float or double or decimal:
                return value;

            case Enum enumValue:
                return enumValue.ToString();

            case UObject unrealObject:
                return SafeObjectPath(unrealObject);

            case byte[] bytes:
                return $"<binary:{bytes.Length}>";

            case IDictionary dictionary:
            {
                var result =
                    new Dictionary<string, object?>(
                        StringComparer.OrdinalIgnoreCase);

                var count = 0;

                foreach (
                    DictionaryEntry pair in dictionary)
                {
                    if (count++ >= MaxCollectionItems)
                        break;

                    var key =
                        Trim(
                            Convert.ToString(pair.Key) ??
                            $"item_{count}");

                    result[key] =
                        ToSafeValue(
                            pair.Value,
                            depth + 1);
                }

                return result;
            }

            case IEnumerable enumerable:
            {
                var result =
                    new List<object?>();

                var count = 0;

                foreach (var item in enumerable)
                {
                    if (count++ >= MaxCollectionItems)
                        break;

                    result.Add(
                        ToSafeValue(
                            item,
                            depth + 1));
                }

                return result;
            }

            default:
                return SafeText(value);
        }
    }

    private static void CollectReferences(
        object? value,
        string kind,
        List<AssetReference> references,
        HashSet<string> seen,
        int depth)
    {
        if (value is null || depth >= 4)
            return;

        if (value is UObject unrealObject)
        {
            TryAddObjectPath(
                unrealObject,
                kind,
                references,
                seen);

            return;
        }

        if (value is string text)
        {
            AddPathsFromText(
                text,
                kind,
                references,
                seen);

            return;
        }

        if (value is byte[])
            return;

        if (value is IDictionary dictionary)
        {
            var count = 0;

            foreach (
                DictionaryEntry pair in dictionary)
            {
                if (count++ >= MaxCollectionItems)
                    break;

                CollectReferences(
                    pair.Key,
                    kind,
                    references,
                    seen,
                    depth + 1);

                CollectReferences(
                    pair.Value,
                    kind,
                    references,
                    seen,
                    depth + 1);
            }

            return;
        }

        if (value is IEnumerable enumerable)
        {
            var count = 0;

            foreach (var item in enumerable)
            {
                if (count++ >= MaxCollectionItems)
                    break;

                CollectReferences(
                    item,
                    kind,
                    references,
                    seen,
                    depth + 1);
            }

            return;
        }

        AddPathsFromText(
            SafeText(value),
            kind,
            references,
            seen);
    }

    private static void TryAddObjectPath(
        UObject value,
        string kind,
        List<AssetReference> references,
        HashSet<string> seen)
    {
        var path =
            SafeObjectPath(value);

        AddReference(
            kind,
            path,
            references,
            seen);
    }

    private static string SafeObjectPath(
        UObject value)
    {
        try
        {
            return Trim(
                value.GetPathName());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void AddPathsFromText(
        string? value,
        string kind,
        List<AssetReference> references,
        HashSet<string> seen)
    {
        var text =
            Trim(value ?? string.Empty);

        if (text.Length == 0)
            return;

        foreach (
            Match match in
            AssetPathRegex().Matches(text))
        {
            var path =
                match.Groups["path"]
                    .Value
                    .TrimEnd(
                        '\'',
                        '"',
                        ')',
                        ']',
                        '}',
                        ',',
                        ';',
                        ':');

            AddReference(
                kind,
                path,
                references,
                seen);
        }
    }

    private static void AddReference(
        string kind,
        string? path,
        List<AssetReference> references,
        HashSet<string> seen)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        path =
            path.Replace('\\', '/')
                .Trim();

        if (path.Length > 2400)
            return;

        var key =
            $"{kind}|{path}";

        if (!seen.Add(key))
            return;

        references.Add(
            new AssetReference(
                Kind: kind,
                Path: path));
    }

    private static string SafeText(
        object value)
    {
        try
        {
            return Trim(
                Convert.ToString(value) ??
                value.GetType().Name);
        }
        catch
        {
            return value.GetType().Name;
        }
    }

    private static string Trim(
        string value)
    {
        value =
            value.Replace(
                    "\0",
                    string.Empty,
                    StringComparison.Ordinal)
                .Trim();

        return value.Length <= MaxTextLength
            ? value
            : value[..MaxTextLength];
    }
}
