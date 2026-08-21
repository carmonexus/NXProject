using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NXProject.Services
{
    /// <summary>
    /// Person / group lookups against Azure DevOps using ONLY the endpoints that work with a
    /// project-scoped PAT (the Teams REST API). It deliberately avoids the Graph/Identity APIs
    /// (<c>_apis/graph/*</c>, <c>_apis/identities</c>, <c>_apis/IdentityPicker</c>), which require
    /// extra token scopes (Graph/Identity read) and return 401 with a Work Items only PAT.
    ///
    /// In Azure DevOps every project group shown in an identity field (e.g. the "Adm_NX" field on
    /// the Project work item) is backed by a Team, whose id equals the identity's <c>id</c>. So the
    /// members can be read from <c>_apis/projects/{project}/teams/{teamId}/members</c>.
    /// </summary>
    public static class PersonSearchService
    {
        /// <summary>A person resolved from DevOps (display name + e-mail/uniqueName).</summary>
        public sealed record Person(string DisplayName, string Email);

        private static readonly HttpClient Http = new(new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        });

        private static AuthenticationHeaderValue BasicAuth(string pat) =>
            new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));

        private static bool HasConnection(TfsConnectionOptions o) =>
            o != null && !string.IsNullOrWhiteSpace(o.OrganizationUrl)
                      && !string.IsNullOrWhiteSpace(o.TeamProject)
                      && !string.IsNullOrWhiteSpace(o.PersonalAccessToken);

        /// <summary>
        /// Members of a project Team by its id. Empty list if the id is not a Team or on any error.
        /// </summary>
        public static async Task<List<Person>> GetTeamMembersAsync(
            TfsConnectionOptions options, string teamId, CancellationToken ct = default)
        {
            var people = new List<Person>();
            if (!HasConnection(options) || string.IsNullOrWhiteSpace(teamId)) return people;

            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var projEnc = Uri.EscapeDataString(options.TeamProject);
            var auth = BasicAuth(options.PersonalAccessToken);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int top = 200, skip = 0, safety = 0;
            while (++safety < 1000)
            {
                var url = $"{orgBase}/_apis/projects/{projEnc}/teams/{Uri.EscapeDataString(teamId)}" +
                          $"/members?$top={top}&$skip={skip}&api-version=6.0";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = auth;
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) break;

                var text = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    break;

                int count = 0;
                foreach (var m in arr.EnumerateArray())
                {
                    var identity = m.TryGetProperty("identity", out var idEl) ? idEl : m;
                    var name  = Str(identity, "displayName");
                    var email = Str(identity, "mailAddress") ?? Str(identity, "uniqueName");
                    if (email != null && !email.Contains('@')) email = null;
                    if (string.IsNullOrWhiteSpace(name)) name = email;
                    if (string.IsNullOrWhiteSpace(name)) { count++; continue; }
                    var key = !string.IsNullOrWhiteSpace(email) ? email! : name!;
                    if (seen.Add(key)) people.Add(new Person(name!.Trim(), (email ?? "").Trim()));
                    count++;
                }
                if (count < top) break;
                skip += top;
            }
            return people;
        }

        /// <summary>
        /// Finds a project Team id by its (display) name, matching case-insensitively and also by the
        /// leaf after a backslash (values like "[Scope]\\Team"). Null when not found.
        /// </summary>
        public static async Task<string?> FindTeamIdByNameAsync(
            TfsConnectionOptions options, string name, CancellationToken ct = default)
        {
            if (!HasConnection(options) || string.IsNullOrWhiteSpace(name)) return null;
            var orgBase = options.OrganizationUrl.TrimEnd('/');
            var projEnc = Uri.EscapeDataString(options.TeamProject);
            var auth = BasicAuth(options.PersonalAccessToken);
            var want = name.Trim();
            var wantLeaf = want.Split('\\').Last().Trim();

            int top = 200, skip = 0, safety = 0;
            while (++safety < 1000)
            {
                var url = $"{orgBase}/_apis/projects/{projEnc}/teams?$top={top}&$skip={skip}&api-version=6.0";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = auth;
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) break;

                var text = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(text);
                if (!doc.RootElement.TryGetProperty("value", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    break;

                int count = 0;
                foreach (var t in arr.EnumerateArray())
                {
                    var tn = Str(t, "name")?.Trim();
                    if (string.Equals(tn, want, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tn, wantLeaf, StringComparison.OrdinalIgnoreCase))
                        return Str(t, "id");
                    count++;
                }
                if (count < top) break;
                skip += top;
            }
            return null;
        }

        /// <summary>
        /// Members of the group referenced by an identity field (e.g. Adm_NX). Prefers the group id
        /// (Team id) when available; otherwise resolves the Team by its display name. Empty list when
        /// the group is not a Team or cannot be read.
        /// </summary>
        public static async Task<List<Person>> GetGroupMembersAsync(
            TfsConnectionOptions options, string groupDisplayName, string groupId, CancellationToken ct = default)
        {
            if (!string.IsNullOrWhiteSpace(groupId))
            {
                var byId = await GetTeamMembersAsync(options, groupId, ct);
                if (byId.Count > 0) return byId;
            }
            if (!string.IsNullOrWhiteSpace(groupDisplayName))
            {
                var teamId = await FindTeamIdByNameAsync(options, groupDisplayName, ct);
                if (!string.IsNullOrWhiteSpace(teamId))
                    return await GetTeamMembersAsync(options, teamId!, ct);
            }
            return new List<Person>();
        }

        private static string? Str(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
