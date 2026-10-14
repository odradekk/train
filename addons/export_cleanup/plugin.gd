@tool
extends EditorPlugin


class McpProbeExport extends EditorExportPlugin:
	const AUTOLOAD := "autoload/MCPRuntimeProbe"
	var _original_value: Variant

	func _get_name() -> String:
		return "ExcludeMcpProbe"

	func _export_begin(_features: PackedStringArray, _is_debug: bool, _path: String, _flags: int) -> void:
		_original_value = ProjectSettings.get_setting(AUTOLOAD, null)
		ProjectSettings.set_setting(AUTOLOAD, null)
		print("export_mcp_probe_removed")

	func _export_end() -> void:
		ProjectSettings.set_setting(AUTOLOAD, _original_value)
		print("export_mcp_probe_restored")


var _export_plugin := McpProbeExport.new()


func _enter_tree() -> void:
	add_export_plugin(_export_plugin)


func _exit_tree() -> void:
	remove_export_plugin(_export_plugin)
