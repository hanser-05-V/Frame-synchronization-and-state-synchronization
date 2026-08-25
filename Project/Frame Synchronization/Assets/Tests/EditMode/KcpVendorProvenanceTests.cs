using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class KcpVendorProvenanceTests
    {
        [Test]
        public void SetNoDelay_OneAndFiveMilliseconds_RemainEffective()
        {
            var packets = new List<byte[]>();
            var one = new kcp2k.Kcp(
                1u,
                (bytes, count) => packets.Add(bytes.Take(count).ToArray()));

            one.SetNoDelay(1u, 1u, 2, false);
            one.Update(0u);
            Assert.AreEqual(1u, one.Check(0u));

            var five = new kcp2k.Kcp(2u, (bytes, count) => { });
            five.SetNoDelay(1u, 5u, 2, false);
            five.Update(0u);
            Assert.AreEqual(5u, five.Check(0u));
        }

        [TestCase("kcp2k.KcpClient")]
        [TestCase("kcp2k.KcpServer")]
        [TestCase("kcp2k.KcpConnection")]
        public void RuntimeAssembly_HighLevelKcpType_IsNotImported(string typeName)
        {
            Assert.IsNull(typeof(kcp2k.Kcp).Assembly.GetType(typeName));
        }
    }
}
