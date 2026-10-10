using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AMP.ModSync {
    public class ReleaseAsset {
        public string name;
        public string url;
        public long   size;
    }

    // The latest GitHub release of the community fork
    public class ReleaseInfo {
        private static readonly string[] TrustedHosts = { "github.com", "api.github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

        public string                tag;
        public string                version;
        public List<ReleaseAsset>    assets = new List<ReleaseAsset>();
        public string                notes;

        // GitHub's "latest release" JSON; null if it isn't one, is a draft or prerelease, or has no tag
        public static ReleaseInfo Parse(string githubJson) {
            if(string.IsNullOrEmpty(githubJson)) return null;
            JObject json;
            try {
                json = JObject.Parse(githubJson);
            } catch(JsonException) {
                return null;
            }

            string tag = Text(json["tag_name"]);
            if(string.IsNullOrEmpty(tag) || IsTrue(json["draft"]) || IsTrue(json["prerelease"])) return null;

            var info = new ReleaseInfo {
                tag     = tag,
                version = tag.StartsWith("v", StringComparison.Ordinal) ? tag.Substring(1) : tag,
                notes   = Text(json["body"]) ?? ""
            };
            foreach(JObject item in (json["assets"] as JArray ?? new JArray()).OfType<JObject>()) {
                string name = Text(item["name"]);
                string url = Text(item["browser_download_url"]);
                if(name == null || url == null) continue;
                info.assets.Add(new ReleaseAsset { name = name, url = url, size = item["size"]?.Type == JTokenType.Integer ? (long)item["size"] : 0 });
            }
            return info;
        }

        // Numeric compare of up to four dot-separated parts; anything else counts as 0.0.0, so it is never "newer"
        public static int CompareVersions(string a, string b) {
            int[] x = ParseVersion(a), y = ParseVersion(b);
            for(int i = 0; i < 4; i++) {
                int c = x[i].CompareTo(y[i]);
                if(c != 0) return c;
            }
            return 0;
        }

        public static ReleaseAsset ZipAsset(ReleaseInfo release) {
            return release.assets.FirstOrDefault(a => a.name.StartsWith("AMP-Community-", StringComparison.OrdinalIgnoreCase)
                                                    && a.name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
        }

        // Finds a file's SHA-256 in a `sha256sum` style file ("<hex>  <name>" or "<hex> *<name>"); lowercase hex, or null
        public static string ExpectedSha256(string sha256sums, string fileName) {
            foreach(string raw in (sha256sums ?? "").Split('\n')) {
                string line = raw.Trim();
                if(line.Length < 66 || line[64] != ' ') continue;
                string hex = line.Substring(0, 64);
                if(!hex.All(Uri.IsHexDigit)) continue;
                if(line.Substring(65).TrimStart(' ', '*') == fileName) return hex.ToLowerInvariant();
            }
            return null;
        }

        // Downloads only come from GitHub over https
        public static bool IsTrustedUrl(string url) {
            Uri uri;
            if(!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && TrustedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
        }

        // "owner/name" as GitHub spells it; the setting comes from a config file and ends up in a URL
        public static bool IsValidRepo(string repo) {
            return repo != null && System.Text.RegularExpressions.Regex.IsMatch(repo, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")
                   && !repo.Contains("..");
        }

        // GitHub allows 60 anonymous requests per hour; one check every few hours is plenty
        public static bool CheckDue(long lastCheckUtcTicks, long nowUtcTicks) {
            return lastCheckUtcTicks <= 0 || lastCheckUtcTicks > nowUtcTicks || nowUtcTicks - lastCheckUtcTicks >= TimeSpan.FromHours(6).Ticks;
        }

        private static int[] ParseVersion(string version) {
            var parts = new int[4];
            string[] split = (version ?? "").Split('.');
            if(split.Length > parts.Length) return parts;
            for(int i = 0; i < split.Length; i++) {
                if(!int.TryParse(split[i], NumberStyles.None, CultureInfo.InvariantCulture, out parts[i])) return new int[4];
            }
            return parts;
        }

        private static string Text(JToken token) {
            return token != null && token.Type == JTokenType.String ? (string)token : null;
        }

        private static bool IsTrue(JToken token) {
            return token != null && token.Type == JTokenType.Boolean && (bool)token;
        }
    }
}
