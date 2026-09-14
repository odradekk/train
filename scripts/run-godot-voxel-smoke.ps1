param(
    [ValidateSet(1, 2, 3, 4)]
    [int] $Slice = 1,
    [switch] $IsolationAbort
)

# Repeatable real-Godot voxel smoke for slice 1 (#39/#46), slice 2 (#50),
# slice 2 collision queries (#51), rotating local boxes (#52),
# world save / process-restart resume (#53), slice 3 parked-vehicle
# manufacture (#58), damage/fracture (#59), atomic refit (#60),
# existing-vehicle content reload (#61), and slice 4 formal straight
# rail construction / occupancy / local damage (#68) and the static
# locomotive-and-carriage assembly (#69).
# Slice 3 reuses the isolated two-process save/resume path with a
# different persist fixture and does not load the slice 2 1089-voxel save.
# Slice 4 is a single process: it must not require later-ticket train motion.
#
# Command (from the repository root, with Godot 4.7 .NET on PATH or $env:GODOT):
#   .\scripts\run-godot-voxel-smoke.ps1 -Slice 1
#   .\scripts\run-godot-voxel-smoke.ps1 -Slice 2
#   .\scripts\run-godot-voxel-smoke.ps1 -Slice 3
#   .\scripts\run-godot-voxel-smoke.ps1 -Slice 4
#
# It loads the formal voxel development scene, removes and re-adds (4,4,4),
# applies formal single-target and multi-target damage, requires the scene's
# query-driven display and derived mesh to refresh, then closes the console,
# returns to the start menu with Esc, and re-enters through the formal menu
# button. Direct remove is not accepted as a substitute for the damage path.
# Unexpected C# exceptions fail the smoke.
# Exit 2 if Godot is unavailable; exit 1 for a failed process or missing marker.

$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Find-Godot {
    if ($env:GODOT -and (Test-Path -LiteralPath $env:GODOT)) {
        return $env:GODOT
    }

    if ($env:TRAIN_VOXEL_SMOKE_NO_SEARCH -eq "1") {
        return $null
    }

    foreach ($name in @("godot-dotnet", "godot", "godot-mono", "Godot_v4.7.2-stable_mono_win64_console", "Godot_v4.7.2-stable_mono_win64")) {
        $command = Get-Command $name -ErrorAction SilentlyContinue
        if ($command) {
            return $command.Source
        }
    }

    $roots = @(
        $env:GODOT_HOME,
        "$env:LOCALAPPDATA\Programs\Godot",
        "$env:ProgramFiles\Godot",
        "${env:ProgramFiles(x86)}\Godot"
    ) | Where-Object { $_ -and (Test-Path $_) }
    foreach ($directory in $roots) {
        $match = Get-ChildItem -LiteralPath $directory -Filter "Godot*_mono*_console.exe" -File -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if (-not $match) {
            $match = Get-ChildItem -LiteralPath $directory -Filter "Godot*.exe" -File -ErrorAction SilentlyContinue |
                Select-Object -First 1
        }
        if ($match) {
            return $match.FullName
        }
    }

    return $null
}

$godot = Find-Godot
$command = '$env:TRAIN_VOXEL_SMOKE="1"; & <godot> --headless --path . --quit-after 90'
if (-not $godot) {
    [Console]::Error.WriteLine("Godot executable not found (PATH, GODOT, GODOT_HOME, or standard Program Files).")
    [Console]::Error.WriteLine("Unrun command: $command")
    exit 2
}

Write-Host "Using Godot: $godot"
foreach ($name in @(
    "TRAIN_VOXEL_SMOKE_REENTER",
    "TRAIN_VOXEL_SMOKE_ISOLATION_ABORT",
    "TRAIN_VOXEL_SMOKE_RESUME",
    "TRAIN_VOXEL_SMOKE_SAVE_ID",
    "TRAIN_MODS_SMOKE_USER_DATA",
    "TRAIN_VOXEL_OBSERVE",
    "TRAIN_VOXEL_BASELINE",
    "TRAIN_CONTENT_SMOKE",
    "TRAIN_CONTENT_EXPORT_SMOKE",
    "TRAIN_MODS_EXPORT_SMOKE",
    "TRAIN_LOG_SMOKE"
)) {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}
$protected = @(
    "builtin\core\resources\voxels\ferrous-ore.voxels.json",
    "builtin\core\resources\voxels\ledge.voxels.json",
    "builtin\core\resources\voxels\ferrous-ore\top.png",
    "builtin\core\resources\voxels\refit-marker.voxels.json",
    "builtin\core\resources\voxels\refit-marker\top.png",
    "builtin\core\resources\voxels\identity-core.voxels.json",
    "builtin\core\resources\voxels\identity-core-heavy.voxels.json",
    "builtin\core\resources\voxels\vehicle-shell.voxels.json",
    "builtin\core\resources\composites\dev-hollow-shell.composites.json",
    "builtin\core\resources\voxels\rail.voxels.json",
    "builtin\core\resources\voxels\rail\top.png",
    "builtin\core\resources\voxels\propulsion.voxels.json",
    "builtin\core\resources\voxels\control.voxels.json",
    "builtin\core\resources\voxels\track-contact.voxels.json",
    "builtin\core\resources\voxels\locomotive-core.voxels.json",
    "builtin\core\resources\voxels\carriage-core.voxels.json",
    "builtin\core\resources\composites\dev-locomotive.composites.json",
    "builtin\core\resources\composites\dev-carriage.composites.json"
)
$beforeHashes = @{}
foreach ($relative in $protected) {
    $beforeHashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $root $relative) -Algorithm SHA256).Hash
}
$env:TRAIN_VOXEL_SMOKE = "1"
$env:TRAIN_VOXEL_SLICE = "$Slice"
if ($IsolationAbort) {
    $env:TRAIN_VOXEL_SMOKE_ISOLATION_ABORT = "1"
}

function Invoke-GodotVoxelSmoke {
    param(
        [int] $QuitAfter,
        [string] $Label
    )
    Write-Host "Starting Godot voxel smoke process: $Label"
    $raw = & $godot --headless --path $root --quit-after $QuitAfter 2>&1
    return @{
        Output = ($raw | Out-String)
        ExitCode = $LASTEXITCODE
    }
}

$godotExitCode = 0
$output = ""
$userData = $null
try {
    if (-not $IsolationAbort -and $Slice -in 2, 3) {
        $prefix = if ($Slice -eq 3) { "train-issue62-smoke-" } else { "train-issue53-smoke-" }
        $writtenMarker = if ($Slice -eq 3) { "VOXEL_SMOKE_SLICE3_SAVE_WRITTEN" } else { "VOXEL_SMOKE_SAVE_WRITTEN" }
        $writeAfter = if ($Slice -eq 3) { 700 } else { 240 }
        $resumeAfter = if ($Slice -eq 3) { 200 } else { 120 }
        $userData = Join-Path ([System.IO.Path]::GetTempPath()) ($prefix + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $userData | Out-Null
        $env:TRAIN_MODS_SMOKE_USER_DATA = $userData
        $write = Invoke-GodotVoxelSmoke -QuitAfter $writeAfter -Label "slice $Slice write"
        Write-Host $write.Output
        if ($write.Output -notmatch "$writtenMarker id=([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})") {
            [Console]::Error.WriteLine("Godot voxel smoke failed. Process 1 did not print a save UUID.")
            exit 1
        }
        $saveId = $Matches[1]
        $writeP = $null
        if ($Slice -eq 3 -and $write.Output -match "VOXEL_SMOKE_SLICE3_SAVE_WRITTEN .* p_id=(\d+)") {
            $writeP = $Matches[1]
        }
        $savePath = Join-Path $userData (Join-Path "saves" (Join-Path $saveId "save.json"))
        if (-not (Test-Path -LiteralPath $savePath)) {
            [Console]::Error.WriteLine("Godot voxel smoke failed. Save file was not on disk: $savePath")
            exit 1
        }
        $env:TRAIN_VOXEL_SMOKE_RESUME = "1"
        $env:TRAIN_VOXEL_SMOKE_SAVE_ID = $saveId
        $resume = Invoke-GodotVoxelSmoke -QuitAfter $resumeAfter -Label "slice $Slice resume"
        Write-Host $resume.Output
        if ($Slice -eq 3) {
            if (-not $writeP -or $resume.Output -notmatch "VOXEL_SMOKE_SLICE3_RESUME_LOADED .* p_id=$writeP(\D|$)") {
                [Console]::Error.WriteLine("Godot voxel smoke failed. Process 2 did not restore the same P id as process 1 (p_id=$writeP).")
                exit 1
            }
        }
        $output = $write.Output + "`n" + $resume.Output
        if ($write.ExitCode -ne 0) { $godotExitCode = $write.ExitCode } else { $godotExitCode = $resume.ExitCode }
    }
    else {
        $quitAfter = if ($IsolationAbort) { 30 } elseif ($Slice -eq 4) { 720 } elseif ($Slice -eq 3) { 520 } else { 90 }
        $single = Invoke-GodotVoxelSmoke -QuitAfter $quitAfter -Label ("slice $Slice")
        $output = $single.Output
        $godotExitCode = $single.ExitCode
        Write-Host $output
    }
}
finally {
    Remove-Item "Env:TRAIN_VOXEL_SMOKE_RESUME" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_VOXEL_SMOKE_SAVE_ID" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_MODS_SMOKE_USER_DATA" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_VOXEL_SMOKE" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_VOXEL_SLICE" -ErrorAction SilentlyContinue
    Remove-Item "Env:TRAIN_VOXEL_SMOKE_ISOLATION_ABORT" -ErrorAction SilentlyContinue
    if ($userData -and (Test-Path -LiteralPath $userData)) {
        Remove-Item -LiteralPath $userData -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$rewritten = @($protected | Where-Object {
    $beforeHashes[$_] -ne (Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash
})
if ($rewritten.Count -gt 0) {
    [Console]::Error.WriteLine("Godot voxel smoke rewrote worktree built-in content: $($rewritten -join ', ')")
    exit 1
}

$required = if ($IsolationAbort) {
    @("VOXEL_SMOKE_ISOLATION_WRITE_DONE")
} elseif ($Slice -eq 4) {
    @(
        "VOXEL_SMOKE_GEOMETRY_VALID",
        "VOXEL_SMOKE_SCENE_READY",
        "VOXEL_SMOKE_SLICE4_BASE",
        "VOXEL_SMOKE_SLICE4_CONSTRUCTED",
        "VOXEL_SMOKE_SLICE4_EDGE_CELL",
        "VOXEL_SMOKE_SLICE4_RAILS",
        "VOXEL_SMOKE_SLICE4_SLEEPERS",
        "VOXEL_SMOKE_SLICE4_CONNECTION",
        "VOXEL_SMOKE_SLICE4_TRAIN_ASSEMBLED",
        "VOXEL_SMOKE_SLICE4_TRAIN_LOCO",
        "VOXEL_SMOKE_SLICE4_TRAIN_CARRIAGE",
        "VOXEL_SMOKE_SLICE4_TRAIN_COUPLER",
        "VOXEL_SMOKE_SLICE4_TRAIN_QUALIFIED",
        "VOXEL_SMOKE_SLICE4_TRAIN_VISUAL",
        "VOXEL_SMOKE_SLICE4_TRAIN_CONTACT_LOST",
        "VOXEL_SMOKE_SLICE4_RENDER_LOCAL",
        "VOXEL_SMOKE_SLICE4_EDGE_CLIP",
        "VOXEL_SMOKE_SLICE4_DAMAGED",
        "VOXEL_SMOKE_SLICE4_GAP",
        "VOXEL_SMOKE_SLICE4_RENDER_GAP",
        "VOXEL_SMOKE_SLICE4_REPAIRED",
        "VOXEL_SMOKE_SLICE4_SAVE_REJECTED",
        "VOXEL_SMOKE_CONSOLE_CLOSED",
        "VOXEL_SMOKE_RETURNED_TO_MENU",
        "VOXEL_SMOKE_MENU_REENTERED"
    )
} elseif ($Slice -eq 3) {
    @(
        "VOXEL_SMOKE_GEOMETRY_VALID",
        "VOXEL_SMOKE_SCENE_READY",
        "VOXEL_SMOKE_SLICE3_BASE",
        "VOXEL_SMOKE_SLICE3_LOADED",
        "VOXEL_SMOKE_SLICE3_MANUFACTURED",
        "VOXEL_SMOKE_SLICE3_VISUAL",
        "VOXEL_SMOKE_SLICE3_QUERY",
        "VOXEL_SMOKE_SLICE3_WORLD_EMPTY",
        "VOXEL_SMOKE_SLICE3_REFIT_ADDED",
        "VOXEL_SMOKE_SLICE3_REFIT_DAMAGED",
        "VOXEL_SMOKE_SLICE3_REFIT_MOVED",
        "VOXEL_SMOKE_SLICE3_REFIT_ROTATED",
        "VOXEL_SMOKE_SLICE3_REFIT_SWAPPED",
        "VOXEL_SMOKE_SLICE3_REFIT_REPLACED",
        "VOXEL_SMOKE_SLICE3_REFIT_REMOVED",
        "VOXEL_SMOKE_SLICE3_REFIT_SUCCESS",
        "VOXEL_SMOKE_SLICE3_REFIT_REJECT_ENVELOPE",
        "VOXEL_SMOKE_SLICE3_REFIT_REJECT_OCCUPIED",
        "VOXEL_SMOKE_SLICE3_REFIT_REJECT_DISCONNECTED",
        "VOXEL_SMOKE_SLICE3_REFIT_REJECT_ORIENTATION",
        "VOXEL_SMOKE_SLICE3_REFIT_CONTINUE",
        "VOXEL_SMOKE_SLICE3_REFIT_PAIRS",
        "COMPOSITE_REFIT_SMOKE_OK",
        "VOXEL_SMOKE_SLICE3_RESET",
        "VOXEL_SMOKE_SLICE3_BRIDGE",
        "VOXEL_SMOKE_SLICE3_FRACTURE",
        "VOXEL_SMOKE_SLICE3_OWNERSHIP",
        "VOXEL_SMOKE_SLICE3_PARTIAL",
        "VOXEL_SMOKE_SLICE3_PARTIAL_OWNERSHIP",
        "VOXEL_SMOKE_SLICE3_DROP",
        "COMPOSITE_SMOKE_OK",
        "VOXEL_SMOKE_SLICE3_REFIT_FRACTURE",
        "VOXEL_SMOKE_SLICE3_RELOAD_MAX_150",
        "VOXEL_SMOKE_SLICE3_RELOAD_MAX_60",
        "VOXEL_SMOKE_SLICE3_RELOAD_DISPLAY",
        "VOXEL_SMOKE_SLICE3_RELOAD_TEMPLATE",
        "VOXEL_SMOKE_SLICE3_RELOAD_COLLISION",
        "VOXEL_SMOKE_SLICE3_RELOAD_COLLISION_PUBLISHED",
        "VOXEL_SMOKE_SLICE3_RELOAD_REJECTED",
        "VOXEL_SMOKE_SLICE3_RELOAD_VALUE",
        "VOXEL_SMOKE_SLICE3_RELOAD_FRACTURE",
        "COMPOSITE_RELOAD_SMOKE_OK",
        "VOXEL_SMOKE_SLICE3_SAVE_WRITTEN",
        "VOXEL_SMOKE_SLICE3_RESUME_LOADED",
        "VOXEL_SMOKE_SLICE3_RESUME_OWNERSHIP",
        "VOXEL_SMOKE_SLICE3_RESUME_CONTINUE",
        "VOXEL_SMOKE_SLICE3_RESUME_SAVED",
        "COMPOSITE_PERSIST_SMOKE_OK",
        "VOXEL_SMOKE_CONSOLE_CLOSED",
        "VOXEL_SMOKE_RETURNED_TO_MENU",
        "VOXEL_SMOKE_MENU_REENTERED"
    )
} elseif ($Slice -eq 2) {
    @(
        "VOXEL_SMOKE_GEOMETRY_VALID",
        "VOXEL_SMOKE_SCENE_READY",
        "VOXEL_SMOKE_SLICE2_BASE",
        "VOXEL_SMOKE_X_BOUNDARY",
        "VOXEL_SMOKE_X_BOUNDARY_MESH",
        "VOXEL_SMOKE_X_BOUNDARY_COLLIDE",
        "VOXEL_SMOKE_Y_BOUNDARY",
        "VOXEL_SMOKE_Y_BOUNDARY_MESH",
        "VOXEL_SMOKE_Y_BOUNDARY_COLLIDE",
        "VOXEL_SMOKE_Z_BOUNDARY",
        "VOXEL_SMOKE_Z_BOUNDARY_MESH",
        "VOXEL_SMOKE_Z_BOUNDARY_COLLIDE",
        "VOXEL_SMOKE_JUNCTION",
        "VOXEL_SMOKE_JUNCTION_MESH",
        "VOXEL_SMOKE_JUNCTION_COLLIDE",
        "VOXEL_SMOKE_CROSS_DAMAGE",
        "VOXEL_SMOKE_CROSS_DAMAGE_MESH",
        "VOXEL_SMOKE_CROSS_DAMAGE_COLLIDE",
        "VOXEL_SMOKE_REJECT_PRESERVED",
        "VOXEL_SMOKE_NONE_OCCUPIED_NO_HIT",
        "VOXEL_SMOKE_RELOAD_COLLISION_NONE",
        "VOXEL_SMOKE_RELOAD_PRESENTATION_COLLISION_UNCHANGED",
        "VOXEL_SMOKE_RELOAD_COLLISION_FULL_BOX",
        "VOXEL_SMOKE_LOCAL_BOX_IDENTITY",
        "VOXEL_SMOKE_LOCAL_BOX_ROTATED",
        "VOXEL_SMOKE_LOCAL_BOX_REMOVED",
        "VOXEL_SMOKE_LOCAL_BOX_RELOADED",
        "VOXEL_SMOKE_LOCAL_BOX_TRANSLATED_EDGE",
        "VOXEL_SMOKE_LOCAL_BOX_ADAPTER_UNDERFLOW",
        "VOXEL_SMOKE_LOCAL_BOX_REJECTED",
        "VOXEL_SMOKE_LOCAL_BOX_LETHAL",
        "VOXEL_SMOKE_RELOAD_DAMAGED_80",
        "VOXEL_SMOKE_RELOAD_MAX_150",
        "VOXEL_SMOKE_RELOAD_TEXT_UPDATED",
        "VOXEL_SMOKE_RELOAD_ASSET_UPDATED",
        "VOXEL_SMOKE_RELOAD_ORIENTATION_MAPPED",
        "VOXEL_SMOKE_RELOAD_MAX_60",
        "VOXEL_SMOKE_RELOAD_PROBE_ADDED",
        "VOXEL_SMOKE_RELOAD_REJECTED",
        "VOXEL_SMOKE_RELOAD_DISPLAY_RETAINED",
        "VOXEL_SMOKE_SAVE_SEQUENCE",
        "VOXEL_SMOKE_SAVE_WRITTEN",
        "VOXEL_SMOKE_RESUME_LOADED",
        "VOXEL_SMOKE_RESUME_COLLIDE_NONE",
        "VOXEL_SMOKE_RESUME_COLLIDE_FULL_BOX",
        "VOXEL_SMOKE_RESUME_COLLIDE_LOCAL_BOX",
        "VOXEL_SMOKE_RESUME_RELOAD_MAX_150",
        "VOXEL_SMOKE_RESUME_CONTINUE_DAMAGE",
        "VOXEL_SMOKE_RESUME_CONTINUE_EDIT",
        "VOXEL_SMOKE_RESUME_BOUNDARY_PAIRS",
        "VOXEL_SMOKE_RESUME_SAVED",
        "VOXEL_SMOKE_CONSOLE_CLOSED",
        "VOXEL_SMOKE_RETURNED_TO_MENU",
        "VOXEL_SMOKE_MENU_REENTERED"
    )
} else {
    @(
        "VOXEL_SMOKE_GEOMETRY_VALID",
        "VOXEL_SMOKE_SCENE_READY",
        "VOXEL_SMOKE_REMOVED",
        "VOXEL_SMOKE_MESH_UPDATED",
        "VOXEL_SMOKE_ADDED",
        "VOXEL_SMOKE_DISPLAY_UPDATED",
        "VOXEL_SMOKE_ORIENTATION_UPDATED",
        "VOXEL_SMOKE_DAMAGED",
        "VOXEL_SMOKE_DAMAGE_DISPLAY_UPDATED",
        "VOXEL_SMOKE_DESTROYED",
        "VOXEL_SMOKE_DESTROY_DISPLAY_UPDATED",
        "VOXEL_SMOKE_RESTORED_AFTER_DAMAGE",
        "VOXEL_SMOKE_INTERNAL_FACE_UPDATED",
        "VOXEL_SMOKE_INTERNAL_DESTROYED",
        "VOXEL_SMOKE_INTERNAL_DESTROY_DISPLAY_UPDATED",
        "VOXEL_SMOKE_MULTI_DAMAGED",
        "VOXEL_SMOKE_MULTI_DISPLAY_UPDATED",
        "VOXEL_SMOKE_RELOAD_DAMAGED_80",
        "VOXEL_SMOKE_RELOAD_MAX_150",
        "VOXEL_SMOKE_RELOAD_TEXT_UPDATED",
        "VOXEL_SMOKE_RELOAD_ASSET_UPDATED",
        "VOXEL_SMOKE_RELOAD_ORIENTATION_MAPPED",
        "VOXEL_SMOKE_RELOAD_MAX_60",
        "VOXEL_SMOKE_RELOAD_PROBE_ADDED",
        "VOXEL_SMOKE_RELOAD_REJECTED",
        "VOXEL_SMOKE_RELOAD_DISPLAY_RETAINED",
        "VOXEL_SMOKE_CONSOLE_CLOSED",
        "VOXEL_SMOKE_RETURNED_TO_MENU",
        "VOXEL_SMOKE_MENU_REENTERED"
    )
}
$missing = @($required | Where-Object { $output -notlike "*$_*" })
if ($output -match 'NullReferenceException' -or $output -match 'ERROR: System\.') {
    [Console]::Error.WriteLine("Godot voxel smoke failed. Unexpected exception in engine output.")
    exit 1
}
if ($godotExitCode -ne 0) {
    $detail = "Godot exited with code $godotExitCode."
    if ($missing.Count -gt 0) {
        $detail += " Missing: $($missing -join ', ')."
    }
    [Console]::Error.WriteLine("Godot voxel smoke failed. $detail")
    exit 1
}

if ($missing.Count -gt 0) {
    [Console]::Error.WriteLine("Godot voxel smoke failed. Missing: $($missing -join ', ')")
    exit 1
}

if (-not $IsolationAbort -and $Slice -eq 2) {
    $publishedMesh = @(
        "VOXEL_SMOKE_X_BOUNDARY_MESH",
        "VOXEL_SMOKE_Y_BOUNDARY_MESH",
        "VOXEL_SMOKE_Z_BOUNDARY_MESH",
        "VOXEL_SMOKE_JUNCTION_MESH",
        "VOXEL_SMOKE_CROSS_DAMAGE_MESH"
    )
    $staleMesh = @($publishedMesh | Where-Object {
        $name = $_
        -not ($output -split "`r?`n" | Where-Object { $_ -like "*$name*" -and $_ -like "*published=1*" })
    })
    if ($staleMesh.Count -gt 0) {
        [Console]::Error.WriteLine("Godot voxel smoke failed. Published ArrayMesh markers missing: $($staleMesh -join ', ')")
        exit 1
    }
    $publishedCollision = @(
        "VOXEL_SMOKE_X_BOUNDARY_COLLIDE",
        "VOXEL_SMOKE_Y_BOUNDARY_COLLIDE",
        "VOXEL_SMOKE_Z_BOUNDARY_COLLIDE",
        "VOXEL_SMOKE_JUNCTION_COLLIDE",
        "VOXEL_SMOKE_CROSS_DAMAGE_COLLIDE",
        "VOXEL_SMOKE_LOCAL_BOX_IDENTITY",
        "VOXEL_SMOKE_LOCAL_BOX_ROTATED",
        "VOXEL_SMOKE_LOCAL_BOX_REMOVED",
        "VOXEL_SMOKE_LOCAL_BOX_RELOADED",
        "VOXEL_SMOKE_LOCAL_BOX_TRANSLATED_EDGE",
        "VOXEL_SMOKE_LOCAL_BOX_ADAPTER_UNDERFLOW",
        "VOXEL_SMOKE_LOCAL_BOX_REJECTED",
        "VOXEL_SMOKE_LOCAL_BOX_LETHAL",
        "VOXEL_SMOKE_RESUME_COLLIDE_NONE",
        "VOXEL_SMOKE_RESUME_COLLIDE_FULL_BOX",
        "VOXEL_SMOKE_RESUME_COLLIDE_LOCAL_BOX"
    )
    $staleCollision = @($publishedCollision | Where-Object {
        $name = $_
        -not ($output -split "`r?`n" | Where-Object { $_ -like "*$name*" -and $_ -like "*published=1*" })
    })
    if ($staleCollision.Count -gt 0) {
        [Console]::Error.WriteLine("Godot voxel smoke failed. Published physics-query collision markers missing: $($staleCollision -join ', ')")
        exit 1
    }
}

if ($IsolationAbort) {
    Write-Host "Godot voxel smoke isolation abort kept worktree built-in content unchanged."
} else {
    Write-Host "Godot voxel smoke passed for slice $Slice."
}
exit 0
