﻿#if DEBUG
#define EXTREME_LOGGING
#endif

namespace Catel.Threading
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Threading;
    using System.Threading.Tasks;
    using Catel.Logging;

    /// <summary>
    /// A mutual exclusion lock that is compatible with async.
    /// </summary>
    /// <remarks>
    /// This code originally comes from AsyncEx: https://github.com/StephenCleary/AsyncEx
    /// </remarks>
    [DebuggerDisplay("Id = {Id}, Taken = {_taken}")]
    [DebuggerTypeProxy(typeof(DebugView))]
    public sealed class AsyncLock
    {
        private static readonly ILog Log = LogManager.GetCurrentClassLogger();

        private readonly Stack<Task<IDisposable>> _cachedKeyTasks;
        private readonly Task<IDisposable> _cacheKeyReleaseTask;

        /// <summary>
        /// The object used for mutual exclusion.
        /// </summary>
        private readonly object _mutex;

        private readonly IAsyncWaitQueue<IDisposable> _queue;

        /// <summary>
        /// The semi-unique identifier for this instance. This is 0 if the id has not yet been created.
        /// </summary>
        private readonly int _id = UniqueIdentifierHelper.GetUniqueIdentifier<AsyncLock>();

        private readonly AsyncLocal<bool> _takenByCurrentTask = new AsyncLocal<bool>();

        private bool _taken;
        private bool _allowTakeoverByTask;

        /// <summary>
        /// Creates a new async-compatible mutual exclusion lock.
        /// </summary>
        public AsyncLock()
            : this(new DefaultAsyncWaitQueue<IDisposable>())
        {
        }

        /// <summary>
        /// Creates a new async-compatible mutual exclusion lock using the specified wait queue.
        /// </summary>
        /// <param name="queue">The wait queue used to manage waiters.</param>
        public AsyncLock(IAsyncWaitQueue<IDisposable> queue)
        {
            _queue = queue;
            _cachedKeyTasks = new Stack<Task<IDisposable>>();
            _cacheKeyReleaseTask = Task.FromResult<IDisposable>(new Key(this));
            _mutex = new object();
        }

        /// <summary>
        /// Gets a semi-unique identifier for this asynchronous lock.
        /// </summary>
        public int Id
        {
            get { return _id; }
        }

        /// <summary>
        /// Gets a value indicating whether this lock is taken.
        /// </summary>
        /// <value><c>true</c> if this lock is taken; otherwise, <c>false</c>.</value>
        public bool IsTaken
        {
            get
            {
                lock (_mutex)
                {
                    return _taken;
                }
            }
        }

        /// <summary>
        /// Gets a value indicating whether the current task has taken this lock.
        /// </summary>
        public bool IsTakenByCurrentTask
        {
            get
            {
                lock (_mutex)
                {
                    return _takenByCurrentTask.Value;
                }
            }
        }

        /// <summary>
        /// Asynchronously acquires the lock. Returns a disposable that releases the lock when disposed.
        /// </summary>
        /// <returns>A disposable that releases the lock when disposed.</returns>
        public AwaitableDisposable<IDisposable> LockAsync()
        {
            return LockAsync(CancellationToken.None);
        }

        /// <summary>
        /// Asynchronously acquires the lock. Returns a disposable that releases the lock when disposed.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
        /// <returns>A disposable that releases the lock when disposed.</returns>
        public AwaitableDisposable<IDisposable> LockAsync(CancellationToken cancellationToken)
        {
            Task<IDisposable> ret;

#if EXTREME_LOGGING
            Log.Debug($"[{_id}] [ASYNC] Requesting lock");
#endif

            lock (_mutex)
            {
                if (!_taken || _allowTakeoverByTask || _takenByCurrentTask.Value)
                {
#if EXTREME_LOGGING
                    Log.Debug($"[{_id}] [ASYNC] Lock was not yet taken, taking lock");
#endif

                    // If the lock is available, take it immediately.
                    _taken = true;
                    _takenByCurrentTask.Value = true;
                    _allowTakeoverByTask = false;

                    _cachedKeyTasks.Push(_cacheKeyReleaseTask);
                    ret = _cacheKeyReleaseTask;
                }
                else
                {
#if EXTREME_LOGGING
                    Log.Debug($"[{_id}] [ASYNC] Lock was already taken, queueing lock request");
#endif

                    // Wait for the lock to become available or cancellation.
                    ret = _queue.EnqueueAsync(_mutex, () =>
                    {
                        _allowTakeoverByTask = true;
                    }, cancellationToken);
                }
            }

            return new AwaitableDisposable<IDisposable>(ret);
        }

        /// <summary>
        /// Synchronously acquires the lock. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
        /// </summary>
        public IDisposable Lock()
        {
            return Lock(CancellationToken.None);
        }

        /// <summary>
        /// Synchronously acquires the lock. Returns a disposable that releases the lock when disposed. This method may block the calling thread.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token used to cancel the lock. If this is already set, then this method will attempt to take the lock immediately (succeeding if the lock is currently available).</param>
        public IDisposable Lock(CancellationToken cancellationToken)
        {
            Task<IDisposable> enqueuedTask;

#if EXTREME_LOGGING
            Log.Debug($"[{_id}] [SYNC] Requesting lock");
#endif

            lock (_mutex)
            {
                if (!_taken || _allowTakeoverByTask || _takenByCurrentTask.Value)
                {
#if EXTREME_LOGGING
                    Log.Debug($"[{_id}] [SYNC] Lock was not yet taken, taking lock");
#endif

                    _taken = true;
                    _takenByCurrentTask.Value = true;
                    _allowTakeoverByTask = false;

                    _cachedKeyTasks.Push(_cacheKeyReleaseTask);
                    return _cacheKeyReleaseTask.Result;
                }

                enqueuedTask = _queue.EnqueueAsync(_mutex, () =>
                {
                    _allowTakeoverByTask = true;
                }, cancellationToken);
            }

            return enqueuedTask.WaitAndUnwrapException();
        }

        /// <summary>
        /// Releases the lock.
        /// </summary>
        internal void ReleaseLock()
        {
            IDisposable? queuedLocker = null;

#if EXTREME_LOGGING
            Log.Debug($"[{_id}] [SYNC] Releasing lock");
#endif

            lock (_mutex)
            {
                // Step 1: clear current task locks
                if (_cachedKeyTasks.Count > 0)
                {
#if EXTREME_LOGGING
                    Log.Debug($"[{_id}] [SYNC] Releasing cached key lock");
#endif

                    var finish = _cachedKeyTasks.Pop();

                    // Important: dispose the task result, not the task itself
                    finish?.Result.Dispose();
                }

                // Step 2: check if there are pending locks left
                if (_cachedKeyTasks.Count == 0)
                {
                    _takenByCurrentTask.Value = false;

                    if (!_queue.IsEmpty)
                    {
#if EXTREME_LOGGING
                        Log.Debug($"[{_id}] [SYNC] Queue is not yet empty, dequeueing next");
#endif

                        queuedLocker = _queue.Dequeue(_cacheKeyReleaseTask);
                    }
                    else
                    {
#if EXTREME_LOGGING
                        Log.Debug($"[{_id}] [SYNC] Lock has no pending requests left, now fully free");
#endif

                        // No lock and no queue, fully free
                        _taken = false;
                    }
                }
            }

            // Outside scope to allow new locks to be taken
            if (queuedLocker is not null)
            {
#if EXTREME_LOGGING
                Log.Debug($"[{_id}] [SYNC] Disposing queued locker");
#endif

                queuedLocker.Dispose();
            }
        }

        /// <summary>
        /// The disposable which releases the lock.
        /// </summary>
        private sealed class Key : IDisposable
        {
            /// <summary>
            /// The lock to release.
            /// </summary>
            private readonly AsyncLock _asyncLock;

            /// <summary>
            /// Creates the key for a lock.
            /// </summary>
            /// <param name="asyncLock">The lock to release. May not be <c>null</c>.</param>
            public Key(AsyncLock asyncLock)
            {
                _asyncLock = asyncLock;
            }

            /// <summary>
            /// Release the lock.
            /// </summary>
            public void Dispose()
            {
#if EXTREME_LOGGING
                Log.Debug($"[{_asyncLock._id}] Releasing key");
#endif

                _asyncLock.ReleaseLock();
            }
        }

        // ReSharper disable UnusedMember.Local
        [DebuggerNonUserCode]
        private sealed class DebugView
        {
            private readonly AsyncLock _mutex;

            public DebugView(AsyncLock mutex)
            {
                _mutex = mutex;
            }

            public int Id
            {
                get { return _mutex.Id; }
            }

            public bool Taken
            {
                get { return _mutex._taken; }
            }

            public IAsyncWaitQueue<IDisposable> WaitQueue
            {
                get { return _mutex._queue; }
            }
        }

        // ReSharper restore UnusedMember.Local
    }
}
