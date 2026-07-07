using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SourceGit.Diagnostics
{
    internal static class PerfettoTraceExporter
    {
        public static void Export(string outputFile, IReadOnlyList<DiagnosticEvent> events)
        {
            if (string.IsNullOrEmpty(outputFile))
                return;

            var dir = Path.GetDirectoryName(outputFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var buffer = new ArrayBufferWriter<byte>(Math.Max(4096, events.Count * 256));
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions() { Indented = false }))
            {
                writer.WriteStartObject();
                writer.WriteString("displayTimeUnit", "ms");
                writer.WriteStartArray("traceEvents");

                WriteProcessMetadata(writer, events);
                foreach (var e in events)
                    WriteTraceEvent(writer, e);

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            using var stream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.Write(buffer.WrittenSpan);
        }

        private static void WriteProcessMetadata(Utf8JsonWriter writer, IReadOnlyList<DiagnosticEvent> events)
        {
            var processIds = new HashSet<int>();
            var threadIds = new HashSet<int>();

            foreach (var e in events)
            {
                if (processIds.Add(e.ProcessId))
                {
                    writer.WriteStartObject();
                    writer.WriteString("ph", "M");
                    writer.WriteString("name", "process_name");
                    writer.WriteNumber("pid", e.ProcessId);
                    writer.WriteNumber("tid", 0);
                    writer.WriteStartObject("args");
                    writer.WriteString("name", "SourceGit");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }

                if (threadIds.Add(e.ThreadId))
                {
                    writer.WriteStartObject();
                    writer.WriteString("ph", "M");
                    writer.WriteString("name", "thread_name");
                    writer.WriteNumber("pid", e.ProcessId);
                    writer.WriteNumber("tid", e.ThreadId);
                    writer.WriteStartObject("args");
                    writer.WriteString("name", string.IsNullOrEmpty(e.ThreadName) ? $"Thread {e.ThreadId}" : e.ThreadName);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
            }
        }

        private static void WriteTraceEvent(Utf8JsonWriter writer, DiagnosticEvent e)
        {
            writer.WriteStartObject();
            writer.WriteString("cat", e.Category);
            writer.WriteString("name", e.Name);
            writer.WriteNumber("pid", e.ProcessId);
            writer.WriteNumber("tid", e.ThreadId);
            writer.WriteNumber("ts", ToTraceTimestampUs(e.TimestampUtc));

            if (e.IsSpan)
            {
                writer.WriteString("ph", "X");
                writer.WriteNumber("dur", Math.Max(0, e.DurationUs));
            }
            else
            {
                writer.WriteString("ph", "i");
                writer.WriteString("s", "t");
            }

            writer.WriteStartObject("args");
            writer.WriteString("level", e.Level.ToString());
            writer.WriteString("operationId", e.OperationId);
            if (!string.IsNullOrEmpty(e.Message))
                writer.WriteString("message", e.Message);
            if (!string.IsNullOrEmpty(e.Exception))
                writer.WriteString("exception", e.Exception);

            foreach (var kv in e.Data)
            {
                writer.WritePropertyName(kv.Key);
                DiagnosticManager.WriteValue(writer, kv.Value);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        private static long ToTraceTimestampUs(DateTime timestampUtc)
        {
            return (timestampUtc.ToUniversalTime().Ticks - DateTime.UnixEpoch.Ticks) / 10;
        }
    }
}
