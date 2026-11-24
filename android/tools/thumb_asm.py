"""Small Thumb-1 assembler for the libgame.so code caves (patch_native.py).

Supports the subset the caves need. Text lines (`;` starts a comment):
    label:
    movs r0, #5            adds r1, #4          adds r0, r1, #3      adds r0, r1, r2
    subs ...               mov rd, rm           cmp r0, #3           cmp r0, r1
    ldr r0, [r1, #8]       ldr r0, [r1, r2]     ldr r0, [sp, #4]     ldrb/strb/str likewise
    lsls r0, r1, #3        lsrs r0, r1, #16     ands/orrs/muls rd, rm
    push {r4, lr}          pop {r4, pc}         add sp, #8           sub sp, #8
    bx lr                  nop
    b label                beq/bne/blt/bge/ble/bgt/bmi/bpl/bhi/bls label
    bl 0xADDR | bl label   ldr r0, =value       lea r0, 0xADDR   (PIC address via ldr + add pc)
    .asciz "text"          (aligned data, addressable with `lea rX, =name`/label)
"""
import re

_COND = {"beq": 0x0, "bne": 0x1, "bcs": 0x2, "bhs": 0x2, "bcc": 0x3, "blo": 0x3, "bmi": 0x4, "bpl": 0x5,
         "bhi": 0x8, "bls": 0x9, "bge": 0xA, "blt": 0xB, "bgt": 0xC, "ble": 0xD}


def _reg(tok: str) -> int:
    tok = tok.strip().lower()
    names = {"sp": 13, "lr": 14, "pc": 15, "sb": 9, "sl": 10, "fp": 11, "ip": 12}
    if tok in names:
        return names[tok]
    m = re.fullmatch(r"r(\d+)", tok)
    if not m or int(m.group(1)) > 15:
        raise ValueError(f"bad register {tok!r}")
    return int(m.group(1))


def _imm(tok: str) -> int:
    tok = tok.strip().lstrip("#")
    return int(tok, 0)


def _split(ops: str) -> list[str]:
    out, depth, cur = [], 0, ""
    for ch in ops:
        if ch in "[{":
            depth += 1
        elif ch in "]}":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur.strip())
            cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur.strip())
    return out


def _bl(src: int, dst: int) -> bytes:
    off = dst - (src + 4)
    if not -4194304 <= off <= 4194302 or off & 1:
        raise ValueError(f"bl 0x{dst:x} out of range from 0x{src:x}")
    return ((0xF000 | ((off >> 12) & 0x7FF)).to_bytes(2, "little")
            + (0xF800 | ((off >> 1) & 0x7FF)).to_bytes(2, "little"))


def assemble(base: int, source: str, externs: dict | None = None, lenient: bool = False) -> tuple[bytes, dict[str, int]]:
    """Returns (bytes, labels). Literal pool and strings follow the code, 4-byte aligned."""
    items: list[tuple] = []   # ("i", mnemonic, ops) | ("l", name) | ("s", name, bytes)
    for raw in source.splitlines():
        line = raw.split(";", 1)[0].strip()
        if not line:
            continue
        if line.endswith(":"):
            items.append(("l", line[:-1].strip()))
            continue
        m = re.match(r'(\w+):\s*\.asciz\s+"(.*)"$', line)
        if m:
            items.append(("s", m.group(1), m.group(2).encode() + b"\0"))
            continue
        parts = line.split(None, 1)
        items.append(("i", parts[0].lower(), parts[1] if len(parts) > 1 else ""))

    labels: dict[str, int] = {}
    code: list[tuple] = []   # (addr, size, mnemonic, ops)
    pc = base
    for it in items:
        if it[0] == "l":
            labels[it[1]] = pc
        elif it[0] == "i":
            size = 4 if it[1] == "bl" else 2
            if it[1] == "lea":
                size = 4
            code.append((pc, size, it[1], it[2]))
            pc += size
    if pc & 3:
        pc += 2
    pool: dict[tuple, int] = {}
    # strings first (so lea can reference them), then literals
    data = bytearray()
    strings = [it for it in items if it[0] == "s"]
    for it in strings:
        labels[it[1]] = pc + len(data)
        data += it[2]
        while len(data) & 3:
            data.append(0)
    pool_base = pc + len(data)
    lits: list[tuple] = []
    for addr, size, mn, ops in code:
        if mn == "ldr" and "=" in ops:
            val = ops.split("=", 1)[1].strip()
            key = ("v", val)
            if key not in pool:
                pool[key] = pool_base + 4 * len(pool)
                lits.append(key)
        if mn == "lea":
            tgt = _split(ops)[1]
            key = ("lea", addr, tgt)
            pool[key] = pool_base + 4 * len(pool)
            lits.append(key)

    def value(tok: str) -> int:
        tok = tok.strip()
        if tok in labels:
            return labels[tok]
        if externs and tok in externs:
            return externs[tok]
        try:
            return int(tok, 0)
        except ValueError:
            if lenient:
                return 0
            raise

    out = bytearray()
    for addr, size, mn, ops in code:
        a = _split(ops)
        hw = None
        if mn == "nop":
            hw = 0x46C0
        elif mn in ("movs", "adds", "subs", "cmp") and len(a) == 2 and a[1].startswith("#"):
            rd, imm = _reg(a[0]), _imm(a[1])
            if not 0 <= imm <= 255:
                raise ValueError(f"{mn} imm {imm} at 0x{addr:x}")
            hw = {"movs": 0x2000, "cmp": 0x2800, "adds": 0x3000, "subs": 0x3800}[mn] | rd << 8 | imm
        elif mn in ("adds", "subs") and len(a) == 3 and a[2].startswith("#"):
            rd, rn, imm = _reg(a[0]), _reg(a[1]), _imm(a[2])
            hw = (0x1C00 if mn == "adds" else 0x1E00) | imm << 6 | rn << 3 | rd
        elif mn in ("adds", "subs") and len(a) == 3:
            rd, rn, rm = _reg(a[0]), _reg(a[1]), _reg(a[2])
            hw = (0x1800 if mn == "adds" else 0x1A00) | rm << 6 | rn << 3 | rd
        elif mn == "mov":
            rd, rm = _reg(a[0]), _reg(a[1])
            hw = 0x4600 | (rd >> 3) << 7 | rm << 3 | (rd & 7)
        elif mn == "cmp":
            hw = 0x4280 | _reg(a[1]) << 3 | _reg(a[0])
        elif mn in ("ands", "orrs", "muls"):
            rd, rm = _reg(a[0]), _reg(a[1])
            hw = {"ands": 0x4000, "orrs": 0x4300, "muls": 0x4340}[mn] | rm << 3 | rd
        elif mn in ("lsls", "lsrs", "asrs"):
            rd, rm, imm = _reg(a[0]), _reg(a[1]), _imm(a[2])
            hw = {"lsls": 0x0000, "lsrs": 0x0800, "asrs": 0x1000}[mn] | imm << 6 | rm << 3 | rd
        elif mn in ("ldr", "str", "ldrb", "strb") and "=" in ops:
            key = ("v", ops.split("=", 1)[1].strip())
            rd = _reg(a[0])
            disp = (pool[key] - ((addr + 4) & ~3)) // 4
            if not 0 <= disp <= 255:
                raise ValueError(f"literal out of range at 0x{addr:x}")
            hw = 0x4800 | rd << 8 | disp
        elif mn in ("ldr", "str", "ldrb", "strb"):
            rd = _reg(a[0])
            mem = [t.strip() for t in a[1].strip()[1:-1].split(",")]
            rn = _reg(mem[0])
            if len(mem) == 1:
                mem.append("#0")
            if mem[1].startswith("#"):
                off = _imm(mem[1])
                if rn == 13:
                    hw = (0x9800 if mn == "ldr" else 0x9000) | rd << 8 | off // 4
                elif mn in ("ldr", "str"):
                    if off % 4 or not 0 <= off <= 124:
                        raise ValueError(f"{mn} offset {off} at 0x{addr:x}")
                    hw = (0x6800 if mn == "ldr" else 0x6000) | (off // 4) << 6 | rn << 3 | rd
                else:
                    if not 0 <= off <= 31:
                        raise ValueError(f"{mn} offset {off} at 0x{addr:x}")
                    hw = (0x7800 if mn == "ldrb" else 0x7000) | off << 6 | rn << 3 | rd
            else:
                rm = _reg(mem[1])
                hw = {"str": 0x5000, "strb": 0x5400, "ldr": 0x5800, "ldrb": 0x5C00}[mn] | rm << 6 | rn << 3 | rd
        elif mn in ("push", "pop"):
            regs = [_reg(t) for t in a[0].strip()[1:-1].split(",")]
            mask = 0
            extra = 0
            for r in regs:
                if r == 14 and mn == "push" or r == 15 and mn == "pop":
                    extra = 0x100
                else:
                    mask |= 1 << r
            hw = (0xB400 if mn == "push" else 0xBC00) | extra | mask
        elif mn == "add" and a[0].lower() == "sp":
            hw = 0xB000 | _imm(a[1]) // 4
        elif mn == "sub" and a[0].lower() == "sp":
            hw = 0xB080 | _imm(a[1]) // 4
        elif mn == "bx":
            hw = 0x4700 | _reg(a[0]) << 3
        elif mn == "b":
            off = value(ops) - (addr + 4)
            if not -2048 <= off <= 2046:
                raise ValueError(f"b out of range at 0x{addr:x}")
            hw = 0xE000 | (off >> 1) & 0x7FF
        elif mn in _COND:
            off = value(ops) - (addr + 4)
            if not -256 <= off <= 254:
                raise ValueError(f"{mn} out of range at 0x{addr:x} ({off})")
            hw = 0xD000 | _COND[mn] << 8 | (off >> 1) & 0xFF
        elif mn == "bl":
            out += _bl(addr, value(ops))
            continue
        elif mn == "lea":
            rd = _reg(a[0])
            key = ("lea", addr, a[1])
            disp = (pool[key] - ((addr + 4) & ~3)) // 4
            if not 0 <= disp <= 255:
                raise ValueError(f"lea literal out of range at 0x{addr:x}")
            out += (0x4800 | rd << 8 | disp).to_bytes(2, "little")
            out += (0x4478 | rd).to_bytes(2, "little")   # add rd, pc
            continue
        if hw is None:
            raise ValueError(f"cannot assemble {mn} {ops} at 0x{addr:x}")
        out += hw.to_bytes(2, "little")
    if len(out) & 3:
        out += b"\xc0\x46"
    out += data
    for key in lits:
        if key[0] == "v":
            v = value(key[1]) & 0xFFFFFFFF
        else:
            _, addr, tgt = key
            # `ldr rd,[pc,#]; add rd, pc` : the add sits at addr + 2 and reads PC = addr + 6
            v = (value(tgt) - (addr + 6)) & 0xFFFFFFFF
        out += v.to_bytes(4, "little")
    return bytes(out), labels
