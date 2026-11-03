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
    [TestMethod]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":1", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":2", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":3", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":4", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":5", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":6", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":7", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":8", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":9", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":10", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":11", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":12", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":13", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":14", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":15", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":16", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":17", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":18", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":19", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":20", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":21", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":23", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":22", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":\"1\"", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":null", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":-1", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":1.5", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24", "\"core_state_format_version\":18446744073709551616", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"core_state_format_version\":24,", "", "unsupported_format", "/core_state_format_version")]
    [DataRow("\"revision\":20,", "", "invalid_state", "/revision")]
    [DataRow("\"revision\":20", "\"revision\":20,\"extra\":1", "invalid_state", "/extra")]
    [DataRow("\"revision\":20", "\"revision\":20,\"revision\":21", "invalid_state", "/revision")]
    [DataRow("\"revision\":20", "\"revision\":18446744073709551616", "invalid_state", "/revision")]
    [DataRow("\"revision\":20", "\"revision\":-1", "invalid_state", "/revision")]
    [DataRow("\"revision\":20", "\"revision\":1.5", "invalid_state", "/revision")]
    [DataRow("\"next_command_number\":10", "\"next_command_number\":0", "invalid_state", "/next_command_number")]
    [DataRow("\"completed_steps\":7", "\"completed_steps\":false", "invalid_state", "/position/completed_steps")]
    [DataRow("\"sequence\":2", "\"sequence\":-1", "invalid_state", "/position/sequence")]
    [DataRow("\"sequence\":2", "\"sequence\":2,\"other\":0", "invalid_state", "/position/other")]
    [DataRow("\"completed_steps\":7,\"sequence\":2", "", "invalid_state", "/position/completed_steps")]
    [DataRow(Identity, "abcd", "invalid_state", "/input_identity")]
    [DataRow(Identity, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "invalid_state", "/input_identity")]
    [DataRow(Identity, "GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG", "invalid_state", "/input_identity")]
    [DataRow(Identity, "\\uD800", "invalid_state", "/input_identity")]
    public void Invalid_saved_fields_return_the_exact_problem_path(string before, string after, string code, string field)
        => AssertFailure(CoreFactory.Create().DecodeState(Payload.Replace(before, after)), code, field);

}
