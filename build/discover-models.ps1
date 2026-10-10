# TEMPORARY (built-in AI, A1): prints the Hugging Face revisions, sizes and SHA-256 of the models the plan offers, as
# annotations, since the cloud session that writes the model list can't reach Hugging Face. Removed before merge.
$ErrorActionPreference = 'Stop'
foreach ($repo in 'Qwen/Qwen3-8B-GGUF', 'Qwen/Qwen3-4B-GGUF', 'Qwen/Qwen3-4B-Instruct-2507-GGUF', 'ggml-org/models') {
    try {
        $info = Invoke-RestMethod "https://huggingface.co/api/models/$repo"
        $path = if ($repo -eq 'ggml-org/models') { 'tree/main/tinyllamas' } else { 'tree/main' }
        $files = Invoke-RestMethod "https://huggingface.co/api/models/$repo/$path"
        $lines = @($files | Where-Object { $_.path -match '(Q4_K_M|Q5_K_M|Q8_0|stories260K)\.gguf$' } |
            ForEach-Object { "$($_.path) size=$($_.lfs.size) sha256=$($_.lfs.oid)" })
        $licence = ($info.cardData.license, $info.tags -match '^license:') -join ' '
        Write-Host "::warning title=Model $repo::revision=$($info.sha) licence=$licence%0A$($lines -join '%0A')"
    }
    catch {
        Write-Host "::warning title=Model $repo::not found ($($_.Exception.Message))"
    }
}
