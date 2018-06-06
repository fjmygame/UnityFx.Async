﻿// Copyright (c) Alexander Bogarsukov.
// Licensed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
#if !NET35
using System.Runtime.ExceptionServices;
#endif
using System.Threading;

namespace UnityFx.Async
{
	/// <summary>
	/// A lightweight <c>net35</c>-compatible asynchronous operation for <c>Unity3d</c>.
	/// </summary>
	/// <remarks>
	/// <para>This class is the core entity of the library. In many aspects it mimics <c>Task</c>
	/// interface and behaviour. For example, any <see cref="AsyncResult"/> instance can have any
	/// number of continuations (added either explicitly via <c>TryAddCompletionCallback</c>
	/// call or implicitly using <c>async</c>/<c>await</c> keywords). These continuations can be
	/// invoked on a captured <see cref="SynchronizationContext"/>. The class inherits <see cref="IAsyncResult"/>
	/// (just like <c>Task</c>) and can be used to implement Asynchronous Programming Model (APM).
	/// There are operation state accessors that can be used exactly like corresponding properties of <c>Task</c>.
	/// </para>
	/// <para>The class implements <see cref="IDisposable"/> interface. So strictly speaking <see cref="Dispose()"/>
	/// should be called when the operation is no longed in use. In practice that is only required
	/// if <see cref="AsyncWaitHandle"/> property was used. Also keep in mind that <see cref="Dispose()"/>
	/// implementation is not thread-safe.
	/// </para>
	/// <para>Please note that while the class is designed as a lightweight and portable <c>Task</c>-like object,
	/// it's NOT a replacement for .NET <c>Task</c>. It is recommended to use <c>Task</c> in general and only switch
	/// to this class if Unity/net35 compatibility is a concern.
	/// </para>
	/// </remarks>
	/// <seealso href="http://www.what-could-possibly-go-wrong.com/promises-for-game-development/">Promises for game development</seealso>
	/// <seealso href="https://blogs.msdn.microsoft.com/nikos/2011/03/14/how-to-implement-the-iasyncresult-design-pattern/">How to implement the IAsyncResult design pattern</seealso>
	/// <seealso href="https://docs.microsoft.com/en-us/dotnet/standard/parallel-programming/task-based-asynchronous-programming">Task-based Asynchronous Pattern (TAP)</seealso>
	/// <seealso href="https://docs.microsoft.com/en-us/dotnet/standard/asynchronous-programming-patterns/asynchronous-programming-model-apm">Asynchronous Programming Model (APM)</seealso>
	/// <seealso href="https://docs.microsoft.com/en-us/dotnet/api/system.threading.tasks.task">Task</seealso>
	/// <seealso href="https://docs.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskcompletionsource-1">TaskCompletionSource</seealso>
	/// <seealso cref="AsyncCompletionSource"/>
	/// <seealso cref="AsyncResult{T}"/>
	/// <seealso cref="IAsyncResult"/>
	[DebuggerDisplay("{DebuggerDisplay,nq}")]
	public partial class AsyncResult : IAsyncOperation, IEnumerator
	{
		#region data

		private const int _flagCompletionReserved = 0x00010000;
		private const int _flagCompleted = 0x00020000;
		private const int _flagSynchronous = 0x00040000;
		private const int _flagCompletedSynchronously = _flagCompleted | _flagCompletionReserved | _flagSynchronous;
		private const int _flagCancellationRequested = 0x00100000;
		private const int _flagDisposed = 0x00200000;

		private const int _flagDoNotDispose = OptionDoNotDispose << _optionsOffset;
		private const int _flagRunContinuationsAsynchronously = OptionRunContinuationsAsynchronously << _optionsOffset;
		private const int _flagSuppressCancellation = OptionSuppressCancellation << _optionsOffset;

		private const int _statusMask = 0x0000000f;
		private const int _optionsMask = 0x70000000;
		private const int _optionsOffset = 28;

		private readonly object _asyncState;
		private Exception _exception;
		private EventWaitHandle _waitHandle;
		private volatile int _flags;

		#endregion

		#region interface

		/// <summary>
		/// Gets the <see cref="AsyncCreationOptions"/> used to create this operation.
		/// </summary>
		/// <value>The operation creation options.</value>
		public AsyncCreationOptions CreationOptions => (AsyncCreationOptions)(_flags >> _optionsOffset);

		/// <summary>
		/// Gets a value indicating whether the operation instance is disposed.
		/// </summary>
		/// <value>The disposed flag.</value>
		protected bool IsDisposed => (_flags & _flagDisposed) != 0;

		/// <summary>
		/// Gets a value indicating whether the operation cancellation was requested.
		/// </summary>
		/// <value>The cancellation request flag.</value>
		protected bool IsCancellationRequested => (_flags & _flagCancellationRequested) != 0;

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class.
		/// </summary>
		public AsyncResult()
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class with the specified <see cref="CreationOptions"/>.
		/// </summary>
		/// <param name="options">The <see cref="AsyncCreationOptions"/> used to customize the operation's behavior.</param>
		public AsyncResult(AsyncCreationOptions options)
			: this((int)options << _optionsOffset)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class.
		/// </summary>
		/// <param name="asyncCallback">User-defined completion callback.</param>
		/// <param name="asyncState">User-defined data returned by <see cref="AsyncState"/>.</param>
		public AsyncResult(AsyncCallback asyncCallback, object asyncState)
		{
			_asyncState = asyncState;
			_callback = asyncCallback;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class.
		/// </summary>
		/// <param name="asyncCallback">User-defined completion callback.</param>
		/// <param name="asyncState">User-defined data returned by <see cref="AsyncState"/>.</param>
		/// <param name="options">The <see cref="AsyncCreationOptions"/> used to customize the operation's behavior.</param>
		public AsyncResult(AsyncCallback asyncCallback, object asyncState, AsyncCreationOptions options)
			: this((int)options << _optionsOffset)
		{
			_asyncState = asyncState;
			_callback = asyncCallback;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class with the specified <see cref="Status"/>.
		/// </summary>
		/// <param name="status">Initial value of the <see cref="Status"/> property.</param>
		public AsyncResult(AsyncOperationStatus status)
			: this((int)status)
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class with the specified <see cref="Status"/> and <see cref="CreationOptions"/>.
		/// </summary>
		/// <param name="status">Initial value of the <see cref="Status"/> property.</param>
		/// <param name="options">The <see cref="AsyncCreationOptions"/> used to customize the operation's behavior.</param>
		public AsyncResult(AsyncOperationStatus status, AsyncCreationOptions options)
			: this((int)status | ((int)options << _optionsOffset))
		{
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class with the specified <see cref="Status"/>.
		/// </summary>
		/// <param name="status">Initial value of the <see cref="Status"/> property.</param>
		/// <param name="asyncState">User-defined data returned by <see cref="AsyncState"/>.</param>
		public AsyncResult(AsyncOperationStatus status, object asyncState)
			: this((int)status)
		{
			_asyncState = asyncState;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class with the specified <see cref="Status"/> and <see cref="CreationOptions"/>.
		/// </summary>
		/// <param name="status">Initial value of the <see cref="Status"/> property.</param>
		/// <param name="asyncState">User-defined data returned by <see cref="AsyncState"/>.</param>
		/// <param name="options">The <see cref="AsyncCreationOptions"/> used to customize the operation's behavior.</param>
		public AsyncResult(AsyncOperationStatus status, object asyncState, AsyncCreationOptions options)
			: this((int)status | ((int)options << _optionsOffset))
		{
			_asyncState = asyncState;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class with the specified <see cref="Status"/>.
		/// </summary>
		/// <param name="status">Initial value of the <see cref="Status"/> property.</param>
		/// <param name="asyncCallback">User-defined completion callback.</param>
		/// <param name="asyncState">User-defined data returned by <see cref="AsyncState"/>.</param>
		public AsyncResult(AsyncOperationStatus status, AsyncCallback asyncCallback, object asyncState)
			: this((int)status)
		{
			_asyncState = asyncState;
			_callback = asyncCallback;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class with the specified <see cref="Status"/> and <see cref="CreationOptions"/>.
		/// </summary>
		/// <param name="status">Initial value of the <see cref="Status"/> property.</param>
		/// <param name="asyncCallback">User-defined completion callback.</param>
		/// <param name="asyncState">User-defined data returned by <see cref="AsyncState"/>.</param>
		/// <param name="options">The <see cref="AsyncCreationOptions"/> used to customize the operation's behavior.</param>
		public AsyncResult(AsyncOperationStatus status, AsyncCallback asyncCallback, object asyncState, AsyncCreationOptions options)
			: this((int)status | ((int)options << _optionsOffset))
		{
			_asyncState = asyncState;
			_callback = asyncCallback;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class that is faulted. For internal use only.
		/// </summary>
		/// <param name="exception">The exception to complete the operation with.</param>
		/// <param name="asyncState">User-defined data returned by <see cref="AsyncState"/>.</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="exception"/> is <see langword="null"/>.</exception>
		internal AsyncResult(Exception exception, object asyncState)
		{
			_exception = exception ?? throw new ArgumentNullException(nameof(exception));

			if (_exception is OperationCanceledException)
			{
				_flags = StatusCanceled | _flagCompletedSynchronously;
			}
			else
			{
				_flags = StatusFaulted | _flagCompletedSynchronously;
			}

			_callback = _callbackCompletionSentinel;
			_asyncState = asyncState;
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="AsyncResult"/> class that is faulted. For internal use only.
		/// </summary>
		/// <param name="exceptions">Exceptions to complete the operation with.</param>
		/// <param name="asyncState">User-defined data returned by <see cref="AsyncState"/>.</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="exceptions"/> is <see langword="null"/>.</exception>
		internal AsyncResult(IEnumerable<Exception> exceptions, object asyncState)
		{
			if (exceptions == null)
			{
				throw new ArgumentNullException(nameof(exceptions));
			}

			_exception = new AggregateException(exceptions);

			if (_exception.InnerException is OperationCanceledException)
			{
				_flags = StatusCanceled | _flagCompletedSynchronously;
			}
			else
			{
				_flags = StatusFaulted | _flagCompletedSynchronously;
			}

			_callback = _callbackCompletionSentinel;
			_asyncState = asyncState;
		}

		/// <summary>
		/// Transitions the operation into the <see cref="AsyncOperationStatus.Running"/> state.
		/// </summary>
		/// <exception cref="InvalidOperationException">Thrown if the transition has failed.</exception>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <seealso cref="TryStart"/>
		/// <seealso cref="TrySetRunning"/>
		/// <seealso cref="OnStarted"/>
		public void Start()
		{
			if (!TrySetRunning())
			{
				throw new InvalidOperationException();
			}
		}

		/// <summary>
		/// Attempts to transitions the operation into the <see cref="AsyncOperationStatus.Running"/> state.
		/// </summary>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <returns>Returns <see langword="true"/> if the operation status was changed to <see cref="AsyncOperationStatus.Running"/>; <see langword="false"/> otherwise.</returns>
		/// <seealso cref="Start"/>
		/// <seealso cref="TrySetRunning"/>
		/// <seealso cref="OnStarted"/>
		public bool TryStart()
		{
			return TrySetRunning();
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.Scheduled"/> state.
		/// </summary>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <seealso cref="TrySetRunning"/>
		protected internal bool TrySetScheduled()
		{
			ThrowIfDisposed();

			if (TrySetStatus(StatusScheduled))
			{
				return true;
			}

			return false;
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.Running"/> state.
		/// </summary>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <seealso cref="TrySetScheduled"/>
		protected internal bool TrySetRunning()
		{
			ThrowIfDisposed();

			if (TrySetStatus(StatusRunning))
			{
				OnStarted();
				return true;
			}

			return false;
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.Canceled"/> state.
		/// </summary>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <returns>Returns <see langword="true"/> if the attemp was successfull; <see langword="false"/> otherwise.</returns>
		/// <seealso cref="TrySetCanceled(bool)"/>
		protected internal bool TrySetCanceled()
		{
			return TrySetCanceled(false);
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.Canceled"/> state.
		/// </summary>
		/// <param name="completedSynchronously">Value of the <see cref="CompletedSynchronously"/> property.</param>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <returns>Returns <see langword="true"/> if the attemp was successfull; <see langword="false"/> otherwise.</returns>
		/// <seealso cref="TrySetCanceled()"/>
		protected internal bool TrySetCanceled(bool completedSynchronously)
		{
			ThrowIfDisposed();

			if (TryReserveCompletion())
			{
				_exception = new OperationCanceledException();
				SetCompleted(StatusCanceled, completedSynchronously);
				return true;
			}
			else if (!IsCompleted)
			{
				AsyncExtensions.SpinUntilCompleted(this);
			}

			return false;
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.Faulted"/> (or <see cref="AsyncOperationStatus.Canceled"/>
		/// if the exception is <see cref="OperationCanceledException"/>) state.
		/// </summary>
		/// <param name="exception">An exception that caused the operation to end prematurely.</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="exception"/> is <see langword="null"/>.</exception>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <returns>Returns <see langword="true"/> if the attemp was successfull; <see langword="false"/> otherwise.</returns>
		/// <seealso cref="TrySetException(Exception, bool)"/>
		protected internal bool TrySetException(Exception exception)
		{
			return TrySetException(exception, false);
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.Faulted"/> (or <see cref="AsyncOperationStatus.Canceled"/>
		/// if the exception is <see cref="OperationCanceledException"/>) state.
		/// </summary>
		/// <param name="exception">An exception that caused the operation to end prematurely.</param>
		/// <param name="completedSynchronously">Value of the <see cref="CompletedSynchronously"/> property.</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="exception"/> is <see langword="null"/>.</exception>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <returns>Returns <see langword="true"/> if the attemp was successfull; <see langword="false"/> otherwise.</returns>
		/// <seealso cref="TrySetException(Exception)"/>
		protected internal bool TrySetException(Exception exception, bool completedSynchronously)
		{
			ThrowIfDisposed();

			if (exception == null)
			{
				throw new ArgumentNullException(nameof(exception));
			}

			if (TryReserveCompletion())
			{
				if (exception is OperationCanceledException)
				{
					_exception = exception;
					SetCompleted(StatusCanceled, completedSynchronously);
				}
				else
				{
					_exception = exception;

					if (exception is AggregateException && _exception.InnerException is OperationCanceledException)
					{
						SetCompleted(StatusCanceled, completedSynchronously);
					}
					else
					{
						SetCompleted(StatusFaulted, completedSynchronously);
					}
				}

				return true;
			}
			else if (!IsCompleted)
			{
				AsyncExtensions.SpinUntilCompleted(this);
			}

			return false;
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.Faulted"/> state.
		/// </summary>
		/// <param name="exceptions">Exceptions that caused the operation to end prematurely.</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="exceptions"/> is <see langword="null"/>.</exception>
		/// <exception cref="ArgumentException">Thrown if <paramref name="exceptions"/> is empty.</exception>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <returns>Returns <see langword="true"/> if the attemp was successfull; <see langword="false"/> otherwise.</returns>
		/// <seealso cref="TrySetExceptions(IEnumerable{Exception}, bool)"/>
		protected internal bool TrySetExceptions(IEnumerable<Exception> exceptions)
		{
			return TrySetExceptions(exceptions, false);
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.Faulted"/> state.
		/// </summary>
		/// <param name="exceptions">Exceptions that caused the operation to end prematurely.</param>
		/// <param name="completedSynchronously">Value of the <see cref="CompletedSynchronously"/> property.</param>
		/// <exception cref="ArgumentNullException">Thrown if <paramref name="exceptions"/> is <see langword="null"/>.</exception>
		/// <exception cref="ArgumentException">Thrown if <paramref name="exceptions"/> is empty.</exception>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <returns>Returns <see langword="true"/> if the attemp was successfull; <see langword="false"/> otherwise.</returns>
		/// <seealso cref="TrySetExceptions(IEnumerable{Exception})"/>
		protected internal bool TrySetExceptions(IEnumerable<Exception> exceptions, bool completedSynchronously)
		{
			ThrowIfDisposed();

			if (exceptions == null)
			{
				throw new ArgumentNullException(nameof(exceptions));
			}

			var list = new List<Exception>();

			foreach (var e in exceptions)
			{
				if (e == null)
				{
					throw new ArgumentException(Constants.ErrorListElementIsNull, nameof(exceptions));
				}

				list.Add(e);
			}

			if (list.Count == 0)
			{
				throw new ArgumentException(Constants.ErrorListIsEmpty, nameof(exceptions));
			}

			if (TryReserveCompletion())
			{
				_exception = new AggregateException(list);

				if (_exception.InnerException is OperationCanceledException)
				{
					SetCompleted(StatusCanceled, completedSynchronously);
				}
				else
				{
					SetCompleted(StatusFaulted, completedSynchronously);
				}

				return true;
			}
			else if (!IsCompleted)
			{
				AsyncExtensions.SpinUntilCompleted(this);
			}

			return false;
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.RanToCompletion"/> state.
		/// </summary>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <returns>Returns <see langword="true"/> if the attemp was successfull; <see langword="false"/> otherwise.</returns>
		/// <seealso cref="TrySetCompleted(bool)"/>
		protected internal bool TrySetCompleted()
		{
			return TrySetCompleted(false);
		}

		/// <summary>
		/// Attempts to transition the operation into the <see cref="AsyncOperationStatus.RanToCompletion"/> state.
		/// </summary>
		/// <param name="completedSynchronously">Value of the <see cref="CompletedSynchronously"/> property.</param>
		/// <exception cref="ObjectDisposedException">Thrown is the operation is disposed.</exception>
		/// <returns>Returns <see langword="true"/> if the attemp was successfull; <see langword="false"/> otherwise.</returns>
		/// <seealso cref="TrySetCompleted()"/>
		protected internal bool TrySetCompleted(bool completedSynchronously)
		{
			ThrowIfDisposed();

			if (TrySetCompleted(StatusRanToCompletion, completedSynchronously))
			{
				return true;
			}
			else if (!IsCompleted)
			{
				AsyncExtensions.SpinUntilCompleted(this);
			}

			return false;
		}

		/// <summary>
		/// Reports changes in operation progress value.
		/// </summary>
		/// <returns>Returns <see langword="true"/> if the attemp was successfull; <see langword="false"/> otherwise.</returns>
		protected internal bool TryReportProgress()
		{
			var status = _flags & _statusMask;

			if (status == StatusRunning)
			{
				OnProgressChanged();
				InvokeProgressCallbacks();
				return true;
			}

			return false;
		}

		/// <summary>
		/// Throws exception if the operation has failed or canceled.
		/// </summary>
		protected internal void ThrowIfNonSuccess()
		{
			var status = _flags & _statusMask;

			if (status == StatusFaulted)
			{
				if (!TryThrowException(_exception))
				{
					// Should never get here. Exception should never be null in faulted state.
					throw new Exception();
				}
			}
			else if (status == StatusCanceled)
			{
				if (!TryThrowException(_exception))
				{
					throw new OperationCanceledException();
				}
			}
		}

		/// <summary>
		/// Throws <see cref="ObjectDisposedException"/> if this operation has been disposed.
		/// </summary>
		protected internal void ThrowIfDisposed()
		{
			if ((_flags & _flagDisposed) != 0)
			{
				throw new ObjectDisposedException(ToString());
			}
		}

		#endregion

		#region virtual interface

		/// <summary>
		/// Called when the progress is requested. Default implementation returns 0.
		/// </summary>
		/// <remarks>
		/// Make sure that each method call returns a value greater or equal to the previous. It is important for
		/// progress reporting consistency.
		/// </remarks>
		/// <seealso cref="Progress"/>
		/// <seealso cref="OnProgressChanged"/>
		/// <seealso cref="TryReportProgress"/>
		protected virtual float GetProgress()
		{
			return 0;
		}

		/// <summary>
		/// Called when the progress value has changed. Default implementation does nothing.
		/// </summary>
		/// <seealso cref="Progress"/>
		/// <seealso cref="GetProgress"/>
		/// <seealso cref="TryReportProgress"/>
		protected virtual void OnProgressChanged()
		{
		}

		/// <summary>
		/// Called when the operation state has changed. Default implementation does nothing.
		/// </summary>
		/// <param name="status">The new status value.</param>
		/// <seealso cref="Status"/>
		/// <seealso cref="TrySetScheduled"/>
		/// <seealso cref="TrySetRunning"/>
		/// <seealso cref="TrySetCanceled(bool)"/>
		/// <seealso cref="TrySetCompleted(bool)"/>
		/// <seealso cref="TrySetException(System.Exception, bool)"/>
		/// <seealso cref="TrySetExceptions(IEnumerable{System.Exception}, bool)"/>
		protected virtual void OnStatusChanged(AsyncOperationStatus status)
		{
		}

		/// <summary>
		/// Called when the operation is started (<see cref="Status"/> is set to <see cref="AsyncOperationStatus.Running"/>). Default implementation does nothing.
		/// </summary>
		/// <seealso cref="OnCompleted"/>
		/// <seealso cref="Status"/>
		/// <seealso cref="Start"/>
		/// <seealso cref="TryStart"/>
		/// <seealso cref="TrySetRunning"/>
		protected virtual void OnStarted()
		{
		}

		/// <summary>
		/// Called when the operation cancellation has been requested. Default implementation throws <see cref="NotSupportedException"/>.
		/// </summary>
		/// <seealso cref="Cancel"/>
		protected virtual void OnCancel()
		{
			throw new NotSupportedException();
		}

		/// <summary>
		/// Called when the operation is completed. Default implementation does nothing.
		/// </summary>
		/// <seealso cref="OnStarted"/>
		/// <seealso cref="Status"/>
		/// <seealso cref="TrySetCanceled(bool)"/>
		/// <seealso cref="TrySetCompleted(bool)"/>
		/// <seealso cref="TrySetException(System.Exception, bool)"/>
		/// <seealso cref="TrySetExceptions(IEnumerable{System.Exception}, bool)"/>
		protected virtual void OnCompleted()
		{
		}

		/// <summary>
		/// Releases unmanaged resources used by the object.
		/// </summary>
		/// <remarks>
		/// Unlike most of the members of <see cref="AsyncResult"/>, this method is not thread-safe.
		/// </remarks>
		/// <param name="disposing">A <see langword="bool"/> value that indicates whether this method is being called due to a call to <see cref="Dispose()"/>.</param>
		/// <seealso cref="Dispose()"/>
		/// <seealso cref="ThrowIfDisposed"/>
		protected virtual void Dispose(bool disposing)
		{
			if (disposing && (_flags & _flagDoNotDispose) == 0)
			{
				_flags |= _flagDisposed;

				if (_waitHandle != null)
				{
#if NET35
					_waitHandle.Close();
#else
					_waitHandle.Dispose();
#endif
					_waitHandle = null;
				}
			}
		}

		#endregion

		#region internals

		internal const int StatusCreated = 0;
		internal const int StatusScheduled = 1;
		internal const int StatusRunning = 2;
		internal const int StatusRanToCompletion = 3;
		internal const int StatusCanceled = 4;
		internal const int StatusFaulted = 5;

		internal const int OptionDoNotDispose = 1;
		internal const int OptionRunContinuationsAsynchronously = 2;
		internal const int OptionSuppressCancellation = 4;

		/// <summary>
		/// Special status setter for <see cref="AsyncOperationStatus.Scheduled"/> and <see cref="AsyncOperationStatus.Running"/>.
		/// </summary>
		internal bool TrySetStatus(int newStatus)
		{
			Debug.Assert(newStatus < StatusRanToCompletion);

			do
			{
				var flags = _flags;

				if ((flags & (_flagCompleted | _flagCompletionReserved)) != 0)
				{
					return false;
				}

				var status = flags & _statusMask;

				if (status >= newStatus)
				{
					return false;
				}

				var newFlags = (flags & ~_statusMask) | newStatus;

				if (Interlocked.CompareExchange(ref _flags, newFlags, flags) == flags)
				{
					OnStatusChanged((AsyncOperationStatus)newStatus);
					return true;
				}
			}
			while (true);
		}

		/// <summary>
		/// Sets the operation status to one of <see cref="AsyncOperationStatus.RanToCompletion"/>/<see cref="AsyncOperationStatus.Canceled"/>/<see cref="AsyncOperationStatus.Faulted"/>.
		/// The call does the same as calling <see cref="TryReserveCompletion"/> and <see cref="SetCompleted(int, bool)"/> but uses one interlocked operation instead of two.
		/// </summary>
		internal bool TrySetCompleted(int status, bool completedSynchronously)
		{
			Debug.Assert(status > StatusRunning);

			status |= _flagCompleted | _flagCompletionReserved;

			if (completedSynchronously)
			{
				status |= _flagSynchronous;
			}

			do
			{
				var flags = _flags;

				if ((flags & (_flagCompletionReserved | _flagCompleted)) != 0)
				{
					return false;
				}

				var newFlags = (flags & ~_statusMask) | status;

				if (Interlocked.CompareExchange(ref _flags, newFlags, flags) == flags)
				{
					NotifyCompleted((AsyncOperationStatus)status);
					return true;
				}
			}
			while (true);
		}

		/// <summary>
		/// Initiates operation completion. Should only be used in pair with <see cref="SetCompleted(int, bool)"/>.
		/// </summary>
		internal bool TryReserveCompletion()
		{
			do
			{
				var flags = _flags;

				if ((flags & (_flagCompletionReserved | _flagCompleted)) != 0)
				{
					return false;
				}

				if (Interlocked.CompareExchange(ref _flags, flags | _flagCompletionReserved, flags) == flags)
				{
					return true;
				}
			}
			while (true);
		}

		/// <summary>
		/// Attempts to add a new flag value.
		/// </summary>
		internal bool TrySetFlag(int newFlag)
		{
			do
			{
				var flags = _flags;

				if ((flags & (newFlag | _flagCompletionReserved | _flagCompleted)) != 0)
				{
					return false;
				}

				if (Interlocked.CompareExchange(ref _flags, flags | newFlag, flags) == flags)
				{
					return true;
				}
			}
			while (true);
		}

		/// <summary>
		/// Unconditionally sets the operation status to one of <see cref="AsyncOperationStatus.RanToCompletion"/>/<see cref="AsyncOperationStatus.Canceled"/>/<see cref="AsyncOperationStatus.Faulted"/>.
		/// Should only be called if <see cref="TryReserveCompletion"/> call succeeded.
		/// </summary>
		internal void SetCompleted(int status, bool completedSynchronously)
		{
			Debug.Assert(status > StatusRunning);
			Debug.Assert((_flags & _flagCompletionReserved) != 0);
			Debug.Assert((_flags & _statusMask) < StatusRanToCompletion);

			var oldFlags = _flags & ~_statusMask;
			var newFlags = status | _flagCompleted;

			if (completedSynchronously)
			{
				newFlags |= _flagSynchronous;
			}

			// Set completed status. After this line IsCompleted will return true.
			Interlocked.Exchange(ref _flags, oldFlags | newFlags);

			// Invoke completion callbacks.
			NotifyCompleted((AsyncOperationStatus)status);
		}

		/// <summary>
		/// Copies state of the specified operation.
		/// </summary>
		internal void CopyCompletionState(IAsyncOperation patternOp, bool completedSynchronously)
		{
			if (!TryCopyCompletionState(patternOp, completedSynchronously))
			{
				throw new InvalidOperationException();
			}
		}

		/// <summary>
		/// Attemts to copy state of the specified operation.
		/// </summary>
		internal bool TryCopyCompletionState(IAsyncOperation patternOp, bool completedSynchronously)
		{
			if (patternOp.IsCompletedSuccessfully)
			{
				return TrySetCompleted(completedSynchronously);
			}
			else if (patternOp.IsFaulted || patternOp.IsCanceled)
			{
				return TrySetException(patternOp.Exception, completedSynchronously);
			}

			return false;
		}

		/// <summary>
		/// Unconditionally reports the operation progress.
		/// </summary>
		internal void ReportProgress()
		{
			OnProgressChanged();
			InvokeProgressCallbacks();
		}

		/// <summary>
		/// Throws if the specified operation is faulted/canceled.
		/// </summary>
		internal static void ThrowIfNonSuccess(IAsyncOperation op)
		{
			var status = op.Status;

			if (status == AsyncOperationStatus.Faulted)
			{
				if (!TryThrowException(op.Exception))
				{
					// Should never get here. Exception should never be null in faulted state.
					throw new Exception();
				}
			}
			else if (status == AsyncOperationStatus.Canceled)
			{
				if (!TryThrowException(op.Exception))
				{
					throw new OperationCanceledException();
				}
			}
		}

		/// <summary>
		/// Rethrows the specified exception.
		/// </summary>
		internal static bool TryThrowException(Exception e)
		{
			if (e != null)
			{
#if NET35
				throw e;
#else
				ExceptionDispatchInfo.Capture(e).Throw();
#endif
			}

			return false;
		}

		#endregion

		#region IAsyncOperation

		/// <inheritdoc/>
		public float Progress
		{
			get
			{
				var status = _flags & _statusMask;

				if (status == StatusRanToCompletion)
				{
					return 1;
				}
				else if (status < StatusRunning)
				{
					return 0;
				}

				return GetProgress();
			}
		}

		/// <inheritdoc/>
		public AsyncOperationStatus Status => (AsyncOperationStatus)(_flags & _statusMask);

		/// <inheritdoc/>
		public Exception Exception => (_flags & _flagCompleted) != 0 ? _exception : null;

		/// <inheritdoc/>
		public bool IsCompletedSuccessfully => (_flags & _statusMask) == StatusRanToCompletion;

		/// <inheritdoc/>
		public bool IsFaulted => (_flags & _statusMask) == StatusFaulted;

		/// <inheritdoc/>
		public bool IsCanceled => (_flags & _statusMask) == StatusCanceled;

		#endregion

		#region IAsyncCancellable

		/// <inheritdoc/>
		public void Cancel()
		{
			if ((_flags & _flagSuppressCancellation) != 0)
			{
				return;
			}

			if (TrySetFlag(_flagCancellationRequested))
			{
				OnCancel();
			}
		}

		#endregion

		#region IAsyncResult

		/// <inheritdoc/>
		public WaitHandle AsyncWaitHandle
		{
			get
			{
				ThrowIfDisposed();

				if (_waitHandle == null)
				{
					var done = IsCompleted;
					var mre = new ManualResetEvent(done);

					if (Interlocked.CompareExchange(ref _waitHandle, mre, null) != null)
					{
						// Another thread created this object's event; dispose the event we just created.
#if NET35
						mre.Close();
#else
						mre.Dispose();
#endif
					}
					else if (!done && IsCompleted)
					{
						// We published the event as unset, but the operation has subsequently completed;
						// set the event state properly so that callers do not deadlock.
						_waitHandle.Set();
					}
				}

				return _waitHandle;
			}
		}

		/// <inheritdoc/>
		public object AsyncState => _asyncState;

		/// <inheritdoc/>
		public bool CompletedSynchronously => (_flags & _flagSynchronous) != 0;

		/// <inheritdoc/>
		public bool IsCompleted => (_flags & _flagCompleted) != 0;

		#endregion

		#region IEnumerator

		/// <inheritdoc/>
		object IEnumerator.Current => null;

		/// <inheritdoc/>
		bool IEnumerator.MoveNext() => _flags == StatusRunning;

		/// <inheritdoc/>
		void IEnumerator.Reset() => throw new NotSupportedException();

		#endregion

		#region IDisposable

		/// <summary>
		/// Disposes the <see cref="AsyncResult"/>, releasing all of its unmanaged resources.
		/// </summary>
		/// <remarks>
		/// Unlike most of the members of <see cref="AsyncResult"/>, this method is not thread-safe.
		/// Also, <see cref="Dispose()"/> may only be called on an <see cref="AsyncResult"/> that is in one of
		/// the final states: <see cref="AsyncOperationStatus.RanToCompletion"/>, <see cref="AsyncOperationStatus.Faulted"/> or
		/// <see cref="AsyncOperationStatus.Canceled"/>.
		/// </remarks>
		/// <exception cref="InvalidOperationException">Thrown if the operation is not completed.</exception>
		/// <seealso cref="Dispose(bool)"/>
		public void Dispose()
		{
			if (!IsCompleted)
			{
				throw new InvalidOperationException(Constants.ErrorOperationIsNotCompleted);
			}

			Dispose(true);
			GC.SuppressFinalize(this);
		}

		#endregion

		#region Object

		/// <inheritdoc/>
		public override string ToString()
		{
			return GetType().Name;
		}

		#endregion

		#region implementation

		private string DebuggerDisplay
		{
			get
			{
				var result = ToString();
				var status = Status;
				var state = status.ToString();

				if (IsFaulted && _exception != null)
				{
					state += " (" + _exception.GetType().Name + ')';
				}

				if (status == AsyncOperationStatus.Running)
				{
					state += ", Progress = " + GetProgress().ToString("N2");
				}

				result += ", Status = ";
				result += state;

				if (IsDisposed)
				{
					result += ", Disposed";
				}

				return result;
			}
		}

		private AsyncResult(int flags)
		{
			if (flags == StatusFaulted)
			{
				_exception = new Exception();
			}
			else if (flags == StatusCanceled)
			{
				_exception = new OperationCanceledException();
			}

			if (flags > StatusRunning)
			{
				_callback = _callbackCompletionSentinel;
				_flags = flags | _flagCompletedSynchronously;
			}
			else
			{
				_flags = flags;
			}
		}

		private void NotifyCompleted(AsyncOperationStatus status)
		{
			try
			{
				OnProgressChanged();
				OnStatusChanged(status);
				OnCompleted();

				InvokeCallbacks();
			}
			finally
			{
				_waitHandle?.Set();
			}
		}

		#endregion
	}
}
