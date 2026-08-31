using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using EpicManifestParser.UE;

namespace NovaSparx.Backend;

/// <summary>
/// NovaSparx live/on-demand VFS provider.
/// It can combine multiple Fortnite BuildPatch manifests in one provider:
/// core Fortnite + Fortnite_Studio + texture-streaming IoStore TOCs.
/// </summary>
public sealed class NovaHybridFileProvider : AbstractVfsFileProvider
{
    private readonly DirectoryInfo _tocCacheDirectory;

    public bool LoadOnDemandTocs { get; set; } = true;

    public NovaHybridFileProvider(
        DirectoryInfo tocCacheDirectory,
        VersionContainer? versions = null)
        : base(versions, StringComparer.OrdinalIgnoreCase)
    {
        _tocCacheDirectory = tocCacheDirectory;
        _tocCacheDirectory.Create();

        // Material package paths are enough for the HTTP manifest.
        // Actual texture resolution is handled by FNAA/Dilly or future Nova texture endpoints.
        SkipReferencedTextures = true;
    }

    // Live provider has no local folder scan.
    public override void Initialize()
    {
    }

    public async Task<ManifestRegistrationResult> RegisterManifestAsync(
        FBuildPatchAppManifest manifest,
        string sourceName,
        CancellationToken cancellationToken)
    {
        var registered = 0;
        var onDemand = 0;
        var skipped = 0;

        var files = manifest.Files
            .Where(file =>
                file.FileName.Contains(
                    "FortniteGame/Content/Paks/",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extension = Path.GetExtension(file.FileName)
                .TrimStart('.')
                .ToLowerInvariant();

            if (extension is "pak" or "utoc")
            {
                // EpicManifestParser's file stream performs on-demand BuildPatch chunk reads.
                // Register the same logical file for CUE4Parse without downloading the whole archive.
                RegisterVfs(
                    file.FileName,
                    [file.GetStream()],
                    requestedName =>
                    {
                        var match = manifest.Files.FirstOrDefault(
                            candidate => candidate.FileName.Equals(
                                requestedName,
                                StringComparison.OrdinalIgnoreCase));

                        if (match is null)
                            throw new FileNotFoundException(
                                $"Manifest stream was not found: {requestedName}");

                        return new FStreamArchive(requestedName, match.GetStream());
                    });

                registered++;
                continue;
            }

            if (extension == "uondemandtoc" && LoadOnDemandTocs)
            {
                // IoChunkToc needs random access. Materialize the small TOC itself,
                // while payload chunks remain remote/on-demand.
                var safeVersion = SanitizeFileName(
                    manifest.Meta?.BuildVersion ?? "unknown");
                var versionDir = new DirectoryInfo(
                    Path.Combine(_tocCacheDirectory.FullName, safeVersion));
                versionDir.Create();

                var fileName = Path.GetFileName(file.FileName);
                var targetPath = Path.Combine(versionDir.FullName, fileName);

                if (!File.Exists(targetPath) || new FileInfo(targetPath).Length == 0)
                {
                    await using var source = file.GetStream();
                    await using var destination = new FileStream(
                        targetPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.Read,
                        1024 * 64,
                        useAsync: true);

                    await source.CopyToAsync(destination, cancellationToken);
                }

                await RegisterVfsAsync(
                    new IoChunkToc(targetPath, Versions));

                registered++;
                onDemand++;
                continue;
            }

            skipped++;
        }

        return new ManifestRegistrationResult(
            sourceName,
            manifest.Meta?.BuildVersion ?? "unknown",
            registered,
            onDemand,
            skipped);
    }

    public async Task<bool> RegisterExternalOnDemandTocAsync(
        string name,
        byte[] tocBytes,
        CancellationToken cancellationToken)
    {
        if (!LoadOnDemandTocs || tocBytes.Length < 32)
            return false;

        cancellationToken.ThrowIfCancellationRequested();

        var fileName = SanitizeFileName(
            string.IsNullOrWhiteSpace(name) ? "IoStoreOnDemand.uondemandtoc" : name);

        if (!fileName.EndsWith(".uondemandtoc", StringComparison.OrdinalIgnoreCase))
            fileName += ".uondemandtoc";

        var path = Path.Combine(_tocCacheDirectory.FullName, fileName);

        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(tocBytes))
            await File.WriteAllBytesAsync(path, tocBytes, cancellationToken);

        await RegisterVfsAsync(new IoChunkToc(path, Versions));
        return true;
    }

    private static string SanitizeFileName(string value)
    {
        foreach (var ch in Path.GetInvalidFileNameChars())
            value = value.Replace(ch, '_');

        return value;
    }
}

public sealed record ManifestRegistrationResult(
    string Source,
    string Version,
    int RegisteredArchives,
    int OnDemandTocs,
    int SkippedFiles);
