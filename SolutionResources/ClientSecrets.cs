namespace ValheimBakaLoader.Properties
{
    /// <summary>
    /// Declarations only - a git-ignored partial (ClientSecrets.Values.cs)
    /// carries the static constructor that fills these in at build time.
    /// </summary>
    public static partial class ClientSecrets
    {
        /// <summary>Name of the HTTP header the remote API expects the key in.</summary>
        public static string RemoteApiKeyHeader { get; } = string.Empty;

        /// <summary>Key sent with every client request to the remote API.</summary>
        public static string RemoteClientApiKey { get; }
    }
}
