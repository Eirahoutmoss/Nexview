"""Kaynak koddaki çeviri anahtarlarını bulur ve en.json'da eksik olanları listeler.
Kullanım: python tools/check_i18n.py [--dump]"""
import json, os, re, sys
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src")
EN = os.path.join(ROOT, "NexView.Core", "Localization", "en.json")
pat_cs = re.compile(r'L\.T\(\s*"((?:[^"\\]|\\.)*)"')
pat_xaml = re.compile(r"\{l:T '([^']*)'\}")
keys = []
for dp, _, files in os.walk(ROOT):
    if os.sep + "bin" in dp or os.sep + "obj" in dp: continue
    for f in files:
        p = os.path.join(dp, f)
        if f.endswith(".cs"):
            for m in pat_cs.finditer(open(p, encoding="utf-8").read()):
                keys.append(m.group(1).replace('\\"', '"').replace("\\\\", "\\"))
        elif f.endswith(".axaml"):
            keys += pat_xaml.findall(open(p, encoding="utf-8").read())
# Core'da L.T ile çevrilen preset adları/açıklamaları
src = open(os.path.join(ROOT, "NexView.Core", "Conversion", "Presets.cs"), encoding="utf-8").read()
keys += re.findall(r'new\("\w+", "([^"]+)", "([^"]+)"', src) and [k for pair in re.findall(r'new\("\w+", "([^"]+)", "([^"]+)"', src) for k in pair]
uniq = list(dict.fromkeys(keys))
en = json.load(open(EN, encoding="utf-8")) if os.path.exists(EN) else {}
missing = [k for k in uniq if k not in en]
if "--dump" in sys.argv:
    print(json.dumps(missing, ensure_ascii=False, indent=1))
else:
    print(f"{len(uniq)} anahtar, {len(missing)} eksik çeviri")
    for k in missing: print("  -", k)
    sys.exit(1 if missing else 0)
