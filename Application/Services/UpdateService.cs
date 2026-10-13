using Octokit;
using PtzJoystickControl.Core.Model;
using PtzJoystickControl.Core.Services;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PtzJoystickControl.Application.Services
{
    public class UpdateService : IUpdateService
    {
        // CI tags releases v<major>.<minor>.<build>; a fourth part is accepted too.
        private static readonly Regex VersionTag = new(@"^v?(\d+\.\d+\.\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant);

        private readonly IGitHubClient _githubClient;
        private readonly string _owner;
        private readonly string _repository;
        private readonly Version _currentVersion;

        public UpdateService(IGitHubClient githubClient, string owner, string repository, Version currentVersion)
        {
            _githubClient = githubClient;
            _owner = owner;
            _repository = repository;
            this._currentVersion = currentVersion;
        }

        // Never throws: any failure is logged and reported as no update, so startup is never affected.
        public async Task<Update> CheckForUpdate()
        {
            Update update;
            try
            {
                // Release.GetLatest skips prereleases, and CI publishes every release as one.
                // One page only (GitHub lists newest first); without ApiOptions GetAll fetches every page.
                var firstPage = new ApiOptions { PageSize = 100, PageCount = 1, StartPage = 1 };
                var releases = await _githubClient.Repository.Release.GetAll(_owner, _repository, firstPage);
                update = FindUpdate(releases, _currentVersion);
            }
            catch (Exception e)
            {
                update = new Update { Error = true, ErrorMessage = e.Message };
            }

            if (update.Error)
                Trace.TraceWarning($"Update check failed, reporting no update: {update.ErrorMessage}");

            return update;
        }

        /// <summary>
        /// Picks the highest-versioned published release (prereleases included, drafts and
        /// releases whose tag is not a version ignored) and reports whether it is newer than
        /// <paramref name="currentVersion"/>. A three-part tag such as v0.0.23 is older than the
        /// four-part build version 0.0.23.43 it was released as, so a current build sees no update.
        /// </summary>
        public static Update FindUpdate(IEnumerable<Release> releases, Version currentVersion)
        {
            Release? newest = null;
            Version? newestVersion = null;
            foreach (var release in releases)
            {
                if (release is null || release.Draft || !TryParseTag(release.TagName, out var version))
                    continue;
                if (newestVersion is null || version > newestVersion)
                {
                    newest = release;
                    newestVersion = version;
                }
            }

            if (newest is null || newestVersion is null)
                return new Update { Error = true, ErrorMessage = "No published release with a version tag was found." };

            if (!Uri.TryCreate(newest.HtmlUrl, UriKind.Absolute, out var uri))
                return new Update
                {
                    Version = newest.TagName,
                    Error = true,
                    ErrorMessage = $"Release {newest.TagName} has no valid page URL.",
                };

            return new Update
            {
                Version = newest.TagName,
                Uri = uri,
                Available = newestVersion > currentVersion,
            };
        }

        private static bool TryParseTag(string? tag, out Version version)
        {
            version = new Version();
            var match = VersionTag.Match(tag?.Trim() ?? "");
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var parsed))
                return false;
            version = parsed;
            return true;
        }
    }
}
