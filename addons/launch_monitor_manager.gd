class_name LaunchMonitorManagerAutoload
extends Node

# Reusable launch-monitor orchestrator autoload. Owns the active monitor
# instance, persists its own settings (so it works drop-in in any Godot
# project), and re-emits monitor signals so host gameplay code doesn't need to
# know which monitor is connected.
#
# Monitor implementations live in sibling folders (e.g. `square/`); shared
# transports and external receivers live under `common/`. Script paths are
# resolved relative to this script's own directory, so the addon works wherever
# it is installed — it does not assume a fixed `res://addons/...` location.

signal hit_ball(data: Dictionary)
# Club metrics for the last shot (GSPro `ClubData` keys; unmeasured keys omitted).
# Square only, and it arrives shortly after `hit_ball`.
signal club_data(data: Dictionary)
signal device_discovered(device_id: String, name: String, rssi: int)
signal status_changed(status: String)
signal error_occurred(message: String)
signal battery_changed(level: int)
signal firmware_changed(firmware: String)
signal ready_changed(is_ready: bool)
# Emitted whenever a persisted setting changes (enabled, provider, tcp_port,
# square_club_code, square_handedness, square_spin_mode, square_swing_stick,
# square_device_id). Host UIs can observe this to stay in sync; they may also
# drive the addon through the setters.
signal setting_changed(key: String, value: Variant)

# Provider identifiers are owned by the addon (no host dependency). Values match
# the historical strings so existing hosts keep working.
const PROVIDER_PITRAC := "PiTrac"
const PROVIDER_SQUARE := "Square"
const PROVIDERS := [PROVIDER_PITRAC, PROVIDER_SQUARE]

const DEFAULT_CLUB_CODE := SquareClubCatalog.DEFAULT_CLUB_CODE
const DEFAULT_TCP_PORT := 49152
const DEFAULT_SPIN_MODE := 1

const SETTINGS_PATH := "user://launch_monitor.cfg"
const SETTINGS_SECTION := "launch_monitor"

# Script locations relative to this addon's root (see `_addon_dir`).
const SQUARE_SCRIPT_REL := "square/SquareLaunchMonitor.cs"
const SQUARE_CLASS_NAME := "SquareLaunchMonitor"
const TCP_SERVER_SCRIPT_REL := "common/tcp_server/TcpServer.cs"
const TCP_SERVER_CLASS_NAME := "TcpServer"

const LMONITOR_LOG_PREFIX := "[LMonitor]"
const SQUARE_DEVICE_PREFIX := "squaregolf"
const BLUEZ_DEVICE_SEGMENT_PREFIX := "/dev_"
const LINUX_AUTO_CONNECT_SCAN_SECONDS := 15.0
const TRANSIENT_CONNECT_ERROR_MARKERS := [
	"not ready yet",
	"could not open the selected bluetooth device"
]

# Control-flow state, kept separate from the human-readable `status` display
# string. Logic branches on `_state`; UI reads `status`. Changing display
# wording can no longer silently break control flow.
enum State { DISABLED, DISCONNECTED, SCANNING, CONNECTED, READY, PITRAC }

var devices: Dictionary = {}
var status := "Disconnected"
var battery_level := -1
var firmware := ""
var is_ready := false

# Persisted settings (addon-owned).
var _enabled := false
var _provider := PROVIDER_PITRAC
var _tcp_port := DEFAULT_TCP_PORT
var _square_device_id := ""
var _square_club_code := DEFAULT_CLUB_CODE
var _square_handedness := 0
var _square_spin_mode := DEFAULT_SPIN_MODE
var _square_swing_stick := false

var _state := State.DISCONNECTED
var _square_init_error := ""
var _tcp_init_error := ""
var _last_create_error := ""

var _square: Node = null
var _config := ConfigFile.new()
var _linux_auto_connect_active := false
var _linux_auto_connect_target_address := ""
var _linux_auto_connect_timer: Timer = null
var _tcp_server: Node = null
var _active_provider := ""


static func is_valid_provider(provider: String) -> bool:
	return provider in PROVIDERS


static func normalize_provider(provider: String) -> String:
	if is_valid_provider(provider):
		return provider
	return PROVIDER_PITRAC


func _ready() -> void:
	_debug_log("Launch monitor ready. OS=%s, C# runtime class exists=%s, assembly=%s" % [
		OS.get_name(),
		str(ClassDB.class_exists("CSharpScript")),
		str(ProjectSettings.get_setting("dotnet/project/assembly_name", ""))
	])
	_load_settings()
	_create_square_monitor()
	if _square == null:
		_debug_error("Square monitor unavailable during startup: %s" % _square_init_error)
	_apply()


func _exit_tree() -> void:
	_stop_pitrac()


# --- Public settings API -----------------------------------------------------

func set_enabled(value: bool) -> void:
	if _enabled == value:
		return
	if not value:
		_cancel_linux_auto_connect_scan()
	_enabled = value
	_persist()
	emit_signal("setting_changed", "enabled", value)
	_apply()


func is_enabled() -> bool:
	return _enabled


func set_provider(value: String) -> void:
	var normalized := normalize_provider(value)
	if _provider == normalized:
		return
	_provider = normalized
	_persist()
	emit_signal("setting_changed", "provider", normalized)
	_apply()


func get_provider() -> String:
	return _provider


func set_tcp_port(value: int) -> void:
	var clamped := clampi(value, 1, 65535)
	if _tcp_port == clamped:
		return
	_tcp_port = clamped
	_persist()
	emit_signal("setting_changed", "tcp_port", clamped)
	if _enabled and _provider == PROVIDER_PITRAC:
		_start_pitrac(_tcp_port)


func get_tcp_port() -> int:
	return _tcp_port


func set_club_code(club_code: String) -> void:
	_square_club_code = club_code
	_persist()
	emit_signal("setting_changed", "square_club_code", club_code)
	if _square != null:
		_square.call("SetClub", club_code)


func get_square_club_code() -> String:
	return _square_club_code


func set_handedness(handedness: int) -> void:
	_square_handedness = handedness
	_persist()
	emit_signal("setting_changed", "square_handedness", handedness)
	if _square != null:
		_square.call("SetHandedness", handedness)


func get_square_handedness() -> int:
	return _square_handedness


func set_square_spin_mode(mode: int) -> void:
	var normalized := 0 if mode == 0 else 1
	if _square_spin_mode == normalized:
		return
	_square_spin_mode = normalized
	_persist()
	emit_signal("setting_changed", "square_spin_mode", normalized)
	if _square != null:
		_square.call("SetSpinMode", normalized)


func get_square_spin_mode() -> int:
	return _square_spin_mode


## Square's swing stick instead of a real club: the selected club goes to the
## device by its swing stick code. square_club_code stays the regular code.
func set_square_swing_stick(on: bool) -> void:
	if _square_swing_stick == on:
		return
	_square_swing_stick = on
	_persist()
	emit_signal("setting_changed", "square_swing_stick", on)
	if _square != null:
		_square.call("SetSwingStick", on)


func get_square_swing_stick() -> bool:
	return _square_swing_stick


func set_selected_device_id(device_id: String) -> void:
	if _square_device_id == device_id:
		return
	_square_device_id = device_id
	_persist()
	emit_signal("setting_changed", "square_device_id", device_id)


func get_selected_device_id() -> String:
	return _square_device_id


# --- Scan / connect API ------------------------------------------------------

func start_scan() -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		_set_status("Select Square")
		return
	_cancel_linux_auto_connect_scan()
	_start_square_scan()


func stop_scan() -> void:
	_cancel_linux_auto_connect_scan()
	_stop_square_scan()


func connect_to_device(device_id: String) -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		_set_status("Select Square")
		return
	_cancel_linux_auto_connect_scan()
	if _square == null:
		var message := _missing_support_message()
		_debug_error("connect_to_device blocked: %s" % message)
		_set_status(message)
		emit_signal("error_occurred", message)
		return
	_debug_log("connect_to_device requested for %s" % device_id)
	set_selected_device_id(device_id)
	_square.call("SetHandedness", _square_handedness)
	_square.call("SetClub", _square_club_code)
	_square.call("SetSpinMode", _square_spin_mode)
	_square.call("SetSwingStick", _square_swing_stick)
	_square.call("ConnectToDevice", device_id)


func disconnect_device() -> void:
	_cancel_linux_auto_connect_scan()
	if _square != null:
		_debug_log("disconnect_device requested")
		_square.call("DisconnectFromDevice")
	_clear_monitor_details()


func set_ready() -> void:
	if _square != null:
		_debug_log("set_ready requested")
		_square.call("SetReady")


func _start_square_scan() -> void:
	if _square == null:
		var message := _missing_support_message()
		_debug_error("start_scan blocked: %s" % message)
		_set_status(message)
		emit_signal("error_occurred", message)
		return
	_debug_log("start_scan requested")
	devices.clear()
	_square.call("StartScan")


func _stop_square_scan() -> void:
	if _square != null:
		_debug_log("stop_scan requested")
		_square.call("StopScan")


# --- Settings persistence ----------------------------------------------------

func _load_settings() -> void:
	if _config.load(SETTINGS_PATH) != OK:
		return
	_enabled = bool(_config.get_value(SETTINGS_SECTION, "enabled", _enabled))
	_provider = normalize_provider(str(_config.get_value(SETTINGS_SECTION, "provider", _provider)))
	_tcp_port = clampi(int(_config.get_value(SETTINGS_SECTION, "tcp_port", _tcp_port)), 1, 65535)
	_square_device_id = str(_config.get_value(SETTINGS_SECTION, "square_device_id", _square_device_id))
	_square_club_code = str(_config.get_value(SETTINGS_SECTION, "square_club_code", _square_club_code))
	_square_handedness = int(_config.get_value(SETTINGS_SECTION, "square_handedness", _square_handedness))
	_square_spin_mode = 0 if int(_config.get_value(SETTINGS_SECTION, "square_spin_mode", DEFAULT_SPIN_MODE)) == 0 else 1
	_square_swing_stick = bool(_config.get_value(SETTINGS_SECTION, "square_swing_stick", _square_swing_stick))


func _persist() -> void:
	_config.set_value(SETTINGS_SECTION, "enabled", _enabled)
	_config.set_value(SETTINGS_SECTION, "provider", _provider)
	_config.set_value(SETTINGS_SECTION, "tcp_port", _tcp_port)
	_config.set_value(SETTINGS_SECTION, "square_device_id", _square_device_id)
	_config.set_value(SETTINGS_SECTION, "square_club_code", _square_club_code)
	_config.set_value(SETTINGS_SECTION, "square_handedness", _square_handedness)
	_config.set_value(SETTINGS_SECTION, "square_spin_mode", _square_spin_mode)
	_config.set_value(SETTINGS_SECTION, "square_swing_stick", _square_swing_stick)
	var err := _config.save(SETTINGS_PATH)
	if err != OK:
		_debug_error("Failed to save launch monitor settings at %s" % SETTINGS_PATH)
		emit_signal("error_occurred", "Launch monitor settings could not be saved.")


# --- Provider orchestration --------------------------------------------------

func _apply() -> void:
	if not _enabled:
		_disable_launch_monitors()
		return
	if _provider == PROVIDER_SQUARE:
		_start_square_provider()
	else:
		_start_pitrac_provider()


func _is_provider_active(provider: String) -> bool:
	return _enabled and _provider == provider


func _disable_launch_monitors() -> void:
	_stop_pitrac()
	_stop_square_provider()
	_active_provider = ""
	_clear_monitor_details()
	_set_status("Disabled", State.DISABLED)


func _start_square_provider() -> void:
	if _active_provider != PROVIDER_SQUARE:
		_stop_pitrac()
		_active_provider = PROVIDER_SQUARE
		if _state == State.DISABLED or _state == State.PITRAC:
			_set_status("Disconnected", State.DISCONNECTED)
		_connect_saved_device_on_startup(_square_device_id)


func _stop_square_provider() -> void:
	var should_stop_runtime := _active_provider == PROVIDER_SQUARE or _linux_auto_connect_active
	_cancel_linux_auto_connect_scan()
	if not should_stop_runtime:
		_clear_monitor_details()
		return
	_stop_square_scan()
	disconnect_device()


func _start_pitrac_provider() -> void:
	if _active_provider != PROVIDER_PITRAC:
		_stop_square_provider()
		_active_provider = PROVIDER_PITRAC
	_clear_monitor_details()
	_start_pitrac(_tcp_port)


func _start_pitrac(port: int) -> void:
	if _tcp_server == null:
		_create_pitrac_tcp_server()
	if _tcp_server == null:
		_set_status(_tcp_init_error)
		return

	if bool(_tcp_server.call("GetIsListening")):
		_tcp_server.call("StopListening")
		await get_tree().process_frame

	_tcp_server.call("StartListening", port)


func _stop_pitrac() -> void:
	if _tcp_server == null:
		return
	_tcp_server.call("StopListening")


# --- Monitor node creation ---------------------------------------------------

# Resolves the addon's root directory from this script's own path, so loads work
# regardless of where the addon is installed in the host project.
func _addon_dir() -> String:
	var script := get_script() as Script
	if script == null:
		return "res://addons"
	return script.resource_path.get_base_dir()


# Shared load -> can_instantiate -> new -> add_child path. Returns the node or
# null; on failure `_last_create_error` describes why.
func _create_addon_node(rel_path: String, class_label: String) -> Node:
	_last_create_error = ""
	var script_path := _addon_dir().path_join(rel_path)
	_debug_log("Attempting to load script %s" % script_path)
	var script := load(script_path) as Script
	if script == null:
		_last_create_error = "%s script could not be loaded at %s." % [class_label, script_path]
		return null
	if not script.can_instantiate():
		_last_create_error = "%s script is loaded but cannot instantiate. Ensure C# build succeeds and class name matches filename." % class_label
		return null
	var node := script.new() as Node
	if node == null:
		_last_create_error = "%s could not be created from %s. Check C# build output for load errors." % [class_label, script_path]
		return null
	add_child(node)
	return node


func _create_square_monitor() -> void:
	_square = _create_addon_node(SQUARE_SCRIPT_REL, SQUARE_CLASS_NAME)
	_square_init_error = _last_create_error
	if _square == null:
		_set_status(_square_init_error)
		emit_signal("error_occurred", _square_init_error)
		_debug_error(_square_init_error)
		return

	_set_status("Disconnected", State.DISCONNECTED)
	_debug_log("%s instantiated and signals connected." % SQUARE_CLASS_NAME)
	_square.connect("DeviceDiscovered", _on_square_device_discovered)
	_square.connect("StatusChanged", _on_square_status_changed)
	_square.connect("ErrorOccurred", _on_square_error_occurred)
	_square.connect("BatteryChanged", _on_square_battery_changed)
	_square.connect("FirmwareChanged", _on_square_firmware_changed)
	_square.connect("ReadyChanged", _on_square_ready_changed)
	_square.connect("ClubDataReceived", _on_square_club_data_received)
	_square.connect("ShotReceived", _on_square_shot_received)


func _create_pitrac_tcp_server() -> void:
	_tcp_server = _create_addon_node(TCP_SERVER_SCRIPT_REL, TCP_SERVER_CLASS_NAME)
	_tcp_init_error = _last_create_error
	if _tcp_server == null:
		_debug_error(_tcp_init_error)
		return
	_tcp_server.connect("HitBall", _on_pitrac_hit_ball)
	_tcp_server.connect("StatusChanged", _on_pitrac_status_changed)


# --- Square monitor signal handlers ------------------------------------------

func _on_square_device_discovered(device_id: String, name: String, rssi: int) -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		return
	if not _is_square_device_name(name):
		_debug_log("ignoring non-square device discovery: %s (%s)" % [name, device_id])
		return
	_debug_log("device discovered: %s (%s) RSSI=%d" % [name, device_id, rssi])
	devices[device_id] = {
		"name": name,
		"rssi": rssi
	}
	emit_signal("device_discovered", device_id, name, rssi)
	if _is_linux_auto_connect_match(device_id):
		_debug_log("saved Linux Square discovered; connecting automatically")
		connect_to_device(device_id)


func _on_square_status_changed(value: String) -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		return
	_set_status(_normalize_square_status(value), _square_state_for(value))


func _on_square_error_occurred(message: String) -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		return
	if _is_transient_square_connect_error(message):
		_debug_log("Square runtime warning: %s" % message)
	else:
		_debug_error("Square runtime error: %s" % message)
	emit_signal("error_occurred", message)


func _on_square_battery_changed(level: int) -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		return
	_debug_log("battery changed: %d%%" % level)
	battery_level = level
	emit_signal("battery_changed", level)


func _on_square_firmware_changed(value: String) -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		return
	_debug_log("firmware changed: %s" % value)
	firmware = value
	emit_signal("firmware_changed", value)


func _on_square_ready_changed(value: bool) -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		return
	_debug_log("ready changed: %s" % str(value))
	is_ready = value
	emit_signal("ready_changed", value)


func _on_square_shot_received(data: Dictionary) -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		return
	_debug_log("shot received with %d fields" % data.size())
	emit_signal("hit_ball", data)


func _on_square_club_data_received(data: Dictionary) -> void:
	if not _is_provider_active(PROVIDER_SQUARE):
		return
	_debug_log("club data received with %d fields" % data.size())
	emit_signal("club_data", data)


# --- PiTrac (network) signal handlers ----------------------------------------

func _on_pitrac_hit_ball(data: Dictionary) -> void:
	_debug_log("PiTrac shot received with %d fields" % data.size())
	emit_signal("hit_ball", data)
	# Manager mode has no gameplay validation hook, so ack on forward. Scenes
	# that embed TcpServer directly ack from their own validation instead.
	if _tcp_server != null:
		_tcp_server.call("RespondShotAccepted")


func _on_pitrac_status_changed(value: String) -> void:
	if not _is_provider_active(PROVIDER_PITRAC):
		return
	_set_status("%s %s" % [PROVIDER_PITRAC, value], State.PITRAC)


# --- Shared helpers ----------------------------------------------------------

func _clear_monitor_details() -> void:
	if battery_level != -1:
		battery_level = -1
		emit_signal("battery_changed", battery_level)
	if firmware != "":
		firmware = ""
		emit_signal("firmware_changed", firmware)


func _missing_support_message() -> String:
	if _square_init_error != "":
		return "Square support is unavailable in this build. %s" % _square_init_error
	return "Square support is unavailable in this build."


func _connect_saved_device_on_startup(device_id: String) -> void:
	if device_id == "":
		return
	if _square == null:
		connect_to_device(device_id)
		return
	if OS.get_name() != "Linux":
		connect_to_device(device_id)
		return
	_start_linux_auto_connect_scan(device_id)


func _start_linux_auto_connect_scan(device_id: String) -> void:
	_cancel_linux_auto_connect_scan()
	var target_address := _normalize_bluetooth_address(device_id)
	if target_address == "":
		_debug_log("saved Linux Bluetooth id cannot be matched automatically")
		return
	_linux_auto_connect_active = true
	_linux_auto_connect_target_address = target_address
	_debug_log("starting saved Linux Square scan")
	_start_square_scan()
	_start_linux_auto_connect_timer()


func _start_linux_auto_connect_timer() -> void:
	_clear_linux_auto_connect_timer()
	_linux_auto_connect_timer = Timer.new()
	_linux_auto_connect_timer.one_shot = true
	_linux_auto_connect_timer.wait_time = LINUX_AUTO_CONNECT_SCAN_SECONDS
	_linux_auto_connect_timer.timeout.connect(_on_linux_auto_connect_timeout)
	add_child(_linux_auto_connect_timer)
	_linux_auto_connect_timer.start()


func _on_linux_auto_connect_timeout() -> void:
	if not _linux_auto_connect_active:
		return
	_debug_log("saved Linux Square was not found during startup scan")
	_linux_auto_connect_active = false
	_linux_auto_connect_target_address = ""
	_clear_linux_auto_connect_timer()
	_stop_square_scan()
	if _state == State.SCANNING:
		_set_status("Disconnected", State.DISCONNECTED)


func _cancel_linux_auto_connect_scan() -> void:
	_linux_auto_connect_active = false
	_linux_auto_connect_target_address = ""
	_clear_linux_auto_connect_timer()


func _clear_linux_auto_connect_timer() -> void:
	if _linux_auto_connect_timer == null:
		return
	if _linux_auto_connect_timer.timeout.is_connected(_on_linux_auto_connect_timeout):
		_linux_auto_connect_timer.timeout.disconnect(_on_linux_auto_connect_timeout)
	_linux_auto_connect_timer.stop()
	_linux_auto_connect_timer.queue_free()
	_linux_auto_connect_timer = null


func _is_linux_auto_connect_match(device_id: String) -> bool:
	if not _linux_auto_connect_active or _linux_auto_connect_target_address == "":
		return false
	return _normalize_bluetooth_address(device_id) == _linux_auto_connect_target_address


func _normalize_bluetooth_address(value: String) -> String:
	var normalized := value.strip_edges()
	if normalized == "":
		return ""
	var device_segment_index := normalized.rfind(BLUEZ_DEVICE_SEGMENT_PREFIX)
	if device_segment_index >= 0:
		normalized = normalized.substr(device_segment_index + BLUEZ_DEVICE_SEGMENT_PREFIX.length())
		var child_path_index := normalized.find("/")
		if child_path_index >= 0:
			normalized = normalized.substr(0, child_path_index)
	normalized = normalized.replace("-", ":").replace("_", ":").to_upper()
	if normalized.length() == 12 and not normalized.contains(":"):
		var parts := PackedStringArray()
		for index in range(0, normalized.length(), 2):
			parts.append(normalized.substr(index, 2))
		normalized = ":".join(parts)
	if not _is_bluetooth_address(normalized):
		return ""
	return normalized


func _is_bluetooth_address(value: String) -> bool:
	var parts := value.split(":")
	if parts.size() != 6:
		return false
	for part in parts:
		if part.length() != 2:
			return false
		for index in range(part.length()):
			if not _is_hex_digit_code(part.unicode_at(index)):
				return false
	return true


func _is_hex_digit_code(value: int) -> bool:
	return (value >= 48 and value <= 57) or (value >= 65 and value <= 70)


func _set_status(value: String, state: int = -1) -> void:
	status = value
	if state >= 0:
		_state = state
	emit_signal("status_changed", value)
	_debug_log("status -> %s" % value)


func _normalize_square_status(value: String) -> String:
	var normalized := value.strip_edges()
	if normalized == "Ready":
		return "Connected"
	return normalized


func _square_state_for(value: String) -> int:
	match value.strip_edges():
		"Scanning":
			return State.SCANNING
		"Connected", "Ready":
			return State.CONNECTED
		"Disconnected":
			return State.DISCONNECTED
		_:
			# Connection-progress / error messages: leave control-flow state
			# untouched, they are display-only.
			return -1


func _debug_log(message: String) -> void:
	print("%s %s" % [LMONITOR_LOG_PREFIX, message])


func _debug_error(message: String) -> void:
	push_error("%s %s" % [LMONITOR_LOG_PREFIX, message])


func _is_transient_square_connect_error(message: String) -> bool:
	var normalized := message.strip_edges().to_lower()
	for marker in TRANSIENT_CONNECT_ERROR_MARKERS:
		if normalized.contains(marker):
			return true
	return false


func _is_square_device_name(name: String) -> bool:
	return name.strip_edges().to_lower().begins_with(SQUARE_DEVICE_PREFIX)
