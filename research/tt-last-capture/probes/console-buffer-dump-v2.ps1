$ErrorActionPreference = 'Stop'

# Build an in-memory P/Invoke type for console buffer APIs.
$asmName = New-Object System.Reflection.AssemblyName('ConProbeDynamic')
$asm = [System.AppDomain]::CurrentDomain.DefineDynamicAssembly($asmName, [System.Reflection.Emit.AssemblyBuilderAccess]::Run)
$mod = $asm.DefineDynamicModule('ConProbeMod')

# COORD struct
$coordType = $mod.DefineType('COORD', [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed -bor [System.Reflection.TypeAttributes]::SequentialLayout, [System.ValueType])
$coordType.DefineField('X', [int16], [System.Reflection.FieldAttributes]::Public) | Out-Null
$coordType.DefineField('Y', [int16], [System.Reflection.FieldAttributes]::Public) | Out-Null
$coordT = $coordType.CreateType()

# SMALL_RECT struct
$rectType = $mod.DefineType('SMALL_RECT', [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed -bor [System.Reflection.TypeAttributes]::SequentialLayout, [System.ValueType])
$rectType.DefineField('Left', [int16], [System.Reflection.FieldAttributes]::Public) | Out-Null
$rectType.DefineField('Top', [int16], [System.Reflection.FieldAttributes]::Public) | Out-Null
$rectType.DefineField('Right', [int16], [System.Reflection.FieldAttributes]::Public) | Out-Null
$rectType.DefineField('Bottom', [int16], [System.Reflection.FieldAttributes]::Public) | Out-Null
$rectT = $rectType.CreateType()

# CONSOLE_SCREEN_BUFFER_INFO struct
$infoType = $mod.DefineType('CONSOLE_SCREEN_BUFFER_INFO', [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed -bor [System.Reflection.TypeAttributes]::SequentialLayout, [System.ValueType])
$infoType.DefineField('dwSize', $coordT, [System.Reflection.FieldAttributes]::Public) | Out-Null
$infoType.DefineField('dwCursorPosition', $coordT, [System.Reflection.FieldAttributes]::Public) | Out-Null
$infoType.DefineField('wAttributes', [int16], [System.Reflection.FieldAttributes]::Public) | Out-Null
$infoType.DefineField('srWindow', $rectT, [System.Reflection.FieldAttributes]::Public) | Out-Null
$infoType.DefineField('dwMaximumWindowSize', $coordT, [System.Reflection.FieldAttributes]::Public) | Out-Null
$infoT = $infoType.CreateType()

# Native type with P/Invoke methods
$nativeType = $mod.DefineType('Native', [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed)
$getStdHandle = $nativeType.DefinePInvokeMethod('GetStdHandle', 'kernel32.dll', [System.Reflection.MethodAttributes]::Public -bor [System.Reflection.MethodAttributes]::Static -bor [System.Reflection.MethodAttributes]::PinvokeImpl, [System.Reflection.CallingConventions]::Standard, [intptr], [type[]]@([int]), [System.Runtime.InteropServices.CallingConvention]::Winapi, [System.Runtime.InteropServices.CharSet]::Auto)
$getStdHandle.SetImplementationFlags($getStdHandle.GetMethodImplementationFlags() -bor [System.Reflection.MethodImplAttributes]::PreserveSig) | Out-Null

$getInfo = $nativeType.DefinePInvokeMethod('GetConsoleScreenBufferInfo', 'kernel32.dll', [System.Reflection.MethodAttributes]::Public -bor [System.Reflection.MethodAttributes]::Static -bor [System.Reflection.MethodAttributes]::PinvokeImpl, [System.Reflection.CallingConventions]::Standard, [bool], [type[]]@([intptr], $infoT.MakeByRefType()), [System.Runtime.InteropServices.CallingConvention]::Winapi, [System.Runtime.InteropServices.CharSet]::Auto)
$getInfo.SetImplementationFlags($getInfo.GetMethodImplementationFlags() -bor [System.Reflection.MethodImplAttributes]::PreserveSig) | Out-Null

$readChars = $nativeType.DefinePInvokeMethod('ReadConsoleOutputCharacterW', 'kernel32.dll', [System.Reflection.MethodAttributes]::Public -bor [System.Reflection.MethodAttributes]::Static -bor [System.Reflection.MethodAttributes]::PinvokeImpl, [System.Reflection.CallingConventions]::Standard, [bool], [type[]]@([intptr], [char[]], [uint32], [uint32], [uint32].MakeByRefType()), [System.Runtime.InteropServices.CallingConvention]::Winapi, [System.Runtime.InteropServices.CharSet]::Unicode)
$readChars.SetImplementationFlags($readChars.GetMethodImplementationFlags() -bor [System.Reflection.MethodImplAttributes]::PreserveSig) | Out-Null

$nativeT = $nativeType.CreateType()

$getInfoM = $nativeT.GetMethod('GetConsoleScreenBufferInfo')
$getStdHandleM = $nativeT.GetMethod('GetStdHandle')
$readCharsM = $nativeT.GetMethod('ReadConsoleOutputCharacterW')

function Get-FieldValue($obj, $fieldName) {
    return $obj.GetType().GetField($fieldName).GetValue($obj)
}

$h = $getStdHandleM.Invoke($null, @([int]-11))
if ($h -eq [intptr]::Zero -or $h -eq [intptr](-1)) {
    Write-Output 'ERROR: no console stdout handle'
    exit 1
}
"Handle: 0x$($h.ToInt64().ToString('X'))"

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

# Read each row as text from column 0.
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
