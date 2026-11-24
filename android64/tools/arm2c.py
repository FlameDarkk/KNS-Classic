#!/usr/bin/env python3
"""Static ARMv5TE (ARM + Thumb-1, soft float) to C translator for KnS libgame.so.

    python android64/tools/arm2c.py <libgame.so> <out_dir> [--chunk N] [--seeds file]

Input is the patched 32-bit ELF (browser KNS_ELF + patch_native.py --android-arena + gateway slot;
android64/build_android64.py prepares it). Output in <out_dir>:
  image.bin      guest memory from LOAD_BASE to the end of the file-backed segments, relocated
  image.h        layout constants (LOAD_BASE, image/bss sizes, import trap and data addresses)
  fn_NNN.c       translated functions, one C function per guest function entry
  table.c        sorted guest block key -> C function table, entry flags, import table
  report.txt     coverage: functions, blocks, instructions, indirect jumps, undecodable code

Guest model (android64/runtime/guest.h): guest pointer = mem + u32. A block key is its address
with bit 0 set in Thumb state, the same key the browser engine (apk-cpu-engine/cpu.js) uses.
Every guest function becomes `void F_<addr>(CPU *c)`; it starts at any of its blocks (switch on
c->pc), keeps guest registers in locals and spills them around calls. After a call it checks
that control came back to the return address; anything else goes through the local switch, then
a tail call (function entry) or a return to the C caller. The run loop in the runtime looks up
any block key, so control flow the translator did not predict still works when it lands on a
known block. Semantics follow apk-cpu-engine/cpu.js decodeArm/decodeThumb (the proven reference).
"""
import struct
import sys
from collections import defaultdict
from pathlib import Path

from elftools.elf.elffile import ELFFile

LOAD_BASE = 0x00100000
TRAP_BASE = 0xFFFE0000          # import i lives at TRAP_BASE + 4*i
RETURN_MAGIC = 0xFFFFFFF0       # host -> guest call return address (runtime)
HEADER = '#include "guest.h"\n#include "translated.h"\n'
BIG_FUNCTION = 2500        # instructions; such functions get their own C file
DATA_IMPORTS = {"__sF": 84 * 3, "__stack_chk_guard": 4, "__page_size": 4}

CONDS = ["Z", "!Z", "C", "!C", "N", "!N", "V", "!V", "(C&&!Z)", "(!C||Z)",
         "(N==V)", "(N!=V)", "(!Z&&N==V)", "(Z||N!=V)"]


def rotr(v, n):
    n &= 31
    return ((v >> n) | (v << (32 - n))) & 0xFFFFFFFF if n else v & 0xFFFFFFFF


def hx(v):
    return f"0x{v & 0xFFFFFFFF:x}u"


def s32(v):
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v & 0x80000000 else v


class Ins:
    __slots__ = ("pc", "len", "cond", "body", "kind", "target", "x", "op", "thumb")

    def __init__(self, pc, thumb):
        self.pc = pc
        self.thumb = thumb
        self.len = 2 if thumb else 4
        self.cond = None      # 0..13, or None for always
        self.body = ""        # C statements (for a terminator: side effects before the transfer)
        self.kind = None      # None (falls through) | b | call | icall | ret | ind | undef | swi
        self.target = None    # direct target key (b, call)
        self.op = 0


# ---------------------------------------------------------------- C fragments
def nz(t="t"):
    return f"N={t}>>31;Z={t}==0;"


def alu(op, A, B, S, sc=None):
    """Data processing: result in t. A, B are C expressions; sc the shifter carry (C expr) or None."""
    lc = f"C={sc};" if (S and sc) else ""
    f = nz() if S else ""
    if op in (0, 8):
        return f"t={A}&{B};" + f + lc
    if op in (1, 9):
        return f"t={A}^{B};" + f + lc
    if op == 12:
        return f"t={A}|{B};" + f + lc
    if op == 13:
        return f"t={B};" + f + lc
    if op == 14:
        return f"t={A}&~{B};" + f + lc
    if op == 15:
        return f"t=~{B};" + f + lc
    if op in (4, 11):
        return (f"t=ADDC({A},{B},0,&C,&V);" + f) if S else f"t={A}+{B};"
    if op in (2, 10):
        return (f"t=ADDC({A},~{B},1,&C,&V);" + f) if S else f"t={A}-{B};"
    if op == 3:
        return alu(2, B, A, S, sc)
    if op == 5:
        return (f"t=ADDC({A},{B},C,&C,&V);" + f) if S else f"t={A}+{B}+C;"
    if op == 6:
        return (f"t=ADDC({A},~{B},C,&C,&V);" + f) if S else f"t={A}+~{B}+C;"
    if op == 7:
        return alu(6, B, A, S, sc)
    raise ValueError(op)


def shift_imm(typ, R, amt):
    """(value expr, carry expr or None)."""
    if typ == 0:
        return (f"({R}<<{amt})", f"(({R}>>{32 - amt})&1)") if amt else (R, None)
    if typ == 1:
        return (f"({R}>>{amt})", f"(({R}>>{amt - 1})&1)") if amt else ("0u", f"({R}>>31)")
    if typ == 2:
        return ((f"(uint32_t)((int32_t){R}>>{amt})", f"(({R}>>{amt - 1})&1)") if amt
                else (f"(uint32_t)((int32_t){R}>>31)", f"({R}>>31)"))
    return ((f"(({R}>>{amt})|({R}<<{32 - amt}))", f"(({R}>>{amt - 1})&1)") if amt
            else (f"(({R}>>1)|(C<<31))", f"({R}&1)"))


SHIFT_FN = ["LSLR", "LSRR", "ASRR", "RORR"]

INTERWORK = "X=(X&1)?X:(X&~3u);"


class Translator:
    def __init__(self, path):
        self.path = Path(path)
        self.raw = self.path.read_bytes()
        f = self.path.open("rb")
        self.elf = ELFFile(f)
        e = self.elf
        self.segs = [(s["p_vaddr"], s["p_offset"], s["p_filesz"], s["p_memsz"], s["p_flags"])
                     for s in e.iter_segments() if s["p_type"] == "PT_LOAD"]
        text = e.get_section_by_name(".text")
        self.text_lo, self.text_hi = text["sh_addr"], text["sh_addr"] + text["sh_size"]
        plt = e.get_section_by_name(".plt")
        self.plt_lo, self.plt_hi = plt["sh_addr"], plt["sh_addr"] + plt["sh_size"]
        self.code_lo, self.code_hi = self.plt_lo, self.text_hi
        # read-only segment range (literal pools there can be folded)
        self.ro = [(v, v + fs) for v, o, fs, ms, fl in self.segs if not fl & 2]
        end = max(v + ms for v, o, fs, ms, fl in self.segs)
        self.file_end = max(v + fs for v, o, fs, ms, fl in self.segs)
        self.mem_end = end
        self.mem = bytearray(end)
        for v, o, fs, ms, fl in self.segs:
            self.mem[v:v + fs] = self.raw[o:o + fs]
        self.dynsym = e.get_section_by_name(".dynsym")
        self.syms = {}           # name -> value
        self.func_names = {}     # key -> name
        for s in self.dynsym.iter_symbols():
            if s.name and s["st_shndx"] != "SHN_UNDEF":
                self.syms[s.name] = s["st_value"]
                if s["st_info"]["type"] == "STT_FUNC":
                    self.func_names.setdefault(s["st_value"], s.name)
        # symbol extents: Thumb-1 code uses `bl` as a far branch inside big functions
        self.extents = sorted((s["st_value"] & ~1, s["st_size"]) for s in self.dynsym.iter_symbols()
                              if s.name and s["st_shndx"] != "SHN_UNDEF" and s["st_info"]["type"] == "STT_FUNC")
        self.extent_starts = [a for a, n in self.extents]
        self.imports = []        # import names, index = trap slot
        self.import_idx = {}
        self.import_data = {}    # name -> guest address
        self.plt_import = {}     # plt stub address -> import name
        self.reloc_targets = set()
        self.dec = {}            # key -> Ins
        self.notes = defaultdict(list)

    # ------------------------------------------------------------ memory
    def u32(self, a):
        return struct.unpack_from("<I", self.mem, a)[0]

    def u16(self, a):
        return struct.unpack_from("<H", self.mem, a)[0]

    def is_ro(self, a, n=4):
        return any(lo <= a and a + n <= hi for lo, hi in self.ro) and not any(
            r in self.reloc_targets for r in range(a & ~3, a + n, 4))

    def in_code(self, a):
        return self.code_lo <= a < self.code_hi

    # ------------------------------------------------------------ relocation
    def relocate(self):
        """Pre-apply all dynamic relocations at LOAD_BASE. Imports become trap addresses."""
        e = self.elf
        data_at = (LOAD_BASE + self.mem_end + 0xFFF) & ~0xFFF

        def imp(name):
            if name in DATA_IMPORTS:
                if name not in self.import_data:
                    nonlocal data_at
                    self.import_data[name] = data_at
                    data_at = (data_at + DATA_IMPORTS[name] + 15) & ~15
                return self.import_data[name]
            if name not in self.import_idx:
                self.import_idx[name] = len(self.imports)
                self.imports.append(name)
            return TRAP_BASE + 4 * self.import_idx[name]

        def sym_value(idx):
            s = self.dynsym.get_symbol(idx)
            if s["st_shndx"] != "SHN_UNDEF":
                return LOAD_BASE + s["st_value"]
            return imp(s.name)

        rel_plt = e.get_section_by_name(".rel.plt")
        for i, r in enumerate(rel_plt.iter_relocations()):
            nm = self.dynsym.get_symbol(r["r_info_sym"]).name
            self.plt_import[self.plt_lo + 20 + i * 12] = nm
        for sec in (".rel.dyn", ".rel.plt"):
            for r in e.get_section_by_name(sec).iter_relocations():
                t, off = r["r_info_type"], r["r_offset"]
                if t == 0:
                    continue
                self.reloc_targets.add(off)
                cur = self.u32(off)
                if t == 23:      # R_ARM_RELATIVE
                    v = cur + LOAD_BASE
                elif t in (21, 22):  # GLOB_DAT, JUMP_SLOT
                    v = sym_value(r["r_info_sym"])
                elif t == 2:     # ABS32
                    v = cur + sym_value(r["r_info_sym"])
                else:
                    raise SystemExit(f"relocation type {t} at 0x{off:x}")
                struct.pack_into("<I", self.mem, off, v & 0xFFFFFFFF)
        self.data_end = data_at
        self.init_array = []
        for name in (".init_array",):
            s = e.get_section_by_name(name)
            if s is not None:
                for i in range(0, s["sh_size"], 4):
                    f = self.u32(s["sh_addr"] + i)
                    if f not in (0, 0xFFFFFFFF):
                        self.init_array.append(f)

    def plt_stub(self, addr):
        """Import name if addr is an unpatched ARM PLT stub."""
        if addr not in self.plt_import:
            return None
        a, b, c = self.u32(addr), self.u32(addr + 4), self.u32(addr + 8)
        if (a & 0x0FFFF000) == 0x028FC000 and (b & 0x0FFFF000) == 0x028CC000 and (c & 0x0FFFF000) == 0x05BCF000:
            return self.plt_import[addr]
        return None

    # ------------------------------------------------------------ decode
    def decode(self, key):
        d = self.dec.get(key)
        if d is None:
            d = self.dec[key] = self.decode_thumb(key & ~1) if key & 1 else self.decode_arm(key)
            if d.kind == "call" and d.thumb and d.target & 1 and self.far_jump(d.pc, d.target & ~1):
                # GCC Thumb-1 far jump: `bl` to a label in the same function (LR is saved in the
                # prologue). A branch that still sets LR, as the instruction does.
                d.kind = "b"
                d.body = f"R[14]={hx(d.pc + 4 + LOAD_BASE + 1)};"
                self.notes["far_jump"].append(d.pc)
        return d

    def far_jump(self, pc, target):
        import bisect
        j = bisect.bisect_right(self.extent_starts, pc) - 1
        if j < 0:
            return False
        a, n = self.extents[j]
        return a <= pc < a + n and a < target < a + n

    def undef(self, ins, why):
        ins.kind = "undef"
        ins.body = f"guest_undef(c,{hx(ins.pc + LOAD_BASE)},{hx(ins.op)});"
        self.notes["undef"].append((ins.pc, why))
        return ins

    def decode_arm(self, pc):
        """pc is the link-time address (no LOAD_BASE)."""
        ins = Ins(pc, False)
        if not self.in_code(pc) or pc + 4 > self.mem_end:
            return self.undef(ins, "outside code")
        op = ins.op = self.u32(pc)
        G = pc + LOAD_BASE   # guest address
        cc = op >> 28
        if cc not in (14, 15):
            ins.cond = cc

        def REG(i, extra=0):
            return hx(G + 8 + extra) if i == 15 else f"R[{i}]"

        if cc == 15:
            if (op & 0x0E000000) == 0x0A000000:   # BLX imm
                off = s32(op << 8) >> 6
                off += (op >> 23) & 2
                ins.kind = "call"
                ins.target = ((pc + 8 + off) | 1) & 0xFFFFFFFF
                return ins
            if (op & 0x0D70F000) == 0x0550F000:   # PLD
                return ins
            return self.undef(ins, "cc15")

        cls = (op >> 25) & 7
        rd, rn, rm, rs = (op >> 12) & 15, (op >> 16) & 15, op & 15, (op >> 8) & 15

        if (op & 0x0FFFFFF0) in (0x012FFF10, 0x012FFF30):   # BX / BLX reg
            link = op & 0x20
            ins.body = f"X={REG(rm)};" + INTERWORK
            ins.kind = "icall" if link else ("ret" if rm == 14 else "ind")
            return ins
        if (op & 0x0FFF0FF0) == 0x016F0F10:   # CLZ
            ins.body = f"R[{rd}]=CLZ({REG(rm)});"
            return ins
        if (op & 0x0FBF0FFF) == 0x010F0000:   # MRS
            ins.body = f"R[{rd}]=(N<<31)|(Z<<30)|(C<<29)|(V<<28)|0x10u;"
            return ins
        if (op & 0x0FB0F000) == 0x0320F000 or (op & 0x0FB0FFF0) == 0x0120F000:   # MSR
            if (op >> 19) & 1:
                val = hx(rotr(op & 255, ((op >> 8) & 15) * 2)) if op & 0x02000000 else REG(rm)
                ins.body = f"t={val};N=t>>31;Z=(t>>30)&1;C=(t>>29)&1;V=(t>>28)&1;"
            return ins

        if cls == 0 and (op & 0x90) == 0x90:
            b65 = (op >> 5) & 3
            if b65 == 0:
                if ((op >> 23) & 31) == 2:   # SWP / SWPB
                    if op & 0x400000:
                        ins.body = f"a={REG(rn)};t=ATOMIC_XCHG8(a,R[{rm}]);R[{rd}]=t;"
                    else:
                        ins.body = f"a={REG(rn)};t=ATOMIC_XCHG32(a,R[{rm}]);R[{rd}]=t;"
                    return ins
                S = (op >> 20) & 1
                kind = (op >> 21) & 7
                A, B = f"R[{rm}]", f"R[{rs}]"
                if kind in (0, 1):   # MUL MLA (rd field = rn here)
                    add = f"+R[{rd}]" if kind == 1 else ""
                    ins.body = f"t={A}*{B}{add};" + (nz() if S else "") + f"R[{rn}]=t;"
                    return ins
                if kind >= 4:        # UMULL UMLAL SMULL SMLAL: RdLo = rd, RdHi = rn
                    signed, acc = kind >= 6, kind & 1
                    p = f"(uint64_t)((int64_t)(int32_t){A}*(int64_t)(int32_t){B})" if signed \
                        else f"((uint64_t){A}*(uint64_t){B})"
                    s = f"w={p};"
                    if acc:
                        s += f"w+=((uint64_t)R[{rn}]<<32)|R[{rd}];"
                    s += f"R[{rd}]=(uint32_t)w;R[{rn}]=(uint32_t)(w>>32);"
                    if S:
                        s += "N=(uint32_t)(w>>63);Z=w==0;"
                    ins.body = s
                    return ins
                return self.undef(ins, "mul kind")
            # halfword / doubleword transfers
            P, U, I, W, L = (op >> 24) & 1, (op >> 23) & 1, (op >> 22) & 1, (op >> 21) & 1, (op >> 20) & 1
            O = hx(((op >> 4) & 0xF0) | (op & 15)) if I else f"R[{rm}]"
            sg = "+" if U else "-"
            addr = f"a={REG(rn)}{sg}{O};" if P else f"a={REG(rn)};"
            wb = ""
            if not P or W:
                wb = f"R[{rn}]=a;" if P else f"R[{rn}]=a{sg}{O};"
            if L:
                no_wb = rn == rd
                ld = {1: "LD16(a)", 2: "(uint32_t)(int32_t)(int8_t)LD8(a)", 3: "(uint32_t)(int32_t)(int16_t)LD16(a)"}[b65]
                ins.body = addr + f"t={ld};" + ("" if no_wb else wb) + f"R[{rd}]=t;"
            elif b65 == 1:
                ins.body = addr + f"ST16(a,R[{rd}]);" + wb
            elif b65 == 2:   # LDRD
                ins.body = addr + f"t=LD32(a);x2=LD32(a+4);" + (wb if rn not in (rd, rd + 1) else "") + \
                    f"R[{rd}]=t;R[{rd + 1}]=x2;"
            else:            # STRD
                ins.body = addr + f"ST32(a,R[{rd}]);ST32(a+4,R[{rd + 1}]);" + wb
            return ins

        if cls == 0 and (op & 0x01900000) == 0x01000000 and (op & 0x90) != 0x90:
            if (op & 0x0F900090) == 0x01000080:   # SMLAxy SMULWy SMLAWy SMLALxy SMULxy
                kind = (op >> 21) & 3
                X, Y = (op >> 5) & 1, (op >> 6) & 1

                def half(R, top):
                    return f"(int32_t)(int16_t)({R}>>16)" if top else f"(int32_t)(int16_t){R}"
                A, B = f"R[{rm}]", f"R[{rs}]"
                if kind == 0:
                    ins.body = f"R[{rn}]=(uint32_t)({half(A, X)}*{half(B, Y)})+R[{rd}];"
                elif kind == 3:
                    ins.body = f"R[{rn}]=(uint32_t)({half(A, X)}*{half(B, Y)});"
                elif kind == 1:
                    ins.body = (f"t=(uint32_t)(((int64_t)(int32_t){A}*{half(B, Y)})>>16);"
                                f"R[{rn}]={'t' if X else f't+R[{rd}]'};")
                else:
                    ins.body = (f"w=(((uint64_t)R[{rn}]<<32)|R[{rd}])+(uint64_t)(int64_t)({half(A, X)}*{half(B, Y)});"
                                f"R[{rd}]=(uint32_t)w;R[{rn}]=(uint32_t)(w>>32);")
                return ins
            if (op & 0x0F900FF0) == 0x01000050:   # QADD QSUB QDADD QDSUB
                kind = (op >> 21) & 3
                B = f"SAT64((int64_t)(int32_t)R[{rn}]*2)" if kind & 2 else f"(int32_t)R[{rn}]"
                ins.body = f"R[{rd}]=(uint32_t)SAT64((int64_t)(int32_t)R[{rm}]{'-' if kind & 1 else '+'}(int64_t){B});"
                return ins
            return self.undef(ins, "dsp")

        if cls in (0, 1):   # data processing
            code = (op >> 21) & 15
            S = (op >> 20) & 1
            cmp = 8 <= code <= 11
            sc, pre = None, ""
            logic = code in (0, 1, 8, 9) or code >= 12
            if cls == 1:
                rot = ((op >> 8) & 15) * 2
                imm = rotr(op & 255, rot)
                B = hx(imm)
                if rot:
                    sc = str(imm >> 31)
            elif op & 0x10:
                typ = (op >> 5) & 3
                RM = hx(G + 12) if rm == 15 else f"R[{rm}]"
                pre = f"v={RM};n={REG(rs)}&255;"
                if S and logic:
                    pre += f"h={SHIFT_FN[typ]}C(v,n,C);"
                    sc = "h"
                pre += f"o={SHIFT_FN[typ]}(v,n);"
                B = "o"
            else:
                B, sc = shift_imm((op >> 5) & 3, REG(rm), (op >> 7) & 31)
            A = hx(G + 8 + (4 if (cls == 0 and op & 0x10) else 0)) if rn == 15 else f"R[{rn}]"
            body = pre + alu(code, A, B, S and rd != 15, sc)
            if cmp:
                ins.body = body
            elif rd == 15:
                ins.body = body + "X=t;" + INTERWORK
                ins.kind = "ret" if (code == 13 and cls == 0 and rm == 14 and not (op & 0x10) and (op >> 7) & 31 == 0
                                     and ((op >> 5) & 3) == 0) else "ind"
                ins.x = None
            else:
                ins.body = body + f"R[{rd}]=t;"
            return ins

        if cls in (2, 3):   # LDR / STR
            if cls == 3 and op & 0x10:
                return self.undef(ins, "media")
            P, U, B_, W, L = (op >> 24) & 1, (op >> 23) & 1, (op >> 22) & 1, (op >> 21) & 1, (op >> 20) & 1
            if cls == 2:
                O = hx(op & 0xFFF)
            else:
                O, _ = shift_imm((op >> 5) & 3, REG(rm), (op >> 7) & 31)
            sg = "+" if U else "-"
            # literal: ldr rX, [pc, #imm]
            if L and not B_ and cls == 2 and rn == 15 and P and not W:
                a = pc + 8 + ((op & 0xFFF) if U else -(op & 0xFFF))
                if rd != 15:
                    ins.body = f"R[{rd}]={self.lit_link(a)};"
                    return ins
            addr = f"a={REG(rn)}{sg}{O};" if P else f"a={REG(rn)};"
            wb = ""
            if not P or W:
                wb = f"R[{rn}]=a;" if P else f"R[{rn}]=a{sg}{O};"
            if L:
                got = addr + f"X={'LD8(a)' if B_ else 'LD32(a)'};"
                if rd == 15:
                    ins.body = got + wb + INTERWORK
                    ins.kind = "ret" if (rn == 13 and not P and U) else "ind"
                    if ins.kind == "ind":
                        self.notes["ldr_pc"].append(pc)
                else:
                    ins.body = got + ("" if rn == rd else wb) + f"R[{rd}]=X;"
            else:
                V = hx(G + 12) if rd == 15 else f"R[{rd}]"
                ins.body = addr + (f"ST8(a,{V});" if B_ else f"ST32(a,{V});") + wb
            return ins

        if cls == 4:   # LDM / STM
            P, U, W, L = (op >> 24) & 1, (op >> 23) & 1, (op >> 21) & 1, (op >> 20) & 1
            if op & 0x400000:
                return self.undef(ins, "ldm ^")
            regs = [i for i in range(16) if op & (1 << i)]
            size = len(regs) * 4
            start = (4 if P else 0) if U else (-size if P else -size + 4)
            after = size if U else -size
            s = f"b=R[{rn}];a=b+{hx(start)};"
            for i in regs:
                if L:
                    s += "X=LD32(a);" if i == 15 else f"R[{i}]=LD32(a);"
                else:
                    s += f"ST32(a,{hx(G + 12) if i == 15 else f'R[{i}]'});"
                s += "a+=4;"
            if W and not (L and op & (1 << rn)):
                s += f"R[{rn}]=b+{hx(after)};"
            if L and op & 0x8000:
                s += INTERWORK
                ins.kind = "ret" if rn == 13 else "ind"
            ins.body = s
            return ins

        if cls == 5:   # B / BL
            off = s32(op << 8) >> 6
            t = (pc + 8 + off) & 0xFFFFFFFF
            ins.kind = "call" if (op >> 24) & 1 else "b"
            ins.target = t
            return ins

        if (op & 0x0F000000) == 0x0F000000:   # SWI
            ins.kind = "swi"
            ins.body = f"guest_swi(c,{op & 0xFFFFFF});"
            return ins
        return self.undef(ins, "arm")

    def lit_link(self, a):
        """Literal pool word at link-time address a."""
        if self.is_ro(a) and a + 4 <= self.mem_end:
            return hx(self.u32(a))
        return f"LD32({hx(a + LOAD_BASE)})"

    def decode_thumb(self, pc):
        ins = Ins(pc, True)
        if not self.in_code(pc) or pc + 2 > self.mem_end:
            return self.undef(ins, "outside code")
        op = ins.op = self.u16(pc)
        G = pc + LOAD_BASE
        PCV = G + 4
        top = op >> 11

        if top <= 2:   # LSL LSR ASR imm
            amt, rs, rd = (op >> 6) & 31, (op >> 3) & 7, op & 7
            val, c_ = shift_imm(top, f"R[{rs}]", amt)
            ins.body = (f"h={c_};" if c_ else "") + f"t={val};" + nz() + ("C=h;" if c_ else "") + f"R[{rd}]=t;"
            return ins
        if top == 3:   # ADD SUB reg / imm3
            I, sub, f6, rs, rd = (op >> 10) & 1, (op >> 9) & 1, (op >> 6) & 7, (op >> 3) & 7, op & 7
            B = hx(f6) if I else f"R[{f6}]"
            ins.body = alu(2 if sub else 4, f"R[{rs}]", B, True) + f"R[{rd}]=t;"
            return ins
        if top <= 7:   # MOV CMP ADD SUB imm8
            kind, rd, imm = top - 4, (op >> 8) & 7, op & 255
            if kind == 0:
                ins.body = f"t={hx(imm)};" + nz() + f"R[{rd}]=t;"
            elif kind == 1:
                ins.body = alu(2, f"R[{rd}]", hx(imm), True)
            else:
                ins.body = alu(4 if kind == 2 else 2, f"R[{rd}]", hx(imm), True) + f"R[{rd}]=t;"
            return ins
        if (op >> 10) == 16:   # ALU reg
            code, rs, rd = (op >> 6) & 15, (op >> 3) & 7, op & 7
            D, S = f"R[{rd}]", f"R[{rs}]"
            if code in (0, 1, 12, 14, 15):
                ins.body = alu(code, D, S, True) + f"{D}=t;"
            elif code in (2, 3, 4, 7):
                typ = {2: 0, 3: 1, 4: 2, 7: 3}[code]
                ins.body = (f"v={D};n={S}&255;h={SHIFT_FN[typ]}C(v,n,C);t={SHIFT_FN[typ]}(v,n);"
                            + nz() + f"C=h;{D}=t;")
            elif code in (5, 6):
                ins.body = alu(code, D, S, True) + f"{D}=t;"
            elif code == 8:
                ins.body = alu(0, D, S, True)
            elif code == 9:
                ins.body = alu(2, "0u", S, True) + f"{D}=t;"
            elif code == 10:
                ins.body = alu(2, D, S, True)
            elif code == 11:
                ins.body = alu(4, D, S, True)
            else:   # 13 MUL
                ins.body = f"t={D}*{S};" + nz() + f"{D}=t;"
            return ins
        if (op >> 10) == 17:   # hi register ops, BX, BLX
            code = (op >> 8) & 3
            rs = (op >> 3) & 15
            rd = (op & 7) | ((op >> 4) & 8)
            RS = hx(PCV) if rs == 15 else f"R[{rs}]"
            RD = hx(PCV) if rd == 15 else f"R[{rd}]"
            if code == 3:
                link = op & 0x80
                ins.body = f"X={RS};" + INTERWORK
                ins.kind = "icall" if link else ("ret" if rs == 14 else "ind")
                return ins
            if code == 1:
                ins.body = alu(2, RD, RS, True)
                return ins
            val = f"{RD}+{RS}" if code == 0 else RS
            if rd == 15:
                ins.body = f"X=({val})|1u;"
                ins.kind = "ret" if (code == 2 and rs == 14) else "ind"
                if ins.kind == "ind":
                    self.notes["thumb_pc_write"].append(pc)
            else:
                ins.body = f"R[{rd}]={val};"
            return ins
        if top == 9:   # LDR literal
            rd = (op >> 8) & 7
            a = ((pc + 4) & ~3) + (op & 255) * 4
            ins.body = f"R[{rd}]={self.lit_link(a)};"
            return ins
        if (op >> 12) == 5:   # register offset
            kind, ro, rb, rd = (op >> 9) & 7, (op >> 6) & 7, (op >> 3) & 7, op & 7
            a, D = f"a=R[{rb}]+R[{ro}];", f"R[{rd}]"
            ins.body = a + [f"ST32(a,{D});", f"ST16(a,{D});", f"ST8(a,{D});",
                            f"{D}=(uint32_t)(int32_t)(int8_t)LD8(a);", f"{D}=LD32(a);", f"{D}=LD16(a);",
                            f"{D}=LD8(a);", f"{D}=(uint32_t)(int32_t)(int16_t)LD16(a);"][kind]
            return ins
        if (op >> 13) == 3:   # imm offset word / byte
            B, L, imm, rb, rd = (op >> 12) & 1, (op >> 11) & 1, (op >> 6) & 31, (op >> 3) & 7, op & 7
            a, D = f"a=R[{rb}]+{hx(imm if B else imm * 4)};", f"R[{rd}]"
            ins.body = a + ((f"{D}=LD8(a);" if B else f"{D}=LD32(a);") if L else
                            (f"ST8(a,{D});" if B else f"ST32(a,{D});"))
            return ins
        if (op >> 12) == 8:   # halfword imm offset
            L, imm, rb, rd = (op >> 11) & 1, (op >> 6) & 31, (op >> 3) & 7, op & 7
            a, D = f"a=R[{rb}]+{hx(imm * 2)};", f"R[{rd}]"
            ins.body = a + (f"{D}=LD16(a);" if L else f"ST16(a,{D});")
            return ins
        if (op >> 12) == 9:   # SP relative
            L, rd, imm = (op >> 11) & 1, (op >> 8) & 7, (op & 255) * 4
            a, D = f"a=R[13]+{hx(imm)};", f"R[{rd}]"
            ins.body = a + (f"{D}=LD32(a);" if L else f"ST32(a,{D});")
            return ins
        if (op >> 12) == 10:   # ADD rd, pc/sp, imm
            SP, rd, imm = (op >> 11) & 1, (op >> 8) & 7, (op & 255) * 4
            ins.body = f"R[{rd}]=R[13]+{hx(imm)};" if SP else f"R[{rd}]={hx((PCV & ~3) + imm)};"
            return ins
        if (op >> 8) == 0xB0:   # ADD/SUB SP
            imm = (op & 127) * 4
            ins.body = f"R[13]{'-' if op & 0x80 else '+'}={hx(imm)};"
            return ins
        if (op >> 12) == 11 and ((op >> 9) & 3) == 2:   # PUSH / POP
            L, Rb = (op >> 11) & 1, (op >> 8) & 1
            regs = [i for i in range(8) if op & (1 << i)]
            if L:
                s = "a=R[13];"
                for i in regs:
                    s += f"R[{i}]=LD32(a);a+=4;"
                if Rb:
                    s += "X=LD32(a);a+=4;"
                s += "R[13]=a;"
                if Rb:
                    s += INTERWORK
                    ins.kind = "ret"
            else:
                n = len(regs) + Rb
                s = f"a=R[13]-{hx(n * 4)};R[13]=a;"
                for i in regs:
                    s += f"ST32(a,R[{i}]);a+=4;"
                if Rb:
                    s += "ST32(a,R[14]);"
            ins.body = s
            return ins
        if (op >> 8) == 0xBE:
            return self.undef(ins, "bkpt")
        if (op >> 12) == 12:   # LDMIA STMIA
            L, rb = (op >> 11) & 1, (op >> 8) & 7
            regs = [i for i in range(8) if op & (1 << i)]
            s = f"a=R[{rb}];"
            for i in regs:
                s += (f"R[{i}]=LD32(a);" if L else f"ST32(a,R[{i}]);") + "a+=4;"
            if not (L and op & (1 << rb)):
                s += f"R[{rb}]=a;"
            ins.body = s
            return ins
        if (op >> 12) == 13:
            cc = (op >> 8) & 15
            if cc == 15:
                ins.kind = "swi"
                ins.body = f"guest_swi(c,{op & 255});"
                return ins
            if cc == 14:
                return self.undef(ins, "thumb udf")
            ins.cond = cc
            ins.kind = "b"
            ins.target = ((pc + 4 + (s32((op & 255) << 24) >> 23)) | 1) & 0xFFFFFFFF
            return ins
        if top == 0x1C:   # B
            ins.kind = "b"
            ins.target = ((pc + 4 + (s32((op & 0x7FF) << 21) >> 20)) | 1) & 0xFFFFFFFF
            return ins
        if top == 0x1E:   # BL / BLX pair
            nxt = self.u16(pc + 2)
            if (nxt >> 11) not in (0x1F, 0x1D):
                return self.undef(ins, "bl prefix alone")
            hi = s32((op & 0x7FF) << 21) >> 9
            lo = (nxt & 0x7FF) << 1
            base = (pc + 4 + hi + lo) & 0xFFFFFFFF
            ins.len = 4
            ins.kind = "call"
            ins.target = (base | 1) if (nxt >> 11) == 0x1F else (base & ~3)
            return ins
        return self.undef(ins, "thumb")

    # ------------------------------------------------------------ discovery
    @staticmethod
    def next_key(ins):
        return (ins.pc + ins.len) | (1 if ins.thumb else 0)

    def successors(self, ins, entries):
        """Intra-function successor keys of a decoded instruction."""
        nk = self.next_key(ins)
        k = ins.kind
        if k is None or k == "swi":
            return [nk]
        out = []
        if k == "b":
            out.append(ins.target)
        if k in ("call", "icall") or ins.cond is not None:
            out.append(nk)
        out += self.jump_table(ins)
        return out

    def jump_table(self, ins):
        """Targets of a recognised switch: ARM `addcc pc, pc, rX, lsl #2` followed by `b` slots,
        or the GCC Thumb-1 PIC switch (`ldr r3,=T-.; lsls i,#2; add r3,pc; ldr r2,[i,r3];
        adds r3,r2,r3; mov pc,r3`, entries relative to the table)."""
        if ins.kind != "ind":
            return []
        if ins.thumb:
            return self.thumb_switch(ins)
        op = ins.op
        if (op & 0x0FFFFFF0) == 0x008FF100:   # add pc, pc, rX, lsl #2
            out, a = [], ins.pc + 8
            while (self.u32(a) & 0xFF000000) == 0xEA000000 and len(out) < 1024:
                out.append(a)
                a += 4
            self.notes["arm_switch"].append((ins.pc, len(out)))
            return out
        return []

    def thumb_switch(self, ins):
        """Targets of a Thumb `mov pc, rX` / `add pc, rX` switch.

        A symbolic run of the instructions before the jump finds the GCC Thumb-1 PIC pattern
        target = B + table[i] with table at B (entries relative to the table). When B is a
        constant the table is read directly; when B was spilled earlier (zlib inflate keeps it
        on the stack) the function's PC-relative constants are tried as B, and one is accepted
        only if every entry (count from the preceding `cmp i, #n`) lands inside the function."""
        cached = getattr(ins, "x", None)
        if isinstance(cached, list):
            return cached
        pc = ins.pc
        start = max(pc - 2 * 12, self.code_lo)
        R = {}
        bound = None

        def get(r):
            return R.get(r, ("?", r, pc))

        def add(a_, b_):
            if a_[0] == "c" and b_[0] == "c":
                return ("c", (a_[1] + b_[1]) & 0xFFFFFFFF)
            return ("add", a_, b_)
        reset = False
        a = start
        while a < pc:
            op = self.u16(a)
            top = op >> 11
            if top == 0x1C or (op & 0xFF00) == 0xBD00 or (op & 0xFF87) == 0x4700:
                # b / pop {..,pc} / bx: the dispatch block starts after it, so nothing before it
                # (registers or a `cmp`) belongs to this switch (ProBattleCharacter took an unrelated
                # `cmp r0,#0` as the count and kept 1 of 13 cases).
                R, bound, reset = {}, None, True
            elif top == 9:                                      # ldr rd, [pc, #imm]
                lit = ((a + 4) & ~3) + (op & 255) * 4
                R[(op >> 8) & 7] = ("c", self.u32(lit)) if self.is_ro(lit) else ("?", "lit", a)
            elif top == 0x13:                                   # ldr rd, [sp, #imm]
                R[(op >> 8) & 7] = ("sp", (op & 255) * 4)
            elif top in (0x0C, 0x0E, 0x10, 0x12) or (op >> 9) in (0x28, 0x29, 0x2A):
                pass                                            # stores write no register
            elif (op >> 8) == 0x44:                             # add rd, rs (hi)
                rd = (op & 7) | ((op >> 4) & 8)
                rs = (op >> 3) & 15
                R[rd] = add(get(rd), ("c", a + 4) if rs == 15 else get(rs))
            elif (op >> 8) == 0x46:                             # mov rd, rs (hi)
                R[(op & 7) | ((op >> 4) & 8)] = get((op >> 3) & 15)
            elif top == 0 and ((op >> 6) & 31):                 # lsls rd, rs, #k
                R[op & 7] = ("shl", get((op >> 3) & 7), (op >> 6) & 31)
            elif top == 0x0D:                                   # ldr rd, [rb, #imm5*4]
                rb, off = get((op >> 3) & 7), ((op >> 6) & 31) * 4
                R[op & 7] = ("ld", rb if not off else add(rb, ("c", off)))
            elif (op >> 9) == 0x2C:                             # ldr rd, [rb, ro]
                R[op & 7] = ("ld", add(get((op >> 3) & 7), get((op >> 6) & 7)))
            elif (op >> 9) == 0x0C:                             # adds rd, rs, rm
                R[op & 7] = add(get((op >> 3) & 7), get((op >> 6) & 7))
            elif (op >> 9) == 0x0E and ((op >> 6) & 7) == 0:    # adds rd, rs, #0 (mov)
                R[op & 7] = get((op >> 3) & 7)
            elif top == 5:                                      # cmp rN, #imm
                bound = (op & 255) + 1
                R.pop("cmp", None)
            elif top in (4, 6, 7, 0x14, 0x15):                  # imm8 ops / add rd, pc|sp: rd in bits 8-10
                R.pop((op >> 8) & 7, None)
            elif (op >> 12) == 0xD or top == 0x1C:              # conditional / plain branch: no write
                pass
            else:                                               # other formats write rd = bits 0-2
                R.pop(op & 7, None)
            a += 2
        op = ins.op
        rs = (op >> 3) & 15
        tgt = get(rs) if ((op >> 8) & 3) == 2 else add(get((op & 7) | ((op >> 4) & 8)), get(rs))
        out = []
        base = None
        if tgt[0] == "add":
            for ld, b_ in ((tgt[1], tgt[2]), (tgt[2], tgt[1])):
                if ld[0] == "ld" and ld[1][0] == "add" and b_ in (ld[1][1], ld[1][2]):
                    base = b_
                    break
        fn_lo, fn_hi = self.extent_of(pc)
        const_base = base is not None and base[0] == "c"
        if bound is None and (not const_base or reset):
            # The count can sit far away (ProMonster: `cmp r1,#8; bhi; b dispatch`). A constant-base
            # table with no block boundary in the window keeps its old open-ended read.
            bound = self.dispatch_bound(pc, fn_lo, fn_hi)
            if bound:
                self.notes["thumb_switch_far_bound"].append((hex(pc), bound))
        if base is None and tgt[0] == "add" and bound:
            # GCC variant with two PIC constants: target = B2 + table[i], table at T (T != B2)
            for ld, b_ in ((tgt[1], tgt[2]), (tgt[2], tgt[1])):
                if ld[0] == "ld" and ld[1][0] == "add" and b_[0] == "c":
                    T = next((t_[1] for t_ in (ld[1][1], ld[1][2]) if t_[0] == "c"), None)
                    if T is not None:
                        out = self.read_rel_table(T, bound, fn_lo, fn_hi, strict=True, rel=b_[1])
                        if out:
                            self.notes["thumb_switch_two_bases"].append((hex(pc), hex(T), hex(b_[1]), len(out)))
                            break
        if out:
            pass
        elif base is not None and base[0] == "c":
            out = self.read_rel_table(base[1], bound, fn_lo, fn_hi, strict=False)
            if reset:
                # Union with the old open-ended read: an extra target is harmless, a lost case crashes.
                wide = self.read_rel_table(base[1], None, fn_lo, fn_hi, strict=False)
                out = out + [t_ for t_ in wide if t_ not in out]
        elif base is not None and bound:
            for T in self.pc_constants(fn_lo, fn_hi):
                cand = self.read_rel_table(T, bound, fn_lo, fn_hi, strict=True)
                if cand:
                    out = cand
                    self.notes["thumb_switch_spilled_base"].append((hex(pc), hex(T), len(out)))
                    break
        self.notes["thumb_switch" if out else "thumb_switch_unknown"].append((hex(pc), len(out)))
        ins.x = out
        return out

    def dispatch_bound(self, pc, lo, hi):
        """Case count of a Thumb switch whose `cmp i, #n` is not just before the jump.

        The dispatch block starts after the last unconditional `b` before `pc`. Every branch to that
        start that is guarded by `cmp rN, #n` within the three preceding instructions gives n + 1;
        the largest one is returned (read_rel_table still validates each entry)."""
        b0 = None
        for a in range(pc - 2, max(pc - 2 * 16, lo) - 1, -2):
            op = self.u16(a)
            if (op >> 11) == 0x1C or (op & 0xFF00) == 0xBD00 or (op & 0xFF87) == 0x4700:
                b0 = a + 2                                      # after b / pop {..,pc} / bx
                break
        if b0 is None:
            return None
        found = []
        for a in range(lo, hi, 2):
            op = self.u16(a)
            if (op >> 11) == 0x1C:                              # b imm11
                t_ = a + 4 + (((op & 0x7FF) ^ 0x400) - 0x400) * 2
            elif (op >> 12) == 0xD and ((op >> 8) & 15) < 14:   # b<cond> imm8
                t_ = a + 4 + (((op & 0xFF) ^ 0x80) - 0x80) * 2
            else:
                continue
            if t_ != b0:
                continue
            for k in range(2, 8, 2):
                p = self.u16(a - k)
                if (p >> 11) == 5:                              # cmp rN, #imm
                    found.append((p & 255) + 1)
                    break
        return max(found) if found else None

    def read_rel_table(self, T, bound, lo, hi, strict, rel=None):
        out = []
        n = bound or 256
        for i in range(n):
            if not self.is_ro(T + 4 * i):
                return [] if strict else out
            t_ = ((T if rel is None else rel) + self.u32(T + 4 * i)) & 0xFFFFFFFF
            if not (lo <= t_ < hi) or t_ & 1:
                if strict:
                    return []
                if bound is None:
                    break
                continue
            out.append(t_ | 1)
        return out

    def pc_constants(self, lo, hi):
        """Values of `ldr rd, =lit ; ... ; add rd, pc` pairs inside [lo, hi) (Thumb)."""
        consts, a = [], lo
        while a < hi:
            op = self.u16(a)
            if (op >> 11) == 9:
                rd = (op >> 8) & 7
                lit = ((a + 4) & ~3) + (op & 255) * 4
                if self.is_ro(lit):
                    r = rd
                    for k in range(2, 20, 2):
                        o2 = self.u16(a + k)
                        if o2 == (0x4478 | ((r & 8) << 4) | (r & 7)):    # add r, pc
                            consts.append((self.u32(lit) + a + k + 4) & 0xFFFFFFFF)
                            break
                        if (o2 >> 8) == 0x46 and ((o2 >> 3) & 15) == r:  # mov hi, r (DrawDamageNum: fp)
                            r = (o2 & 7) | ((o2 >> 4) & 8)
            a += 2
        return consts

    def extent_of(self, pc):
        import bisect
        j = bisect.bisect_right(self.extent_starts, pc) - 1
        if j >= 0:
            a, n = self.extents[j]
            if a <= pc < a + n:
                return a, a + n
        return self.text_lo, self.text_hi

    def extent_end(self, pc):
        import bisect
        j = bisect.bisect_right(self.extent_starts, pc) - 1
        if j >= 0:
            a, n = self.extents[j]
            if a <= pc < a + n:
                return a + n
        return None

    def discover_entries(self):
        """Function entries: dynsym functions, ARM.exidx starts, call targets, code pointers in data."""
        entries = set()
        for addr in self.func_names:
            if self.in_code(addr & ~1):
                entries.add(addr)
        # exidx: function starts without mode; use the symbol's mode, else guess from the prologue.
        x = self.elf.get_section_by_name(".ARM.exidx")
        d, base = x.data(), x["sh_addr"]
        known = {a & ~1: a for a in entries}
        for i in range(0, len(d), 8):
            w = struct.unpack_from("<I", d, i)[0]
            off = w & 0x7FFFFFFF
            if off & 0x40000000:
                off -= 0x80000000
            a = (base + i + off) & 0xFFFFFFFF
            if a in known or not self.in_code(a):
                continue
            h = self.u16(a)
            w32 = self.u32(a)
            if (w32 & 0xFFFF0000) == 0xE92D0000 or (w32 >> 28) == 0xE:
                entries.add(a)
            elif (h & 0xFF00) in (0xB500, 0xB400, 0xB000) or (h >> 9) == 0x5A:
                entries.add(a | 1)
            else:
                entries.add(a | 1)
            self.notes["exidx_only"].append(a)
        # code pointers in relocated data (vtables, callbacks)
        for off in self.reloc_targets:
            v = self.u32(off) - LOAD_BASE
            if self.in_code(v & ~1) and not (self.plt_lo <= v < self.plt_hi):
                entries.add(v if v & 1 else v & ~3)
        for f in self.init_array:
            entries.add((f - LOAD_BASE) & 0xFFFFFFFF)
        # closure: walk all code reachable from the entries once; call targets become entries.
        todo = list(entries)
        vis = set()
        while todo:
            k = todo.pop()
            if k in vis:
                continue
            vis.add(k)
            ins = self.decode(k)
            if ins.kind == "call" and ins.target is not None:
                t = ins.target
                if not self.plt_stub(t & ~1) and self.in_code(t & ~1):
                    entries.add(t)
                    todo.append(t)
            for s in self.successors(ins, entries):
                if self.in_code(s & ~1):
                    todo.append(s)
        return entries

    def build_function(self, entry, entries):
        """Leaders and blocks of the function starting at entry."""
        insts = {}
        leaders = {entry}
        todo = [entry]
        while todo:
            k = todo.pop()
            if k in insts:
                continue
            ins = self.decode(k)
            insts[k] = ins
            succ = self.successors(ins, entries)
            term = ins.kind is not None and ins.kind != "swi"
            for s in succ:
                if s in entries and s != entry:
                    continue                    # tail call / fallthrough into another function
                if not self.in_code(s & ~1):
                    continue
                if term or s != self.next_key(ins):
                    leaders.add(s)
                todo.append(s)
            if ins.kind in ("call", "icall"):
                leaders.add(self.next_key(ins))
        blocks = {}
        for L in sorted(leaders):
            if L not in insts:
                continue
            lst, k = [], L
            while True:
                ins = insts.get(k)
                if ins is None:
                    break
                lst.append(ins)
                if ins.kind is not None and ins.kind != "swi":
                    break
                k = self.next_key(ins)
                if k in leaders or (k in entries and k != entry):
                    break
            blocks[L] = lst
        return blocks

    # ------------------------------------------------------------ emission
    def fname(self, key):
        return f"F_{key:x}"

    def emit_function(self, entry, blocks, entries):
        G = LOAD_BASE
        out = []
        name = self.func_names.get(entry)
        out.append(f"/* {name or 'sub'} {'thumb' if entry & 1 else 'arm'} 0x{entry:x} */")
        out.append(f"void {self.fname(entry)}(CPU *c){{")
        out.append("FN_PROLOGUE")
        out.append("disp: switch(PC){")
        for k in blocks:
            out.append(f"case {hx(k + G)}:goto L_{k:x};")
        out.append("default: goto other;}")
        bk = set(blocks)

        def goto_key(k):
            """Transfer to a known link-time key."""
            if k in bk:
                return f"goto L_{k:x};"
            if k in entries:
                return f"TAILCALL({self.fname(k)},{hx(k + G)});"
            return f"{{PC={hx(k + G)};goto other;}}"

        def transfer(ins):
            k = ins.kind
            nk = self.next_key(ins)
            if k == "b":
                return goto_key(ins.target)
            if k == "call":
                t = ins.target
                imp = self.plt_stub(t & ~1) if not t & 1 else None
                ret = hx(nk + G)
                cont = goto_key(nk)
                if imp is not None:
                    return f"CALLIMP({self.import_idx[imp]},{ret},{cont});"
                if t in entries:
                    return f"CALL({self.fname(t)},{hx(t + G)},{ret},{cont});"
                return f"CALLX({hx(t + G)},{ret},{cont});"
            if k == "icall":
                return f"CALLX(X,{hx(nk + G)},{goto_key(nk)});"
            if k == "ret":
                return "{PC=X;goto leave;}"
            if k == "ind":
                return "{PC=X;goto disp;}"
            if k == "undef":
                return "{PC=0;goto leave;}"
            raise ValueError(k)

        for k, lst in blocks.items():
            out.append(f"L_{k:x}:")
            last = lst[-1]
            for ins in lst:
                is_term = ins.kind is not None and ins.kind != "swi"
                body = ins.body + (transfer(ins) if is_term else "")
                if ins.kind == "swi":
                    body = f"SPILL;{ins.body}RELOAD;"
                if ins.cond is not None:
                    out.append(f"if({CONDS[ins.cond]}){{{body}}}")
                else:
                    out.append(body)
            if not (last.kind is not None and last.kind != "swi" and last.cond is None):
                out.append(goto_key(self.next_key(last)))
        out.append("FN_EPILOGUE")
        out.append("}")
        return "\n".join(out)

    def run(self, out_dir, chunk=150):
        out = Path(out_dir)
        out.mkdir(parents=True, exist_ok=True)
        self.relocate()
        entries = self.discover_entries()
        # PLT stubs are reached through the import table, not translated (unless patched).
        entries = {e for e in entries if not self.plt_stub(e & ~1) or e & 1}
        funcs = {}
        for e in sorted(entries):
            funcs[e] = self.build_function(e, entries)
        # image
        (out / "image.bin").write_bytes(bytes(self.mem[:self.file_end]))
        hdr = [
            "/* generated by android64/tools/arm2c.py */",
            "#pragma once",
            f"#define GUEST_LOAD_BASE {hx(LOAD_BASE)}",
            f"#define GUEST_IMAGE_SIZE {hx(self.file_end)}",
            f"#define GUEST_MEM_END {hx(self.mem_end)}",
            f"#define GUEST_DATA_END {hx(self.data_end)}",
            f"#define GUEST_TRAP_BASE {hx(TRAP_BASE)}",
            f"#define GUEST_RETURN_MAGIC {hx(RETURN_MAGIC)}",
            f"#define GUEST_IMPORT_COUNT {len(self.imports)}",
            f"#define GUEST_INIT_COUNT {len(self.init_array)}",
        ]
        ex = self.elf.get_section_by_name(".ARM.exidx")
        hdr.append(f"#define GUEST_EXIDX_ADDR {hx(ex['sh_addr'] + LOAD_BASE)}")
        hdr.append(f"#define GUEST_EXIDX_SIZE {hx(ex['sh_size'])}")
        for n, a in self.import_data.items():
            hdr.append(f"#define GUEST_DATA_{n.strip('_')} {hx(a)}")
        (out / "image.h").write_text("\n".join(hdr) + "\n")
        # functions
        keys = sorted(funcs)
        for old in out.glob("fn_*.c"):
            old.unlink()
        files = []
        # Very large functions (NetworkEvent, JanssonParser, ...) go to their own files, which the
        # build compiles at a lower optimisation level: -O2 on one 20k-line switch takes minutes.
        size = {e: sum(len(l) for l in funcs[e].values()) for e in keys}
        big = [e for e in keys if size[e] >= BIG_FUNCTION]
        for e in big:
            fn = out / f"fn_big_{e:x}.c"
            fn.write_text(HEADER + self.emit_function(e, funcs[e], entries) + "\n")
            files.append(fn.name)
        keys_small = [e for e in keys if size[e] < BIG_FUNCTION]
        for ci in range(0, len(keys_small), chunk):
            part = keys_small[ci:ci + chunk]
            src = ['#include "guest.h"', '#include "translated.h"']
            for e in part:
                src.append(self.emit_function(e, funcs[e], entries))
            fn = out / f"fn_{ci // chunk:03d}.c"
            fn.write_text("\n".join(src) + "\n")
            files.append(fn.name)
        # declarations + tables
        decl = ['#pragma once', '#include "guest.h"']
        decl += [f"void {self.fname(e)}(CPU *c);" for e in keys]
        (out / "translated.h").write_text("\n".join(decl) + "\n")
        rows = []
        for e in keys:
            for b in funcs[e]:
                rows.append(((b + LOAD_BASE) & 0xFFFFFFFF, e, b == e))
        rows.sort()
        best = {}
        for key, e, is_entry in rows:   # prefer the function that starts at the key
            if key not in best or is_entry:
                best[key] = (e, is_entry)
        tab = ['#include "guest.h"', '#include "translated.h"',
               "const GuestBlock guest_blocks[] = {"]
        for key in sorted(best):
            e, is_entry = best[key]
            tab.append(f"{{{hx(key)},{1 if is_entry else 0},{self.fname(e)}}},")
        tab.append("};")
        tab.append(f"const unsigned guest_block_count = {len(best)};")
        tab.append("const char *const guest_import_names[] = {" + ",".join(f'"{n}"' for n in self.imports) + "};")
        tab.append("const uint32_t guest_init_array[] = {" + ",".join(hx(a) for a in self.init_array) + "};")
        # exported symbol table (name -> guest key) for JNI entry points and lookups
        ex = sorted((n, v) for n, v in self.syms.items() if n.startswith(("Java_", "JNI_OnLoad")))
        tab.append("const GuestSym guest_exports[] = {" + ",".join(f'{{"{n}",{hx(v + LOAD_BASE)}}}' for n, v in ex) + "};")
        tab.append(f"const unsigned guest_export_count = {len(ex)};")
        (out / "table.c").write_text("\n".join(tab) + "\n")
        # imports: weak default shims
        imp = ['#include "guest.h"']
        for n in self.imports:
            imp.append(f'__attribute__((weak)) void imp_{n}(CPU *c){{guest_missing_import(c,"{n}");}}')
        imp.append("const GuestImport guest_imports[] = {" + ",".join(f"imp_{n}" for n in self.imports) + "};")
        (out / "imports.c").write_text("\n".join(imp) + "\n")
        # report
        nblocks = sum(len(b) for b in funcs.values())
        ninsn = sum(len(l) for b in funcs.values() for l in b.values())
        kinds = defaultdict(int)
        for b in funcs.values():
            for lst in b.values():
                kinds[lst[-1].kind] += 1
        rep = [f"input {self.path}",
               f"functions {len(funcs)} (named {sum(1 for e in funcs if e in self.func_names)})",
               f"blocks {nblocks}, unique block keys {len(best)}, instructions emitted {ninsn}",
               f"decoded instruction keys {len(self.dec)}",
               f"imports {len(self.imports)} functions, data {sorted(self.import_data)}",
               f"init_array {[hex(a) for a in self.init_array]}",
               "block terminators " + ", ".join(f"{k}:{v}" for k, v in sorted(kinds.items(), key=str)),
               f"text coverage {self.coverage():.1%} of .text bytes decoded as reachable code"]
        for k, v in self.notes.items():
            rep.append(f"{k}: {len(v)}")
            for item in v[:40]:
                rep.append(f"    {item if not isinstance(item, int) else hex(item)}")
        (out / "report.txt").write_text("\n".join(rep) + "\n")
        print("\n".join(rep[:9]))
        return files

    def coverage(self):
        covered = set()
        for k, ins in self.dec.items():
            if ins.kind != "undef" and self.text_lo <= ins.pc < self.text_hi:
                covered.update(range(ins.pc, ins.pc + ins.len))
        return len(covered) / (self.text_hi - self.text_lo)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    chunk = 150
    if "--chunk" in sys.argv:
        chunk = int(sys.argv[sys.argv.index("--chunk") + 1])
    Translator(sys.argv[1]).run(sys.argv[2], chunk)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
