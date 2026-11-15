param($dir, $out)
Add-Type -AssemblyName System.Drawing
$cells = @(
  @("old\r3-03-self-after-quit.png", "S1, round-3 build: A still borderless at the app's spot"),
  @("new\r3-03-self-after-quit.png", "S1, this build: A has its original frame back"),
  @("old\r3-05-min-reopened.png", "S6, round-3 build: B reopens at the app's centred spot"),
  @("new\r3-05-min-reopened.png", "S6, this build: B reopens where it was")
)
$w = 960; $h = 540; $pad = 44
$bmp = New-Object System.Drawing.Bitmap (2 * $w + 20), (2 * ($h + $pad) + 10)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$g.Clear([System.Drawing.Color]::FromArgb(20,20,22))
$f = New-Object System.Drawing.Font "Segoe UI", 15, ([System.Drawing.FontStyle]::Bold)
for ($i = 0; $i -lt 4; $i++) {
  $x = ($i % 2) * ($w + 20); $y = [math]::Floor($i / 2) * ($h + $pad + 10)
  $img = [System.Drawing.Image]::FromFile((Join-Path $dir $cells[$i][0]))
  $g.DrawString($cells[$i][1], $f, [System.Drawing.Brushes]::White, $x + 4, $y + 8)
  $g.DrawImage($img, (New-Object System.Drawing.Rectangle $x, ($y + $pad), $w, $h))
  $img.Dispose()
}
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
