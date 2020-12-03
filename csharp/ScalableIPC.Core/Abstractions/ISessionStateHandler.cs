﻿using System;
using System.Collections.Generic;

namespace ScalableIPC.Core.Abstractions
{
    public interface ISessionStateHandler
    {
        bool SendInProgress { get; }
        void Shutdown(Exception error);
        bool ProcessReceive(ProtocolDatagram message);
        bool ProcessSend(ProtocolDatagram message, PromiseCompletionSource<VoidType> promiseCb);
        bool ProcessClose(bool closeGracefully, PromiseCompletionSource<VoidType> promiseCb);
    }
}