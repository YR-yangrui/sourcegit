using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SourceGit.Diagnostics
{
    internal sealed class DiagnosticScope : IDisposable
    {
        public static DiagnosticScope Disabled { get; } = new DiagnosticScope();

        public string OperationId
        {
            get;
        } = string.Empty;

        private DiagnosticScope()
        {
            _enabled = false;
        }

        internal DiagnosticScope(string category, string name, DiagnosticLevel level, Dictionary<string, object> data)
        {
            _enabled = true;
            _category = category;
            _name = name;
            _level = level;
            _data = data ?? new Dictionary<string, object>();
            _startTimestampUtc = DateTime.UtcNow;
            _startTicks = Stopwatch.GetTimestamp();
            OperationId = DiagnosticManager.NextOperationId();
        }

        public void Set(string key, object value)
        {
            if (!_enabled || _disposed || string.IsNullOrEmpty(key))
                return;

            _data[key] = value;
        }

        public void MarkCanceled(bool canceled)
        {
            Set("canceled", canceled);
        }

        public void Dispose()
        {
            if (!_enabled || _disposed)
                return;

            _disposed = true;

            var elapsedTicks = Stopwatch.GetTimestamp() - _startTicks;
            var durationUs = elapsedTicks * 1000000 / Stopwatch.Frequency;
            DiagnosticManager.EmitSpan(_startTimestampUtc, _level, _category, _name, OperationId, durationUs, _data);
        }

        private readonly bool _enabled;
        private readonly string _category = string.Empty;
        private readonly string _name = string.Empty;
        private readonly DiagnosticLevel _level = DiagnosticLevel.Trace;
        private readonly DateTime _startTimestampUtc = DateTime.UtcNow;
        private readonly long _startTicks;
        private readonly Dictionary<string, object> _data = null;
        private bool _disposed;
    }
}
