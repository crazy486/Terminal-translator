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

    return $obj.GetType().GetField($name).GetValue($obj)
}

$size = Get-Field $info 'dwSize'
$cursor = Get-Field $info 'dwCursorPosition'
$window = Get-Field $info 'srWindow'
$maxWin = Get-Field $info 'dwMaximumWindowSize'

"BufferSize: $($size.X) x $($size.Y)"
"Cursor: $($cursor.X), $($cursor.Y)"
"Window: ($($window.Left), $($window.Top)) - ($($window.Right), $($window.Bottom))"
"MaxWindowSize: $($maxWin.X) x $($maxWin.Y)"

# Read each row as text from column 0.
$rows = [int]$size.Y
$cols = [int]$size.X
$sb = New-Object System.Text.StringBuilder

'===== CONSOLE TEXT ====='
$sb.ToString()
