<#
.SYNOPSIS
Fetches the public-domain books the README's screenshots show, from the Internet Archive.

.DESCRIPTION
Every book here was published before 1925 and its author and illustrators died more than 70 years ago, so it is in
the public domain in the United States and in countries counting life plus 70 years. Their PDFs are never committed:
this script downloads them into the folder given, named by title, skipping any already there. Then:

    Bibliotaph.exe --screenshots <pictures> --screenshot-files <folder> --data-root <empty folder> --screenshot-open "Treasure Island|1"

See docs\developers.md, "README screenshots".
#>
param([Parameter(Mandatory)][string]$Folder)
$ErrorActionPreference = 'Stop'
$books = [ordered]@{
    'The Green Fairy Book'                      = 'greenfairybook00lang'
    'The Story of the Champions of the Round Table' = 'storyofchampions00pyle'
    'The Story of King Arthur and His Knights'  = 'storyofkingarth100pyle'
    'The Merry Adventures of Robin Hood'        = 'merryadventureso00pyle2'
    'The Red Fairy Book'                        = 'redfairybook00lang'
    'The Blue Fairy Book'                       = 'cu31924011575739'
    'The Arabian Nights Entertainments'         = 'McGillLibrary-rbsc_bourke_arabian-nights-entertainment_PJ7715L361898-15699'
    'The Heroes'                                = 'heroesorgreekfai00king3'
    'Treasure Island'                           = 'treasureisland00stev2'
    'Dracula'                                   = 'dracu00stok'
    'The Wonderful Wizard of Oz'                = 'wonderfulwizardo00baumiala'
    'Dorothy and the Wizard in Oz'              = 'dorothywizardino00baum'
}
New-Item -ItemType Directory -Force $Folder | Out-Null
foreach ($title in $books.Keys) {
    $target = Join-Path $Folder "$title.pdf"
    if (Test-Path $target) { continue }
    $id = $books[$title]
    try {
        $files = (Invoke-RestMethod "https://archive.org/metadata/$id/files" -TimeoutSec 60).result
        # The scan with its text layer; a black-and-white copy only when there is no other.
        $pdf = $files | Where-Object { $_.name -like '*.pdf' -and $_.name -notlike '*_bw.pdf' } | Select-Object -First 1
        if (-not $pdf) { $pdf = $files | Where-Object { $_.name -like '*.pdf' } | Select-Object -First 1 }
        if (-not $pdf) { Write-Warning "$title ($id) has no PDF."; continue }
        Write-Host "$title <- $id/$($pdf.name)"
        Invoke-WebRequest "https://archive.org/download/$id/$([uri]::EscapeDataString($pdf.name))" -OutFile "$target.partial" -UseBasicParsing -TimeoutSec 600
        Move-Item "$target.partial" $target -Force
    }
    catch { Write-Warning "$title ($id) didn't download: $($_.Exception.Message)" }
}
