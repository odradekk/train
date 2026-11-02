using System.Collections.Immutable;
using Godot;
using Train.Content.Contracts;

namespace Train.Ui;

internal partial class AuthoringDefinitionForm
{
    internal bool TryCapture(out DefinitionDraft? draft)
    {
        draft = null;
        foreach (var field in _fields.Where(f => GodotObject.IsInstanceValid(f.Control))) field.ErrorText = field.Error ?? "";
        var valid = true;
        foreach (var field in _fields.Where(f => GodotObject.IsInstanceValid(f.Control) && f.Control.IsInsideTree() && f.Control.IsVisibleInTree()))
        {
            var error = field.Validate?.Invoke();
            if (error is null) continue;
            field.ErrorText = error; valid = false;
        }
        if (!valid) return false;
        try { draft = Capture(); return true; }
        catch (FormatException error)
        {
            var field = _fields.FirstOrDefault(f => error.Message.StartsWith(f.TooltipText + "：", StringComparison.Ordinal));
            if (field is not null) field.ErrorText = error.Message;
            return false;
        }
    }

    internal void SetDiagnostics(ImmutableArray<ContentDiagnostic> diagnostics)
    {
        foreach (var field in _fields.Where(f => GodotObject.IsInstanceValid(f.Control))) field.ErrorText = field.Error ?? "";
        foreach (var error in _groupErrors.Values) { error.Text = ""; error.Visible = false; }
        foreach (var diagnostic in diagnostics)
        {
            if (_groupErrors.TryGetValue(diagnostic.Field, out var collectionError))
            {
                collectionError.Text = diagnostic.Message; collectionError.Visible = true; continue;
            }
            var field = _fields.Where(f => GodotObject.IsInstanceValid(f.Control) && f.Control.IsInsideTree()).FirstOrDefault(f => FieldPath(f) == diagnostic.Field)
                ?? _fields.Where(f => GodotObject.IsInstanceValid(f.Control) && f.Control.IsInsideTree()).FirstOrDefault(f => FieldPath(f).StartsWith(diagnostic.Field + "/", StringComparison.Ordinal))
                ?? _fields.Where(f => GodotObject.IsInstanceValid(f.Control) && f.Control.IsInsideTree()).FirstOrDefault(f => FieldPath(f).Length > 0 && diagnostic.Field.StartsWith(FieldPath(f) + "/", StringComparison.Ordinal));
            if (field is null)
            {
                var group = _groupErrors.Keys.Where(path => diagnostic.Field.StartsWith(path + "/", StringComparison.Ordinal)).OrderByDescending(path => path.Length).FirstOrDefault();
                if (group is not null) { _groupErrors[group].Text = diagnostic.Message; _groupErrors[group].Visible = true; }
                continue;
            }
            if (field.Validate?.Invoke() is null && field.ErrorText == field.ReferenceError?.Invoke()) field.ErrorText = "";
            field.ErrorText = string.Join('\n', new[] { field.ErrorText, diagnostic.Message }.Where(s => s.Length > 0));
        }
    }

    internal void SetReferenceChoices(ImmutableArray<AuthoringReferenceChoice> choices)
    {
        if (_choices.SequenceEqual(choices)) return;
        _choices = choices;
        foreach (var field in _fields.Where(f => GodotObject.IsInstanceValid(f.Control) && f.Control.IsInsideTree())) field.RefreshChoices?.Invoke();
    }

    private string FieldPath(FormField field)
    {
        if (field.Path.StartsWith('/')) return field.Path;
        for (var parent = field.Control.GetParent(); parent is not null && parent != this; parent = parent.GetParent())
        {
            if (parent.HasMeta("array_row"))
            {
                var group = parent.GetParent();
                var index = group.GetChildren().Where(c => c.HasMeta("array_row")).ToList().IndexOf(parent);
                return group.GetMeta("field_path").AsString() + "/" + index + (field.Path.Length == 0 ? "" : "/" + field.Path);
            }
            if (parent.HasMeta("field_path")) return parent.GetMeta("field_path").AsString() + "/" + field.Path;
        }
        throw new InvalidOperationException("局部字段路径必须属于一个字段组。");
    }
}
