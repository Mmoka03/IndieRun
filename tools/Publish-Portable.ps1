param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $projectRoot ('artifacts\releases\portable-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw '기존 폴더에 DLL이 남지 않도록 새 출력 폴더를 지정하세요.'}
dotnet publish (Join-Path $projectRoot 'src\IndieRun\IndieRun.csproj') -p:PublishProfile=Portable -o $OutputDirectory
if($LASTEXITCODE -ne 0){throw '배포 빌드 실패'}
$files=@(Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse)
if($files.Count -ne 1 -or $files[0].Name -ne 'IndieRun.exe'){throw '배포 파일 구성을 확인하세요.'}
Write-Output "단일 EXE 생성: $OutputDirectory\IndieRun.exe"
