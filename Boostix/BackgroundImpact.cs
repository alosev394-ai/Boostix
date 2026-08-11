using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Boostix
{
    internal sealed class BackgroundProcessIdentity
    {
        public int ProcessId;
        public DateTime StartTimeUtc;
        public string ProcessName;
        public string ExecutablePath;
    }

    internal sealed class BackgroundImpactResult
    {
        public BackgroundProcessIdentity Identity;
        public TimeSpan CpuTime;
        public double CpuPercent;
        public long PrivateBytes;
        public long WorkingSetBytes;
        public long ReadBytes;
        public long WriteBytes;
        public string MeasurementNote;
    }

    internal sealed class BackgroundCloseResult
    {
        public BackgroundProcessIdentity Identity;
        public bool CloseRequested;
        public bool Exited;
        public string Message;
    }

    /// <summary>
    /// Measures user-session processes without changing them. Results expose
    /// direct CPU, memory and I/O deltas rather than an opaque score.
    /// </summary>
    internal static class BackgroundImpactAnalyzer
    {
        private const int DefaultMeasurementMilliseconds = 15000;
        private const int MinimumMeasurementMilliseconds = 1000;
        private const int MaximumMeasurementMilliseconds = 30000;
        private const int MaximumResults = 24;

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        private sealed class ProcessSample
        {
            public BackgroundProcessIdentity Identity;
            public TimeSpan CpuTime;
            public long PrivateBytes;
            public long WorkingSetBytes;
            public ulong ReadBytes;
            public ulong WriteBytes;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(
            IntPtr processHandle,
            out IoCounters counters);

        public static async Task<IList<BackgroundImpactResult>> MeasureAsync(
            int measurementMilliseconds,
            int excludedProcessId,
            CancellationToken cancellationToken)
        {
            int duration = Math.Max(
                MinimumMeasurementMilliseconds,
                Math.Min(MaximumMeasurementMilliseconds,
                    measurementMilliseconds <= 0
                        ? DefaultMeasurementMilliseconds
                        : measurementMilliseconds));
            Dictionary<int, ProcessSample> before = Capture(excludedProcessId);
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<int, ProcessSample> after = Capture(excludedProcessId);

            var results = new List<BackgroundImpactResult>();
            foreach (KeyValuePair<int, ProcessSample> pair in after)
            {
                ProcessSample first;
                ProcessSample last = pair.Value;
                if (!before.TryGetValue(pair.Key, out first) ||
                    !SameProcess(first.Identity, last.Identity))
                {
                    continue;
                }

                TimeSpan cpuDelta = last.CpuTime - first.CpuTime;
                if (cpuDelta < TimeSpan.Zero)
                {
                    continue;
                }
                double cpuPercent = cpuDelta.TotalMilliseconds * 100.0 /
                    duration / Math.Max(1, Environment.ProcessorCount);
                results.Add(new BackgroundImpactResult
                {
                    Identity = last.Identity,
                    CpuTime = cpuDelta,
                    CpuPercent = Math.Max(0, cpuPercent),
                    PrivateBytes = Math.Max(0, last.PrivateBytes),
                    WorkingSetBytes = Math.Max(0, last.WorkingSetBytes),
                    ReadBytes = SaturatingDelta(last.ReadBytes, first.ReadBytes),
                    WriteBytes = SaturatingDelta(last.WriteBytes, first.WriteBytes),
                    MeasurementNote = "Измерено за " +
                        (duration / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) +
                        " с; процесс не изменялся."
                });
            }

            return results
                .OrderByDescending(item => item.CpuPercent)
                .ThenByDescending(item => item.ReadBytes + item.WriteBytes)
                .ThenByDescending(item => item.PrivateBytes)
                .Take(MaximumResults)
                .ToList();
        }

        /// <summary>
        /// Requests a graceful close for exact, explicitly selected process
        /// identities. This method never uses forced or command-line termination.
        /// </summary>
        public static IList<BackgroundCloseResult> RequestGracefulClose(
            IEnumerable<BackgroundProcessIdentity> selected,
            int waitMilliseconds)
        {
            int wait = Math.Max(0, Math.Min(10000, waitMilliseconds));
            var results = new List<BackgroundCloseResult>();
            foreach (BackgroundProcessIdentity identity in
                (selected ?? Enumerable.Empty<BackgroundProcessIdentity>()))
            {
                var result = new BackgroundCloseResult { Identity = identity };
                Process process = null;
                try
                {
                    if (identity == null || identity.ProcessId <= 0)
                    {
                        throw new InvalidOperationException("Некорректная выбранная программа.");
                    }
                    process = Process.GetProcessById(identity.ProcessId);
                    if (!SameProcess(identity, CreateIdentity(process)) ||
                        process.MainWindowHandle == IntPtr.Zero)
                    {
                        throw new InvalidOperationException(
                            "Программа уже закрыта, перезапущена или не имеет окна.");
                    }
                    result.CloseRequested = process.CloseMainWindow();
                    if (!result.CloseRequested)
                    {
                        result.Message = "Windows не приняла запрос на обычное закрытие.";
                    }
                    else
                    {
                        result.Exited = wait > 0 && process.WaitForExit(wait);
                        result.Message = result.Exited
                            ? "Программа закрылась штатно."
                            : "Запрос отправлен; программа оставлена запущенной.";
                    }
                }
                catch (Exception ex)
                {
                    result.Message = ex.Message;
                }
                finally
                {
                    if (process != null)
                    {
                        process.Dispose();
                    }
                }
                results.Add(result);
            }
            return results;
        }

        private static Dictionary<int, ProcessSample> Capture(int excludedProcessId)
        {
            var samples = new Dictionary<int, ProcessSample>();
            int ownSession;
            try { ownSession = Process.GetCurrentProcess().SessionId; }
            catch { ownSession = -1; }

            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == excludedProcessId || process.HasExited ||
                            (ownSession >= 0 && process.SessionId != ownSession) ||
                            process.MainWindowHandle == IntPtr.Zero)
                        {
                            continue;
                        }
                        BackgroundProcessIdentity identity = CreateIdentity(process);
                        if (identity == null || IsExcludedName(identity.ProcessName))
                        {
                            continue;
                        }
                        IoCounters io;
                        bool ioAvailable = GetProcessIoCounters(process.Handle, out io);
                        samples[identity.ProcessId] = new ProcessSample
                        {
                            Identity = identity,
                            CpuTime = process.TotalProcessorTime,
                            PrivateBytes = process.PrivateMemorySize64,
                            WorkingSetBytes = process.WorkingSet64,
                            ReadBytes = ioAvailable ? io.ReadTransferCount : 0,
                            WriteBytes = ioAvailable ? io.WriteTransferCount : 0
                        };
                    }
                    catch
                    {
                        // Protected, exiting and cross-architecture processes are
                        // expected. The analyzer remains read-only and skips them.
                    }
                }
            }
            return samples;
        }

        private static BackgroundProcessIdentity CreateIdentity(Process process)
        {
            if (process == null)
            {
                return null;
            }
            string path = string.Empty;
            try { path = process.MainModule.FileName; }
            catch { }
            return new BackgroundProcessIdentity
            {
                ProcessId = process.Id,
                StartTimeUtc = process.StartTime.ToUniversalTime(),
                ProcessName = process.ProcessName ?? string.Empty,
                ExecutablePath = path ?? string.Empty
            };
        }

        private static bool SameProcess(
            BackgroundProcessIdentity first,
            BackgroundProcessIdentity second)
        {
            return first != null && second != null &&
                first.ProcessId == second.ProcessId &&
                first.StartTimeUtc == second.StartTimeUtc;
        }

        private static bool IsExcludedName(string name)
        {
            string value = (name ?? string.Empty).Trim();
            return value.Length == 0 ||
                value.Equals("Boostix", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("dwm", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("Taskmgr", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("SystemSettings", StringComparison.OrdinalIgnoreCase);
        }

        private static long SaturatingDelta(ulong later, ulong earlier)
        {
            if (later <= earlier)
            {
                return 0;
            }
            ulong delta = later - earlier;
            return delta > long.MaxValue ? long.MaxValue : (long)delta;
        }
    }
}
