using Newtonsoft.Json.Linq;
using System;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// What the orphan check found when a server was already up with no BakaLoader in charge
    /// of it: adopt it, end it, or leave it alone for the host to deal with by hand.
    /// </summary>
    public enum OrphanServerChoice
    {
        Adopt,
        Kill,
        LeaveAlone,
    }

    /// <summary>
    /// The one seam between the bridge and whatever is showing it. Every handler in
    /// BlendWindow used to reach into the Form it was part of for these: the thread to post
    /// to, the WebView to push a message at, a dialog to put up, the shell to open a folder
    /// with. On Windows the window answers all of it; on a box with no window the headless
    /// host does, and the bridge cannot tell the difference. Each member says what it stands
    /// for in the window, so the Windows implementation is the old code and nothing else.
    /// </summary>
    public interface IHostUi
    {
        /// <summary>
        /// The host is there to talk to. In the window: a handle exists and the form is not
        /// disposed or disposing, which is the check every push made before it touched the
        /// WebView.
        /// </summary>
        bool IsAlive { get; }

        /// <summary>
        /// The caller is already on the thread the host owns. In the window: the inverse of
        /// InvokeRequired. A host with no thread of its own answers true, so nothing waits.
        /// </summary>
        bool IsUiThread { get; }

        /// <summary>
        /// Runs work on the host's own thread once whatever is running there has finished.
        /// In the window: BeginInvoke when the handle exists, the work itself when it does
        /// not yet. Throws InvalidOperationException when the window is already gone, which
        /// is what BeginInvoke did, so the callers that caught that still can.
        /// </summary>
        void Post(Action work);

        /// <summary>
        /// Runs work on the host's own thread and waits for the answer. In the window:
        /// Invoke, which is what a modal dialog needs under it.
        /// </summary>
        T RunOnUiThread<T>(Func<T> work);

        /// <summary>
        /// Hands one JSON message to the page. In the window: CoreWebView2.PostWebMessageAsJson,
        /// marshalled onto the UI thread first because the getter is UI-thread-only.
        /// </summary>
        void PostJson(string json);

        /// <summary>
        /// A win.* message from the page: minimize, maximize, close, the title bar drag and
        /// the resize edges. Pure window chrome, so a host with no window ignores it.
        /// </summary>
        void OnWindowMessage(string method, JObject parameters);

        /// <summary>Asks the host to close. In the window: Form.Close, close guard and all.</summary>
        void RequestClose();

        /// <summary>
        /// The host's Text size moved. In the window: the page zoom and the window minimum
        /// that follows it.
        /// </summary>
        void ApplyTextSize(string size);

        /// <summary>
        /// The system file picker, on the host's own thread. In the window: OpenFileDialog
        /// with the filter and the starting folder given, one existing file only. Null when
        /// the host cancelled or there is no picker to show.
        /// </summary>
        string PickFile(string filter, string initialDirectory);

        /// <summary>
        /// The system folder picker, on the host's own thread. In the window:
        /// FolderBrowserDialog with the new folder button on. Null when cancelled.
        /// </summary>
        string PickFolder(string initialDirectory);

        /// <summary>
        /// Tells the host another BakaLoader already owns the running server and this one is
        /// about to close. In the window: a TaskDialog with one Close button and no way to
        /// cancel, because there is nothing to cancel into.
        /// </summary>
        void NoticeOtherInstance(string caption, string heading, string text);

        /// <summary>
        /// A server is already running and no BakaLoader is in charge of it. In the window:
        /// the Yes / No / Cancel box, which is adopt, kill, leave alone.
        /// </summary>
        OrphanServerChoice AskAdoptOrKill(string caption, string text);

        /// <summary>
        /// The host asked to close while an update is running and the close is being held.
        /// In the window: a TaskDialog with the message and the usual OK.
        /// </summary>
        void NoticeUpdateRunning(string caption, string heading, string text);

        /// <summary>
        /// Servers are still running and the host asked to close. In the window: a TaskDialog
        /// with the two buttons named, warning icon when vikings are online. The cancel
        /// button is the platform's own when <paramref name="cancelButton"/> is null. True
        /// means save and shut down.
        /// </summary>
        bool AskSaveAndShutDown(
            string caption, string heading, string text, bool warn, string shutDownButton, string cancelButton);

        /// <summary>
        /// A newer BakaLoader is staged, so the whole application closes. In the window: the
        /// walk over Application.OpenForms, the splash told to end the process, Application.Exit
        /// when nothing was left to ask.
        /// </summary>
        void CloseEveryWindowForSelfUpdate();

        /// <summary>Opens a folder for the host to look at. In the window: Explorer.</summary>
        void OpenPath(string path);

        /// <summary>Opens a web address for the host. In the window: the default browser.</summary>
        void OpenUrl(string url);
    }
}
