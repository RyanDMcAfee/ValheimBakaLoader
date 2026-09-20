using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ValheimBakaLoader.Forms;
using Xunit;
using Xunit.Abstractions;

namespace ValheimBakaLoader.Tests.Forms
{
    /// <summary>
    /// THE TEST THAT WOULD HAVE CAUGHT IT. A real WebView2, the app's real startup order, and
    /// the question asked from the PAGE: can it read a language pack.
    /// <para>
    /// 1.2.0 served packs off the page's own origin and answered the requests by hand, and every
    /// test it had passed: they drove the path mapper as a pure function and asserted the source
    /// text of the window. None of them started an engine, so none of them could notice that a
    /// host registered with SetVirtualHostNameToFolderMapping never raises WebResourceRequested
    /// and the handler was never called once. The install worked, the read-back was dead, and
    /// every language on the shipped build answered "could not be downloaded".
    /// </para>
    /// <para>
    /// So this asserts what the page RECEIVED and never that a handler ran. A design that answers
    /// 200 without the header a cross-origin font needs would pass the second kind of assertion
    /// and fail a host; it fails this one. Three runs, because the two neighbouring traps are the
    /// order a fresh install really takes: the folder is mapped while it is still empty and the
    /// pack lands afterwards, a pack is already there, and a pack is replaced underneath a
    /// mapping that is already live.
    /// </para>
    /// <para>
    /// The same three runs carry the other half of the question: what the page may NOT read. The
    /// mapping hands it a whole folder, and that folder sits beside userprefs, the logs and the
    /// caches, so every run tries the neighbours from the page and has to be refused. Three
    /// spellings, because the one that matters is whichever the engine does not flatten: a plain
    /// <c>../</c>, the percent encoded <c>%2e%2e/</c>, and a folder planted beside the languages
    /// folder. Each run prints the address the URL parser resolved as well as the one it was
    /// asked with, so an engine that one day stops flattening one of them is visible in the
    /// output rather than passing quietly. The catalog is asserted to still read in the very same
    /// run, because a mapping that answered nothing at all would satisfy the refusals on its own.
    /// </para>
    /// <para>
    /// With no WebView2 runtime on the machine it passes having exercised nothing, and SAYS so in
    /// its output rather than reading as a route that was proved. It never launches BakaLoader,
    /// it never touches the real languages folder, and every run gets its own user data folder
    /// under the temp tree. It does not go looking for browser processes to end, either: the
    /// engine's children are ended by disposing the view that owns them, and hunting them down by
    /// name is how a test reaches out of its own sandbox and closes the window of an app that is
    /// running beside it.
    /// </para>
    /// </summary>
    public class LanguageRouteRealEngineTests
    {
        private readonly ITestOutputHelper _output;

        public LanguageRouteRealEngineTests(ITestOutputHelper output) => _output = output;

        /// <summary>The two constants the window builds a pack address from, copied nowhere.</summary>
        private static string StringsUrl(string code, string version) =>
            BlendWindow.LanguageUrlPrefix + code + "/" + version + "/strings.json";

        private static string FontUrl(string stored) => BlendWindow.LanguageUrlPrefix + stored;

        private const string Code = "ja";
        private const string Version = "1.2.1";
        private const string FontStored = "_fonts/deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef.woff2";

        /// <summary>
        /// What the page is not allowed to read, asked for from the page in the same run as the
        /// catalog. The first two are one neighbour spelled two ways, because an engine that
        /// stopped decoding <c>%2e</c> would hand the mapper a path segment of its own rather
        /// than a step up; the third is a folder of its own beside the mapped one.
        /// <para>
        /// What the output shows is that neither spelling ever reaches the mapper as a step up:
        /// the URL parser flattens both before the request leaves the page, so all three are
        /// asked for as <c>https://lang.baka/userprefs.json</c> and
        /// <c>https://lang.baka/beside/secret.json</c>, which are addresses INSIDE the mapped
        /// folder, and nothing is there under those names. So what this pins is the outcome
        /// rather than the mechanism: neither a file beside the mapped folder nor a folder
        /// beside it is readable under any address the page can form, while the catalog inside
        /// it reads in the very same run. Each run prints the resolved address as well as the
        /// one it was asked with, so the day an engine stops flattening one of them the change
        /// is visible in the output instead of passing quietly.
        /// </para>
        /// </summary>
        private static readonly string[] EscapeUrls =
        {
            BlendWindow.LanguageUrlPrefix + "../userprefs.json",
            BlendWindow.LanguageUrlPrefix + "%2e%2e/userprefs.json",
            BlendWindow.LanguageUrlPrefix + "../beside/secret.json",
        };

        /// <summary>The file names the two planted neighbours are written under.</summary>
        private const string NeighbourFile = "userprefs.json";
        private const string SiblingFolder = "beside";
        private const string SiblingFile = "secret.json";

        /// <summary>
        /// The order a fresh install really takes: nothing is downloaded yet when the window comes
        /// up, so the folder is mapped while it is empty and the pack arrives afterwards. A route
        /// that only works when the files were there first would look perfect on this machine and
        /// fail every new host.
        /// </summary>
        [Fact]
        public void A_pack_that_lands_after_the_mapping_is_read_by_the_page()
        {
            var run = Drive(writePackBeforeMapping: false, replaceAfterNavigation: false);
            if (run == null) return;

            Assert.True(run.StringsOk, "the page could not fetch the catalog: " + run.StringsNote);
            Assert.True(run.CatalogKeys > 0, "the catalog did not parse as JSON: " + run.StringsNote);
            Assert.Equal("loaded", run.FontStatus);
            NothingEscaped(run);
        }

        /// <summary>The ordinary case, with the pack already on disk when the mapping is made.</summary>
        [Fact]
        public void A_pack_already_on_disk_is_read_by_the_page()
        {
            var run = Drive(writePackBeforeMapping: true, replaceAfterNavigation: false);
            if (run == null) return;

            Assert.True(run.StringsOk, "the page could not fetch the catalog: " + run.StringsNote);
            Assert.True(run.CatalogKeys > 0, "the catalog did not parse as JSON: " + run.StringsNote);
            Assert.Equal("loaded", run.FontStatus);
            NothingEscaped(run);
        }

        /// <summary>
        /// And a pack replaced underneath a live mapping. An update writes a new version folder
        /// while the window is up, so what the page reads has to be what is on disk now rather
        /// than what was there when the host was mapped.
        /// </summary>
        [Fact]
        public void A_pack_replaced_underneath_the_mapping_is_read_as_it_is_now()
        {
            var run = Drive(writePackBeforeMapping: true, replaceAfterNavigation: true);
            if (run == null) return;

            Assert.True(run.StringsOk, "the page could not fetch the catalog: " + run.StringsNote);
            Assert.Equal(2, run.CatalogKeys);
            Assert.Equal("replaced", run.CatalogMark);
            Assert.Equal("loaded", run.FontStatus);
            NothingEscaped(run);
        }

        /// <summary>
        /// The neighbours, from the page, in the run the catalog was read in. Every one of them
        /// has to be refused and the catalog has to still be readable, because a mapping that
        /// answers nothing would satisfy the first half on its own.
        /// </summary>
        private static void NothingEscaped(PageReport run)
        {
            Assert.True(run.StringsOk, "the catalog was not readable in the run the escapes were tried in");
            Assert.Equal(EscapeUrls.Length, run.Escapes.Count);

            foreach (var escape in run.Escapes)
                Assert.False(escape.Ok,
                    "the page read " + escape.Url + ", asked for as " + escape.Asked + " (" + escape.Note + ")");
        }

        // ------------------------------------------------------------------ the engine

        private sealed class PageReport
        {
            public bool StringsOk;
            public int CatalogKeys;
            public string CatalogMark = "";
            public string StringsNote = "";
            public string FontStatus = "";
            public readonly List<Escape> Escapes = new List<Escape>();
        }

        /// <summary>One address the page was refused, as the page saw it.</summary>
        private sealed class Escape
        {
            public string Url = "";
            public string Asked = "";
            public bool Ok;
            public string Note = "";
        }

        /// <summary>
        /// Null when there is no runtime to drive, which the caller reads as "nothing was
        /// exercised" and the output says out loud.
        /// </summary>
        private PageReport Drive(bool writePackBeforeMapping, bool replaceAfterNavigation)
        {
            SweepOldRuns();

            if (!RuntimeIsHere(out var why))
            {
                _output.WriteLine(
                    "SKIPPED, NOT PASSED: no WebView2 runtime on this agent, the language route was not exercised (" + why + ")");
                return null;
            }

            var root = Path.Combine(Path.GetTempPath(), "baka-langroute-" + Guid.NewGuid().ToString("N"));
            var webUi = Path.Combine(root, "WebUI");
            var languages = Path.Combine(root, "languages");
            var userData = Path.Combine(root, "wv2");

            try
            {
                Directory.CreateDirectory(webUi);
                Directory.CreateDirectory(languages);
                Directory.CreateDirectory(userData);
                File.WriteAllText(Path.Combine(webUi, "index.html"), PageHtml(), new UTF8Encoding(false));
                PlantNeighbours(root);

                if (writePackBeforeMapping) WritePack(languages, "as-installed", 1);

                var report = RunOnItsOwnThread(webUi, languages, userData, () =>
                {
                    if (!writePackBeforeMapping) WritePack(languages, "as-installed", 1);
                    if (replaceAfterNavigation) WritePack(languages, "replaced", 2);
                });

                _output.WriteLine(
                    "catalog ok=" + report.StringsOk + " keys=" + report.CatalogKeys +
                    " mark=" + report.CatalogMark + " font=" + report.FontStatus +
                    (report.StringsNote.Length > 0 ? " note=" + report.StringsNote : ""));

                foreach (var escape in report.Escapes)
                    _output.WriteLine(
                        "escape " + escape.Url + " asked=" + escape.Asked +
                        " read=" + escape.Ok + " " + escape.Note);

                return report;
            }
            finally
            {
                TryDelete(root);
            }
        }

        /// <summary>
        /// Trees left behind by earlier runs, cleared before this one starts.
        /// <para>
        /// The delete at the end of a run usually cannot happen: the engine is still letting go
        /// of its user data folder when the run finishes, the recursive delete throws, and the
        /// finally swallows it because a temp folder that will not go is not a reason to fail a
        /// test about a language route. So every run leaves a baka-langroute-* folder in the
        /// temp tree, and they pile up. By the next run the engine that held one is long gone
        /// and it deletes without complaint, which is why the sweep belongs at the START.
        /// </para>
        /// <para>
        /// An hour is the floor, and it is read off whichever of the two stamps is later, so a
        /// run happening in another process right now is never reached into: three facts of this
        /// file bound how long a run can take, the sixty second cap inside the window and the
        /// ninety second wait outside it, and an hour is well past both. Nothing here throws:
        /// the sweep is housekeeping, and housekeeping never decides whether a test passed.
        /// </para>
        /// </summary>
        private static void SweepOldRuns()
        {
            try
            {
                var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
                foreach (var folder in Directory.EnumerateDirectories(Path.GetTempPath(), "baka-langroute-*"))
                {
                    try
                    {
                        var made = Directory.GetCreationTimeUtc(folder);
                        var touched = Directory.GetLastWriteTimeUtc(folder);
                        if ((touched > made ? touched : made) > cutoff) continue;
                        Directory.Delete(folder, recursive: true);
                    }
                    catch (Exception) { /* still held, or gone since the listing; both are fine */ }
                }
            }
            catch (Exception) { /* the temp folder could not even be listed */ }
        }

        private static bool RuntimeIsHere(out string why)
        {
            try
            {
                var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                why = version ?? "GetAvailableBrowserVersionString returned null";
                return !string.IsNullOrWhiteSpace(version);
            }
            catch (Exception e)
            {
                why = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// WebView2 wants an STA thread with a message loop, which a test runner thread is not.
        /// The whole run happens on one of its own, with a hard cap on it: a window that never
        /// comes up must fail this test rather than hold the suite.
        /// </summary>
        private PageReport RunOnItsOwnThread(string webUi, string languages, string userData, Action packWriter)
        {
            PageReport report = null;
            Exception failure = null;
            var done = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                Form host = null;
                WebView2 view = null;
                try
                {
                    host = new Form
                    {
                        Width = 900,
                        Height = 600,
                        StartPosition = FormStartPosition.Manual,
                        // Off screen and out of the taskbar: this is a probe, not a window
                        // anybody is meant to see appear over what they are doing.
                        Location = new System.Drawing.Point(-4000, -4000),
                        ShowInTaskbar = false,
                        FormBorderStyle = FormBorderStyle.FixedToolWindow,
                    };
                    view = new WebView2 { Dock = DockStyle.Fill };
                    host.Controls.Add(view);

                    var cap = new System.Windows.Forms.Timer { Interval = 60000 };
                    cap.Tick += (s, e) => { cap.Stop(); host.Close(); };

                    host.Load += async (s, e) =>
                    {
                        try
                        {
                            cap.Start();

                            // The app's own order, and the point of the whole test: the
                            // environment, the page's host, then the languages host, then
                            // Navigate. Nothing is answered by hand anywhere in here.
                            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
                            await view.EnsureCoreWebView2Async(environment);
                            var core = view.CoreWebView2;

                            core.SetVirtualHostNameToFolderMapping(
                                "app.baka", webUi, CoreWebView2HostResourceAccessKind.Allow);
                            core.SetVirtualHostNameToFolderMapping(
                                "lang.baka", languages, CoreWebView2HostResourceAccessKind.Allow);

                            core.WebMessageReceived += (ws, we) =>
                            {
                                try { report = Parse(we.TryGetWebMessageAsString()); }
                                catch (Exception ex) { failure = ex; }
                                host.Close();
                            };

                            // The pack lands here on the fresh-install run: after the mapping
                            // is live and before the page is asked for anything.
                            packWriter();

                            await core.ExecuteScriptAsync(Injected());
                            core.AddScriptToExecuteOnDocumentCreatedAsync(Injected()).Wait(5000);

                            core.Navigate("https://app.baka/index.html");
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                            host.Close();
                        }
                    };

                    Application.Run(host);
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    // Disposing the view is what ends the engine's own child processes. They are
                    // never hunted by name: the app under test may be running on this very
                    // machine and its browser is spelled exactly the same way.
                    try { view?.Dispose(); } catch (Exception) { }
                    try { host?.Dispose(); } catch (Exception) { }
                    done.Set();
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            Assert.True(done.Wait(TimeSpan.FromSeconds(90)), "the WebView2 run never finished");
            if (failure != null) throw new Xunit.Sdk.XunitException("the run failed: " + failure);
            Assert.True(report != null, "the page never reported anything back");
            return report;
        }

        // ------------------------------------------------------------------ the pack and the page

        /// <summary>The three addresses the page is handed, in one string, injected twice.</summary>
        private static string Injected() =>
            "window.BAKA_STRINGS_URL=" + Json(StringsUrl(Code, Version)) + ";" +
            "window.BAKA_FONT_URL=" + Json(FontUrl(FontStored)) + ";" +
            "window.BAKA_ESCAPE_URLS=[" + string.Join(",", EscapeUrls.Select(Json)) + "];";

        /// <summary>
        /// The two neighbours, written OUTSIDE the languages folder and never inside it: one file
        /// beside it under the name the real tree uses, and one folder of its own. The real
        /// languages folder sits beside userprefs, the logs and the caches, which is the whole
        /// reason this is worth a run of the engine rather than a reading of the mapping call.
        /// </summary>
        private static void PlantNeighbours(string root)
        {
            File.WriteAllText(Path.Combine(root, NeighbourFile),
                "{\"secret\":\"userprefs is not translation data\"}", new UTF8Encoding(false));

            var sibling = Path.Combine(root, SiblingFolder);
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, SiblingFile),
                "{\"secret\":\"a folder beside the languages folder is not the languages folder\"}",
                new UTF8Encoding(false));
        }

        /// <summary>
        /// A pack of the shape the service installs: a catalog, a pack.json, and a face in the
        /// shared store. The face is a real woff2 off this repo, because a FontFace made from
        /// bytes that are not a font reports "error" whatever the route did.
        /// </summary>
        private static void WritePack(string languages, string mark, int keys)
        {
            var folder = Path.Combine(languages, Code, Version);
            Directory.CreateDirectory(folder);

            var pairs = new List<string> { "\"mark\":" + Json(mark) };
            if (keys > 1) pairs.Add("\"keys\":{\"app.title\":\"BakaLoader\"}");
            File.WriteAllText(Path.Combine(folder, "strings.json"),
                "{" + string.Join(",", pairs) + "}", new UTF8Encoding(false));

            File.WriteAllText(Path.Combine(folder, "pack.json"),
                "{\"code\":\"" + Code + "\",\"appVersion\":\"" + Version + "\"}", new UTF8Encoding(false));

            var face = Path.Combine(languages, FontStored.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(face));
            if (!File.Exists(face)) File.Copy(RealFontFile(), face);
        }

        /// <summary>One of the six faces this build ships, read only and copied out.</summary>
        private static string RealFontFile()
        {
            var fonts = Path.Combine(AppContext.BaseDirectory, "WebUI", "fonts");
            if (Directory.Exists(fonts))
            {
                var shipped = Directory.EnumerateFiles(fonts, "*.woff2").FirstOrDefault();
                if (shipped != null) return shipped;
            }

            return Path.Combine(
                Tools.AppSourceTree.RepoRoot(), "ValheimBakaLoader", "WebUI", "fonts", "Inter-latin.woff2");
        }

        /// <summary>
        /// The page's whole job: fetch the catalog the way switchLanguage does, load the face the
        /// way the injected @font-face would, try the neighbours the way a page that had been
        /// turned against its host would, and say what happened to each. It asks the two questions
        /// switchLanguage asks, which is why a 200 with no CORS header cannot pass this.
        /// <para>
        /// The escapes report the address as the URL parser resolved it as well as the one they
        /// were asked with, because the two are not the same string and only one of them is what
        /// the mapper was handed.
        /// </para>
        /// </summary>
        private static string PageHtml() =>
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>lang route</title></head><body>" +
            "<script>" +
            "(async function(){" +
            "  var out={stringsOk:false,catalogKeys:0,catalogMark:'',stringsNote:'',fontStatus:'',escapes:[]};" +
            "  try{" +
            "    var r=await fetch(window.BAKA_STRINGS_URL,{cache:'no-store'});" +
            "    out.stringsOk=!!r.ok;" +
            "    if(!r.ok) out.stringsNote='status '+r.status;" +
            "    var j=await r.json();" +
            "    out.catalogMark=j&&j.mark?String(j.mark):'';" +
            "    out.catalogKeys=j?Object.keys(j).length:0;" +
            "  }catch(e){out.stringsOk=false;out.stringsNote=String(e&&e.message||e);}" +
            "  try{" +
            "    var f=new FontFace('BakaProbe',\"url('\"+window.BAKA_FONT_URL+\"') format('woff2')\");" +
            "    await f.load();" +
            "    out.fontStatus=f.status;" +
            "  }catch(e){out.fontStatus='error: '+String(e&&e.message||e);}" +
            "  var tries=window.BAKA_ESCAPE_URLS||[];" +
            "  for(var i=0;i<tries.length;i++){" +
            "    var hit={url:tries[i],asked:'',ok:false,note:''};" +
            "    try{" +
            "      hit.asked=new URL(tries[i],location.href).href;" +
            "      var er=await fetch(tries[i],{cache:'no-store'});" +
            "      hit.ok=!!er.ok;" +
            "      hit.note=er.ok?('read '+(await er.text()).length+' bytes'):('status '+er.status);" +
            "    }catch(e){hit.ok=false;hit.note=String(e&&e.message||e);}" +
            "    out.escapes.push(hit);" +
            "  }" +
            "  window.chrome.webview.postMessage(JSON.stringify(out));" +
            "})();" +
            "</script></body></html>";

        private static PageReport Parse(string raw)
        {
            var json = Newtonsoft.Json.Linq.JObject.Parse(raw ?? "{}");
            var report = new PageReport
            {
                StringsOk = json.Value<bool?>("stringsOk") == true,
                CatalogKeys = json.Value<int?>("catalogKeys") ?? 0,
                CatalogMark = json.Value<string>("catalogMark") ?? "",
                StringsNote = json.Value<string>("stringsNote") ?? "",
                FontStatus = json.Value<string>("fontStatus") ?? "",
            };

            foreach (var hit in json["escapes"] as Newtonsoft.Json.Linq.JArray
                                ?? new Newtonsoft.Json.Linq.JArray())
                report.Escapes.Add(new Escape
                {
                    Url = hit.Value<string>("url") ?? "",
                    Asked = hit.Value<string>("asked") ?? "",
                    Ok = hit.Value<bool?>("ok") == true,
                    Note = hit.Value<string>("note") ?? "",
                });

            return report;
        }

        private static string Json(string value) => Newtonsoft.Json.JsonConvert.ToString(value ?? "");

        private static void TryDelete(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception) { /* the engine may still be letting go of its user data folder */ }
        }
    }
}
