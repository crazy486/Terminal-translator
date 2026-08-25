param([int]$TargetRows = 200)
$ErrorActionPreference = 'Stop'

$asmName = New-Object System.Reflection.AssemblyName('SetBufferProbe2')
$asm = [System.AppDomain]::CurrentDomain.DefineDynamicAssembly($asmName, [System.Reflection.Emit.AssemblyBuilderAccess]::Run)
$mod = $asm.DefineDynamicModule('SetBufferMod2')

function New-StructType([string]$name, [System.Collections.Specialized.OrderedDictionary]$fields) {
    $t = $mod.DefineType($name, [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed -bor [System.Reflection.TypeAttributes]::SequentialLayout, [System.ValueType])
    foreach ($k in $fields.Keys) {
        $t.DefineField([string]$k, $fields[$k], [System.Reflection.FieldAttributes]::Public) | Out-Null
    }
    return $t.CreateType()
}

$coordT = New-StructType 'COORD8' ([ordered]@{ X = [int16]; Y = [int16] })
$rectT = New-StructType 'SMALL_RECT8' ([ordered]@{ Left = [int16]; Top = [int16]; Right = [int16]; Bottom = [int16] })
$infoT = New-StructType 'CONSOLE_SCREEN_BUFFER_INFO8' ([ordered]@{ dwSize = $coordT; dwCursorPosition = $coordT; wAttributes = [int16]; srWindow = $rectT; dwMaximumWindowSize = $coordT })

$nativeType = $mod.DefineType('Native8', [System.Reflection.TypeAttributes]::Public -bor [System.Reflection.TypeAttributes]::Sealed)
$mb = [System.Reflection.MethodAttributes]::Public -bor [System.Reflection.MethodAttributes]::Static -bor [System.Reflection.MethodAttributes]::PinvokeImpl
$cc = [System.Reflection.CallingConventions]::Standard
$wc = [System.Runtime.InteropServices.CallingConvention]::Winapi
$ps = [System.Reflection.MethodImplAttributes]::PreserveSig

$createFile = $nativeType.DefinePInvokeMethod('CreateFileW', 'kernel32.dll', $mb, $cc, [intptr], [type[]]@([string], [uint32], [uint32], [intptr], [uint32], [uint32], [intptr]), $wc, [System.Runtime.InteropServices.CharSet]::Unicode)
$createFile.SetImplementationFlags($createFile.GetMethodImplementationFlags() -bor $ps) | Out-Null

$getInfo = $nativeType.DefinePInvokeMethod('GetConsoleScreenBufferInfo', 'kernel32.dll', $mb, $cc, [bool], [type[]]@([intptr], $infoT.MakeByRefType()), $wc, [System.Runtime.InteropServices.CharSet]::Auto)
$getInfo.SetImplementationFlags($getInfo.GetMethodImplementationFlags() -bor $ps) | Out-Null

$setBuffer = $nativeType.DefinePInvokeMethod('SetConsoleScreenBufferSize', 'kernel32.dll', $mb, $cc, [bool], [type[]]@([intptr], $coordT), $wc, [System.Runtime.InteropServices.CharSet]::Auto)
$setBuffer.SetImplementationFlags($setBuffer.GetMethodImplementationFlags() -bor $ps) | Out-Null

$nativeT = $nativeType.CreateType()
$createFileM = $nativeT.GetMethod('CreateFileW')
$getInfoM = $nativeT.GetMethod('GetConsoleScreenBufferInfo')
$setBufferM = $nativeT.GetMethod('SetConsoleScreenBufferSize')

function Get-FieldValue($obj, $fieldName) { return $obj.GetType().GetField($fieldName).GetValue($obj) }
function New-ObjArray([int]$count) { return [System.Array]::CreateInstance([object], $count) }

$GENERIC_READ = [uint32]2147483648
$GENERIC_WRITE = [uint32]1073741824
$FILE_SHARE_READ = [uint32]1
$FILE_SHARE_WRITE = [uint32]2
$OPEN_EXISTING = [uint32]3

$ca = New-ObjArray 7
$ca.SetValue('CONOUT$', 0); $ca.SetValue(($GENERIC_READ -bor $GENERIC_WRITE), 1); $ca.SetValue(($FILE_SHARE_READ -bor $FILE_SHARE_WRITE), 2); $ca.SetValue([intptr]::Zero, 3); $ca.SetValue($OPEN_EXISTING, 4); $ca.SetValue([uint32]0, 5); $ca.SetValue([intptr]::Zero, 6)
$h = $createFileM.Invoke($null, $ca)
if ($null -eq $h -or $h -eq [intptr]::Zero -or $h -eq [intptr](-1)) { Write-Output "ERROR open CONOUT$"; exit 1 }

$ia = New-ObjArray 2; $ia.SetValue($h, 0); $ia.SetValue([System.Activator]::CreateInstance($infoT), 1)
$null = $getInfoM.Invoke($null, $ia)
$info = $ia.GetValue(1)
$size = Get-FieldValue $info 'dwSize'
"Original buffer: $($size.X) x $($size.Y)"

$newSize = [System.Activator]::CreateInstance($coordT)
$newSize.GetType().GetField('X').SetValue($newSize, [int16]$size.X)
$newSize.GetType().GetField('Y').SetValue($newSize, [int16]$TargetRows)
$sa = New-ObjArray 2; $sa.SetValue($h, 0); $sa.SetValue($newSize, 1)
$setOk = $setBufferM.Invoke($null, $sa)
"SetConsoleScreenBufferSize($($size.X)x$TargetRows) returned: $setOk"

$ia2 = New-ObjArray 2; $ia2.SetValue($h, 0); $ia2.SetValue([System.Activator]::CreateInstance($infoT), 1)
$null = $getInfoM.Invoke($null, $ia2)
$info2 = $ia2.GetValue(1)
$size2 = Get-FieldValue $info2 'dwSize'
$window2 = Get-FieldValue $info2 'srWindow'
"After set: buffer: $($size2.X) x $($size2.Y); window: ($($window2.Left), $($window2.Top)) - ($($window2.Right), $($window2.Bottom))"

$restore = [System.Activator]::CreateInstance($coordT)
$restore.GetType().GetField('X').SetValue($restore, [int16]$size.X)
$restore.GetType().GetField('Y').SetValue($restore, [int16]$size.Y)
$ra = New-ObjArray 2; $ra.SetValue($h, 0); $ra.SetValue($restore, 1)
$null = $setBufferM.Invoke($null, $ra)
"Restored buffer to: $($size.X) x $($size.Y)"
