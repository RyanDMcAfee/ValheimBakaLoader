using System.Runtime.CompilerServices;

// The Atlas save readers are internal on purpose: nothing outside this assembly
// should be parsing world files. Their tests still have to reach them, and the
// alternative is making the readers public just so a test can see them, which
// is a worse trade.
[assembly: InternalsVisibleTo("ValheimBakaLoader.Tests")]
// The suite that runs with no window (ValheimBakaLoader.Core.Tests, plain net6.0) reaches
// the same internals, because the platform seam's proofs have to run on a Linux box.
[assembly: InternalsVisibleTo("ValheimBakaLoader.Core.Tests")]
