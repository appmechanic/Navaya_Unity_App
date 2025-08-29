#if BESTHTTP_ENABLE_BUFFERPOOL_DOUBLE_RELEASE_CHECKER
using Best.HTTP.Shared.Extensions;
#endif
using Best.HTTP.Shared.Logger;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

using Unity.Burst;

using static Unity.Burst.Intrinsics.X86.Bmi1;

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
using System.Linq;
#endif

namespace Best.HTTP.Shared.PlatformSupport.Memory
{
    /// <summary>
    /// The BufferPool is a foundational element of the Best HTTP package, aiming to reduce dynamic memory allocation overheads by reusing byte arrays. The concept is elegantly simple: rather than allocating and deallocating memory for every requirement, byte arrays can be "borrowed" and "returned" within this pool. Once returned, these arrays are retained for subsequent use, minimizing repetitive memory operations.
    /// <para>While the BufferPool is housed within the Best HTTP package, its benefits are not limited to just HTTP operations. All protocols and packages integrated with or built upon the Best HTTP package utilize and benefit from the BufferPool. This ensures that memory is used efficiently and performance remains optimal across all integrated components.</para>
    /// </summary>
    [Best.HTTP.Shared.PlatformSupport.IL2CPP.Il2CppEagerStaticClassConstructionAttribute]
    public static class BufferPool
    {
        /// <summary>
        /// Specifies the minimum buffer size that will be allocated. If a request is made for a size smaller than this and canBeLarger is <c>true</c>, 
        /// this size will be used.
        /// </summary>
        public const long MIN_BUFFER_SIZE = 512;

        /// <summary>
        /// Specifies the maximum size of a buffer that the system will consider storing back into the pool.
        /// </summary>
        public const long MAX_BUFFER_SIZE = 32 * 1024 * 1024;

        /// <summary>
        /// Represents an empty byte array that can be returned for zero-length requests.
        /// </summary>
        public static readonly byte[] NoData = new byte[0];

        /// <summary>
        /// Gets or sets a value indicating whether the buffer pooling mechanism is enabled or disabled.
        /// Disabling will also clear all stored entries.
        /// </summary>
        public static bool IsEnabled
        {
            get { return _isEnabled; }
            set
            {
                _isEnabled = value;

                // When set to non-enabled remove all stored entries
                if (!_isEnabled)
                    Clear();
            }
        }
        private static volatile bool _isEnabled = true;

        /// <summary>
        /// Specifies how frequently the maintenance cycle should run to manage old buffers.
        /// </summary>
        public static TimeSpan RunMaintenanceEvery = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Specifies the maximum total size of all stored buffers. When the buffer reach this threshold, new releases will be declined.
        /// </summary>
        public static long MaxPoolSize = 100 * 1024 * 1024;

        public static long MinBufferSize => MIN_BUFFER_SIZE;

#if BESTHTTP_ENABLE_BUFFERPOOL_BUFFER_STEALING
        /// <summary>
        /// Index threshold that getting a larger buffer is allowed in.
        /// </summary>
        public static int MaxIndexThreshold = 3;
#endif

        private static DateTime lastMaintenance = DateTime.MinValue;

        // Statistics
        private static long PoolSize = 0;
        private static long GetBuffers = 0;
        private static long ReleaseBuffers = 0;
        private static long Borrowed = 0;
        private static long ArrayAllocations = 0;

        private static readonly Bucket[] _buckets;
        private static readonly int firstIdx;
        private static readonly int lastIdx;

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
        private static ConditionalWeakTable<byte[], Tracker> _trackers = new ConditionalWeakTable<byte[], Tracker>();
#endif

#if BESTHTTP_ENABLE_BUFFERPOOL_DOUBLE_RELEASE_CHECKER
        private static ConcurrentDictionary<byte[], (byte[] buffer, string stackTrace)> _doubleReleaseTracker = new ConcurrentDictionary<byte[], (byte[] buffer, string stackTrace)>();
#endif

        static BufferPool()
        {
#if UNITY_ANDROID || UNITY_IOS
            UnityEngine.Application.lowMemory -= OnLowMemory;
            UnityEngine.Application.lowMemory += OnLowMemory;
#endif

            firstIdx = GetIdx(BufferPool.MIN_BUFFER_SIZE);
            lastIdx = GetIdx(BufferPool.MAX_BUFFER_SIZE);

            _buckets = new Bucket[(lastIdx - firstIdx) + 1];

            for (int i = 0; i < _buckets.Length; i++)
                _buckets[i] = new Bucket((int)(MIN_BUFFER_SIZE << i));
        }

#if UNITY_EDITOR
        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        public static void ResetSetup()
        {
            HTTPManager.Logger.Information("BufferPool", "Reset called!");
            PoolSize = 0;
            GetBuffers = 0;
            ReleaseBuffers = 0;
            Borrowed = 0;
            ArrayAllocations = 0;

            for (int i = 0; i < _buckets.Length; i++)
                _buckets[i] = new Bucket((int)(MIN_BUFFER_SIZE << i));

            lastMaintenance = DateTime.MinValue;

#if UNITY_ANDROID || UNITY_IOS
            UnityEngine.Application.lowMemory -= OnLowMemory;
            UnityEngine.Application.lowMemory += OnLowMemory;
#endif

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
            ClearTrackers();
#endif

#if BESTHTTP_ENABLE_BUFFERPOOL_DOUBLE_RELEASE_CHECKER
            _doubleReleaseTracker.Clear();
#endif
        }
#endif

#if UNITY_ANDROID || UNITY_IOS
        private static void OnLowMemory()
        {
            HTTPManager.Logger.Warning(nameof(BufferPool), nameof(OnLowMemory));

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
            ClearTrackers();
#endif

#if BESTHTTP_ENABLE_BUFFERPOOL_DOUBLE_RELEASE_CHECKER
            _doubleReleaseTracker.Clear();
#endif

            // Drop all buffers
            Clear();
        }
#endif

        /// <summary>
        /// Fetches a byte array from the pool.
        /// </summary>
        /// <remarks>Depending on the `canBeLarger` parameter, the returned buffer may be larger than the requested size!</remarks>
        /// <param name="size">Requested size of the buffer.</param>
        /// <param name="canBeLarger">If <c>true</c>, the returned buffer can be larger than the requested size.</param>
        /// <param name="context">Optional context for logging purposes.</param>
        /// <returns>A byte array from the pool or a newly allocated one if suitable size is not available.</returns>
        public static byte[] Get(long size, bool canBeLarger, LoggingContext context = null)
        {
            if (!_isEnabled)
                return new byte[size];

            // Return a fix reference for 0 length requests. Any resize call (even Array.Resize) creates a new reference
            //  so we are safe to expose it to multiple callers.
            if (size == 0)
                return BufferPool.NoData;

            if (size > BufferPool.MAX_BUFFER_SIZE)
                return new byte[size];

            if (size < BufferPool.MIN_BUFFER_SIZE)
                size = BufferPool.MIN_BUFFER_SIZE;
            else if (!BufferPool.IsPowerOfTwo(size))
                size = BufferPool.NextPowerOf2(size);

            Interlocked.Add(ref Borrowed, size);

            int idx = GetIdx(size >> firstIdx);

            var buckets = _buckets;

#if BESTHTTP_ENABLE_BUFFERPOOL_BUFFER_STEALING
            int maxIdx = idx + MaxIndexThreshold;

            while (idx < buckets.Length && idx <= maxIdx)
#else
            if (idx < buckets.Length)
#endif
            {
                ref Bucket bucket = ref buckets[idx];
                var item = bucket.FastItem;

                if ((item != null && Interlocked.CompareExchange(ref bucket.FastItem, null, item) == item) ||
                    bucket.Items.TryPop(out item))
                {
                    Interlocked.Increment(ref GetBuffers);
                    Interlocked.Add(ref PoolSize, -size);

                    UpdateMinCount(ref bucket, 0);

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
                    _trackers.Add(item, new Tracker(context));
#endif

#if BESTHTTP_ENABLE_BUFFERPOOL_DOUBLE_RELEASE_CHECKER
                    _doubleReleaseTracker.Remove(item, out var _);
#endif

                    return item;
                }

#if BESTHTTP_ENABLE_BUFFERPOOL_BUFFER_STEALING
                idx++;
#endif
            }

            Interlocked.Increment(ref ArrayAllocations);

            var result = new byte[size];

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
            _trackers.Add(result, new Tracker(context));
#endif
            return result;
        }

        /// <summary>
        /// Releases a list of buffer segments back to the pool in a bulk operation.
        /// </summary>
        /// <param name="segments">List of buffer segments to release.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReleaseBulk(ConcurrentQueue<BufferSegment> segments)
        {
            if (!_isEnabled || segments == null)
                return;

            while (segments.TryDequeue(out var segment))
                Release(segment);
        }

        /// <summary>
        /// Releases a list of buffer segments back to the pool in a bulk operation.
        /// </summary>
        /// <param name="segments">List of buffer segments to release.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ReleaseBulk(List<BufferSegment> segments)
        {
            if (!_isEnabled || segments == null)
                return;

            for (int i = 0; i < segments.Count; i++)
                Release(segments[i]);
            segments.Clear();
        }

        /// <summary>
        /// Releases a byte array back to the pool.
        /// </summary>
        /// <param name="buffer">Buffer to be released back to the pool.</param>
        public static void Release(byte[] buffer)
        {
            if (!_isEnabled || buffer == null)
                return;

            int size = buffer.Length;

            if (size == 0 || size < MIN_BUFFER_SIZE || size > MAX_BUFFER_SIZE)
                return;

            if (!IsPowerOfTwo(size))
                return;

#if BESTHTTP_ENABLE_BUFFERPOOL_DOUBLE_RELEASE_CHECKER
            if (_doubleReleaseTracker.TryGetValue(buffer, out var entry))
            {
                HTTPManager.Logger.Error("BufferPool", $"Buffer already added to the pool! Previously added from: {ProcessStackTrace(entry.stackTrace)}. Buffer: {entry.buffer.AsBuffer()}");
                return;
            }
            else
                _doubleReleaseTracker.TryAdd(buffer, new(buffer, Environment.StackTrace));
#endif

            Interlocked.Add(ref Borrowed, -size);

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
            if (_trackers.TryGetValue(buffer, out var tracker))
            {
                _trackers.Remove(buffer);
                tracker.Dispose();
            }
#endif

            var ps = Interlocked.Read(ref PoolSize);
            if (ps + size > MaxPoolSize)
                return;

            Interlocked.Add(ref PoolSize, size);
            Interlocked.Increment(ref ReleaseBuffers);

            int idx = GetIdx(size >> firstIdx);
            var buckets = _buckets;
            ref var bucket = ref buckets[idx];

            if (Interlocked.CompareExchange(ref bucket.FastItem, buffer, null) != null)
            {
                bucket.Items.Push(buffer);

                UpdateMinCount(ref bucket, 0);
            }
            else
            {
                UpdateMinCount(ref bucket, 1);
            }
        }

        /// <summary>
        /// Resizes a byte array by returning the old one to the pool and fetching (or creating) a new one of the specified size.
        /// </summary>
        /// <param name="buffer">Buffer to resize.</param>
        /// <param name="newSize">New size for the buffer.</param>
        /// <param name="canBeLarger">If <c>true</c>, the new buffer can be larger than the specified size.</param>
        /// <param name="clear">If <c>true</c>, the new buffer will be cleared (set to all zeros).</param>
        /// <param name="context">Optional context for logging purposes.</param>
        /// <returns>A resized buffer.</returns>
        public static byte[] Resize(ref byte[] buffer, int newSize, bool canBeLarger, bool clear, LoggingContext context = null)
        {
            if (!_isEnabled)
            {
                Array.Resize<byte>(ref buffer, newSize);
                return buffer;
            }

            byte[] newBuf = BufferPool.Get(newSize, canBeLarger, context);
            if (buffer != null)
            {
                if (!clear)
                    Array.Copy(buffer, 0, newBuf, 0, Math.Min(newBuf.Length, buffer.Length));
                BufferPool.Release(buffer);
            }

            if (clear)
                Array.Clear(newBuf, 0, newSize);

            return buffer = newBuf;
        }

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
        public static KeyValuePair<byte[], Tracker>[] GetBorrowedBuffers() => _trackers.ToArray();
#endif

#if BESTHTTP_PROFILE
        public static void GetStatistics(ref BufferPoolStats stats)
        {
            //using (new ReadLock(rwLock))
            stats.GetBuffers = Interlocked.Read(ref GetBuffers);
            stats.ReleaseBuffers = Interlocked.Read(ref ReleaseBuffers);
            stats.PoolSize = Interlocked.Read(ref PoolSize);
            stats.MinBufferSize = MIN_BUFFER_SIZE;
            stats.MaxBufferSize = MAX_BUFFER_SIZE;
            stats.MaxPoolSize = Interlocked.Read(ref MaxPoolSize);

            stats.Borrowed = Interlocked.Read(ref Borrowed);
            stats.ArrayAllocations = Interlocked.Read(ref ArrayAllocations);

            /*stats.FreeBufferCount = FreeBuffers.Count;
                if (stats.FreeBufferStats == null)
                    stats.FreeBufferStats = new List<BufferStats>(FreeBuffers.Count);
                else
                    stats.FreeBufferStats.Clear();

                for (int i = 0; i < FreeBuffers.Count; ++i)
                {
                    BufferStore store = FreeBuffers[i];
                    List<BufferDesc> buffers = store.buffers;

                    BufferStats bufferStats = new BufferStats();
                    bufferStats.Size = store.Size;
                    bufferStats.Count = buffers.Count;

                    stats.FreeBufferStats.Add(bufferStats);
            }*/

            stats.NextMaintenance = (lastMaintenance + RunMaintenanceEvery) - DateTime.UtcNow;
        }
#endif

        /// <summary>
        /// Clears all stored entries in the buffer pool instantly, releasing memory.
        /// </summary>
        public static void Clear()
        {
            var buckets = _buckets;
            for (int i = 0; i < _buckets.Length; ++i)
            {
                ref Bucket bucket = ref buckets[i];
                bucket.Items.Clear();
                Interlocked.Exchange(ref bucket.FastItem, null);
            }

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
            ClearTrackers();
#endif

            Interlocked.Exchange(ref PoolSize, 0);
        }

#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
        private static void ClearTrackers()
        {
            var all = _trackers.ToArray();
            foreach (var item in all)
                _trackers.Remove(item.Key);
            _trackers.Clear();
        }
#endif

        /// <summary>
        /// Internal method called by the plugin to remove old, non-used buffers.
        /// </summary>
        internal static void Maintain()
        {
            DateTime now = DateTime.UtcNow;
            if (!_isEnabled || lastMaintenance + RunMaintenanceEvery > now)
                return;

            lastMaintenance = now;

            var buckets = _buckets;
            for (int i = 0; i < buckets.Length; ++i)
            {
                ref Bucket bucket = ref buckets[i];

                // Size of the bucket, already negated.
                int bucketSize = -(int)(MIN_BUFFER_SIZE << i);
                var remove = Interlocked.Exchange(ref bucket.MinCount, int.MaxValue);
                int removed = 0;

                for (int counter = 0; counter < remove && bucket.Items.TryPop(out var _); ++counter)
                {
                    Interlocked.Add(ref PoolSize, bucketSize);
                    removed++;
                }

                // Remove FastItem too, when it wasn't used in a full maintainence round
                if (remove == int.MaxValue && Interlocked.Exchange(ref bucket.FastItem, null) != null)
                    Interlocked.Add(ref PoolSize, bucketSize);
            }
        }

        private static void UpdateMinCount(ref Bucket bucket, int fastItem)
        {
            int newMinCount = bucket.Items.Count + fastItem;
            int oldMinCount = bucket.MinCount;
            while (newMinCount < oldMinCount && Interlocked.CompareExchange(ref bucket.MinCount, newMinCount, oldMinCount) != oldMinCount)
                oldMinCount = bucket.MinCount;
        }

        #region Helper functions

        public static bool IsPowerOfTwo(long x) => (x & (x - 1)) == 0;

        public static long NextPowerOf2(long x)
        {
            long pow = 1;
            while (pow <= x)
                pow *= 2;
            return pow;
        }

        [ThreadStatic]
        private static System.Text.StringBuilder stacktraceBuilder;
        public static string ProcessStackTrace(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
                return string.Empty;

            stacktraceBuilder ??= new System.Text.StringBuilder();

            var lines = stackTrace.Split('\n');

            for (int i = 0; i < lines.Length; ++i)
            {
                var line = lines[i];
                if (!line.Contains("Shared.PlatformSupport.Memory.Tracker..ctor") &&
                    !line.Contains(".Memory.BufferPool") &&
                    !line.Contains("Environment") &&
                    !line.Contains("System.Threading"))
                    stacktraceBuilder.Append(line.Replace("Best.HTTP.", ""));
            }

            return stacktraceBuilder.ToString();
        }

        [BurstCompile]
        private static int GetIdx(long po2)
        {
            if (IsBmi1Supported)
                return (int)tzcnt_u64((ulong)(po2 >> firstIdx));
            /*else if (IsNeonSupported)
            {
                v64 v = new v64((uint)po2, (uint)po2);
                var result = vclz_s32(v);
                return (int)result.UInt0;
            }*/

            ulong a = (ulong)po2;
            ulong c = 64;
            a &= (ulong)-(long)(a);
            if (a != 0) c--;
            if ((a & 0x00000000FFFFFFFF) != 0) c -= 32;
            if ((a & 0x0000FFFF0000FFFF) != 0) c -= 16;
            if ((a & 0x00FF00FF00FF00FF) != 0) c -= 8;
            if ((a & 0x0F0F0F0F0F0F0F0F) != 0) c -= 4;
            if ((a & 0x3333333333333333) != 0) c -= 2;
            if ((a & 0x5555555555555555) != 0) c -= 1;
            return (int)c;
        }

        #endregion
    }
}
