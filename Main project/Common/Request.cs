using System.Net.Sockets;

namespace Common
{
    public class Request : Packet
    {
        public Request(byte[] raw) : base(raw)
        {

        }
        public Request(long timeStamp, byte applicationID, ushort sequenceControl, byte serviceType, byte serviceSubtype, byte[] data) : base(timeStamp, applicationID, sequenceControl, serviceType, serviceSubtype, data)
        {

        }
    }
}
