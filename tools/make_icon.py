"""生成 ADBDrive 应用图标（多尺寸 ico + 256 png）。"""
import os
from PIL import Image, ImageDraw

SIZE = 1024
SS = 4  # 超采样倍数

def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))

def make_base():
    w = SIZE * SS
    img = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    # 背景圆角矩形 + 垂直渐变
    grad = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    gd = ImageDraw.Draw(grad)
    top = (46, 134, 222)
    bottom = (14, 63, 126)
    for y in range(w):
        c = lerp(top, bottom, y / (w - 1))
        gd.line([(0, y), (w, y)], fill=c + (255,))
    mask = Image.new("L", (w, w), 0)
    md = ImageDraw.Draw(mask)
    md.rounded_rectangle([0, 0, w - 1, w - 1], radius=int(w * 0.22), fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    # 顶部高光
    hl = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    hd.ellipse([-w * 0.3, -w * 0.55, w * 1.1, w * 0.55], fill=(255, 255, 255, 26))
    img = Image.alpha_composite(img, hl)
    d = ImageDraw.Draw(img)

    # 白色磁盘主体
    left, right = int(w * 0.20), int(w * 0.80)
    top_y, bottom_y = int(w * 0.33), int(w * 0.685)
    d.rounded_rectangle([left, top_y, right, bottom_y], radius=int(w * 0.085), fill=(255, 255, 255, 255))

    # 磁盘凹槽 + 指示灯
    d.rounded_rectangle(
        [int(w * 0.28), int(w * 0.40), int(w * 0.60), int(w * 0.455)],
        radius=int(w * 0.028),
        fill=(23, 84, 156, 255),
    )
    d.ellipse(
        [int(w * 0.665), int(w * 0.395), int(w * 0.735), int(w * 0.465)],
        fill=(23, 84, 156, 255),
    )

    # 无线信号弧（磁盘上方）
    cx, cy = int(w * 0.50), int(w * 0.245)
    for i, r in enumerate([int(w * 0.055), int(w * 0.105), int(w * 0.155)]):
        width = int(w * 0.022)
        d.arc(
            [cx - r, cy - r, cx + r, cy + r],
            start=200,
            end=340,
            fill=(255, 255, 255, 235),
            width=width,
        )
    d.ellipse([cx - int(w * 0.018), cy - int(w * 0.018), cx + int(w * 0.018), cy + int(w * 0.018)],
              fill=(255, 255, 255, 255))
    return img

def main():
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "ADBDrive.App", "Assets")
    out_dir = os.path.normpath(out_dir)
    os.makedirs(out_dir, exist_ok=True)

    base = make_base()
    sizes = [16, 24, 32, 48, 64, 128, 256]
    frames = []
    for s in sizes:
        img = base.resize((s, s), Image.LANCZOS)
        frames.append(img)

    ico_path = os.path.join(out_dir, "app.ico")
    frames[-1].save(ico_path, format="ICO", sizes=[(s, s) for s in sizes])
    png_path = os.path.join(out_dir, "app-256.png")
    frames[-1].save(png_path, format="PNG")
    print("written:", ico_path, png_path)

if __name__ == "__main__":
    main()
