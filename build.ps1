# Сборка Pomodoro.exe. Нужен только сам Windows: компилятор берётся из .NET Framework.
#   powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $here 'src'
$out  = Join-Path $here 'Pomodoro.exe'
$ico  = Join-Path $src  'icon.ico'

$sources = @('Program.cs', 'Alarm.cs', 'AssemblyInfo.cs')

# ── 1. Иконка ──────────────────────────────────────────────────────────────────
Add-Type -AssemblyName System.Drawing

function New-IconLayer {
    param([int]$size)
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad  = [single]($size * 0.02)
    $side = [single]($size - 2 * $pad)
    $disc = New-Object System.Drawing.RectangleF -ArgumentList $pad, $pad, $side, $side
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList `
        $disc,
        ([System.Drawing.Color]::FromArgb(255, 255, 122, 92)),
        ([System.Drawing.Color]::FromArgb(255, 209, 42, 42)),
        ([single]55)
    $g.FillEllipse($brush, $disc)

    # верхний блик
    $glossRect = New-Object System.Drawing.RectangleF -ArgumentList `
        ([single]($size * 0.12)), ([single]($size * 0.06)),
        ([single]($size * 0.62)), ([single]($size * 0.42))
    $gloss = New-Object System.Drawing.Drawing2D.LinearGradientBrush -ArgumentList `
        $glossRect,
        ([System.Drawing.Color]::FromArgb(110, 255, 255, 255)),
        ([System.Drawing.Color]::FromArgb(0, 255, 255, 255)),
        ([single]90)
    $g.FillEllipse($gloss, $glossRect)

    # кольцо таймера
    $inset = [single]($size * 0.28)
    $rside = [single]($size - 2 * $inset)
    $ring  = New-Object System.Drawing.RectangleF -ArgumentList $inset, $inset, $rside, $rside
    $pen = New-Object System.Drawing.Pen -ArgumentList `
        ([System.Drawing.Color]::FromArgb(245, 255, 255, 255)), ([single]($size * 0.11))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap   = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawArc($pen, $ring, -90, 275)

    $pen.Dispose(); $gloss.Dispose(); $brush.Dispose(); $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

$sizes  = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$layers = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) { [byte[]]$png = New-IconLayer $s; $layers.Add($png) }

$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim)
    $bw.Write([byte]0);    $bw.Write([byte]0)
    $bw.Write([uint16]1);  $bw.Write([uint16]32)
    $bw.Write([uint32]$layers[$i].Length)
    $bw.Write([uint32]$offset)
    $offset += $layers[$i].Length
}
foreach ($l in $layers) { $bw.Write($l) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($ico, $ms.ToArray())
$bw.Dispose()
Write-Host ("Иконка: {0} ({1} байт, {2} размеров)" -f $ico, (Get-Item $ico).Length, $sizes.Count)

# ── 2. Кодировка исходников: csc и XamlReader ждут UTF-8 с BOM ─────────────────
# Переписываем только файлы без BOM, чтобы не трогать время изменения зря.
$bomEnc = New-Object System.Text.UTF8Encoding($true)
foreach ($f in ($sources + 'ui.xaml')) {
    $p = Join-Path $src $f
    $head = [System.IO.File]::ReadAllBytes($p) | Select-Object -First 3
    if ($head.Count -lt 3 -or $head[0] -ne 0xEF -or $head[1] -ne 0xBB -or $head[2] -ne 0xBF) {
        [System.IO.File]::WriteAllText($p, [System.IO.File]::ReadAllText($p), $bomEnc)
        Write-Host "  BOM добавлен: $f"
    }
}

# ── 3. Компиляция ──────────────────────────────────────────────────────────────
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xaml, System.Drawing

$needed = 'System', 'System.Core', 'System.Xml', 'System.Xaml',
          'WindowsBase', 'PresentationCore', 'PresentationFramework'
$refs = foreach ($n in $needed) {
    $a = [AppDomain]::CurrentDomain.GetAssemblies() |
         Where-Object { $_.GetName().Name -eq $n -and $_.Location } | Select-Object -First 1
    if (-not $a) { throw "Не найдена сборка $n" }
    "/r:$($a.Location)"
}

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe не найден — нужен .NET Framework 4.x' }

if (Test-Path $out) { Remove-Item $out -Force }

$cscArgs = @(
    '/nologo', '/noconfig', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:4',
    "/out:$out",
    "/win32icon:$ico",
    "/resource:$(Join-Path $src 'ui.xaml'),ui.xaml"
) + $refs + @($sources | ForEach-Object { Join-Path $src $_ })

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc завершился с кодом $LASTEXITCODE" }

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
Write-Host ("Готово: {0} ({1} КБ)" -f $out, $size) -ForegroundColor Green
