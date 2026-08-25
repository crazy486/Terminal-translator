try {
  Add-Type -TypeDefinition 'public static class X { public static string Hi(){return "hi";} }'
  'Add-Type OK: ' + [X]::Hi()
} catch {
  'Add-Type FAILED: ' + $_.Exception.Message
}
try {
  Set-Content -Path 'D:\Projects\Terminal Translator\research\tt-last-capture\docs\child-write-test.txt' -Value 'x' -ErrorAction Stop
  'write OK'
} catch {
  'write FAILED: ' + $_.Exception.Message
}
