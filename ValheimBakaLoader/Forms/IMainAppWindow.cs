namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// Startup contract for the Blend WebView2 main window, so SplashForm can create
    /// and track it: assign the profile to load, whether to auto-start its server, and
    /// the window's slot in the splash screen's open-window list.
    /// </summary>
    public interface IMainAppWindow
    {
        /// <summary>This window's index in SplashForm's window list.</summary>
        int SplashIndex { get; set; }

        /// <summary>The server profile to load when the window is first shown.</summary>
        string StartProfile { get; set; }

        /// <summary>If true, start the server from StartProfile as soon as the window is shown.</summary>
        bool StartServerAutomatically { get; set; }
    }
}
