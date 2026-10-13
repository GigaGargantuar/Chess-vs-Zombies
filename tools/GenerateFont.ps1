Add-Type -AssemblyName System.Drawing
$outputPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../ChessVsZombies/Assets'))
[IO.Directory]::CreateDirectory($outputPath) | Out-Null
$bitmap = New-Object Drawing.Bitmap 1024,512
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$graphics.Clear([Drawing.Color]::Transparent)
$graphics.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$font = New-Object Drawing.Font 'Segoe UI',26,([Drawing.FontStyle]::Regular),([Drawing.GraphicsUnit]::Pixel)
$format = [Drawing.StringFormat]::GenericTypographic.Clone()
$format.FormatFlags = $format.FormatFlags -bor [Drawing.StringFormatFlags]::MeasureTrailingSpaces
$glyphs = @()
for ($code = 32; $code -le 126; $code++) {
  $index = $code-32
  $x = ($index % 16)*64
  $y = [Math]::Floor($index/16)*64
  $character = [string][char]$code
  $width = $graphics.MeasureString($character,$font,1000,$format).Width
  $graphics.DrawString($character,$font,[Drawing.Brushes]::White,[single]($x+4),[single]($y+2),$format)
  $glyphs += @{ Code=$code; X=$x; Y=$y; Width=[Math]::Ceiling($width)+8; Advance=$width }
}
$bitmap.Save((Join-Path $outputPath 'ui-font.png'),[Drawing.Imaging.ImageFormat]::Png)
ConvertTo-Json -InputObject $glyphs | Set-Content (Join-Path $outputPath 'ui-font.json') -Encoding UTF8
$font.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
