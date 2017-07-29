﻿// Copyright (c) Alexander Bogarsukov.
// Licensed under the MIT license. See the LICENSE.md file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Threading;

namespace UnityFx.Async
{
	/// <summary>
	/// An operation that finishes when any of the child operations finish.
	/// </summary>
	internal sealed class AsyncOperationWhenAny<T> : AsyncResult<T>, IAsyncOperationContainer
	{
		#region data

		private readonly List<IAsyncOperation<T>> _ops;
		private readonly AsyncContinuationOptions _options;

		#endregion

		#region interface

		public AsyncOperationWhenAny(IAsyncOperation<T>[] ops, CancellationToken cancellationToken, AsyncContinuationOptions options)
			: base(null, cancellationToken)
		{
			_ops = new List<IAsyncOperation<T>>(ops.Length);
			_options = options;

			foreach (var op in ops)
			{
				if (op != null)
				{
					_ops.Add(op);
				}
			}
		}

		#endregion

		#region AsyncResult

		protected override void OnUpdate()
		{
			var allCompleted = true;

			foreach (var op in _ops)
			{
				if (op.IsCompleted)
				{
					if (IsCompletedWithOptions(op, _options))
					{
						SetResult(op.Result);
						return;
					}
				}
				else
				{
					if (op is IAsyncOperation asyncOp)
					{
						if (asyncOp.Progress > Progress)
						{
							SetProgress(asyncOp.Progress);
						}
					}

					allCompleted = false;
				}
			}

			if (allCompleted)
			{
				SetException(null);
			}
		}

		#endregion

		#region IAsyncOperationContainer

		public int Size => 1;

		#endregion

		#region implementation
		#endregion
	}
}
