using ValheimBakaLoader.Forms;
using Xunit;

namespace ValheimBakaLoader.Tests.Forms
{
    public class SplashFormTests : BaseTest
    {
        [Fact]
        public void SplashFormResolvesFromTheContainer()
        {
            GetForm<SplashForm>();
        }
    }
}
