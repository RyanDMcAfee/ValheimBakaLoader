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

        public bool Ok => Outcome == GaleCheckOutcome.Ok;
    }

    public interface IGaleSyncClient
    {
        /// <summary>
        /// Reads one profile's meta. Never throws, and never takes longer than
        /// <see cref="GaleSyncClient.Bound"/>: every way it can go wrong comes back as a
        /// <see cref="GaleCheckResult"/>.
        /// </summary>
        Task<GaleCheckResult> GetMetaAsync(string profileId, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// The one thing in BakaLoader that talks to Gale's sync service, and all it does is read a
    /// profile's meta: GET https://gale.kesomannen.com/api/profile/{id}/meta, which needs no
    /// login. It is never asked about a profile the host has not named, and nothing is ever
    /// sent to it except the id in the address.
    /// <para>
    /// The whole trip, headers and body, is held to <see cref="Bound"/>. The check runs at
    /// start up, when the Mods hall opens and on Check now, and a service that is slow or down
    /// must cost the host one quiet line, not a stuck hall. A body larger than
    /// <see cref="MaxBodyBytes"/> is read as malformed rather than read to the end.
    /// </para>
    /// <para>
    /// Only the manifest's mods and the profile's updatedAt are read. The rest of the meta
    /// (and the config zip behind /profile/{id}) is step two and is not touched here.
    /// </para>
    /// </summary>
    public sealed class GaleSyncClient : IGaleSyncClient
    {
        /// <summary>The service's address. Nothing else is ever contacted.</summary>
        public const string BaseUrl = "https://gale.kesomannen.com/api";

        /// <summary>The service's host, for the tests that prove nothing else is asked.</summary>
        public const string Host = "gale.kesomannen.com";

        /// <summary>A profile's meta is a few kilobytes; a hundred times that is not a meta.</summary>
        public const int MaxBodyBytes = 2 * 1024 * 1024;

        public const string BadRequestSentence = "mods.gale.check.bad_request";
        public const string NotFoundSentence = "mods.gale.check.not_found";
        public const string TimeoutSentence = "mods.gale.check.timeout";
        public const string ServerErrorSentence = "mods.gale.check.server_error";
        public const string NetworkErrorSentence = "mods.gale.check.network";
        public const string MalformedSentence = "mods.gale.check.malformed";
        public const string CancelledSentence = "mods.gale.check.cancelled";

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

            return new GaleProfileMeta { Mods = lines, UpdatedAt = updatedAt };
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
