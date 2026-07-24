"""
Per-application + global volume control with an on-screen overlay.

Hold RIGHT CTRL and press:
- Up / Down arrows    -> raise / lower the volume of whichever app
                         currently has focus (the foreground window).
- Left / Right arrows -> raise / lower the global (system/master)
                         volume.

An overlay pops up showing the app's icon, name, and current volume
level (or "System Volume" for the global control).

A system tray icon provides device switching and settings.

--------------------------------------------------------------------
INSTALL (run once, in an elevated or normal cmd/PowerShell):

    pip install pycaw comtypes psutil keyboard pywin32 pillow pystray

RUN:

    python main.py

NOTES / CAVEATS:
- Because these are Right Ctrl + arrow combos, plain arrow keys keep
  working normally everywhere else — only the combo is intercepted.
- Apps running as Administrator (e.g. some games) may not be
  reachable by the keyboard hook unless you also run this script as
  Administrator too.
- Not every foreground window has an audio session (e.g. it hasn't
  played sound yet, or it's a system window) — the overlay will say
  "No audio session" in that case.
- Close the script from the terminal (Ctrl+C) or Task Manager to stop it.
--------------------------------------------------------------------
"""

import json
import os
import queue
import sys
import threading
import tkinter as tk
from tkinter import ttk
from ctypes import c_ulong, c_wchar_p

import comtypes
import comtypes.client
import keyboard
import psutil
import pystray
import win32api
import win32con
import win32gui
import win32process
import win32ui
from PIL import Image, ImageDraw, ImageTk
from pycaw.pycaw import (
    AudioUtilities,
    IAudioEndpointVolume,
    IMMDeviceEnumerator,
)
from pycaw.constants import EDataFlow, DEVICE_STATE
from comtypes import CLSCTX_ALL, COMMETHOD, GUID, HRESULT, IUnknown

# ----------------------------- CONFIG -------------------------------
STEP = 0.05                  # volume change per key press (5%)
HOTKEY_UP = "right ctrl+up"        # per-app volume up
HOTKEY_DOWN = "right ctrl+down"    # per-app volume down
HOTKEY_LEFT = "right ctrl+left"    # global/system volume down
HOTKEY_RIGHT = "right ctrl+right"  # global/system volume up
SUPPRESS_KEYS = True          # block the normal OS action for these combos
OVERLAY_DURATION_MS = 1300    # how long the overlay stays visible
OVERLAY_WIDTH = 340
OVERLAY_HEIGHT = 100
OVERLAY_OPACITY = 0.92
ICON_SIZE = 40
# ---------------------------------------------------------------------

event_queue = queue.Queue()
_icon_cache = {}


# ----------------------- Config management ---------------------------

def _config_path():
    if getattr(sys, "frozen", False):
        base = os.path.dirname(sys.executable)
    else:
        base = os.path.dirname(os.path.abspath(__file__))
    return os.path.join(base, "settings.json")


def load_config():
    path = _config_path()
    if os.path.exists(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            pass
    return {"hidden_devices": []}


def save_config(cfg):
    path = _config_path()
    try:
        with open(path, "w", encoding="utf-8") as f:
            json.dump(cfg, f, indent=2)
    except Exception:
        pass


# ----------------------- IPolicyConfig COM ---------------------------

class IPolicyConfig(IUnknown):
    _iid_ = GUID("{F8679F50-850A-41CF-9C72-430F290290C8}")
    _methods_ = (
        COMMETHOD([], HRESULT, "GetMixFormat"),
        COMMETHOD([], HRESULT, "Initialize"),
        COMMETHOD([], HRESULT, "GetDeviceFormat"),
        COMMETHOD([], HRESULT, "ResetDeviceFormat"),
        COMMETHOD([], HRESULT, "SetDeviceFormat"),
        COMMETHOD([], HRESULT, "GetProcessingPeriod"),
        COMMETHOD([], HRESULT, "SetProcessingPeriod"),
        COMMETHOD([], HRESULT, "GetShareMode"),
        COMMETHOD([], HRESULT, "SetShareMode"),
        COMMETHOD([], HRESULT, "GetCurrentCertificate"),
        COMMETHOD(
            [], HRESULT, "SetDefaultEndpoint",
            (["in"], c_wchar_p, "pwstrID"),
            (["in"], c_ulong, "dwRole"),
        ),
        COMMETHOD([], HRESULT, "SetEndpointVisibility"),
    )


CLSID_PolicyConfigClient = GUID("{870AF99C-171D-4F9E-AF0D-E63Df40c2BC9}")


def _get_policy_config():
    return comtypes.CoCreateInstance(
        CLSID_PolicyConfigClient,
        IPolicyConfig,
        comtypes.CLSCTX_INPROC_SERVER,
    )


# ----------------------- Audio helpers -------------------------------

def get_foreground_pid_and_title():
    hwnd = win32gui.GetForegroundWindow()
    if not hwnd:
        return None, None
    try:
        _, pid = win32process.GetWindowThreadProcessId(hwnd)
        title = win32gui.GetWindowText(hwnd)
        return pid, title
    except Exception:
        return None, None


def get_process_name(pid):
    try:
        return psutil.Process(pid).name()
    except Exception:
        return None


def get_exe_path(pid):
    try:
        return psutil.Process(pid).exe()
    except Exception:
        return None


def find_audio_session(pid, proc_name):
    sessions = AudioUtilities.GetAllSessions()

    for session in sessions:
        proc = session.Process
        if proc and proc.pid == pid:
            return session

    if proc_name:
        for session in sessions:
            proc = session.Process
            if proc:
                try:
                    if proc.name().lower() == proc_name.lower():
                        return session
                except Exception:
                    continue
    return None


def get_master_volume_interface():
    devices = AudioUtilities.GetSpeakers()
    interface = devices.Activate(IAudioEndpointVolume._iid_, CLSCTX_ALL, None)
    return interface.QueryInterface(IAudioEndpointVolume)


def get_icon_image(exe_path, size=ICON_SIZE):
    if not exe_path:
        return None
    if exe_path in _icon_cache:
        return _icon_cache[exe_path]

    img = None
    try:
        large, small = win32gui.ExtractIconEx(exe_path, 0)
        hicon = None
        if small:
            hicon = small[0]
        elif large:
            hicon = large[0]

        if hicon:
            ico_x = win32api.GetSystemMetrics(win32con.SM_CXICON)
            ico_y = win32api.GetSystemMetrics(win32con.SM_CYICON)

            hdc = win32ui.CreateDCFromHandle(win32gui.GetDC(0))
            hbmp = win32ui.CreateBitmap()
            hbmp.CreateCompatibleBitmap(hdc, ico_x, ico_y)
            hdc_mem = hdc.CreateCompatibleDC()
            hdc_mem.SelectObject(hbmp)
            hdc_mem.DrawIcon((0, 0), hicon)

            bmpinfo = hbmp.GetInfo()
            bmpstr = hbmp.GetBitmapBits(True)
            raw = Image.frombuffer(
                "RGBA",
                (bmpinfo["bmWidth"], bmpinfo["bmHeight"]),
                bmpstr, "raw", "BGRA", 0, 1,
            )
            img = raw.resize((size, size), Image.LANCZOS)

        for h in (large or []):
            win32gui.DestroyIcon(h)
        for h in (small or []):
            win32gui.DestroyIcon(h)
    except Exception:
        img = None

    _icon_cache[exe_path] = img
    return img


# ----------------------- Audio device enumeration/switching ----------

def get_active_output_devices():
    """Return list of (device_id, friendly_name) for all active render endpoints."""
    deviceEnumerator = comtypes.CoCreateInstance(
        GUID("{BCDE0395-E52F-467C-8E3D-C4579291692E}"),
        IMMDeviceEnumerator,
        comtypes.CLSCTX_INPROC_SERVER,
    )
    collection = deviceEnumerator.EnumAudioEndpoints(
        EDataFlow.eRender.value, DEVICE_STATE.ACTIVE.value
    )
    count = collection.GetCount()
    result = []
    for i in range(count):
        dev = collection.Item(i)
        ad = AudioUtilities.CreateDevice(dev)
        result.append((ad.id, ad.FriendlyName))
    return result


def get_default_device_id():
    return AudioUtilities.GetSpeakers().GetId()


def set_default_device(device_id):
    policy = _get_policy_config()
    policy.SetDefaultEndpoint(device_id, 0)


def generate_tray_image():
    """Create a simple speaker icon for the tray."""
    size = 64
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)

    # Speaker body
    draw.rectangle([18, 24, 30, 40], fill="white")
    # Speaker cone
    draw.polygon([(30, 18), (48, 8), (48, 56), (30, 46)], fill="white")
    # Sound waves
    draw.arc([40, 14, 56, 50], start=315, end=45, fill="white", width=3)
    draw.arc([46, 8, 64, 56], start=315, end=45, fill="white", width=3)

    return img


# ----------------------------- Overlay UI -----------------------------

class Overlay:
    def __init__(self, root):
        self.root = root
        self.hide_job = None

        self.win = tk.Toplevel(root)
        self.win.overrideredirect(True)
        self.win.attributes("-topmost", True)
        self.win.attributes("-alpha", OVERLAY_OPACITY)
        self.win.configure(bg="#1e1e1e")
        self.win.withdraw()

        self._place_window()

        frame = tk.Frame(self.win, bg="#1e1e1e")
        frame.pack(fill="both", expand=True, padx=14, pady=12)

        self.icon_label = tk.Label(frame, bg="#1e1e1e")
        self.icon_label.grid(row=0, column=0, rowspan=2, padx=(0, 12))

        self.name_label = tk.Label(
            frame, text="", fg="white", bg="#1e1e1e",
            font=("Segoe UI", 11, "bold"), anchor="w",
        )
        self.name_label.grid(row=0, column=1, sticky="w")

        self.pct_label = tk.Label(
            frame, text="", fg="#cccccc", bg="#1e1e1e",
            font=("Segoe UI", 10), anchor="w",
        )
        self.pct_label.grid(row=1, column=1, sticky="w")

        style = ttk.Style()
        style.theme_use("default")
        style.configure(
            "Overlay.Horizontal.TProgressbar",
            troughcolor="#3a3a3a", background="#4caf50",
            thickness=8, borderwidth=0,
        )
        self.progress = ttk.Progressbar(
            frame, style="Overlay.Horizontal.TProgressbar",
            orient="horizontal", length=OVERLAY_WIDTH - 90,
            mode="determinate", maximum=100,
        )
        self.progress.grid(row=2, column=0, columnspan=2, sticky="we", pady=(8, 0))

        self._tk_icon_ref = None

    def _place_window(self):
        screen_w = self.win.winfo_screenwidth()
        screen_h = self.win.winfo_screenheight()
        x = (screen_w - OVERLAY_WIDTH) // 2
        y = screen_h - OVERLAY_HEIGHT - 140
        self.win.geometry(f"{OVERLAY_WIDTH}x{OVERLAY_HEIGHT}+{x}+{y}")

    def show(self, name, pct, pil_icon, no_session=False):
        if pil_icon is not None:
            self._tk_icon_ref = ImageTk.PhotoImage(pil_icon)
            self.icon_label.configure(image=self._tk_icon_ref)
        else:
            self.icon_label.configure(image="")

        self.name_label.configure(text=name or "Unknown app")

        if no_session:
            self.pct_label.configure(text="No audio session")
            self.progress["value"] = 0
        else:
            pct_int = max(0, min(100, round(pct * 100)))
            self.pct_label.configure(text=f"Volume: {pct_int}%")
            self.progress["value"] = pct_int

        self._place_window()
        self.win.deiconify()
        self.win.lift()

        if self.hide_job is not None:
            self.root.after_cancel(self.hide_job)
        self.hide_job = self.root.after(OVERLAY_DURATION_MS, self.win.withdraw)


# --------------------------- Settings window --------------------------

class SettingsWindow:
    def __init__(self, root, config, on_save):
        self.root = root
        self.config = config
        self.on_save = on_save
        self.win = None
        self.vars = {}

    def open(self):
        if self.win is not None:
            self.win.lift()
            return

        self.win = tk.Toplevel(self.root)
        self.win.title("SmartVol Settings")
        self.win.geometry("420x340")
        self.win.resizable(False, False)
        self.win.configure(bg="#2d2d2d")
        self.win.protocol("WM_DELETE_WINDOW", self._on_close)

        header = tk.Label(
            self.win, text="Hidden Audio Devices",
            fg="white", bg="#2d2d2d", font=("Segoe UI", 12, "bold"),
        )
        header.pack(pady=(14, 4))

        subtitle = tk.Label(
            self.win,
            text="Uncheck devices to hide them from the tray menu.",
            fg="#aaaaaa", bg="#2d2d2d", font=("Segoe UI", 9),
        )
        subtitle.pack(pady=(0, 8))

        container = tk.Frame(self.win, bg="#2d2d2d")
        container.pack(fill="both", expand=True, padx=14, pady=(0, 4))

        canvas = tk.Canvas(container, bg="#2d2d2d", highlightthickness=0)
        scrollbar = ttk.Scrollbar(container, orient="vertical", command=canvas.yview)
        inner = tk.Frame(canvas, bg="#2d2d2d")

        inner.bind(
            "<Configure>",
            lambda e: canvas.configure(scrollregion=canvas.bbox("all")),
        )
        canvas.create_window((0, 0), window=inner, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)

        canvas.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")

        self.vars.clear()
        devices = get_active_output_devices()
        hidden = set(self.config.get("hidden_devices", []))

        for dev_id, dev_name in devices:
            visible = tk.BooleanVar(value=(dev_id not in hidden))
            self.vars[dev_id] = visible
            cb = tk.Checkbutton(
                inner, text=dev_name, variable=visible,
                fg="white", bg="#2d2d2d", selectcolor="#444444",
                activebackground="#2d2d2d", activeforeground="white",
                font=("Segoe UI", 10), anchor="w", pady=3,
            )
            cb.pack(fill="x")

        btn_frame = tk.Frame(self.win, bg="#2d2d2d")
        btn_frame.pack(fill="x", padx=14, pady=(4, 14))

        save_btn = tk.Button(
            btn_frame, text="Save", command=self._on_save,
            bg="#4caf50", fg="white", font=("Segoe UI", 10, "bold"),
            relief="flat", padx=20, pady=4, cursor="hand2",
        )
        save_btn.pack(side="right", padx=(8, 0))

        cancel_btn = tk.Button(
            btn_frame, text="Cancel", command=self._on_close,
            bg="#555555", fg="white", font=("Segoe UI", 10),
            relief="flat", padx=20, pady=4, cursor="hand2",
        )
        cancel_btn.pack(side="right")

    def _on_save(self):
        hidden = [dev_id for dev_id, var in self.vars.items() if not var.get()]
        self.config["hidden_devices"] = hidden
        save_config(self.config)
        if self.on_save:
            self.on_save()
        self._on_close()

    def _on_close(self):
        if self.win is not None:
            self.win.destroy()
            self.win = None


# --------------------------- Core logic --------------------------------

def handle_app_volume(direction, overlay):
    pid, _title = get_foreground_pid_and_title()
    if pid is None:
        return

    proc_name = get_process_name(pid)
    display_name = proc_name or "Unknown"

    session = find_audio_session(pid, proc_name)

    if session is None:
        exe_path = get_exe_path(pid)
        icon = get_icon_image(exe_path)
        overlay.show(display_name, 0, icon, no_session=True)
        return

    volume_ctl = session.SimpleAudioVolume
    current = volume_ctl.GetMasterVolume()
    new_vol = max(0.0, min(1.0, current + direction * STEP))
    volume_ctl.SetMasterVolume(new_vol, None)

    exe_path = get_exe_path(pid)
    icon = get_icon_image(exe_path)

    overlay.show(display_name, new_vol, icon)


def handle_global_volume(direction, overlay):
    volume_ctl = get_master_volume_interface()
    current = volume_ctl.GetMasterVolumeLevelScalar()
    new_vol = max(0.0, min(1.0, current + direction * STEP))
    volume_ctl.SetMasterVolumeLevelScalar(new_vol, None)
    overlay.show("System Volume", new_vol, None)


def poll_queue(root, overlay):
    try:
        while True:
            mode, direction = event_queue.get_nowait()
            if mode == "app":
                handle_app_volume(direction, overlay)
            else:
                handle_global_volume(direction, overlay)
    except queue.Empty:
        pass
    root.after(30, poll_queue, root, overlay)


# --------------------------- Tray icon ---------------------------------

class TrayManager:
    def __init__(self, root, overlay):
        self.root = root
        self.overlay = overlay
        self.config = load_config()
        self.settings_win = SettingsWindow(root, self.config, self._on_settings_saved)
        self.icon = None
        self._icon_thread = None

    def start(self):
        self.icon = pystray.Icon(
            "SmartVol",
            icon=generate_tray_image(),
            title="SmartVol",
            menu=self._build_menu(),
        )
        self._icon_thread = threading.Thread(
            target=self.icon.run, daemon=True,
        )
        self._icon_thread.start()

    def _build_menu(self):
        devices = get_active_output_devices()
        default_id = get_default_device_id()
        hidden = set(self.config.get("hidden_devices", []))

        visible_devices = [
            (did, name) for did, name in devices if did not in hidden
        ]

        device_items = []
        for dev_id, dev_name in visible_devices:
            is_default = (dev_id == default_id)
            prefix = "\u2713 " if is_default else "   "

            def _make_switch(did=dev_id):
                def _switch(icon, item):
                    try:
                        set_default_device(did)
                        self._refresh_menu()
                    except Exception:
                        pass
                return _switch

            device_items.append(
                pystray.MenuItem(
                    prefix + dev_name,
                    _make_switch(),
                    radio=True,
                    default=is_default,
                )
            )

        if not device_items:
            device_items.append(
                pystray.MenuItem("  (no devices found)", None, enabled=False)
            )

        menu = pystray.Menu(
            *device_items,
            pystray.Menu.SEPARATOR,
            pystray.MenuItem("Settings", self._on_settings),
            pystray.MenuItem("Exit", self._on_exit),
        )
        return menu

    def _refresh_menu(self):
        if self.icon is not None:
            self.icon.menu = self._build_menu()
            self.icon.update_menu()

    def _on_settings(self, icon, item):
        self.root.after(0, self.settings_win.open)

    def _on_settings_saved(self):
        self._refresh_menu()

    def _on_exit(self, icon, item):
        icon.stop()
        self.root.after(0, self.root.destroy)

    def stop(self):
        if self.icon is not None:
            self.icon.stop()


# --------------------------- Main --------------------------------------

def main():
    root = tk.Tk()
    root.withdraw()

    overlay = Overlay(root)

    tray = TrayManager(root, overlay)
    tray.start()

    keyboard.add_hotkey(
        HOTKEY_UP, lambda: event_queue.put(("app", 1)), suppress=SUPPRESS_KEYS
    )
    keyboard.add_hotkey(
        HOTKEY_DOWN, lambda: event_queue.put(("app", -1)), suppress=SUPPRESS_KEYS
    )
    keyboard.add_hotkey(
        HOTKEY_RIGHT, lambda: event_queue.put(("global", 1)), suppress=SUPPRESS_KEYS
    )
    keyboard.add_hotkey(
        HOTKEY_LEFT, lambda: event_queue.put(("global", -1)), suppress=SUPPRESS_KEYS
    )

    root.after(30, poll_queue, root, overlay)
    root.mainloop()


if __name__ == "__main__":
    main()
