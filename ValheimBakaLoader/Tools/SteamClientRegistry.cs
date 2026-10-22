namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The one registry read the server build tracker needs: where the Steam client lives.
    /// It is here, with the Windows host, rather than in ServerBuildTracker itself because
    /// that file compiles into Core, which names no registry; Program.Main hands this over
    /// through ServerBuildTracker.SteamClientPath before the window opens.
    /// </summary>
    internal static class SteamClientRegistry
    {
        internal static string ClientPath() =>
            Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
    }
}
