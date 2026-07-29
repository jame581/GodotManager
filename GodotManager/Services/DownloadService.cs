using GodotManager.Config;
using GodotManager.Infrastructure;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GodotManager.Services;

/// <summary>
/// Unverified is explicitly zero so it is the default. This field is
/// security-relevant and reaches the persisted registry, where a pre-1.3.0 entry
/// or a property that fails to bind deserializes to the enum's default: that must
/// fail closed, never read back as though a checksum had been confirmed.
/// </summary>
internal enum ChecksumStatus
{
    /// <summary>
    /// An attempt was made to check this download against published sums and it did
    /// not succeed: the sums endpoint answered with something other than "not
    /// found", the request itself failed, or the sums file exists but does not list
    /// this asset. This is the one state worth telling the user about.
    /// </summary>
    Unverified = 0,
    Verified = 1,

    /// <summary>
    /// Nothing was attempted, or nothing could have succeeded regardless of network
    /// conditions: no <see cref="ChecksumSource"/> was supplied (a custom --url or a
    /// local --archive has no upstream sums to check against), or the upstream
    /// release publishes no SHA512-SUMS.txt at all (HTTP 404 — the common,
    /// unremarkable case for many releases). Kept distinct from
    /// <see cref="Unverified"/> so callers can warn only when verification was
    /// actually attempted and failed, not on this expected condition.
    /// </summary>
    NotApplicable = 2
}

/// <param name="ArchiveName">Pre-redirect name. Drives install-folder naming; must match pre-1.3.0 behaviour.</param>
/// <param name="ResolvedFileName">Post-redirect name. The SHA512-SUMS.txt lookup key.</param>
internal sealed record DownloadOutcome(
    string FilePath,
    string ArchiveName,
    string ResolvedFileName,
    string Sha512,
    ChecksumStatus Status,
    string? UnverifiedReason);

/// <summary>
/// Identifies an upstream release whose published checksums can be fetched.
/// Supplied only for auto-built download URLs.
/// </summary>
internal sealed record ChecksumSource(string Version, string Flavor = "stable");

/// <summary>A failure worth retrying: a network blip, or a server status suggesting a later attempt.</summary>
internal sealed class TransientDownloadException : Exception
{
    public TransientDownloadException(string message, Exception? inner = null)
        : base(message, inner) { }
}

/// <summary>
/// Sidecar describing an in-flight transfer. It exists only so a partial download
/// can be resumed by a later process, and is deleted the moment the transfer ends.
/// </summary>
internal sealed class DownloadCacheMeta
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Read back on resume, where the 206 may carry no Content-Disposition to re-derive it from.</summary>
    public string ArchiveName { get; set; } = string.Empty;

    /// <summary>
    /// The SHA512-SUMS.txt lookup key resolved on the leg that started the transfer.
    /// Kept for the same reason as <see cref="ArchiveName"/>, and one more: without
    /// Content-Disposition this name comes from the response's post-redirect URI, so a
    /// transfer resumed against a different mirror would otherwise key the lookup on
    /// that mirror's URI and silently drop to Unverified.
    /// </summary>
    public string ResolvedFileName { get; set; } = string.Empty;

    public string? ETag { get; set; }
}

internal sealed class DownloadService
{
    /// <summary>
    /// Delays BETWEEN attempts, so N entries means N+1 attempts. These two
    /// entries therefore allow three attempts in total.
    /// </summary>
    private static readonly TimeSpan[] DefaultBackoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2)
    ];

    private const int BufferSize = 81920;

    private readonly AppPaths _paths;
    private readonly HttpClient _httpClient;
    private readonly DiagnosticContext? _diagnostics;
    private readonly TimeSpan[] _backoff;

    public DownloadService(
        AppPaths paths,
        HttpClient? httpClient = null,
        DiagnosticContext? diagnostics = null,
        TimeSpan[]? backoff = null)
    {
        _paths = paths;
        _httpClient = httpClient ?? new HttpClient();
        _diagnostics = diagnostics;
        _backoff = backoff ?? DefaultBackoff;
    }

    /// <summary>Stable cache key for a URI: first 16 hex chars of its SHA-256.</summary>
    internal static string ComputeCacheKey(Uri uri) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))[..16];

    public async Task<DownloadOutcome> DownloadAsync(
        Uri uri,
        ChecksumSource? checksums,
        Action<double>? progress,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_paths.DownloadCacheDirectory);

        var key = ComputeCacheKey(uri);
        var partPath = Path.Combine(_paths.DownloadCacheDirectory, key + ".part");
        var metaPath = Path.Combine(_paths.DownloadCacheDirectory, key + ".json");
        var archivePath = Path.Combine(_paths.DownloadCacheDirectory, key + ".archive");

        // A completed archive can survive a download that succeeded but whose install
        // then failed. This run has not established its provenance, so discard it
        // rather than reuse it. This is not a content cache.
        TryDelete(archivePath);

        var meta = LoadMeta(metaPath, uri);
        if (meta is null)
        {
            // No usable sidecar: bytes on disk cannot be safely resumed, because
            // there is no ETag to guard the Range request with.
            TryDelete(partPath);
            TryDelete(metaPath);
            meta = new DownloadCacheMeta { Url = uri.AbsoluteUri };
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (archiveName, resolvedFileName, sha512) =
                    await TransferAsync(uri, partPath, metaPath, meta, progress, cancellationToken);

                // Verify before promoting the .part to an archive, so a bad payload
                // never exists under the name the rest of the install trusts.
                var (status, reason) = await VerifyAsync(sha512, resolvedFileName, checksums, cancellationToken);

                // The sidecar describes an in-flight transfer; it is meaningless now.
                TryDelete(metaPath);
                File.Move(partPath, archivePath, overwrite: true);

                return new DownloadOutcome(archivePath, archiveName, resolvedFileName, sha512, status, reason);
            }
            catch (ChecksumMismatchException)
            {
                // Deleting the partial is the point: leaving it would have the next
                // run resume the same bad bytes and fail identically forever. Sitting
                // above the retry catch also keeps a mismatch unretryable no matter
                // what IsRetryable grows to accept.
                TryDelete(partPath);
                TryDelete(metaPath);
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken) && attempt < _backoff.Length)
            {
                _diagnostics?.Warn($"Download attempt {attempt + 1} failed ({ex.Message}); retrying.");
                await Task.Delay(_backoff[attempt], cancellationToken);
            }
            catch (Exception ex) when (IsRetryable(ex, cancellationToken))
            {
                // Retries are exhausted. Everything IsRetryable accepts —
                // TransientDownloadException included — is outside the GodmanException
                // hierarchy, so escaping here would reach the user as a stack trace
                // instead of a rendered message plus hint. Wrapping at the throw site
                // rather than at one call site covers every caller of this service, and
                // the original rides along as InnerException so nothing is lost.
                throw new GodmanException(
                    $"Download failed after {_backoff.Length + 1} attempts: {ex.Message}",
                    // IOException is in the retryable set above to cover a connection
                    // dropping mid-transfer, so this exhaustion path cannot assume the
                    // cause was the network: the same IOException is what a full disk
                    // surfaces as too. Naming only "network" here would send that user
                    // chasing a connection that was never the problem, so the hint below
                    // names disk space as well. It also mentions permissions as generic
                    // troubleshooting advice, but that is not this catch's doing: a
                    // genuine permission failure raises UnauthorizedAccessException,
                    // which IsRetryable does not accept, so it never reaches this catch
                    // or this hint — it escapes to the command layer's unhinted catch.
                    "Check your network connection and that this machine has disk space " +
                    "and permission to write here, then try again. If the server keeps " +
                    "returning an error, wait and retry later, or pass --url to install " +
                    "from a mirror.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Best-effort removal of a completed cache entry. The .json sidecar is already
    /// gone by the time this runs -- DownloadAsync deletes it right after
    /// verification, well before the archive is handed back for installation -- so
    /// only the archive itself remains to clean up. TryDelete already swallows
    /// everything it can throw, so there is nothing left here for a surrounding
    /// try/catch to ever actually catch.
    /// </summary>
    public void DeleteCacheEntry(string archiveFilePath) => TryDelete(archiveFilePath);

    private static bool IsRetryable(Exception ex, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        return ex is TransientDownloadException
            or HttpRequestException
            or TaskCanceledException
            or IOException;
    }

    private async Task<(string ArchiveName, string ResolvedFileName, string Sha512)> TransferAsync(
        Uri uri,
        string partPath,
        string metaPath,
        DownloadCacheMeta meta,
        Action<double>? progress,
        CancellationToken cancellationToken)
    {
        var existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0L;

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existing > 0)
        {
            // A single unbounded range: everything from the first byte we lack.
            request.Headers.Range = new RangeHeaderValue(existing, null);

            if (!string.IsNullOrEmpty(meta.ETag)
                && EntityTagHeaderValue.TryParse(meta.ETag, out var validator))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(validator);
            }
        }

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            TryDelete(partPath);
            throw new TransientDownloadException("Cached partial download was stale; restarting.");
        }

        var status = (int)response.StatusCode;
        if (status is 408 or 429 || status >= 500)
        {
            throw new TransientDownloadException($"Server returned HTTP {status}.");
        }

        // Deliberately NOT EnsureSuccessStatusCode(): it raises HttpRequestException,
        // which IsRetryable accepts, so a 404 would burn every attempt.
        if (!response.IsSuccessStatusCode)
        {
            throw new GodmanException(
                $"Download failed: HTTP {status} for {uri}",
                "Check the version number, and that this edition and platform exist for it upstream.");
        }

        // All three conditions matter. A 200 means the server ignored Range (or If-Range
        // failed), so the bytes on disk are worthless. An unsolicited 206 with nothing
        // on disk would open a nonexistent file for FileMode.Open, raising a
        // FileNotFoundException that IsRetryable treats as transient. And the status code
        // alone does not establish where the body starts: a range-rewriting proxy can
        // answer "bytes=50000-" with "Content-Range: bytes 0-199999/200000" and the whole
        // body, which appended blind would splice a duplicate prefix into the archive and
        // then hash, rename and return it with no error. So the server's own account of
        // what it sent must match the offset we asked for; a 206 carrying no Content-Range
        // at all is likewise untrustworthy. Anything that fails this restarts from zero,
        // which FileMode.Create below makes safe by truncating the stale prefix.
        var contentRange = response.Content.Headers.ContentRange;
        var append = response.StatusCode == HttpStatusCode.PartialContent
            && existing > 0
            && contentRange?.From == existing;
        if (!append)
        {
            existing = 0;
        }

        var contentDispositionName = response.Content.Headers.ContentDisposition?.FileNameStar?.Trim('"')
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"');

        // Pre-1.3.0 resolution, preserved exactly so install folder names don't change.
        // On a resumed transfer the 206 need not repeat Content-Disposition, so the name
        // recorded when the transfer started is what keeps the folder name stable.
        var archiveName = contentDispositionName
            ?? (append && !string.IsNullOrWhiteSpace(meta.ArchiveName) ? meta.ArchiveName : null)
            ?? Path.GetFileName(uri.LocalPath);

        // Post-redirect name: what SHA512-SUMS.txt actually lists. As with ArchiveName,
        // the name recorded when the transfer started wins on a resumed leg: the URI
        // fallback below reflects wherever this particular response was served from,
        // which need not be the mirror the first leg redirected to.
        var resolvedFileName = contentDispositionName
            ?? (append && !string.IsNullOrWhiteSpace(meta.ResolvedFileName) ? meta.ResolvedFileName : null)
            ?? Path.GetFileName(response.RequestMessage?.RequestUri?.LocalPath ?? string.Empty);
        if (string.IsNullOrWhiteSpace(resolvedFileName))
        {
            resolvedFileName = archiveName;
        }

        var remaining = response.Content.Headers.ContentLength ?? -1;
        var total = remaining > 0 ? existing + remaining : -1;

        meta.Url = uri.AbsoluteUri;
        meta.ArchiveName = archiveName;
        meta.ResolvedFileName = resolvedFileName;
        // ToString() rather than Tag, so a weak validator stays marked weak and a
        // later If-Range built from it can only fail closed (a full 200), never
        // masquerade as the strong comparison If-Range requires. A validator kept
        // from an earlier response is likewise never cleared: at worst it is stale
        // and costs one restart, whereas dropping it would leave the next resume
        // unguarded.
        meta.ETag = response.Headers.ETag?.ToString() ?? meta.ETag;
        SaveMeta(metaPath, meta);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);

        // Hash state cannot survive a process boundary, so rebuild it from the bytes
        // already on disk. This runs on every entry, including in-process retries,
        // rather than maintaining two code paths for one operation. The read handle
        // is opened and disposed before the write handle below, so there is no
        // FileShare.None conflict.
        if (append)
        {
            await RehashAsync(partPath, existing, hasher, cancellationToken);
        }

        await using (var file = new FileStream(
            partPath,
            append ? FileMode.Open : FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            if (append)
            {
                // Seek to the offset actually sent in the Range header rather than to
                // the file's end. The two agree today, but they are sourced
                // independently, so writing from the requested offset keeps the
                // invariant explicit instead of silently gapping or overlapping if
                // they ever diverge.
                file.Seek(existing, SeekOrigin.Begin);
            }

            await using var network = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[BufferSize];
            var read = existing;
            int r;

            while ((r = await network.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, r), cancellationToken);
                hasher.AppendData(buffer, 0, r);
                read += r;

                if (total > 0)
                {
                    progress?.Invoke((double)read / total * 100d);
                }
            }
        }

        progress?.Invoke(100d);
        return (archiveName, resolvedFileName, Convert.ToHexStringLower(hasher.GetHashAndReset()));
    }

    /// <summary>
    /// Compares the transferred payload against the release's published SHA512-SUMS.txt.
    /// Verify-when-available: a mismatch throws, but an absent or unusable sums file
    /// only downgrades the outcome to Unverified with a reason.
    /// </summary>
    private async Task<(ChecksumStatus Status, string? Reason)> VerifyAsync(
        string sha512,
        string resolvedFileName,
        ChecksumSource? source,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            // Nothing was attempted and nothing is wrong: a custom URL or a local
            // archive has no upstream sums to check against. No warning belongs here.
            return (ChecksumStatus.NotApplicable, "no published checksums for this source (custom URL or local archive)");
        }

        var sumsUri = new Uri(
            $"https://github.com/godotengine/godot-builds/releases/download/{source.Version}-{source.Flavor}/SHA512-SUMS.txt");

        string content;
        try
        {
            // One attempt, deliberately, and outside the transfer's retry loop: a
            // release that publishes no sums answers 404, and that is the common,
            // expected case. Retrying it would add the full backoff to every such
            // install for no benefit.
            using var response = await _httpClient.GetAsync(sumsUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var httpReason = $"could not fetch {sumsUri} (HTTP {(int)response.StatusCode})";

                // Verbose-only, like every diagnostic below. This service runs
                // in-process under Terminal.Gui during a TUI install, so writing to
                // AnsiConsole unconditionally would paint raw ANSI over a screen it
                // does not own. The reason travels back on DownloadOutcome instead,
                // and the caller decides how to render it.
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // The release simply published no sums. Normal, not an error --
                    // NotApplicable rather than Unverified so callers don't warn.
                    _diagnostics?.Warn(
                        $"No checksums published for {source.Version}-{source.Flavor}; skipping verification.");
                    return (ChecksumStatus.NotApplicable,
                        $"no checksums published for {source.Version}-{source.Flavor}");
                }

                _diagnostics?.Warn($"Could not verify this download: {httpReason}");
                return (ChecksumStatus.Unverified, httpReason);
            }

            content = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                   && !cancellationToken.IsCancellationRequested)
        {
            var reason = $"could not fetch {sumsUri}: {ex.Message}";
            _diagnostics?.Warn($"Could not verify this download: {reason}");
            return (ChecksumStatus.Unverified, reason);
        }

        var expected = ParseSums(content, resolvedFileName);
        if (expected is null)
        {
            // Sums exist but do not cover this asset. Verification was attempted and
            // could not be completed; the reason rides back on DownloadOutcome for
            // the caller to surface.
            var reason = $"{resolvedFileName} is not listed in {sumsUri}";
            _diagnostics?.Warn($"Could not verify this download: {reason}");
            return (ChecksumStatus.Unverified, reason);
        }

        if (!IsSha512Hex(expected))
        {
            // A malformed line (wrong length, non-hex characters) is not the same
            // failure as a genuine mismatch: a real archive should not hard-fail
            // because the published sums line for it is garbled. Treat it the same
            // as "not listed" rather than comparing against noise.
            var reason = $"the entry for {resolvedFileName} in {sumsUri} is not a valid SHA-512 digest";
            _diagnostics?.Warn($"Could not verify this download: {reason}");
            return (ChecksumStatus.Unverified, reason);
        }

        if (!string.Equals(expected, sha512, StringComparison.OrdinalIgnoreCase))
        {
            throw new ChecksumMismatchException(resolvedFileName, expected, sha512, sumsUri);
        }

        return (ChecksumStatus.Verified, null);
    }

    /// <summary>
    /// Parses sha512sum-format content, returning the hash for the given file name
    /// or null when absent.
    /// </summary>
    internal static string? ParseSums(string content, string fileName)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var separator = line.IndexOf(' ');
            if (separator <= 0)
            {
                continue;
            }

            // GitHub release asset names are case-sensitive, so the file name half of
            // the comparison must be too. (The hex digest half, compared where this
            // return value is consumed, is case-insensitive on purpose -- sha512sum
            // output and this project's own hex encoding disagree on letter case.)
            var name = line[separator..].TrimStart(' ', '*');
            if (string.Equals(name, fileName, StringComparison.Ordinal))
            {
                return line[..separator];
            }
        }

        return null;
    }

    /// <summary>128 lowercase-or-uppercase hex characters: the shape of a SHA-512 digest.</summary>
    private static bool IsSha512Hex(string value) =>
        value.Length == 128 && value.All(Uri.IsHexDigit);

    private static async Task RehashAsync(
        string path, long length, IncrementalHash hasher, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var buffer = new byte[BufferSize];
        long done = 0;

        while (done < length)
        {
            var want = (int)Math.Min(buffer.Length, length - done);
            var r = await stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken);
            if (r <= 0)
            {
                break;
            }

            hasher.AppendData(buffer, 0, r);
            done += r;
        }
    }

    /// <summary>Returns null when no usable sidecar exists for this URI.</summary>
    private DownloadCacheMeta? LoadMeta(string metaPath, Uri uri)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return null;
            }

            var loaded = JsonSerializer.Deserialize<DownloadCacheMeta>(File.ReadAllText(metaPath));
            return loaded is not null && string.Equals(loaded.Url, uri.AbsoluteUri, StringComparison.Ordinal)
                ? loaded
                : null;
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Ignoring unreadable download metadata at {metaPath}: {ex.Message}");
            return null;
        }
    }

    private void SaveMeta(string metaPath, DownloadCacheMeta meta)
    {
        try
        {
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta));
        }
        catch (Exception ex)
        {
            _diagnostics?.Warn($"Failed to write download metadata to {metaPath}: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cache maintenance.
        }
    }
}
