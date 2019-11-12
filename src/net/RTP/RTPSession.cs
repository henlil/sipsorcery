﻿//-----------------------------------------------------------------------------
// Filename: RTPSession.cs
//
// Description: Represents an RTP session constituted of a single media stream. The session
// does not control the sockets as they may be shared by multiple sessions.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
//
// History:
// 25 Aug 2019	Aaron Clauson	Created, Montreux, Switzerland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Net;
using System.Net.Sockets;
using SIPSorcery.Sys;
using Microsoft.Extensions.Logging;

namespace SIPSorcery.Net
{
    public delegate int ProtectRtpPacket(byte[] payload, int length);

    public class RTPSession
    {
        private const int RTP_MAX_PAYLOAD = 1400;
        private const int SRTP_AUTH_KEY_LENGTH = 10;

        private static ILogger logger = Log.Logger;

        public int PayloadType { get; private set; }
        public uint Ssrc       { get; private set; }
        public ushort SeqNum   { get; private set; }

        public uint PacketsSent { get; private set; }
        public uint OctetsSent { get; private set; }

        /// <summary>
        /// Function pointer to an SRTP context that encrypts an RTP packet.
        /// </summary>
        public ProtectRtpPacket SrtpProtect { get; private set; }

        /// <summary>
        /// Function pointer to an SRTCP context that encrypts an RTCP packet.
        /// </summary>
        public ProtectRtpPacket SrtcpProtect { get; private set; }


        public RTPSession(int payloadType, ProtectRtpPacket srtpProtect, ProtectRtpPacket srtcpProtect)
        {
            PayloadType = payloadType;
            SrtpProtect = srtpProtect;
            SrtcpProtect = srtcpProtect;
            Ssrc = Convert.ToUInt32(Crypto.GetRandomInt(0, Int32.MaxValue));
            SeqNum = Convert.ToUInt16(Crypto.GetRandomInt(0, UInt16.MaxValue));
        }

        /// <summary>
        /// Packages and sends a single audio frame over one or more RTP packets.
        /// </summary>
        public void SendAudioFrame(Socket srcRtpSocket, IPEndPoint dstRtpSocket, uint timestamp, byte[] buffer)
        {
            try
            {
                for (int index = 0; index * RTP_MAX_PAYLOAD < buffer.Length; index++)
                {
                    SeqNum = (ushort)(SeqNum % UInt16.MaxValue);

                    int offset = (index == 0) ? 0 : (index * RTP_MAX_PAYLOAD);
                    int payloadLength = (offset + RTP_MAX_PAYLOAD < buffer.Length) ? RTP_MAX_PAYLOAD : buffer.Length - offset;
                    int srtpProtectionLength = (SrtpProtect != null) ? SRTP_AUTH_KEY_LENGTH : 0;

                    RTPPacket rtpPacket = new RTPPacket(payloadLength + srtpProtectionLength);
                    rtpPacket.Header.SyncSource = Ssrc;
                    rtpPacket.Header.SequenceNumber = SeqNum++;
                    rtpPacket.Header.Timestamp = timestamp;
                    rtpPacket.Header.MarkerBit = ((offset + payloadLength) >= buffer.Length) ? 1 : 0; // Set marker bit for the last packet in the frame.
                    rtpPacket.Header.PayloadType = PayloadType;

                    Buffer.BlockCopy(buffer, offset, rtpPacket.Payload, 0, payloadLength);

                    var rtpBuffer = rtpPacket.GetBytes();

                    int rtperr = SrtpProtect == null ? 0 : SrtpProtect(rtpBuffer, rtpBuffer.Length - srtpProtectionLength);
                    if (rtperr != 0)
                    {
                        logger.LogError("SendAudioFrame SRTP packet protection failed, result " + rtperr + ".");
                    }
                    else
                    {
                        srcRtpSocket.SendTo(rtpBuffer, dstRtpSocket);
                    }

                    PacketsSent++;
                    OctetsSent += (uint)payloadLength;
                }
            }
            catch (System.Net.Sockets.SocketException sockExcp)
            {
                logger.LogError("SocketException SendAudioFrame. " + sockExcp.Message);
            }
        }

        public void SendVp8Frame(Socket srcRtpSocket, IPEndPoint dstRtpSocket, uint timestamp, byte[] buffer)
        {
            try
            {
                for (int index = 0; index * RTP_MAX_PAYLOAD < buffer.Length; index++)
                {
                    SeqNum = (ushort)(SeqNum % UInt16.MaxValue);

                    int offset = (index == 0) ? 0 : (index * RTP_MAX_PAYLOAD);
                    int payloadLength = (offset + RTP_MAX_PAYLOAD < buffer.Length) ? RTP_MAX_PAYLOAD : buffer.Length - offset;
                    int srtpProtectionLength = (SrtpProtect != null) ? SRTP_AUTH_KEY_LENGTH : 0;

                    byte[] vp8HeaderBytes = (index == 0) ? new byte[] { 0x10 } : new byte[] { 0x00 };

                    RTPPacket rtpPacket = new RTPPacket(payloadLength + vp8HeaderBytes.Length + srtpProtectionLength);
                    rtpPacket.Header.SyncSource = Ssrc;
                    rtpPacket.Header.SequenceNumber = SeqNum++;
                    rtpPacket.Header.Timestamp = timestamp;
                    rtpPacket.Header.MarkerBit = ((offset + payloadLength) >= buffer.Length) ? 1 : 0; // Set marker bit for the last packet in the frame.
                    rtpPacket.Header.PayloadType = PayloadType;

                    Buffer.BlockCopy(vp8HeaderBytes, 0, rtpPacket.Payload, 0, vp8HeaderBytes.Length);
                    Buffer.BlockCopy(buffer, offset, rtpPacket.Payload, vp8HeaderBytes.Length, payloadLength);

                    var rtpBuffer = rtpPacket.GetBytes();


                    int rtperr = SrtpProtect == null ? 0 : SrtpProtect(rtpBuffer, rtpBuffer.Length - srtpProtectionLength);
                    if (rtperr != 0)
                    {
                        logger.LogError("SendVp8Frame SRTP packet protection failed, result " + rtperr + ".");
                    }
                    else
                    {
                        srcRtpSocket.SendTo(rtpBuffer, dstRtpSocket);
                    }

                    PacketsSent++;
                    OctetsSent += (uint)payloadLength;
                }
            }
            catch (System.Net.Sockets.SocketException sockExcp)
            {
                logger.LogError("SocketException SendVp8Frame. " + sockExcp.Message);
            }
        }

        public void SendRtcpSenderReport(Socket srcRtpSocket, IPEndPoint dstRtpSocket, uint timestamp)
        {
            try
            {
                var ntp = RTSPSession.DateTimeToNptTimestamp(DateTime.Now);
                var rtcpSRPacket = new RTCPPacket(Ssrc, ntp, timestamp, PacketsSent, OctetsSent);

                if(SrtcpProtect == null)
                {
                    srcRtpSocket.SendTo(rtcpSRPacket.GetBytes(), dstRtpSocket);
                }
                else
                {
                    var rtcpSRBytes = rtcpSRPacket.GetBytes();
                    byte[] sendBuffer = new byte[rtcpSRBytes.Length + SRTP_AUTH_KEY_LENGTH];
                    Buffer.BlockCopy(rtcpSRBytes, 0, sendBuffer, 0, rtcpSRBytes.Length);

                    int rtperr = SrtcpProtect(sendBuffer, sendBuffer.Length - SRTP_AUTH_KEY_LENGTH);
                    if (rtperr != 0)
                    {
                        logger.LogWarning("SRTP RTCP packet protection failed, result " + rtperr + ".");
                    }
                    else
                    {
                        srcRtpSocket.SendTo(sendBuffer, dstRtpSocket);
                    }
                }
            }
            catch(Exception excp)
            {
                logger.LogWarning("Exception SendRtcpSenderReport. " + excp.Message);
            }
        }
    }
}
