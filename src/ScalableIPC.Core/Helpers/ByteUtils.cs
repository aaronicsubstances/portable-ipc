﻿using System;

namespace ScalableIPC.Core.Helpers
{
    public static class ByteUtils
    {
        public static string GenerateUuid()
        {
            return Guid.NewGuid().ToString("n");
        }

        public static string ConvertBytesToHex(byte[] data, int offset, int len)
        {
            // send out lower case for similarity with other platforms (Java, Python, NodeJS, etc)
            // ensure even length.
            return BitConverter.ToString(data, offset, len).Replace("-", "").ToLower();
        }

        public static byte[] ConvertHexToBytes(string hex)
        {
            int charCount = hex.Length;
            if (charCount % 2 != 0)
            {
                throw new Exception("arg must have even length");
            }
            byte[] rawBytes = new byte[charCount / 2];
            ConvertHexToBytes(hex, rawBytes, 0);
            return rawBytes;
        }

        public static void ConvertHexToBytes(string hex, byte[] rawBytes, int offset)
        {
            int charCount = hex.Length;
            if (charCount % 2 != 0)
            {
                throw new Exception("arg must have even length");
            }
            for (int i = 0; i < charCount; i += 2)
            {
                // accept both upper and lower case hex chars.
                rawBytes[offset + i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
        }

        public static byte[] SerializeUnsignedInt16BigEndian(int v)
        {
            byte[] rawBytes = new byte[2];
            SerializeUnsignedInt16BigEndian(v, rawBytes, 0);
            return rawBytes;
        }

        public static void SerializeUnsignedInt16BigEndian(int v, byte[] rawBytes, int offset)
        {
            SerializeInt16BigEndian((short)v, rawBytes, offset);
        }

        public static byte[] SerializeInt16BigEndian(short v)
        {
            byte[] rawBytes = new byte[2];
            SerializeInt16BigEndian(v, rawBytes, 0);
            return rawBytes;
        }

        public static void SerializeInt16BigEndian(short v, byte[] rawBytes, int offset)
        {
            rawBytes[offset] = (byte)(0xff & (v >> 8));
            rawBytes[offset + 1] = (byte)(0xff & v);
        }

        public static byte[] SerializeInt32BigEndian(int v)
        {
            byte[] rawBytes = new byte[4];
            SerializeInt32BigEndian(v, rawBytes, 0);
            return rawBytes;
        }

        public static void SerializeInt32BigEndian(int v, byte[] rawBytes, int offset)
        {
            rawBytes[offset] = (byte)(0xff & (v >> 24));
            rawBytes[offset + 1] = (byte)(0xff & (v >> 16));
            rawBytes[offset + 2] = (byte)(0xff & (v >> 8));
            rawBytes[offset + 3] = (byte)(0xff & v);
        }

        public static byte[] SerializeInt64BigEndian(long v)
        {
            byte[] rawBytes = new byte[8];
            SerializeInt64BigEndian(v, rawBytes, 0);
            return rawBytes;
        }

        public static void SerializeInt64BigEndian(long v, byte[] rawBytes, int offset)
        {
            rawBytes[offset] = (byte)(0xff & (v >> 56));
            rawBytes[offset + 1] = (byte)(0xff & (v >> 48));
            rawBytes[offset + 2] = (byte)(0xff & (v >> 40));
            rawBytes[offset + 3] = (byte)(0xff & (v >> 32));
            rawBytes[offset + 4] = (byte)(0xff & (v >> 24));
            rawBytes[offset + 5] = (byte)(0xff & (v >> 16));
            rawBytes[offset + 6] = (byte)(0xff & (v >> 8));
            rawBytes[offset + 7] = (byte)(0xff & v);
        }

        public static short DeserializeInt16BigEndian(byte[] rawBytes, int offset)
        {
            byte a = rawBytes[offset];
            byte b = rawBytes[offset + 1];
            int v = (a << 8) | (b & 0xff);
            return (short)v;
        }

        public static int DeserializeUnsignedInt16BigEndian(byte[] rawBytes, int offset)
        {
            byte a = rawBytes[offset];
            byte b = rawBytes[offset + 1];
            int v = (a << 8) | (b & 0xff);
            return v; // NB: no cast to short.
        }

        public static int DeserializeInt32BigEndian(byte[] rawBytes, int offset)
        {
            byte a = rawBytes[offset];
            byte b = rawBytes[offset + 1];
            byte c = rawBytes[offset + 2];
            byte d = rawBytes[offset + 3];
            int v = ((a & 0xff) << 24) | ((b & 0xff) << 16) |
                ((c & 0xff) << 8) | (d & 0xff);
            return v;
        }

        public static long DeserializeInt64BigEndian(byte[] rawBytes, int offset)
        {
            byte a = rawBytes[offset];
            byte b = rawBytes[offset + 1];
            byte c = rawBytes[offset + 2];
            byte d = rawBytes[offset + 3];
            byte e = rawBytes[offset + 4];
            byte f = rawBytes[offset + 5];
            byte g = rawBytes[offset + 6];
            byte h = rawBytes[offset + 7];
            long v = ((long)(a & 0xff) << 56) | ((long)(b & 0xff) << 48) |
                ((long)(c & 0xff) << 40) | ((long)(d & 0xff) << 32) |
                ((long)(e & 0xff) << 24) | ((long)(f & 0xff) << 16) |
                ((long)(g & 0xff) << 8) | ((long)h & 0xff);
            return v;
        }
    }
}
