namespace Common
{
    public class Report : DataField
    {
        public Report(byte[] raw) : base(raw)
        {

        }
        public Report(long timeStamp, ushort sequenceControl, byte serviceType, byte serviceSubtype, byte[] data) : base(timeStamp, sequenceControl, serviceType, serviceSubtype, data)
        {

        }
    }

}
