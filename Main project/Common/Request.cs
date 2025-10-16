namespace Common
{
    public class Request : DataField
    {
        public Request(byte[] raw) : base(raw)
        {

        }
        public Request(long timeStamp, ushort sequenceControl, byte serviceType, byte serviceSubtype, byte[] data) : base(timeStamp, sequenceControl, serviceType, serviceSubtype, data)
        {

        }
    }
}
