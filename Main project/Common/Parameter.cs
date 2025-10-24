using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Common
{
    public class Parameter
    {
        public object Value { get; set; }
        public byte ID { get; }
        public Type DataType { get; }

        public int Length { get; }

        //public byte TypeID { get; }

        public enum TypeID
        {
            Unknown = 0,

            // Unsigned integer types
            Byte = 1,
            UShort = 2,
            UInt = 3,
            ULong = 4,

            // Signed integer types
            Short = 5,
            Int = 6,
            Long = 7,

            // Floating point
            Float = 8,
            Double = 9,

            // Logical
            Boolean = 10
        }

        public Parameter(byte ID, object value)
        {
            this.Value = value;
            this.ID = ID;
            this.DataType = value?.GetType() ?? typeof(object);
            this.Length = 2 + GetLength(ToTypeID(DataType));
        }
        public byte[] Serialize()
        {
            byte[] parameterBytes;

            switch (Value)
            {
                case byte b:
                    parameterBytes = new[] { b };
                    break;
                case bool flag:
                    parameterBytes = BitConverter.GetBytes(flag);
                    break;
                case short s:
                    parameterBytes = BitConverter.GetBytes(s);
                    break;
                case ushort us:
                    parameterBytes = BitConverter.GetBytes(us);
                    break;
                case int i:
                    parameterBytes = BitConverter.GetBytes(i);
                    break;
                case uint ui:
                    parameterBytes = BitConverter.GetBytes(ui);
                    break;
                case long l:
                    parameterBytes = BitConverter.GetBytes(l);
                    break;
                case ulong ul:
                    parameterBytes = BitConverter.GetBytes(ul);
                    break;
                case float f:
                    parameterBytes = BitConverter.GetBytes(f);
                    break;
                case double d:
                    parameterBytes = BitConverter.GetBytes(d);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported parameter type: {Value.GetType().Name}");
            }
            // Convert from to big-endian byte order if necessary
            if (BitConverter.IsLittleEndian)
                Array.Reverse(parameterBytes);

            int nBytes = parameterBytes.Length;
            byte[] buffer = new byte[2 + nBytes];

            buffer[0] = (byte) ID;
            buffer[1] = (byte)ToTypeID(DataType);
            Array.Copy(parameterBytes, 0, buffer, 2, nBytes);
            return buffer;
        }
        public Parameter(byte[] raw, int startIndex)
        {
            if (raw == null || raw.Length < 2 + startIndex)
                throw new ArgumentException("Invalid parameter buffer.");

            int index = startIndex;

            // --- Extract ID (2 bytes, big-endian) ---
            this.ID = (byte)raw[index];
            index++; ;

            // --- Extract TypeID (1 byte) ---
            TypeID typeID = (TypeID)raw[index];
            index++;
            this.DataType = GetType(typeID);
            int valueLength = GetLength(typeID);
            this.Length = 2 + valueLength;

            // --- Extract parameter bytes ---
            byte[] valueBytes = new byte[valueLength];
            Array.Copy(raw, index, valueBytes, 0, valueLength);

            // Convert from big-endian to host byte order if necessary
            if (BitConverter.IsLittleEndian)
                Array.Reverse(valueBytes);

            // --- Convert bytes to value ---
            this.Value = typeID switch
            {
                TypeID.Byte => valueBytes[0],
                TypeID.Boolean => BitConverter.ToBoolean(valueBytes, 0),
                TypeID.Short => BitConverter.ToInt16(valueBytes, 0),
                TypeID.UShort => BitConverter.ToUInt16(valueBytes, 0),
                TypeID.Int => BitConverter.ToInt32(valueBytes, 0),
                TypeID.UInt => BitConverter.ToUInt32(valueBytes, 0),
                TypeID.Long => BitConverter.ToInt64(valueBytes, 0),
                TypeID.ULong => BitConverter.ToUInt64(valueBytes, 0),
                TypeID.Float => BitConverter.ToSingle(valueBytes, 0),
                TypeID.Double => BitConverter.ToDouble(valueBytes, 0),
                _ => throw new InvalidOperationException($"Unsupported TypeID: {typeID}")
            };

            
        }
        public static TypeID ToTypeID(Type type) => type switch
        {
            var t when t == typeof(byte) => TypeID.Byte,
            var t when t == typeof(ushort) => TypeID.UShort,
            var t when t == typeof(uint) => TypeID.UInt,
            var t when t == typeof(ulong) => TypeID.ULong,
            var t when t == typeof(short) => TypeID.Short,
            var t when t == typeof(int) => TypeID.Int,
            var t when t == typeof(long) => TypeID.Long,
            var t when t == typeof(float) => TypeID.Float,
            var t when t == typeof(double) => TypeID.Double,
            var t when t == typeof(bool) => TypeID.Boolean,
            _ => TypeID.Unknown
        };
        private Type GetType(TypeID typeID)
        {

            Type dataType = typeID switch
            {
                // Unsigned integer types
                TypeID.Byte     => typeof(byte),
                TypeID.UShort   => typeof(ushort),
                TypeID.UInt     => typeof(uint),
                TypeID.ULong    => typeof(ulong),
                // Signed integer types
                TypeID.Short    => typeof(short),
                TypeID.Int      => typeof(int),
                TypeID.Long     => typeof(long),
                // Floating point
                TypeID.Float    => typeof(float),
                TypeID.Double   => typeof(double),
                // Logical
                TypeID.Boolean  => typeof(bool),
                _ => typeof(object),
            };
            return dataType;
        }

        private int GetLength(TypeID typeID)
        {

            return typeID switch
            {
                // Unsigned integer types
                TypeID.Byte => 1,
                TypeID.UShort => 2,
                TypeID.UInt => 4,
                TypeID.ULong => 8,
                // Signed integer types
                TypeID.Short => 2,
                TypeID.Int => 4,
                TypeID.Long => 8,
                // Floating point
                TypeID.Float => 4,
                TypeID.Double => 8,
                // Logical
                TypeID.Boolean => 1,
                _ => 0,
            };
        }
    }
}
