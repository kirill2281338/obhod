using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ObhodSetup
{
    internal static class InstallerUpdates
    {
        private const string AssetName = "obhod-setup.exe";
        private const long MaximumAssetSize = 100L * 1024 * 1024;

        internal static ReleaseAsset FindNewer()
        {
            string repository = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "ObhodReleaseRepository")?.Value;
            if (string.IsNullOrEmpty(repository)) return null;
            if (!Regex.IsMatch(repository, @"\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z"))
                throw new InvalidOperationException("Неверный адрес репозитория обновлений.");

            var request = Installer.Request($"https://api.github.com/repos/{repository}/releases/latest");
            request.Timeout = 5000;
            request.ReadWriteTimeout = 5000;
            request.AllowAutoRedirect = false;
            request.Accept = "application/vnd.github+json";
            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
                return ReadRelease(stream, repository, new Version(Program.Version));
        }

        internal static ReleaseAsset ReadRelease(Stream stream, string repository, Version current)
        {
            var release = (GitHubRelease)new DataContractJsonSerializer(typeof(GitHubRelease)).ReadObject(stream);
            string tag = release?.Tag;
            if (tag == null || !Regex.IsMatch(tag, @"\Av[0-9]+\.[0-9]+\.[0-9]+\z") ||
                !Version.TryParse(tag.Substring(1), out var version) || version <= current) return null;

            var item = release.Assets?.FirstOrDefault(a => a.Name == AssetName);
            if (item == null || item.Size <= 0 || item.Size > MaximumAssetSize || item.Digest == null ||
                !Regex.IsMatch(item.Digest, @"\Asha256:[a-fA-F0-9]{64}\z")) return null;

            // Construct the URL from the configured repository and validated tag.
            return new ReleaseAsset(version, $"https://github.com/{repository}/releases/download/{tag}/{AssetName}",
                item.Digest.Substring(7), item.Size);
        }

        internal static DownloadedInstaller Download(ReleaseAsset release)
        {
            var directory = Path.Combine(Path.GetDirectoryName(Program.InstallDir), ".obhod-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var installer = new DownloadedInstaller(directory, Path.Combine(directory, AssetName));
            try
            {
                using (var response = Installer.Request(release.Url).GetResponse())
                using (var incoming = response.GetResponseStream())
                using (var file = new FileStream(installer.Path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = incoming.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (file.Length + read > release.Size) throw new InvalidDataException("Неверный размер установщика.");
                        file.Write(buffer, 0, read);
                    }
                    if (file.Length != release.Size) throw new InvalidDataException("Загрузка установщика прервалась.");
                    file.Position = 0;
                    using (var sha = SHA256.Create())
                    {
                        string digest = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
                        if (!digest.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Контрольная сумма установщика не совпала.");
                    }
                }
                return installer;
            }
            catch
            {
                installer.Dispose();
                throw;
            }
        }

        internal sealed class ReleaseAsset
        {
            internal ReleaseAsset(Version version, string url, string sha256, long size) =>
                (Version, Url, Sha256, Size) = (version, url, sha256, size);
            internal Version Version { get; }
            internal string Url { get; }
            internal string Sha256 { get; }
            internal long Size { get; }
        }

        internal sealed class DownloadedInstaller : IDisposable
        {
            private readonly string _directory;
            internal string Path { get; }
            internal DownloadedInstaller(string directory, string path) => (_directory, Path) = (directory, path);
            internal Process Start() => Process.Start(new ProcessStartInfo(Path, "/skip-update-check") { UseShellExecute = false })
                ?? throw new InvalidOperationException("Не удалось запустить установщик.");

            public void Dispose()
            {
                try { File.Delete(Path); Directory.Delete(_directory); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        [DataContract]
        private sealed class GitHubRelease
        {
            [DataMember(Name = "tag_name")] public string Tag { get; set; }
            [DataMember(Name = "assets")] public GitHubAsset[] Assets { get; set; }
        }

        [DataContract]
        private sealed class GitHubAsset
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "digest")] public string Digest { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
        }
    }
}
