param([string]$Out = (Join-Path $PSScriptRoot '..\local\screen.png'), [double]$Scale = 0.5)

# Captures the primary monitor (physical pixels) so test-copy windows can be checked without a person looking.
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -Namespace Native -Name Dpi -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();'
[Native.Dpi]::SetProcessDPIAware() | Out-Null

$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$full = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($full)
$g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
$small = New-Object System.Drawing.Bitmap $full, ([int]($b.Width * $Scale)), ([int]($b.Height * $Scale))
$small.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $full.Dispose(); $small.Dispose()
Write-Host "$Out ($($b.Width)x$($b.Height) at $Scale)"
