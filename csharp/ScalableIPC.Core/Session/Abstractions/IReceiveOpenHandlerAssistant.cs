﻿using System;
using System.Collections.Generic;
using System.Text;

namespace ScalableIPC.Core.Session.Abstractions
{
    public interface IReceiveOpenHandlerAssistant
    {
        Action<ProtocolDatagram> DataCallback { get; set; }
        Action<ProtocolOperationException> ErrorCallback { get; set; }

        void OnReceive(ProtocolDatagram datagram);
        void Cancel();
    }
}