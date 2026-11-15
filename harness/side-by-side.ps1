param($a, $b, $out)
Add-Type -AssemblyName System.Drawing
$ia = [System.Drawing.Image]::FromFile($a); $ib = [System.Drawing.Image]::FromFile($b)
$src = New-Object System.Drawing.Rectangle 1000, 380, 560, 700
$bmp = New-Object System.Drawing.Bitmap 1140, 760
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::FromArgb(28,28,30))
$f = New-Object System.Drawing.Font "Segoe UI", 16, ([System.Drawing.FontStyle]::Bold)
$g.DrawString("Before the fix (6a1d44f): client 400x562", $f, [System.Drawing.Brushes]::White, 10, 10)
$g.DrawString("After the fix: client 400x581", $f, [System.Drawing.Brushes]::White, 580, 10)
$g.DrawImage($ia, (New-Object System.Drawing.Rectangle 0, 50, 560, 700), $src, [System.Drawing.GraphicsUnit]::Pixel)
$g.DrawImage($ib, (New-Object System.Drawing.Rectangle 580, 50, 560, 700), $src, [System.Drawing.GraphicsUnit]::Pixel)
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
