using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using System.Windows;
using K4os.Hash.xxHash; // <-- NuGet: K4os.Hash.xxHash

namespace WoWLauncher.Patcher;

/// <summary>
///     Responsible for downloading new patches
/// </summary>
internal class PatchController
{
    private static readonly string CacheFilePath = "Cache/Hash/Cache.txt";

    // Cache: filename -> (hash, size, lastWriteUtc)
    private readonly Dictionary<string, CacheEntry> fileHashCache = new();

    private readonly string logFilePath = "Logs/Launcher_log.log";
    private readonly Stopwatch m_DownloadStopWatch;
    private readonly string m_PatchListUri = "http://MadClownWorld.com/Patch/CheckList.txt";
    private readonly string m_PatchUri = "http://MadClownWorld.com/Patch/Files/";
    private readonly MainWindow m_WndRef;

    private List<PatchData> m_Patches;
    private int m_PatchIndex;

    public PatchController(MainWindow _wndRef)
    {
        m_WndRef = _wndRef;
        m_DownloadStopWatch = new Stopwatch();
        m_Patches = new List<PatchData>();
        m_PatchIndex = -1;
    }

    public bool IsPatching { get; private set; }

    private struct CacheEntry
    {
        public string Hash;
        public long Size;
        public DateTime LastWriteUtc;
    }

    private struct PatchData
    {
        public string Filename;
        public string Checksum;
        public string Link;
        public long Size;
        public DateTime Timestamp;
    }

    private void Log(string message)
    {
        File.AppendAllText(logFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}: {message}\n");
    }

    public static double Map(double value, double fromSource, double toSource, double fromTarget, double toTarget)
    {
        return (value - fromSource) / (toSource - fromSource) * (toTarget - fromTarget) + fromTarget;
    }

    #region ---------- CACHE ----------
    private void LoadCache()
    {
        if (!File.Exists(CacheFilePath)) return;

        var lines = File.ReadAllLines(CacheFilePath);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(',');
            if (parts.Length == 2) // old format
            {
                fileHashCache[parts[0]] = new CacheEntry { Hash = parts[1], Size = 0, LastWriteUtc = DateTime.MinValue };
            }
            else if (parts.Length >= 4 &&
                     long.TryParse(parts[2], out var size) &&
                     DateTime.TryParse(parts[3], out var dt))
            {
                fileHashCache[parts[0]] = new CacheEntry { Hash = parts[1], Size = size, LastWriteUtc = dt };
            }
        }
        Log($"Loaded {fileHashCache.Count} cache entries");
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFilePath)!);
            using var writer = new StreamWriter(CacheFilePath, false);
            foreach (var kv in fileHashCache)
            {
                writer.WriteLine($"{kv.Key},{kv.Value.Hash},{kv.Value.Size},{kv.Value.LastWriteUtc:yyyy-MM-ddTHH:mm:ssZ}");
            }
            Log("Cache saved");
        }
        catch (Exception ex)
        {
            Log("Error saving cache: " + ex.Message);
        }
    }

    public static void ClearCache()
    {
        if (File.Exists(CacheFilePath))
            File.Delete(CacheFilePath);
    }
    #endregion

    #region ---------- HASH ----------
    private async Task<string> ComputeXXHash64Async(string path, IProgress<int> progress = null)
    {
        const int bufferSize = 1024 * 1024;
        var buffer = new byte[bufferSize];
        ulong hash = 0;

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize, FileOptions.SequentialScan);

        var fi = new FileInfo(path);
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            hash ^= XXH64.DigestOf(buffer, 0, bytesRead);
            totalRead += bytesRead;
            progress?.Report((int)(totalRead * 100 / fi.Length));
        }

        return hash.ToString("X16");
    }
    #endregion

    #region ---------- PATCH ----------
    public async Task CheckPatch(bool _init = true)
    {
        if (_init)
        {
            m_WndRef.ProgressInfo.IsEnabled = false;
            m_WndRef.ProgressInfo2.IsEnabled = false;
            m_WndRef.ProgressInfo3.IsEnabled = false;
            m_WndRef.ProgressBar.Value = 0;

            try
            {
                var request = WebRequest.Create(m_PatchListUri);
                using var _ = await request.GetResponseAsync();
            }
            catch
            {
                m_WndRef.ProgressBar.Value = 100;
                m_WndRef.PlayBtn.IsEnabled = true;
                m_WndRef.ProgressInfo.Visibility = Visibility.Visible;
                m_WndRef.ProgressInfo.Content = "Unable to download Check list!";
                return;
            }

            m_WndRef.ProgressInfo.Visibility = Visibility.Visible;
            m_WndRef.ProgressInfo2.Visibility = Visibility.Visible;
            m_WndRef.ProgressInfo3.Visibility = Visibility.Visible;
            m_WndRef.ProgressInfo4.Visibility = Visibility.Visible;
            m_WndRef.ProgressInfo.Content = "Getting Check list...";

            Directory.CreateDirectory("Cache/L");
            foreach (var f in new[] { "Cache/L/plist.txt", "Cache/L/patching", logFilePath })
                if (File.Exists(f)) File.Delete(f);

            Directory.CreateDirectory("Logs");
            LoadCache();

            using var wc = new WebClient();
            wc.DownloadFileAsync(new Uri(m_PatchListUri), "Cache/L/plist.txt");
            wc.DownloadFileCompleted += (s, e) => { if (!e.Cancelled && e.Error == null) CheckPatch(false); };
            return;
        }

        if (!File.Exists("Cache/L/plist.txt")) return;

        m_Patches = PreparePatchList(File.ReadLines("Cache/L/plist.txt"));
        if (m_Patches.Count == 0)
        {
            FinishPatch();
            return;
        }

        Directory.CreateDirectory("Data");

        // --------------------------------------------------------------
        //  NEW: Fast cache-vs-server compare
        // --------------------------------------------------------------
        var toVerify = new List<PatchData>();
        foreach (var patch in m_Patches)
        {
            if (fileHashCache.TryGetValue(patch.Filename, out var cached) &&
                string.Equals(cached.Hash, patch.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                Log($"Cache hit (hash match) – skipping {patch.Filename}");
                continue; // hash already matches → no work needed
            }
            toVerify.Add(patch);
        }

        if (toVerify.Count == 0)
        {
            Log("All files already up-to-date");
            FinishPatch();
            return;
        }

        // Only the files that differ go through the normal verify/download flow
        m_Patches = toVerify;
        // --------------------------------------------------------------

        if (File.Exists("Cache/L/patching"))
        {
            var incomplete = File.ReadAllText("Cache/L/patching").Trim();
            var path = $"Data/{incomplete}";
            if (File.Exists(path)) File.Delete(path);
        }

        m_PatchIndex = 0;
        File.WriteAllText("Cache/L/patching", m_Patches[0].Filename.ToLower());
        IsPatching = true;
        await DownloadPatch(m_PatchIndex);
    }

    private List<PatchData> PreparePatchList(IEnumerable<string> lines)
    {
        var patches = new List<PatchData>();
        foreach (var line in lines)
        {
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) continue;

            var pd = new PatchData
            {
                Filename = parts[0].ToLower(),
                Checksum = parts[1].ToUpper(),
                Link = parts.Length > 2 ? parts[2] : ""
            };
            if (parts.Length >= 4 && long.TryParse(parts[3], out var size)) pd.Size = size;
            if (parts.Length >= 5 && DateTime.TryParse(parts[4], out var dt)) pd.Timestamp = dt;
            patches.Add(pd);
        }
        return patches;
    }

    private async Task DownloadPatch(int index)
    {
        var patch = m_Patches[m_PatchIndex];
        var patchName = patch.Filename;
        var patchHash = patch.Checksum;
        var localPath = $"Data/{patchName}";

        Log($"Checking {m_PatchIndex + 1}/{m_Patches.Count}: {patchName}");

        m_WndRef.ProgressInfo.IsEnabled = true;
        m_WndRef.ProgressInfo2.IsEnabled = true;
        m_WndRef.ProgressInfo3.IsEnabled = true;
        m_WndRef.ProgressInfo4.IsEnabled = true;
        m_WndRef.ProgressInfo.Content = $"Checking {m_PatchIndex + 1}/{m_Patches.Count}: {patchName}";

        string localHash = null;

        if (File.Exists(localPath))
        {
            var fi = new FileInfo(localPath);

            // FAST PRE-CHECK (size + timestamp)
            if (fileHashCache.TryGetValue(patchName, out var cached) &&
                cached.Size == fi.Length &&
                cached.LastWriteUtc == fi.LastWriteTimeUtc)
            {
                localHash = cached.Hash;
                Log("Cache hit (size+time)");
            }
            else
            {
                Log("Computing xxHash64...");
                m_WndRef.ProgressBar.Value = 0;
                var progress = new Progress<int>(p =>
                {
                    m_WndRef.Dispatcher.Invoke(() =>
                    {
                        m_WndRef.ProgressBar.Value = p;
                        m_WndRef.ProgressInfo2.Content = $"{p}%";
                    });
                });

                localHash = await ComputeXXHash64Async(localPath, progress);

                fileHashCache[patchName] = new CacheEntry
                {
                    Hash = localHash,
                    Size = fi.Length,
                    LastWriteUtc = fi.LastWriteTimeUtc
                };
                SaveCache();
            }

            if (string.Equals(localHash, patchHash, StringComparison.OrdinalIgnoreCase))
            {
                Log("Hash match – skipping");
                NextPatch();
                return;
            }
            else
            {
                Log($"Hash mismatch: local={localHash}, server={patchHash}");
                File.Delete(localPath);
            }
        }
        else
        {
            Log("File missing");
        }

        // DOWNLOAD
        File.WriteAllText("Cache/L/patching", patchName);
        m_WndRef.ProgressBar.Value = 0;

        using var wc = new WebClient();
        wc.DownloadProgressChanged += (s, e) => UpdateDownloadProgress(e, patchName);
        wc.DownloadFileCompleted += async (s, e) =>
        {
            if (e.Error != null)
            {
                ErroredPatch($"Download failed: {e.Error.Message}");
                return;
            }

            var fi = new FileInfo(localPath);
            fileHashCache[patchName] = new CacheEntry
            {
                Hash = patchHash,
                Size = fi.Length,
                LastWriteUtc = fi.LastWriteTimeUtc
            };
            SaveCache();

            NextPatch();
        };

        m_DownloadStopWatch.Restart();
        wc.DownloadFileAsync(new Uri($"{m_PatchUri}{patchName}"), localPath);
    }

    private void UpdateDownloadProgress(DownloadProgressChangedEventArgs e, string patchName)
    {
        var totalMB = e.TotalBytesToReceive / (1024f * 1024f);
        var downloadedMB = e.BytesReceived / (1024f * 1024f);
        var speed = downloadedMB / m_DownloadStopWatch.Elapsed.TotalSeconds;
        var eta = TimeSpan.FromSeconds((totalMB - downloadedMB) / (speed > 0 ? speed : 1));

        m_WndRef.ProgressBar.Value = e.ProgressPercentage;
        m_WndRef.ProgressInfo.Content = $"Downloading {m_PatchIndex + 1}/{m_Patches.Count}: {patchName}";
        m_WndRef.ProgressInfo2.Content = $"{e.ProgressPercentage}%  {speed:0.0} MB/s";
        m_WndRef.ProgressInfo3.Content = $"{totalMB - downloadedMB:0.0} MB left";
        m_WndRef.ProgressInfo4.Content = $"ETA: {eta:h\\h\\ mm\\m\\ ss\\s}";
    }

    private async void NextPatch()
    {
        m_PatchIndex++;
        if (m_PatchIndex >= m_Patches.Count)
        {
            FinishPatch();
        }
        else
        {
            File.WriteAllText("Cache/L/patching", m_Patches[m_PatchIndex].Filename.ToLower());
            await DownloadPatch(m_PatchIndex);
        }
    }

    private void ErroredPatch(string msg)
    {
        m_WndRef.ProgressBar.Value = 100;
        m_WndRef.PlayBtn.IsEnabled = false;
        m_WndRef.ProgressInfo.Content = "Download error occurred.";
        m_WndRef.ProgressInfo2.Content = "Restart launcher and try again.";
        Log(msg);
    }

    private void FinishPatch()
    {
        m_WndRef.ProgressBar.Value = 100;
        m_WndRef.PlayBtn.IsEnabled = true;
        m_WndRef.ProgressInfo.Visibility = Visibility.Hidden;
        m_WndRef.ProgressInfo2.Visibility = Visibility.Hidden;
        m_WndRef.ProgressInfo3.Visibility = Visibility.Hidden;
        m_WndRef.ProgressInfo4.Visibility = Visibility.Hidden;

        IsPatching = false;
        m_PatchIndex = -1;
        m_Patches.Clear();
        m_DownloadStopWatch.Reset();

        foreach (var f in new[] { "Cache/L/patching", "Cache/L/plist.txt" })
            if (File.Exists(f)) File.Delete(f);

        Log("Patching completed successfully");
    }
    #endregion
}