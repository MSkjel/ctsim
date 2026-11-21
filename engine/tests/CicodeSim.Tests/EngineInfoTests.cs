using Xunit;

namespace CicodeSim.Tests
{
    public class EngineInfoTests
    {
        [Fact]
        public void HasAName() => Assert.Equal("cicode-sim", EngineInfo.Name);
    }
}
