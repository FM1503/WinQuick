# 对渲染出的轮盘 PNG 做数值分析，把图像问题变成可读文本。
#
# 存在理由：驱动本项目的模型**不支持图像输入**，图会被直接丢弃，
# 所以「渲染出来看一眼」这条回环对它不成立。本脚本承担几何与配色的自检，
# 审美部分仍然需要人眼看。
#
# 设计原则：**不依赖对布局的假设**。最初版本把「扇区边界在 i*45°」写死在采样逻辑里，
# 几何约定一改（0° 从边界变成扇区中心）就得出错误结论 —— 那是在验证假设而不是验证现实。
# 现在改为直接扫描：
#   - 沿固定半径扫一圈，报出所有颜色连续区间 → 缝隙与扇区自然显现
#   - 沿固定角度扫半径，报出颜色跳变 → 各环的半径自然显现
#   - 找出强调色的角度区间 → 悬停高亮的扇区
# 无论几何怎么改，输出都是对现实的描述。
#
# 用法: analyze-render.ps1 -Path <png> [-CenterX ..] [-CenterY ..] [-ScanRadius ..]

param(
    [Parameter(Mandatory = $true)][string]$Path,
    [int]$CenterX = -1,
    [int]$CenterY = -1,
    [int]$ScanRadius = -1,
    [int]$IconRadius = -1,
    [int]$SectorCount = 8,
    # 径向剖面的方向。子弧只画在父扇区那一侧，所以验证子弧时必须把剖面转到它的方向上，
    # 否则沿 90° 剖下去什么都测不到、看不出问题。
    [int]$ProfileAngle = 90
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $Path)) { throw "找不到文件: $Path" }

$img = [System.Drawing.Bitmap]::FromFile((Resolve-Path $Path).Path)
if ($CenterX -lt 0) { $CenterX = [int]($img.Width / 2) }
if ($CenterY -lt 0) { $CenterY = [int]($img.Height / 2) }
if ($ScanRadius -lt 0) { $ScanRadius = [int]($img.Width * 0.17) }
# 图标所在的半径。默认按 tokens.json 的比例推算：56 + (200-56)*0.32 ≈ 102
if ($IconRadius -lt 0) { $IconRadius = [int]($img.Width * 0.138) }

Write-Output "文件      : $Path"
Write-Output "尺寸      : $($img.Width) x $($img.Height)"
Write-Output "圆心      : ($CenterX, $CenterY)"
Write-Output "扫描半径  : $ScanRadius"
Write-Output "剖面方向  : $ProfileAngle 度"

function Get-PixelAt([int]$x, [int]$y) {
    if ($x -lt 0 -or $y -lt 0 -or $x -ge $img.Width -or $y -ge $img.Height) { return $null }
    return $img.GetPixel($x, $y)
}

function Format-Pixel($p) {
    if ($null -eq $p) { return '(out of bounds)' }
    return ('#{0:X2}{1:X2}{2:X2}{3:X2}' -f $p.A, $p.R, $p.G, $p.B)
}

# 角度约定与 Core 一致：0° = 正上，顺时针。屏幕坐标 y 向下。
function SampleAt([double]$deg, [double]$radius) {
    $rad = $deg * [math]::PI / 180.0
    $x = [int][math]::Round($CenterX + $radius * [math]::Sin($rad))
    $y = [int][math]::Round($CenterY - $radius * [math]::Cos($rad))
    return Get-PixelAt $x $y
}

function Color-Key($p) {
    if ($null -eq $p) { return '(oob)' }
    return '#{0:X2}{1:X2}{2:X2}{3:X2}' -f $p.A, $p.R, $p.G, $p.B
}

# ---------------------------------------------------------------- 角向扫描
Write-Output ""
Write-Output "=== 角向扫描 (r=$ScanRadius, 1 度步长，颜色连续区间) ==="
Write-Output ""

$runs = New-Object System.Collections.ArrayList
$prevKey = $null
$runStart = 0
$runRep = $null

for ($deg = 0; $deg -lt 360; $deg++) {
    $p = SampleAt $deg $ScanRadius
    $key = Color-Key $p

    if ($null -eq $prevKey) {
        $prevKey = $key
        $runStart = 0
        $runRep = $p
        continue
    }
    if ($key -ne $prevKey) {
        [void]$runs.Add([pscustomobject]@{
            Start = $runStart; End = $deg - 1; Key = $prevKey
            R = $runRep.R; G = $runRep.G; B = $runRep.B; A = $runRep.A
        })
        $prevKey = $key
        $runStart = $deg
        $runRep = $p
    }
}
[void]$runs.Add([pscustomobject]@{
    Start = $runStart; End = 359; Key = $prevKey
    R = $runRep.R; G = $runRep.G; B = $runRep.B; A = $runRep.A
})

# 只保留足够长的连续区间，滤掉文字笔画造成的碎片
$minRun = 4
$significant = @($runs | Where-Object { ($_.End - $_.Start + 1) -ge $minRun })

Write-Output ("  {0,-16} {1,-8} {2,-14} {3}" -f "角度区间", "跨度", "颜色", "A")
foreach ($run in $significant) {
    $span = $run.End - $run.Start + 1
    Write-Output ("  {0,-16} {1,-8} {2,-14} {3}" -f `
        ("$($run.Start)-$($run.End)"), "$span", ('#{0:X2}{1:X2}{2:X2}' -f $run.R, $run.G, $run.B), $run.A)
}

# 扇区填充色 = 出现角度最多的那个颜色
$dominant = $significant | Group-Object Key | Sort-Object {
    ($_.Group | Measure-Object -Property End -Sum).Sum
} -Descending | Select-Object -First 1

$sectorKey = $dominant.Name
Write-Output ""
Write-Output "  占比最大的颜色（推断为扇区填充）: $sectorKey  共 $((($dominant.Group | Measure-Object -Property End -Sum).Sum)) 度"

# ---------------------------------------------------------------- 缝隙
# 缝隙的判定不能靠"颜色不等于扇区色" —— 强调色、文字像素都不等于扇区色。
# 用 alpha 判定：背景色是叠加在透明底上的，alpha 明显低于 255。
# 另外缝隙只有 2 度左右，会被上面的 minRun 过滤掉，所以这里从原始 runs 里找。
Write-Output ""
Write-Output "=== 缝隙（alpha < 200 的区间，不受最小长度限制）==="
$gapRuns = @($runs | Where-Object { $_.A -lt 200 })
if ($gapRuns.Count -eq 0) {
    Write-Output "  未检测到缝隙 —— 扇区连成一片，说明缝隙或描边没画出来。"
} else {
    Write-Output ("  {0,-14} {1,-6} {2,-12} {3}" -f "角度区间", "宽度", "颜色", "A")
    foreach ($gap in $gapRuns) {
        $mid = [math]::Round(($gap.Start + $gap.End) / 2.0, 1)
        Write-Output ("  {0,-14} {1,-6} {2,-12} {3}" -f `
            ("$($gap.Start)-$($gap.End)"), ($gap.End - $gap.Start + 1), `
            ('#{0:X2}{1:X2}{2:X2}' -f $gap.R, $gap.G, $gap.B), $gap.A)
    }
    $mids = ($gapRuns | ForEach-Object { [math]::Round(($_.Start + $_.End) / 2.0, 1) }) -join ', '
    Write-Output ""
    Write-Output "  缝隙中点: $mids"
    Write-Output "  （缝隙可能因扫描步长被切成几段，看中点即可：n 个扇区时应在 360/n 的奇数倍处）"
}

# ---------------------------------------------------------------- 悬停
Write-Output ""
Write-Output "=== 悬停高亮（强调色 #0078D4）==="
$accent = @($significant | Where-Object { $_.Key -match '#FF0078D4' })
if ($accent.Count -gt 0) {
    foreach ($run in $accent) {
        $mid = [math]::Round(($run.Start + $run.End) / 2.0, 1)
        Write-Output ("  强调色区间 {0}-{1}，中点 {2}°，跨度 {3}°" -f `
            $run.Start, $run.End, $mid, ($run.End - $run.Start + 1))
    }
} else {
    Write-Output "  未检测到强调色。default 状态正常；hover 状态下出现说明高亮失效。"
}

# ---------------------------------------------------------------- 图标是否真的渲染
# 判据用饱和度：shell 图标是彩色的，占位图是灰度圆 + 文字，纯扇区填充则是无彩深灰。
# 这样能区分「真取到图标」「退化成占位」「什么都没画」三种情况 ——
# 而这三者在灰度上几乎无法区分，靠肉眼看小图也很容易漏掉。
Write-Output ""
Write-Output "=== 图标渲染核对（按扇区中心角采样图标区域）==="
Write-Output ""

function Test-AnyColorful([double]$deg, [int]$radius, [int]$box) {
    $maxSat = 0
    for ($dy = -$box; $dy -le $box; $dy++) {
        for ($dx = -$box; $dx -le $box; $dx++) {
            $rad = $deg * [math]::PI / 180.0
            $cx = [int][math]::Round($CenterX + $radius * [math]::Sin($rad)) + $dx
            $cy = [int][math]::Round($CenterY - $radius * [math]::Cos($rad)) + $dy
            $p = Get-PixelAt $cx $cy
            if ($null -eq $p -or $p.A -lt 32) { continue }
            $mx = [math]::Max($p.R, [math]::Max($p.G, $p.B))
            $mn = [math]::Min($p.R, [math]::Min($p.G, $p.B))
            if ($mx -gt 0) {
                $sat = [int](100 * ($mx - $mn) / $mx)
                if ($sat -gt $maxSat) { $maxSat = $sat }
            }
        }
    }
    return $maxSat
}

$colorful = 0
$grayOnly = 0
for ($i = 0; $i -lt $SectorCount; $i++) {
    $centerDeg = $i * (360.0 / $SectorCount)
    $sat = Test-AnyColorful $centerDeg $IconRadius 18
    # 阈值 12%：足以把彩色图标和灰度占位/纯扇区分开，又不会把浅色图标判成灰色。
    $verdict = if ($sat -ge 12) { "彩色（真图标）"; $colorful++ } else { "无彩色（占位或无内容）"; $grayOnly++ }
    Write-Output ("  扇区 {0}  中心 {1,5}°  最大饱和度 {2,3}%  -> {3}" -f $i, ("{0:0.#}" -f $centerDeg), $sat, $verdict)
}
Write-Output ""
Write-Output ("  彩色扇区 {0} / {1}，无彩色 {2}" -f $colorful, $SectorCount, $grayOnly)
Write-Output "  应用启动时会打印「成功取到图标的项 N/M」，两个数字应当一致。"

# ---------------------------------------------------------------- 径向剖面
Write-Output ""
Write-Output "=== 径向剖面 (沿 $ProfileAngle 度方向，圆心向外，颜色跳变处) ==="
Write-Output "  提示：验证子弧时不要把 -ProfileAngle 设成子弧的**中心角** ——"
Write-Output "        子项为偶数个时那里正好是两个子项之间的缝隙，沿它剖下去会看到一片透明，"
Write-Output "        容易被误判成「子弧没画出来」。要取某个**子项**的中心角。"
Write-Output ""
Write-Output ""

$maxRadius = [int]([math]::Min($img.Width, $img.Height) / 2)
$prevKey = $null
$prevStart = 0
$prevRep = $null

for ($r = 0; $r -le $maxRadius; $r++) {
    $p = SampleAt $ProfileAngle $r
    $key = Color-Key $p
    if ($null -eq $prevKey) { $prevKey = $key; $prevStart = 0; $prevRep = $p; continue }
    if ($key -ne $prevKey) {
        if (($r - $prevStart) -ge 2) {
            Write-Output ("  r={0,-4}..{1,-4}  {2}  A={3}" -f $prevStart, ($r - 1), `
                ('#{0:X2}{1:X2}{2:X2}' -f $prevRep.R, $prevRep.G, $prevRep.B), $prevRep.A)
        }
        $prevKey = $key
        $prevStart = $r
        $prevRep = $p
    }
}
Write-Output ("  r={0,-4}..{1,-4}  {2}  A={3}" -f $prevStart, $maxRadius, `
    ('#{0:X2}{1:X2}{2:X2}' -f $prevRep.R, $prevRep.G, $prevRep.B), $prevRep.A)

# ---------------------------------------------------------------- 透明度
Write-Output ""
Write-Output "=== 外缘抗锯齿与整体透明度 ==="

$outermostOpaque = 0
for ($r = $maxRadius; $r -ge 0; $r--) {
    $p = SampleAt $ProfileAngle $r
    if ($null -ne $p -and $p.A -gt 8) { $outermostOpaque = $r; break }
}
Write-Output "  内容外边界 ($ProfileAngle 度方向，A>8): r=$outermostOpaque"

$partial = 0
$total = 0
$minR = [math]::Max(0, $outermostOpaque - 4)
for ($deg = 0; $deg -lt 360; $deg += 2) {
    for ($r = $minR; $r -le $outermostOpaque + 2; $r++) {
        $p = SampleAt $deg $r
        if ($null -ne $p) {
            $total++
            if ($p.A -gt 0 -and $p.A -lt 255) { $partial++ }
        }
    }
}
if ($total -gt 0) {
    $pct = [math]::Round(100.0 * $partial / $total, 1)
    Write-Output ("  外边界邻域采样 {0} 点，半透明 {1} 点 ({2}%)" -f $total, $partial, $pct)
    if ($partial -gt 0) {
        Write-Output "  >>> 存在抗锯齿过渡，边缘不是硬切 —— 逐像素 alpha 生效。"
    } else {
        Write-Output "  >>> 没有半透明像素 —— 边缘硬切，逐像素 alpha 未生效。"
    }
}

Write-Output ("  窗口左上角像素: " + (Format-Pixel (Get-PixelAt 2 2)) + "  (应为 A=00 全透明)")

$img.Dispose()
