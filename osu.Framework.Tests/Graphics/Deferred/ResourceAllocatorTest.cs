// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using NUnit.Framework;
using osu.Framework.Development;
using osu.Framework.Graphics.Rendering.Deferred.Allocation;

namespace osu.Framework.Tests.Graphics.Deferred
{
    [TestFixture]
    public class ResourceAllocatorTest
    {
        private bool previousDrawThreadState;

        [SetUp]
        public void SetUp()
        {
            previousDrawThreadState = ThreadSafety.IsDrawThread;
            ThreadSafety.IsDrawThread = true;
        }

        [TearDown]
        public void TearDown() => ThreadSafety.IsDrawThread = previousDrawThreadState;

        [Test]
        public void TestAdjacentReferencesReuseOnlyTheSameObject()
        {
            var allocator = new ResourceAllocator();
            var first = new ObjectWithThrowingEquals();
            var second = new ObjectWithThrowingEquals();

            ResourceReference firstReference = allocator.ReferenceAdjacent(first);
            ResourceReference repeatedReference = allocator.ReferenceAdjacent(first);
            ResourceReference differentReference = allocator.ReferenceAdjacent(second);
            ResourceReference nonAdjacentReference = allocator.ReferenceAdjacent(first);

            Assert.That(repeatedReference.Id, Is.EqualTo(firstReference.Id));
            Assert.That(differentReference.Id, Is.EqualTo(firstReference.Id + 1));
            Assert.That(nonAdjacentReference.Id, Is.EqualTo(differentReference.Id + 1));
            Assert.That(allocator.Dereference<ObjectWithThrowingEquals>(firstReference), Is.SameAs(first));
            Assert.That(allocator.Dereference<ObjectWithThrowingEquals>(differentReference), Is.SameAs(second));
        }

        [Test]
        public void TestAdjacentNullReferencesReuseId()
        {
            var allocator = new ResourceAllocator();

            ResourceReference firstNull = allocator.ReferenceAdjacent<object?>(null);
            ResourceReference secondNull = allocator.ReferenceAdjacent<object?>(null);
            ResourceReference afterNull = allocator.ReferenceAdjacent(new object());

            Assert.That(secondNull.Id, Is.EqualTo(firstNull.Id));
            Assert.That(afterNull.Id, Is.EqualTo(firstNull.Id + 1));
            Assert.That(allocator.Dereference<object?>(firstNull), Is.Null);
        }

        [Test]
        public void TestNewFrameResetsAdjacentReferenceReuse()
        {
            var allocator = new ResourceAllocator();
            object shared = new object();

            ResourceReference beforeFrame = allocator.ReferenceAdjacent(shared);
            Assert.That(allocator.ReferenceAdjacent(shared).Id, Is.EqualTo(beforeFrame.Id));

            allocator.NewFrame();

            ResourceReference afterFrame = allocator.ReferenceAdjacent(shared);
            Assert.That(afterFrame.Id, Is.Zero);
            Assert.That(allocator.Dereference<object>(afterFrame), Is.SameAs(shared));
        }

        [Test]
        public void TestAdjacentBatchReferencesReuseResourceId()
        {
            var allocator = new ResourceAllocator();
            var batch = new ObjectWithThrowingEquals();
            ResourceReference firstReference = allocator.Reference(batch);
            ResourceReference lastReference = firstReference;

            for (int i = 0; i < 10_000; i++)
                lastReference = allocator.ReferenceAdjacent(batch);

            Assert.That(lastReference.Id, Is.EqualTo(firstReference.Id));
        }

        [Test]
        public void TestRegularReferenceAlwaysAppendsAndAdjacentReferenceCanReuseItsLastEntry()
        {
            var allocator = new ResourceAllocator();
            var first = new ObjectWithThrowingEquals();
            var second = new ObjectWithThrowingEquals();

            ResourceReference firstReference = allocator.Reference(first);
            ResourceReference secondReference = allocator.Reference(first);
            ResourceReference adjacentReference = allocator.ReferenceAdjacent(first);
            ResourceReference repeatedAdjacentReference = allocator.ReferenceAdjacent(first);
            ResourceReference differentReference = allocator.ReferenceAdjacent(second);
            ResourceReference nonAdjacentReference = allocator.ReferenceAdjacent(first);
            ResourceReference appendedReference = allocator.Reference(first);

            Assert.That(secondReference.Id, Is.EqualTo(firstReference.Id + 1));
            Assert.That(adjacentReference.Id, Is.EqualTo(secondReference.Id));
            Assert.That(repeatedAdjacentReference.Id, Is.EqualTo(adjacentReference.Id));
            Assert.That(differentReference.Id, Is.EqualTo(secondReference.Id + 1));
            Assert.That(nonAdjacentReference.Id, Is.EqualTo(differentReference.Id + 1));
            Assert.That(appendedReference.Id, Is.EqualTo(nonAdjacentReference.Id + 1));
            Assert.That(allocator.Dereference<ObjectWithThrowingEquals>(firstReference), Is.SameAs(first));
            Assert.That(allocator.Dereference<ObjectWithThrowingEquals>(secondReference), Is.SameAs(first));
            Assert.That(allocator.Dereference<ObjectWithThrowingEquals>(adjacentReference), Is.SameAs(first));
            Assert.That(allocator.Dereference<ObjectWithThrowingEquals>(differentReference), Is.SameAs(second));
            Assert.That(allocator.Dereference<ObjectWithThrowingEquals>(nonAdjacentReference), Is.SameAs(first));
        }

        private sealed class ObjectWithThrowingEquals
        {
            public override bool Equals(object? obj) => throw new InvalidOperationException("Resource references must compare object identity.");

            public override int GetHashCode() => throw new InvalidOperationException("Resource references must compare object identity.");
        }
    }
}
