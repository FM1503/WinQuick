# 执行书：skin-color-redesign-fix-coverkeys（compile-change 编译产物）

> 由 scripts/compile-change.mjs 编译 docs 变更单生成；勿手改，改变更单后重新编译。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.Core/Design/SecondaryColors.cs | 43 | covered.Contains(key) | 1 | <NL>            if (!covered.Contains(key.ToLowerInvariant())) color[key] = value; |
| 2 | design/skins.json | 131 | deadZoneStrokeAlpha | 1 | <NL>          "deadZoneStrokeAlpha": 0.55 |
| 3 | tests/WinQuick.Core.Tests/SkinCatalogTests.cs | 133 | [Fact] | 2 | [Fact] |

## 补丁脚本

```python
import sys, os, json, base64, re
DATA = json.loads(base64.b64decode("eyJzbHVnIjoic2tpbi1jb2xvci1yZWRlc2lnbi1maXgtY292ZXJrZXlzIiwicGF0Y2hlcyI6W3siZmlsZSI6InNyYy9XaW5RdWljay5Db3JlL0Rlc2lnbi9TZWNvbmRhcnlDb2xvcnMuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo0MywiYW5jaG9yIjoiY292ZXJlZC5Db250YWlucyhrZXkpIiwib2xkX2NvdW50IjoxLCJuZXciOiJcbiAgICAgICAgICAgIGlmICghY292ZXJlZC5Db250YWlucyhrZXkuVG9Mb3dlckludmFyaWFudCgpKSkgY29sb3Jba2V5XSA9IHZhbHVlOyJ9LHsiZmlsZSI6ImRlc2lnbi9za2lucy5qc29uIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MTMxLCJhbmNob3IiOiJkZWFkWm9uZVN0cm9rZUFscGhhIiwib2xkX2NvdW50IjoxLCJuZXciOiJcbiAgICAgICAgICBcImRlYWRab25lU3Ryb2tlQWxwaGFcIjogMC41NSJ9LHsiZmlsZSI6InRlc3RzL1dpblF1aWNrLkNvcmUuVGVzdHMvU2tpbkNhdGFsb2dUZXN0cy5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjEzMywiYW5jaG9yIjoiW0ZhY3RdIiwib2xkX2NvdW50IjoyLCJuZXciOiJbRmFjdF0ifV0sImV4cGVjdCI6W3siZmlsZSI6InNyYy9XaW5RdWljay5Db3JlL0Rlc2lnbi9TZWNvbmRhcnlDb2xvcnMuY3MiLCJjb250YWlucyI6ImtleS5Ub0xvd2VySW52YXJpYW50KCkifV19").decode("utf-8"))
SLUG = DATA["slug"]; PATCHES = DATA["patches"]; EXPECT = DATA["expect"]
RB = os.path.join("docs", "changes", SLUG + ".rollback.json")

def fail(file, msg, lines, hit):
    ctx = "\n".join("%4d %s" % (i + 1, l) for i, l in enumerate(lines) if abs(i - hit) <= 3)
    sys.stderr.buffer.write(("[compile-change] FAIL %s: %s\n%s\n" % (file, msg, ctx)).encode("utf-8"))
    sys.exit(1)

# 执行前快照：编辑文件存原始字节，新文件存"已存在"或"created"标记
snap = {}; created = []
for p in PATCHES:
    f = p["file"]
    if p["newfile"]:
        if os.path.exists(f): snap[f] = base64.b64encode(open(f, "rb").read()).decode()
        else: created.append(f)
        continue
    if not os.path.exists(f): fail(f, "文件不存在", [], 0)
    if f not in snap: snap[f] = base64.b64encode(open(f, "rb").read()).decode()
os.makedirs(os.path.dirname(RB), exist_ok=True)
json.dump({"snapshot": snap, "created": created}, open(RB, "w"))

# 逐条应用
for p in PATCHES:
    f = p["file"]
    if p["newfile"]:
        d = os.path.dirname(f)
        if d: os.makedirs(d, exist_ok=True)
        open(f, "wb").write(p["new"].encode("utf-8"))
        continue
    raw = open(f, "rb").read(); text = raw.decode("utf-8")
    # 终修 P2-2：弃用 splitlines(True)（其把 \f \v \x1c-\x1e \x85 \u2028 \u2029 也当行界，
    # 致行号漂移）；改用 re.split 捕获 (\r\n|\r|\n) 重组，仅按这三种分隔符分行且保留原行尾。
    _segs = re.split("(\\r\\n|\\r|\\n)", text)
    ke = []
    for _j in range(0, len(_segs) - 1, 2): ke.append(_segs[_j] + _segs[_j + 1])
    if len(_segs) % 2 == 1 and _segs[-1] != "": ke.append(_segs[-1])  # 末行无行尾（P2-3）
    def _strip(l):
        if l.endswith("\r\n"): return l[:-2]
        if l.endswith("\n") or l.endswith("\r"): return l[:-1]
        return l
    lines = [_strip(l) for l in ke]
    idx = p["lineno"] - 1; oc = p["old_count"]; anchor = p["anchor"]
    if idx < 0 or idx >= len(ke): fail(f, "行号越界 %d" % p["lineno"], lines, max(idx, 0))
    if anchor and anchor not in lines[idx]: fail(f, "锚点不匹配: 期望该行含 %r" % anchor, lines, idx)
    end = idx + oc
    if oc > 0 and end > len(ke): fail(f, "旧行数越界: 行 %d 起 %d 行超出文件" % (p["lineno"], oc), lines, idx)
    _f = ke[idx]
    eol = "\r\n" if _f.endswith("\r\n") else ("\n" if _f.endswith("\n") else ("\r" if _f.endswith("\r") else "\n"))
    if p["new"] == "":
        repl = []  # P1-2：空新内容=删除区间（del lines[idx:end]）
    else:
        repl = [c + eol for c in p["new"].replace("\r\n", "\n").replace("\r", "\n").split("\n")]
        # 终修 P2-3：替换区间含文件末行且原末行无 EOL → 新内容末行同样不补尾 EOL（保持无尾换行）
        if repl and end == len(ke) and not (ke[-1].endswith("\n") or ke[-1].endswith("\r")):
            repl[-1] = _strip(repl[-1])
    ke[idx:end] = repl  # 未触碰行保持原始字节行尾
    open(f, "wb").write("".join(ke).encode("utf-8"))

# expect_after 后置断言
for e in EXPECT:
    f = e["file"]
    if not os.path.exists(f): fail(f, "expect_after 文件不存在", [], 0)
    d = open(f, "rb").read().decode("utf-8")
    if e["contains"] not in d:
        fail(f, "expect_after 缺少子串 %r" % e["contains"], d.replace("\r\n", "\n").split("\n"), 0)

print("[compile-change] OK: %d patches applied" % len(PATCHES))
```

## 执行命令

```bash
node scripts/compile-change.mjs docs/changes/skin-color-redesign-fix-coverkeys.md | python - && ```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-fix-coverkeys.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests -c Release --nologo
```
```

## 回滚说明

执行正向补丁时已快照 docs/changes/skin-color-redesign-fix-coverkeys.rollback.json。回滚（还原编辑、删除新建文件）：

```bash
node scripts/compile-change.mjs docs/changes/skin-color-redesign-fix-coverkeys.md --rollback | python -
```

回滚脚本：

```python
import sys, os, json, base64
SLUG = "skin-color-redesign-fix-coverkeys"
RB = os.path.join("docs", "changes", SLUG + ".rollback.json")
if not os.path.exists(RB):
    sys.stderr.buffer.write(("[compile-change] 回滚失败：缺少 %s\n" % RB).encode("utf-8")); sys.exit(1)
data = json.load(open(RB))
for f, b64 in data.get("snapshot", {}).items():
    d = os.path.dirname(f)
    if d: os.makedirs(d, exist_ok=True)
    open(f, "wb").write(base64.b64decode(b64))
for f in data.get("created", []):
    if os.path.exists(f): os.remove(f)
print("[compile-change] rollback ok: %d restored, %d removed" % (len(data.get("snapshot", {})), len(data.get("created", []))))
```
