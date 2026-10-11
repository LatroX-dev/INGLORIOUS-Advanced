# Repackages the generated PNG as a Windows multi-resolution icon.
# The original artwork is preserved; only standard icon-size rasterization is performed.
#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase
$taskRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourcePath = Join-Path $taskRoot 'src/InGlorious.Core/Assets/inglorious-mark.png'
$targetPath = Join-Path $taskRoot 'src/InGlorious.Core/Assets/inglorious.ico'
$frames = foreach ($size in @(16,24,32,48,64,128,256)) {
    $bitmap = [Windows.Media.Imaging.BitmapImage]::new()
    $bitmap.BeginInit()
    $bitmap.CacheOption = [Windows.Media.Imaging.BitmapCacheOption]::OnLoad
    $bitmap.UriSource = [Uri]::new($sourcePath)
    $bitmap.DecodePixelWidth = $size
    $bitmap.EndInit()
    $bitmap.Freeze()
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = [IO.MemoryStream]::new()
    $encoder.Save($memory)
    [pscustomobject]@{Size=$size;Bytes=$memory.ToArray()}
    $memory.Dispose()
}
$stream = [IO.File]::Create($targetPath)
$writer = [IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if($frame.Size -eq 256) {0} else {$frame.Size}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) {$writer.Write([byte[]]$frame.Bytes)}
    $writer.Flush()
} finally {$writer.Dispose(); $stream.Dispose()}
Write-Output "Windows icon created with $($frames.Count) sizes: $targetPath"
