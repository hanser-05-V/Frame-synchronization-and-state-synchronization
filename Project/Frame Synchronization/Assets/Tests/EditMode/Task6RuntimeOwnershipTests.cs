using System.Reflection;
using NUnit.Framework;

namespace FrameSyncDemo.Tests
{
    public class Task6RuntimeOwnershipTests
    {
        [Test]
        public void GameController_DoesNotKeepRollbackRequestBufferAsMismatchFactSource()
        {
            FieldInfo rollbackRequests = typeof(GameController).GetField(
                "_rollbackRequests",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.IsNull(rollbackRequests);
        }
    }
}
