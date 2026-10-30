"""Writes every GB 2312 character (all two-byte codes) as one line of UTF-8 text, for pyftsubset --text-file."""
import sys

chars = []
for hi in range(0xA1, 0xF8):
    for lo in range(0xA1, 0xFF):
        try:
            chars.append(bytes([hi, lo]).decode("gb2312"))
        except UnicodeDecodeError:
            pass
with open(sys.argv[1], "w", encoding="utf-8") as f:
    f.write("".join(chars))
print(len(chars), "GB 2312 characters")
