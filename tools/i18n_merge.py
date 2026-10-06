"""번역 조각을 언어 파일에 합친다.

조각 파일 형식 (JSON):  { "키": ["한국어", "English"], ... }

  python tools/i18n_merge.py <조각.json> [<조각2.json> ...]

이미 있는 키는 새 값으로 바뀐다. 언어 파일은 키 순서를 유지하며 UTF-8(BOM 없음)로 저장한다.
"""
import json
import os
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src", "FileRelay", "Lang")
LANGS = ["ko", "en"]


def load(lang):
    p = os.path.join(ROOT, lang + ".json")
    if not os.path.exists(p):
        return {}
    with open(p, encoding="utf-8-sig") as f:
        return json.load(f)


def save(lang, d):
    os.makedirs(ROOT, exist_ok=True)
    p = os.path.join(ROOT, lang + ".json")
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        json.dump(d, f, ensure_ascii=False, indent=2)
        f.write("\n")


def main(paths):
    data = {lang: load(lang) for lang in LANGS}
    added = 0
    for path in paths:
        with open(path, encoding="utf-8-sig") as f:
            frag = json.load(f)
        for key, vals in frag.items():
            if not isinstance(vals, list) or len(vals) != len(LANGS):
                raise SystemExit(f"{path}: '{key}' 값은 [ko, en] 배열이어야 합니다")
            for lang, v in zip(LANGS, vals):
                data[lang][key] = v
            added += 1
    for lang in LANGS:
        save(lang, data[lang])
    print(f"merged {added} keys -> total ko={len(data['ko'])} en={len(data['en'])}")


if __name__ == "__main__":
    main(sys.argv[1:])
