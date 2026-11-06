// Copyright (c) GooGuTeam. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading;

namespace osu.Framework.Allocation
{
    /// <summary>
    /// Owns a pooled array while exposing only the requested memory range.
    /// </summary>
    public sealed class PooledMemoryOwner<T> : IMemoryOwner<T>
    {
        private T[]? array;
        private readonly int length;

        public PooledMemoryOwner(int length, bool clear = false)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            this.length = length;
            array = ArrayPool<T>.Shared.Rent(length);
            if (clear)
                array.AsSpan(0, length).Clear();
        }

        public Memory<T> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(array == null, this);
                return array.AsMemory(0, length);
            }
        }

        public void Dispose()
        {
            var rented = Interlocked.Exchange(ref array, null);
            if (rented != null)
                ArrayPool<T>.Shared.Return(rented, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }
}
