﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace PortableIPC.Core.Abstractions
{
    public interface ISessionHandlerFactory
    {
        ISessionHandler Create(IPEndPoint endpoint, Guid sessionId);
    }
}
