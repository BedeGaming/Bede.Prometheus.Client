﻿using Prometheus.Advanced;
using Prometheus.Advanced.DataContracts;
using Prometheus.Internal;
using Prometheus.SummaryImpl;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Prometheus
{
    public interface ISummary
    {
        void Observe(double val);
    }

    public class Summary : Collector<Summary.Child>, ISummary
    {
        // Label that defines the quantile in a summary.
        const string QuantileLabel = "quantile";

        internal static readonly QuantileEpsilonPair[] DefObjectivesArray = new[]
        {
            new QuantileEpsilonPair(0.5, 0.05),
            new QuantileEpsilonPair(0.9, 0.01),
            new QuantileEpsilonPair(0.99, 0.001)
        };

        // Default Summary quantile values.
        public static readonly IList<QuantileEpsilonPair> DefObjectives = new List<QuantileEpsilonPair>(DefObjectivesArray);

        // Default duration for which observations stay relevant
        public static readonly TimeSpan DefMaxAge = TimeSpan.FromMinutes(10);

        // Default number of buckets used to calculate the age of observations
        public static readonly int DefAgeBuckets = 5;

        // Standard buffer size for collecting Summary observations
        public static readonly int DefBufCap = 500;

        readonly IReadOnlyList<QuantileEpsilonPair> _objectives;
        readonly TimeSpan _maxAge;
        readonly int _ageBuckets;
        readonly int _bufCap;

        internal Summary(
            string name,
            string help,
            string[] labelNames,
            bool suppressInitialValue = false,
            IReadOnlyList<QuantileEpsilonPair> objectives = null,
            TimeSpan? maxAge = null,
            int? ageBuckets = null,
            int? bufCap = null)
            : base(name, help, labelNames, suppressInitialValue)
        {
            _objectives = objectives ?? DefObjectivesArray;
            _maxAge = maxAge ?? DefMaxAge;
            _ageBuckets = ageBuckets ?? DefAgeBuckets;
            _bufCap = bufCap ?? DefBufCap;

            if (_objectives.Count == 0)
                _objectives = DefObjectivesArray;

            if (_maxAge < TimeSpan.Zero)
                throw new ArgumentException($"Illegal max age {_maxAge}");

            if (_ageBuckets == 0)
                _ageBuckets = DefAgeBuckets;

            if (_bufCap == 0)
                _bufCap = DefBufCap;

            if (labelNames?.Any(_ => _ == QuantileLabel) == true)
                throw new ArgumentException($"{QuantileLabel} is a reserved label name");
        }

        protected override MetricType Type => MetricType.SUMMARY;

        public class Child : Advanced.Child, ISummary
        {
            // Objectives defines the quantile rank estimates with their respective
            // absolute error. If Objectives[q] = e, then the value reported
            // for q will be the φ-quantile value for some φ between q-e and q+e.
            // The default value is DefObjectives.
            IReadOnlyList<QuantileEpsilonPair> _objectives = new List<QuantileEpsilonPair>();
            double[] _sortedObjectives;
            double _sum;
            uint _count;
            SampleBuffer _hotBuf;
            SampleBuffer _coldBuf;
            QuantileStream[] _streams;
            TimeSpan _streamDuration;
            QuantileStream _headStream;
            int _headStreamIdx;
            DateTime _headStreamExpTime;
            DateTime _hotBufExpTime;
            // Protects hotBuf and hotBufExpTime.
            readonly object _bufLock = new object();
            // Protects every other moving part.
            // Lock bufMtx before mtx if both are needed.
            readonly object _lock = new object();
            readonly QuantileComparer _quantileComparer = new QuantileComparer();

            // MaxAge defines the duration for which an observation stays relevant
            // for the summary. Must be positive. The default value is DefMaxAge.
            TimeSpan _maxAge;

            // AgeBuckets is the number of buckets used to exclude observations that
            // are older than MaxAge from the summary. A higher number has a
            // resource penalty, so only increase it if the higher resolution is
            // really required. For very high observation rates, you might want to
            // reduce the number of age buckets. With only one age bucket, you will
            // effectively see a complete reset of the summary each time MaxAge has
            // passed. The default value is DefAgeBuckets.
            int _ageBuckets;

            // BufCap defines the default sample stream buffer size.  The default
            // value of DefBufCap should suffice for most uses. If there is a need
            // to increase the value, a multiple of 500 is recommended (because that
            // is the internal buffer size of the underlying package
            // "github.com/bmizerany/perks/quantile").      
            int _bufCap;

            Advanced.DataContracts.SummaryInfo _wireMetric;

            internal override void Init(ICollector parent, LabelValues labelValues, bool publish)
            {
                Init(parent, labelValues, DateTime.UtcNow, publish);
            }

            internal void Init(ICollector parent, LabelValues labelValues, DateTime now, bool publish)
            {
                base.Init(parent, labelValues, publish);

                _objectives = ((Summary)parent)._objectives;
                _maxAge = ((Summary)parent)._maxAge;
                _ageBuckets = ((Summary)parent)._ageBuckets;
                _bufCap = ((Summary)parent)._bufCap;

                _sortedObjectives = new double[_objectives.Count];
                _hotBuf = new SampleBuffer(_bufCap);
                _coldBuf = new SampleBuffer(_bufCap);
                _streamDuration = new TimeSpan(_maxAge.Ticks / _ageBuckets);
                _headStreamExpTime = now.Add(_streamDuration);
                _hotBufExpTime = _headStreamExpTime;

                _streams = new QuantileStream[_ageBuckets];
                for (var i = 0; i < _ageBuckets; i++)
                {
                    _streams[i] = QuantileStream.NewTargeted(_objectives);
                }

                _headStream = _streams[0];

                for (var i = 0; i < _objectives.Count; i++)
                {
                    _sortedObjectives[i] = _objectives[i].Quantile;
                }

                Array.Sort(_sortedObjectives);

                _wireMetric = new Advanced.DataContracts.SummaryInfo();

                for (var i = 0; i < _objectives.Count; i++)
                {
                    _wireMetric.Quantile.Add(new QuantileValue
                    {
                        Quantile = _objectives[i].Quantile
                    });
                }
            }

            protected override void Populate(Metric metric)
            {
                Populate(metric, DateTime.UtcNow);
            }

            internal void Populate(Metric metric, DateTime now)
            {
                var summary = new Advanced.DataContracts.SummaryInfo();
                var quantiles = new QuantileValue[_objectives.Count];

                lock (_bufLock)
                {
                    lock (_lock)
                    {
                        // Swap bufs even if hotBuf is empty to set new hotBufExpTime.
                        SwapBufs(now);
                        FlushColdBuf();
                        summary.SampleCount = _count;
                        summary.SampleSum = _sum;

                        for (var idx = 0; idx < _sortedObjectives.Length; idx++)
                        {
                            var rank = _sortedObjectives[idx];
                            var q = _headStream.Count == 0 ? double.NaN : _headStream.Query(rank);

                            quantiles[idx] = new QuantileValue
                            {
                                Quantile = rank,
                                Value = q
                            };
                        }
                    }
                }

                if (quantiles.Length > 0)
                    Array.Sort(quantiles, _quantileComparer);

                for (var i = 0; i < quantiles.Length; i++)
                {
                    summary.Quantile.Add(quantiles[i]);
                }

                metric.Summary = summary;
            }

            public void Observe(double val)
            {
                Observe(val, DateTime.UtcNow);
            }

            /// <summary>
            /// For unit tests only
            /// </summary>
            internal void Observe(double val, DateTime now)
            {
                if (double.IsNaN(val))
                    return;

                lock (_bufLock)
                {
                    if (now > _hotBufExpTime)
                        Flush(now);

                    _hotBuf.Append(val);

                    if (_hotBuf.IsFull)
                        Flush(now);
                }

                _publish = true;
            }

            // Flush needs bufMtx locked.
            void Flush(DateTime now)
            {
                lock (_lock)
                {
                    SwapBufs(now);

                    // Go version flushes on a separate goroutine, but doing this on another
                    // thread actually makes the benchmark tests slower in .net
                    FlushColdBuf();
                }
            }

            // SwapBufs needs mtx AND bufMtx locked, coldBuf must be empty.
            void SwapBufs(DateTime now)
            {
                if (!_coldBuf.IsEmpty)
                    throw new InvalidOperationException("coldBuf is not empty");

                var temp = _hotBuf;
                _hotBuf = _coldBuf;
                _coldBuf = temp;

                // hotBuf is now empty and gets new expiration set.
                while (now > _hotBufExpTime)
                {
                    _hotBufExpTime = _hotBufExpTime.Add(_streamDuration);
                }
            }

            // FlushColdBuf needs mtx locked. 
            void FlushColdBuf()
            {
                for (var bufIdx = 0; bufIdx < _coldBuf.Position; bufIdx++)
                {
                    var value = _coldBuf[bufIdx];

                    for (var streamIdx = 0; streamIdx < _streams.Length; streamIdx++)
                    {
                        _streams[streamIdx].Insert(value);
                    }

                    _count++;
                    _sum += value;
                }

                _coldBuf.Reset();
                MaybeRotateStreams();
            }

            // MaybeRotateStreams needs mtx AND bufMtx locked.
            void MaybeRotateStreams()
            {
                while (!_hotBufExpTime.Equals(_headStreamExpTime))
                {
                    _headStream.Reset();
                    _headStreamIdx++;

                    if (_headStreamIdx >= _streams.Length)
                        _headStreamIdx = 0;

                    _headStream = _streams[_headStreamIdx];
                    _headStreamExpTime = _headStreamExpTime.Add(_streamDuration);
                }
            }
        }

        public void Observe(double val)
        {
            Unlabelled.Observe(val);
        }

        public void Publish() => Unlabelled.Publish();
    }
}