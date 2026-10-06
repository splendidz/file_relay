"""다국어 점검.

  python tools/i18n_check.py

  1) 소스(.cs)의 T("키") / TN("키") 가 ko.json / en.json 에 모두 있는지
  2) ko.json 과 en.json 의 키 목록이 같은지
  3) 소스에 남아 있는 한글 문자열 리터럴 (주석 제외) — 번역 누락 후보
  4) 언어 파일의 {0} 같은 자리 표시자 개수가 두 언어에서 같은지
"""
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "src", "FileRelay")
LANG = os.path.join(SRC, "Lang")

# 한글을 남겨도 되는 곳 (파서 별칭, 언어 이름 등)
ALLOW = [
    re.compile(r'case "(없음|읽기|쓰기|전체)"'),   # AccessParse 한글 별칭
    re.compile(r'"한국어"'),                     # 언어 이름은 그 언어로
    re.compile(r'"(나눔고딕|맑은 고딕)"'),         # 글꼴 이름
]

KEY_RE = re.compile(r'\b(?:T|TN|L\.T|L\.TN)\(\s*"([A-Za-z0-9_.\-]+)"')
STR_RE = re.compile(r'"((?:[^"\\\n]|\\.)*)"')
HANGUL = re.compile(r"[가-힣]")
PH = re.compile(r"\{(\d+)(?:[,:][^}]*)?\}")


def load(lang):
    with open(os.path.join(LANG, lang + ".json"), encoding="utf-8-sig") as f:
        return json.load(f)


def main():
    ko, en = load("ko"), load("en")
    problems = 0
    used = set()
    leftovers = []
    for root, dirs, files in os.walk(SRC):
        dirs[:] = [d for d in dirs if d not in ("bin", "obj")]
        for fn in files:
            if not fn.endswith(".cs"):
                continue
            p = os.path.join(root, fn)
            rel = os.path.relpath(p, SRC)
            for no, line in enumerate(open(p, encoding="utf-8"), 1):
                code = line.split("//")[0] if not re.search(r'"[^"]*//', line) else line
                s = line.strip()
                if s.startswith("//") or s.startswith("///"):
                    continue
                for m in KEY_RE.finditer(line):
                    used.add(m.group(1))
                # T(a ? "key1" : "key2") 처럼 간접으로 쓰는 키 — 키와 똑같은 문자열 리터럴
                for m in STR_RE.finditer(line):
                    lit = m.group(1)
                    if lit in ko or (lit + ".other") in ko:
                        used.add(lit)
                if any(a.search(line) for a in ALLOW):
                    continue
                for m in STR_RE.finditer(code):
                    if HANGUL.search(m.group(1)):
                        leftovers.append(f"{rel}:{no}: {m.group(0)[:70]}")

    def has(d, k):
        return k in d or (k + ".other") in d or (k + ".one") in d

    dyn = {k for k in used if k.endswith(".")}      # T("err." + code) 같은 동적 접두사
    used -= dyn
    for k in sorted(used):
        for name, d in (("ko", ko), ("en", en)):
            if not has(d, k):
                print(f"[누락] {name}.json: {k}")
                problems += 1
    for k in sorted(set(ko) ^ set(en)):
        print(f"[불일치] {'ko' if k in ko else 'en'} 에만 있음: {k}")
        problems += 1
    for k in sorted(set(ko) & set(en)):
        a = sorted(set(PH.findall(ko[k])))
        b = sorted(set(PH.findall(en[k])))
        if a != b and not k.endswith(".one"):
            print(f"[자리표시자] {k}: ko={a} en={b}")
            problems += 1
    unused = [k for k in ko if not k.startswith("_") and k not in used
              and k.rsplit(".", 1)[0] not in used
              and not any(k.startswith(p) for p in dyn)]
    if leftovers:
        print(f"\n[한글 리터럴 남음] {len(leftovers)}개")
        for l in leftovers[:400]:
            print("  " + l)
    print(f"\nkeys: ko={len(ko)} en={len(en)} used={len(used)} unused={len(unused)} problems={problems} leftovers={len(leftovers)}")
    if "-v" in sys.argv and unused:
        print("미사용 키: " + ", ".join(unused[:100]))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
