# Test whether overriding Out-Default intercepts PowerShell output vs native stdout.
$global:captured = New-Object System.Collections.Generic.List[string]

function Out-Default {
    param([Parameter(ValueFromPipeline)]$InputObject)
    process {
        $global:captured.Add([string]$InputObject)
        Write-Output ("OUTDEFAULT_SAW: " + $InputObject)
    }
}

Write-Output 'POWERSHELL_OUTPUT_ITEM'
cmd /c echo NATIVE_ECHO_ITEM
Write-Output ('CAPTURED_COUNT=' + $global:captured.Count)
Write-Output ('CAPTURED_JOIN=' + ($global:captured -join '|'))
