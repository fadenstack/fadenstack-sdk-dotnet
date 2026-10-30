"""Gives a subset the family name "Noto Sans SC" in the legacy name records (ID 1), so that Avalonia finds Regular,
Medium and Bold under one family and tells them apart by weight (OS/2). The Medium file names its family "Noto Sans SC Medium"
there and keeps the real family only in the typographic records (ID 16, 17)."""
import sys
from fontTools.ttLib import TTFont

font = TTFont(sys.argv[1])
name = font["name"]
for rec in list(name.names):
    if rec.nameID in (16, 17):
        name.removeNames(nameID=rec.nameID)
for rec in name.names:
    if rec.nameID == 1:
        rec.string = "Noto Sans SC"
font.save(sys.argv[1])
