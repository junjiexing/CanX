$ErrorActionPreference = 'Stop'

$directoryVariables = @{
    APPDATA = 'ApplicationData'
    LOCALAPPDATA = 'LocalApplicationData'
    USERPROFILE = 'UserProfile'
    ProgramFiles = 'ProgramFiles'
    'ProgramFiles(x86)' = 'ProgramFilesX86'
    ProgramData = 'CommonApplicationData'
    ALLUSERSPROFILE = 'CommonApplicationData'
}

foreach ($variableName in $directoryVariables.Keys) {
    $directory = [Environment]::GetEnvironmentVariable($variableName, 'Process')
    if ([string]::IsNullOrWhiteSpace($directory) -or -not [IO.Directory]::Exists($directory)) {
        $directory = [Environment]::GetFolderPath($directoryVariables[$variableName])
        if ([string]::IsNullOrWhiteSpace($directory) -or -not [IO.Directory]::Exists($directory)) {
            throw "Windows build directory could not be resolved for ${variableName}. Check the current user's profile and directory permissions."
        }
        [Environment]::SetEnvironmentVariable($variableName, $directory, 'Process')
    }
}

if ([string]::IsNullOrWhiteSpace($env:OS)) {
    $env:OS = 'Windows_NT'
}