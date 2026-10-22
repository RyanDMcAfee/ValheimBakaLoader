using Newtonsoft.Json.Linq;
using Serilog;
using System;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// The host for a bridge with no window: a service, a test, a box with no desktop. Every
    /// answer here is the one that keeps a server running unattended, because that is the
    /// only kind of host that has no window to ask.
    /// </summary>
    public sealed class HeadlessHostUi : IHostUi
    {
        private readonly ILogger Logger;
        private volatile bool Closed;

        /// <summary>Every message the bridge would have pushed at a page.</summary>
        public event Action<string> Posted;

        /// <summary>Raised once, when the bridge asks its host to close.</summary>
        public event Action CloseRequested;

        public HeadlessHostUi(ILogger logger)
        {
            // Serilog's own default is a logger that writes nowhere, which is the right thing
            // for a host that was given none rather than a null reference waiting to happen.
            Logger = logger ?? Log.Logger;
        }

        // Alive from the first line until something asks it to close: there is no handle to
        // wait for and nothing to dispose.
        public bool IsAlive => !Closed;

        // There is no thread the host owns, so the caller's thread is as good as any. True
        // here is what keeps OnUiThread from ever blocking on a slot a writer holds.
        public bool IsUiThread => true;

        // No message loop to queue onto, so the work runs here and now, in order.
        public void Post(Action work) => work?.Invoke();

        public T RunOnUiThread<T>(Func<T> work) => work == null ? default : work();

        public void PostJson(string json) => Posted?.Invoke(json);

        // Window chrome has no meaning without a window.
        public void OnWindowMessage(string method, JObject parameters) { }

        public void RequestClose()
        {
            if (Closed) return;
            Closed = true;
            CloseRequested?.Invoke();
        }

        // The zoom lives on a page this host does not show.
        public void ApplyTextSize(string size) { }

        // No picker to put up, and null is what a cancelled picker answers, so the page gets
        // the same reply it gets when a host presses Cancel.
        public string PickFile(string filter, string initialDirectory) => null;

        public string PickFolder(string initialDirectory) => null;

        // The notices are for a host who is looking; the log is the only place one can look.
        public void NoticeOtherInstance(string caption, string heading, string text) =>
            Logger.Warning("{heading}: {text}", heading, text);

        // A service that finds a server already up keeps it up. Killing a running world with
        // nobody watching is the one answer that cannot be undone.
        public OrphanServerChoice AskAdoptOrKill(string caption, string text)
        {
            Logger.Warning("{text} Adopting it, because there is nobody here to ask.", text);
            return OrphanServerChoice.Adopt;
        }

        public void NoticeUpdateRunning(string caption, string heading, string text) =>
            Logger.Information("{heading}: {text}", heading, text);

        // A headless close is always deliberate (a stop command, a self update), and the
        // graceful road saves the world first. Cancel would leave a stop request hanging.
        public bool AskSaveAndShutDown(
            string caption, string heading, string text, bool warn, string shutDownButton, string cancelButton)
        {
            Logger.Information("{heading}: {text} Saving and shutting down.", heading, text);
            return true;
        }

        // There is one host and it is this one, so closing every window is closing it.
        public void CloseEveryWindowForSelfUpdate() => RequestClose();

        // Nothing to open them with yet; a later step hands them to whatever page is attached.
        public void OpenPath(string path) => Logger.Information("Open folder: {path}", path);

        public void OpenUrl(string url) => Logger.Information("Open address: {url}", url);
    }
}
