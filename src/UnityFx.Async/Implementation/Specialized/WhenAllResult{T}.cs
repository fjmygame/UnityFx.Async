﻿// Copyright (c) Alexander Bogarsukov.
// Licensed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;

namespace UnityFx.Async
{
	internal class WhenAllResult<T> : AsyncResult<T[]>
	{
		#region data

		private readonly IAsyncOperation[] _ops;
		private int _count;

		#endregion

		#region interface

		public WhenAllResult(IAsyncOperation[] ops)
			: base(AsyncOperationStatus.Running)
		{
			_ops = ops;
			_count = ops.Length;

			foreach (var op in ops)
			{
				op.AddCompletionCallback(this, null);
			}
		}

		#endregion

		#region AsyncResult

		protected override float GetProgress()
		{
			var result = 0f;

			foreach (var op in _ops)
			{
				result += op.Progress;
			}

			return result / _ops.Length;
		}

		protected override void OnCancel()
		{
			foreach (var op in _ops)
			{
				op.Cancel();
			}
		}

		#endregion

		#region IAsyncContinuation

		public override void Invoke(IAsyncOperation asyncOp)
		{
			if (IsCompleted)
			{
				return;
			}

			if (Interlocked.Decrement(ref _count) == 0)
			{
				List<Exception> exceptions = null;
				IAsyncOperation canceledOp = null;

				foreach (var op in _ops)
				{
					if (op.IsFaulted)
					{
						if (exceptions == null)
						{
							exceptions = new List<Exception>() { op.Exception };
						}
					}
					else if (op.IsCanceled)
					{
						canceledOp = op;
					}
				}

				if (exceptions != null)
				{
#if NET35
					TrySetException(exceptions[0]);
#else
					TrySetException(new AggregateException(exceptions));
#endif
				}
				else if (canceledOp != null)
				{
					TrySetCanceled();
				}
				else if (typeof(T) == typeof(VoidResult))
				{
					TrySetCompleted();
				}
				else
				{
					var results = new List<T>(_ops.Length);

					foreach (var op in _ops)
					{
						if (op is IAsyncOperation<T> rop)
						{
							results.Add(rop.Result);
						}
					}

					TrySetResult(results.ToArray());
				}
			}
			else
			{
				ReportProgress();
			}
		}

		#endregion
	}
}
