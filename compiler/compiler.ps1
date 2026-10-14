param(
	[Parameter(Position = 0)]
	[string]$OutputDir
)

$ErrorActionPreference = 'Stop'

function Convert-ToFileUri {
	param(
		[Parameter(Mandatory = $true)]
		[string]$Path
	)

	$absolutePath = [System.IO.Path]::GetFullPath($Path)
	return ([System.Uri]$absolutePath).AbsoluteUri
}

$script:ExitCode = 0
$script:LogFile = $null
$script:ExePath = $null
$script:LogDir = $null

function Write-Log {
	param(
		[Parameter(Mandatory = $true)]
		[string]$Message
	)

	Write-Host $Message
	if ($script:LogFile) {
		Add-Content -LiteralPath $script:LogFile -Value $Message
	}
}

function Print-LogLinksOnExit {
	param(
		[Parameter(Mandatory = $true)]
		[int]$Code
	)

	$exePathDisplay = if ([string]::IsNullOrWhiteSpace($script:ExePath)) { '(not available)' } else { $script:ExePath }
	$logFileDisplay = if ([string]::IsNullOrWhiteSpace($script:LogFile)) { '(not available)' } else { $script:LogFile }
	$logDirDisplay = if ([string]::IsNullOrWhiteSpace($script:LogDir)) { '(not available)' } else { $script:LogDir }

	if ($Code -eq 0) {
		Write-Host "Build complete: $exePathDisplay"
	}
	else {
		Write-Host "Build failed with exit code $Code."
	}

	Write-Host "Compiled EXE path: $exePathDisplay"
	if (-not [string]::IsNullOrWhiteSpace($script:ExePath)) {
		Write-Host "Compiled EXE URI: $(Convert-ToFileUri -Path $script:ExePath)"
	}
	else {
		Write-Host 'Compiled EXE URI: (not available)'
	}

	Write-Host "Compiler log: $logFileDisplay"
	if (-not [string]::IsNullOrWhiteSpace($script:LogFile)) {
		Write-Host "Compiler log URI: $(Convert-ToFileUri -Path $script:LogFile)"
	}
	else {
		Write-Host 'Compiler log URI: (not available)'
	}

	Write-Host "Runtime logs directory: $logDirDisplay"
	if (-not [string]::IsNullOrWhiteSpace($script:LogDir)) {
		Write-Host "Runtime logs URI: $(Convert-ToFileUri -Path $script:LogDir)"
	}
	else {
		Write-Host 'Runtime logs URI: (not available)'
	}
}

try {
	$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

	$projectFile = Join-Path $scriptDir 'PhantomHaze.csproj'
	if (-not (Test-Path -LiteralPath $projectFile)) {
		$projectFile = Join-Path (Split-Path -Parent $scriptDir) 'PhantomHaze.csproj'
	}

	if (-not (Test-Path -LiteralPath $projectFile)) {
		throw "Could not find PhantomHaze.csproj in '$scriptDir' or its parent directory."
	}

	$tempRoot = if ($env:PHANTOMHAZE_TEMP_ROOT) {
		$env:PHANTOMHAZE_TEMP_ROOT
	}
	else {
		Join-Path $scriptDir 'tmp'
	}

	if (-not $OutputDir) {
		$OutputDir = Join-Path $tempRoot 'PhantomHaze\publish'
	}

	$logDir = Join-Path $tempRoot 'PhantomHaze\logs'
	$binDir = Join-Path $tempRoot 'PhantomHaze\bin'
	$objDir = Join-Path $tempRoot 'PhantomHaze\obj'

	Remove-Item -LiteralPath $binDir, $objDir, $OutputDir -Recurse -Force -ErrorAction SilentlyContinue

	New-Item -ItemType Directory -Path $logDir -Force | Out-Null
	New-Item -ItemType Directory -Path $binDir -Force | Out-Null
	New-Item -ItemType Directory -Path $objDir -Force | Out-Null
	New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

	$timestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
	$logFile = Join-Path $logDir "compiler-$timestamp.log"
	New-Item -ItemType File -Path $logFile -Force | Out-Null

	$projectDir = Split-Path -Parent $projectFile
	$legacyBinDir = Join-Path $projectDir 'bin'
	$legacyObjDir = Join-Path $projectDir 'obj'
	$projectLocalTempRoot = Join-Path $scriptDir 'tmp'
	$projectLocalTempRootFull = [System.IO.Path]::GetFullPath($projectLocalTempRoot)
	$currentTempRootFull = [System.IO.Path]::GetFullPath($tempRoot)
	$projectLocalTempRootPrefix = $projectLocalTempRootFull.TrimEnd('\') + '\'
	$preservedTempChildDirFull = $null

	if ($currentTempRootFull.Equals($projectLocalTempRootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
		$preservedTempChildDirFull = [System.IO.Path]::GetFullPath((Join-Path $projectLocalTempRootFull 'PhantomHaze'))
	}
	elseif ($currentTempRootFull.StartsWith($projectLocalTempRootPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
		$relativeTempRootFromProjectTmp = $currentTempRootFull.Substring($projectLocalTempRootFull.Length).TrimStart('\')
		$activeTopLevelChild = $relativeTempRootFromProjectTmp.Split('\')[0]
		if ($activeTopLevelChild) {
			$preservedTempChildDirFull = [System.IO.Path]::GetFullPath((Join-Path $projectLocalTempRootFull $activeTopLevelChild))
		}
	}

	$script:LogFile = $logFile
	$script:ExePath = Join-Path $OutputDir 'PhantomHaze.exe'
	$script:LogDir = $logDir

	$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
	if (-not $dotnetCmd) {
		Write-Log 'Error: dotnet SDK is not installed or not on PATH.'
		$script:ExitCode = 1
		return
	}

	$dotnetVersion = (& dotnet --version)
	Write-Log "Using .NET SDK $dotnetVersion"
	Write-Log "Publishing $projectFile to $OutputDir"
	Write-Log "Temp build paths: bin=$binDir obj=$objDir"

	if ((Test-Path -LiteralPath $legacyBinDir) -or (Test-Path -LiteralPath $legacyObjDir)) {
		Write-Log 'Cleaning project-root bin/obj to avoid duplicate compile items with custom temp paths.'
		Remove-Item -LiteralPath $legacyBinDir, $legacyObjDir -Recurse -Force -ErrorAction SilentlyContinue
	}

	if ((Test-Path -LiteralPath $projectLocalTempRoot) -and $preservedTempChildDirFull) {
		Get-ChildItem -LiteralPath $projectLocalTempRoot -Directory -ErrorAction SilentlyContinue |
			Where-Object { [System.IO.Path]::GetFullPath($_.FullName) -ne $preservedTempChildDirFull } |
			ForEach-Object {
				Write-Log "Cleaning stale temp build root: $($_.FullName)"
				Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
			}
	}

	$publishArgs = @(
		'publish'
		$projectFile
		'-c'
		'Release'
		'-r'
		'win-x64'
		'--self-contained'
		'true'
		'-p:PublishSingleFile=true'
		'-p:IncludeNativeLibrariesForSelfExtract=true'
		'-p:EnableWindowsTargeting=true'
		"-p:BaseOutputPath=$([System.IO.Path]::GetFullPath($binDir))\\"
		"-p:BaseIntermediateOutputPath=$([System.IO.Path]::GetFullPath($objDir))\\"
	)

	$publishArgs += @(
		'-o'
		$OutputDir
	)

	& dotnet @publishArgs 2>&1 | Tee-Object -FilePath $script:LogFile -Append

	$script:ExitCode = $LASTEXITCODE
}
catch {
	if ($script:LogFile) {
		Write-Log "Error: $($_.Exception.Message)"
	}
	else {
		Write-Host "Error: $($_.Exception.Message)"
	}
	$script:ExitCode = 1
}
finally {
	Print-LogLinksOnExit -Code $script:ExitCode
	exit $script:ExitCode
}
