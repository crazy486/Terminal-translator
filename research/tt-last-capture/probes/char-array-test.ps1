$cols = 10
$chars = New-Object char[] $cols
"type1: $($chars.GetType().FullName)"
$chars2 = New-Object 'char[]' $cols
"type2: $($chars2.GetType().FullName)"
$chars3 = [char[]]::new($cols)
"type3: $($chars3.GetType().FullName)"
