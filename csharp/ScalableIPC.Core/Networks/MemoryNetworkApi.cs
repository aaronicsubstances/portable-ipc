﻿using ScalableIPC.Core.Abstractions;
using ScalableIPC.Core.Concurrency;
using ScalableIPC.Core.Helpers;
using ScalableIPC.Core.Networks.Common;
using ScalableIPC.Core.Session;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using static ScalableIPC.Core.Helpers.CustomLogEvent;

namespace ScalableIPC.Core.Networks
{
    public class MemoryNetworkApi : AbstractNetworkApi
    {
        public interface ISendBehaviour
        {
            SendConfig Create(GenericNetworkIdentifier remoteIdentifier, ProtocolDatagram datagram);
        }
        public class DefaultSendBehaviour: ISendBehaviour
        {
            public SendConfig Config { get; set; }
            public SendConfig Create(GenericNetworkIdentifier remoteIdentifier, ProtocolDatagram datagram)
            {
                return Config;
            }
        }
        public class SendConfig
        {
            public bool SerializeDatagram { get; set; }
            public int Delay { get; set; }
            public Exception Error { get; set; }
        }
        public interface ITransmissionBehaviour
        {
            TransmissionConfig Create(GenericNetworkIdentifier remoteIdentifier, ProtocolDatagram datagram);
        }
        public class DefaultTransmissionBehaviour: ITransmissionBehaviour
        {
            public TransmissionConfig Config { get; set; }
            public TransmissionConfig Create(GenericNetworkIdentifier remoteIdentifier, ProtocolDatagram datagram)
            {
                return Config;
            }
        }
        public class TransmissionConfig
        {
            public int[] Delays { get; set; }
        }

        internal static readonly string LogDataKeyIsReceiverThread = "isReceiverThread";

        private readonly SessionHandlerStore _sessionHandlerStore;
        private bool _isShuttingDown;
        private readonly object _isShuttingDownLock = new object();

        public MemoryNetworkApi()
        {
            _sessionHandlerStore = new SessionHandlerStore();
            PromiseApi = DefaultPromiseApi.Instance;
            ConnectedNetworks = new Dictionary<GenericNetworkIdentifier, MemoryNetworkApi>();
        }

        public AbstractPromiseApi PromiseApi { get; set; }
        public ISessionHandlerFactory SessionHandlerFactory { get; set; }
        public ISessionTaskExecutorGroup SessionTaskExecutorGroup { get; set; }

        public GenericNetworkIdentifier LocalEndpoint { get; set; }

        public Dictionary<GenericNetworkIdentifier, MemoryNetworkApi> ConnectedNetworks { get; set; }

        public ISendBehaviour SendBehaviour { get; set; }

        public ITransmissionBehaviour TransmissionBehaviour { get; set; }

        public AbstractPromise<VoidType> StartAsync()
        {
            // nothing to do.
            return PromiseApi.CompletedPromise();
        }

        public AbstractPromise<ISessionHandler> OpenSessionAsync(GenericNetworkIdentifier remoteEndpoint, string sessionId, 
            ISessionHandler sessionHandler)
        {
            try
            {
                if (IsShuttingDown())
                {
                    throw new Exception("Cannot start new session due to shutdown");
                }

                if (!ConnectedNetworks.ContainsKey(remoteEndpoint))
                {
                    throw new Exception($"{remoteEndpoint} remote endpoint not found.");
                }

                if (sessionId == null)
                {
                    sessionId = ProtocolDatagram.GenerateSessionId();
                }
                if (sessionHandler == null)
                {
                    if (SessionHandlerFactory == null)
                    {
                        throw new Exception("Must provide sessionHandler if SessionHandlerFactory is null");
                    }
                    sessionHandler = SessionHandlerFactory.Create();
                    if (sessionHandler == null)
                    {
                        throw new Exception("SessionHandlerFactory failed to create session handler");
                    }
                }
                lock (_sessionHandlerStore)
                {
                    _sessionHandlerStore.Add(remoteEndpoint, sessionId,
                        new SessionHandlerWrapper(sessionHandler));
                }
                sessionHandler.CompleteInit(sessionId, true, this, remoteEndpoint);
                return PromiseApi.Resolve(sessionHandler);
            }
            catch (Exception ex)
            {
                return PromiseApi.Reject<ISessionHandler>(ex);
            }
        }

        public void RequestSend(GenericNetworkIdentifier remoteEndpoint, ProtocolDatagram message, Action<Exception> cb)
        {
            // Fire outside of event loop thread if possible.
            var newLogicalThreadId = GenerateAndRecordLogicalThreadId(logEvent =>
            {
                logEvent.AddProperty(LogDataKeyIsReceiverThread, false);
            });
            Task.Run(() =>
            {
                var promise = PromiseApi.CompletedPromise()
                    .StartLogicalThread(newLogicalThreadId)
                    .ThenCompose(_ => _HandleSendAsync(remoteEndpoint, message))
                    .Then(_ =>
                    {
                        cb(null);
                        return VoidType.Instance;
                    })
                    .CatchCompose(ex =>
                    {
                        cb(ex);
                        return PromiseApi.CompletedPromise();
                    })
                    .CatchCompose(ex => RecordLogicalThreadException(
                        "1b554af7-6b87-448a-af9c-103d9c676030",
                        $"Error occured in callback processing during message send to {remoteEndpoint}",
                        ex))
                    .EndLogicalThread(() => RecordEndOfLogicalThread());
                return ((DefaultPromise<VoidType>)promise).WrappedTask;
            });
        }

        public AbstractPromise<VoidType> _HandleSendAsync(GenericNetworkIdentifier remoteEndpoint,
            ProtocolDatagram datagram)
        {
            // simulate sending.

            SendConfig sendConfig = null;
            if (SendBehaviour != null)
            {
                sendConfig = SendBehaviour.Create(remoteEndpoint, datagram);
            }

            // interpret null send config as immediate success.
            AbstractPromise<VoidType> sendResult = PromiseApi.CompletedPromise();
            byte[] serialized = null;
            if (sendConfig != null)
            {
                if (sendConfig.SerializeDatagram)
                {
                    // Simulate serialization
                    serialized = datagram.ToRawDatagram();
                }
                if (sendConfig.Delay > 0)
                {
                    sendResult = sendResult.ThenCompose(_ => PromiseApi.Delay(sendConfig.Delay));
                }
                if (sendConfig.Error != null)
                {
                    // don't proceed further
                    return sendResult.ThenCompose(_ => PromiseApi.Reject<VoidType>(sendConfig.Error));
                }
            }

            // done with simulating sending.

            // Simulate transmission delays and duplication of datagrams.
            // NB: null/empty array of transmission delays simulates dropping of datagrams.
            // multiple transmission delays simulates duplication of datagrams

            TransmissionConfig transmissionConfig = null;
            if (TransmissionBehaviour != null)
            {
                transmissionConfig = TransmissionBehaviour.Create(remoteEndpoint, datagram);
            }
            if (transmissionConfig == null)
            {
                // interpret as immediate transfer to connected network.
                transmissionConfig = new TransmissionConfig
                {
                    Delays = new int[] { 0 }
                };
            }

            // do nothing if delays are not specified.
            if (transmissionConfig.Delays == null)
            {
                return sendResult;
            }

            var connectedNetwork = ConnectedNetworks[remoteEndpoint];
            for (int i = 0; i < transmissionConfig.Delays.Length; i++)
            {
                // capture usage of index i before entering closure
                int transmissionDelay = transmissionConfig.Delays[i];
                var newLogicalThreadId = GenerateAndRecordLogicalThreadId(logEvent =>
                {
                    logEvent.AddProperty(LogDataKeyIsReceiverThread, true);
                });
                Task.Run(() => {
                    var transmissionResult = PromiseApi.CompletedPromise()
                        .StartLogicalThread(newLogicalThreadId)
                        .ThenCompose(_ =>
                        {
                            if (transmissionDelay > 0)
                            {
                                return PromiseApi.Delay(transmissionDelay);
                            }
                            else
                            {
                                return PromiseApi.CompletedPromise();
                            }
                        })
                        .Then(_ =>
                        {
                            if (serialized == null)
                            {
                                return datagram;
                            }
                            ProtocolDatagram deserialized = ProtocolDatagram.Parse(serialized, 0, serialized.Length);
                            return deserialized;
                        })
                        .ThenCompose(deserialized => connectedNetwork.HandleReceiveAsync(
                            LocalEndpoint, deserialized))
                        .CatchCompose(ex => RecordLogicalThreadException(
                            "bb741504-3a4b-4ea3-a749-21fc8aec347f",
                            $"Error occured during message receipt handling from {remoteEndpoint}",
                            ex))
                        .EndLogicalThread(() => RecordEndOfLogicalThread());

                    return ((DefaultPromise<VoidType>)transmissionResult).WrappedTask;
                });
            }

            return sendResult;
        }

        private AbstractPromise<VoidType> HandleReceiveAsync(GenericNetworkIdentifier remoteEndpoint,
            ProtocolDatagram datagram)
        {
            ISessionHandler sessionHandler;
            lock (_sessionHandlerStore)
            {
                var sessionHandlerWrapper = _sessionHandlerStore.Get(remoteEndpoint, datagram.SessionId);
                if (sessionHandlerWrapper != null)
                {
                    sessionHandler = sessionHandlerWrapper.SessionHandler;
                }
                else
                {
                    if (IsShuttingDown())
                    {
                        // ignore new session if shutting down.
                        RecordTestLog("823df166-6430-4bcf-ae8f-2cb4c5e77cb1", logEvent =>
                        {
                            logEvent.Message = "Ignoring creation of new session as instance is shutting down";
                        });
                        return PromiseApi.CompletedPromise();
                    }
                        
                    if (SessionHandlerFactory == null)
                    {
                        throw new Exception("SessionHandlerFactory is null so new session handler could not be " +
                            "created for incoming message");
                    }

                    sessionHandler = SessionHandlerFactory.Create();
                    _sessionHandlerStore.Add(remoteEndpoint, datagram.SessionId,
                        new SessionHandlerWrapper(sessionHandler));
                    sessionHandler.CompleteInit(datagram.SessionId, false, this, remoteEndpoint);
                }
            }
            return sessionHandler.ProcessReceiveAsync(datagram);
        }

        public void RequestSessionDispose(GenericNetworkIdentifier remoteEndpoint,
            string sessionId, SessionDisposedException cause)
        {
            // Fire outside of event loop thread if possible.
            var newLogicalThreadId = GenerateAndRecordLogicalThreadId(null);
            Task.Run(() => {
                var promise = PromiseApi.CompletedPromise()
                    .StartLogicalThread(newLogicalThreadId)
                    .ThenCompose(_ => _DisposeSessionAsync(remoteEndpoint, sessionId, cause))
                    .CatchCompose(ex => RecordLogicalThreadException(
                        "86a662a4-c098-4053-ac26-32b984079419",
                        "Error encountered while disposing session handler", ex))
                    .EndLogicalThread(() => RecordEndOfLogicalThread());
                return ((DefaultPromise<VoidType>)promise).WrappedTask;
            });
        }

        public AbstractPromise<VoidType> _DisposeSessionAsync(GenericNetworkIdentifier remoteEndpoint, string sessionId,
            SessionDisposedException cause)
        {
            SessionHandlerWrapper sessionHandler;
            lock (_sessionHandlerStore)
            {
                sessionHandler = _sessionHandlerStore.Get(remoteEndpoint, sessionId);
                _sessionHandlerStore.Remove(remoteEndpoint, sessionId);
            }
            if (sessionHandler != null)
            {
                return sessionHandler.SessionHandler.FinaliseDisposeAsync(cause);
            }
            return PromiseApi.CompletedPromise();
        }

        public AbstractPromise<VoidType> ShutdownAsync(int gracefulWaitPeriodSecs)
        {
            // it is enough to prevent creation of new session handlers
            lock (_isShuttingDownLock)
            {
                _isShuttingDown = true;
            }
            return PromiseApi.Delay(gracefulWaitPeriodSecs * 1000);
        }

        public bool IsShuttingDown()
        {
            return _isShuttingDown;
        }

        private Guid GenerateAndRecordLogicalThreadId(Action<CustomLogEvent> customizer)
        {
            var logicalThreadId = Guid.NewGuid();
            CustomLoggerFacade.TestLog(() =>
            {
                var logEvent = new CustomLogEvent(GetType(), "Starting new logical thread")
                    .AddProperty(LogDataKeyNewLogicalThreadId, logicalThreadId)
                    .AddProperty(LogDataKeyCurrentLogicalThreadId, PromiseApi.CurrentLogicalThreadId);
                if (customizer != null)
                {
                    customizer.Invoke(logEvent);
                }
                return logEvent;
            });
            return logicalThreadId;
        }

        private void RecordEndOfLogicalThread()
        {
            CustomLoggerFacade.TestLog(() =>
            {
                var logEvent = new CustomLogEvent(GetType(), "Ending current logical thread")
                    .AddProperty(LogDataKeyEndingLogicalThreadId, PromiseApi.CurrentLogicalThreadId);
                return logEvent;
            });
        }

        private AbstractPromise<VoidType> RecordLogicalThreadException(string logPosition,
            string message, Exception ex)
        {
            CustomLoggerFacade.Log(() => new CustomLogEvent(GetType(), message, ex)
                   .AddProperty(LogDataKeyCurrentLogicalThreadId, PromiseApi.CurrentLogicalThreadId)
                   .AddProperty(LogDataKeyLogPositionId, logPosition));
            return PromiseApi.CompletedPromise();
        }

        private void RecordTestLog(string logPosition, Action<CustomLogEvent> customizer)
        {
            CustomLoggerFacade.TestLog(() =>
            {
                var logEvent = new CustomLogEvent(GetType())
                    .AddProperty(LogDataKeyCurrentLogicalThreadId, PromiseApi.CurrentLogicalThreadId)
                    .AddProperty(LogDataKeyLogPositionId, logPosition);
                if (customizer != null)
                {
                    customizer.Invoke(logEvent);
                }
                return logEvent;
            });
        }
    }
}
