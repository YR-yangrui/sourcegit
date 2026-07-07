using System;
using System.Collections.Generic;

namespace SourceGit.Diagnostics
{
    internal enum DiagnosticLevel
    {
        Trace,
        Information,
        Warning,
        Error,
    }

    internal sealed class DiagnosticEvent
    {
        public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
        public DiagnosticLevel Level { get; init; } = DiagnosticLevel.Information;
        public string Category { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string OperationId { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        public string Exception { get; init; } = string.Empty;
        public int ProcessId { get; init; }
        public int ThreadId { get; init; }
        public string ThreadName { get; init; } = string.Empty;
        public long DurationUs { get; init; } = -1;
        public Dictionary<string, object> Data { get; init; } = new Dictionary<string, object>();

        public bool IsSpan => DurationUs >= 0;
    }
}
