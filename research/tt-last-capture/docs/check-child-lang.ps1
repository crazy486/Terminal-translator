"LanguageMode: " + $ExecutionContext.SessionState.LanguageMode
try {
  [System.IO.File]::WriteAllText('D:\Projects\Terminal Translator\research\tt-last-capture\docs\net-write-test.txt', 'x')
  'net write OK'
} catch {
  'net write FAILED: ' + $_.Exception.Message
}
"PSVersion: " + $PSVersionTable.PSVersion
