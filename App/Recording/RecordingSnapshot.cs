using Train.Content.Contracts;
using Train.Core.Contracts;

namespace Train.App.Recording;

internal sealed record RecordingSnapshot(HostBinding Binding, RuntimeIdentity Runtime,
    CoreRecordingWindow Window, IContentReplayCapture Content);
