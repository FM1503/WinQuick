Add-Type -AssemblyName System.Drawing

$path = 'C:\Users\22572\.zcode\cli\image-cache\sess_fb131d20-5fcc-472b-9ebd-9cc573665849\image-b47bff776dad8da8247ebed2bc2276ad.png'
$img = [System.Drawing.Bitmap]::FromFile($path)
$w = $img.Width
$h = $img.Height
Write-Output "尺寸: $w x $h"
Write-Output ''

# 先看颜色分布，确认"白"到底是哪个值、背景是什么
$counts = @{}
for ($y = 0; $y -lt $h; $y += 2) {
  for ($x = 0; $x -lt $w; $x += 2) {
    $p = $img.GetPixel($x, $y)
    $key = '{0:X2}{1:X2}{2:X2}' -f $p.R, $p.G, $p.B
    if ($counts.ContainsKey($key)) { $counts[$key]++ } else { $counts[$key] = 1 }
  }
}
Write-Output '=== 出现最多的 12 种颜色 ==='
$counts.GetEnumerator() | Sort-Object -Property Value -Descending | Select-Object -First 12 | ForEach-Object {
  Write-Output ("  #{0}  {1} 次" -f $_.Key, $_.Value)
}
Write-Output ''

# 字符画：每格 6x6 像素，纵向按 2:1 压扁以符合字符比例
$c = 6
Write-Output "=== 字符画（每格 ${c}x${c} 像素；# = 接近纯白，. 很深，- 很浅）==="
$rows = [math]::Floor($h / $c)
$cols = [math]::Floor($w / $c)
for ($ry = 0; $ry -lt $rows; $ry += 2) {
  $line = ''
  for ($rx = 0; $rx -lt $cols; $rx++) {
    $sum = 0.0; $n = 0; $white = 0
    for ($y = $ry * $c; $y -lt [math]::Min(($ry + 2) * $c, $h); $y++) {
      for ($x = $rx * $c; $x -lt [math]::Min(($rx + 1) * $c, $w); $x++) {
        $p = $img.GetPixel($x, $y)
        $sum += ($p.R + $p.G + $p.B) / 3.0; $n++
        if ($p.R -ge 235 -and $p.G -ge 235 -and $p.B -ge 235) { $white++ }
      }
    }
    if ($n -eq 0) { $line += ' '; continue }
    $avg = $sum / $n
    $wr = $white / $n
    $line += if ($wr -gt 0.35) { '#' }
      elseif ($avg -lt 40) { '.' }
      elseif ($avg -lt 90) { ':' }
      elseif ($avg -lt 150) { '+' }
      elseif ($avg -lt 215) { '=' }
      else { '-' }
  }
  Write-Output ("{0,4} {1}" -f ($ry * $c), $line)
}

$img.Dispose()
