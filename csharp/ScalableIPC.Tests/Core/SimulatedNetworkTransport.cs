﻿using ScalableIPC.Core;
using ScalableIPC.Core.Abstractions;
using ScalableIPC.Core.ConcreteComponents;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ScalableIPC.Tests.Core
{
    public class SimulatedNetworkTransport : NetworkTransportBase
    {
        public SimulatedNetworkTransport()
        {
            ConnectedNetworks = new Dictionary<GenericNetworkIdentifier, SimulatedNetworkTransport>();
            SessionHandlerFactory = new DefaultSessionHandlerFactory(typeof(TestSessionHandler));
            IdleTimeoutSecs = 5;
            AckTimeoutSecs = 3;
            MaxRetryCount = 0;
            MaximumTransferUnitSize = 512;
            MaxReceiveWindowSize = 1;
            MaxSendWindowSize = 1;
            MinTransmissionDelayMs = 0;
            MaxTransmissionDelayMs = 2;
        }

        public Dictionary<GenericNetworkIdentifier, SimulatedNetworkTransport> ConnectedNetworks { get; }

        public int MinTransmissionDelayMs { get; set; }
        public int MaxTransmissionDelayMs { get; set; }

        protected override AbstractPromise<VoidType> HandleSendData(GenericNetworkIdentifier remoteEndpoint, 
            string sessionId, byte[] data, int offset, int length)
        {
            if (ConnectedNetworks.ContainsKey(remoteEndpoint))
            {
                Task.Run(async () =>
                {
                    // Simulate transmission delay here.
                    var connectedNetwork = ConnectedNetworks[remoteEndpoint];
                    int transmissionDelayMs = new Random().Next(connectedNetwork.MinTransmissionDelayMs,
                        connectedNetwork.MaxTransmissionDelayMs);
                    if (transmissionDelayMs > 0)
                    {
                        await Task.Delay(transmissionDelayMs);
                    }
                    try
                    {
                        var pendingPromise = connectedNetwork.HandleReceive(LocalEndpoint, data, offset, length);
                        await ((DefaultPromise<VoidType>)pendingPromise).WrappedTask;
                    }
                    catch (Exception ex)
                    {
                        CustomLoggerFacade.Log(() =>
                            new CustomLogEvent("1dec508c-2d59-4336-8617-30bb71a9a5a8", $"Error occured during message " +
                                $"receipt handling at {remoteEndpoint}", ex));
                    }
                });
                return PromiseApi.Resolve(VoidType.Instance);
            }
            else
            {
                return PromiseApi.Reject(new Exception($"{remoteEndpoint} remote endpoint not found."));
            }
        }
    }
    class TestSessionHandler : ProtocolSessionHandler
    {
        public override void OnDataReceived(byte[] data, Dictionary<string, List<string>> options)
        {
            string dataMessage = ProtocolDatagram.ConvertBytesToString(data, 0, data.Length);
            CustomLoggerFacade.Log(() => new CustomLogEvent("71931970-3923-4472-b110-3449141998e3",
                $"Received data: {dataMessage}", null));
        }
    }
}