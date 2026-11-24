"""Hide the unused Text/Email friend invite controls without deleting their code or art."""
import struct

NOP = bytes.fromhex('c046')
DRAW_SITES = (
    (0xb3ebe, bytes.fromhex('a847')),      # Text plate
    (0xb417c, bytes.fromhex('a847')),      # Text pressed plate
    (0xb3f08, bytes.fromhex('3df0befe')),  # Text label
    (0xb3f36, bytes.fromhex('a847')),      # Email plate
    (0xb41a6, bytes.fromhex('a847')),      # Email pressed plate
    (0xb3f6e, bytes.fromhex('3df08bfe')),  # Email label
)


def patch(elf):
    data = bytearray(elf)

    def replace(at, expected, replacement):
        current = bytes(data[at:at + len(expected)])
        if current == replacement:
            return
        if current != expected:
            raise ValueError(f'Unsupported friend invite instruction at 0x{at:x}: {current.hex()}')
        data[at:at + len(expected)] = replacement

    for at, expected in DRAW_SITES:
        replace(at, expected, NOP * (len(expected) // 2))
    # Route release IDs 6/7 to the existing no-action branch; retain both handlers.
    table = 0x1c9b14
    for row, target in ((6, 0xb5640), (7, 0xb55ee)):
        replace(table + row * 4, struct.pack('<i', target - table),
                struct.pack('<i', 0xb50c2 - table))
    return bytes(data)
