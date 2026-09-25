using Xunit;

namespace ValheimBakaLoader.Tests
{
    /// <summary>
    /// The tests that read a stopwatch, kept out of the parallel run.
    /// <para>
    /// A test that asserts "this was over in under ten seconds" is measuring the machine as
    /// much as the code. On the two-core runner the suite starts every collection at once, and
    /// in that first half minute a method that takes two milliseconds here took fifteen
    /// seconds there: the thread pool was starved and the timer that ends a 250 millisecond
    /// budget could not get a thread to end it on. Every other test was fine with that; the
    /// ones with a bound were not, and one of them held a release for it.
    /// </para>
    /// <para>
    /// So the bounded ones share this collection and the collection does not run beside any
    /// other. xUnit runs it after the parallel collections have finished, on a quiet machine,
    /// where a 250 millisecond budget ends in about 250 milliseconds and the bounds mean what
    /// they say. The bounds themselves are unchanged: loosening them to fit a starved runner
    /// would have kept a real regression green.
    /// </para>
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class WallClockCollection
    {
        public const string Name = "Wall clock";
    }
}
