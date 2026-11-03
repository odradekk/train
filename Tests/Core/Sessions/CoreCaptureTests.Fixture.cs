using Microsoft.VisualStudio.TestTools.UnitTesting;
using Train.Core.Contracts;
using Train.Core.ContractSupport;
using System.Text.Json;
using System.Text.Json.Nodes;
using Train.Core.Tests.World;
using Train.Core.Sessions;

namespace Train.Core.Tests.Sessions;

public sealed partial class CoreCaptureTests
{
    private static CoreSession Session() => new(WorldFixture.State(new(7, 2), 10, new(20), Content('A')));
    private static ICoreCapture Saved()
    {
        using var session = Session();
        return Capture(session);
    }
}
