﻿using ScalableIPC.Core.Abstractions;
using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;

namespace ScalableIPC.Core
{
    public class EndpointConfig
    {
        public IPEndPoint LocalEndpoint { get; set; }
        public int IdleTimeoutSecs { get; set; }
        public int AckTimeoutSecs { get; set; }
        public int MaxSendWindowSize { get; set; }
        public int MaxReceiveWindowSize { get; set; }
        public int MaxRetryCount { get; set; }
        public int MaximumTransferUnitSize { get; set; }
        public ISessionHandlerFactory SessionHandlerFactory { get; set; }
    }
}
