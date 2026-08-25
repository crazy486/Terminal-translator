$ErrorActionPreference = 'Stop'

$asmName = New-Object System.Reflection.AssemblyName('ConProbeDynamic4')
$asm = [System.AppDomain]::CurrentDomain.DefineDynamicAssembly($asmName, [System.Reflection.Emit.AssemblyBuilderAccess]::Run)
$mod = $asm.DefineDynamicModule('ConProbeMod4')

function New-StructType([string]$name, [hashtable]$fields) {
    $t = $mod.DefineType($name, [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed -bor [System.Reflection.TypeAttributes]::SequentialLayout, [System.ValueType])
    foreach ($k in $fields.Keys) {
        $t.DefineField($k, $fields[$k], [System.Reflection.FieldAttributes]::Public) | Out-Null
    }
    return $t.CreateType()
}

$coordT = New-StructType 'COORD4' @{ X = [int16]; Y = [int16] }
$rectT = New-StructType 'SMALL_RECT4' @{ Left = [int16]; Top = [int16]; Right = [int16]; Bottom = [int16] }
$infoT = New-StructType 'CONSOLE_SCREEN_BUFFER_INFO4' @{ dwSize = $coordT; dwCursorPosition = $coordT; wAttributes = [int16]; srWindow = $rectT; dwMaximumWindowSize = $coordT }

$nativeType = $mod.DefineType('Native4', [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed)
$mb = [System.Reflection.MethodAttributes]::Public -bor [System.Reflection.MethodAttributes]::Static -bor [System.Reflection.MethodAttributes]::PinvokeImpl
$cc = [System.Reflection.CallingConventions]::Standard
$wc = [System.Runtime.InteropServices.CallingConvention]::Winapi
$ps = [System.Reflection.MethodImplAttributes]::PreserveSig

$createFile = $nativeType.DefinePInvokeMethod('CreateFileW', 'kernel32.dll', $mb, $cc, [intptr], [type[]]@([string], [uint32], [uint32], [intptr], [uint32], [uint32], [intptr]), $wc, [System.Runtime.InteropServices.CharSet]::Unicode)
$createFile.SetImplementationFlags($createFile.GetMethodImplementationFlags() -bor $ps) | Out-Null

$getInfo = $nativeType.DefinePInvokeMethod('GetConsoleScreenBufferInfo', 'kernel32.dll', $mb, $cc, [bool], [type[]]@([intptr], $infoT.MakeByRefType()), $wc, [System.Runtime.InteropServices.CharSet]::Auto)
$getInfo.SetImplementationFlags($getInfo.GetMethodImplementationFlags() -bor $ps) | Out-Null

$readChars = $nativeType.DefinePInvokeMethod('ReadConsoleOutputCharacterW', 'kernel32.dll', $mb, $cc, [bool], [type[]]@([intptr], [char[]], [uint32], [uint32], [uint32].MakeByRefType()), $wc, [System.Runtime.InteropServices.CharSet]::Unicode)
$readChars.SetImplementationFlags($readChars.GetMethodImplementationFlags() -bor $ps) | Out-Null

$nativeT = $nativeType.CreateType()
$createFileM = $nativeT.GetMethod('CreateFileW')
$getInfoM = $nativeT.GetMethod('GetConsoleScreenBufferInfo')
$readCharsM = $nativeT.GetMethod('ReadConsoleOutputCharacterW')

function Get-FieldValue($obj, $fieldName) {
    return $obj.GetType().GetField($fieldName).GetValue($obj)
}

$GENERIC_READ = [uint32]2147483648
$GENERIC_WRITE = [uint32]1073741824
$FILE_SHARE_READ = [uint32]1
$FILE_SHARE_WRITE = [uint32]2
$OPEN_EXISTING = [uint32]3

$h = $createFileM.Invoke($null, @('CONOUT$', ($GENERIC_READ -bor $GENERIC_WRITE), ($FILE_SHARE_READ -bor $FILE_SHARE_WRITE), [intptr]::Zero, $OPEN_EXISTING, [uint32]0, [intptr]::Zero))
if ($null -eq $h) {
    Write-Output 'ERROR: CreateFile returned null'
    exit 1
}
if ($h -eq [intptr]::Zero -or $h -eq [intptr](-1)) {
    Write-Output "ERROR: cannot open CONOUT$ LastWin32Error=$([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    exit 1
}
"CONOUT handle: 0x$($h.ToInt64().ToString('X'))"

$infoArgs = @($h, [System.Activator]::CreateInstance($infoT))
$infoOk = $getInfoM.Invoke($null, $infoArgs)
"GetConsoleScreenBufferInfo returned: $infoOk  LastWin32Error=$([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
$info = $infoArgs[1]

$size = Get-FieldValue $info 'dwSize'
$cursor = Get-FieldValue $info 'dwCursorPosition'
$window = Get-FieldValue $info 'srWindow'
$maxWin = Get-FieldValue $info 'dwMaximumWindowSize'

"BufferSize: $($size.X) x $($size.Y)"
"Cursor: $($cursor.X), $($cursor.Y)"
"Window: ($($window.Left), $($window.Top)) - ($($window.Right), $($window.Bottom))"
"MaxWindowSize: $($maxWin.X) x $($maxWin.Y)"

$rows = [int]$size.Y
$cols = [int]$size.X
$sb = New-Object System.Text.StringBuilder
for ($y = 0; $y -lt $rows; $y++) {
    $chars = New-Object char[] $cols
    $readArgs = @($h, $chars, [uint32]$cols, [uint32]$y, [uint32]0)
    $ok = $readCharsM.Invoke($null, $readArgs)
    if (-not $ok) {
        $sb.AppendLine("<<row $y unreadable>>") | Out-Null
        continue
    }
    $read = [int]$readArgs[4]
    $text = -join $chars[0..($read - 1)]
    $sb.AppendLine($text) | Out-Null
}
'===== CONSOLE TEXT ====='
$sb.ToString()
