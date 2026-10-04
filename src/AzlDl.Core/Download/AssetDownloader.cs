using System.Security.Cryptography;
using System.Text.Json;
using AzlDl.Core.Versioning;

namespace AzlDl.Core.Download;

public sealed record DownloadOptions(string AssetDirectory, int Concurrency = 8, bool DryRun = false, int Limit = 0);

public sealed record DownloadSummary(int Downloaded, int Skipped, int Failed, long Bytes);

// Mirrors hash-list rows into AssetDirectory/<path>. A row is skipped when the state file says the file on disk
// already has the row's MD5, so a new version only fetches what changed.
public sealed class AssetDownloader(HttpClient http, CdnClient cdn)
{
    public const string StateFileName = "downloaded.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<DownloadSummary> DownloadAsync(
        IEnumerable<HashRow> rows,
        DownloadOptions options,
        IProgress<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(options.AssetDirectory);
        var statePath = Path.Combine(options.AssetDirectory, StateFileName);
        var state = LoadState(statePath);
        var stateGate = new object();

        var pending = rows
            .Where(r => !(state.TryGetValue(r.Path, out var md5) && md5 == r.Md5
                          && File.Exists(Path.Combine(options.AssetDirectory, r.Path))))
            .ToList();
        var skipped = rows.Count() - pending.Count;
        if (options.Limit > 0)
        {
            pending = pending.Take(options.Limit).ToList();
        }

        if (options.DryRun)
        {
            foreach (var row in pending)
            {
                log?.Report($"DRY  {row.Path}  {row.Size} bytes");
            }

            return new DownloadSummary(0, skipped, 0, pending.Sum(r => r.Size));
        }

        var downloaded = 0;
        var failed = 0;
        long bytes = 0;

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Concurrency), CancellationToken = cancellationToken },
            async (row, token) =>
            {
                var error = await DownloadOneAsync(row, options.AssetDirectory, token);
                if (error is null)
                {
                    Interlocked.Increment(ref downloaded);
                    Interlocked.Add(ref bytes, row.Size);
                    lock (stateGate)
                    {
                        state[row.Path] = row.Md5;
                        if (downloaded % 200 == 0)
                        {
                            SaveState(statePath, state);
                        }
                    }

                    log?.Report($"OK   {row.Path}");
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    log?.Report($"FAIL {row.Path}: {error}");
                }
            });

        SaveState(statePath, state);
        return new DownloadSummary(downloaded, skipped, failed, bytes);
    }

    // Returns null on success, otherwise why the file was rejected.
    private async Task<string?> DownloadOneAsync(HashRow row, string assetDirectory, CancellationToken cancellationToken)
    {
        var target = Path.Combine(assetDirectory, row.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".part";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(cdn.AssetUrl(row.Md5), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return $"HTTP {(int)response.StatusCode}";
                }

                string hash;
                long written;
                await using (var output = File.Create(temporary))
                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                using (var md5 = MD5.Create())
                await using (var digest = new CryptoStream(output, md5, CryptoStreamMode.Write))
                {
                    await input.CopyToAsync(digest, cancellationToken);
                    await digest.FlushFinalBlockAsync(cancellationToken);
                    written = output.Length;
                    hash = Convert.ToHexString(md5.Hash!).ToLowerInvariant();
                }

                if (written != row.Size || !string.Equals(hash, row.Md5, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(temporary);
                    return $"got {written} bytes with MD5 {hash}, expected {row.Size} bytes with MD5 {row.Md5}";
                }

                File.Move(temporary, target, overwrite: true);
                return null;
            }
            catch (Exception e) when (e is HttpRequestException or IOException
                                      || e is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                if (attempt >= 3)
                {
                    return e.Message;
                }
            }
        }
    }

    private static Dictionary<string, string> LoadState(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (JsonException)
        {
            // An interrupted write leaves a broken file; every row is then checked again, which is safe.
            return [];
        }
    }

    private static void SaveState(string path, Dictionary<string, string> state)
    {
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }
}
