using System.Diagnostics;

namespace ValheimBakaLoader.Tools
{
    public static class OpenHelper
    {
        /// <summary>Opens a URL in the user's default browser.</summary>
        public static void OpenWebAddress(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;

            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }
}
