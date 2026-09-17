using System;
using System.Collections.Generic;

namespace ValheimBakaLoader.Tools
{
    /// <summary>
    /// An exception whose sentence was written for the host to read, carrying a stable id
    /// beside the English text.
    /// <para>
    /// Every uncaught exception out of an RPC handler becomes a toast, because the page
    /// shows <c>ex.Message</c> exactly as it arrives. That is fine while there is one
    /// language and impossible to key once there is more than one: a sentence is not an
    /// identity, and matching on English text is the bug class that passes every test and
    /// then fails the moment the text changes.
    /// </para>
    /// <para>
    /// So a throw that the host is meant to read carries <see cref="MessageId"/>, a stable
    /// dotted name that never changes, and <see cref="Params"/>, the values the sentence
    /// interpolates. The English text stays exactly where it was, which is what keeps this
    /// additive: the reply still carries <c>error</c>, a page that knows nothing about ids
    /// keeps working, and a catalog that is missing an id falls back to English with no
    /// branch anywhere.
    /// </para>
    /// </summary>
    public sealed class HostFacingException : Exception
    {
        private static readonly IReadOnlyDictionary<string, object> NoParams =
            new Dictionary<string, object>(0);

        /// <summary>
        /// The stable, dotted name of this sentence, normally the RPC it is thrown from plus
        /// what went wrong: <c>profiles.remove.serverRunning</c>, <c>mods.noSuchMod</c>.
        /// It is an identity, not copy: once written it never changes, even if the English
        /// sentence beside it is rewritten.
        /// </summary>
        public string MessageId { get; }

        /// <summary>
        /// The values the sentence interpolates, by name, so the same sentence can be
        /// rebuilt in another language with the words in another order. Never null.
        /// </summary>
        public IReadOnlyDictionary<string, object> Params { get; }

        /// <summary>
        /// One host-facing sentence, its id, and the values it names.
        /// </summary>
        /// <param name="messageId">The stable dotted id. Required.</param>
        /// <param name="english">The sentence as the host reads it today, unchanged.</param>
        /// <param name="args">Named values the sentence interpolates, if any.</param>
        public HostFacingException(string messageId, string english, params (string Name, object Value)[] args)
            : base(english)
        {
            if (string.IsNullOrWhiteSpace(messageId))
                throw new ArgumentException("A host facing message needs an id.", nameof(messageId));

            MessageId = messageId.Trim();

            if (args == null || args.Length == 0)
            {
                Params = NoParams;
                return;
            }

            var map = new Dictionary<string, object>(args.Length, StringComparer.Ordinal);
            foreach (var (name, value) in args)
            {
                if (string.IsNullOrWhiteSpace(name)) continue;
                map[name.Trim()] = value;
            }

            Params = map;
        }

        /// <summary>
        /// The id of an exception if it carries one, otherwise null. The reply envelope asks
        /// this of every exception it answers, so the question lives here rather than being
        /// written out as a cast at each call site.
        /// </summary>
        public static string IdOf(Exception exception) => (exception as HostFacingException)?.MessageId;

        /// <summary>The named values of an exception if it carries any, otherwise null.</summary>
        public static IReadOnlyDictionary<string, object> ParamsOf(Exception exception)
        {
            var host = exception as HostFacingException;
            if (host == null || host.Params == null || host.Params.Count == 0) return null;
            return host.Params;
        }
    }
}
