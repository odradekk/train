using Godot;
using Train.Ui;

namespace Train.App.Measurement;

internal sealed partial class MeasurementRun
{
    internal static (string[] BlockingControls, bool Modal) ReadSceneUi(Node root)
    {
        var size = root.GetViewport().GetVisibleRect().Size;
        var mainWindow = root.GetWindow();
        List<string>? blocking = null;
        var modal = false;
        Visit(root);
        return (blocking?.ToArray() ?? [], modal);

        void Visit(Node node)
        {
            var childCount = node.GetChildCount();
            for (var i = 0; i < childCount; i++)
            {
                var child = node.GetChild(i);
                if (child is Node3D) continue;
                if (child is Control control && control.IsVisibleInTree() &&
                    control.MouseFilter != Control.MouseFilterEnum.Ignore && control.GetGlobalRect().Size.X >= size.X &&
                    control.GetGlobalRect().Size.Y >= size.Y && control is not MacroMap)
                    (blocking ??= []).Add(control.GetPath().ToString());
                if (child is Window window && window != mainWindow && window.Visible && window.Exclusive)
                    modal = true;
                Visit(child);
            }
        }
    }
}
