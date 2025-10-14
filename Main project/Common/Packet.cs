using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Common
{
    public class Packet
    {
        public ushort SequenceControl;      // 2 bytes
        public byte ServiceType;            // 1 byte
        public byte ServiceSubtype;         // 1 byte
        public ushort Nbytes;               // 2 bytes
        public byte[] Data;                 // N bytes (payload)

        private int totalNumberOfBytes;

        // ----------------------------------------------------------
        // Constructor 1: Create packet by filling each field manually
        // ----------------------------------------------------------
        public Packet(ushort sequenceControl, byte serviceType, byte serviceSubtype, byte[] data)
        {
            SequenceControl = sequenceControl;
            ServiceType = serviceType;
            ServiceSubtype = serviceSubtype;
            Nbytes = (ushort)(data != null ? data.Length : 0);
            Data = data ?? Array.Empty<byte>();
            totalNumberOfBytes = 2 + 1 + 1 + 2 + Nbytes; // header + data
        }

        // ----------------------------------------------------------
        // Constructor 2: Create packet by deserializing a byte array
        // ----------------------------------------------------------
        public Packet(byte[] raw)
        {
            if (raw == null || raw.Length < 6)
                throw new ArgumentException("Invalid packet length");

            int index = 0; // index of read byte

            // Sequence Control (ushort, big-endian)
            byte[] seqBytes = { raw[index + 1], raw[index] };
            SequenceControl = BitConverter.ToUInt16(seqBytes, 0);
            index += 2;

            // Service Type and Subtype
            ServiceType = raw[index++];
            ServiceSubtype = raw[index++];

            // nBytes (ushort, big-endian)
            byte[] lenBytes = { raw[index + 1], raw[index] };
            Nbytes = BitConverter.ToUInt16(lenBytes, 0);
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

            // Sequence Control
            ushort seqBE = (ushort)IPAddress.HostToNetworkOrder((short)SequenceControl);
            Array.Copy(BitConverter.GetBytes(seqBE), 0, buffer, index, 2);
            index += 2;

            // Service Type and Subtype
            buffer[index++] = ServiceType;
            buffer[index++] = ServiceSubtype;

            // nBytes
            ushort lenBE = (ushort)IPAddress.HostToNetworkOrder((short)Nbytes);
            Array.Copy(BitConverter.GetBytes(lenBE), 0, buffer, index, 2);
            index += 2;

            // Data
            if (Data != null && Data.Length > 0)
                Array.Copy(Data, 0, buffer, index, Data.Length);

            return buffer;
        }

        // ----------------------------------------------------------
        // Printi packet info
        // ----------------------------------------------------------
        public override string ToString()
        {
            return $"SeqCtrl={SequenceControl}, Service={ServiceType}, Subservice={ServiceSubtype}, Len={Nbytes}, Total={totalNumberOfBytes}";
        }
    }

}
