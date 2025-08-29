using Best.HTTP.Shared.Logger;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Best.HTTP.Shared.Extensions
{
    public sealed class RunOnceOnMainThread : IHeartbeat
    {
        private Action _action;
        private int _subscribed;
        private LoggingContext _context;

        public RunOnceOnMainThread(Action action, LoggingContext context)
        {
            this._action = action;
            this._context = context;
        }

        public void Subscribe()
        {
            if (Interlocked.CompareExchange(ref this._subscribed, 1, 0) == 0)
                HTTPManager.Heartbeats.Subscribe(this);
        }

        public void OnHeartbeatUpdate(DateTime now, TimeSpan dif)
        {
            try
            {
                this._action?.Invoke();
            }
            catch (Exception ex)
            {
                HTTPManager.Logger.Exception(nameof(RunOnceOnMainThread.OnHeartbeatUpdate), $"{nameof(_action)}", ex, this._context);
            }
            finally
            {
                HTTPManager.Heartbeats.Unsubscribe(this);
            }
        }
    }

    public interface IHeartbeat
    {
        void OnHeartbeatUpdate(DateTime utcNow, TimeSpan dif);
    }

    enum HeartbeatUpdateEventType
    {
        Subscribe,
        Unsubscribe,
        Clear
    }

    struct HeartbeatUpdateEvent
    {
        public HeartbeatUpdateEventType Event;
        public IHeartbeat Heartbeat;
    }

    /// <summary>
    /// A manager class that can handle subscribing and unsubscribeing in the same update.
    /// </summary>
    public sealed class HeartbeatManager
    {
        private List<IHeartbeat> _heartbeats = new List<IHeartbeat>();
        private DateTime _lastUpdate = DateTime.MinValue;

        private ConcurrentQueue<HeartbeatUpdateEvent> _updates = new();

        public void Subscribe(IHeartbeat heartbeat)
        {
            this._updates.Enqueue(new HeartbeatUpdateEvent { Event = HeartbeatUpdateEventType.Subscribe, Heartbeat = heartbeat });
        }

        public void Unsubscribe(IHeartbeat heartbeat)
        {
            this._updates.Enqueue(new HeartbeatUpdateEvent { Event = HeartbeatUpdateEventType.Unsubscribe, Heartbeat = heartbeat });
        }

        public void Update()
        {
            var now = HTTPManager.CurrentFrameDateTime;

            if (_lastUpdate == DateTime.MinValue)
                _lastUpdate = now;
            else
            {
                TimeSpan dif = now - _lastUpdate;
                _lastUpdate = now;
                
                while (this._updates.TryDequeue(out var updateEvent))
                {
                    switch (updateEvent.Event)
                    {
                        case HeartbeatUpdateEventType.Subscribe: this._heartbeats.Add(updateEvent.Heartbeat); break;
                        case HeartbeatUpdateEventType.Unsubscribe: this._heartbeats.Remove(updateEvent.Heartbeat); break;
                        case HeartbeatUpdateEventType.Clear: this._heartbeats.Clear(); break;
                    }
                }

                for (int i = 0; i < this._heartbeats.Count; ++i)
                {
                    var heartbeat = this._heartbeats[i];
                    try
                    {
                        heartbeat.OnHeartbeatUpdate(now, dif);
                    }
                    catch (Exception ex)
                    {
                        HTTPManager.Logger.Exception("HeartbeatManager", heartbeat.GetType().Name, ex, null);
                    }
                }
            }
        }

        public void Clear()
        {
            this._updates.Enqueue(new HeartbeatUpdateEvent { Event = HeartbeatUpdateEventType.Clear, Heartbeat = null });
        }
    }
}
