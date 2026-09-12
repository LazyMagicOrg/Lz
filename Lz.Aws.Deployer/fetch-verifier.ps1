#Requires -Version 7
<#
.SYNOPSIS
  Fetch the Notation verifier that signature-hook.zip packages, refuse anything that is not the exact
  set verified on 2026-09-12, and lay it out in Lz.Aws.Deployer/notation/.

.DESCRIPTION
  The signature hook (DecoupledCd.md §4.4.1) runs the Notation CLI and the AWS Signer plugin against a
  root certificate. They are third-party binaries, so they are never committed: this script is how a
  machine gets them, and Lz.Aws's build packages whatever is in notation/ into signature-hook.zip.

  EVERY FILE IS PINNED BY SHA-256, and a mismatch is a refusal. The pins are not a convenience - they
  are the only link between these bytes and the verification that was done by hand when they were
  first fetched:

    notation_1.3.2_linux_amd64.tar.gz  its SHA-256 matched notation_1.3.2_checksums.txt from the same
                                       GitHub release.
    notation-aws-signer-plugin.zip     its GPG signature (made 2025-06-13) was good, by the key whose
                                       fingerprint AWS Signer's documentation publishes:
                                       E84A F8A2 A9B5 2F1F 4435 AE71 A3B5 2DA6 5461 CF90.
    aws-signer-notation-root.cert      its DER SHA-256 (90a87d05...473f) is the root thumbprint in the
                                       x509 chain of the signature ECR managed signing produced for
                                       this pipeline's own image, sha256:5e4f504e..., in scu-cicd.

  THE PLUGIN URL IS "latest". When AWS publishes a new build, this script refuses it. That is the
  intended behaviour: do not update the hash to make it pass. Re-verify the new zip's GPG signature
  against the fingerprint above (from AWS's documentation, not from the download host), then re-pin.

  Nothing downloaded is executed. They are Linux x86-64 binaries for the Lambda; this script only
  checks their ELF headers.

.PARAMETER FromDirectory
  Use archives already in this directory instead of downloading them. The pins apply either way.

.PARAMETER Destination
  Where to lay the verifier out. Defaults to notation/ beside this script, which .gitignore excludes.
#>
[CmdletBinding()]
param(
    [string] $FromDirectory,
    [string] $Destination = (Join-Path $PSScriptRoot 'notation')
)

$ErrorActionPreference = 'Stop'

$pins = [ordered]@{
    'notation_1.3.2_linux_amd64.tar.gz' = @{
        Url    = 'https://github.com/notaryproject/notation/releases/download/v1.3.2/notation_1.3.2_linux_amd64.tar.gz'
        Sha256 = 'e1a0f060308086bf8020b2d31defb7c5348f133ca0dba6a1a7820ef3cbb6dfe5'
    }
    'notation-aws-signer-plugin.zip' = @{
        Url    = 'https://d2hvyiie56hcat.cloudfront.net/linux/amd64/plugin/latest/notation-aws-signer-plugin.zip'
        Sha256 = 'f8efa548932edc4851f4ba464496994bad753cdf21cd32baa575f7291a26c7b9'
    }
    'aws-signer-notation-root.cert' = @{
        Url    = 'https://d2hvyiie56hcat.cloudfront.net/aws-signer-notation-root.cert'
        Sha256 = '0ccf529561f610b9eb7f9b0ee02fffe2f74f7ffdf0ac56348ca224e879461c9b'
    }
}

# Staged OUTSIDE notation/: Lz.Aws packages every file in that folder, and the archives must not ride
# along into the Lambda.
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ("lz-verifier-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null

try {
    foreach ($name in $pins.Keys) {
        $path = Join-Path $stage $name
        if ($FromDirectory) {
            Copy-Item -LiteralPath (Join-Path $FromDirectory $name) -Destination $path
        }
        else {
            Write-Host "downloading $name"
            Invoke-WebRequest -Uri $pins[$name].Url -OutFile $path -UseBasicParsing
        }

        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $pins[$name].Sha256) {
            throw "$name has SHA-256 $actual, not the pinned $($pins[$name].Sha256). Refusing. " +
                  "If this is the plugin, AWS has probably published a new 'latest': verify its GPG " +
                  "signature against the fingerprint in this script's header before re-pinning."
        }
        Write-Host "  $name matches its pin"
    }

    # Read single named entries rather than extracting archives wholesale: nothing an archive contains
    # beyond these names is written anywhere.
    function Read-TarEntry([string] $archive, [string] $entryName) {
        $file = [System.IO.File]::OpenRead($archive)
        try {
            $gzip = [System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionMode]::Decompress)
            $reader = [System.Formats.Tar.TarReader]::new($gzip)
            while ($null -ne ($entry = $reader.GetNextEntry())) {
                if ($entry.Name -eq $entryName -and $entry.EntryType -in 'RegularFile', 'V7RegularFile') {
                    $buffer = [System.IO.MemoryStream]::new()
                    $entry.DataStream.CopyTo($buffer)
                    return , $buffer.ToArray()
                }
            }
            throw "$archive has no regular file named $entryName"
        }
        finally { $file.Dispose() }
    }

    function Read-ZipEntry([string] $archive, [string] $entryName) {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($archive)
        try {
            $entry = $zip.GetEntry($entryName) ?? $(throw "$archive has no entry named $entryName")
            $buffer = [System.IO.MemoryStream]::new()
            $stream = $entry.Open()
            try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
            return , $buffer.ToArray()
        }
        finally { $zip.Dispose() }
    }

    $tarball = Join-Path $stage 'notation_1.3.2_linux_amd64.tar.gz'
    $plugin = Join-Path $stage 'notation-aws-signer-plugin.zip'

    $files = [ordered]@{
        'notation'                                        = Read-TarEntry $tarball 'notation'
        'LICENSE.notation'                                = Read-TarEntry $tarball 'LICENSE'
        'notation-com.amazonaws.signer.notation.plugin'   = Read-ZipEntry $plugin 'notation-com.amazonaws.signer.notation.plugin'
        'LICENSE.aws-signer-notation-plugin'              = Read-ZipEntry $plugin 'LICENSE'
        'THIRD_PARTY_LICENSES.aws-signer-notation-plugin' = Read-ZipEntry $plugin 'THIRD_PARTY_LICENSES'
        'aws-signer-notation-root.cert'                   = [System.IO.File]::ReadAllBytes((Join-Path $stage 'aws-signer-notation-root.cert'))
    }

    # Linux x86-64 ELF: the magic, a 64-bit little-endian class, and machine 0x3E. The Lambda's
    # architecture is x86_64 (DeployerBootstrapper), so an arm64 or macOS build here would fail there.
    foreach ($binary in 'notation', 'notation-com.amazonaws.signer.notation.plugin') {
        $b = $files[$binary]
        $isElf = $b.Length -gt 20 -and $b[0] -eq 0x7F -and $b[1] -eq 0x45 -and $b[2] -eq 0x4C -and $b[3] -eq 0x46 `
                 -and $b[4] -eq 2 -and $b[5] -eq 1 -and ($b[18] -bor ($b[19] -shl 8)) -eq 0x3E
        if (-not $isElf) { throw "$binary is not a Linux x86-64 ELF binary. Refusing." }
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($name in $files.Keys) {
        [System.IO.File]::WriteAllBytes((Join-Path $Destination $name), $files[$name])
    }

    # What the package carries, shipped inside it — so a function's code says which verifier checked an
    # image, and a package built on any machine says the same thing.
    $sha = { param($bytes) [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
    [ordered]@{
        note            = 'What signature-hook.zip packages as its verifier. Third-party binaries, never committed. ' +
                          'Laid out by Lz.Aws.Deployer/fetch-verifier.ps1, whose header records how each pin was verified.'
        notation        = [ordered]@{
            version       = '1.3.2'
            source        = $pins['notation_1.3.2_linux_amd64.tar.gz'].Url
            archiveSha256 = $pins['notation_1.3.2_linux_amd64.tar.gz'].Sha256
            binarySha256  = & $sha $files['notation']
            license       = 'Apache-2.0 (LICENSE.notation)'
        }
        awsSignerPlugin = [ordered]@{
            source        = $pins['notation-aws-signer-plugin.zip'].Url
            signedBy      = 'E84A F8A2 A9B5 2F1F 4435 AE71 A3B5 2DA6 5461 CF90 (signature made 2025-06-13)'
            archiveSha256 = $pins['notation-aws-signer-plugin.zip'].Sha256
            binarySha256  = & $sha $files['notation-com.amazonaws.signer.notation.plugin']
            license       = 'Apache-2.0 (LICENSE.aws-signer-notation-plugin, THIRD_PARTY_LICENSES.aws-signer-notation-plugin)'
        }
        rootCertificate = [ordered]@{
            source    = $pins['aws-signer-notation-root.cert'].Url
            subject   = 'CN=AWS Signer Code Signing Root CA G1, OU=Cryptography, O=AWS, ST=WA, C=US'
            sha256    = $pins['aws-signer-notation-root.cert'].Sha256
            derSha256 = '90a87d0543c3f094dbff9589b6649affe2f3d6e0f308799be2258461c686473f'
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $Destination 'verifier.json') -Encoding utf8NoBOM

    Write-Host "verifier laid out in $Destination"
    Write-Host "Rebuild Lz (dotnet build Lz.slnx) to package it into Lambda/signature-hook.zip."
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
