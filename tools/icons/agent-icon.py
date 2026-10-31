"""Writes the path data of Icon.Agent: the stack avatar's silhouette on the Material icons' 24x24 grid.

    python tools/icons/agent-icon.py        prints the path; paste it into icons.json ("own", "Agent")

The stack avatar (Fadenstack's brand art, stack_pose.py and make_agent_avatar.py) is three plates seen from the front:
116 wide and 58 tall (a 2:1 isometric rhombus), 9 thick, their top centres at 62 (the lid), 122 (the middle plate) and
146 (the bottom plate) in a 200 box; two eyes peek out between the lid and the middle plate, and the middle plate's
loose thread end hangs off its right corner as the tail. The icon keeps the 2:1 plates, the order (the lid floats over
the middle plate, which covers the bottom plate), the eyes right under the lid and the tail's sweep, and redraws them at
the weight of the Material icons beside it: filled shapes, 1.5 to 2 unit gaps, plates and tail 1.75 thick, inside the
2..22 live area. The eyes are holes in the middle plate, so the path uses the even-odd rule (F0).
Standard library only.
"""
import math

# On the 24 grid. The plates are 2:1 like the avatar's (58 / 29).
CX = 10.25            # the plates' centre line; the tail takes the room on the right
HW = 6.5              # half a plate's width
HH = HW / 2           # half its depth
T = 1.75              # a plate's thickness, and the tail's
TOP = 2.0             # the lid's top corner
BOTTOM = 22.0         # the bottom plate's lowest corner
GAP_LID = 1.5         # between the lid and the middle plate (perpendicular to the edges)
GAP_BASE = 2.0        # between the middle plate and the bottom plate
BAND = 1.75           # how much of the bottom plate shows under the middle one (vertically)
EYE_RX, EYE_RY = 1.3, 1.55
EYE_DX = 2.4          # each eye's centre from the centre line
EYE_CLEAR = 0.9       # from the gap under the lid down to an eye (vertically, at the eye's shoulder)
# The tail: a cubic from the middle plate's right side, relative to (its right corner, its top centre). The avatar's
# (from its right corner: -6 6, 10 10, 18 26, 10 38 in a 58-unit half plate) is pushed out a little, so the thicker
# stroke clears the bottom plate's corner.
TAIL = ((0, T / 2), (2.0, T / 2), (2.9, 2.2), (2.6, 4.9))

SLOPE = math.hypot(HW, HH) / HW  # vertical size of a gap measured across a slanted plate edge


def fmt(v):
    s = f"{v:.2f}".rstrip("0").rstrip(".")
    if s.startswith("0."):
        s = s[1:]
    elif s.startswith("-0."):
        s = "-" + s[2:]
    return "0" if s in ("", "-0") else s


def pt(p):
    return f"{fmt(p[0])} {fmt(p[1])}"


def cross(a, b, c, d):
    """Where the lines a-b and c-d meet."""
    (x1, y1), (x2, y2), (x3, y3), (x4, y4) = a, b, c, d
    den = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4)
    u = x1 * y2 - y1 * x2
    v = x3 * y4 - y3 * x4
    return (u * (x3 - x4) - (x1 - x2) * v) / den, (u * (y3 - y4) - (y1 - y2) * v) / den


def moved(a, b, d):
    """The line a-b moved by d to the right of its direction (y down)."""
    dx, dy = b[0] - a[0], b[1] - a[1]
    n = math.hypot(dx, dy)
    return (a[0] - dy / n * d, a[1] + dx / n * d), (b[0] - dy / n * d, b[1] + dx / n * d)


def offset_cubic(c, d):
    """A cubic moved sideways by d (Tiller and Hanson: each leg of the control polygon moved, the legs intersected)."""
    legs = [moved(c[i], c[i + 1], d) for i in range(3)]
    return [legs[0][0], cross(*legs[0], *legs[1]), cross(*legs[1], *legs[2]), legs[2][1]]


def path():
    left, right = CX - HW, CX + HW
    lid_y = TOP + HH                                      # the lid's top centre
    mid_y = BOTTOM - HH - T - GAP_BASE * SLOPE - BAND     # the middle plate's, as low as the bottom band allows
    base_y = mid_y + GAP_BASE * SLOPE + BAND              # the bottom plate's

    # The lid: a whole plate.
    lid = [(left, lid_y), (CX, lid_y - HH), (right, lid_y), (right, lid_y + T), (CX, lid_y + HH + T), (left, lid_y + T)]

    # The middle plate, cut under the lid: the lid's lower edges moved down by the gap.
    under_l = moved((left, lid_y + T), (CX, lid_y + HH + T), GAP_LID)
    under_r = moved((CX, lid_y + HH + T), (right, lid_y + T), GAP_LID)
    top_l = ((left, mid_y), (CX, mid_y - HH))
    top_r = ((CX, mid_y - HH), (right, mid_y))
    p1, p2, p3 = cross(*top_l, *under_l), cross(*under_l, *under_r), cross(*under_r, *top_r)
    assert left < p1[0] < p2[0] < p3[0] < right, "the lid must leave the middle plate's corners showing"

    # The tail, a stroke T thick out of the middle plate's right side: its outer edge, a round end, its inner edge.
    centre = [(right + dx, mid_y + dy) for dx, dy in TAIL]
    outer, inner = offset_cubic(centre, -T / 2), offset_cubic(centre, T / 2)
    if outer[0][1] > inner[0][1]:
        outer, inner = inner, outer
    assert abs(outer[0][1] - mid_y) < 1e-9 and abs(inner[0][1] - (mid_y + T)) < 1e-9, "the tail starts on the plate's side"
    middle = (f"M{pt((left, mid_y))}L{pt(p1)}L{pt(p2)}L{pt(p3)}L{pt((right, mid_y))}"
              f"C{pt(outer[1])} {pt(outer[2])} {pt(outer[3])}"
              f"A{fmt(T / 2)} {fmt(T / 2)} 0 0 1 {pt(inner[3])}"
              f"C{pt(inner[2])} {pt(inner[1])} {pt((right, mid_y + T))}"
              f"L{pt((CX, mid_y + HH + T))}L{pt((left, mid_y + T))}z")

    # The eyes: holes in the middle plate, just under the lid.
    shoulder = EYE_DX - EYE_RX * 0.6
    eye_y = TOP + 2 * HH + T + GAP_LID * SLOPE - HH * shoulder / HW + EYE_CLEAR + EYE_RY
    eyes = "".join(
        f"M{pt((CX + side * EYE_DX - EYE_RX, eye_y))}"
        f"a{fmt(EYE_RX)} {fmt(EYE_RY)} 0 1 0 {fmt(2 * EYE_RX)} 0a{fmt(EYE_RX)} {fmt(EYE_RY)} 0 1 0 {fmt(-2 * EYE_RX)} 0z"
        for side in (-1, 1))

    # The bottom plate: what shows under the middle plate, whose lower edges are moved down by the gap.
    over_l = moved((left, mid_y + T), (CX, mid_y + HH + T), GAP_BASE)
    over_r = moved((CX, mid_y + HH + T), (right, mid_y + T), GAP_BASE)
    side_l = over_l[0][1] + (over_l[1][1] - over_l[0][1]) * (left - over_l[0][0]) / (over_l[1][0] - over_l[0][0])
    side_r = over_r[0][1] + (over_r[1][1] - over_r[0][1]) * (right - over_r[0][0]) / (over_r[1][0] - over_r[0][0])
    notch = cross(*over_l, *over_r)
    base = [(left, side_l), notch, (right, side_r), (right, base_y + T), (CX, base_y + HH + T), (left, base_y + T)]

    def poly(points):
        return "M" + " ".join(pt(p) for p in points) + "z"

    return "F0 " + poly(lid) + middle + eyes + poly(base)


if __name__ == "__main__":
    print(path())
