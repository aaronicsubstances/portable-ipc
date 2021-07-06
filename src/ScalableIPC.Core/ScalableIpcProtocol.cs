﻿using ScalableIPC.Core.Abstractions;
using ScalableIPC.Core.Helpers;
using ScalableIPC.Core.ProtocolOperation;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("ScalableIPC.Core.UnitTests")]
[assembly: InternalsVisibleTo("ScalableIPC.IntegrationTests")]

namespace ScalableIPC.Core
{
    public class ScalableIpcProtocol: IScalableIpcProtocol
    {
        public const int MinimumMessageSizeLimit = 65_536;
        public const int MinimumPduSizeLimit = 512;

        private readonly EndpointStructuredDatastore<IncomingTransfer> incomingTransfers;
        private readonly EndpointStructuredDatastore<OutgoingTransfer> outgoingTransfers;
        private readonly Dictionary<GenericNetworkIdentifier, string> knownMessageDestinationIds;

        public ScalableIpcProtocol()
        {
            EndpointOwnerId = ByteUtils.GenerateUuid();
            incomingTransfers = new EndpointStructuredDatastore<IncomingTransfer>();
            outgoingTransfers = new EndpointStructuredDatastore<OutgoingTransfer>();
            knownMessageDestinationIds = new Dictionary<GenericNetworkIdentifier, string>();
        }

        public string EndpointOwnerId { get; }
        public int PduSizeLimit { get; set; }
        public int MessageSizeLimit { get; set; }
        public int MinRetryBackoffPeriod { get; set; }
        public int MaxRetryBackoffPeriod { get; set; }
        public int AckReceiveTimeout { get; set; }
        public int DataReceiveTimeout { get; set; }
        public int ProcessedMessageDisposalWaitTime { get; set; }
        public bool VaryMessageSourceIds { get; set; }
        public ScalableIpcProtocolListener EventListener { get; set; }
        public TransportApi UnderlyingTransport { get; set; }
        public EventLoopApi EventLoop { get; set; }

        public string BeginSend(GenericNetworkIdentifier remoteEndpoint,
            byte[] data, int offset, int length, Action<ProtocolOperationException> cb)
        {
            string messageId = ByteUtils.GenerateUuid();
            var transfer = new OutgoingTransfer
            {
                MessageId = messageId,
                RemoteEndpoint = remoteEndpoint,
                Data = data,
                StartOffset = offset,
                EndOffset = offset + length,
                SendCallback = cb
            };
            EventLoop.PostCallback(() =>
            {
                ProcessSendTransfer(transfer);
            });
            return messageId;
        }

        private void ProcessSendTransfer(OutgoingTransfer transfer)
        {
            // validate transfer
            if (transfer.StartOffset < 0)
            {
                throw new Exception();
            }
            if (transfer.EndOffset < transfer.StartOffset)
            {
                throw new Exception();
            }
            if (transfer.EndOffset > transfer.Data.Length)
            {
                throw new Exception();
            }
            outgoingTransfers.Add(transfer.RemoteEndpoint, transfer.MessageId, transfer);
            transfer.MessageDestId = GetKnownMessageDestinationId(transfer.RemoteEndpoint);
            if (transfer.MessageDestId == null)
            {
                transfer.MessageDestId = ByteUtils.GenerateUuid();
            }

            // start ack timeout
            transfer.ReceiveAckTimeout = EventLoop.ScheduleTimeout(AckReceiveTimeout,
                () => AbortSendTransfer(transfer, ProtocolOperationException.ErrorCodeSendTimeout));

            var dataLengthToSend = Math.Min(transfer.EndOffset - transfer.StartOffset, PduSizeLimit);
            transfer.PendingPdu = new ProtocolDatagram
            {
                OpCode = ProtocolDatagram.OpCodeHeader,
                Version = ProtocolDatagram.ProtocolVersion1_0,
                MessageId = transfer.MessageId,
                MessageLength = transfer.EndOffset - transfer.StartOffset,
                Data = transfer.Data,
                DataOffset = transfer.StartOffset,
                DataLength = dataLengthToSend
            };
            ResendPendingPdu(transfer);
        }

        private void AbortSendTransfer(OutgoingTransfer transfer, int abortCode)
        {
            if (!outgoingTransfers.Remove(transfer.RemoteEndpoint, transfer.MessageId))
            {
                return;
            }
            transfer.SendCancellationHandle?.Cancel();
            EventLoop.CancelTimeout(transfer.RetryBackoffTimeout);
            EventLoop.CancelTimeout(transfer.ReceiveAckTimeout);
            if (transfer.SendCallback != null)
            {
                if (abortCode == 0)
                {
                    transfer.SendCallback(null);
                }
                else
                {
                    transfer.SendCallback(new ProtocolOperationException(abortCode));
                }
            }
        }

        private void ResendPendingPdu(OutgoingTransfer transfer)
        {
            transfer.SendCancellationHandle?.Cancel();
            EventLoop.CancelTimeout(transfer.RetryBackoffTimeout);
            var pduBytes = transfer.PendingPdu.Serialize();
            transfer.SendCancellationHandle = new CancellationHandle();
            var sendCb = WrapForCancellation(transfer.SendCancellationHandle,
                () => ProcessSendPduOutcome(transfer));
            // disregard success or failure result. just interested in waiting.
            UnderlyingTransport.BeginSend(transfer.RemoteEndpoint, pduBytes, 0, pduBytes.Length, _ =>
            {
                EventLoop.PostCallback(sendCb);
            });
        }

        private void ProcessSendPduOutcome(OutgoingTransfer transfer)
        {
            int retryBackoffPeriod = MinRetryBackoffPeriod;
            if (MaxRetryBackoffPeriod > MinRetryBackoffPeriod)
            {
                retryBackoffPeriod += MathUtils.GetRandomInt(MaxRetryBackoffPeriod - MinRetryBackoffPeriod);
            }
            transfer.RetryBackoffTimeout = EventLoop.ScheduleTimeout(retryBackoffPeriod,
                () => ResendPendingPdu(transfer));
        }

        private void ProcessAck(GenericNetworkIdentifier remoteEndpoint, ProtocolDatagram ack)
        {
            OutgoingTransfer transfer = outgoingTransfers.Get(remoteEndpoint, ack.MessageId, null);
            if (transfer == null)
            {
                throw new Exception($"could not find outgoing transfer of message {ack.MessageId} to {remoteEndpoint}");
            }
            if (ack.OpCode != transfer.PendingPdu.OpCode + 1)
            {
                // discard.
                return;
            }
            if (ack.OpCode == ProtocolDatagram.OpCodeDataAck && ack.SequenceNumber != transfer.PendingPdu.SequenceNumber)
            {
                // discard.
                return;
            }
            if (ack.ErrorCode < 0)
            {
                // discard.
                return;
            }
            if (ack.ErrorCode > 0)
            {
                if (ack.ErrorCode == ProtocolOperationException.ErrorCodeInvalidDestinationEndpointId)
                {
                    // save for future use.
                    UpdateKnownMessageDestinationIds(remoteEndpoint, ack.MessageSourceId);

                    if (ack.OpCode == ProtocolDatagram.OpCodeHeaderAck && transfer.MessageDestId != ack.MessageSourceId)
                    {
                        transfer.MessageDestId = ack.MessageSourceId;
                        transfer.PendingPdu.MessageDestinationId = transfer.MessageDestId;
                        ResendPendingPdu(transfer);
                    }
                    else
                    {
                        // abort transfer.
                        AbortSendTransfer(transfer, ack.ErrorCode);
                    }
                }
                else
                {
                    // abort transfer.
                    AbortSendTransfer(transfer, ack.ErrorCode);
                }
            }
            else
            {
                // successfully sent pending pdu.
                transfer.StartOffset += transfer.PendingPdu.DataLength;

                // check if we are done.
                if (transfer.StartOffset == transfer.EndOffset)
                {
                    AbortSendTransfer(transfer, 0);
                }
                else
                {
                    // not done.

                    // reset ack timeout
                    EventLoop.CancelTimeout(transfer.ReceiveAckTimeout);
                    transfer.ReceiveAckTimeout = EventLoop.ScheduleTimeout(AckReceiveTimeout,
                        () => AbortSendTransfer(transfer, ProtocolOperationException.ErrorCodeSendTimeout));

                    // prepare to send next pdu
                    var dataLengthToSend = Math.Min(transfer.EndOffset - transfer.StartOffset, PduSizeLimit);
                    int nextSequenceNumber = 1;
                    if (transfer.PendingPdu.OpCode == ProtocolDatagram.OpCodeData)
                    {
                        nextSequenceNumber = transfer.PendingPdu.SequenceNumber + 1;
                    }
                    transfer.PendingPdu = new ProtocolDatagram
                    {
                        OpCode = ProtocolDatagram.OpCodeData,
                        Version = ProtocolDatagram.ProtocolVersion1_0,
                        MessageId = transfer.MessageId,
                        MessageDestinationId = transfer.MessageDestId,
                        SequenceNumber = nextSequenceNumber,
                        Data = transfer.Data,
                        DataOffset = transfer.StartOffset,
                        DataLength = dataLengthToSend
                    };
                    ResendPendingPdu(transfer);
                }
            }
        }

        private string GetKnownMessageDestinationId(GenericNetworkIdentifier remoteEndpoint)
        {
            if (knownMessageDestinationIds.ContainsKey(remoteEndpoint))
            {
                return knownMessageDestinationIds[remoteEndpoint];
            }
            else
            {
                return null;
            }
        }

        private void UpdateKnownMessageDestinationIds(GenericNetworkIdentifier remoteEndpoint, string messageSourceId)
        {
            if (knownMessageDestinationIds.ContainsKey(remoteEndpoint))
            {
                knownMessageDestinationIds[remoteEndpoint] = messageSourceId;
            }
            else
            {
                // impose any arbitrary max limit on entries.
                if (knownMessageDestinationIds.Count > 1000)
                {
                    knownMessageDestinationIds.Clear();
                }
                knownMessageDestinationIds.Add(remoteEndpoint, messageSourceId);
            }
        }

        private Action WrapForCancellation(CancellationHandle cancellationHandle, Action cb)
        {
            return () =>
            {
                if (!cancellationHandle.Cancelled)
                {
                    cb();
                }
            };
        }

        public void BeginReceive(GenericNetworkIdentifier remoteEndpoint,
            byte[] data, int offset, int length)
        {
            EventLoop.PostCallback(() =>
            {
                ProtocolDatagram pdu = ProtocolDatagram.Deserialize(data, offset, length);
                ValidatePdu(pdu);
                if (pdu.OpCode == ProtocolDatagram.OpCodeHeader)
                {
                    ProcessReceivedMessageHeaderPart(remoteEndpoint, pdu);
                }
                else if (pdu.OpCode == ProtocolDatagram.OpCodeData)
                {
                    ProcessReceivedMessageDataPart(remoteEndpoint, pdu);
                }
                if (pdu.OpCode == ProtocolDatagram.OpCodeHeaderAck ||
                    pdu.OpCode == ProtocolDatagram.OpCodeDataAck)
                {
                    ProcessAck(remoteEndpoint, pdu);
                }
                else
                {
                    throw new Exception($"unexpected opcode: {pdu.OpCode}");
                }
            });
        }

        public static void ValidatePdu(ProtocolDatagram pdu)
        {
            if (pdu.Version == 0)
            {
                throw new Exception();
            }
            if (pdu.MessageId.Trim('0').Length == 0)
            {
                throw new Exception();
            }
            if (pdu.MessageDestinationId != null && pdu.MessageDestinationId.Trim('0').Length == 0)
            {
                throw new Exception();
            }
            if (pdu.MessageSourceId != null && pdu.MessageSourceId.Trim('0').Length == 0)
            {
                throw new Exception();
            }
            if (pdu.Data != null && pdu.DataLength == 0)
            {
                throw new Exception();
            }
            if (pdu.OpCode == ProtocolDatagram.OpCodeHeader && pdu.MessageLength < 0)
            {
                throw new Exception();
            }
            if (pdu.OpCode == ProtocolDatagram.OpCodeData && pdu.SequenceNumber <= 0)
            {
                throw new Exception();
            }
        }

        private void ProcessReceivedMessageHeaderPart(GenericNetworkIdentifier remoteEndpoint, ProtocolDatagram pdu)
        {
            IncomingTransfer transfer = incomingTransfers.Get(remoteEndpoint, pdu.MessageId, null);
            if (transfer?.Processed == true)
            {
                if (transfer.MessageSrcId == pdu.MessageDestinationId && transfer.ExpectedSequenceNumber == 0)
                {
                    // send back again the last ack sent out.
                    if (transfer.LastAckSent == null)
                    {
                        transfer.LastAckSent = new ProtocolDatagram
                        {
                            OpCode = ProtocolDatagram.OpCodeHeaderAck,
                            Version = ProtocolDatagram.ProtocolVersion1_0,
                            MessageId = transfer.MessageId,
                            MessageSourceId = transfer.MessageSrcId,
                            ErrorCode = transfer.ProcessingErrorCode
                        };
                    }
                    var lastAckBytes = transfer.LastAckSent.Serialize();
                    // ignore outcome of send.
                    UnderlyingTransport.BeginSend(remoteEndpoint, lastAckBytes, 0, lastAckBytes.Length, null);
                }
                else
                {
                    // discard
                }
            }
            else
            {
                if (transfer == null)
                {
                    transfer = new IncomingTransfer
                    {
                        RemoteEndpoint = remoteEndpoint,
                        MessageId = pdu.MessageId,
                        MessageSrcId = EndpointOwnerId,
                        ReceiveBuffer = new MemoryStream()
                    };
                    if (VaryMessageSourceIds)
                    {
                        transfer.MessageSrcId = ByteUtils.GenerateUuid();

                        // before we can tell whether pdu is worth processing,
                        // transfer may not need to be actually set up.
                        // only add transfer when varying source ids
                        incomingTransfers.Add(remoteEndpoint, pdu.MessageId, transfer);
                        transfer.ReceiveTimeout = EventLoop.ScheduleTimeout(DataReceiveTimeout,
                            () => ProcessReceiveDataTimeout(transfer));
                    }
                }
                if (transfer.MessageSrcId != pdu.MessageDestinationId)
                {
                    var ack = new ProtocolDatagram
                    {
                        OpCode = ProtocolDatagram.OpCodeHeaderAck,
                        Version = ProtocolDatagram.ProtocolVersion1_0,
                        MessageId = transfer.MessageId,
                        MessageSourceId = transfer.MessageSrcId,
                        ErrorCode = ProtocolOperationException.ErrorCodeInvalidDestinationEndpointId
                    };
                    var ackBytes = ack.Serialize();
                    // ignore outcome of send.
                    UnderlyingTransport.BeginSend(remoteEndpoint, ackBytes, 0, ackBytes.Length, null);
                }
                else if (transfer.ExpectedSequenceNumber != 0)
                {
                    if (transfer.ExpectedSequenceNumber == 1)
                    {
                        // send back again the last ack sent out.
                        var lastAckBytes = transfer.LastAckSent.Serialize();
                        // ignore outcome of send.
                        UnderlyingTransport.BeginSend(remoteEndpoint, lastAckBytes, 0, lastAckBytes.Length, null);
                    }
                    else
                    {
                        // discard
                    }
                }
                else if (pdu.MessageLength > MessageSizeLimit)
                {
                    var ack = new ProtocolDatagram
                    {
                        OpCode = ProtocolDatagram.OpCodeHeaderAck,
                        Version = ProtocolDatagram.ProtocolVersion1_0,
                        MessageId = transfer.MessageId,
                        MessageSourceId = transfer.MessageSrcId,
                        ErrorCode = ProtocolOperationException.ErrorCodeMessageTooLarge
                    };
                    var ackBytes = ack.Serialize();
                    // ignore outcome of send.
                    UnderlyingTransport.BeginSend(remoteEndpoint, ackBytes, 0, ackBytes.Length, null);
                }
                else
                {
                    // all is well.
                    transfer.BytesRemaining = pdu.MessageLength;

                    // ensure addition of transfer
                    incomingTransfers.Add(remoteEndpoint, pdu.MessageId, transfer);
                        
                    // reset timeout
                    EventLoop.CancelTimeout(transfer.ReceiveTimeout);
                    transfer.ReceiveTimeout = EventLoop.ScheduleTimeout(DataReceiveTimeout,
                        () => ProcessReceiveDataTimeout(transfer));

                    int dataLengthToUse = Math.Min(pdu.DataLength, transfer.BytesRemaining);
                    transfer.ReceiveBuffer.Write(pdu.Data, pdu.DataOffset, dataLengthToUse);
                    transfer.BytesRemaining -= dataLengthToUse;
                    if (transfer.BytesRemaining == 0)
                    {
                        // mark as processed and successful.
                        transfer.Processed = true;
                        EventLoop.CancelTimeout(transfer.ReceiveTimeout);
                        var messageBytes = transfer.ReceiveBuffer.ToArray();
                        transfer.ReceiveBuffer.Dispose();
                        EventListener?.OnMessageReceived(remoteEndpoint, pdu.MessageId,
                            messageBytes, 0, messageBytes.Length);
                        transfer.ExpirationTimeout = EventLoop.ScheduleTimeout(ProcessedMessageDisposalWaitTime,
                            () => ProcessExpiredDataTimeout(transfer));
                    }
                    else
                    {
                        transfer.ExpectedSequenceNumber++;
                    }
                    transfer.LastAckSent = new ProtocolDatagram
                    {
                        OpCode = ProtocolDatagram.OpCodeHeaderAck,
                        Version = ProtocolDatagram.ProtocolVersion1_0,
                        MessageId = transfer.MessageId,
                        MessageSourceId = transfer.MessageSrcId
                    };
                    var lastAckBytes = transfer.LastAckSent.Serialize();
                    // ignore outcome of send.
                    UnderlyingTransport.BeginSend(remoteEndpoint, lastAckBytes, 0, lastAckBytes.Length, null);
                }
            }
        }

        private void ProcessReceivedMessageDataPart(GenericNetworkIdentifier remoteEndpoint, ProtocolDatagram pdu)
        {
            IncomingTransfer transfer = incomingTransfers.Get(remoteEndpoint, pdu.MessageId, null);
            if (transfer == null)
            {
                throw new Exception("could not find existing incoming transfer from " +
                    $"{remoteEndpoint} for message {pdu.MessageId}");
            }
            if (transfer.Processed == true)
            {
                if (transfer.MessageSrcId == pdu.MessageDestinationId && transfer.ExpectedSequenceNumber == pdu.SequenceNumber)
                {
                    // send back again the last ack sent out.
                    if (transfer.LastAckSent == null)
                    {
                        transfer.LastAckSent = new ProtocolDatagram
                        {
                            OpCode = ProtocolDatagram.OpCodeDataAck,
                            Version = ProtocolDatagram.ProtocolVersion1_0,
                            MessageId = transfer.MessageId,
                            MessageSourceId = transfer.MessageSrcId,
                            SequenceNumber = transfer.ExpectedSequenceNumber,
                            ErrorCode = transfer.ProcessingErrorCode
                        };
                    }
                    var lastAckBytes = transfer.LastAckSent.Serialize();
                    // ignore outcome of send.
                    UnderlyingTransport.BeginSend(remoteEndpoint, lastAckBytes, 0, lastAckBytes.Length, null);
                }
                else
                {
                    // discard
                }
            }
            else
            {
                if (transfer.MessageSrcId != pdu.MessageDestinationId)
                {
                    var ack = new ProtocolDatagram
                    {
                        OpCode = ProtocolDatagram.OpCodeDataAck,
                        Version = ProtocolDatagram.ProtocolVersion1_0,
                        MessageId = transfer.MessageId,
                        MessageSourceId = transfer.MessageSrcId,
                        SequenceNumber = pdu.SequenceNumber,
                        ErrorCode = ProtocolOperationException.ErrorCodeInvalidDestinationEndpointId
                    };
                    var ackBytes = ack.Serialize();
                    // ignore outcome of send.
                    UnderlyingTransport.BeginSend(remoteEndpoint, ackBytes, 0, ackBytes.Length, null);
                }
                else if (transfer.ExpectedSequenceNumber != pdu.SequenceNumber)
                {
                    if (transfer.ExpectedSequenceNumber == pdu.SequenceNumber + 1)
                    {
                        // send back again the last ack sent out.
                        var lastAckBytes = transfer.LastAckSent.Serialize();
                        // ignore outcome of send.
                        UnderlyingTransport.BeginSend(remoteEndpoint, lastAckBytes, 0, lastAckBytes.Length, null);
                    }
                    else
                    {
                        // discard
                    }
                }
                else
                {
                    // all is well.

                    // reset timeout
                    EventLoop.CancelTimeout(transfer.ReceiveTimeout);
                    transfer.ReceiveTimeout = EventLoop.ScheduleTimeout(DataReceiveTimeout,
                        () => ProcessReceiveDataTimeout(transfer));

                    int dataLengthToUse = Math.Min(pdu.DataLength, transfer.BytesRemaining);
                    transfer.ReceiveBuffer.Write(pdu.Data, pdu.DataOffset, dataLengthToUse);
                    transfer.BytesRemaining -= dataLengthToUse;
                    if (transfer.BytesRemaining == 0)
                    {
                        // mark as processed and successful.
                        transfer.Processed = true;
                        EventLoop.CancelTimeout(transfer.ReceiveTimeout);
                        var messageBytes = transfer.ReceiveBuffer.ToArray();
                        transfer.ReceiveBuffer.Dispose();
                        EventListener?.OnMessageReceived(remoteEndpoint, pdu.MessageId,
                            messageBytes, 0, messageBytes.Length);
                        transfer.ExpirationTimeout = EventLoop.ScheduleTimeout(ProcessedMessageDisposalWaitTime,
                            () => ProcessExpiredDataTimeout(transfer));
                    }
                    else
                    {
                        transfer.ExpectedSequenceNumber++;
                    }
                    transfer.LastAckSent = new ProtocolDatagram
                    {
                        OpCode = ProtocolDatagram.OpCodeDataAck,
                        Version = ProtocolDatagram.ProtocolVersion1_0,
                        MessageId = transfer.MessageId,
                        MessageSourceId = transfer.MessageSrcId,
                        SequenceNumber = pdu.SequenceNumber
                    };
                    var lastAckBytes = transfer.LastAckSent.Serialize();
                    // ignore outcome of send.
                    UnderlyingTransport.BeginSend(remoteEndpoint, lastAckBytes, 0, lastAckBytes.Length, null);
                }
            }
        }

        private void ProcessReceiveDataTimeout(IncomingTransfer transfer)
        {
            // mark as processed and failed.
            transfer.Processed = true;
            transfer.ProcessingErrorCode = ProtocolOperationException.ErrorCodeReceiveTimeout;
            EventLoop.CancelTimeout(transfer.ReceiveTimeout);
            transfer.ReceiveBuffer.Dispose();
            transfer.ExpirationTimeout = EventLoop.ScheduleTimeout(ProcessedMessageDisposalWaitTime,
                () => ProcessExpiredDataTimeout(transfer));
        }

        private void ProcessExpiredDataTimeout(IncomingTransfer transfer)
        {
            incomingTransfers.Remove(transfer.RemoteEndpoint, transfer.MessageId);
        }

        public void Reset(ProtocolOperationException causeOfReset)
        {

        }
    }
}
