// Licensed to Elasticsearch B.V under
// one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Elastic.Apm.Api;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;

namespace Elastic.Apm.Metrics.MetricsProvider
{
	internal class SystemTotalCpuProvider : IMetricsProvider, IDisposable
	{
		internal const string SystemCpuTotalPct = "system.cpu.total.norm.pct";

		// ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
		private readonly IApmLogger _logger;
		private readonly PerformanceCounter _processorTimePerfCounter;
		private readonly StreamReader _procStatStreamReader;

		public SystemTotalCpuProvider(IApmLogger logger)
		{
			_logger = logger.Scoped(nameof(SystemTotalCpuProvider));
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				var categoryName = "Processor";
				try
				{
					try
					{
						_processorTimePerfCounter = new PerformanceCounter(categoryName, "% Processor Time", "_Total");
					}
					catch (InvalidOperationException e)
					{
						_logger.Debug()?.LogException(e, "Error instantiating '{CategoryName}' performance counter.", categoryName);
						_processorTimePerfCounter?.Dispose();
						// If the Processor performance counter category does not exist, try Processor Information.
						categoryName = "Processor Information";
						_processorTimePerfCounter = new PerformanceCounter(categoryName, "% Processor Time", "_Total");
					}

					//The perf. counter API returns 0 the for the 1. call (probably because there is no delta in the 1. call) - so we just call it here first
					_processorTimePerfCounter.NextValue();
				}
				catch (Exception e)
				{
					if (e is UnauthorizedAccessException)
					{
						_logger.Error()
							?.LogException(e, "Error instantiating '{CategoryName}' performance counter."
								+ " Process does not have permissions to read performance counters."
								+ " See https://www.elastic.co/guide/en/apm/agent/dotnet/current/metrics.html#metrics-system to see how to configure.", categoryName);
					}
					else
					{
						_logger.Error()
							?.LogException(e, "Error instantiating '{CategoryName}' performance counter", categoryName);
					}

					_logger.Warning()?.Log("System metrics won't be collected");
					_processorTimePerfCounter?.Dispose();
					_processorTimePerfCounter = null;
				}
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				var (success, idle, total) = ReadProcStat();
				if (!success) return;

				_prevIdleTime = idle;
				_prevTotalTime = total;
			}
		}

		internal SystemTotalCpuProvider(IApmLogger logger, StreamReader procStatStreamReader)
			=> (_logger, _procStatStreamReader) = (logger.Scoped(nameof(SystemTotalCpuProvider)), procStatStreamReader);

		private long _prevIdleTime;
		private long _prevTotalTime;

		public int ConsecutiveNumberOfFailedReads { get; set; }
		public string DbgName => "total system CPU time";

		public bool IsMetricAlreadyCaptured => true;

		internal (bool success, long idle, long total) ReadProcStat()
		{
			using (var sr = GetProcStatAsStream())
			{
				if (sr == null)
					return (false, 0, 0);

				try
				{
					var firstLine = sr.ReadLine();
					if (firstLine == null || !firstLine.ToLower().StartsWith("cpu")) return (false, 0, 0);

					var values = firstLine.Substring(3, firstLine.Length - 3).Trim().Split(' ').ToArray();
					if (values.Length < 4)
						return (false, 0, 0);

					var numbers = new long[values.Length];

					if (values.Where((t, i) => !long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[i])).Any())
						return (false, 0, 0);

					var total = numbers.Sum();
					var idle = numbers[3];

					return (true, idle, total);
				}
				catch
				{
					return (false, 0, 0);
				}
			}
		}

		private StreamReader GetProcStatAsStream()
			=> _procStatStreamReader ?? (File.Exists("/proc/stat") ? new StreamReader("/proc/stat") : null);

		public IEnumerable<MetricSet> GetSamples()
		{
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
			{
				if (_processorTimePerfCounter == null) return null;

				var val = _processorTimePerfCounter.NextValue();

				return new List<MetricSet>
				{
					new MetricSet(TimeUtils.TimestampNow(), new List<MetricSample> { new MetricSample(SystemCpuTotalPct, (double)val / 100) })
				};
			}

			if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
			{
				var (success, idle, total) = ReadProcStat();
				if (!success) return null;

				var idleTimeDelta = idle - _prevIdleTime;
				var totalTimeDelta = total - _prevTotalTime;
				var notIdle = 1.0 - idleTimeDelta / (double)totalTimeDelta;

				_prevIdleTime = idle;
				_prevTotalTime = total;

				return new List<MetricSet>
				{
					new MetricSet(TimeUtils.TimestampNow(), new List<MetricSample> { new MetricSample(SystemCpuTotalPct, notIdle) })
				};
			}

			return null;
		}

		public bool IsEnabled(IReadOnlyList<WildcardMatcher> disabledMetrics) => !WildcardMatcher.IsAnyMatch(disabledMetrics, SystemCpuTotalPct);

		public void Dispose()
		{
			_procStatStreamReader?.Dispose();
			_processorTimePerfCounter?.Dispose();
		}
	}
}
