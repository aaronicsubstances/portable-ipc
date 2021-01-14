﻿using ScalableIPC.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace ScalableIPC.Core.Session.Abstractions
{
    public interface IStandardSessionHandler: ISessionHandler
    {
        // Intended to enable testing. Maps to the same classes in production usage.
        ISendHandlerAssistant CreateSendHandlerAssistant();
        IRetrySendHandlerAssistant CreateRetrySendHandlerAssistant();
        IReceiveHandlerAssistant CreateReceiveHandlerAssistant();

        int SessionState { get; set; }

        // Rules for window id changes are:
        //  - First window id must be 0. 
        //  - Receiver usually accepts only next ids larger than last received window id.
        //  - The only exception is that after 9E18, receiver must receive a next starting from 1
        //  - In any case increments must be less than one thousand (1000).
        // By so doing receiver can be conservative, and sender can have 
        // freedom in varying trend of window ids.
        long NextWindowIdToSend { get; set; }
        long LastWindowIdReceived { get; set; }
        int LastMaxSeqReceived { get; set; }
        void IncrementNextWindowIdToSend();
        bool IsSendInProgress();

        int? RemoteIdleTimeout { get; set; }

        void ResetIdleTimeout();

        void ResetAckTimeout(int timeout, Action cb);
        void CancelAckTimeout();
        void InitiateDispose(SessionDisposedException cause);
        void InitiateDispose(SessionDisposedException cause, PromiseCompletionSource<VoidType> promiseCb);
        void ContinueDispose(SessionDisposedException cause);

        // application layer interface. contract here is that these should be scheduled on event loop.
        Action<ISessionHandler, ProtocolDatagram> DatagramDiscardedHandler { get; set; }
        Action<ISessionHandler, ProtocolMessage> MessageReceivedHandler { get; set; }
        Action<ISessionHandler, SessionDisposedException> SessionDisposingHandler { get; set; }
        Action<ISessionHandler, SessionDisposedException> SessionDisposedHandler { get; set; }
        void OnDatagramDiscarded(ProtocolDatagram datagram);
        void OnMessageReceived(ProtocolMessage message);
        void OnSessionDisposing(SessionDisposedException cause);
        void OnSessionDisposed(SessionDisposedException cause);
    }
}