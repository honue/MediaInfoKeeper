using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MediaInfoKeeper.Options;

namespace MediaInfoKeeper.Common {
    internal sealed class PluginUpdateSource {
        public const string OfficialRepository = "honue/MediaInfoKeeper";

        private PluginUpdateSource(string repository) {
            Repository = repository;
        }

        public string Repository { get; }

        public string RepositoryUrl => $"https://github.com/{Repository}";

        public bool IsOfficial => string.Equals(Repository, OfficialRepository, StringComparison.OrdinalIgnoreCase);

        public bool MatchesAssembly(Assembly assembly) {
            var repository = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(attribute => string.Equals(attribute.Key, "ReleaseRepository", StringComparison.Ordinal))
                ?.Value;
            if (string.IsNullOrWhiteSpace(repository)) repository = OfficialRepository;

            return string.Equals(Repository, repository.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        public static PluginUpdateSource Resolve(string updateChannel, string betaRepositoryUrl) {
            if (!string.Equals(updateChannel, MainPageOptions.UpdateChannelOption.Beta.ToString(),
                    StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(betaRepositoryUrl))
                return new PluginUpdateSource(OfficialRepository);

            if (!Uri.TryCreate(betaRepositoryUrl.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw InvalidRepositoryUrl();

            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length != 2) throw InvalidRepositoryUrl();

            var owner = segments[0];
            var repository = segments[1];
            if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                repository = repository.Substring(0, repository.Length - 4);

            if (!Regex.IsMatch(owner, "\\A[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\\z") ||
                !Regex.IsMatch(repository, "\\A[A-Za-z0-9._-]+\\z") || repository == "." || repository == "..")
                throw InvalidRepositoryUrl();

            return new PluginUpdateSource($"{owner}/{repository}");
        }

        public string GetReleasesUrl(int page, int pageSize) {
            return $"https://api.github.com/repos/{Repository}/releases?per_page={pageSize}&page={page}";
        }

        public string GetVersionUrl(string releaseTag) {
            // Custom releases may come from a branch other than master.
            var revision = IsOfficial ? "master" : Uri.EscapeDataString(releaseTag);
            return $"https://raw.githubusercontent.com/{Repository}/{revision}/Version.json";
        }

        private static ArgumentException InvalidRepositoryUrl() {
            return new ArgumentException(
                "Beta GitHub 仓库链接无效，请填写仓库首页，例如 https://github.com/sisheng36/MediaInfoKeeper。");
        }
    }
}
