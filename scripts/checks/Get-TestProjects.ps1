@(
    @{ name = 'core'; path = 'Tests/Core/Train.Core.Tests.csproj'; fast = 481; integration = $(if ($IsWindows) { 10 } else { 7 }) },
    @{ name = 'content'; path = 'Tests/Content/Train.Content.Tests.csproj'; fast = 426; integration = 1 },
    @{ name = 'scenes'; path = 'Tests/Scenes/Train.Scenes.Tests.csproj'; fast = 251; integration = 1 },
    @{ name = 'godot'; path = 'Tests/Godot/Train.Godot.Tests.csproj'; fast = 145; integration = $(if ($IsWindows) { 85 } else { 77 }); engine = 109 },
    @{ name = 'contract-support'; path = 'Tests/ContractSupport/SelfTests/Train.ContractSupport.Tests.csproj'; fast = 41; integration = 0 },
    @{ name = 'contract-checks'; path = 'Tests/ContractChecks.Tests/Train.ContractChecks.Tests.csproj'; fast = 9; integration = 4 },
    @{ name = 'scene-scale'; path = 'Tests/SceneScale/Train.SceneScale.Tests.csproj'; fast = 20; integration = 8 },
    @{ name = 'cli'; path = 'Tests/Cli/Train.Cli.Tests.csproj'; fast = 4; integration = $(if ($IsWindows) { 7 } else { 3 }) }
)
