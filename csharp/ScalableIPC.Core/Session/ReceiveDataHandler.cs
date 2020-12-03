﻿using ScalableIPC.Core.Abstractions;
using System;
using System.Collections.Generic;

namespace ScalableIPC.Core.Session
{
    public class ReceiveDataHandler : ISessionStateHandler
    {
        private readonly ISessionHandler _sessionHandler;
        private ReceiveHandlerAssistant _currentWindowHandler;

        public ReceiveDataHandler(ISessionHandler sessionHandler)
        {
            _sessionHandler = sessionHandler;
        }

        public bool SendInProgress
        {
            get
            {
                return false;
            }
        }

        public void Shutdown(Exception error)
        {
            // nothing to do
        }

        public bool ProcessReceive(ProtocolDatagram message)
        {
            // check opcode.
            if (message.OpCode != ProtocolDatagram.OpCodeData)
            {
                return false;
            }

            _sessionHandler.Log("cdd5a60c-239d-440d-b7cb-03516c9ed818", message,
                "Pdu accepted for processing in receive pdu handler");
            OnReceiveRequest(message);
            return true;
        }

        public bool ProcessSend(ProtocolDatagram message, PromiseCompletionSource<VoidType> promiseCb)
        {
            return false;
        }

        public bool ProcessClose(bool closeGracefully, PromiseCompletionSource<VoidType> promiseCb)
        {
            return false;
        }

        private void OnReceiveRequest(ProtocolDatagram message)
        {
            if (_sessionHandler.SessionState >= DefaultSessionHandler.StateClosing)
            {
                // Session handler is closing so don't receive data.
                return;
            }

            if (_currentWindowHandler == null)
            {
                _currentWindowHandler = new ReceiveHandlerAssistant(_sessionHandler)
                {
                    AckOpCode = ProtocolDatagram.OpCodeAck,
                    SuccessCallback = OnWindowReceiveSuccess
                };
            }
            _currentWindowHandler.OnReceive(message);
        }

        private void OnWindowReceiveSuccess(List<ProtocolDatagram> currentWindow)
        {
            _currentWindowHandler = null;
            
            // ready to pass on to application layer, unless input has been shutdown
            // in which case silently ignore.
            if (_sessionHandler.IsInputShutdown())
            {
                return;
            }

            ProtocolDatagram windowAsMessage = ProtocolDatagram.CreateMessageOutOfWindow(currentWindow);
            ProcessCurrentWindowOptions(windowAsMessage.Options);

            _sessionHandler.Log("85b3284a-7787-4949-a8de-84211f91e154",
                "Successfully received full window of data",
                "count", currentWindow.Count, "sessionState", _sessionHandler.SessionState,
                "remoteIdleTimeout", _sessionHandler.RemoteIdleTimeoutSecs,
                "sessionState", _sessionHandler.SessionState);

            // should be called from event loop.
            _sessionHandler.EventLoop.PostCallback(() => _sessionHandler.OnMessageReceived(
                new MessageReceivedEventArgs { Message = windowAsMessage }));
        }

        private void ProcessCurrentWindowOptions(ProtocolDatagramOptions windowOptions)
        {
            if (windowOptions?.IdleTimeoutSecs != null)
            {
                _sessionHandler.RemoteIdleTimeoutSecs = windowOptions.IdleTimeoutSecs;
            }
        }
    }
}
