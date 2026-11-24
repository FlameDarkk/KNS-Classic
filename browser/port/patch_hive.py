"""Remove Hive brand badges from Arena lists and player-detail nameplates.

Only the five verified symbol-855 draw calls change. Rank badges, avatars, names,
scores, layout and the source artwork remain intact. Android consumes this ELF.
"""

# Static addresses in the original 1.4.0 ELF, verified in the active browser payload.
SITES = (
    (0x050fa8, bytes.fromhex('e047'), 'Arena opponents'),
    (0x051580, bytes.fromhex('a047'), 'Arena rankings'),
    (0x1396b0, bytes.fromhex('a847'), 'comparison profile left'),
    (0x139840, bytes.fromhex('a847'), 'comparison profile right'),
    (0x13b03e, bytes.fromhex('b047'), 'single player profile'),
)
NOP = bytes.fromhex('c046')


def patch(elf):
    data = bytearray(elf)
    for at, expected, label in SITES:
        current = bytes(data[at:at + 2])
        if current == NOP:
            continue
        if current != expected:
            raise ValueError(f'Unsupported {label} Hive draw at 0x{at:x}: {current.hex()}')
        data[at:at + 2] = NOP
    return bytes(data)
