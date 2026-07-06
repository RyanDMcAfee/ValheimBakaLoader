namespace ValheimBakaLoader.Game
{
    /// <summary>
    /// Lifecycle of the managed dedicated-server process. Numeric values are
    /// part of the Blend bridge contract, so they must not be reordered.
    /// </summary>
    public enum ServerStatus
    {
        Stopped = 0,
        Starting = 1,
        Running = 2,
        Stopping = 3,
    }

    /// <summary>
    /// Connection state of a single player, as inferred from server log
    /// output. Numeric values are part of the Blend bridge contract.
    /// </summary>
    public enum PlayerStatus
    {
        Offline = 0,
        Joining = 1,
        Online = 2,
        Leaving = 3,
    }
}
