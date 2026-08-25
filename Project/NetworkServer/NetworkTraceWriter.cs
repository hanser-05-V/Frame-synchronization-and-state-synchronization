using System;
using System.Globalization;
using System.IO;
using System.Text;
using FrameSyncDemo;

namespace FrameSyncServer
{
    public sealed class NetworkTraceWriter : IDisposable
    {
        private readonly object _sync = new object();
        private readonly StreamWriter _decisionWriter;
        private readonly StreamWriter _timingWriter;

        public NetworkTraceWriter(
            string decisionTracePath,
            string timingTracePath)
        {
            _decisionWriter = CreateWriter(decisionTracePath);
            _timingWriter = CreateWriter(timingTracePath);
        }

        public void RecordDecision(
            int senderIndex,
            long senderSequence,
            int frameID,
            uint raw,
            DeterministicNetworkFaultModel.Decision decision)
        {
            if (_decisionWriter == null)
                return;

            string line = FormatDecision(
                senderIndex,
                senderSequence,
                frameID,
                raw,
                decision);
            lock (_sync)
            {
                _decisionWriter.WriteLine(line);
            }
        }

        public void RecordTiming(
            DeterministicPacketScheduler.Delivery delivery,
            long actualSendAtMs,
            long batchId)
        {
            if (_timingWriter == null)
                return;

            string line = FormatTiming(delivery, actualSendAtMs, batchId);
            lock (_sync)
            {
                _timingWriter.WriteLine(line);
            }
        }

        public static string FormatDecision(
            int senderIndex,
            long senderSequence,
            int frameID,
            uint raw,
            DeterministicNetworkFaultModel.Decision decision)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"sender\":{0},\"sequence\":{1},\"frame\":{2}," +
                "\"raw\":\"0x{3:X8}\",\"delayMs\":{4},\"jitterMs\":{5}," +
                "\"reorder\":{6},\"duplicate\":{7},\"lossRecovered\":{8}}}",
                senderIndex,
                senderSequence,
                frameID,
                raw,
                decision.EffectiveDelayMs,
                decision.JitterOffsetMs,
                decision.ApplicationReordered ? "true" : "false",
                decision.Duplicated ? "true" : "false",
                decision.LossRecovered ? "true" : "false");
        }

        public static string FormatTiming(
            DeterministicPacketScheduler.Delivery delivery,
            long actualSendAtMs,
            long batchId)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{{\"sender\":{0},\"sequence\":{1},\"frame\":{2}," +
                "\"copy\":{3},\"enqueuedAtMs\":{4},\"dueAtMs\":{5}," +
                "\"actualSendAtMs\":{6},\"batchId\":{7},\"latenessMs\":{8}}}",
                delivery.SenderIndex,
                delivery.SenderSequence,
                delivery.FrameID,
                delivery.CopyIndex,
                delivery.EnqueuedAtMs,
                delivery.DueAtMs,
                actualSendAtMs,
                batchId,
                actualSendAtMs - delivery.DueAtMs);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_decisionWriter != null)
                    _decisionWriter.Dispose();
                if (_timingWriter != null)
                    _timingWriter.Dispose();
            }
        }

        private static StreamWriter CreateWriter(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var writer = new StreamWriter(
                fullPath,
                false,
                new UTF8Encoding(false));
            writer.AutoFlush = true;
            return writer;
        }
    }
}
