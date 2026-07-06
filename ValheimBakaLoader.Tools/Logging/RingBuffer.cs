using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ValheimBakaLoader.Tools.Logging
{
    /// <summary>
    /// Thread-safe FIFO buffer that keeps at most <see cref="Capacity"/> items,
    /// discarding the oldest as new ones arrive. Backs the in-memory log tail
    /// that crash reports attach.
    /// </summary>
    public class RingBuffer<T> : IEnumerable<T>
    {
        private readonly ConcurrentQueue<T> Items = new();

        public RingBuffer(int capacity)
        {
            Capacity = capacity;
        }

        public int Capacity { get; }

        public void Add(T item)
        {
            Items.Enqueue(item);

            while (Items.Count > Capacity)
            {
                Items.TryDequeue(out _);
            }
        }

        public IEnumerator<T> GetEnumerator() => Items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
