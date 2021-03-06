using Prometheus.Advanced;
using Prometheus.Advanced.DataContracts;
using Prometheus.Internal;
using System;
using System.Linq;

namespace Prometheus
{
    public interface IHistogram
    {
        void Observe(double val);
    }

    public class Histogram : Collector<Histogram.Child>, IHistogram
    {
        private static readonly double[] DefaultBuckets = { .005, .01, .025, .05, .075, .1, .25, .5, .75, 1, 2.5, 5, 7.5, 10 };
        private readonly double[] _buckets;

        internal Histogram(string name, string help, string[] labelNames, bool suppressInitialValue, double[] buckets) : base(name, help, labelNames, suppressInitialValue)
        {
            if (labelNames?.Any(l => l == "le") == true)
            {
                throw new ArgumentException("'le' is a reserved label name");
            }
            _buckets = buckets ?? DefaultBuckets;

            if (_buckets.Length == 0)
            {
                throw new ArgumentException("Histogram must have at least one bucket");
            }

            if (!double.IsPositiveInfinity(_buckets[_buckets.Length - 1]))
            {
                _buckets = _buckets.Concat(new[] { double.PositiveInfinity }).ToArray();
            }

            for (int i = 1; i < _buckets.Length; i++)
            {
                if (_buckets[i] <= _buckets[i - 1])
                {
                    throw new ArgumentException("Bucket values must be increasing");
                }
            }
        }

        public class Child : Advanced.Child, IHistogram
        {
            private ThreadSafeDouble _sum = new ThreadSafeDouble(0.0D);
            private ThreadSafeLong[] _bucketCounts;
            private double[] _upperBounds;

            internal override void Init(ICollector parent, LabelValues labelValues, bool publish)
            {
                base.Init(parent, labelValues, publish);

                _upperBounds = ((Histogram)parent)._buckets;
                _bucketCounts = new ThreadSafeLong[_upperBounds.Length];
            }

            protected override void Populate(Metric metric)
            {
                var wireMetric = new Advanced.DataContracts.HistogramInfo();
                wireMetric.SampleCount = 0L;

                for (var i = 0; i < _bucketCounts.Length; i++)
                {
                    wireMetric.SampleCount += (ulong)_bucketCounts[i].Value;
                    wireMetric.Bucket.Add(new BucketInfo
                    {
                        UpperBound = _upperBounds[i],
                        CumulativeCount = wireMetric.SampleCount
                    });
                }
                wireMetric.SampleSum = _sum.Value;

                metric.Histogram = wireMetric;
            }

            public void Observe(double val)
            {
                if (double.IsNaN(val))
                {
                    return;
                }

                for (int i = 0; i < _upperBounds.Length; i++)
                {
                    if (val <= _upperBounds[i])
                    {
                        _bucketCounts[i].Add(1);
                        break;
                    }
                }
                _sum.Add(val);
                _publish = true;
            }
        }

        protected override MetricType Type
        {
            get { return MetricType.HISTOGRAM; }
        }

        public void Observe(double val)
        {
            Unlabelled.Observe(val);
        }

        public void Publish() => Unlabelled.Publish();
    }
}