// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using osu.Framework.Development;
using osu.Framework.Graphics.Rendering.Deferred.Allocation;

namespace osu.Framework.Benchmarks
{
    [MemoryDiagnoser]
    [GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
    [CategoriesColumn]
    public class BenchmarkResourceReference
    {
        private const int reference_count = 10_000;

        private readonly ResourceAllocator allocator = new ResourceAllocator();
        private readonly List<object?> oldResources = new List<object?>();
        private readonly object firstResource = new object();
        private readonly object secondResource = new object();
        private bool wasDrawThread;

        [GlobalSetup]
        public void Setup()
        {
            wasDrawThread = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
        }

        [GlobalCleanup]
        public void Cleanup() => ThreadSafety.IsDrawThread = wasDrawThread;

        [Benchmark(Baseline = true)]
        [BenchmarkCategory("Adjacent same object")]
        public int OldAppendAdjacentReferences()
        {
            oldResources.Clear();
            int lastId = -1;

            for (int i = 0; i < reference_count; i++)
            {
                ThreadSafety.EnsureDrawThread();
                oldResources.Add(firstResource);
                lastId = oldResources.Count - 1;
            }

            return lastId;
        }

        [Benchmark]
        [BenchmarkCategory("Adjacent same object")]
        public int ReferenceAdjacentRepeatedReferences()
        {
            allocator.NewFrame();
            int lastId = -1;

            for (int i = 0; i < reference_count; i++)
                lastId = allocator.ReferenceAdjacent(firstResource).Id;

            return lastId;
        }

        [Benchmark]
        [BenchmarkCategory("Adjacent same object")]
        public int ReferenceRepeatedReferences()
        {
            allocator.NewFrame();
            int lastId = -1;

            for (int i = 0; i < reference_count; i++)
                lastId = allocator.Reference(firstResource).Id;

            return lastId;
        }

        [Benchmark(Baseline = true)]
        [BenchmarkCategory("Alternating objects")]
        public int OldAppendAlternatingReferences()
        {
            oldResources.Clear();
            int lastId = -1;

            for (int i = 0; i < reference_count; i++)
            {
                ThreadSafety.EnsureDrawThread();
                oldResources.Add((i & 1) == 0 ? firstResource : secondResource);
                lastId = oldResources.Count - 1;
            }

            return lastId;
        }

        [Benchmark]
        [BenchmarkCategory("Alternating objects")]
        public int ReferenceAlternatingReferences()
        {
            allocator.NewFrame();
            int lastId = -1;

            for (int i = 0; i < reference_count; i++)
                lastId = allocator.Reference((i & 1) == 0 ? firstResource : secondResource).Id;

            return lastId;
        }
    }
}
