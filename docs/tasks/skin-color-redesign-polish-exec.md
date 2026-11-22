# 执行书：skin-color-redesign-polish（compile-change 编译产物）

> 由 scripts/compile-change.mjs 编译 docs 变更单生成；勿手改，改变更单后重新编译。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.App/Radial/SkinPanel.xaml | 30 | Wq.Color.Accent | 1 | <NL>            <Setter Property="Foreground" Value="{DynamicResource Wq.Color.Text}" /> |
| 2 | src/WinQuick.App/Design/TokenResources.cs | 120 | Set("Color.Accent", TokenBrushes.Brush(panel.Accent)); | 1 | <NL>        Set("Color.Accent", TokenBrushes.Brush(panel.Accent)); |
| 3 | src/WinQuick.App/Design/SystemSkin.cs | 53 | ["label"] = "#1B1B1B", | 1 | <NL>                ["label"] = "#1B1B1B", |
| 4 | design/skins.json | 132 | deadZoneStrokeAlpha | 1 | <NL>          "deadZoneStrokeAlpha": 0.55 |
| 5 | design/skins.json | 17 | 新令牌（strokeWidth | 1 | <NL>    "新令牌（strokeWidth / halo* / hoverFillAlpha / hoverLabel / childStaggerMs）", |
| 6 | src/WinQuick.Core/Design/ColorMath.cs | 102 | ]; | 1 | <NL>    ]; |
| 7 | src/WinQuick.Core/Design/SkinCatalog.cs | 111 | public string MergeWithOverrides | 1 | <NL>    public string MergeWithOverrides(string baseTokensJson, JsonObject overrides) |
| 8 | src/WinQuick.Core/Design/SkinCatalog.cs | 91 | public string Merge( | 1 | <NL>    public string Merge(string baseTokensJson, string? skinId) |
| 9 | src/WinQuick.Core/Design/DesignTokens.cs | 304 | public string HoverLabel | 1 | <NL>    public string HoverLabel { get; init; } = "#FFFFFF"; |
| 10 | src/WinQuick.Core/Design/DesignTokens.cs | 83 | if (Color.HoverFillAlpha is | 1 | <NL>        if (Color.HoverFillAlpha is < 0 or > 1) |
| 11 | tests/WinQuick.Core.Tests/SkinCatalogTests.cs | 133 | [Fact] | 1 | <NL>    [Fact] |
| 12 | docs/specs/skin-color-redesign.md | 65 | R5 面板标题类 | 2 | R5 面板标题类「强调色当文字」处（Section 用 Color.Accent）：给出两档让用户挑<NL>   （达标保证的强调色标题 vs Text 色 + SemiBold），依据是微软明文不建议在 acrylic 面上放强调色文字。<NL>   **定稿（2026-11-23 用户原型确认，默认档）**：B 档 —— Section 标题用 Text 色 + SemiBold（SkinPanel.xaml），Accent 只保留「当线/当点缀」语义。 |
| 13 | docs/specs/skin-color-redesign.md | 58 | R2 半透明皮肤加 | 2 | R2 半透明皮肤加 **scrim 保底层**（Fluent Acrylic 的真实构成），白字在浅色/纯白壁纸上达标；<NL>   **定稿（2026-11-23 用户原型挑定）**：五档 A–E（0.45/0.55 提亮、0.65 提亮、0.65 压暗、0.72 压暗）中取 D 档 —— scrim α0.65 + 悬停压暗（hoverFill #000000 α0.18）。 |
| 14 | docs/specs/skin-color-redesign.md | 116 | 进 DesignTokens.Validate 域（0..1） | 1 | 进 DesignTokens.Validate 域（0..0.9 —— 实现时把上限从 1 收紧到 0.9，超过就不是玻璃是黑板；reviewer P2 勘误）。默认皮肤行为不变。 |
| 15 | docs/specs/skin-color-redesign.md | 143 | 页内 selfCheck() | 1 | - 页内 selfCheck()（onerror + 关键配对计算复核），`node preview/_verify.mjs` 可跑纯逻辑自检。<NL>  注（reviewer P2-3 勘误）：原型 selfCheck 的 W4/W5 按面板合成底口径，比单测口径乐观；单测按实际渲染分支（scrim 保底盘死区、角标叠扇区底）判定 —— 终判以单测与实机为准，两者当时皆过。 |

## 补丁脚本

```python
import sys, os, json, base64, re
DATA = json.loads(base64.b64decode("eyJzbHVnIjoic2tpbi1jb2xvci1yZWRlc2lnbi1wb2xpc2giLCJwYXRjaGVzIjpbeyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9SYWRpYWwvU2tpblBhbmVsLnhhbWwiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjozMCwiYW5jaG9yIjoiV3EuQ29sb3IuQWNjZW50Iiwib2xkX2NvdW50IjoxLCJuZXciOiJcbiAgICAgICAgICAgIDxTZXR0ZXIgUHJvcGVydHk9XCJGb3JlZ3JvdW5kXCIgVmFsdWU9XCJ7RHluYW1pY1Jlc291cmNlIFdxLkNvbG9yLlRleHR9XCIgLz4ifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL0Rlc2lnbi9Ub2tlblJlc291cmNlcy5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjEyMCwiYW5jaG9yIjoiU2V0KFwiQ29sb3IuQWNjZW50XCIsIFRva2VuQnJ1c2hlcy5CcnVzaChwYW5lbC5BY2NlbnQpKTsiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgICAgICBTZXQoXCJDb2xvci5BY2NlbnRcIiwgVG9rZW5CcnVzaGVzLkJydXNoKHBhbmVsLkFjY2VudCkpOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvRGVzaWduL1N5c3RlbVNraW4uY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo1MywiYW5jaG9yIjoiW1wibGFiZWxcIl0gPSBcIiMxQjFCMUJcIiwiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgICAgICAgICAgICAgIFtcImxhYmVsXCJdID0gXCIjMUIxQjFCXCIsIn0seyJmaWxlIjoiZGVzaWduL3NraW5zLmpzb24iLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjoxMzIsImFuY2hvciI6ImRlYWRab25lU3Ryb2tlQWxwaGEiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgICAgICAgIFwiZGVhZFpvbmVTdHJva2VBbHBoYVwiOiAwLjU1In0seyJmaWxlIjoiZGVzaWduL3NraW5zLmpzb24iLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjoxNywiYW5jaG9yIjoi5paw5Luk54mM77yIc3Ryb2tlV2lkdGgiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgIFwi5paw5Luk54mM77yIc3Ryb2tlV2lkdGggLyBoYWxvKiAvIGhvdmVyRmlsbEFscGhhIC8gaG92ZXJMYWJlbCAvIGNoaWxkU3RhZ2dlck1z77yJXCIsIn0seyJmaWxlIjoic3JjL1dpblF1aWNrLkNvcmUvRGVzaWduL0NvbG9yTWF0aC5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjEwMiwiYW5jaG9yIjoiXTsiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgIF07In0seyJmaWxlIjoic3JjL1dpblF1aWNrLkNvcmUvRGVzaWduL1NraW5DYXRhbG9nLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MTExLCJhbmNob3IiOiJwdWJsaWMgc3RyaW5nIE1lcmdlV2l0aE92ZXJyaWRlcyIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgcHVibGljIHN0cmluZyBNZXJnZVdpdGhPdmVycmlkZXMoc3RyaW5nIGJhc2VUb2tlbnNKc29uLCBKc29uT2JqZWN0IG92ZXJyaWRlcykifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQ29yZS9EZXNpZ24vU2tpbkNhdGFsb2cuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo5MSwiYW5jaG9yIjoicHVibGljIHN0cmluZyBNZXJnZSgiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgIHB1YmxpYyBzdHJpbmcgTWVyZ2Uoc3RyaW5nIGJhc2VUb2tlbnNKc29uLCBzdHJpbmc/IHNraW5JZCkifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQ29yZS9EZXNpZ24vRGVzaWduVG9rZW5zLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MzA0LCJhbmNob3IiOiJwdWJsaWMgc3RyaW5nIEhvdmVyTGFiZWwiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgIHB1YmxpYyBzdHJpbmcgSG92ZXJMYWJlbCB7IGdldDsgaW5pdDsgfSA9IFwiI0ZGRkZGRlwiOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5Db3JlL0Rlc2lnbi9EZXNpZ25Ub2tlbnMuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo4MywiYW5jaG9yIjoiaWYgKENvbG9yLkhvdmVyRmlsbEFscGhhIGlzIiwib2xkX2NvdW50IjoxLCJuZXciOiJcbiAgICAgICAgaWYgKENvbG9yLkhvdmVyRmlsbEFscGhhIGlzIDwgMCBvciA+IDEpIn0seyJmaWxlIjoidGVzdHMvV2luUXVpY2suQ29yZS5UZXN0cy9Ta2luQ2F0YWxvZ1Rlc3RzLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MTMzLCJhbmNob3IiOiJbRmFjdF0iLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgIFtGYWN0XSJ9LHsiZmlsZSI6ImRvY3Mvc3BlY3Mvc2tpbi1jb2xvci1yZWRlc2lnbi5tZCIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjY1LCJhbmNob3IiOiJSNSDpnaLmnb/moIfpopjnsbsiLCJvbGRfY291bnQiOjIsIm5ldyI6IlI1IOmdouadv+agh+mimOexu+OAjOW8uuiwg+iJsuW9k+aWh+Wtl+OAjeWkhO+8iFNlY3Rpb24g55SoIENvbG9yLkFjY2VudO+8ie+8mue7meWHuuS4pOaho+iuqeeUqOaIt+aMkVxuICAg77yI6L6+5qCH5L+d6K+B55qE5by66LCD6Imy5qCH6aKYIHZzIFRleHQg6ImyICsgU2VtaUJvbGTvvInvvIzkvp3mja7mmK/lvq7ova/mmI7mlofkuI3lu7rorq7lnKggYWNyeWxpYyDpnaLkuIrmlL7lvLrosIPoibLmloflrZfjgIJcbiAgICoq5a6a56i/77yIMjAyNi0xMS0yMyDnlKjmiLfljp/lnovnoa7orqTvvIzpu5jorqTmoaPvvIkqKu+8mkIg5qGjIOKAlOKAlCBTZWN0aW9uIOagh+mimOeUqCBUZXh0IOiJsiArIFNlbWlCb2xk77yIU2tpblBhbmVsLnhhbWzvvInvvIxBY2NlbnQg5Y+q5L+d55WZ44CM5b2T57q/L+W9k+eCuee8gOOAjeivreS5ieOAgiJ9LHsiZmlsZSI6ImRvY3Mvc3BlY3Mvc2tpbi1jb2xvci1yZWRlc2lnbi5tZCIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjU4LCJhbmNob3IiOiJSMiDljYrpgI/mmI7nmq7ogqTliqAiLCJvbGRfY291bnQiOjIsIm5ldyI6IlIyIOWNiumAj+aYjuearuiCpOWKoCAqKnNjcmltIOS/neW6leWxgioq77yIRmx1ZW50IEFjcnlsaWMg55qE55yf5a6e5p6E5oiQ77yJ77yM55m95a2X5Zyo5rWF6ImyL+e6r+eZveWjgee6uOS4iui+vuagh++8m1xuICAgKirlrprnqL/vvIgyMDI2LTExLTIzIOeUqOaIt+WOn+Wei+aMkeWumu+8iSoq77ya5LqU5qGjIEHigJNF77yIMC40NS8wLjU1IOaPkOS6ruOAgTAuNjUg5o+Q5Lqu44CBMC42NSDljovmmpfjgIEwLjcyIOWOi+aal++8ieS4reWPliBEIOahoyDigJTigJQgc2NyaW0gzrEwLjY1ICsg5oKs5YGc5Y6L5pqX77yIaG92ZXJGaWxsICMwMDAwMDAgzrEwLjE477yJ44CCIn0seyJmaWxlIjoiZG9jcy9zcGVjcy9za2luLWNvbG9yLXJlZGVzaWduLm1kIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MTE2LCJhbmNob3IiOiLov5sgRGVzaWduVG9rZW5zLlZhbGlkYXRlIOWfn++8iDAuLjHvvIkiLCJvbGRfY291bnQiOjEsIm5ldyI6Iui/myBEZXNpZ25Ub2tlbnMuVmFsaWRhdGUg5Z+f77yIMC4uMC45IOKAlOKAlCDlrp7njrDml7bmiorkuIrpmZDku44gMSDmlLbntKfliLAgMC4577yM6LaF6L+H5bCx5LiN5piv546755KD5piv6buR5p2/77ybcmV2aWV3ZXIgUDIg5YuY6K+v77yJ44CC6buY6K6k55qu6IKk6KGM5Li65LiN5Y+Y44CCIn0seyJmaWxlIjoiZG9jcy9zcGVjcy9za2luLWNvbG9yLXJlZGVzaWduLm1kIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MTQzLCJhbmNob3IiOiLpobXlhoUgc2VsZkNoZWNrKCkiLCJvbGRfY291bnQiOjEsIm5ldyI6Ii0g6aG15YaFIHNlbGZDaGVjaygp77yIb25lcnJvciArIOWFs+mUrumFjeWvueiuoeeul+WkjeaguO+8ie+8jGBub2RlIHByZXZpZXcvX3ZlcmlmeS5tanNgIOWPr+i3kee6r+mAu+i+keiHquajgOOAglxuICDms6jvvIhyZXZpZXdlciBQMi0zIOWLmOivr++8ie+8muWOn+WeiyBzZWxmQ2hlY2sg55qEIFc0L1c1IOaMiemdouadv+WQiOaIkOW6leWPo+W+hO+8jOavlOWNlea1i+WPo+W+hOS5kOingu+8m+WNlea1i+aMieWunumZhea4suafk+WIhuaUr++8iHNjcmltIOS/neW6leebmOatu+WMuuOAgeinkuagh+WPoOaJh+WMuuW6le+8ieWIpOWumiDigJTigJQg57uI5Yik5Lul5Y2V5rWL5LiO5a6e5py65Li65YeG77yM5Lik6ICF5b2T5pe255qG6L+H44CCIn1dLCJleHBlY3QiOlt7ImZpbGUiOiJkb2NzL3NwZWNzL3NraW4tY29sb3ItcmVkZXNpZ24ubWQiLCJjb250YWlucyI6IueUqOaIt+WOn+Wei+aMkeWumiJ9LHsiZmlsZSI6ImRvY3Mvc3BlY3Mvc2tpbi1jb2xvci1yZWRlc2lnbi5tZCIsImNvbnRhaW5zIjoiQiDmoaMg4oCU4oCUIFNlY3Rpb24g5qCH6aKY55SoIFRleHQg6ImyIn1dfQ==").decode("utf-8"))
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
node scripts/compile-change.mjs docs/changes/skin-color-redesign-polish.md | python - && ```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-polish.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests -c Release --nologo
```
```

## 回滚说明

执行正向补丁时已快照 docs/changes/skin-color-redesign-polish.rollback.json。回滚（还原编辑、删除新建文件）：

```bash
node scripts/compile-change.mjs docs/changes/skin-color-redesign-polish.md --rollback | python -
```

回滚脚本：

```python
import sys, os, json, base64
SLUG = "skin-color-redesign-polish"
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
