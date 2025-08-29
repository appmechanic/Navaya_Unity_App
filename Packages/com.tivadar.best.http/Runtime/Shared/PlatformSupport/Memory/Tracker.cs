#if BESTHTTP_ENABLE_BUFFERPOOL_BORROW_CHECKER
using Best.HTTP.Shared.Logger;

using System;

namespace Best.HTTP.Shared.PlatformSupport.Memory
{
    public sealed class Tracker : IDisposable
    {
        public string Stack => this._stack;
        public LoggingContext Context => this._context;

        private readonly string _stack;
        private readonly LoggingContext _context;
        private bool _disposed;

        public Tracker(LoggingContext context)
        {
            this._stack = BufferPool.ProcessStackTrace(Environment.StackTrace);
            this._context = context;
        }

        public void Dispose()
        {
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        ~Tracker()
        {
            if (!_disposed && !Environment.HasShutdownStarted)
                HTTPManager.Logger.Error("BufferPool", $"Buffer Leaked! Borrowed at: {_stack}", this._context);
        }
    }
}

#endif
