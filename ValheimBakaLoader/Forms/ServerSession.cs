using System;
using ValheimBakaLoader.Game;

namespace ValheimBakaLoader.Forms
{
    /// <summary>
    /// One live server slot in the multi-server registry: a profile name, the
    /// ValheimServer instance that runs it, and the per-process CPU-sampling
    /// state the Forge Load metrics RPC needs between polls.
    /// </summary>
    public class ServerSession
    {
        public ServerSession(string profileName, ValheimServer server)
        {
            ProfileName = profileName ?? throw new ArgumentNullException(nameof(profileName));
            Server = server ?? throw new ArgumentNullException(nameof(server));
        }

        public string ProfileName { get; }

        public ValheimServer Server { get; }

        // CPU-usage sampling state (per tracked process). A CPU percentage is a
        // delta between two samples, so the previous sample must persist per session.
        public int MetricsPid { get; set; } = -1;
        public DateTime MetricsSampleTime { get; set; }
        public TimeSpan MetricsCpuTime { get; set; }
    }
}
