"""FileRelay 앱 아이콘(app.ico) 생성기.

둥근 사각형 그라데이션 배경 위에 '주고받기' 화살표 두 개를 그린다.
작은 크기(16~24px)에서도 형태가 보이도록 크기별로 따로 그린다.

  python tools/make_icon.py
"""
import os
from PIL import Image, ImageDraw

OUT = os.path.join(os.path.dirname(__file__), "..", "src", "FileRelay", "Resources", "app.ico")
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

TOP = (151, 110, 255)     # #976EFF (밝은 보라)
BOTTOM = (92, 46, 214)    # #5C2ED6 (진한 보라)


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def draw(size):
    ss = 4                                  # 슈퍼샘플링 배율
    S = size * ss
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # 대각선 그라데이션
    grad = Image.new("RGBA", (S, S))
    gp = grad.load()
    for y in range(S):
        for x in range(S):
            t = (x * 0.35 + y * 0.65) / S
            gp[x, y] = lerp(TOP, BOTTOM, t) + (255,)

    pad = max(1, int(S * 0.04))
    radius = int(S * 0.24)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, S - pad - 1, S - pad - 1], radius=radius, fill=255)
    img.paste(grad, (0, 0), mask)

    d = ImageDraw.Draw(img)
    white = (255, 255, 255, 255)
    # 위로 가는 화살표 (왼쪽)  /  아래로 가는 화살표 (오른쪽)
    w = S * (0.13 if size >= 32 else 0.16)  # 화살표 몸통 두께
    head = S * (0.17 if size >= 32 else 0.2)
    cx1, cx2 = S * 0.36, S * 0.64
    top, bot = S * 0.24, S * 0.76

    def arrow(cx, up):
        if up:
            tip = top
            base = tip + head * 1.1
            d.polygon([(cx, tip), (cx - head, base), (cx + head, base)], fill=white)
            d.rectangle([cx - w / 2, base - 1, cx + w / 2, bot], fill=white)
        else:
            tip = bot
            base = tip - head * 1.1
            d.polygon([(cx, tip), (cx - head, base), (cx + head, base)], fill=white)
            d.rectangle([cx - w / 2, top, cx + w / 2, base + 1], fill=white)

    arrow(cx1, True)
    arrow(cx2, False)
    return img.resize((size, size), Image.LANCZOS)


def main():
    imgs = [draw(s) for s in SIZES]
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    big = imgs[-1]
    big.save(OUT, format="ICO", sizes=[(s, s) for s in SIZES], append_images=imgs[:-1])
    print("saved", os.path.abspath(OUT))


if __name__ == "__main__":
    main()
