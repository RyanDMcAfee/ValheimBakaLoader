using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using ValheimBakaLoader.Game;
using ValheimBakaLoader.Tools;
using ValheimBakaLoader.Tools.Logging;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// The service bridge behind the "Blend" UI: the HTML/CSS/JS interface (WebUI/ folder,
    /// visual spec: design-mockups/blend.html) talks to BakaLoader through this and nothing
    /// else. It is not a window. Whatever shows the page, the WebView2 form on Windows or a
    /// host with no window at all, hands it messages through <see cref="HandlePageMessageAsync"/>
    /// and takes its pushes back through <see cref="IHostUi.PostJson"/>; everything a handler
    /// ever needed from a window goes through <see cref="Ui"/>.
    ///
    /// Message protocol (JSON over postMessage):
    ///   JS -> C# fire-and-forget:  { method: "win.minimize" }            (no id)
    ///   JS -> C# RPC:              { id: 1, method: "...", params: {} }
    ///   C# -> JS RPC reply:        { id: 1, ok: true, result: ... } | { id, ok: false, error }
    ///   C# -> JS event push:       { event: "...", data: ... }
    /// </summary>
    public partial class BlendWindow : IMainAppWindow, ISelfUpdateClosable
    {
        private readonly ILogger Logger;

        private readonly Dictionary<string, Func<JObject, Task<object>>> RpcHandlers = new();
        private bool IsFirstShown = true;

        /// <summary>
        /// The host this bridge is showing through. The Windows form sets itself here the
        /// moment it is made; until then, and on a box with no window, the headless host
        /// answers, so no handler ever has to ask whether there is a window.
        /// </summary>
        public IHostUi Ui { get; set; }

        #region IMainAppWindow (SplashForm startup contract)

        public int SplashIndex { get; set; }

        /// <summary>The server profile to load when the window is first shown.</summary>
        public string StartProfile { get; set; }

        /// <summary>If true, start the server from StartProfile as soon as the window is shown.</summary>
        public bool StartServerAutomatically { get; set; }

        #endregion

        internal const string VirtualHost = "app.baka";
        internal const string AtlasVirtualHost = "atlas.baka";

        /// <summary>
        /// The host an installed language pack is read from: a folder mapping of its own over
        /// the languages folder beside userprefs.
        /// <para>
        /// A pack's fonts have to have a URL, because <c>@font-face src</c> cannot read a local
        /// file. The obvious shape is to serve them off the page's own origin and answer the
        /// requests by hand, and that shape cannot work: a host registered with
        /// <c>SetVirtualHostNameToFolderMapping</c> is claimed whole by the folder mapper and
        /// never raises <c>WebResourceRequested</c>, so a filter on a path under
        /// <c>app.baka</c> is attached and never called once. That is what shipped in 1.2.0,
        /// and it is why every language answered "could not be downloaded" while the download
        /// and the install were working perfectly: the read-back was dead, not the pack.
        /// </para>
        /// <para>
        /// So the packs get a host of their own and the mapper serves them. That puts them on a
        /// second origin, which makes every pack fetch a cross-origin one, and a font fetch is
        /// always a CORS request: the Allow access kind is the only one that answers both the
        /// JSON and the fonts, measured in the engine rather than reasoned about. What the page
        /// can read is then the whole languages folder and nothing else, which is downloaded
        /// translation data; userprefs, the logs and the caches sit BESIDE that folder rather
        /// than inside it.
        /// </para>
        /// </summary>
        internal const string LangVirtualHost = "lang.baka";

        /// <summary>The address shape a pack file is asked for by: https://lang.baka/...</summary>
        internal const string LanguageUrlPrefix = "https://" + LangVirtualHost + "/";

        /// <summary>
        /// The WebView2 profile folder. Kept out of the install folder (which may be read-only)
        /// and under <see cref="AppDataRoot.LocalRoot"/>, so an isolated run has its own.
        /// </summary>
        internal static string WebViewUserDataFolder() => Path.Combine(
            AppDataRoot.LocalRoot, "ValheimBakaLoader", "WebView2");

        /// <summary>Writable cache folder for rendered Atlas map PNGs, served via the atlas.baka virtual host.</summary>
        internal static string GetAtlasCacheDir()
        {
            var dir = Path.Combine(
                AppDataRoot.LocalRoot,
                "ValheimBakaLoader", "AtlasCache");
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>
        /// Where installed language packs live: beside userprefs, not in the install folder,
        /// which may be read-only and which a manual re-extract does not carry anything across.
        /// </summary>
        internal static string GetLanguagesDir()
        {
            var dir = PathCheck.Resolve(Properties.Resources.LanguagesFolderPath);
            Directory.CreateDirectory(dir);
            return dir;
        }

        public BlendWindow(
            ILogger logger,
            IServiceProvider serviceProvider,
            IUserPreferencesProvider userPrefsProvider,
            IServerPreferencesProvider serverPrefsProvider,
            IWorldPreferencesProvider worldPrefsProvider,
            IPlayerDataRepository playerDataProvider,
            IIpAddressProvider ipAddressProvider,
            IModScanner modScanner,
            IThunderstoreClient thunderstoreClient,
            IModUpdateService modUpdateService,
            IModRemovalService modRemovalService,
            IRequiredModChecker requiredModChecker,
            Game.ItemCatalog itemCatalog,
            PlayerListService playerListService,
            IAppUpdateService appUpdateService,
            IHeartbeatService heartbeatService,
            IApplicationLogger appLogger)
        {
            Logger = logger;

            // A host before there is one: the wiring below already pushes (the Herald, the
            // heartbeat), and a push into nothing is dropped rather than thrown at.
            Ui = new HeadlessHostUi(logger);

            InitializeBridge(
                serviceProvider,
                userPrefsProvider,
                serverPrefsProvider,
                worldPrefsProvider,
                playerDataProvider,
                ipAddressProvider,
                modScanner,
                thunderstoreClient,
                modUpdateService,
                modRemovalService,
                requiredModChecker,
                itemCatalog,
                playerListService,
                appUpdateService,
                heartbeatService,
                appLogger);
        }

        /// <summary>
        /// The language the page should come up in, or null when the first frame is English
        /// anyway and there is nothing to say.
        /// <para>
        /// Three things have to hold together for a window to be worth holding back. The
        /// saved preference has to be a language this app knows, so an unreadable spelling
        /// left in userprefs.json cannot decide anything. It has to be a language other than
        /// English, because English is the markup in index.html and is already what the
        /// first frame paints. And a pack for it has to be on disk, because the words come
        /// out of a pack: naming a language with nothing installed would hide the window
        /// and then paint the English it was hiding.
        /// </para>
        /// <para>
        /// The pack is asked about through a delegate rather than read here, which is what
        /// makes this a decision rather than a lookup: the suite drives all four roads
        /// through it without a languages folder, and the short circuit above the call means
        /// an English window never asks the pack service anything at all.
        /// </para>
        /// </summary>
        /// <param name="savedLanguage">The language preference exactly as it was saved.</param>
        /// <param name="hasPack">Answers whether a pack is installed for a language.</param>
        internal static string LanguageToAnnounce(string savedLanguage, Func<string, bool> hasPack)
        {
            var normalized = LanguageCodes.Normalize(savedLanguage);
            if (normalized == null || LanguageCodes.IsEnglish(normalized)) return null;

            return hasPack != null && hasPack(normalized) ? normalized : null;
        }

        #region Host lifecycle

        /// <summary>
        /// The host is up and showing the page: orphaned-server adoption first, then the
        /// profile's auto-start. Once only, however many times a window is shown.
        /// </summary>
        public void OnHostShown()
        {
            if (!IsFirstShown) return;
            IsFirstShown = false;

            // Mirrors MainWindow.OnShown. Implemented in BlendWindow.Bridge.cs.
            OnBlendStartup();
        }

        #endregion

        #region Messaging

        /// <summary>
        /// One message from the page, exactly as the page posted it. Parsed, dispatched and
        /// answered here; a window message goes to the host, an RPC to its handler, and the
        /// reply goes back through <see cref="IHostUi.PostJson"/>.
        /// </summary>
        public async Task HandlePageMessageAsync(string json)
        {
            JObject message;
            try
            {
                message = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Blend UI sent an unparseable message");
                return;
            }

            var method = message.Value<string>("method");
            if (string.IsNullOrEmpty(method)) return;

            var id = message["id"]?.Value<long?>();
            var parameters = message["params"] as JObject ?? new JObject();

            // Window-chrome messages are fire-and-forget and must run on the UI thread.
            if (method.StartsWith("win.", StringComparison.Ordinal))
            {
                Ui.OnWindowMessage(method, parameters);
                return;
            }

            if (id == null)
            {
                Logger.Warning("Blend UI called {method} without an id - ignored", method);
                return;
            }

            if (!RpcHandlers.TryGetValue(method, out var handler))
            {
                PostReply(id.Value, ok: false, error: $"Unknown method: {method}");
                return;
            }

            // Counted here and nowhere else: one place, before the handler runs, so a command
            // that threw is still a command a host issued. Nothing but the method name, the
            // profile it was about, and for a console line its verb, ever reaches the tally,
            // and the tally itself ignores every method that is not one of the commands.
            try { CountCommand(method, parameters); }
            catch { /* a count is never worth a command */ }

            // A command for one realm writes its application lines as that realm's, so with two
            // realms running the Saga of the other one does not read them. The scope follows the
            // handler through its awaits and into the work it starts.
            using var realmScope = AppLog.ForRealm(RealmOfCall(method, parameters));

            try
            {
                var result = await handler(parameters);
                PostReply(id.Value, ok: true, result: result);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Blend UI RPC {method} failed", method);

                // The English sentence travels exactly as it always has, and an id travels
                // beside it when the throw wrote one. A page that reads only "error" is
                // unaffected; a page that knows the id can say it in another language.
                PostReply(
                    id.Value,
                    ok: false,
                    error: ex.Message,
                    errorId: HostFacingException.IdOf(ex),
                    errorParams: HostFacingException.ParamsOf(ex));
            }
        }

        /// <summary>
        /// Adds one host command to the anonymous tally, when the opt-out allows it and the
        /// method is one of the commands. The bridge partial owns the tally and the profile;
        /// this is the seam so the dispatcher above does not have to know about either.
        /// </summary>
        partial void CountCommand(string method, JObject parameters);

        /// <summary>Register an RPC method callable from the Blend UI.</summary>
        protected void RegisterRpc(string method, Func<JObject, Task<object>> handler)
        {
            RpcHandlers[method] = handler;
        }

        /// <summary>
        /// One answer to one RPC. <c>error</c> is the English sentence and stays what it has
        /// always been; <c>errorId</c> and <c>errorParams</c> are how that sentence can be
        /// said in another language, and are null for anything that did not name itself.
        /// </summary>
        private void PostReply(
            long id,
            bool ok,
            object result = null,
            string error = null,
            string errorId = null,
            object errorParams = null)
        {
            PostJson(new { id, ok, result, error, errorId, errorParams });
        }

        /// <summary>Push an event to the Blend UI (safe to call from any thread).</summary>
        public void PostEvent(string eventName, object data)
        {
            PostJson(new { @event = eventName, data });
        }

        private void PostJson(object payload)
        {
            // The host does the marshalling (the WebView's getter is UI-thread-only, and this
            // is called from server stdout, timers and RCON). Asked whether it is there BEFORE
            // anything is serialized, and asked through a local so a host swapped mid push is
            // one host or the other rather than half of each.
            var ui = Ui;
            if (ui == null || !ui.IsAlive) return;

            ui.PostJson(JsonConvert.SerializeObject(payload));
        }

        #endregion
    }
}
