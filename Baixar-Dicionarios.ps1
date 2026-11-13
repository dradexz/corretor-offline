$ErrorActionPreference = 'Stop'
$revision = '4f1df64b2e8e14751aa7c2205d170ca016071315'
$base = "https://raw.githubusercontent.com/LibreOffice/dictionaries/$revision"
$destination = Join-Path $PSScriptRoot 'dicionarios'
$files = [ordered]@{
    'pt_BR/pt_BR.aff' = '21D8AD2A769A60E17E2B5EA4EF11D4D593A58B9E2A82D642EF82D6A4C5523865'
    'pt_BR/pt_BR.dic' = 'A38BFB26B68ECE2834E79FE83E48D5792652970ACE12DB89D1B9674BF9933183'
    'pt_BR/README_pt_BR.txt' = '9974CE691FDC1FE731717D7A2DC668244405FDC2BF9BF3367EB9B29E85177C88'
    'en/en_US.aff' = 'E746C882DD6F303C2C46E7452804B9201115A6942CFEB15F18F8EDF774D2E24E'
    'en/en_US.dic' = 'F0B1A234BD178BDD01875B2A392A9647F888B8FE879F79C52AAE62C2759B3647'
    'en/README_en_US.txt' = '168B4C01CB841F766A72F310B065C04D09CFF46376C37A81D799593CE751C371'
    'en/license.txt' = 'DF84246204E3C54CD24AEBBC23714235F5FC41D754D53B65444A2AFBF070130F'
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($entry in $files.GetEnumerator()) {
    $target = Join-Path $destination (Split-Path -Leaf $entry.Key)
    $temp = "$target.download"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$base/$($entry.Key)" -OutFile $temp
        if ((Get-FileHash -LiteralPath $temp).Hash -ne $entry.Value) {
            throw "Hash incorreto para $($entry.Key)."
        }
        Move-Item -LiteralPath $temp -Destination $target -Force
    }
    finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
    }
}
