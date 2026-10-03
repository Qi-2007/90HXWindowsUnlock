"""Static inspection/extraction of the user-confirmed 469dc0c EFI; never executes it."""
import argparse
import hashlib
import re
import struct
from pathlib import Path

EFI_SHA256 = "6d7a035713a4d8f226e5523156d81154ef04dffef21727dcd3ee262917c2e0ed"


def load(path):
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != EFI_SHA256:
        raise ValueError("EFI does not match the user-confirmed new binary")
    for match in re.finditer(b"\x7fELF", data):
        base = match.start()
        if data[base + 4:base + 7] != b"\x02\x01\x01":
            continue
        if struct.unpack_from("<HH", data, base + 16) != (1, 62):
            continue
        sh = struct.unpack_from("<Q", data, base + 40)[0]
        count = struct.unpack_from("<H", data, base + 60)[0]
        if not 0 < count <= 128 or base + sh + count * 64 > len(data):
            continue
        sections = [struct.unpack_from("<IIQQQQIIQQ", data, base + sh + i * 64)
                    for i in range(count)]
        end = max([sh + count * 64] + [s[4] + s[5] for s in sections if s[1] != 8])
        if base + end > len(data):
            raise ValueError("Embedded ELF exceeds its enclosing EFI")
        core = data[base:base + end]
        sym = next(s for s in sections if s[1] == 2)
        st = sections[sym[6]]
        strings = core[st[4]:st[4] + st[5]]
        symbols = []
        for off in range(sym[4], sym[4] + sym[5], 24):
            name, info, _, sec, value, size = struct.unpack_from("<IBBHQQ", core, off)
            symbols.append((strings[name:strings.index(b"\0", name)].decode(),
                            info, sec, value, size))
        return core, sections, symbols, base
    raise ValueError("No valid x64 relocatable ELF")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("efi", type=Path)
    parser.add_argument("--extract", type=Path)
    parser.add_argument("--symbol")
    parser.add_argument("--range", dest="code_range", help="Static .text range START:END (hex)")
    parser.add_argument("--limit", type=int, default=0)
    args = parser.parse_args()
    core, sections, symbols, base = load(args.efi)
    print("ELF offset=", base, "bytes=", len(core), "sha256=", hashlib.sha256(core).hexdigest())
    if args.extract:
        args.extract.parent.mkdir(parents=True, exist_ok=True)
        args.extract.write_bytes(core)
        print("Extracted:", args.extract.resolve())
        if not args.symbol and not args.code_range:
            return
    if args.code_range:
        start, end = [int(n, 16) for n in args.code_range.split(":")]
        if not 0 <= start < end <= sections[1][5]:
            raise ValueError("Invalid .text range")
        symbols.append(("STATIC_RANGE", 2, 1, start, end - start))
        args.symbol = "^STATIC_RANGE$"
    if not args.symbol:
        for name, info, sec, value, size in symbols:
            if name and (sec == 0 or re.search(r"handoff|managed|session|pcie|permissive|open_plm", name)):
                print(sec, hex(value), size, name)
        return
    from capstone import Cs, CS_ARCH_X86, CS_MODE_64
    decoder = Cs(CS_ARCH_X86, CS_MODE_64)
    relocs = {}
    for s in sections:
        if s[1] == 4:
            for off in range(s[4], s[4] + s[5], 24):
                target, info, addend = struct.unpack_from("<QQq", core, off)
                relocs[s[7], target] = (symbols[info >> 32][0], addend)
    for name, info, sec, value, size in symbols:
        if not sec or not re.search(args.symbol, name):
            continue
        print("\n", name, "section=", sec, "value=", hex(value), "size=", size)
        s = sections[sec]
        if info & 15 != 2:
            print(core[s[4] + value:s[4] + value + size].hex(" "))
            continue
        for i, ins in enumerate(decoder.disasm(core[s[4] + value:s[4] + value + size], value)):
            if args.limit and i >= args.limit:
                print("[limit]")
                break
            refs = [f"{symbol}{addend:+#x}" for (section, offset), (symbol, addend) in relocs.items()
                    if section == sec and ins.address <= offset < ins.address + ins.size]
            print(f"{ins.address:08x}: {ins.mnemonic:8s} {ins.op_str:50s} {' '.join(refs)}")


if __name__ == "__main__":
    main()
