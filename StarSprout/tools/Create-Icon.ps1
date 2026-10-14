$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskBitmap = [System.Drawing.Bitmap]::new(128, 128)
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
$taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$taskGraphics.Clear([System.Drawing.Color]::Transparent)
$taskBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#39A56F'))
$taskGraphics.FillEllipse($taskBrush, 4, 4, 120, 120)
$taskStem = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 7)
$taskStem.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$taskStem.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$taskGraphics.DrawBezier($taskStem,64,96,66,72,52,67,67,42)
$taskLeaf = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
$taskShape = [System.Drawing.Drawing2D.GraphicsPath]::new()
$taskShape.AddBezier(62,73,31,70,32,44,35,41)
$taskShape.AddBezier(35,41,61,44,67,53,62,73)
$taskGraphics.FillPath($taskLeaf,$taskShape)
$taskShape.Reset()
$taskShape.AddBezier(64,63,66,37,87,28,94,32)
$taskShape.AddBezier(94,32,98,55,85,64,64,63)
$taskGraphics.FillPath($taskLeaf,$taskShape)
$taskPng = [System.IO.MemoryStream]::new()
$taskBitmap.Save($taskPng,[System.Drawing.Imaging.ImageFormat]::Png)
$taskIconPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\starsprout.ico'))
$taskWriter = [System.IO.BinaryWriter]::new([System.IO.File]::Create($taskIconPath))
try {
    $taskWriter.Write([uint16]0); $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]1)
    $taskWriter.Write([byte]128); $taskWriter.Write([byte]128); $taskWriter.Write([byte]0); $taskWriter.Write([byte]0)
    $taskWriter.Write([uint16]1); $taskWriter.Write([uint16]32)
    $taskWriter.Write([uint32]$taskPng.Length); $taskWriter.Write([uint32]22)
    $taskWriter.Write($taskPng.ToArray())
}
finally { $taskWriter.Dispose(); $taskPng.Dispose(); $taskShape.Dispose(); $taskLeaf.Dispose(); $taskStem.Dispose(); $taskBrush.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose() }
