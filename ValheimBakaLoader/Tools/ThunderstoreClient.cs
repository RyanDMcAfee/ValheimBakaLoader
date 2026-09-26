using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ValheimBakaLoader.Tools.Http;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// What a read of the package index came to: when it was read, which of the two
    /// addresses answered, how many packages came back, and whether this call went out
    /// to the site at all or reused a copy that had only just been read.
    /// </summary>
    public sealed class ThunderstoreIndexState
    {
        /// <summary>When the held index was read, or null when nothing has been read.</summary>
        public DateTime? FetchedUtc { get; init; }

        /// <summary>
        /// Which address answered: <see cref="ThunderstoreClient.ListingIndexSource"/>
        /// when the listing index and its chunks answered, and
        /// <see cref="ThunderstoreClient.V1Source"/> when the full listing stood in.
        /// Null when nothing has been read.
        /// </summary>
        public string Source { get; init; }

        /// <summary>How many packages the held index holds.</summary>
        public int PackageCount { get; init; }

        /// <summary>True when this call actually went out and read the index again.</summary>
        public bool Fetched { get; init; }

        /// <summary>
        /// True when the call was refused the trip because the index had only just been
        /// read. The answer the host sees is the one that was read moments ago, which is
        /// the point: a second press of Scan has nothing newer to find.
        /// </summary>
        public bool ReusedFresh { get; init; }
    }

    /// <summary>
    /// What asking Thunderstore about one package came to. "The site answered and has no
    /// such package" and "the site did not answer" are different things, and a host
    /// reading a row has to be told which one happened: the first is a package that has
    /// been pulled, the second is a bad minute on the internet.
    /// </summary>
    public sealed class ThunderstoreLiveLookup
    {
        public ThunderstorePackage Package { get; init; }

        /// <summary>True when Thunderstore answered at all, including with a plain no.</summary>
        public bool Answered { get; init; }

        public bool Found => Package != null;
    }

    public interface IThunderstoreClient
    {
        /// <summary>
        /// Looks up the latest published version of a Thunderstore package.
        /// Returns null when the package cannot be found (e.g. wrong namespace,
        /// not in the Valheim community) or the index is unreachable.
        /// </summary>
        Task<ThunderstorePackage> GetLatestAsync(string author, string modName);

        /// <summary>
        /// Asks Thunderstore about this one package directly, right now, rather than
        /// reading a held index. This is the answer the package's own page shows, so it
        /// is never behind. Returns null when the package is unknown or the site did not
        /// answer; never throws.
        /// </summary>
        Task<ThunderstorePackage> GetLiveAsync(string author, string modName);

        /// <summary>
        /// The same question with enough detail to tell a package that has been pulled
        /// from a site that did not answer. Never throws.
        /// </summary>
        Task<ThunderstoreLiveLookup> LookupLiveAsync(string author, string modName);

        /// <summary>
        /// Reads the index again even when the held copy is still inside its window.
        /// A second call within <see cref="ThunderstoreClient.ForceCooldown"/> is refused
        /// the trip and reuses what was just read, so pressing Scan twice does not knock
        /// on the site twice. Never throws.
        /// </summary>
        Task<ThunderstoreIndexState> RefreshAsync();

        /// <summary>When the held index was read, or null when nothing has been read yet.</summary>
        DateTime? IndexFetchedUtc { get; }

        /// <summary>Which of the two addresses the held index came from, or null.</summary>
        string IndexSource { get; }

        /// <summary>
        /// The last read that failed, or null when the last one worked. A scan reads this to
        /// tell a host WHY nothing came back, and the window in it is what keeps twenty-two
        /// mods from each paying for their own stalled trip to the site.
        /// </summary>
        ThunderstoreFailureMemo LastFailure { get; }
    }

    /// <summary>
    /// A read of the index that did not work: when, what kind of failure it was, and how
    /// long every caller is refused the trip for.
    /// <para>
    /// This exists because of one host's log. Their machine could not reach thunderstore.io
    /// through .NET at all, and a failed read cached nothing, so each of their twenty-two
    /// installed mods asked for the index in turn, waited out the listing-index timeout,
    /// waited out the full-listing timeout, and handed the lock to the next one. The scan
    /// took ninety minutes and finished with nothing. A failure has to be remembered for
    /// exactly the same reason a success is.
    /// </para>
    /// </summary>
    public sealed class ThunderstoreFailureMemo
    {
        /// <summary>When the read failed.</summary>
        public DateTime LastFailureUtc { get; init; }

        /// <summary>
        /// What kind of failure it was, as a short stable name rather than a sentence:
        /// timeout, connect, http, read, or the exception's own type when it is none of those.
        /// </summary>
        public string Reason { get; init; }

        /// <summary>How many reads in a row have failed. The backoff is chosen off this.</summary>
        public int Streak { get; init; }

        /// <summary>How long from the failure until another read is allowed.</summary>
        public TimeSpan BackOff { get; init; }

        /// <summary>The first moment a read is allowed again.</summary>
        public DateTime RetryAtUtc => LastFailureUtc + BackOff;
    }

    /// <summary>
    /// Read-only client for the Thunderstore Valheim package index. Used to check
    /// whether a newer version of a locally-installed mod exists. Does not download
    /// or install anything.
    ///
    /// <para>
    /// There are two addresses that answer with the whole community, and they are not
    /// equally fresh. The full listing (<see cref="V1IndexUrl"/>) is one enormous JSON
    /// array that Thunderstore builds on a timer and serves from a cache, so it can sit
    /// hours behind what a package's own page shows: a host watching a mod get three
    /// releases in an afternoon saw none of them. The listing index
    /// (<see cref="ListingIndexUrl"/>) hands back a short list of chunk addresses, each
    /// one named after the hash of its contents, and those are rebuilt every few minutes.
    /// A chunk therefore can never be stale, because a changed chunk is a different
    /// address. This reads the listing index first and keeps the full listing as the
    /// fallback for when it or any of its chunks does not answer.
    /// </para>
    ///
    /// <para>
    /// The parsed index is held for <see cref="CacheTtl"/> and guarded by a single-flight
    /// lock so concurrent scans share one read. It is held per instance, not statically:
    /// the app registers one of these as a singleton, and a test gets a clean one.
    /// <see cref="RefreshAsync"/> is what the Scan button uses, and it walks past the
    /// window on purpose.
    /// </para>
    /// </summary>
    public class ThunderstoreClient : RestClient, IThunderstoreClient
    {
        /// <summary>
        /// The community listing index: a short document naming the chunk files that
        /// together make up the package list. Redirects to a content-addressed blob.
        /// </summary>
        public const string ListingIndexUrl = "https://thunderstore.io/c/valheim/api/v1/package-listing-index/";

        /// <summary>
        /// The full Valheim-community listing: every package in one JSON array, newest
        /// version first within each package's versions[]. The fallback, because it is a
        /// snapshot that can be hours old.
        /// </summary>
        public const string V1IndexUrl = "https://valheim.thunderstore.io/api/v1/package/";

        /// <summary>The per-package address, which always answers with the current release.</summary>
        public const string PackageUrlFormat = "https://thunderstore.io/api/experimental/package/{0}/{1}/";

        /// <summary>Thunderstore's own registrable domain. Nothing else is ever contacted.</summary>
        public const string RootHost = "thunderstore.io";

        /// <summary>The name for the fresh path, as the interface shows it and tests assert it.</summary>
        public const string ListingIndexSource = "listing-index";

        /// <summary>The name for the fallback path.</summary>
        public const string V1Source = "v1";

        private const string UserAgentName = "ValheimBakaLoader";

        /// <summary>How long a read of the index stands for an ordinary background check.</summary>
        public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);

        /// <summary>
        /// The shortest gap between two deliberate refreshes. A host who presses Scan
        /// twice in a row gets one trip to the site, not two.
        /// </summary>
        public static readonly TimeSpan ForceCooldown = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How long a failed read stands before another one is allowed, by how many have
        /// failed in a row. The last step is the cap: a site that has been unreachable for an
        /// hour is asked once a quarter of an hour, not once a scan.
        /// </summary>
        public static readonly IReadOnlyList<TimeSpan> BackoffSteps = new[]
        {
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(15),
        };

        // --- Held index (per instance; the app holds one of these) ---
        private Dictionary<string, ThunderstorePackage> Index;
        private DateTime? FetchedUtc;
        private string Source;
        private readonly SemaphoreSlim IndexLock = new(1, 1);

        // --- The failure memo, and which kinds of failure have already been written down ---
        private ThunderstoreFailureMemo Failure;
        private readonly HashSet<string> Explained = new(StringComparer.OrdinalIgnoreCase);
        private int FailureNotes;
        private string PendingReason;

        public ThunderstoreClient(IRestClientContext context) : base(context)
        {
        }

        /// <summary>Test seam: the clock the window and the cooldown read.</summary>
        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        /// <summary>How many times this client has actually read the index. Read by tests.</summary>
        public int FetchCount { get; private set; }

        /// <summary>The most of the listing-index document itself that will be read.</summary>
        public long MaxDocumentBytes { get; set; } = 8L * 1024 * 1024;

        /// <summary>The most of one packed chunk that will be read off the wire.</summary>
        public long MaxChunkBytes { get; set; } = 64L * 1024 * 1024;

        /// <summary>The most one chunk is allowed to unpack to.</summary>
        public long MaxUnpackedBytes { get; set; } = 256L * 1024 * 1024;

        /// <summary>The most chunk addresses the listing index may name.</summary>
        public int MaxChunks { get; set; } = 256;

        // --- How long each stage may take. Settable because a test that has to prove a
        // stalled site costs seconds cannot wait out the real numbers to do it. ---

        /// <summary>How long the headers of any one request may take to arrive.</summary>
        public TimeSpan HeaderTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How long one whole document may take, headers and body together: the listing
        /// index itself, one chunk, or one package page.
        /// </summary>
        public TimeSpan DocumentTimeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>
        /// The longest the full listing may go without a byte arriving. It is a hundred and
        /// fifty megabytes, so it cannot be held to the document timeout, but a connection
        /// that has stopped delivering is not a slow one.
        /// </summary>
        public TimeSpan ListingIdleTimeout { get; set; } = TimeSpan.FromSeconds(20);

        /// <summary>The longest the full listing may take from the first byte to the last.</summary>
        public TimeSpan ListingTotalTimeout { get; set; } = TimeSpan.FromSeconds(120);

        /// <summary>
        /// The most one whole read of the index may cost, both paths together. Worst case is
        /// the listing index stalling out and the full listing stalling out after it, and
        /// this is the ceiling over the pair.
        /// </summary>
        public TimeSpan RefreshBudget { get; set; } = TimeSpan.FromMinutes(2);

        /// <summary>
        /// The longest a caller will wait for the one read slot before giving up on it.
        /// <para>
        /// A bare wait on the slot is how one stalled read became everybody's stall: the
        /// request its holder is stuck on carries its own deadlines, but every caller queued
        /// behind it carried none, so a single package lookup taken during a stalled index read
        /// waited the whole of that read out. It is held to one request's budget, because
        /// waiting for a turn should never cost more than taking one. A wait that runs out is
        /// a failed read like any other: it writes the memo, so the callers behind it are
        /// answered from the memo instead of queueing too.
        /// </para>
        /// </summary>
        public TimeSpan LockWait { get; set; } = TimeSpan.FromSeconds(20);

        public DateTime? IndexFetchedUtc => FetchedUtc;

        public string IndexSource => Source;

        public ThunderstoreFailureMemo LastFailure => Failure;

        /// <summary>
        /// True while the last failure's backoff is still running, which is the answer to
        /// "should this caller go out to the site at all".
        /// </summary>
        private bool InsideFailureWindow() =>
            Failure != null && UtcNow() < Failure.RetryAtUtc;

        public async Task<ThunderstorePackage> GetLatestAsync(string author, string modName)
        {
            if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(modName))
            {
                Logger.Information("Thunderstore lookup skipped: missing author or mod name.");
                return null;
            }

            var index = await EnsureIndexAsync();
            if (index == null)
            {
                // First read failed and nothing is held: the caller treats null as
                // "couldn't check" (no false update prompt).
                return null;
            }

            var key = $"{author}-{modName}";
            if (index.TryGetValue(key, out var package))
                return package;

            Logger.Information("Thunderstore package not in Valheim index: {0}-{1}", author, modName);
            return null;
        }

        public async Task<ThunderstorePackage> GetLiveAsync(string author, string modName) =>
            (await LookupLiveAsync(author, modName)).Package;

        public async Task<ThunderstoreLiveLookup> LookupLiveAsync(string author, string modName)
        {
            // Nothing to ask about, so nothing is asked and nobody answered.
            if (string.IsNullOrWhiteSpace(author) || string.IsNullOrWhiteSpace(modName))
                return new ThunderstoreLiveLookup { Answered = false };

            // The memo gates this one too, and leaving it out is what kept the ninety minutes
            // in place after the index path was fixed. The memo only ever stood in front of
            // EnsureIndexAsync, and Update all asks this per PACKAGE: twenty-two mods on a
            // machine that cannot reach the site is twenty-two page requests, each one waiting
            // out the document timeout, with the memo saying all along that the site is down.
            // The machine is unreachable or it is not; which URL is being asked for does not
            // change that.
            if (InsideFailureWindow())
                return new ThunderstoreLiveLookup { Answered = false };

            try
            {
                using var client = NewClient();

                var url = string.Format(PackageUrlFormat, author.Trim(), modName.Trim());
                if (!IsThunderstoreAddress(url))
                {
                    Logger.Warning("Refused a package address that is not Thunderstore's: {0}", url);
                    return new ThunderstoreLiveLookup { Answered = false };
                }

                using var deadline = Deadline(DocumentTimeout, CancellationToken.None);
                using var response = await client.GetAsync(url, deadline.Token);

                // A plain no is an answer, and the only one that means the package is not
                // there. Anything else is the site failing to speak, which says nothing
                // about the package at all.
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return new ThunderstoreLiveLookup { Answered = true };

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Information("Thunderstore did not answer about {0}-{1} (HTTP {2}).",
                        author, modName, (int)response.StatusCode);
                    return new ThunderstoreLiveLookup { Answered = false };
                }

                var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                var latest = body["latest"];
                var version = latest?.Value<string>("version_number");
                if (string.IsNullOrWhiteSpace(version))
                {
                    // It answered, with something this cannot read. Treating that as "the
                    // package is gone" would be a guess, so it is not treated as anything.
                    Logger.Warning("Thunderstore answered about {0}-{1} with no version in it.", author, modName);
                    return new ThunderstoreLiveLookup { Answered = false };
                }

                return new ThunderstoreLiveLookup
                {
                    Answered = true,
                    Package = new ThunderstorePackage
                    {
                        Namespace = body.Value<string>("namespace") ?? author,
                        Name = body.Value<string>("name") ?? modName,
                        Latest = latest.ToObject<ThunderstorePackageVersion>(),
                        // Left null when the answer did not carry the field at all, because
                        // "nobody said" and "it said no" are different answers and the
                        // BepInEx window only holds back on the second one.
                        IsDeprecated = body.Value<bool?>("is_deprecated"),
                    },
                };
            }
            catch (Exception e)
            {
                // Explained, never remembered: the memo and its backoff are about the INDEX,
                // and one package page that would not answer is not a reason to stop reading
                // the index for a quarter of an hour.
                ExplainFailure("live package lookup", e,
                    string.Format(PackageUrlFormat, author?.Trim(), modName?.Trim()),
                    remember: false);
                return new ThunderstoreLiveLookup { Answered = false };
            }
        }

        public async Task<ThunderstoreIndexState> RefreshAsync()
        {
            // Bounded like every other wait on this slot. A press of Scan while a stalled read
            // holds it used to hang the page for as long as that read took.
            if (!await IndexLock.WaitAsync(LockWait))
            {
                // "busy" and nothing else. This press never left the machine, so whatever
                // reason the read holding the lock has written down is that read's and not
                // this one's.
                RememberFailure("busy", ListingIndexUrl, takePendingReason: false);
                return State(fetched: false, reusedFresh: false);
            }

            try
            {
                // Only just read: there is nothing newer to find, and asking again would
                // only be a second knock on the door.
                if (Index != null && FetchedUtc is { } read && UtcNow() - read < ForceCooldown)
                    return State(fetched: false, reusedFresh: true);

                // A press of Scan is a host saying "try it now", so the backoff a run of
                // failures built up is put down. It is the one thing that does put it down:
                // everything else waits the window out.
                ClearFailure();

                var fresh = await FetchIndexAsync();
                return State(fetched: fresh, reusedFresh: false);
            }
            catch (Exception e)
            {
                ExplainFailure("refresh", e, ListingIndexUrl);
                RememberFailure("read", ListingIndexUrl);
                return State(fetched: false, reusedFresh: false);
            }
            finally
            {
                IndexLock.Release();
            }
        }

        private ThunderstoreIndexState State(bool fetched, bool reusedFresh) => new()
        {
            FetchedUtc = FetchedUtc,
            Source = Source,
            PackageCount = Index?.Count ?? 0,
            Fetched = fetched,
            ReusedFresh = reusedFresh,
        };

        /// <summary>
        /// The held index while it is inside its window, otherwise one read shared by
        /// every caller waiting on it. A failed read leaves whatever was held in place,
        /// so a blip does not wipe a scan's results.
        /// </summary>
        private async Task<Dictionary<string, ThunderstorePackage>> EnsureIndexAsync()
        {
            if (IsFresh()) return Index;

            // The last read failed and its window has not run out, so this one is answered
            // with whatever is held (usually nothing) and no request goes out. Asked BEFORE
            // the lock on purpose: the whole cost of the ninety-minute scan was twenty-two
            // callers queueing behind a lock to each make the same failing trip.
            if (InsideFailureWindow()) return Index;

            // Bounded, and that is the other half of the same lesson. Skipping the queue when a
            // memo is already written does nothing for the callers who arrive while the read
            // that will WRITE that memo is still stalling, and those are the ones that made a
            // scan take ninety minutes. A turn that does not come is a failed read: the memo is
            // written here too, so everyone behind is answered rather than queued.
            if (!await IndexLock.WaitAsync(LockWait))
            {
                // The same rule as the press above: a turn that did not come is a busy press,
                // never the reason belonging to the read that is still holding the lock.
                RememberFailure("busy", ListingIndexUrl, takePendingReason: false);
                return Index;
            }

            try
            {
                // Somebody else may have read it while this call waited, or failed while
                // this call waited, and both answers stand.
                if (IsFresh()) return Index;
                if (InsideFailureWindow()) return Index;

                await FetchIndexAsync();
                return Index;
            }
            finally
            {
                IndexLock.Release();
            }
        }

        private bool IsFresh() =>
            Index != null && FetchedUtc is { } read && UtcNow() - read < CacheTtl;

        // ------------------------------------------------------------ the failure memo

        /// <summary>A read worked, so the run of failures is over and so is the backoff.</summary>
        private void ClearFailure()
        {
            Failure = null;
            Explained.Clear();
            PendingReason = null;
        }

        /// <summary>
        /// Says in the log what went wrong, once per kind of failure per window. Once per
        /// kind, because the alternative on the host this was written for is twenty-two
        /// identical lines a scan, and remembers the reason for the memo the read writes when
        /// it gives up.
        /// <para>
        /// What goes in the line is the three things an exception message on its own does not
        /// carry: the exception TYPE, the INNERMOST message (an HttpRequestException's own
        /// text is usually "An error occurred while sending the request" and the reason is a
        /// SocketException two levels down), and what the Windows proxy settings make of the
        /// address. The reporter's curl worked and .NET stalled, and the proxy is the
        /// difference between those two that nothing in the log used to name.
        /// </para>
        /// </summary>
        private void ExplainFailure(string kind, Exception problem, string url,
            string reasonWhenNothingThrew = "read", bool remember = true)
        {
            FailureNotes++;
            // Only a read of the INDEX leaves a reason behind for the memo. A package page
            // that would not answer writes no memo of its own, so a reason left here by one
            // would be consumed by the next index failure and shown as ITS reason.
            if (remember) PendingReason = ReasonFor(problem, reasonWhenNothingThrew);

            if (!Explained.Add(kind)) return;

            Logger.Warning(problem,
                "Thunderstore {0} failed: {1} ({2}). The proxy for {3} resolves to {4}.",
                kind,
                problem?.GetType().Name ?? "no exception",
                Innermost(problem),
                url,
                HttpClientProvider.ProxyFor(url));
        }

        /// <summary>
        /// One read gave up, so one memo is written and one step of the backoff is spent. The
        /// two paths inside a read are two attempts at the same question, not two failures:
        /// counting them separately is how a host who pressed Scan once would land on the
        /// two-minute step before they had pressed it twice.
        /// </summary>
        private void RememberFailure(string fallbackReason, string url) =>
            RememberFailure(fallbackReason, url, takePendingReason: true);

        /// <summary>
        /// The same, for a memo whose reason is its own and not the read's.
        /// </summary>
        /// <param name="takePendingReason">
        /// False for a press that never asked the site anything.
        /// <para>
        /// The two busy sites are the only callers that write a memo WITHOUT having made a
        /// request: they gave up waiting for the lock. The read that holds that lock is on
        /// another thread and may already have logged an index-stage exception, which leaves
        /// its reason behind for the memo it is going to write; taking it here would hand a
        /// press that timed out on a queue inside BakaLoader the reason a different read had
        /// for failing on the network, and send a host looking at their router for it. So a
        /// busy press writes "busy", and it leaves the reason where it found it for the read
        /// that is still going to need it.
        /// </para>
        /// </param>
        private void RememberFailure(string fallbackReason, string url, bool takePendingReason)
        {
            // The name is what the page words its sentence from, so it is one of the closed
            // list or it is nothing a host is shown. A stage name is not a reason.
            var streak = (Failure?.Streak ?? 0) + 1;
            var step = BackoffSteps[Math.Min(streak - 1, BackoffSteps.Count - 1)];

            Failure = new ThunderstoreFailureMemo
            {
                LastFailureUtc = UtcNow(),
                Reason = (takePendingReason ? PendingReason : null) ?? fallbackReason,
                Streak = streak,
                BackOff = step,
            };

            if (takePendingReason) PendingReason = null;

            Logger.Information(
                "Thunderstore was not reachable ({0}), so nothing is asked of {1} for {2}.",
                Failure.Reason, url, Describe(step));
        }

        /// <summary>The innermost message, which is where the real reason usually is.</summary>
        private static string Innermost(Exception problem)
        {
            if (problem == null) return "no message";
            var walk = problem;
            while (walk.InnerException != null) walk = walk.InnerException;
            return walk.Message;
        }

        /// <summary>
        /// Every short name a failure is allowed to carry. The page keeps one sentence for
        /// each of them, so a reason travels as a NAME and is worded where the words live.
        /// A word that is not on this list never reaches a host: the slot it would land in
        /// sits inside a translated sentence, and an internal stage name there is neither a
        /// reason nor a language anybody reads.
        /// </summary>
        public static readonly string[] ReasonNames =
            { "timeout", "connect", "tls", "http", "read", "answered", "busy" };

        /// <summary>
        /// A memo's reason as one of those names, or "unknown". The page keeps one sentence
        /// for each of them and words it there; anything off the list is worded as the
        /// general one rather than travelling to a host as the word it is.
        /// </summary>
        public static string ReasonName(string reason) =>
            Array.IndexOf(ReasonNames, reason ?? "") >= 0 ? reason : "unknown";

        /// <summary>A short stable name for what went wrong, for the memo and the page.</summary>
        private static string ReasonFor(Exception problem, string fallback)
        {
            var walk = problem;
            while (walk != null)
            {
                switch (walk)
                {
                    case OperationCanceledException: return "timeout";
                    case System.Net.Sockets.SocketException: return "connect";
                    case System.Security.Authentication.AuthenticationException: return "tls";
                    case HttpRequestException: return "http";
                    case IOException: return "read";
                }

                walk = walk.InnerException;
            }

            // An exception none of those matched is still a read that went wrong, and its
            // type name is not a reason: it would land in a slot inside a sentence the
            // packs translate, in English, saying nothing to the host reading it. With no
            // exception at all the caller says which of the names fits.
            return problem == null ? fallback : "read";
        }

        private static string Describe(TimeSpan span) =>
            span < TimeSpan.FromMinutes(1)
                ? ((int)span.TotalSeconds) + " seconds"
                : ((int)span.TotalMinutes) + " minutes";

        // ------------------------------------------------------------ the stage timeouts

        /// <summary>
        /// A token that gives up after <paramref name="after"/>, linked to whatever budget the
        /// caller is already working inside, so the tighter of the two wins.
        /// </summary>
        private static CancellationTokenSource Deadline(TimeSpan after, CancellationToken outer)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(outer);
            source.CancelAfter(after);
            return source;
        }

        /// <summary>
        /// Reads the index: the listing index first, the full listing when that path does
        /// not answer in full. Records what was read and where it came from. Returns true
        /// when something was read; on failure the previously held index stands.
        /// </summary>
        private async Task<bool> FetchIndexAsync()
        {
            FetchCount++;

            // The ceiling over the whole read. Every stage below is bounded on its own as
            // well; this is the one that holds when the stages are made to run one after
            // another and their sum is what a host is waiting out.
            using var budget = new CancellationTokenSource(RefreshBudget);
            var notesBefore = FailureNotes;

            var (fresh, source) = await FetchViaListingIndexAsync(budget.Token);
            if (fresh == null)
            {
                Logger.Information("Thunderstore listing index did not answer in full; reading the full listing instead.");
                fresh = await FetchViaV1Async(budget.Token);
                source = V1Source;
            }

            if (fresh == null)
            {
                // Nothing came back, and this is what stops the next twenty-one callers
                // paying the same price. A stage that threw has written its own memo; this
                // covers the stages that answer with a plain null (an HTTP status, a body
                // that is not a package list), because a window has to open either way.
                // Nothing threw: the site was reached and what came back was not a package
                // list, which is what a captive portal and a 503 both look like from here.
                // "Answered" is the truthful name for that, and "index read" was the name of
                // the STAGE, which is how a host came to read "could not reach Thunderstore
                // (index read)" about a site that had answered them.
                if (FailureNotes == notesBefore)
                    ExplainFailure("index read", null, ListingIndexUrl, "answered");
                RememberFailure("answered", ListingIndexUrl);
                return false;
            }

            Index = fresh;
            FetchedUtc = UtcNow();
            Source = source;
            ClearFailure();
            return true;
        }

        /// <summary>
        /// Reads the listing index and every chunk it names. One chunk that does not
        /// answer fails the whole path, because half a package list would read as though
        /// mods had been delisted. Returns null when that happens.
        /// </summary>
        private async Task<(Dictionary<string, ThunderstorePackage> Index, string Source)> FetchViaListingIndexAsync(
            CancellationToken budget)
        {
            try
            {
                using var client = NewClient();

                var document = await GetBytesAsync(client, ListingIndexUrl, MaxDocumentBytes, budget);
                if (document == null) return (null, null);

                var chunkUrls = ReadChunkList(Unpack(document, MaxUnpackedBytes));
                if (chunkUrls == null || chunkUrls.Count == 0)
                {
                    Logger.Warning("Thunderstore listing index named no chunks.");
                    return (null, null);
                }

                if (chunkUrls.Count > MaxChunks)
                {
                    Logger.Warning("Thunderstore listing index named {0} chunks, which is more than the {1} allowed.",
                        chunkUrls.Count, MaxChunks);
                    return (null, null);
                }

                var map = new Dictionary<string, ThunderstorePackage>(StringComparer.OrdinalIgnoreCase);

                foreach (var chunkUrl in chunkUrls)
                {
                    // Every address is checked before it is fetched, so a listing index
                    // that named somewhere else could not send BakaLoader there.
                    if (!IsThunderstoreAddress(chunkUrl))
                    {
                        Logger.Warning("Refused a listing-index chunk that is not on Thunderstore: {0}", chunkUrl);
                        return (null, null);
                    }

                    var packed = await GetBytesAsync(client, chunkUrl, MaxChunkBytes, budget);
                    if (packed == null) return (null, null);

                    if (!ReadPackagesInto(map, Unpack(packed, MaxUnpackedBytes)))
                        return (null, null);
                }

                Logger.Information("Thunderstore listing index read: {0} packages over {1} chunks.",
                    map.Count, chunkUrls.Count);
                return (map, ListingIndexSource);
            }
            catch (Exception e)
            {
                ExplainFailure("listing index", e, ListingIndexUrl);
                return (null, null);
            }
        }

        /// <summary>
        /// Streams the full listing and builds a lookup keyed by "{owner}-{name}"
        /// (case-insensitive). Only versions[0] (the latest) is kept per package.
        /// Returns null on any transport/HTTP/parse failure.
        /// </summary>
        private async Task<Dictionary<string, ThunderstorePackage>> FetchViaV1Async(CancellationToken budget)
        {
            // Three lines at most, all at Verbose: the read opening, the watchdog if it ever
            // trips, and what the whole body came to. The full listing is the one read in the
            // app that can take minutes and be a hundred and fifty megabytes, and a host
            // chasing a stall needs to know whether the bytes were arriving at all.
            var clock = Stopwatch.StartNew();
            // Asked BEFORE the sentence is built. Say() asks it again and would drop the
            // line either way, but the address and the two joins in front of it are work
            // this read pays for on every scan of every install whose log is at Debug.
            if (WireTrace.Wanted(Logger))
                WireTrace.Say(Logger, "Thunderstore listing read started: " + ListingAddress());

            try
            {
                using var client = NewClient();

                using var headers = Deadline(HeaderTimeout, budget);
                using var response = await client.GetAsync(
                    V1IndexUrl, HttpCompletionOption.ResponseHeadersRead, headers.Token);
                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning("Thunderstore full listing fetch failed: HTTP {0}", (int)response.StatusCode);
                    return null;
                }

                // The body is the whole community and can be a hundred and fifty megabytes, so
                // it gets two clocks rather than one: a total, and a watchdog that gives up when
                // nothing has arrived for a while. A connection that has stopped delivering is
                // not a slow one, and it is the shape this host was stuck in.
                using var whole = Deadline(ListingTotalTimeout, budget);
                using var stream = await response.Content.ReadAsStreamAsync(whole.Token);
                await using var watched = new IdleWatchdogStream(stream, ListingIdleTimeout, whole.Token, Logger);
                using var streamReader = new StreamReader(watched);
                using var jsonReader = new JsonTextReader(streamReader);

                var map = new Dictionary<string, ThunderstorePackage>(StringComparer.OrdinalIgnoreCase);

                // Iterate the top-level array, deserializing one package object at a time
                // so the whole 150MB+ payload is never held in memory.
                if (!ReadPackagesInto(map, jsonReader)) return null;

                if (WireTrace.Wanted(Logger))
                    WireTrace.Say(Logger, "Thunderstore listing read finished: "
                        + WireTrace.Count(watched.BytesRead) + " bytes in "
                        + WireTrace.Count(clock.ElapsedMilliseconds) + " ms");
                Logger.Information("Thunderstore full listing loaded: {0} packages.", map.Count);
                return map;
            }
            catch (Exception e)
            {
                ExplainFailure("full listing", e, V1IndexUrl);
                return null;
            }
        }

        /// <summary>
        /// The full listing's address as the trace writes every address: scheme, host and
        /// path, and nothing that could carry a term a host did not choose to publish.
        /// </summary>
        private static string ListingAddress() =>
            Uri.TryCreate(V1IndexUrl, UriKind.Absolute, out var uri)
                ? WireTrace.Address(uri)
                : V1IndexUrl;

        private HttpClient NewClient()
        {
            var client = Context.HttpClientProvider.CreateClient();
            // Every stage below carries its own deadline, and HttpClient.Timeout is one clock
            // over the whole call that cannot tell a slow connect from a slow download. One
            // hundred and twenty seconds of it, per request, per mod, is the ninety minutes.
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgentName);
            return client;
        }

        /// <summary>
        /// A read-only wrapper that fails the read when nothing has arrived for a while. Each
        /// Read is given its own deadline, so a stream that keeps delivering never trips it
        /// however long the whole download takes, and one that stops delivering is over in
        /// <see cref="ListingIdleTimeout"/> instead of in two minutes.
        /// </summary>
        private sealed class IdleWatchdogStream : Stream
        {
            private readonly Stream Inner;
            private readonly TimeSpan Idle;
            private readonly CancellationToken Outer;
            private readonly Serilog.ILogger Tracer;
            private bool Tripped;

            public IdleWatchdogStream(
                Stream inner, TimeSpan idle, CancellationToken outer, Serilog.ILogger tracer = null)
            {
                Inner = inner;
                Idle = idle;
                Outer = outer;
                Tracer = tracer;
            }

            /// <summary>How many bytes of the body have arrived so far.</summary>
            public long BytesRead { get; private set; }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            // StreamReader over a synchronous read path, which is what JsonTextReader drives.
            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

            public override async Task<int> ReadAsync(
                byte[] buffer, int offset, int count, CancellationToken token)
            {
                using var deadline = Deadline(Idle, Outer);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token);
                try
                {
                    var read = await Inner.ReadAsync(buffer.AsMemory(offset, count), linked.Token).ConfigureAwait(false);
                    BytesRead += read;
                    return read;
                }
                catch (OperationCanceledException) when (deadline.Token.IsCancellationRequested)
                {
                    Trip();
                    throw;
                }
            }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer, CancellationToken token = default)
            {
                using var deadline = Deadline(Idle, Outer);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, token);
                try
                {
                    var read = await Inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                    BytesRead += read;
                    return read;
                }
                catch (OperationCanceledException) when (deadline.Token.IsCancellationRequested)
                {
                    Trip();
                    throw;
                }
            }

            /// <summary>
            /// The one line the watchdog writes, and it writes it once. A stream that has
            /// stopped delivering is the shape the stalled host was stuck in, and how many
            /// bytes had arrived before it stopped is what tells a slow site apart from a
            /// connection that died in the middle of the body.
            /// </summary>
            private void Trip()
            {
                if (Tripped) return;
                Tripped = true;
                WireTrace.Say(Tracer, "Thunderstore listing stalled: nothing arrived for "
                    + WireTrace.Count((long)Idle.TotalSeconds) + " s after "
                    + WireTrace.Count(BytesRead) + " bytes");
            }

            protected override void Dispose(bool disposing)
            {
                // The response body is owned by the caller's using, not by this.
                base.Dispose(disposing);
            }
        }

        /// <summary>
        /// The body at an address, capped, with redirects followed.
        /// <para>
        /// The control that holds is the landed-host check below: whatever the response
        /// came from, its address has to be Thunderstore's or the body is dropped unread.
        /// That is what runs in the app, where the transport follows the redirect itself
        /// and this only ever sees the 200 the blob answered with. The hand-rolled hop
        /// below is for a transport that hands the 3xx back instead, and there the address
        /// is also checked before the next request goes out. Either way nothing is sent
        /// anywhere but Thunderstore, and no credentials are sent at all.
        /// </para>
        /// </summary>
        private async Task<byte[]> GetBytesAsync(
            HttpClient client, string url, long cap, CancellationToken budget, int maxHops = 4)
        {
            var current = url;

            // One clock over the whole document, headers and body, and a tighter one inside
            // it for the headers alone: a site that answers and then stops sending and a site
            // that never answers at all are both over in seconds rather than in two minutes.
            using var document = Deadline(DocumentTimeout, budget);

            for (var hop = 0; hop <= maxHops; hop++)
            {
                if (!IsThunderstoreAddress(current))
                {
                    Logger.Warning("Refused an address that is not on Thunderstore: {0}", current);
                    return null;
                }

                using var headers = Deadline(HeaderTimeout, document.Token);
                using var response = await client.GetAsync(
                    current, HttpCompletionOption.ResponseHeadersRead, headers.Token);

                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400)
                {
                    var location = response.Headers.Location;
                    if (location == null)
                    {
                        Logger.Warning("Thunderstore answered {0} with nowhere to go: {1}", status, current);
                        return null;
                    }

                    current = (location.IsAbsoluteUri ? location : new Uri(new Uri(current), location)).ToString();
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    Logger.Warning("Thunderstore fetch failed: HTTP {0} for {1}", status, current);
                    return null;
                }

                // Where the request actually landed. This is the check that holds in the
                // app, where the transport follows the redirect itself and the body
                // arrives from the blob address without this code seeing the 3xx at all.
                var landed = response.RequestMessage?.RequestUri?.ToString();
                if (!string.IsNullOrWhiteSpace(landed) && !IsThunderstoreAddress(landed))
                {
                    Logger.Warning("A Thunderstore fetch landed somewhere else and was dropped: {0}", landed);
                    return null;
                }

                return await ReadCappedAsync(response, cap, document.Token);
            }

            Logger.Warning("Thunderstore redirected more times than allowed for {0}", url);
            return null;
        }

        private static async Task<byte[]> ReadCappedAsync(
            HttpResponseMessage response, long cap, CancellationToken token)
        {
            using var buffer = new MemoryStream();
            await using var raw = await response.Content.ReadAsStreamAsync(token);

            var chunk = new byte[81920];
            int read;
            while ((read = await raw.ReadAsync(chunk.AsMemory(0, chunk.Length), token)) > 0)
            {
                if (buffer.Length + read > cap)
                    throw new IOException($"A Thunderstore response passed {cap} bytes, so it was not read.");

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }

        /// <summary>
        /// The bytes as they are meant to be read. The listing index and its chunks come
        /// down as raw gzip with no header saying so, so the two bytes at the front are
        /// what decide rather than anything the server claimed.
        /// </summary>
        private static byte[] Unpack(byte[] raw, long cap)
        {
            if (raw == null || raw.Length < 2) return raw;
            if (raw[0] != 0x1f || raw[1] != 0x8b) return raw;

            using var source = new MemoryStream(raw);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var unpacked = new MemoryStream();

            var chunk = new byte[81920];
            int read;
            while ((read = gzip.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (unpacked.Length + read > cap)
                    throw new IOException($"A Thunderstore chunk unpacked past {cap} bytes, so it was not read.");

                unpacked.Write(chunk, 0, read);
            }

            return unpacked.ToArray();
        }

        /// <summary>The chunk addresses the listing-index document names, or null when it is not that.</summary>
        private List<string> ReadChunkList(byte[] json)
        {
            if (json == null) return null;

            try
            {
                return JsonConvert.DeserializeObject<List<string>>(
                    System.Text.Encoding.UTF8.GetString(json));
            }
            catch (Exception e)
            {
                Logger.Warning(e, "Thunderstore listing index could not be read as a list of chunks.");
                return null;
            }
        }

        private bool ReadPackagesInto(Dictionary<string, ThunderstorePackage> map, byte[] json)
        {
            if (json == null) return false;

            try
            {
                using var stream = new MemoryStream(json);
                using var streamReader = new StreamReader(stream);
                using var jsonReader = new JsonTextReader(streamReader);
                return ReadPackagesInto(map, jsonReader);
            }
            catch (Exception e)
            {
                Logger.Warning(e, "A Thunderstore chunk could not be read.");
                return false;
            }
        }

        /// <summary>
        /// Reads one array of package objects into the lookup. The chunks and the full
        /// listing carry the same shape, so both paths land here and everything
        /// downstream sees the same model whichever one answered. Merging is by exact
        /// full name; a later entry for a name already read wins, which is what makes
        /// reading chunk after chunk into one map safe.
        /// <para>
        /// Each body is judged on its own contents and nothing else. The lookup is shared
        /// across chunks, so a verdict that read it would let the first chunk vouch for
        /// every chunk after it, and a chunk that came back holding an error document or
        /// an empty list would pass as read. That is how a thirteenth of the community
        /// would go missing from a list this then published as complete, with every mod in
        /// it marked as no longer on Thunderstore. So: it has to be an array of packages,
        /// and it has to have held at least one.
        /// </para>
        /// </summary>
        private bool ReadPackagesInto(Dictionary<string, ThunderstorePackage> map, JsonTextReader jsonReader)
        {
            var serializer = new JsonSerializer();
            var packagesRead = 0;

            // A 200 is not an answer on its own. An error document is an object, and a
            // list of packages is an array, and only the second one is a package list.
            if (!jsonReader.Read() || jsonReader.TokenType != JsonToken.StartArray)
            {
                Logger.Warning("A Thunderstore package list did not begin with an array of packages.");
                return false;
            }

            while (jsonReader.Read())
            {
                // Only the objects sitting directly inside the top-level array are
                // packages; anything nested is read by the deserializer itself.
                if (jsonReader.TokenType != JsonToken.StartObject) continue;

                var pkg = serializer.Deserialize<V1Package>(jsonReader);
                if (pkg?.Versions == null || pkg.Versions.Count == 0) continue;
                if (string.IsNullOrEmpty(pkg.Owner) || string.IsNullOrEmpty(pkg.Name)) continue;

                map[$"{pkg.Owner}-{pkg.Name}"] = new ThunderstorePackage
                {
                    Namespace = pkg.Owner,
                    Name = pkg.Name,
                    Latest = pkg.Versions[0],
                    IsDeprecated = pkg.IsDeprecated,
                };

                packagesRead++;
            }

            // What THIS body held, never what the lookup holds: the lookup already has
            // the chunks read before this one in it. Counting packages rather than objects
            // also keeps an array of something else from passing for a package list.
            if (packagesRead == 0)
                Logger.Warning("A Thunderstore package list came back with no packages in it.");

            return packagesRead > 0;
        }

        /// <summary>
        /// True when an address is one of Thunderstore's: https, and either the domain
        /// itself or a name under it at a label boundary, so "gcdn.thunderstore.io" is
        /// allowed while "thunderstore.io.example.com" and "notthunderstore.io" are not.
        /// </summary>
        public static bool IsThunderstoreAddress(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;

            return IsThunderstoreHost(uri.Host);
        }

        /// <summary>True when a host name belongs to Thunderstore at a label boundary.</summary>
        public static bool IsThunderstoreHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;

            var trimmed = host.Trim().TrimEnd('.').ToLowerInvariant();
            return trimmed == RootHost || trimmed.EndsWith("." + RootHost, StringComparison.Ordinal);
        }

        /// <summary>
        /// Minimal projection of an index entry. Both the chunks and the full listing
        /// carry this shape, and it reuses <see cref="ThunderstorePackageVersion"/> for
        /// the versions array (version_number / download_url / website_url / full_name).
        /// </summary>
        private class V1Package
        {
            [JsonProperty("full_name")]
            public string FullName { get; set; }

            [JsonProperty("owner")]
            public string Owner { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("versions")]
            public List<ThunderstorePackageVersion> Versions { get; set; }

            /// <summary>
            /// Whether the community listing marks this package deprecated. The listing is
            /// also where a per-version file_size comes from, which is what the BepInEx
            /// installer reads this whole entry for when the package page leaves it out.
            /// </summary>
            [JsonProperty("is_deprecated")]
            public bool? IsDeprecated { get; set; }
        }
    }
}
