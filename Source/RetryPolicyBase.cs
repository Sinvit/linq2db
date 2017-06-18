﻿// BASEDON: https://github.com/aspnet/EntityFramework/blob/dev/src/EFCore/Storage/ExecutionStrategy.cs

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using JetBrains.Annotations;

namespace LinqToDB
{
	public abstract class RetryPolicyBase : IRetryPolicy
	{
		/// <summary>
		///     The default number of retry attempts.
		/// </summary>
		protected static readonly int DefaultMaxRetryCount = 5;

		/// <summary>
		///     The default maximum time delay between retries, must be nonnegative.
		/// </summary>
		protected static readonly TimeSpan DefaultMaxDelay = TimeSpan.FromSeconds(30);

		/// <summary>
		///     The default maximum random factor, must not be lesser than 1.
		/// </summary>
		private const double DefaultRandomFactor = 1.1;

		/// <summary>
		///     The default base for the exponential function used to compute the delay between retries, must be positive.
		/// </summary>
		private const double DefaultExponentialBase = 2;

		/// <summary>
		///     The default coefficient for the exponential function used to compute the delay between retries, must be nonnegative.
		/// </summary>
		private static readonly TimeSpan _defaultCoefficient = TimeSpan.FromSeconds(1);

		/// <summary>
		///   Creates a new instance of <see cref="RetryPolicyBase" />.
		/// </summary>
		/// <param name="maxRetryCount"> The maximum number of retry attempts. </param>
		/// <param name="maxRetryDelay"> The maximum delay in milliseconds between retries. </param>
		protected RetryPolicyBase(
			int maxRetryCount,
			TimeSpan maxRetryDelay)
		{
			if (maxRetryCount < 0)
				throw new ArgumentOutOfRangeException("maxRetryCount");
			if (maxRetryDelay.TotalMilliseconds < 0.0)
				throw new ArgumentOutOfRangeException("maxRetryDelay");

			MaxRetryCount = maxRetryCount;
			MaxRetryDelay = maxRetryDelay;
			ExceptionsEncountered = new List<Exception>();
			Random = new Random();
		}

		/// <summary>
		///     The list of exceptions that caused the operation to be retried so far.
		/// </summary>
		protected virtual List<Exception> ExceptionsEncountered { get; private set; }

		/// <summary>
		///     A pseudo-random number generater that can be used to vary the delay between retries.
		/// </summary>
		protected virtual Random Random { get; private set; }

		/// <summary>
		///     The maximum number of retry attempts.
		/// </summary>
		protected virtual int MaxRetryCount { get; private set; }

		/// <summary>
		///     The maximum delay in milliseconds between retries.
		/// </summary>
		protected virtual TimeSpan MaxRetryDelay { get; private set; }

		[ThreadStatic]
		private static volatile bool _suspended;

		/// <summary>
		///     Indicates whether the strategy is suspended. The strategy is typically suspending while executing to avoid
		///     recursive execution from nested operations.
		/// </summary>
		protected static bool Suspended
		{
			get { return _suspended; }
			set { _suspended = value; }
		}

		/// <summary>
		///     Executes the specified operation and returns the result.
		/// </summary>
		/// <param name="operation">
		///     A delegate representing an executable operation that returns the result of type <typeparamref name="TResult" />.
		/// </param>
		/// <typeparam name="TResult"> The return type of <paramref name="operation" />. </typeparam>
		/// <returns> The result from the operation. </returns>
		public virtual TResult Execute<TResult>(Func<TResult> operation)
		{
			if (Suspended)
				return operation();

			OnFirstExecution();

			return ExecuteImplementation(operation);
		}

		private TResult ExecuteImplementation<TResult>(Func<TResult> operation)
		{
			while (true)
			{
				TimeSpan? delay;
				try
				{
					Suspended = true;
					var result = operation();
					Suspended = false;
					return result;
				}
				catch (Exception ex)
				{
					Suspended = false;
					if (!CallOnWrappedException(ex, ShouldRetryOn))
						throw;

					ExceptionsEncountered.Add(ex);

					delay = GetNextDelay(ex);
					if (delay == null)
						throw new RetryLimitExceededException(ex);

					OnRetry();
				}

				using (var waitEvent = new ManualResetEventSlim(false))
				{
					waitEvent.WaitHandle.WaitOne(delay.Value);
				}
			}
		}

		/// <summary>
		///     Executes the specified asynchronous operation and returns the result.
		/// </summary>
		/// <param name="operation">
		///     A function that returns a started task of type <typeparamref name="TResult" />.
		/// </param>
		/// <param name="cancellationToken">
		///     A cancellation token used to cancel the retry operation, but not operations that are already in flight
		///     or that already completed successfully.
		/// </param>
		/// <typeparam name="TResult"> The result type of the <see cref="Task{T}" /> returned by <paramref name="operation" />. </typeparam>
		/// <returns>
		///     A task that will run to completion if the original task completes successfully (either the
		///     first time or after retrying transient failures). If the task fails with a non-transient error or
		///     the retry limit is reached, the returned task will become faulted and the exception must be observed.
		/// </returns>
		/// <exception cref="RetryLimitExceededException">
		///     Thrown if the operation has not succeeded after the configured number of retries.
		/// </exception>
		public virtual Task<TResult> ExecuteAsync<TResult>(
			Func<CancellationToken, Task<TResult>> operation,
			CancellationToken cancellationToken = default(CancellationToken))
		{
			if (Suspended)
				return operation(cancellationToken);

			OnFirstExecution();
			return ExecuteImplementationAsync(operation, cancellationToken);
		}

		private async Task<TResult> ExecuteImplementationAsync<TResult>(
			Func<CancellationToken, Task<TResult>> operation,
			CancellationToken cancellationToken)
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();

				TimeSpan? delay;
				try
				{
					Suspended = true;
					var result = await operation(cancellationToken);
					Suspended = false;
					return result;
				}
				catch (Exception ex)
				{
					Suspended = false;

					if (!CallOnWrappedException(ex, ShouldRetryOn))
						throw;

					ExceptionsEncountered.Add(ex);

					delay = GetNextDelay(ex);
					if (delay == null)
						throw new RetryLimitExceededException(ex);

					OnRetry();
				}

				await Task.Delay(delay.Value, cancellationToken);
			}
		}

		/// <summary>
		///     Method called before the first operation execution
		/// </summary>
		protected virtual void OnFirstExecution()
		{
			ExceptionsEncountered.Clear();
		}

		/// <summary>
		///     Method called before retrying the operation execution
		/// </summary>
		protected virtual void OnRetry()
		{}

		/// <summary>
		///     Determines whether the operation should be retried and the delay before the next attempt.
		/// </summary>
		/// <param name="lastException"> The exception thrown during the last execution attempt. </param>
		/// <returns>
		///     Returns the delay indicating how long to wait for before the next execution attempt if the operation should be retried;
		///     <c>null</c> otherwise
		/// </returns>
		protected virtual TimeSpan? GetNextDelay([NotNull] Exception lastException)
		{
			var currentRetryCount = ExceptionsEncountered.Count - 1;
			if (currentRetryCount < MaxRetryCount)
			{
				var delta = (Math.Pow(DefaultExponentialBase, currentRetryCount) - 1.0)
				            * (1.0 + Random.NextDouble() * (DefaultRandomFactor - 1.0));

				var delay = Math.Min(
					_defaultCoefficient.TotalMilliseconds * delta,
					MaxRetryDelay.TotalMilliseconds);

				return TimeSpan.FromMilliseconds(delay);
			}

			return null;
		}

		/// <summary>
		///     Determines whether the specified exception represents a transient failure that can be compensated by a retry.
		/// </summary>
		/// <param name="exception"> The exception object to be verified. </param>
		/// <returns>
		///     <c>true</c> if the specified exception is considered as transient, otherwise <c>false</c>.
		/// </returns>
		protected abstract bool ShouldRetryOn([NotNull] Exception exception);

		/// <summary>
		///     Recursively gets InnerException from <paramref name="exception" /> as long as it is an
		///     exception created by Entity Framework and calls <paramref name="exceptionHandler" /> on the innermost one.
		/// </summary>
		/// <param name="exception"> The exception to be unwrapped. </param>
		/// <param name="exceptionHandler"> A delegate that will be called with the unwrapped exception. </param>
		/// <typeparam name="TResult"> The return type of <paramref name="exceptionHandler" />. </typeparam>
		/// <returns>
		///     The result from <paramref name="exceptionHandler" />.
		/// </returns>
		public static TResult CallOnWrappedException<TResult>(
			[NotNull] Exception exception,
			[NotNull] Func<Exception, TResult> exceptionHandler)
		{
			return exceptionHandler(exception);
		}
	}
}