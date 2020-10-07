﻿using System;

namespace PortableIPC.Core
{
    /// <summary>
    /// Promise API design is to take common functionality of NodeJS Promises, C#.NET Core Tasks, and
    /// Java 8 CompletableFuture.
    /// 
    /// 1. Promises automatically unwrap in NodeJs. Equivalent are
    ///  - c# task.unwrap
    ///  - java 8 completablefuture.thenComposeAsync
    ///  Conclusion: don't automatically unwrap, instead be explicit about it.
    ///  
    /// 2. Task cancellation. NodeJS Promises doesn't have Cancel API, but Java and C# do
    ///  - fortunately cancellation is needed only for timeout
    ///  Conclusion: Have a Cancel API which works only for timeouts
    /// 
    /// 3. Rejection handlers in NodeJS can return values and continue like no error happened.
    ///  - not so in C#. an error in async-await keyword usage results in an exception 
    ///  Conclusion: only accept exceptions in rejection handlers, but allow them to return values.
    /// </summary>
    public interface AbstractPromiseApi
    {
        AbstractPromise<T> Create<T>(PromiseExecutorCallback<T> code);
        AbstractPromiseOnHold<T> CreateOnHold<T>();
        AbstractPromise<T> Resolve<T>(T value);
        AbstractPromise<VoidType> Reject(Exception reason);

        object ScheduleTimeout(long millis, StoredCallback cb);
        void CancelTimeout(object id);
    }

    public interface AbstractPromise<out T>
    {
        AbstractPromise<U> Then<U>(Func<T, U> onFulfilled, Action<Exception> onRejected = null);
        AbstractPromise<U> ThenCompose<U>(Func<T, AbstractPromise<U>> onFulfilled,
            Func<Exception, AbstractPromise<U>> onRejected = null);
        T Sync();
    }

    public delegate void PromiseExecutorCallback<out T>(Action<T> resolve, Action<Exception> reject);

    public interface AbstractPromiseOnHold<T>
    {
        AbstractPromise<T> Extract();
        void CompleteSuccessfully(T value);
        void CompleteExceptionally(Exception error);
    }

    public class StoredCallback
    {
        public StoredCallback(Action<object> callback, object arg = default)
        {
            Callback = callback;
            Arg = arg;
        }

        public Action<object> Callback { get; }
        public object Arg { get; }
        public void Run()
        {
            Callback.Invoke(Arg);
        }
    }
}
