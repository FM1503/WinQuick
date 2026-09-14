"""生成 WinQuick 的应用图标（assets/winquick.ico）。

设计：一个径向轮盘 —— 浅色环形扇区 + 一个强调色扇区 + 中心孔。
在 16px 下也要能认出「一圈东西」这个形态，所以扇区数取 6 而不是 8：
16px 时 8 个扇区每个只有约 2px 宽，会糊成一圈灰。

配色取自 design/tokens.json 的语义（sectorFill / hoverFill），但为托盘做了调整：
托盘图标要在浅色和深色任务栏上都看得清，所以环形用接近白色的浅色 + 深色描边，
而不是界面里那个深灰。

绘制方式：**自己构造环形扇区的多边形，不用 PIL 的 pieslice。**
pieslice 在同时传 fill / outline / width 时的行为不符合预期 ——
第一版就是这么写的，结果整个环形被描边色盖住、一个强调色扇区都没有。
显式多边形 + 单独的 line 描边让每一步都可预期。
"""

from PIL import Image, ImageDraw
import math
import os

SUPERSAMPLE = 4
# 尺寸覆盖常见缩放比例下的小图标请求（100%/125%/150%/175%/200% → 16/20/24/28/32），
# 让外壳能选到最接近的一帧、少一次缩放。大尺寸供任务栏与 Alt+Tab 使用。
SIZES = [256, 128, 64, 48, 40, 32, 28, 24, 20, 16]

SEGMENT_FILL = (245, 245, 245, 255)
SEGMENT_ACCENT = (0, 120, 212, 255)          # 与 tokens 的 hoverFill 一致
OUTLINE = (25, 25, 25, 200)
HUB_FILL = (70, 70, 70, 255)

SECTOR_COUNT = 6
ACCENT_INDEX = 1        # 12 点方向顺时针第二个，视觉上更像"正被选中"


def annular_sector(cx, cy, r_in, r_out, start_deg, end_deg, steps=64):
    """环形扇区的多边形顶点。0° = 正上，顺时针（与 Core 的角度约定一致）。"""
    points = []
    for i in range(steps + 1):
        a = math.radians(start_deg + (end_deg - start_deg) * i / steps)
        points.append((cx + r_out * math.sin(a), cy - r_out * math.cos(a)))
    for i in range(steps, -1, -1):
        a = math.radians(start_deg + (end_deg - start_deg) * i / steps)
        points.append((cx + r_in * math.sin(a), cy - r_in * math.cos(a)))
    return points


def draw_icon(size_px: int) -> Image.Image:
    s = size_px * SUPERSAMPLE
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    cx = cy = s / 2
    outer = s * 0.46
    r_in = s * 0.24          # 环带内缘
    hub_r = s * 0.10         # 中心小圆点
    gap_deg = 8.0            # 扇区之间的缝
    outline_w = max(1, int(s * 0.020))

    sweep = 360.0 / SECTOR_COUNT
    for i in range(SECTOR_COUNT):
        start = i * sweep + gap_deg / 2
        end = (i + 1) * sweep - gap_deg / 2
        fill = SEGMENT_ACCENT if i == ACCENT_INDEX else SEGMENT_FILL

        poly = annular_sector(cx, cy, r_in, outer, start, end)
        d.polygon(poly, fill=fill)
        d.line(poly + [poly[0]], fill=OUTLINE, width=outline_w, joint="curve")

    # 中心小圆点：让"轮盘"的形态更明确，也避免中心完全空掉显得像甜甜圈。
    d.ellipse([cx - hub_r, cy - hub_r, cx + hub_r, cy + hub_r],
              fill=HUB_FILL, outline=OUTLINE, width=outline_w)

    return img.resize((size_px, size_px), Image.LANCZOS)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    out_dir = os.path.join(root, "assets")
    os.makedirs(out_dir, exist_ok=True)
    out_path = os.path.join(out_dir, "winquick.ico")

    images = [draw_icon(n) for n in SIZES]
    images[0].save(out_path, format="ICO",
                   sizes=[(im.width, im.height) for im in images],
                   append_images=images[1:])

    print(f"已生成 {out_path}")


if __name__ == "__main__":
    main()
