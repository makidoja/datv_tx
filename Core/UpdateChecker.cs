using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DatvTx.Core
{
    /// <summary>Looks for a newer release on GitHub (makidoja/datv_tx).</summary>
    public static class UpdateChecker
    {
        public const string ReleasesPage = "https://github.com/makidoja/datv_tx/releases";
        private const string ApiUrl = "https://api.github.com/repos/makidoja/datv_tx/releases?per_page=20";

        public class Result
        {
            public string Tag;
            public string Url;
        }

        /// <summary>
        /// Returns the newest release newer than <paramref name="current"/>, or null.
        /// Betas are offered only while you are running a beta.
        /// </summary>
        public static async Task<Result> CheckAsync(string current)
        {
            Version cur; bool curPre;
            if (!Parse(current, out cur, out curPre)) return null;

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("DatvTx/" + cur);
                http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                string json = await http.GetStringAsync(ApiUrl).ConfigureAwait(false);

                Result best = null; Version bestV = cur; bool bestPre = curPre;
                foreach (var r in JArray.Parse(json))
                {
                    if ((bool?)r["draft"] == true) continue;
                    string tag = (string)r["tag_name"];
                    Version v; bool pre;
                    if (!Parse(tag, out v, out pre)) continue;
                    if (pre && !curPre) continue;
                    if (IsNewer(v, pre, bestV, bestPre))
                    {
                        best = new Result { Tag = tag, Url = (string)r["html_url"] ?? ReleasesPage };
                        bestV = v; bestPre = pre;
                    }
                }
                return best;
            }
        }

        private static bool IsNewer(Version v, bool pre, Version than, bool thanPre)
        {
            int c = v.CompareTo(than);
            return c > 0 || (c == 0 && thanPre && !pre);
        }

        /// <summary>"v1.0.0", "0.1.3-beta" or "0.1.3-beta+abc123" → 1.0.0 / 0.1.3 and whether it's a pre-release.</summary>
        public static bool Parse(string s, out Version v, out bool pre)
        {
            v = null; pre = false;
            if (string.IsNullOrEmpty(s)) return false;
            var m = Regex.Match(s.Trim(), @"^v?(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.\-]+)?");
            if (!m.Success) return false;
            v = new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            pre = m.Groups[4].Success;
            return true;
        }

        /// <summary>Version for display, without any "+commit" build suffix.</summary>
        public static string DisplayVersion(string productVersion)
        {
            int plus = productVersion.IndexOf('+');
            return plus >= 0 ? productVersion.Substring(0, plus) : productVersion;
        }
    }
}
