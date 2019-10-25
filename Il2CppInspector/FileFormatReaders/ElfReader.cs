﻿/*
    Copyright 2017 Perfare - https://github.com/Perfare/Il2CppDumper
    Copyright 2017-2019 Katy Coe - http://www.hearthcode.org - http://www.djkaty.com

    All rights reserved.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Il2CppInspector
{
    internal class ElfReader32 : ElfReader<uint, elf_32_phdr, elf_32_sym, ElfReader32, ElfMath32>
    {
        public ElfReader32(Stream stream) : base(stream) {
            ElfReloc.GetRelocType = info => (Elf) (info & 0xff);
            ElfReloc.GetSymbolIndex = info => info >> 8;
        }

        public override int Bits => 32;
        protected override Elf ArchClass => Elf.ELFCLASS32;

        protected override void Write(BinaryWriter writer, uint value) => writer.Write(value);
    }

    internal class ElfReader64 : ElfReader<ulong, elf_64_phdr, elf_64_sym, ElfReader64, ElfMath64>
    {
        public ElfReader64(Stream stream) : base(stream) {
            ElfReloc.GetRelocType = info => (Elf) (info & 0xffff_ffff);
            ElfReloc.GetSymbolIndex = info => info >> 32;
        }

        public override int Bits => 64;
        protected override Elf ArchClass => Elf.ELFCLASS64;

        protected override void Write(BinaryWriter writer, ulong value) => writer.Write(value);
    }

    // NOTE: What we really should have done here is add a TWord type parameter to FileFormatReader<T>
    // then we could probably avoid most of this
    interface IElfMath<TWord> where TWord : struct
    {
        TWord Add(TWord a, TWord b);
        TWord Sub(TWord a, TWord b);
        TWord Div(TWord a, TWord b);
        TWord Div(TWord a, int b);
        int Int(TWord a);
        long Long(TWord a);
        ulong ULong(TWord a);
        bool Gt(TWord a, TWord b);
        uint[] UIntArray(TWord[] a);
    }

    internal class ElfMath32 : IElfMath<uint>
    {
        public uint Add(uint a, uint b) => a + b;
        public uint Sub(uint a, uint b) => a - b;
        public uint Div(uint a, uint b) => a / b;
        public uint Div(uint a, int b) => a / (uint) b;
        public int Int(uint a) => (int) a;
        public long Long(uint a) => a;
        public ulong ULong(uint a) => a;
        public bool Gt(uint a, uint b) => a > b;
        public uint[] UIntArray(uint[] a) => a;
    }
    internal class ElfMath64 : IElfMath<ulong>
    {
        public ulong Add(ulong a, ulong b) => a + b;
        public ulong Sub(ulong a, ulong b) => a - b;
        public ulong Div(ulong a, ulong b) => a / b;
        public ulong Div(ulong a, int b) => a / (uint) b;
        public int Int(ulong a) => (int) a;
        public long Long(ulong a) => (long) a;
        public ulong ULong(ulong a) => a;
        public bool Gt(ulong a, ulong b) => a > b;
        public uint[] UIntArray(ulong[] a) => Array.ConvertAll(a, x => (uint) x);
    }

    internal abstract class ElfReader<TWord, TPHdr, TSym, TReader, TMath> : FileFormatReader<TReader>
        where TWord : struct
        where TPHdr : Ielf_phdr<TWord>, new()
        where TSym : Ielf_sym<TWord>, new()
        where TMath : IElfMath<TWord>, new()
        where TReader : FileFormatReader<TReader>
    {
        private readonly TMath math = new TMath();

        // Internal relocation entry helper
        protected class ElfReloc
        {
            public Elf Type;
            public TWord Offset;
            public TWord? Addend;
            public TWord SymbolTable;
            public TWord SymbolIndex;

            // Equality based on target address
            public override bool Equals(object obj) => obj is ElfReloc reloc && Equals(reloc);

            public bool Equals(ElfReloc other) {
                return Offset.Equals(other.Offset);
            }

            public override int GetHashCode() => Offset.GetHashCode();

            // Cast operators (makes the below code MUCH easier to read)
            public ElfReloc(elf_rel<TWord> rel, TWord symbolTable) {
                Offset = rel.r_offset;
                Addend = null;
                Type = GetRelocType(rel.r_info);
                SymbolIndex = GetSymbolIndex(rel.r_info);
                SymbolTable = symbolTable;
            }

            public ElfReloc(elf_rela<TWord> rela, TWord symbolTable)
                : this(new elf_rel<TWord> { r_info = rela.r_info, r_offset = rela.r_offset }, symbolTable) =>
                Addend = rela.r_addend;

            public static Func<TWord, Elf> GetRelocType;
            public static Func<TWord, TWord> GetSymbolIndex;
        }

        // See also: https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/operators/sizeof
        private int Sizeof(Type type) {
            int size = 0;
            foreach (var i in type.GetTypeInfo().GetFields()) {
                if (i.FieldType == typeof(byte) || i.FieldType == typeof(sbyte))
                    size += sizeof(byte);
                if (i.FieldType == typeof(long) || i.FieldType == typeof(ulong))
                    size += sizeof(ulong);
                if (i.FieldType == typeof(int) || i.FieldType == typeof(uint))
                    size += sizeof(uint);
                if (i.FieldType == typeof(short) || i.FieldType == typeof(ushort))
                    size += sizeof(ushort);
            }
            return size;
        }

        private TPHdr[] program_header_table;
        private elf_shdr<TWord>[] section_header_table;
        private elf_dynamic<TWord>[] dynamic_table;
        private elf_header<TWord> elf_header;

        public ElfReader(Stream stream) : base(stream) { }

        public override string Format => "ELF";

        public override string Arch => (Elf) elf_header.e_machine switch {
            Elf.EM_386 => "x86",
            Elf.EM_ARM => "ARM",
            Elf.EM_X86_64 => "x64",
            Elf.EM_AARCH64 => "ARM64",
            _ => "Unsupported"
        };

        public override int Bits => (elf_header.m_arch == (uint) Elf.ELFCLASS64) ? 64 : 32;

        private elf_shdr<TWord> getSection(Elf sectionIndex) => section_header_table.FirstOrDefault(x => x.sh_type == (uint) sectionIndex);
        private IEnumerable<elf_shdr<TWord>> getSections(Elf sectionIndex) => section_header_table.Where(x => x.sh_type == (uint) sectionIndex);
        private TPHdr getProgramHeader(Elf programIndex) => program_header_table.FirstOrDefault(x => x.p_type == (uint) programIndex);
        private elf_dynamic<TWord> getDynamic(Elf dynamicIndex) => dynamic_table?.FirstOrDefault(x => (Elf) math.ULong(x.d_tag) == dynamicIndex);

        protected abstract Elf ArchClass { get; }

        protected abstract void Write(BinaryWriter writer, TWord value);

        protected override bool Init() {
            elf_header = ReadObject<elf_header<TWord>>();

            // Check for magic bytes
            if ((Elf) elf_header.m_dwFormat != Elf.ELFMAG)
                return false;

            // 64-bit not supported
            if ((Elf) elf_header.m_arch != ArchClass)
                return false;

            program_header_table = ReadArray<TPHdr>(math.Long(elf_header.e_phoff), elf_header.e_phnum);
            section_header_table = ReadArray<elf_shdr<TWord>>(math.Long(elf_header.e_shoff), elf_header.e_shnum);

            if (getProgramHeader(Elf.PT_DYNAMIC) is TPHdr PT_DYNAMIC)
                dynamic_table = ReadArray<elf_dynamic<TWord>>(math.Long(PT_DYNAMIC.p_offset), (int) (math.Long(PT_DYNAMIC.p_filesz) / Sizeof(typeof(elf_dynamic<TWord>))));

            // Get global offset table
            var _GLOBAL_OFFSET_TABLE_ = getDynamic(Elf.DT_PLTGOT)?.d_un;
            if (_GLOBAL_OFFSET_TABLE_ == null)
                throw new InvalidOperationException("Unable to get GLOBAL_OFFSET_TABLE from PT_DYNAMIC");
            GlobalOffset = math.ULong((TWord) _GLOBAL_OFFSET_TABLE_);

            // Find all relocations; target address => (rela header (rels are converted to rela), symbol table base address, is rela?)
            var rels = new HashSet<ElfReloc>();

            // Two types: add value from offset in image, and add value from specified addend
            foreach (var relSection in getSections(Elf.SHT_REL))
                rels.UnionWith(
                    from rel in ReadArray<elf_rel<TWord>>(math.Long(relSection.sh_offset), math.Int(math.Div(relSection.sh_size, relSection.sh_entsize)))
                    select new ElfReloc(rel, section_header_table[relSection.sh_link].sh_offset));
                
            foreach (var relaSection in getSections(Elf.SHT_RELA))
                rels.UnionWith(
                    from rela in ReadArray<elf_rela<TWord>>(math.Long(relaSection.sh_offset), math.Int(math.Div(relaSection.sh_size, relaSection.sh_entsize)))
                    select new ElfReloc(rela, section_header_table[relaSection.sh_link].sh_offset));

            // Relocations in dynamic section
            if (getDynamic(Elf.DT_REL) is elf_dynamic<TWord> dt_rel) {
                var dt_rel_count = math.Div(getDynamic(Elf.DT_RELSZ).d_un, getDynamic(Elf.DT_RELENT).d_un);
                var dt_rel_list = ReadArray<elf_rel<TWord>>(MapVATR(math.ULong(dt_rel.d_un)), math.Int(dt_rel_count));
                var dt_symtab = getDynamic(Elf.DT_SYMTAB).d_un;
                rels.UnionWith(from rel in dt_rel_list select new ElfReloc(rel, dt_symtab));
            }

            if (getDynamic(Elf.DT_RELA) is elf_dynamic<TWord> dt_rela) {
                var dt_rela_count = math.Div(getDynamic(Elf.DT_RELASZ).d_un, getDynamic(Elf.DT_RELAENT).d_un);
                var dt_rela_list = ReadArray<elf_rela<TWord>>(MapVATR(math.ULong(dt_rela.d_un)), math.Int(dt_rela_count));
                var dt_symtab = getDynamic(Elf.DT_SYMTAB).d_un;
                rels.UnionWith(from rela in dt_rela_list select new ElfReloc(rela, dt_symtab));
            }

            // Process relocations
            // WARNING: This modifies the stream passed in the constructor
            if (BaseStream is FileStream)
                throw new InvalidOperationException("Input stream to ElfReader is a file. Please supply a mutable stream source.");

            var writer = new BinaryWriter(BaseStream);
            var relsz = Sizeof(typeof(TSym));

            foreach (var rel in rels) {
                var symValue = ReadObject<TSym>(math.Long(rel.SymbolTable) + math.Long(rel.SymbolIndex) * relsz).st_value; // S

                // The addend is specified in the struct for rela, and comes from the target location for rel
                Position = MapVATR(math.ULong(rel.Offset));
                var addend = rel.Addend ?? ReadObject<TWord>(); // A

                // Only handle relocation types we understand, skip the rest
                // Relocation types from https://docs.oracle.com/cd/E23824_01/html/819-0690/chapter6-54839.html#scrolltoc
                // and https://studfiles.net/preview/429210/page:18/
                // and http://infocenter.arm.com/help/topic/com.arm.doc.ihi0056b/IHI0056B_aaelf64.pdf (AArch64)
                (TWord newValue, bool recognized) result = (rel.Type, (Elf) elf_header.e_machine) switch {
                    (Elf.R_ARM_ABS32, Elf.EM_ARM) => (math.Add(symValue, addend), true), // S + A
                    (Elf.R_ARM_REL32, Elf.EM_ARM) => (math.Add(math.Sub(symValue, rel.Offset), addend), true), // S - P + A
                    (Elf.R_ARM_COPY, Elf.EM_ARM) => (symValue, true), // S

                    (Elf.R_AARCH64_ABS64, Elf.EM_AARCH64) => (math.Add(symValue, addend), true), // S + A
                    (Elf.R_AARCH64_PREL64, Elf.EM_AARCH64) => (math.Sub(math.Add(symValue, addend), rel.Offset), true), // S + A - P
                    (Elf.R_AARCH64_GLOB_DAT, Elf.EM_AARCH64) => (math.Add(symValue, addend), true), // S + A
                    (Elf.R_AARCH64_JUMP_SLOT, Elf.EM_AARCH64) => (math.Add(symValue, addend), true), // S + A
                    (Elf.R_AARCH64_RELATIVE, Elf.EM_AARCH64) => (math.Add(symValue, addend), true), // Delta(S) + A

                    (Elf.R_386_32, Elf.EM_386) => (math.Add(symValue, addend), true), // S + A
                    (Elf.R_386_PC32, Elf.EM_386) => (math.Sub(math.Add(symValue, addend), rel.Offset), true), // S + A - P
                    (Elf.R_386_GLOB_DAT, Elf.EM_386) => (symValue, true), // S
                    (Elf.R_386_JMP_SLOT, Elf.EM_386) => (symValue, true), // S

                    (Elf.R_AMD64_64, Elf.EM_AARCH64) => (math.Add(symValue, addend), true), // S + A

                    _ => (default(TWord), false)
                };

                if (result.recognized) {
                    Position = MapVATR(math.ULong(rel.Offset));
                    Write(writer, result.newValue);
                }
            }
            Console.WriteLine($"Processed {rels.Count} relocations");

            return true;
        }

        public override Dictionary<string, ulong> GetSymbolTable() {
            // Three possible symbol tables in ELF files
            var pTables = new List<(TWord offset, TWord count, TWord strings)>();

            // String table (a sequence of null-terminated strings, total length in sh_size
            var SHT_STRTAB = getSection(Elf.SHT_STRTAB);

            if (SHT_STRTAB != null) {
                // Section header shared object symbol table (.symtab)
                if (getSection(Elf.SHT_SYMTAB) is elf_shdr<TWord> SHT_SYMTAB)
                    pTables.Add((SHT_SYMTAB.sh_offset, math.Div(SHT_SYMTAB.sh_size, SHT_SYMTAB.sh_entsize), SHT_STRTAB.sh_offset));
                
                // Section header executable symbol table (.dynsym)
                if (getSection(Elf.SHT_DYNSYM) is elf_shdr<TWord> SHT_DYNSYM)
                    pTables.Add((SHT_DYNSYM.sh_offset, math.Div(SHT_DYNSYM.sh_size, SHT_DYNSYM.sh_entsize), SHT_STRTAB.sh_offset));
            }

            // Symbol table in dynamic section (DT_SYMTAB)
            // Normally the same as .dynsym except that .dynsym may be removed in stripped binaries

            // Dynamic string table
            if (getDynamic(Elf.DT_STRTAB) is elf_dynamic<TWord> DT_STRTAB) {
                if (getDynamic(Elf.DT_SYMTAB) is elf_dynamic<TWord> DT_SYMTAB) {
                    // Find the next pointer in the dynamic table to calculate the length of the symbol table
                    var end = (from x in dynamic_table where math.Gt(x.d_un, DT_SYMTAB.d_un) orderby x.d_un select x).First().d_un;

                    // Dynamic symbol table
                    pTables.Add((DT_SYMTAB.d_un, math.Div(math.Sub(end, DT_SYMTAB.d_un), Sizeof(typeof(TSym))), DT_STRTAB.d_un));
                }
            }

            // Now iterate through all of the symbol and string tables we found to build a full list
            var symbolTable = new Dictionary<string, ulong>();

            foreach (var pTab in pTables) {
                var symbol_table = ReadArray<TSym>(math.Long(pTab.offset), math.Int(pTab.count));

                foreach (var symbol in symbol_table) {
                    var name = ReadNullTerminatedString(math.Long(pTab.strings) + symbol.st_name);

                    // Avoid duplicates
                    symbolTable.TryAdd(name, math.ULong(symbol.st_value));
                }
            }

            return symbolTable;
        }

        public override uint[] GetFunctionTable() {
            // INIT_ARRAY contains a list of pointers to initialization functions (not all functions in the binary)
            // INIT_ARRAYSZ contains the size of INIT_ARRAY

            var init = MapVATR(math.ULong(getDynamic(Elf.DT_INIT_ARRAY).d_un));
            var size = getDynamic(Elf.DT_INIT_ARRAYSZ).d_un;

            return math.UIntArray(ReadArray<TWord>(init, math.Int(size) / (Bits / 8)));
        }

        // Map a virtual address to an offset into the image file. Throws an exception if the virtual address is not mapped into the file.
        // Note if uiAddr is a valid segment but filesz < memsz and the adjusted uiAddr falls between the range of filesz and memsz,
        // an exception will be thrown. This area of memory is assumed to contain all zeroes.
        public override uint MapVATR(ulong uiAddr) {
            // Additions in the argument to MapVATR may cause an overflow which should be discarded for 32-bit files
            if (Bits == 32)
                uiAddr &= 0xffff_ffff;
             var program_header_table = this.program_header_table.First(x => uiAddr >= math.ULong(x.p_vaddr) && uiAddr <= math.ULong(math.Add(x.p_vaddr, x.p_filesz)));
            return (uint) (uiAddr - math.ULong(math.Sub(program_header_table.p_vaddr, program_header_table.p_offset)));
        }
    }
}