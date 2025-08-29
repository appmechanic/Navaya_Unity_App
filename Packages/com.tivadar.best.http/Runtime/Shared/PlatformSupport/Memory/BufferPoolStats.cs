#if BESTHTTP_PROFILE
using System;
using System.Collections.Generic;

namespace Best.HTTP.Shared.PlatformSupport.Memory
{
    public struct BufferStats
    {
        public long Size;
        public int Count;
    }

    public struct BufferPoolStats
    {
        public long GetBuffers;
        public long ReleaseBuffers;
        public long PoolSize;
        public long MaxPoolSize;
        public long MinBufferSize;
        public long MaxBufferSize;

        public long Borrowed;
        public long ArrayAllocations;

        public int FreeBufferCount;
        public List<BufferStats> FreeBufferStats;

        public TimeSpan NextMaintenance;
    }
}
#endif
