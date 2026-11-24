"""Patch the original 1.4.0 settings actions in the shared browser/Android ELF.

Makes Credits (row 2), Terms (row 5), and Inquiry (row 12) inert no-ops, disables
DrawCredit, removes the obsolete Com2uS policy URL, and shows the clean version string
from the language table without a server suffix.
"""
import struct


def branch(source, target):
    delta = target - source - 4
    if delta % 2 or not -2048 <= delta <= 2046:
        raise ValueError('Thumb branch out of range')
    return struct.pack('<H', 0xe000 | ((delta // 2) & 0x7ff))


def patch(elf):
    data = bytearray(elf)

    def replace(at, expected, new):
        current = bytes(data[at:at + len(expected)])
        if current == new:
            return
        if current != expected or len(new) != len(expected):
            raise ValueError(f'Unsupported settings instruction/data at 0x{at:x}: {current.hex()}')
        data[at:at + len(expected)] = new

    # Offsets verified against DrawUIMainOption and its native release dispatcher.
    # Touch rows 2 (Credits), 5 (Terms), and 12 (Inquiry) take the existing no-action path.
    table = 0x1c9e04
    for row, original in ((2, 0xc82ac), (5, 0xc82dc), (12, 0xc8170)):
        replace(table + row * 4, struct.pack('<i', original - table),
                struct.pack('<i', 0xc811c - table))
    replace(0xc7090, bytes.fromhex('f0b5'), bytes.fromhex('7047'))  # DrawCredit: return, even if called directly.
    # Display the complete version in its language-table label, with no server suffix.
    replace(0xc7e42, bytes.fromhex('089a'), branch(0xc7e42, 0xc7e6e))
    # Clear the obsolete Com2uS policy URL string in rodata.
    old_url = b'http://terms.withhive.com/terms/policy/view/M30\0'
    replace(0x1c8e04, old_url, b'\0' * len(old_url))
    return bytes(data)
