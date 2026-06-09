using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using SwellSSH.Models;

namespace SwellSSH.Services
{
    public sealed class AppUpdateService
    {
        private const string ReleaseApiUrl = "https://api.github.com/repos/yaog6700-bit/Swell-SSH/releases/latest";
        private const string AppExeName     = "SwellSSH.exe";
        private const string UpdaterExeName = "SwellSSH.Updater.exe";

        public static Version CurrentVersion
        {
            get
            {
                try
                {
                    var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                    return ver ?? new Version(0, 0, 0, 0);
                }
                catch
                {
                    return new Version(0, 0, 0, 0);
                }
            }
        }

        private static string UpdatesDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwellSSH", "Updates");

        /// <summary>
        /// 检查是否有新版本。返回 null 表示当前已是最新版或网络不可达。
        /// </summary>
        public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
        {
            // Dev builds (0.0.x) 跳过检查
            if (CurrentVersion.Major == 0 && CurrentVersion.Minor == 0) return null;

            using var client = CreateHttpClient(TimeSpan.FromSeconds(20));

            AppGhRelease? release;
            try
            {
                release = await client.GetFromJsonAsync(
                    ReleaseApiUrl, AppUpdateJsonSerializerContext.Default.AppGhRelease, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return null;
            }

            if (release is null || release.Draft || release.Prerelease) return null;

            var tag = (release.TagName ?? string.Empty).TrimStart('v');
            if (!Version.TryParse(tag, out var remoteVersion)) return null;

            var localNorm  = Normalize(CurrentVersion);
            var remoteNorm = Normalize(remoteVersion);

            if (remoteNorm.CompareTo(localNorm) <= 0) return null;

            var rid = CurrentRid();
            if (rid is null) return null;

            var zipName    = $"SwellSSH-{rid}.zip";
            var sha256Name = $"{zipName}.sha256";

            string? zipUrl = null, shaUrl = null;
            if (release.Assets is not null)
            {
                foreach (var a in release.Assets)
                {
                    if (a?.Name is null || a.Url is null) continue;
                    if (a.Name == zipName)    zipUrl = a.Url;
                    if (a.Name == sha256Name) shaUrl = a.Url;
                }
            }

            if (zipUrl is null || shaUrl is null) return null;

            return new UpdateInfo(remoteVersion, release.TagName!, zipUrl, shaUrl, zipName);
        }

        /// <summary>
        /// 下载、校验 SHA256、解压到 staging 目录。
        /// </summary>
        public async Task<UpdateStaging> DownloadVerifyAndExtractAsync(
            UpdateInfo info, IProgress<ProgressDialogUpdate> progress, CancellationToken ct)
        {
            var stageRoot   = Path.Combine(UpdatesDir, info.NewVersion.ToString());
            var downloadDir = Path.Combine(stageRoot, "download");
            var extractDir  = Path.Combine(stageRoot, "extracted");
            var runnerDir   = Path.Combine(stageRoot, "runner");

            if (Directory.Exists(stageRoot))
                try { Directory.Delete(stageRoot, recursive: true); } catch { }

            Directory.CreateDirectory(downloadDir);
            Directory.CreateDirectory(extractDir);
            Directory.CreateDirectory(runnerDir);

            using var client = CreateHttpClient(TimeSpan.FromMinutes(10));

            // 1. 下载 SHA256 校验文件
            progress.Report(new ProgressDialogUpdate("正在获取校验文件…"));
            string expectedHash;
            try
            {
                var shaText = await client.GetStringAsync(info.Sha256Url, ct);
                expectedHash = ParseSha256SumLine(shaText)
                    ?? throw new InvalidDataException("更新校验文件格式异常");
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException("无法下载更新校验文件：" + ex.Message);
            }

            // 2. 下载 zip 并同时计算 SHA256
            var zipPath    = Path.Combine(downloadDir, info.ZipAssetName);
            var actualHash = await DownloadAndHashAsync(client, info.ZipUrl, zipPath, info.ZipAssetName, progress, ct);

            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("更新包校验失败：SHA256 与服务器公布的不一致。");

            // 3. 解压
            progress.Report(new ProgressDialogUpdate("正在解压更新包…"));
            try
            {
                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("更新包解压失败：" + ex.Message);
            }

            // 4. 验证内容完整性
            progress.Report(new ProgressDialogUpdate("正在验证更新包…"));
            var newAppExe     = Path.Combine(extractDir, AppExeName);
            var newUpdaterExe = Path.Combine(extractDir, UpdaterExeName);

            if (!File.Exists(newAppExe) || !File.Exists(newUpdaterExe))
                throw new InvalidDataException("更新包内容异常：缺少必要文件。");

            // 5. 将当前 Updater 复制到 runner 目录（从外部独立运行）
            var installDir     = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var currentUpdater = Path.Combine(installDir, UpdaterExeName);
            if (!File.Exists(currentUpdater))
                throw new FileNotFoundException("缺少升级辅助组件，请重新下载完整安装包。", currentUpdater);

            var stagedRunner = Path.Combine(runnerDir, UpdaterExeName);
            File.Copy(currentUpdater, stagedRunner, overwrite: true);

            progress.Report(new ProgressDialogUpdate("正在准备重启…"));
            return new UpdateStaging(extractDir, stagedRunner, installDir, info.NewVersion);
        }

        /// <summary>
        /// 启动 Updater 进程（等待主程序退出后执行文件覆盖并重启）。
        /// </summary>
        public void LaunchUpdater(UpdateStaging staging)
        {
            var psi = new ProcessStartInfo
            {
                FileName        = staging.RunnerExePath,
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            psi.ArgumentList.Add($"--parent-pid={Environment.ProcessId}");
            psi.ArgumentList.Add($"--extracted-dir={staging.ExtractedDir}");
            psi.ArgumentList.Add($"--install-dir={staging.InstallDir}");
            psi.ArgumentList.Add($"--launch-after={AppExeName}");

            Process.Start(psi);
        }

        /// <summary>
        /// 清理上次更新遗留的 staging 目录（在应用启动时调用）。
        /// </summary>
        public void CleanupOldStagingDirs()
        {
            try
            {
                if (!Directory.Exists(UpdatesDir)) return;
                foreach (var sub in Directory.EnumerateDirectories(UpdatesDir))
                    try { Directory.Delete(sub, recursive: true); } catch { }
            }
            catch { }
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static string? CurrentRid() => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _                  => null,
        };

        // 返回可比较的四元组
        private static (int, int, int, int) Normalize(Version v) =>
            (v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

        private static HttpClient CreateHttpClient(TimeSpan timeout)
        {
            var client = new HttpClient { Timeout = timeout };
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.UserAgent.ParseAdd($"SwellSSH/{CurrentVersion}");
            return client;
        }

        private static string? ParseSha256SumLine(string content)
        {
            var line = content.Trim();
            if (line.Length == 0) return null;
            int sep = 0;
            while (sep < line.Length && !char.IsWhiteSpace(line[sep])) sep++;
            var token = line[..sep];
            if (token.Length != 64) return null;
            foreach (var c in token)
                if (!char.IsAsciiHexDigit(c)) return null;
            return token.ToLowerInvariant();
        }

        private static async Task<string> DownloadAndHashAsync(
            HttpClient client, string url, string destPath, string displayName,
            IProgress<ProgressDialogUpdate> progress, CancellationToken ct)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            progress.Report(new ProgressDialogUpdate(FormatProgress(displayName, 0, total), 0));

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(
                destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            using var hasher   = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer         = new byte[81920];
            long received      = 0;
            long lastReport    = 0;

            while (true)
            {
                var read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
                if (read == 0) break;

                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                hasher.AppendData(buffer, 0, read);
                received += read;

                if (received - lastReport >= 512 * 1024)
                {
                    double? percent = total.HasValue ? (double)received / total.Value * 100 : null;
                    progress.Report(new ProgressDialogUpdate(FormatProgress(displayName, received, total), percent));
                    lastReport = received;
                }
            }

            progress.Report(new ProgressDialogUpdate(FormatProgress(displayName, received, total), 100));
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        private static string FormatProgress(string name, long received, long? total)
        {
            var mb = received / 1024.0 / 1024.0;
            return total.HasValue
                ? $"正在下载 {name} … {mb:0.0} / {total.Value / 1024.0 / 1024.0:0.0} MB"
                : $"正在下载 {name} … {mb:0.0} MB";
        }
    }
}
