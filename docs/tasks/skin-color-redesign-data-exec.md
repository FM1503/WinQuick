# 执行书：skin-color-redesign-data（compile-change 编译产物）

> 由 scripts/compile-change.mjs 编译 docs 变更单生成；勿手改，改变更单后重新编译。

## 变更表

| 序号 | 文件 | 行号 | 锚点 | 旧行数 | 新内容 |
| --- | --- | --- | --- | --- | --- |
| 1 | design/skins.json | 245 | "color": { | 19 | <NL>        "color": {<NL>          "dimAlpha": 0.5,<NL>          "sectorFill": "#0D140D",<NL>          "sectorFillAlpha": 1.0,<NL>          "sectorStroke": "#33FF66",<NL>          "sectorStrokeAlpha": 0.55,<NL>          "strokeWidth": 1.5,<NL>          "hoverFill": "#33FF66",<NL>          "hoverFillAlpha": 0.22,<NL>          "hoverStroke": "#33FF66",<NL>          "hoverStrokeAlpha": 1.0,<NL>          "hoverLabel": "#33FF66",<NL>          "label": "#5CFF8E",<NL>          "hintAlpha": 0.8<NL>        }, |
| 2 | design/skins.json | 200 | "color": { | 19 | <NL>        "color": {<NL>          "dimAlpha": 0.72,<NL>          "sectorFill": "#000000",<NL>          "sectorFillAlpha": 1.0,<NL>          "sectorStroke": "#FFFFFF",<NL>          "sectorStrokeAlpha": 1.0,<NL>          "strokeWidth": 2.0,<NL>          "hoverFill": "#FFFF00",<NL>          "hoverFillAlpha": 1.0,<NL>          "hoverStroke": "#FFFFFF",<NL>          "hoverStrokeAlpha": 1.0,<NL>          "hoverLabel": "#000000",<NL>          "deadZoneStrokeAlpha": 1.0,<NL>          "label": "#FFFFFF",<NL>          "hintAlpha": 0.9<NL>        }, |
| 3 | design/skins.json | 154 | "color": { | 18 | <NL>        "color": {<NL>          "dimAlpha": 0.32,<NL>          "sectorFill": "#1C1B1F",<NL>          "sectorFillAlpha": 1.0,<NL>          "sectorStroke": "#49454F",<NL>          "sectorStrokeAlpha": 0.9,<NL>          "hoverFill": "#D0BCFF",<NL>          "hoverFillAlpha": 1.0,<NL>          "hoverStroke": "#49454F",<NL>          "hoverStrokeAlpha": 1.0,<NL>          "hoverLabel": "#1C1B1F",<NL>          "label": "#E6E1E5",<NL>          "hintAlpha": 0.7<NL>        }, |
| 4 | design/skins.json | 114 | "color": { | 18 | <NL>        "color": {<NL>          "dimAlpha": 0.2,<NL>          "sectorFill": "#FFFFFF",<NL>          "sectorFillAlpha": 0.07,<NL>          "sectorScrim": "#000000",<NL>          "sectorScrimAlpha": 0.65,<NL>          "sectorStroke": "#FFFFFF",<NL>          "sectorStrokeAlpha": 0.5,<NL>          "strokeWidth": 1.4,<NL>          "strokeGradient": true,<NL>          "hoverFill": "#000000",<NL>          "hoverFillAlpha": 0.18,<NL>          "hoverStroke": "#FFFFFF",<NL>          "hoverStrokeAlpha": 0.85,<NL>          "hoverLabel": "#FFFFFF",<NL>          "label": "#FFFFFF",<NL>          "hintAlpha": 0.7<NL>        }, |
| 5 | design/skins.json | 110 | tagline | 3 | <NL>      "tagline": "玻璃拟态：白 tint + 黑 scrim 保底，慢而柔",<NL>      "cost": "低-中（scrim 多一层纯几何垫底）",<NL>      "src": "出处：Fluent Acrylic 官方配方（Microsoft Learn《Acrylic material》）—— 半透明 tint 之下必须有保底层，官方并明文「不要在 acrylic 面上放强调色文字」。分层窗口拿不到系统模糊，以纯几何黑 α0.65 垫底近似（spec R2 用户原型挑定 D 档）：白字在最坏纯白壁纸 5.8:1、悬停压暗 7.7:1。真模糊不可用，这仍是「半透明 + 保底」而不是「毛玻璃」。", |
| 6 | design/skins.json | 65 | "color": { | 22 | <NL>        "color": {<NL>          "dimAlpha": 0.55,<NL>          "sectorFill": "#060A14",<NL>          "sectorFillAlpha": 0.88,<NL>          "sectorStroke": "#00E5FF",<NL>          "sectorStrokeAlpha": 0.55,<NL>          "strokeWidth": 1.2,<NL>          "haloPx": 6,<NL>          "haloAlpha": 0.2,<NL>          "haloColor": "#00E5FF",<NL>          "hoverFill": "#00E5FF",<NL>          "hoverFillAlpha": 0.22,<NL>          "hoverStroke": "#00E5FF",<NL>          "hoverStrokeAlpha": 1.0,<NL>          "hoverLabel": "#E0F8FF",<NL>          "label": "#E0F8FF",<NL>          "hintAlpha": 0.75<NL>        }, |
| 7 | design/skins.json | 37 | "color": { | 19 | <NL>        "color": {<NL>          "dimAlpha": 0.28,<NL>          "sectorFill": "#FAF6EF",<NL>          "sectorFillAlpha": 0.97,<NL>          "sectorStroke": "#6B5D4A",<NL>          "sectorStrokeAlpha": 0.22,<NL>          "hoverFill": "#B45309",<NL>          "hoverFillAlpha": 1.0,<NL>          "hoverStroke": "#FFFFFF",<NL>          "hoverStrokeAlpha": 0.9,<NL>          "hoverLabel": "#FFFFFF",<NL>          "label": "#3F3A31",<NL>          "hint": "#3F3A31",<NL>          "hintAlpha": 0.6<NL>        } |
| 8 | design/skins.json | 14 | 新令牌（strokeWidth | 1 | <NL>    "次要表面（deadZone* / badge*）是**派生键**（spec skin-color-redesign R3）：",<NL>    "皮肤不写就按 sectorFill 合成底自动派生；确需身份特例才显式覆盖（如高对比那圈实心白描边）。",<NL>    "新令牌（strokeWidth / halo* / hoverFillAlpha / hoverLabel / childStaggerMs）" |
| 9 | design/tokens.json | 27 | "haloColor": "#000000", | 2 | <NL>    "haloColor": "#000000",<NL>    "sectorScrim": "#000000",<NL>    "sectorScrimAlpha": 0.0,<NL>    "hoverFill": "#0078D4", |

## 补丁脚本

```python
import sys, os, json, base64, re
DATA = json.loads(base64.b64decode("eyJzbHVnIjoic2tpbi1jb2xvci1yZWRlc2lnbi1kYXRhIiwicGF0Y2hlcyI6W3siZmlsZSI6ImRlc2lnbi9za2lucy5qc29uIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MjQ1LCJhbmNob3IiOiJcImNvbG9yXCI6IHsiLCJvbGRfY291bnQiOjE5LCJuZXciOiJcbiAgICAgICAgXCJjb2xvclwiOiB7XG4gICAgICAgICAgXCJkaW1BbHBoYVwiOiAwLjUsXG4gICAgICAgICAgXCJzZWN0b3JGaWxsXCI6IFwiIzBEMTQwRFwiLFxuICAgICAgICAgIFwic2VjdG9yRmlsbEFscGhhXCI6IDEuMCxcbiAgICAgICAgICBcInNlY3RvclN0cm9rZVwiOiBcIiMzM0ZGNjZcIixcbiAgICAgICAgICBcInNlY3RvclN0cm9rZUFscGhhXCI6IDAuNTUsXG4gICAgICAgICAgXCJzdHJva2VXaWR0aFwiOiAxLjUsXG4gICAgICAgICAgXCJob3ZlckZpbGxcIjogXCIjMzNGRjY2XCIsXG4gICAgICAgICAgXCJob3ZlckZpbGxBbHBoYVwiOiAwLjIyLFxuICAgICAgICAgIFwiaG92ZXJTdHJva2VcIjogXCIjMzNGRjY2XCIsXG4gICAgICAgICAgXCJob3ZlclN0cm9rZUFscGhhXCI6IDEuMCxcbiAgICAgICAgICBcImhvdmVyTGFiZWxcIjogXCIjMzNGRjY2XCIsXG4gICAgICAgICAgXCJsYWJlbFwiOiBcIiM1Q0ZGOEVcIixcbiAgICAgICAgICBcImhpbnRBbHBoYVwiOiAwLjhcbiAgICAgICAgfSwifSx7ImZpbGUiOiJkZXNpZ24vc2tpbnMuanNvbiIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjIwMCwiYW5jaG9yIjoiXCJjb2xvclwiOiB7Iiwib2xkX2NvdW50IjoxOSwibmV3IjoiXG4gICAgICAgIFwiY29sb3JcIjoge1xuICAgICAgICAgIFwiZGltQWxwaGFcIjogMC43MixcbiAgICAgICAgICBcInNlY3RvckZpbGxcIjogXCIjMDAwMDAwXCIsXG4gICAgICAgICAgXCJzZWN0b3JGaWxsQWxwaGFcIjogMS4wLFxuICAgICAgICAgIFwic2VjdG9yU3Ryb2tlXCI6IFwiI0ZGRkZGRlwiLFxuICAgICAgICAgIFwic2VjdG9yU3Ryb2tlQWxwaGFcIjogMS4wLFxuICAgICAgICAgIFwic3Ryb2tlV2lkdGhcIjogMi4wLFxuICAgICAgICAgIFwiaG92ZXJGaWxsXCI6IFwiI0ZGRkYwMFwiLFxuICAgICAgICAgIFwiaG92ZXJGaWxsQWxwaGFcIjogMS4wLFxuICAgICAgICAgIFwiaG92ZXJTdHJva2VcIjogXCIjRkZGRkZGXCIsXG4gICAgICAgICAgXCJob3ZlclN0cm9rZUFscGhhXCI6IDEuMCxcbiAgICAgICAgICBcImhvdmVyTGFiZWxcIjogXCIjMDAwMDAwXCIsXG4gICAgICAgICAgXCJkZWFkWm9uZVN0cm9rZUFscGhhXCI6IDEuMCxcbiAgICAgICAgICBcImxhYmVsXCI6IFwiI0ZGRkZGRlwiLFxuICAgICAgICAgIFwiaGludEFscGhhXCI6IDAuOVxuICAgICAgICB9LCJ9LHsiZmlsZSI6ImRlc2lnbi9za2lucy5qc29uIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MTU0LCJhbmNob3IiOiJcImNvbG9yXCI6IHsiLCJvbGRfY291bnQiOjE4LCJuZXciOiJcbiAgICAgICAgXCJjb2xvclwiOiB7XG4gICAgICAgICAgXCJkaW1BbHBoYVwiOiAwLjMyLFxuICAgICAgICAgIFwic2VjdG9yRmlsbFwiOiBcIiMxQzFCMUZcIixcbiAgICAgICAgICBcInNlY3RvckZpbGxBbHBoYVwiOiAxLjAsXG4gICAgICAgICAgXCJzZWN0b3JTdHJva2VcIjogXCIjNDk0NTRGXCIsXG4gICAgICAgICAgXCJzZWN0b3JTdHJva2VBbHBoYVwiOiAwLjksXG4gICAgICAgICAgXCJob3ZlckZpbGxcIjogXCIjRDBCQ0ZGXCIsXG4gICAgICAgICAgXCJob3ZlckZpbGxBbHBoYVwiOiAxLjAsXG4gICAgICAgICAgXCJob3ZlclN0cm9rZVwiOiBcIiM0OTQ1NEZcIixcbiAgICAgICAgICBcImhvdmVyU3Ryb2tlQWxwaGFcIjogMS4wLFxuICAgICAgICAgIFwiaG92ZXJMYWJlbFwiOiBcIiMxQzFCMUZcIixcbiAgICAgICAgICBcImxhYmVsXCI6IFwiI0U2RTFFNVwiLFxuICAgICAgICAgIFwiaGludEFscGhhXCI6IDAuN1xuICAgICAgICB9LCJ9LHsiZmlsZSI6ImRlc2lnbi9za2lucy5qc29uIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MTE0LCJhbmNob3IiOiJcImNvbG9yXCI6IHsiLCJvbGRfY291bnQiOjE4LCJuZXciOiJcbiAgICAgICAgXCJjb2xvclwiOiB7XG4gICAgICAgICAgXCJkaW1BbHBoYVwiOiAwLjIsXG4gICAgICAgICAgXCJzZWN0b3JGaWxsXCI6IFwiI0ZGRkZGRlwiLFxuICAgICAgICAgIFwic2VjdG9yRmlsbEFscGhhXCI6IDAuMDcsXG4gICAgICAgICAgXCJzZWN0b3JTY3JpbVwiOiBcIiMwMDAwMDBcIixcbiAgICAgICAgICBcInNlY3RvclNjcmltQWxwaGFcIjogMC42NSxcbiAgICAgICAgICBcInNlY3RvclN0cm9rZVwiOiBcIiNGRkZGRkZcIixcbiAgICAgICAgICBcInNlY3RvclN0cm9rZUFscGhhXCI6IDAuNSxcbiAgICAgICAgICBcInN0cm9rZVdpZHRoXCI6IDEuNCxcbiAgICAgICAgICBcInN0cm9rZUdyYWRpZW50XCI6IHRydWUsXG4gICAgICAgICAgXCJob3ZlckZpbGxcIjogXCIjMDAwMDAwXCIsXG4gICAgICAgICAgXCJob3ZlckZpbGxBbHBoYVwiOiAwLjE4LFxuICAgICAgICAgIFwiaG92ZXJTdHJva2VcIjogXCIjRkZGRkZGXCIsXG4gICAgICAgICAgXCJob3ZlclN0cm9rZUFscGhhXCI6IDAuODUsXG4gICAgICAgICAgXCJob3ZlckxhYmVsXCI6IFwiI0ZGRkZGRlwiLFxuICAgICAgICAgIFwibGFiZWxcIjogXCIjRkZGRkZGXCIsXG4gICAgICAgICAgXCJoaW50QWxwaGFcIjogMC43XG4gICAgICAgIH0sIn0seyJmaWxlIjoiZGVzaWduL3NraW5zLmpzb24iLCJuZXdmaWxlIjpmYWxzZSwibGluZW5vIjoxMTAsImFuY2hvciI6InRhZ2xpbmUiLCJvbGRfY291bnQiOjMsIm5ldyI6IlxuICAgICAgXCJ0YWdsaW5lXCI6IFwi546755KD5ouf5oCB77ya55m9IHRpbnQgKyDpu5Egc2NyaW0g5L+d5bqV77yM5oWi6ICM5p+UXCIsXG4gICAgICBcImNvc3RcIjogXCLkvY4t5Lit77yIc2NyaW0g5aSa5LiA5bGC57qv5Yeg5L2V5Z6r5bqV77yJXCIsXG4gICAgICBcInNyY1wiOiBcIuWHuuWkhO+8mkZsdWVudCBBY3J5bGljIOWumOaWuemFjeaWue+8iE1pY3Jvc29mdCBMZWFybuOAikFjcnlsaWMgbWF0ZXJpYWzjgIvvvInigJTigJQg5Y2K6YCP5piOIHRpbnQg5LmL5LiL5b+F6aG75pyJ5L+d5bqV5bGC77yM5a6Y5pa55bm25piO5paH44CM5LiN6KaB5ZyoIGFjcnlsaWMg6Z2i5LiK5pS+5by66LCD6Imy5paH5a2X44CN44CC5YiG5bGC56qX5Y+j5ou/5LiN5Yiw57O757uf5qih57OK77yM5Lul57qv5Yeg5L2V6buRIM6xMC42NSDlnqvlupXov5HkvLzvvIhzcGVjIFIyIOeUqOaIt+WOn+Wei+aMkeWumiBEIOaho++8ie+8mueZveWtl+WcqOacgOWdj+e6r+eZveWjgee6uCA1Ljg6MeOAgeaCrOWBnOWOi+aalyA3Ljc6MeOAguecn+aooeeziuS4jeWPr+eUqO+8jOi/meS7jeaYr+OAjOWNiumAj+aYjiArIOS/neW6leOAjeiAjOS4jeaYr+OAjOavm+eOu+eSg+OAjeOAglwiLCJ9LHsiZmlsZSI6ImRlc2lnbi9za2lucy5qc29uIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6NjUsImFuY2hvciI6IlwiY29sb3JcIjogeyIsIm9sZF9jb3VudCI6MjIsIm5ldyI6IlxuICAgICAgICBcImNvbG9yXCI6IHtcbiAgICAgICAgICBcImRpbUFscGhhXCI6IDAuNTUsXG4gICAgICAgICAgXCJzZWN0b3JGaWxsXCI6IFwiIzA2MEExNFwiLFxuICAgICAgICAgIFwic2VjdG9yRmlsbEFscGhhXCI6IDAuODgsXG4gICAgICAgICAgXCJzZWN0b3JTdHJva2VcIjogXCIjMDBFNUZGXCIsXG4gICAgICAgICAgXCJzZWN0b3JTdHJva2VBbHBoYVwiOiAwLjU1LFxuICAgICAgICAgIFwic3Ryb2tlV2lkdGhcIjogMS4yLFxuICAgICAgICAgIFwiaGFsb1B4XCI6IDYsXG4gICAgICAgICAgXCJoYWxvQWxwaGFcIjogMC4yLFxuICAgICAgICAgIFwiaGFsb0NvbG9yXCI6IFwiIzAwRTVGRlwiLFxuICAgICAgICAgIFwiaG92ZXJGaWxsXCI6IFwiIzAwRTVGRlwiLFxuICAgICAgICAgIFwiaG92ZXJGaWxsQWxwaGFcIjogMC4yMixcbiAgICAgICAgICBcImhvdmVyU3Ryb2tlXCI6IFwiIzAwRTVGRlwiLFxuICAgICAgICAgIFwiaG92ZXJTdHJva2VBbHBoYVwiOiAxLjAsXG4gICAgICAgICAgXCJob3ZlckxhYmVsXCI6IFwiI0UwRjhGRlwiLFxuICAgICAgICAgIFwibGFiZWxcIjogXCIjRTBGOEZGXCIsXG4gICAgICAgICAgXCJoaW50QWxwaGFcIjogMC43NVxuICAgICAgICB9LCJ9LHsiZmlsZSI6ImRlc2lnbi9za2lucy5qc29uIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MzcsImFuY2hvciI6IlwiY29sb3JcIjogeyIsIm9sZF9jb3VudCI6MTksIm5ldyI6IlxuICAgICAgICBcImNvbG9yXCI6IHtcbiAgICAgICAgICBcImRpbUFscGhhXCI6IDAuMjgsXG4gICAgICAgICAgXCJzZWN0b3JGaWxsXCI6IFwiI0ZBRjZFRlwiLFxuICAgICAgICAgIFwic2VjdG9yRmlsbEFscGhhXCI6IDAuOTcsXG4gICAgICAgICAgXCJzZWN0b3JTdHJva2VcIjogXCIjNkI1RDRBXCIsXG4gICAgICAgICAgXCJzZWN0b3JTdHJva2VBbHBoYVwiOiAwLjIyLFxuICAgICAgICAgIFwiaG92ZXJGaWxsXCI6IFwiI0I0NTMwOVwiLFxuICAgICAgICAgIFwiaG92ZXJGaWxsQWxwaGFcIjogMS4wLFxuICAgICAgICAgIFwiaG92ZXJTdHJva2VcIjogXCIjRkZGRkZGXCIsXG4gICAgICAgICAgXCJob3ZlclN0cm9rZUFscGhhXCI6IDAuOSxcbiAgICAgICAgICBcImhvdmVyTGFiZWxcIjogXCIjRkZGRkZGXCIsXG4gICAgICAgICAgXCJsYWJlbFwiOiBcIiMzRjNBMzFcIixcbiAgICAgICAgICBcImhpbnRcIjogXCIjM0YzQTMxXCIsXG4gICAgICAgICAgXCJoaW50QWxwaGFcIjogMC42XG4gICAgICAgIH0ifSx7ImZpbGUiOiJkZXNpZ24vc2tpbnMuanNvbiIsIm5ld2ZpbGUiOmZhbHNlLCJsaW5lbm8iOjE0LCJhbmNob3IiOiLmlrDku6TniYzvvIhzdHJva2VXaWR0aCIsIm9sZF9jb3VudCI6MSwibmV3IjoiXG4gICAgXCLmrKHopoHooajpnaLvvIhkZWFkWm9uZSogLyBiYWRnZSrvvInmmK8qKua0vueUn+mUrioq77yIc3BlYyBza2luLWNvbG9yLXJlZGVzaWduIFIz77yJ77yaXCIsXG4gICAgXCLnmq7ogqTkuI3lhpnlsLHmjIkgc2VjdG9yRmlsbCDlkIjmiJDlupXoh6rliqjmtL7nlJ/vvJvnoa7pnIDouqvku73nibnkvovmiY3mmL7lvI/opobnm5bvvIjlpoLpq5jlr7nmr5TpgqPlnIjlrp7lv4Pnmb3mj4/ovrnvvInjgIJcIixcbiAgICBcIuaWsOS7pOeJjO+8iHN0cm9rZVdpZHRoIC8gaGFsbyogLyBob3ZlckZpbGxBbHBoYSAvIGhvdmVyTGFiZWwgLyBjaGlsZFN0YWdnZXJNc++8iVwiIn0seyJmaWxlIjoiZGVzaWduL3Rva2Vucy5qc29uIiwibmV3ZmlsZSI6ZmFsc2UsImxpbmVubyI6MjcsImFuY2hvciI6IlwiaGFsb0NvbG9yXCI6IFwiIzAwMDAwMFwiLCIsIm9sZF9jb3VudCI6MiwibmV3IjoiXG4gICAgXCJoYWxvQ29sb3JcIjogXCIjMDAwMDAwXCIsXG4gICAgXCJzZWN0b3JTY3JpbVwiOiBcIiMwMDAwMDBcIixcbiAgICBcInNlY3RvclNjcmltQWxwaGFcIjogMC4wLFxuICAgIFwiaG92ZXJGaWxsXCI6IFwiIzAwNzhENFwiLCJ9XSwiZXhwZWN0IjpbeyJmaWxlIjoiZGVzaWduL3NraW5zLmpzb24iLCJjb250YWlucyI6Ilwic2VjdG9yU2NyaW1BbHBoYVwiOiAwLjY1In0seyJmaWxlIjoiZGVzaWduL3Rva2Vucy5qc29uIiwiY29udGFpbnMiOiJcInNlY3RvclNjcmltXCI6IFwiIzAwMDAwMFwiIn1dfQ==").decode("utf-8"))
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
node scripts/compile-change.mjs docs/changes/skin-color-redesign-data.md | python - && ```bash
node "C:/Users/22572/.zcode/cli/plugins/cache/dev-sop/dev-sop/1.9.0/scripts/compile-change.mjs" docs/changes/skin-color-redesign-data.md | python - && timeout 115 dotnet build src/WinQuick.App/WinQuick.App.csproj -c Release --nologo && timeout 115 dotnet test tests/WinQuick.Core.Tests --filter "FullyQualifiedName~SkinContrast" --nologo
```
```

## 回滚说明

执行正向补丁时已快照 docs/changes/skin-color-redesign-data.rollback.json。回滚（还原编辑、删除新建文件）：

```bash
node scripts/compile-change.mjs docs/changes/skin-color-redesign-data.md --rollback | python -
```

回滚脚本：

```python
import sys, os, json, base64
SLUG = "skin-color-redesign-data"
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
