# 执行书：skin-color-redesign-panel（compile-change 编译产物）

> 由 scripts/compile-change.mjs 编译 docs 变更单生成；勿手改，改变更单后重新编译。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 309 | private void RefreshTextColors() | 19 | <NL>    private void RefreshTextColors()<NL>    {<NL>        var text = TokenResources.Brush("Color.Text");<NL>        var muted = TokenResources.Brush("Color.TextMuted");<NL>        var onAccent = TokenResources.Brush("Color.AccentText");<NL>        var family = TokenResources.Get<FontFamily>("Font.Family");<NL>        var body = TokenResources.Get<double>("Font.BodySize");<NL>        var small = TokenResources.Get<double>("Font.SmallSize");<NL><NL>        // 选中行底是 Accent → 文字必须换配对色 AccentText（spec R1）。<NL>        // 旧实现按字重统一刷 Text/TextMuted，半透明皮肤就白底白字 —— 用户截图里那行「整块纯白无文字」。<NL>        void RefreshRow(Border row, bool isCurrent)<NL>        {<NL>            foreach (var block in Descendants(row).OfType<TextBlock>())<NL>            {<NL>                block.FontFamily = family;<NL>                var isBody = block.FontWeight == FontWeights.SemiBold;<NL>                block.FontSize = isBody ? body : small;<NL>                block.Foreground = isCurrent ? onAccent : (isBody ? text : muted);<NL>            }<NL>        }<NL><NL>        foreach (var (id, row, _) in _skinRows)<NL>            RefreshRow(row, string.Equals(id, _currentSkin, StringComparison.OrdinalIgnoreCase));<NL>        foreach (var (form, row) in _formRows)<NL>            RefreshRow(row, form == _currentForm);<NL>    } |
| 2 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 212 | Foreground = TokenResources.Brush("Color.TextMuted"), | 1 | <NL>                        Foreground = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.TextMuted"), |
| 3 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 204 | Foreground = TokenResources.Brush("Color.Text"), | 1 | <NL>                        Foreground = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.Text"), |
| 4 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 167 | Foreground = TokenResources.Brush("Color.TextMuted"), | 1 | <NL>            Foreground = noteBrush, |
| 5 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 159 | Foreground = TokenResources.Brush("Color.TextMuted"), | 1 | <NL>            Foreground = noteBrush, |
| 6 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 151 | Foreground = TokenResources.Brush("Color.Text"), | 1 | <NL>            Foreground = nameBrush, |
| 7 | src/WinQuick.App/Radial/SkinPanel.xaml.cs | 147 | var text = new StackPanel | 1 | <NL>        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };<NL>        // 选中行底是 Accent → 行内文字换成 on-accent 配对色（spec R1；缺陷：半透明白底白字整行消失）。<NL>        var nameBrush = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.Text");<NL>        var noteBrush = isCurrent ? TokenResources.Brush("Color.AccentText") : TokenResources.Brush("Color.TextMuted"); |

## 补丁脚本

```python
import sys, os, json, base64, re
DATA = json.loads(base64.b64decode("eyJzbHVnIjoic2tpbi1jb2xvci1yZWRlc2lnbi1wYW5lbCIsInBhdGNoZXMiOlt7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL1JhZGlhbC9Ta2luUGFuZWwueGFtbC5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjMwOSwiYW5jaG9yIjoicHJpdmF0ZSB2b2lkIFJlZnJlc2hUZXh0Q29sb3JzKCkiLCJvbGRfY291bnQiOjE5LCJuZXciOiJcbiAgICBwcml2YXRlIHZvaWQgUmVmcmVzaFRleHRDb2xvcnMoKVxuICAgIHtcbiAgICAgICAgdmFyIHRleHQgPSBUb2tlblJlc291cmNlcy5CcnVzaChcIkNvbG9yLlRleHRcIik7XG4gICAgICAgIHZhciBtdXRlZCA9IFRva2VuUmVzb3VyY2VzLkJydXNoKFwiQ29sb3IuVGV4dE11dGVkXCIpO1xuICAgICAgICB2YXIgb25BY2NlbnQgPSBUb2tlblJlc291cmNlcy5CcnVzaChcIkNvbG9yLkFjY2VudFRleHRcIik7XG4gICAgICAgIHZhciBmYW1pbHkgPSBUb2tlblJlc291cmNlcy5HZXQ8Rm9udEZhbWlseT4oXCJGb250LkZhbWlseVwiKTtcbiAgICAgICAgdmFyIGJvZHkgPSBUb2tlblJlc291cmNlcy5HZXQ8ZG91YmxlPihcIkZvbnQuQm9keVNpemVcIik7XG4gICAgICAgIHZhciBzbWFsbCA9IFRva2VuUmVzb3VyY2VzLkdldDxkb3VibGU+KFwiRm9udC5TbWFsbFNpemVcIik7XG5cbiAgICAgICAgLy8g6YCJ5Lit6KGM5bqV5pivIEFjY2VudCDihpIg5paH5a2X5b+F6aG75o2i6YWN5a+56ImyIEFjY2VudFRleHTvvIhzcGVjIFIx77yJ44CCXG4gICAgICAgIC8vIOaXp+WunueOsOaMieWtl+mHjee7n+S4gOWItyBUZXh0L1RleHRNdXRlZO+8jOWNiumAj+aYjuearuiCpOWwseeZveW6leeZveWtlyDigJTigJQg55So5oi35oiq5Zu+6YeM6YKj6KGM44CM5pW05Z2X57qv55m95peg5paH5a2X44CN44CCXG4gICAgICAgIHZvaWQgUmVmcmVzaFJvdyhCb3JkZXIgcm93LCBib29sIGlzQ3VycmVudClcbiAgICAgICAge1xuICAgICAgICAgICAgZm9yZWFjaCAodmFyIGJsb2NrIGluIERlc2NlbmRhbnRzKHJvdykuT2ZUeXBlPFRleHRCbG9jaz4oKSlcbiAgICAgICAgICAgIHtcbiAgICAgICAgICAgICAgICBibG9jay5Gb250RmFtaWx5ID0gZmFtaWx5O1xuICAgICAgICAgICAgICAgIHZhciBpc0JvZHkgPSBibG9jay5Gb250V2VpZ2h0ID09IEZvbnRXZWlnaHRzLlNlbWlCb2xkO1xuICAgICAgICAgICAgICAgIGJsb2NrLkZvbnRTaXplID0gaXNCb2R5ID8gYm9keSA6IHNtYWxsO1xuICAgICAgICAgICAgICAgIGJsb2NrLkZvcmVncm91bmQgPSBpc0N1cnJlbnQgPyBvbkFjY2VudCA6IChpc0JvZHkgPyB0ZXh0IDogbXV0ZWQpO1xuICAgICAgICAgICAgfVxuICAgICAgICB9XG5cbiAgICAgICAgZm9yZWFjaCAodmFyIChpZCwgcm93LCBfKSBpbiBfc2tpblJvd3MpXG4gICAgICAgICAgICBSZWZyZXNoUm93KHJvdywgc3RyaW5nLkVxdWFscyhpZCwgX2N1cnJlbnRTa2luLCBTdHJpbmdDb21wYXJpc29uLk9yZGluYWxJZ25vcmVDYXNlKSk7XG4gICAgICAgIGZvcmVhY2ggKHZhciAoZm9ybSwgcm93KSBpbiBfZm9ybVJvd3MpXG4gICAgICAgICAgICBSZWZyZXNoUm93KHJvdywgZm9ybSA9PSBfY3VycmVudEZvcm0pO1xuICAgIH0ifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL1JhZGlhbC9Ta2luUGFuZWwueGFtbC5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjIxMiwiYW5jaG9yIjoiRm9yZWdyb3VuZCA9IFRva2VuUmVzb3VyY2VzLkJydXNoKFwiQ29sb3IuVGV4dE11dGVkXCIpLCIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgICAgICAgICAgICAgICAgICAgICBGb3JlZ3JvdW5kID0gaXNDdXJyZW50ID8gVG9rZW5SZXNvdXJjZXMuQnJ1c2goXCJDb2xvci5BY2NlbnRUZXh0XCIpIDogVG9rZW5SZXNvdXJjZXMuQnJ1c2goXCJDb2xvci5UZXh0TXV0ZWRcIiksIn0seyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9SYWRpYWwvU2tpblBhbmVsLnhhbWwuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjoyMDQsImFuY2hvciI6IkZvcmVncm91bmQgPSBUb2tlblJlc291cmNlcy5CcnVzaChcIkNvbG9yLlRleHRcIiksIiwib2xkX2NvdW50IjoxLCJuZXciOiJcbiAgICAgICAgICAgICAgICAgICAgICAgIEZvcmVncm91bmQgPSBpc0N1cnJlbnQgPyBUb2tlblJlc291cmNlcy5CcnVzaChcIkNvbG9yLkFjY2VudFRleHRcIikgOiBUb2tlblJlc291cmNlcy5CcnVzaChcIkNvbG9yLlRleHRcIiksIn0seyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9SYWRpYWwvU2tpblBhbmVsLnhhbWwuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjoxNjcsImFuY2hvciI6IkZvcmVncm91bmQgPSBUb2tlblJlc291cmNlcy5CcnVzaChcIkNvbG9yLlRleHRNdXRlZFwiKSwiLCJvbGRfY291bnQiOjEsIm5ldyI6IlxuICAgICAgICAgICAgRm9yZWdyb3VuZCA9IG5vdGVCcnVzaCwifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL1JhZGlhbC9Ta2luUGFuZWwueGFtbC5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjE1OSwiYW5jaG9yIjoiRm9yZWdyb3VuZCA9IFRva2VuUmVzb3VyY2VzLkJydXNoKFwiQ29sb3IuVGV4dE11dGVkXCIpLCIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgICAgICAgICBGb3JlZ3JvdW5kID0gbm90ZUJydXNoLCJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1NraW5QYW5lbC54YW1sLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MTUxLCJhbmNob3IiOiJGb3JlZ3JvdW5kID0gVG9rZW5SZXNvdXJjZXMuQnJ1c2goXCJDb2xvci5UZXh0XCIpLCIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgICAgICAgICBGb3JlZ3JvdW5kID0gbmFtZUJydXNoLCJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1NraW5QYW5lbC54YW1sLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MTQ3LCJhbmNob3IiOiJ2YXIgdGV4dCA9IG5ldyBTdGFja1BhbmVsIiwib2xkX2NvdW50IjoxLCJuZXciOiJcbiAgICAgICAgdmFyIHRleHQgPSBuZXcgU3RhY2tQYW5lbCB7IFZlcnRpY2FsQWxpZ25tZW50ID0gVmVydGljYWxBbGlnbm1lbnQuQ2VudGVyIH07XG4gICAgICAgIC8vIOmAieS4reihjOW6leaYryBBY2NlbnQg4oaSIOihjOWGheaWh+Wtl+aNouaIkCBvbi1hY2NlbnQg6YWN5a+56Imy77yIc3BlYyBSMe+8m+e8uumZt++8muWNiumAj+aYjueZveW6leeZveWtl+aVtOihjOa2iOWkse+8ieOAglxuICAgICAgICB2YXIgbmFtZUJydXNoID0gaXNDdXJyZW50ID8gVG9rZW5SZXNvdXJjZXMuQnJ1c2goXCJDb2xvci5BY2NlbnRUZXh0XCIpIDogVG9rZW5SZXNvdXJjZXMuQnJ1c2goXCJDb2xvci5UZXh0XCIpO1xuICAgICAgICB2YXIgbm90ZUJydXNoID0gaXNDdXJyZW50ID8gVG9rZW5SZXNvdXJjZXMuQnJ1c2goXCJDb2xvci5BY2NlbnRUZXh0XCIpIDogVG9rZW5SZXNvdXJjZXMuQnJ1c2goXCJDb2xvci5UZXh0TXV0ZWRcIik7In1dLCJleHBlY3QiOlt7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL1JhZGlhbC9Ta2luUGFuZWwueGFtbC5jcyIsImNvbnRhaW5zIjoidmFyIG5hbWVCcnVzaCA9IGlzQ3VycmVudCA/IFRva2VuUmVzb3VyY2VzLkJydXNoKFwiQ29sb3IuQWNjZW50VGV4dFwiKSJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvUmFkaWFsL1NraW5QYW5lbC54YW1sLmNzIiwiY29udGFpbnMiOiJSZWZyZXNoUm93KHJvdywgZm9ybSA9PSBfY3VycmVudEZvcm0pOyJ9XX0=").decode("utf-8"))
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
node scripts/compile-change.mjs docs/changes/skin-color-redesign-panel.md | python - && ```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-panel.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo
```
```

## 回滚说明

执行正向补丁时已快照 docs/changes/skin-color-redesign-panel.rollback.json。回滚（还原编辑、删除新建文件）：

```bash
node scripts/compile-change.mjs docs/changes/skin-color-redesign-panel.md --rollback | python -
```

回滚脚本：

```python
import sys, os, json, base64
SLUG = "skin-color-redesign-panel"
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
