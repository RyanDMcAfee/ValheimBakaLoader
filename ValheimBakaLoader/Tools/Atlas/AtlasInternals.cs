using System.Runtime.CompilerServices;

// The Atlas save readers are internal on purpose: nothing outside this assembly
// should be parsing world files. Their tests still have to reach them, and the
// alternative is making the readers public just so a test can see them, which
// is a worse trade.
[assembly: InternalsVisibleTo("ValheimBakaLoader.Tests")]
