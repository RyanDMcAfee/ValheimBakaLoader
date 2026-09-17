using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ValheimBakaLoader.Tools.Logging;
using ValheimBakaLoader.Tools.Models;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// The one step of a mod scan that looks at the second site, and the one place the
    /// host's switch is honoured.
    /// <para>
    /// It lives here rather than inside the window so the promise can be driven directly:
    /// handed a false, it returns without opening a connection, and a recording transport
    /// with nothing in it is what proves that. Handed a true, it fills in what Hexium
    /// holds for each row and nothing else about the row is touched.
    /// </para>
    /// <para>
    /// A Hexium failure can never reach the scan. The client answers null instead of
    /// throwing, and every lookup is wrapped besides, so a site that is down costs the
    /// host nothing but the marks they would have seen.
    /// </para>
    /// </summary>
    public class HexiumScanStep
    {
        private readonly IHexiumClient Hexium;
        private readonly IApplicationLogger Logger;

        public HexiumScanStep(IHexiumClient hexium, IApplicationLogger logger)
        {
            Hexium = hexium;
            Logger = logger;
        }

        /// <summary>
        /// Fills in each row's Hexium version when <paramref name="enabled"/> is true, and
        /// answers how many rows the site actually had something for. A false does nothing
        /// at all: no lookup, no connection, no change to any row.
        /// <para>
        /// <paramref name="force"/> is a host pressing Scan rather than a background check:
        /// the held index is read again before the rows are filled in, so both sites answer
        /// with what they hold right now. It is still inside the "off means off" gate, so a
        /// forced scan with the switch off contacts nobody.
        /// </para>
        /// </summary>
        public async Task<int> ApplyAsync(IEnumerable<InstalledMod> mods, bool enabled, bool force = false)
        {
            // The whole of "off means off". Nothing below this line runs without a yes.
            if (!enabled) return 0;
            if (mods == null) return 0;

            var rows = mods.Where(m => m != null).ToList();
            if (rows.Count == 0) return 0;

            // Asked before the rows are read, so every row is filled in from the same
            // fresh copy. The client's own cooldown is what stops two presses meaning
            // two reads.
            if (force)
            {
                try { await Hexium.RefreshAsync(); }
                catch (Exception e) { Logger?.Debug("Hexium refresh did not answer: {0}", e.Message); }
            }

            var matched = 0;

            await Task.WhenAll(rows.Select(async mod =>
            {
                try
                {
                    // Matched on the exact full name and nothing else. Both sites hold
                    // packages whose names differ only by their capitals, and an id from
                    // one site means nothing at all on the other.
                    var package = await Hexium.GetPackageAsync(mod.FullName);
                    if (package == null) return;

                    mod.HexiumOwner = package.Owner;
                    mod.HexiumName = package.Name;
                    mod.HexiumLatestVersion = package.LatestFor(mod.InstalledVersion)?.VersionNumber;
                    System.Threading.Interlocked.Increment(ref matched);
                }
                catch (Exception e)
                {
                    // Belt to the client's brace: a lookup that somehow throws costs this
                    // row its mark and nothing else.
                    Logger?.Debug("Hexium lookup for {0} did not answer: {1}", mod.FullName, e.Message);
                }
            }));

            return matched;
        }
    }
}
