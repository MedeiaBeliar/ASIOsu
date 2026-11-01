using System.Text;

namespace ASIOsu.Services;

public sealed record ExportSurface(ushort Machine, ushort OrdinalBase, IReadOnlyDictionary<ushort, string> Names);

public static class PeExports
{
    public static ExportSurface Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.ASCII, true);
        if (reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("DOS 헤더가 없습니다.");
        stream.Position = 0x3C;
        uint peOffset = reader.ReadUInt32();
        if (peOffset > stream.Length - 256) throw new InvalidDataException("PE 헤더 위치가 올바르지 않습니다.");
        stream.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("PE 서명이 없습니다.");
        ushort machine = reader.ReadUInt16();
        ushort sectionCount = reader.ReadUInt16();
        stream.Position += 12;
        ushort optionalSize = reader.ReadUInt16();
        stream.Position += 2;
        long optional = stream.Position;
        if (reader.ReadUInt16() != 0x20B) throw new InvalidDataException("x64 PE 파일이 아닙니다.");
        stream.Position = optional + 112;
        uint exportRva = reader.ReadUInt32();
        uint exportSize = reader.ReadUInt32();
        if (exportRva == 0 || exportSize == 0) throw new InvalidDataException("export table이 없습니다.");

        stream.Position = optional + optionalSize;
        var sections = new List<Section>();
        for (int index = 0; index < sectionCount; index++)
        {
            stream.Position += 8;
            uint virtualSize = reader.ReadUInt32();
            uint virtualAddress = reader.ReadUInt32();
            uint rawSize = reader.ReadUInt32();
            uint rawAddress = reader.ReadUInt32();
            stream.Position += 16;
            sections.Add(new(virtualAddress, Math.Max(virtualSize, rawSize), rawAddress));
        }

        long Offset(uint rva)
        {
            Section section = sections.FirstOrDefault(item => rva >= item.VirtualAddress && rva - item.VirtualAddress < item.Size)
                              ?? throw new InvalidDataException("PE RVA가 section을 벗어났습니다.");
            long offset = section.RawAddress + (rva - section.VirtualAddress);
            if (offset < 0 || offset >= stream.Length) throw new InvalidDataException("PE RVA 변환이 파일을 벗어났습니다.");
            return offset;
        }

        stream.Position = Offset(exportRva) + 16;
        uint ordinalBase = reader.ReadUInt32();
        uint functionCount = reader.ReadUInt32();
        uint nameCount = reader.ReadUInt32();
        stream.Position += 4;
        uint nameTable = reader.ReadUInt32();
        uint ordinalTable = reader.ReadUInt32();
        if (ordinalBase > ushort.MaxValue || functionCount > 65535 || nameCount > 65535) throw new InvalidDataException("export 개수가 올바르지 않습니다.");
        var names = new Dictionary<ushort, string>();
        for (uint index = 0; index < nameCount; index++)
        {
            stream.Position = Offset(nameTable) + index * 4;
            uint nameRva = reader.ReadUInt32();
            stream.Position = Offset(ordinalTable) + index * 2;
            ushort functionIndex = reader.ReadUInt16();
            uint ordinal = ordinalBase + functionIndex;
            if (functionIndex >= functionCount || ordinal > ushort.MaxValue) throw new InvalidDataException("export ordinal이 올바르지 않습니다.");
            stream.Position = Offset(nameRva);
            var bytes = new List<byte>();
            while (bytes.Count < 1024)
            {
                byte value = reader.ReadByte();
                if (value == 0) break;
                bytes.Add(value);
            }
            if (bytes.Count == 0 || bytes.Count == 1024) throw new InvalidDataException("export 이름이 올바르지 않습니다.");
            names.Add((ushort)ordinal, Encoding.ASCII.GetString(bytes.ToArray()));
        }
        return new(machine, (ushort)ordinalBase, names);
    }

    private sealed record Section(uint VirtualAddress, uint Size, uint RawAddress);
}

