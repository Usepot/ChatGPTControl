Add-Type -AssemblyName System.Drawing
$src = 'desktop_state\desktop_now.png'
$dst = 'desktop_state\desktop_now_preview.jpg'
$img = [System.Drawing.Image]::FromFile((Resolve-Path $src))
$w = 700
$h = [int]($img.Height * ($w / $img.Width))
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.DrawImage($img, 0, 0, $w, $h)
$codec = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object { $_.MimeType -eq 'image/jpeg' }
$params = New-Object System.Drawing.Imaging.EncoderParameters 1
$params.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter ([System.Drawing.Imaging.Encoder]::Quality), 45L
$bmp.Save((Join-Path (Get-Location) $dst), $codec, $params)
$g.Dispose(); $bmp.Dispose(); $img.Dispose()
Write-Output $dst
