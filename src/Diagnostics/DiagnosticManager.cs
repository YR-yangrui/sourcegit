using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace SourceGit.Diagnostics
{
    internal static class DiagnosticManager
    {
        public static bool IsEnabled => _enabled && _initialized;

        public static void Setup(string dataDir)
        {
            if (Interlocked.Exchange(ref _setupState, 1) != 0)
                return;

            var disabled = Environment.GetEnvironmentVariable("SOURCEGIT_DIAGNOSTICS");
            if (disabled != null && disabled.Equals("0", StringComparison.OrdinalIgnoreCase))
                return;

            if (string.IsNullOrEmpty(dataDir))
                return;

            _logDir = Path.Combine(dataDir, "logs");
            _profileDir = Path.Combine(dataDir, "profiles");

            try
            {
                Directory.CreateDirectory(_logDir);
                Directory.CreateDirectory(_profileDir);
                CleanupOldFiles(_logDir, "sourcegit-*.jsonl", TimeSpan.FromDays(14));
                CleanupOldFiles(_profileDir, "sourcegit-*.perfetto.json", TimeSpan.FromDays(14));
            }
            catch
            {
                return;
            }

            _enabled = true;
            _initialized = true;
            _writerThread = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "SourceGit Diagnostics Writer",
            };
            _writerThread.Start();

            Info("App", "app.start", "Diagnostics initialized", CreateData(
                ("version", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty),
                ("framework", AppDomain.CurrentDomain.SetupInformation.TargetFrameworkName ?? string.Empty),
                ("os", Environment.OSVersion.ToString())));
        }

        public static void Shutdown(bool exportPerfettoOnExit)
        {
            if (!IsEnabled)
                return;

            Info("App", "app.stop", "Diagnostics shutdown");

            if (exportPerfettoOnExit)
            {
                try
                {
                    ExportRecentPerfettoTrace();
                }
                catch
                {
                    // Ignore export failures during shutdown.
                }
            }

            Interlocked.Exchange(ref _stopping, 1);
            _pendingSignal.Set();
            _writerThread?.Join(2000);
            _initialized = false;
        }

        public static DiagnosticScope StartSpan(string category, string name, Dictionary<string, object> data = null, DiagnosticLevel level = DiagnosticLevel.Trace)
        {
            if (!IsEnabled)
                return DiagnosticScope.Disabled;

            return new DiagnosticScope(category, name, level, data);
        }

        public static void Info(string category, string name, string message = "", Dictionary<string, object> data = null)
        {
            Log(DiagnosticLevel.Information, category, name, message, data);
        }

        public static void Warning(string category, string name, string message = "", Dictionary<string, object> data = null)
        {
            Log(DiagnosticLevel.Warning, category, name, message, data);
        }

        public static void Error(string category, string name, string message = "", Exception exception = null, Dictionary<string, object> data = null)
        {
            Log(DiagnosticLevel.Error, category, name, message, data, exception);
        }

        public static void WriteRecentEvents(TextWriter writer, int maxCount)
        {
            if (writer == null)
                return;

            var events = SnapshotRecentEvents();
            var start = Math.Max(0, events.Count - maxCount);
            for (int i = start; i < events.Count; i++)
            {
                var e = events[i];
                var duration = e.IsSpan ? $" {e.DurationUs / 1000.0:F2}ms" : string.Empty;
                var message = string.IsNullOrEmpty(e.Message) ? string.Empty : $" {e.Message}";
                writer.WriteLine($"{e.TimestampUtc:O} [{e.Level}] {e.Category}/{e.Name}{duration}{message}");
            }
        }

        public static string ExportRecentPerfettoTrace(string outputFile = null)
        {
            if (!IsEnabled && string.IsNullOrEmpty(_profileDir))
                return string.Empty;

            if (string.IsNullOrEmpty(outputFile))
            {
                Directory.CreateDirectory(_profileDir);
                var time = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                outputFile = Path.Combine(_profileDir, $"sourcegit-{time}.perfetto.json");
            }

            PerfettoTraceExporter.Export(outputFile, SnapshotRecentEvents());
            return outputFile;
        }

        public static string GetRepositoryId(string path)
        {
            var normalized = GetRepositoryPath(path);
            if (string.IsNullOrEmpty(normalized))
                return string.Empty;

            if (OperatingSystem.IsWindows())
                normalized = normalized.ToUpperInvariant();

            var bytes = Encoding.UTF8.GetBytes(normalized);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        }

        public static string GetRepositoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                path = Path.GetFullPath(path);
            }
            catch
            {
                // Keep the original value when it cannot be normalized.
            }

            return path.Replace('\\', '/').TrimEnd('/');
        }

        public static string Redact(string value, int maxLength = 512)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var redacted = _urlCredentials.Replace(value, "$1***@");
            redacted = _sensitiveAssignments.Replace(redacted, "$1=***");
            redacted = _sshIdentity.Replace(redacted, "$1***");

            if (redacted.Length > maxLength)
                redacted = $"{redacted.Substring(0, maxLength)}...";

            return redacted;
        }

        public static Dictionary<string, object> CreateData(params (string Key, object Value)[] values)
        {
            var data = new Dictionary<string, object>();
            if (values == null)
                return data;

            foreach (var (key, value) in values)
            {
                if (!string.IsNullOrEmpty(key))
                    data[key] = value;
            }

            return data;
        }

        internal static string NextOperationId()
        {
            return $"op_{Interlocked.Increment(ref _nextOperationId):x}";
        }

        internal static void EmitSpan(DateTime timestampUtc, DiagnosticLevel level, string category, string name, string operationId, long durationUs, Dictionary<string, object> data)
        {
            if (!IsEnabled)
                return;

            Emit(new DiagnosticEvent()
            {
                TimestampUtc = timestampUtc,
                Level = level,
                Category = category ?? string.Empty,
                Name = name ?? string.Empty,
                OperationId = operationId ?? string.Empty,
                DurationUs = durationUs,
                ProcessId = _processId,
                ThreadId = Environment.CurrentManagedThreadId,
                ThreadName = Thread.CurrentThread.Name ?? string.Empty,
                Data = data ?? new Dictionary<string, object>(),
            });
        }

        internal static void WriteValue(Utf8JsonWriter writer, object value)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case string s:
                    writer.WriteStringValue(s);
                    break;
                case bool b:
                    writer.WriteBooleanValue(b);
                    break;
                case int i:
                    writer.WriteNumberValue(i);
                    break;
                case long l:
                    writer.WriteNumberValue(l);
                    break;
                case double d when !double.IsNaN(d) && !double.IsInfinity(d):
                    writer.WriteNumberValue(d);
                    break;
                case float f when !float.IsNaN(f) && !float.IsInfinity(f):
                    writer.WriteNumberValue(f);
                    break;
                case decimal m:
                    writer.WriteNumberValue(m);
                    break;
                case DateTime dt:
                    writer.WriteStringValue(dt.ToUniversalTime());
                    break;
                default:
                    writer.WriteStringValue(value.ToString());
                    break;
            }
        }

        private static void Log(DiagnosticLevel level, string category, string name, string message, Dictionary<string, object> data, Exception exception = null)
        {
            if (!IsEnabled)
                return;

            Emit(new DiagnosticEvent()
            {
                TimestampUtc = DateTime.UtcNow,
                Level = level,
                Category = category ?? string.Empty,
                Name = name ?? string.Empty,
                OperationId = NextOperationId(),
                Message = message ?? string.Empty,
                Exception = exception?.ToString() ?? string.Empty,
                ProcessId = _processId,
                ThreadId = Environment.CurrentManagedThreadId,
                ThreadName = Thread.CurrentThread.Name ?? string.Empty,
                Data = data ?? new Dictionary<string, object>(),
            });
        }

        private static void Emit(DiagnosticEvent e)
        {
            lock (_recentLock)
            {
                _recentEvents.Enqueue(e);
                while (_recentEvents.Count > RECENT_EVENT_LIMIT)
                    _recentEvents.Dequeue();
            }

            _pendingEvents.Enqueue(e);
            _pendingSignal.Set();
        }

        private static List<DiagnosticEvent> SnapshotRecentEvents()
        {
            lock (_recentLock)
            {
                return new List<DiagnosticEvent>(_recentEvents);
            }
        }

        private static void WriterLoop()
        {
            while (Interlocked.CompareExchange(ref _stopping, 0, 0) == 0 || !_pendingEvents.IsEmpty)
            {
                if (_pendingEvents.TryDequeue(out var e))
                {
                    try
                    {
                        WriteJsonLine(e);
                    }
                    catch
                    {
                        // Diagnostics must never break the app.
                    }

                    continue;
                }

                _pendingSignal.WaitOne(500);
            }
        }

        private static void WriteJsonLine(DiagnosticEvent e)
        {
            Directory.CreateDirectory(_logDir);
            var file = GetLogFile(e.TimestampUtc);
            var buffer = new ArrayBufferWriter<byte>(2048);

            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteEventJson(writer, e);
            }

            using var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write(buffer.WrittenSpan);
            stream.WriteByte((byte)'\n');
        }

        private static string GetLogFile(DateTime timestampUtc)
        {
            var date = timestampUtc.ToString("yyyyMMdd");
            var baseFile = Path.Combine(_logDir, $"sourcegit-{date}.jsonl");
            if (!File.Exists(baseFile) || new FileInfo(baseFile).Length < MAX_LOG_FILE_BYTES)
                return baseFile;

            for (int i = 1; i < 100; i++)
            {
                var candidate = Path.Combine(_logDir, $"sourcegit-{date}-{i}.jsonl");
                if (!File.Exists(candidate) || new FileInfo(candidate).Length < MAX_LOG_FILE_BYTES)
                    return candidate;
            }

            return baseFile;
        }

        private static void WriteEventJson(Utf8JsonWriter writer, DiagnosticEvent e)
        {
            writer.WriteStartObject();
            writer.WriteString("ts", e.TimestampUtc);
            writer.WriteString("level", e.Level.ToString());
            writer.WriteString("category", e.Category);
            writer.WriteString("event", e.Name);
            writer.WriteString("operationId", e.OperationId);
            writer.WriteNumber("pid", e.ProcessId);
            writer.WriteNumber("tid", e.ThreadId);
            writer.WriteString("thread", e.ThreadName);

            if (e.IsSpan)
                writer.WriteNumber("durationUs", e.DurationUs);
            if (!string.IsNullOrEmpty(e.Message))
                writer.WriteString("message", e.Message);
            if (!string.IsNullOrEmpty(e.Exception))
                writer.WriteString("exception", e.Exception);

            if (e.Data.Count > 0)
            {
                writer.WriteStartObject("data");
                foreach (var kv in e.Data)
                {
                    writer.WritePropertyName(kv.Key);
                    WriteValue(writer, kv.Value);
                }
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        private static void CleanupOldFiles(string dir, string pattern, TimeSpan maxAge)
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.GetFiles(dir, pattern))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch
                {
                    // Ignore cleanup failures.
                }
            }
        }

        private const int RECENT_EVENT_LIMIT = 4096;
        private const long MAX_LOG_FILE_BYTES = 10L * 1024 * 1024;

        private static readonly Regex _urlCredentials = new Regex(@"([a-zA-Z][a-zA-Z0-9+\-.]*://)([^/\s@]+@)", RegexOptions.CultureInvariant);
        private static readonly Regex _sensitiveAssignments = new Regex(@"(?i)\b(token|password|passwd|secret|apikey|api_key)=([^\s]+)", RegexOptions.CultureInvariant);
        private static readonly Regex _sshIdentity = new Regex(@"(-i\s+)(?:""[^""]+""|'[^']+'|\S+)", RegexOptions.CultureInvariant);
        private static readonly ConcurrentQueue<DiagnosticEvent> _pendingEvents = new ConcurrentQueue<DiagnosticEvent>();
        private static readonly AutoResetEvent _pendingSignal = new AutoResetEvent(false);
        private static readonly object _recentLock = new object();
        private static readonly Queue<DiagnosticEvent> _recentEvents = new Queue<DiagnosticEvent>();
        private static readonly int _processId = Environment.ProcessId;

        private static string _logDir = string.Empty;
        private static string _profileDir = string.Empty;
        private static Thread _writerThread = null;
        private static long _nextOperationId;
        private static int _setupState;
        private static int _stopping;
        private static bool _enabled;
        private static bool _initialized;
    }
}
