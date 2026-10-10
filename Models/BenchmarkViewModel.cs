namespace SampleR2.Models
{
    /// <summary>
    /// Model halaman benchmark upload.
    /// </summary>
    public class BenchmarkViewModel
    {
        public int Iterations { get; set; }

        public int SizeKb { get; set; }

        public bool R2Configured { get; set; }
    }

    /// <summary>
    /// Hasil benchmark untuk satu target (wwwroot / R2 / R2 dari browser).
    /// </summary>
    public class BenchmarkTargetResult
    {
        public string Target { get; set; } = string.Empty;

        public int Success { get; set; }

        public int Failed { get; set; }

        public string? FirstError { get; set; }

        public double MinMs { get; set; }

        public double AvgMs { get; set; }

        public double MedianMs { get; set; }

        public double MaxMs { get; set; }

        /// <summary>Total waktu semua iterasi (tanpa warmup), dalam detik.</summary>
        public double TotalSeconds { get; set; }

        public double PayloadMb { get; set; }

        public double TotalMb => PayloadMb * (Success + Failed);

        public double ThroughputMBps => TotalSeconds > 0 ? TotalMb / TotalSeconds : 0;

        public static BenchmarkTargetResult Build(string target, long payloadBytes, List<double> samplesMs, int failed, string? firstError)
        {
            var ordered = samplesMs.OrderBy(x => x).ToList();
            double median = ordered.Count == 0
                ? 0
                : ordered.Count % 2 == 1
                    ? ordered[ordered.Count / 2]
                    : (ordered[ordered.Count / 2 - 1] + ordered[ordered.Count / 2]) / 2.0;

            return new BenchmarkTargetResult
            {
                Target = target,
                Success = samplesMs.Count - failed,
                Failed = failed,
                FirstError = firstError,
                MinMs = ordered.FirstOrDefault(),
                MaxMs = ordered.LastOrDefault(),
                AvgMs = ordered.Count == 0 ? 0 : ordered.Average(),
                MedianMs = median,
                TotalSeconds = samplesMs.Sum() / 1000.0,
                PayloadMb = payloadBytes / (1024.0 * 1024.0),
            };
        }
    }
}
