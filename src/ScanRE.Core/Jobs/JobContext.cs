// Progress + cancellation passed into every long operation. Inner loops call Check() (cheap) so Cancel
// takes effect within milliseconds; Report() is throttled so UI marshalling never dominates.
using System;
using System.Diagnostics;
using System.Threading;

namespace ScanRE.Core.Jobs
{
    public sealed class JobContext
    {
        private readonly CancellationToken token;
        private readonly Action<string, double> onProgress;
        private readonly Stopwatch since = Stopwatch.StartNew();
        private long lastReportMs = -1000;

        public static readonly JobContext None = new JobContext(CancellationToken.None, null);

        public JobContext(CancellationToken token, Action<string, double> onProgress)
        {
            this.token = token;
            this.onProgress = onProgress;
        }

        public CancellationToken Token { get { return token; } }

        /// <summary>Throws OperationCanceledException when the user pressed Cancel.</summary>
        public void Check() { token.ThrowIfCancellationRequested(); }

        /// <summary>Report the current activity and fraction 0..1 (at most ~10 per second reach the UI).</summary>
        public void Report(string activity, double fraction, bool force = false)
        {
            Check();
            if (onProgress == null) return;
            long now = since.ElapsedMilliseconds;
            if (!force && now - lastReportMs < 100) return;
            lastReportMs = now;
            onProgress(activity, Math.Max(0, Math.Min(1, fraction)));
        }

        /// <summary>Loop helper: check cancel every iteration, report every `every` iterations.</summary>
        public void Step(string activity, long i, long n, long every = 4096)
        {
            if ((i & 1023) == 0) Check();
            if (i % every == 0) Report(activity, n > 0 ? (double)i / n : 0);
        }
    }

    /// <summary>A failure the user can act on: what went wrong and what to do about it (spec principle 7).</summary>
    public sealed class ScanReException : Exception
    {
        public string Advice { get; private set; }
        public ScanReException(string message, string advice, Exception inner = null) : base(message, inner) { Advice = advice; }
        public override string ToString() { return Message + (string.IsNullOrEmpty(Advice) ? "" : "\n\n" + Advice); }
    }
}
