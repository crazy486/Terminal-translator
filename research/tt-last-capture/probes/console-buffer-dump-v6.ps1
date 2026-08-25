$ErrorActionPreference = 'Stop'

$asmName = New-Object System.Reflection.AssemblyName('ConProbeDynamic6')
$asm = [System.AppDomain]::CurrentDomain.DefineDynamicAssembly($asmName, [System.Reflection.Emit.AssemblyBuilderAccess]::Run)
$mod = $asm.DefineDynamicModule('ConProbeMod6')

function New-StructType([string]$name, [System.Collections.Specialized.OrderedDictionary]$fields) {
    $t = $mod.DefineType($name, [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed -bor [System.Reflection.TypeAttributes]::SequentialLayout, [System.ValueType])
    foreach ($k in $fields.Keys) {
        $t.DefineField([string]$k, $fields[$k], [System.Reflection.FieldAttributes]::Public) | Out-Null
    }
    return $t.CreateType()
}

$coordT = New-StructType 'COORD6' ([ordered]@{ X = [int16]; Y = [int16] })
$rectT = New-StructType 'SMALL_RECT6' ([ordered]@{ Left = [int16]; Top = [int16]; Right = [int16]; Bottom = [int16] })
$infoT = New-StructType 'CONSOLE_SCREEN_BUFFER_INFO6' ([ordered]@{ dwSize = $coordT; dwCursorPosition = $coordT; wAttributes = [int16]; srWindow = $rectT; dwMaximumWindowSize = $coordT })

$nativeType = $mod.DefineType('Native6', [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed)
$mb = [System.Reflection.MethodAttributes]::Public -bor [System.Reflection.MethodAttributes]::Static -bor [System.Reflection.MethodAttributes]::PinvokeImpl
$cc = [System.Reflection.CallingConventions]::Standard
$wc = [System.Runtime.InteropServices.CallingConvention]::Winapi
$ps = [System.Reflection.MethodImplAttributes]::PreserveSig

$createFile = $nativeType.DefinePInvokeMethod('CreateFileW', 'kernel32.dll', $mb, $cc, [intptr], [type[]]@([string], [uint32], [uint32], [intptr], [uint32], [uint32], [intptr]), $wc, [System.Runtime.InteropServices.CharSet]::Unicode)
$createFile.SetImplementationFlags($createFile.GetMethodImplementationFlags() -bor $ps) | Out-Null

$getInfo = $nativeType.DefinePInvokeMethod('GetConsoleScreenBufferInfo', 'kernel32.dll', $mb, $cc, [bool], [type[]]@([intptr], $infoT.MakeByRefType()), $wc, [System.Runtime.InteropServices.CharSet]::Auto)
$getInfo.SetImplementationFlags($getInfo.GetMethodImplementationFlags() -bor $ps) | Out-Null

$readChars = $nativeType.DefinePInvokeMethod('ReadConsoleOutputCharacterW', 'kernel32.dll', $mb, $cc, [bool], [type[]]@([intptr], [System.Text.StringBuilder], [uint32], [uint32], [uint32].MakeByRefType()), $wc, [System.Runtime.InteropServices.CharSet]::Unicode)
$readChars.SetImplementationFlags($readChars.GetMethodImplementationFlags() -bor $ps) | Out-Null

$nativeT = $nativeType.CreateType()
$createFileM = $nativeT.GetMethod('CreateFileW')
$getInfoM = $nativeT.GetMethod('GetConsoleScreenBufferInfo')
$readCharsM = $nativeT.GetMethod('ReadConsoleOutputCharacterW')

function Get-FieldValue($obj, $fieldName) {
    return $obj.GetType().GetField($fieldName).GetValue($obj)
}

function New-ObjArray([int]$count) {
    return [System.Array]::CreateInstance([object], $count)
}

$GENERIC_READ = [uint32]2147483648
$GENERIC_WRITE = [uint32]1073741824
$FILE_SHARE_READ = [uint32]1
$FILE_SHARE_WRITE = [uint32]2
$OPEN_EXISTING = [uint32]3

$createArgs = New-ObjArray 7
$createArgs.SetValue('CONOUT$', 0)
$createArgs.SetValue(($GENERIC_READ -bor $GENERIC_WRITE), 1)
$createArgs.SetValue(($FILE_SHARE_READ -bor $FILE_SHARE_WRITE), 2)
$createArgs.SetValue([intptr]::Zero, 3)
$createArgs.SetValue($OPEN_EXISTING, 4)
$createArgs.SetValue([uint32]0, 5)
$createArgs.SetValue([intptr]::Zero, 6)
$h = $createFileM.Invoke($null, $createArgs)
if ($null -eq $h) {
    Write-Output 'ERROR: CreateFile returned null'
    exit 1
}
if ($h -eq [intptr]::Zero -or $h -eq [intptr](-1)) {
    Write-Output "ERROR: cannot open CONOUT$ LastWin32Error=$([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
    exit 1
}
"CONOUT handle: 0x$($h.ToInt64().ToString('X'))"

$infoArgs = New-ObjArray 2
$infoArgs.SetValue($h, 0)
$infoArgs.SetValue([System.Activator]::CreateInstance($infoT), 1)
$infoOk = $getInfoM.Invoke($null, $infoArgs)
"GetConsoleScreenBufferInfo returned: $infoOk  LastWin32Error=$([System.Runtime.InteropServices.Marshal]::GetLastWin32Error())"
$info = $infoArgs.GetValue(1)

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
    $line = New-Object System.Text.StringBuilder $cols
    $readArgs = New-ObjArray 5
    $readArgs.SetValue($h, 0)
    $readArgs.SetValue($line, 1)
    $readArgs.SetValue([uint32]$cols, 2)
    $readArgs.SetValue([uint32]$y, 3)
    $readArgs.SetValue([uint32]0, 4)
    $ok = $readCharsM.Invoke($null, $readArgs)
    if (-not $ok) {
        $sb.AppendLine("<<row $y unreadable>>") | Out-Null
        continue
    }
    $read = [int]$readArgs.GetValue(4)
    $sb.AppendLine($line.ToString(0, $read)) | Out-Null
}
'===== CONSOLE TEXT ====='
$sb.ToString()
