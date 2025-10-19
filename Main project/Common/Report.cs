namespace Common
{
    public class Report : Packet
    {
        public Report(byte[] raw) : base(raw)
        {

        }
        public Report(long timeStamp, byte applicationID, ushort sequenceControl, byte serviceType, byte serviceSubtype, byte[] data) : base(timeStamp, applicationID,  sequenceControl, serviceType, serviceSubtype, data)
        {

        }
    }
}
