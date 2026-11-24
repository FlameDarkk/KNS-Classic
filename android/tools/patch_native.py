#!/usr/bin/env python3
"""Neuter native anti-tamper functions in libgame.so that crash on modern Android.

Several Com2uS `Protection::*` checks call system()/posix_spawn to look for hacking
tools; on Android 14+ that path SIGSEGVs (system() for shell commands is blocked),
killing the game right after login in `nativeStartClet`. We overwrite each target's
prologue with Thumb `movs r0,#0 ; bx lr` (return 0 = "clean"), so the check is a no-op.

Usage: python tools/patch_native.py <decoded_dir_or_libgame.so> [extra_symbol ...] [--no-friend-picker] [--android-arena] [--no-textrel]

--no-friend-picker: enter stages (and Try Again) without the "select a friend" popup.
--android-arena: skip protocol 402's stale temporary story-array cleanup.
--no-textrel: keep the text relocation (the browser build; bionic needs it removed, the JS loader does not).
"""
import sys
from pathlib import Path

from elftools.elf.elffile import ELFFile


# Thumb: movs r0, #0 (0x2000) ; bx lr (0x4770)  -> return 0 immediately.
RET0_THUMB = bytes([0x00, 0x20, 0x70, 0x47])

DEFAULT_TARGETS = [
    "_ZN10Protection19isRunningMemoryHackEv",  # confirmed crash (system())
    "_ZN10Protection8isRootedEv",              # root check (also shells out)
    "_ZN10Protection6verifyEv",                # signature/integrity verify
    # Signing-cert check, called from NetworkEvent during the post-login protocol
    # burst: strcmp(Java GetSHA1(), "EA:DD:CD:E7:...") and exit(0) on mismatch. We
    # re-sign with a debug key, so this always mismatched -> a clean exit(0) that
    # looked like the "lobby crash". Stub returns 0 (the success value) without ever
    # calling GetSHA1 or exit. This is the game's own exit, not a system-lib throw;
    # the intermittent libc++abi mutex line was teardown noise during exit().
    "jCheckSHA1",
    "_ZN10Protection17onDownloadSuccessEPc",
    "_ZN10Protection4saveEv",
]

# operator delete / nothrow delete. Each is `push{r4,lr}; cmp r0,#0; beq end; free(r0); end`.
# On Android 11+ the game frees 8-aligned pointers that Scudo (16-aligned chunks) aborts
# on. We rewrite `cmp r0,#0` -> `lsls r4,r0,#28` (Z set iff low 4 bits are 0) and flip
# `beq`->`bne`, so free() runs only for 16-aligned pointers; misaligned ones are leaked
# instead of crashing. r4 is caller-saved here (pushed/popped), so clobbering it is safe.
ALIGN_DELETE_TARGETS = ["_ZdlPv", "_ZdlPvRKSt9nothrow_t"]
_CMP_R0_0 = bytes([0x00, 0x28])       # cmp r0, #0
_LSLS_R4_R0_28 = bytes([0x04, 0x07])  # lsls r4, r0, #28
_BEQ = bytes([0x01, 0xd0])            # beq .+2 (over the free call)
_BNE = bytes([0x01, 0xd1])            # bne .+2

# PLT stubs to neuter outright. `Protection::nativeFinish(int)` spawns
# `Protection::threadExitApplication`, which does sleep(n) -> kill(getpid(), SIGKILL):
# a self-SIGKILL that leaves no crash log, no tombstone and no Java exception, so it
# looks like a clean exit. `kill` has exactly one call site in the whole library, so
# turning its PLT entry into `mov r0,#0 ; bx lr` disables every anti-tamper self-kill
# regardless of which check fired. PLT stubs are ARM-mode, hence ARM encodings.
PLT_RET0_TARGETS = ["kill"]
_ARM_MOV_R0_0 = bytes.fromhex("0000a0e3")  # mov r0, #0
_ARM_BX_LR = bytes.fromhex("1eff2fe1")     # bx lr
_ARM_NOP = bytes.fromhex("0000a0e1")       # mov r0, r0

# --no-friend-picker (opt-in, libgame.so 1.4.0): skip the "select a friend" (SelectHelper)
# popup before a stage. Once the stage's stamina / bag checks pass, both tap handlers either
# request the helper list (protocol 118 {type: 7}) or open the popup (blDrawSelectHelper = 1);
# the popup's Enter button (touch id 1001) then sends protocol 71 with friend_user_no 0 while
# no helper is selected (i32SelectedHelperIndex = -1, set by both scenes' Init). Each patch is
# one Thumb `b` from that decision straight to the popup's Enter code; r4 (GOT base), r5 (0x14c8)
# and r6 (0x1aec) mean the same at both ends and the Enter code reloads everything else.
#   (address, original 2 bytes, branch target, what)
NO_FRIEND_PICKER = [
    (0x7368a, bytes.fromhex("834b"), 0x73d96,
     "Scene_PointerReleaseEvent_SC_LOBBY (Explore map): Enter -> helper popup Enter"),
    (0x6bcb4, bytes.fromhex("404b"), 0x6b614,
     "Scene_PointerReleaseEvent_SC_EVENT (Event Dungeon): Enter -> helper popup Enter"),
    (0xea09e, bytes.fromhex("5746"), 0xe9c2a,
     "Scene_PointerReleaseEvent_SC_RESULT: Try Again -> helper popup Enter"),
]


# The title's "Additional data will be downloaded automatically. WiFi connection is recommended. Continue?" (text 9999512):
# ProTitleState shows it on every start unless a flag byte is set (0xeba56 ldrb r2,[r5,r2]; cmp r2,#0; beq show; b 0xebc26).
# Every file ships with the build, so the beq becomes a branch to the skip path, which sets the same go-on flag
# ([game+0x3496] = 1; r3 = game) as the dialog's OK. Always applied (browser and Android).
SKIP_DOWNLOAD_PROMPT = (0xeba5a, bytes.fromhex("00d0"), 0xebc26, "ProTitleState: skip the additional-data download prompt")

# "Write a Review" (text 9990053/9990054): Scene_Process_SC_RESULT sets the flag byte game+0x337e after a stage clear
# (0xeb0ca strb r3,[r7,r2]); Scene_Init_SC_LOBBY / SC_POPUP_CHAR then show the review message box and clear it. That store
# is the only writer, so it becomes a Thumb nop (mov r8, r8) and the prompt never appears. Owner's choice; always applied.
SKIP_REVIEW_PROMPT = (0xeb0ca, bytes.fromhex("bb54"), bytes.fromhex("c046"), "Scene_Process_SC_RESULT: never request the Write a Review prompt")

# Dungeon HUD chest row (DrawGetItemList@0xeee7c draws one chest per entry of the pickup list, count at map+0xa70).
# CheckCollisionObjectItemNode appends every picked-up drop to that list, event items (1901001-1910000, e.g. World Tree
# Branch) included (0x13da9e cmp r2,r3; bls 0x13dab2). The result screen never opens a chest for those: they are drawn
# beside the chest row (DrawResult@0xe8334) and credited from event_items. The bls now goes to 0x13daaa, which calls
# cave_record_event_pickup to increment map[+0xa38]/map[+0xa40] and append the drop's offer number to map[+0xa48..0xa64]
# (for Protocol 72 event_items) while playing the pickup sound/fly-off without touching map[+0xa70] or reward_items.
EVENT_PICKUP_NO_HUD_CHEST = (0x13daa0, bytes.fromhex("07d9"), bytes.fromhex("03d9"),
                             "CheckCollisionObjectItemNode: event item pickups add no HUD chest")

# DrawDropItem@0x109214: widen the item-icon ground drop range from 1910001..1910018 to 1901001..1917384 so World Tree
# Branches (1901007) and other collectibles (1901001..1901008) draw their item icon + sparkle on the ground instead of a chest.
EVENT_DROP_ICON_LIT = (0x1093dc, bytes.fromhex("0fdbe2ff"), bytes.fromhex("37fee2ff"),
                       "DrawDropItem: base item_id literal -1910001 -> -1901001")
EVENT_DROP_ICON_CMP = (0x109218, bytes.fromhex("112b3fd8"), bytes.fromhex("9b0b3fd1"),
                       "DrawDropItem: draw 1901001..1917384 as item icon instead of chest")

# Code cave in the dead tail of Protection::isRunningMemoryHack() (0x19a8a8, 584 bytes; first 4 bytes are RET0_THUMB).
_CAVE_BASE = 0x19a8ac


def _thumb_bl(src: int, dst: int) -> bytes:
    """Thumb 32-bit `bl dst` placed at src."""
    off = dst - (src + 4)
    if not -4194304 <= off <= 4194302 or off & 1:
        raise ValueError(f"bl 0x{dst:x} out of range from 0x{src:x}")
    imm11_hi = (off >> 12) & 0x7FF
    imm11_lo = (off >> 1) & 0x7FF
    return (0xF000 | imm11_hi).to_bytes(2, "little") + (0xF800 | imm11_lo).to_bytes(2, "little")


class _ThumbAsm:
    """Minimal Thumb-1 emitter with labels and PC-relative literal pool."""

    def __init__(self, base: int):
        self.base = base
        self.buf = bytearray()
        self.labels: dict[str, int] = {}
        self.fixups: list[tuple[int, str, object]] = []
        self.lits: list[tuple[int, int, int]] = []  # (insn_off, reg, value)

    @property
    def pc(self) -> int:
        return self.base + len(self.buf)

    def label(self, name: str) -> int:
        self.labels[name] = self.pc
        return self.pc

    def h(self, hw: int) -> None:
        self.buf.extend(hw.to_bytes(2, "little"))

    def bl_abs(self, target: int) -> None:
        self.buf.extend(_thumb_bl(self.pc, target))

    def b_cond(self, cond: int, target_label: str) -> None:
        self.fixups.append((len(self.buf), "cond", (cond, target_label)))
        self.h(0xD000 | (cond << 8))

    def b_uncond(self, target_label: str) -> None:
        self.fixups.append((len(self.buf), "b", target_label))
        self.h(0xE000)

    def ldr_lit(self, reg: int, value: int) -> None:
        self.lits.append((len(self.buf), reg, value & 0xFFFFFFFF))
        self.h(0x4800 | (reg << 8))

    def lea(self, reg: int, target: int) -> None:
        """reg = target (PIC): `ldr reg,[pc,#lit]; add reg, pc` with the literal holding target - (add_pc + 4)."""
        self.ldr_lit(reg, target - (self.pc + 6))
        self.h(0x4478 | reg)  # add reg, pc

    def finish(self) -> bytes:
        if len(self.buf) & 2:
            self.h(0x46C0)  # nop (mov r8, r8) to 4-byte align literal pool
        pool_map: dict[int, int] = {}
        for insn_off, reg, val in self.lits:
            if val not in pool_map:
                pool_map[val] = self.pc
                self.buf.extend(val.to_bytes(4, "little"))
            lit_va = pool_map[val]
            pc_aligned = (self.base + insn_off + 4) & ~3
            disp = (lit_va - pc_aligned) // 4
            if not 0 <= disp <= 255:
                raise ValueError(f"literal 0x{val:x} out of range from 0x{self.base + insn_off:x}")
            hw = 0x4800 | (reg << 8) | disp
            self.buf[insn_off:insn_off + 2] = hw.to_bytes(2, "little")
        for insn_off, kind, arg in self.fixups:
            src = self.base + insn_off
            if kind == "cond":
                cond, lbl = arg
                off = self.labels[lbl] - (src + 4)
                assert -256 <= off <= 254 and not (off & 1)
                hw = 0xD000 | (cond << 8) | ((off >> 1) & 0xFF)
            else:
                off = self.labels[arg] - (src + 4)
                assert -2048 <= off <= 2046 and not (off & 1)
                hw = 0xE000 | ((off >> 1) & 0x7FF)
            self.buf[insn_off:insn_off + 2] = hw.to_bytes(2, "little")
        return bytes(self.buf)


def _build_event_drop_cave() -> tuple[bytes, dict[str, int]]:
    """Build Thumb helpers in _CAVE_BASE for multi-branch boss drops and exact branch pickup counting."""
    a = _ThumbAsm(_CAVE_BASE)

    # 1. cave_end_a68: return 0xa68 in r1 (cap event_items loop to 8 slots, freeing map+0xa68/0xa6c).
    a.label("end_a68")
    a.ldr_lit(1, 0xA68)
    a.h(0x4770)  # bx lr

    # 2. cave_inc_8fd / cave_inc_8fe: increment monster_handle[+0x8fd/+0x8fe] instead of setting to 1.
    for name, off in (("inc_8fd", 0x8FD), ("inc_8fe", 0x8FE)):
        a.label(name)
        a.ldr_lit(3, off)
        a.h(0x5CC1)  # ldrb r1, [r0, r3]
        a.h(0x3101)  # adds r1, #1
        a.h(0x54C1)  # strb r1, [r0, r3]
        a.h(0x4770)  # bx lr

    # 3. cave_del_if_gt1: DelDataListIndex(r0=DataList*, r1=idx) only when DataList.count > 1.
    a.label("del_if_gt1")
    a.h(0x6882)  # ldr r2, [r0, #8]
    a.h(0x2A01)  # cmp r2, #1
    a.b_cond(0xD, "del_skip")  # ble del_skip
    a.h(0xB500)  # push {lr}
    a.bl_abs(0x148D90)
    a.h(0xBD00)  # pop {pc}
    a.label("del_skip")
    a.h(0x4770)  # bx lr

    # 4. cave_init_monster_end: call 0x10b418, save offer numbers to map+0xa68/0xa6c, clear map+0xa38/0xa40 & 0xa48..0xa64.
    a.label("init_monster_end")
    a.h(0xB510)  # push {r4, lr}
    a.bl_abs(0x10B418)
    a.h(0x9908)  # ldr r1, [sp, #0x20]  (InitMonster sp+0x18 + 8 bytes pushed)
    a.h(0x4658)  # mov r0, fp
    a.h(0x5843)  # ldr r3, [r0, r1]     (r3 = map)
    a.ldr_lit(1, 0xA48)
    a.h(0x585A)  # ldr r2, [r3, r1]
    a.ldr_lit(4, 0xA68)
    a.h(0x511A)  # str r2, [r3, r4]     (map+0xa68 = offer0)
    a.h(0x3104)  # adds r1, #4          (0xa4c)
    a.h(0x585A)  # ldr r2, [r3, r1]
    a.h(0x3404)  # adds r4, #4          (0xa6c)
    a.h(0x511A)  # str r2, [r3, r4]     (map+0xa6c = offer1)
    a.h(0x3C04)  # subs r4, #4          (r4 = 0xa68)
    a.h(0x2200)  # movs r2, #0
    a.ldr_lit(1, 0xA38)
    a.h(0x505A)  # str r2, [r3, r1]     (map+0xa38 = 0)
    a.h(0x3108)  # adds r1, #8          (0xa40)
    a.h(0x505A)  # str r2, [r3, r1]     (map+0xa40 = 0)
    a.h(0x3108)  # adds r1, #8          (0xa48)
    a.label("clr_loop")
    a.h(0x505A)  # str r2, [r3, r1]
    a.h(0x3104)  # adds r1, #4
    a.h(0x42A1)  # cmp r1, r4           (stop at 0xa68)
    a.b_cond(0x1, "clr_loop")  # bne clr_loop
    a.h(0xBD10)  # pop {r4, pc}

    # 5. cave_set_event0_item_no / cave_set_event1_item_no:
    # Called from CreateDropItem@0x10b2ce / 0x10b308 with r3 = &sp[0x38 + 8*r7], no stack push so sp is CreateDropItem's sp.
    for name, offer_off in (("set_event0_item_no", 0xA68), ("set_event1_item_no", 0xA6C)):
        a.label(name)
        a.h(0x9A00)  # ldr r2, [sp, #0]     (GOT base)
        a.h(0x9909)  # ldr r1, [sp, #0x24]  (0x1424)
        a.h(0x5851)  # ldr r1, [r2, r1]     (r1 = map)
        a.ldr_lit(2, offer_off)
        a.h(0x5889)  # ldr r1, [r1, r2]     (offer_no)
        a.h(0x2200)  # movs r2, #0
        a.h(0x6019)  # str r1, [r3]
        a.h(0x605A)  # str r2, [r3, #4]
        a.h(0x4770)  # bx lr

    # 6. cave_spawn_done: called from CreateDropItem@0x10b0e8 (replaces adds r0, r5, #0; bl 0x148efc).
    # No stack push so sp is CreateDropItem's sp. If r6[+0x8f5] > 1 or r6[+0x8f6] > 1, decrement it,
    # populate sp+0x38[0] with offer_no, fp=0, r8=item_id, sb=count-1, and re-enter the drop body at 0x10af88.
    # Otherwise finish normally.
    a.label("spawn_done")
    for flag_off, id_off, offer_off, next_lbl in (
        (0x8F5, 0xA3C, 0xA68, "spawn_chk1"),
        (0x8F6, 0xA44, 0xA6C, "spawn_exit"),
    ):
        if flag_off == 0x8F6:
            a.label("spawn_chk1")
        a.ldr_lit(3, flag_off)
        a.h(0x5CF2)  # ldrb r2, [r6, r3]
        a.h(0x2A01)  # cmp r2, #1
        a.b_cond(0xD, next_lbl)  # ble next_lbl
        a.h(0x3A01)  # subs r2, #1
        a.h(0x54F2)  # strb r2, [r6, r3]
        a.h(0x9A00)  # ldr r2, [sp, #0]     (GOT base)
        a.h(0x9909)  # ldr r1, [sp, #0x24]  (0x1424)
        a.h(0x5851)  # ldr r1, [r2, r1]     (r1 = map)
        a.ldr_lit(2, offer_off)
        a.h(0x588A)  # ldr r2, [r1, r2]     (offer_no)
        a.h(0x920E)  # str r2, [sp, #0x38]
        a.h(0x2200)  # movs r2, #0
        a.h(0x920F)  # str r2, [sp, #0x3c]
        a.h(0x4693)  # mov fp, r2           (fp = 0)
        a.ldr_lit(2, id_off)
        a.h(0x588A)  # ldr r2, [r1, r2]     (item_id)
        a.h(0x4690)  # mov r8, r2
        # The loop tail (0x10b0d8) does sb += 1; cmp sb, [sp,#4]; beq 0x10b0e8. sb must be count-1 so the burst
        # drop exits back here instead of running past the 5-slot stack arrays.
        a.h(0x9A01)  # ldr r2, [sp, #4]     (drop count)
        a.h(0x3A01)  # subs r2, #1
        a.h(0x4691)  # mov sb, r2
        # PC-relative re-entry (libgame.so is PIC; an absolute `bx` literal crashed at any non-zero load base).
        # lr is dead in CreateDropItem's body; its epilogue pops pc from the stack.
        a.bl_abs(0x10AF88)
    a.label("spawn_exit")
    a.h(0xB500)  # push {lr}
    a.h(0x1C28)  # adds r0, r5, #0
    a.bl_abs(0x148EFC)
    a.h(0xBD00)  # pop {pc}

    # 7. cave_record_event_pickup: called from CheckCollisionObjectItemNode@0x13daaa (r4=dropNode, fp=GOT).
    # Increments map[+0xa38] or map[+0xa40] for matching event item_id, appends dropNode[+0xa78] into
    # first zero slot of map[+0xa48..0xa64], then plays sound 0x9c.
    a.label("record_event_pickup")
    a.h(0xB510)  # push {r4, lr}
    a.ldr_lit(3, 0x1424)
    a.h(0x465A)  # mov r2, fp
    a.h(0x58D2)  # ldr r2, [r2, r3]     (r2 = map)
    a.ldr_lit(3, 0xA68)
    a.h(0x58E0)  # ldr r0, [r4, r3]     (r0 = dropNode.item_id)
    a.ldr_lit(3, 0xA3C)
    a.h(0x58D1)  # ldr r1, [r2, r3]     (map+0xa3c)
    a.h(0x4288)  # cmp r0, r1
    a.b_cond(0x1, "chk_slot1")  # bne chk_slot1
    a.h(0x3B04)  # subs r3, #4          (0xa38)
    a.h(0x58D1)  # ldr r1, [r2, r3]
    a.h(0x3101)  # adds r1, #1
    a.h(0x50D1)  # str r1, [r2, r3]
    a.b_uncond("rec_offer")
    a.label("chk_slot1")
    a.h(0x3308)  # adds r3, #8          (0xa44)
    a.h(0x58D1)  # ldr r1, [r2, r3]
    a.h(0x4288)  # cmp r0, r1
    a.b_cond(0x1, "rec_offer")  # bne rec_offer
    a.h(0x3B04)  # subs r3, #4          (0xa40)
    a.h(0x58D1)  # ldr r1, [r2, r3]
    a.h(0x3101)  # adds r1, #1
    a.h(0x50D1)  # str r1, [r2, r3]
    a.label("rec_offer")
    a.ldr_lit(3, 0xA78)
    a.h(0x58E0)  # ldr r0, [r4, r3]     (r0 = dropNode.offer_no)
    a.h(0x2800)  # cmp r0, #0
    a.b_cond(0x0, "play_sfx")   # beq play_sfx
    a.ldr_lit(3, 0xA48)
    a.ldr_lit(4, 0xA68)
    a.label("find_zero")
    a.h(0x58D1)  # ldr r1, [r2, r3]
    a.h(0x2900)  # cmp r1, #0
    a.b_cond(0x0, "store_offer")  # beq store_offer
    a.h(0x3304)  # adds r3, #4
    a.h(0x42A3)  # cmp r3, r4
    a.b_cond(0x1, "find_zero")  # bne find_zero
    a.b_uncond("play_sfx")
    a.label("store_offer")
    a.h(0x50D0)  # str r0, [r2, r3]
    a.label("play_sfx")
    a.h(0x209C)  # movs r0, #0x9c
    a.bl_abs(0x1647EC)
    a.h(0xBD10)  # pop {r4, pc}

    # Event question-mark preview: InitTiledMap failure used to open the previous
    # monster model alongside the newly selected description. Refuse the popup
    # and release the newly initialized hero before returning through the caller's
    # normal epilogue. Failed InitTiledMap leaves the old map untouched; do not
    # release it again (DrawBossInfo already released it when its last card closed).
    a.label("event_card_map")
    a.h(0xB510)  # push {r4, lr}
    a.bl_abs(0x1680E4)  # InitTiledMap
    a.h(0x2800)  # cmp r0, #0
    a.b_cond(0x1, "event_card_ok")
    a.h(0x21B2)  # movs r1, #0xb2
    a.h(0x0089)  # lsls r1, r1, #2
    a.h(0x1868)  # adds r0, r5, r1 (gameDrawInfo + 0x2c8)
    a.bl_abs(0xFE628)  # DestroyHeroGameData
    a.h(0xBC18)  # pop {r3, r4}: original r4 in r3, return address in r4
    a.h(0x4622)  # mov r2, r4
    a.h(0x461C)  # mov r4, r3
    a.ldr_lit(3, 0x6B00F - 0x6BA3D)  # PIC: return site to common release epilogue
    a.h(0x18D2)  # adds r2, r2, r3
    a.h(0x4710)  # bx r2
    a.label("event_card_ok")
    a.h(0xBD10)  # pop {r4, pc}

    # Protocol 71's event-array parser only writes entries present in the reply.
    # Empty/short arrays otherwise retain the last run's IDs, counts and offers,
    # spawning ghost branches and replaying stale pickup IDs on later stages.
    # At this call r6 is fightInfo+0xe2dc (two IDs, counts and offer numbers).
    a.label("event_start_reset")
    a.h(0xB570)  # push {r4, r5, r6, lr}
    a.h(0x4605)  # mov r5, r0 (JSON array)
    a.h(0x2100)  # movs r1, #0
    for offset in range(0, 24, 4):
        a.h(0x6031 | ((offset // 4) << 6))  # str r1, [r6, #offset]
    a.ldr_lit(3, 0x1424)
    a.h(0x58E0)  # ldr r0, [r4, r3] (map)
    a.ldr_lit(3, 0xA38)
    a.ldr_lit(2, 0xA70)
    a.label("event_start_clear_map")
    a.h(0x50C1)  # str r1, [r0, r3]
    a.h(0x3304)  # adds r3, #4
    a.h(0x4293)  # cmp r3, r2
    a.b_cond(0x1, "event_start_clear_map")
    a.h(0x4628)  # mov r0, r5
    a.bl_abs(0x16AE84)  # json_array_size (original call)
    a.h(0xBD70)  # pop {r4, r5, r6, pc}

    # Protocol 72's receive buffer is cleared before parsing. Read credited
    # event totals from this result reply, never from protocol 71's erased slots.
    # Keep the original wallet parser; r5 is the current JSON result object.
    a.label("event_result_totals")
    a.h(0xB5F8)  # push {r3, r4, r5, r6, r7, lr}
    a.bl_abs(0x14C878)  # ParsingDataToUserGoods (original call)
    a.ldr_lit(3, 0x1424)
    a.h(0x58E7)  # ldr r7, [r4, r3] (map)
    a.ldr_lit(3, 0xA38)
    a.h(0x18FF)  # adds r7, r7, r3 (two result count/id pairs)
    a.h(0x4628)  # mov r0, r5 (result JSON)
    a.lea(1, 0x1CD9AC)  # "drop_event_item_list"
    a.bl_abs(0x16B26C)  # json_object_get
    a.h(0x4604)  # mov r4, r0 (credited array)
    a.h(0x2500)  # movs r5, #0 (slot)
    a.label("event_result_slot")
    a.h(0x4620)  # mov r0, r4
    a.h(0x4629)  # mov r1, r5
    a.bl_abs(0x16AE98)  # json_array_get (missing slots return null)
    a.h(0x4606)  # mov r6, r0
    a.lea(1, 0x1CBDBB)  # "count"
    a.bl_abs(0x16B26C)
    a.bl_abs(0x14996C)  # ParsingJsonObjectToInt32
    a.h(0x6038)  # str r0, [r7]
    a.h(0x4630)  # mov r0, r6
    a.lea(1, 0x1CC467)  # "item_id"
    a.bl_abs(0x16B26C)
    a.bl_abs(0x14996C)
    a.h(0x6078)  # str r0, [r7, #4]
    a.h(0x3708)  # adds r7, #8
    a.h(0x3501)  # adds r5, #1
    a.h(0x2D02)  # cmp r5, #2
    a.b_cond(0x1, "event_result_slot")
    a.h(0xBDF8)  # pop {r3, r4, r5, r6, r7, pc}

    blob = a.finish()
    if len(blob) > 580:
        raise ValueError(f"event drop cave too large: {len(blob)} > 580")
    return blob, a.labels

# Text relocations: bionic refuses DT_TEXTREL for targetSdk >= 23 ("dlopen failed: failed to
# link libgame.so" under Houdini). libgame.so 1.4.0 has exactly one, in crtbegin's __on_dlclose:
#   add r0, pc, #4 ; ldr r0, [r0] ; b __cxa_finalize ; .word __dso_handle  (R_ARM_RELATIVE)
# We rewrite it PC-relative (ldr r0, [pc, #4] ; add r0, pc, r0 ; .word __dso_handle - (here+8)),
# turn the relocation into R_ARM_NONE and the DT_TEXTREL tag into a harmless DT_DEBUG.
_R_ARM_RELATIVE = 23
_DT_TEXTREL = 22
_DT_DEBUG = 21
_ARM_ADD_R0_PC_4 = bytes.fromhex("04008fe2")   # add r0, pc, #4
_ARM_LDR_R0_R0 = bytes.fromhex("000090e5")     # ldr r0, [r0]
_ARM_LDR_R0_PC_4 = bytes.fromhex("04009fe5")   # ldr r0, [pc, #4]
_ARM_ADD_R0_PC_R0 = bytes.fromhex("00008fe0")  # add r0, pc, r0


def _thumb_b(src: int, dst: int) -> bytes:
    """Thumb T2 unconditional branch `b dst` placed at src (range +-2 KB)."""
    off = dst - (src + 4)
    if not -2048 <= off <= 2046 or off & 1:
        raise ValueError(f"b 0x{dst:x} out of range from 0x{src:x}")
    return (0xE000 | ((off >> 1) & 0x7FF)).to_bytes(2, "little")


def find_lib(arg: Path) -> Path:
    if arg.is_file():
        return arg
    hits = list(arg.rglob("libgame.so"))
    if not hits:
        sys.exit(f"no libgame.so under {arg}")
    return hits[0]


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    args = [a for a in sys.argv[2:] if not a.startswith("--")]
    no_friend_picker = "--no-friend-picker" in sys.argv[2:]
    lib = find_lib(Path(sys.argv[1]))
    targets = DEFAULT_TARGETS + args

    with lib.open("rb") as f:
        elf = ELFFile(f)
        segs = [(s['p_vaddr'], s['p_offset'], s['p_filesz'])
                for s in elf.iter_segments() if s['p_type'] == 'PT_LOAD']
        syms = {}
        for sec in elf.iter_sections():
            if sec.name in ('.dynsym', '.symtab'):
                for s in sec.iter_symbols():
                    if s.name:
                        syms[s.name] = s['st_value']
        # imported-symbol -> PLT stub address. ARM .plt is a 20-byte header followed by
        # 12-byte stubs, one per .rel.plt entry, in relocation order.
        plt_addr = {}
        plt_sec = elf.get_section_by_name('.plt')
        rel_plt = elf.get_section_by_name('.rel.plt')
        dynsym = elf.get_section_by_name('.dynsym')
        if plt_sec is not None and rel_plt is not None and dynsym is not None:
            for i, r in enumerate(rel_plt.iter_relocations()):
                nm = dynsym.get_symbol(r['r_info_sym']).name
                if nm:
                    plt_addr[nm] = plt_sec['sh_addr'] + 20 + i * 12
        # Relocations that write into the read-only (executable) load segment.
        rx = [(s['p_vaddr'], s['p_memsz']) for s in elf.iter_segments()
              if s['p_type'] == 'PT_LOAD' and not s['p_flags'] & 2]
        text_rels = []
        for sec_name in ('.rel.dyn', '.rel.plt'):
            sec = elf.get_section_by_name(sec_name)
            if sec is None:
                continue
            for i, r in enumerate(sec.iter_relocations()):
                if r['r_info_type'] and any(v <= r['r_offset'] < v + m for v, m in rx):
                    text_rels.append((sec['sh_offset'] + i * sec['sh_entsize'], r['r_offset'], r['r_info_type']))
        dyn = elf.get_section_by_name('.dynamic')
        textrel_tag_offs = [dyn['sh_offset'] + i * dyn['sh_entsize']
                            for i, t in enumerate(dyn.iter_tags()) if t.entry.d_tag == 'DT_TEXTREL']

    def vaddr_to_off(vaddr):
        for v, o, fsz in segs:
            if v <= vaddr < v + fsz:
                return o + (vaddr - v)
        return None

    data = bytearray(lib.read_bytes())
    patched = 0
    for name in targets:
        if name not in syms:
            print(f"[skip] {name} not found")
            continue
        va = syms[name] & ~1          # clear Thumb bit
        off = vaddr_to_off(va)
        if off is None:
            print(f"[skip] {name} @0x{va:x} not in a loadable segment")
            continue
        orig = bytes(data[off:off + 4])
        data[off:off + 4] = RET0_THUMB
        print(f"[patch] {name} @0x{va:08x} (off 0x{off:x}): {orig.hex()} -> {RET0_THUMB.hex()}")
        patched += 1

    # Scudo-safe delete: skip free() of misaligned pointers (see ALIGN_DELETE_TARGETS).
    for name in ALIGN_DELETE_TARGETS:
        if name not in syms:
            print(f"[skip] {name} not found")
            continue
        va = syms[name] & ~1
        off = vaddr_to_off(va)
        if off is None:
            continue
        # layout: +0 push{r4,lr}; +2 cmp r0,#0; +4 beq end
        if bytes(data[off + 2:off + 4]) == _CMP_R0_0 and bytes(data[off + 4:off + 6]) == _BEQ:
            data[off + 2:off + 4] = _LSLS_R4_R0_28
            data[off + 4:off + 6] = _BNE
            patched += 1
            print(f"[patch] {name} @0x{va:08x}: skip free() of misaligned (non-16) pointers")
        else:
            print(f"[skip] {name} @0x{va:08x}: unexpected prologue "
                  f"{bytes(data[off+2:off+6]).hex()}")

    # Anti-tamper self-kill: make kill() a no-op (see PLT_RET0_TARGETS).
    for name in PLT_RET0_TARGETS:
        va = plt_addr.get(name)
        if va is None:
            print(f"[skip] PLT {name} not found")
            continue
        off = vaddr_to_off(va)
        if off is None:
            continue
        orig = bytes(data[off:off + 12])
        data[off:off + 12] = _ARM_MOV_R0_0 + _ARM_BX_LR + _ARM_NOP
        print(f"[patch] PLT {name}() @0x{va:08x} (off 0x{off:x}): {orig.hex()} -> no-op")
        patched += 1

    # Remove text relocations (see _DT_TEXTREL).
    unresolved = 0
    if "--no-textrel" in sys.argv[2:]:
        text_rels, textrel_tag_offs = [], []
    for rel_off, where, rtype in text_rels:
        off = vaddr_to_off(where)
        code = bytes(data[off - 12:off - 4])
        if rtype == _R_ARM_RELATIVE and code == _ARM_ADD_R0_PC_4 + _ARM_LDR_R0_R0:
            target = int.from_bytes(data[off:off + 4], "little")
            data[off - 12:off - 4] = _ARM_LDR_R0_PC_4 + _ARM_ADD_R0_PC_R0
            data[off:off + 4] = ((target - where) & 0xFFFFFFFF).to_bytes(4, "little")
            data[rel_off + 4:rel_off + 8] = bytes(4)  # r_info = R_ARM_NONE
            patched += 1
            print(f"[patch] text reloc @0x{where:08x} -> PC-relative 0x{target:x}")
        else:
            unresolved += 1
            print(f"[skip] text reloc @0x{where:08x} type {rtype}: unknown pattern {code.hex()}")
    if not unresolved:
        for tag_off in textrel_tag_offs:
            data[tag_off:tag_off + 4] = _DT_DEBUG.to_bytes(4, "little")
            patched += 1
            print(f"[patch] DT_TEXTREL -> DT_DEBUG (off 0x{tag_off:x})")
    elif textrel_tag_offs:
        print(f"[warn] {unresolved} text reloc(s) left; DT_TEXTREL kept (fails on targetSdk >= 23)")

    va, orig, dst, what = SKIP_DOWNLOAD_PROMPT
    off = vaddr_to_off(va)
    new = _thumb_b(va, dst)
    cur = bytes(data[off:off + 2]) if off is not None else b""
    if cur == new:
        print(f"[skip] {what}: already patched")
    elif cur != orig:
        print(f"[skip] {what} @0x{va:08x}: expected {orig.hex()}, found {cur.hex()} (not 1.4.0?)")
    else:
        data[off:off + 2] = new
        patched += 1
        print(f"[patch] {what} @0x{va:08x}: {orig.hex()} -> {new.hex()} (b 0x{dst:x})")

    for va, orig, new, what in (SKIP_REVIEW_PROMPT, EVENT_PICKUP_NO_HUD_CHEST, EVENT_DROP_ICON_LIT, EVENT_DROP_ICON_CMP):
        off = vaddr_to_off(va)
        n = len(orig)
        cur = bytes(data[off:off + n]) if off is not None else b""
        if cur == new:
            print(f"[skip] {what}: already patched")
        elif cur != orig:
            print(f"[skip] {what} @0x{va:08x}: expected {orig.hex()}, found {cur.hex()} (not 1.4.0?)")
        else:
            data[off:off + n] = new
            patched += 1
            print(f"[patch] {what} @0x{va:08x}: {orig.hex()} -> {new.hex()}")

    cave_blob, cave_labels = _build_event_drop_cave()
    cave_off = vaddr_to_off(_CAVE_BASE)
    if cave_off is not None and bytes(data[cave_off:cave_off + len(cave_blob)]) != cave_blob:
        data[cave_off:cave_off + len(cave_blob)] = cave_blob
        patched += 1
        print(f"[patch] event-drop cave @0x{_CAVE_BASE:08x} ({len(cave_blob)} bytes)")
    event_hooks = [
        (0x15E5B4, _thumb_bl(0x15E5B4, 0x16AE84), _thumb_bl(0x15E5B4, cave_labels["event_start_reset"]),
         "Stage start: reset both event-offer slots and stale map branch state"),
        (0x15FD06, _thumb_bl(0x15FD06, 0x14C878), _thumb_bl(0x15FD06, cave_labels["event_result_totals"]),
         "Stage result: display all credited event drops on clear, none on failure"),
        (0x6BA38, _thumb_bl(0x6BA38, 0x1680E4), _thumb_bl(0x6BA38, cave_labels["event_card_map"]),
         "Event boss card: reject failed monster-map load before opening popup"),
        (0x0EB35E, bytes.fromhex("a7210901"), _thumb_bl(0x0EB35E, cave_labels["end_a68"]),
         "Scene_Init_SC_RESULT: cap event_items loop to 8 slots"),
        (0x10FE74, bytes.fromhex("504b0121c154"), _thumb_bl(0x10FE74, cave_labels["inc_8fd"]) + bytes.fromhex("c046"),
         "InitMonster: increment slot-0 event drop count"),
        (0x10FE7E, bytes.fromhex("38f087ff"), _thumb_bl(0x10FE7E, cave_labels["del_if_gt1"]),
         "InitMonster: keep last monster for remaining slot-0 drops"),
        (0x10FEEA, bytes.fromhex("344b0121c154"), _thumb_bl(0x10FEEA, cave_labels["inc_8fe"]) + bytes.fromhex("c046"),
         "InitMonster: increment slot-1 event drop count"),
        (0x10FEF4, bytes.fromhex("38f04cff"), _thumb_bl(0x10FEF4, cave_labels["del_if_gt1"]),
         "InitMonster: keep last monster for remaining slot-1 drops"),
        (0x10FF18, bytes.fromhex("fbf77efa"), _thumb_bl(0x10FF18, cave_labels["init_monster_end"]),
         "InitMonster: save offer numbers and zero picked-up event counters"),
        (0x10B2CE, bytes.fromhex("0021002219605a60"), _thumb_bl(0x10B2CE, cave_labels["set_event0_item_no"]) + bytes.fromhex("c046c046"),
         "CreateDropItem: attach slot-0 offer_no to dropped event item"),
        (0x10B308, bytes.fromhex("0021002219605a60"), _thumb_bl(0x10B308, cave_labels["set_event1_item_no"]) + bytes.fromhex("c046c046"),
         "CreateDropItem: attach slot-1 offer_no to dropped event item"),
        (0x10B0E8, bytes.fromhex("281c3df007ff"), _thumb_bl(0x10B0E8, cave_labels["spawn_done"]) + bytes.fromhex("c046"),
         "CreateDropItem: burst-drop all stacked event items on boss/last monster"),
        (0x13DAAA, bytes.fromhex("9c2026f09efe"), _thumb_bl(0x13DAAA, cave_labels["record_event_pickup"]) + bytes.fromhex("c046"),
         "CheckCollisionObjectItemNode: record event item pickup count and offer_no"),
    ]
    for va, orig, new, what in event_hooks:
        off = vaddr_to_off(va)
        n = len(orig)
        cur = bytes(data[off:off + n]) if off is not None else b""
        if cur == new:
            print(f"[skip] {what}: already patched")
        elif cur != orig:
            print(f"[skip] {what} @0x{va:08x}: expected {orig.hex()}, found {cur.hex()} (not 1.4.0?)")
        else:
            data[off:off + n] = new
            patched += 1
            print(f"[patch] {what} @0x{va:08x}: {orig.hex()} -> {new.hex()}")

    if no_friend_picker:
        for va, orig, dst, what in NO_FRIEND_PICKER:
            off = vaddr_to_off(va)
            new = _thumb_b(va, dst)
            cur = bytes(data[off:off + 2])
            if cur == new:
                print(f"[skip] {what}: already patched")
            elif cur != orig:
                print(f"[skip] {what} @0x{va:08x}: expected {orig.hex()}, found {cur.hex()} (not 1.4.0?)")
            else:
                data[off:off + 2] = new
                patched += 1
                print(f"[patch] {what} @0x{va:08x}: {orig.hex()} -> {new.hex()} (b 0x{dst:x})")

    if "--android-arena" in sys.argv[2:]:
        # JanssonParser case 402 updates story cards directly at 0x15ce5a.
        # Its post-handler incorrectly consumes packet+0xcfbc, a temporary
        # buffer owned by other reply cases. A captured stale pointer reaches
        # C2S_knlFree at 0x151890 and aborts in Android Scudo. Resume the normal
        # battle setup after that obsolete block; retain parser-applied cards.
        va, destination = 0x1517f0, 0x1518a0
        off = vaddr_to_off(va)
        expected, replacement = bytes.fromhex("834a"), _thumb_b(va, destination)
        if off is None or bytes(data[off:off + 2]) not in (expected, replacement):
            sys.exit("Android Arena patch: unsupported native instruction")
        if bytes(data[off:off + 2]) == expected:
            data[off:off + 2] = replacement
            patched += 1
            print(f"[patch] Android Arena stale story-array cleanup @0x{va:x} -> 0x{destination:x}")

    if patched:
        lib.write_bytes(bytes(data))
    print(f"[done] patched {patched} function(s) in {lib.name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
