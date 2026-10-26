"""README 이미지 만들기 — FileRelay.Tests readme 가 찍은 창 캡처를 다듬어 docs/images/<언어>/ 에 저장한다.

    python tools/readme_images.py <캡처폴더> [출력폴더=docs/images]

<캡처폴더> 안에 en-light, ko-light, en-dark, ko-dark 폴더가 있어야 한다 (각각 PNG + layout.json).
창마다 Windows 11 처럼 모서리를 둥글게 하고 그림자를 넣어 투명 배경에 놓는다.
쪽지 팝업 장면은 layout.json 의 실제 화면 위치 그대로 메인 창 위에 쪽지 창을 겹친다.
Pillow 가 필요하다 (pip install pillow).
"""
import json
import os
import sys

from PIL import Image, ImageChops, ImageDraw, ImageFilter

SS = 4  # 둥근 모서리 마스크 슈퍼샘플링 배율


def rounded_mask(size, radius):
    w, h = size
    big = Image.new("L", (w * SS, h * SS), 0)
    ImageDraw.Draw(big).rounded_rectangle((0, 0, w * SS - 1, h * SS - 1), radius * SS, fill=255)
    return big.resize((w, h), Image.LANCZOS)


class Shots:
    def __init__(self, folder):
        self.folder = folder
        with open(os.path.join(folder, "layout.json"), encoding="utf-8") as f:
            self.layout = json.load(f)
        # 화면 배율 (2560px 창이면 2배) — 모서리·그림자 크기에 쓴다.
        self.scale = max(1, round(self.layout["main"][2] / 1266))

    def img(self, name):
        return Image.open(os.path.join(self.folder, name + ".png")).convert("RGBA")


def window(im, scale, radius=8, border=True):
    """둥근 모서리 + 얇은 테두리 (밝은/어두운 배경 모두에서 창 경계가 보이게)."""
    r = radius * scale
    mask = rounded_mask(im.size, r)
    out = im.copy()
    if border:
        w, h = im.size
        big = Image.new("L", (w * SS, h * SS), 0)
        d = ImageDraw.Draw(big)
        d.rounded_rectangle((0, 0, w * SS - 1, h * SS - 1), r * SS, outline=255, width=SS * max(1, scale // 2 + 1))
        line = big.resize((w, h), Image.LANCZOS)
        edge = Image.new("RGBA", im.size, (128, 128, 128, 0))
        edge.putalpha(line.point(lambda v: v * 90 // 255))
        out = Image.alpha_composite(out, edge)
    out.putalpha(ImageChops.multiply(out.getchannel("A"), mask))
    return out


def scene(items, scale, pad=None):
    """items: [(이미지, x, y)] — 화면 좌표. 그림자를 깔고 순서대로 겹쳐 투명 캔버스에 놓는다."""
    pad = pad or 28 * scale
    blur = 14 * scale
    dy = 6 * scale
    x0 = min(x for _, x, _ in items) - pad
    y0 = min(y for _, _, y in items) - pad
    x1 = max(x + im.width for im, x, _ in items) + pad
    y1 = max(y + im.height for im, _, y in items) + pad
    canvas = Image.new("RGBA", (x1 - x0, y1 - y0), (0, 0, 0, 0))
    for im, x, y in items:
        sh = Image.new("L", canvas.size, 0)
        sh.paste(im.getchannel("A"), (x - x0, y - y0 + dy))
        sh = sh.filter(ImageFilter.GaussianBlur(blur)).point(lambda v: v * 70 // 255)
        shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        shadow.putalpha(sh)
        canvas = Image.alpha_composite(canvas, shadow)
        layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
        layer.paste(im, (x - x0, y - y0))
        canvas = Image.alpha_composite(canvas, layer)
    return canvas


def at(s, name, radius=8):
    x, y, _, _ = s.layout[name]
    return window(s.img(name), s.scale, radius), x, y


def row(images, scale, gap=None):
    """나란히 (위쪽 맞춤)."""
    gap = gap or 36 * scale
    items, x = [], 0
    for im in images:
        items.append((im, x, 0))
        x += im.width + gap
    return items


def save(im, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    im.save(path, optimize=True)
    print(f"{path}  {im.width}x{im.height}  {os.path.getsize(path) // 1024} KB")


def build(src, out):
    for lang in ("en", "ko"):
        light = Shots(os.path.join(src, lang + "-light"))
        dark = Shots(os.path.join(src, lang + "-dark"))
        k = light.scale
        dst = os.path.join(out, lang)

        save(scene([at(light, "main")], k), os.path.join(dst, "main.png"))

        # 폴더 비교: 두 파일 창 부분만
        cmp = light.img("compare")
        # 피어 목록 오른쪽 ~ 파일 창 상태 줄 (기본 창 배치 기준, 배율 1 좌표)
        box = (275 * k, 97 * k, cmp.width, 515 * k + k // 2)
        save(scene([(window(cmp.crop(box), k, 6), 0, 0)], k), os.path.join(dst, "compare.png"))

        # 쪽지가 오면: 메인 창 위 오른쪽 위에 스티커 창 (실제 화면 위치)
        save(scene([at(light, "notes_bg"), at(light, "note_bob"), at(light, "note_carol")], k), os.path.join(dst, "notes-popup.png"))
        # 받은 쪽지 → 답장 → 읽음 확인
        flow = [window(light.img(n), k) for n in ("note_bob", "compose", "note_sent")]
        save(scene(row(flow, k), k), os.path.join(dst, "notes-flow.png"))
        save(scene([at(light, "inbox")], k), os.path.join(dst, "notes-inbox.png"))

        save(scene([at(light, "share_edit")], k), os.path.join(dst, "share-access.png"))
        save(scene([at(light, "conflict")], k), os.path.join(dst, "conflict.png"))

        save(scene([at(dark, "notes_bg"), at(dark, "note_bob"), at(dark, "note_carol")], k), os.path.join(dst, "dark.png"))


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    build(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else os.path.join(os.path.dirname(__file__), "..", "docs", "images"))
