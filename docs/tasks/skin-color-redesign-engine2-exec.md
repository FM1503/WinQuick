# 执行书：skin-color-redesign-engine2（compile-change 编译产物）

> 由 scripts/compile-change.mjs 编译 docs 变更单生成；勿手改，改变更单后重新编译。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | src/WinQuick.Core/Design/ColorMath.cs | 102 | ]; | 1 | ]; |
| 2 | src/WinQuick.Core/Design/SkinCatalog.cs | 111 | public string MergeWithOverrides | 1 | public string MergeWithOverrides(string baseTokensJson, JsonObject overrides) |
| 3 | src/WinQuick.Core/Design/SkinCatalog.cs | 91 | public string Merge( | 1 | public string Merge(string baseTokensJson, string? skinId) |
| 4 | src/WinQuick.Core/Design/DesignTokens.cs | 304 | public string HoverLabel | 1 | public string HoverLabel { get; init; } = "#FFFFFF"; |
| 5 | src/WinQuick.Core/Design/DesignTokens.cs | 83 | if (Color.HoverFillAlpha | 1 | if (Color.HoverFillAlpha is < 0 or > 1) |
| 6 | src/WinQuick.App/Design/SystemSkin.cs | 65 | 悬停字色 | 11 | /// 悬停字色按强调色自动取黑/白 —— Win11 官方机制（强调色「自动为明暗两模式优化对比度」）。<NL>    /// 规则升级（spec skin-color-redesign R1）：旧的「线性亮度 > 0.5」硬阈值换成 ColorMath.BestTextOn<NL>    /// —— 取 WCAG 对比度更大的一侧。常见强调色两规则同结果，中间调（#7C4DFF 类）新规则站到可读的一侧；<NL>    /// 「暗态 + #0078D4 + 深字差 0.29 被校验拦下」那一类配错从此被恒达标取代。<NL>    /// </summary><NL>    private static string AccentLabelText(string accentHex) =><NL>        WinQuick.Core.Design.ColorMath.BestTextOn(accentHex);<NL>} |
| 7 | src/WinQuick.App/Design/SystemSkin.cs | 53 | ["deadZoneFill"] | 7 | ["label"] = "#1B1B1B",<NL>                ["hint"] = "#1B1B1B",<NL>                ["hintAlpha"] = 0.6, |
| 8 | src/WinQuick.App/Design/TokenResources.cs | 112 | Set("Color.Accent", TokenBrushes.Brush(panel.Accent)); | 1 | Set("Color.Accent", TokenBrushes.Brush(panel.Accent));<NL>        // 选中行/高亮态的 on-accent 配对字色（spec R1；Material on-X）。<NL>        Set("Color.AccentText", TokenBrushes.Brush(panel.AccentText)); |
| 9 | src/WinQuick.App/Design/TokenResources.cs | 48 | return new PanelPalette( | 11 | // 强调色兜底（spec R1 连带修正）：D 档玻璃的 HoverFill 是压暗黑（悬停语义），<NL>        // 与面板底不足 3:1 时它不再适合当「强调文字/指示线」，翻到可读一侧 —— 选中行成白底黑字，与原型挑定一致。<NL>        var accent = ColorMath.Contrast(c.HoverFill, surface) >= 3.0<NL>            ? c.HoverFill : ColorMath.BestTextOn(surface);<NL>        return new PanelPalette(<NL>            SurfaceSolid: surface,<NL>            Surface: surface,<NL>            FieldBackground: field,<NL>            Outline: light ? "#000000" : "#FFFFFF",<NL>            HoverSurface: light ? "#000000" : "#FFFFFF",<NL>            // 强调色取原色不透明：HoverFill 的 α 是「轮盘悬停高亮的半透明感」参数，<NL>            // 面板强调是实色语义 —— 按 α 合成会把 #33FF66/0.22 压成与底趋同的暗绿（SkinContrast 抓到）。<NL>            Accent: accent,<NL>            // on-accent 配对字色（spec R1）：选中行以 Accent 为底时文字用它 ——<NL>            // 半透明皮肤 HoverFill 与 Label 同为 #FFFFFF，复用 Color.Text 就是白底白字整行消失（用户截图实锤）。<NL>            AccentText: ColorMath.BestTextOn(accent),<NL>            Danger: light ? "#C42B1C" : "#F08A8A",<NL>            Success: light ? "#1B7F3A" : "#8CC88C"); |
| 10 | src/WinQuick.App/Design/TokenResources.cs | 26 | 面板派生键的结果 | 4 | /// <summary>面板派生键的结果（纯数据，供 Install 与单测共用）。</summary><NL>    public sealed record PanelPalette(<NL>        string SurfaceSolid, string Surface, string FieldBackground,<NL>        string Outline, string HoverSurface, string Accent, string Danger, string Success,<NL>        string AccentText); |

## 补丁脚本

```python
import sys, os, json, base64, re
DATA = json.loads(base64.b64decode("eyJzbHVnIjoic2tpbi1jb2xvci1yZWRlc2lnbi1lbmdpbmUyIiwicGF0Y2hlcyI6W3siZmlsZSI6InNyYy9XaW5RdWljay5Db3JlL0Rlc2lnbi9Db2xvck1hdGguY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjoxMDIsImFuY2hvciI6Il07Iiwib2xkX2NvdW50IjoxLCJuZXciOiJdOyJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5Db3JlL0Rlc2lnbi9Ta2luQ2F0YWxvZy5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjExMSwiYW5jaG9yIjoicHVibGljIHN0cmluZyBNZXJnZVdpdGhPdmVycmlkZXMiLCJvbGRfY291bnQiOjEsIm5ldyI6InB1YmxpYyBzdHJpbmcgTWVyZ2VXaXRoT3ZlcnJpZGVzKHN0cmluZyBiYXNlVG9rZW5zSnNvbiwgSnNvbk9iamVjdCBvdmVycmlkZXMpIn0seyJmaWxlIjoic3JjL1dpblF1aWNrLkNvcmUvRGVzaWduL1NraW5DYXRhbG9nLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6OTEsImFuY2hvciI6InB1YmxpYyBzdHJpbmcgTWVyZ2UoIiwib2xkX2NvdW50IjoxLCJuZXciOiJwdWJsaWMgc3RyaW5nIE1lcmdlKHN0cmluZyBiYXNlVG9rZW5zSnNvbiwgc3RyaW5nPyBza2luSWQpIn0seyJmaWxlIjoic3JjL1dpblF1aWNrLkNvcmUvRGVzaWduL0Rlc2lnblRva2Vucy5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjMwNCwiYW5jaG9yIjoicHVibGljIHN0cmluZyBIb3ZlckxhYmVsIiwib2xkX2NvdW50IjoxLCJuZXciOiJwdWJsaWMgc3RyaW5nIEhvdmVyTGFiZWwgeyBnZXQ7IGluaXQ7IH0gPSBcIiNGRkZGRkZcIjsifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQ29yZS9EZXNpZ24vRGVzaWduVG9rZW5zLmNzIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6ODMsImFuY2hvciI6ImlmIChDb2xvci5Ib3ZlckZpbGxBbHBoYSIsIm9sZF9jb3VudCI6MSwibmV3IjoiaWYgKENvbG9yLkhvdmVyRmlsbEFscGhhIGlzIDwgMCBvciA+IDEpIn0seyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9EZXNpZ24vU3lzdGVtU2tpbi5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjY1LCJhbmNob3IiOiLmgqzlgZzlrZfoibIiLCJvbGRfY291bnQiOjExLCJuZXciOiIvLy8g5oKs5YGc5a2X6Imy5oyJ5by66LCD6Imy6Ieq5Yqo5Y+W6buRL+eZvSDigJTigJQgV2luMTEg5a6Y5pa55py65Yi277yI5by66LCD6Imy44CM6Ieq5Yqo5Li65piO5pqX5Lik5qih5byP5LyY5YyW5a+55q+U5bqm44CN77yJ44CCXG4gICAgLy8vIOinhOWImeWNh+e6p++8iHNwZWMgc2tpbi1jb2xvci1yZWRlc2lnbiBSMe+8ie+8muaXp+eahOOAjOe6v+aAp+S6ruW6piA+IDAuNeOAjeehrOmYiOWAvOaNouaIkCBDb2xvck1hdGguQmVzdFRleHRPblxuICAgIC8vLyDigJTigJQg5Y+WIFdDQUcg5a+55q+U5bqm5pu05aSn55qE5LiA5L6n44CC5bi46KeB5by66LCD6Imy5Lik6KeE5YiZ5ZCM57uT5p6c77yM5Lit6Ze06LCD77yIIzdDNERGRiDnsbvvvInmlrDop4TliJnnq5nliLDlj6/or7vnmoTkuIDkvqfvvJtcbiAgICAvLy8g44CM5pqX5oCBICsgIzAwNzhENCArIOa3seWtl+W3riAwLjI5IOiiq+agoemqjOaLpuS4i+OAjemCo+S4gOexu+mFjemUmeS7juatpOiiq+aBkui+vuagh+WPluS7o+OAglxuICAgIC8vLyA8L3N1bW1hcnk+XG4gICAgcHJpdmF0ZSBzdGF0aWMgc3RyaW5nIEFjY2VudExhYmVsVGV4dChzdHJpbmcgYWNjZW50SGV4KSA9PlxuICAgICAgICBXaW5RdWljay5Db3JlLkRlc2lnbi5Db2xvck1hdGguQmVzdFRleHRPbihhY2NlbnRIZXgpO1xufSJ9LHsiZmlsZSI6InNyYy9XaW5RdWljay5BcHAvRGVzaWduL1N5c3RlbVNraW4uY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo1MywiYW5jaG9yIjoiW1wiZGVhZFpvbmVGaWxsXCJdIiwib2xkX2NvdW50Ijo3LCJuZXciOiJbXCJsYWJlbFwiXSA9IFwiIzFCMUIxQlwiLFxuICAgICAgICAgICAgICAgIFtcImhpbnRcIl0gPSBcIiMxQjFCMUJcIixcbiAgICAgICAgICAgICAgICBbXCJoaW50QWxwaGFcIl0gPSAwLjYsIn0seyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9EZXNpZ24vVG9rZW5SZXNvdXJjZXMuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjoxMTIsImFuY2hvciI6IlNldChcIkNvbG9yLkFjY2VudFwiLCBUb2tlbkJydXNoZXMuQnJ1c2gocGFuZWwuQWNjZW50KSk7Iiwib2xkX2NvdW50IjoxLCJuZXciOiJTZXQoXCJDb2xvci5BY2NlbnRcIiwgVG9rZW5CcnVzaGVzLkJydXNoKHBhbmVsLkFjY2VudCkpO1xuICAgICAgICAvLyDpgInkuK3ooYwv6auY5Lqu5oCB55qEIG9uLWFjY2VudCDphY3lr7nlrZfoibLvvIhzcGVjIFIx77ybTWF0ZXJpYWwgb24tWO+8ieOAglxuICAgICAgICBTZXQoXCJDb2xvci5BY2NlbnRUZXh0XCIsIFRva2VuQnJ1c2hlcy5CcnVzaChwYW5lbC5BY2NlbnRUZXh0KSk7In0seyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9EZXNpZ24vVG9rZW5SZXNvdXJjZXMuY3MiLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjo0OCwiYW5jaG9yIjoicmV0dXJuIG5ldyBQYW5lbFBhbGV0dGUoIiwib2xkX2NvdW50IjoxMSwibmV3IjoiLy8g5by66LCD6Imy5YWc5bqV77yIc3BlYyBSMSDov57luKbkv67mraPvvInvvJpEIOaho+eOu+eSg+eahCBIb3ZlckZpbGwg5piv5Y6L5pqX6buR77yI5oKs5YGc6K+t5LmJ77yJ77yMXG4gICAgICAgIC8vIOS4jumdouadv+W6leS4jei2syAzOjEg5pe25a6D5LiN5YaN6YCC5ZCI5b2T44CM5by66LCD5paH5a2XL+aMh+ekuue6v+OAje+8jOe/u+WIsOWPr+ivu+S4gOS+pyDigJTigJQg6YCJ5Lit6KGM5oiQ55m95bqV6buR5a2X77yM5LiO5Y6f5Z6L5oyR5a6a5LiA6Ie044CCXG4gICAgICAgIHZhciBhY2NlbnQgPSBDb2xvck1hdGguQ29udHJhc3QoYy5Ib3ZlckZpbGwsIHN1cmZhY2UpID49IDMuMFxuICAgICAgICAgICAgPyBjLkhvdmVyRmlsbCA6IENvbG9yTWF0aC5CZXN0VGV4dE9uKHN1cmZhY2UpO1xuICAgICAgICByZXR1cm4gbmV3IFBhbmVsUGFsZXR0ZShcbiAgICAgICAgICAgIFN1cmZhY2VTb2xpZDogc3VyZmFjZSxcbiAgICAgICAgICAgIFN1cmZhY2U6IHN1cmZhY2UsXG4gICAgICAgICAgICBGaWVsZEJhY2tncm91bmQ6IGZpZWxkLFxuICAgICAgICAgICAgT3V0bGluZTogbGlnaHQgPyBcIiMwMDAwMDBcIiA6IFwiI0ZGRkZGRlwiLFxuICAgICAgICAgICAgSG92ZXJTdXJmYWNlOiBsaWdodCA/IFwiIzAwMDAwMFwiIDogXCIjRkZGRkZGXCIsXG4gICAgICAgICAgICAvLyDlvLrosIPoibLlj5bljp/oibLkuI3pgI/mmI7vvJpIb3ZlckZpbGwg55qEIM6xIOaYr+OAjOi9ruebmOaCrOWBnOmrmOS6rueahOWNiumAj+aYjuaEn+OAjeWPguaVsO+8jFxuICAgICAgICAgICAgLy8g6Z2i5p2/5by66LCD5piv5a6e6Imy6K+t5LmJIOKAlOKAlCDmjIkgzrEg5ZCI5oiQ5Lya5oqKICMzM0ZGNjYvMC4yMiDljovmiJDkuI7lupXotovlkIznmoTmmpfnu7/vvIhTa2luQ29udHJhc3Qg5oqT5Yiw77yJ44CCXG4gICAgICAgICAgICBBY2NlbnQ6IGFjY2VudCxcbiAgICAgICAgICAgIC8vIG9uLWFjY2VudCDphY3lr7nlrZfoibLvvIhzcGVjIFIx77yJ77ya6YCJ5Lit6KGM5LulIEFjY2VudCDkuLrlupXml7bmloflrZfnlKjlroMg4oCU4oCUXG4gICAgICAgICAgICAvLyDljYrpgI/mmI7nmq7ogqQgSG92ZXJGaWxsIOS4jiBMYWJlbCDlkIzkuLogI0ZGRkZGRu+8jOWkjeeUqCBDb2xvci5UZXh0IOWwseaYr+eZveW6leeZveWtl+aVtOihjOa2iOWkse+8iOeUqOaIt+aIquWbvuWunumUpO+8ieOAglxuICAgICAgICAgICAgQWNjZW50VGV4dDogQ29sb3JNYXRoLkJlc3RUZXh0T24oYWNjZW50KSxcbiAgICAgICAgICAgIERhbmdlcjogbGlnaHQgPyBcIiNDNDJCMUNcIiA6IFwiI0YwOEE4QVwiLFxuICAgICAgICAgICAgU3VjY2VzczogbGlnaHQgPyBcIiMxQjdGM0FcIiA6IFwiIzhDQzg4Q1wiKTsifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL0Rlc2lnbi9Ub2tlblJlc291cmNlcy5jcyIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjI2LCJhbmNob3IiOiLpnaLmnb/mtL7nlJ/plK7nmoTnu5PmnpwiLCJvbGRfY291bnQiOjQsIm5ldyI6Ii8vLyA8c3VtbWFyeT7pnaLmnb/mtL7nlJ/plK7nmoTnu5PmnpzvvIjnuq/mlbDmja7vvIzkvpsgSW5zdGFsbCDkuI7ljZXmtYvlhbHnlKjvvInjgII8L3N1bW1hcnk+XG4gICAgcHVibGljIHNlYWxlZCByZWNvcmQgUGFuZWxQYWxldHRlKFxuICAgICAgICBzdHJpbmcgU3VyZmFjZVNvbGlkLCBzdHJpbmcgU3VyZmFjZSwgc3RyaW5nIEZpZWxkQmFja2dyb3VuZCxcbiAgICAgICAgc3RyaW5nIE91dGxpbmUsIHN0cmluZyBIb3ZlclN1cmZhY2UsIHN0cmluZyBBY2NlbnQsIHN0cmluZyBEYW5nZXIsIHN0cmluZyBTdWNjZXNzLFxuICAgICAgICBzdHJpbmcgQWNjZW50VGV4dCk7In1dLCJleHBlY3QiOlt7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL0Rlc2lnbi9TeXN0ZW1Ta2luLmNzIiwiY29udGFpbnMiOiJDb2xvck1hdGguQmVzdFRleHRPbihhY2NlbnRIZXgpIn0seyJmaWxlIjoic3JjL1dpblF1aWNrLkFwcC9EZXNpZ24vVG9rZW5SZXNvdXJjZXMuY3MiLCJjb250YWlucyI6IkFjY2VudFRleHQ6IENvbG9yTWF0aC5CZXN0VGV4dE9uKGFjY2VudCkifSx7ImZpbGUiOiJzcmMvV2luUXVpY2suQXBwL0Rlc2lnbi9Ub2tlblJlc291cmNlcy5jcyIsImNvbnRhaW5zIjoiQ29sb3IuQWNjZW50VGV4dCJ9XX0=").decode("utf-8"))
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
node scripts/compile-change.mjs docs/changes/skin-color-redesign-engine2.md | python - && ```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-engine2.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" --nologo
```
```

## 回滚说明

执行正向补丁时已快照 docs/changes/skin-color-redesign-engine2.rollback.json。回滚（还原编辑、删除新建文件）：

```bash
node scripts/compile-change.mjs docs/changes/skin-color-redesign-engine2.md --rollback | python -
```

回滚脚本：

```python
import sys, os, json, base64
SLUG = "skin-color-redesign-engine2"
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
