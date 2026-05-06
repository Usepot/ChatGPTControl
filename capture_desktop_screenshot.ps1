Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bitmap = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)

try {
    $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bitmap.Size)
    $path = Join-Path (Get-Location) 'desktop_screenshot_2026-05-05.png'
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output $path
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}
