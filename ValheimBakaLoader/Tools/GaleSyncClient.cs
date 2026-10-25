using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Tools.Http;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// One mod line from a Gale profile's manifest, as read. A line the reader could not make
    /// sense of is still handed on, with the parts it could not read left null, so the diff is
    /// the one place that decides what to skip and the host can be told how many it skipped.
    /// </summary>
    public sealed class GaleProfileMod
    {
        /// <summary>"Namespace-Name", or null when the line carried no name that is a string.</summary>
        public string Name { get; init; }

        /// <summary>The profile's tick for this mod, or null when the line carried no true or false.</summary>
        public bool? Enabled { get; init; }

        /// <summary>
        /// "major.minor.patch" built from the line's version object, or null when any of the
        /// three was missing, not a whole number, or below zero.
        /// </summary>
        public string Version { get; init; }

        /// <summary>
        /// A hash of the mod's files, or null. Gale's service carries versions only and never
        /// sets it; a feed that reads a mod folder on this PC (a test copy) does, so a mod rebuilt
        /// without a new version number still compares as different.
        /// </summary>
        public string ContentHash { get; init; }
    }

    /// <summary>What the profile's /meta answered, when it answered with something readable.</summary>
    public sealed class GaleProfileMeta
    {
        /// <summary>Every mod line in the manifest, in the order the profile lists them.</summary>
        public IReadOnlyList<GaleProfileMod> Mods { get; init; } = Array.Empty<GaleProfileMod>();

        /// <summary>
        /// The profile's updatedAt exactly as the service wrote it. It is only ever compared
        /// for equality with the one stored after the last approval or dismissal, so it is
        /// kept as the service's own text and never turned into a date and back.
        /// </summary>
        public string UpdatedAt { get; init; }

        /// <summary>
        /// The game the profile is for, manifest.community as the service wrote it ("valheim",
        /// "atlyss", "bonelab", ...), or null when the manifest carried none, as an old profile
        /// may not. Kept here so the check can refuse a profile for another game; the client
        /// itself reads it and nothing more.
        /// </summary>
        public string Community { get; init; }

        /// <summary>
        /// True when the mods were read off a realm folder on this PC (a test copy) rather than
        /// from Gale's service. Such a feed names each mod by its folder, which need not be the
        /// "Namespace-Name" Thunderstore spells, and a mod with no readable version is still
        /// compared, by its files.
        /// </summary>
        public bool LocalFeed { get; init; }
    }

    /// <summary>The ways a check of a Gale profile can come out. Every one of them is an answer, none is a throw.</summary>
    public enum GaleCheckOutcome
    {
        /// <summary>The service answered with a manifest this side could read.</summary>
        Ok,

        /// <summary>The id was refused here before anything was sent.</summary>
        InvalidId,

        /// <summary>The service answered 400: it does not take that id.</summary>
        BadRequest,

        /// <summary>The service answered 404: no profile has that id, or it was deleted.</summary>
        NotFound,

        /// <summary>No full answer inside <see cref="GaleSyncClient.Bound"/>.</summary>
        Timeout,

        /// <summary>The service answered with something other than the three above.</summary>
        ServerError,

        /// <summary>No answer at all: no connection, no name lookup, a reset.</summary>
        NetworkError,

        /// <summary>A 200 whose body is not the shape a profile's meta has.</summary>
        Malformed,

        /// <summary>The caller called it off before it finished.</summary>
        Cancelled,

        /// <summary>
        /// The service answered with a readable profile for a game other than Valheim. The
        /// client never answers this; the check does, from the community the meta carries.
        /// </summary>
        OtherGame,
    }

    /// <summary>What a check came to: the outcome, the meta when there is one, and the sentence for the status line.</summary>
    public sealed class GaleCheckResult
    {
        public GaleCheckOutcome Outcome { get; init; }

        /// <summary>The profile's meta. Set only when <see cref="Outcome"/> is <see cref="GaleCheckOutcome.Ok"/>.</summary>
        public GaleProfileMeta Meta { get; init; }

        /// <summary>The HTTP status the service answered with, or null when it did not answer.</summary>
        public int? StatusCode { get; init; }

        /// <summary>
        /// The catalog id of the one quiet status line this outcome gets, or null for
        /// <see cref="GaleCheckOutcome.Ok"/>, whose status line is about the manifest rather
        /// than about the trip.
        /// </summary>
        public string SentenceId { get; init; }

        /// <summary>
        /// The game a profile for another game is for, filled into the status sentence. Set
        /// only when <see cref="Outcome"/> is <see cref="GaleCheckOutcome.OtherGame"/>.
        /// </summary>
        public string Game { get; init; }

        public bool Ok => Outcome == GaleCheckOutcome.Ok;
    }

    /// <summary>
    /// What fetching a profile's zip came to. On <see cref="GaleCheckOutcome.Ok"/> the zip is
    /// in a temp file at <see cref="ZipPath"/>, and disposing this result deletes it, so the
    /// caller holds it in a using and nothing is left behind in the temp folder.
    /// </summary>
    public sealed class GaleZipResult : IDisposable
    {
        public GaleCheckOutcome Outcome { get; init; }

        /// <summary>The HTTP status of the last answer, or null when nothing answered.</summary>
        public int? StatusCode { get; init; }

        /// <summary>The catalog id of the sentence for a fetch that did not come back, or null on Ok.</summary>
        public string SentenceId { get; init; }

        /// <summary>The temp file the zip was written to. Set only on Ok.</summary>
        public string ZipPath { get; init; }

        public bool Ok => Outcome == GaleCheckOutcome.Ok;

        public void Dispose()
        {
            if (string.IsNullOrEmpty(ZipPath)) return;
            try { if (File.Exists(ZipPath)) File.Delete(ZipPath); }
            catch (Exception) { /* a temp file the system will clear; nothing to say about it */ }
        }
    }

    public interface IGaleSyncClient
    {
        /// <summary>
        /// Reads one profile's meta. Never throws, and never takes longer than
        /// <see cref="GaleSyncClient.Bound"/>: every way it can go wrong comes back as a
        /// <see cref="GaleCheckResult"/>.
        /// </summary>
        Task<GaleCheckResult> GetMetaAsync(string profileId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Fetches one profile's zip (its configs and its export) into a temp file. Never throws,
        /// and never takes longer than <see cref="GaleSyncClient.ZipBound"/>.
        /// </summary>
        Task<GaleZipResult> GetProfileZipAsync(string profileId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The one thing in BakaLoader that talks to Gale's sync service. It reads two things, both
    /// with no login: a profile's meta, GET https://gale.kesomannen.com/api/profile/{id}/meta,
    /// and the profile's zip, GET https://gale.kesomannen.com/api/profile/{id}. It is never
    /// asked about a profile the host has not named, and nothing is ever sent to it except the
    /// id in the address.
    /// <para>
    /// The meta trip, headers and body, is held to <see cref="Bound"/>. The check runs at
    /// start up, when the Mods hall opens and on Check now, and a service that is slow or down
    /// must cost the host one quiet line, not a stuck hall. A body larger than
    /// <see cref="MaxBodyBytes"/> is read as malformed rather than read to the end. The meta
    /// check talks to <see cref="Host"/> and nothing else.
    /// </para>
    /// <para>
    /// The zip is the config step, and it is fetched only when the host opens the configs part
    /// of the Gale dialog or presses its Refresh, never by the check. The service answers the
    /// zip's address with a redirect to the file's storage, so that second host is contacted
    /// too, for that fetch only. The shared transport follows a redirect on its own; when an
    /// answer still comes back as one, a single hop is followed here, and only to an https
    /// address. The trip is held to <see cref="ZipBound"/> and <see cref="MaxZipBytes"/>.
    /// </para>
    /// </summary>
    public sealed class GaleSyncClient : IGaleSyncClient
    {
        /// <summary>The service's address. The meta check contacts nothing else.</summary>
        public const string BaseUrl = "https://gale.kesomannen.com/api";

        /// <summary>The service's host, for the tests that prove the check asks nothing else.</summary>
        public const string Host = "gale.kesomannen.com";

        /// <summary>A profile's meta is a few kilobytes; a hundred times that is not a meta.</summary>
        public const int MaxBodyBytes = 2 * 1024 * 1024;

        /// <summary>A profile zip is configs and a manifest. Fifty megabytes is far past any real one.</summary>
        public const long MaxZipBytes = 50L * 1024 * 1024;

        /// <summary>
        /// The longest one zip fetch may take, start to finish. Thirty seconds in the app;
        /// settable for a test. Only the host's own press waits on it.
        /// </summary>
        public TimeSpan ZipBound { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Where the zip is written while it is read. The system temp folder in the app; settable for a test.</summary>
        public string TempDirectory { get; set; }

        public const string BadRequestSentence = "mods.gale.check.bad_request";
        public const string NotFoundSentence = "mods.gale.check.not_found";
        public const string TimeoutSentence = "mods.gale.check.timeout";
        public const string ServerErrorSentence = "mods.gale.check.server_error";
        public const string NetworkErrorSentence = "mods.gale.check.network";
        public const string MalformedSentence = "mods.gale.check.malformed";
        public const string CancelledSentence = "mods.gale.check.cancelled";

        // The zip's own sentences. The trip is the same shape as the meta's, but the host is
        // somewhere else when it fails (the configs part of the dialog, with its Refresh, not
        // the box with its Check now) and the bound is thirty seconds, not ten.
        public const string ZipBadRequestSentence = "mods.gale.configs.fetch.bad_request";
        public const string ZipNotFoundSentence = "mods.gale.configs.fetch.not_found";
        public const string ZipTimeoutSentence = "mods.gale.configs.fetch.timeout";
        public const string ZipServerErrorSentence = "mods.gale.configs.fetch.server_error";
        public const string ZipNetworkErrorSentence = "mods.gale.configs.fetch.network";
        public const string ZipMalformedSentence = "mods.gale.configs.fetch.malformed";
        public const string ZipCancelledSentence = "mods.gale.configs.fetch.cancelled";

        private readonly Func<HttpClient> NewClient;

        /// <summary>
        /// The longest one check may take, start to finish. Ten seconds in the app; settable so
        /// a test can prove the bound without waiting it out.
        /// </summary>
        public TimeSpan Bound { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>
        /// Every request goes through the app's one transport seam, so the Connection card's
        /// switches apply here too and HttpTransportSwitchTests holds. A test hands in a
        /// provider over its own fake HttpMessageHandler; nothing here builds a client of its own.
        /// </summary>
        public GaleSyncClient(IHttpClientProvider httpClientProvider)
        {
            if (httpClientProvider == null) throw new ArgumentNullException(nameof(httpClientProvider));
            NewClient = httpClientProvider.CreateClient;
        }

        /// <summary>The address a profile's meta is read from. Null for an id the shape check refuses.</summary>
        public static Uri MetaUri(string profileId)
        {
            var check = GaleProfileId.Check(profileId);
            if (!check.Ok) return null;
            return new Uri(BaseUrl + "/profile/" + Uri.EscapeDataString(check.Id) + "/meta");
        }

        public async Task<GaleCheckResult> GetMetaAsync(string profileId, CancellationToken cancellationToken = default)
        {
            var uri = MetaUri(profileId);
            if (uri == null)
            {
                return new GaleCheckResult { Outcome = GaleCheckOutcome.InvalidId, SentenceId = GaleProfileId.BadIdSentence };
            }

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(Bound);

            int? status = null;
            try
            {
                using var client = NewClient();
                // The bound above is the only clock. HttpClient's own Timeout is released the
                // moment the headers land, which is exactly the stall a body can still have.
                client.Timeout = Timeout.InfiniteTimeSpan;

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.ParseAdd("application/json");

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token)
                    .ConfigureAwait(false);
                status = (int)response.StatusCode;

                if (response.StatusCode == HttpStatusCode.BadRequest)
                    return Failed(GaleCheckOutcome.BadRequest, BadRequestSentence, status);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return Failed(GaleCheckOutcome.NotFound, NotFoundSentence, status);
                if (!response.IsSuccessStatusCode)
                    return Failed(GaleCheckOutcome.ServerError, ServerErrorSentence, status);

                var body = await ReadBoundedAsync(response.Content, bound.Token).ConfigureAwait(false);
                if (body == null)
                    return Failed(GaleCheckOutcome.Malformed, MalformedSentence, status);

                var meta = Parse(body);
                if (meta == null)
                    return Failed(GaleCheckOutcome.Malformed, MalformedSentence, status);

                return new GaleCheckResult { Outcome = GaleCheckOutcome.Ok, Meta = meta, StatusCode = status };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Failed(GaleCheckOutcome.Cancelled, CancelledSentence, status);
            }
            catch (OperationCanceledException)
            {
                // Ours, not the caller's: the bound ran out.
                return Failed(GaleCheckOutcome.Timeout, TimeoutSentence, status);
            }
            catch (HttpRequestException)
            {
                return Failed(GaleCheckOutcome.NetworkError, NetworkErrorSentence, status);
            }
            catch (IOException)
            {
                // A connection dropped half way through the body.
                return Failed(GaleCheckOutcome.NetworkError, NetworkErrorSentence, status);
            }
            catch (Exception)
            {
                // Belt to the brace. A check is never allowed to throw at the Mods hall.
                return status == null
                    ? Failed(GaleCheckOutcome.NetworkError, NetworkErrorSentence, status)
                    : Failed(GaleCheckOutcome.Malformed, MalformedSentence, status);
            }
        }

        private static GaleCheckResult Failed(GaleCheckOutcome outcome, string sentence, int? status) =>
            new() { Outcome = outcome, SentenceId = sentence, StatusCode = status };

        /// <summary>The address a profile's zip is read from. Null for an id the shape check refuses.</summary>
        public static Uri ProfileUri(string profileId)
        {
            var check = GaleProfileId.Check(profileId);
            if (!check.Ok) return null;
            return new Uri(BaseUrl + "/profile/" + Uri.EscapeDataString(check.Id));
        }

        public async Task<GaleZipResult> GetProfileZipAsync(string profileId, CancellationToken cancellationToken = default)
        {
            var uri = ProfileUri(profileId);
            if (uri == null)
                return new GaleZipResult { Outcome = GaleCheckOutcome.InvalidId, SentenceId = GaleProfileId.BadIdSentence };

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(ZipBound);

            int? status = null;
            string path = null;
            var handedOver = false;
            try
            {
                using var client = NewClient();
                // The same single clock as the meta: HttpClient's own Timeout lets go once the
                // headers are in, and a zip is exactly the body that can stall after them.
                client.Timeout = Timeout.InfiniteTimeSpan;

                var response = await ZipRequestAsync(client, uri, bound.Token).ConfigureAwait(false);
                try
                {
                    status = (int)response.StatusCode;
                    if (IsRedirect(response.StatusCode))
                    {
                        // The transport did not follow it (a test's handler never does). One hop,
                        // to an https address only, and a second redirect is not a zip.
                        var next = RedirectTarget(uri, response);
                        response.Dispose();
                        response = null;
                        if (next == null) return ZipFailed(GaleCheckOutcome.ServerError, ZipServerErrorSentence, status);
                        response = await ZipRequestAsync(client, next, bound.Token).ConfigureAwait(false);
                        status = (int)response.StatusCode;
                        if (IsRedirect(response.StatusCode))
                            return ZipFailed(GaleCheckOutcome.ServerError, ZipServerErrorSentence, status);
                    }

                    if (response.StatusCode == HttpStatusCode.BadRequest)
                        return ZipFailed(GaleCheckOutcome.BadRequest, ZipBadRequestSentence, status);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                        return ZipFailed(GaleCheckOutcome.NotFound, ZipNotFoundSentence, status);
                    if (!response.IsSuccessStatusCode)
                        return ZipFailed(GaleCheckOutcome.ServerError, ZipServerErrorSentence, status);

                    if (response.Content?.Headers.ContentLength is long declared && declared > MaxZipBytes)
                        return ZipFailed(GaleCheckOutcome.Malformed, ZipMalformedSentence, status);

                    var folder = string.IsNullOrWhiteSpace(TempDirectory) ? Path.GetTempPath() : TempDirectory;
                    path = Path.Combine(folder, "bakaloader-gale-" + Guid.NewGuid().ToString("N") + ".zip");
                    if (!await WriteBoundedAsync(response.Content, path, bound.Token).ConfigureAwait(false))
                        return ZipFailed(GaleCheckOutcome.Malformed, ZipMalformedSentence, status);

                    handedOver = true;
                    return new GaleZipResult { Outcome = GaleCheckOutcome.Ok, StatusCode = status, ZipPath = path };
                }
                finally
                {
                    response?.Dispose();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return ZipFailed(GaleCheckOutcome.Cancelled, ZipCancelledSentence, status);
            }
            catch (OperationCanceledException)
            {
                return ZipFailed(GaleCheckOutcome.Timeout, ZipTimeoutSentence, status);
            }
            catch (HttpRequestException)
            {
                return ZipFailed(GaleCheckOutcome.NetworkError, ZipNetworkErrorSentence, status);
            }
            catch (IOException)
            {
                return ZipFailed(GaleCheckOutcome.NetworkError, ZipNetworkErrorSentence, status);
            }
            catch (Exception)
            {
                return status == null
                    ? ZipFailed(GaleCheckOutcome.NetworkError, ZipNetworkErrorSentence, status)
                    : ZipFailed(GaleCheckOutcome.Malformed, ZipMalformedSentence, status);
            }
            finally
            {
                if (!handedOver && path != null)
                {
                    try { if (File.Exists(path)) File.Delete(path); }
                    catch (Exception) { /* a half written temp file the system will clear */ }
                }
            }
        }

        private static Task<HttpResponseMessage> ZipRequestAsync(HttpClient client, Uri uri, CancellationToken token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/zip");
            request.Headers.Accept.ParseAdd("application/octet-stream");
            request.Headers.Accept.ParseAdd("*/*");
            return client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }

        private static bool IsRedirect(HttpStatusCode code) =>
            code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

        /// <summary>Where a redirect points, or null when it points nowhere or anywhere but an https address.</summary>
        internal static Uri RedirectTarget(Uri from, HttpResponseMessage response)
        {
            var location = response?.Headers.Location;
            if (location == null) return null;
            if (!location.IsAbsoluteUri)
            {
                if (!Uri.TryCreate(from, location, out var joined)) return null;
                location = joined;
            }
            return location.Scheme == Uri.UriSchemeHttps ? location : null;
        }

        private static GaleZipResult ZipFailed(GaleCheckOutcome outcome, string sentence, int? status) =>
            new() { Outcome = outcome, SentenceId = sentence, StatusCode = status };

        /// <summary>The body into a file. False when it runs past <see cref="MaxZipBytes"/>, and the file is then left for the caller to drop.</summary>
        private static async Task<bool> WriteBoundedAsync(HttpContent content, string path, CancellationToken token)
        {
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            if (content == null) return true;
            await using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var chunk = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > MaxZipBytes) return false;
                await file.WriteAsync(chunk.AsMemory(0, read), token).ConfigureAwait(false);
            }
            return true;
        }

        /// <summary>The body as text, or null when it runs past <see cref="MaxBodyBytes"/>.</summary>
        private static async Task<string> ReadBoundedAsync(HttpContent content, CancellationToken token)
        {
            if (content == null) return string.Empty;
            if (content.Headers.ContentLength is long declared && declared > MaxBodyBytes) return null;

            using var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaxBodyBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        /// <summary>
        /// Reads the meta document, or answers null when it is not one: not JSON, not an
        /// object, no manifest object, no mods array, or no updatedAt string. A mods array
        /// whose lines are partly unreadable is still a meta; the lines are handed on with what
        /// could be read and the diff skips the rest.
        /// <para>
        /// Dates are not parsed. Newtonsoft's default turns an ISO timestamp into a DateTime,
        /// and writing that back out does not give the service's text again, so a stored
        /// updatedAt compared against a fresh one would never be equal and the row would come
        /// back after every dismissal.
        /// </para>
        /// </summary>
        public static GaleProfileMeta Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            JToken root;
            try
            {
                using var reader = new JsonTextReader(new StringReader(json))
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Decimal,
                };
                root = JToken.ReadFrom(reader);
                // Anything after the document is not a document.
                if (reader.Read()) return null;
            }
            catch (JsonException)
            {
                return null;
            }

            if (root is not JObject meta) return null;
            if (meta["manifest"] is not JObject manifest) return null;
            if (manifest["mods"] is not JArray mods) return null;

            var updatedAt = meta["updatedAt"] is JValue { Type: JTokenType.String } stamp ? (string)stamp : null;
            if (string.IsNullOrWhiteSpace(updatedAt)) return null;

            var lines = new List<GaleProfileMod>(mods.Count);
            foreach (var line in mods)
            {
                lines.Add(ReadMod(line));
            }

            var community = manifest["community"] is JValue { Type: JTokenType.String } game ? ((string)game)?.Trim() : null;

            return new GaleProfileMeta { Mods = lines, UpdatedAt = updatedAt, Community = community };
        }

        private static GaleProfileMod ReadMod(JToken line)
        {
            if (line is not JObject mod) return new GaleProfileMod();

            var name = mod["name"] is JValue { Type: JTokenType.String } n ? ((string)n)?.Trim() : null;
            bool? enabled = mod["enabled"] is JValue { Type: JTokenType.Boolean } e ? (bool)e : null;

            return new GaleProfileMod { Name = name, Enabled = enabled, Version = ReadVersion(mod["version"]) };
        }

        private static string ReadVersion(JToken token)
        {
            if (token is not JObject version) return null;

            var major = Component(version["major"]);
            var minor = Component(version["minor"]);
            var patch = Component(version["patch"]);
            if (major == null || minor == null || patch == null) return null;

            return major + "." + minor + "." + patch;
        }

        private static long? Component(JToken token)
        {
            if (token is not JValue { Type: JTokenType.Integer } value) return null;
            try
            {
                var n = value.ToObject<long>();
                return n < 0 ? null : n;
            }
            catch (Exception)
            {
                // A number too large for a long is not a version component.
                return null;
            }
        }
    }
}
