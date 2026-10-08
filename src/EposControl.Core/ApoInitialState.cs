using System.Buffers.Binary;

namespace EposControl.Core;

internal static class ApoInitialState
{
    internal const int StructureSize = 2112;

    // Recovered from service SHA256 350D5EC4...72B8DD44, initializer 0x00553D10.
    // Only fresh, pagefile-backed memory may receive these bytes.
    internal static byte[] Create()
    {
        var memory = new byte[StructureSize];
        BinaryPrimitives.WriteInt32LittleEndian(memory, 2);
        BinaryPrimitives.WriteInt32LittleEndian(memory.AsSpan(4), 4);
        BinaryPrimitives.WriteInt32LittleEndian(memory.AsSpan(180), 2);
        for (var offset = 1088; offset <= 1144; offset += 4)
            BinaryPrimitives.WriteSingleLittleEndian(memory.AsSpan(offset), -120);
        return memory;
    }
}
