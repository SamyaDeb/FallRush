using FallRush.Shared;
using NUnit.Framework;

namespace FallRush.Tests
{
    public class RollingSamplesTests
    {
        [Test]
        public void EmptyIsNaN() => Assert.That(float.IsNaN(new RollingSamples(4).Percentile(0.5f)), Is.True);

        [Test]
        public void NearestRankPercentiles()
        {
            var r = new RollingSamples(100);
            for (var i = 1; i <= 100; i++)
                r.Add(i);
            r.Percentiles(0.5f, 0.95f, out var p50, out var p95);
            Assert.That(p50, Is.EqualTo(50f));
            Assert.That(p95, Is.EqualTo(95f));
        }

        [Test]
        public void RingKeepsOnlyLastN()
        {
            var r = new RollingSamples(3);
            r.Add(100); r.Add(1); r.Add(2); r.Add(3);
            Assert.That(r.Count, Is.EqualTo(3));
            Assert.That(r.Percentile(1f), Is.EqualTo(3f));
        }
    }
}
