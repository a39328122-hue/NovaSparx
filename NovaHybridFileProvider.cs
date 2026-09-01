using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.IO;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using EpicManifestParser.UE;

namespace NovaSparx.Backend;

/// <summary>
/// NovaSparx 1.0 live/on-demand VFS provider.
///
/// One provider can combine:
/// - Fortnite core BuildPatch archives
/// - Fortnite_Studio archives
/// - IoStore .uondemandtoc files
/// - the streamed-texture TOC
///
/// Important 1.0 change:
/// referenced textures are NOT skipped by default. Material/texture fidelity is
/// now part of NovaSparx's job, so CUE4Parse must be allowed to resolve them.
/// Set NOVASPARX_SKIP_REFERENCED_TEXTURES=true only as an emergency low-memory
/// compatibility switch.
/// </summary>
public sealed class NovaHybridFileProvider : AbstractVfsFileProvider
{
    private readonly DirectoryInfo _tocCacheDirectory;

    public bool LoadOnDemandTocs { get; set; } = true;

    public NovaHybridFileProvider(
        DirectoryInfo tocCacheDirectory,
        VersionContainer? versions = null)
        : base(
            versions,
            StringComparer.OrdinalIgnoreCase)
    {
        _tocCacheDirectory = tocCacheDirectory;
        _tocCacheDirectory.Create();

        SkipReferencedTextures =
            ReadBoolEnvironment(
                "NOVASPARX_SKIP_REFERENCED_TEXTURES",
                fallback: false);
    }

    /// <summary>
    /// Live provider has no local directory scan.
    /// Archives are registered explicitly from Epic manifests.
    /// </summary>
    public override void Initialize()
    {
    }

    public async Task<ManifestRegistrationResult>
        RegisterManifestAsync(
            FBuildPatchAppManifest manifest,
            string sourceName,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var registered = 0;
        var onDemand = 0;
        var skipped = 0;

        var files = manifest.Files
            .Where(file =>
                IsFortnitePakFile(file.FileName))
            .ToArray();

        foreach (var file in files)
        {
            cancellationToken
                .ThrowIfCancellationRequested();

            var logicalName =
                file.FileName
                    .Replace('\\', '/');

            var extension =
                Path.GetExtension(logicalName)
                    .TrimStart('.')
                    .ToLowerInvariant();

            if (extension is "pak" or "utoc")
            {
                // EpicManifestParser streams BuildPatch chunks on demand.
                //
                // CUE4Parse receives the logical archive and only requests the
                // data ranges it actually needs. This is the main reason
                // NovaSparx does not need a full Fortnite installation.
                RegisterVfs(
                    logicalName,
                    [file.GetStream()],
                    requestedName =>
                    {
                        var normalized =
                            requestedName.Replace('\\', '/');

                        var match =
                            manifest.Files.FirstOrDefault(
                                candidate =>
                                    candidate.FileName
                                        .Replace('\\', '/')
                                        .Equals(
                                            normalized,
                                            StringComparison.OrdinalIgnoreCase));

                        if (match is null)
                        {
                            throw new FileNotFoundException(
                                $"Manifest stream was not found: {requestedName}");
                        }

                        return new FStreamArchive(
                            requestedName,
                            match.GetStream());
                    });

                registered++;
                continue;
            }

            if (
                extension == "uondemandtoc" &&
                LoadOnDemandTocs)
            {
                // IoChunkToc needs random access to the TOC itself.
                // The TOC is small, so we cache only that file locally while
                // payload chunks remain remote/on-demand.
                var safeVersion =
                    SanitizeFileName(
                        manifest.Meta?.BuildVersion ??
                        "unknown");

                var versionDirectory =
                    new DirectoryInfo(
                        Path.Combine(
                            _tocCacheDirectory.FullName,
                            safeVersion));

                versionDirectory.Create();

                var fileName =
                    SanitizeFileName(
                        Path.GetFileName(logicalName));

                var targetPath =
                    Path.Combine(
                        versionDirectory.FullName,
                        fileName);

                if (
                    !File.Exists(targetPath) ||
                    new FileInfo(targetPath).Length == 0)
                {
                    await using var source =
                        file.GetStream();

                    await using var destination =
                        new FileStream(
                            targetPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.Read,
                            1024 * 64,
                            useAsync: true);

                    await source.CopyToAsync(
                        destination,
                        cancellationToken);
                }

                await RegisterVfsAsync(
                    new IoChunkToc(
                        targetPath,
                        Versions));

                registered++;
                onDemand++;
                continue;
            }

            skipped++;
        }

        return new ManifestRegistrationResult(
            Source: sourceName,
            Version:
                manifest.Meta?.BuildVersion ??
                "unknown",
            RegisteredArchives: registered,
            OnDemandTocs: onDemand,
            SkippedFiles: skipped);
    }

    /// <summary>
    /// Registers a streamed-texture or other external IoStore OnDemand TOC.
    ///
    /// The bytes are cached by content, not blindly rewritten on every boot.
    /// Payload chunks still come from the configured OnDemand host.
    /// </summary>
    public async Task<bool>
        RegisterExternalOnDemandTocAsync(
            string name,
            byte[] tocBytes,
            CancellationToken cancellationToken)
    {
        if (
            !LoadOnDemandTocs ||
            tocBytes is null ||
            tocBytes.Length < 32)
        {
            return false;
        }

        cancellationToken
            .ThrowIfCancellationRequested();

        var fileName =
            SanitizeFileName(
                string.IsNullOrWhiteSpace(name)
                    ? "IoStoreOnDemand.uondemandtoc"
                    : name);

        if (
            !fileName.EndsWith(
                ".uondemandtoc",
                StringComparison.OrdinalIgnoreCase))
        {
            fileName += ".uondemandtoc";
        }

        var path =
            Path.Combine(
                _tocCacheDirectory.FullName,
                fileName);

        var mustWrite = true;

        if (File.Exists(path))
        {
            try
            {
                var current =
                    await File.ReadAllBytesAsync(
                        path,
                        cancellationToken);

                mustWrite =
                    !current.AsSpan()
                        .SequenceEqual(tocBytes);
            }
            catch
            {
                // Rewrite a corrupted/unreadable cached TOC.
                mustWrite = true;
            }
        }

        if (mustWrite)
        {
            await File.WriteAllBytesAsync(
                path,
                tocBytes,
                cancellationToken);
        }

        await RegisterVfsAsync(
            new IoChunkToc(
                path,
                Versions));

        return true;
    }

    private static bool IsFortnitePakFile(
        string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        var value =
            fileName.Replace('\\', '/');

        // Core game and Fortnite_Studio manifests both expose their relevant
        // package containers through Content/Paks. Do not restrict by a
        // specific chunk number or platform suffix.
        return value.Contains(
            "/Content/Paks/",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadBoolEnvironment(
        string name,
        bool fallback)
    {
        var value =
            Environment.GetEnvironmentVariable(
                name);

        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        return value.Trim() switch
        {
            "1" => true,
            "0" => false,
            "yes" => true,
            "no" => false,
            "on" => true,
            "off" => false,
            _ => fallback
        };
    }

    private static string SanitizeFileName(
        string value)
    {
        value =
            string.IsNullOrWhiteSpace(value)
                ? "unknown"
                : value;

        foreach (var character in
                 Path.GetInvalidFileNameChars())
        {
            value =
                value.Replace(
                    character,
                    '_');
        }

        return value;
    }
}

public sealed record ManifestRegistrationResult(
    string Source,
    string Version,
    int RegisteredArchives,
    int OnDemandTocs,
    int SkippedFiles
);
