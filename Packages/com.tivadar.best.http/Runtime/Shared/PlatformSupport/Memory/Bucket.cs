using System.Collections.Concurrent;

namespace Best.HTTP.Shared.PlatformSupport.Memory
{
    internal struct Bucket
    {
#if UNITY_EDITOR
        /// <summary>
        /// Size the Bucket is associated with. Serves mostly debug purposes.
        /// </summary>
        public readonly int Size;
#endif

        /// <summary>
        /// What was Items' minimum Count between two checks.
        /// </summary>
        public int MinCount;

        /// <summary>
        /// Direct access to a buffer, without going throug ConcurrentStack's pop/push logic.
        /// </summary>
        public byte[] FastItem;
        public readonly ConcurrentStack<byte[]> Items;

        public Bucket(int size)
        {
#if UNITY_EDITOR
            this.Size = size;
#endif
            this.FastItem = null;
            this.Items = new ConcurrentStack<byte[]>();
            this.MinCount = int.MaxValue;
        }

#if UNITY_EDITOR
        public override string ToString() => $"[{Size}, {(FastItem != null ? "y" : "n")}, {Items.Count}, {MinCount}]";
#endif
    }
}
