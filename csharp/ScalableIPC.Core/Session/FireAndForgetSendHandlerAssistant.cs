﻿using ScalableIPC.Core.Session.Abstractions;
using System;
using System.Collections.Generic;
using System.Text;

namespace ScalableIPC.Core.Session
{
    public class FireAndForgetSendHandlerAssistant: IFireAndForgetSendHandlerAssistant
    {
        private readonly IStandardSessionHandler _sessionHandler;

        public FireAndForgetSendHandlerAssistant(IStandardSessionHandler sessionHandler)
        {
            _sessionHandler = sessionHandler;
        }

        public ProtocolDatagram MessageToSend { get; set; }
        public Action SuccessCallback { get; set; }
        public Action<ProtocolOperationException> ErrorCallback { get; set; }
        public bool IsComplete { get; private set; } = false;
        public bool Sent { get; private set; }

        public void Start()
        {
            if (IsComplete)
            {
                throw new Exception("Cannot reuse cancelled handler");
            }

            Sent = false;

            MessageToSend.WindowId = _sessionHandler.NextWindowIdToSend;
            MessageToSend.SequenceNumber = 0;

            _sessionHandler.NetworkApi.RequestSend(_sessionHandler.RemoteEndpoint, MessageToSend, null, e =>
            {
                if (e == null)
                {
                    HandleSendSuccess();
                }
                else
                {
                    HandleSendError(e);
                }
            });
            Sent = true;
        }

        public void Cancel()
        {
            IsComplete = true;
        }

        private void HandleSendSuccess()
        {
            _sessionHandler.PostEventLoopCallback(() =>
            {
                // check if not needed or arriving too late.
                if (IsComplete || !Sent)
                {
                    // send success callback received too late
                    return;
                }

                IsComplete = true;
                _sessionHandler.IncrementNextWindowIdToSend();
                SuccessCallback.Invoke();
            }, null);
        }

        private void HandleSendError(Exception error)
        {
            _sessionHandler.PostEventLoopCallback(() =>
            {
                // check if not needed or arriving too late.
                if (IsComplete || !Sent)
                {
                    // send error callback received too late
                    return;
                }

                IsComplete = true;
                _sessionHandler.IncrementNextWindowIdToSend();
                ErrorCallback.Invoke(new ProtocolOperationException(error));
            }, null);
        }
    }
}
