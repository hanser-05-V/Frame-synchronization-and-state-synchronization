using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace FrameSyncDemo.Tests
{
    public class P2CRuntimeOwnershipTests
    {
        [Test]
        public void GameController_P2CUsesCoordinatorAsOnlyFrameSyncCoreOwner()
        {
            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.NonPublic;

            Assert.IsNotNull(typeof(GameController).GetField(
                "_frameSyncCoordinator",
                Flags));
            Assert.IsNull(typeof(GameController).GetField(
                "_inputLedger",
                Flags));
            Assert.IsNull(typeof(GameController).GetField(
                "_predictionSystem",
                Flags));
            Assert.IsNull(typeof(GameController).GetField(
                "_deterministicWorld",
                Flags));
        }

        [Test]
        public void GameController_P2DRealtimePresentationOwnsNoSecondLogicWorld()
        {
            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.NonPublic;

            Assert.IsNotNull(typeof(GameController).GetField(
                "_presentationInterpolator",
                Flags));
            Assert.IsNull(typeof(GameController).GetField(
                "_viewPlayerEntities",
                Flags));
            Assert.IsNull(typeof(GameController).GetField(
                "_viewBallEntity",
                Flags));
            Assert.IsNull(typeof(GameController).GetField(
                "_viewStateMachines",
                Flags));
        }

        [Test]
        public void GameController_P2EDiagnosticsIsSidecarOnly()
        {
            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.NonPublic;

            FieldInfo diagnosticsField = typeof(GameController).GetField(
                "_runtimeNetworkDiagnostics",
                Flags);
            Assert.IsNotNull(diagnosticsField);
            Assert.AreEqual(
                typeof(RuntimeNetworkDiagnostics),
                diagnosticsField.FieldType);

            foreach (FieldInfo field in typeof(RuntimeNetworkDiagnostics)
                .GetFields(Flags))
            {
                Assert.AreNotEqual(typeof(FrameSyncCoordinator), field.FieldType);
                Assert.AreNotEqual(typeof(FrameInputLedger), field.FieldType);
                Assert.AreNotEqual(typeof(DeterministicWorld), field.FieldType);
            }

            FieldInfo targetField = typeof(RuntimeNetworkDiagnostics).GetField(
                "_targetReadyBacklog",
                Flags);
            FieldInfo capacityField = typeof(RuntimeNetworkDiagnostics).GetField(
                "_maxReadyBacklog",
                Flags);
            Assert.IsNotNull(targetField);
            Assert.IsNotNull(capacityField);
            Assert.AreEqual(typeof(int), targetField.FieldType);
            Assert.AreEqual(typeof(int), capacityField.FieldType);
            Assert.IsTrue(targetField.IsInitOnly);
            Assert.IsTrue(capacityField.IsInitOnly);
            Assert.IsNull(typeof(RuntimeNetworkDiagnostics).GetField(
                "_confirmedPlaybackSettings",
                Flags));

            AssertHasNoDiagnosticsField(typeof(SimulationWorldState));
            AssertHasNoDiagnosticsField(typeof(FrameSnapshot));
            AssertHasNoDiagnosticsField(typeof(FrameInputLedger));
            AssertHasNoDiagnosticsField(typeof(WorldHash));
        }

        [Test]
        public void GameController_OwnsSerializedConfirmedPlaybackSettingsAndOneCursor()
        {
            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.NonPublic;

            FieldInfo settingsField = typeof(GameController).GetField(
                "_confirmedPlaybackSettings",
                Flags);
            FieldInfo cursorField = typeof(GameController).GetField(
                "_confirmedPresentationCursor",
                Flags);

            Assert.IsNotNull(settingsField);
            Assert.AreEqual(
                typeof(ConfirmedPlaybackSettings),
                settingsField.FieldType);
            Assert.IsNotNull(settingsField.GetCustomAttribute<SerializeField>());
            Assert.IsNotNull(cursorField);
            Assert.AreEqual(
                typeof(ConfirmedPresentationCursor),
                cursorField.FieldType);
            Assert.IsNull(typeof(GameController).GetField(
                "_confirmedPresentationWorld",
                Flags));
        }

        [Test]
        public void GameController_UsesAdvanceThenFinalSampleOrchestrationEntryPoint()
        {
            const BindingFlags Flags =
                BindingFlags.Instance | BindingFlags.NonPublic;

            Assert.IsNotNull(typeof(GameController).GetMethod(
                "AdvanceRealtimePresentation",
                Flags));
            Assert.IsNull(typeof(GameController).GetMethod(
                "PushRealtimePresentationFrame",
                Flags));
        }

        private static void AssertHasNoDiagnosticsField(System.Type type)
        {
            const BindingFlags Flags = BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic;
            foreach (FieldInfo field in type.GetFields(Flags))
            {
                Assert.AreNotEqual(
                    typeof(RuntimeNetworkDiagnostics),
                    field.FieldType,
                    $"{type.Name}.{field.Name}");
                Assert.AreNotEqual(
                    typeof(ConfirmedPlaybackAdvance),
                    field.FieldType,
                    $"{type.Name}.{field.Name}");
            }
        }
    }
}
