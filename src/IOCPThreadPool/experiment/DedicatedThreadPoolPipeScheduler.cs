﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ThePool.Experiment
{
    /// <summary>
    /// An implementation of a pipe-scheduler that uses a dedicated pool of threads, deferring to
    /// the thread-pool if that becomes too backlogged
    /// </summary>
    /// <remarks>
    /// 摘录自Pipelines.Sockets.Unofficial，略加修改；
    /// 此类型侧重于提供一个完整的线程池，稍作改动也可以搞一个limit concurrency level的出来
    /// </remarks>
    public sealed class DedicatedThreadPoolPipeScheduler : PipeScheduler, IDisposable
    {
        /// <summary>
        /// Reusable shared scheduler instance
        /// </summary>
        public static DedicatedThreadPoolPipeScheduler Default => StaticContext.Instance;

        private static class StaticContext
        {   // locating here rather than as a static field on DedicatedThreadPoolPipeScheduler so that it isn't instantiated too eagerly
            internal static readonly DedicatedThreadPoolPipeScheduler Instance = new DedicatedThreadPoolPipeScheduler(nameof(Default));
        }

        [ThreadStatic]
        private static int s_threadWorkerPoolId;
        private static int s_nextWorkerPoolId;

        /// <summary>
        /// Indicates whether the current thread is a worker, optionally for the specific pool
        /// (otherwise for any pool)
        /// </summary>
        public static bool IsWorker(DedicatedThreadPoolPipeScheduler pool = null)
            => pool == null ? s_threadWorkerPoolId != 0 : s_threadWorkerPoolId == pool.Id;

        private int Id { get; }

        /// <summary>
        /// The name of the pool
        /// </summary>
        public override string ToString() => Name;

        /// <summary>
        /// The number of workers associated with this pool
        /// </summary>
        public int WorkerCount { get; }

        private int UseThreadPoolQueueLength { get; }

        private ThreadPriority Priority { get; }

        private string Name { get; }

        /// <summary>
        /// Create a new dedicated thread-pool
        /// </summary>
        public DedicatedThreadPoolPipeScheduler(string name = null, int workerCount = 5, int useThreadPoolQueueLength = 10,
            ThreadPriority priority = ThreadPriority.Normal)
        {
            if (workerCount < 0)
                throw new ArgumentNullException(nameof(workerCount));

            do { Id = Interlocked.Increment(ref s_nextWorkerPoolId); }
            while (Id == 0); // in case of roll-around; unlikely, though

            WorkerCount = workerCount;
            UseThreadPoolQueueLength = useThreadPoolQueueLength;
            if (string.IsNullOrWhiteSpace(name)) name = GetType().Name;
            Name = name.Trim();
            Priority = priority;
            for (int i = 0; i < workerCount; i++)
            {
                StartWorker(i);
            }
        }

        private long _totalServicedByQueue, _totalServicedByPool;

        /// <summary>
        /// The total number of operations serviced by the queue
        /// </summary>
        public long TotalServicedByQueue => Volatile.Read(ref _totalServicedByQueue);

        /// <summary>
        /// The total number of operations that could not be serviced by the queue, but which were sent to the thread-pool instead
        /// </summary>
        public long TotalServicedByPool => Volatile.Read(ref _totalServicedByPool);

        private readonly struct WorkItem
        {
            public readonly Action<object> Action;
            public readonly object State;
            public WorkItem(Action<object> action, object state)
            {
                Action = action;
                State = state;
            }
        }

        private volatile bool _disposed;

        private readonly Queue<WorkItem> _queue = new Queue<WorkItem>();
        private void StartWorker(int id)
        {
            var thread = new Thread(ThreadRunWorkLoop)
            {
                Name = $"{Name}:{id}",
                Priority = Priority,
                IsBackground = true
            };
            thread.Start(this);
        }

        /// <summary>
        /// Requests <paramref name="action"/> to be run on scheduler with <paramref name="state"/> being passed in
        /// </summary>
        public override void Schedule(Action<object> action, object state)
        {
            if (action == null) return; // nothing to do
            lock (_queue)
            {
                // 说明：
                // 此处要两个条件同时都 不 满 足 才会执行if中的语句，
                // 所以当queue的count < 10，为什么这里10个往上走就需要fallback to thread-pool了？
                if (!(_disposed | _queue.Count >= UseThreadPoolQueueLength))
                {
                    _queue.Enqueue(new WorkItem(action, state));
                    if (_availableCount != 0)
                    {
                        Monitor.Pulse(_queue); // wake up someone
                    }
                    return;
                }
            }

            // fallback to thread-pool
            ThreadPool.Schedule(action, state);
        }

        private static readonly ParameterizedThreadStart ThreadRunWorkLoop = state => ((DedicatedThreadPoolPipeScheduler)state).RunWorkLoop();

        private int _availableCount;
        /// <summary>
        /// The number of workers currently actively engaged in work
        /// </summary>
        public int AvailableCount => Thread.VolatileRead(ref _availableCount);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Execute(Action<object> action, object state)
        {
            try
            {
                action(state);
            }
            catch (Exception ex)
            {
                Console.WriteLine(Name, ex.Message);
            }
        }

        private void RunWorkLoop()
        {
            s_threadWorkerPoolId = Id;
            try
            {
                while (true)
                {
                    WorkItem next;
                    lock (_queue)
                    {
                        while (_queue.Count == 0)
                        {
                            if (_disposed) break;
                            _availableCount++;
                            Monitor.Wait(_queue);
                            _availableCount--;
                        }
                        if (_queue.Count == 0)
                        {
                            if (_disposed) break;
                            else continue;
                        }
                        next = _queue.Dequeue();
                    }
                    Interlocked.Increment(ref _totalServicedByQueue);
                    Execute(next.Action, next.State);
                }
            }
            catch (Exception ex)
            {
                Debug.Write(ex.Message);
            }
            finally
            {
                s_threadWorkerPoolId = 0;
            }
        }
        /// <summary>
        /// Release the threads associated with this pool; if additional work is requested, it will
        /// be sent to the main thread-pool
        /// </summary>
        public void Dispose()
        {
            _disposed = true;
            lock (_queue)
            {
                Monitor.PulseAll(_queue);
            }
        }
    }
}
