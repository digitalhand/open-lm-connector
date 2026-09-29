@tool
extends EditorPlugin

# Registers the launch-monitor orchestrator as a project autoload when the plugin
# is enabled, so a host project gets `LaunchMonitorManager` without editing
# project.godot by hand. The path is resolved from this script's own location, so
# the addon works wherever it is installed.

const AUTOLOAD_NAME := "LaunchMonitorManager"


func _enable_plugin() -> void:
	add_autoload_singleton(AUTOLOAD_NAME, _manager_script_path())


func _disable_plugin() -> void:
	remove_autoload_singleton(AUTOLOAD_NAME)


func _manager_script_path() -> String:
	return get_script().resource_path.get_base_dir().path_join("launch_monitor_manager.gd")
