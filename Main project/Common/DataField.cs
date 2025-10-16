using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace Common
{
    public class DataField
    {
        public long TimeStamp;              // 8 bytes
        public ushort SequenceControl;      // 2 bytes
        public byte ServiceType;            // 1 byte
        public byte ServiceSubtype;         // 1 byte
        public ushort Nbytes;               // 2 bytes
        public byte[] Data;                 // N bytes (payload)

        private int totalNumberOfBytes;
        // ______________________________________________________________________________
        // | Timestamp | Sequence | Service | Subservice | NumberOfBytes |     Data     |
        // ------------------------------------------------------------------------------
        // |  8 bytes  |  2 bytes | 1 byte  |   1 byte   |    2 bytes    |    N bytes   |
        // |   0 - 7   |   8 - 9  |   10    |     11     |    12 - 13    |  14 - 14+N-1 | 
        // |    Unix   |  

        // ----------------------------------------------------------
        //    Constructor 1: Create packet by filling each field
        // ----------------------------------------------------------
        public DataField(long timeStamp, ushort sequenceControl, byte serviceType, byte serviceSubtype, byte[] data)
        {
            TimeStamp = timeStamp;
            SequenceControl = sequenceControl;
            ServiceType = serviceType;
            ServiceSubtype = serviceSubtype;
            Nbytes = (ushort)(data != null ? data.Length : 0);
            Data = data ?? Array.Empty<byte>();
            totalNumberOfBytes = 8 + 2 + 1 + 1 + 2 + Nbytes; // header + data
        }

        // ----------------------------------------------------------
        // Constructor 2: Create packet by deserializing a byte array
        // ----------------------------------------------------------
        public DataField(byte[] raw)
        {
            if (raw == null || raw.Length < 6)
                throw new ArgumentException("Invalid packet length");

            int index = 0; // index of read byte

            // Timestamp  (uint64, big-endian)
            byte[] timeBytes = { raw[index + 7], raw[index + 6], raw[index + 5], raw[index + 4], raw[index + 3], raw[index + 2], raw[index + 1], raw[index] };
            TimeStamp = BitConverter.ToInt64(timeBytes, 0);

            index += 8;

            // Sequence Control (ushort, big-endian)
            byte[] seqBytes = { raw[index + 1], raw[index] };
            SequenceControl = BitConverter.ToUInt16(seqBytes);
            index += 2;

            // Service Type and Subtype
            ServiceType = raw[index++];
            ServiceSubtype = raw[index++];

            // nBytes (ushort, big-endian)
            Nbytes = (ushort)((raw[index] << 8) | raw[index + 1]);
            index += 2;

            // Data
            if (Nbytes > 0)
            {
                Data = new byte[Nbytes];
                Array.Copy(raw, index, Data, 0, Nbytes);
            }
            else
            {
                Data = Array.Empty<byte>();
            }

            totalNumberOfBytes = index + Nbytes;
        }

        // ----------------------------------------------------------
        // Serialize the packet into a byte array (for transmission)
        // ----------------------------------------------------------
        public byte[] Serialize()
        {
            byte[] buffer = new byte[totalNumberOfBytes];
            int index = 0;

            // Timestamp : byte 0 - 7
            long timeBE = (long)IPAddress.HostToNetworkOrder(TimeStamp);
            Array.Copy(BitConverter.GetBytes(timeBE), 0, buffer, index, 8);
            index += 8;

            // Sequence Control : byte 8 - 9
            ushort seqBE = (ushort)IPAddress.HostToNetworkOrder(SequenceControl);
            Array.Copy(BitConverter.GetBytes(seqBE), 0, buffer, index, 2);
            index += 2;

            // Service Type and Subtype 
            buffer[index++] = ServiceType;    // : byte 10 
            buffer[index++] = ServiceSubtype; // : byte 11

            // nBytes : byte 12 - 13
            buffer[index++] = (byte)(Nbytes >> 8);
            buffer[index++] = (byte)(Nbytes & 0xFF);

            // Data
            if (Data != null && Data.Length > 0)
                Array.Copy(Data, 0, buffer, index, Data.Length);

            return buffer;
        }

        // ----------------------------------------------------------
        //                     Print packet info
        // ----------------------------------------------------------
        public override string ToString()
        {
            DateTimeOffset unixTime = DateTimeOffset.FromUnixTimeSeconds(TimeStamp);
            return $"{unixTime.ToUniversalTime().ToString()}: Seq={SequenceControl}, Service={ServiceType}, Subservice={ServiceSubtype}, Databytes={Nbytes}, Total={totalNumberOfBytes}";
        }
    }
}
