try { "Console BufferWidth: " + [Console]::BufferWidth } catch { "Console BufferWidth error: " + $_.Exception.Message }
try { "Console BufferHeight: " + [Console]::BufferHeight } catch { "Console BufferHeight error: " + $_.Exception.Message }
try { "Console WindowWidth: " + [Console]::WindowWidth } catch { "Console WindowWidth error: " + $_.Exception.Message }
try { "RawUI BufferSize: $($Host.UI.RawUI.BufferSize)" } catch { "RawUI BufferSize error: " + $_.Exception.Message }
try { "RawUI WindowTitle: $($Host.UI.RawUI.WindowTitle)" } catch { "RawUI WindowTitle error: " + $_.Exception.Message }
