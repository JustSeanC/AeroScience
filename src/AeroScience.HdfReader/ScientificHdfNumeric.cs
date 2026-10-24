using PureHDF.VOL.Native;

namespace AeroScience.Hdf;

// Validate physical layouts before assigning CLR numeric names. Raw reads
// preserve fill/NaN payloads and allow endian conversion without scaling.
public static class ScientificHdfNumeric
{
    public static (string Type,bool BigEndian) Describe(NativeDataset dataset)=>Describe(dataset.InternalDataType);
    public static (string Type,bool BigEndian) Describe(NativeAttribute attribute)=>Describe(attribute.Message.Datatype);
    private static (string,bool) Describe(DatatypeMessage type)
    {
        var bits=checked(type.Size*8);
        if(type.BitField is FixedPointBitFieldDescription integer&&type.Properties is [FixedPointPropertyDescription p]
            &&type.Size is 1 or 2 or 4 or 8&&p.BitOffset==0&&p.BitPrecision==bits)
            return ((integer.IsSigned?"Int":"UInt")+bits,Order(integer.ByteOrder));
        if(type.BitField is FloatingPointBitFieldDescription floating&&type.Properties is [FloatingPointPropertyDescription f]
            &&type.Size is 4 or 8&&f.BitOffset==0&&f.BitPrecision==bits&&floating.SignLocation==bits-1
            &&floating.MantissaNormalization==MantissaNormalization.MsbIsNotStoredButImplied&&f.MantissaLocation==0
            &&f.ExponentLocation==(type.Size==4?23:52)&&f.ExponentSize==(type.Size==4?8:11)
            &&f.MantissaSize==(type.Size==4?23:52)&&f.ExponentBias==(type.Size==4?127:1023))
            return ("Float"+bits,Order(floating.ByteOrder));
        throw new NotSupportedException("HDF numeric representation is not a supported full-width integer or IEEE float.");
    }
    private static bool Order(ByteOrder order)=>order switch {ByteOrder.LittleEndian=>false,ByteOrder.BigEndian=>true,_=>throw new NotSupportedException("HDF byte order is unsupported.")};
}
