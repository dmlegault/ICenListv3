<#
  One hash over everything the runner image is built from, so "is this image current?" is a content
  question rather than a clock one.

  Dot-sourced by build.ps1, which stamps the result on the image as the enlist.source-sha256 label,
  and by verify.ps1, which recomputes it and compares. One definition for both, because two copies
  that drifted apart would fail a correct image or pass a stale one.

  Why not timestamps: Docker's cache is content-addressed. A file that is touched but not changed - a
  git checkout, an edit undone - leaves the image exactly current and its Created time exactly as old
  as before, so "built after the newest source file" fails, and rebuilding cannot fix it.

  The file set mirrors src\Enlist.Runner\Dockerfile's COPY lines and .dockerignore's exclusions. A new
  COPY there needs a new root here; the Dockerfile says so beside its COPY lines.
#>
function Get-RunnerSourceHash {
    param([Parameter(Mandatory = $true)] [string] $RepoRoot)

    $RepoRoot = (Resolve-Path $RepoRoot).Path.TrimEnd('\')
    $roots = @('Directory.Build.props', 'src\branding', 'src\Enlist.Runner')

    $entries = New-Object System.Collections.Generic.List[string]
    foreach ($root in $roots) {
        $path = Join-Path $RepoRoot $root
        $files = if (Test-Path $path -PathType Leaf) { @(Get-Item $path) } else { @(Get-ChildItem $path -Recurse -File) }
        foreach ($file in $files) {
            # .dockerignore: **/bin/, **/obj/, **/TestResults/, **/*.user, **/.env
            if ($file.FullName -match '\\(bin|obj|TestResults)\\' -or $file.Name -like '*.user' -or $file.Name -eq '.env') { continue }
            $relative = $file.FullName.Substring($RepoRoot.Length + 1).Replace('\', '/')
            $entries.Add("$relative $((Get-FileHash $file.FullName -Algorithm SHA256).Hash)")
        }
    }

    $sorted = $entries.ToArray()
    [Array]::Sort($sorted, [StringComparer]::Ordinal)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($sorted -join "`n"))
        return (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
    }
    finally { $sha.Dispose() }
}
